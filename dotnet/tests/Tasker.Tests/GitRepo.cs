using System.Diagnostics;
using Microsoft.Data.Sqlite;

namespace Tasker.Tests;

/// <summary>Настоящий репозиторий git во временной папке — для проверки хуков.</summary>
public sealed class GitRepo : IDisposable
{
    public GitRepo()
    {
        // Каталог и его канонический путь: на macOS /var — ссылка на /private/var, а git отдаёт настоящий.
        Root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "tasker-tests-" + Guid.NewGuid().ToString("N"))).FullName;
        Root = Tasker.Storage.Files.Workspaces.CanonicalPath.Of(Root);
        Git("init", "-q", "-b", "main");
        Git("config", "user.email", "tests@tasker");
        Git("config", "user.name", "Tasker tests");
        Git("config", "commit.gpgsign", "false");
    }

    public string Root { get; }

    public string Folder(string relative) => relative.Length == 0 ? Root : Directory.CreateDirectory(Path.Combine(Root, relative)).FullName;

    public string Hooks => Path.Combine(Root, ".git", "hooks");

    public string Hook(string name) => Path.Combine(Hooks, name);

    /// <summary>Выполняет git; ошибка — исключение. Возвращает stdout и stderr вместе (хуки пишут в stderr).</summary>
    public string Git(params string[] arguments) => GitIn(Root, arguments);

    public string GitIn(string directory, params string[] arguments)
    {
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        // Настройки git пользователя (core.hooksPath, шаблоны) тестам не мешают.
        info.Environment["GIT_CONFIG_GLOBAL"] = "/dev/null";
        info.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);

        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        var text = output.Result + error.Result;
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed ({process.ExitCode}): {text}");
        return text;
    }

    public void Commit(string message)
    {
        Git("add", "-A");
        Git("commit", "-q", "-m", message);
    }

    /// <summary>Сколько проектов в кэше папки; -1 — кэша нет. Кэш читаем напрямую: ни один процесс Tasker при этом не запускается.</summary>
    public static int ProjectsInCache(string workspaceFolder)
    {
        var file = Path.Combine(workspaceFolder, ".tasker", ".cache", "index.db");
        if (!File.Exists(file))
            return -1;

        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM files WHERE kind = 'Project' AND error IS NULL";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
