using System.Text;

namespace Tasker.Daemon;

/// <summary>
/// Строки для <c>CreateProcessW</c> (Windows принимает одну командную строку, а не массив аргументов). Чистые функции без вызовов ОС:
/// их можно проверить на любой системе.
/// </summary>
public static class WindowsCommandLine
{
    /// <summary>Командная строка, которую <c>CommandLineToArgvW</c> и рантайм .NET разберут обратно в те же аргументы.</summary>
    public static string Build(string file, IEnumerable<string> arguments)
    {
        var line = new StringBuilder();
        Quote(line, file);
        foreach (var argument in arguments)
        {
            line.Append(' ');
            Quote(line, argument);
        }

        return line.ToString();
    }

    private static void Quote(StringBuilder line, string argument)
    {
        if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0)
        {
            line.Append(argument);
            return;
        }

        line.Append('"');
        for (var i = 0; i < argument.Length; i++)
        {
            var slashes = 0;
            while (i < argument.Length && argument[i] == '\\')
            {
                slashes++;
                i++;
            }

            if (i == argument.Length)
            {
                // Обратные косые перед закрывающей кавычкой удваиваются.
                line.Append('\\', slashes * 2);
                break;
            }

            if (argument[i] == '"')
                line.Append('\\', slashes * 2 + 1).Append('"');
            else
                line.Append('\\', slashes).Append(argument[i]);
        }

        line.Append('"');
    }

    /// <summary>
    /// Блок окружения для <c>CreateProcessW</c> с <c>CREATE_UNICODE_ENVIRONMENT</c>: «имя=значение», по возрастанию имени без учёта регистра
    /// (так требует Windows), каждая строка кончается нулём и ещё один ноль в конце. Переменные без значения пропускаются.
    /// </summary>
    public static string EnvironmentBlock(IEnumerable<KeyValuePair<string, string?>> variables)
    {
        var block = new StringBuilder();
        foreach (var (name, value) in variables.Where(x => x.Value != null).OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
            block.Append(name).Append('=').Append(value).Append('\0');
        return block.Append('\0').ToString();
    }
}
