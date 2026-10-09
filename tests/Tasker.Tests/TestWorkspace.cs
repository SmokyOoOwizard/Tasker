using System.Diagnostics;
using System.Text.Json.Nodes;
using Tasker.Cli;
using Tasker.Daemon;

namespace Tasker.Tests;

public record CliResult(int Code, string Out, string Err)
{
    public JsonNode Json => JsonNode.Parse(Out)!;

    /// <summary>Первая строка списка с количеством найденного («Found 3»); null — вывод не список или с <c>-q</c>.</summary>
    public string? Found => Out.StartsWith("Found ") ? Out.Split('\n')[0] : null;

    /// <summary>Вывод списка без первой строки с итогом: только строки данных.</summary>
    public string Data => Found == null ? Out : string.Join('\n', Out.Split('\n').Skip(1));

    public Guid Id => Guid.Parse(Out.Trim().Split(' ')[^1]);
}

/// <summary>
/// Временная рабочая область для CLI: папка с файлами или файл SQLite. Команды идут либо внутри процесса тестов
/// (<see cref="CliApp.Run"/>), либо отдельным процессом <c>tasker</c> (<paramref name="asProcess"/> в <see cref="Create"/>) —
/// такой тест проверяет и чужой бинарник (<c>TASKER_BIN</c>, см. <see cref="TaskerProcess"/>).
/// </summary>
public sealed class TestWorkspace : IDisposable
{
    private readonly string[] _location;
    private readonly bool _asProcess;

    private TestWorkspace(string root, string[] location, bool asProcess)
    {
        Root = root;
        _location = location;
        _asProcess = asProcess;
    }

    public string Root { get; }

    /// <param name="storage"><c>files</c> или <c>sqlite</c>.</param>
    /// <param name="asProcess">Выполнять команды отдельным процессом <c>tasker</c>, а не внутри процесса тестов.</param>
    public static TestWorkspace Create(string storage, bool asProcess = false)
    {
        var root = Path.Combine(Path.GetTempPath(), "tasker-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return storage == "sqlite"
            ? new TestWorkspace(root, ["--sqlite", Path.Combine(root, "tasker.db")], asProcess)
            : new TestWorkspace(root, ["--workspace", root], asProcess);
    }

    public static IEnumerable<object[]> Storages() => [["files"], ["sqlite"]];

    /// <summary>Выполняет команду в этой области.</summary>
    public Task<CliResult> Run(params string[] args) => Execute([.. args, .. _location]);

    /// <summary>Та же команда, но заведомо отдельным процессом <c>tasker</c>.</summary>
    public Task<CliResult> RunProcess(params string[] args) => TaskerProcess.Run([.. args, .. _location]);

    /// <summary>Аргументы, выбирающие эту область.</summary>
    public string[] Location => _location;

    /// <summary>Команда внутри проекта <paramref name="project"/> (id или имя).</summary>
    public Task<CliResult> InProject(string project, params string[] args) => Execute([.. args, "--project", project, .. _location]);

    /// <summary>
    /// Команда с заданным вводом: <paramref name="input"/> — то, что «набрал пользователь» (null — ввод пуст, как EOF); interactive — подключён ли терминал.
    /// Отдельному процессу терминал не подключить, поэтому в режиме процесса доступен только неинтерактивный ввод.
    /// </summary>
    public Task<CliResult> RunWithInput(string? input, bool interactive, params string[] args)
    {
        if (!_asProcess)
            return Invoke([.. args, .. _location], input, interactive);
        if (interactive)
            throw new NotSupportedException("A separate tasker process has no terminal: interactive input works in-process only");
        return TaskerProcess.RunWithInput(input, [.. args, .. _location]);
    }

    private Task<CliResult> Execute(string[] args) => _asProcess ? TaskerProcess.Run(args) : Invoke(args);

    /// <summary>По умолчанию ввод не интерактивный: тест не должен зависеть от настоящего stdin.</summary>
    public static async Task<CliResult> Invoke(string[] args, string? input = null, bool interactive = false)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = await CliApp.Run(args, output, error, new StringReader(input ?? ""), interactive);
        return new CliResult(code, output.ToString(), error.ToString());
    }

    public void Dispose()
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

/// <summary>
/// Запуск настоящего процесса <c>tasker</c> — чтобы проверять работу нескольких процессов с одной папкой и чужой бинарник.
/// По умолчанию это <c>dotnet tasker.dll</c> из каталога тестов (туда ссылки на проекты кладут и консоль, и демон).
/// Переменная <c>TASKER_BIN</c> — путь к исполняемому файлу консоли: тогда запускается он, напрямую. Демон (<c>tasker-mcpd</c>)
/// консоль всегда ищет рядом с собой, поэтому он должен лежать в том же каталоге, что и <c>TASKER_BIN</c>; <c>TASKER_MCPD_BIN</c>
/// нужна только тестам, которые запускают демон сами (<see cref="DaemonCommand"/>), и по умолчанию равна этому соседу.
/// </summary>
public static class TaskerProcess
{
    /// <summary>Исполняемый файл консоли из <c>TASKER_BIN</c> (см. <see cref="TestRun"/>); null — тесты гоняют свою сборку через <c>dotnet</c>.</summary>
    public static string? Binary => TestRun.TaskerBin;

    /// <summary>Каталог, где лежат программы <c>tasker</c> и <c>tasker-mcpd</c> (или их dll).</summary>
    public static string ProgramDirectory =>
        Binary is { } binary ? Path.GetDirectoryName(Path.GetFullPath(binary))! : AppContext.BaseDirectory;

    /// <summary>Команда запуска консоли: сам бинарник или <c>dotnet</c> с путём к <c>tasker.dll</c>.</summary>
    public static (string File, string[] Arguments) Command() => CommandIn(ProgramDirectory);

    /// <summary>
    /// Команда запуска консоли, лежащей в <paramref name="directory"/> (например, в копии каталога программ): тот же способ,
    /// что и у <see cref="Command"/>, но файлы берутся оттуда.
    /// </summary>
    public static (string File, string[] Arguments) CommandIn(string directory) =>
        Binary is { } binary
            ? (Path.Combine(directory, Path.GetFileName(binary)), [])
            : ("dotnet", [Path.Combine(directory, "tasker.dll")]);

    /// <summary>
    /// Команда запуска демона <c>tasker-mcpd</c>: <c>TASKER_MCPD_BIN</c>, иначе программа рядом с <c>TASKER_BIN</c>, иначе —
    /// как находит его сама консоль тестовой сборки (<see cref="Launcher.DaemonCommand()"/>).
    /// </summary>
    public static (string File, string[] Arguments) DaemonCommand()
    {
        if (TestRun.TaskerMcpdBin is { } daemon)
            return (daemon, []);
        if (Binary is { } binary)
            return (Path.Combine(ProgramDirectory, Launcher.DaemonName + Path.GetExtension(binary)), []);
        return Launcher.DaemonCommand();
    }

    public static Process Start(params string[] args) => StartIn(null, args);

    /// <summary>Процесс <c>tasker</c>; <paramref name="workingDirectory"/> — его текущая папка (по умолчанию — как у теста).</summary>
    public static Process StartIn(string? workingDirectory, params string[] args) => Process.Start(StartInfo(workingDirectory, args))!;

    /// <summary>Описание процесса <c>tasker</c> с перенаправленным выводом — когда тесту нужно подправить окружение или ввод перед запуском.</summary>
    public static ProcessStartInfo StartInfo(string? workingDirectory, params string[] args) => StartInfo(Command(), workingDirectory, args);

    /// <summary>Как <see cref="StartInfo(string?, string[])"/>, но консоль берётся из каталога <paramref name="directory"/>.</summary>
    public static ProcessStartInfo StartInfoFrom(string directory, params string[] args) => StartInfo(CommandIn(directory), null, args);

    private static ProcessStartInfo StartInfo((string File, string[] Arguments) command, string? workingDirectory, string[] args)
    {
        var info = new ProcessStartInfo(command.File)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        if (workingDirectory != null)
            info.WorkingDirectory = workingDirectory;
        Tasker.Global.AppEnvironment.Apply(info);
        foreach (var arg in command.Arguments)
            info.ArgumentList.Add(arg);
        foreach (var arg in args)
            info.ArgumentList.Add(arg);
        return info;
    }

    /// <summary>
    /// Чужой процесс, удерживающий блокировку на файле, — как другой Tasker: .NET берёт на файле flock
    /// (<see cref="FileShare.None"/>), и python делает то же самое системным вызовом.
    /// </summary>
    public static LockHolder StartHolder(string path)
    {
        var info = new ProcessStartInfo("python3")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true
        };
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add("import fcntl,sys\nf=open(sys.argv[1],'a+')\nfcntl.flock(f,fcntl.LOCK_EX)\nprint('held',flush=True)\nsys.stdin.read()");
        info.ArgumentList.Add(path);
        return new LockHolder(Process.Start(info)!);
    }

    public static Task<CliResult> Run(params string[] args) => RunIn(null, args);

    /// <summary>Как <see cref="Run"/>, но в папке <paramref name="workingDirectory"/> — вместо смены текущей папки всего процесса тестов.</summary>
    public static Task<CliResult> RunIn(string? workingDirectory, params string[] args) => Run(StartInfo(workingDirectory, args), null);

    /// <summary>Как <see cref="Run"/>, но со стандартным вводом <paramref name="input"/> (null — пустой ввод, сразу EOF).</summary>
    public static Task<CliResult> RunWithInput(string? input, params string[] args) => Run(StartInfo(null, args), input ?? "");

    private static async Task<CliResult> Run(ProcessStartInfo info, string? input)
    {
        info.RedirectStandardInput = input != null;
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (input != null)
        {
            await process.StandardInput.WriteAsync(input);
            process.StandardInput.Close();
        }

        await process.WaitForExitAsync();
        return new CliResult(process.ExitCode, await output, await error);
    }
}

/// <summary>Отдельный процесс, который берёт блокировку и держит её, пока не закроют его stdin.</summary>
public sealed class LockHolder : IDisposable
{
    private readonly Process _process;

    public LockHolder(Process process) => _process = process;

    public async Task WaitUntilHeld()
    {
        var line = await _process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20));
        if (line != "held")
            throw new InvalidOperationException($"The holder did not take the lock: {line}");
    }

    public void Release()
    {
        _process.StandardInput.Close();
        _process.WaitForExit();
    }

    public void Dispose()
    {
        if (!_process.HasExited)
            _process.Kill();
        _process.Dispose();
    }
}
