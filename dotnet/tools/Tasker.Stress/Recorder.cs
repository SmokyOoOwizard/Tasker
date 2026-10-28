using System.Diagnostics;
using System.Text;

namespace Tasker.Stress;

/// <summary>Чем кончился вызов клиента.</summary>
public enum Outcome
{
    Ok,

    /// <summary>Оптимистическая версия: запись изменил кто-то другой (<c>modified</c>, 409).</summary>
    Modified,

    /// <summary>Запись занята другим на время правки (<c>locked</c>).</summary>
    Locked,

    /// <summary>Сущности нет (удалена другим).</summary>
    NotFound,

    /// <summary>Отказ по существу, а не сбой: связь замкнула бы цикл (<c>Cycle:</c>, 400/<c>invalid</c>) — так и должно быть у одной из двух встречных связей.</summary>
    Invalid,

    /// <summary>Демон убит стендом (kill -9): соединения нет — ожидаемо, пока он мёртв.</summary>
    Down,

    /// <summary>Всё остальное: не должно случаться в штатной работе.</summary>
    Error
}

/// <summary>Один вызов: что, кто, чем кончился и сколько длился.</summary>
public sealed record Sample(string Scenario, string Client, string Kind, string Op, Outcome Outcome, double Ms, string? Detail);

/// <summary>Собирает вызовы всех клиентов (потокобезопасно) и считает по ним отчёт.</summary>
public sealed class Recorder
{
    private readonly List<Sample> _samples = [];
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    public void Add(Sample sample)
    {
        lock (_samples)
            _samples.Add(sample);
    }

    public IReadOnlyList<Sample> Samples
    {
        get
        {
            lock (_samples)
                return _samples.ToArray();
        }
    }

    public TimeSpan Elapsed => _clock.Elapsed;

    /// <summary>Перцентиль по отсортированному массиву (ближайший ранг).</summary>
    public static double Percentile(IReadOnlyList<double> sorted, double p)
    {
        if (sorted.Count == 0)
            return 0;
        var rank = (int)Math.Ceiling(p / 100 * sorted.Count) - 1;
        return sorted[Math.Clamp(rank, 0, sorted.Count - 1)];
    }

    /// <summary>Таблица latency по вызовам: p50/p95/p99, число и итоги.</summary>
    public static string Table(IEnumerable<Sample> samples, Func<Sample, string> key, string title)
    {
        var rows = samples.GroupBy(key).OrderBy(x => x.Key).Select(g =>
        {
            var ms = g.Select(x => x.Ms).Order().ToArray();
            return (g.Key, Count: ms.Length, P50: Percentile(ms, 50), P95: Percentile(ms, 95), P99: Percentile(ms, 99), Max: ms.Max(),
                Ok: g.Count(x => x.Outcome == Outcome.Ok), Mod: g.Count(x => x.Outcome == Outcome.Modified), Lck: g.Count(x => x.Outcome == Outcome.Locked),
                Nf: g.Count(x => x.Outcome == Outcome.NotFound), Down: g.Count(x => x.Outcome == Outcome.Down), Err: g.Count(x => x.Outcome == Outcome.Error));
        }).ToArray();
        if (rows.Length == 0)
            return "";

        var width = Math.Max(title.Length, rows.Max(x => x.Key.Length));
        var text = new StringBuilder();
        text.AppendLine($"{title.PadRight(width)}  {"n",6} {"ok",6} {"modif",6} {"locked",6} {"nfound",6} {"down",6} {"error",6}  {"p50 ms",8} {"p95 ms",8} {"p99 ms",8} {"max ms",8}");
        foreach (var r in rows)
            text.AppendLine($"{r.Key.PadRight(width)}  {r.Count,6} {r.Ok,6} {r.Mod,6} {r.Lck,6} {r.Nf,6} {r.Down,6} {r.Err,6}  {r.P50,8:F0} {r.P95,8:F0} {r.P99,8:F0} {r.Max,8:F0}");
        return text.ToString();
    }
}
