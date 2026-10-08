using System.CommandLine;
using Tasker.Cli.Commands;
using Tasker.Cli.Completion;
using Tasker.Configs;
using Tasker.Core;
using Tasker.Daemon;
using Tasker.Daemon.Services;
using Tasker.Global;

namespace Tasker.Cli;

/// <summary>Команда <c>tasker</c>: разбор аргументов и выполнение. Вывод передаётся снаружи — так её проверяют тесты.</summary>
public static class CliApp
{
    /// <returns>Код выхода: 0 — успех, 1 — ошибка команды (сообщение в <paramref name="error"/>).</returns>
    public static Task<int> Run(string[] args, TextWriter output, TextWriter error) => Run(args, output, error, null);

    /// <summary>Как <see cref="Run(string[], TextWriter, TextWriter)"/>, но с заданным вводом: откуда читать ответы на вопросы и можно ли их задавать (нет — терминал не подключён).</summary>
    public static Task<int> Run(string[] args, TextWriter output, TextWriter error, TextReader input, bool interactive) =>
        Run(args, output, error, null, null, input, interactive);

    /// <param name="terminal">Терминал для обрезки строк; null — настоящий (тесты подставляют свой).</param>
    /// <param name="service">Служба автозапуска; null — настоящая для этой системы (тесты подставляют свою).</param>
    /// <param name="manual">Каталог справочника; null — встроенный (тесты подставляют свой).</param>
    /// <param name="environment">Переменные окружения для проверки опечаток; null — окружение процесса.</param>
    internal static Task<int> Run(string[] args, TextWriter output, TextWriter error, IServiceManager? service, IReadOnlyDictionary<string, string>? environment = null, TextReader? input = null, bool? interactive = null, ManualCatalog? manual = null, Terminal? terminal = null)
    {
        // Переменная TASKER_PROJCT вместо TASKER_PROJECT иначе молча не подействует. В stderr: stdout (в том числе --json) не портим.
        // Дополнение по Tab (директива [suggest]) молчит всегда: на каждое нажатие клавиши предупреждение было бы лишним шумом.
        var completing = args.Length > 0 && args[0].StartsWith("[suggest", StringComparison.Ordinal);
        if (!completing)
            foreach (var warning in ConfigDiagnostics.CheckEnvironment(environment))
                error.WriteLine($"Warning: {warning}");

        PerfTrace.Mark("env-check");
        // Дерево с одной веткой — только для обычного вызова «tasker <команда> ...»; директивы ([suggest]) и параметры вперёд команды — полное.
        var only = !completing && args.Length > 0 && args[0] is not ['-' or '[' or '/', ..] ? args[0] : null;
        var root = BuildRoot(output, error, service, input, interactive, manual, only, terminal);
        PerfTrace.Mark("build-root");
        var parsed = root.Parse(args);
        PerfTrace.Mark("parse");
        return parsed.InvokeAsync(new InvocationConfiguration { Output = output, Error = error, ProcessTerminationTimeout = TimeSpan.FromSeconds(15) });
    }

    /// <summary>Дерево всех команд <c>tasker</c>; по нему и разбирают аргументы, и проверяют справку в тестах.</summary>
    internal static RootCommand BuildRoot(TextWriter output, TextWriter error, IServiceManager? service = null, TextReader? input = null, bool? interactive = null, ManualCatalog? manual = null, string? only = null, Terminal? terminal = null)
    {
        var globals = new GlobalOptions(output, error, input, interactive, terminal);
        var root = new RootCommand("Tasker command line: works with a workspace folder (data in <folder>/.tasker) or a SQLite file");
        globals.AddTo(root);
        // Вместо директивы System.CommandLine — своя тихая и ограниченная (Completion/CompleteDirective.cs).
        root.Directives.Clear();
        root.Directives.Add(new CompleteDirective());

        // Для одной команды строим только её ветку: все дерево нужно справке, автодополнению и разбору ошибок (only == null).
        void Add(string name, Func<Command> make)
        {
            if (only == null || only == name)
                root.Subcommands.Add(make());
        }

        Add("project", () => ProjectCommands.Build(globals));
        Add("status", () => StatusCommands.Status(globals));
        Add("status-set", () => StatusCommands.StatusSet(globals));
        Add("task-type", () => TaskTypeCommands.Build(globals));
        Add("link-type", () => LinkTypeCommands.Build(globals));
        Add("field", () => FieldCommands.Fields(globals));
        Add("enum", () => FieldCommands.Enums(globals));
        Add("board", () => BoardCommands.Build(globals));
        Add("task", () => TaskCommands.Build(globals));
        Add("series", () => SeriesCommands.Build(globals));
        Add("cleanup", () => CleanupCommands.Build(globals));
        Add("migrate", () => MigrateCommands.Build(globals));
        Add("user", () => UserCommands.User(globals));
        Add("agent", () => UserCommands.Agent(globals));
        Add("lock", () => LockCommands.Lock(globals));
        Add("whoami", () => LockCommands.Whoami(globals, new SettingsStore()));
        Add("sync", () => SyncCommands.Build(globals));
        Add("hooks", () => HooksCommands.Build(globals));
        Add("manual", () => ManualCommands.Build(globals, manual ?? ManualCatalog.Embedded));
        Add("completion", () => CompletionCommands.Build(globals));
        Add("mcp", () => McpCommands.Build(globals, new SettingsStore(), new DaemonController(service ?? ServiceManagers.Create())));

        // Имени нет среди команд (опечатка, справка): нужно всё дерево, чтобы ошибка подсказала верные команды.
        if (only != null && root.Subcommands.Count == 0)
            return BuildRoot(output, error, service, input, interactive, manual, null, terminal);

        return root;
    }
}
