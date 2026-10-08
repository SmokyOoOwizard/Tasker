using Tasker.Storage.Files.Workspaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Serilog;
using Tasker.Desktop.Tabs;
using Tasker.Mcp;

using Tasker.Web.Workspaces;

namespace Tasker.Desktop;

/// <summary>
/// Все окна приложения и вкладки в них: создание, закрытие, открытие папок, перенос вкладок между окнами.
/// Окна хранятся в порядке последней активации — первое самое верхнее (для переноса вкладки на окно под курсором).
/// </summary>
public sealed class WindowManager
{
    private readonly WorkspaceRegistry _registry;
    private readonly FrontendAddress _frontend;
    private readonly LocalMcpAddress _mcp;
    private readonly List<WorkspaceWindow> _windows = [];

    public WindowManager(WorkspaceRegistry registry, FrontendAddress frontend, LocalMcpAddress mcp)
    {
        _registry = registry;
        _frontend = frontend;
        _mcp = mcp;
        Commands = new WindowCommands(this);
        Drag = new TabDragController(this);
    }

    public WindowCommands Commands { get; }

    public TabDragController Drag { get; }

    /// <summary>Окна: первое — последнее активированное.</summary>
    public IReadOnlyList<WorkspaceWindow> Windows => _windows;

    /// <summary>Новое окно с одной пустой вкладкой (или без вкладок — их добавит вызывающий).</summary>
    public WorkspaceWindow CreateWindow(PixelPoint? position = null, Size? size = null, bool withEmptyTab = true)
    {
        var window = new WorkspaceWindow(this);
        if (size is { } s)
        {
            window.Width = s.Width;
            window.Height = s.Height;
        }

        window.Position = position ?? NextPosition();
        _windows.Add(window);
        if (withEmptyTab)
            window.AddTab(CreateTab());
        return window;
    }

    public TabModel CreateTab() => new(_registry, _frontend, PickFolderInto);

    /// <summary>Новая пустая вкладка в окне.</summary>
    public void NewTab(WorkspaceWindow window) => window.AddTab(CreateTab());

    public void NewWindow() => Show(CreateWindow());

    /// <summary>
    /// «Открыть папку»: выбор папки, затем вкладка с ней — активная, если она пустая, иначе новая.
    /// Та же папка может быть открыта в нескольких вкладках — они работают через одну рабочую область.
    /// </summary>
    public async Task OpenFolder(WorkspaceWindow window)
    {
        if (await PickFolder(window) is not { } path)
            return;

        var tab = window.ActiveTab is { IsEmpty: true } empty ? empty : null;
        if (tab == null)
        {
            tab = CreateTab();
            window.AddTab(tab);
        }

        await OpenIn(tab, WorkspaceLocation.Files(path));
    }

    /// <summary>Папки и файлы БД из аргументов запуска (или от второго запуска) — во вкладках верхнего окна.</summary>
    public async Task Open(StartupRequest request)
    {
        var window = _windows.FirstOrDefault() ?? CreateWindow(withEmptyTab: false);
        Show(window);

        foreach (var location in request.Locations())
        {
            var tab = window.ActiveTab is { IsEmpty: true } empty ? empty : null;
            if (tab == null)
            {
                tab = CreateTab();
                window.AddTab(tab);
            }

            await OpenIn(tab, location);
        }

        if (window.Tabs.Count == 0)
            window.AddTab(CreateTab());
    }

    /// <summary>Показывает верхнее окно (клик по значку в Dock, пункт трея); окон нет — создаёт.</summary>
    public void ShowTopWindow() => Show(_windows.FirstOrDefault() ?? CreateWindow());

    /// <summary>
    /// Переносит вкладку в другое окно на позицию <paramref name="index"/> — без перезагрузки WebView:
    /// на время переноса нативный WebView не уничтожается (<see cref="NativeWebView.BeginReparenting"/>),
    /// а прикрепляется к новому окну после прохода раскладки.
    /// </summary>
    public async Task MoveTab(TabModel tab, WorkspaceWindow from, WorkspaceWindow to, int index)
    {
        var webView = tab.Content as NativeWebView;
        using (webView?.BeginReparenting(true))
        {
            from.DetachTab(tab);
            // Сначала раскладка без WebView, потом — в новом окне: иначе Avalonia бросает
            // «InvalidateArrange on wrong LayoutManager» (проверено на macOS).
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Render);
            to.AddTab(tab, index);
        }

        Show(to);
    }

    internal void OnWindowActivated(WorkspaceWindow window)
    {
        _windows.Remove(window);
        _windows.Insert(0, window);
    }

    internal void OnWindowClosed(WorkspaceWindow window) => _windows.Remove(window);

    /// <summary>
    /// В окне не осталось вкладок. Есть другие окна — закрываем это; последнее окно не закрываем
    /// (приложение живёт в трее), а прячем с новой пустой вкладкой.
    /// </summary>
    internal void OnWindowEmptied(WorkspaceWindow window)
    {
        if (_windows.Count > 1)
        {
            window.Close();
            return;
        }

        window.AddTab(CreateTab());
        window.Hide();
    }

    private async Task PickFolderInto(TabModel tab, TopLevel? owner)
    {
        if (await PickFolder(owner) is { } path)
            await OpenIn(tab, WorkspaceLocation.Files(path));
    }

    private async Task OpenIn(TabModel tab, WorkspaceLocation location)
    {
        await tab.Open(location);
        if (tab.Lease is { } lease)
        {
            Log.Information("Tab {Name}: MCP {Address}, argument workspace=\"{Key}\"",
                location.Name,
                _mcp.Port is { } port ? McpRegistration.LocalUrl(port) : "is served by the MCP daemon or unavailable",
                lease.Key);
        }
    }

    private static async Task<string?> PickFolder(TopLevel? owner)
    {
        if (owner == null)
            return null;

        var folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Выберите папку",
            AllowMultiple = false
        });

        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    private static void Show(Window window)
    {
        window.Show();
        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;
        window.Activate();
    }

    // Новое окно — со сдвигом от верхнего, как в браузерах.
    private PixelPoint NextPosition() =>
        _windows.FirstOrDefault() is { } top ? top.Position + new PixelPoint(28, 28) : new PixelPoint(120, 80);
}
