using System.CommandLine;
using Tasker.Cli.Completion;
using Tasker.Global;

namespace Tasker.Cli.Commands;

/// <summary>
/// <c>tasker completion zsh|bash|pwsh</c>: скрипт автодополнения по Tab. zsh и bash подключает scripts/install.sh;
/// PowerShell — <c>tasker completion pwsh --install</c> (помеченный блок в <c>$PROFILE</c>), её же вызывает установщик Windows.
/// </summary>
internal static class CompletionCommands
{
    public static Command Build(GlobalOptions g)
    {
        var shell = new Argument<string>("shell") { Description = $"Shell: {string.Join(", ", Shells.Names)}" };
        shell.AcceptOnlyFromAmong(Shells.AcceptedNames);

        var install = new Option<bool>("--install")
        {
            Description = "pwsh only: save the script and add one marked block to your PowerShell profile ($PROFILE) that loads it " +
                "(a copy <profile>.tasker-backup is made before the first change; repeating is safe)"
        };
        var uninstall = new Option<bool>("--uninstall") { Description = "pwsh only: remove the block from your PowerShell profile (the saved script is deleted too)" };
        var profile = new Option<string[]>("--profile")
        {
            Description = "pwsh only, with --install or --uninstall: profile file to change (repeatable). Default: the profiles of PowerShell 7 and " +
                "Windows PowerShell 5.1 (Windows) or of pwsh (macOS, Linux)",
            AllowMultipleArgumentsPerToken = false
        };
        var script = new Option<string?>("--script")
        {
            Description = "pwsh only, with --install: where to save the script. Default: <Tasker data folder>/completions/tasker.ps1"
        };

        return Kit.Plain(g, "completion",
            "Prints the Tab completion script of a shell: commands, options and values (projects, statuses, tasks...) of the current workspace",
            c =>
            {
                c.Arguments.Add(shell);
                c.Options.Add(install);
                c.Options.Add(uninstall);
                c.Options.Add(profile);
                c.Options.Add(script);
            },
            (parse, ctx) =>
            {
                var chosen = Shells.Find(parse.GetRequiredValue(shell))!;
                var installing = parse.GetValue(install);
                var uninstalling = parse.GetValue(uninstall);
                if (!installing && !uninstalling)
                {
                    if (parse.GetValue(profile) is { Length: > 0 } || parse.GetValue(script) != null)
                        throw new CliException("--profile and --script need --install or --uninstall");
                    ctx.Out.Write(chosen.Script());
                    return Task.CompletedTask;
                }

                if (installing && uninstalling)
                    throw new CliException("Use either --install or --uninstall, not both");
                if (chosen.Name != "pwsh")
                    throw new CliException($"--install and --uninstall are for pwsh only: {chosen.Name} is connected by scripts/install.sh (or print the script with 'tasker completion {chosen.Name}')");

                var scriptPath = Path.GetFullPath(UserPath.Expand(parse.GetValue(script)) ?? Path.Combine(AppDirectories.Data, "completions", "tasker.ps1"));
                var profiles = parse.GetValue(profile) is { Length: > 0 } given
                    ? given.Select(x => Path.GetFullPath(UserPath.Expand(x)!)).ToArray()
                    : PowerShellProfile.DefaultProfiles(
                        OperatingSystem.IsWindows(),
                        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME"));
                var newline = OperatingSystem.IsWindows() ? "\r\n" : "\n";

                if (installing)
                {
                    PowerShellProfileFiles.WriteScript(scriptPath, chosen.Script());
                    ctx.Out.WriteLine($"Tab completion script: {scriptPath}");
                }

                foreach (var file in profiles)
                    ctx.Out.WriteLine(PowerShellProfileFiles.Apply(file, installing ? scriptPath : null, newline).Report);

                if (uninstalling && File.Exists(scriptPath))
                {
                    File.Delete(scriptPath);
                    ctx.Out.WriteLine($"Removed the script {scriptPath}");
                }

                if (installing)
                    ctx.Out.WriteLine("Open a new PowerShell window to use it. cmd.exe has no programmable completion: it is not supported there.");
                return Task.CompletedTask;
            }, usesWorkspace: false);
    }
}
