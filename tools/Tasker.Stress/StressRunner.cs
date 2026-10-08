using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Tasker.Stress;

/// <summary>Итог прогона: результаты сценариев и все вызовы.</summary>
public sealed class StressReport(StressOptions options, IReadOnlyList<ScenarioResult> results, Recorder recorder, TimeSpan elapsed, double startupMs)
{
    public StressOptions Options { get; } = options;

    public IReadOnlyList<ScenarioResult> Results { get; } = results;

    public Recorder Recorder { get; } = recorder;

    public TimeSpan Elapsed { get; } = elapsed;

    /// <summary>Сколько занимает пустой вызов консоли (запуск процесса): нижняя граница любого вызова-процесса.</summary>
    public double StartupMs { get; } = startupMs;

    public bool Passed => Results.All(x => x.Passed);

    public string ToText()
    {
        var o = Options;
        var text = new StringBuilder();
        text.AppendLine($"== tasker-stress: {o.Clients} clients x {o.Ops} ops, client {o.Client}, storage {o.Storage}, {Elapsed.TotalSeconds:F0} s ==");
        text.AppendLine($"(console call floor: an empty `tasker whoami` takes {StartupMs:F0} ms, the process start)");
        text.AppendLine();
        foreach (var r in Results)
        {
            text.AppendLine($"[{(r.Passed ? "PASS" : "FAIL")}] {r.Name} — {r.Description}");
            var samples = Recorder.Samples.Where(x => x.Scenario == r.Name || r.Name == "scale" && x.Scenario.StartsWith("scale-")).ToArray();
            var rate = r.Duration.TotalSeconds > 0 ? samples.Length / r.Duration.TotalSeconds : 0;
            text.AppendLine($"       {r.Setup}; {samples.Length} calls in {r.Duration.TotalSeconds:F1} s = {rate:F1} calls/s");
            foreach (var f in r.Facts)
                text.AppendLine($"       {(f.Pass ? "ok  " : "FAIL")} {f.Expected} -> {f.Actual}");
            foreach (var n in r.Findings.Notes)
                text.AppendLine($"       note {n}");
            text.AppendLine();
        }

        var all = Recorder.Samples;
        text.AppendLine(Recorder.Table(all, x => x.Scenario, "scenario"));
        text.AppendLine(Recorder.Table(all, x => $"{x.Kind} {x.Op}", "client / call"));
        text.AppendLine(Recorder.Table(all, x => x.Kind, "client"));
        var unexpected = all.Where(x => x.Outcome == Outcome.Error).GroupBy(x => $"{x.Scenario}: {x.Kind} {x.Op}: {x.Detail}").ToArray();
        text.AppendLine($"calls: {all.Count}; ok {all.Count(x => x.Outcome == Outcome.Ok)}; expected conflicts modified {all.Count(x => x.Outcome == Outcome.Modified)}, " +
            $"locked {all.Count(x => x.Outcome == Outcome.Locked)}, not found {all.Count(x => x.Outcome == Outcome.NotFound)}; unexpected errors {all.Count(x => x.Outcome == Outcome.Error)}");
        foreach (var group in unexpected.Take(15))
            text.AppendLine($"  unexpected {group.Count()}x {group.Key}");
        text.AppendLine();
        text.AppendLine(Passed ? "RESULT: all invariants hold" : $"RESULT: FAILED — {Results.Count(x => !x.Passed)} scenario(s) with violations");
        foreach (var r in Results.Where(x => !x.Passed))
        {
            foreach (var v in r.Findings.Violations.Take(10))
                text.AppendLine($"  [{r.Name}] {v}");
        }

        return text.ToString();
    }

    public JsonObject ToJson() => new()
    {
        ["clients"] = Options.Clients,
        ["ops"] = Options.Ops,
        ["client"] = Options.Client,
        ["storage"] = Options.Storage,
        ["elapsedSeconds"] = Elapsed.TotalSeconds,
        ["startupMs"] = StartupMs,
        ["passed"] = Passed,
        ["scenarios"] = new JsonArray(Results.Select(r => (JsonNode)new JsonObject
        {
            ["name"] = r.Name,
            ["passed"] = r.Passed,
            ["seconds"] = r.Duration.TotalSeconds,
            ["calls"] = r.Ops,
            ["facts"] = new JsonArray(r.Facts.Select(f => (JsonNode)new JsonObject { ["expected"] = f.Expected, ["actual"] = f.Actual, ["pass"] = f.Pass }).ToArray()),
            ["violations"] = new JsonArray(r.Findings.Violations.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray()),
            ["notes"] = new JsonArray(r.Findings.Notes.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray())
        }).ToArray()),
        ["samples"] = new JsonArray(Recorder.Samples.Select(s => (JsonNode)new JsonObject
        {
            ["scenario"] = s.Scenario, ["client"] = s.Client, ["kind"] = s.Kind, ["op"] = s.Op, ["outcome"] = s.Outcome.ToString(), ["ms"] = Math.Round(s.Ms, 1), ["detail"] = s.Detail
        }).ToArray())
    };
}

public static class StressRunner
{
    /// <summary>Поднимает стенд, прогоняет сценарии по порядку, возвращает отчёт. Временная папка удаляется (если не <c>--keep</c>).</summary>
    public static async Task<StressReport> Run(StressOptions options, Action<string>? progress = null)
    {
        var total = Stopwatch.StartNew();
        await using var stand = new Stand(options);
        progress?.Invoke($"stand: {stand.Root}");
        await stand.Setup();
        var recorder = new Recorder();
        var ctx = new StressContext(options, stand, recorder);
        progress?.Invoke(stand.DaemonRunning ? $"daemon: {stand.McpUrl}" : "daemon: not needed");

        // Нижняя граница вызова консоли: запуск процесса без работы.
        var floor = new List<double>();
        for (var i = 0; i < 3; i++)
        {
            var watch = Stopwatch.StartNew();
            await stand.Cli(stand.Main, false, "whoami");
            floor.Add(watch.Elapsed.TotalMilliseconds);
        }

        var results = new List<ScenarioResult>();
        foreach (var name in options.Scenarios)
        {
            progress?.Invoke($"scenario {name} ...");
            ScenarioResult result;
            try
            {
                result = await Scenarios.Run(name, ctx);
            }
            catch (Exception e)
            {
                result = new ScenarioResult(name, "crashed");
                result.Check(false, "the scenario runs", e.ToString());
            }

            progress?.Invoke($"  {(result.Passed ? "PASS" : "FAIL")} {name} ({result.Duration.TotalSeconds:F1} s)");
            results.Add(result);
        }

        var report = new StressReport(options, results, recorder, total.Elapsed, floor.Order().ElementAt(1));
        if (options.JsonReport != null)
            await File.WriteAllTextAsync(options.JsonReport, report.ToJson().ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return report;
    }
}
