using System.Text;

namespace Tasker.Core.IO;

/// <summary>
/// Чтение, запись, переименование и удаление файлов так, чтобы это работало и на Windows. Там файл, открытый другим процессом
/// без <see cref="FileShare.Delete"/>, нельзя переименовать, заменить или удалить (на Unix можно), а антивирус, индексатор поиска
/// и проводник открывают только что записанные файлы на доли секунды. Поэтому:
/// <list type="bullet">
/// <item>читаем с <c>FileShare.ReadWrite | FileShare.Delete</c> — читатель не мешает чужой замене файла;</item>
/// <item>замену, переименование, удаление и создание временного файла при кратковременном отказе («файл занят», «доступ запрещён»)
/// повторяем несколько раз с короткими паузами (только на Windows: на Unix такого отказа нет, и повтор скрыл бы настоящую ошибку).</item>
/// </list>
/// Решение «повторять ли» — чистая функция <see cref="IsTransient"/>, повтор — <see cref="Retry{T}"/> с подставляемыми паузами: оба проверяются тестами на любой платформе.
/// </summary>
public static class AtomicFile
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    // Паузы между попытками (мс): в сумме около полутора секунд — дольше антивирус файл не держит.
    private static readonly int[] Delays = [5, 10, 20, 40, 80, 100, 100, 100, 100, 100, 100, 100, 100, 100, 100, 100];

    private const int ErrorAccessDenied = 5;
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;
    private const int ErrorUserMappedFile = 1224;

    /// <summary>
    /// Кратковременный ли это отказ Windows (файл занят другим процессом, удаление ещё не завершилось): такую операцию стоит повторить.
    /// «Файла нет», «имя уже занято», «путь слишком длинный» — нет: повтор их не исправит.
    /// </summary>
    public static bool IsTransient(Exception e) => e switch
    {
        UnauthorizedAccessException => true,
        FileNotFoundException or DirectoryNotFoundException or PathTooLongException => false,
        IOException io => (io.HResult & 0xFFFF) is ErrorAccessDenied or ErrorSharingViolation or ErrorLockViolation or ErrorUserMappedFile,
        _ => false
    };

    /// <summary>Выполняет <paramref name="operation"/>; при кратковременном отказе (<see cref="IsTransient"/>) повторяет, пока не кончатся <paramref name="delays"/>.</summary>
    /// <param name="retry">false — одна попытка (не Windows).</param>
    /// <param name="sleep">Пауза в мс.</param>
    public static T Retry<T>(Func<T> operation, bool retry, IReadOnlyList<int> delays, Action<int> sleep)
    {
        for (var attempt = 0;; attempt++)
        {
            try
            {
                return operation();
            }
            catch (Exception e) when (retry && attempt < delays.Count && IsTransient(e))
            {
                sleep(delays[attempt]);
            }
        }
    }

    private static T Retry<T>(Func<T> operation) => Retry(operation, OperatingSystem.IsWindows(), Delays, Thread.Sleep);

    /// <summary>Переименование (<paramref name="overwrite"/> — с заменой существующего файла: атомарно, где система это умеет).</summary>
    public static void Move(string from, string to, bool overwrite) => Retry(() =>
    {
        File.Move(from, to, overwrite);
        return 0;
    });

    /// <summary>Удаление файла; файла нет — не ошибка.</summary>
    public static void Delete(string path) => Retry(() =>
    {
        File.Delete(path);
        return 0;
    });

    /// <summary>
    /// Атомарная запись: во временный файл рядом (<c>&lt;path&gt;.tmp</c>), затем переименование поверх. При обрыве посреди записи
    /// остаётся только <c>.tmp</c> (он в .gitignore и ничего не значит, следующая запись его перезапишет), а не обрезанный файл.
    /// </summary>
    public static async Task WriteAllBytes(string path, byte[] bytes, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";

        // Создание временного файла тоже может упереться в файл, который ещё не удалён после прошлой записи (антивирус держит).
        await using (var stream = Retry(() => new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous)))
            await stream.WriteAsync(bytes, ct);

        Move(temp, path, overwrite: true);
    }

    public static Task WriteAllText(string path, string text, CancellationToken ct = default) =>
        WriteAllBytes(path, Utf8.GetBytes(text), ct);

    /// <summary>Синхронная <see cref="WriteAllText"/>: для настроек и других мест без асинхронного кода.</summary>
    public static void WriteAllTextSync(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        Retry(() =>
        {
            File.WriteAllText(temp, text, Utf8);
            return 0;
        });
        Move(temp, path, overwrite: true);
    }

    /// <summary>Читает файл целиком так, чтобы не мешать его замене или удалению другим процессом.</summary>
    /// <exception cref="FileNotFoundException">Файла нет.</exception>
    public static async Task<byte[]> ReadAllBytes(string path, CancellationToken ct = default)
    {
        for (var attempt = 0;; attempt++)
        {
            try
            {
                await using var stream = Open(path, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var bytes = new byte[stream.Length];
                await stream.ReadExactlyAsync(bytes, ct);
                return bytes;
            }
            catch (Exception e) when (OperatingSystem.IsWindows() && attempt < Delays.Length && IsTransient(e))
            {
                await Task.Delay(Delays[attempt], ct);
            }
        }
    }

    /// <summary>Читает текст (UTF-8, BOM отбрасывается) так же, как <see cref="ReadAllBytes"/>.</summary>
    public static async Task<string> ReadAllText(string path, CancellationToken ct = default) =>
        LineEndings.StripBom(Utf8.GetString(await ReadAllBytes(path, ct)));

    /// <summary>Синхронная <see cref="ReadAllText"/>.</summary>
    public static string ReadAllTextSync(string path) => Retry(() =>
    {
        using var stream = Open(path, FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, Utf8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    });

    private static FileStream Open(string path, FileOptions options) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, options);
}
