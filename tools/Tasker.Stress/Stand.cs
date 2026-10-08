using System.Collections;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;

namespace Tasker.Stress;

public sealed record CliResult(int Code, string Out, string Err)
{
    public bool Ok => Code == 0;

    public JsonNode Json => JsonNode.Parse(Out)!;

    public string Text => (Err.Length > 0 ? Err : Out).Trim();
}

/// <summary>Рабочая область стенда с одним проектом: всё, что нужно клиентам, чтобы работать с ней.</summary>
public sealed class Area
{
    public required string Name { get; init; }

    /// <summary>Папка (режим files) или файл SQLite.</summary>
    public required string Path { get; init; }

    public required bool IsSqlite { get; init; }

    public string Project { get; init; } = "Demo";

    public Guid ProjectId { get; set; }

    public Guid TypeId { get; set; }

    public Guid Todo { get; set; }

    public Guid Done { get; set; }

    public Guid SeriesId { get; set; }

    public string Prefix { get; init; } = "TSK";

    /// <summary>Имя области для аргумента <c>workspace</c> в MCP (ключ у демона).</summary>
    public string Key { get; set; } = "";

    public string[] Location => IsSqlite ? ["--sqlite", Path] : ["--workspace", Path];

    /// <summary>Папка <c>.tasker</c> (только files).</summary>
    public string TaskerDir => System.IO.Path.Combine(Path, ".tasker");

    public string TasksDir => System.IO.Path.Combine(TaskerDir, "projects", ProjectId.ToString(), "tasks");
}

/// <summary>
/// Изолированный стенд: временная папка со своим <c>TASKER_HOME</c>, областью (папка или SQLite), агентами и своим демоном MCP
/// на свободном порту. Настоящий демон (5719) и данные репозитория не затрагиваются: каждый процесс получает наши переменные окружения.
/// </summary>
public sealed class Stand : IAsyncDisposable
{
    private readonly StressOptions _options;
    private readonly string _dll;
    private readonly Dictionary<string, string> _env = [];
    private int _daemonPid;

    public Stand(StressOptions options)
    {
        _options = options;
        _dll = options.Tasker ?? System.IO.Path.Combine(AppContext.BaseDirectory, "tasker.dll");
        if (!File.Exists(_dll))
            throw new FileNotFoundException($"tasker.dll not found: {_dll} (build the solution or pass --tasker)");

        Root = System.IO.Path.Combine(options.Dir ?? System.IO.Path.GetTempPath(), "tasker-stress-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(Root);
        Home = Directory.CreateDirectory(System.IO.Path.Combine(Root, "home")).FullName;
        var service = Directory.CreateDirectory(System.IO.Path.Combine(Root, "service")).FullName;
        _env["TASKER_HOME"] = Home;
        _env["TASKER_SERVICE_DIR"] = service;
        _env["TASKER_SERVICE_LABEL"] = "com.tasker.stress-" + Guid.NewGuid().ToString("N")[..8];

        // Windows: супервизор демона по умолчанию выключен, а сценарий upgrade (бесшовная замена, TSK-103/TSK-117) нужен именно ему.
        // Запускать на Windows: tasker-stress --scenarios upgrade (чек-лист «ПРОВЕРИТЬ НА WINDOWS» в описании задачи TSK-117 и на странице вики «Windows»).
        if (OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("TASKER_MCP_SUPERVISOR") == null)
            _env["TASKER_MCP_SUPERVISOR"] = "1";
    }

    public string Root { get; }

    public string Home { get; }

    public Area Main { get; private set; } = null!;

    public List<Area> Areas { get; } = [];

    public int Port { get; private set; }

    public string McpUrl => $"http://127.0.0.1:{Port}/mcp";

    public bool DaemonRunning => _daemonPid != 0;

    private readonly Dictionary<string, List<(string Name, Guid Id)>> _agents = [];

    /// <summary>Агент номер <paramref name="index"/> области: у каждого клиента-MCP свой — отсюда разные держатели блокировок (пользователи у каждой папки свои).</summary>
    public (string Name, Guid Id) AgentOf(Area area, int index)
    {
        var list = _agents[area.Path];
        return list[index % list.Count];
    }

    /// <summary>Создаёт агентов в области (до запуска клиентов MCP).</summary>
    public async Task CreateAgents(Area area)
    {
        var list = new List<(string Name, Guid Id)>();
        for (var i = 0; i < Math.Max(_options.Clients, 2); i++)
        {
            var name = $"agent{i}";
            var created = await Must(area, false, "agent", "create", name, "--json");
            list.Add((name, Guid.Parse(created.Json["id"]!.GetValue<string>())));
        }

        _agents[area.Path] = list;
    }

    public static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public ProcessStartInfo Info(params string[] args)
    {
        var info = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        // Чужие настройки Tasker (проект и область по умолчанию, порт демона…) не должны просочиться в стенд.
        foreach (DictionaryEntry e in Environment.GetEnvironmentVariables())
        {
            if (e.Key is string k && k.StartsWith("TASKER_", StringComparison.Ordinal))
                info.Environment.Remove(k);
        }

        foreach (var (k, v) in _env)
            info.Environment[k] = v;
        info.ArgumentList.Add(_dll);
        foreach (var a in args)
            info.ArgumentList.Add(a);
        return info;
    }

    public static async Task<CliResult> RunAsync(ProcessStartInfo info, CancellationToken ct = default)
    {
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync(ct);
        var error = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return new CliResult(process.ExitCode, await output, await error);
    }

    /// <summary>Команда tasker: <paramref name="area"/> добавляет <c>-w</c>/<c>--sqlite</c> и (если <paramref name="project"/>) <c>-p</c>.</summary>
    public Task<CliResult> Cli(Area? area, bool project, params string[] args) => RunAsync(Info(CliArgs(area, project, args)));

    public static string[] CliArgs(Area? area, bool project, string[] args) =>
        area == null ? args : [.. args, .. area.Location, .. (project ? new[] { "-p", area.Project } : [])];

    public async Task<CliResult> Must(Area? area, bool project, params string[] args)
    {
        var result = await Cli(area, project, args);
        if (!result.Ok)
            throw new InvalidOperationException($"tasker {string.Join(' ', args)}: {result.Text}");
        return result;
    }

    /// <summary>Новая рабочая область (папка или файл SQLite) с проектом «Demo».</summary>
    public async Task<Area> CreateArea(string name)
    {
        var path = _options.Storage == "sqlite"
            ? System.IO.Path.Combine(Root, name + ".db")
            : Directory.CreateDirectory(System.IO.Path.Combine(Root, name)).FullName;
        var area = await NewProject(new Area { Name = name, Path = path, IsSqlite = _options.Storage == "sqlite" }, "Demo");
        Areas.Add(area);
        Main ??= area;
        return area;
    }

    /// <summary>Свежий проект в той же области (у каждого сценария свой — счёт задач и номеров начинается с нуля): статусы, набор, тип, серия TSK.</summary>
    public async Task<Area> NewProject(Area workspace, string project)
    {
        var area = new Area { Name = workspace.Name, Path = workspace.Path, IsSqlite = workspace.IsSqlite, Project = project, Key = workspace.Key };

        string Id(CliResult r) => r.Json["id"]!.GetValue<string>();
        area.ProjectId = Guid.Parse(Id(await Must(area, false, "project", "create", project, "--json")));
        area.Todo = Guid.Parse(Id(await Must(area, true, "status", "create", "Todo", "--json")));
        area.Done = Guid.Parse(Id(await Must(area, true, "status", "create", "Done", "--json")));
        await Must(area, true, "status-set", "create", "Flow", "--status", "Todo", "--status", "Done");
        area.TypeId = Guid.Parse(Id(await Must(area, true, "task-type", "create", "Bug", "--status-set", "Flow", "--json")));
        area.SeriesId = Guid.Parse(Id(await Must(area, true, "series", "create", "Tasks", "--prefix", area.Prefix, "--json")));
        return area;
    }

    /// <summary>Основная область, агенты и (если нужны клиенты MCP) демон.</summary>
    public async Task Setup()
    {
        var main = await CreateArea("ws");
        if (_options.UsesMcp || _options.Scenarios.Contains("locks") || _options.Scenarios.Contains("multiws") || _options.Scenarios.Contains("upgrade"))
        {
            await CreateAgents(main);
            await StartDaemon();
        }
    }

    public async Task StartDaemon()
    {
        Port = FreePort();
        await Must(null, false, "mcp", "port", Port.ToString());
        foreach (var area in Areas)
            await Must(null, false, "mcp", "workspace", "add", area.Path);
        var started = await Must(null, false, "mcp", "start", "--json");
        ReadStatus(started.Json["status"]!);
    }

    private void ReadStatus(JsonNode status)
    {
        _daemonPid = status["pid"]!.GetValue<int>();
        Port = status["port"]!.GetValue<int>();
        foreach (var w in status["workspaces"]!.AsArray())
        {
            var path = w!["path"]!.GetValue<string>();
            if (Areas.FirstOrDefault(a => SamePath(a.Path, path)) is { } area)
                area.Key = w["key"]!.GetValue<string>();
        }
    }

    private static bool SamePath(string a, string b) =>
        System.IO.Path.GetFullPath(a).TrimEnd('/') == System.IO.Path.GetFullPath(b).TrimEnd('/')
        || Directory.Exists(a) && Directory.Exists(b) && new DirectoryInfo(a).ResolveLinkTarget(true)?.FullName == b
        || System.IO.Path.GetFullPath(a).Replace("/private", "") == System.IO.Path.GetFullPath(b).Replace("/private", "");

    /// <summary>Разрешает демону ещё одну область и ждёт, пока он её откроет.</summary>
    public async Task AddAreaToDaemon(Area area)
    {
        await Must(null, false, "mcp", "workspace", "add", area.Path);
        for (var i = 0; i < 100; i++)
        {
            var status = await Must(null, false, "mcp", "status", "--json");
            var workspaces = status.Json["daemon"]!["workspaces"]!.AsArray();
            if (workspaces.FirstOrDefault(w => SamePath(w!["path"]!.GetValue<string>(), area.Path)) is { } w && w["state"]!.GetValue<string>() == "open")
            {
                ReadStatus(status.Json["daemon"]!);
                return;
            }

            if (i == 20)
                await Must(null, false, "mcp", "restart");
            await Task.Delay(200);
        }

        throw new TimeoutException("The daemon did not open " + area.Path);
    }

    /// <summary>Убивает демон как «упал»: SIGKILL, без остановки.</summary>
    public void KillDaemon()
    {
        try
        {
            Process.GetProcessById(_daemonPid).Kill(entireProcessTree: true);
        }
        catch (ArgumentException)
        {
        }

        for (var i = 0; i < 100 && ProcessAlive(_daemonPid); i++)
            Thread.Sleep(50);
        _daemonPid = 0;
    }

    public static bool ProcessAlive(int pid)
    {
        try
        {
            return !Process.GetProcessById(pid).HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_daemonPid != 0)
            {
                await Cli(null, false, "mcp", "stop");
                KillDaemon();
            }
        }
        catch
        {
            // Лучшее, что можно сделать: ниже папка всё равно удаляется.
        }

        if (!_options.Keep)
        {
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
