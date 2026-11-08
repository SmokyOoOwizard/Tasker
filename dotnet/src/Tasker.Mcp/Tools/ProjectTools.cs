using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using Tasker.Core.Agents;
using Tasker.Core.Dto;
using Tasker.Core.Projects;
using Tasker.Core.Users;
using Tasker.Core.Workspace;

namespace Tasker.Mcp.Tools;

[McpServerToolType]
public static class ProjectTools
{
    [McpServerTool(Name = "whoami", ReadOnly = true)]
    [Description(
        "Returns the user this agent acts as. Agents are regular users: they see only projects they are members of. " +
        "`rights` are the agent's rights: editTasks (create and edit tasks, links, series membership, comments, locks), " +
        "deleteTasks (delete tasks and comments), configureProject (statuses, status sets, task types, fields, enums, link types, boards, the project), " +
        "manageSeries (create, change and delete series, renumber tasks). Without a right the call fails with [forbidden]. " +
        "Locally the rights apply to all projects; on the server they are set per project and listed in `projects`.")]
    public static Task<AgentIdentity> WhoAmI(UserService users, AgentGuard guard, CancellationToken ct) =>
        McpCall.Run(async () =>
        {
            var user = await users.GetCurrent(ct);
            if (!user.IsAgent)
                return new AgentIdentity(user);
            var projects = await guard.ProjectRights(user, ct);
            return new AgentIdentity(user with { Rights = projects == null ? user.Rights ?? AgentRights.Full : null }) { Projects = projects };
        });

    [McpServerTool(Name = "list_projects", ReadOnly = true)]
    [Description("Lists projects visible to the agent. Everything else (statuses, task types, tasks, boards) lives inside a project. Paged: offset/limit (limit 1-200, default 50); the result has totalCount.")]
    public static Task<ListDto<Project>> ListProjects(ProjectService projects, int offset = 0, int limit = Page.DefaultLimit, CancellationToken ct = default) =>
        McpCall.Run(() => projects.GetRange(Page.Of(offset, limit), ct));

    [McpServerTool(Name = "get_project", ReadOnly = true)]
    [Description("Gets a project by id. For an agent `rights` are its rights in this project (see whoami).")]
    public static Task<AgentProject> GetProject(ProjectService projects, AgentGuard guard, Guid projectId, CancellationToken ct) =>
        McpCall.Run(async () =>
        {
            var project = McpCall.Found(await projects.GetById(projectId, ct), $"Project {projectId}");
            return new AgentProject(project) { Rights = await guard.RightsIn(project.Id, ct) };
        });

    [McpServerTool(Name = "create_project")]
    [Description("Creates a project. Only a person can create projects: for an agent the call fails with [forbidden].")]
    public static Task<Project> CreateProject(ProjectService projects, string name, CancellationToken ct) =>
        McpCall.Run(() => projects.Create(new CreateProject(name), ct));

    [McpServerTool(Name = "update_project", Idempotent = true)]
    [Description("Renames a project. `version` is the project's current version (from get_project); if someone changed it since, the call fails with [modified] — re-read and retry.")]
    public static Task<Project> UpdateProject(ProjectService projects, Guid projectId, string name, string version, CancellationToken ct) =>
        McpCall.Run(async () => McpCall.Found(await projects.Update(projectId, new UpdateProject(name, version), ct), $"Project {projectId}"));

    [McpServerTool(Name = "list_users", ReadOnly = true)]
    [Description("Lists all users (people and agents), e.g. to find a user id. Paged: offset/limit (limit 1-200, default 50); the result has totalCount.")]
    public static Task<ListDto<User>> ListUsers(IUserStorage users, int offset = 0, int limit = Page.DefaultLimit, CancellationToken ct = default) =>
        McpCall.Run(() => users.GetRange(null, Page.Of(offset, limit), ct));

    [McpServerTool(Name = "list_project_members", ReadOnly = true)]
    [Description("Lists members of a project (server only: locally every project is open to everyone). Paged: offset/limit (limit 1-200, default 50); the result has totalCount.")]
    public static Task<ListDto<User>> ListProjectMembers(
        IServiceProvider services, ProjectService projects, Guid projectId,
        int offset = 0, int limit = Page.DefaultLimit, CancellationToken ct = default) =>
        McpCall.Run(async () =>
        {
            await McpCall.RequireProject(projects, projectId, ct);
            var members = services.GetService<ProjectMemberService>()
                ?? throw new ModelContextProtocol.McpException("[invalid] Project members exist only on the server");
            return await members.GetRange(projectId, Page.Of(offset, limit), ct);
        });

    [McpServerTool(Name = "list_workspace_problems", ReadOnly = true)]
    [Description(
        "File storage only: lists .tasker files that could not be read (unresolved git merge conflicts, " +
        "invalid YAML, id not matching the file name). Their entities are hidden from all lists until the file is fixed; " +
        "the files are plain YAML, and a fixed file shows up in lists again by itself. " +
        "Also lists a second folder of a project (projects/<folder>, left by a git merge after the project was renamed): " +
        "its entities are listed with the project, and `tasker cleanup` merges the folders. " +
        "Paged: offset/limit (limit 1-200, default 50); the result has totalCount.")]
    public static Task<ListDto<WorkspaceProblem>> ListWorkspaceProblems(
        IServiceProvider services, int offset = 0, int limit = Page.DefaultLimit, CancellationToken ct = default) =>
        McpCall.Run(async () =>
        {
            var workspace = services.GetService<IWorkspaceIndex>()
                ?? throw new ModelContextProtocol.McpException("[invalid] Workspace problems exist only with file storage");
            return await workspace.GetProblems(Page.Of(offset, limit), ct);
        });
}
