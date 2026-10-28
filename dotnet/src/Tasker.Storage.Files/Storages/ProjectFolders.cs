using System.Text;
using System.Text.RegularExpressions;
using Tasker.Core.IO;
using Tasker.Core.Workspace;

namespace Tasker.Storage.Files.Storages;

/// <summary>
/// Папки проектов: <c>projects/&lt;slug имени&gt;-&lt;id8&gt;/</c> — по названию проекта и первым 8 знакам его id, по тем же правилам, что
/// имена файлов сущностей (<see cref="EntityFileNames"/>); прежнее имя — полный Guid <c>projects/&lt;guid&gt;/</c> — читается наравне с
/// новым. Настоящий id проекта — в <c>project.yaml</c>, имя папки сверяется с ним по первым 8 знакам.
/// <para>
/// У одного проекта может оказаться несколько папок (тот же id): ветка git переименовала проект, а в другой ветке в старую папку
/// добавили задачу. Чтение и индекс объединяют их: сущности всех папок — сущности проекта, <c>project.yaml</c> берётся из основной папки
/// (<see cref="FoldersOf"/>: папка с текущим именем, затем папка с <c>project.yaml</c>, затем по имени), остальные — вторые папки:
/// о них сообщают проблемы области (<see cref="SecondFolderProblems"/>), а <c>tasker cleanup</c> и <c>tasker migrate</c> сливают их файлы
/// в основную (<see cref="Tidy"/>).
/// </para>
/// Снимок <c>projects/</c> на одну операцию: список папок и прочитанные <c>project.yaml</c> запоминаются.
/// </summary>
internal sealed partial class ProjectFolders(TaskerDirectory directory)
{
    /// <summary>Имя папки, если в названии проекта нет ни одной буквы или цифры.</summary>
    public const string EmptySlug = "project";

    public const string NoProjectFileReason = "No project.yaml: cannot tell which project the folder belongs to";
    public const string MismatchReason = "The id in project.yaml does not match the folder name: not touched";

    // Строка id: верхнего уровня — id проекта из файла, который целиком не читается (маркер конфликта git): по нему папка всё равно
    // узнаётся, и сущности проекта не пропадают из списков.
    [GeneratedRegex(@"^id:[ \t]*['""]?(?<id>[0-9A-Fa-f-]{36})['""]?[ \t]*\r?$", RegexOptions.Multiline)]
    private static partial Regex IdLine();

    private record Head(bool Exists, Guid? Id, string? Name);

    private List<(string Folder, string Id8, Guid? Guid)>? listing;
    private readonly Dictionary<string, Head> heads = new(StringComparer.Ordinal);

    /// <summary>Имя папки проекта: <c>мой-проект-3f2a9c1e</c>.</summary>
    public static string FolderName(string name, Guid id) => $"{EntityFileNames.Slug(name, EmptySlug)}-{EntityFileNames.IdPrefix(id)}";

    /// <summary>Совпадает ли имя папки с тем, каким оно должно быть у проекта (без учёта формы Unicode).</summary>
    public static bool IsCurrent(string folder, string name, Guid id) =>
        string.Equals(folder.Normalize(NormalizationForm.FormC), FolderName(name, id), StringComparison.Ordinal);

    /// <summary>Что имя папки говорит о проекте: первые 8 знаков id и, у прежнего имени, полный Guid; false — не папка проекта.</summary>
    public static bool TryParse(string folder, out string id8, out Guid? guid)
    {
        guid = null;
        id8 = "";
        if (!EntityFileNames.TryParse(folder + YamlFile.Extension, out var fullId, out var prefix))
            return false;
        guid = fullId;
        id8 = fullId is { } g ? EntityFileNames.IdPrefix(g) : prefix!;
        return true;
    }

    /// <summary>Папки <c>projects/</c>, похожие на папки проектов, по имени (Ordinal).</summary>
    public IReadOnlyList<(string Folder, string Id8, Guid? Guid)> Listing()
    {
        if (listing != null)
            return listing;
        listing = [];
        if (Directory.Exists(directory.Projects))
        {
            foreach (var path in Directory.EnumerateDirectories(directory.Projects))
            {
                var name = Path.GetFileName(path);
                if (TryParse(name, out var id8, out var guid))
                    listing.Add((name, id8, guid));
            }
        }
        listing.Sort((a, b) => string.CompareOrdinal(a.Folder, b.Folder));
        return listing;
    }

    private Head HeadOf(string folder)
    {
        if (heads.TryGetValue(folder, out var known))
            return known;

        var path = Path.Combine(directory.Projects, folder, ProjectDirectory.ProjectFileName);
        Head head;
        try
        {
            var bytes = AtomicFile.ReadAllBytesSync(path);
            try
            {
                var project = ProjectFile.ReadSync(bytes, path);
                head = new Head(true, project?.Id, project?.Name);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                var text = Encoding.UTF8.GetString(bytes);
                var match = IdLine().Match(text);
                head = new Head(true, match.Success && Guid.TryParse(match.Groups["id"].Value, out var id) ? id : null, null);
            }
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            head = new Head(false, null, null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Не прочитался (занят, нет прав): папку узнаём только по имени.
            head = new Head(true, null, null);
        }

        heads[folder] = head;
        return head;
    }

    /// <summary>
    /// Id проекта, чьи файлы лежат в папке; null — не папка проекта или проект по ней не узнать. Прежнее имя — сам Guid. Новое — id из
    /// <c>project.yaml</c> папки, если он согласуется с именем; если <c>project.yaml</c> в папке нет (вторая папка после слияния), —
    /// проект с теми же первыми 8 знаками id из соседних папок.
    /// </summary>
    public Guid? ProjectId(string folder)
    {
        if (!TryParse(folder, out var id8, out var guid))
            return null;
        if (guid != null)
            return guid;

        var head = HeadOf(folder);
        if (head.Exists)
            return head.Id is { } own && EntityFileNames.IdPrefix(own) == id8 ? own : null;

        foreach (var (other, otherId8, otherGuid) in Listing())
        {
            if (other == folder || otherId8 != id8)
                continue;
            if (otherGuid != null)
                return otherGuid;
            if (HeadOf(other).Id is { } id && EntityFileNames.IdPrefix(id) == id8)
                return id;
        }
        return null;
    }

    // 0 — папка с текущим именем, затем папки с project.yaml (новое имя раньше прежнего), затем без него.
    private int Rank(string folder, Guid id)
    {
        var head = HeadOf(folder);
        var guid = TryParse(folder, out _, out var g) && g != null;
        var own = head.Id == id;
        if (own && head.Name is { } name && IsCurrent(folder, name, id))
            return 0;
        if (own)
            return guid ? 2 : 1;
        return guid ? 4 : 3;
    }

    /// <summary>Все папки проекта, основная первой; пусто — папки нет.</summary>
    public string[] FoldersOf(Guid id)
    {
        var id8 = EntityFileNames.IdPrefix(id);
        return Listing()
            .Where(x => x.Guid is { } guid ? guid == id : x.Id8 == id8 && ProjectId(x.Folder) == id)
            .Select(x => x.Folder)
            .OrderBy(x => Rank(x, id))
            .ThenBy(x => x, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>Основная папка проекта (её <c>project.yaml</c> — проект); null — папки нет.</summary>
    public string? Primary(Guid id) => FoldersOf(id).FirstOrDefault();

    /// <summary>Папки по проектам (основная первой), в порядке имён основных папок; и папки, проект которых не узнать.</summary>
    public (List<(Guid Id, string[] Folders)> Groups, List<string> Unknown) Groups()
    {
        var ids = new List<Guid>();
        var unknown = new List<string>();
        foreach (var (folder, _, _) in Listing())
        {
            if (ProjectId(folder) is not { } id)
                unknown.Add(folder);
            else if (!ids.Contains(id))
                ids.Add(id);
        }
        var groups = ids.Select(id => (id, FoldersOf(id))).OrderBy(x => x.Item2[0], StringComparer.Ordinal).ToList();
        return (groups, unknown);
    }

    /// <summary>Название проекта из <c>project.yaml</c> папки; null — файла нет или он не читается.</summary>
    public string? NameIn(string folder) => HeadOf(folder).Name;

    public bool HasProjectFile(string folder) => HeadOf(folder).Exists;

    /// <summary>Текст проблемы второй папки проекта.</summary>
    public static string SecondFolderReason(string primary) =>
        $"Second folder of the project in projects/{primary}: its files are listed with the project; run 'tasker cleanup' to merge the folders";

    /// <summary>Вторые папки проектов области как проблемы (<c>projects/&lt;папка&gt;</c>), по пути.</summary>
    public static List<WorkspaceProblem> SecondFolderProblems(TaskerDirectory directory)
    {
        var result = new List<WorkspaceProblem>();
        foreach (var (_, folders) in new ProjectFolders(directory).Groups().Groups)
        {
            foreach (var second in folders.Skip(1))
                result.Add(new WorkspaceProblem($"projects/{second}", SecondFolderReason(folders[0])));
        }
        return result.OrderBy(x => x.Path, StringComparer.Ordinal).ToList();
    }

    /// <summary>Что сделано с папками проектов: переименования, слияния и то, что осталось как было.</summary>
    public record TidyReport(List<MigrationRename> Renamed, List<MigrationRename> Merged, List<MigrationProblem> Problems, List<string> Changed);

    /// <summary>
    /// Приводит папки проектов к текущим именам и сливает вторые папки в основную — под блокировкой записи (вызывающий её держит).
    /// Основная папка переименовывается в <c>&lt;slug имени&gt;-&lt;id8&gt;</c> (имя занято — в отчёт). Файлы второй папки переносятся в
    /// основную; файл, который там уже есть с тем же содержимым, удаляется, с другим — остаётся на месте и попадает в отчёт: ничего не
    /// теряется. Опустевшая вторая папка удаляется.
    /// </summary>
    /// <param name="rename">Ещё и переименовать папку проекта с одной папкой, если её имя не текущее (<c>tasker migrate</c>).</param>
    public TidyReport Tidy(bool dryRun, bool rename)
    {
        var report = new TidyReport([], [], [], []);
        if (!Directory.Exists(directory.Projects))
            return report;

        var (groups, unknown) = Groups();
        if (rename)
        {
            foreach (var folder in unknown)
                report.Problems.Add(new MigrationProblem($"projects/{folder}", HasProjectFile(folder) ? MismatchReason : NoProjectFileReason));
        }

        foreach (var (id, group) in groups)
        {
            if (group.Length < 2 && !rename)
                continue;

            var primary = group[0];
            var target = primary;
            if (ProjectId(primary) == id && NameIn(primary) is { } name && !IsCurrent(primary, name, id))
            {
                var current = FolderName(name, id);
                if (group.Contains(current))
                    // Папка с текущим именем уже есть (без project.yaml): сливаем всё в неё.
                    target = current;
                else if (Directory.Exists(Path.Combine(directory.Projects, current)) || File.Exists(Path.Combine(directory.Projects, current)))
                    report.Problems.Add(new MigrationProblem($"projects/{primary}", $"Cannot rename to '{current}': a folder with that name exists"));
                else
                {
                    report.Renamed.Add(new MigrationRename($"projects/{primary}", $"projects/{current}"));
                    if (!dryRun)
                    {
                        AtomicFile.MoveDirectory(Path.Combine(directory.Projects, primary), Path.Combine(directory.Projects, current));
                        report.Changed.Add(Path.Combine(directory.Projects, primary));
                        report.Changed.Add(Path.Combine(directory.Projects, current));
                    }
                    target = current;
                }
            }

            var movedPrimary = target != primary && !group.Contains(target);
            // Где файлы основной папки сейчас: без записи переименование только рассказано.
            var targetDir = Path.Combine(directory.Projects, movedPrimary && dryRun ? primary : target);
            foreach (var second in group)
            {
                if (second == target || (movedPrimary && second == primary))
                    continue;
                Merge(second, target, targetDir, dryRun, report);
            }
        }
        return report;
    }

    private void Merge(string second, string target, string to, bool dryRun, TidyReport report)
    {
        var from = Path.Combine(directory.Projects, second);
        report.Merged.Add(new MigrationRename($"projects/{second}", $"projects/{target}"));
        var files = Directory.EnumerateFiles(from, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
            .Select(x => Path.GetRelativePath(from, x))
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();
        foreach (var relative in files)
        {
            var source = Path.Combine(from, relative);
            var destination = Path.Combine(to, relative);
            var slashed = relative.Replace('\\', '/');
            if (File.Exists(destination))
            {
                if (!File.ReadAllBytes(source).AsSpan().SequenceEqual(File.ReadAllBytes(destination)))
                {
                    report.Problems.Add(new MigrationProblem($"projects/{second}/{slashed}",
                        $"Not merged: projects/{target}/{slashed} exists with other content; merge the two files by hand"));
                    continue;
                }
                if (!dryRun)
                    AtomicFile.Delete(source);
            }
            else if (!dryRun)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                AtomicFile.Move(source, destination, overwrite: false);
            }
        }

        if (dryRun)
            return;
        RemoveEmptyDirectories(from);
        report.Changed.Add(from);
        report.Changed.Add(to);
    }

    private static void RemoveEmptyDirectories(string folder)
    {
        foreach (var child in Directory.EnumerateDirectories(folder))
            RemoveEmptyDirectories(child);
        if (!Directory.EnumerateFileSystemEntries(folder).Any())
            Directory.Delete(folder);
    }
}
