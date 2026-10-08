using System.CommandLine;
using Tasker.Cli.Completion;
using Tasker.Core.Links;

namespace Tasker.Cli.Commands;

internal static class LinkTypeCommands
{
    private static async Task<LinkType> Find(Context ctx, Guid projectId, string reference) =>
        Refs.FindItem(await ctx.Get<LinkTypeService>().GetAll(projectId, ctx.Ct), reference, x => x.Id, x => x.Name, "link type");

    private static string Sides(LinkType type) => type.IsSymmetric ? type.OutwardName : $"{type.OutwardName} / {type.InwardName}";

    public static Command Build(GlobalOptions g)
    {
        var name = Kit.Name("name", "Link type name");
        var outward = new Option<string>("--outward") { Description = "How the link reads for the task it starts from, e.g. 'depends on'", Required = true };
        var inward = new Option<string?>("--inward") { Description = "How it reads for the task it points to, e.g. 'is a dependency of'. Omit it for a link without direction ('relates to')" };
        var allowCycles = new Option<bool?>("--allow-cycles")
        {
            Description = "true or false: whether a chain of links of this type may close a cycle (A blocks B, B blocks A). "
                + "false rejects such a link; the default is true for your own types (Blocks forbids cycles). Ignored for links without direction"
        };
        var newAllowCycles = new Option<bool?>("--allow-cycles")
        {
            Description = "true or false: whether a chain of links of this type may close a cycle. Changing it does not touch existing links (cycles that exist are reported by 'tasker sync' and 'tasker cleanup --check')"
        };
        var hierarchical = new Option<bool?>("--hierarchical")
        {
            Description = "true or false: a parent/child type ('includes' / 'is part of'): the task the link starts from is the parent (an epic), the target its child; "
                + "'task list' shows children under the parent, a task may have several parents. Cycles are always rejected, the two names must differ. Default false (Parent/Child is hierarchical)"
        };
        var newHierarchical = new Option<bool?>("--hierarchical")
        {
            Description = "true or false: make the type parent/child (cycles become forbidden, the two names must differ) or an ordinary one. Existing links are not touched"
        };
        var reference = Kit.Ref("Link type");
        var newName = new Option<string?>("--name") { Description = "New name" };
        var newOutward = new Option<string?>("--outward") { Description = "New outward name" };
        var newInward = new Option<string?>("--inward") { Description = "New inward name" };
        var expected = Kit.ExpectedVersion();

        reference.Suggests(g, Sources.LinkTypes);
        
        return Kit.Group("link-type", "Link types between tasks (Blocks, Duplicate, Cloners, Relates, Problem/Incident, Parent/Child by default; add your own)",
            Kit.Leaf(g, "create", "Creates a link type", c =>
            {
                c.Arguments.Add(name);
                c.Options.Add(outward);
                c.Options.Add(inward);
                c.Options.Add(allowCycles);
                c.Options.Add(hierarchical);
            }, async (parse, ctx) =>
            {
                var type = await ctx.Get<LinkTypeService>().Create(await ctx.ProjectId(),
                    new CreateLinkType(parse.GetRequiredValue(name), parse.GetRequiredValue(outward), parse.GetValue(inward), parse.GetValue(allowCycles), parse.GetValue(hierarchical)), ctx.Ct);
                ctx.Print(type, $"Created link type '{type.Name}' ({Sides(type)}) {type.Id}");
            }),
            Kit.List(g, "Lists link types",
                async (ctx, page) => await ctx.Get<LinkTypeService>().GetRange(await ctx.ProjectId(), page, ctx.Ct),
                x => Kit.Row(x.Id, x.Name, Sides(x))),
            Kit.Leaf(g, "get", "Shows a link type", c => c.Arguments.Add(reference), async (parse, ctx) =>
            {
                var type = await Find(ctx, await ctx.ProjectId(), parse.GetRequiredValue(reference));
                ctx.Print(type, Kit.Fields(("id", type.Id), ("name", type.Name), ("outward", type.OutwardName), ("inward", type.InwardName), ("allowCycles", type.AllowCycles), ("hierarchical", type.Hierarchical), ("version", type.Version)));
            }),
            Kit.Leaf(g, "update", "Changes a link type", c =>
            {
                c.Arguments.Add(reference);
                c.Options.Add(newName);
                c.Options.Add(newOutward);
                c.Options.Add(newInward);
                c.Options.Add(newAllowCycles);
                c.Options.Add(newHierarchical);
                c.Options.Add(expected);
            }, async (parse, ctx) =>
            {
                Kit.RequireChange(parse, newName, newOutward, newInward, newAllowCycles, newHierarchical);
                var projectId = await ctx.ProjectId();
                var type = await Find(ctx, projectId, parse.GetRequiredValue(reference));
                var updated = await ctx.Get<LinkTypeService>().Update(projectId, type.Id,
                    new UpdateLinkType(parse.GetValue(newName), parse.GetValue(newOutward), parse.GetValue(newInward), parse.GetValue(expected) ?? type.Version, parse.GetValue(newAllowCycles), parse.GetValue(newHierarchical)), ctx.Ct)
                    ?? throw new CliException($"No link type '{type.Id}'");
                ctx.Print(updated, $"Updated link type '{updated.Name}' ({Sides(updated)}) {updated.Id}");
            }),
            Kit.Leaf(g, "delete", "Deletes a link type (without links)", c =>
            {
                c.Arguments.Add(reference);
                c.Options.Add(expected);
            }, async (parse, ctx) =>
            {
                var projectId = await ctx.ProjectId();
                var type = await Find(ctx, projectId, parse.GetRequiredValue(reference));
                await ctx.Get<LinkTypeService>().Delete(projectId, type.Id, parse.GetValue(expected) ?? type.Version, ctx.Ct);
                ctx.Print(new { deleted = type.Id }, Kit.Deleted("link type", type.Name, type.Id));
            }));
    }
}
