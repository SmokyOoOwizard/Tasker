using Tasker.Cli;
using Tasker.Daemon.Services;
using Xunit;

namespace Tasker.Tests;

/// <summary>Git-хуки <c>tasker hooks</c> на настоящем git: установка, чужие хуки и обновление кэша после команд git.</summary>
public class GitHooksTests : IDisposable
{
    private readonly IsolatedHome _home = new();
    private readonly GitRepo _repo = new();

    public void Dispose()
    {
        _repo.Dispose();
        _home.Dispose();
    }

    private static Task<CliResult> Tasker(params string[] args) => TaskerProcess.Run(args);

    /// <summary>Папка с проектом A на main и веткой b с проектом B (оба закоммичены).</summary>
    private async Task<string> RepoWithTwoBranches(string relative = "")
    {
        var workspace = _repo.Folder(relative);
        Assert.Equal(0, (await Tasker("project", "create", "A", "-w", workspace)).Code);
        _repo.Commit("A");
        _repo.Git("checkout", "-q", "-b", "b");
        Assert.Equal(0, (await Tasker("project", "create", "B", "-w", workspace)).Code);
        _repo.Commit("B");
        return workspace;
    }

    private static bool IsExecutable(string path) =>
        OperatingSystem.IsWindows() || (File.GetUnixFileMode(path) & UnixFileMode.UserExecute) != 0;

    // ---- установка ----

    [Fact]
    public async Task Install_creates_the_hooks_that_run_sync_for_the_workspace()
    {
        var workspace = _repo.Folder("");
        await Tasker("project", "create", "A", "-w", workspace);

        var result = await Tasker("hooks", "install", "-w", workspace);

        Assert.Equal(0, result.Code);
        foreach (var name in new[] { "post-merge", "post-checkout", "post-rewrite" })
        {
            var text = await File.ReadAllTextAsync(_repo.Hook(name));
            Assert.StartsWith("#!/bin/sh\n", text);
            Assert.Contains("sync --quiet --workspace \"$(git rev-parse --show-toplevel)\"", text);
            Assert.Contains("|| true", text);
            Assert.True(IsExecutable(_repo.Hook(name)), $"{name} must be executable");
        }
    }

    [Fact]
    public async Task Install_twice_does_not_duplicate_the_block()
    {
        var workspace = _repo.Folder("");
        await Tasker("project", "create", "A", "-w", workspace);

        await Tasker("hooks", "install", "-w", workspace);
        await Tasker("hooks", "install", "-w", workspace);

        var text = await File.ReadAllTextAsync(_repo.Hook("post-merge"));
        Assert.Equal(1, text.Split("# >>> tasker").Length - 1);
    }

    [Fact]
    public async Task Existing_hooks_keep_their_commands_and_are_restored_on_uninstall()
    {
        var workspace = _repo.Folder("");
        await Tasker("project", "create", "A", "-w", workspace);
        Directory.CreateDirectory(_repo.Hooks);
        const string mine = "#!/bin/bash\necho mine\nexit 3\n";
        await File.WriteAllTextAsync(_repo.Hook("post-merge"), mine);

        await Tasker("hooks", "install", "-w", workspace);

        var installed = (await File.ReadAllTextAsync(_repo.Hook("post-merge"))).Split('\n');
        Assert.Equal("#!/bin/bash", installed[0]);
        Assert.StartsWith("# >>> tasker", installed[1]);
        Assert.Contains("echo mine", installed);
        Assert.True(Array.IndexOf(installed, "echo mine") > Array.IndexOf(installed, "# <<< tasker <<<"),
            "the Tasker block must run before the user's commands: their 'exit' would cancel it");
        Assert.Contains("post-merge     installed (has other commands)", (await Tasker("hooks", "status", "-w", workspace)).Out);

        var removed = await Tasker("hooks", "uninstall", "-w", workspace);
        Assert.Equal(0, removed.Code);
        Assert.Equal(mine, await File.ReadAllTextAsync(_repo.Hook("post-merge")));
        // Хуки, созданные нами целиком, удаляются.
        Assert.False(File.Exists(_repo.Hook("post-checkout")));
        Assert.False(File.Exists(_repo.Hook("post-rewrite")));
    }

    [Fact]
    public async Task A_hook_without_a_shebang_gets_one()
    {
        var workspace = _repo.Folder("");
        await Tasker("project", "create", "A", "-w", workspace);
        Directory.CreateDirectory(_repo.Hooks);
        await File.WriteAllTextAsync(_repo.Hook("post-checkout"), "echo mine\n");

        await Tasker("hooks", "install", "-w", workspace);

        var text = await File.ReadAllTextAsync(_repo.Hook("post-checkout"));
        Assert.StartsWith("#!/bin/sh\n# >>> tasker", text);
        Assert.EndsWith("echo mine\n", text);
    }

    [Fact]
    public async Task Status_and_uninstall_of_a_repository_without_hooks()
    {
        var workspace = _repo.Folder("");
        await Tasker("project", "create", "A", "-w", workspace);

        Assert.Contains("post-merge     not installed", (await Tasker("hooks", "status", "-w", workspace)).Out);
        var removed = await Tasker("hooks", "uninstall", "-w", workspace);
        Assert.Equal(0, removed.Code);
        Assert.Equal("No Tasker hooks found", removed.Out.Trim());
    }

    [Fact]
    public async Task Hooks_go_where_core_hooksPath_points()
    {
        var workspace = _repo.Folder("sub");
        await Tasker("project", "create", "A", "-w", workspace);
        _repo.Git("config", "core.hooksPath", ".githooks");

        var result = await Tasker("hooks", "install", "-w", workspace);

        Assert.Equal(0, result.Code);
        Assert.True(File.Exists(Path.Combine(_repo.Root, ".githooks", "post-merge")));
        Assert.False(Directory.Exists(_repo.Hooks) && File.Exists(_repo.Hook("post-merge")));
    }

    [Fact]
    public async Task Install_outside_a_git_repository_or_a_workspace_is_a_clear_error()
    {
        var folder = Path.Combine(Path.GetTempPath(), "tasker-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(folder, ".tasker"));
        try
        {
            var noRepo = await Tasker("hooks", "install", "-w", folder);
            Assert.Equal(1, noRepo.Code);
            Assert.Contains("is not in a git repository", noRepo.Err);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }

        var noWorkspace = await Tasker("hooks", "install", "-w", _repo.Folder(""));
        Assert.Equal(1, noWorkspace.Code);
        Assert.Contains("is not a Tasker workspace", noWorkspace.Err);
    }

    [Fact]
    public void Block_quotes_paths_for_the_shell()
    {
        var block = GitHooks.Block(new HookTarget("/repo", "my dir/it's", "/repo/.git/hooks"));

        Assert.Contains("\"$(git rev-parse --show-toplevel)\"/'my dir/it'\\''s'", block);
    }

    // ---- хуки в деле ----

    [Fact]
    public async Task Checkout_updates_the_cache_right_away_without_any_tasker_process_running()
    {
        if (OperatingSystem.IsWindows())
            return;

        var workspace = await RepoWithTwoBranches();
        Assert.Equal(2, GitRepo.ProjectsInCache(workspace));
        await Tasker("hooks", "install", "-w", workspace);

        _repo.Git("checkout", "-q", "main");
        Assert.Equal(1, GitRepo.ProjectsInCache(workspace));

        _repo.Git("checkout", "-q", "b");
        Assert.Equal(2, GitRepo.ProjectsInCache(workspace));
    }

    [Fact]
    public async Task Without_the_hooks_nothing_updates_the_cache_until_someone_opens_the_workspace()
    {
        if (OperatingSystem.IsWindows())
            return;

        var workspace = await RepoWithTwoBranches();

        _repo.Git("checkout", "-q", "main");

        // Контроль: без хуков кэш отстаёт — проект B из ветки b ещё в нём. Следующий процесс Tasker его догонит.
        Assert.Equal(2, GitRepo.ProjectsInCache(workspace));
        Assert.Equal("A", (await Tasker("project", "list", "-w", workspace)).Out.Trim().Split("  ")[1]);
        Assert.Equal(1, GitRepo.ProjectsInCache(workspace));
    }

    [Fact]
    public async Task Merge_updates_the_cache()
    {
        if (OperatingSystem.IsWindows())
            return;

        var workspace = await RepoWithTwoBranches();
        _repo.Git("checkout", "-q", "main");
        await Tasker("hooks", "install", "-w", workspace);
        await Tasker("sync", "-w", workspace);
        Assert.Equal(1, GitRepo.ProjectsInCache(workspace));

        _repo.Git("merge", "-q", "b");

        Assert.Equal(2, GitRepo.ProjectsInCache(workspace));
    }

    [Fact]
    public async Task Rebase_updates_the_cache()
    {
        if (OperatingSystem.IsWindows())
            return;

        var workspace = await RepoWithTwoBranches();
        // На main появился ещё один проект — ветка b перебазируется на него.
        _repo.Git("checkout", "-q", "main");
        await Tasker("project", "create", "C", "-w", workspace);
        _repo.Commit("C");
        _repo.Git("checkout", "-q", "b");
        await Tasker("hooks", "install", "-w", workspace);
        await Tasker("sync", "-w", workspace);
        Assert.Equal(2, GitRepo.ProjectsInCache(workspace));

        _repo.Git("rebase", "-q", "main");

        Assert.Equal(3, GitRepo.ProjectsInCache(workspace));
    }

    [Fact]
    public async Task A_workspace_in_a_subfolder_with_a_space_in_its_name_works()
    {
        if (OperatingSystem.IsWindows())
            return;

        var workspace = await RepoWithTwoBranches("tools/my ws");
        await Tasker("hooks", "install", "-w", workspace);

        _repo.Git("checkout", "-q", "main");

        Assert.Equal(1, GitRepo.ProjectsInCache(workspace));
    }

    [Fact]
    public async Task A_new_worktree_syncs_its_own_workspace()
    {
        if (OperatingSystem.IsWindows())
            return;

        var workspace = await RepoWithTwoBranches();
        await Tasker("hooks", "install", "-w", workspace);
        var other = Path.Combine(_repo.Root + "-wt");
        try
        {
            _repo.Git("worktree", "add", "-q", other, "main");

            // Хук лежит в общем .git, а рабочая папка определяется в момент запуска — это папка нового дерева.
            Assert.Equal(1, GitRepo.ProjectsInCache(other));
        }
        finally
        {
            if (Directory.Exists(other))
                Directory.Delete(other, recursive: true);
        }
    }

    [Fact]
    public async Task Sync_after_a_conflicting_merge_lists_the_conflicted_file()
    {
        var workspace = _repo.Folder("");
        await Tasker("project", "create", "A", "-w", workspace);
        _repo.Commit("A");
        var project = Directory.GetFiles(Path.Combine(workspace, ".tasker", "projects"), "project.yaml", SearchOption.AllDirectories).Single();
        _repo.Git("checkout", "-q", "-b", "b");
        await File.WriteAllTextAsync(project, (await File.ReadAllTextAsync(project)).Replace("name: A", "name: FromB"));
        _repo.Commit("b renames A");
        _repo.Git("checkout", "-q", "main");
        await File.WriteAllTextAsync(project, (await File.ReadAllTextAsync(project)).Replace("name: A", "name: FromMain"));
        _repo.Commit("main renames A");

        // Слияние с конфликтом оставляет в файле маркеры git.
        Assert.Throws<InvalidOperationException>(() => _repo.Git("merge", "b"));
        Assert.Contains("<<<<<<<", await File.ReadAllTextAsync(project));

        // Даже в тихом режиме (так зовёт хук) о нечитаемых файлах сообщается.
        var sync = await Tasker("sync", "--quiet", "-w", workspace);
        Assert.Equal(0, sync.Code);
        Assert.Contains("1 file(s) cannot be read", sync.Out);
        Assert.Contains("Unresolved git merge conflict", sync.Out);
    }

    [Fact]
    public async Task A_missing_tasker_never_breaks_git()
    {
        if (OperatingSystem.IsWindows())
            return;

        var workspace = await RepoWithTwoBranches();
        await Tasker("hooks", "install", "-w", workspace);
        // Хук вызывает тот tasker, который его поставил (tasker.dll или сам бинарник): подменяем его путь на несуществующий.
        var (file, arguments) = TaskerProcess.Command();
        var program = arguments.Length > 0 ? arguments[0] : file;
        foreach (var name in new[] { "post-merge", "post-checkout", "post-rewrite" })
        {
            var path = _repo.Hook(name);
            var text = await File.ReadAllTextAsync(path);
            Assert.Contains(program, text);
            await File.WriteAllTextAsync(path, text.Replace(program, program + "-gone"));
        }

        var output = _repo.Git("checkout", "main");

        Assert.DoesNotContain("Unhandled", output);
        Assert.Equal("main", _repo.Git("rev-parse", "--abbrev-ref", "HEAD").Trim());
    }

    [Fact]
    public async Task A_hook_in_a_folder_without_a_workspace_is_silent_and_succeeds()
    {
        if (OperatingSystem.IsWindows())
            return;

        var workspace = await RepoWithTwoBranches("sub");
        await Tasker("hooks", "install", "-w", workspace);
        Directory.Delete(Path.Combine(workspace, ".tasker"), recursive: true);

        var info = new System.Diagnostics.ProcessStartInfo("sh") { WorkingDirectory = _repo.Root, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add(_repo.Hook("post-checkout"));
        using var process = System.Diagnostics.Process.Start(info)!;
        var output = await process.StandardOutput.ReadToEndAsync() + await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.Equal(0, process.ExitCode);
        Assert.Empty(output);
    }
}
