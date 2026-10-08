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
