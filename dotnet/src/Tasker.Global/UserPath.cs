namespace Tasker.Global;

/// <summary>
/// Пути, набранные человеком. <c>~</c> раскрывает оболочка (zsh, bash), но не cmd.exe и не PowerShell при вызове программы:
/// там <c>-w ~\work</c> доходит до нас как есть. Раскрываем сами, на любой платформе, и с <c>/</c>, и с <c>\</c>.
/// </summary>
public static class UserPath
{
    /// <summary><c>~</c>, <c>~/…</c> и <c>~\…</c> — в каталоге пользователя; всё остальное (в том числе <c>~user</c>) без изменений.</summary>
    public static string? Expand(string? path) => Expand(path, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    /// <param name="home">Каталог пользователя; пусто (профиль не определён) — путь не меняется.</param>
    public static string? Expand(string? path, string home)
    {
        if (string.IsNullOrEmpty(path) || path[0] != '~' || string.IsNullOrEmpty(home))
            return path;
        if (path.Length == 1)
            return home;
        return path[1] is '/' or '\\' ? Path.Combine(home, path[2..]) : path;
    }
}
