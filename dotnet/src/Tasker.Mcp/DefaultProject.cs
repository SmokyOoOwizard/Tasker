using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Tasker.Core.Dto;
using Tasker.Core.Projects;

namespace Tasker.Mcp;

/// <summary>
/// Проект по умолчанию: если в рабочей области (доступных агенту) ровно один проект, инструменты, которым нужен <c>projectId</c>,
/// можно вызывать без него — как консоль без <c>--project</c>. Проектов нет или несколько — инструмент отвечает ошибкой
/// <c>[invalid]</c> с подсказкой; указанный <c>projectId</c> всегда главнее.
/// <para>
/// Делается в одном месте, а не в каждом инструменте: у инструментов <c>projectId</c> стоит перед обязательными параметрами, и в C#
/// сделать его необязательным можно только перестановкой параметров всех инструментов. Вместо этого (1) схема каждого инструмента с
/// <c>projectId</c> объявляет его необязательным (<see cref="MakeOptional"/>), а (2) фильтр вызова (<see cref="Filter"/>) подставляет
/// единственный проект в аргументы до того, как SDK свяжет их с параметрами. Сами инструменты не знают о подстановке.
/// </para>
/// </summary>
internal static class DefaultProject
{
    public const string Argument = "projectId";

    /// <summary>У инструмента есть параметр <c>projectId</c> (он работает внутри проекта).</summary>
    public static bool TakesProject(Tool tool) =>
        tool.InputSchema.ValueKind == JsonValueKind.Object
        && tool.InputSchema.TryGetProperty("properties", out var properties)
        && properties.ValueKind == JsonValueKind.Object
        && properties.TryGetProperty(Argument, out _);

    /// <summary>
    /// Схема инструмента: <c>projectId</c> убирается из <c>required</c>, в его описание добавляется, когда его можно опустить.
    /// Идемпотентна: повторный вызов ничего не меняет.
    /// </summary>
    public static void MakeOptional(Tool tool)
    {
        if (!TakesProject(tool))
            return;

        var schema = JsonNode.Parse(tool.InputSchema.GetRawText())!.AsObject();
        if (schema["required"] is JsonArray required)
        {
            foreach (var item in required.Where(x => x?.GetValue<string>() == Argument).ToList())
                required.Remove(item);
            if (required.Count == 0)
                schema.Remove("required");
        }

        const string Note = " Optional if the workspace has exactly one project: then that project is used; with several projects it is required.";
        if (schema["properties"]?[Argument] is JsonObject property && !(property["description"]?.GetValue<string>() ?? "").Contains(Note))
            property["description"] = (property["description"]?.GetValue<string>() ?? "").TrimEnd() + Note;

        tool.InputSchema = JsonSerializer.SerializeToElement(schema);
    }

    /// <summary>Фильтр вызова инструмента: подставляет единственный проект или отвечает понятной ошибкой.</summary>
    public static McpRequestHandler<CallToolRequestParams, CallToolResult> Filter(McpRequestHandler<CallToolRequestParams, CallToolResult> next) =>
        async (context, ct) =>
        {
            if (context.MatchedPrimitive is not McpServerTool tool || !TakesProject(tool.ProtocolTool) || HasArgument(context.Params))
                return await next(context, ct);

            var projects = (context.Services ?? context.Server.Services)!.GetRequiredService<ProjectService>();
            var found = await projects.GetRange(new Page(0, 2), ct);
            switch (found.TotalCount)
            {
                case 1:
                    context.Params!.Arguments ??= new Dictionary<string, JsonElement>();
                    context.Params.Arguments[Argument] = JsonSerializer.SerializeToElement(found.Data[0].Id);
                    return await next(context, ct);

                case 0:
                    return ErrorText.Error($"[invalid] projectId is required: this workspace has no projects (create one with create_project)");

                default:
                    return ErrorText.Error($"[invalid] projectId is required: this workspace has {found.TotalCount} projects; list_projects shows them");
            }
        };

    private static bool HasArgument(CallToolRequestParams? request) =>
        request?.Arguments is { } arguments
        && arguments.TryGetValue(Argument, out var value)
        && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);
}
