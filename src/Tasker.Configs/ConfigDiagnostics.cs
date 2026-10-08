namespace Tasker.Configs;

/// <summary>
/// Опечатки в настройках. Парсер (<see cref="ConfigsParser"/>) молча пропускает всё, что не знает, — и <c>--sqllite=tasker.db</c>
/// просто не действует. Здесь такие случаи находят и описывают словами; хост пишет их в журнал, консоль — в stderr.
/// <list type="bullet">
/// <item>Переменная окружения <c>TASKER_*</c>, которой нет среди настроек и известных переменных Tasker, — предупреждение
/// (с подсказкой «возможно, вы имели в виду», если похожая есть).</item>
/// <item>Аргумент, похожий на известный (расстояние до алиаса или ключа не больше пары правок), но не совпадающий с ним, — предупреждение
/// с подсказкой. Аргумент с префиксом <c>TASKER_</c>, которого нет, — тоже. Остальные незнакомые аргументы не трогаем: их могут ждать
/// ASP.NET Core (<c>--urls</c>, <c>--Logging:LogLevel:Default=…</c>) или другие части хоста.</item>
/// </list>
/// </summary>
public static class ConfigDiagnostics
{
    /// <summary>
    /// Переменные <c>TASKER_*</c>, которые Tasker читает не как группы настроек, а напрямую. Появилась новая — допишите сюда
    /// (тест следит, чтобы в исходниках не осталось незарегистрированных <c>"TASKER_…"</c>).
    /// </summary>
    public static readonly string[] OtherVariables =
    [
        "TASKER_HOME",
        "TASKER_PROJECT",
        "TASKER_LANG",
        "TASKER_WIDTH",
        "TASKER_SERVICE_DIR",
        "TASKER_SERVICE_LABEL",
        "TASKER_FRONTEND_DEV_URL",
        "TASKER_PROFILE",
        "TASKER_MCP_OPEN_WAIT_MS",
        "TASKER_MCP_OPEN_DELAY_MS",
        "TASKER_MCP_DRAIN_QUIET_MS",
        "TASKER_MCP_DRAIN_TIMEOUT_MS",
        "TASKER_MCP_SUPERVISOR",
        "TASKER_MCP_HANDOFF"
    ];

    // Аргументы самого ASP.NET Core и хоста: они не настройки Tasker, но и не опечатки.
    private static readonly string[] FrameworkArguments =
    [
        "urls", "environment", "contentRoot", "applicationName", "webroot", "hostingStartupAssemblies", "hostingStartupExcludeAssemblies",
        "preventHostingStartup", "startupAssembly", "captureStartupErrors", "detailedErrors", "https_port", "shutdownTimeoutSeconds",
        "help", "h", "version", "?"
    ];

    private const string Prefix = "TASKER_";

    /// <summary>Предупреждения по аргументам командной строки и переменным окружения для уже найденных групп настроек.</summary>
    /// <param name="environment">Переменные окружения; по умолчанию — окружение процесса.</param>
    public static IReadOnlyList<string> Check(string[] args, IEnumerable<AConfigs> configs, IReadOnlyDictionary<string, string>? environment = null)
    {
        var keys = Keys(configs);
        return [.. CheckArguments(args, keys), .. CheckEnvironment(environment, keys)];
    }

    /// <summary>Только переменные окружения — для консольной утилиты, у которой нет групп настроек в аргументах.</summary>
    public static IReadOnlyList<string> CheckEnvironment(IReadOnlyDictionary<string, string>? environment = null)
    {
        // Быстрый путь (каждый вызов tasker): ключи настроек собираются рефлексией (десятки миллисекунд), а нужны они, только если есть
        // переменная TASKER_*, не входящая в OtherVariables. Обычно таких нет.
        var names = environment?.Keys.ToArray() ?? Environment.GetEnvironmentVariables().Keys.Cast<string>().ToArray();
        if (!names.Any(x => x.StartsWith(Prefix, StringComparison.Ordinal) && !OtherVariables.Contains(x, StringComparer.Ordinal)))
            return [];

        return CheckEnvironment(environment, Keys(ConfigsParser.GetConfigs([], sources: [])));
    }

    private sealed record KnownKeys(string[] FullKeys, string[] Aliases);

    private static KnownKeys Keys(IEnumerable<AConfigs> configs)
    {
        var fullKeys = new List<string>();
        var aliases = new List<string>();
        foreach (var config in configs)
        {
            var type = config.GetType();
            foreach (var property in ConfigsParser.Settable(type))
            {
                fullKeys.Add(ConfigKeys.For(type, property));
                aliases.AddRange(ConfigKeys.Aliases(property));
            }
        }

        return new KnownKeys(fullKeys.Distinct().ToArray(), aliases.Distinct().ToArray());
    }

    private static List<string> CheckEnvironment(IReadOnlyDictionary<string, string>? environment, KnownKeys keys)
    {
        var warnings = new List<string>();
        var variables = environment?.Keys.ToArray() ?? Environment.GetEnvironmentVariables().Keys.Cast<string>().ToArray();

        var known = keys.FullKeys.Concat(OtherVariables).ToArray();
        foreach (var name in variables.Where(x => x.StartsWith(Prefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            if (known.Contains(name, StringComparer.Ordinal))
                continue;

            var hint = Suggest(name, known) is { } suggestion ? $" Did you mean {suggestion}?" : "";
            warnings.Add($"Environment variable {name} is not a Tasker setting and is ignored.{hint}");
        }

        return warnings;
    }

    private static List<string> CheckArguments(string[] args, KnownKeys keys)
    {
        var warnings = new List<string>();
        var known = keys.Aliases.Concat(keys.FullKeys).ToArray();

        for (var i = 0; i < args.Length; i++)
        {
            var raw = args[i];
            if (!raw.StartsWith('-'))
                continue;

            var token = raw.TrimStart('-');
            var eq = token.IndexOf('=');
            var name = eq >= 0 ? token[..eq] : token;
            // «--ключ значение»: следующий аргумент — значение, а не ещё один ключ (как в ArgsConfigSource).
            if (eq < 0 && i + 1 < args.Length && !args[i + 1].StartsWith('-'))
                i++;

            if (name.Length == 0
                || known.Contains(name, StringComparer.OrdinalIgnoreCase)
                || FrameworkArguments.Contains(name, StringComparer.OrdinalIgnoreCase)
                || name.Contains(':') || name.Contains("__", StringComparison.Ordinal))
                continue;

            if (Suggest(name, known) is { } suggestion)
                warnings.Add($"Unknown argument --{name} is ignored. Did you mean --{suggestion}?");
            else if (name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
                warnings.Add($"Unknown argument --{name} is ignored: it is not a Tasker setting.");
        }

        return warnings;
    }

    /// <summary>Самое похожее известное имя (без учёта регистра), если оно отличается не больше чем парой правок.</summary>
    internal static string? Suggest(string name, IEnumerable<string> known)
    {
        var limit = Math.Clamp(name.Length / 3, 1, 3);
        string? best = null;
        var bestDistance = int.MaxValue;
        foreach (var candidate in known)
        {
            var distance = Distance(name, candidate);
            if (distance < bestDistance)
            {
                best = candidate;
                bestDistance = distance;
            }
        }

        return bestDistance <= limit ? best : null;
    }

    /// <summary>Расстояние Дамерау — Левенштейна (вставка, удаление, замена, перестановка соседних букв) без учёта регистра.</summary>
    internal static int Distance(string a, string b)
    {
        a = a.ToUpperInvariant();
        b = b.ToUpperInvariant();
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++)
            d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++)
            d[0, j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                    d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
            }
        }

        return d[a.Length, b.Length];
    }
}
