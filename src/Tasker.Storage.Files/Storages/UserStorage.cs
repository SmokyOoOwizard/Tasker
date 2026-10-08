using Tasker.Core;
using Tasker.Core.Dto;
using Tasker.Core.Users;
using Tasker.Storage.Files.Index;

namespace Tasker.Storage.Files.Storages;

/// <summary>
/// Пользователи десктопа — только имена. Почты и паролей в файлах нет: сервер (где нужен вход)
/// работает только с БД, а хранить хэши паролей в git-репозитории незачем.
/// </summary>
internal class UserStorage(TaskerDirectory directory, WorkspaceIndex index) : IUserStorage
{
    public Task<User?> GetById(Guid id, CancellationToken ct = default) =>
        UserFile.Read(directory.UserFile(id), ct);

    public Task<User?> GetByUsername(string username, CancellationToken ct = default) =>
        index.FirstOrDefault<User>(new IndexQuery(IndexKind.User) { SortKey = Normalize(username) }, ct);

    public Task<User?> GetByEmail(string email, CancellationToken ct = default) => Task.FromResult<User?>(null);

    public Task<ListDto<User>> GetRange(UserFilter? filter, Page page, CancellationToken ct = default) =>
        index.GetRange<User>(Query(filter), page, ct);

    public Task<int> Count(UserFilter? filter = null, CancellationToken ct = default) =>
        index.Count(Query(filter), ct);

    /// <summary>Ключ имени в индексе: уникальность и сортировка — без учёта регистра и пробелов по краям.</summary>
    public static string Normalize(string username) => username.Trim().ToLowerInvariant();

    public Task<UserCredentials?> GetCredentials(Guid id, CancellationToken ct = default) =>
        Task.FromResult<UserCredentials?>(null);

    // Проверка уникальности и запись — под одной блокировкой каталога пользователей.
    public Task<string> Add(User user, UserCredentials? credentials, CancellationToken ct = default)
    {
        if (credentials != null || user.Email != null)
            throw new NotSupportedException("File storage keeps only usernames: sign-in requires database storage");

        return YamlFile.Locked(directory.Users, ct, async () =>
        {
            await EnsureUnique(user, ct);
            var path = directory.UserFile(user.Id);
            return await index.Written(path, UserFile.Write(path, user, ct), ct);
        });
    }

    public Task<string?> Update(User user, string expectedVersion, CancellationToken ct = default)
    {
        if (user.Email != null)
            throw new NotSupportedException("File storage keeps only usernames");

        return YamlFile.Locked(directory.Users, ct, async () =>
        {
            await EnsureUnique(user, ct);
            var path = directory.UserFile(user.Id);
            return await index.Written(path, UserFile.Update(path, user, expectedVersion, ct), ct);
        });
    }

    public Task<bool> SetCredentials(Guid id, UserCredentials credentials, CancellationToken ct = default) =>
        throw new NotSupportedException("File storage keeps only usernames: sign-in requires database storage");

    public Task<bool> Delete(Guid id, string expectedVersion, CancellationToken ct = default)
    {
        var path = directory.UserFile(id);
        return index.Written(path, YamlFile.DeleteIfMatch(path, expectedVersion, ct), ct);
    }

    private async Task EnsureUnique(User user, CancellationToken ct)
    {
        if (await GetByUsername(user.Username, ct) is { } taken && taken.Id != user.Id)
            throw new TaskerConflictException($"Username '{user.Username}' is already taken");
    }

    // В файлах нет владельцев агентов и админов: фильтр по ним сразу пустой.
    private static IndexQuery Query(UserFilter? filter) => new(IndexKind.User)
    {
        UserKind = filter?.Kind,
        Ids = filter is { OwnerId: not null } or { IsAdmin: true } ? [] : filter?.Ids
    };
}
