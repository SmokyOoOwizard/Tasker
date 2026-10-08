using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace Tasker.Stress;

/// <summary>Чего ждём от области в конце сценария.</summary>
public sealed class Expect
{
    /// <summary>Точное число задач в проекте; null — не проверять.</summary>
    public int? Tasks { get; init; }

    /// <summary>Номера серии без пропусков 1..N (нужно там, где ничего не удаляли и не убивали).</summary>
    public bool ContiguousNumbers { get; init; }

    /// <summary>Нет ли действующих блокировок на время правки (все сценарии снимают свои).</summary>
    public bool NoEditLocks { get; init; } = true;
}

/// <summary>Результат проверки: нарушения и замечания (не нарушения: например, оставшийся временный файл после kill -9).</summary>
public sealed class Findings
{
    public List<string> Violations { get; } = [];

    public List<string> Notes { get; } = [];

    public void Merge(Findings other)
    {
        Violations.AddRange(other.Violations);
        Notes.AddRange(other.Notes);
    }
}

/// <summary>
/// Проверка инвариантов области после прогона: число задач, уникальность (и сплошность) номеров серии, целостность файлов YAML
/// (разбор, formatVersion, имя = id), индекс = файлам (свежий индекс копии без <c>.cache</c> показывает то же), <c>sync</c> и
/// <c>cleanup --check</c> чистые, нет залипших блокировок (новая запись проходит, <c>write.lock</c>/<c>index.lock</c> свободны), нет блокировок правки.
/// </summary>
public static class Invariants
{
    public static async Task<Findings> Check(StressContext ctx, Area area, Expect expect, string label)
    {
        var f = new Findings();
        var stand = ctx.Stand;

        // 1. Задачи глазами индекса/БД: число и номера серии.
        var list = await stand.Cli(area, true, "task", "list", "--all", "--json");
        if (!list.Ok)
        {
            f.Violations.Add($"[{label}] task list failed: {list.Text}");
            return f;
        }

        var tasks = list.Json["data"]!.AsArray();
        var ids = tasks.Select(x => x!["id"]!.GetValue<string>()).ToArray();
        if (ids.Distinct().Count() != ids.Length)
            f.Violations.Add($"[{label}] duplicate task ids in the list");
        if (expect.Tasks is { } n && tasks.Count != n)
            f.Violations.Add($"[{label}] tasks: expected {n}, got {tasks.Count}");

        var numbers = tasks.SelectMany(t => t!["seriesNumbers"]!.AsArray().Select(s => (Series: s!["seriesId"]!.GetValue<string>(), Number: s["number"]!.GetValue<int>()))).ToArray();
        foreach (var group in numbers.GroupBy(x => x.Series))
        {
            var dup = group.GroupBy(x => x.Number).Where(g => g.Count() > 1).Select(g => g.Key).Order().ToArray();
            if (dup.Length > 0)
                f.Violations.Add($"[{label}] duplicate series numbers: {string.Join(", ", dup)}");
            if (expect.ContiguousNumbers)
            {
                var sorted = group.Select(x => x.Number).Order().ToArray();
                if (!sorted.SequenceEqual(Enumerable.Range(1, sorted.Length)))
                    f.Violations.Add($"[{label}] series numbers are not 1..{sorted.Length}: gaps at {string.Join(", ", Enumerable.Range(1, sorted.Length).Except(sorted).Take(10))}");
            }
        }

        // 2. Файлы и индекс.
        if (!area.IsSqlite)
            await CheckFiles(ctx, area, ids, label, f);
        else
            await CheckSqlite(area, label, f);

        // 3. sync и cleanup --check чистые.
        var sync = await stand.Cli(area, true, "sync", "-q");
        if (!sync.Ok || sync.Out.Trim().Length > 0 || sync.Err.Trim().Length > 0)
            f.Violations.Add($"[{label}] sync is not clean: exit {sync.Code} {sync.Text}");
        var cleanup = await stand.Cli(area, true, "cleanup", "--check");
        if (cleanup.Code != 0)
            f.Violations.Add($"[{label}] cleanup --check: exit {cleanup.Code} {cleanup.Text}");

        // 4. Нет залипших блокировок: новая запись проходит быстро.
        var watch = Stopwatch.StartNew();
        var probe = await stand.Cli(area, true, "task", "create", "probe-" + label, "--type", area.TypeId.ToString(), "--json");
        if (!probe.Ok || watch.Elapsed > TimeSpan.FromSeconds(15))
            f.Violations.Add($"[{label}] write after the run: {(probe.Ok ? "slow " + watch.Elapsed.TotalSeconds.ToString("F1") + " s" : probe.Text)} (stuck lock?)");
        else
        {
            var id = probe.Json["id"]!.GetValue<string>();
            var removed = await stand.Cli(area, true, "task", "delete", id);
            if (!removed.Ok)
                f.Violations.Add($"[{label}] cannot delete the probe task: {removed.Text}");
        }

        if (expect.NoEditLocks && !area.IsSqlite)
        {
            var locksDir = Path.Combine(area.TaskerDir, ".cache", "edit-locks");
            var active = Directory.Exists(locksDir) ? Directory.GetFiles(locksDir).Where(IsActiveLock).ToArray() : [];
            if (active.Length > 0)
                f.Violations.Add($"[{label}] {active.Length} edit lock(s) still active");
        }

        return f;
    }

    private static bool IsActiveLock(string file)
    {
        if (file.EndsWith(".tmp"))
            return false;
        try
        {
            var json = JsonNode.Parse(File.ReadAllText(file))!;
            var expires = json["expiresAt"] ?? json["ExpiresAt"];
            return expires == null || DateTimeOffset.Parse(expires.GetValue<string>()) > DateTimeOffset.UtcNow;
        }
        catch
        {
            return false;
        }
    }

    private static readonly Regex FileName = new(@"-([0-9a-f]{8})\.yaml$", RegexOptions.Compiled);

    private static async Task CheckFiles(StressContext ctx, Area area, string[] listedIds, string label, Findings f)
    {
        var root = Path.Combine(area.TaskerDir, "projects", area.ProjectId.ToString());
        var taskFiles = new List<string>();
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);
            if (name.EndsWith(".tmp"))
            {
                f.Notes.Add($"[{label}] leftover temp file {Path.GetRelativePath(area.TaskerDir, file)} (a killed writer; harmless: .gitignore, overwritten by the next write)");
                continue;
            }

            if (!name.EndsWith(".yaml"))
            {
                f.Violations.Add($"[{label}] unexpected file {Path.GetRelativePath(area.TaskerDir, file)}");
                continue;
            }

            string text;
            try
            {
                text = await File.ReadAllTextAsync(file);
            }
            catch (FileNotFoundException)
            {
                continue; // удалили между обходом и чтением — но стенд к этому моменту уже ничего не пишет
            }

            if (text.Length == 0)
                f.Violations.Add($"[{label}] empty file {Path.GetRelativePath(area.TaskerDir, file)}");
            else if (!text.StartsWith("formatVersion: "))
                f.Violations.Add($"[{label}] no formatVersion on the first line: {Path.GetRelativePath(area.TaskerDir, file)}");
            if (Regex.IsMatch(text, @"^(<{7}|={7}|>{7})", RegexOptions.Multiline))
                f.Violations.Add($"[{label}] merge conflict markers in {Path.GetRelativePath(area.TaskerDir, file)}");

            YamlMappingNode? map = null;
            try
            {
                var yaml = new YamlStream();
                yaml.Load(new StringReader(text));
                map = yaml.Documents.Count == 1 ? yaml.Documents[0].RootNode as YamlMappingNode : null;
                if (map == null)
                    f.Violations.Add($"[{label}] not a single YAML mapping: {Path.GetRelativePath(area.TaskerDir, file)}");
            }
            catch (Exception e)
            {
                f.Violations.Add($"[{label}] YAML parse error in {Path.GetRelativePath(area.TaskerDir, file)}: {e.Message}");
            }

            var dir = Path.GetFileName(Path.GetDirectoryName(file));
            if (dir == "tasks")
            {
                taskFiles.Add(file);
                var id = map?.Children.TryGetValue(new YamlScalarNode("id"), out var node) == true ? (node as YamlScalarNode)?.Value : null;
                var match = FileName.Match(name);
                if (id == null || !match.Success || !id.StartsWith(match.Groups[1].Value))
                    f.Violations.Add($"[{label}] task file name does not match its id: {name} (id {id})");
            }
        }

        // Индекс = файлам: список из индекса (живой) и список из свежего индекса копии без .cache.
        var fileIds = taskFiles.Select(file =>
        {
            var m = FileName.Match(Path.GetFileName(file));
            return m.Success ? m.Groups[1].Value : "";
        }).Order().ToArray();
        var indexIds = listedIds.Select(x => x[..8]).Order().ToArray();
        if (!fileIds.SequenceEqual(indexIds))
            f.Violations.Add($"[{label}] index != files: {indexIds.Length} tasks in the index, {fileIds.Length} task files; " +
                $"only in the index: {string.Join(",", indexIds.Except(fileIds).Take(5))}; only in files: {string.Join(",", fileIds.Except(indexIds).Take(5))}");

        // Индекс самого демона (живой, держится наблюдателем за папкой): после затишья он тоже равен файлам.
        if (ctx.Stand.DaemonRunning)
        {
            var viewer = new McpClient("invariants", ctx, area, ctx.Stand.AgentOf(area, 0));
            string[] daemonIds = [];
            for (var attempt = 0; attempt < 40; attempt++)
            {
                var all = new List<string>();
                for (var offset = 0; ; offset += 200)
                {
                    var page = await viewer.Tool("list_tasks", new { projectId = area.ProjectId, offset, limit = 200, descriptionLength = 0 });
                    if (!page.Item1)
                        break;
                    var data = JsonNode.Parse(page.Item2)!["data"]!.AsArray();
                    all.AddRange(data.Select(x => x!["id"]!.GetValue<string>()[..8]));
                    if (data.Count < 200)
                        break;
                }

                daemonIds = [.. all.Order()];
                if (daemonIds.SequenceEqual(fileIds))
                    break;
                await Task.Delay(250);
            }

            if (!daemonIds.SequenceEqual(fileIds))
                f.Violations.Add($"[{label}] the daemon's index != files after 10 s of quiet: {daemonIds.Length} tasks in the daemon, {fileIds.Length} files; " +
                    $"only in the daemon: {string.Join(",", daemonIds.Except(fileIds).Take(5))}; only in files: {string.Join(",", fileIds.Except(daemonIds).Take(5))}");
        }

        var copy = Path.Combine(ctx.Stand.Root, "copy-" + label + "-" + Guid.NewGuid().ToString("N")[..6]);
        CopyWithoutCache(area.Path, copy);
        try
        {
            var fresh = new Area { Name = "copy", Path = copy, IsSqlite = false, ProjectId = area.ProjectId, Project = area.ProjectId.ToString() };
            var rebuilt = await ctx.Stand.Cli(fresh, true, "task", "list", "--all", "--json");
            if (!rebuilt.Ok)
                f.Violations.Add($"[{label}] a fresh index does not build: {rebuilt.Text}");
            else
            {
                var rebuiltIds = rebuilt.Json["data"]!.AsArray().Select(x => x!["id"]!.GetValue<string>()[..8]).Order().ToArray();
                if (!rebuiltIds.SequenceEqual(indexIds))
                    f.Violations.Add($"[{label}] the index differs from a rebuilt one: {indexIds.Length} vs {rebuiltIds.Length}");
            }
        }
        finally
        {
            Directory.Delete(copy, recursive: true);
        }
    }

    private static void CopyWithoutCache(string workspace, string target)
    {
        var source = Path.Combine(workspace, ".tasker");
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            if (relative.StartsWith(".cache") || file.EndsWith(".tmp"))
                continue;
            var destination = Path.Combine(target, ".tasker", relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }
    }

    private static async Task CheckSqlite(Area area, string label, Findings f)
    {
        // sqlite3 есть не везде (macOS и большинство Linux — да): без него целостность файла не проверяем.
        try
        {
            var info = new ProcessStartInfo("sqlite3", [area.Path, "PRAGMA integrity_check;"]) { RedirectStandardOutput = true, RedirectStandardError = true };
            using var process = Process.Start(info)!;
            var output = (await process.StandardOutput.ReadToEndAsync()).Trim();
            await process.WaitForExitAsync();
            if (output != "ok")
                f.Violations.Add($"[{label}] sqlite integrity_check: {output}");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            f.Notes.Add($"[{label}] sqlite3 is not installed: integrity_check skipped");
        }
    }

    /// <summary>Хэш содержимого файла: версия сущности в файловом режиме (первые 16 знаков SHA-256).</summary>
    public static string FileVersion(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))[..16];
}
