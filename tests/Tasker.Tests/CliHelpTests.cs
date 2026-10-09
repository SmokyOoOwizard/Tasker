using System.CommandLine;
using Tasker.Cli;
using Xunit;

namespace Tasker.Tests;

/// <summary>Архитектурная проверка: у каждой команды <c>tasker</c>, параметра и аргумента есть описание, и <c>-h</c> его выводит.</summary>
public class CliHelpTests
{
    private static RootCommand Root() => CliApp.BuildRoot(TextWriter.Null, TextWriter.Null);

    private static IEnumerable<Command> AllCommands(Command command)
    {
        yield return command;
        foreach (var child in command.Subcommands.SelectMany(AllCommands))
            yield return child;
    }

    private static string PathOf(Command command)
    {
        var parts = new List<string>();
        for (Command? c = command; c is not null; c = c.Parents.OfType<Command>().FirstOrDefault())
            parts.Insert(0, c.Name);
        return string.Join(' ', parts);
    }

    public static IEnumerable<object[]> CommandPaths() =>
        AllCommands(Root()).Select(x => new object[] { PathOf(x) });

    [Fact]
    public void Every_command_option_and_argument_has_a_description()
    {
        var missing = new List<string>();
        foreach (var command in AllCommands(Root()))
        {
            var path = PathOf(command);
            if (string.IsNullOrWhiteSpace(command.Description))
                missing.Add($"command '{path}'");
            foreach (var option in command.Options.Where(x => !x.Hidden && string.IsNullOrWhiteSpace(x.Description)))
                missing.Add($"option '{option.Name}' of '{path}'");
            foreach (var argument in command.Arguments.Where(x => !x.Hidden && string.IsNullOrWhiteSpace(x.Description)))
                missing.Add($"argument '{argument.Name}' of '{path}'");
        }

        Assert.True(missing.Count == 0, "No help text for:\n" + string.Join('\n', missing));
    }

    [Theory, MemberData(nameof(CommandPaths))]
    public async Task Dash_h_prints_help_of_every_command(string path)
    {
        var args = path.Split(' ').Skip(1).Append("-h").ToArray();

        // Отдельным процессом: список команд и их описания берутся из сборки, а справку печатает проверяемый tasker.
        var result = await TaskerProcess.Run(args);

        Assert.Equal(0, result.Code);
        Assert.Contains("Usage:", result.Out);
        Assert.Empty(result.Err);
        var command = AllCommands(Root()).Single(x => PathOf(x) == path);
        Assert.Contains(command.Description!, result.Out);
    }
}
