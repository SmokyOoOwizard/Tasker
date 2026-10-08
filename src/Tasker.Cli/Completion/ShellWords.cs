using System.Text;

namespace Tasker.Cli.Completion;

/// <summary>По чьим правилам разбирать набранную строку.</summary>
internal enum ShellDialect
{
    /// <summary>zsh и bash: <c>'…'</c> буквально, в <c>"…"</c> и вне кавычек обратная косая черта экранирует.</summary>
    Posix,

    /// <summary>
    /// PowerShell: <c>'…'</c> буквально, <c>''</c> внутри — одна кавычка; в <c>"…"</c> экранирует обратный апостроф, <c>""</c> — одна
    /// кавычка; вне кавычек экранирует обратный апостроф, а обратная косая черта — обычный знак (пути Windows: <c>C:\work</c>).
    /// </summary>
    PowerShell
}

/// <summary>
/// Строка, набранная в оболочке, — для разбора System.CommandLine: оно понимает только двойные кавычки, а в zsh и bash слова с
/// пробелами пишут и как <c>'Основная доска'</c>, и как <c>Основная\ доска</c>. Каждое слово разбирается по правилам оболочки и
/// записывается заново в двойных кавычках, если в нём есть пробелы.
/// </summary>
internal static class ShellWords
{
    /// <summary>
    /// Строка для разбора. Последнее слово, если оно набирается (курсор сразу за ним), с пробелами остаётся с незакрытой кавычкой:
    /// так System.CommandLine видит его одним словом, а <see cref="Unquote"/> достаёт набранное.
    /// </summary>
    public static string Normalize(string text, ShellDialect dialect = ShellDialect.Posix)
    {
        var powerShell = dialect == ShellDialect.PowerShell;
        var escape = powerShell ? '`' : '\\';
        var words = new List<(string Text, bool Quoted)>();
        var current = new StringBuilder();
        var inWord = false;
        var quoted = false;
        var quote = '\0';

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quote == '\'')
            {
                if (powerShell && IsSingleQuote(c) && i + 1 < text.Length && IsSingleQuote(text[i + 1]))
                    current.Append(text[++i]);
                else if (powerShell ? IsSingleQuote(c) : c == '\'')
                    quote = '\0';
                else
                    current.Append(c);
            }
            else if (quote == '"')
            {
                if (powerShell && IsDoubleQuote(c) && i + 1 < text.Length && IsDoubleQuote(text[i + 1]))
                    current.Append(text[++i]);
                else if (powerShell ? IsDoubleQuote(c) : c == '"')
                    quote = '\0';
                else if (powerShell && c == '`' && i + 1 < text.Length)
                    current.Append(text[++i]);
                else if (!powerShell && c == '\\' && i + 1 < text.Length && text[i + 1] is '"' or '\\' or '$' or '`')
                    current.Append(text[++i]);
                else
                    current.Append(c);
            }
            else if (char.IsWhiteSpace(c))
            {
                if (inWord)
                    words.Add((current.ToString(), quoted));
                current.Clear();
                inWord = quoted = false;
            }
            else if (c == escape)
            {
                if (i + 1 < text.Length)
                    current.Append(text[++i]);
                inWord = true;
            }
            else if (powerShell ? IsSingleQuote(c) || IsDoubleQuote(c) : c is '\'' or '"')
            {
                quote = IsSingleQuote(c) ? '\'' : '"';
                inWord = quoted = true;
            }
            else
            {
                current.Append(c);
                inWord = true;
            }
        }

        var typing = inWord;
        if (typing)
            words.Add((current.ToString(), quoted));

        var result = new StringBuilder();
        for (var i = 0; i < words.Count; i++)
        {
            var (word, wasQuoted) = words[i];
            var needsQuotes = word.Length == 0 && wasQuoted || word.Any(char.IsWhiteSpace);
            var last = typing && i == words.Count - 1;
            if (!needsQuotes)
                result.Append(word);
            else if (last && (quote != '\0' || !wasQuoted))
                result.Append('"').Append(word);
            else
                result.Append('"').Append(word).Append('"');
            if (i < words.Count - 1)
                result.Append(' ');
        }

        if (!typing && text.Length > 0)
            result.Append(' ');
        return result.ToString();
    }

    // PowerShell считает кавычками и «умные» (U+2018..U+201F), которые подставляют редакторы и мессенджеры.
    private static bool IsSingleQuote(char c) => c is '\'' or '\u2018' or '\u2019' or '\u201A' or '\u201B';

    private static bool IsDoubleQuote(char c) => c is '"' or '\u201C' or '\u201D' or '\u201E';

    /// <summary>
    /// Делит строку, уже приведённую <see cref="Normalize"/>, на то, что до последнего слова, и само слово (без кавычек);
    /// строка кончается пробелом — слово пустое.
    /// </summary>
    public static (string Preceding, string Word) SplitLast(string normalized)
    {
        var start = 0;
        var quoted = false;
        for (var i = 0; i < normalized.Length; i++)
        {
            if (normalized[i] == '"')
                quoted = !quoted;
            else if (!quoted && char.IsWhiteSpace(normalized[i]))
                start = i + 1;
        }

        return (normalized[..start], normalized[start..].Replace("\"", ""));
    }

    /// <summary>Слово, как его дали System.CommandLine (с открывающей кавычкой, если она есть), — без кавычек.</summary>
    public static string Unquote(string word)
    {
        if (word.StartsWith('"'))
            word = word[1..];
        return word.EndsWith('"') ? word[..^1] : word;
    }
}
