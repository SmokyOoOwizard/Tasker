using Tasker.Global;

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
public sealed record SyncRequest(string Path);

public sealed record SearchRequest(string Path, Guid ProjectId, string Text, int? Limit);

public sealed record SearchResult(Tasker.Core.Tasks.Search.TaskSearchHit[] Hits);

/// <summary>Что о себе сообщает программа демона по <c>--build-info</c>.</summary>
public sealed record DaemonBuildInfo(string Version, string Build);
