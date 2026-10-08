namespace Tasker.Stress;

/// <summary>Одна строка «ожидание / факт» сценария.</summary>
public sealed record Fact(string Expected, string Actual, bool Pass);

/// <summary>Итог сценария: что ожидали и что получили, нарушения инвариантов, длительность, число вызовов.</summary>
public sealed class ScenarioResult(string name, string description)
{
    public string Name { get; } = name;

    public string Description { get; } = description;

    public List<Fact> Facts { get; } = [];

    public Findings Findings { get; } = new();

    public TimeSpan Duration { get; set; }

    public int Ops { get; set; }

    public string Setup { get; set; } = "";

    public bool Passed => Findings.Violations.Count == 0 && Facts.All(x => x.Pass);

    /// <summary>Проверка с описанием: нарушенная становится нарушением в отчёте.</summary>
    public bool Check(bool pass, string expected, string actual)
    {
        Facts.Add(new Fact(expected, actual, pass));
        if (!pass)
            Findings.Violations.Add($"{expected}: {actual}");
        return pass;
    }

    public void Note(string text) => Findings.Notes.Add(text);

    /// <summary>
    /// Все вызовы сценария, чей исход не из <paramref name="allowed"/>, — нарушения (неожиданные ошибки): сгруппированы по тексту, чтобы отчёт читался.
    /// </summary>
    public void RequireOutcomes(Recorder recorder, params Outcome[] allowed)
    {
        var bad = recorder.Samples.Where(x => x.Scenario == Name && !allowed.Contains(x.Outcome)).ToArray();
        Check(bad.Length == 0, $"only {string.Join("/", allowed)} outcomes", bad.Length == 0 ? "yes" : $"{bad.Length} unexpected");
        foreach (var group in bad.GroupBy(x => $"{x.Op} {x.Outcome}: {x.Detail}").Take(5))
            Findings.Notes.Add($"unexpected ({group.Count()}x) {group.Key}");
    }
}
