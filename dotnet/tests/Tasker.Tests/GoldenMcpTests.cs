using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Tasker.Storage.Files.Workspaces;
using Xunit;

namespace Tasker.Tests;

public partial class GoldenMcpTests
{
    private const string GoldenProject = "11111111-1111-4111-8111-111111111111";
    private const string LegacyProject = "22222222-2222-4222-8222-222222222222";
    private const string BugId = "00000035-0000-4000-8000-000000000035";

    private static readonly JsonSerializerOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly JsonSerializerOptions Indented = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, WriteIndented = true, IndentSize = 2 };
    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    [GeneratedRegex(@"\d{4}-\d\d-\d\d[ T]\d\d:\d\d:\d\d(\.\d+)? ?\+00:00")]
    private static partial Regex WallClock();

    private static string Golden
    {
        get
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            {
                if (Directory.Exists(Path.Combine(directory.FullName, "tests", "golden", "workspace")))
                    return Path.Combine(directory.FullName, "tests", "golden");
            }
            throw new DirectoryNotFoundException("tests/golden not found above the test binaries");
        }
    }

    private static bool Updating => Environment.GetEnvironmentVariable("TASKER_GOLDEN_UPDATE") is { Length: > 0 } value && value != "0";

    private static void Copy(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from))
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        foreach (var directory in Directory.GetDirectories(from))
            Copy(directory, Path.Combine(to, Path.GetFileName(directory)));
    }

    private static string TempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "tasker-golden-mcp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "home"));
        root = new DirectoryInfo(root).ResolveLinkTarget(true)?.FullName ?? Path.GetFullPath(root);
        if (OperatingSystem.IsMacOS() && root.StartsWith("/var/", StringComparison.Ordinal))
            root = "/private" + root;
        return root;
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class Fixture : IDisposable
    {
        private readonly Process _daemon;
        private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(60) };

        private Fixture(string root, int port, Process daemon)
        {
            Root = root;
            Port = port;
            _daemon = daemon;
        }

        public string Root { get; }

        public int Port { get; }

        public string GoldenPath => Path.Combine(Root, "golden");

        public string Home => Path.Combine(Root, "home");

        public static async Task<Fixture> Start(Action<string>? prepare = null)
        {
            var root = TempRoot();
            Copy(Path.Combine(Golden, "workspace"), Path.Combine(root, "golden"));
            prepare?.Invoke(Path.Combine(root, "golden"));
            var workspaces = new[] { Path.Combine(root, "golden"), Path.Combine(root, "broken") }.Select(x => (Kind: "files", Path: x));
            return await Launch(root, workspaces, beforeLaunch: null);
        }

        public static async Task<Fixture> Launch(string root, IEnumerable<(string Kind, string Path)> workspaces, Func<Task>? beforeLaunch)
        {
            var port = FreePort();
            var entries = string.Join(",", workspaces.Select(x => $"{{\"kind\":\"{x.Kind}\",\"path\":{JsonSerializer.Serialize(x.Path, Compact)}}}"));
            await File.WriteAllTextAsync(Path.Combine(root, "home", "settings.json"), $"{{\"mcp\":{{\"port\":{port},\"workspaces\":[{entries}]}}}}\n");
            if (beforeLaunch != null)
                await beforeLaunch();

            var (file, arguments) = TaskerProcess.DaemonCommand();
            var info = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var argument in arguments)
                info.ArgumentList.Add(argument);
            info.Environment.Remove("TASKER_GOLDEN_UPDATE");
            info.Environment.Remove("TASKER_MCP_OPEN_WAIT_MS");
            info.Environment["TASKER_HOME"] = Path.Combine(root, "home");
            info.Environment["TASKER_SERVICE_DIR"] = Path.Combine(root, "home", "service");
            info.Environment["TASKER_SERVICE_LABEL"] = $"com.tasker.mcp-test-{port}";
            var daemon = Process.Start(info)!;
            daemon.OutputDataReceived += (_, _) => { };
            daemon.ErrorDataReceived += (_, _) => { };
            daemon.BeginOutputReadLine();
            daemon.BeginErrorReadLine();
            var fixture = new Fixture(root, port, daemon);

            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (true)
            {
                try
                {
                    using var response = await fixture._http.GetAsync($"http://127.0.0.1:{port}/ready");
                    if (response.StatusCode == HttpStatusCode.OK)
                        break;
                }
                catch (HttpRequestException)
                {
                }
                if (DateTime.UtcNow > deadline)
                {
                    fixture.Dispose();
                    throw new TimeoutException("the daemon did not get ready in 60 s");
                }
                await Task.Delay(50);
            }
            return fixture;
        }

        public async Task<JsonNode> Rpc(string method, JsonNode parameters, IReadOnlyDictionary<string, string>? headers = null)
        {
            var body = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = method, ["params"] = parameters }.ToJsonString(Compact);
            using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{Port}/mcp")
            {
                Content = new StringContent(body, Utf8, "application/json")
            };
            request.Headers.Accept.ParseAdd("application/json");
            request.Headers.Accept.ParseAdd("text/event-stream");
            foreach (var (name, value) in headers ?? new Dictionary<string, string>())
                request.Headers.Add(name, value);
            using var response = await _http.SendAsync(request);
            Assert.True(response.StatusCode == HttpStatusCode.OK, method);
            var text = await response.Content.ReadAsStringAsync();
            var data = text.Split('\n').Select(x => x.TrimEnd('\r')).FirstOrDefault(x => x.StartsWith("data:"))
                ?? throw new InvalidOperationException("no data line in " + text);
            return JsonNode.Parse(data["data:".Length..].Trim())!;
        }

        public Task<JsonNode> Call(string tool, JsonNode arguments, IReadOnlyDictionary<string, string>? headers = null) =>
            Rpc("tools/call", new JsonObject { ["name"] = tool, ["arguments"] = arguments }, headers);

        public async Task<JsonNode> ResultJson(string tool, JsonNode arguments)
        {
            var text = (await Call(tool, arguments))["result"]!["content"]![0]!["text"]!.GetValue<string>();
            return JsonNode.Parse(text) ?? throw new InvalidOperationException($"{tool}: {text}");
        }

        public async Task<(bool IsError, string Text)> Text(string tool, JsonNode arguments)
        {
            var result = (await Call(tool, arguments))["result"]!;
            return (result["isError"]?.GetValue<bool>() ?? false, result["content"]![0]!["text"]!.GetValue<string>());
        }

        public void Dispose()
        {
            try
            {
                if (!_daemon.HasExited)
                {
                    _daemon.Kill(true);
                    _daemon.WaitForExit(10000);
                }
            }
            catch (InvalidOperationException)
            {
            }
            _daemon.Dispose();
            _http.Dispose();
            for (var attempt = 0; attempt < 20; attempt++)
            {
                try
                {
                    Directory.Delete(Root, true);
                    return;
                }
                catch (IOException)
                {
                    Thread.Sleep(100);
                }
            }
        }
    }

    private sealed class Snapshots(string directory, List<(string From, string To)> replacements)
    {
        public List<string> Failures { get; } = [];

        public List<(string From, string To)> Replacements { get; } = replacements;

        private string Placeholders(string text)
        {
            foreach (var (from, to) in Replacements)
                text = to == GoldenPaths.Root ? GoldenPaths.Rooted(text, from) : text.Replace(from, to);
            return text;
        }

        private JsonNode Normalize(JsonNode value)
        {
            var node = JsonNode.Parse(Placeholders(value.ToJsonString(Compact)))!;
            if (node is JsonObject map && map["isError"] is JsonValue flag && flag.TryGetValue<bool>(out var isError) && !isError)
                map.Remove("isError");
            CanonicalTexts(node);
            return node;
        }

        private static void CanonicalTexts(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject map:
                    foreach (var key in map.Select(x => x.Key).ToArray())
                    {
                        if (key == "text" && map[key] is JsonValue text && text.TryGetValue<string>(out var raw))
                        {
                            JsonNode? parsed = null;
                            try
                            {
                                parsed = JsonNode.Parse(raw);
                            }
                            catch (JsonException)
                            {
                            }
                            if (parsed != null)
                                map[key] = parsed.ToJsonString(Compact);
                        }
                        else
                            CanonicalTexts(map[key]);
                    }
                    break;
                case JsonArray items:
                    foreach (var item in items)
                        CanonicalTexts(item);
                    break;
            }
        }

        public void Check(string name, JsonNode actual)
        {
            var path = Path.Combine(directory, name + ".json");
            if (Updating)
                File.WriteAllText(path, Placeholders(actual.ToJsonString(Indented)) + "\n", Utf8);

            var expected = JsonNode.Parse(File.ReadAllText(path, Utf8))!;
            var normalizedExpected = Normalize(expected["result"]!);
            var normalizedActual = Normalize(actual["result"]!);
            if (!JsonNode.DeepEquals(normalizedExpected, normalizedActual))
                Failures.Add($"{name}:\n  expected {normalizedExpected.ToJsonString(Compact)}\n  actual   {normalizedActual.ToJsonString(Compact)}");
        }

        public void AssertNone() =>
            Assert.True(Failures.Count == 0, $"{Failures.Count} snapshot(s) differ:\n{string.Join("\n", Failures)}");
    }

    private static JsonObject Args(string workspace, string? project, params (string Key, JsonNode? Value)[] extra)
    {
        var map = new JsonObject { ["workspace"] = workspace };
        if (project != null)
            map["projectId"] = project;
        foreach (var (key, value) in extra)
            map[key] = value;
        return map;
    }

    private static string TextOf(JsonNode answer) => answer["result"]!["content"]![0]!["text"]!.GetValue<string>();

    private static JsonNode ScrubTimes(JsonNode answer) => JsonNode.Parse(WallClock().Replace(answer.ToJsonString(Compact), "<TIME>"))!;

    private static async Task<string> Console(Fixture f, params string[] args)
    {
        var info = new ProcessStartInfo(TaskerProcess.Binary)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding = Utf8,
            WorkingDirectory = f.Root
        };
        foreach (var name in new[] { "TASKER_PROJECT", "TASKER_WIDTH", "TASKER_PROFILE", "TASKER_LANG", "COLUMNS", "LINES", "TASKER_GOLDEN_UPDATE" })
            info.Environment.Remove(name);
        info.Environment["TASKER_HOME"] = f.Home;
        info.Environment["TASKER_SERVICE_DIR"] = Path.Combine(f.Home, "service");
        foreach (var arg in args)
            info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, string.Join(' ', args) + ": " + await error);
        return await output;
    }

    [Fact]
    public async Task Mcp_answers_match_the_snapshots()
    {
        var root = TempRoot();
        Copy(Path.Combine(Golden, "workspace"), Path.Combine(root, "golden"));
        var golden = Path.Combine(root, "golden");
        var broken = Path.Combine(root, "broken");

        using var f = await Fixture.Launch(root, [("files", golden), ("files", broken)], async () =>
        {
            var whoami = new ProcessStartInfo(TaskerProcess.Binary) { RedirectStandardOutput = true, RedirectStandardError = true };
            whoami.Environment["TASKER_HOME"] = Path.Combine(root, "home");
            whoami.Environment["TASKER_SERVICE_DIR"] = Path.Combine(root, "home", "service");
            whoami.ArgumentList.Add("whoami");
            whoami.ArgumentList.Add("golden-user");
            using var process = Process.Start(whoami)!;
            var error = process.StandardError.ReadToEndAsync();
            await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.True(process.ExitCode == 0, await error);
        });

        var snapshots = new Snapshots(Path.Combine(Golden, "expected", "mcp"),
        [
            (f.Root, "<ROOT>"),
            (WorkspaceLocation.Files(golden).Id, "<WSID:golden>"),
            (WorkspaceLocation.Files(Path.Combine(f.Root, "broken")).Id, "<WSID:broken>")
        ]);
        JsonNode G(params (string Key, JsonNode? Value)[] extra) => Args("golden", GoldenProject, extra);

        var me = await f.ResultJson("whoami", new JsonObject { ["workspace"] = "golden" });
        foreach (var (key, marker) in new[] { ("id", "<LOCAL-AGENT-ID>"), ("createdAt", "<LOCAL-AGENT-CREATED>"), ("version", "<LOCAL-AGENT-VERSION>") })
            snapshots.Replacements.Add((me[key]!.GetValue<string>(), marker));

        snapshots.Check("initialize", await f.Rpc("initialize", JsonNode.Parse("""{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"golden","version":"0"}}""")!));
        snapshots.Check("list_workspaces", await f.Call("list_workspaces", new JsonObject()));
        snapshots.Check("whoami", await f.Call("whoami", new JsonObject { ["workspace"] = "golden" }));
        snapshots.Check("list_projects", await f.Call("list_projects", new JsonObject { ["workspace"] = "golden" }));
        snapshots.Check("list_tasks", await f.Call("list_tasks", G()));
        snapshots.Check("list_tasks-brief", await f.Call("list_tasks", G(("descriptionLength", 0), ("limit", 5))));
        var bug = await f.Call("get_task", G(("taskId", BugId)));
        snapshots.Check("get_task", bug);
        var bugVersion = JsonNode.Parse(TextOf(bug))!["version"]!.GetValue<string>();
        snapshots.Check("get_task-by-reference", await f.Call("get_task", G(("taskId", "GLD-1"))));
        snapshots.Check("get_task_links", await f.Call("get_task_links", G(("taskId", BugId))));
        snapshots.Check("find_tasks_by_reference", await f.Call("find_tasks_by_reference", G(("reference", "BUG-1"))));
        snapshots.Check("list_fields", await f.Call("list_fields", G()));
        snapshots.Check("list_enums", await f.Call("list_enums", G()));
        var boards = await f.ResultJson("list_boards", G());
        snapshots.Check("list_boards", await f.Call("list_boards", G()));
        var mainBoard = boards["data"]!.AsArray().First(x => x!["name"]!.GetValue<string>() == "Main board")!["id"]!.GetValue<string>();
        snapshots.Check("get_board", await f.Call("get_board", G(("boardId", mainBoard))));
        snapshots.Check("list_link_types", await f.Call("list_link_types", G()));
        snapshots.Check("list_series", await f.Call("list_series", G()));
        snapshots.Check("series_health", await f.Call("series_health", G()));
        var statuses = await f.ResultJson("list_statuses", G());
        snapshots.Check("list_statuses", await f.Call("list_statuses", G()));
        snapshots.Check("list_status_sets", await f.Call("list_status_sets", G()));
        snapshots.Check("list_task_types", await f.Call("list_task_types", G()));
        snapshots.Check("list_users", await f.Call("list_users", new JsonObject { ["workspace"] = "golden" }));
        snapshots.Check("list_project_members", await f.Call("list_project_members", G()));
        snapshots.Check("list_workspace_problems", await f.Call("list_workspace_problems", new JsonObject { ["workspace"] = "golden" }));
        snapshots.Check("get_lock", await f.Call("get_lock", G(("entity", "task"), ("entityId", BugId))));

        snapshots.Check("errors/invalid-no-project", await f.Call("get_task", new JsonObject { ["workspace"] = "golden", ["taskId"] = "GLD-1" }));
        snapshots.Check("errors/invalid-bad-id", await f.Call("get_task", G(("taskId", "not-a-task"))));
        snapshots.Check("errors/invalid-missing-argument", await f.Call("get_task", G()));
        snapshots.Check("errors/invalid-no-workspace", await f.Call("list_projects", new JsonObject()));
        snapshots.Check("errors/not_found-task", await f.Call("get_task", G(("taskId", "GLD-999"))));
        snapshots.Check("errors/not_found-workspace", await f.Call("list_projects", new JsonObject { ["workspace"] = "nowhere" }));
        snapshots.Check("errors/modified", await f.Call("update_task", G(("taskId", BugId), ("title", "x"), ("version", "0000000000000000"))));
        var done = statuses["data"]!.AsArray().First(x => x!["name"]!.GetValue<string>() == "Done")!;
        snapshots.Check("errors/in_use", await f.Call("delete_status", G(("statusId", done["id"]!.DeepClone()), ("version", done["version"]!.DeepClone()))));
        snapshots.Check("errors/forbidden", await f.Call("list_projects", new JsonObject { ["workspace"] = "golden" },
            new Dictionary<string, string> { ["X-Tasker-Agent"] = "99999999-9999-4999-8999-999999999999" }));
        snapshots.Check("errors/unsupported_format", await f.Call("get_task",
            new JsonObject { ["workspace"] = "golden", ["projectId"] = LegacyProject, ["taskId"] = "ffffffff-0000-4000-8000-ffffffffffff" }));
        snapshots.Check("errors/failed", await f.Call("list_projects", new JsonObject { ["workspace"] = "broken" }));

        await Console(f, "lock", "acquire", "task", BugId, "-w", golden, "-p", "Golden");
        var locked = await f.Call("update_task", G(("taskId", BugId), ("title", "x"), ("version", bugVersion)));
        snapshots.Check("errors/locked", ScrubTimes(locked));
        await Console(f, "lock", "release", "task", BugId, "-w", golden, "-p", "Golden");

        snapshots.Check("errors/unknown-tool", await f.Call("no_such_tool", new JsonObject()));

        snapshots.AssertNone();
    }

    [Fact]
    public async Task Mcp_tools_list_matches_the_snapshot_tool_by_tool_in_key_order()
    {
        using var f = await Fixture.Start();
        var snapshots = new Snapshots(Path.Combine(Golden, "expected", "mcp"), [(f.Root, "<ROOT>")]);
        var tools = await f.Rpc("tools/list", new JsonObject());
        var toolsFile = Path.Combine(Golden, "expected", "mcp", "tools-list.json");
        if (Updating)
            File.WriteAllText(toolsFile, tools.ToJsonString(Indented) + "\n", Utf8);
        var expectedTools = JsonNode.Parse(await File.ReadAllTextAsync(toolsFile))!;
        var actualByName = tools["result"]!["tools"]!.AsArray().ToDictionary(x => x!["name"]!.GetValue<string>(), x => x!.ToJsonString(Compact));
        foreach (var tool in expectedTools["result"]!["tools"]!.AsArray())
        {
            var name = tool!["name"]!.GetValue<string>();
            if (!actualByName.TryGetValue(name, out var actualTool))
                snapshots.Failures.Add($"tools-list: tool {name} is missing");
            else if (actualTool != tool.ToJsonString(Compact))
                snapshots.Failures.Add($"tools-list: tool {name} differs in key order or content:\n  expected {tool.ToJsonString(Compact)}\n  actual   {actualTool}");
        }
        var names = string.Join("\n", tools["result"]!["tools"]!.AsArray().Select(x => x!["name"]!.GetValue<string>()).Order(StringComparer.Ordinal)) + "\n";
        var namesFile = Path.Combine(Golden, "expected", "mcp", "tool-names.txt");
        if (Updating)
            File.WriteAllText(namesFile, names, Utf8);
        Assert.Equal(File.ReadAllText(namesFile, Utf8), names);

        snapshots.AssertNone();
    }

    [Fact]
    public async Task Unreadable_files_answer_with_their_read_error()
    {
        const string conflict = "Unresolved git merge conflict (line 3)";
        const string future = "from-the-future-ffffffff.yaml: format version 99 is newer than this Tasker supports (12): update Tasker";
        using var f = await Fixture.Start(golden =>
        {
            var project = Path.Combine(golden, ".tasker", "projects", LegacyProject);
            File.WriteAllText(Path.Combine(project, "fields", "broken-dddddddd.yaml"),
                "formatVersion: 4\nid: dddddddd-0000-4000-8000-dddddddddddd\n<<<<<<< HEAD\nname: A\n=======\nname: B\n>>>>>>> x\ntype: string\n", Utf8);
            File.WriteAllText(Path.Combine(project, "enums", "later-eeeeeeee.yaml"),
                "formatVersion: 99\nid: eeeeeeee-0000-4000-8000-eeeeeeeeeeee\nname: Later\nvalues: []\n", Utf8);
        });

        JsonNode Legacy(params (string Key, JsonNode? Value)[] extra) => Args("golden", LegacyProject, extra);
        async Task<string> Failure(string tool, JsonNode arguments)
        {
            var (isError, text) = await f.Text(tool, arguments);
            Assert.True(isError, $"{tool}: {text}");
            return text;
        }

        var mismatches = new List<string>();
        void Check(string expected, string actual, string what)
        {
            if (expected != actual)
                mismatches.Add($"{what}: expected {expected}, actual {actual}");
        }

        foreach (var task in new[] { "cccccccc-0000-4000-8000-cccccccccccc", "cccccccc", "CCCCCCCC0000" })
        {
            Check($"[invalid] {conflict}", await Failure("get_task", Legacy(("taskId", task))), "get_task " + task);
            Check($"[invalid] {conflict}", await Failure("get_task_links", Legacy(("taskId", task))), "get_task_links " + task);
        }
        foreach (var task in new[] { "ffffffff-0000-4000-8000-ffffffffffff", "ffffffff" })
            Check($"[unsupported_format] {future}", await Failure("get_task", Legacy(("taskId", task))), "get_task " + task);
        Check("[not_found] Task abcdef12 not found", await Failure("get_task", Legacy(("taskId", "abcdef12"))), "get_task abcdef12");

        Check($"[invalid] {conflict}", await Failure("get_field", Legacy(("field", "dddddddd-0000-4000-8000-dddddddddddd"))), "get_field conflict");
        var enumeration = await Failure("get_enum", Legacy(("enumeration", "eeeeeeee-0000-4000-8000-eeeeeeeeeeee")));
        if (!enumeration.StartsWith("[unsupported_format] later-eeeeeeee.yaml: format version 99 is newer"))
            mismatches.Add($"get_enum future: {enumeration}");
        Check("[not_found] Field 'Nope' not found", await Failure("get_field", Legacy(("field", "Nope"))), "get_field Nope");
        Assert.True(mismatches.Count == 0, string.Join("\n", mismatches));
    }
}
