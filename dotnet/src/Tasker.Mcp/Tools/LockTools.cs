using System.ComponentModel;
using ModelContextProtocol.Server;
using Tasker.Core.Locks;
using Tasker.Core.Projects;

namespace Tasker.Mcp.Tools;

/// <summary>
/// Блокировка на время правки: «запись занята, её редактирует Иван». Одиночным изменениям блокировка не нужна —
/// достаточно обычного вызова. Она для серии связанных вызовов, которые никто не должен перебить.
/// </summary>
[McpServerToolType]
public static class LockTools
{
    private const string EntityHint =
        "`entity` is one of: project, task, taskType, linkType, field, enum, status, statusSet, board, series. `entityId` is the entity's id " +
        "(for `project` it may be omitted: the project itself).";

    public record LockResult(LockInfo? Lock);

    [McpServerTool(Name = "lock_entity")]
    [Description(
        "Locks an entity for editing so nobody else can change or delete it until you unlock it; calling it again renews your lock. " +
        "The lock expires by itself after 2 minutes, so renew it while you work. If someone else holds it, fails with [locked] naming the holder — " +
        "wait or ask them. A free entity can be changed without locking. " + EntityHint)]
    public static Task<LockInfo> LockEntity(
        ProjectService projects, EntityLockService locks, Guid projectId, string entity, Guid? entityId = null, CancellationToken ct = default) =>
        McpCall.Run(async () =>
        {
            await McpCall.RequireProject(projects, projectId, ct);
            var kind = EntityLockService.ParseEntity(entity);
            var id = IdOf(kind, projectId, entityId);
            return McpCall.Found(await locks.Acquire(projectId, kind, id, ct), $"{entity} {id}");
        });

    [McpServerTool(Name = "unlock_entity", Idempotent = true)]
    [Description("Releases your lock on an entity. Nothing to release (or someone else's lock) is not an error. " + EntityHint)]
    public static Task<string> UnlockEntity(
        ProjectService projects, EntityLockService locks, Guid projectId, string entity, Guid? entityId = null, CancellationToken ct = default) =>
        McpCall.Run(async () =>
        {
            await McpCall.RequireProject(projects, projectId, ct);
            var kind = EntityLockService.ParseEntity(entity);
            return await locks.Release(kind, IdOf(kind, projectId, entityId), ct) ? "unlocked" : "not locked by you";
        });

    [McpServerTool(Name = "get_lock", ReadOnly = true)]
    [Description("Shows who is editing an entity and until when (`mine` tells whether it is you). `lock` is null if nobody is. " + EntityHint)]
    public static Task<LockResult> GetLock(
        ProjectService projects, EntityLockService locks, Guid projectId, string entity, Guid? entityId = null, CancellationToken ct = default) =>
        McpCall.Run(async () =>
        {
            await McpCall.RequireProject(projects, projectId, ct);
            var kind = EntityLockService.ParseEntity(entity);
            return new LockResult(await locks.Get(kind, IdOf(kind, projectId, entityId), ct));
        });

    private static Guid IdOf(LockedEntity kind, Guid projectId, Guid? entityId)
    {
        if (kind == LockedEntity.User)
            throw new Tasker.Core.TaskerValidationException("Entity: users cannot be locked here; expected one of project, task, taskType, linkType, field, enum, status, statusSet, board, series");
        if (kind == LockedEntity.Project)
            return entityId is { } id && id != projectId
                ? throw new Tasker.Core.TaskerValidationException("EntityId: for a project it must be the projectId or omitted")
                : projectId;
        return entityId ?? throw new Tasker.Core.TaskerValidationException($"EntityId: required to lock a {kind}");
    }
}
