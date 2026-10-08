using System.CommandLine;
using Tasker.Cli.Completion;
using Tasker.Core.Statuses;

namespace Tasker.Cli.Commands;

internal static class StatusCommands
{
    private static async Task<Status> Find(Context ctx, Guid projectId, string reference) =>
        Refs.FindItem(await ctx.Get<IStatusStorage>().GetAll(projectId, ctx.Ct), reference, x => x.Id, x => x.Name, "status");

    private static async Task<StatusSet> FindSet(Context ctx, Guid projectId, string reference) =>
        Refs.FindItem(await ctx.Get<IStatusSetStorage>().GetAll(projectId, ctx.Ct), reference, x => x.Id, x => x.Name, "status set");

    public static Command Status(GlobalOptions g)
    {
        var name = Kit.Name("name", "Status name");
        var color = new Option<string>("--color") { Description = "Color in #RRGGBB format", DefaultValueFactory = _ => "#808080" };
        var reference = Kit.Ref("Status");
        var newName = new Option<string?>("--name") { Description = "New name" };
        var newColor = new Option<string?>("--color") { Description = "New color in #RRGGBB format" };
        var description = Kit.NewDescription("status");
        var newDescription = Kit.ChangedDescription();
        var expected = Kit.ExpectedVersion();

        reference.Suggests(g, Sources.Statuses);
        
        return Kit.Group("status", "Statuses of a project (Todo, Done, ...)",
            Kit.Leaf(g, "create", "Creates a status", c =>
            {
                c.Arguments.Add(name);
                c.Options.Add(color);
                c.Options.Add(description);
            }, async (parse, ctx) =>
            {
                var status = await ctx.Get<StatusService>().Create(
                    await ctx.ProjectId(), new CreateStatus(parse.GetRequiredValue(name), parse.GetRequiredValue(color), parse.GetValue(description)), ctx.Ct);
                ctx.Print(status, $"Created status '{status.Name}' {status.Id}");
            }),
            Kit.ListWithDescriptions(g, "Lists statuses", "status",
                async (ctx, page, length) => await ctx.Get<StatusService>().List(await ctx.ProjectId(), page, length, ctx.Ct),
                x => Kit.Row(x.Id, x.Name, x.Color)),
            Kit.Leaf(g, "get", "Shows a status", c => c.Arguments.Add(reference), async (parse, ctx) =>
            {
                var status = await Find(ctx, await ctx.ProjectId(), parse.GetRequiredValue(reference));
                ctx.Print(status, Kit.WithDescription(
                    Kit.Fields(("id", status.Id), ("name", status.Name), ("color", status.Color), ("version", status.Version)), status.Description));
            }),
            Kit.Leaf(g, "update", "Changes a status", c =>
            {
                c.Arguments.Add(reference);
                c.Options.Add(newName);
                c.Options.Add(newColor);
                c.Options.Add(newDescription);
                c.Options.Add(expected);
            }, async (parse, ctx) =>
            {
                Kit.RequireChange(parse, newName, newColor, newDescription);
                var projectId = await ctx.ProjectId();
                var status = await Find(ctx, projectId, parse.GetRequiredValue(reference));
                var updated = await ctx.Get<StatusService>().Update(projectId, status.Id,
                    new UpdateStatus(parse.GetValue(newName), parse.GetValue(newColor), parse.GetValue(expected) ?? status.Version, parse.GetValue(newDescription)), ctx.Ct)
                    ?? throw new CliException($"No status '{status.Id}'");
                ctx.Print(updated, $"Updated status '{updated.Name}' {updated.Id}");
            }),
            Kit.Leaf(g, "delete", "Deletes a status (not used by sets, boards or tasks)", c =>
            {
                c.Arguments.Add(reference);
                c.Options.Add(expected);
            }, async (parse, ctx) =>
            {
                var projectId = await ctx.ProjectId();
                var status = await Find(ctx, projectId, parse.GetRequiredValue(reference));
                await ctx.Get<StatusService>().Delete(projectId, status.Id, parse.GetValue(expected) ?? status.Version, ctx.Ct);
                ctx.Print(new { deleted = status.Id }, Kit.Deleted("status", status.Name, status.Id));
            }));
    }

    public static Command StatusSet(GlobalOptions g)
    {
        var name = Kit.Name("name", "Status set name");
        var statuses = Kit.Many("--status", "Statuses of the set in order (id or name); repeat or list several", required: true);
        var reference = Kit.Ref("Status set");
        var newName = new Option<string?>("--name") { Description = "New name" };
        var newStatuses = Kit.Many("--status", "New statuses of the set in order (id or name); replaces the current ones");
        var expected = Kit.ExpectedVersion();

        reference.Suggests(g, Sources.StatusSets);
        statuses.Suggests(g, Sources.Statuses);
        newStatuses.Suggests(g, Sources.Statuses);
        
        return Kit.Group("status-set", "Status sets: the ordered statuses a task type goes through",
            Kit.Leaf(g, "create", "Creates a status set", c =>
            {
                c.Arguments.Add(name);
                c.Options.Add(statuses);
            }, async (parse, ctx) =>
            {
                var projectId = await ctx.ProjectId();
                var all = await ctx.Get<IStatusStorage>().GetAll(projectId, ctx.Ct);
                var ids = Refs.FindAll(all, parse.GetRequiredValue(statuses), x => x.Id, x => x.Name, "status");

                var set = await ctx.Get<StatusSetService>().Create(projectId, new CreateStatusSet(parse.GetRequiredValue(name), ids), ctx.Ct);
                ctx.Print(set, $"Created status set '{set.Name}' {set.Id}");
            }),
            Kit.List(g, "Lists status sets",
                async (ctx, page) => await ctx.Get<IStatusSetStorage>().GetRange(await ctx.ProjectId(), page, ctx.Ct),
                x => Kit.Row(x.Id, x.Name, $"({x.StatusIds.Length} statuses)")),
            Kit.Leaf(g, "get", "Shows a status set", c => c.Arguments.Add(reference), async (parse, ctx) =>
            {
                var projectId = await ctx.ProjectId();
                var set = await FindSet(ctx, projectId, parse.GetRequiredValue(reference));
                var all = await ctx.Get<IStatusStorage>().GetAll(projectId, ctx.Ct);
                var list = string.Join("\n", set.StatusIds.Select(x => "  " + Kit.Named(all, x, s => s.Id, s => s.Name)));
                ctx.Print(set, Kit.Fields(("id", set.Id), ("name", set.Name), ("version", set.Version)) + "\nstatuses:\n" + list);
            }),
            Kit.Leaf(g, "update", "Changes a status set", c =>
            {
                c.Arguments.Add(reference);
                c.Options.Add(newName);
                c.Options.Add(newStatuses);
                c.Options.Add(expected);
            }, async (parse, ctx) =>
            {
                Kit.RequireChange(parse, newName, newStatuses);
                var projectId = await ctx.ProjectId();
                var set = await FindSet(ctx, projectId, parse.GetRequiredValue(reference));

                Guid[]? ids = null;
                if (Kit.Values(parse, newStatuses) is { } refs)
                    ids = Refs.FindAll(await ctx.Get<IStatusStorage>().GetAll(projectId, ctx.Ct), refs, x => x.Id, x => x.Name, "status");

                var updated = await ctx.Get<StatusSetService>().Update(projectId, set.Id,
                    new UpdateStatusSet(parse.GetValue(newName), ids, parse.GetValue(expected) ?? set.Version), ctx.Ct)
                    ?? throw new CliException($"No status set '{set.Id}'");
                ctx.Print(updated, $"Updated status set '{updated.Name}' {updated.Id}");
            }),
            Kit.Leaf(g, "delete", "Deletes a status set (not used by task types or boards)", c =>
            {
                c.Arguments.Add(reference);
                c.Options.Add(expected);
            }, async (parse, ctx) =>
            {
                var projectId = await ctx.ProjectId();
                var set = await FindSet(ctx, projectId, parse.GetRequiredValue(reference));
                await ctx.Get<StatusSetService>().Delete(projectId, set.Id, parse.GetValue(expected) ?? set.Version, ctx.Ct);
                ctx.Print(new { deleted = set.Id }, Kit.Deleted("status set", set.Name, set.Id));
            }));
    }
}
