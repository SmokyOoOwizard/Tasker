using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol;
using Tasker.Core;
using Tasker.Core.Projects;
using Tasker.Core.Tasks;

namespace Tasker.Mcp;

/// <summary>
/// Общее для инструментов MCP: ошибки Core превращаются в ошибку инструмента с тем же текстом, что в REST —
/// агент видит причину и может исправить запрос. Код конфликта — в начале текста: <c>[modified] …</c>.
/// </summary>
internal static class McpCall
{

    public static async Task<T> Run<T>(Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (TaskerValidationException e)
        {
            throw new McpException($"[invalid] {e.Message}");
        }
        catch (TaskerForbiddenException e)
        {
            throw new McpException($"[forbidden] {e.Message}");
        }
        catch (TaskerNotFoundException e)
        {
            throw new McpException($"[not_found] {e.Message}");
        }
        catch (TaskerConflictException e)
        {
            var code = e.Code switch
            {
                ConflictCode.Modified => "modified",
                ConflictCode.Locked => "locked",
                ConflictCode.UnsupportedFormat => "unsupported_format",
                _ => "in_use"
            };
            throw new McpException($"[{code}] {e.Message}");
        }
    }

    /// <summary>
    /// Id задачи из аргумента инструмента: полный Guid или короткий id (первые 8 и более шестнадцатеричных символов, как в консоли).
    /// Задача по префиксу не нашлась — [not_found], префикс подходит нескольким — [invalid] со списком полных id.
    /// </summary>
    public static async Task<Guid> TaskId(TaskService tasks, Guid projectId, string id, CancellationToken ct) =>
        await tasks.ResolveId(projectId, id, ct) ?? throw new McpException($"[not_found] Task {id} not found");

    public static T Found<T>(T? value, string what) where T : class =>
        value ?? throw new McpException($"[not_found] {what} not found");

    public static void Found(bool found, string what)
    {
        if (!found)
            throw new McpException($"[not_found] {what} not found");
    }

    /// <summary>
    /// Проект существует и доступен текущему агенту. REST проверяет это фильтром группы маршрутов,
    /// в MCP — каждый инструмент, работающий внутри проекта.
    /// </summary>
    public static async Task RequireProject(ProjectService projects, Guid projectId, CancellationToken ct) =>
        Found(await projects.GetById(projectId, ct), $"Project {projectId}");
}
