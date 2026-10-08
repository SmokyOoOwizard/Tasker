using Tasker.Storage.Files.Workspaces;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tasker.Configs;
using Tasker.Storage.Db.Configs.Tasker;
using Tasker.Storage.Files.Configs.Tasker;
using Tasker.Web.Workspaces;

namespace Tasker.Desktop;

/// <summary>
/// Что открыть при запуске: <c>--files=&lt;папка&gt;</c> и <c>--sqlite=&lt;файл&gt;</c> — по вкладке на каждое.
/// Пути абсолютные: запрос может прийти от второго запуска с другой текущей папкой (см. <see cref="SingleInstance"/>).
/// </summary>
public record StartupRequest(string[] Folders, string[] SqliteFiles)
{
    public static readonly StartupRequest Empty = new([], []);

    public static StartupRequest FromArgs(string[] args)
    {
        var configs = ConfigsParser.GetConfigs(args);
        var files = configs.Get<FilesConfigs>();
        var db = configs.Get<DbConfigs>();

        return new StartupRequest(
            files.IsSet ? [Path.GetFullPath(files.Path!)] : [],
            string.IsNullOrWhiteSpace(db.SqliteFile) ? [] : [Path.GetFullPath(db.SqliteFile)]);
    }

    public IEnumerable<WorkspaceLocation> Locations() =>
        Folders.Select(WorkspaceLocation.Files).Concat(SqliteFiles.Select(WorkspaceLocation.Sqlite));

    public bool IsEmpty => Folders.Length == 0 && SqliteFiles.Length == 0;
}
