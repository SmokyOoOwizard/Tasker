using System.CommandLine;

namespace Tasker.Cli.Completion;

/// <summary>
/// Команда, которая не обращается к рабочей области (справочник, скрипт автодополнения): общие параметры области
/// (<c>-w</c>, <c>--sqlite</c>, <c>-p</c>) у неё бессмысленны, и автодополнение их не предлагает. Разбор параметров не меняется:
/// указанные вручную, они по-прежнему принимаются.
/// </summary>
internal sealed class WorkspaceFreeCommand(string name, string description, IEnumerable<Option> hidden) : Command(name, description)
{
    /// <summary>Имена и псевдонимы параметров, которые не предлагаются.</summary>
    public HashSet<string> HiddenNames { get; } = [.. hidden.SelectMany(x => x.Aliases.Prepend(x.Name))];
}
