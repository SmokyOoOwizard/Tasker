using Tasker.Global;

namespace Tasker.Daemon.Services;

/// <summary>
/// Linux: пользовательская служба systemd <c>~/.config/systemd/user/tasker-mcp.service</c>. WantedBy=default.target —
/// стартует при входе (чтобы без входа — <c>loginctl enable-linger</c>), Restart=always — перезапуск при падении.
/// <c>TASKER_SERVICE_DIR</c> и <c>TASKER_SERVICE_LABEL</c> переопределяют каталог и имя.
/// </summary>
public sealed class SystemdService(IProcessRunner runner, string? directory = null, string? unit = null) : IServiceManager
{
    private readonly string _directory = directory
        ?? AppEnvironment.Get("TASKER_SERVICE_DIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "systemd", "user");

    public string Unit { get; } = unit ?? AppEnvironment.Get("TASKER_SERVICE_LABEL") ?? "tasker-mcp.service";

    public string Name => "systemd";

    public string UnitPath => Path.Combine(_directory, Unit);

    public bool IsEnabled => File.Exists(UnitPath);

    public async Task Enable()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(UnitPath, UnitFile());
        try
        {
            Check(await runner.Run("systemctl", "--user", "daemon-reload"), "systemctl daemon-reload");
            Check(await runner.Run("systemctl", "--user", "enable", "--now", Unit), "systemctl enable");
        }
        catch (ServiceException)
        {
            // Без сеанса пользователя (su, sudo -u, ssh без logind) systemctl --user не достучится до шины: не оставляем описание,
            // которое выглядело бы как «автозапуск включён» (IsEnabled — это файл).
            File.Delete(UnitPath);
            throw;
        }
    }

    public async Task Disable()
    {
        await runner.Run("systemctl", "--user", "disable", "--now", Unit);
        if (File.Exists(UnitPath))
            File.Delete(UnitPath);
        await runner.Run("systemctl", "--user", "daemon-reload");
    }

    public async Task Start()
    {
        if (!IsEnabled)
            throw new ServiceException("Autostart is not enabled: run 'tasker mcp autostart enable'");

        Check(await runner.Run("systemctl", "--user", "start", Unit), "systemctl start");
    }

    public async Task Stop() => Check(await runner.Run("systemctl", "--user", "stop", Unit), "systemctl stop");

    public async Task<bool> IsLoaded() => (await runner.Run("systemctl", "--user", "is-active", "--quiet", Unit)).Success;

    internal string UnitFile()
    {
        var command = string.Join(' ', ServiceManagers.DaemonCommand().Select(Quote));
        var environment = string.Concat(ServiceManagers.Environment().Select(x => $"Environment={Quote($"{x.Key}={x.Value}")}\n"));
        return $"""
            [Unit]
            Description=Tasker MCP server

            [Service]
            ExecStart={command}
            {environment}Restart=always
            RestartSec=5

            [Install]
            WantedBy=default.target

            """;
    }

    // Аргументы с пробелами и спецсимволами — в кавычках по правилам systemd.
    private static string Quote(string value) =>
        value.Any(c => char.IsWhiteSpace(c) || c is '"' or '\'' or '\\' or '$' or '%')
            ? "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("%", "%%").Replace("$", "$$") + "\""
            : value;

    private static void Check(ProcessResult result, string what)
    {
        if (result.Success)
            return;

        var text = (result.Error + result.Output).Trim();
        var hint = text.Contains("Failed to connect to bus", StringComparison.Ordinal)
            ? " (systemctl --user needs a login session of the user: log in directly or over ssh with a session, or run 'loginctl enable-linger' and set XDG_RUNTIME_DIR=/run/user/<uid>)"
            : "";
        throw new ServiceException($"{what} failed ({result.ExitCode}): {text}{hint}");
    }
}
