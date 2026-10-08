using System.Text.RegularExpressions;
using Tasker.Core.IO;
using Tasker.Core.Workspace;
using Tasker.Storage.Files.Index;

namespace Tasker.Storage.Files.Storages;

/// <summary>
/// Приводит файлы сущностей .tasker к текущему формату на диске (<see cref="FormatVersions"/>): добавляет или обновляет
/// <c>formatVersion</c> и применяет шаги миграции, остальной текст не трогает. Файлы более нового формата и файлы с конфликтом
/// слияния git не меняются, а попадают в отчёт. Каждый файл переписывается под блокировкой записи, индекс обновляется после.
/// </summary>
internal sealed partial class FileMigration(TaskerDirectory directory, WorkspaceIndex index) : IFileMigration
{
    public int CurrentFormat => FormatVersions.Current;

    // Те же маркеры, что у индекса: файл с конфликтом слияния не переписываем, пока его не исправят.
    [GeneratedRegex(@"^(<{7}|>{7})( |$)|^={7}$", RegexOptions.Multiline)]
    private static partial Regex ConflictMarker();

    public async Task<MigrationReport> Run(MigrationOptions options, CancellationToken ct = default)
    {
        var scanned = 0;
        var upToDate = 0;
        var migrated = new List<MigrationFile>();
        var renamed = new List<MigrationRename>();
        var newer = new List<MigrationFile>();
        var unreadable = new List<MigrationProblem>();
        var changed = new List<string>();

        if (Directory.Exists(directory.Root))
        {
            var files = Directory.EnumerateFiles(directory.Root, "*" + YamlFile.Extension,
                    new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
                .Select(x => (Full: x, Relative: Relative(x)))
                .Where(x => WorkspaceLayout.Classify(x.Relative) != null) // .cache, временные и чужие файлы — не сущности
                .OrderBy(x => x.Relative, StringComparer.Ordinal);

            foreach (var (full, relative) in files)
            {
                ct.ThrowIfCancellationRequested();
                scanned++;

                string text;
                try
                {
                    text = await AtomicFile.ReadAllText(full, ct);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    unreadable.Add(new MigrationProblem(relative, e.Message));
                    continue;
                }

                if (ConflictMarker().IsMatch(text))
                {
                    unreadable.Add(new MigrationProblem(relative, "Unresolved git merge conflict: fix the file first"));
                    continue;
                }

                int version;
                try
                {
                    version = FormatVersions.Of(text, full);
                }
                catch (UnsupportedFormatException e)
                {
                    unreadable.Add(new MigrationProblem(relative, e.Message));
                    continue;
                }

                if (version > FormatVersions.Current)
                {
                    newer.Add(new MigrationFile(relative, version));
                    continue;
                }

                // Файл в папке сущностей проекта должен быть настоящей сущностью: проверяем до любых изменений, чтобы чужой или битый файл
                // не тронуть вовсе.
                EntityHead? head = null;
                var layout = WorkspaceLayout.Classify(relative)!;
                var folder = EntityFolders.Find(layout.Kind);
                if (folder != null)
                {
                    var (inspected, problem) = await Inspect(folder, layout, full, ct);
                    if (problem != null)
                    {
                        unreadable.Add(new MigrationProblem(relative, problem));
                        continue;
                    }
                    head = inspected;
                }

                if (version == FormatVersions.Current)
                    upToDate++;
                else
                {
                    migrated.Add(new MigrationFile(relative, version));
                    if (!options.DryRun)
                    {
                        await YamlFile.UpgradeOnDisk(full, ct);
                        changed.Add(full);
                    }
                }

                if (head != null)
                    await Rename(folder!, head, full, relative, options, renamed, unreadable, changed, ct);
            }
        }

        if (changed.Count > 0)
            await index.Refresh(changed, ct);

        return new MigrationReport(FormatVersions.Current, scanned, upToDate, migrated.ToArray(), renamed.ToArray(), newer.ToArray(), unreadable.ToArray());
    }

    /// <summary>
    /// Файл сущности проекта должен называться по её названию (<see cref="EntityFileNames"/>): старые имена (Guid) и имена, от которых
    /// название «ушло» (правка мимо Tasker, слияние), приводятся к нужному. Чужой файл под новым именем не затираем.
    /// </summary>
    private async Task Rename(
        EntityFolder folder, EntityHead head, string full, string relative, MigrationOptions options, List<MigrationRename> renamed,
        List<MigrationProblem> unreadable, List<string> changed, CancellationToken ct)
    {
        if (folder.IsCurrent(Path.GetFileName(full), head.Name, head.Id))
            return;

        var target = Path.Combine(Path.GetDirectoryName(full)!, folder.FileName(head.Name, head.Id));
        var targetRelative = Relative(target);
        if (File.Exists(target))
        {
            unreadable.Add(new MigrationProblem(relative, $"Cannot rename to '{Path.GetFileName(target)}': a file with that name exists"));
            return;
        }

        renamed.Add(new MigrationRename(relative, targetRelative));
        if (options.DryRun)
            return;

        if (await YamlFile.Rename(full, target, ct))
        {
            changed.Add(full);
            changed.Add(target);
        }
    }

    /// <summary>
    /// Читает файл сущности (старый формат — в памяти) и проверяет, что это настоящая сущность: id внутри согласуется с именем, как в индексе.
    /// Иначе (пустой или чужой файл, неудачное слияние) файл не трогаем совсем: назвать его по названию значило бы его потерять.
    /// </summary>
    /// <returns>Id и название сущности или причина, по которой файл не трогаем.</returns>
    private static async Task<(EntityHead? Head, string? Problem)> Inspect(EntityFolder folder, LayoutEntry layout, string full, CancellationToken ct)
    {
        EntityHead? head;
        try
        {
            head = await folder.Peek(layout.ProjectId!.Value, full, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return (null, e.Message);
        }

        var consistent = head != null && head.Id != Guid.Empty
            && (layout.Id is { } named ? head.Id == named : layout.IdPrefix == null || EntityFileNames.IdPrefix(head.Id) == layout.IdPrefix);
        return consistent ? (head, null) : (null, "The id inside the file does not match the file name: not touched");
    }

    private string Relative(string path) => Path.GetRelativePath(directory.Root, path).Replace('\\', '/');
}
