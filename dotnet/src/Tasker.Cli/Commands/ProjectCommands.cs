using System.CommandLine;
using Tasker.Cli.Completion;
using Tasker.Core.Projects;

namespace Tasker.Cli.Commands;

internal static class ProjectCommands
{
    public static Command Build(GlobalOptions g)
    {
        var name = Kit.Name("name", "Project name");
        var reference = Kit.Ref("Project");
        var newName = new Option<string?>("--name") { Description = "New name" };
        var expected = Kit.ExpectedVersion();
        var yes = new Option<bool>("--yes", "-y") { Description = "Confirm: deleting a project deletes everything in it" };

        reference.Suggests(g, Sources.Projects);
        
        return Kit.Group("project", "Projects",
            Kit.Leaf(g, "create", "Creates a project", c => c.Arguments.Add(name), async (parse, ctx) =>
            {
                var project = await ctx.Get<ProjectService>().Create(new CreateProject(parse.GetRequiredValue(name)), ctx.Ct);
                ctx.Print(project, $"Created project '{project.Name}' {project.Id}");
            }),
            Kit.List(g, "Lists projects",
                (ctx, page) => ctx.Get<ProjectService>().GetRange(page, ctx.Ct),
                x => Kit.Row(x.Id, x.Name)),
            Kit.Leaf(g, "get", "Shows a project", c => c.Arguments.Add(reference), async (parse, ctx) =>
            {
                var project = await ctx.FindProject(parse.GetRequiredValue(reference));
                ctx.Print(project, Kit.Fields(("id", project.Id), ("name", project.Name), ("created", project.CreatedAt), ("version", project.Version)));
            }),
            Kit.Leaf(g, "update", "Changes a project", c =>
            {
                c.Arguments.Add(reference);
                c.Options.Add(newName);
                c.Options.Add(expected);
            }, async (parse, ctx) =>
            {
                Kit.RequireChange(parse, newName);
                var project = await ctx.FindProject(parse.GetRequiredValue(reference));
                var updated = await ctx.Get<ProjectService>().Update(project.Id,
                    new UpdateProject(parse.GetValue(newName), parse.GetValue(expected) ?? project.Version), ctx.Ct)
                    ?? throw new CliException($"No project '{project.Id}'");
                ctx.Print(updated, $"Updated project '{updated.Name}' {updated.Id}");
            }),
            Kit.Leaf(g, "delete", "Deletes a project with everything in it", c =>
            {
                c.Arguments.Add(reference);
                c.Options.Add(yes);
                c.Options.Add(expected);
            }, async (parse, ctx) =>
            {
                var project = await ctx.FindProject(parse.GetRequiredValue(reference));
                if (!parse.GetValue(yes))
                {
                    var stats = await ctx.Get<ProjectService>().GetStats(project.Id, ctx.Ct)
                        ?? throw new CliException($"No project '{project.Id}'");
                    var summary = Summary(project.Name, stats);
                    if (!ctx.IsInteractive)
                        throw new CliException($"{summary} Repeat with --yes to confirm");

                    ctx.Prompts.WriteLine(summary);
                    ctx.Prompts.Write($"Delete project '{project.Name}'? [y/N] ");
                    ctx.Prompts.Flush();
                    var answer = (await ctx.Input.ReadLineAsync(ctx.Ct))?.Trim();
                    if (!string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase) && !string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase))
                    {
                        // Ввод закончился без перевода строки (EOF) — курсор остался после вопроса.
                        ctx.Prompts.WriteLine();
                        throw new CliException("Cancelled, nothing was deleted");
                    }
                }

                await ctx.Get<ProjectService>().Delete(project.Id, parse.GetValue(expected) ?? project.Version, ctx.Ct);
                ctx.Print(new { deleted = project.Id }, Kit.Deleted("project", project.Name, project.Id));
            }));
    }

    /// <summary>Что пропадёт вместе с проектом; нулевые количества не показываются.</summary>
    private static string Summary(string name, ProjectStats stats)
    {
        var parts = new (int Count, string One, string Many)[]
        {
            (stats.Tasks, "task", "tasks"), (stats.Boards, "board", "boards"), (stats.Statuses, "status", "statuses"),
            (stats.StatusSets, "status set", "status sets"), (stats.TaskTypes, "task type", "task types"),
            (stats.Series, "series", "series"), (stats.LinkTypes, "link type", "link types")
        }.Where(x => x.Count > 0).Select(x => $"{x.Count} {(x.Count == 1 ? x.One : x.Many)}").ToArray();

        return parts.Length == 0
            ? $"Project '{name}' is empty and will be deleted. This cannot be undone."
            : $"Project '{name}' will be deleted with everything in it: {string.Join(", ", parts)}. This cannot be undone.";
    }
}
