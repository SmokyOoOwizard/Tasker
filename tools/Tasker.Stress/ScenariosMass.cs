using System.Collections.Concurrent;
using System.Diagnostics;

namespace Tasker.Stress;

public static partial class Scenarios
{
    // ---------- 5. массовые операции параллельно с обычными ----------

    private static async Task<ScenarioResult> Mass(StressContext ctx)
    {
        var r = Begin(ctx, "mass", "series delete (cascade over all tasks) and cleanup run while other clients edit and create: no lost update, the cascade completes");
        var area = await ctx.Stand.NewProject(ctx.Stand.Main, "mass");
        var count = Math.Max(ctx.Options.Clients, 4);
        var clients = ctx.Clients(count, area);
        var editors = clients.Take(count - 3).ToArray();
        var deleter = clients[count - 3];
        var cleaner = clients[count - 2];
        var creator = clients[count - 1];

        // Каждому редактору — своя задача (все в серии: каскад затронет их все).
        var owned = new Guid[editors.Length];
        await Par(editors, async (c, i) => owned[i] = await CreateTask(c, $"mass {c.Name}"));

        var acked = new ConcurrentDictionary<int, ConcurrentBag<string>>();
        long retries = 0;
        var deleted = false;
        var created = 0;
        await Timed(r, async () =>
        {
            var tasks = new List<Task>();
            for (var e = 0; e < editors.Length; e++)
            {
                var index = e;
                tasks.Add(Task.Run(async () =>
                {
                    var c = editors[index];
                    var bag = acked.GetOrAdd(index, _ => []);
                    for (var m = 0; m < ctx.Options.Ops * 2; m++)
                    {
                        var token = $"{c.Name}#{m}";
                        for (var attempt = 0; attempt < 200; attempt++)
                        {
                            if (await c.Read(owned[index]) is not { } view)
                                break;
                            var op = await c.Update(owned[index], view.Version, description: (view.Description ?? "") + token + ";");
                            if (op.Ok)
                            {
                                bag.Add(token);
                                break;
                            }

                            if (!op.Modified)
                                break;
                            Interlocked.Increment(ref retries);
                        }
                    }
                }));
            }

            tasks.Add(Task.Run(async () =>
            {
                await Task.Delay(500);
                for (var attempt = 0; attempt < 10 && !deleted; attempt++)
                    deleted = (await deleter.DeleteSeries()).Ok;
            }));
            tasks.Add(Task.Run(async () =>
            {
                for (var m = 0; m < ctx.Options.Ops; m++)
                    await cleaner.Cleanup();
            }));
            tasks.Add(Task.Run(async () =>
            {
                for (var m = 0; m < ctx.Options.Ops * 2; m++)
                {
                    if (await CreateTask(creator, $"mass new {m}", series: false) != Guid.Empty)
                        Interlocked.Increment(ref created);
                }
            }));
            await Task.WhenAll(tasks);
            return 0;
        });

        r.RequireOutcomes(ctx.Recorder, Outcome.Ok, Outcome.Modified, Outcome.NotFound);
        r.Check(deleted, "the series delete (cascade over every task) completes while the tasks are being edited", deleted ? "ok" : "never succeeded");
        var lost = 0;
        var kept = 0;
        for (var e = 0; e < editors.Length; e++)
        {
            var view = await creator.Read(owned[e]);
            var tokens = (view?.Description ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
            lost += acked[e].Count(x => !tokens.Contains(x));
            kept += tokens.Count;
            if (view != null && view.Numbers.Length > 0 && deleted)
                r.Check(false, "no task keeps a number of the deleted series", $"{view.Title} still has {string.Join(",", view.Numbers)}");
        }

        r.Check(lost == 0, "no edit is lost between the cascade rewrite and the editors (every acknowledged token is in its task)", $"{lost} lost of {kept + lost}");
        r.Note($"modified answers retried by editors: {retries}");
        await Finish(ctx, r, area, new Expect { Tasks = editors.Length + created });
        return r;
    }

    // ---------- 5a. каскады полей и значений перечислений ----------

    private static async Task<ScenarioResult> Fields(StressContext ctx)
    {
        var r = Begin(ctx, "fields", "an enum value is reassigned and a field is dropped from the task type (cascades over every task) while clients edit those tasks: no lost update, no task keeps the removed value");
        var area = await ctx.Stand.NewProject(ctx.Stand.Main, "fields");
        var stand = ctx.Stand;
        var count = Math.Max(ctx.Options.Clients, 3);
        await stand.Must(area, true, "enum", "create", "Priority", "--value", "Low", "High", "Urgent");
        await stand.Must(area, true, "field", "create", "Priority", "--type", "enum", "--enum", "Priority");
        await stand.Must(area, true, "field", "create", "Note", "--type", "string");
        await stand.Must(area, true, "task-type", "update", "Bug", "--add-field", "Priority", "--add-field", "Note");

        var clients = ctx.Clients(count, area);
        var owned = new Guid[count];
        await Par(clients, async (c, i) =>
        {
            var created = await stand.Must(area, true, "task", "create", $"fields {c.Name}", "--type", area.TypeId.ToString(), "--field", "Priority=Low", "--field", "Note=initial", "--json");
            owned[i] = Guid.Parse(created.Json["id"]!.GetValue<string>());
        });

        var acked = new ConcurrentDictionary<int, ConcurrentBag<string>>();
        var cascade = new List<CliResult>();
        await Timed(r, async () =>
        {
            var work = new List<Task>();
            for (var e = 0; e < count; e++)
            {
                var index = e;
                work.Add(Task.Run(async () =>
                {
                    var c = clients[index];
                    var bag = acked.GetOrAdd(index, _ => []);
                    for (var m = 0; m < ctx.Options.Ops * 2; m++)
                    {
                        var token = $"{c.Name}#{m}";
                        for (var attempt = 0; attempt < 200; attempt++)
                        {
                            if (await c.Read(owned[index]) is not { } view)
                                break;
                            var op = await c.Update(owned[index], view.Version, description: (view.Description ?? "") + token + ";");
                            if (op.Ok)
                            {
                                bag.Add(token);
                                break;
                            }

                            if (!op.Modified)
                                break;
                        }
                    }
                }));
            }

            work.Add(Task.Run(async () =>
            {
                await Task.Delay(400);
                var reassign = await stand.Cli(area, true, "enum", "update", "Priority", "--remove-value", "Low", "--replace-with", "High");
                var drop = await stand.Cli(area, true, "task-type", "update", "Bug", "--remove-field", "Note", "--drop-values");
                lock (cascade)
                    cascade.AddRange([reassign, drop]);
            }));
            await Task.WhenAll(work);
            return 0;
        });

        r.RequireOutcomes(ctx.Recorder, Outcome.Ok, Outcome.Modified, Outcome.NotFound);
        r.Check(cascade.Count == 2 && cascade.All(x => x.Ok), "both cascades (reassign an enum value, drop a field with its values) complete while the tasks are being edited",
            string.Join(" | ", cascade.Select(x => x.Ok ? x.Out.Trim().Split('\n')[^1] : x.Text)));
        int Count(CliResult result) => result.Json["totalCount"]!.GetValue<int>();
        var low = Count(await stand.Must(area, true, "task", "list", "--field", "Priority!=High", "--json"));
        var high = Count(await stand.Must(area, true, "task", "list", "--field", "Priority=High", "--json"));
        var note = Count(await stand.Must(area, true, "task", "list", "--field", "Note:set", "--json"));
        r.Check(low == 0 && high == count && note == 0, "afterwards no task has the removed value or the dropped field's value, all have the replacement",
            $"Priority!=High: {low}, Priority=High: {high} of {count}, Note set: {note}");

        var lost = 0;
        var total = 0;
        for (var e = 0; e < count; e++)
        {
            var view = await clients[0].Read(owned[e]);
            var tokens = (view?.Description ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
            lost += acked[e].Count(x => !tokens.Contains(x));
            total += acked[e].Count;
        }

        r.Check(lost == 0, "no edit is lost between the cascades and the editors", $"{lost} lost of {total} acknowledged");
        await Finish(ctx, r, area, new Expect { Tasks = count });
        return r;
    }

    // ---------- 5b. связи в обе стороны ----------

    private static async Task<ScenarioResult> Links(StressContext ctx)
    {
        var r = Begin(ctx, "links", "links are stored on the source task: concurrent links from one task, from both sides, in both directions: nothing lost, both sides consistent");
        var area = await ctx.Stand.NewProject(ctx.Stand.Main, "links");
        var count = Math.Max(ctx.Options.Clients, 2);
        var clients = ctx.Clients(count, area);
        var pairs = Math.Max(ctx.Options.Ops, 3);

        var pairIds = new List<(Guid A, Guid B)>();
        for (var i = 0; i < pairs; i++)
            pairIds.Add((await CreateTask(clients[0], $"pair {i} a", series: false), await CreateTask(clients[0], $"pair {i} b", series: false)));

        var pairOps = new ConcurrentBag<Op[]>();
        await Timed(r, async () =>
        {
            // A) «A blocks B» и «B blocks A» одновременно (TSK-97): цикл запрещён, проверка и запись — под одной секцией записи проекта,
            // поэтому из двух встречных связей проходит ровно одна, а вторая получает отказ «Cycle: ...».
            await Task.WhenAll(pairIds.Select((p, i) => Task.Run(async () =>
            {
                var first = clients[i % count];
                var second = clients[(i + 1) % count];
                pairOps.Add(await Task.WhenAll(first.Link(p.A, "blocks", p.B), second.Link(p.B, "blocks", p.A)));
            })));
            return 0;
        });

        var cycles = 0;
        foreach (var (a, b) in pairIds)
        {
            var links = await clients[0].ReadLinks(a);
            if (links != null && links.Any(x => x.Type == "Blocks" && x.Direction == "outward" && x.Other == b) && links.Any(x => x.Type == "Blocks" && x.Direction == "inward" && x.Other == b))
                cycles++;
        }

        var notOne = pairOps.Count(x => x.Count(o => o.Ok) != 1 || x.Count(o => o.Outcome == Outcome.Invalid) != 1);
        r.Check(notOne == 0, "(TSK-97) parallel 'A blocks B' and 'B blocks A': exactly one succeeds, the other is refused with the cycle path",
            $"{notOne} of {pairs} pairs differ; {pairOps.Count(x => x.Any(o => o.Outcome == Outcome.Invalid && o.Text.Contains("→")))} refusals name the path");
        r.Check(cycles == 0, "(TSK-97) no pair ended up as a 2-cycle (both links stored)", $"{cycles} of {pairs} pairs have both links");

        // A2) треугольник A→B, B→C, C→A одновременно: проходят ровно две связи, замыкающая отклоняется.
        var triples = new List<(Guid A, Guid B, Guid C)>();
        for (var i = 0; i < pairs; i++)
            triples.Add((await CreateTask(clients[0], $"tri {i} a", series: false), await CreateTask(clients[0], $"tri {i} b", series: false), await CreateTask(clients[0], $"tri {i} c", series: false)));
        var triOps = new ConcurrentBag<Op[]>();
        await Timed(r, async () =>
        {
            await Task.WhenAll(triples.Select((t, i) => Task.Run(async () =>
                triOps.Add(await Task.WhenAll(
                    clients[i % count].Link(t.A, "blocks", t.B), clients[(i + 1) % count].Link(t.B, "blocks", t.C), clients[(i + 2) % count].Link(t.C, "blocks", t.A))))));
            return 0;
        });
        var badTriples = triOps.Count(x => x.Count(o => o.Ok) != 2 || x.Count(o => o.Outcome == Outcome.Invalid) != 1);
        r.Check(badTriples == 0, "(TSK-97) a triangle of three concurrent links: two succeed, the closing one is refused", $"{badTriples} of {pairs} triangles differ");

        // B) одна и та же связь с двух сторон одновременно: «A blocks B» и «B is blocked by A».
        var samePairs = new List<(Guid A, Guid B)>();
        for (var i = 0; i < pairs; i++)
            samePairs.Add((await CreateTask(clients[0], $"same {i} a", series: false), await CreateTask(clients[0], $"same {i} b", series: false)));
        await Timed(r, async () =>
        {
            await Task.WhenAll(samePairs.Select((p, i) => Task.Run(async () =>
                await Task.WhenAll(clients[i % count].Link(p.A, "blocks", p.B), clients[(i + 1) % count].Link(p.B, "is blocked by", p.A)))));
            return 0;
        });
        var duplicated = 0;
        foreach (var (a, b) in samePairs)
        {
            var links = await clients[0].ReadLinks(a) ?? [];
            if (links.Count(x => x.Other == b) != 1)
                duplicated++;
        }

        r.Check(duplicated == 0, "the same link added from both sides at once exists exactly once", $"{duplicated} of {pairs} pairs with 0 or 2 links");

        // C) много связей от одной задачи одновременно: ни одна не потеряна.
        var hub = await CreateTask(clients[0], "hub", series: false);
        var targets = new Guid[count][];
        await Par(clients, async (c, i) =>
        {
            targets[i] = new Guid[ctx.Options.Ops];
            for (var m = 0; m < ctx.Options.Ops; m++)
                targets[i][m] = await CreateTask(c, $"target {c.Name} {m}", series: false);
        });
        var linkOps = new ConcurrentBag<Op>();
        await Timed(r, async () =>
        {
            await Par(clients, async (c, i) =>
            {
                for (var m = 0; m < ctx.Options.Ops; m++)
                    linkOps.Add(await c.Link(hub, "relates to", targets[i][m]));
            });
            return 0;
        });
        var hubLinks = await clients[0].ReadLinks(hub) ?? [];
        var expected = count * ctx.Options.Ops;
        r.Check(linkOps.All(x => x.Ok), "links from ONE task by N clients with no version (commutative: retried inside): every call succeeds",
            $"{linkOps.Count(x => x.Ok)} ok, {linkOps.Count(x => x.Modified)} modified of {linkOps.Count}");
        r.Check(hubLinks.Length == linkOps.Count(x => x.Ok), "every acknowledged link exists (no lost link)", $"{hubLinks.Length} links, {linkOps.Count(x => x.Ok)} acknowledged of {expected}");

        // D) согласованность обратных ссылок: у каждой исходящей связи есть входящая на другой стороне и наоборот.
        var all = pairIds.SelectMany(p => new[] { p.A, p.B }).Concat(triples.SelectMany(t => new[] { t.A, t.B, t.C })).Concat(samePairs.SelectMany(p => new[] { p.A, p.B })).Append(hub).Concat(targets.SelectMany(x => x)).ToArray();
        var linksOf = new ConcurrentDictionary<Guid, LinkView[]>();
        await Parallel.ForEachAsync(all, new ParallelOptions { MaxDegreeOfParallelism = Math.Min(8, all.Length) }, async (id, _) =>
            linksOf[id] = await clients[(int)(Math.Abs(id.GetHashCode()) % count)].ReadLinks(id) ?? []);
        var broken = 0;
        foreach (var (id, links) in linksOf)
        {
            foreach (var l in links)
            {
                var mirror = linksOf.TryGetValue(l.Other, out var other) ? other : [];
                var want = l.Direction == "outward" ? "inward" : "outward";
                // Симметричная связь («relates to») показывается с двух сторон одинаково: проверяем наличие любой пары.
                if (!mirror.Any(x => x.Other == id && x.Type == l.Type && (x.Direction == want || x.Direction == l.Direction)))
                    broken++;
            }
        }

        r.Check(broken == 0, "both sides of every link agree (an outward link has its inward mirror and vice versa)", $"{broken} one-sided links among {linksOf.Values.Sum(x => x.Length)}");
        r.RequireOutcomes(ctx.Recorder, Outcome.Ok, Outcome.Modified, Outcome.Invalid);
        await Finish(ctx, r, area, new Expect { Tasks = pairs * 7 + 1 + count * ctx.Options.Ops });
        return r;
    }

    // ---------- 5c. удаление во время правки ----------

    private static async Task<ScenarioResult> DeleteWhileEdit(StressContext ctx)
    {
        var r = Begin(ctx, "delete-while-edit", "a task is deleted while another client is editing it: the delete wins for good, the editor gets modified/not found, nothing is resurrected");
        var area = await ctx.Stand.NewProject(ctx.Stand.Main, "delete-while-edit");
        var count = Math.Max(ctx.Options.Clients, 2);
        var clients = ctx.Clients(count, area);
        var rounds = ctx.Options.Ops;
        var resurrected = 0;
        var notGone = 0;
        var deletedCount = 0;

        await Timed(r, async () =>
        {
            var work = new List<Task>();
            for (var p = 0; p < count / 2; p++)
            {
                var editor = clients[2 * p];
                var deleter = clients[2 * p + 1];
                work.Add(Task.Run(async () =>
                {
                    var random = new Random(ctx.Options.Seed + p);
                    for (var round = 0; round < rounds; round++)
                    {
                        var id = await CreateTask(editor, $"victim {p}/{round}", series: false);
                        long deletedAt = long.MaxValue;
                        var okAfterDelete = 0;
                        var deleting = Task.Run(async () =>
                        {
                            await Task.Delay(random.Next(0, 600));
                            for (var attempt = 0; attempt < 30; attempt++)
                            {
                                var op = await deleter.Delete(id);
                                if (op.Ok)
                                {
                                    Volatile.Write(ref deletedAt, Stopwatch.GetTimestamp());
                                    Interlocked.Increment(ref deletedCount);
                                    return;
                                }

                                if (op.Outcome == Outcome.NotFound)
                                    return;
                            }
                        });
                        for (var m = 0; m < 12; m++)
                        {
                            var start = Stopwatch.GetTimestamp();
                            var view = await editor.Read(id);
                            if (view == null)
                                break;
                            var op = await editor.Update(id, view.Version, description: "edit " + m);
                            if (op.Ok && start > Volatile.Read(ref deletedAt))
                                okAfterDelete++;
                        }

                        await deleting;
                        resurrected += okAfterDelete;
                        if (await editor.Get(id) is { Ok: true })
                            Interlocked.Increment(ref notGone);
                    }
                }));
            }

            await Task.WhenAll(work);
            return 0;
        });

        r.RequireOutcomes(ctx.Recorder, Outcome.Ok, Outcome.Modified, Outcome.NotFound);
        r.Check(resurrected == 0, "no update succeeds after its task was deleted (nothing is resurrected)", $"{resurrected} updates after the delete");
        r.Check(notGone == 0, "after the delete returned ok the task is really gone", $"{notGone} deleted tasks still readable");
        r.Note($"deleted {deletedCount} tasks; {Describe(ctx.Recorder.Samples.Where(x => x.Scenario == r.Name && x.Op == "update"))}");
        await Finish(ctx, r, area, new Expect { Tasks = (count / 2) * rounds - deletedCount });
        return r;
    }

    // ---------- 5d. удаление проекта во время работы ----------

    private static async Task<ScenarioResult> DeleteProject(StressContext ctx)
    {
        var r = Begin(ctx, "delete-project", "a project is deleted while clients create and edit tasks in it: no hang, no half-deleted project, no folder resurrected by a late writer");
        var area = await ctx.Stand.NewProject(ctx.Stand.Main, "doomed");
        var count = Math.Max(ctx.Options.Clients, 3);
        var clients = ctx.Clients(count, area);
        var shared = await CreateTask(clients[0], "seed");

        var deleted = new CliResult(-1, "", "not run");
        await Timed(r, async () =>
        {
            var stop = false;
            var work = clients.Select((c, i) => Task.Run(async () =>
            {
                // Пишут до тех пор, пока удаление не завершится, и ещё немного после: поздние писатели — как раз то, что проверяется.
                var after = 0;
                for (var m = 0; m < 5000 && after < 6; m++)
                {
                    if (Volatile.Read(ref stop))
                        after++;
                    if (i % 2 == 0)
                        await c.Create($"late {c.Name} {m}", null, false);
                    else if (await c.Read(shared) is { } view)
                        await c.Update(shared, view.Version, description: $"{c.Name} {m}");
                }
            })).ToList();
            work.Add(Task.Run(async () =>
            {
                await Task.Delay(1500);
                deleted = await ctx.Stand.Cli(area, false, "project", "delete", area.ProjectId.ToString(), "--yes");
                Volatile.Write(ref stop, true);
            }));
            await Task.WhenAll(work);
            return 0;
        });

        r.Check(deleted.Ok, "the project delete completes while clients are writing", deleted.Ok ? "ok" : deleted.Text);
        var projects = await ctx.Stand.Must(ctx.Stand.Main, false, "project", "list", "--json");
        var stillListed = projects.Json["data"]!.AsArray().Any(x => x!["id"]!.GetValue<string>() == area.ProjectId.ToString());
        r.Check(!stillListed, "the deleted project is not listed", stillListed ? "still listed" : "gone");
        if (!area.IsSqlite)
        {
            var folder = Path.Combine(area.TaskerDir, "projects", area.ProjectId.ToString());
            var left = Directory.Exists(folder) ? Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Select(Path.GetFileName).ToArray() : [];
            r.Check(left.Length == 0, "no file of the deleted project is left on disk (a late writer does not resurrect it)", left.Length == 0 ? "gone" : $"{left.Length} files: {string.Join(", ", left.Take(5))}");
        }

        var sync = await ctx.Stand.Cli(ctx.Stand.Main, false, "sync", "-q");
        r.Check(sync.Ok && sync.Out.Trim().Length == 0, "sync is clean afterwards", sync.Ok ? "clean" : sync.Text);
        var unexpected = ctx.Recorder.Samples.Where(x => x.Scenario == r.Name && x.Outcome == Outcome.Error).ToArray();
        r.Note($"{Describe(ctx.Recorder.Samples.Where(x => x.Scenario == r.Name))}");
        foreach (var group in unexpected.GroupBy(x => $"{x.Op}: {x.Detail}").Take(5))
            r.Note($"errors after the delete ({group.Count()}x) {group.Key}");
        r.Ops = ctx.Recorder.Samples.Count(x => x.Scenario == r.Name);
        return r;
    }

    // ---------- 8. падения: kill -9 посреди записи ----------

    private static async Task<ScenarioResult> Kill(StressContext ctx)
    {
        var r = Begin(ctx, "kill", "console processes killed with SIGKILL in the middle of create/update (and the daemon in the middle of MCP calls): no half-written file, the index recovers, no stuck lock");
        var area = await ctx.Stand.NewProject(ctx.Stand.Main, "kill");
        var count = Math.Max(ctx.Options.Clients, 2);
        var rounds = Math.Max(ctx.Options.Ops, 3) * 2;
        var survived = 0;
        var killed = 0;

        var shared = await CreateTask(new CliClient("setup", ctx, area), "shared", series: false);

        await Timed(r, async () =>
        {
            var random = new Random(ctx.Options.Seed);
            await Task.WhenAll(Enumerable.Range(0, count).Select(w => Task.Run(async () =>
            {
                for (var round = 0; round < rounds; round++)
                {
                    var args = (w + round) % 3 == 0
                        ? new[] { "task", "update", shared.ToString(), "-d", $"killer {w}/{round}" }
                        : ["task", "create", $"victim {w}/{round}", "--type", area.TypeId.ToString(), "--series", area.Prefix];
                    using var process = Process.Start(ctx.Stand.Info(Stand.CliArgs(area, true, args)))!;
                    _ = process.StandardOutput.ReadToEndAsync();
                    _ = process.StandardError.ReadToEndAsync();
                    int delay;
                    lock (random)
                        delay = random.Next(120, 1600);
                    var exited = await Task.WhenAny(process.WaitForExitAsync(), Task.Delay(delay));
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        Interlocked.Increment(ref killed);
                    }
                    else
                        Interlocked.Increment(ref survived);
                    await process.WaitForExitAsync();
                }
            })));
            return 0;
        });

        r.Note($"{killed} processes killed in the middle of a call, {survived} finished before the kill");
        r.Check(killed > 0, "the stand really kills processes in the middle of a call", $"{killed} killed");
        r.Ops = killed + survived;

        // Демон: клиенты MCP пишут без остановки, демон убивается (SIGKILL) несколько раз подряд и запускается заново.
        if (ctx.Stand.DaemonRunning)
        {
            var mcp = new List<StressClient>();
            for (var i = 0; i < count; i++)
                mcp.Add(Agent(ctx, area, i));
            using var stop = new CancellationTokenSource();
            ctx.ExpectDown = true;
            var load = Task.WhenAll(mcp.Select(c => Task.Run(async () =>
            {
                for (var m = 0; m < 100000 && !stop.IsCancellationRequested; m++)
                {
                    var op = await c.Create($"daemon victim {c.Name} {m}", series: m % 2 == 0);
                    if (!op.Ok)
                        await Task.Delay(50);
                }
            })));
            var restarts = 5;
            for (var round = 0; round < restarts; round++)
            {
                await Task.Delay(1200);
                ctx.Stand.KillDaemon();
                await Task.Delay(300);
                await ctx.Stand.StartDaemon();
            }

            await Task.Delay(1000);
            stop.Cancel();
            await load;
            ctx.ExpectDown = false;
            var again = await mcp[0].Create("after the daemon restarts", null, false);
            r.Check(again.Ok, $"after {restarts} kill -9 of the daemon in the middle of writes it starts again and serves MCP", again.Ok ? "ok" : again.Text);
            var down = ctx.Recorder.Samples.Count(x => x.Scenario == r.Name && x.Outcome == Outcome.Down);
            var done = ctx.Recorder.Samples.Count(x => x.Scenario == r.Name && x.Kind == "mcp" && x.Op == "create" && x.Outcome == Outcome.Ok);
            r.Note($"{done} MCP creates done across {restarts} kills of the daemon; {down} calls failed while it was dead (expected)");
            r.RequireOutcomes(ctx.Recorder, Outcome.Ok, Outcome.Modified, Outcome.NotFound, Outcome.Down);
        }

        // Kill между записью временного файла и переименованием оставляет рядом с файлом обрезанный «.tmp»: он не должен мешать.
        if (!area.IsSqlite)
        {
            var file = Directory.GetFiles(area.TasksDir, "*.yaml").First();
            await File.WriteAllTextAsync(file + ".tmp", "formatVersion: 6\nid: trunc");
            var ghost = Path.Combine(area.TasksDir, "ghost-deadbeef.yaml.tmp");
            await File.WriteAllTextAsync(ghost, "formatVersion: 6\nid: deadbeef-0000-0000-0000-000000000000\ntitle: ghost");
            var sync = await ctx.Stand.Cli(area, true, "sync", "-q");
            var listed = JsonCount(await ctx.Stand.Must(area, true, "task", "list", "--all", "--json"));
            var before = Directory.GetFiles(area.TasksDir, "*.yaml").Length;
            r.Check(sync.Ok && sync.Out.Trim().Length == 0 && listed == before, "a truncated .tmp left by a killed writer is ignored: sync is clean, the listing equals the files",
                $"sync exit {sync.Code}, {listed} listed, {before} files");
            var fileId = System.Text.RegularExpressions.Regex.Match(await File.ReadAllTextAsync(file), @"^id: (\S+)", System.Text.RegularExpressions.RegexOptions.Multiline).Groups[1].Value;
            var updated = await ctx.Stand.Cli(area, true, "task", "update", fileId, "-d", "after the stale tmp");
            r.Check(updated.Ok && !File.Exists(file + ".tmp"), "the next write to that file succeeds and replaces the stale .tmp", updated.Ok ? "ok" : updated.Text);
            File.Delete(ghost);
        }

        await Finish(ctx, r, area, new Expect());
        return r;
    }

    private static int JsonCount(CliResult list) => list.Json["totalCount"]!.GetValue<int>();

    // ---------- 6. несколько областей через один демон ----------

    private static async Task<ScenarioResult> MultiWorkspace(StressContext ctx)
    {
        var r = Begin(ctx, "multiws", "agents work in two workspaces at once through one daemon (workspace argument) while the console works in the first: nothing leaks between workspaces");
        if (!ctx.Stand.DaemonRunning)
        {
            r.Check(false, "the stand daemon is up", "not started");
            return r;
        }

        var first = await ctx.Stand.NewProject(ctx.Stand.Main, "multiws");
        var second = await ctx.Stand.CreateArea("ws2");
        await ctx.Stand.CreateAgents(second);
        await ctx.Stand.AddAreaToDaemon(second);
        var secondKey = second.Key;
        var count = ctx.Options.Clients;

        var clients = new List<StressClient>();
        for (var i = 0; i < count; i++)
        {
            var area = i % 2 == 0 ? first : second;
            clients.Add(i % 3 == 2 ? new CliClient($"cli-{i}", ctx, area) : Agent(ctx, area, i));
        }

        await Timed(r, async () =>
        {
            await Par(clients, async (c, i) =>
            {
                for (var m = 0; m < ctx.Options.Ops; m++)
                    await c.Create($"{c.Area.Name} {c.Name} #{m}");
            });
            return 0;
        });

        r.RequireOutcomes(ctx.Recorder, Outcome.Ok);
        var perArea = clients.GroupBy(c => c.Area.Path).ToDictionary(g => g.Key, g => g.Count() * ctx.Options.Ops);
        foreach (var area in new[] { first, second })
        {
            var list = await ctx.Stand.Cli(area, true, "task", "list", "--all", "--json");
            var titles = list.Json["data"]!.AsArray().Select(x => x!["title"]!.GetValue<string>()).ToArray();
            r.Check(titles.Length == perArea[area.Path] && titles.All(t => t.StartsWith(area.Name + " ")),
                $"workspace {area.Name}: exactly its own {perArea[area.Path]} tasks, none from the other workspace", $"{titles.Length} tasks, {titles.Count(t => !t.StartsWith(area.Name + " "))} foreign");
        }

        await Finish(ctx, r, first, new Expect { Tasks = perArea[first.Path], ContiguousNumbers = true });
        var findings = await Invariants.Check(ctx, second, new Expect { Tasks = perArea[second.Path], ContiguousNumbers = true }, "multiws-2");
        r.Facts.Add(new Fact("second workspace invariants", findings.Violations.Count == 0 ? "hold" : $"{findings.Violations.Count} violation(s)", findings.Violations.Count == 0));
        r.Findings.Merge(findings);
        return r;
    }
}
