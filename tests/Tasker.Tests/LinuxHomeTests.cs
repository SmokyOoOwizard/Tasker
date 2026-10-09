using System.Diagnostics;
using Xunit;

namespace Tasker.Tests;

/// <summary>
/// Linux (TSK-111): на свежей учётной записи нет ни <c>~/.local/share</c>, ни <c>TASKER_HOME</c>. .NET на Unix для
/// <c>GetFolderPath(LocalApplicationData)</c> по умолчанию возвращает пустую строку, если каталога нет, и данные Tasker
/// оказывались в <c>/Tasker</c> («Access to the path '/Tasker' is denied»). Каталог данных должен вычисляться и без существующей папки.
/// </summary>
public sealed class LinuxHomeTests
{
    [PlatformFact(TestPlatform.Linux)]
    public async Task Data_directory_is_created_under_a_home_that_has_no_local_share_yet()
    {
        var home = Directory.CreateTempSubdirectory("tasker-home-").FullName;
        try
        {
            var info = TaskerProcess.StartInfo(home, "mcp", "status");
            info.Environment["HOME"] = home;
            info.Environment.Remove("TASKER_HOME");
            info.Environment.Remove("XDG_DATA_HOME");
            using var process = Process.Start(info)!;
            var error = process.StandardError.ReadToEndAsync();
            await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            Assert.DoesNotContain("Unhandled exception", await error);
            Assert.True(Directory.Exists(Path.Combine(home, ".local", "share", "Tasker")), "The data directory must be created under ~/.local/share");
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }
}
