using Autofac.Core.Lifetime;
using Autofac.Extensions.DependencyInjection;
using Tasker.Mcp;

namespace Tasker.Web.Workspaces;

/// <summary>
/// Области, доступные агентам через MCP (<c>/mcp</c>): открытые в <see cref="WorkspaceRegistry"/>. Десктоп открывает область
/// вкладкой, демон — по списку из настроек: что открыто, то и разрешено; других папок агент открыть не может.
/// Имя области для агента — её ключ в реестре (имя папки, у одинаковых — с суффиксом «-2»), как в адресе интерфейса.
/// </summary>
public sealed class McpWorkspaceCatalog(WorkspaceRegistry registry) : IMcpWorkspaces
{
    /// <summary>
    /// Разрешённые, но не открытые области (демон: ещё открываются или не открылись) — чтобы <c>list_workspaces</c> показывал и их.
    /// Задаёт хост, который знает об этом больше реестра.
    /// </summary>
    public Func<IReadOnlyList<McpWorkspace>> Unavailable { get; set; } = () => [];

    public IReadOnlyList<McpWorkspace> List() => registry.OpenedWithKeys
        .Select(x => new McpWorkspace(x.Key, x.Location.Path, McpWorkspaceStatus.Open))
        .Concat(Unavailable())
        .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
        .ThenBy(x => x.Path, StringComparer.Ordinal)
        .ToArray();

    public McpWorkspaceScope? Enter(string nameOrPath)
    {
        // Как запрос в UseWorkspaces: закрывающаяся область (последнюю вкладку закрыли, демон убрал её из настроек) вызовов не принимает.
        if (registry.FindByNameOrPath(nameOrPath) is not { } workspace || !workspace.TryEnter())
            return null;

        try
        {
            var scope = workspace.Scope.BeginLifetimeScope(MatchingScopeLifetimeTags.RequestLifetimeScopeTag);
            return new McpWorkspaceScope(workspace.Key, new AutofacServiceProvider(scope), async () =>
            {
                try
                {
                    await scope.DisposeAsync();
                }
                finally
                {
                    workspace.Exit();
                }
            });
        }
        catch
        {
            workspace.Exit();
            throw;
        }
    }
}
