namespace Tasker.Core.Agents;

public enum AgentRight
{
    EditTasks,
    DeleteTasks,
    ConfigureProject,
    ManageSeries
}

public record AgentRights(bool EditTasks, bool DeleteTasks, bool ConfigureProject, bool ManageSeries)
{
    public static readonly AgentRights Full = new(true, true, true, true);
    public static readonly AgentRights ReadOnly = new(false, false, false, false);

    public bool Has(AgentRight right) => right switch
    {
        AgentRight.EditTasks => EditTasks,
        AgentRight.DeleteTasks => DeleteTasks,
        AgentRight.ConfigureProject => ConfigureProject,
        AgentRight.ManageSeries => ManageSeries,
        _ => false
    };

    public int ToMask() =>
        (EditTasks ? 1 : 0) | (DeleteTasks ? 2 : 0) | (ConfigureProject ? 4 : 0) | (ManageSeries ? 8 : 0);

    public static AgentRights FromMask(int mask) =>
        new((mask & 1) != 0, (mask & 2) != 0, (mask & 4) != 0, (mask & 8) != 0);

    public static string Wire(AgentRight right) => right switch
    {
        AgentRight.EditTasks => "editTasks",
        AgentRight.DeleteTasks => "deleteTasks",
        AgentRight.ConfigureProject => "configureProject",
        AgentRight.ManageSeries => "manageSeries",
        _ => right.ToString()
    };

    public string[] ToWire() =>
        Enum.GetValues<AgentRight>().Where(Has).Select(Wire).ToArray();

    public static AgentRights FromWire(IEnumerable<string> names)
    {
        var set = names.Select(x => x.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new(
            set.Contains(Wire(AgentRight.EditTasks)),
            set.Contains(Wire(AgentRight.DeleteTasks)),
            set.Contains(Wire(AgentRight.ConfigureProject)),
            set.Contains(Wire(AgentRight.ManageSeries)));
    }
}

public record ProjectAgentRights(Guid ProjectId, AgentRights Rights);
