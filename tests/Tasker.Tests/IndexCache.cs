namespace Tasker.Tests;

/// <summary>
/// Файлы кэша индекса рабочей папки — у каждой консоли свои: .NET держит <c>.cache/index.db</c> и <c>index.lock</c>, Rust —
/// <c>index-rs.db</c> и <c>index-rs.lock</c> (своя схема, решение (Б) TSK-131; общие у них только YAML и <c>write.lock</c>).
/// Имя файла кэша — не поведение для пользователя, поэтому тесты, которые заглядывают в кэш (хуки git его обновляют, чтение ждёт
/// его блокировки), берут файлы той консоли, что их создала: тесты гоняются и против чужого бинарника (<c>TASKER_BIN</c>).
/// </summary>
public static class IndexCache
{
    public static string Folder(string workspaceFolder) => Path.Combine(workspaceFolder, ".tasker", ".cache");

    /// <summary>Кэш создала Rust-консоль (её файл индекса есть, .NET-ского нет).</summary>
    private static bool IsRust(string workspaceFolder) =>
        File.Exists(Path.Combine(Folder(workspaceFolder), "index-rs.db")) && !File.Exists(Path.Combine(Folder(workspaceFolder), "index.db"));

    /// <summary>Файл индекса (может ещё не существовать).</summary>
    public static string Database(string workspaceFolder) =>
        Path.Combine(Folder(workspaceFolder), IsRust(workspaceFolder) ? "index-rs.db" : "index.db");

    /// <summary>Короткая блокировка индекса на время сверки. Папка должна быть уже открыта консолью: по ней видно, чей это кэш.</summary>
    public static string Lock(string workspaceFolder) =>
        Path.Combine(Folder(workspaceFolder), IsRust(workspaceFolder) ? "index-rs.lock" : "index.lock");
}
