using System.Text;

namespace Tasker.Cli.Completion;

/// <summary>Что сделала правка файла профиля.</summary>
internal enum ProfileChange
{
    /// <summary>Блока не было — дописан в конец.</summary>
    Added,

    /// <summary>Блок был другим — заменён.</summary>
    Replaced,

    /// <summary>Блок уже такой же — файл не меняется.</summary>
    Unchanged,

    /// <summary>Блок удалён.</summary>
    Removed,

    /// <summary>Удалять нечего: блока нет.</summary>
    Absent,

    /// <summary>В файле только одна из двух строк-меток: его не трогаем, пусть человек исправит сам.</summary>
    Broken
}

/// <summary>
/// Подключение автодополнения к профилю PowerShell (<c>$PROFILE</c>) — как в scripts/install.sh для zsh и bash: один помеченный
/// блок между <c>&gt;&gt;&gt;</c> и <c>&lt;&lt;&lt;</c>, повторная установка заменяет его, а не дублирует; всё, что вокруг, не меняется.
/// Это чистые функции над текстом файла: ни диска, ни ОС. Работа с файлами — <see cref="PowerShellProfileFiles"/>.
/// </summary>
internal static class PowerShellProfile
{
    public const string Begin = "# >>> tasker completion >>>";
    public const string End = "# <<< tasker completion <<<";

    /// <summary>
    /// Блок для профиля (без завершающего перевода строки): подключает файл со скриптом, если он есть. Сам скрипт в профиль не
    /// копируется — тогда его обновляет установка, а запуск PowerShell не вызывает <c>tasker</c>.
    /// </summary>
    public static string Block(string scriptPath, string newline) => string.Join(newline,
        Begin,
        "# Tab completion for tasker, added by 'tasker completion pwsh --install'. To undo: delete this block or run 'tasker completion pwsh --uninstall'.",
        $"if (Test-Path -LiteralPath {Quote(scriptPath)}) {{ . {Quote(scriptPath)} }}",
        End);

    /// <summary>Строка в одинарных кавычках PowerShell: <c>'</c> (и «умные» одинарные кавычки, которые он тоже считает кавычками) удваиваются.</summary>
    public static string Quote(string value)
    {
        var builder = new StringBuilder("'");
        foreach (var c in value)
        {
            builder.Append(c);
            if (c is '\'' or '‘' or '’' or '‚' or '‛')
                builder.Append(c);
        }

        return builder.Append('\'').ToString();
    }

    /// <summary>Перевод строки, которым пишет файл: как в нём (CRLF, если он есть), у пустого — по умолчанию системы.</summary>
    public static string NewlineOf(string content, string fallback) =>
        content.Contains("\r\n") ? "\r\n" : content.Contains('\n') ? "\n" : fallback;

    /// <summary>Ставит блок: заменяет существующий или дописывает в конец (отделив пустой строкой от предыдущего содержимого).</summary>
    public static (string Content, ProfileChange Change) Install(string content, string block, string newline)
    {
        var lines = Split(content);
        switch (Find(lines))
        {
            case (-2, _):
                return (content, ProfileChange.Broken);
            case (>= 0 and var begin, var end):
                var existing = string.Join(newline, lines[begin..(end + 1)]);
                if (existing == block)
                    return (content, ProfileChange.Unchanged);
                var replaced = new List<string>(lines[..begin]) { block };
                replaced.AddRange(lines[(end + 1)..]);
                return (string.Join(newline, replaced), ProfileChange.Replaced);
        }

        if (content.Length == 0)
            return (block + newline, ProfileChange.Added);
        var separator = content.EndsWith('\n') ? newline : newline + newline;
        return (content + separator + block + newline, ProfileChange.Added);
    }

    /// <summary>Убирает блок (и пустую строку перед ним, которой отделили при установке); остальное содержимое — как было.</summary>
    public static (string Content, ProfileChange Change) Uninstall(string content, string newline)
    {
        var lines = Split(content);
        switch (Find(lines))
        {
            case (-2, _):
                return (content, ProfileChange.Broken);
            case (-1, _):
                return (content, ProfileChange.Absent);
        }

        var (begin, end) = Find(lines);
        var from = begin > 0 && lines[begin - 1].Length == 0 && begin - 1 > 0 ? begin - 1 : begin;
        var rest = new List<string>(lines[..from]);
        rest.AddRange(lines[(end + 1)..]);
        var text = string.Join(newline, rest);
        // Блок стоял в самом конце: завершающий перевод строки остаётся у предыдущей строки.
        if (rest.Count > 0 && rest[^1].Length != 0 && end == lines.Length - 1)
            text += newline;
        return (rest.All(x => x.Length == 0) ? "" : text, ProfileChange.Removed);
    }

    // Строки без символов перевода (CRLF и LF); последняя пустая — если текст кончается переводом строки.
    private static string[] Split(string content) => content.Replace("\r\n", "\n").Split('\n');

    // Границы блока: (начало, конец); (-1, -1) — блока нет; (-2, -2) — есть только одна метка или они в неправильном порядке.
    private static (int Begin, int End) Find(string[] lines)
    {
        var begin = Array.FindIndex(lines, x => x.TrimEnd() == Begin);
        var end = Array.FindIndex(lines, x => x.TrimEnd() == End);
        if (begin < 0 && end < 0)
            return (-1, -1);
        return begin < 0 || end < begin ? (-2, -2) : (begin, end);
    }

    /// <summary>Где лежат профили PowerShell текущего пользователя (<c>$PROFILE</c>, «текущий пользователь, текущий узел»).</summary>
    /// <param name="documents">Папка «Документы» (на Windows может быть перенесена в OneDrive); только для Windows.</param>
    /// <param name="home">Домашняя папка; только не для Windows.</param>
    /// <param name="configHome"><c>XDG_CONFIG_HOME</c>, если задана; только не для Windows.</param>
    /// <returns>Windows: профили PowerShell 7 и Windows PowerShell 5.1 (они в разных папках; ставим в оба); иначе — профиль pwsh.</returns>
    public static string[] DefaultProfiles(bool windows, string documents, string home, string? configHome)
    {
        const string File = "Microsoft.PowerShell_profile.ps1";
        if (windows)
            return [Path.Combine(documents, "PowerShell", File), Path.Combine(documents, "WindowsPowerShell", File)];
        var config = string.IsNullOrEmpty(configHome) ? Path.Combine(home, ".config") : configHome;
        return [Path.Combine(config, "powershell", File)];
    }
}
