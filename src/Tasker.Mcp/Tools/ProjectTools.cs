using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using Tasker.Core.Dto;
using Tasker.Core.Projects;
using Tasker.Core.Users;
using Tasker.Core.Workspace;

namespace Tasker.Mcp.Tools;

[McpServerToolType]
public static class ProjectTools
{
    [McpServerTool(Name = "whoami", ReadOnly = true)]
    [Description("Returns the user this agent acts as. Agents are regular users: they see only projects they are members of.")]
    public static Task<User> WhoAmI(UserService users, CancellationToken ct) =>
        McpCall.Run(() => users.GetCurrent(ct));

    [McpServerTool(Name = "list_projects", ReadOnly = true)]
    [Description("Lists projects visible to the agent. Everything else (statuses, task types, tasks, boards) lives inside a project. Paged: offset/limit (limit 1-200, default 50); the result has totalCount.")]
    public static Task<ListDto<Project>> ListProjects(ProjectService projects, int offset = 0, int limit = Page.DefaultLimit, CancellationToken ct = default) =>
        McpCall.Run(() => projects.GetRange(Page.Of(offset, limit), ct));

    [McpServerTool(Name = "get_project", ReadOnly = true)]
    [Description("Gets a project by id.")]
    public static Task<Project> GetProject(ProjectService projects, Guid projectId, CancellationToken ct) =>
        McpCall.Run(async () => McpCall.Found(await projects.GetById(projectId, ct), $"Project {projectId}"));

    [McpServerTool(Name = "create_project")]
    [Description("Creates a project. On the server the agent becomes its member.")]
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
    [Description("Lists members of a project (server only: in the desktop app every project is open to everyone). Paged: offset/limit (limit 1-200, default 50); the result has totalCount.")]
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
        "Desktop app with file storage only: lists .tasker files that could not be read (unresolved git merge conflicts, " +
        "invalid YAML, id not matching the file name). Their entities are hidden from all lists until the file is fixed; " +
        "the files are plain YAML, and a fixed file shows up in lists again by itself. " +
        "Paged: offset/limit (limit 1-200, default 50); the result has totalCount.")]
    public static Task<ListDto<WorkspaceProblem>> ListWorkspaceProblems(
        IServiceProvider services, int offset = 0, int limit = Page.DefaultLimit, CancellationToken ct = default) =>
        McpCall.Run(async () =>
        {
            var workspace = services.GetService<IWorkspaceIndex>()
                ?? throw new ModelContextProtocol.McpException("[invalid] Workspace problems exist only in the desktop app with file storage");
            return await workspace.GetProblems(Page.Of(offset, limit), ct);
        });
}
