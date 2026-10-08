using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Tasker.Core.IO;
using Tasker.Core.Workspace;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.EventEmitters;
using YamlDotNet.Serialization.NamingConventions;

namespace Tasker.Storage.Files.Storages;

/// <summary>Модель файла и версия файла, из которого она прочитана.</summary>
internal record Versioned<T>(T Model, string Version);

/// <summary>
/// Общее чтение и запись YAML-файлов в .tasker: одно поле на строку, многострочные тексты
/// блоками (<c>|</c>), поэтому конфликты слияния в git правятся руками построчно.
/// <para>
/// Версия файла — хэш его содержимого (см. Tasker.Core.Versioning), а не поле внутри файла:
/// отдельная строка <c>version:</c> конфликтовала бы при каждом git-слиянии, даже когда
/// правки в разных полях. Любое изменение файла — в Tasker, руками или через git — меняет версию.
/// </para>
/// </summary>
internal static class YamlFile
{
    public const string Extension = ".yaml";

    // Неизвестные поля пропускаем: файл мог записать более новый Tasker.
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .WithTypeConverter(new DateTimeOffsetYamlConverter())
        .IgnoreUnmatchedProperties()
        .Build();

    // Порядок полей в файле — порядок свойств в модели файла.
    // Без якорей (&a / *a) для одинаковых значений — файл должен читаться и правиться руками.
    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .WithTypeConverter(new DateTimeOffsetYamlConverter())
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
        .WithEventEmitter(next => new MultilineLiteralEmitter(next))
        // Всегда LF, а не Environment.NewLine: файлы одинаковы на всех системах (иначе на Windows каждая запись дала бы дифф во всех строках).
        .WithNewLine("\n")
        // Строка, которую без кавычек прочли бы как null, число или bool («null», «~», «007», «true»), пишется в кавычках:
        // значения полей и заголовки — произвольный текст, и «null» не должен стать пустотой.
        .WithQuotingNecessaryStrings()
        .DisableAliases()
        .Build();

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    // Блокировки «сравнить версию и записать» — по одной на файл, в пределах процесса.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new(PathRules.Comparer);

    public static async Task<Versioned<T>?> Read<T>(string path, CancellationToken ct) where T : class
    {
        var bytes = await ReadBytes(path, ct);
        if (bytes == null)
            return null;

        // Старый формат приводится к текущему в памяти, файл на диске не меняется. Версия сущности — хэш настоящих байтов файла.
        var model = Deserializer.Deserialize<T?>(FormatVersions.Upgrade(Decode(bytes), path));
        return model == null ? null : new Versioned<T>(model, Hash(bytes));
    }

    /// <summary>Запись нового файла. Возвращает его версию.</summary>
    public static Task<string> Write<T>(string path, T model, CancellationToken ct) where T : class =>
        WithLock(path, ct, () => WriteUnlocked(path, model, ct));

    /// <summary>
    /// Перезапись, только если файл не изменился с версии <paramref name="expectedVersion"/>.
    /// Возвращает новую версию или null, если файл изменён или удалён.
    /// </summary>
    public static Task<string?> WriteIfMatch<T>(string path, T model, string expectedVersion, CancellationToken ct)
        where T : class =>
        WithLock(path, ct, async () =>
            await CurrentVersion(path, ct) == expectedVersion
                ? await WriteUnlocked(path, model, ct)
                : null);

    /// <summary>
    /// Перезапись с переименованием: если файл <paramref name="path"/> не изменился с версии <paramref name="expectedVersion"/>, модель
    /// записывается в <paramref name="newPath"/> (то же имя — на месте), а прежний файл удаляется. Всё под блокировкой записи.
    /// </summary>
    /// <returns>Новая версия; null — файл изменён или удалён.</returns>
    /// <exception cref="IOException">Под новым именем уже лежит другой файл (его не затираем).</exception>
    public static Task<string?> WriteIfMatch<T>(string path, string newPath, T model, string expectedVersion, CancellationToken ct)
        where T : class =>
        WithLock(path, ct, async () =>
        {
            if (await CurrentVersion(path, ct) != expectedVersion)
                return null;
            if (PathRules.SamePath(newPath, path))
                return await WriteUnlocked(path, model, ct);

            if (File.Exists(newPath))
                throw new IOException($"Cannot rename '{Path.GetFileName(path)}' to '{Path.GetFileName(newPath)}': the file exists");

            var version = await WriteUnlocked(newPath, model, ct);
            AtomicFile.Delete(path);
            return version;
        });

    /// <summary>Переименование файла под блокировкой записи. Файл под новым именем уже есть — не затираем.</summary>
    /// <returns>false — исходного файла уже нет или новое имя занято.</returns>
    public static Task<bool> Rename(string path, string newPath, CancellationToken ct) =>
        WithLock(path, ct, () =>
        {
            if (!File.Exists(path) || File.Exists(newPath))
                return Task.FromResult(false);

            AtomicFile.Move(path, newPath, overwrite: false);
            return Task.FromResult(true);
        });

    /// <summary>Удаление, только если файл не изменился с версии <paramref name="expectedVersion"/>.</summary>
    public static Task<bool> DeleteIfMatch(string path, string expectedVersion, CancellationToken ct) =>
        DeleteIfMatch(path, expectedVersion, () => AtomicFile.Delete(path), ct);

    /// <summary>
    /// Выполняет <paramref name="delete"/> (например, удаление папки проекта), только если файл
    /// <paramref name="path"/> не изменился с версии <paramref name="expectedVersion"/>.
    /// </summary>
    public static Task<bool> DeleteIfMatch(string path, string expectedVersion, Action delete, CancellationToken ct) =>
        WithLock(path, ct, async () =>
        {
            if (await CurrentVersion(path, ct) != expectedVersion)
                return false;

            delete();
            return true;
        });

    /// <summary>
    /// Атомарная запись: сначала во временный файл рядом, затем переименование —
    /// при сбое посреди записи на диске не останется обрезанного файла.
    /// </summary>
    private static async Task<string> WriteUnlocked<T>(string path, T model, CancellationToken ct) where T : class
    {
        // Любая запись — в текущем формате: файл получает formatVersion первой строкой.
        if (model is FileModel stamped)
            stamped.FormatVersion = FormatVersions.Current;

        var bytes = Utf8.GetBytes(Serializer.Serialize(model));
        await WriteBytes(path, bytes, ct);
        return Hash(bytes);
    }

    // Временный файл рядом и переименование поверх; на Windows занятый файл (антивирус, чужой читатель) ждётся короткими повторами.
    private static Task WriteBytes(string path, byte[] bytes, CancellationToken ct) => AtomicFile.WriteAllBytes(path, bytes, ct);

    /// <summary>
    /// Переписывает файл в текущий формат на диске, не меняя в нём ничего другого (текст приводится шагами <see cref="FormatVersions"/>).
    /// Под той же блокировкой, что и обычная запись, — не потеряет чужую правку.
    /// </summary>
    /// <returns>Версия, из которой переписан файл; null — файл уже в текущем формате или его нет.</returns>
    /// <exception cref="UnsupportedFormatException">Файл более нового формата.</exception>
    public static Task<int?> UpgradeOnDisk(string path, CancellationToken ct) =>
        WithLock(path, ct, async () =>
        {
            if (await ReadBytes(path, ct) is not { } bytes)
                return (int?)null;

            var text = Decode(bytes);
            var version = FormatVersions.Of(text, path);
            if (version == FormatVersions.Current)
                return null;

            await WriteBytes(path, Utf8.GetBytes(FormatVersions.Upgrade(text, path)), ct);
            return version;
        });

    private static async Task<string?> CurrentVersion(string path, CancellationToken ct) =>
        await ReadBytes(path, ct) is { } bytes ? Hash(bytes) : null;

    private static async Task<byte[]?> ReadBytes(string path, CancellationToken ct)
    {
        try
        {
            // Не мешаем чужой замене файла: на Windows читатель без FileShare.Delete не даёт переименовать файл поверх.
            return await AtomicFile.ReadAllBytes(path, ct);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// Версия — хэш содержимого без BOM и с окончаниями строк LF: git на Windows (<c>core.autocrlf</c>) отдаёт те же файлы с CRLF, и версия
    /// не должна меняться от checkout. У файла, записанного Tasker (LF, без BOM), хэш тот же, что и от самих байтов.
    /// </summary>
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(LineEndings.ForHash(bytes)))[..16];

    /// <summary>Версия содержимого файла (см. <see cref="Hash"/>); для тестов.</summary>
    internal static string VersionOf(byte[] bytes) => Hash(bytes);

    private static string Decode(byte[] bytes) => LineEndings.StripBom(Utf8.GetString(bytes));

    /// <summary>
    /// Выполняет действие под блокировкой по ключу (пути файла или каталога) — например, проверку
    /// уникальности и запись одним шагом. Ключ отличается от путей отдельных файлов, поэтому внутри
    /// можно вызывать Write/WriteIfMatch этих файлов.
    /// </summary>
    public static Task<TResult> Locked<TResult>(string key, CancellationToken ct, Func<Task<TResult>> action) =>
        WithLock(key, ct, action);

    // Сначала блокировка записи папки между процессами, затем очередь на файл внутри процесса:
    // «сравнить версию и записать» атомарно, даже когда пишут демон, десктоп и командная строка.
    // Порядок общий для всех (в том числе для IWriteScope.Exclusive, который держит блокировку записи
    // и внутри пишет файлы): обратный порядок дал бы взаимную блокировку.
    private static Task<TResult> WithLock<TResult>(string path, CancellationToken ct, Func<Task<TResult>> action) =>
        WriteLockPath(path) is { } lockPath
            ? FileLock.Run(lockPath, () => Gated(path, ct, action), ct)
            : Gated(path, ct, action);

    private static async Task<TResult> Gated<TResult>(string path, CancellationToken ct, Func<Task<TResult>> action)
    {
        var gate = Locks.GetOrAdd(Path.GetFullPath(path), _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            return await action();
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Файл блокировки записи (см. <see cref="TaskerDirectory.WriteLock"/>); null — путь не в <c>.tasker</c>.</summary>
    private static string? WriteLockPath(string path)
    {
        for (var directory = new DirectoryInfo(Path.GetFullPath(path)); directory != null; directory = directory.Parent)
        {
            if (directory.Name == TaskerDirectory.Name)
                return TaskerDirectory.WriteLockOf(directory.FullName);
        }

        return null;
    }

    private class MultilineLiteralEmitter(IEventEmitter next) : ChainedEventEmitter(next)
    {
        public override void Emit(ScalarEventInfo eventInfo, IEmitter emitter)
        {
            if (eventInfo.Source.Value is string text && text.Contains('\n'))
                eventInfo.Style = ScalarStyle.Literal;

            base.Emit(eventInfo, emitter);
        }
    }
}
