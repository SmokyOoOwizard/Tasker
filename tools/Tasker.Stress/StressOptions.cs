namespace Tasker.Stress;

/// <summary>Параметры прогона стенда.</summary>
public sealed class StressOptions
{
    public static readonly string[] AllScenarios =
        ["create", "edit-same", "edit-fields", "edit-different", "locks", "cascade-wait", "mass", "fields", "links", "delete-while-edit", "delete-project", "kill", "multiws", "scale", "upgrade"];

    /// <summary>Сколько одновременных клиентов («агентов»).</summary>
    public int Clients { get; set; } = 10;

    /// <summary>Сколько операций выполняет каждый клиент в сценарии.</summary>
    public int Ops { get; set; } = 5;

    /// <summary><c>cli</c> — процессы tasker; <c>mcp</c> — HTTP к демону; <c>mix</c> — поровну.</summary>
    public string Client { get; set; } = "cli";

    /// <summary><c>files</c> — папка (YAML + индекс SQLite); <c>sqlite</c> — файл SQLite.</summary>
    public string Storage { get; set; } = "files";

    public List<string> Scenarios { get; set; } = [.. AllScenarios];

    /// <summary>Не удалять временную папку (посмотреть файлы после прогона).</summary>
    public bool Keep { get; set; }

    /// <summary>Где создать временную папку (по умолчанию — системная временная); внутри — свой TASKER_HOME, область и демон.</summary>
    public string? Dir { get; set; }

    /// <summary>Путь к tasker.dll; по умолчанию — рядом со стендом.</summary>
    public string? Tasker { get; set; }

    /// <summary>Записать отчёт ещё и в JSON.</summary>
    public string? JsonReport { get; set; }

    /// <summary>Долгие проверки: настоящее истечение блокировки (2 минуты), ожидание каскада до отказа (30 секунд).</summary>
    public bool Long { get; set; }

    /// <summary>Для сценария <c>scale</c>: числа клиентов.</summary>
    public int[] Scale { get; set; } = [1, 2, 5, 10];

    public int Seed { get; set; } = 12345;

    public bool UsesMcp => Client is "mcp" or "mix";

    public static StressOptions Parse(string[] args)
    {
        var o = new StressOptions();
        for (var i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]}: value expected");
            switch (args[i])
            {
                case "--clients": o.Clients = int.Parse(Next()); break;
                case "--ops": o.Ops = int.Parse(Next()); break;
                case "--client": o.Client = Next(); break;
                case "--storage": o.Storage = Next(); break;
                case "--scenarios": o.Scenarios = [.. Next().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)]; break;
                case "--keep": o.Keep = true; break;
                case "--dir": o.Dir = Next(); break;
                case "--tasker": o.Tasker = Next(); break;
                case "--json": o.JsonReport = Next(); break;
                case "--long": o.Long = true; break;
                case "--scale": o.Scale = [.. Next().Split(',').Select(int.Parse)]; break;
                case "--seed": o.Seed = int.Parse(Next()); break;
                default: throw new ArgumentException($"Unknown option {args[i]}");
            }
        }

        if (o.Client is not ("cli" or "mcp" or "mix"))
            throw new ArgumentException("--client: cli, mcp or mix");
        if (o.Storage is not ("files" or "sqlite"))
            throw new ArgumentException("--storage: files or sqlite");
        var unknown = o.Scenarios.Except(AllScenarios).ToArray();
        if (unknown.Length > 0)
            throw new ArgumentException($"Unknown scenarios: {string.Join(", ", unknown)}. Known: {string.Join(", ", AllScenarios)}");
        return o;
    }

    public const string Usage = """
        tasker-stress — parallel work stand: many clients (console, MCP, mix) in one workspace and project.

          --clients N        concurrent clients (default 10)
          --ops M            operations per client in a scenario (default 5)
          --client cli|mcp|mix   console processes, HTTP /mcp of an own daemon, or half and half (default cli)
          --storage files|sqlite (default files)
          --scenarios a,b,c  create, edit-same, edit-fields, edit-different, locks, cascade-wait, mass, fields, links, delete-while-edit, delete-project, kill, multiws, scale, upgrade (daemon replaced on the fly under load) (default: all)
          --scale 1,2,5,10   client counts of the scale scenario
          --long             also real lock expiry (2 min) and cascade wait until refusal (30 s)
          --keep             keep the temporary folder; --dir <path> puts it elsewhere
          --tasker <dll>     tasker.dll to drive (default: next to this program)
          --json <file>      write the report as JSON too
          --seed N           random seed

        Everything lives in a temporary folder: own TASKER_HOME, own workspace, own daemon on a free port.
        The real daemon (5719) and the repository data are never touched. Exit code 0 — all invariants hold, 1 — violations.
        """;
}
