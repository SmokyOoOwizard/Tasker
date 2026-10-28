using System.Collections.Concurrent;
using System.Collections.Immutable;

namespace Tasker.Core.IO;

/// <summary>
/// Короткая блокировка между процессами и внутри процесса: файл, открытый эксклюзивно
/// (<see cref="FileShare.None"/>; на Unix .NET берёт на нём flock). Держат только на время самой операции —
/// прочитать или записать кэш, сравнить версию и записать файл, — а не пока приложение открыто:
/// поэтому с одной папкой одновременно работают демон, десктоп и командная строка.
/// <para>
/// Занято — ждём, пока освободится (до <see cref="DefaultTimeout"/>). Процесс упал — ОС снимает блокировку
/// сама (и на Unix, и на Windows: блокировка живёт, пока открыт дескриптор), файл остаётся и просто переиспользуется. На Windows
/// это обязательная блокировка (второе открытие не проходит, <c>FileShare.None</c>), поэтому в файл блокировки ничего не пишут и не читают. Блокировка не реентерабельна для файла, но
/// <see cref="Run{T}"/> внутри уже удерживаемой блокировки (в том же потоке выполнения) не ждёт самого себя.
/// </para>
/// </summary>
public sealed class FileLock : IDisposable
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    // Самая длинная пауза между попытками занять блокировку, которую держит другой процесс (мс).
    private const int MaxPoll = 5;

    // Внутри процесса очередь на файл — семафор: без опроса и по порядку. Файл разводит процессы.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(PathRules.Comparer);

    // Блокировки, которые удерживает текущий поток выполнения: вложенный Run их не берёт второй раз.
    private static readonly AsyncLocal<ImmutableHashSet<string>?> Held = new();
    private static readonly ImmutableHashSet<string> NoLocks = ImmutableHashSet.Create<string>(PathRules.Comparer);

    private readonly FileStream _stream;
    private readonly SemaphoreSlim _gate;
    private int _disposed;

    private FileLock(FileStream stream, SemaphoreSlim gate)
    {
        _stream = stream;
        _gate = gate;
    }

    /// <summary>Ждёт блокировку. Освобождать — <see cref="Dispose"/>.</summary>
    /// <exception cref="TimeoutException">Блокировка не освободилась за <paramref name="timeout"/>.</exception>
    public static async Task<FileLock> Acquire(string path, CancellationToken ct = default, TimeSpan? timeout = null)
    {
        var full = Path.GetFullPath(path);
        var wait = timeout ?? DefaultTimeout;
        var deadline = DateTime.UtcNow + wait;

        var gate = Gates.GetOrAdd(full, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(wait, ct))
            throw Busy(full, wait);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);

            // Держат блокировки доли миллисекунды (запрос к индексу), а ждущий спит между попытками: потолок в 50 мс заставлял его
            // просыпаться через десятки миллисекунд после освобождения, и очередь из нескольких процессов растягивалась на сотни.
            // Короткий шаг — те же попытки (открытие файла), но очередь движется со скоростью самой работы.
            for (var delay = 1;; delay = Math.Min(delay * 2, MaxPoll))
            {
                try
                {
                    return new FileLock(new FileStream(full, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None), gate);
                }
                catch (Exception e) when (e is IOException || (e is UnauthorizedAccessException && OperatingSystem.IsWindows()))
                {
                    // Держит другой процесс. На Windows так же отвечает файл, который ещё удаляется (отказ в доступе): подождём.
                }

                if (DateTime.UtcNow >= deadline)
                    throw Busy(full, wait);
                await Task.Delay(delay, ct);
            }
        }
        catch
        {
            gate.Release();
            throw;
        }
    }

    /// <summary>
    /// Выполняет <paramref name="action"/> под блокировкой. Если этот поток выполнения уже держит её
    /// (вложенный вызов), берёт не заново, а продолжает под той же.
    /// </summary>
    public static async Task<T> Run<T>(string path, Func<Task<T>> action, CancellationToken ct = default, TimeSpan? timeout = null)
    {
        var full = Path.GetFullPath(path);
        var held = Held.Value ?? NoLocks;
        if (held.Contains(full))
            return await action();

        using var _ = await Acquire(full, ct, timeout);
        Held.Value = held.Add(full);
        return await action();
    }

    public static Task Run(string path, Func<Task> action, CancellationToken ct = default, TimeSpan? timeout = null) =>
        Run<byte>(path, async () =>
        {
            await action();
            return 0;
        }, ct, timeout);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        _stream.Dispose();
        _gate.Release();
    }

    private static TimeoutException Busy(string path, TimeSpan timeout) =>
        new($"Lock {path} is busy: another Tasker process keeps it for more than {timeout.TotalSeconds:0} s");
}
