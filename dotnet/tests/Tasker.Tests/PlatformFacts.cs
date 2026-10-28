using Xunit;

namespace Tasker.Tests;

/// <summary>
/// Короткие формы <see cref="PlatformFactAttribute"/> для самых частых случаев: на остальных системах тест явно ПРОПУСКАЕТСЯ
/// (в отчёте — «skipped» с причиной), а не проходит вхолостую. Правило одно, в <see cref="Platform"/>.
/// </summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute() => Skip = Platform.SkipReason(TestPlatform.Windows);
}

/// <summary>Тест опирается на Unix (права файлов, /bin/sh, сигналы): на Windows явно пропускается.</summary>
public sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute() => Skip = Platform.SkipReason(TestPlatform.Unix);
}

/// <summary>Тест, который на Windows пропускается из-за известного бага: <paramref name="reason"/> — номер задачи и суть; на macOS и Linux он идёт.</summary>
public sealed class SkipOnWindowsFactAttribute : FactAttribute
{
    public SkipOnWindowsFactAttribute(string reason) => Skip = OperatingSystem.IsWindows() ? reason : null;
}
