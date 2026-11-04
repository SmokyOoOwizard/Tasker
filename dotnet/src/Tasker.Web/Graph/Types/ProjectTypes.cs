using HotChocolate.Types;
using Tasker.Core.Projects;

namespace Tasker.Web.Graph.Types;

internal sealed class ProjectStatsType : ObjectType<ProjectStats>
{
    protected override void Configure(IObjectTypeDescriptor<ProjectStats> descriptor)
    {
        descriptor.Name("ProjectStats");
        descriptor.BindFieldsExplicitly();
        descriptor.Field(x => x.Tasks);
        descriptor.Field(x => x.Boards);
        descriptor.Field(x => x.Statuses);
        descriptor.Field(x => x.StatusSets);
        descriptor.Field(x => x.TaskTypes);
        descriptor.Field(x => x.Series);
        descriptor.Field(x => x.LinkTypes);
    }
}

internal sealed record Registration(bool Open);

internal sealed class RegistrationType : ObjectType<Registration>
{
    protected override void Configure(IObjectTypeDescriptor<Registration> descriptor)
    {
        descriptor.Name("Registration");
        descriptor.BindFieldsExplicitly();
        descriptor.Field(x => x.Open);
    }
}
