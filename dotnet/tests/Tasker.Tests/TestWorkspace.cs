using System.Diagnostics;
using System.Text.Json.Nodes;

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
/// Временная рабочая область для CLI: папка с файлами или файл SQLite. Команды выполняются отдельным процессом консоли
/// <c>tasker</c> (<see cref="TaskerProcess"/>).
/// </summary>
public sealed class TestWorkspace : IDisposable
{
    private readonly string[] _location;

    private TestWorkspace(string root, string[] location)
    {
        Root = root;
        _location = location;
    }

    public string Root { get; }

    /// <param name="storage"><c>files</c> или <c>sqlite</c>.</param>
    public static TestWorkspace Create(string storage)
    {
        var root = Path.Combine(Path.GetTempPath(), "tasker-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return storage == "sqlite"
            ? new TestWorkspace(root, ["--sqlite", Path.Combine(root, "tasker.db")])
            : new TestWorkspace(root, ["--workspace", root]);
    }

    public static IEnumerable<object[]> Storages() => [["files"], ["sqlite"]];

    /// <summary>Выполняет команду в этой области.</summary>
    public Task<CliResult> Run(params string[] args) => Invoke([.. args, .. _location]);

    /// <summary>То же, что <see cref="Run"/>: команда отдельным процессом <c>tasker</c>.</summary>
    public Task<CliResult> RunProcess(params string[] args) => Run(args);

    /// <summary>Аргументы, выбирающие эту область.</summary>
    public string[] Location => _location;

    /// <summary>Команда внутри проекта <paramref name="project"/> (id или имя).</summary>
    public Task<CliResult> InProject(string project, params string[] args) => Invoke([.. args, "--project", project, .. _location]);

    /// <summary>
    /// Команда с заданным вводом: <paramref name="input"/> — то, что «набрал пользователь» (null — ввод пуст, как EOF); <paramref name="interactive"/>
    /// — стандартный ввод консоли — терминал (псевдотерминал, <see cref="TaskerProcess.RunInTerminal"/>), а не перенаправленный поток.
    /// </summary>
    public Task<CliResult> RunWithInput(string? input, bool interactive, params string[] args) =>
        interactive ? TaskerProcess.RunInTerminal(input, [.. args, .. _location]) : Invoke([.. args, .. _location], input);

    /// <summary>
    /// Команда отдельным процессом <c>tasker</c> вне какой-либо области. Стандартный ввод — <paramref name="input"/> (по умолчанию пустой, сразу
    /// EOF): тест не должен зависеть от настоящего stdin.
    /// </summary>
    public static Task<CliResult> Invoke(string[] args, string? input = null) => TaskerProcess.RunWithInput(input, args);

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
/// Запуск настоящего процесса <c>tasker</c> (Rust, <c>rust/crates/tasker-cli</c>). По умолчанию — бинарник из каталога тестов: его и демон
/// <c>tasker-mcpd</c> туда кладёт сборка тестов (<c>tools/RustBinaries.targets</c>). Переменная <c>TASKER_BIN</c> — путь к другому
/// исполняемому файлу консоли. Демон консоль всегда ищет рядом с собой, поэтому он должен лежать в том же каталоге, что и консоль;
/// <c>TASKER_MCPD_BIN</c> нужна только тестам, которые запускают демон сами (<see cref="DaemonCommand"/>), и по умолчанию равна этому соседу.
/// </summary>
public static class TaskerProcess
{
    private static readonly System.Text.Encoding Utf8 = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private static string Executable(string name) => OperatingSystem.IsWindows() ? name + ".exe" : name;

    /// <summary>Исполняемый файл консоли: <c>TASKER_BIN</c> (см. <see cref="TestRun"/>) или <c>tasker</c> из каталога тестов.</summary>
    public static string Binary => Path.GetFullPath(TestRun.TaskerBin ?? Path.Combine(AppContext.BaseDirectory, Executable("tasker")));

    /// <summary>Каталог, где лежат программы <c>tasker</c> и <c>tasker-mcpd</c>.</summary>
    public static string ProgramDirectory => Path.GetDirectoryName(Binary)!;

    /// <summary>Команда запуска консоли.</summary>
    public static (string File, string[] Arguments) Command() => CommandIn(ProgramDirectory);

    /// <summary>Команда запуска консоли, лежащей в <paramref name="directory"/> (например, в копии каталога программ).</summary>
    public static (string File, string[] Arguments) CommandIn(string directory) => (Path.Combine(directory, Path.GetFileName(Binary)), []);

    /// <summary>Команда запуска демона <c>tasker-mcpd</c>: <c>TASKER_MCPD_BIN</c>, иначе программа рядом с консолью.</summary>
    public static (string File, string[] Arguments) DaemonCommand() =>
        (TestRun.TaskerMcpdBin ?? Path.Combine(ProgramDirectory, Executable(DaemonName)), []);

    /// <summary>Имя программы демона без расширения.</summary>
    public const string DaemonName = "tasker-mcpd";

    public static Process Start(params string[] args) => StartIn(null, args);

    public static Process StartWithoutInput(params string[] args)
    {
        var info = StartInfo(null, args);
        info.RedirectStandardInput = true;
        var process = Process.Start(info)!;
        process.StandardInput.Close();
        return process;
    }

    /// <summary>Процесс <c>tasker</c>; <paramref name="workingDirectory"/> — его текущая папка (по умолчанию — как у теста).</summary>
    public static Process StartIn(string? workingDirectory, params string[] args) => Process.Start(StartInfo(workingDirectory, args))!;

    /// <summary>Описание процесса <c>tasker</c> с перенаправленным выводом — когда тесту нужно подправить окружение или ввод перед запуском.</summary>
    public static ProcessStartInfo StartInfo(string? workingDirectory, params string[] args) => StartInfo(Command(), workingDirectory, args);

    /// <summary>Как <see cref="StartInfo(string?, string[])"/>, но консоль берётся из каталога <paramref name="directory"/>.</summary>
    public static ProcessStartInfo StartInfoFrom(string directory, params string[] args) => StartInfo(CommandIn(directory), null, args);

    private static ProcessStartInfo StartInfo((string File, string[] Arguments) command, string? workingDirectory, string[] args)
    {
        // Консоль пишет в каналы UTF-8 на любой ОС; без явной кодировки .NET на Windows читает их (и пишет ввод) в кодовой странице консоли.
        var info = new ProcessStartInfo(command.File)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding = Utf8
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

    /// <summary>
    /// Консоль, у которой стандартный ввод — псевдотерминал (как у человека за клавиатурой), а вывод и ошибки — по-прежнему потоки.
    /// В терминал «набирается» <paramref name="input"/>; ввод без перевода строки в конце завершается Ctrl+D (EOF). Псевдотерминал
    /// открывает python3 (модуль <c>pty</c>, только macOS и Linux).
    /// </summary>
    public static Task<CliResult> RunInTerminal(string? input, params string[] args)
    {
        if (OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("A pseudo-terminal for the console is opened with python3 pty: macOS and Linux only");

        var info = new ProcessStartInfo("python3") { RedirectStandardOutput = true, RedirectStandardError = true };
        Tasker.Global.AppEnvironment.Apply(info);
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add(TerminalScript);
        info.ArgumentList.Add(input ?? "");
        info.ArgumentList.Add(Binary);
        foreach (var arg in args)
            info.ArgumentList.Add(arg);
        return Run(info, null);
    }

    private const string TerminalScript = """
        import os, pty, subprocess, sys
        master, slave = pty.openpty()
        child = subprocess.Popen(sys.argv[2:], stdin=slave)
        os.close(slave)
        typed = sys.argv[1].encode()
        try:
            os.write(master, typed if typed.endswith(b"\n") else typed + b"\x04")
        except OSError:
            pass
        code = child.wait()
        os.close(master)
        sys.exit(code)
        """;

    public static Task<CliResult> Run(params string[] args) => RunIn(null, args);

    /// <summary>Как <see cref="Run"/>, но в папке <paramref name="workingDirectory"/> — вместо смены текущей папки всего процесса тестов.</summary>
    public static Task<CliResult> RunIn(string? workingDirectory, params string[] args) => Run(StartInfo(workingDirectory, args), null);

    /// <summary>Как <see cref="Run"/>, но со стандартным вводом <paramref name="input"/> (null — пустой ввод, сразу EOF).</summary>
    public static Task<CliResult> RunWithInput(string? input, params string[] args) => Run(StartInfo(null, args), input ?? "");

    private static async Task<CliResult> Run(ProcessStartInfo info, string? input)
    {
        info.RedirectStandardInput = input != null;
        info.StandardInputEncoding = input != null ? Utf8 : null;
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (input != null)
        {
            try
            {
                await process.StandardInput.WriteAsync(input);
                process.StandardInput.Close();
            }
            catch (IOException)
            {
                // Консоль завершилась, не читая ввод (например, отказала без терминала): это и проверяется.
            }
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
