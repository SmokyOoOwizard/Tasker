using System.Diagnostics;

namespace Tasker.Core;

/// <summary>
/// Фазы одного вызова <c>tasker</c> для профилирования (стенд <c>scripts/perf/bench.py</c>). Включается переменной
/// <c>TASKER_PROFILE=1</c>; без неё <see cref="Mark"/> ничего не делает (одна проверка булева флага). Метки печатаются в stderr
/// после выполнения команды строками <c>[profile] имя миллисекунды_от_старта_процесса</c>.
/// </summary>
public static class PerfTrace
{
    public static readonly bool Enabled =
        Environment.GetEnvironmentVariable("TASKER_PROFILE") is { Length: > 0 } value && value != "0";

    private static readonly object Gate = new();
    private static readonly List<(string Name, double Ms, int Jit)> Marks = [];
    private static readonly long Origin = Stopwatch.GetTimestamp();
    private static double _processOffsetMs = double.NaN;

    /// <summary>Фиксирует момент: прошедшее с запуска процесса время (запуск рантайма до первой метки входит в первую).</summary>
    public static void Mark(string name)
    {
        if (!Enabled)
            return;

        lock (Gate)
        {
            if (double.IsNaN(_processOffsetMs))
            {
                // Сколько процесс жил до того, как мы начали считать: старт рантайма и загрузка хоста.
                try
                {
                    _processOffsetMs = Math.Max(0, (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds - Stopwatch.GetElapsedTime(Origin).TotalMilliseconds);
                }
                catch (Exception)
                {
                    _processOffsetMs = 0;
                }
            }

            Marks.Add((name, _processOffsetMs + Stopwatch.GetElapsedTime(Origin).TotalMilliseconds, (int)System.Runtime.JitInfo.GetCompiledMethodCount()));
        }
    }

    private static readonly Dictionary<string, (int Count, double Ms)> Counters = [];

    /// <summary>Начало замера (null, когда профилирование выключено); конец — <see cref="Count"/>.</summary>
    public static long? Start() => Enabled ? Stopwatch.GetTimestamp() : null;

    /// <summary>Складывает время от <paramref name="started"/> в счётчик <paramref name="name"/> (число вызовов и сумма) — для частых мелких операций.</summary>
    public static void Count(string name, long? started)
    {
        if (started is not { } begin)
            return;

        var ms = Stopwatch.GetElapsedTime(begin).TotalMilliseconds;
        lock (Gate)
        {
            Counters.TryGetValue(name, out var current);
            Counters[name] = (current.Count + 1, current.Ms + ms);
        }
    }

    public static void Dump(TextWriter writer)
    {
        if (!Enabled)
            return;

        lock (Gate)
        {
            foreach (var (name, ms, jit) in Marks)
                writer.WriteLine($"[profile] {name} {ms.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)} {jit}");
            writer.WriteLine($"[count] jit.methods {System.Runtime.JitInfo.GetCompiledMethodCount()} {System.Runtime.JitInfo.GetCompilationTime().TotalMilliseconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)}");
            foreach (var (name, (count, ms)) in Counters)
                writer.WriteLine($"[count] {name} {count} {ms.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)}");
        }
    }
}
