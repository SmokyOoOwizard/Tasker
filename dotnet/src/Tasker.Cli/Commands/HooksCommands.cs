using System.CommandLine;
using Tasker.Daemon.Services;

namespace Tasker.Cli.Commands;

/// <summary>
/// <c>tasker hooks</c>: git-хуки, которые после <c>pull</c>, <c>checkout</c>, <c>merge</c> и <c>rebase</c> запускают
/// <c>tasker sync</c> для рабочей папки (см. <see cref="GitHooks"/>).
/// </summary>
internal static class HooksCommands
{
    public static Command Build(GlobalOptions g)
    {
        var hooks = new GitHooks(new SystemProcessRunner());
        string Folder(ParseResult parse) => parse.GetValue(g.Workspace) ?? Directory.GetCurrentDirectory();

        return Kit.Group("hooks", "Git hooks that keep the cache up to date after pull, checkout, merge and rebase",
            Kit.Plain(g, "install", "Installs the hooks (post-merge, post-checkout, post-rewrite) that run 'tasker sync'", _ => { }, async (parse, ctx) =>
            {
                var states = await hooks.Install(Folder(parse));
                ctx.Print(states, $"Installed in {Path.GetDirectoryName(states[0].Path)}:\n" + string.Join('\n', states.Select(x => $"  {x.Name}")));
            }),
            Kit.Plain(g, "uninstall", "Removes the Tasker part of the hooks; other commands in them stay", _ => { }, async (parse, ctx) =>
            {
                var removed = await hooks.Uninstall(Folder(parse));
                ctx.Print(new { removed }, removed.Length == 0 ? "No Tasker hooks found" : "Removed from: " + string.Join(", ", removed));
            }),
            Kit.Plain(g, "status", "Shows which hooks are installed", _ => { }, async (parse, ctx) =>
            {
                var states = await hooks.Status(Folder(parse));
                ctx.Print(states, string.Join('\n', states.Select(x =>
                    $"{x.Name,-14} {(x.Installed ? "installed" : "not installed")}{(x.Foreign ? " (has other commands)" : "")}")));
            }));
    }
}
