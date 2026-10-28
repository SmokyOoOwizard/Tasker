using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Tasker.Daemon;
using Tasker.Global;

namespace Tasker.Tests;

/// <summary>
/// Демон MCP в изолированном окружении: свой каталог данных, свободный порт, своё место для описания службы.
/// Все процессы <c>tasker</c>, которые запускает тест, получают эти переменные окружения и работают с этим же демоном.
/// </summary>
public sealed class DaemonFixture : IDisposable
{
    private readonly IsolatedHome _home = new();
    private readonly List<IDisposable> _overrides = [];

    public DaemonFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), "tasker-tests-" + Guid.NewGuid().ToString("N"));
        ServiceDir = Path.Combine(Root, "service");
        Directory.CreateDirectory(ServiceDir);
        _overrides.Add(AppEnvironment.Override("TASKER_SERVICE_DIR", ServiceDir));
        _overrides.Add(AppEnvironment.Override("TASKER_SERVICE_LABEL", "com.tasker.tests-" + Guid.NewGuid().ToString("N")[..8]));
        Port = FreePort();
        Store.SetPort(Port).GetAwaiter().GetResult();
    }

    public string Root { get; }

    public string ServiceDir { get; }

    public int Port { get; }

    public IsolatedHome Home => _home;

    public SettingsStore Store => _home.Store;

    public static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>Рабочая папка с проектом <paramref name="project"/>; добавляется в настройки MCP, если <paramref name="allow"/>.</summary>
    public async Task<string> Workspace(string name, string project, bool allow = true)
    {
        var folder = Directory.CreateDirectory(Path.Combine(Root, name)).FullName;
        var created = await TaskerProcess.Run("project", "create", project, "-w", folder);
        if (created.Code != 0)
            throw new InvalidOperationException(created.Err);
        if (allow)
            await Mcp("workspace", "add", folder);
        return folder;
    }

    public async Task<CliResult> Mcp(params string[] args)
    {
        var result = await TaskerProcess.Run(["mcp", .. args]);
        return result;
    }

    public async Task<DaemonStatus> Start()
    {
        var started = await Mcp("start", "--json");
        if (started.Code != 0)
            throw new InvalidOperationException($"start failed: {started.Err}");
        return (await Status())!;
    }

    public async Task<DaemonStatus?> Status()
    {
        var result = await Mcp("status", "--json");
        var node = result.Json["daemon"];
        return node == null ? null : System.Text.Json.JsonSerializer.Deserialize<DaemonStatus>(node.ToJsonString(), Json);
    }

    private static readonly System.Text.Json.JsonSerializerOptions Json = new(System.Text.Json.JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase) }
    };

    /// <summary>Адрес MCP демона: один на все рабочие области.</summary>
    public string McpUrl => $"http://127.0.0.1:{Port}/mcp";

    /// <summary>
    /// Вызывает инструмент MCP и возвращает текст результата (или HTTP-код, если ответ не 200).
    /// <paramref name="workspace"/> — имя области (аргумент <c>workspace</c>); null — аргумент не передаётся.
    /// </summary>
    public async Task<string> CallTool(string? workspace, string tool, object? arguments = null, string? host = null, string? url = null)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        var request = new HttpRequestMessage(HttpMethod.Post, url ?? McpUrl)
        {
            Content = new StringContent(RpcBody("tools/call", ToolCall(workspace, tool, arguments)), Encoding.UTF8, new MediaTypeHeaderValue("application/json"))
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        if (host != null)
            request.Headers.Host = host;

        using var response = await http.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            return ((int)response.StatusCode).ToString();

        // Ответ — поток SSE: строка «data: {json}».
        var data = body.Split('\n').First(x => x.StartsWith("data:"))["data:".Length..].Trim();
        return JsonNode.Parse(data)!["result"]!["content"]![0]!["text"]!.GetValue<string>();
    }

    /// <summary>HTTP-код ответа на POST по произвольному адресу (например, по старому адресу MCP области).</summary>
    public async Task<int> PostStatus(string url)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(RpcBody("tools/list", new JsonObject()), Encoding.UTF8, new MediaTypeHeaderValue("application/json"))
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        using var response = await http.SendAsync(request);
        return (int)response.StatusCode;
    }

    private static string RpcBody(string method, JsonNode parameters) =>
        new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = method, ["params"] = parameters }.ToJsonString();

    private static JsonObject ToolCall(string? workspace, string tool, object? arguments)
    {
        var args = arguments == null ? new JsonObject() : JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(arguments))!.AsObject();
        if (workspace != null)
            args["workspace"] = workspace;
        return new JsonObject { ["name"] = tool, ["arguments"] = args };
    }

    /// <summary>Любой запрос JSON-RPC к MCP; возвращает <c>result</c> (например, <c>tools/list</c>).</summary>
    public async Task<JsonNode> Rpc(string method, JsonNode? parameters = null, IReadOnlyDictionary<string, string>? headers = null)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        var request = new HttpRequestMessage(HttpMethod.Post, McpUrl)
        {
            Content = new StringContent(RpcBody(method, parameters ?? new JsonObject()), Encoding.UTF8, new MediaTypeHeaderValue("application/json"))
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        foreach (var (name, value) in headers ?? new Dictionary<string, string>())
            request.Headers.Add(name, value);

        using var response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        var data = body.Split('\n').First(x => x.StartsWith("data:"))["data:".Length..].Trim();
        return JsonNode.Parse(data)!["result"]!;
    }

    /// <summary>Вызов инструмента с признаком ошибки: <c>IsError</c> и текст результата (для ошибки — её сообщение, например <c>[invalid] …</c>).</summary>
    public async Task<(bool IsError, string Text)> CallToolResult(string? workspace, string tool, object? arguments = null, IReadOnlyDictionary<string, string>? headers = null)
    {
        var result = await Rpc("tools/call", ToolCall(workspace, tool, arguments), headers);
        return (result["isError"]?.GetValue<bool>() ?? false, result["content"]![0]!["text"]!.GetValue<string>());
    }

    public static string[] ProjectNames(string toolResult) =>
        JsonNode.Parse(toolResult)!["data"]!.AsArray().Select(x => x!["name"]!.GetValue<string>()).Order().ToArray();

    public void Dispose()
    {
        // Демон мог остаться от упавшего теста.
        try
        {
            if (DaemonFiles.IsRunning() && DaemonFiles.ReadInfo() is { } info)
            {
                try
                {
                    System.Diagnostics.Process.GetProcessById(info.Pid).Kill();
                }
                catch (ArgumentException)
                {
                }

                for (var i = 0; i < 50 && DaemonFiles.IsRunning(); i++)
                    Thread.Sleep(100);
            }
        }
        finally
        {
            for (var i = _overrides.Count - 1; i >= 0; i--)
                _overrides[i].Dispose();
            _home.Dispose();
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
