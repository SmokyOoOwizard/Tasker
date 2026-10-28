using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace Tasker.Stress;

/// <summary>Результат одного вызова клиента.</summary>
public sealed record Op(Outcome Outcome, string Text, JsonNode? Json)
{
    public bool Ok => Outcome == Outcome.Ok;

    public bool Modified => Outcome == Outcome.Modified;
}

/// <summary>Задача глазами клиента.</summary>
public sealed record TaskView(Guid Id, string Version, string Title, string? Description, Guid StatusId, int[] Numbers, JsonNode Json)
{
    public static TaskView Of(JsonNode j) => new(
        Guid.Parse(j["id"]!.GetValue<string>()),
        j["version"]!.GetValue<string>(),
        j["title"]?.GetValue<string>() ?? "",
        j["description"]?.GetValue<string>(),
        Guid.Parse(j["statusId"]!.GetValue<string>()),
        j["seriesNumbers"]?.AsArray().Select(x => x!["number"]!.GetValue<int>()).ToArray() ?? [],
        j);
}

/// <summary>Связь у задачи: вид связи, направление относительно этой задачи и вторая задача.</summary>
public sealed record LinkView(string Type, string Direction, Guid Other);

/// <summary>Контекст прогона: куда писать вызовы и под каким сценарием.</summary>
public sealed class StressContext(StressOptions options, Stand stand, Recorder recorder)
{
    public StressOptions Options { get; } = options;

    public Stand Stand { get; } = stand;

    public Recorder Recorder { get; } = recorder;

    public string Scenario { get; set; } = "";

    /// <summary>Пока true, обрыв соединения с демоном — ожидаемый исход <see cref="Outcome.Down"/> (стенд сам его убил).</summary>
    public volatile bool ExpectDown;

    public Random Random { get; } = new(options.Seed);

    /// <summary>Клиенты на выбор: cli, mcp или чередование (mix).</summary>
    public List<StressClient> Clients(int count, Area? area = null)
    {
        area ??= Stand.Main;
        var list = new List<StressClient>();
        for (var i = 0; i < count; i++)
        {
            var mcp = Options.Client == "mcp" || Options.Client == "mix" && i % 2 == 1;
            list.Add(mcp
                ? new McpClient($"mcp-{i}", this, area, Stand.AgentOf(area, i))
                : new CliClient($"cli-{i}", this, area));
        }

        return list;
    }
}

/// <summary>
/// Один «агент». Все вызовы измеряются и записываются в <see cref="Recorder"/>; исход разбирается по тексту ошибки
/// (<c>Locked:</c>/<c>[locked]</c>, <c>Modified by someone else</c>/<c>[modified]</c>), чтобы сценарий мог отличить ожидаемый конфликт от поломки.
/// </summary>
public abstract class StressClient(string name, string kind, StressContext ctx, Area area)
{
    public string Name { get; } = name;

    public string Kind { get; } = kind;

    public Area Area { get; } = area;

    protected StressContext Ctx { get; } = ctx;

    protected abstract Task<(bool Ok, string Text)> CreateRaw(string title, string? description, bool series);

    protected abstract Task<(bool Ok, string Text)> GetRaw(Guid id);

    protected abstract Task<(bool Ok, string Text)> UpdateRaw(Guid id, string? version, string? title, string? description, Guid? status);

    protected abstract Task<(bool Ok, string Text)> DeleteRaw(Guid id, string? version);

    protected abstract Task<(bool Ok, string Text)> LinkRaw(Guid a, string phrase, Guid b, bool add);

    protected abstract Task<(bool Ok, string Text)> LinksRaw(Guid id);

    protected abstract Task<(bool Ok, string Text)> LockRaw(Guid id, bool acquire);

    protected abstract Task<(bool Ok, string Text)> LockShowRaw(Guid id);

    protected abstract Task<(bool Ok, string Text)> SeriesDeleteRaw();

    protected abstract Task<(bool Ok, string Text)> CleanupRaw();

    public static Outcome Classify(bool ok, string text)
    {
        if (ok)
            return Outcome.Ok;
        if (text.Contains("Modified by someone else") || text.Contains("[modified]"))
            return Outcome.Modified;
        if (text.Contains("Cycle: "))
            return Outcome.Invalid;
        if (text.StartsWith("Locked:") || text.Contains("[locked]"))
            return Outcome.Locked;
        // Проект удалён, пока шёл запрос; при удалении проекта его сущности исчезают по очереди — тип уже мог пропасть.
        if (text.Contains("[not_found]") || text.StartsWith("Not found:") || text.StartsWith("Error: No task") || text.Contains("No task '") || text.Contains("No project '")
            || text.Contains("project not found") || text.Contains("TypeId: not found in the project"))
            return Outcome.NotFound;
        return Outcome.Error;
    }

    private async Task<Op> Timed(string op, Func<Task<(bool Ok, string Text)>> call)
    {
        var watch = Stopwatch.StartNew();
        (bool Ok, string Text) result;
        try
        {
            result = await call();
        }
        catch (Exception e)
        {
            result = (false, "exception: " + e.Message);
        }

        watch.Stop();
        var outcome = Classify(result.Ok, result.Text);
        if (outcome == Outcome.Error && Ctx.ExpectDown && Kind == "mcp" && (result.Text.StartsWith("exception:") || result.Text.StartsWith("HTTP") || result.Text.Contains("is not available: opening")))
            outcome = Outcome.Down;
        Ctx.Recorder.Add(new Sample(Ctx.Scenario, Name, Kind, op, outcome, watch.Elapsed.TotalMilliseconds, outcome == Outcome.Ok ? null : Trim(result.Text)));
        JsonNode? json = null;
        if (result.Ok)
        {
            try
            {
                json = JsonNode.Parse(result.Text);
            }
            catch (System.Text.Json.JsonException)
            {
            }
        }

        return new Op(outcome, result.Text, json);
    }

    private static string Trim(string text) => text.Length <= 300 ? text : text[..300];

    public Task<Op> Create(string title, string? description = null, bool series = true) => Timed("create", () => CreateRaw(title, description, series));

    public Task<Op> Get(Guid id) => Timed("get", () => GetRaw(id));

    /// <summary><paramref name="version"/>: null — без проверки версии (консоль подставит текущую).</summary>
    public Task<Op> Update(Guid id, string? version, string? title = null, string? description = null, Guid? status = null) =>
        Timed("update", () => UpdateRaw(id, version, title, description, status));

    public Task<Op> Delete(Guid id, string? version = null) => Timed("delete", () => DeleteRaw(id, version));

    public Task<Op> Link(Guid a, string phrase, Guid b) => Timed("link", () => LinkRaw(a, phrase, b, true));

    public Task<Op> Unlink(Guid a, string phrase, Guid b) => Timed("unlink", () => LinkRaw(a, phrase, b, false));

    public Task<Op> Links(Guid id) => Timed("links", () => LinksRaw(id));

    public Task<Op> Lock(Guid id) => Timed("lock", () => LockRaw(id, true));

    public Task<Op> Unlock(Guid id) => Timed("unlock", () => LockRaw(id, false));

    public Task<Op> LockShow(Guid id) => Timed("get-lock", () => LockShowRaw(id));

    public Task<Op> DeleteSeries() => Timed("series-delete", SeriesDeleteRaw);

    public Task<Op> Cleanup() => Timed("cleanup", CleanupRaw);

    /// <summary>Читает задачу; null — не вышло (вызов уже записан и виден в отчёте).</summary>
    public async Task<TaskView?> Read(Guid id) => await Get(id) is { Ok: true, Json: { } json } ? TaskView.Of(json) : null;

    public async Task<LinkView[]?> ReadLinks(Guid id) => await Links(id) is { Ok: true, Json: { } json }
        ? json["links"]!.AsArray().Select(x => new LinkView(x!["typeName"]!.GetValue<string>(), x["direction"]!.GetValue<string>(), Guid.Parse(x["task"]!["id"]!.GetValue<string>()))).ToArray()
        : null;
}

/// <summary>Консоль: каждый вызов — отдельный процесс <c>tasker</c> (как у настоящего агента, вызывающего команды).</summary>
public sealed class CliClient(string name, StressContext ctx, Area area) : StressClient(name, "cli", ctx, area)
{
    private async Task<(bool, string)> Run(params string[] args)
    {
        var result = await Ctx.Stand.Cli(Area, true, args);
        return (result.Ok, result.Ok ? result.Out : result.Text);
    }

    protected override Task<(bool, string)> CreateRaw(string title, string? description, bool series)
    {
        var args = new List<string> { "task", "create", title, "--type", Area.TypeId.ToString(), "--json" };
        if (series)
            args.AddRange(["--series", Area.Prefix]);
        if (description != null)
            args.AddRange(["-d", description]);
        return Run([.. args]);
    }

    protected override Task<(bool, string)> GetRaw(Guid id) => Run("task", "get", id.ToString(), "--json");

    protected override Task<(bool, string)> UpdateRaw(Guid id, string? version, string? title, string? description, Guid? status)
    {
        var args = new List<string> { "task", "update", id.ToString(), "--json" };
        if (version != null)
            args.AddRange(["--expected-version", version]);
        if (title != null)
            args.AddRange(["--title", title]);
        if (description != null)
            args.AddRange(["-d", description]);
        if (status != null)
            args.AddRange(["--status", status.Value.ToString()]);
        return Run([.. args]);
    }

    protected override Task<(bool, string)> DeleteRaw(Guid id, string? version) =>
        version == null ? Run("task", "delete", id.ToString(), "--json") : Run("task", "delete", id.ToString(), "--expected-version", version, "--json");

    protected override Task<(bool, string)> LinkRaw(Guid a, string phrase, Guid b, bool add) =>
        Run("task", add ? "link" : "unlink", a.ToString(), phrase, b.ToString(), "--json");

    protected override Task<(bool, string)> LinksRaw(Guid id) => Run("task", "links", id.ToString(), "--json");

    protected override Task<(bool, string)> LockRaw(Guid id, bool acquire) => Run("lock", acquire ? "acquire" : "release", "task", id.ToString(), "--json");

    protected override Task<(bool, string)> LockShowRaw(Guid id) => Run("lock", "show", "task", id.ToString(), "--json");

    protected override Task<(bool, string)> SeriesDeleteRaw() => Run("series", "delete", Area.Prefix, "--json");

    protected override Task<(bool, string)> CleanupRaw() => Run("cleanup", "--json");
}

/// <summary>MCP: JSON-RPC по HTTP к демону стенда, у каждого агента свой заголовок <c>X-Tasker-Agent</c> (свой держатель блокировок).</summary>
public sealed class McpClient : StressClient
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(120) };
    private readonly Guid _agent;
    private int _id;

    public McpClient(string name, StressContext ctx, Area area, (string Name, Guid Id) agent) : base(name, "mcp", ctx, area)
    {
        _agent = agent.Id;
    }

    public Guid AgentId => _agent;

    /// <summary>Вызов инструмента: (успех, текст результата; для ошибки — её сообщение, начинающееся с кода <c>[locked]</c>).</summary>
    public async Task<(bool, string)> Tool(string tool, object args)
    {
        var arguments = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(args))!.AsObject();
        arguments["workspace"] = Area.Key;
        var body = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = Interlocked.Increment(ref _id),
            ["method"] = "tools/call",
            ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = arguments }
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, Ctx.Stand.McpUrl)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, new MediaTypeHeaderValue("application/json"))
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        request.Headers.Add("X-Tasker-Agent", _agent.ToString());

        using var response = await _http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            return (false, $"HTTP {(int)response.StatusCode}: {text}");

        var data = text.Split('\n').FirstOrDefault(x => x.StartsWith("data:"));
        if (data == null)
            return (false, "no data in response: " + text);
        var result = JsonNode.Parse(data["data:".Length..].Trim())!;
        if (result["error"] is { } error)
            return (false, "rpc error: " + error);
        var content = result["result"]!["content"]![0]!["text"]!.GetValue<string>();
        return (!(result["result"]!["isError"]?.GetValue<bool>() ?? false), content);
    }

    protected override Task<(bool, string)> CreateRaw(string title, string? description, bool series) =>
        Tool("create_task", new { projectId = Area.ProjectId, title, typeId = Area.TypeId, description, seriesIds = series ? new[] { Area.SeriesId } : null });

    protected override Task<(bool, string)> GetRaw(Guid id) => Tool("get_task", new { projectId = Area.ProjectId, taskId = id.ToString() });

    protected override async Task<(bool, string)> UpdateRaw(Guid id, string? version, string? title, string? description, Guid? status)
    {
        // MCP всегда требует версию: без неё берём текущую, как консоль.
        if (version == null)
        {
            var current = await GetRaw(id);
            if (!current.Item1)
                return current;
            version = JsonNode.Parse(current.Item2)!["version"]!.GetValue<string>();
        }

        return await Tool("update_task", new { projectId = Area.ProjectId, taskId = id.ToString(), version, title, description, statusId = status });
    }

    protected override async Task<(bool, string)> DeleteRaw(Guid id, string? version)
    {
        if (version == null)
        {
            var current = await GetRaw(id);
            if (!current.Item1)
                return current;
            version = JsonNode.Parse(current.Item2)!["version"]!.GetValue<string>();
        }

        var result = await Tool("delete_task", new { projectId = Area.ProjectId, taskId = id.ToString(), version });
        return (result.Item1, result.Item1 ? "{\"deleted\":true}" : result.Item2);
    }

    protected override Task<(bool, string)> LinkRaw(Guid a, string phrase, Guid b, bool add) =>
        Tool(add ? "link_tasks" : "unlink_tasks", new { projectId = Area.ProjectId, taskId = a.ToString(), link = phrase, otherTaskId = b.ToString() });

    protected override Task<(bool, string)> LinksRaw(Guid id) => Tool("get_task_links", new { projectId = Area.ProjectId, taskId = id.ToString() });

    protected override Task<(bool, string)> LockRaw(Guid id, bool acquire) =>
        Tool(acquire ? "lock_entity" : "unlock_entity", new { projectId = Area.ProjectId, entity = "task", entityId = id });

    protected override Task<(bool, string)> LockShowRaw(Guid id) => Tool("get_lock", new { projectId = Area.ProjectId, entity = "task", entityId = id });

    protected override async Task<(bool, string)> SeriesDeleteRaw()
    {
        var series = await Tool("get_series", new { projectId = Area.ProjectId, series = Area.SeriesId.ToString() });
        if (!series.Item1)
            return series;
        var result = await Tool("delete_series", new { projectId = Area.ProjectId, seriesId = Area.SeriesId, version = JsonNode.Parse(series.Item2)!["version"]!.GetValue<string>() });
        return (result.Item1, result.Item1 ? "{}" : result.Item2);
    }

    // В MCP чистки нет: это команда консоли. Отчёт о здоровье серий — ближайшее по смыслу чтение.
    protected override Task<(bool, string)> CleanupRaw() => Tool("series_health", new { projectId = Area.ProjectId });
}
