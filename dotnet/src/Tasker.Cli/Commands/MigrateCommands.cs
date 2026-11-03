using System.CommandLine;
using Tasker.Core.Workspace;

namespace Tasker.Cli.Commands;

/// <summary>
/// <c>tasker migrate</c>: приводит файлы .tasker к текущему формату на диске. Каждый файл хранит <c>formatVersion</c>; Tasker
/// старые файлы читает всегда, а эта команда переписывает те, которые никто не менял, — чтобы в репозитории не оставалось
/// файлов разных форматов. Меняется только версия (и то, что меняет шаг миграции), остальной текст файла — нет.
/// </summary>
internal static class MigrateCommands
{
    /// <summary>Код выхода <c>--check</c>: есть файлы старого или нового формата либо нечитаемые.</summary>
    public const int NeedsMigration = 2;

    private const int ShownFiles = 10;

    public static Command Build(GlobalOptions g)
    {
        var dryRun = new Option<bool>("--dry-run") { Description = "Write nothing, only show which files would be migrated" };
        var check = new Option<bool>("--check") { Description = "Write nothing; exit with code 2 if any file is not in the current format or needs attention (for scripts and CI)" };

        return Kit.Leaf(g, "migrate", "Brings the files of the workspace to the current file format version (formatVersion) and names the files of project entities after their names (titles of tasks)",
            c =>
            {
                c.Options.Add(dryRun);
                c.Options.Add(check);
            }, async (parse, ctx) =>
            {
                var readOnly = parse.GetValue(dryRun) || parse.GetValue(check);
                if (ctx.GetOptional<IFileMigration>() is not { } migration)
                {
                    ctx.Print(new { applicable = false },
                        "This workspace is a database: there are no files to migrate (the database schema is not versioned yet)");
                    return;
                }

                var report = await migration.Run(new MigrationOptions(readOnly), ctx.Ct);
                Show(ctx, report, parse.GetValue(dryRun), parse.GetValue(check));

                if (parse.GetValue(check))
                    ctx.ExitCode = report.NeedsAttention ? NeedsMigration : 0;
                else if (report.Newer.Length > 0 || report.Unreadable.Length > 0)
                {
                    // Часть файлов осталась как была — скрипт должен это заметить.
                    ctx.ExitCode = 1;
                    ctx.Error.WriteLine("Error: some files were not migrated (see above)");
                }
            });
    }

    private static void Show(Context ctx, MigrationReport report, bool dryRun, bool check)
    {
        var readOnly = dryRun || check;
        var lines = new List<string>();
        if (readOnly)
            lines.Add("Nothing is written (dry run):");

        foreach (var file in report.Migrated.Take(ShownFiles))
            lines.Add($"  {(readOnly ? "would migrate" : "migrated")}  {file.Path}  (format {file.Version} -> {report.CurrentFormat})");
        if (report.Migrated.Length > ShownFiles)
            lines.Add($"  ... and {report.Migrated.Length - ShownFiles} more");
        foreach (var rename in report.Renamed.Take(ShownFiles))
            lines.Add($"  {(readOnly ? "would rename" : "renamed")}  {rename.From}  ->  {Path.GetFileName(rename.To)}");
        if (report.Renamed.Length > ShownFiles)
            lines.Add($"  ... and {report.Renamed.Length - ShownFiles} more");
        foreach (var file in report.Newer)
            lines.Add($"  skipped  {file.Path}  (format {file.Version} is newer than this Tasker supports ({report.CurrentFormat}): update Tasker)");
        foreach (var problem in report.Unreadable)
            lines.Add($"  skipped  {problem.Path}  ({problem.Reason})");

        if (report.Migrated.Length > 0)
            lines.Add(readOnly
                ? $"{report.Migrated.Length} file(s) would be migrated to format {report.CurrentFormat}"
                : $"Migrated {report.Migrated.Length} file(s) to format {report.CurrentFormat}");
        if (report.Renamed.Length > 0)
            lines.Add(readOnly
                ? $"{report.Renamed.Length} file(s) would be renamed after the names of their entities"
                : $"Renamed {report.Renamed.Length} file(s) after the names of their entities");
        if (!report.NeedsAttention)
            lines.Add($"Nothing to migrate: all {report.Scanned} file(s) are in the current format ({report.CurrentFormat})");

        ctx.Print(new
        {
            applicable = true,
            dryRun,
            check,
            currentFormat = report.CurrentFormat,
            scanned = report.Scanned,
            upToDate = report.UpToDate,
            needsAttention = report.NeedsAttention,
            migrated = report.Migrated,
            renamed = report.Renamed,
            newer = report.Newer,
            unreadable = report.Unreadable
        }, string.Join('\n', lines));
    }
}
