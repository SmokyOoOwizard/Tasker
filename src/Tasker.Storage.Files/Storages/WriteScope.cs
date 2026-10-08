using Tasker.Core.IO;
using Tasker.Core.TaskSeries;
using Tasker.Storage.Files.Index;

namespace Tasker.Storage.Files.Storages;

/// <summary>
/// Блокировка записи между процессами (<c>.cache/write.lock</c>, та же, что берёт <see cref="YamlFile"/>) плюс
/// сверка индекса задач проекта: <c>git pull</c> мог принести файлы, о которых кэш ещё не знает,
/// а номер считается по индексу. Вложенный вызов и записи хранилищ внутри действия не ждут самих себя.
/// </summary>
internal class WriteScope(TaskerDirectory directory, WorkspaceIndex index) : IWriteScope
{
    public Task<T> Exclusive<T>(Guid projectId, Func<Task<T>> action, CancellationToken ct = default) =>
        FileLock.Run(directory.WriteLock, async () =>
        {
            await index.Refresh(directory.Project(projectId).Tasks, ct);
            return await action();
        }, ct);
}
