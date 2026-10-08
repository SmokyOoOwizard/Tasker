using System.Diagnostics;
using Tasker.Global;

namespace Tasker.Daemon;

/// <summary>
/// <c>tasker mcp run</c>: запускает программу демона (<c>tasker-mcpd</c>) в этом терминале и ждёт её. Ввод и вывод — общие,
/// код выхода — её. Остановка (Ctrl+C, SIGTERM) передаётся демону, чтобы он закрыл области штатно.
/// </summary>
public static class DaemonHost
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(20);

    public static async Task<int> Run(bool detached, CancellationToken ct)
    {
        var (file, arguments) = Launcher.DaemonCommand();
        var info = new ProcessStartInfo(file) { UseShellExecute = false };
        AppEnvironment.Apply(info);
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);
        if (detached)
            info.ArgumentList.Add("--detached");

        using var process = Process.Start(info) ?? throw new DaemonStartException("Cannot start the MCP server program");

        // Ctrl+C, SIGTERM и закрытие терминала System.CommandLine превращает в отмену ct (и держит процесс, пока команда не завершится).
        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            await Stop(process);
        }

        return process.ExitCode;
    }

    // Штатно: SIGTERM (Windows — именованное событие или запрос /daemon/stop); демон закрывает области и убирает свои файлы.
    // Не успел — снимаем процесс вместе с рабочими.
    private static async Task Stop(Process process)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                // Сигналов нет: просим демон остановиться событием, затем запросом; Kill — только если он не вышел за срок.
                if (!ShutdownSignal.Raise())
                    await DaemonClient.RequestStop();
            }
            else
            {
                using var kill = Process.Start("kill", ["-TERM", process.Id.ToString()]);
                if (kill != null)
                    await kill.WaitForExitAsync();
            }

            using var timeout = new CancellationTokenSource(StopTimeout);
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
        catch (InvalidOperationException)
        {
            // Уже завершился.
        }
    }
}
