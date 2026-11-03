using Microsoft.Extensions.DependencyInjection;
using Tasker.Core.Agents;

namespace Tasker.Mcp;

/// <summary>
/// Десктоп: локальный агент, от имени которого идут запросы MCP. Создаётся при первом обращении;
/// id запоминается, пока открыта рабочая область (у каждой области свои пользователи — и свой агент).
/// </summary>
public sealed class LocalMcpAgent
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Guid? _id;

    /// <param name="requestServices">Контейнер запроса: AgentService работает с хранилищами области.</param>
    public async Task<Guid> GetId(IServiceProvider requestServices, CancellationToken ct)
    {
        if (_id is { } id)
            return id;

        await _gate.WaitAsync(ct);
        try
        {
            _id ??= (await requestServices.GetRequiredService<AgentService>().EnsureLocalAgent(ct)).Id;
            return _id.Value;
        }
        finally
        {
            _gate.Release();
        }
    }
}
