using System.Security.Cryptography;
using System.Text;
using Tasker.Cli;
using Tasker.Core.IO;
using Tasker.Core.Projects;
using Tasker.Storage.Files;
using Tasker.Storage.Files.Storages;
using Tasker.Storage.Files.Workspaces;
using Xunit;

namespace Tasker.Tests;

/// <summary>
/// Файловая система и Windows (TSK-116): правила Windows — чистые функции, их проверяем на любой системе; то, что требует настоящей
/// Windows (занятые файлы, регистр путей), — <see cref="PlatformFactAttribute"/>, на других системах явно пропускается.
/// </summary>
public class WindowsFileSystemTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tasker-tests-" + Guid.NewGuid().ToString("N"));

    public WindowsFileSystemTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    // ---- имена файлов Windows ----

    [Theory]
    [InlineData("CON")]
    [InlineData("con")]
    [InlineData("Prn")]
    [InlineData("AUX")]
    [InlineData("nul")]
    [InlineData("COM1")]
    [InlineData("com9")]
    [InlineData("LPT1")]
    [InlineData("lpt9")]
    [InlineData("COM\u00B2")]
    [InlineData("CONIN$")]
    [InlineData("con.yaml")] // с расширением имя устройства тоже зарезервировано
    [InlineData("NUL.tar.gz")]
    [InlineData("aux .txt")] // пробел перед точкой отбрасывается
    [InlineData("con.")]
    public void Windows_reserved_names_are_recognized(string name)
    {
        Assert.True(WindowsNames.IsReserved(name));
        Assert.NotNull(WindowsNames.Problem(name));
    }

    [Theory]
    [InlineData("console.yaml")]
    [InlineData("con-3f2a9c1e.yaml")] // суффикс id делает имя не зарезервированным
    [InlineData("com10.yaml")]
    [InlineData("lpt0")]
    [InlineData("nul-3f2a9c1e.yaml")]
    [InlineData("исправить-вход-3f2a9c1e.yaml")]
    public void Ordinary_names_are_not_reserved(string name) => Assert.Null(WindowsNames.Problem(name));

    [Theory]
    [InlineData("a<b")]
    [InlineData("a>b")]
    [InlineData("a:b")]
    [InlineData("a\"b")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("a|b")]
    [InlineData("a?b")]
    [InlineData("a*b")]
    [InlineData("a\tb")]
    [InlineData("a\u0001b")]
    [InlineData("name.")]
    [InlineData("name ")]
    [InlineData("")]
    public void Names_Windows_does_not_allow_are_reported(string name) => Assert.NotNull(WindowsNames.Problem(name));

    public static IEnumerable<object[]> HostileTitles() =>
    [
        ["CON"], ["nul"], ["COM1"], ["aux.txt"], ["con."], ["LPT9 "],
        ["a<b>c:d\"e/f\\g|h?i*j"], ["..."], ["   "], [" . "], ["\u0001\u0002"], ["\t\n"], [""],
        ["Привет, мир!!!"], ["Исправить вход: <ошибка> (v2)?"], ["C:\\Users\\me\\file.txt"], ["../../etc/passwd"],
        ["Cafe\u0301"], // NFD
        ["Café"], ["ß Straße İstanbul"], ["日本語のタスク"], ["emoji 😀 task"],
        [new string('я', 300)], [new string('a', 61)], [string.Join(" ", Enumerable.Repeat("слово", 80))],
        ["a\ud800b"] // одиночный суррогат
    ];

    [Theory]
    [MemberData(nameof(HostileTitles))]
    public void Entity_file_names_are_valid_on_every_system_and_short(string title)
    {
        foreach (var folder in EntityFolders.All)
        {
            var name = folder.FileName(title, Guid.NewGuid());

            Assert.Null(WindowsNames.Problem(name));
            Assert.Equal(name, name.Normalize(System.Text.NormalizationForm.FormC));
            Assert.True(name.Length <= EntityFileNames.MaxSlugLength + 1 + EntityFileNames.IdLength + YamlFile.Extension.Length, name);
            Assert.True(EntityFileNames.TryParse(name, out _, out var prefix) && prefix is { Length: EntityFileNames.IdLength }, name);
        }
    }

    [Fact]
    public void Titles_that_differ_only_in_case_give_the_same_slug_and_different_files_by_id()
    {
        // Регистронезависимая файловая система: «Баг» и «баг» не должны столкнуться — различает суффикс id.
        var first = EntityFileNames.FileName("Баг", Guid.NewGuid(), "task");
        var second = EntityFileNames.FileName("баг", Guid.NewGuid(), "task");

        Assert.NotEqual(first, second, StringComparer.OrdinalIgnoreCase);
        Assert.StartsWith("баг-", first);
        Assert.StartsWith("баг-", second);
    }

    [Fact]
    public void A_file_name_in_decomposed_unicode_is_still_the_current_name()
    {
        var id = Guid.NewGuid();
        var nfc = EntityFileNames.FileName("Café", id, "task");
        var nfd = nfc.Normalize(System.Text.NormalizationForm.FormD);

        Assert.NotEqual(nfc, nfd);
        Assert.True(EntityFileNames.IsCurrent(nfd, "Café", id, "task"));
    }

    // ---- длина пути ----

    [Fact]
    public void The_longest_path_budget_matches_the_real_longest_file()
    {
        var workspace = Path.Combine(_root, "w");
        var directory = new TaskerDirectory(workspace);
        var project = directory.Project(Guid.NewGuid());
        var longest = EntityFolders.All
            .Select(folder => project.EntityFile(folder, Guid.NewGuid(), new string('я', 200)) + ".tmp")
            .Max(x => Path.GetRelativePath(directory.Root, x).Length + TaskerDirectory.Name.Length + 1);

        Assert.Equal(longest, PathBudget.LongestRelativePath);
        Assert.True(PathBudget.MaxWorkspaceRootLength > 80, "an ordinary path like C:\\Users\\name\\source\\project must fit");
    }

    [Fact]
    public void A_workspace_fits_the_windows_limit_when_its_path_is_short_enough()
    {
        var fits = new string('a', PathBudget.MaxWorkspaceRootLength);
        Assert.False(PathBudget.ExceedsLegacyLimit(fits));
        Assert.False(PathBudget.ExceedsLegacyLimit(fits + "\\"));
        Assert.True(PathBudget.ExceedsLegacyLimit(fits + "a"));
        Assert.True(fits.Length + 1 + PathBudget.LongestRelativePath < PathBudget.WindowsMaxPath);
    }

    // ---- пути ----

    [Theory]
    [InlineData(@"\\?\C:\Users\me\work", @"C:\Users\me\work")]
    [InlineData(@"\\?\UNC\server\share\dir", @"\\server\share\dir")]
    [InlineData(@"\\?\unc\server\share", @"\\server\share")]
    [InlineData(@"C:\Users\me", @"C:\Users\me")]
    [InlineData(@"\\server\share\dir", @"\\server\share\dir")]
    [InlineData(@"\\?\Volume{1234}\dir", @"\\?\Volume{1234}\dir")]
    [InlineData("/home/me/work", "/home/me/work")]
    public void The_extended_prefix_is_stripped_from_windows_paths(string path, string expected) =>
        Assert.Equal(expected, CanonicalPath.StripExtendedPrefix(path));

    [Theory]
    [InlineData(@"c:\", @"C:\")]
    [InlineData(@"C:\", @"C:\")]
    [InlineData(@"z:/", @"Z:/")]
    [InlineData(@"\\server\share\", @"\\server\share\")]
    [InlineData("/", "/")]
    public void The_drive_letter_is_upper_case_in_the_canonical_root(string root, string expected) =>
        Assert.Equal(expected, CanonicalPath.NormalizeRoot(root));

    [Fact]
    public void Paths_inside_a_folder_are_told_by_the_separator_and_by_the_case_rule_of_the_system()
    {
        Assert.True(PathRules.IsInside(@"C:\w\.tasker\.cache", @"C:\w\.tasker\.cache\index.db", StringComparison.Ordinal));
        Assert.True(PathRules.IsInside(@"C:\w\.tasker\.cache", @"C:\w\.tasker\.cache", StringComparison.Ordinal));
        Assert.False(PathRules.IsInside(@"C:\w\.tasker\.cache", @"C:\w\.tasker\.cache2\x", StringComparison.Ordinal));
        Assert.False(PathRules.IsInside(@"C:\w\.tasker\.cache", @"c:\W\.TASKER\.CACHE\index.db", PathRules.ComparisonFor(windows: false)));
        Assert.True(PathRules.IsInside(@"C:\w\.tasker\.cache", @"c:\W\.TASKER\.CACHE\index.db", PathRules.ComparisonFor(windows: true)));
    }

    [Fact]
    public void A_path_with_spaces_and_cyrillic_is_canonical_and_stable()
    {
        var folder = Path.Combine(_root, "Мой проект (тест)");
        Directory.CreateDirectory(folder);

        var first = WorkspaceLocation.Files(folder);
        var second = WorkspaceLocation.Files(Path.Combine(folder, ".") + Path.DirectorySeparatorChar);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal("Мой проект (тест)", first.Name);
    }

    // ---- окончания строк (git autocrlf) и BOM ----

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes))[..16];

    [Fact]
    public void The_version_of_a_file_does_not_depend_on_line_endings_or_bom()
    {
        var lf = "formatVersion: 9\nid: 1\ndescription: |\n  one\n  two\nname: x\n"u8.ToArray();
        var crlf = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(lf).Replace("\n", "\r\n"));
        var bom = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(crlf).ToArray();

        Assert.Equal(YamlFile.VersionOf(lf), YamlFile.VersionOf(crlf));
        Assert.Equal(YamlFile.VersionOf(lf), YamlFile.VersionOf(bom));
        // Версия файлов, записанных Tasker (LF, без BOM), не изменилась: это хэш самих байтов.
        Assert.Equal(Sha(lf), YamlFile.VersionOf(lf));
        // Настоящее изменение содержимого версию меняет.
        Assert.NotEqual(YamlFile.VersionOf(lf), YamlFile.VersionOf("formatVersion: 9\nid: 1\ndescription: |\n  one\n  TWO\nname: x\n"u8.ToArray()));
    }

    [Fact]
    public void Line_ending_helpers_keep_a_lone_carriage_return()
    {
        Assert.Equal("a\nb\rc\n", LineEndings.ToLf("a\r\nb\rc\r\n"));
        Assert.Equal("\r\n", LineEndings.Of("a\r\nb\n"));
        Assert.Equal("\n", LineEndings.Of("a\nb\n"));
        Assert.Equal("x", LineEndings.StripBom("\uFEFFx"));
    }

    [Fact]
    public async Task A_file_checked_out_with_crlf_and_bom_reads_with_the_same_version_and_fields()
    {
        var path = Path.Combine(_root, "project.yaml");
        var project = new Project { Id = Guid.NewGuid(), Name = "Мой проект", CreatedAt = DateTimeOffset.UtcNow, Version = "" };
        var version = await ProjectFile.Write(path, project, default);

        var converted = new byte[] { 0xEF, 0xBB, 0xBF }
            .Concat(Encoding.UTF8.GetBytes((await File.ReadAllTextAsync(path)).Replace("\n", "\r\n"))).ToArray();
        await File.WriteAllBytesAsync(path, converted);

        var read = await ProjectFile.Read(path, default);
        Assert.NotNull(read);
        Assert.Equal(version, read.Version);
        Assert.Equal("Мой проект", read.Name);
        Assert.Equal(project.Id, read.Id);

        // Запись по версии, прочитанной из CRLF-файла, проходит, и новый файл снова с LF.
        var updated = await ProjectFile.Update(path, new Project { Id = project.Id, Name = "Новое", CreatedAt = project.CreatedAt, Version = "" }, read.Version, default);
        Assert.NotNull(updated);
        Assert.DoesNotContain('\r', await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Written_files_use_lf_whatever_the_system_newline_is()
    {
        var path = Path.Combine(_root, "project.yaml");
        await ProjectFile.Write(path, new Project { Id = Guid.NewGuid(), Name = "a\nb", CreatedAt = DateTimeOffset.UtcNow, Version = "" }, default);

        var text = await File.ReadAllTextAsync(path);
        Assert.DoesNotContain('\r', text);
        Assert.StartsWith("formatVersion: " + FormatVersions.Current + "\n", text);
    }

    [Fact]
    public void Upgrading_a_crlf_file_keeps_its_line_endings()
    {
        var upgraded = FormatVersions.Upgrade("formatVersion: 3\r\nid: 1\r\nname: x\r\n", "x.yaml");

        Assert.StartsWith("formatVersion: " + FormatVersions.Current + "\r\nid: 1\r\n", upgraded);
        Assert.Equal(FormatVersions.Current, FormatVersions.Of(upgraded, "x.yaml"));

        var bomless = FormatVersions.Upgrade("id: 1\r\nname: x\r\n", "x.yaml");
        Assert.StartsWith("formatVersion: " + FormatVersions.Current + "\r\nid: 1\r\n", bomless);
    }

    // ---- git-хуки ----

    [Theory]
    [InlineData(@"C:\Users\me\.local\bin\tasker.exe", true, "C:/Users/me/.local/bin/tasker.exe")]
    [InlineData(@"C:\Program Files\Tasker\tasker.exe", true, "C:/Program Files/Tasker/tasker.exe")]
    [InlineData("/usr/local/bin/tasker", false, "/usr/local/bin/tasker")]
    [InlineData("/home/me/we\\ird", false, "/home/me/we\\ird")] // на Unix обратная косая — обычный знак имени
    public void Hook_paths_use_forward_slashes_on_windows_only(string path, bool windows, string expected) =>
        Assert.Equal(expected, GitHooks.ToShellPath(path, windows));

    [Fact]
    public void A_hook_with_crlf_loses_only_the_tasker_block()
    {
        var block = "# >>> tasker (managed by 'tasker hooks install', do not edit) >>>\nif true; then\n  :\nfi\n# <<< tasker <<<\n";
        var text = "#!/bin/sh\r\n" + block.Replace("\n", "\r\n") + "echo mine\r\n";

        var rest = GitHooks.WithoutBlock(text);

        Assert.Equal("#!/bin/sh\r\necho mine\r\n", rest);
        Assert.False(GitHooks.HasBlock(rest));
    }

    // ---- повторы при занятом файле (Windows) ----

    [Fact]
    public void Busy_file_errors_are_transient_but_missing_files_and_existing_names_are_not()
    {
        Assert.True(AtomicFile.IsTransient(new IOException("sharing violation", unchecked((int)0x80070020))));
        Assert.True(AtomicFile.IsTransient(new IOException("lock violation", unchecked((int)0x80070021))));
        Assert.True(AtomicFile.IsTransient(new UnauthorizedAccessException()));
        Assert.False(AtomicFile.IsTransient(new IOException("exists", unchecked((int)0x800700B7))));
        Assert.False(AtomicFile.IsTransient(new FileNotFoundException()));
        Assert.False(AtomicFile.IsTransient(new DirectoryNotFoundException()));
        Assert.False(AtomicFile.IsTransient(new InvalidOperationException()));
    }

    [Fact]
    public void A_busy_file_is_retried_with_pauses_and_then_succeeds()
    {
        var attempts = 0;
        var pauses = new List<int>();

        var result = AtomicFile.Retry(() =>
        {
            if (++attempts < 4)
                throw new IOException("busy", unchecked((int)0x80070020));
            return "ok";
        }, retry: true, [5, 10, 20, 40, 80], pauses.Add);

        Assert.Equal("ok", result);
        Assert.Equal(4, attempts);
        Assert.Equal([5, 10, 20], pauses);
    }

    [Fact]
    public void A_file_that_stays_busy_fails_after_the_last_pause_and_off_windows_there_is_one_attempt()
    {
        var attempts = 0;
        Assert.Throws<IOException>(() => AtomicFile.Retry<int>(() =>
        {
            attempts++;
            throw new IOException("busy", unchecked((int)0x80070020));
        }, retry: true, [1, 1, 1], _ => { }));
        Assert.Equal(4, attempts);

        attempts = 0;
        Assert.Throws<IOException>(() => AtomicFile.Retry<int>(() =>
        {
            attempts++;
            throw new IOException("busy", unchecked((int)0x80070020));
        }, retry: false, [1, 1, 1], _ => { }));
        Assert.Equal(1, attempts);

        attempts = 0;
        Assert.Throws<FileNotFoundException>(() => AtomicFile.Retry<int>(() =>
        {
            attempts++;
            throw new FileNotFoundException();
        }, retry: true, [1, 1, 1], _ => { }));
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task Atomic_write_replaces_the_file_and_leaves_no_temporary_file()
    {
        var path = Path.Combine(_root, "sub", "a.yaml");
        await AtomicFile.WriteAllText(path, "one\n");
        await AtomicFile.WriteAllText(path, "two\n");

        Assert.Equal("two\n", await AtomicFile.ReadAllText(path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    // ---- платформенные: только на своей системе, на остальных — явный пропуск ----

    [PlatformFact(TestPlatform.Windows)]
    public async Task Windows_a_file_can_be_replaced_while_a_reader_has_it_open()
    {
        var path = Path.Combine(_root, "a.yaml");
        await AtomicFile.WriteAllText(path, "one\n");

        // Так читает AtomicFile.ReadAllBytes (Share.Delete): замена не блокируется.
        await using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            await AtomicFile.WriteAllText(path, "two\n");

        Assert.Equal("two\n", await AtomicFile.ReadAllText(path));
    }

    [PlatformFact(TestPlatform.Windows)]
    public async Task Windows_a_short_lived_reader_without_share_delete_only_delays_the_replacement()
    {
        var path = Path.Combine(_root, "a.yaml");
        await AtomicFile.WriteAllText(path, "one\n");

        var holder = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); // как File.ReadAllBytes
        _ = Task.Run(async () =>
        {
            await Task.Delay(150);
            await holder.DisposeAsync();
        });

        await AtomicFile.WriteAllText(path, "two\n");

        Assert.Equal("two\n", await AtomicFile.ReadAllText(path));
    }

    [PlatformFact(TestPlatform.Windows)]
    public async Task Windows_a_lock_is_the_same_whatever_the_case_of_the_path()
    {
        var path = Path.Combine(_root, "Lock.lock");
        using var held = await FileLock.Acquire(path);

        await Assert.ThrowsAsync<TimeoutException>(() =>
            FileLock.Acquire(path.ToUpperInvariant(), timeout: TimeSpan.FromMilliseconds(200)));
    }

    [PlatformFact(TestPlatform.Windows)]
    public void Windows_the_canonical_path_has_an_upper_case_drive_and_the_real_case_of_names()
    {
        var folder = Path.Combine(_root, "Case Folder");
        Directory.CreateDirectory(folder);
        var lower = char.ToLowerInvariant(folder[0]) + folder[1..];

        Assert.Equal(CanonicalPath.Of(folder), CanonicalPath.Of(lower));
        Assert.StartsWith(char.ToUpperInvariant(folder[0]) + ":\\", CanonicalPath.Of(lower));
        Assert.Equal(CanonicalPath.Of(folder), CanonicalPath.Of(@"\\?\" + folder));
    }

    [PlatformFact(TestPlatform.Windows)]
    public void Windows_a_junction_resolves_to_its_target()
    {
        var target = Path.Combine(_root, "target");
        Directory.CreateDirectory(target);
        var link = Path.Combine(_root, "link");
        var info = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { UseShellExecute = false, CreateNoWindow = true };
        System.Diagnostics.Process.Start(info)!.WaitForExit();

        Assert.Equal(CanonicalPath.Of(target), CanonicalPath.Of(link));
    }

    [PlatformFact(TestPlatform.Unix)]
    public void Unix_paths_differing_in_case_are_different_names_for_locks()
    {
        Assert.Equal(StringComparer.Ordinal, PathRules.Comparer);
    }
}
