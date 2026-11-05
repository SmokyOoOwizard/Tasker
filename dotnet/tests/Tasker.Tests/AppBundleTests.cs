using Tasker.Daemon;
using Tasker.Daemon.Services;
using Xunit;

namespace Tasker.Tests;

public class AppBundleTests : IDisposable
{
    private const string Helper = "/Applications/Tasker.app/Contents/Helpers/tasker-mcpd";

    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "tasker-tests-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData("/Applications/Tasker.app/Contents/Helpers/tasker", true, false)]
    [InlineData("/Volumes/Tasker/Tasker.app/Contents/Helpers/tasker", true, true)]
    [InlineData("/private/var/folders/x/AppTranslocation/y/d/Tasker.app/Contents/Helpers/tasker", true, true)]
    [InlineData("/Volumes/Disk/bin/tasker", false, false)]
    [InlineData("/Users/u/.local/share/tasker/app/tasker", false, false)]
    public void A_location_is_temporary_only_for_a_bundle_on_a_disk_image_or_translocated(string path, bool inBundle, bool temporary)
    {
        Assert.Equal(inBundle, Launcher.IsInAppBundle(path));
        Assert.Equal(temporary, Launcher.IsTemporaryLocation(path));
    }

    private string Console()
    {
        var helpers = Directory.CreateDirectory(Path.Combine(_root, "Tasker.app", "Contents", "Helpers")).FullName;
        var console = Path.Combine(helpers, "tasker");
        File.WriteAllText(console, "");
        return console;
    }

    [UnixFact]
    public void Scripts_record_the_command_link_of_a_console_from_the_bundle()
    {
        var console = Console();
        var home = Path.Combine(_root, "home");
        var bin = Directory.CreateDirectory(Path.Combine(home, ".local", "bin")).FullName;
        var link = Path.Combine(bin, "tasker");
        File.CreateSymbolicLink(link, console);

        Assert.Equal(link, Launcher.ScriptCommand((console, []), home).File);
        Assert.Equal(link, Launcher.ScriptCommand((link, []), home).File);
    }

    [UnixFact]
    public void Scripts_record_the_program_itself_without_a_link_or_outside_a_bundle()
    {
        var console = Console();
        var home = Path.Combine(_root, "home");
        var bin = Directory.CreateDirectory(Path.Combine(home, ".local", "bin")).FullName;

        Assert.Equal(console, Launcher.ScriptCommand((console, []), home).File);
        File.CreateSymbolicLink(Path.Combine(bin, "tasker"), "/somewhere/else");
        Assert.Equal(console, Launcher.ScriptCommand((console, []), home).File);

        var plain = Path.Combine(_root, "tasker");
        File.WriteAllText(plain, "");
        Assert.Equal(plain, Launcher.ScriptCommand((plain, []), home).File);
    }

    [Fact]
    public void The_program_counts_as_removed_only_after_the_grace_period_without_it()
    {
        var exists = false;
        var watch = new ProgramWatch(_ => exists);
        var start = DateTime.UtcNow;

        Assert.False(watch.IsGone(Helper, start));
        Assert.False(watch.IsGone(Helper, start + ProgramWatch.Grace - TimeSpan.FromSeconds(1)));
        Assert.True(watch.IsGone(Helper, start + ProgramWatch.Grace));
    }

    [Fact]
    public void A_program_that_comes_back_restarts_the_grace_period()
    {
        var exists = false;
        var watch = new ProgramWatch(_ => exists);
        var start = DateTime.UtcNow;

        Assert.False(watch.IsGone(Helper, start));
        exists = true;
        Assert.False(watch.IsGone(Helper, start + TimeSpan.FromSeconds(30)));
        exists = false;
        Assert.False(watch.IsGone(Helper, start + ProgramWatch.Grace));
        Assert.True(watch.IsGone(Helper, start + ProgramWatch.Grace * 2));
    }

    [Fact]
    public void A_program_outside_a_bundle_is_never_watched()
    {
        var watch = new ProgramWatch(_ => false);
        var start = DateTime.UtcNow;

        Assert.False(watch.IsGone("/Users/u/.local/share/tasker/app/tasker-mcpd", start));
        Assert.False(watch.IsGone("/Users/u/.local/share/tasker/app/tasker-mcpd", start + ProgramWatch.Grace * 2));
    }

    [UnixFact]
    public void The_console_and_the_app_use_one_service_label()
    {
        Assert.Equal("com.technotronika.tasker.daemon", LaunchdService.DefaultLabel);
    }
}
