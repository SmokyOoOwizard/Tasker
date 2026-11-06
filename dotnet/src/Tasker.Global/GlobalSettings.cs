using System.Text.Json.Serialization;
using Tasker.Storage.Files.Workspaces;

namespace Tasker.Global;

/// <summary>
/// Глобальные настройки Tasker — в одном файле <c>settings.json</c> в <see cref="AppDirectories.Data"/>.
/// Их читают и меняют десктоп, командная строка и демон MCP; изменение одним видят остальные.
/// </summary>
public sealed record GlobalSettings
{
    public const int MaxUserNameLength = 100;

    /// <summary>
    /// Как зовут человека за десктопом и консолью, где нет входа: это имя видят другие в «правит Иван».
    /// null — имя пользователя операционной системы.
    /// </summary>
    public string? UserName { get; init; }

    public LocalUserSettings? LocalUser { get; init; }

    [JsonIgnore]
    public string EffectiveUserName => string.IsNullOrWhiteSpace(UserName) ? OsUser.Name : UserName;

    public McpSettings Mcp { get; init; } = new();
}

public sealed record LocalUserSettings(Guid Id, DateTimeOffset CreatedAt);

public sealed record McpSettings
{
    public const int DefaultPort = 5719;

    /// <summary>Порт MCP на 127.0.0.1 — постоянный, чтобы адрес можно было один раз прописать агенту.</summary>
    public int Port { get; init; } = DefaultPort;

    /// <summary>Рабочие области, доступные через MCP демона.</summary>
    public IReadOnlyList<WorkspaceEntry> Workspaces { get; init; } = [];
}

public enum WorkspaceKind
{
    Files,
    Sqlite
}

/// <summary>Рабочая область в настройках: папка (данные в <c>.tasker</c>) или старая запись о файле SQLite, которую демон не открывает.</summary>
public sealed record WorkspaceEntry(WorkspaceKind Kind, string Path)
{
    [JsonIgnore]
    public WorkspaceLocation Location => WorkspaceLocation.Files(Path);

    [JsonIgnore]
    public string? Unsupported => Kind == WorkspaceKind.Files
        ? null
        : $"SQLite workspaces are no longer supported; the file is not touched, remove this entry with: tasker mcp workspace remove \"{Path}\"";

    public static WorkspaceEntry Of(WorkspaceLocation location) => new(WorkspaceKind.Files, location.Path);
}
