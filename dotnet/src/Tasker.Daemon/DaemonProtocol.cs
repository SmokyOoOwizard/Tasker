using Tasker.Global;
using Tasker.Storage.Files.Workspaces;

namespace Tasker.Daemon;

/// <summary>Итог сверки области (<c>/daemon/sync</c>): файлы, которые не удалось прочитать, и состояние серий.</summary>
/// <param name="Problems">Первые <see cref="ProblemsShown"/> из <paramref name="ProblemCount"/>.</param>
public sealed record SyncResult(string Path, int ProblemCount, Tasker.Core.Workspace.WorkspaceProblem[] Problems)
{
    public const int ProblemsShown = 10;

    /// <summary>Проекты, у которых что-то не в порядке с сериями (пусто — всё хорошо). Новый член: старый JSON без него читается.</summary>
    public ProjectSeriesHealth[] Series { get; init; } = [];
}

/// <summary>Просьба сверить область: <c>POST /daemon/sync</c>.</summary>
public sealed record SyncRequest(WorkspaceKind Kind, string Path);

/// <summary>Просьба заменить рабочий процесс демона на лету: <c>POST /daemon/upgrade</c>.</summary>
/// <param name="File">Программа нового рабочего процесса (<c>tasker-mcpd</c> новой сборки) и её начальные аргументы.</param>
/// <param name="TimeoutSeconds">Сколько ждать, пока новый процесс откроет области и будет готов.</param>
public sealed record UpgradeRequest(string File, string[] Arguments, int TimeoutSeconds);

/// <summary>Итог замены. <c>Ok = false</c> — старый процесс продолжает работать, <c>Message</c> объясняет почему.</summary>
public sealed record UpgradeResult(bool Ok, string Message, int? OldPid = null, int? NewPid = null, string? OldBuild = null, string? NewBuild = null);
