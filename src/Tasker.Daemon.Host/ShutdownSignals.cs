using System.Runtime.InteropServices;

namespace Tasker.Daemon.Host;

/// <summary>Все способы остановить демон снаружи, какие есть на этой системе: сигналы (SIGTERM, SIGINT; на Windows ещё Ctrl+Break) и именованное событие (Windows).</summary>
internal sealed class ShutdownSignals : IDisposable
{
    private readonly List<IDisposable> _registrations = [];

    public ShutdownSignals(Action<string> stop)
    {
        Add(PosixSignal.SIGTERM, "SIGTERM", stop);
        Add(PosixSignal.SIGINT, "SIGINT", stop);
        if (OperatingSystem.IsWindows())
            Add(PosixSignal.SIGQUIT, "Ctrl+Break", stop);

        if (ShutdownSignal.Listen(() => stop("the stop event of 'tasker mcp stop'")) is { } listener)
            _registrations.Add(listener);
    }

    private void Add(PosixSignal signal, string name, Action<string> stop)
    {
        try
        {
            _registrations.Add(PosixSignalRegistration.Create(signal, context =>
            {
                context.Cancel = true;
                stop(name);
            }));
        }
        catch (PlatformNotSupportedException)
        {
            // Этого сигнала на системе нет: остаются остальные способы.
        }
    }

    public void Dispose()
    {
        foreach (var registration in _registrations)
            registration.Dispose();
    }
}
