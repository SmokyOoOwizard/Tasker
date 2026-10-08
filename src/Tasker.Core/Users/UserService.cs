using Microsoft.AspNetCore.Identity;

namespace Tasker.Core.Users;

/// <summary>Регистрация первого пользователя сервера. Он становится администратором.</summary>
public record RegisterUser(string Username, string Email, string Password);

/// <summary>На сервере Email и Password обязательны, на десктопе их передавать нельзя.</summary>
public record CreateUser(string Username, string? Email, string? Password, bool IsAdmin = false);

/// <summary>Поля null — не меняются. Version — версия, которую видел клиент (см. <see cref="Versioning"/>).</summary>
public record UpdateUser(string? Username, string? Email, bool? IsAdmin, string? Version);

/// <param name="CurrentPassword">Обязателен, когда пользователь меняет свой пароль; админ сбрасывает чужой без него.</param>
public record ChangePassword(string? CurrentPassword, string NewPassword);

/// <param name="Login">Имя или почта.</param>
public record SignIn(string Login, string Password);

/// <summary>
/// Пользователи-люди. На сервере (<see cref="UserOptions.RequireCredentials"/>) — вход по паролю и права:
/// пользователей создаёт и удаляет админ, остальные могут менять только свой профиль и пароль.
/// На десктопе — только имена, без проверок прав.
/// Агенты — тоже пользователи, но управляются через <see cref="Agents.AgentService"/>.
/// </summary>
public class UserService(IUserStorage users, ICurrentUser currentUser, UserOptions options, TimeProvider time, Locks.EditLockService locks)
{
    // Регистрация возможна, только пока пользователей нет. Блокировка — чтобы два одновременных
    // запроса не создали двух админов (в пределах одного процесса сервера).
    private static readonly SemaphoreSlim RegisterLock = new(1, 1);

    private static readonly PasswordHasher<User> Hasher = new();

    /// <summary>Первый пользователь сервера — администратор. Дальше пользователей создаёт он.</summary>
    public async Task<User> Register(RegisterUser command, CancellationToken ct = default)
    {
        RequireServer();

        await RegisterLock.WaitAsync(ct);
        try
        {
            if (await users.Count(ct: ct) > 0)
                throw new TaskerForbiddenException("Registration is closed: ask an administrator to create an account");

            return await Add(command.Username, command.Email, command.Password, isAdmin: true, ct);
        }
        finally
        {
            RegisterLock.Release();
        }
    }

    public async Task<User> Create(CreateUser command, CancellationToken ct = default)
    {
        if (!options.RequireCredentials)
        {
            if (command.Email != null || command.Password != null || command.IsAdmin)
                throw new TaskerValidationException("Local users have only a username: email, password and admin are not supported");
            return await Add(command.Username, null, null, isAdmin: false, ct);
        }

        await RequireAdmin(ct);
        return await Add(command.Username, command.Email, command.Password, command.IsAdmin, ct);
    }

    /// <summary>
    /// Админ меняет что угодно, пользователь — только своё имя и почту.
    /// Снять права с последнего админа нельзя.
    /// </summary>
    /// <returns>null — пользователя нет.</returns>
    public async Task<User?> Update(Guid id, UpdateUser command, CancellationToken ct = default)
    {
        var user = await users.GetById(id, ct);
        if (user == null)
            return null;
        await locks.EnsureWritable(Locks.LockedEntity.User, user.Id, Subject(user), ct);
        var expected = Versioning.Check(user.Version, command.Version, Subject(user));
        EnsureHuman(user);

        if (options.RequireCredentials)
        {
            var actor = await RequireSelfOrAdmin(id, ct);
            if (command.IsAdmin != null && command.IsAdmin != user.IsAdmin)
            {
                if (!actor.IsAdmin)
                    throw new TaskerForbiddenException("Only an administrator can change admin rights");
                if (user.IsAdmin && !command.IsAdmin.Value)
                    await EnsureNotLastAdmin(user, "lose admin rights", ct);
            }
        }
        else if (command.Email != null || command.IsAdmin != null)
        {
            throw new TaskerValidationException("Local users have only a username");
        }

        var username = command.Username == null ? user.Username : Validate.Username(command.Username);
        var email = command.Email == null ? user.Email : Validate.Email(command.Email);
        await EnsureUnique(username, email, except: user.Id, ct);

        var updated = user with { Username = username, Email = email, IsAdmin = command.IsAdmin ?? user.IsAdmin };
        var version = await users.Update(updated, expected, ct) ?? throw Versioning.Modified(Subject(user));
        return updated with { Version = version };
    }

    /// <summary>
    /// Свой пароль — с проверкой текущего; админ сбрасывает чужой пароль без него.
    /// Все выданные ранее токены пользователя перестают действовать.
    /// </summary>
    /// <returns>false — пользователя нет.</returns>
    public async Task<bool> ChangePassword(Guid id, ChangePassword command, CancellationToken ct = default)
    {
        RequireServer();
        var user = await users.GetById(id, ct);
        if (user == null)
            return false;
        EnsureHuman(user);

        var actor = await RequireSelfOrAdmin(id, ct);
        if (actor.Id == id || !actor.IsAdmin)
        {
            if (command.CurrentPassword == null || !await CheckPassword(user, command.CurrentPassword, ct))
                throw new TaskerValidationException("Current password is incorrect");
        }

        var credentials = new UserCredentials(Hash(user, Validate.Password(command.NewPassword)), Guid.NewGuid());
        return await users.SetCredentials(id, credentials, ct);
    }

    /// <summary>
    /// На сервере — только админ; себя и последнего админа удалить нельзя.
    /// Участие пользователя в проектах удаляется вместе с ним.
    /// </summary>
    /// <returns>false — пользователя нет.</returns>
    public async Task<bool> Delete(Guid id, string? version, CancellationToken ct = default)
    {
        var user = await users.GetById(id, ct);
        if (user == null)
            return false;
        await locks.EnsureWritable(Locks.LockedEntity.User, user.Id, Subject(user), ct);
        var expected = Versioning.Check(user.Version, version, Subject(user));
        EnsureHuman(user);

        if (options.RequireCredentials)
        {
            var actor = await RequireAdmin(ct);
            if (actor.Id == id)
                throw new TaskerConflictException("You cannot delete your own account");
            if (user.IsAdmin)
                await EnsureNotLastAdmin(user, "be deleted", ct);
        }

        if (!await users.Delete(id, expected, ct))
            throw Versioning.Modified(Subject(user));
        await locks.Forget(Locks.LockedEntity.User, user.Id, ct);
        return true;
    }

    /// <summary>Проверка имени (или почты) и пароля при входе.</summary>
    /// <returns>null — неверные данные. Причину не уточняем, чтобы не подсказывать, какие имена существуют.</returns>
    public async Task<(User User, UserCredentials Credentials)?> Authenticate(SignIn command, CancellationToken ct = default)
    {
        RequireServer();
        var login = command.Login?.Trim() ?? "";
        var user = login.Contains('@')
            ? await users.GetByEmail(login, ct)
            : await users.GetByUsername(login, ct);
        // Агент по паролю не входит: у него только токены MCP.
        if (user == null || user.IsAgent || command.Password == null)
            return null;

        var credentials = await users.GetCredentials(user.Id, ct);
        if (credentials == null)
            return null;

        var result = Hasher.VerifyHashedPassword(user, credentials.PasswordHash, command.Password);
        if (result == PasswordVerificationResult.Failed)
            return null;

        // Параметры хэширования устарели (новая версия Identity) — пересохраняем пароль с новыми.
        if (result == PasswordVerificationResult.SuccessRehashNeeded)
        {
            credentials = credentials with { PasswordHash = Hash(user, command.Password) };
            await users.SetCredentials(user.Id, credentials, ct);
        }

        return (user, credentials);
    }

    /// <summary>
    /// Текущий пользователь (человек из JWT или агент из MCP). На десктопе для REST-запросов его нет —
    /// там только агент MCP (<see cref="ICurrentUser"/>).
    /// </summary>
    public async Task<User> GetCurrent(CancellationToken ct = default)
    {
        return currentUser.Id is { } id && await users.GetById(id, ct) is { } user
            ? user
            : throw new TaskerForbiddenException("Sign in required");
    }

    /// <summary>Текущий человек сервера — для действий, которые агенту недоступны.</summary>
    public async Task<User> GetCurrentHuman(CancellationToken ct = default)
    {
        var actor = await GetCurrent(ct);
        if (actor.IsAgent)
            throw new TaskerForbiddenException("Agents cannot do this: only a person can");
        return actor;
    }

    /// <summary>Пользователь-агент без почты и пароля. Права проверяет <see cref="Agents.AgentService"/>.</summary>
    internal async Task<User> AddAgent(string? username, Guid? ownerId, CancellationToken ct)
    {
        var name = Validate.Username(username);
        await EnsureUnique(name, null, except: null, ct);

        var agent = new User
        {
            Id = Guid.NewGuid(),
            Username = name,
            Kind = UserKind.Agent,
            OwnerId = ownerId,
            IsAdmin = false,
            CreatedAt = time.GetUtcNow(),
            Version = Versioning.New
        };
        return agent with { Version = await users.Add(agent, null, ct) };
    }

    private async Task<User> Add(string? username, string? email, string? password, bool isAdmin, CancellationToken ct)
    {
        var name = Validate.Username(username);
        string? mail = null;
        string? pass = null;
        if (options.RequireCredentials)
        {
            mail = Validate.Email(email);
            pass = Validate.Password(password);
        }
        await EnsureUnique(name, mail, except: null, ct);

        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = name,
            Email = mail,
            IsAdmin = isAdmin,
            CreatedAt = time.GetUtcNow(),
            Version = Versioning.New
        };
        var credentials = pass == null ? null : new UserCredentials(Hash(user, pass), Guid.NewGuid());

        return user with { Version = await users.Add(user, credentials, ct) };
    }

    /// <summary>Имя уникально среди всех пользователей, включая агентов.</summary>
    internal async Task EnsureUnique(string username, string? email, Guid? except, CancellationToken ct)
    {
        if (await users.GetByUsername(username, ct) is { } byName && byName.Id != except)
            throw new TaskerConflictException($"Username '{username}' is already taken");
        if (email != null && await users.GetByEmail(email, ct) is { } byEmail && byEmail.Id != except)
            throw new TaskerConflictException($"Email '{email}' is already taken");
    }

    private async Task EnsureNotLastAdmin(User user, string action, CancellationToken ct)
    {
        var admins = await users.Count(new UserFilter { IsAdmin = true }, ct);
        if (admins <= 1)
            throw new TaskerConflictException($"'{user.Username}' is the last administrator and cannot {action}");
    }

    private async Task<bool> CheckPassword(User user, string password, CancellationToken ct) =>
        await users.GetCredentials(user.Id, ct) is { } credentials
        && Hasher.VerifyHashedPassword(user, credentials.PasswordHash, password) != PasswordVerificationResult.Failed;

    private async Task<User> RequireAdmin(CancellationToken ct)
    {
        var actor = await GetCurrentHuman(ct);
        if (!actor.IsAdmin)
            throw new TaskerForbiddenException("Only an administrator can do this");
        return actor;
    }

    private async Task<User> RequireSelfOrAdmin(Guid id, CancellationToken ct)
    {
        var actor = await GetCurrentHuman(ct);
        if (actor.Id != id && !actor.IsAdmin)
            throw new TaskerForbiddenException("You can change only your own account");
        return actor;
    }

    private static void EnsureHuman(User user)
    {
        if (user.IsAgent)
            throw new TaskerValidationException($"'{user.Username}' is an agent: manage it through /api/agents");
    }

    private void RequireServer()
    {
        if (!options.RequireCredentials)
            throw new TaskerValidationException("Sign-in is available only on the server: local users have no passwords");
    }

    private static string Hash(User user, string password) => Hasher.HashPassword(user, password);

    private static string Subject(User user) => $"User '{user.Username}'";
}
