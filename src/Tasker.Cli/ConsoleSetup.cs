using System.Text;

namespace Tasker.Cli;

/// <summary>
/// Потоки консоли и их кодировка. На macOS и Linux консоль и так UTF-8. На Windows cmd.exe и старый PowerShell работают в кодовой
/// странице (866, 1251), и кириллица в заголовках ломается; перенаправление (<c>&gt; file</c>, <c>| more</c>) .NET пишет в той же
/// кодовой странице. Поэтому на Windows: в терминале переключаем кодовую страницу консоли на UTF-8 (и возвращаем при выходе),
/// а при перенаправлении пишем и читаем UTF-8 без BOM напрямую в потоки, минуя <see cref="Console"/>.
/// </summary>
internal sealed class ConsoleSetup : IDisposable
{
    /// <summary>UTF-8 без BOM: файл, созданный через <c>&gt;</c>, не должен начинаться с метки порядка байт.</summary>
    public static Encoding Utf8 { get; } = new UTF8Encoding(false);

    private readonly Action? _restore;

    private ConsoleSetup(TextWriter output, TextWriter error, TextReader? input, Action? restore)
    {
        Output = output;
        Error = error;
        Input = input;
        _restore = restore;
    }

    public TextWriter Output { get; }

    public TextWriter Error { get; }

    /// <summary>Перенаправленный ввод в UTF-8; null — читать <see cref="Console.In"/> (терминал или не Windows).</summary>
    public TextReader? Input { get; }

    /// <summary>Настоящая консоль процесса: не Windows — как есть, Windows — в UTF-8 (<see cref="Open(bool, bool, bool)"/>).</summary>
    public static ConsoleSetup Open() =>
        OperatingSystem.IsWindows() ? Open(Console.IsOutputRedirected, Console.IsErrorRedirected, Console.IsInputRedirected) : Plain();

    private static ConsoleSetup Plain() => new(Console.Out, Console.Error, null, null);

    /// <summary>Windows: см. описание класса. Параметры — какие из потоков перенаправлены.</summary>
    internal static ConsoleSetup Open(bool outputRedirected, bool errorRedirected, bool inputRedirected)
    {
        Encoding? previousOutput = null;
        Encoding? previousInput = null;

        // Кодовая страница консоли — одна на вывод (stdout и stderr вместе), поэтому она переключается, пока хотя бы один поток в терминале.
        // Если консоли нет вовсе (процесс запущен без окна), установка бросает IOException: это не помеха, пишем напрямую в потоки.
        if (!outputRedirected || !errorRedirected)
        {
            try
            {
                previousOutput = Console.OutputEncoding;
                if (previousOutput.CodePage != Utf8.CodePage)
                    Console.OutputEncoding = Utf8;
                else
                    previousOutput = null;
            }
            catch (IOException)
            {
                previousOutput = null;
            }
        }

        if (!inputRedirected)
        {
            try
            {
                previousInput = Console.InputEncoding;
                if (previousInput.CodePage != Utf8.CodePage)
                    Console.InputEncoding = Utf8;
                else
                    previousInput = null;
            }
            catch (IOException)
            {
                previousInput = null;
            }
        }

        var output = outputRedirected ? Writer(Console.OpenStandardOutput()) : Console.Out;
        var error = errorRedirected ? Writer(Console.OpenStandardError()) : Console.Error;
        var input = inputRedirected ? new StreamReader(Console.OpenStandardInput(), Utf8, detectEncodingFromByteOrderMarks: true) : null;

        return new ConsoleSetup(output, error, input, () =>
        {
            Restore(() => Console.OutputEncoding = previousOutput!, previousOutput);
            Restore(() => Console.InputEncoding = previousInput!, previousInput);
        });
    }

    private static StreamWriter Writer(Stream stream) => new(stream, Utf8, 4096) { AutoFlush = true };

    private static void Restore(Action restore, Encoding? previous)
    {
        if (previous == null)
            return;
        try
        {
            restore();
        }
        catch (IOException)
        {
            // Консоль уже закрыта — возвращать нечего.
        }
    }

    public void Dispose()
    {
        Output.Flush();
        Error.Flush();
        _restore?.Invoke();
    }
}
