using Tasker.Core.Users;

namespace Tasker.Storage.Files.Storages;

/// <summary>
/// Формат файла пользователя <c>.tasker/users/&lt;id&gt;.yaml</c>. В файлах пользователь — только имя:
/// файлы используются локальным десктопом, где входа нет (и пароли в git-репозитории не нужны).
/// <code>
/// id: 4e5f6a7b-8c9d-4e0f-a1b2-c3d4e5f6a7b8
/// username: ivan
/// createdAt: 2026-09-25T10:00:00.0000000+00:00
/// </code>
/// У агента есть строка <c>kind: agent</c>; у человека поля нет.
/// </summary>
internal static class UserFile
{
    public static async Task<User?> Read(string path, CancellationToken ct) =>
        await YamlFile.Read<UserFileModel>(path, ct) is { } file ? Map(file) : null;

    public static Task<string> Write(string path, User user, CancellationToken ct) =>
        YamlFile.Write(path, ToFile(user), ct);

    public static Task<string?> Update(string path, User user, string expectedVersion, CancellationToken ct) =>
        YamlFile.WriteIfMatch(path, ToFile(user), expectedVersion, ct);

    private const string AgentKind = "agent";

    private static UserFileModel ToFile(User user) => new()
    {
        Id = user.Id,
        Username = user.Username,
        Kind = user.IsAgent ? AgentKind : null,
        CreatedAt = user.CreatedAt
    };

    private static User Map(Versioned<UserFileModel> file) => new()
    {
        Id = file.Model.Id,
        Username = file.Model.Username ?? "",
        Kind = string.Equals(file.Model.Kind, AgentKind, StringComparison.OrdinalIgnoreCase) ? UserKind.Agent : UserKind.Human,
        IsAdmin = false,
        CreatedAt = file.Model.CreatedAt,
        Version = file.Version
    };

    private class UserFileModel : FileModel
    {
        public Guid Id { get; set; }
        public string? Username { get; set; }

        /// <summary>«agent» или нет поля (человек).</summary>
        public string? Kind { get; set; }

        public DateTimeOffset CreatedAt { get; set; }
    }
}
