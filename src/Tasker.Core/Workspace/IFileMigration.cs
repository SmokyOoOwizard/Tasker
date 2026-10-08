namespace Tasker.Core.Workspace;

/// <summary>
/// Файл .tasker записан более новым Tasker, чем этот: формат (<c>formatVersion</c>) неизвестен, и читать или перезаписывать его нельзя —
/// можно испортить. Нужно обновить Tasker. В API — 409 с кодом <c>unsupported_format</c>.
/// </summary>
public class UnsupportedFormatException(string message) : TaskerConflictException(message, ConflictCode.UnsupportedFormat);

/// <param name="DryRun">Ничего не записывать, только рассказать, что было бы изменено.</param>
public record MigrationOptions(bool DryRun = false);

/// <param name="Path">Относительно .tasker, через «/».</param>
/// <param name="Version">Версия формата файла (0 — версии в файле нет: записан до введения версии).</param>
public record MigrationFile(string Path, int Version);

/// <param name="From">Прежний путь относительно .tasker, через «/».</param>
/// <param name="To">Новый путь: файл сущности проекта называется по её названию, у задачи — по заголовку (формат 2 у задач, 5 у остальных сущностей).</param>
public record MigrationRename(string From, string To);

/// <param name="Path">Относительно .tasker, через «/».</param>
/// <param name="Reason">Почему файл пропущен: конфликт слияния, ошибка чтения.</param>
public record MigrationProblem(string Path, string Reason);

/// <summary>
/// Что нашла и что сделала миграция файлов.
/// </summary>
/// <param name="CurrentFormat">Версия формата, которую пишет этот Tasker.</param>
/// <param name="Scanned">Сколько файлов сущностей проверено.</param>
/// <param name="UpToDate">Сколько уже в текущем формате.</param>
/// <param name="Migrated">Файлы старого формата: переписаны в текущий (при <c>DryRun</c> — были бы переписаны).</param>
/// <param name="Renamed">Файлы сущностей проекта, имя которых не по названию (старое — Guid, или название изменили мимо Tasker): переименованы (при <c>DryRun</c> — были бы).</param>
/// <param name="Newer">Файлы более нового формата: не тронуты, нужен более новый Tasker.</param>
/// <param name="Unreadable">Файлы, которые не удалось проверить (конфликт слияния git, не читается): не тронуты.</param>
public record MigrationReport(
    int CurrentFormat,
    int Scanned,
    int UpToDate,
    MigrationFile[] Migrated,
    MigrationRename[] Renamed,
    MigrationFile[] Newer,
    MigrationProblem[] Unreadable)
{
    /// <summary>Есть что мигрировать или о чём нужно знать: старые файлы, файлы нового формата, нечитаемые.</summary>
    public bool NeedsAttention => Migrated.Length > 0 || Renamed.Length > 0 || Newer.Length > 0 || Unreadable.Length > 0;
}

/// <summary>
/// Миграция файлов .tasker к текущему формату — только в файловом режиме (в БД сервис не зарегистрирован).
/// Каждый файл сущности хранит <c>formatVersion</c>. Старые файлы Tasker читает всегда (приводя к текущему формату в памяти),
/// а пишет всегда в текущем; эта миграция переписывает на диске те, которые никто не менял. Работает под блокировкой записи.
/// </summary>
public interface IFileMigration
{
    /// <summary>Версия формата, которую пишет этот Tasker.</summary>
    int CurrentFormat { get; }

    Task<MigrationReport> Run(MigrationOptions options, CancellationToken ct = default);
}
