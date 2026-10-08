using System.Text.RegularExpressions;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Tasker.Cli;
using Tasker.Configs;
using Tasker.Daemon;
using Tasker.Daemon.Host;
using Tasker.Global;
using Tasker.Web;
using Xunit;

namespace Tasker.Tests;

/// <summary>Опечатки в аргументах и переменных окружения TASKER_* больше не игнорируются молча.</summary>
public partial class ConfigDiagnosticsTests
{
    private static readonly AConfigs[] Configs = ConfigsParser.GetConfigs([], sources: []);

    private static IReadOnlyList<string> Args(params string[] args) =>
        ConfigDiagnostics.Check(args, Configs, new Dictionary<string, string>());

    private static IReadOnlyList<string> Env(params (string Name, string Value)[] variables) =>
        ConfigDiagnostics.Check([], Configs, variables.ToDictionary(x => x.Name, x => x.Value));

    // ---- аргументы ----

    [Fact]
    public void A_typo_in_an_alias_is_reported_with_a_hint()
    {
        var warnings = Args("--sqllite=tasker.db");

        var warning = Assert.Single(warnings);
        Assert.Equal("Unknown argument --sqllite is ignored. Did you mean --sqlite?", warning);
    }

    [Fact]
    public void Swapped_letters_count_as_one_typo()
    {
        Assert.Equal("Unknown argument --mcpprot is ignored. Did you mean --mcpport?", Assert.Single(Args("--mcpprot", "6000")));
    }

    [Fact]
    public void The_hint_is_found_in_every_form_of_an_argument()
    {
        Assert.Contains("Did you mean --files?", Assert.Single(Args("--flies", "data")));
        Assert.Contains("Did you mean --files?", Assert.Single(Args("-fils=data")));
        Assert.Contains("Did you mean --postgres?", Assert.Single(Args("--postgress=Host=a")));
    }

    [Fact]
    public void Known_arguments_are_silent_in_any_form_and_case()
    {
        Assert.Empty(Args("--sqlite=a.db", "--files", "data", "--postgres=Host=a", "--db=b", "--mcpport", "5719", "--jwtkey=k"));
        Assert.Empty(Args("-sqlite=a.db", "--SQLITE", "a.db", "---Files=data", "--MCPPORT=1"));
        Assert.Empty(Args("--TASKER_DB_CONFIGS_SQLITE_FILE=a.db", "--tasker_mcp_configs_port=5719"));
    }

    [Fact]
    public void A_value_is_not_mistaken_for_an_argument()
    {
        // «sqllite_value» похоже на опечатку, но это значение --sqlite: у него нет дефисов.
        Assert.Empty(Args("--sqlite", "sqllite_value", "--files", "sqlite"));
    }

    [Fact]
    public void Arguments_of_aspnet_core_are_not_typos()
    {
        Assert.Empty(Args("--urls", "http://127.0.0.1:0", "--environment", "Development", "--contentRoot=/x", "--applicationName=a",
            "--Logging:LogLevel:Default=Debug", "--Kestrel__Endpoints__Http__Url=http://*:1", "--help", "-h"));
    }

    [Fact]
    public void An_argument_far_from_every_known_one_is_left_alone()
    {
        // Его может ждать другая часть хоста; без похожего известного имени подсказать нечего.
        Assert.Empty(Args("--banana", "--some-other-flag=1"));
    }

    [Fact]
    public void A_tasker_prefixed_key_that_does_not_exist_is_reported()
    {
        var typo = Assert.Single(Args("--TASKER_DB_CONFIGS_SQLITE_FIL=a.db"));
        Assert.Equal("Unknown argument --TASKER_DB_CONFIGS_SQLITE_FIL is ignored. Did you mean --TASKER_DB_CONFIGS_SQLITE_FILE?", typo);

        var unrelated = Assert.Single(Args("--TASKER_BANANA=1"));
        Assert.Equal("Unknown argument --TASKER_BANANA is ignored: it is not a Tasker setting.", unrelated);
    }

    [Fact]
    public void Several_typos_give_several_warnings_in_order()
    {
        var warnings = Args("--sqllite=a.db", "--files=data", "--mcpprot=1");

        Assert.Equal(2, warnings.Count);
        Assert.Contains("--sqllite", warnings[0]);
        Assert.Contains("--mcpprot", warnings[1]);
    }

    // ---- переменные окружения ----

    [Fact]
    public void A_typo_in_a_variable_is_reported_with_a_hint()
    {
        var warning = Assert.Single(Env(("TASKER_DB_CONFIGS_SQLITE_FIL", "a.db")));

        Assert.Equal(
            "Environment variable TASKER_DB_CONFIGS_SQLITE_FIL is not a Tasker setting and is ignored. Did you mean TASKER_DB_CONFIGS_SQLITE_FILE?",
            warning);
    }

    [Fact]
    public void Typos_of_the_other_tasker_variables_are_found_too()
    {
        Assert.Contains("Did you mean TASKER_PROJECT?", Assert.Single(Env(("TASKER_PROJCT", "Demo"))));
        Assert.Contains("Did you mean TASKER_HOME?", Assert.Single(Env(("TASKER_HOEM", "/x"))));
        Assert.Contains("Did you mean TASKER_SERVICE_LABEL?", Assert.Single(Env(("TASKER_SERVICE_LABLE", "x"))));
    }

    [Fact]
    public void A_tasker_variable_nobody_knows_is_reported_without_a_hint()
    {
        Assert.Equal(
            "Environment variable TASKER_SOMETHING_ELSE_ENTIRELY is not a Tasker setting and is ignored.",
            Assert.Single(Env(("TASKER_SOMETHING_ELSE_ENTIRELY", "1"))));
    }

    [Fact]
    public void Known_and_foreign_variables_are_silent()
    {
        var known = ConfigDiagnostics.OtherVariables.Select(x => (x, "v")).ToList();
        known.Add(("TASKER_DB_CONFIGS_SQLITE_FILE", "a.db"));
        known.Add(("TASKER_MCP_CONFIGS_PORT", "5719"));
        known.Add(("PATH", "/bin"));
        known.Add(("SQLITE", "x"));
        known.Add(("MY_TASKER_THING", "x"));

        Assert.Empty(Env(known.ToArray()));
    }

    [Fact]
    public void Variable_names_are_case_sensitive()
    {
        // На Unix имена переменных различаются по регистру: tasker_home — не TASKER_HOME (и не наша: префикс другой).
        Assert.Empty(Env(("tasker_home", "/x")));
    }

    [Fact]
    public void The_variables_are_listed_in_a_stable_order()
    {
        var warnings = Env(("TASKER_ZZZ_UNKNOWN", "1"), ("TASKER_AAA_UNKNOWN", "1"));

        Assert.Contains("TASKER_AAA_UNKNOWN", warnings[0]);
        Assert.Contains("TASKER_ZZZ_UNKNOWN", warnings[1]);
    }

    [Fact]
    public void The_process_environment_is_checked_when_none_is_given()
    {
        // Окружение процесса теста: ничего лишнего с префиксом TASKER_ там быть не должно (тесты задают их через AppEnvironment).
        var warnings = ConfigDiagnostics.CheckEnvironment();

        Assert.DoesNotContain(warnings, x => x.Contains("TASKER_HOME") || x.Contains("TASKER_PROJECT"));
    }

    // ---- подсказки ----

    [Theory]
    [InlineData("sqllite", "sqlite")]
    [InlineData("SQLITE", "sqlite")]
    [InlineData("sqlte", "sqlite")]
    [InlineData("mcpprot", "mcpport")]
    [InlineData("jwtky", "jwtkey")]
    public void The_nearest_known_name_is_suggested(string typo, string expected)
    {
        Assert.Equal(expected, ConfigDiagnostics.Suggest(typo, ["files", "sqlite", "postgres", "db", "mcpport", "jwtkey"]));
    }

    [Theory]
    [InlineData("banana")]
    [InlineData("sqlitefile")]
    [InlineData("xyz")]
    public void Nothing_is_suggested_when_nothing_is_close(string typo)
    {
        Assert.Null(ConfigDiagnostics.Suggest(typo, ["files", "sqlite", "postgres", "db", "mcpport", "jwtkey"]));
    }

    [Theory]
    [InlineData("abc", "abc", 0)]
    [InlineData("abc", "ABC", 0)]
    [InlineData("abc", "abd", 1)]
    [InlineData("abc", "ab", 1)]
    [InlineData("abc", "abcd", 1)]
    [InlineData("abc", "acb", 1)]
    [InlineData("kitten", "sitting", 3)]
    [InlineData("", "abc", 3)]
    public void Distance_counts_insertions_deletions_substitutions_and_swaps(string a, string b, int expected)
    {
        Assert.Equal(expected, ConfigDiagnostics.Distance(a, b));
    }

    // ---- реестр переменных ----

    [GeneratedRegex("\"(TASKER_[A-Z0-9_]+)\"")]
    private static partial Regex VariableLiteral();

    [Fact]
    public void Every_tasker_variable_named_in_the_sources_is_registered()
    {
        // Сторож от дрейфа: добавили в код новую переменную TASKER_… и забыли внести в ConfigDiagnostics.OtherVariables —
        // и она станет «опечаткой» с предупреждением при каждом запуске.
        var src = SourceRoot();
        var known = Configs.SelectMany(c => ConfigsParser_Keys(c)).Concat(ConfigDiagnostics.OtherVariables).ToHashSet();

        var unregistered = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(x => !x.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !x.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && Path.GetFileName(x) != "ConfigDiagnostics.cs")
            .SelectMany(file => VariableLiteral().Matches(File.ReadAllText(file)).Select(m => (File: Path.GetFileName(file), Name: m.Groups[1].Value)))
            .Where(x => !known.Contains(x.Name))
            .ToArray();

        Assert.True(unregistered.Length == 0,
            "Add to ConfigDiagnostics.OtherVariables: " + string.Join(", ", unregistered.Select(x => $"{x.Name} ({x.File})")));
    }

    private static IEnumerable<string> ConfigsParser_Keys(AConfigs config)
    {
        var type = config.GetType();
        return type.GetProperties().Where(x => x.CanWrite).Select(x => ConfigKeys.For(type, x));
    }

    private static string SourceRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "src", "Tasker.Configs")))
                return Path.Combine(directory.FullName, "src");
        }

        throw new DirectoryNotFoundException("src/ not found above the test binaries");
    }

    // ---- в хостах ----

    private sealed class ListSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];

        public void Emit(LogEvent logEvent)
        {
            lock (Events)
                Events.Add(logEvent);
        }

        public string[] Warnings
        {
            get
            {
                lock (Events)
                    return Events.Where(x => x.Level == LogEventLevel.Warning).Select(x => x.RenderMessage()).ToArray();
            }
        }
    }

    private static Serilog.ILogger Logger(ListSink sink) => new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();

    [Fact]
    public void The_web_host_logs_a_warning_for_a_typo()
    {
        var sink = new ListSink();

        TaskerWebApp.CreateBuilder<DaemonModule>(["--sqllite=x.db"], TaskerMode.Local, log: Logger(sink));

        Assert.Equal(["Unknown argument --sqllite is ignored. Did you mean --sqlite?"], sink.Warnings);
    }

    [Fact]
    public void The_web_host_is_silent_when_all_arguments_are_known()
    {
        var sink = new ListSink();

        TaskerWebApp.CreateBuilder<DaemonModule>(["--urls", "http://127.0.0.1:0", "--mcpport=6001"], TaskerMode.Local, log: Logger(sink));

        Assert.Empty(sink.Warnings);
    }

    [Fact]
    public async Task The_cli_warns_on_stderr_about_a_mistyped_variable_and_keeps_stdout_clean()
    {
        using var home = new IsolatedHome();
        var output = new StringWriter();
        var error = new StringWriter();

        var code = await CliApp.Run(["mcp", "config", "--json"], output, error, null,
            new Dictionary<string, string> { ["TASKER_PROJCT"] = "Demo" });

        Assert.Equal(0, code);
        Assert.Equal(
            "Warning: Environment variable TASKER_PROJCT is not a Tasker setting and is ignored. Did you mean TASKER_PROJECT?",
            error.ToString().Trim());
        // stdout остаётся чистым JSON: сценарии, которые его читают, не ломаются.
        Assert.NotNull(System.Text.Json.Nodes.JsonNode.Parse(output.ToString()));
    }

    [Fact]
    public async Task The_cli_is_silent_when_variables_are_right()
    {
        using var home = new IsolatedHome();
        var error = new StringWriter();

        var code = await CliApp.Run(["mcp", "config"], new StringWriter(), error, null,
            new Dictionary<string, string> { ["TASKER_PROJECT"] = "Demo", ["TASKER_HOME"] = home.Path, ["PATH"] = "/bin" });

        Assert.Equal(0, code);
        Assert.Empty(error.ToString());
    }

    [Fact]
    public async Task The_real_tasker_process_warns_about_a_mistyped_variable()
    {
        using var home = new IsolatedHome();
        using var typo = AppEnvironment.Override("TASKER_PROJCT", "Demo");

        var result = await TaskerProcess.Run("mcp", "port");

        Assert.Equal(0, result.Code);
        Assert.Equal("5719", result.Out.Trim());
        Assert.Contains("Warning: Environment variable TASKER_PROJCT is not a Tasker setting", result.Err);
        Assert.Contains("Did you mean TASKER_PROJECT?", result.Err);
    }
}
