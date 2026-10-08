using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Tasker.Core;
using Tasker.Core.IO;
using Tasker.Storage.Files.Workspaces;

namespace Tasker.Global;

/// <summary>Файл настроек нельзя прочитать. Его не перезаписываем молча: правку пользователя не теряем.</summary>
public class SettingsException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Чтение, изменение и слежение за <c>settings.json</c>. Изменение — «прочитать, поменять, записать» под короткой
/// блокировкой <c>settings.lock</c>, поэтому одновременные правки из десктопа и командной строки не теряются;
/// запись атомарная (временный файл и переименование), читатель не увидит половину файла.
/// </summary>
public sealed class SettingsStore(string? directory = null)
{
    private static readonly JsonSerializerOptions Json = new(TaskerJson.Options) { WriteIndented = true };

    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(200);

    private readonly string _directory = directory ?? AppDirectories.Data;

    public string FilePath => Path.Combine(_directory, "settings.json");

    private string LockPath => Path.Combine(_directory, "settings.lock");

    /// <summary>Текущие настройки; файла нет — по умолчанию.</summary>
    /// <exception cref="SettingsException">Файл есть, но в нём не JSON настроек.</exception>
    public GlobalSettings Load()
    {
        string text;
        try
        {
            text = AtomicFile.ReadAllTextSync(FilePath);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return new GlobalSettings();
        }

        try
        {
            return (string.IsNullOrWhiteSpace(text) ? null : JsonSerializer.Deserialize<GlobalSettings>(text, Json)) ?? new GlobalSettings();
        }
        catch (JsonException e)
        {
            throw new SettingsException($"Cannot read {FilePath}: {e.Message}. Fix or delete the file", e);
        }
    }

    /// <summary>Меняет настройки под блокировкой и возвращает записанные. Не изменилось — файл не трогаем.</summary>
    public async Task<GlobalSettings> Update(Func<GlobalSettings, GlobalSettings> change, CancellationToken ct = default) =>
        await FileLock.Run(LockPath, () =>
        {
            var current = Load();
            var updated = change(current);
            var text = Serialize(updated);
            if (text == Serialize(current) && File.Exists(FilePath))
                return Task.FromResult(updated);

            AtomicFile.WriteAllTextSync(FilePath, text);
            return Task.FromResult(updated);
        }, ct);

    /// <summary>Добавляет рабочую область (та же папка, открытая иначе, — та же запись). Возвращает запись и признак «добавлена».</summary>
    public async Task<(WorkspaceEntry Entry, bool Added)> AddWorkspace(WorkspaceLocation location, CancellationToken ct = default)
    {
        var entry = WorkspaceEntry.Of(location);
        var added = false;
        await Update(settings =>
        {
            if (settings.Mcp.Workspaces.Any(x => x.Location.Id == location.Id))
                return settings;

            added = true;
            return settings with { Mcp = settings.Mcp with { Workspaces = [.. settings.Mcp.Workspaces, entry] } };
        }, ct);
        return (entry, added);
    }

    /// <returns>false — такой области в настройках не было.</returns>
    public async Task<bool> RemoveWorkspace(WorkspaceLocation location, CancellationToken ct = default)
    {
        var removed = false;
        await Update(settings =>
        {
            var left = settings.Mcp.Workspaces.Where(x => x.Location.Id != location.Id).ToArray();
            removed = left.Length != settings.Mcp.Workspaces.Count;
            return removed ? settings with { Mcp = settings.Mcp with { Workspaces = left } } : settings;
        }, ct);
        return removed;
    }

    public async Task SetPort(int port, CancellationToken ct = default)
    {
        if (port is < 1 or > 65535)
            throw new SettingsException($"Port must be from 1 to 65535, got {port}");

        await Update(settings => settings with { Mcp = settings.Mcp with { Port = port } }, ct);
    }

    /// <param name="name">Пустое имя или null — сбросить на имя пользователя операционной системы.</param>
    public async Task SetUserName(string? name, CancellationToken ct = default)
    {
        var value = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        if (value is { Length: > GlobalSettings.MaxUserNameLength })
            throw new SettingsException($"The name must be at most {GlobalSettings.MaxUserNameLength} characters, got {value.Length}");

        await Update(settings => settings with { UserName = value }, ct);
    }

    /// <summary>
    /// Вызывает <paramref name="onChanged"/>, когда файл настроек меняется (в том числе другим процессом).
    /// События склеиваются: обработчик получает уже готовые настройки после того, как файловая система затихла.
    /// Файл нечитаем — обработчик не вызывается, прежние настройки остаются в силе.
    /// </summary>
    public IDisposable Watch(Action<GlobalSettings> onChanged)
    {
        Directory.CreateDirectory(_directory);
        return new Watcher(this, onChanged);
    }

    private static string Serialize(GlobalSettings settings) => JsonSerializer.Serialize(settings, Json) + "\n";

    private sealed class Watcher : IDisposable
    {
        private readonly SettingsStore _store;
        private readonly Action<GlobalSettings> _onChanged;
        private readonly FileSystemWatcher _watcher;
        private readonly Timer _timer;
        private string? _last;

        public Watcher(SettingsStore store, Action<GlobalSettings> onChanged)
        {
            _store = store;
            _onChanged = onChanged;
            _timer = new Timer(_ => Fire());
            _watcher = new FileSystemWatcher(store._directory, "settings.json")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
            };
            FileSystemEventHandler handler = (_, _) => _timer.Change(Quiet, Timeout.InfiniteTimeSpan);
            _watcher.Created += handler;
            _watcher.Changed += handler;
            _watcher.Renamed += (_, _) => _timer.Change(Quiet, Timeout.InfiniteTimeSpan);
            _watcher.EnableRaisingEvents = true;
            _last = Fingerprint();
        }

        private void Fire()
        {
            try
            {
                // Тот же файл (временная запись, повторное событие) — не изменение.
                var now = Fingerprint();
                if (now == _last)
                    return;

                var settings = _store.Load();
                _last = now;
                _onChanged(settings);
            }
            catch (SettingsException)
            {
                // Файл сломан на время правки руками — ждём следующего изменения.
            }
        }

        private string? Fingerprint()
        {
            try
            {
                return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(AtomicFile.ReadAllTextSync(_store.FilePath))));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        public void Dispose()
        {
            _watcher.Dispose();
            _timer.Dispose();
        }
    }
}
