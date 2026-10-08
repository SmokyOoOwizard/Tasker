using Tasker.Core.Dto;
using Tasker.Core.Projects;
using Tasker.Storage.Files.Index;

namespace Tasker.Storage.Files.Storages;

/// <summary>Один проект — из файла, списки — из индекса (см. <see cref="WorkspaceIndex"/>).</summary>
internal class ProjectStorage(TaskerDirectory directory, WorkspaceIndex index) : IProjectStorage
{
    public Task<Project?> GetById(Guid id, CancellationToken ct = default) =>
        ProjectFile.Read(directory.Project(id).ProjectFile, ct);

    public Task<ListDto<Project>> GetRange(Guid[]? ids, Page page, CancellationToken ct = default) =>
        index.GetRange<Project>(new IndexQuery(IndexKind.Project) { Ids = ids }, page, ct);

    public Task<string> Add(Project project, CancellationToken ct = default)
    {
        var path = directory.Project(project.Id).ProjectFile;
        return index.Written(path, ProjectFile.Write(path, project, ct), ct);
    }

    public Task<string?> Update(Project project, string expectedVersion, CancellationToken ct = default)
    {
        var path = directory.Project(project.Id).ProjectFile;
        return index.Written(path, ProjectFile.Update(path, project, expectedVersion, ct), ct);
    }

    /// <summary>
    /// Папка проекта удаляется целиком — если project.yaml не изменился с ожидаемой версии.
    /// В git-репозитории её можно восстановить из истории. Из индекса уходит всё её содержимое.
    /// </summary>
    public Task<bool> Delete(Guid id, string expectedVersion, CancellationToken ct = default)
    {
        var project = directory.Project(id);
        return index.Written(project.Root,
            YamlFile.DeleteIfMatch(project.ProjectFile, expectedVersion, () =>
            {
                Directory.Delete(project.Root, recursive: true);
                var marker = directory.DeletedProjectMarker(id);
                Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
                File.WriteAllText(marker, "");
            }, ct), ct);
    }
}
