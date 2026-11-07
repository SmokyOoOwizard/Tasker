namespace Tasker.Core.Users;

public sealed record LocalPerson(Guid Id, string Name, DateTimeOffset CreatedAt);

public interface ILocalPerson
{
    Task<LocalPerson?> Current(CancellationToken ct = default);

    Task<LocalPerson?> Remember(CancellationToken ct = default);
}

public sealed class LocalHuman(ILocalPerson person)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<Guid?> Find(IUserStorage users, CancellationToken ct = default)
    {
        if (await person.Current(ct) is not { } me)
            return null;

        try
        {
            return (await Existing(users, me, ct))?.Id;
        }
        catch (TaskerValidationException)
        {
            return null;
        }
    }

    public async Task<Guid?> Ensure(IUserStorage users, CancellationToken ct = default)
    {
        if (await person.Remember(ct) is not { } me)
            return null;

        await _gate.WaitAsync(ct);
        try
        {
            var user = await Existing(users, me, ct);
            return user == null ? await Create(users, me, ct) : await Rename(users, user, me.Name, ct);
        }
        catch (TaskerValidationException)
        {
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<User?> Existing(IUserStorage users, LocalPerson me, CancellationToken ct)
    {
        if (await users.GetById(me.Id, ct) is { IsAgent: false } byId)
            return byId;
        return await users.GetByUsername(me.Name, ct) is { IsAgent: false } byName ? byName : null;
    }

    private static async Task<Guid?> Create(IUserStorage users, LocalPerson me, CancellationToken ct)
    {
        var name = me.Name;
        for (var i = 2; await users.GetByUsername(name, ct) != null; i++)
            name = $"{me.Name}-{i}";

        var user = new User
        {
            Id = me.Id,
            Username = name,
            IsAdmin = false,
            CreatedAt = me.CreatedAt,
            Version = Versioning.New
        };

        try
        {
            await users.Add(user, null, ct);
            return me.Id;
        }
        catch (TaskerConflictException)
        {
            return (await Existing(users, me, ct))?.Id;
        }
    }

    private static async Task<Guid?> Rename(IUserStorage users, User user, string name, CancellationToken ct)
    {
        if (user.Username == name)
            return user.Id;
        if (await users.GetByUsername(name, ct) is { } taken && taken.Id != user.Id)
            return user.Id;

        try
        {
            await users.Update(user with { Username = name }, user.Version, ct);
        }
        catch (TaskerConflictException)
        {
        }
        return user.Id;
    }
}
