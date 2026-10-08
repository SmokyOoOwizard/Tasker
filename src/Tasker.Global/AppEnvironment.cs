using System.Diagnostics;

namespace Tasker.Global;

/// <summary>
/// Переменные окружения, которые Tasker читает у себя (<c>TASKER_HOME</c>, <c>TASKER_PROJECT</c>, <c>TASKER_SERVICE_*</c>).
/// Значение можно переопределить только для текущего потока выполнения (<see cref="Override"/>), не трогая окружение процесса:
/// так тесты с разным окружением идут параллельно. Переопределения получают и дочерние процессы (<see cref="Apply"/>).
/// </summary>
public static class AppEnvironment
{
    private static readonly AsyncLocal<IReadOnlyDictionary<string, string?>?> Overrides = new();

    public static string? Get(string name) =>
        Overrides.Value is { } overrides && overrides.TryGetValue(name, out var value)
            ? value
            : Environment.GetEnvironmentVariable(name);

    /// <summary>Переопределяет переменную (<c>null</c> — считать не заданной), пока не вызвали <see cref="IDisposable.Dispose"/>.</summary>
    public static IDisposable Override(string name, string? value)
    {
        var previous = Overrides.Value;
        Overrides.Value = new Dictionary<string, string?>(previous ?? new Dictionary<string, string?>()) { [name] = value };
        return new Restore(previous);
    }

    /// <summary>Передаёт переопределения дочернему процессу, который иначе унаследовал бы окружение родителя.</summary>
    public static void Apply(ProcessStartInfo info)
    {
        if (Overrides.Value is not { } overrides)
            return;

        foreach (var (name, value) in overrides)
        {
            if (value == null)
                info.Environment.Remove(name);
            else
                info.Environment[name] = value;
        }
    }

    private sealed class Restore(IReadOnlyDictionary<string, string?>? previous) : IDisposable
    {
        public void Dispose() => Overrides.Value = previous;
    }
}
