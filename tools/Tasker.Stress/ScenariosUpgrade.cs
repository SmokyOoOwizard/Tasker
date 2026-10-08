using System.Text.Json.Nodes;

namespace Tasker.Stress;

public static partial class Scenarios
{
    // ---------- upgrade: замена демона на лету под нагрузкой (TSK-103) ----------

    private static async Task<ScenarioResult> UpgradeUnderLoad(StressContext ctx)
    {
        var r = Begin(ctx, "upgrade", "MCP agents create and edit tasks while the daemon is replaced on the fly (tasker mcp upgrade) several times: no failed call, no lost or duplicated write");
        var area = await ctx.Stand.NewProject(ctx.Stand.Main, "upgrade");
        var agents = Enumerable.Range(0, ctx.Options.Clients).Select(i => Agent(ctx, area, i)).ToArray();

        async Task<(int Pid, int[] Workers)> Daemon()
        {
            var status = (await ctx.Stand.Must(null, false, "mcp", "status", "--json")).Json["daemon"]!;
            return (status["pid"]!.GetValue<int>(), status["workers"]!.AsArray().Select(x => x!["pid"]!.GetValue<int>()).ToArray());
        }

        var before = await Daemon();
        r.Check(before.Workers.Length == 1, "the daemon runs under a supervisor with one worker", $"{before.Workers.Length} worker(s)");

        using var stop = new CancellationTokenSource();
        var created = 0;
        var load = Par(agents, async (c, i) =>
        {
            for (var n = 0; !stop.IsCancellationRequested; n++)
            {
                var create = await c.Create($"upgrade {c.Name} #{n}", "first");
                if (!create.Ok)
                    continue;
                Interlocked.Increment(ref created);
                if (await c.Read(TaskView.Of(create.Json!).Id) is { } view)
                    await c.Update(view.Id, view.Version, description: "first\nsecond");
            }
        });

        var upgrades = Math.Max(2, ctx.Options.Ops / 2);
        var replaced = new List<int>();
        await Timed(r, async () =>
        {
            await Task.Delay(1000);
            for (var k = 0; k < upgrades; k++)
            {
                var result = await ctx.Stand.Cli(null, false, "mcp", "upgrade", "--json");
                r.Check(result.Ok, $"upgrade #{k + 1} succeeds", result.Ok ? "ok" : result.Text);
                replaced.Add((await Daemon()).Workers.FirstOrDefault());
                await Task.Delay(700);
            }

            stop.Cancel();
            await load;
            return 0;
        });

        var after = await Daemon();
        r.Check(after.Pid == before.Pid, "the daemon (supervisor) process and port stay the same", $"{before.Pid} -> {after.Pid}");
        r.Check(after.Workers.Length == 1 && !before.Workers.Contains(after.Workers[0]) && replaced.Distinct().Count() == replaced.Count,
            $"the worker is replaced on every upgrade ({upgrades}x) and one remains", $"workers {string.Join(",", after.Workers)}");
        r.Check(created > 0, "the agents did work during the replacements", $"{created} tasks created");

        // Ни один вызов не отказал (без повторов на стороне клиента), число задач = числу подтверждённых записей.
        r.RequireOutcomes(ctx.Recorder, Outcome.Ok);
        await Finish(ctx, r, area, new Expect { Tasks = created, ContiguousNumbers = true });
        return r;
    }
}
