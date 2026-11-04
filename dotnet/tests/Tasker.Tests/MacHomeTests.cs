using System.Diagnostics;
using Xunit;

namespace Tasker.Tests;

public sealed class MacHomeTests
{
    [PlatformFact(TestPlatform.MacOS)]
    public async Task Data_directory_and_the_daemon_follow_the_home_variable()
    {
        var home = Directory.CreateTempSubdirectory("tasker-home-").FullName;
        try
        {
            var info = TaskerProcess.StartInfo(home, "mcp", "status");
            info.Environment["HOME"] = home;
            info.Environment.Remove("TASKER_HOME");
            using var process = Process.Start(info)!;
            var error = process.StandardError.ReadToEndAsync();
            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            Assert.DoesNotContain("Unhandled exception", await error);
            Assert.True(Directory.Exists(Path.Combine(home, "Library", "Application Support", "Tasker")),
                "The data directory must be under $HOME/Library/Application Support");
            Assert.DoesNotContain("pid ", output);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }
}
