using Tasker.Storage.Files.Storages;

namespace Tasker.Storage.Files;

/// <summary>
/// Раскладка каталога .tasker внутри рабочей папки. У каждого проекта своя папка —
/// сущности разных проектов физически не пересекаются. Внутри — по файлу на сущность,
/// чтобы изменения были отдельными диффами в git. Каждый файл начинается с <c>formatVersion</c> —
/// версии формата файла (см. <see cref="Storages.FormatVersions"/>):
/// <code>
/// &lt;workspace&gt;/.tasker/
///   .gitignore            — исключает .cache и временные файлы записи
///   .cache/               — локальный индекс (см. Index.WorkspaceIndex) и блокировки, в git не попадает
///   users/&lt;id&gt;.yaml        — пользователи общие для всех проектов
///   projects/&lt;projectId&gt;/
///     project.yaml
///     tasks/&lt;заголовок&gt;-&lt;id8&gt;.yaml   — задачи; как и у всех сущностей проекта ниже, в имени файла — название (у задачи заголовок)
///                                       и первые 8 знаков id (см. <see cref="Storages.EntityFileNames"/>); старые имена — по полному Guid: &lt;id&gt;.yaml
///     task-types/&lt;название&gt;-&lt;id8&gt;.yaml
///     statuses/&lt;название&gt;-&lt;id8&gt;.yaml
///     status-sets/&lt;название&gt;-&lt;id8&gt;.yaml
///     series/&lt;название&gt;-&lt;id8&gt;.yaml       — серии задач (id, name, prefix); номера — в файлах задач (series)
///     link-types/&lt;название&gt;-&lt;id8&gt;.yaml    — типы связей между задачами (name, outwardName, inwardName); сами связи — в файлах задач (links)
///     fields/&lt;название&gt;-&lt;id8&gt;.yaml        — каталог полей задач (name, type, enum, multiple)
///     enums/&lt;название&gt;-&lt;id8&gt;.yaml         — перечисления для полей (name, values: id + name)
///     boards/&lt;название&gt;-&lt;id8&gt;.yaml
/// </code>
/// </summary>
public class TaskerDirectory(string workspacePath)
{
    public const string Name = ".tasker";
    public const string CacheName = ".cache";

    /// <summary>Файл блокировки записи для каталога .tasker <paramref name="taskerRoot"/> — единственное место, где задан его путь.</summary>
    public static string WriteLockOf(string taskerRoot) => Path.Combine(taskerRoot, CacheName, "write.lock");

    public string Root { get; } = Path.Combine(Path.GetFullPath(workspacePath), Name);
    public string Projects => Path.Combine(Root, "projects");
    public string Users => Path.Combine(Root, "users");
    public string Cache => Path.Combine(Root, CacheName);
    public string IndexFile => Path.Combine(Cache, "index.db");
    /// <summary>Короткая блокировка обращений к индексу (см. FileLock).</summary>
    public string IndexLock => Path.Combine(Cache, "index.lock");
    /// <summary>Блокировка записи между процессами (см. FileLock): под ней сравнивают версию файла и пишут, выдают номера серий.</summary>
    public string WriteLock => WriteLockOf(Root);
    /// <summary>Блокировки на время правки (см. Tasker.Core.Locks): по файлу на сущность, в git не попадают.</summary>
    public string EditLocks => Path.Combine(Cache, "edit-locks");
    /// <summary>Короткая блокировка обращений к <see cref="EditLocks"/> (см. FileLock).</summary>
    public string EditLocksLock => Path.Combine(Cache, "edit-locks.lock");
    public string GitIgnore => Path.Combine(Root, ".gitignore");

    /// <summary>
    /// Метка «проект удалён здесь» (локально, в .cache): по ней запись сущности отличает проект, удалённый, пока запрос шёл, от проекта,
    /// которого просто ещё нет. Вернувшийся проект (есть project.yaml) метку перекрывает.
    /// </summary>
    public string DeletedProjectMarker(Guid projectId) => Path.Combine(Cache, "deleted-projects", projectId.ToString());

    public ProjectDirectory Project(Guid projectId) => new(Path.Combine(Projects, projectId.ToString()));

    public string UserFile(Guid id) => Path.Combine(Users, $"{id}{YamlFile.Extension}");

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Projects);
        Directory.CreateDirectory(Users);
        EnsureServiceFilesIgnored();
    }

    // Правила дописываются, если их нет: свои правила пользователя в .gitignore не трогаем.
    // Локальный индекс и блокировки лежат в .cache; *.tmp — временные файлы атомарной записи.
    private void EnsureServiceFilesIgnored()
    {
        var text = File.Exists(GitIgnore) ? File.ReadAllText(GitIgnore) : "";
        var lines = text.Split('\n').Select(x => x.Trim()).ToHashSet();
        var missing = new[] { "/.cache/", "*.tmp" }.Where(x => !lines.Contains(x)).ToArray();
        if (missing.Length == 0)
            return;

        var separator = text.Length > 0 && !text.EndsWith('\n') ? "\n" : "";
        File.AppendAllText(GitIgnore, $"{separator}# Tasker: local index and locks (rebuilt, not for git) and temporary files of atomic writes\n{string.Join('\n', missing)}\n");
    }
}

public class ProjectDirectory(string root)
{
    public const string ProjectFileName = "project" + YamlFile.Extension;

    public string Root { get; } = root;
    public string ProjectFile => Path.Combine(Root, ProjectFileName);

    public string Tasks => Folder(EntityFolders.Tasks);
    public string TaskTypes => Folder(EntityFolders.TaskTypes);
    public string Statuses => Folder(EntityFolders.Statuses);
    public string StatusSets => Folder(EntityFolders.StatusSets);
    public string Boards => Folder(EntityFolders.Boards);
    public string Series => Folder(EntityFolders.Series);
    public string LinkTypes => Folder(EntityFolders.LinkTypes);
    public string Fields => Folder(EntityFolders.Fields);
    public string Enums => Folder(EntityFolders.Enums);

    internal string Folder(EntityFolder folder) => Path.Combine(Root, folder.Name);

    /// <summary>Файл сущности под её названием (<see cref="EntityFileNames"/>): <c>исправить-вход-3f2a9c1e.yaml</c>.</summary>
    internal string EntityFile(EntityFolder folder, Guid id, string? name) => Path.Combine(Folder(folder), folder.FileName(name, id));

    /// <summary>Старое имя файла сущности — по полному Guid (формат до версии 2 у задач, до версии 5 у остальных).</summary>
    internal string LegacyFile(EntityFolder folder, Guid id) => Path.Combine(Folder(folder), $"{id}{YamlFile.Extension}");

    /// <summary>
    /// Существующие файлы сущности по id — под старым или новым именем. Ищет по имени (старое — Guid, новое — суффикс из id),
    /// не заглядывая в индекс, поэтому видит и файлы, которых индекс ещё не знает. В файл не заглядывает: совпадение id проверяет
    /// читающий (<see cref="EntityFileNames"/> — суффикс из 8 знаков может, хотя и крайне редко, совпасть).
    /// </summary>
    internal IEnumerable<string> FindFiles(EntityFolder folder, Guid id)
    {
        var legacy = LegacyFile(folder, id);
        if (File.Exists(legacy))
            yield return legacy;

        if (!Directory.Exists(Folder(folder)))
            yield break;

        foreach (var path in Directory.EnumerateFiles(Folder(folder), $"*-{EntityFileNames.IdPrefix(id)}{YamlFile.Extension}"))
            yield return path;
    }

    /// <summary>Файл задачи с именем по заголовку: <c>исправить-вход-3f2a9c1e.yaml</c>.</summary>
    public string TaskFile(Guid id, string? title) => EntityFile(EntityFolders.Tasks, id, title);

    /// <summary>Существующие файлы задачи по id — под старым или новым именем (<see cref="FindFiles"/>).</summary>
    public IEnumerable<string> FindTaskFiles(Guid id) => FindFiles(EntityFolders.Tasks, id);

    /// <summary>Первый найденный файл задачи (<see cref="FindTaskFiles"/>); null — нет.</summary>
    public string? FindTaskFile(Guid id) => FindTaskFiles(id).FirstOrDefault();
}

