using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Tasker.Core;
using Tasker.Core.Boards;
using Tasker.Core.Dto;
using Tasker.Core.Fields;
using Tasker.Core.Links;
using Tasker.Core.IO;
using Tasker.Core.Projects;
using Tasker.Core.Statuses;
using Tasker.Core.Tasks;
using Tasker.Core.TaskSeries;
using Tasker.Core.Users;
using Tasker.Core.Workspace;
using Tasker.Storage.Files.Storages;
using Tasker.Storage.Files.Workspaces;

namespace Tasker.Storage.Files.Index;

/// <summary>Условия выборки из индекса; null — без ограничения, пустой массив — ничего не подходит.</summary>
internal record IndexQuery(IndexKind Kind)
{
    public Guid? ProjectId { get; init; }
    public Guid[]? Ids { get; init; }

    /// <summary>Id начинается с этого ключа — началом Guid в виде <c>D</c> строчными (<see cref="Tasker.Core.ShortId.TryKey"/>).</summary>
    public string? IdPrefix { get; init; }
    public Guid[]? TypeIds { get; init; }
    public Guid[]? StatusIds { get; init; }

    /// <summary>Задачи, у которых есть номер хотя бы в одной из этих серий (таблица <c>task_series</c>).</summary>
    public Guid[]? SeriesIds { get; init; }

    /// <summary>Задачи, у которых есть исходящая связь одного из этих типов (таблица <c>task_links</c>).</summary>
    public Guid[]? LinkTypeIds { get; init; }

    /// <summary>Задачи, у которых записано хотя бы одно из этих полей (таблица <c>task_fields</c>).</summary>
    public Guid[]? FieldIds { get; init; }

    /// <summary>Задачи, у которых есть собственное поле с одним из этих перечислений (таблица <c>task_fields</c>).</summary>
    public Guid[]? EnumIds { get; init; }

    /// <summary>Все условия по значениям полей (И; таблица <c>task_field_values</c>).</summary>
    public FieldCondition[]? FieldValues { get; init; }

    /// <summary>Порядок задач (<see cref="TaskFilter.Sort"/>); null — по времени создания, затем по id.</summary>
    public TaskSortKey[]? Sort { get; init; }
    public UserKind? UserKind { get; init; }

    /// <summary>Точное значение ключа сортировки — например, нормализованное имя пользователя.</summary>
    public string? SortKey { get; init; }
}

/// <summary>
/// Индекс .tasker в SQLite (<c>.tasker/.cache/index.db</c>, в git не попадает). Файлы — источник правды:
/// индекс хранит для каждого файла его отпечаток (размер, время изменения) и сущность целиком,
/// а списки, страницы и подсчёты идут SQL-запросами по нему.
/// <para>
/// Обновление: при первом обращении — сверка со всеми файлами (перечитываются только изменившиеся),
/// дальше — точечно после своих записей (<see cref="Refresh"/>) и по событиям файловой системы
/// (<see cref="WorkspaceWatcher"/>). Чтение одной сущности и все записи идут мимо индекса, в файлы,
/// поэтому отстающий индекс не может привести к потере данных: версия сверяется с файлом.
/// </para>
/// <para>
/// Индекс — кэш: при смене схемы или повреждении он удаляется и строится заново.
/// Папку одновременно открывают несколько процессов (демон, десктоп, командная строка) и делят один индекс:
/// каждое обращение к нему — чтение, запись, сверка — идёт под короткой блокировкой <c>.cache/index.lock</c>
/// (<see cref="FileLock"/>), которую занимают только на время самого обращения. Соединения с SQLite не держатся
/// в пуле: пока никто не работает с кэшем, файл никем не занят.
/// </para>
/// </summary>
internal sealed partial class WorkspaceIndex : IWorkspaceIndex, IDisposable
{
    // Поднять, если меняется схема или содержимое data: старый индекс пересоберётся.
    private const int SchemaVersion = 10;

    private readonly TaskerDirectory directory;
    private readonly ILogger<WorkspaceIndex> log;
    private readonly string connectionString;
    private readonly Lazy<Task> ready;

    // Свои записи: событие об изменении файла от наблюдателя за ними уже не нужно — индекс их сам показал.
    private static readonly TimeSpan OwnWriteWindow = TimeSpan.FromSeconds(5);
    private readonly ConcurrentDictionary<string, DateTime> ownWrites = new();

    public event Action<IReadOnlyList<WorkspaceFileChange>>? Changed;

    public WorkspaceIndex(TaskerDirectory directory, ILogger<WorkspaceIndex> log)
    {
        this.directory = directory;
        this.log = log;
        connectionString = new SqliteConnectionStringBuilder { DataSource = directory.IndexFile, Pooling = false }.ToString();
        ready = new Lazy<Task>(Initialize);
    }

    /// <summary>Открывает индекс и сверяет его с файлами. Повторные вызовы ждут ту же первую сверку.</summary>
    public Task EnsureReady() => ready.Value;

    public async Task<T[]> All<T>(IndexQuery query, CancellationToken ct = default)
    {
        await EnsureReady();
        return await Cache(async connection =>
        {
            await using var command = Select(connection, query, "data");
            command.CommandText += OrderBy(query);
            return await ReadData<T>(command, ct);
        }, ct);
    }

    public async Task<ListDto<T>> GetRange<T>(IndexQuery query, Page page, CancellationToken ct = default)
    {
        await EnsureReady();
        return await Cache(async connection =>
        {
            await using var count = Select(connection, query, "count(*)");
            var total = Convert.ToInt32(await count.ExecuteScalarAsync(ct));

            await using var command = Select(connection, query, "data");
            command.CommandText += OrderBy(query) + " LIMIT @limit OFFSET @offset";
            command.Parameters.AddWithValue("@limit", (int)page.Limit);
            command.Parameters.AddWithValue("@offset", page.Offset);

            return new ListDto<T>
            {
                TotalCount = total,
                Offset = page.Offset,
                Limit = page.Limit,
                Data = await ReadData<T>(command, ct)
            };
        }, ct);
    }

    /// <summary>Id сущностей по условиям, в порядке запроса; сами сущности не читаются.</summary>
    public async Task<Guid[]> Ids(IndexQuery query, CancellationToken ct = default)
    {
        await EnsureReady();
        return await Cache(async connection =>
        {
            await using var command = Select(connection, query, "id");
            command.CommandText += OrderBy(query);
            var result = new List<Guid>();
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                result.Add(Guid.Parse(reader.GetString(0)));
            return result.ToArray();
        }, ct);
    }

    public async Task<int> Count(IndexQuery query, CancellationToken ct = default)
    {
        await EnsureReady();
        return await Cache(async connection =>
        {
            await using var command = Select(connection, query, "count(*)");
            return Convert.ToInt32(await command.ExecuteScalarAsync(ct));
        }, ct);
    }

    public async Task<T?> FirstOrDefault<T>(IndexQuery query, CancellationToken ct = default) where T : class
    {
        await EnsureReady();
        return await Cache(async connection =>
        {
            await using var command = Select(connection, query, "data");
            command.CommandText += OrderBy(query) + " LIMIT 1";
            return (await ReadData<T>(command, ct)).FirstOrDefault();
        }, ct);
    }

    public async Task<ListDto<WorkspaceProblem>> GetProblems(Page page, CancellationToken ct = default)
    {
        await EnsureReady();
        return await Cache(async connection =>
        {
            await using var count = connection.CreateCommand();
            count.CommandText = "SELECT count(*) FROM files WHERE error IS NOT NULL";
            var total = Convert.ToInt32(await count.ExecuteScalarAsync(ct));

            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT path, error FROM files WHERE error IS NOT NULL ORDER BY path LIMIT @limit OFFSET @offset";
            command.Parameters.AddWithValue("@limit", (int)page.Limit);
            command.Parameters.AddWithValue("@offset", page.Offset);

            var problems = new List<WorkspaceProblem>();
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                problems.Add(new WorkspaceProblem(reader.GetString(0), reader.GetString(1)));

            return new ListDto<WorkspaceProblem> { TotalCount = total, Offset = page.Offset, Limit = page.Limit, Data = problems.ToArray() };
        }, ct);
    }

    public async Task Rescan(CancellationToken ct = default)
    {
        await EnsureReady();
        await Sync([directory.Root], ct);
    }

    /// <summary>
    /// Полная сверка по сигналу наблюдателя за файлами. Индекс общий с другими процессами, и они могли
    /// уже его обновить, — поэтому вкладкам сообщаем об изменении, даже если строки индекса не менялись.
    /// </summary>
    internal async Task RescanExternal(CancellationToken ct = default)
    {
        await EnsureReady();
        await Sync([directory.Root], ct, external: true);
    }

    /// <summary>То же для отдельных путей: файлы, которые менял не этот процесс.</summary>
    internal async Task RefreshExternal(IReadOnlyCollection<string> paths, CancellationToken ct = default)
    {
        await EnsureReady();
        await Sync(paths, ct, external: true);
    }

    /// <summary>
    /// Перечитывает файлы по путям (файл или папка целиком — например, удалённый проект).
    /// Хранилища вызывают после каждой своей записи, чтобы список сразу видел изменение.
    /// </summary>
    public async Task Refresh(IReadOnlyCollection<string> paths, CancellationToken ct = default)
    {
        await EnsureReady();
        var now = DateTime.UtcNow;
        foreach (var stale in ownWrites.Where(x => now - x.Value > OwnWriteWindow).Select(x => x.Key))
            ownWrites.TryRemove(stale, out _);
        foreach (var path in paths)
            ownWrites[Relative(path)] = now;

        await Sync(paths, ct);
    }

    public Task Refresh(string path, CancellationToken ct = default) => Refresh([path], ct);

    /// <summary>
    /// Путь файла сущности проекта (полный) по id — из индекса; null — индекс такого файла не знает. Файл называется по названию
    /// сущности, поэтому по имени его не найти, а id в индексе — из содержимого. Подсказка, а не истина: индекс мог отстать,
    /// поэтому вызывающий проверяет, что файл есть, и ищет по имени, если нет (<see cref="ProjectDirectory.FindFiles"/>).
    /// </summary>
    public async Task<string?> EntityPath(IndexKind kind, Guid projectId, Guid id, CancellationToken ct = default)
    {
        await EnsureReady();
        return await Cache(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT path FROM files WHERE kind = @kind AND project_id = @project AND id = @id AND error IS NULL LIMIT 1";
            command.Parameters.AddWithValue("@kind", kind.ToString());
            command.Parameters.AddWithValue("@project", Key(projectId));
            command.Parameters.AddWithValue("@id", Key(id));
            return await command.ExecuteScalarAsync(ct) is string relative ? Path.Combine(directory.Root, relative.Replace('/', Path.DirectorySeparatorChar)) : null;
        }, ct);
    }

    /// <summary>
    /// Ждёт записи в файл и обновляет его в индексе. Обновляет, даже если запись не прошла
    /// (версия не совпала): значит, файл изменили снаружи, и индекс заодно догонит.
    /// </summary>
    public Task<T> Written<T>(string path, Task<T> write, CancellationToken ct) => Written([path], write, ct);

    /// <summary>То же для записи, которая меняет несколько файлов (например, переименование: прежний и новый путь).</summary>
    public async Task<T> Written<T>(IReadOnlyCollection<string> paths, Task<T> write, CancellationToken ct)
    {
        try
        {
            return await write;
        }
        finally
        {
            await Refresh(paths, ct);
        }
    }

    // Соединения не в пуле — освобождать нечего.
    public void Dispose()
    {
    }

    /// <summary>Обращение к кэшу: под блокировкой <c>index.lock</c>, с открытым и закрытым по его окончании соединением.</summary>
    private Task<T> Cache<T>(Func<SqliteConnection, Task<T>> action, CancellationToken ct)
    {
        var started = PerfTrace.Start();
        return FileLock.Run(directory.IndexLock, async () =>
        {
            await using var connection = await Open(ct);
            var opened = PerfTrace.Start();
            PerfTrace.Count("cache.open", started);
            try
            {
                return await action(connection);
            }
            finally
            {
                PerfTrace.Count("cache.action", opened);
            }
        }, ct);
    }

    private async Task Initialize()
    {
        // Загрузка SQLite (родная библиотека, первые вызовы) — работа процесса, а не индекса: делаем её до блокировки, чтобы остальные
        // процессы не ждали её под index.lock (при десятке одновременных вызовов консоли это основная часть очереди).
        await using (var warm = new SqliteConnection("Data Source=:memory:"))
        {
            await warm.OpenAsync();
            await using var ping = warm.CreateCommand();
            ping.CommandText = "SELECT 1";
            await ping.ExecuteScalarAsync();
        }

        await FileLock.Run(directory.IndexLock, InitializeLocked);
    }

    private async Task InitializeLocked()
    {
        PerfTrace.Mark("index-lock");
        if (OperatingSystem.IsWindows() && Path.GetDirectoryName(directory.Root) is { } workspaceRoot && PathBudget.ExceedsLegacyLimit(workspaceRoot))
            log.LogWarning(
                "The workspace path {Path} is longer than {Max} characters: the longest .tasker files may not fit into 260 characters (MAX_PATH). " +
                "Tasker copes, but git needs 'git config core.longpaths true' and Explorer may not open them. Move the folder closer to the drive root",
                workspaceRoot, PathBudget.MaxWorkspaceRootLength);
        try
        {
            await CreateSchema();
        }
        catch (SqliteException e)
        {
            // Повреждённый или чужой файл: индекс — только кэш, строим заново.
            log.LogWarning(e, "Index {File} is unreadable, rebuilding it", directory.IndexFile);
            foreach (var suffix in new[] { "", "-wal", "-shm" })
                AtomicFile.Delete(directory.IndexFile + suffix);
            await CreateSchema();
        }

        PerfTrace.Mark("index-schema");
        var stopwatch = Stopwatch.StartNew();
        var (updated, removed) = await Sync([directory.Root], CancellationToken.None);
        // Число проблем — отдельное соединение и запрос: считаем, только если запись кто-то увидит (консоль её не пишет).
        if (log.IsEnabled(LogLevel.Information))
            log.LogInformation(
                "Workspace index is ready in {Elapsed} ms: {Updated} files read, {Removed} removed, {Problems} problems",
                stopwatch.ElapsedMilliseconds, updated, removed, await GetProblemsCount());
    }

    private async Task CreateSchema()
    {
        await using var connection = await Open(CancellationToken.None);

        await using var version = connection.CreateCommand();
        version.CommandText = "PRAGMA user_version";
        if (Convert.ToInt32(await version.ExecuteScalarAsync()) == SchemaVersion)
            return;

        await using var create = connection.CreateCommand();
        create.CommandText = $"""
            PRAGMA journal_mode = WAL;
            DROP TABLE IF EXISTS task_series;
            DROP TABLE IF EXISTS task_links;
            DROP TABLE IF EXISTS task_fields;
            DROP TABLE IF EXISTS task_field_values;
            DROP TABLE IF EXISTS files;
            CREATE TABLE files (
                path       TEXT PRIMARY KEY,   -- относительно .tasker, через «/»
                size       INTEGER NOT NULL,
                mtime      INTEGER NOT NULL,   -- ticks UTC
                kind       TEXT NOT NULL,
                project_id TEXT,
                id         TEXT NOT NULL,
                sort_text  TEXT,               -- имя (у пользователя — нормализованное; у задачи — заголовок в нижнем регистре, для упорядочивания)
                sort_num   INTEGER,            -- у задачи — createdAt, ticks UTC
                sort_updated INTEGER,          -- у задачи — updatedAt, ticks UTC
                type_id    TEXT,
                status_id  TEXT,
                user_kind  TEXT,
                data       TEXT,               -- сущность целиком, JSON; null, если файл не прочитан
                error      TEXT                -- почему файл не прочитан; такие файлы не попадают в списки
            );
            CREATE INDEX ix_files_by_name ON files (kind, project_id, sort_text, id);
            CREATE INDEX ix_files_by_created ON files (kind, project_id, sort_num, id);
            CREATE INDEX ix_files_by_updated ON files (kind, project_id, sort_updated, sort_num, id);
            CREATE INDEX ix_files_by_status ON files (kind, project_id, status_id);
            -- номера задач в сериях; строки файла заменяются и удаляются вместе с ним, в той же транзакции
            CREATE TABLE task_series (
                path          TEXT NOT NULL,      -- файл задачи, как в files
                project_id    TEXT NOT NULL,
                series_id     TEXT NOT NULL,
                number        INTEGER NOT NULL,
                task_id       TEXT NOT NULL,
                created_ticks INTEGER NOT NULL    -- createdAt задачи, ticks UTC
            );
            CREATE INDEX ix_task_series_by_number ON task_series (project_id, series_id, number);
            CREATE INDEX ix_task_series_by_path ON task_series (path);
            -- исходящие связи задач; строки файла заменяются и удаляются вместе с ним, в той же транзакции
            CREATE TABLE task_links (
                path       TEXT NOT NULL,         -- файл задачи-источника, как в files
                project_id TEXT NOT NULL,
                type_id    TEXT NOT NULL,         -- тип связи
                source_id  TEXT NOT NULL,
                target_id  TEXT NOT NULL
            );
            CREATE INDEX ix_task_links_by_target ON task_links (project_id, target_id);
            CREATE INDEX ix_task_links_by_source ON task_links (project_id, type_id, source_id);
            CREATE INDEX ix_task_links_by_path ON task_links (path);
            -- поля, записанные в задаче (значения и дополнительные); строки файла заменяются и удаляются вместе с ним, в той же транзакции
            CREATE TABLE task_fields (
                path       TEXT NOT NULL,         -- файл задачи, как в files
                project_id TEXT NOT NULL,
                field_id   TEXT NOT NULL,         -- поле каталога или собственное поле задачи
                enum_id    TEXT,                  -- перечисление собственного поля; у поля каталога null
                own_name   TEXT,                  -- собственное поле: имя в нижнем регистре (FieldNames.Key); у поля каталога null
                own_type   INTEGER                -- собственное поле: тип (FieldType); у поля каталога null
            );
            CREATE INDEX ix_task_fields_by_field ON task_fields (project_id, field_id);
            CREATE INDEX ix_task_fields_by_enum ON task_fields (project_id, enum_id);
            CREATE INDEX ix_task_fields_by_own_name ON task_fields (own_name);
            CREATE INDEX ix_task_fields_by_path ON task_fields (path);
            -- значения полей задач (канонический текст); строки файла заменяются и удаляются вместе с ним, в той же транзакции
            CREATE TABLE task_field_values (
                path       TEXT NOT NULL,         -- файл задачи, как в files
                project_id TEXT NOT NULL,
                field_id   TEXT NOT NULL,
                value      TEXT NOT NULL,
                number     REAL,                  -- значение числом (FieldNumbers); null, если оно не число. Сравнения int и float идут по нему
                own_name   TEXT,                  -- собственное поле: имя в нижнем регистре (FieldNames.Key); у поля каталога null
                own_type   INTEGER                -- собственное поле: тип (FieldType); у поля каталога null
            );
            -- id поля — Guid, общий для проекта и без повторов между проектами, поэтому проект в индексы не входит
            CREATE INDEX ix_task_field_values_by_value ON task_field_values (field_id, value);
            CREATE INDEX ix_task_field_values_by_number ON task_field_values (field_id, number);
            -- собственные поля у каждой задачи со своим id, поэтому ищутся по имени (и типу, он проверяется по строке)
            CREATE INDEX ix_task_field_values_by_own_value ON task_field_values (own_name, value);
            CREATE INDEX ix_task_field_values_by_own_number ON task_field_values (own_name, number);
            CREATE INDEX ix_task_field_values_by_path ON task_field_values (path);
            PRAGMA user_version = {SchemaVersion};
            """;
        await create.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Сверяет индекс с диском внутри указанных путей: удаляет записи о пропавших файлах
    /// и перечитывает новые и изменившиеся (по размеру и времени изменения). Одна транзакция.
    /// </summary>
    private Task<(int Updated, int Removed)> Sync(IReadOnlyCollection<string> paths, CancellationToken ct, bool external = false) =>
        FileLock.Run(directory.IndexLock, async () =>
        {
            var scopes = paths.Select(Relative).Distinct().ToArray();
            // Путь снаружи .tasker (например, через symlink) — не угадываем, сверяем всё.
            if (scopes.Any(x => x.StartsWith("..", StringComparison.Ordinal)))
                scopes = [""];

            await using var connection = await Open(ct);
            await using var transaction = connection.BeginTransaction();

            var indexed = await IndexedStamps(transaction, scopes, ct);
            PerfTrace.Mark("sync-indexed");
            var onDisk = ScanDisk(scopes);
            PerfTrace.Mark("sync-scan");

            // Что изменилось: путь и id сущности — из содержимого файла (а у удалённого — из индекса), не из имени.
            var changed = new List<WorkspaceFileChange>();
            var removed = 0;
            foreach (var path in indexed.Keys.Where(x => !onDisk.ContainsKey(x)))
            {
                await Delete(transaction, path, ct);
                changed.Add(new WorkspaceFileChange(path, IdOf(indexed[path].Id)));
                removed++;
            }

            var updated = 0;
            foreach (var (path, file) in onDisk)
            {
                if (indexed.TryGetValue(path, out var known) && known.Stamp == file.Stamp)
                    continue;

                if (await Read(path, file, ct) is { } row)
                {
                    await Upsert(transaction, row, ct);
                    changed.Add(new WorkspaceFileChange(path, row.EntityId ?? file.Layout.Id));
                }
                else
                {
                    await Delete(transaction, path, ct);
                    changed.Add(new WorkspaceFileChange(path, IdOf(known.Id) ?? file.Layout.Id));
                }
                updated++;
            }

            await transaction.CommitAsync(ct);
            PerfTrace.Mark("sync-commit");

            // Изменил файл другой процесс и уже обновил общий индекс — строки не поменялись, но вкладки об этом не знают.
            if (external)
            {
                var now = DateTime.UtcNow;
                foreach (var scope in scopes)
                {
                    if (!(ownWrites.TryGetValue(scope, out var written) && now - written < OwnWriteWindow) && changed.All(x => x.Path != scope))
                        changed.Add(new WorkspaceFileChange(scope, null));
                }
            }

            if (changed.Count > 0)
                Changed?.Invoke(changed);
            return (updated, removed);
        }, ct);

    private record struct Stamp(long Size, long ModifiedTicks);

    private record DiskFile(string FullPath, LayoutEntry Layout, Stamp Stamp);

    private record IndexRow(
        string Path, Stamp Stamp, LayoutEntry Layout,
        string? SortText = null, long? SortNum = null, long? SortUpdated = null, Guid? TypeId = null, Guid? StatusId = null, UserKind? UserKind = null,
        string? Data = null, string? Error = null, IReadOnlyList<TaskSeriesNumber>? Numbers = null, IReadOnlyList<TaskLink>? Links = null,
        IReadOnlyList<TaskField>? Fields = null, Guid? EntityId = null)
    {
        /// <summary>Значение колонки id: id сущности; у нечитаемого файла — из имени (полный Guid) или само имя.</summary>
        public string IdKey => EntityId is { } entity ? Key(entity) : Layout.Id is { } named ? Key(named) : Layout.Stem;
    }

    private Dictionary<string, DiskFile> ScanDisk(string[] scopes)
    {
        var result = new Dictionary<string, DiskFile>();
        foreach (var scope in scopes)
        {
            var full = scope == "" ? directory.Root : Path.Combine(directory.Root, scope);
            IEnumerable<string> files;
            if (File.Exists(full))
                files = [full];
            else if (Directory.Exists(full))
                files = Directory.EnumerateFiles(full, "*" + YamlFile.Extension,
                    new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true });
            else
                continue;

            try
            {
                foreach (var file in files)
                {
                    var relative = Relative(file);
                    if (WorkspaceLayout.Classify(relative) is not { } layout)
                        continue;

                    var info = new FileInfo(file);
                    if (info.Exists)
                        result[relative] = new DiskFile(file, layout, new Stamp(info.Length, info.LastWriteTimeUtc.Ticks));
                }
            }
            catch (DirectoryNotFoundException)
            {
                // Папку удалили посреди обхода (например, git checkout) — её события придут следом.
            }
        }
        return result;
    }

    /// <returns>null — файла уже нет.</returns>
    private async Task<IndexRow?> Read(string path, DiskFile file, CancellationToken ct)
    {
        var layout = file.Layout;
        try
        {
            string text;
            try
            {
                text = await AtomicFile.ReadAllText(file.FullPath, ct);
            }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
            {
                return null;
            }

            if (ConflictMarker().Match(text) is { Success: true } marker)
            {
                var line = text[..marker.Index].Count(x => x == '\n') + 1;
                return Problem($"Unresolved git merge conflict (line {line})");
            }

            var row = layout.Kind switch
            {
                IndexKind.Project => Row(await ProjectFile.Read(file.FullPath, ct), x => x.Id, x => new() { SortText = x.Name }),
                IndexKind.User => Row(await UserFile.Read(file.FullPath, ct), x => x.Id,
                    x => new() { SortText = UserStorage.Normalize(x.Username), UserKind = x.Kind }),
                IndexKind.Status => Row(await StatusFile.Read(layout.ProjectId!.Value, file.FullPath, ct), x => x.Id,
                    x => new() { SortText = x.Name }),
                IndexKind.StatusSet => Row(await StatusSetFile.Read(layout.ProjectId!.Value, file.FullPath, ct), x => x.Id,
                    x => new() { SortText = x.Name }),
                IndexKind.TaskType => Row(await TaskTypeFile.Read(layout.ProjectId!.Value, file.FullPath, ct), x => x.Id,
                    x => new() { SortText = x.Name }),
                IndexKind.Board => Row(await BoardFile.Read(layout.ProjectId!.Value, file.FullPath, ct), x => x.Id,
                    x => new() { SortText = x.Name }),
                // Префикс — ключ сортировки; сравнение в SQLite двоичное, поэтому TSK и tsk — разные, а порядок один во всех клонах.
                IndexKind.Series => Row(await SeriesFile.Read(layout.ProjectId!.Value, file.FullPath, ct), x => x.Id,
                    x => new() { SortText = x.Prefix }),
                IndexKind.Task => Row(await TaskFile.Read(layout.ProjectId!.Value, file.FullPath, ct), x => x.Id,
                    x => new() { SortText = TaskSortNames.Fold(x.Title), SortNum = x.CreatedAt.UtcTicks, SortUpdated = x.UpdatedAt.UtcTicks, TypeId = x.TypeId, StatusId = x.StatusId, Numbers = x.SeriesNumbers, Links = x.Links, Fields = x.Fields }),
                IndexKind.LinkType => Row(await LinkTypeFile.Read(layout.ProjectId!.Value, file.FullPath, ct), x => x.Id,
                    x => new() { SortText = x.Name }),
                IndexKind.Field => Row(await FieldFile.Read(layout.ProjectId!.Value, file.FullPath, ct), x => x.Id,
                    x => new() { SortText = x.Name }),
                IndexKind.FieldEnum => Row(await FieldEnumFile.Read(layout.ProjectId!.Value, file.FullPath, ct), x => x.Id,
                    x => new() { SortText = x.Name }),
                _ => throw new ArgumentOutOfRangeException(nameof(layout), layout.Kind, null)
            };
            return row;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return Problem(e.Message);
        }

        IndexRow Problem(string error)
        {
            log.LogWarning("Cannot read {Path}: {Error}", path, error);
            return new IndexRow(path, file.Stamp, layout, Error: error);
        }

        IndexRow Row<T>(T? entity, Func<T, Guid> id, Func<T, Columns> columns) where T : class
        {
            if (entity == null)
                return Problem("File is empty");
            if (layout.Id is { } named && id(entity) != named)
                return Problem($"Id in the file ({id(entity)}) does not match the file name");
            // Имя по заголовку: из id в нём только начало.
            if (layout.IdPrefix is { } prefix && !EntityFileNames.IdPrefix(id(entity)).Equals(prefix, StringComparison.Ordinal))
                return Problem($"Id in the file ({id(entity)}) does not match the end of the file name ({prefix})");

            var c = columns(entity);
            return new IndexRow(path, file.Stamp, layout, c.SortText, c.SortNum, c.SortUpdated, c.TypeId, c.StatusId, c.UserKind,
                IndexJsonContext.Serialize(entity), Numbers: c.Numbers, Links: c.Links, Fields: c.Fields, EntityId: id(entity));
        }
    }

    private record Columns
    {
        public string? SortText { get; init; }
        public long? SortNum { get; init; }
        public long? SortUpdated { get; init; }
        public Guid? TypeId { get; init; }
        public Guid? StatusId { get; init; }
        public UserKind? UserKind { get; init; }

        /// <summary>Номера задачи в сериях — строки <c>task_series</c>.</summary>
        public IReadOnlyList<TaskSeriesNumber>? Numbers { get; init; }

        /// <summary>Исходящие связи задачи — строки <c>task_links</c>.</summary>
        public IReadOnlyList<TaskLink>? Links { get; init; }

        /// <summary>Поля, записанные в задаче, — строки <c>task_fields</c>.</summary>
        public IReadOnlyList<TaskField>? Fields { get; init; }
    }

    /// <summary>Что индекс знает о файле: отпечаток и значение колонки id (id сущности из содержимого; у нечитаемого файла — из имени).</summary>
    private record struct Known(Stamp Stamp, string Id);

    private static async Task<Dictionary<string, Known>> IndexedStamps(SqliteTransaction transaction, string[] scopes, CancellationToken ct)
    {
        var result = new Dictionary<string, Known>();
        foreach (var scope in scopes)
        {
            await using var command = Command(transaction);
            command.CommandText = scope == ""
                ? "SELECT path, size, mtime, id FROM files"
                : "SELECT path, size, mtime, id FROM files WHERE path = @scope OR substr(path, 1, length(@prefix)) = @prefix";
            command.Parameters.AddWithValue("@scope", scope);
            command.Parameters.AddWithValue("@prefix", scope + "/");

            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                result[reader.GetString(0)] = new Known(new Stamp(reader.GetInt64(1), reader.GetInt64(2)), reader.GetString(3));
        }
        return result;
    }

    /// <summary>Id из колонки id: у нечитаемого файла там имя без расширения, а не Guid — тогда id неизвестен.</summary>
    private static Guid? IdOf(string? column) => Guid.TryParse(column, out var id) ? id : null;

    private static async Task Upsert(SqliteTransaction transaction, IndexRow row, CancellationToken ct)
    {
        await using var command = Command(transaction);
        command.CommandText = """
            INSERT OR REPLACE INTO files
                (path, size, mtime, kind, project_id, id, sort_text, sort_num, sort_updated, type_id, status_id, user_kind, data, error)
            VALUES
                (@path, @size, @mtime, @kind, @project, @id, @sortText, @sortNum, @sortUpdated, @type, @status, @userKind, @data, @error)
            """;
        command.Parameters.AddWithValue("@path", row.Path);
        command.Parameters.AddWithValue("@size", row.Stamp.Size);
        command.Parameters.AddWithValue("@mtime", row.Stamp.ModifiedTicks);
        command.Parameters.AddWithValue("@kind", row.Layout.Kind.ToString());
        command.Parameters.AddWithValue("@project", Db(row.Layout.ProjectId));
        command.Parameters.AddWithValue("@id", row.IdKey);
        command.Parameters.AddWithValue("@sortText", (object?)row.SortText ?? DBNull.Value);
        command.Parameters.AddWithValue("@sortNum", (object?)row.SortNum ?? DBNull.Value);
        command.Parameters.AddWithValue("@sortUpdated", (object?)row.SortUpdated ?? DBNull.Value);
        command.Parameters.AddWithValue("@type", Db(row.TypeId));
        command.Parameters.AddWithValue("@status", Db(row.StatusId));
        command.Parameters.AddWithValue("@userKind", (object?)row.UserKind?.ToString() ?? DBNull.Value);
        command.Parameters.AddWithValue("@data", (object?)row.Data ?? DBNull.Value);
        command.Parameters.AddWithValue("@error", (object?)row.Error ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(ct);

        await DeleteNumbers(transaction, row.Path, ct);
        if (row.Layout.ProjectId is { } linkProject && row.Links != null)
        {
            foreach (var link in row.Links.Distinct())
            {
                await using var insertLink = Command(transaction);
                insertLink.CommandText =
                    "INSERT INTO task_links (path, project_id, type_id, source_id, target_id) " +
                    "VALUES (@path, @project, @type, @source, @target)";
                insertLink.Parameters.AddWithValue("@path", row.Path);
                insertLink.Parameters.AddWithValue("@project", Key(linkProject));
                insertLink.Parameters.AddWithValue("@type", Key(link.TypeId));
                insertLink.Parameters.AddWithValue("@source", row.IdKey);
                insertLink.Parameters.AddWithValue("@target", Key(link.TargetId));
                await insertLink.ExecuteNonQueryAsync(ct);
            }
        }

        if (row.Layout.ProjectId is { } fieldProject && row.Fields != null)
        {
            foreach (var field in row.Fields.DistinctBy(x => x.FieldId))
            {
                await using var insertField = Command(transaction);
                insertField.CommandText =
                    "INSERT INTO task_fields (path, project_id, field_id, enum_id, own_name, own_type) VALUES (@path, @project, @field, @enum, @ownName, @ownType)";
                insertField.Parameters.AddWithValue("@path", row.Path);
                insertField.Parameters.AddWithValue("@project", Key(fieldProject));
                insertField.Parameters.AddWithValue("@field", Key(field.FieldId));
                insertField.Parameters.AddWithValue("@enum", Db(field.Own?.EnumId));
                var ownName = field.Own == null ? null : FieldNames.Key(field.Own.Name);
                var ownType = field.Own == null ? null : (object)(int)field.Own.Type;
                insertField.Parameters.AddWithValue("@ownName", (object?)ownName ?? DBNull.Value);
                insertField.Parameters.AddWithValue("@ownType", ownType ?? DBNull.Value);
                await insertField.ExecuteNonQueryAsync(ct);

                foreach (var value in field.Values.Distinct())
                {
                    await using var insertValue = Command(transaction);
                    insertValue.CommandText =
                        "INSERT INTO task_field_values (path, project_id, field_id, value, number, own_name, own_type) " +
                        "VALUES (@path, @project, @field, @value, @number, @ownName, @ownType)";
                    insertValue.Parameters.AddWithValue("@path", row.Path);
                    insertValue.Parameters.AddWithValue("@project", Key(fieldProject));
                    insertValue.Parameters.AddWithValue("@field", Key(field.FieldId));
                    insertValue.Parameters.AddWithValue("@value", value);
                    insertValue.Parameters.AddWithValue("@number", (object?)FieldNumbers.Parse(value) ?? DBNull.Value);
                    insertValue.Parameters.AddWithValue("@ownName", (object?)ownName ?? DBNull.Value);
                    insertValue.Parameters.AddWithValue("@ownType", ownType ?? DBNull.Value);
                    await insertValue.ExecuteNonQueryAsync(ct);
                }
            }
        }

        if (row.Numbers == null || row.Layout.ProjectId is not { } projectId)
            return;
        foreach (var number in row.Numbers)
        {
            await using var insert = Command(transaction);
            insert.CommandText = """
                INSERT INTO task_series (path, project_id, series_id, number, task_id, created_ticks)
                VALUES (@path, @project, @series, @number, @task, @created)
                """;
            insert.Parameters.AddWithValue("@path", row.Path);
            insert.Parameters.AddWithValue("@project", Key(projectId));
            insert.Parameters.AddWithValue("@series", Key(number.SeriesId));
            insert.Parameters.AddWithValue("@number", number.Number);
            insert.Parameters.AddWithValue("@task", row.IdKey);
            insert.Parameters.AddWithValue("@created", row.SortNum ?? 0);
            await insert.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task DeleteNumbers(SqliteTransaction transaction, string path, CancellationToken ct)
    {
        await using var command = Command(transaction);
        command.CommandText = "DELETE FROM task_series WHERE path = @path";
        command.Parameters.AddWithValue("@path", path);
        await command.ExecuteNonQueryAsync(ct);

        await using var links = Command(transaction);
        links.CommandText = "DELETE FROM task_links WHERE path = @path";
        links.Parameters.AddWithValue("@path", path);
        await links.ExecuteNonQueryAsync(ct);

        await using var fields = Command(transaction);
        fields.CommandText = "DELETE FROM task_fields WHERE path = @path";
        fields.Parameters.AddWithValue("@path", path);
        await fields.ExecuteNonQueryAsync(ct);

        await using var values = Command(transaction);
        values.CommandText = "DELETE FROM task_field_values WHERE path = @path";
        values.Parameters.AddWithValue("@path", path);
        await values.ExecuteNonQueryAsync(ct);
    }

    private static async Task Delete(SqliteTransaction transaction, string path, CancellationToken ct)
    {
        await using var command = Command(transaction);
        command.CommandText = "DELETE FROM files WHERE path = @path";
        command.Parameters.AddWithValue("@path", path);
        await command.ExecuteNonQueryAsync(ct);
        await DeleteNumbers(transaction, path, ct);
    }

    private static SqliteCommand Select(SqliteConnection connection, IndexQuery query, string what)
    {
        var command = connection.CreateCommand();
        var where = new List<string> { "kind = @kind", "error IS NULL" };
        command.Parameters.AddWithValue("@kind", query.Kind.ToString());

        if (query.ProjectId is { } projectId)
        {
            where.Add("project_id = @project");
            command.Parameters.AddWithValue("@project", Key(projectId));
        }
        if (query.IdPrefix is { } idPrefix)
        {
            // Ключ — шестнадцатеричные цифры и дефисы (ShortId.TryKey): знаков LIKE в нём нет.
            where.Add("id LIKE @idPrefix");
            command.Parameters.AddWithValue("@idPrefix", idPrefix + "%");
        }
        if (query.UserKind is { } userKind)
        {
            where.Add("user_kind = @userKind");
            command.Parameters.AddWithValue("@userKind", userKind.ToString());
        }
        if (query.SortKey is { } sortKey)
        {
            where.Add("sort_text = @sortKey");
            command.Parameters.AddWithValue("@sortKey", sortKey);
        }
        AddIn("id", "@ids", query.Ids);
        AddIn("type_id", "@types", query.TypeIds);
        AddIn("status_id", "@statuses", query.StatusIds);
        if (query.SeriesIds is { } seriesIds)
        {
            where.Add("path IN (SELECT path FROM task_series WHERE series_id IN (SELECT value FROM json_each(@series)))");
            command.Parameters.AddWithValue("@series", IndexJsonContext.Array(seriesIds.Select(Key)));
        }

        if (query.LinkTypeIds is { } linkTypeIds)
        {
            where.Add("path IN (SELECT path FROM task_links WHERE type_id IN (SELECT value FROM json_each(@linkTypes)))");
            command.Parameters.AddWithValue("@linkTypes", IndexJsonContext.Array(linkTypeIds.Select(Key)));
        }

        if (query.FieldIds is { } fieldIds)
        {
            where.Add("path IN (SELECT path FROM task_fields WHERE field_id IN (SELECT value FROM json_each(@fields)))");
            command.Parameters.AddWithValue("@fields", IndexJsonContext.Array(fieldIds.Select(Key)));
        }

        if (query.EnumIds is { } enumIds)
        {
            where.Add("path IN (SELECT path FROM task_fields WHERE enum_id IN (SELECT value FROM json_each(@enums)))");
            command.Parameters.AddWithValue("@enums", IndexJsonContext.Array(enumIds.Select(Key)));
        }

        for (var i = 0; i < (query.FieldValues?.Length ?? 0); i++)
        {
            var condition = query.FieldValues![i];
            where.Add(FieldSql(condition, i));
            if (condition.FieldId is { } catalogField)
                command.Parameters.AddWithValue($"@field{i}", Key(catalogField));
            command.Parameters.AddWithValue($"@own{i}", condition.Key);
            command.Parameters.AddWithValue($"@ownType{i}", (int)condition.Type);
            if (condition.Value != null)
                command.Parameters.AddWithValue($"@value{i}", condition.Value);
            if (condition.Number is { } number)
                command.Parameters.AddWithValue($"@number{i}", number);
            if (condition.TypeIds != null)
                command.Parameters.AddWithValue($"@fieldTypes{i}", IndexJsonContext.Array(condition.TypeIds.Select(Key)));
        }

        command.CommandText = $"SELECT {what} FROM files WHERE {string.Join(" AND ", where)}";
        return command;

        // Список id — одним параметром через json_each, без ограничения на число параметров.
        void AddIn(string column, string parameter, Guid[]? ids)
        {
            if (ids == null)
                return;
            where.Add($"{column} IN (SELECT value FROM json_each({parameter}))");
            command.Parameters.AddWithValue(parameter, IndexJsonContext.Array(ids.Select(Key)));
        }
    }

    /// <summary>
    /// Условие по полю (<see cref="FieldCondition"/>) в SQL. «Есть значение, которое…» — подзапрос к <c>task_field_values</c>;
    /// «нет значения…» — его отрицание (поэтому <c>!=</c> и <c>:unset</c> берут и задачи без значения). Строка относится к условию, если это поле каталога
    /// (<c>field_id</c>) или собственное поле с тем же именем и типом (<c>own_name</c>, <c>own_type</c>). Подключённость поля: тип задачи из списка
    /// типов, где поле каталога есть, или запись о поле в самой задаче (<c>task_fields</c>).
    /// </summary>
    private static string FieldSql(FieldCondition c, int i)
    {
        var (value, number) = ($"@value{i}", $"@number{i}");
        var row = $"(own_name = @own{i} AND own_type = @ownType{i})";
        if (c.FieldId != null)
            row = $"(field_id = @field{i} OR {row})";
        var values = $"SELECT path FROM task_field_values WHERE {row}";
        var attached = $"path IN (SELECT path FROM task_fields WHERE {row})";
        if (c.TypeIds != null)
            attached = $"(type_id IN (SELECT value FROM json_each(@fieldTypes{i})) OR {attached})";
        return c.Operator switch
        {
            FieldOperator.Equal => $"path IN ({values} AND value = {value})",
            FieldOperator.NotEqual => $"path NOT IN ({values} AND value = {value})",
            FieldOperator.Greater => Ordered(">"),
            FieldOperator.GreaterOrEqual => Ordered(">="),
            FieldOperator.Less => Ordered("<"),
            FieldOperator.LessOrEqual => Ordered("<="),
            FieldOperator.Set => $"path IN ({values})",
            FieldOperator.Unset => $"path NOT IN ({values})",
            FieldOperator.Attached => attached,
            FieldOperator.Detached => $"NOT {attached}",
            _ => throw new ArgumentOutOfRangeException(nameof(c), c.Operator, null)
        };

        // int и float — по числовому столбцу, date — по тексту yyyy-MM-dd (сортируется как текст).
        string Ordered(string sign) => c.Number != null
            ? $"path IN ({values} AND number {sign} {number})"
            : $"path IN ({values} AND value {sign} {value})";
    }

    private static async Task<T[]> ReadData<T>(SqliteCommand command, CancellationToken ct)
    {
        var result = new List<T>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(IndexJsonContext.Deserialize<T>(reader.GetString(0)));
        return result.ToArray();
    }

    /// <summary>Сколько файлов внутри папки <paramref name="folder"/> не удалось прочитать.</summary>
    public async Task<int> CountProblems(string folder, CancellationToken ct = default)
    {
        await EnsureReady();
        var prefix = Relative(folder) + "/";
        return await Cache(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT count(*) FROM files WHERE error IS NOT NULL AND substr(path, 1, length(@prefix)) = @prefix";
            command.Parameters.AddWithValue("@prefix", prefix);
            return Convert.ToInt32(await command.ExecuteScalarAsync(ct));
        }, ct);
    }

    /// <summary>Наибольший номер в серии; 0 — номеров нет. Серию не проверяем: недействительные ссылки тоже считаются.</summary>
    public async Task<int> MaxNumber(Guid projectId, Guid seriesId, CancellationToken ct = default)
    {
        await EnsureReady();
        return await Cache(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT coalesce(max(number), 0) FROM task_series WHERE project_id = @project AND series_id = @series";
            command.Parameters.AddWithValue("@project", Key(projectId));
            command.Parameters.AddWithValue("@series", Key(seriesId));
            return Convert.ToInt32(await command.ExecuteScalarAsync(ct));
        }, ct);
    }

    /// <summary>Различные пары «тип, перечисление» у собственных полей задач проекта с этим именем (<c>task_fields</c>).</summary>
    public async Task<OwnFieldKind[]> OwnFieldKinds(Guid projectId, string nameKey, CancellationToken ct = default)
    {
        await EnsureReady();
        return await Cache(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT DISTINCT own_type, enum_id FROM task_fields WHERE own_name = @name AND project_id = @project";
            command.Parameters.AddWithValue("@name", nameKey);
            command.Parameters.AddWithValue("@project", Key(projectId));
            var result = new List<OwnFieldKind>();
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                result.Add(new OwnFieldKind((FieldType)reader.GetInt32(0), reader.IsDBNull(1) ? null : Guid.Parse(reader.GetString(1))));
            return result.ToArray();
        }, ct);
    }

    /// <summary>Задачи с этим номером в серии: по createdAt, затем по id.</summary>
    public async Task<TaskItem[]> TasksByNumber(Guid projectId, Guid seriesId, int number, CancellationToken ct = default)
    {
        await EnsureReady();
        return await Cache(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT data FROM files WHERE path IN (
                    SELECT path FROM task_series WHERE project_id = @project AND series_id = @series AND number = @number)
                ORDER BY sort_num, id
                """;
            command.Parameters.AddWithValue("@project", Key(projectId));
            command.Parameters.AddWithValue("@series", Key(seriesId));
            command.Parameters.AddWithValue("@number", number);
            return await ReadData<TaskItem>(command, ct);
        }, ct);
    }

    /// <summary>Число входящих связей у каждой из задач (строки <c>task_links</c> по цели); у задач без входящих связей записи нет.</summary>
    public async Task<Dictionary<Guid, int>> CountLinkedTo(Guid projectId, Guid[] targetIds, CancellationToken ct = default)
    {
        if (targetIds.Length == 0)
            return [];

        await EnsureReady();
        return await Cache(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT target_id, count(*) FROM task_links WHERE project_id = @project " +
                "AND target_id IN (SELECT value FROM json_each(@targets)) GROUP BY target_id";
            command.Parameters.AddWithValue("@project", Key(projectId));
            command.Parameters.AddWithValue("@targets", IndexJsonContext.Array(targetIds.Select(Key)));

            var counts = new Dictionary<Guid, int>();
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                counts[Guid.Parse(reader.GetString(0))] = reader.GetInt32(1);
            return counts;
        }, ct);
    }

    /// <summary>Исходящие связи типа у перечисленных задач (индекс <c>task_links</c> по проекту, типу и источнику): источник → цели.</summary>
    public async Task<Dictionary<Guid, Guid[]>> LinkTargets(Guid projectId, Guid typeId, Guid[] sourceIds, CancellationToken ct = default)
    {
        if (sourceIds.Length == 0)
            return [];

        await EnsureReady();
        return await Cache(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT DISTINCT source_id, target_id FROM task_links WHERE project_id = @project AND type_id = @type " +
                "AND source_id IN (SELECT value FROM json_each(@sources))";
            command.Parameters.AddWithValue("@project", Key(projectId));
            command.Parameters.AddWithValue("@type", Key(typeId));
            command.Parameters.AddWithValue("@sources", IndexJsonContext.Array(sourceIds.Select(Key)));

            var result = new Dictionary<Guid, List<Guid>>();
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var source = Guid.Parse(reader.GetString(0));
                if (!result.TryGetValue(source, out var list))
                    result[source] = list = [];
                list.Add(Guid.Parse(reader.GetString(1)));
            }
            return result.ToDictionary(x => x.Key, x => x.Value.ToArray());
        }, ct);
    }

    /// <summary>Все связи типа проекта (источник → цель).</summary>
    public async Task<LinkEdge[]> LinkEdges(Guid projectId, Guid typeId, CancellationToken ct = default)
    {
        await EnsureReady();
        return await Cache(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT DISTINCT source_id, target_id FROM task_links WHERE project_id = @project AND type_id = @type";
            command.Parameters.AddWithValue("@project", Key(projectId));
            command.Parameters.AddWithValue("@type", Key(typeId));

            var result = new List<LinkEdge>();
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                result.Add(new LinkEdge(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1))));
            return result.ToArray();
        }, ct);
    }

    /// <summary>Задачи проекта с исходящей связью на <paramref name="targetId"/>: по createdAt, затем по id.</summary>
    public async Task<TaskItem[]> TasksLinkedTo(Guid projectId, Guid targetId, CancellationToken ct = default)
    {
        await EnsureReady();
        return await Cache(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT data FROM files WHERE path IN (" +
                "SELECT path FROM task_links WHERE project_id = @project AND target_id = @target) " +
                "ORDER BY sort_num, id";
            command.Parameters.AddWithValue("@project", Key(projectId));
            command.Parameters.AddWithValue("@target", Key(targetId));
            return await ReadData<TaskItem>(command, ct);
        }, ct);
    }

    /// <summary>
    /// Задачи со связью на задачу или тип, которых нет: по createdAt, затем по id. Задача «есть», если её файл прочитан;
    /// нечитаемые файлы чистка учитывает сама (<see cref="CountProblems"/>).
    /// </summary>
    public async Task<TaskItem[]> TasksWithInvalidLinks(Guid projectId, Guid[] knownTypeIds, CancellationToken ct = default)
    {
        await EnsureReady();
        return await Cache(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT data FROM files WHERE kind = 'Task' AND project_id = @project AND error IS NULL AND path IN (" +
                "SELECT l.path FROM task_links l WHERE l.project_id = @project AND (" +
                "l.type_id NOT IN (SELECT value FROM json_each(@known)) OR NOT EXISTS (" +
                "SELECT 1 FROM files t WHERE t.kind = 'Task' AND t.project_id = @project AND t.error IS NULL AND t.id = l.target_id))) " +
                "ORDER BY sort_num, id";
            command.Parameters.AddWithValue("@project", Key(projectId));
            command.Parameters.AddWithValue("@known", IndexJsonContext.Array(knownTypeIds.Select(Key)));
            return await ReadData<TaskItem>(command, ct);
        }, ct);
    }

    /// <summary>Номера, которые есть у нескольких задач: по серии и номеру; задачи — по createdAt, затем по id.</summary>
    public async Task<NumberConflict[]> NumberConflicts(Guid projectId, CancellationToken ct = default)
    {
        await EnsureReady();
        return await Cache(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT DISTINCT series_id, number, task_id, created_ticks FROM task_series
                WHERE project_id = @project AND (series_id, number) IN (
                    SELECT series_id, number FROM task_series WHERE project_id = @project
                    GROUP BY series_id, number HAVING count(DISTINCT task_id) > 1)
                ORDER BY series_id, number, created_ticks, task_id
                """;
            command.Parameters.AddWithValue("@project", Key(projectId));

            var result = new List<NumberConflict>();
            (Guid Series, int Number)? current = null;
            var tasks = new List<Guid>();
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var key = (Guid.Parse(reader.GetString(0)), reader.GetInt32(1));
                if (current != null && current != key)
                {
                    result.Add(new NumberConflict(current.Value.Series, current.Value.Number, tasks.ToArray()));
                    tasks.Clear();
                }
                current = key;
                tasks.Add(Guid.Parse(reader.GetString(2)));
            }
            if (current != null)
                result.Add(new NumberConflict(current.Value.Series, current.Value.Number, tasks.ToArray()));
            return result.ToArray();
        }, ct);
    }

    /// <summary>Задачи с номером в серии вне <paramref name="knownSeriesIds"/>: по createdAt, затем по id.</summary>
    public async Task<TaskItem[]> TasksWithSeriesNotIn(Guid projectId, Guid[] knownSeriesIds, CancellationToken ct = default)
    {
        await EnsureReady();
        return await Cache(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT data FROM files WHERE path IN (
                    SELECT path FROM task_series WHERE project_id = @project
                        AND series_id NOT IN (SELECT value FROM json_each(@known)))
                ORDER BY sort_num, id
                """;
            command.Parameters.AddWithValue("@project", Key(projectId));
            command.Parameters.AddWithValue("@known", IndexJsonContext.Array(knownSeriesIds.Select(Key)));
            return await ReadData<TaskItem>(command, ct);
        }, ct);
    }

    private async Task<int> GetProblemsCount()
    {
        await using var connection = await Open(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM files WHERE error IS NOT NULL";
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private async Task<SqliteConnection> Open(CancellationToken ct)
    {
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(ct);
        // lower() в SQLite знает только ASCII; порядок задач по тексту без учёта регистра (Unicode) использует то же правило, что и индекс (TaskSortNames.Fold).
        connection.CreateFunction("lower", (string? text) => text == null ? null : TaskSortNames.Fold(text), isDeterministic: true);
        return connection;
    }

    private static SqliteCommand Command(SqliteTransaction transaction)
    {
        var command = transaction.Connection!.CreateCommand();
        command.Transaction = transaction;
        return command;
    }

    /// <summary>Путь относительно .tasker через «/»; сам .tasker — пустая строка.</summary>
    private string Relative(string path)
    {
        var relative = Path.GetRelativePath(directory.Root, Path.GetFullPath(path)).Replace('\\', '/');
        return relative == "." ? "" : relative;
    }

    private static string Key(Guid id) => id.ToString("D");

    private static object Db(Guid? id) => id is { } value ? Key(value) : DBNull.Value;

    // Маркеры конфликта git в начале строки. Внутри многострочного описания YAML их бы проглотил молча.
    [GeneratedRegex(@"^(<{7}|>{7})( |$)|^={7}$", RegexOptions.Multiline)]
    private static partial Regex ConflictMarker();
}
