using Tasker.Core.Agents;
using Tasker.Core.Boards;
using Tasker.Core.Fields;
using Tasker.Core.Links;
using Tasker.Core.Locks;
using Tasker.Core.Projects;
using Tasker.Core.Statuses;
using Tasker.Core.Tasks;
using Tasker.Core.TaskSeries;
using Tasker.Core.Users;

namespace Tasker.Core;

public sealed record StorageSet(
    IProjectStorage Projects,
    ITaskStorage Tasks,
    IStatusStorage Statuses,
    IStatusSetStorage StatusSets,
    ITaskTypeStorage TaskTypes,
    ISeriesStorage Series,
    IWriteScope Writes,
    ILinkTypeStorage LinkTypes,
    IFieldStorage Fields,
    IFieldEnumStorage FieldEnums,
    IBoardStorage Boards,
    IUserStorage Users,
    IEditLockStorage EditLocks,
    IAgentTokenStorage? AgentTokens = null);
