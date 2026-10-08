using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Tasker.Mcp;

/// <summary>
/// Текст ошибок инструментов. SDK превращает <see cref="McpException"/> в результат <c>isError</c>, добавляя к сообщению префикс
/// <c>An error occurred invoking '&lt;tool&gt;':</c>, так что код <c>[modified]</c> оказывается не в начале. Фильтр вызова убирает префикс:
/// ошибка приходит результатом инструмента (<c>isError: true</c>), а текст начинается с кода в квадратных скобках.
/// </summary>
internal static class ErrorText
{
    public static McpRequestHandler<CallToolRequestParams, CallToolResult> Filter(McpRequestHandler<CallToolRequestParams, CallToolResult> next) =>
        async (context, ct) =>
        {
            CallToolResult result;
            try
            {
                result = await next(context, ct);
            }
            catch (McpException e) when (!ct.IsCancellationRequested)
            {
                // Если SDK не поймал сам (например, ошибка в фильтре ниже): то же самое, без префикса.
                return Error(e.Message);
            }

            if (result.IsError == true && context.Params?.Name is { } name)
            {
                var prefix = Prefix(name);
                foreach (var block in result.Content.OfType<TextContentBlock>())
                {
                    if (block.Text.StartsWith(prefix, StringComparison.Ordinal))
                        block.Text = block.Text[prefix.Length..];
                }
            }

            return result;
        };

    /// <summary>Префикс, который SDK добавляет к сообщению об ошибке инструмента.</summary>
    internal static string Prefix(string tool) => $"An error occurred invoking '{tool}': ";

    public static CallToolResult Error(string text) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = text }]
    };
}
