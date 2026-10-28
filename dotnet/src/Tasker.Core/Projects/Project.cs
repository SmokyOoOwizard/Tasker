namespace Tasker.Core.Projects;

/// <summary>
/// Проект. Статусы, наборы статусов, типы задач, задачи и доски принадлежат одному проекту
/// и не пересекаются с сущностями других проектов.
/// </summary>
public record Project
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>См. <see cref="Versioning"/>.</summary>
    public required string Version { get; init; }
}
