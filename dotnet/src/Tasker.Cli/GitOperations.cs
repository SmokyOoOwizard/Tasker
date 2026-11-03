using Tasker.Daemon.Services;

namespace Tasker.Cli;

/// <summary>
/// Идёт ли в репозитории git слияние, rebase, cherry-pick или revert (в служебной папке есть <c>MERGE_HEAD</c>,
/// <c>rebase-merge</c>, <c>rebase-apply</c>, <c>CHERRY_PICK_HEAD</c> или <c>REVERT_HEAD</c>). Пока оно идёт, файлы наполовину
/// слиты, и чистка серий могла бы принять недочитанное за лишнее. Рабочие деревья учитываются: путь спрашиваем у git.
/// </summary>
internal sealed class GitOperations(IProcessRunner runner)
{
    private static readonly (string Path, string Name)[] Markers =
    [
        ("MERGE_HEAD", "merge"),
        ("rebase-merge", "rebase"),
        ("rebase-apply", "rebase"),
        ("CHERRY_PICK_HEAD", "cherry-pick"),
        ("REVERT_HEAD", "revert")
    ];

    /// <returns>Название операции в процессе (merge, rebase, cherry-pick, revert); null — ничего не идёт, папка не в репозитории или git не установлен.</returns>
    public async Task<string?> InProgress(string folder)
    {
        foreach (var (marker, name) in Markers)
        {
            var result = await runner.Run("git", "-C", folder, "rev-parse", "--path-format=absolute", "--git-path", marker);
            if (!result.Success)
                return null;

            var path = result.Output.Trim();
            if (path.Length > 0 && (File.Exists(path) || Directory.Exists(path)))
                return name;
        }

        return null;
    }
}
