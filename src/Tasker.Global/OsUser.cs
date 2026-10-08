namespace Tasker.Global;

/// <summary>Имя пользователя операционной системы для подписи правок и блокировок.</summary>
public static class OsUser
{
    /// <summary>Имя пользователя ОС без домена.</summary>
    public static string Name => Normalize(Environment.UserName);

    /// <summary>
    /// <c>DOMAIN\user</c> и <c>user@domain.local</c> (так имя встречается на Windows в переменных окружения и в сведениях о доменных
    /// учётных записях) — просто <c>user</c>: домен в подписи только мешает. Пусто — «user».
    /// </summary>
    public static string Normalize(string? name)
    {
        var text = name?.Trim() ?? "";
        var slash = text.LastIndexOf('\\');
        if (slash >= 0)
            text = text[(slash + 1)..];
        var at = text.IndexOf('@');
        if (at > 0)
            text = text[..at];
        return text.Length == 0 ? "user" : text;
    }
}
