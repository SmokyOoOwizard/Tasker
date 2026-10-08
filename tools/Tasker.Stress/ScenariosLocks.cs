using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Tasker.Stress;

public static partial class Scenarios
{
    // ---------- 4. блокировка на время правки ----------

    private static async Task<ScenarioResult> Locks(StressContext ctx)
    {
        var r = Begin(ctx, "locks", "edit lock between different holders (agent A, agent B, console): refusal, renewal, instant release, exactly one winner of a race");
        r.Setup = $"{ctx.Options.Clients} racers x {ctx.Options.Ops} rounds, MCP agents + console, {ctx.Options.Storage}";
        if (!ctx.Stand.DaemonRunning)
        {
            r.Check(false, "the stand daemon is up", "not started");
            return r;
        }

        var area = await ctx.Stand.NewProject(ctx.Stand.Main, "locks");
        var a = Agent(ctx, area, 0);
        var b = Agent(ctx, area, 1);
        var cli = new CliClient("console", ctx, area);
        var id = await CreateTask(cli, "locked task");

        await Timed(r, async () =>
        {
            // a) один держатель — остальные получают отказ.
            var taken = await a.Lock(id);
            r.Check(taken.Ok, "agent A takes the lock of a free task", taken.Outcome.ToString());
            var expires = taken.Json?["expiresAt"]?.GetValue<DateTimeOffset>();
            var left = expires - DateTimeOffset.UtcNow;
            r.Check(left is { } l && l > TimeSpan.FromSeconds(90) && l <= TimeSpan.FromSeconds(125), "the lock lasts 2 minutes", left is { } x ? $"{x.TotalSeconds:F0} s" : "no expiresAt");
            r.Check((await b.Lock(id)).Outcome == Outcome.Locked, "agent B cannot take A's lock", "locked");
            r.Check((await b.Update(id, null, title: "by B")).Outcome == Outcome.Locked, "agent B cannot change the locked task", "locked");
            r.Check((await b.Delete(id)).Outcome == Outcome.Locked, "agent B cannot delete the locked task", "locked");
            r.Check((await cli.Update(id, null, title: "by console")).Outcome == Outcome.Locked, "the console cannot change the task locked by an agent", "locked");
            r.Check((await cli.Delete(id)).Outcome == Outcome.Locked, "the console cannot delete the task locked by an agent", "locked");
            r.Check((await cli.Link(id, "blocks", await CreateTask(cli, "other"))).Outcome == Outcome.Locked, "the console cannot link FROM the locked task", "locked");

            // б) владелец правит, продлевает, показывает.
            r.Check((await a.Update(id, null, title: "by A")).Ok, "the holder changes its own locked task", "ok");
            var renewed = await a.Lock(id);
            var newExpires = renewed.Json?["expiresAt"]?.GetValue<DateTimeOffset>();
            r.Check(renewed.Ok && newExpires >= expires, "the holder renews the lock (heartbeat)", renewed.Outcome.ToString());
            var shownA = await a.LockShow(id);
            var shownB = await b.LockShow(id);
            r.Check(shownA.Json?["lock"]?["mine"]?.GetValue<bool>() == true && shownB.Json?["lock"]?["mine"]?.GetValue<bool>() == false,
                "get_lock tells whose lock it is (mine for A, not mine for B)", $"A: {shownA.Json?["lock"]?["mine"]}, B: {shownB.Json?["lock"]?["mine"]}");
            // B не может снять чужую блокировку, и это не ошибка.
            await b.Unlock(id);
            r.Check((await b.Update(id, null, title: "by B again")).Outcome == Outcome.Locked, "an unlock by a stranger does not release the lock", "still locked");

            // в) мгновенное освобождение.
            var watch = Stopwatch.StartNew();
            r.Check((await a.Unlock(id)).Ok, "the holder releases the lock", "ok");
            var afterRelease = await b.Update(id, null, title: "by B after release");
            r.Check(afterRelease.Ok, "right after the release another holder changes the task (no waiting for expiry)", $"{afterRelease.Outcome} in {watch.ElapsedMilliseconds} ms");
            r.Check((await a.LockShow(id)).Json?["lock"] == null, "get_lock after the release: nobody", "null");

            // г) консоль держит — агент получает отказ.
            r.Check((await cli.Lock(id)).Ok, "the console takes the lock", "ok");
            r.Check((await a.Update(id, null, title: "by A")).Outcome == Outcome.Locked, "an agent cannot change the task locked by the console", "locked");
            r.Check((await cli.Update(id, null, title: "by console")).Ok, "the console changes its own locked task", "ok");
            await cli.Unlock(id);
            r.Check((await a.Update(id, null, title: "by A")).Ok, "after the console releases, the agent changes the task", "ok");

            // д) гонка за блокировку: ровно один победитель.
            var racers = new List<StressClient>();
            for (var i = 0; i < ctx.Options.Clients; i++)
                racers.Add(i == 0 ? cli : Agent(ctx, area, i));
            var tooManyWinners = 0;
            var noWinner = 0;
            for (var round = 0; round < ctx.Options.Ops; round++)
            {
                var raceId = await CreateTask(cli, $"race {round}", series: false);
                var results = new Op[racers.Count];
                await Par(racers, async (c, i) => results[i] = await c.Lock(raceId));
                var winners = results.Count(x => x.Ok);
                var others = results.Count(x => x.Outcome == Outcome.Locked);
                if (winners > 1)
                    tooManyWinners++;
                if (winners == 0 || winners + others != racers.Count)
                    noWinner++;
                // Победитель снимает блокировку: следующий раунд — с другой задачей, но чистим за собой.
                for (var i = 0; i < racers.Count; i++)
                {
                    if (results[i].Ok)
                        await racers[i].Unlock(raceId);
                }
            }

            r.Check(tooManyWinners == 0, "a race of N holders for one lock: never two winners", $"{tooManyWinners} of {ctx.Options.Ops} rounds with several winners");
            r.Check(noWinner == 0, "a race: exactly one winner, everyone else gets locked", $"{noWinner} of {ctx.Options.Ops} rounds without a clean result");

            // е) настоящее истечение (долго).
            if (ctx.Options.Long)
            {
                var expiring = await CreateTask(cli, "expiring");
                await a.Lock(expiring);
                await Task.Delay(TimeSpan.FromSeconds(125));
                var after = await b.Update(expiring, null, title: "after expiry");
                r.Check(after.Ok, "a forgotten lock expires by itself after 2 minutes", after.Outcome.ToString());
            }

            return 0;
        });

        r.RequireOutcomes(ctx.Recorder, Outcome.Ok, Outcome.Locked);
        await Finish(ctx, r, area, new Expect());
        return r;
    }

    // ---------- 4b. ожидание блокировок в массовых правках, взаимные блокировки ----------

    private static async Task<ScenarioResult> CascadeWait(StressContext ctx)
    {
        var r = Begin(ctx, "cascade-wait", "mass edits (series delete, cleanup) wait for a lock of an affected task, do not deadlock with each other, refuse atomically");
        r.Setup = $"5 tasks, MCP agent + console, {ctx.Options.Storage}";
        if (!ctx.Stand.DaemonRunning)
        {
            r.Check(false, "the stand daemon is up", "not started");
            return r;
        }

        var area = await ctx.Stand.NewProject(ctx.Stand.Main, "cascade");
        var holder = Agent(ctx, area, 0);
        var cli = new CliClient("console", ctx, area);
        var cli2 = new CliClient("console-2", ctx, area);

        // Вторая и третья серия: задачи входят во все три, чтобы каскады двух серий пересекались.
        string[] prefixes = ["CW1", "CW2"];
        foreach (var prefix in prefixes)
            await ctx.Stand.Must(area, true, "series", "create", prefix, "--prefix", prefix);
        var ids = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            var created = await ctx.Stand.Must(area, true, "task", "create", $"cascade {i}", "--type", area.TypeId.ToString(), "--series", area.Prefix,
                "--series", "CW1", "--series", "CW2", "--json");
            ids.Add(Guid.Parse(created.Json["id"]!.GetValue<string>()));
        }

        await Timed(r, async () =>
        {
            // а) удаление серии ждёт, пока другой держит затрагиваемую задачу, и проходит после снятия.
            Check((await holder.Lock(ids[2])).Ok, r, "an agent locks a task of the series");
            var watch = Stopwatch.StartNew();
            var deleting = Task.Run(() => ctx.Stand.Cli(area, true, "series", "delete", area.Prefix));
            var concurrent1 = Task.Run(() => ctx.Stand.Cli(area, true, "series", "delete", "CW1"));
            var concurrent2 = Task.Run(() => ctx.Stand.Cli(area, true, "series", "delete", "CW2"));
            await Task.Delay(3000);
            r.Check(!deleting.IsCompleted && !concurrent1.IsCompleted && !concurrent2.IsCompleted,
                "three mass edits over one locked task wait (do not fail at once, do not change anything)", $"completed after 3 s: {(deleting.IsCompleted ? 1 : 0) + (concurrent1.IsCompleted ? 1 : 0) + (concurrent2.IsCompleted ? 1 : 0)}");
            var untouched = await holder.Read(ids[0]);
            r.Check(untouched is { Numbers.Length: 3 }, "while waiting, no task is changed half-way (refusal/wait is atomic)", $"numbers of an unlocked task: {untouched?.Numbers.Length}");

            await holder.Unlock(ids[2]);
            var finished = await Task.WhenAll(deleting, concurrent1, concurrent2).WaitAsync(TimeSpan.FromSeconds(60));
            r.Check(finished[0].Code == 0 && finished[1].Code == 0 && finished[2].Code == 0,
                "after the release all mass edits complete (no deadlock between overlapping cascades)",
                $"{watch.Elapsed.TotalSeconds:F1} s; exit {finished[0].Code}/{finished[1].Code}/{finished[2].Code} {finished[0].Err.Trim()} {finished[1].Err.Trim()} {finished[2].Err.Trim()}");

            var numbersLeft = 0;
            foreach (var id in ids)
                numbersLeft += (await cli2.Read(id))?.Numbers.Length ?? 99;
            r.Check(numbersLeft == 0, "all three series are gone from all 5 tasks", $"{numbersLeft} numbers left");

            // б) ожидание конечно: чужая блокировка не снимается — отказ после CascadeWait, ничего не изменено (долго).
            if (ctx.Options.Long)
            {
                await ctx.Stand.Must(area, true, "series", "create", "Other", "--prefix", "OTH");
                var others = new List<Guid>();
                for (var i = 0; i < 3; i++)
                {
                    var created = await ctx.Stand.Must(area, true, "task", "create", $"oth {i}", "--type", area.TypeId.ToString(), "--series", "OTH", "--json");
                    others.Add(Guid.Parse(created.Json["id"]!.GetValue<string>()));
                }

                await holder.Lock(others[1]);
                var started = Stopwatch.StartNew();
                var refused = await ctx.Stand.Cli(area, true, "series", "delete", "OTH");
                r.Check(refused.Code != 0 && refused.Err.StartsWith("Locked:") && started.Elapsed > TimeSpan.FromSeconds(25) && started.Elapsed < TimeSpan.FromSeconds(45),
                    "a lock that is never released: the mass edit gives up with Locked after about 30 s", $"exit {refused.Code} after {started.Elapsed.TotalSeconds:F1} s: {refused.Err.Trim()}");
                var kept = 0;
                foreach (var id in others)
                    kept += (await cli2.Read(id))?.Numbers.Length == 1 ? 1 : 0;
                r.Check(kept == 3, "the refused cascade changed nothing", $"{kept}/3 tasks keep their number");
                await holder.Unlock(others[1]);
            }

            return 0;
        });

        // Блокировки правки сняты, массовые правки отработали.
        r.RequireOutcomes(ctx.Recorder, Outcome.Ok, Outcome.Locked);
        await Finish(ctx, r, area, new Expect { Tasks = 5 + (ctx.Options.Long ? 3 : 0) });
        return r;

        void Check(bool ok, ScenarioResult result, string what) => result.Check(ok, what, ok ? "ok" : "failed");
    }
}
