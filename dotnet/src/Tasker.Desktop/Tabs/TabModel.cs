using Tasker.Storage.Files.Workspaces;
using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Serilog;
using Tasker.Web.Workspaces;

namespace Tasker.Desktop.Tabs;

/// <summary>
/// Вкладка: пустая (стартовая страница с выбором папки) или открытая рабочая область — WebView
/// с фронтендом по адресу <c>/w/{key}/</c>. Вкладка держит <see cref="WorkspaceLease"/>: пока она открыта,
/// папка открыта в <see cref="WorkspaceRegistry"/>.
/// <para>
/// Содержимое (<see cref="Content"/>) переживает перенос вкладки в другое окно: WebView не пересоздаётся
/// (см. <see cref="WindowManager.MoveTab"/>).
/// </para>
/// </summary>
public sealed class TabModel
{
    private readonly WorkspaceRegistry _registry;
    private readonly FrontendAddress _frontend;
    private readonly StartPage _startPage;

    public TabModel(WorkspaceRegistry registry, FrontendAddress frontend, Func<TabModel, TopLevel?, Task> pickFolder)
    {
        _registry = registry;
        _frontend = frontend;
        _startPage = new StartPage(() => pickFolder(this, TopLevel.GetTopLevel(_startPage)));
        Content = _startPage;
    }

    public string Title { get; private set; } = "Новая вкладка";

    /// <summary>Путь папки (или файла БД) — подсказка к вкладке; null у пустой вкладки.</summary>
    public string? Path => Lease?.Location.Path;

    public WorkspaceLease? Lease { get; private set; }

    public Control Content { get; private set; }

    public bool IsEmpty => Lease == null;

    /// <summary>Поменялись заголовок или содержимое.</summary>
    public event Action<TabModel, Control>? ContentChanged;

    /// <summary>Открывает рабочую область в этой (пустой) вкладке. Ошибку показывает на стартовой странице.</summary>
    public async Task Open(WorkspaceLocation location)
    {
        if (!IsEmpty)
            throw new InvalidOperationException("The tab already shows a workspace");

        _startPage.SetBusy(true);
        try
        {
            Lease = await _registry.Open(location);
        }
        catch (Exception e)
        {
            Log.Error(e, "Cannot open workspace {Location}", location);
            _startPage.ShowError($"Не удалось открыть {location.Path}: {e.Message}");
            return;
        }
        finally
        {
            _startPage.SetBusy(false);
        }

        Title = location.Name;
        var previous = Content;
        Content = new NativeWebView { Source = new Uri(_frontend.Uri, Lease.BasePath + "/") };
        ContentChanged?.Invoke(this, previous);
    }

    /// <summary>Вкладку закрыли: отпускаем рабочую область (последняя вкладка папки закрывает её).</summary>
    public void Close()
    {
        if (Lease is { } lease)
            _ = lease.DisposeAsync().AsTask();
        Lease = null;
    }
}
