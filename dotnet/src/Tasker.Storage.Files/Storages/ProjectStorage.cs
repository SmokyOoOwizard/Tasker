using Tasker.Core.Dto;
using Tasker.Core.Projects;
using Tasker.Storage.Files.Index;

namespace Tasker.Storage.Files.Storages;

/// <summary>
/// Один проект — из файла, списки — из индекса (см. <see cref="WorkspaceIndex"/>). Папка проекта — <c>projects/&lt;slug имени&gt;-&lt;id8&gt;/</c>
/// (<see cref="ProjectFolders"/>): смена названия переносит папку.
/// </summary>
internal class ProjectStorage(TaskerDirectory directory, WorkspaceIndex index) : IProjectStorage
{
    // Сколько раз искать папку заново, если её перенёс другой процесс между поиском и чтением.
    private const int ResolveAttempts = 5;

    /// <summary><c>project.yaml</c> основной папки. Папка переехала между поиском и чтением (проект переименовали) — поиск повторяется.</summary>
    public async Task<Project?> GetById(Guid id, CancellationToken ct = default)
    {
        for (var attempt = 0; attempt < ResolveAttempts; attempt++)
        {
            var folder = directory.Project(id);
            if (await ProjectFile.Read(folder.ProjectFile, ct) is { } project)
                return project;
            // Папка есть, а проекта в ней нет (файла нет или он пуст) — проекта нет; папки нет — её могли перенести, пока шёл поиск.
            if (Directory.Exists(folder.Root))
                return null;
        }
        return null;
    }

    public Task<ListDto<Project>> GetRange(Guid[]? ids, Page page, CancellationToken ct = default) =>
        index.GetRange<Project>(new IndexQuery(IndexKind.Project) { Ids = ids }, page, ct);

    /// <summary>Новый проект — в папке по названию: <c>projects/&lt;slug&gt;-&lt;id8&gt;/</c>.</summary>
    public Task<string> Add(Project project, CancellationToken ct = default)
    {
        var path = directory.NamedProject(project.Name, project.Id).ProjectFile;
        return index.Written(path, ProjectFile.Write(path, project, ct), ct);
    }

    /// <summary>
    /// Перезапись по версии; папка, имя которой не по названию (переименование, прежнее имя — Guid), переносится в
    /// <c>projects/&lt;slug&gt;-&lt;id8&gt;/</c> той же операцией под блокировкой записи.
    /// </summary>
    public Task<string?> Update(Project project, string expectedVersion, CancellationToken ct = default)
    {
        var current = directory.Project(project.Id);
        var target = directory.NamedProject(project.Name, project.Id);
        return index.Written([current.Root, target.Root],
            ProjectFile.Update(current.ProjectFile, current.Root, target.Root, project, expectedVersion, ct), ct);
    }

    /// <summary>
    /// Все папки проекта удаляются целиком — если project.yaml не изменился с ожидаемой версии.
    /// В git-репозитории их можно восстановить из истории. Из индекса уходит всё их содержимое.
    /// </summary>
    public Task<bool> Delete(Guid id, string expectedVersion, CancellationToken ct = default)
    {
        var roots = directory.ProjectDirs(id).Select(x => x.Root).ToArray();
        var file = directory.Project(id).ProjectFile;
        return index.Written(roots,
            YamlFile.DeleteIfMatch(file, expectedVersion, () =>
            {
                foreach (var root in roots)
                    Directory.Delete(root, recursive: true);
                var marker = directory.DeletedProjectMarker(id);
                Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
                File.WriteAllText(marker, "");
            }, ct), ct);
    }
}
