using System.Diagnostics;
using System.Text.Json.Nodes;
using Tasker.Global;
using Xunit;

namespace Tasker.Tests;

/// <summary>
/// Демон только что запущен и ещё открывает области (TSK-100): вызов MCP ждёт открытия, а если область так и не открылась в срок —
/// отвечает повторяемой ошибкой <c>[unavailable]</c>, а не <c>[invalid]</c>. Открытие замедляет тестовая переменная
/// <c>TASKER_MCP_OPEN_DELAY_MS</c> демона; срок ожидания вызова — <c>TASKER_MCP_OPEN_WAIT_MS</c>.
/// </summary>
public class SlowOpenMcpTests : IDisposable
{
    private readonly DaemonFixture _daemon = new();
    private readonly List<IDisposable> _env = [];
    private Process? _process;

    public void Dispose()
    {
        _daemon.Dispose();
        _process?.Dispose();
        foreach (var x in _env)
            x.Dispose();
    }

    /// <summary>Запускает демон напрямую (не <c>mcp start</c>, который ждёт открытия) и ждёт, пока он начнёт отвечать.</summary>
    private async Task StartSlow(int delayMs, int waitMs)
    {
        _env.Add(AppEnvironment.Override("TASKER_MCP_OPEN_DELAY_MS", delayMs.ToString()));
        _env.Add(AppEnvironment.Override("TASKER_MCP_OPEN_WAIT_MS", waitMs.ToString()));
        _process = TaskerProcess.Start("mcp", "run", "--detached");

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if ((await http.GetAsync($"http://127.0.0.1:{_daemon.Port}/api/health")).IsSuccessStatusCode)
                    return;
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
            {
                // Порт уже слушает супервизор: пока рабочий процесс не принял соединение, оно ждёт в очереди (таймаут), а не отклоняется.
            }
            await Task.Delay(50);
        }
        throw new InvalidOperationException("The daemon did not start");
    }

    private static string StatusOf(string list, string name) =>
        JsonNode.Parse(list)!["workspaces"]!.AsArray().Single(x => x!["name"]!.GetValue<string>() == name)!["status"]!.GetValue<string>();

    [Fact]
    public async Task A_call_during_the_opening_waits_for_the_workspace_and_succeeds_with_or_without_the_argument()
    {
        await _daemon.Workspace("a", "Alpha");
        await StartSlow(delayMs: 1500, waitMs: 20000);

        // list_workspaces видит область открывающейся, а вызов без аргумента (область одна) ждёт её и проходит.
        var (listError, listText) = await _daemon.CallToolResult(null, "list_workspaces");
        Assert.False(listError, listText);
        Assert.Equal("opening", StatusOf(listText, "a"));

        var clock = Stopwatch.StartNew();
        var (isError, text) = await _daemon.CallToolResult(null, "list_projects");
        Assert.False(isError, text);
        Assert.Equal(["Alpha"], DaemonFixture.ProjectNames(text));
        Assert.True(clock.Elapsed > TimeSpan.FromMilliseconds(200), "the call must have waited for the opening");

        // Область открыта — то же с аргументом.
        Assert.Equal(["Alpha"], DaemonFixture.ProjectNames((await _daemon.CallToolResult("a", "list_projects")).Text));
        Assert.Equal("open", StatusOf((await _daemon.CallToolResult(null, "list_workspaces")).Text, "a"));
    }

    [Fact]
    public async Task A_call_that_names_the_workspace_waits_for_it_too()
    {
        await _daemon.Workspace("a", "Alpha");
        await _daemon.Workspace("b", "Beta");
        await StartSlow(delayMs: 800, waitMs: 20000);

        var (isError, text) = await _daemon.CallToolResult("b", "list_projects");

        Assert.False(isError, text);
        Assert.Equal(["Beta"], DaemonFixture.ProjectNames(text));
    }

    [Fact]
    public async Task When_the_workspace_is_still_opening_after_the_wait_the_error_is_retryable_and_not_invalid()
    {
        await _daemon.Workspace("a", "Alpha");
        await StartSlow(delayMs: 4000, waitMs: 300);

        var (isError, text) = await _daemon.CallToolResult(null, "list_projects");

        Assert.True(isError);
        Assert.StartsWith("[unavailable] Workspace 'a' is opening, retry in 2 s", text);
        Assert.DoesNotContain("[invalid]", text);

        // Повтор после открытия проходит.
        var deadline = DateTime.UtcNow.AddSeconds(20);
        (bool IsError, string Text) retry;
        do
        {
            await Task.Delay(300);
            retry = await _daemon.CallToolResult("a", "list_projects");
        }
        while (retry.IsError && DateTime.UtcNow < deadline);
        Assert.False(retry.IsError, retry.Text);
        Assert.Equal(["Alpha"], DaemonFixture.ProjectNames(retry.Text));
    }
}
