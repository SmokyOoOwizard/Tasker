using HotChocolate.Types;
using Tasker.Core.Boards;
using Tasker.Core.Fields;
using Tasker.Core.Links;
using Tasker.Core.Locks;
using Tasker.Core.Projects;
using Tasker.Core.Statuses;
using Tasker.Core.Tasks;
using Tasker.Core.TaskSeries;
using Tasker.Core.Users;
using Tasker.Web.Graph.Types;

namespace Tasker.Web.Graph;

internal sealed class ProjectType : ObjectType<Project>
{
    protected override void Configure(IObjectTypeDescriptor<Project> descriptor)
    {
        descriptor.Name("Project");
        descriptor.BindFieldsExplicitly();
        descriptor.Field(x => x.Id);
        descriptor.Field(x => x.Name);
        descriptor.Field(x => x.CreatedAt);
        descriptor.Field(x => x.Version);

        descriptor.Read("stats")
            .Returns<ProjectStatsType>()
            .Resolve(async x => await x.Service<ProjectService>().GetStats(x.Parent<Project>().Id, x.RequestAborted));

        descriptor.Read("statuses").Paged().Described()
            .Returns<PageType<StatusListItem>>()
            .Resolve(async x => await x.Service<StatusService>().List(x.Parent<Project>().Id, x.Page(), x.DescriptionLength(), x.RequestAborted));

        descriptor.Read("status").Id()
            .Type<StatusType>()
            .Resolve(async x => await x.Service<IStatusStorage>().GetById(x.Parent<Project>().Id, x.Id(), x.RequestAborted));

        descriptor.Read("statusSets").Paged()
            .Returns<PageType<StatusSet>>()
            .Resolve(async x => await x.Service<IStatusSetStorage>().GetRange(x.Parent<Project>().Id, x.Page(), x.RequestAborted));

        descriptor.Read("statusSet").Id()
            .Type<StatusSetType>()
            .Resolve(async x => await x.Service<IStatusSetStorage>().GetById(x.Parent<Project>().Id, x.Id(), x.RequestAborted));

        descriptor.Read("taskTypes").Paged().Described()
            .Returns<PageType<TaskTypeListItem>>()
            .Resolve(async x => await x.Service<TaskTypeService>().List(x.Parent<Project>().Id, x.Page(), x.DescriptionLength(), x.RequestAborted));

        descriptor.Read("taskType").Id()
            .Type<TaskTypeType>()
            .Resolve(async x => await x.Service<ITaskTypeStorage>().GetById(x.Parent<Project>().Id, x.Id(), x.RequestAborted));

        descriptor.Read("fields").Paged()
            .Returns<PageType<FieldDefinition>>()
            .Resolve(async x => await x.Service<FieldService>().GetRange(x.Parent<Project>().Id, x.Page(), x.RequestAborted));

        descriptor.Read("field").Id()
            .Type<FieldDefinitionType>()
            .Resolve(async x => await x.Service<FieldService>().GetById(x.Parent<Project>().Id, x.Id(), x.RequestAborted));

        descriptor.Read("enums").Paged()
            .Returns<PageType<FieldEnum>>()
            .Resolve(async x => await x.Service<FieldEnumService>().GetRange(x.Parent<Project>().Id, x.Page(), x.RequestAborted));

        descriptor.Read("enum").Id()
            .Type<FieldEnumType>()
            .Resolve(async x => await x.Service<FieldEnumService>().GetById(x.Parent<Project>().Id, x.Id(), x.RequestAborted));

        descriptor.Read("linkTypes").Paged()
            .Returns<PageType<LinkType>>()
            .Resolve(async x => await x.Service<LinkTypeService>().GetRange(x.Parent<Project>().Id, x.Page(), x.RequestAborted));

        descriptor.Read("linkType").Id()
            .Type<LinkTypeType>()
            .Resolve(async x => await x.Service<LinkTypeService>().GetById(x.Parent<Project>().Id, x.Id(), x.RequestAborted));

        descriptor.Read("tasks").TaskFilters()
            .Returns<PageType<TaskListItem>>()
            .Resolve(async x => await x.Service<TaskService>().List(
                x.Parent<Project>().Id, x.TaskFilter(), x.FieldFilters(), x.Page(), x.DescriptionLength(), x.RequestAborted, x.Sort()));

        descriptor.Read("taskTree").TaskFilters()
            .Argument("parentId", x => x.Type<UuidType>())
            .Argument("levels", x => x.Type<IntType>())
            .Returns<TaskTreeListType>()
            .Resolve(async x => await x.Service<TaskService>().ListTree(
                x.Parent<Project>().Id, x.TaskFilter(), x.FieldFilters(), x.Page(), x.DescriptionLength(), x.RequestAborted, x.Sort(),
                x.ArgumentValue<Guid?>("parentId"), x.ArgumentValue<int?>("levels") ?? TaskHierarchy.MaxDepth));

        descriptor.Read("resolveTasks")
            .Argument("ref", x => x.Type<NonNullType<StringType>>())
            .Type<NonNullType<ListType<NonNullType<TaskItemType>>>>()
            .Resolve(async x => await x.Service<TaskService>().Resolve(x.Parent<Project>().Id, x.ArgumentValue<string>("ref"), x.RequestAborted));

        descriptor.Read("task").Id()
            .Type<TaskDetailsType>()
            .Resolve(async x => await x.Service<TaskService>().Describe(x.Parent<Project>().Id, x.Id(), x.RequestAborted));

        descriptor.Read("taskLinks").Id()
            .Type<ListType<NonNullType<TaskLinkViewType>>>()
            .Resolve(async x => await x.Service<TaskLinkService>().GetLinks(x.Parent<Project>().Id, x.Id(), x.RequestAborted));

        descriptor.Read("seriesList").Paged()
            .Returns<PageType<Series>>()
            .Resolve(async x => await x.Service<ISeriesStorage>().GetRange(x.Parent<Project>().Id, x.Page(), x.RequestAborted));

        descriptor.Read("series").Id()
            .Type<SeriesType>()
            .Resolve(async x => await x.Service<ISeriesStorage>().GetById(x.Parent<Project>().Id, x.Id(), x.RequestAborted));

        descriptor.Read("seriesHealth")
            .Returns<SeriesHealthType>()
            .Resolve(async x => await x.Service<SeriesHealthService>().Check(x.Parent<Project>().Id, x.RequestAborted));

        descriptor.Read("seriesConflictPlan").Id("seriesId")
            .Type<ConflictPlanType>()
            .Resolve(async x => await x.Service<SeriesConflictService>().Plan(x.Parent<Project>().Id, x.Id("seriesId"), x.RequestAborted));

        descriptor.Read("boards").Paged()
            .Returns<PageType<Board>>()
            .Resolve(async x => await x.Service<IBoardStorage>().GetRange(x.Parent<Project>().Id, x.Page(), x.RequestAborted));

        descriptor.Read("board").Id()
            .Type<BoardType>()
            .Resolve(async x => await x.Service<IBoardStorage>().GetById(x.Parent<Project>().Id, x.Id(), x.RequestAborted));

        descriptor.Read("columnTasks").Id("boardId").Id("columnId").TaskQuery()
            .Type<PageType<TaskListItem>>()
            .Resolve(async x => await x.Service<BoardService>().GetColumnTasks(
                x.Parent<Project>().Id, x.Id("boardId"), x.Id("columnId"), x.Page(), x.RequestAborted, x.FieldFilters(), x.DescriptionLength(), x.Sort()));

        descriptor.Read("locks").Paged()
            .Returns<PageType<LockInfo>>()
            .Resolve(async x => x.Page().All(await x.Service<EntityLockService>().GetByProject(x.Parent<Project>().Id, x.RequestAborted)));

        descriptor.Read("lock")
            .Argument("entity", x => x.Type<NonNullType<NamedEnumType<LockedEntity>>>())
            .Id()
            .Type<LockInfoType>()
            .Resolve(async x => await x.Service<EntityLockService>().Get(x.ArgumentValue<LockedEntity>("entity"), x.Id(), x.RequestAborted));

        descriptor.Read("members").Paged()
            .Returns<PageType<User>>()
            .Resolve(async x =>
            {
                x.RequireServer("members");
                return await x.Service<ProjectMemberService>().GetRange(x.Parent<Project>().Id, x.Page(), x.RequestAborted);
            });
    }
}
