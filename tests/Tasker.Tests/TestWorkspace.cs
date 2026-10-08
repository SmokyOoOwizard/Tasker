using System.Diagnostics;
using System.Text.Json.Nodes;
using Tasker.Cli;

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

/// <summary>Временная рабочая область для CLI: папка с файлами или файл SQLite.</summary>
public sealed class TestWorkspace : IDisposable
{
    private readonly string[] _location;

    private TestWorkspace(string root, string[] location)
    {
        Root = root;
        _location = location;
    }

    public string Root { get; }

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

    /// <summary>Та же команда, но отдельным процессом <c>tasker</c>.</summary>
    public Task<CliResult> RunProcess(params string[] args) => TaskerProcess.Run([.. args, .. _location]);

    /// <summary>Аргументы, выбирающие эту область.</summary>
    public string[] Location => _location;

    /// <summary>Команда внутри проекта <paramref name="project"/> (id или имя).</summary>
    public Task<CliResult> InProject(string project, params string[] args) => Invoke([.. args, "--project", project, .. _location]);

    /// <summary>Команда с заданным вводом: <paramref name="input"/> — то, что «набрал пользователь» (null — ввод пуст, как EOF); interactive — подключён ли терминал.</summary>
    public Task<CliResult> RunWithInput(string? input, bool interactive, params string[] args) => Invoke([.. args, .. _location], input, interactive);

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

/// <summary>Запуск настоящего процесса <c>tasker</c> — чтобы проверять работу нескольких процессов с одной папкой.</summary>
public static class TaskerProcess
{
    public static Process Start(params string[] args) => StartIn(null, args);

    /// <summary>Процесс <c>tasker</c>; <paramref name="workingDirectory"/> — его текущая папка (по умолчанию — как у теста).</summary>
    public static Process StartIn(string? workingDirectory, params string[] args)
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "tasker.dll");
        var info = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        if (workingDirectory != null)
            info.WorkingDirectory = workingDirectory;
        Tasker.Global.AppEnvironment.Apply(info);
        info.ArgumentList.Add(dll);
        foreach (var arg in args)
            info.ArgumentList.Add(arg);

        return Process.Start(info)!;
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
    public static async Task<CliResult> RunIn(string? workingDirectory, params string[] args)
    {
        using var process = StartIn(workingDirectory, args);
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
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
