namespace Tasker.Mcp;

public enum McpWorkspaceStatus
{
    /// <summary>Открывается: первая сверка индекса ещё идёт.</summary>
    Opening,
    Open,
    /// <summary>Не открылась (папку удалили, диск не подключён…): причина — <see cref="McpWorkspace.Error"/>.</summary>
    Failed
}

/// <summary>Рабочая область, разрешённая агентам.</summary>
/// <param name="Name">Имя, которым агент выбирает область (аргумент <c>workspace</c>): имя папки, у одинаковых — с суффиксом (<c>Tasker-2</c>).</param>
/// <param name="Path">Канонический путь папки (или файла SQLite).</param>
/// <param name="Projects">Сколько проектов в области; заполняет <c>list_workspaces</c>, у неоткрытой области его нет.</param>
public sealed record McpWorkspace(string Name, string Path, McpWorkspaceStatus Status, int? Projects = null, string? Error = null);

/// <summary>
/// Открытая область на время одного вызова инструмента: контейнер, из которого берутся сервисы вызова. Освободить после вызова.
/// </summary>
public sealed class McpWorkspaceScope(string name, IServiceProvider services, Func<ValueTask> release) : IAsyncDisposable
{
    public string Name { get; } = name;

    /// <summary>Сервисы области: хранилища именно этой папки.</summary>
    public IServiceProvider Services { get; } = services;

    public ValueTask DisposeAsync() => release();
}

/// <summary>
/// Рабочие области, которые MCP разрешает агентам: у демона — список из настроек (<c>tasker mcp workspace add</c>),
/// у десктопа — открытые вкладки. Других областей агент открыть не может: ни по имени, ни по пути.
/// Сервер (одна область, вход по токену) этого не использует — там аргумента <c>workspace</c> нет.
/// </summary>
public interface IMcpWorkspaces
{
    /// <summary>Все разрешённые области, в том числе ещё не открытые, — по имени.</summary>
    IReadOnlyList<McpWorkspace> List();

    /// <summary>
    /// Открытая разрешённая область по имени (без учёта регистра) или каноническому пути.
    /// null — такой области нет среди разрешённых либо она не открыта: для агента это одно и то же.
    /// </summary>
    McpWorkspaceScope? Enter(string nameOrPath);
}
