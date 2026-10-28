using System.Text.Json;
using Tasker.Core.IO;
using Tasker.Core.Locks;

namespace Tasker.Storage.Files.Storages;

/// <summary>
/// Блокировки на время правки: <c>.cache/edit-locks/&lt;сущность&gt;-&lt;id&gt;.json</c>, в git не попадают.
/// Работает между процессами одной машины (FileLock на <c>.cache/edit-locks.lock</c>); между машинами
/// блокировки не передаются — там остаётся проверка версии.
/// <para>
/// Читают без блокировки: файл пишется во временный и переименовывается, поэтому виден целиком или никак.
/// Повреждённый файл считается отсутствующей блокировкой — она временная, ничего не теряется.
/// </para>
/// </summary>
internal class EditLockStorage(TaskerDirectory directory) : IEditLockStorage
{
    private record LockFile(string Holder, string HolderName, DateTimeOffset AcquiredAt, DateTimeOffset ExpiresAt, Guid? ProjectId = null);

    public async Task<EditLock?> Get(LockedEntity entity, Guid id, DateTimeOffset now, CancellationToken ct = default)
    {
        var held = await Read(entity, id, ct);
        return held != null && held.ExpiresAt > now ? held : null;
    }

    public async Task<EditLock[]> GetByProject(Guid projectId, DateTimeOffset now, CancellationToken ct = default)
    {
        if (!Directory.Exists(directory.EditLocks))
            return [];

        var found = new List<EditLock>();
        foreach (var path in Directory.EnumerateFiles(directory.EditLocks, "*.json"))
        {
            if (Parse(Path.GetFileNameWithoutExtension(path)) is not var (entity, id))
                continue;
            if (await Read(entity, id, ct) is { } held && held.ProjectId == projectId && held.ExpiresAt > now)
                found.Add(held);
        }

        return found.OrderBy(x => x.AcquiredAt).ThenBy(x => x.Id).ToArray();
    }

    public Task<EditLock> Acquire(LockedEntity entity, Guid id, EditHolder holder, DateTimeOffset now, DateTimeOffset expiresAt, Guid? projectId = null, CancellationToken ct = default) =>
        FileLock.Run(directory.EditLocksLock, async () =>
        {
            if (await Get(entity, id, now, ct) is { } held && held.Holder.Key != holder.Key)
                return held;

            var acquiredAt = await Read(entity, id, ct) is { } own && own.Holder.Key == holder.Key && own.ExpiresAt > now
                ? own.AcquiredAt
                : now;
            var taken = new EditLock(entity, id, holder, acquiredAt, expiresAt, projectId);
            await Write(taken, ct);
            return taken;
        }, ct);

    public Task<bool> Release(LockedEntity entity, Guid id, string holderKey, CancellationToken ct = default) =>
        FileLock.Run(directory.EditLocksLock, async () =>
        {
            if (await Read(entity, id, ct) is not { } held || held.Holder.Key != holderKey)
                return false;

            AtomicFile.Delete(PathOf(entity, id));
            return true;
        }, ct);

    public Task Remove(LockedEntity entity, Guid id, CancellationToken ct = default)
    {
        // Почти всегда блокировки нет (сущность никто не правил): не берём межпроцессную блокировку ради пустого удаления.
        if (!File.Exists(PathOf(entity, id)))
            return Task.CompletedTask;

        return FileLock.Run(directory.EditLocksLock, () =>
        {
            AtomicFile.Delete(PathOf(entity, id));
            return Task.CompletedTask;
        }, ct);
    }

    /// <summary>Имя файла <c>&lt;сущность&gt;-&lt;id&gt;</c> → сущность и id; чужие файлы в папке — null.</summary>
    private static (LockedEntity Entity, Guid Id)? Parse(string name)
    {
        var dash = name.IndexOf('-');
        return dash > 0 && Enum.TryParse<LockedEntity>(name[..dash], out var entity) && Guid.TryParse(name[(dash + 1)..], out var id)
            ? (entity, id)
            : null;
    }

    private string PathOf(LockedEntity entity, Guid id) => Path.Combine(directory.EditLocks, $"{entity}-{id}.json");

    private async Task<EditLock?> Read(LockedEntity entity, Guid id, CancellationToken ct)
    {
        try
        {
            await using var stream = new FileStream(PathOf(entity, id), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var file = await JsonSerializer.DeserializeAsync<LockFile>(stream, cancellationToken: ct);
            return file == null ? null : new EditLock(entity, id, new EditHolder(file.Holder, file.HolderName), file.AcquiredAt, file.ExpiresAt, file.ProjectId);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException or JsonException)
        {
            return null;
        }
    }

    private async Task Write(EditLock held, CancellationToken ct)
    {
        Directory.CreateDirectory(directory.EditLocks);
        var path = PathOf(held.Entity, held.Id);
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";

        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(
            new LockFile(held.Holder.Key, held.Holder.Name, held.AcquiredAt, held.ExpiresAt, held.ProjectId)), ct);
        AtomicFile.Move(temp, path, overwrite: true);
    }
}
