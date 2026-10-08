using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Tasker.Stress;

/// <summary>
/// Сценарии стенда. У каждого свой проект в общей области, поэтому счёт задач и номеров серии начинается с нуля, а сценарии
/// не мешают друг другу. Клиенты (<c>cli</c>, <c>mcp</c>, <c>mix</c>) задаются параметрами; сценарии, которым нужны разные
/// держатели блокировок, используют агентов MCP независимо от параметра.
/// </summary>
public static partial class Scenarios
{
    public static async Task<ScenarioResult> Run(string name, StressContext ctx)
    {
        ctx.Scenario = name;
        return name switch
        {
            "create" => await Create(ctx),
            "edit-same" => await EditSame(ctx),
            "edit-fields" => await EditFields(ctx),
            "edit-different" => await EditDifferent(ctx),
            "locks" => await Locks(ctx),
            "cascade-wait" => await CascadeWait(ctx),
            "mass" => await Mass(ctx),
            "fields" => await Fields(ctx),
            "links" => await Links(ctx),
            "delete-while-edit" => await DeleteWhileEdit(ctx),
            "delete-project" => await DeleteProject(ctx),
            "kill" => await Kill(ctx),
            "multiws" => await MultiWorkspace(ctx),
            "scale" => await Scale(ctx),
            "upgrade" => await UpgradeUnderLoad(ctx),
            _ => throw new ArgumentException("Unknown scenario " + name)
        };
    }

    // ---------- вспомогательное ----------

    private static ScenarioResult Begin(StressContext ctx, string name, string description) => new(name, description)
    {
        Setup = $"{ctx.Options.Clients} clients x {ctx.Options.Ops} ops, {ctx.Options.Client}, {ctx.Options.Storage}"
    };

    private static Task Par(IEnumerable<StressClient> clients, Func<StressClient, int, Task> body) =>
        Task.WhenAll(clients.Select((c, i) => Task.Run(() => body(c, i))));

    private static async Task<T> Timed<T>(ScenarioResult r, Func<Task<T>> body)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            return await body();
        }
        finally
        {
            r.Duration += watch.Elapsed;
        }
    }

    private static async Task Finish(StressContext ctx, ScenarioResult r, Area area, Expect expect)
    {
        r.Ops = ctx.Recorder.Samples.Count(x => x.Scenario == r.Name);
        var findings = await Invariants.Check(ctx, area, expect, r.Name);
        r.Facts.Add(new Fact("invariants: task count, unique series numbers, YAML integrity, index = files, sync/cleanup clean, no stuck locks",
            findings.Violations.Count == 0 ? "hold" : $"{findings.Violations.Count} violation(s)", findings.Violations.Count == 0));
        r.Findings.Merge(findings);
    }

    private static string Describe(IEnumerable<Sample> samples)
    {
        var list = samples.ToArray();
        return $"ok {list.Count(x => x.Outcome == Outcome.Ok)}, modified {list.Count(x => x.Outcome == Outcome.Modified)}, locked {list.Count(x => x.Outcome == Outcome.Locked)}, " +
            $"not found {list.Count(x => x.Outcome == Outcome.NotFound)}, error {list.Count(x => x.Outcome == Outcome.Error)}";
    }

    private static McpClient Agent(StressContext ctx, Area area, int index) =>
        new($"agent{index}", ctx, area, ctx.Stand.AgentOf(area, index));

    private static async Task<Guid> CreateTask(StressClient client, string title, bool series = true)
    {
        var op = await client.Create(title, null, series);
        return op.Ok ? TaskView.Of(op.Json!).Id : Guid.Empty;
    }

    // ---------- 1. создание ----------

    private static async Task<ScenarioResult> Create(StressContext ctx)
    {
        var r = Begin(ctx, "create", "N clients x M creates into one series: every task exists, numbers unique and gap-free");
        var area = await ctx.Stand.NewProject(ctx.Stand.Main, "create");
        var clients = ctx.Clients(ctx.Options.Clients, area);

        await Timed(r, async () =>
        {
            await Par(clients, async (c, i) =>
            {
                for (var m = 0; m < ctx.Options.Ops; m++)
                    await c.Create($"task {c.Name} #{m}", $"description {m}\nsecond line");
            });
            return 0;
        });

        var total = ctx.Options.Clients * ctx.Options.Ops;
        r.RequireOutcomes(ctx.Recorder, Outcome.Ok);
        await Finish(ctx, r, area, new Expect { Tasks = total, ContiguousNumbers = true });
        return r;
    }

    // ---------- 2. одна задача, много правщиков ----------

    private static async Task<ScenarioResult> EditSame(StressContext ctx)
    {
        var r = Begin(ctx, "edit-same", "N clients append their token to ONE task's description with read -> update(version) -> retry on modified: no lost update");
        var area = await ctx.Stand.NewProject(ctx.Stand.Main, "edit-same");
        var clients = ctx.Clients(ctx.Options.Clients, area);
        var id = await CreateTask(clients[0], "shared");

        var acked = new System.Collections.Concurrent.ConcurrentBag<string>();
        long retries = 0, gaveUp = 0;
        await Timed(r, async () =>
        {
            await Par(clients, async (c, i) =>
            {
                var random = new Random(ctx.Options.Seed + i);
                for (var m = 0; m < ctx.Options.Ops; m++)
                {
                    var token = $"{c.Name}#{m}";
                    var done = false;
                    for (var attempt = 0; attempt < 300 && !done; attempt++)
                    {
                        if (await c.Read(id) is not { } view)
                            break;
                        var update = await c.Update(id, view.Version, description: (view.Description ?? "") + token + ";");
                        if (update.Ok)
                        {
                            acked.Add(token);
                            done = true;
                        }
                        else if (update.Modified)
                        {
                            Interlocked.Increment(ref retries);
                            await Task.Delay(random.Next(0, 25));
                        }
                        else
                            break;
                    }

                    if (!done)
                        Interlocked.Increment(ref gaveUp);
                }
            });
            return 0;
        });

        var final = await clients[0].Read(id);
        var tokens = (final?.Description ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries);
        var total = ctx.Options.Clients * ctx.Options.Ops;
        r.RequireOutcomes(ctx.Recorder, Outcome.Ok, Outcome.Modified);
        r.Check(gaveUp == 0, "every client eventually gets its update in (success or modified -> retry)", $"{total - gaveUp}/{total} applied, {retries} modified retries");
        r.Check(acked.Count == tokens.Length && acked.ToHashSet().SetEquals(tokens),
            "no silently lost update: the final description holds exactly the tokens whose update returned success",
            $"{acked.Count} acknowledged, {tokens.Length} in the task, {tokens.Distinct().Count()} distinct");
        if (!area.IsSqlite && final != null)
        {
            var file = Directory.GetFiles(area.TasksDir, "*.yaml").Single();
            r.Check(Invariants.FileVersion(file) == final.Version, "version = hash of the file (final consistency)", $"file {Invariants.FileVersion(file)}, reported {final.Version}");
        }

        r.Note($"modified answers (expected, retried): {retries}; {Describe(ctx.Recorder.Samples.Where(x => x.Scenario == r.Name && x.Op == "update"))}");
        await Finish(ctx, r, area, new Expect { Tasks = 1, ContiguousNumbers = true });
        return r;
    }

    // ---------- 2b. разные поля одной задачи: правка без версии не затирает чужое поле ----------

    private static async Task<ScenarioResult> EditFields(StressContext ctx)
    {
        var r = Begin(ctx, "edit-fields", "half of the clients change only the title, half only the description of ONE task, with no version of their own: no field of another client is rolled back");
        var area = await ctx.Stand.NewProject(ctx.Stand.Main, "edit-fields");
        var clients = ctx.Clients(Math.Max(ctx.Options.Clients, 2), area);
        var id = await CreateTask(clients[0], "initial title");
        await clients[0].Update(id, null, description: "initial description");

        var titles = new System.Collections.Concurrent.ConcurrentBag<string>();
        var descriptions = new System.Collections.Concurrent.ConcurrentBag<string>();
        long retries = 0;
        await Timed(r, async () =>
        {
            await Par(clients, async (c, i) =>
            {
                for (var m = 0; m < ctx.Options.Ops; m++)
                {
                    var value = $"{c.Name}-{m}";
                    for (var attempt = 0; attempt < 50; attempt++)
                    {
                        var op = i % 2 == 0 ? await c.Update(id, null, title: "T:" + value) : await c.Update(id, null, description: "D:" + value);
                        if (op.Ok)
                        {
                            (i % 2 == 0 ? titles : descriptions).Add((i % 2 == 0 ? "T:" : "D:") + value);
                            break;
                        }

                        if (!op.Modified)
                            break;
                        Interlocked.Increment(ref retries);
                    }
                }
            });
            return 0;
        });

        var final = await clients[0].Read(id);
        r.RequireOutcomes(ctx.Recorder, Outcome.Ok, Outcome.Modified);
        r.Check(final != null && titles.Contains(final.Title), "the final title is one the clients wrote (not a mix, not the initial)", final?.Title ?? "no task");
        r.Check(final != null && descriptions.Contains(final.Description ?? ""), "the final description is one the clients wrote (a title-only write did not roll it back)", final?.Description ?? "none");
        r.Note($"modified retries: {retries}");
        await Finish(ctx, r, area, new Expect { Tasks = 1, ContiguousNumbers = true });
        return r;
    }

    // ---------- 3. разные задачи ----------

    private static async Task<ScenarioResult> EditDifferent(StressContext ctx)
    {
        var r = Begin(ctx, "edit-different", "each client edits ITS OWN tasks of one project: no conflicts at all (nothing is blocked or rejected without need)");
        var area = await ctx.Stand.NewProject(ctx.Stand.Main, "edit-different");
        var clients = ctx.Clients(ctx.Options.Clients, area);
        var owned = new Guid[clients.Count][];

        ctx.Scenario = "edit-different";
        await Par(clients, async (c, i) =>
        {
            owned[i] = [await CreateTask(c, $"own {c.Name} 0"), await CreateTask(c, $"own {c.Name} 1")];
        });
        var setup = ctx.Recorder.Samples.Count(x => x.Scenario == r.Name);

        await Timed(r, async () =>
        {
            await Par(clients, async (c, i) =>
            {
                for (var m = 0; m < ctx.Options.Ops; m++)
                {
                    var id = owned[i][m % 2];
                    if (await c.Read(id) is { } view)
                        await c.Update(id, view.Version, title: $"{c.Name} edit {m}", description: $"edit {m}", status: m % 2 == 0 ? area.Done : area.Todo);
                }
            });
            return 0;
        });

        r.RequireOutcomes(ctx.Recorder, Outcome.Ok);
        var updates = ctx.Recorder.Samples.Where(x => x.Scenario == r.Name && x.Op == "update").ToArray();
        r.Check(updates.All(x => x.Outcome == Outcome.Ok) && updates.Length == ctx.Options.Clients * ctx.Options.Ops,
            "all updates of own tasks succeed (no modified/locked)", Describe(updates));
        var elapsed = r.Duration.TotalSeconds;
        r.Note($"{ctx.Options.Clients * ctx.Options.Ops * 2} calls (get + update) in {elapsed:F1} s = {(ctx.Options.Clients * ctx.Options.Ops * 2) / Math.Max(elapsed, 0.001):F1} ops/s (setup calls: {setup})");
        await Finish(ctx, r, area, new Expect { Tasks = ctx.Options.Clients * 2, ContiguousNumbers = true });
        return r;
    }

    // ---------- 10. масштабирование ----------

    private static async Task<ScenarioResult> Scale(StressContext ctx)
    {
        var r = Begin(ctx, "scale", "latency and throughput of creates vs number of clients");
        r.Setup = $"clients {string.Join(",", ctx.Options.Scale)} x {ctx.Options.Ops} ops, {ctx.Options.Client}, {ctx.Options.Storage}";
        foreach (var count in ctx.Options.Scale)
        {
            var name = $"scale-{count}";
            ctx.Scenario = name;
            var area = await ctx.Stand.NewProject(ctx.Stand.Main, name);
            var clients = ctx.Clients(count, area);
            var watch = Stopwatch.StartNew();
            await Par(clients, async (c, i) =>
            {
                for (var m = 0; m < ctx.Options.Ops; m++)
                    await c.Create($"scale {c.Name} #{m}");
            });
            watch.Stop();
            r.Duration += watch.Elapsed;

            var samples = ctx.Recorder.Samples.Where(x => x.Scenario == name).ToArray();
            var ms = samples.Select(x => x.Ms).Order().ToArray();
            r.Check(samples.All(x => x.Outcome == Outcome.Ok),
                $"{count} clients: all creates succeed", $"{Describe(samples)}; {samples.Length / watch.Elapsed.TotalSeconds:F1} ops/s, p50 {Recorder.Percentile(ms, 50):F0} ms, p95 {Recorder.Percentile(ms, 95):F0} ms, p99 {Recorder.Percentile(ms, 99):F0} ms");
            var findings = await Invariants.Check(ctx, area, new Expect { Tasks = count * ctx.Options.Ops, ContiguousNumbers = true }, name);
            r.Check(findings.Violations.Count == 0, $"{count} clients: invariants hold", findings.Violations.Count == 0 ? "hold" : string.Join("; ", findings.Violations));
        }

        ctx.Scenario = "scale";
        r.Ops = ctx.Recorder.Samples.Count(x => x.Scenario.StartsWith("scale-"));
        return r;
    }
}
