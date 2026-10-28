using Xunit;

namespace Tasker.Tests;

/// <summary>Системы, на которых выполняется платформенный тест.</summary>
[Flags]
public enum TestPlatform
{
    Windows = 1,
    MacOS = 2,
    Linux = 4,
    Unix = MacOS | Linux
}

/// <summary>
/// Общий для всех тестов способ пометить тест, который осмыслен только на некоторых системах: на остальных он явно ПРОПУСКАЕТСЯ
/// (в отчёте о прогоне — «skipped» с причиной), а не молча проходит. Чистые функции платформенных правил (имена Windows, CRLF,
/// разбор путей) проверяются обычными <c>[Fact]</c> на всех системах, а здесь — только то, что нужно настоящей ОС.
/// </summary>
public static class Platform
{
    public static bool Current(TestPlatform platforms) =>
        (platforms.HasFlag(TestPlatform.Windows) && OperatingSystem.IsWindows())
        || (platforms.HasFlag(TestPlatform.MacOS) && OperatingSystem.IsMacOS())
        || (platforms.HasFlag(TestPlatform.Linux) && OperatingSystem.IsLinux());

    public static string? SkipReason(TestPlatform platforms) =>
        Current(platforms) ? null : $"Runs only on {platforms}; skipped on {(OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : "Linux")}";
}

public sealed class PlatformFactAttribute : FactAttribute
{
    public PlatformFactAttribute(TestPlatform platforms) => Skip = Platform.SkipReason(platforms);
}

public sealed class PlatformTheoryAttribute : TheoryAttribute
{
    public PlatformTheoryAttribute(TestPlatform platforms) => Skip = Platform.SkipReason(platforms);
}
