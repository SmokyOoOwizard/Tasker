namespace Tasker.Tests;

/// <summary>
/// Отдельный каталог данных Tasker (<c>TASKER_HOME</c>) на время теста: настройки и состояние демона
/// не лезут в настоящие. Переменная переопределена только для этого теста (<see cref="Tasker.Global.AppEnvironment"/>),
/// её получают и запускаемые тестом процессы <c>tasker</c>.
/// </summary>
public sealed class IsolatedHome : IDisposable
{
    private readonly IDisposable _override;
    private readonly List<IDisposable> _daemonOverrides = [];

    public IsolatedHome()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tasker-home-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
        _override = Tasker.Global.AppEnvironment.Override("TASKER_HOME", Path);
    }

    public string Path { get; }

    public string SettingsFile => System.IO.Path.Combine(Path, "settings.json");

    public Tasker.Global.SettingsStore Store => new(Path);

    /// <summary>
    /// Отделяет демон теста от настоящего: свободный порт в настройках и своё описание службы (иначе <c>mcp start</c>
    /// видит демон на порту по умолчанию или запускает настоящую службу автозапуска). Вызывать до запуска процессов tasker.
    /// </summary>
    public void IsolateDaemon()
    {
        var serviceDir = Directory.CreateDirectory(System.IO.Path.Combine(Path, "service")).FullName;
        _daemonOverrides.Add(Tasker.Global.AppEnvironment.Override("TASKER_SERVICE_DIR", serviceDir));
        _daemonOverrides.Add(Tasker.Global.AppEnvironment.Override("TASKER_SERVICE_LABEL", "com.tasker.tests-" + Guid.NewGuid().ToString("N")[..8]));
        Store.SetPort(DaemonFixture.FreePort()).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        for (var i = _daemonOverrides.Count - 1; i >= 0; i--)
            _daemonOverrides[i].Dispose();
        _override.Dispose();
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
