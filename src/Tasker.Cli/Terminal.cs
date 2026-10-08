using Tasker.Global;

namespace Tasker.Cli;

/// <summary>Состояние терминала на момент вызова: идёт ли вывод в терминал и сколько в нём знаков в строке (0 — неизвестно).</summary>
internal sealed record Terminal(bool IsOutput, int Columns, bool IsScreen = false)
{
    /// <summary>Нижняя граница ширины: уже не сжимаем, чтобы не получить пустой вывод.</summary>
    public const int MinWidth = 20;

    /// <summary>
    /// Терминал сейчас: размер окна читается при каждом вызове (окно могли изменить). Вывод считается терминальным только когда
    /// <paramref name="output"/> — настоящий <see cref="Console.Out"/> и он не перенаправлен. Размер: окно, иначе <c>COLUMNS</c>; не удалось — 0.
    /// </summary>
    public static Terminal Current(TextWriter output)
    {
        var isOutput = ReferenceEquals(output, Console.Out) && !Console.IsOutputRedirected;
        var columns = 0;
        if (isOutput)
        {
            try
            {
                columns = UsableColumns(Console.WindowWidth, OperatingSystem.IsWindows());
            }
            catch (Exception e) when (e is IOException or PlatformNotSupportedException or InvalidOperationException)
            {
                columns = 0;
            }
        }

        if (columns <= 0 && int.TryParse(AppEnvironment.Get("COLUMNS"), out var fromEnvironment))
            columns = fromEnvironment;
        // «Под watch»: stdout не терминал, но экспортированы обе COLUMNS и LINES (так делает procps watch) — вывод экранный.
        var screen = !isOutput && columns > 0 && PositiveNumber(AppEnvironment.Get("LINES"));
        return new Terminal(isOutput, Math.Max(columns, 0), screen);
    }

    /// <summary>
    /// Сколько знаков строки можно занять. В классической консоли Windows (conhost, cmd.exe) строка ровно на всю ширину окна уходит
    /// на следующую строку сама, и после неё перевод строки даёт пустую строку: на Windows оставляем последний столбец свободным.
    /// </summary>
    public static int UsableColumns(int windowWidth, bool windows) => windows && windowWidth > 1 ? windowWidth - 1 : windowWidth;

    private static bool PositiveNumber(string? text) => int.TryParse(text, out var number) && number > 0;

    /// <summary>
    /// Предельная ширина строк текстового вывода; null — не обрезать. Явное требование — <c>--width N</c> / <c>TASKER_WIDTH</c> с числом
    /// (задаёт ширину сам) или <c>--truncate</c> / <c>auto</c> / <c>0</c> (ширина терминала): обрезают и при перенаправлении, если ширина известна.
    /// <c>--no-truncate</c> отключает всё; <c>TASKER_WIDTH=off</c> / <c>0</c> (и <c>--width off</c>) — тоже не обрезать. Ничего не задано —
    /// обрезаем в терминале с известной шириной и «под watch» (<see cref="IsScreen"/>: не терминал, но заданы обе <c>COLUMNS</c> и <c>LINES</c>).
    /// </summary>
    /// <exception cref="CliException">Ширина — не число и не <c>auto</c>; <c>--truncate</c> вместе с <c>--no-truncate</c>.</exception>
    public int? Limit(bool truncate, bool noTruncate, string? width)
    {
        if (noTruncate)
        {
            if (truncate)
                throw new CliException("Use either --truncate or --no-truncate, not both");
            return null;
        }

        var fromVariable = width == null;
        var requested = width ?? AppEnvironment.Get("TASKER_WIDTH");
        var auto = truncate;
        if (!string.IsNullOrWhiteSpace(requested))
        {
            var text = requested.Trim();
            if (text.Equals("off", StringComparison.OrdinalIgnoreCase) || (fromVariable && text == "0"))
            {
                if (truncate)
                    throw new CliException("Use either --truncate or --no-truncate, not both");
                return null;
            }

            if (text.Equals("auto", StringComparison.OrdinalIgnoreCase))
                auto = true;
            else if (!int.TryParse(text, out var number) || number < 0)
                throw new CliException($"Width must be a non-negative number or 'auto', got '{requested}'");
            else if (number == 0)
                auto = true;
            else
                return Math.Max(number, MinWidth);
        }

        if (!auto && !IsOutput && !IsScreen)
            return null; // не терминал и ничего не просили: файл и конвейер не обрезаем
        return Columns > 0 ? Math.Max(Columns, MinWidth) : null;
    }
}
