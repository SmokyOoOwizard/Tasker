using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using Tasker.Core.Dto;
using Tasker.Core.Projects;

namespace Tasker.Mcp.Tools;

/// <summary>Рабочие области MCP (десктоп и демон). На сервере область одна, инструмент не регистрируется.</summary>
[McpServerToolType]
public static class WorkspaceTools
{
    /// <summary>Результат: объект, а не голый массив — у инструмента MCP корень результата объект.</summary>
    public record WorkspaceList(McpWorkspace[] Workspaces);

    [McpServerTool(Name = WorkspaceArgument.ListTool, ReadOnly = true)]
    [Description(
        "Lists the workspaces (folders with their own projects and tasks) this MCP server gives access to: name, path, status (open/opening/failed) " +
        "and the number of projects. Every other tool takes the workspace name in its optional `workspace` argument; " +
        "it can be omitted when exactly one workspace is available. Only these workspaces can be used: any other folder is not found.")]
    public static async Task<WorkspaceList> ListWorkspaces(IMcpWorkspaces workspaces, CancellationToken ct)
    {
        var result = new List<McpWorkspace>();
        foreach (var workspace in workspaces.List())
        {
            if (workspace.Status != McpWorkspaceStatus.Open || workspaces.Enter(workspace.Name) is not { } scope)
            {
                result.Add(workspace);
                continue;
            }

            await using (scope)
            {
                var projects = await scope.Services.GetRequiredService<ProjectService>().GetRange(new Page(0, 1), ct);
                result.Add(workspace with { Projects = projects.TotalCount });
            }
        }

        return new WorkspaceList([.. result]);
    }
}
