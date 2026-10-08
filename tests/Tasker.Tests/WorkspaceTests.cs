using Tasker.Storage.Files.Workspaces;
using Xunit;

namespace Tasker.Tests;

/// <summary>Общий код рабочих областей (его используют и десктоп, и CLI).</summary>
public class WorkspaceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tasker-tests-" + Guid.NewGuid().ToString("N"));

    public WorkspaceTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Location_of_tasker_folder_is_its_parent()
    {
        var folder = Path.Combine(_root, "repo");
        Directory.CreateDirectory(Path.Combine(folder, ".tasker"));

        Assert.Equal(WorkspaceLocation.Files(folder).Id, WorkspaceLocation.Files(Path.Combine(folder, ".tasker")).Id);
        Assert.Equal("repo", WorkspaceLocation.Files(Path.Combine(folder, ".tasker")).Name);
    }

    [Fact]
    public void Location_differs_by_kind_and_path()
    {
        var a = WorkspaceLocation.Files(Path.Combine(_root, "a"));
        Assert.NotEqual(a.Id, WorkspaceLocation.Files(Path.Combine(_root, "b")).Id);
        Assert.NotEqual(a.Id, WorkspaceLocation.Sqlite(Path.Combine(_root, "a")).Id);
    }

    [Fact]
    public void Canonical_path_resolves_symlinks_and_dots()
    {
        var real = Directory.CreateDirectory(Path.Combine(_root, "real")).FullName;
        var link = Path.Combine(_root, "link");
        Directory.CreateSymbolicLink(link, real);

        Assert.Equal(CanonicalPath.Of(real), CanonicalPath.Of(link));
        Assert.Equal(CanonicalPath.Of(real), CanonicalPath.Of(Path.Combine(real, "sub", "..")));
        Assert.Equal(CanonicalPath.Of(real), WorkspaceLocation.Files(link).Path);
    }

    [Fact]
    public void Canonical_path_of_missing_file_is_kept()
    {
        var path = Path.Combine(_root, "new", "file.db");

        Assert.EndsWith(Path.Combine("new", "file.db"), CanonicalPath.Of(path));
    }

    [Fact]
    public void Tasker_directory_ignores_service_files_once_and_keeps_user_rules()
    {
        var directory = new Tasker.Storage.Files.TaskerDirectory(_root);
        Directory.CreateDirectory(directory.Root);
        File.WriteAllText(directory.GitIgnore, "my-rule");

        directory.EnsureCreated();
        directory.EnsureCreated();

        var lines = File.ReadAllLines(directory.GitIgnore);
        Assert.Contains("my-rule", lines);
        Assert.Single(lines, x => x == "/.cache/");
        Assert.Single(lines, x => x == "*.tmp");
    }
}
