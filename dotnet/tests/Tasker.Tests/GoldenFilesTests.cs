using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tasker.Core.Links;
using Tasker.Core.Tasks;
using Tasker.Core.Users;
using Tasker.Core.Workspace;
using Tasker.Storage.Files.Storages;
using Xunit;

namespace Tasker.Tests;

public class GoldenFilesTests
{
    private const string GoldenProjectId = "11111111-1111-4111-8111-111111111111";
    private const string LegacyProjectId = "22222222-2222-4222-8222-222222222222";

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        IndentSize = 2,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private enum Kind
    {
        Project, User, Status, StatusSet, TaskType, Series, LinkType, Field, Enum, Board, Task
    }

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

    private static string WorkspaceTasker => Path.Combine(Golden, "workspace", ".tasker");

    private static string MigratedTasker => Path.Combine(Golden, "expected", "migrate", "tasker");

    private static string[] YamlFiles(string root) =>
        Directory.GetFiles(root, "*.yaml", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();

    private static string Relative(string root, string file) => Path.GetRelativePath(root, file).Replace('\\', '/');

    private static SortedDictionary<string, byte[]> Files(string root)
    {
        var result = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        void Walk(string directory)
        {
            foreach (var entry in Directory.GetFileSystemEntries(directory))
            {
                if (Path.GetFileName(entry) == ".cache")
                    continue;
                if (Directory.Exists(entry))
                    Walk(entry);
                else
                    result[Relative(root, entry)] = File.ReadAllBytes(entry);
            }
        }
        Walk(root);
        return result;
    }

    private static void Copy(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from))
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        foreach (var directory in Directory.GetDirectories(from))
            Copy(directory, Path.Combine(to, Path.GetFileName(directory)));
    }

    private static Dictionary<string, string> Versions(string path) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path, Utf8))!;

    private static Guid ProjectOf(string folder) =>
        Guid.TryParse(folder, out var id)
            ? id
            : folder[^8..] switch
            {
                "11111111" => Guid.Parse(GoldenProjectId),
                "22222222" => Guid.Parse(LegacyProjectId),
                var other => throw new InvalidOperationException("unknown project folder " + other)
            };

    private static (Kind Kind, Guid Project) Classify(string relative)
    {
        var parts = relative.Split('/');
        switch (parts)
        {
            case ["users", _]:
                return (Kind.User, Guid.Empty);
            case ["projects", var id, "project.yaml"]:
                return (Kind.Project, ProjectOf(id));
            case ["projects", var id, var folder, _]:
                var kind = folder switch
                {
                    "statuses" => Kind.Status,
                    "status-sets" => Kind.StatusSet,
                    "task-types" => Kind.TaskType,
                    "series" => Kind.Series,
                    "link-types" => Kind.LinkType,
                    "fields" => Kind.Field,
                    "enums" => Kind.Enum,
                    "boards" => Kind.Board,
                    "tasks" => Kind.Task,
                    _ => throw new InvalidOperationException("unknown folder " + folder)
                };
                return (kind, ProjectOf(id));
            default:
                throw new InvalidOperationException("unexpected path " + relative);
        }
    }

    private static async Task<object?> Read(Kind kind, Guid project, string path, CancellationToken ct = default) => kind switch
    {
        Kind.Project => await ProjectFile.Read(path, ct),
        Kind.User => await UserFile.Read(path, ct),
        Kind.Status => await StatusFile.Read(project, path, ct),
        Kind.StatusSet => await StatusSetFile.Read(project, path, ct),
        Kind.TaskType => await TaskTypeFile.Read(project, path, ct),
        Kind.Series => await SeriesFile.Read(project, path, ct),
        Kind.LinkType => await LinkTypeFile.Read(project, path, ct),
        Kind.Field => await FieldFile.Read(project, path, ct),
        Kind.Enum => await FieldEnumFile.Read(project, path, ct),
        Kind.Board => await BoardFile.Read(project, path, ct),
        _ => await TaskFile.Read(project, path, ct)
    };

    private static Task Write(Kind kind, string path, object model, CancellationToken ct = default) => kind switch
    {
        Kind.Project => ProjectFile.Write(path, (Tasker.Core.Projects.Project)model, ct),
        Kind.User => UserFile.Write(path, (User)model, ct),
        Kind.Status => StatusFile.Write(path, (Tasker.Core.Statuses.Status)model, ct),
        Kind.StatusSet => StatusSetFile.Write(path, (Tasker.Core.Statuses.StatusSet)model, ct),
        Kind.TaskType => TaskTypeFile.Write(path, (Tasker.Core.Tasks.TaskType)model, ct),
        Kind.Series => SeriesFile.Write(path, (Tasker.Core.TaskSeries.Series)model, ct),
        Kind.LinkType => LinkTypeFile.Write(path, (LinkType)model, ct),
        Kind.Field => FieldFile.Write(path, (Tasker.Core.Fields.FieldDefinition)model, ct),
        Kind.Enum => FieldEnumFile.Write(path, (Tasker.Core.Fields.FieldEnum)model, ct),
        Kind.Board => BoardFile.Write(path, (Tasker.Core.Boards.Board)model, ct),
        _ => TaskFile.Write(path, (TaskItem)model, ct)
    };

    private static async Task<(Guid Id, string? Version, byte[] Bytes)> RoundTrip(Kind kind, Guid project, string file, string scratch)
    {
        var model = await Read(kind, project, file) ?? throw new InvalidOperationException(file + " is empty");
        var target = Path.Combine(scratch, Guid.NewGuid().ToString("N") + ".yaml");
        await Write(kind, target, model);
        var id = (Guid)model.GetType().GetProperty("Id")!.GetValue(model)!;
        var version = model.GetType().GetProperty("Version")?.GetValue(model) as string;
        return (id, version, await File.ReadAllBytesAsync(target));
    }

    private static (string Path, byte[] Bytes) MigratedFile(string relative, Guid id)
    {
        var parts = Path.GetDirectoryName(relative)!.Replace('\\', '/').Split('/').ToList();
        if (parts.Count >= 2 && parts[0] == "projects")
        {
            var id8 = parts[1][..8];
            parts[1] = Directory.GetDirectories(Path.Combine(MigratedTasker, "projects"))
                .Select(Path.GetFileName)
                .First(x => x!.EndsWith("-" + id8, StringComparison.Ordinal) || x.StartsWith(id8, StringComparison.Ordinal))!;
        }
        var folder = parts.Aggregate(MigratedTasker, Path.Combine);
        var needle = $"id: {id}";
        foreach (var file in YamlFiles(folder))
        {
            var bytes = File.ReadAllBytes(file);
            if (Utf8.GetString(bytes).Contains(needle, StringComparison.Ordinal))
                return (file, bytes);
        }
        throw new InvalidOperationException("no migrated file for " + relative);
    }

    private static bool IsCurrentLf(byte[] bytes, string text) =>
        !(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        && !bytes.Contains((byte)'\r')
        && text.StartsWith($"formatVersion: {FormatVersions.Current}\n", StringComparison.Ordinal);

    [Fact]
    public void Versions_of_every_file_match_versions_json()
    {
        var path = Path.Combine(Golden, "versions.json");
        var actual = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in YamlFiles(WorkspaceTasker))
            actual[Relative(WorkspaceTasker, file)] = YamlFile.VersionOf(File.ReadAllBytes(file));
        if (Updating)
            File.WriteAllText(path, JsonSerializer.Serialize(actual, Pretty) + "\n", Utf8);

        var expected = Versions(path);
        var wrong = actual.Where(x => !expected.TryGetValue(x.Key, out var version) || version != x.Value)
            .Select(x => $"{x.Key}: expected {(expected.TryGetValue(x.Key, out var v) ? v : "<none>")}, actual {x.Value}").ToArray();
        Assert.True(wrong.Length == 0, string.Join("\n", wrong));
        Assert.Equal(expected.Count, actual.Count);
        Assert.True(actual.Count > 100);
    }

    [Fact]
    public async Task Every_workspace_file_round_trips_byte_for_byte()
    {
        var scratch = Directory.CreateTempSubdirectory("tasker-golden-files-").FullName;
        try
        {
            var expectedVersions = Versions(Path.Combine(Golden, "versions.json"));
            var migratedVersions = Versions(Path.Combine(Golden, "expected", "migrate", "versions.json"));
            int exact = 0, viaMigrate = 0;
            var failures = new List<string>();
            foreach (var file in YamlFiles(WorkspaceTasker))
            {
                var relative = Relative(WorkspaceTasker, file);
                var (kind, project) = Classify(relative);
                var bytes = await File.ReadAllBytesAsync(file);
                var text = Utf8.GetString(bytes.AsSpan(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0));

                if (relative.EndsWith("conflicted-title-ours-cccccccc.yaml", StringComparison.Ordinal))
                {
                    var error = await Assert.ThrowsAsync<Tasker.Core.TaskerValidationException>(() => RoundTrip(kind, project, file, scratch));
                    Assert.Equal("Unresolved git merge conflict (line 3)", error.Message);
                    continue;
                }
                if (relative.EndsWith("from-the-future-ffffffff.yaml", StringComparison.Ordinal))
                {
                    var error = await Assert.ThrowsAsync<UnsupportedFormatException>(() => RoundTrip(kind, project, file, scratch));
                    Assert.Equal("from-the-future-ffffffff.yaml: format version 99 is newer than this Tasker supports (13): update Tasker", error.Message);
                    continue;
                }

                var (id, version, serialized) = await RoundTrip(kind, project, file, scratch);
                if (version != null)
                    Assert.Equal(expectedVersions[relative], version);
                Assert.Equal(expectedVersions[relative], YamlFile.VersionOf(bytes));

                if (IsCurrentLf(bytes, text))
                {
                    exact++;
                    if (!serialized.AsSpan().SequenceEqual(bytes))
                        failures.Add($"{relative}\n--- expected\n{text}--- actual\n{Utf8.GetString(serialized)}");
                    continue;
                }

                viaMigrate++;
                var (migratedPath, migratedBytes) = MigratedFile(relative, id);
                var upgraded = FormatVersions.Upgrade(text, file);
                if (kind == Kind.Task && TaskFile.ReorderKeys(upgraded) is { } reordered)
                    upgraded = reordered;
                if (upgraded != Utf8.GetString(migratedBytes))
                    failures.Add($"{relative}: upgrade in memory differs from tasker migrate");
                var migratedRelative = Relative(MigratedTasker, migratedPath);
                Assert.Equal(migratedVersions[migratedRelative], YamlFile.VersionOf(migratedBytes));

                var (_, _, fromMigrated) = await RoundTrip(kind, project, migratedPath, scratch);
                if (!serialized.AsSpan().SequenceEqual(fromMigrated))
                    failures.Add($"{relative}: serialize(legacy) differs from serialize(migrated)");
                if (kind != Kind.LinkType)
                {
                    var migratedLf = Utf8.GetString(migratedBytes).Replace("\r\n", "\n");
                    if (Utf8.GetString(serialized) != migratedLf)
                        failures.Add($"{relative}: serialize differs from the migrated file");
                }
            }
            Assert.True(failures.Count == 0, $"{failures.Count} files differ:\n{string.Join("\n", failures)}");
            Assert.True(exact >= 100, $"only {exact} current-format files checked");
            Assert.Equal(19, viaMigrate);
        }
        finally
        {
            Directory.Delete(scratch, true);
        }
    }

    [Fact]
    public async Task Migrated_workspace_files_round_trip()
    {
        var scratch = Directory.CreateTempSubdirectory("tasker-golden-files-").FullName;
        try
        {
            var checkedCount = 0;
            var failures = new List<string>();
            foreach (var file in YamlFiles(MigratedTasker))
            {
                var relative = Relative(MigratedTasker, file);
                var bytes = await File.ReadAllBytesAsync(file);
                if (bytes.Contains((byte)'\r') || relative.Contains("cccccccc") || relative.Contains("ffffffff"))
                    continue;
                var (kind, project) = Classify(relative);
                var (_, _, serialized) = await RoundTrip(kind, project, file, scratch);
                var text = Utf8.GetString(bytes);
                var written = Utf8.GetString(serialized);
                if (kind == Kind.LinkType && !text.Contains("hierarchical:", StringComparison.Ordinal))
                {
                    if (!written.Contains("\nhierarchical: false\n", StringComparison.Ordinal))
                        failures.Add(relative + ": no hierarchical flag written");
                    continue;
                }
                if (written != text)
                    failures.Add($"{relative}\n--- expected\n{text}--- actual\n{written}");
                checkedCount++;
            }
            Assert.True(failures.Count == 0, $"{failures.Count} files differ:\n{string.Join("\n", failures)}");
            Assert.True(checkedCount >= 110, $"only {checkedCount} files checked");
        }
        finally
        {
            Directory.Delete(scratch, true);
        }
    }

    [Fact]
    public void Default_link_type_ids_follow_the_md5_rule()
    {
        var checkedCount = 0;
        foreach (var project in new[] { GoldenProjectId, LegacyProjectId })
        {
            var projectId = Guid.Parse(project);
            var folder = Path.Combine(WorkspaceTasker, "projects", project == GoldenProjectId ? "golden-11111111" : project, "link-types");
            foreach (var file in Directory.GetFiles(folder))
            {
                var text = File.ReadAllText(file, Utf8).Replace("\r\n", "\n").TrimStart('﻿');
                string Top(string key) => text.Split('\n').First(x => x.StartsWith(key + ": ", StringComparison.Ordinal))[(key.Length + 2)..].Trim().Trim('"', '\'');
                var name = Top("name");
                var id = Guid.Parse(Top("id"));
                if (DefaultLinkTypes.All.FirstOrDefault(x => x.Name == name) is { } definition)
                {
                    Assert.True(DefaultLinkTypes.IdOf(projectId, definition.Key) == id, $"{project} {name}");
                    checkedCount++;
                }
            }
        }
        Assert.Equal(12, checkedCount);
    }

    [Fact]
    public async Task Legacy_defaults_match_the_corpus()
    {
        var legacy = Guid.Parse(LegacyProjectId);
        var folder = Path.Combine(WorkspaceTasker, "projects", LegacyProjectId, "link-types");
        async Task<LinkType> ReadLink(string name) => (await LinkTypeFile.Read(legacy, Path.Combine(folder, name), CancellationToken.None))!;

        var blocks = await ReadLink("blocks-6d62a7a2.yaml");
        Assert.Equal(DefaultLinkTypes.IdOf(legacy, "blocks"), blocks.Id);
        Assert.False(blocks.AllowCycles);
        Assert.False(blocks.Hierarchical);
        var relates = await ReadLink("relates-6cad151c.yaml");
        Assert.True(relates.AllowCycles);
        Assert.False(relates.Hierarchical);
        Assert.Equal("relates to", relates.InwardName);
        var parent = await ReadLink("parent-child-c8c8fe73.yaml");
        Assert.False(parent.AllowCycles);
        Assert.False(parent.Hierarchical);

        var scratch = Directory.CreateTempSubdirectory("tasker-golden-files-").FullName;
        try
        {
            var bare = Path.Combine(scratch, "x.yaml");
            await File.WriteAllTextAsync(bare, "id: 6d62a7a2-ae30-a532-8d8c-cfd3591b38ee\nname: X\noutwardName: out\n");
            var bareType = (await LinkTypeFile.Read(legacy, bare, CancellationToken.None))!;
            Assert.Equal("out", bareType.InwardName);
            Assert.False(bareType.AllowCycles);

            var golden = Guid.Parse(GoldenProjectId);
            var taskFile = Path.Combine(scratch, "t.yaml");
            await File.WriteAllTextAsync(taskFile,
                "formatVersion: 9\nid: 00000001-0000-4000-8000-000000000001\ntitle: T\ntypeId: 00000002-0000-4000-8000-000000000002\nstatusId: 00000003-0000-4000-8000-000000000003\n" +
                "createdAt: 2026-01-01T00:00:00.0000000+00:00\nupdatedAt: 2026-01-01T00:00:00.0000000+00:00\nlinks:\n" +
                "- typeId: 00000004-0000-4000-8000-000000000004\n  taskId: 00000005-0000-4000-8000-000000000005\n" +
                "- typeId: 00000004-0000-4000-8000-000000000004\n  taskId: 00000005-0000-4000-8000-000000000005\nfutureKey: ignored\n");
            var task = (await TaskFile.Read(golden, taskFile, CancellationToken.None))!;
            Assert.Single(task.Links);
            Assert.Null(task.Description);
            Assert.Equal(golden, task.ProjectId);
            var rewritten = Path.Combine(scratch, "rewritten.yaml");
            await TaskFile.Write(rewritten, task, CancellationToken.None);
            Assert.Equal(1, (await File.ReadAllTextAsync(rewritten)).Split("taskId:").Length - 1);

            var field = Path.Combine(scratch, "f.yaml");
            await File.WriteAllTextAsync(field, "formatVersion: 9\nid: 00000001-0000-4000-8000-000000000001\nname: F\ntype: money\n");
            var fieldError = await Assert.ThrowsAsync<UnsupportedFormatException>(() => FieldFile.Read(golden, field, CancellationToken.None));
            Assert.Equal("f.yaml: unknown field type 'money': update Tasker", fieldError.Message);

            var own = Path.Combine(scratch, "t2.yaml");
            await File.WriteAllTextAsync(own,
                "formatVersion: 9\nid: 00000001-0000-4000-8000-000000000001\ntitle: T\nfields:\n- id: 00000002-0000-4000-8000-000000000002\n  name: Own\n  type: Money\n");
            var ownError = await Assert.ThrowsAsync<UnsupportedFormatException>(() => TaskFile.Read(golden, own, CancellationToken.None));
            Assert.Equal("t2.yaml: unknown field type 'Money': update Tasker", ownError.Message);

            var board = Path.Combine(scratch, "b.yaml");
            await File.WriteAllTextAsync(board,
                "formatVersion: 9\nid: 00000001-0000-4000-8000-000000000001\nname: B\ncolumns:\n- id: 00000002-0000-4000-8000-000000000002\n  name: C\n  fieldFilters:\n  - field: 00000003-0000-4000-8000-000000000003\n    op: between\n");
            Assert.Null((await BoardFile.Read(golden, board, CancellationToken.None))!.Columns.Single().Query);

            var intField = Path.Combine(scratch, "f2.yaml");
            await File.WriteAllTextAsync(intField, "formatVersion: 9\nid: 00000001-0000-4000-8000-000000000001\nname: F\ntype: Int\n");
            Assert.Equal(Tasker.Core.Fields.FieldType.Int, (await FieldFile.Read(golden, intField, CancellationToken.None))!.Type);

            var agent = (await UserFile.Read(Path.Combine(WorkspaceTasker, "users", "00000083-0000-4000-8000-000000000083.yaml"), CancellationToken.None))!;
            Assert.Equal(UserKind.Agent, agent.Kind);
            var humanFile = Path.Combine(scratch, "u.yaml");
            await File.WriteAllTextAsync(humanFile, "id: 00000083-0000-4000-8000-000000000083\nusername: x\nkind: Human\ncreatedAt: 2026-01-01T00:00:00.0000000+00:00\n");
            var human = (await UserFile.Read(humanFile, CancellationToken.None))!;
            Assert.Equal(UserKind.Human, human.Kind);
            var humanOut = Path.Combine(scratch, "u-out.yaml");
            await UserFile.Write(humanOut, human, CancellationToken.None);
            Assert.DoesNotContain("kind:", await File.ReadAllTextAsync(humanOut));
        }
        finally
        {
            Directory.Delete(scratch, true);
        }
    }

    [Fact]
    public async Task Scalars_match_yamldotnet_style_probes()
    {
        var scratch = Directory.CreateTempSubdirectory("tasker-golden-files-").FullName;
        try
        {
            var failures = new List<string>();
            var checkedCount = 0;
            foreach (var (name, key) in new[] { ("titles.json", "title"), ("descriptions.json", "description") })
            {
                var path = Path.Combine(Golden, "yaml-style", name);
                var probes = JsonNode.Parse(await File.ReadAllTextAsync(path, Utf8))!.AsArray();
                var index = 0;
                foreach (var probe in probes)
                {
                    var value = probe!["value"]!.GetValue<string>();
                    var task = new TaskItem
                    {
                        Id = Guid.Parse("00000001-0000-4000-8000-000000000001"),
                        ProjectId = Guid.Parse(GoldenProjectId),
                        Title = key == "title" ? value : "T",
                        Description = key == "description" ? value : null,
                        TypeId = Guid.Parse("00000002-0000-4000-8000-000000000002"),
                        StatusId = Guid.Parse("00000003-0000-4000-8000-000000000003"),
                        CreatedAt = DateTimeOffset.Parse("2026-01-01T00:00:00+00:00"),
                        UpdatedAt = DateTimeOffset.Parse("2026-01-01T00:00:00+00:00"),
                        Version = "0"
                    };
                    var file = Path.Combine(scratch, $"{name}-{index++}.yaml");
                    await TaskFile.Write(file, task, CancellationToken.None);
                    var lines = (await File.ReadAllTextAsync(file, Utf8)).Split('\n');
                    var start = Array.FindIndex(lines, x => x.StartsWith(key + ":", StringComparison.Ordinal));
                    var taken = new List<string> { lines[start] };
                    for (var i = start + 1; i < lines.Length && (lines[i].Length == 0 || lines[i][0] == ' '); i++)
                        taken.Add(lines[i]);
                    var actual = string.Join('\n', taken).TrimEnd('\n');

                    if (Updating)
                        probe["yaml"] = actual;
                    checkedCount++;
                    var expected = probe["yaml"]!.GetValue<string>();
                    if (actual != expected)
                        failures.Add($"{probe["file"]} {JsonSerializer.Serialize(value, Pretty)}\n  expected: {JsonSerializer.Serialize(expected, Pretty)}\n  actual:   {JsonSerializer.Serialize(actual, Pretty)}");
                }
                if (Updating)
                    await File.WriteAllTextAsync(path, probes.ToJsonString(Pretty) + "\n", Utf8);
            }
            Assert.True(checkedCount >= 70, $"only {checkedCount} probes");
            Assert.True(failures.Count == 0, $"{failures.Count} mismatches:\n{string.Join("\n", failures)}");
        }
        finally
        {
            Directory.Delete(scratch, true);
        }
    }

    private static async Task<(string Out, string Err, int Code)> Console(string home, string cwd, params string[] args)
    {
        var info = new ProcessStartInfo(TaskerProcess.Binary)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding = Utf8,
            WorkingDirectory = cwd
        };
        foreach (var name in new[] { "TASKER_PROJECT", "TASKER_WIDTH", "TASKER_PROFILE", "TASKER_LANG", "COLUMNS", "LINES", "TASKER_GOLDEN_UPDATE" })
            info.Environment.Remove(name);
        info.Environment["TASKER_HOME"] = home;
        foreach (var arg in args)
            info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (await output, await error, process.ExitCode);
    }

    private static int ExpectedCode(string name) =>
        int.Parse(File.ReadAllText(Path.Combine(Golden, "expected", "migrate", name + ".code"), Utf8).Trim());

    [Fact]
    public async Task Migrate_rewrites_and_renames_the_corpus_exactly_like_the_snapshot()
    {
        var root = Directory.CreateTempSubdirectory("tasker-golden-migrate-").FullName;
        try
        {
            var home = Directory.CreateDirectory(Path.Combine(root, "home")).FullName;
            var workspace = Path.Combine(root, "golden");
            Copy(Path.Combine(Golden, "workspace"), workspace);
            var tasker = Path.Combine(workspace, ".tasker");
            var before = Files(tasker);

            foreach (var args in new[] { new[] { "migrate", "--check" }, ["migrate", "--check", "--json"], ["migrate", "--dry-run"] })
            {
                await Console(home, root, [.. args, "-w", workspace]);
                var after = Files(tasker);
                Assert.Equal(before.Keys, after.Keys);
                foreach (var (name, bytes) in before)
                    Assert.True(bytes.AsSpan().SequenceEqual(after[name]), $"{string.Join(' ', args)} changed {name}");
            }

            var migrated = await Console(home, root, "migrate", "-w", workspace);
            Assert.Equal(ExpectedCode("001-migrate"), migrated.Code);
            var actual = Files(tasker);

            if (Updating)
            {
                Directory.Delete(MigratedTasker, true);
                foreach (var (name, bytes) in actual)
                {
                    var target = Path.Combine(MigratedTasker, name);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.WriteAllBytes(target, bytes);
                }
                var versions = actual.Where(x => x.Key.EndsWith(".yaml", StringComparison.Ordinal))
                    .ToDictionary(x => x.Key, x => YamlFile.VersionOf(x.Value));
                File.WriteAllText(Path.Combine(Golden, "expected", "migrate", "versions.json"),
                    JsonSerializer.Serialize(new SortedDictionary<string, string>(versions, StringComparer.Ordinal), Pretty) + "\n", Utf8);
            }

            var expected = Files(MigratedTasker);
            Assert.Equal(expected.Keys, actual.Keys);
            var differ = expected.Where(x => !x.Value.AsSpan().SequenceEqual(actual[x.Key])).Select(x => x.Key).ToArray();
            Assert.True(differ.Length == 0, "bytes differ after migrate:\n" + string.Join("\n", differ));

            var versionsAfter = Versions(Path.Combine(Golden, "expected", "migrate", "versions.json"));
            foreach (var (name, bytes) in actual.Where(x => x.Key.EndsWith(".yaml", StringComparison.Ordinal)))
                Assert.Equal(versionsAfter[name], YamlFile.VersionOf(bytes));

            var again = await Console(home, root, "migrate", "-w", workspace);
            Assert.Equal(ExpectedCode("003-migrate-again"), again.Code);
            var third = Files(tasker);
            Assert.Equal(expected.Keys, third.Keys);
            foreach (var (name, bytes) in expected)
                Assert.True(bytes.AsSpan().SequenceEqual(third[name]), $"a second migrate changed {name}");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
