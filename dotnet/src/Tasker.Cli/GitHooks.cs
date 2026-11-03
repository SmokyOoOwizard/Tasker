using System.Text;
using Tasker.Core.IO;
using Tasker.Daemon;
using Tasker.Daemon.Services;
using Tasker.Storage.Files;
using Tasker.Storage.Files.Workspaces;

namespace Tasker.Cli;

/// <summary>Состояние одного git-хука.</summary>
/// <param name="Installed">В хуке есть блок Tasker.</param>
/// <param name="Foreign">В хуке есть и чужие команды (их установка и удаление не трогают).</param>
internal sealed record HookState(string Name, string Path, bool Installed, bool Foreign);

/// <summary>Где лежит рабочая папка в репозитории git и куда ставить хуки.</summary>
/// <param name="Top">Корень репозитория (рабочего дерева).</param>
/// <param name="Relative">Путь рабочей папки от корня через «/»; пусто — сама корневая папка.</param>
/// <param name="HooksDirectory">Каталог хуков: <c>.git/hooks</c> или <c>core.hooksPath</c>.</param>
internal sealed record HookTarget(string Top, string Relative, string HooksDirectory);

/// <summary>
/// Git-хуки, после которых кэш рабочей папки нужно обновить: <c>post-merge</c> (pull, merge), <c>post-checkout</c>
/// (checkout, switch, clone) и <c>post-rewrite</c> (rebase, amend). Хук запускает <c>tasker sync</c> — кэш обновляется
/// сразу после команды git, без задержки слежения за файлами и даже когда никакой демон или десктоп не запущен.
/// <para>
/// Блок Tasker помечен маркерами и вставляется в начало хука, чужие команды остаются как есть; установка идемпотентна,
/// удаление убирает только блок. Хук не должен мешать git: ошибка <c>tasker sync</c> его не прерывает.
/// </para>
/// </summary>
internal sealed class GitHooks(IProcessRunner runner)
{
    public static readonly string[] Names = ["post-merge", "post-checkout", "post-rewrite"];

    private const string Begin = "# >>> tasker (managed by 'tasker hooks install', do not edit) >>>";
    private const string End = "# <<< tasker <<<";

    public async Task<HookTarget> Locate(string workspaceFolder)
    {
        var workspace = CanonicalPath.Of(workspaceFolder);
        if (!Directory.Exists(Path.Combine(workspace, TaskerDirectory.Name)))
            throw new CliException($"{workspace} is not a Tasker workspace: there is no {TaskerDirectory.Name} folder");

        var top = await Git(workspace, "rev-parse", "--show-toplevel");
        // Абсолютный путь: core.hooksPath может быть относительным, а из подпапки git отдал бы путь от неё.
        var hooks = await Git(workspace, "rev-parse", "--path-format=absolute", "--git-path", "hooks");
        var relative = Path.GetRelativePath(CanonicalPath.Of(top), workspace).Replace('\\', '/');
        return new HookTarget(CanonicalPath.Of(top), relative == "." ? "" : relative, Path.GetFullPath(hooks, workspace));
    }

    public async Task<HookState[]> Install(string workspaceFolder)
    {
        var target = await Locate(workspaceFolder);
        var block = Block(target);
        Directory.CreateDirectory(target.HooksDirectory);

        foreach (var name in Names)
        {
            var path = Path.Combine(target.HooksDirectory, name);
            var text = File.Exists(path) ? await File.ReadAllTextAsync(path) : "";
            // Хуки читает sh: окончания строк только LF (на Windows чужой хук мог быть сохранён с CRLF — sh Git for Windows споткнулся бы о \r).
            await File.WriteAllTextAsync(path, LineEndings.ToLf(WithBlock(text, block)));
            MakeExecutable(path);
        }

        return await Status(target);
    }

    /// <returns>Хуки, из которых убран блок Tasker.</returns>
    public async Task<string[]> Uninstall(string workspaceFolder)
    {
        var target = await Locate(workspaceFolder);
        var removed = new List<string>();
        foreach (var name in Names)
        {
            var path = Path.Combine(target.HooksDirectory, name);
            if (!File.Exists(path))
                continue;

            var text = await File.ReadAllTextAsync(path);
            if (!HasBlock(text))
                continue;

            var rest = WithoutBlock(text);
            if (IsEmpty(rest))
                File.Delete(path);
            else
                await File.WriteAllTextAsync(path, LineEndings.ToLf(rest));
            removed.Add(name);
        }

        return removed.ToArray();
    }

    public async Task<HookState[]> Status(string workspaceFolder) => await Status(await Locate(workspaceFolder));

    private static async Task<HookState[]> Status(HookTarget target)
    {
        var states = new List<HookState>();
        foreach (var name in Names)
        {
            var path = Path.Combine(target.HooksDirectory, name);
            var text = File.Exists(path) ? await File.ReadAllTextAsync(path) : "";
            var installed = HasBlock(text);
            states.Add(new HookState(name, path, installed, !IsEmpty(installed ? WithoutBlock(text) : text)));
        }

        return states.ToArray();
    }

    /// <summary>Блок хука: запускает <c>tasker sync</c> для этой папки (путь от корня репозитория — хук работает и в других рабочих деревьях).</summary>
    internal static string Block(HookTarget target)
    {
        var (file, arguments) = Launcher.Command();
        // Хук исполняет sh (на Windows — из Git for Windows): пути с «/», а не «\», иначе обратная косая — знак экранирования.
        var command = new[] { file }.Concat(arguments).Select(x => ToShellPath(x, OperatingSystem.IsWindows())).ToArray();
        var exists = string.Join(" && ", command.Select(x => $"[ -e {Quote(x)} ]"));
        var run = string.Join(' ', command.Select(Quote));
        var workspace = target.Relative.Length == 0
            ? "\"$(git rev-parse --show-toplevel)\""
            : $"\"$(git rev-parse --show-toplevel)\"/{Quote(target.Relative)}";

        return new StringBuilder()
            .Append(Begin).Append('\n')
            .Append($"if {exists}; then\n")
            .Append($"  {run} sync --quiet --workspace {workspace} || true\n")
            .Append("fi\n")
            .Append(End).Append('\n')
            .ToString();
    }

    // Чужие команды остаются; наш блок — сразу после строки #!, чтобы завершение чужого скрипта (exit, exec) его не отменило.
    internal static string WithBlock(string text, string block)
    {
        text = HasBlock(text) ? WithoutBlock(text) : text;
        if (IsEmpty(text))
            return "#!/bin/sh\n" + block;

        if (!text.StartsWith("#!", StringComparison.Ordinal))
            return "#!/bin/sh\n" + block + text;

        var firstLine = text.IndexOf('\n');
        return firstLine < 0
            ? text + "\n" + block
            : text[..(firstLine + 1)] + block + text[(firstLine + 1)..];
    }

    internal static bool HasBlock(string text) => text.Contains(Begin, StringComparison.Ordinal);

    internal static string WithoutBlock(string text)
    {
        var start = text.IndexOf(Begin, StringComparison.Ordinal);
        var end = text.IndexOf(End, start < 0 ? 0 : start, StringComparison.Ordinal);
        if (start < 0 || end < 0)
            return text;

        var after = end + End.Length;
        if (after < text.Length && text[after] == '\r')
            after++;
        if (after < text.Length && text[after] == '\n')
            after++;
        return text[..start] + text[after..];
    }

    // Пусто или только строка #! — хуку больше нечего выполнять.
    private static bool IsEmpty(string text) =>
        text.Split('\n').All(x => string.IsNullOrWhiteSpace(x) || x.StartsWith("#!", StringComparison.Ordinal));

    /// <summary>Путь для скрипта sh: на Windows разделители «/» (<c>C:/Users/me/tasker.exe</c> понимают и Git for Windows, и сама система). Чистая функция.</summary>
    internal static string ToShellPath(string path, bool windows) => windows ? path.Replace('\\', '/') : path;

    private static string Quote(string value) => "'" + value.Replace("'", "'\\''") + "'";

    private static void MakeExecutable(string path)
    {
        if (OperatingSystem.IsWindows())
            return;

        File.SetUnixFileMode(path, File.GetUnixFileMode(path)
            | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
    }

    private async Task<string> Git(string directory, params string[] arguments)
    {
        var result = await runner.Run("git", ["-C", directory, .. arguments]);
        if (!result.Success)
        {
            var message = result.Error.Trim();
            throw new CliException(message.Contains("not a git repository", StringComparison.OrdinalIgnoreCase)
                ? $"{directory} is not in a git repository"
                : $"git {string.Join(' ', arguments)} failed: {message}");
        }

        return result.Output.Trim();
    }
}
