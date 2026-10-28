using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Input;
using Tasker.Desktop.Tabs;

namespace Tasker.Desktop;

/// <summary>
/// Окно с вкладками. Вкладки — в строке заголовка (нативные контролы Avalonia), под ними — содержимое
/// активной вкладки: стартовая страница или WebView рабочей области. Окнами и переносом вкладок
/// между ними управляет <see cref="WindowManager"/>.
/// </summary>
public partial class WorkspaceWindow : Window
{
    private readonly WindowManager? _manager;
    private readonly List<TabModel> _tabs = [];
    private readonly Dictionary<TabModel, TabHeader> _headers = [];

    // Для превьюера XAML в IDE.
    public WorkspaceWindow() : this(null)
    {
    }

    public WorkspaceWindow(WindowManager? manager)
    {
        _manager = manager;
        InitializeComponent();

        // macOS: слева «светофор» окна; Windows: справа кнопки окна. Место под них не занимаем вкладками.
        LeftInset.Width = OperatingSystem.IsMacOS() ? 84 : 8;
        RightInset.Width = OperatingSystem.IsMacOS() ? 8 : 140;
        WindowDecorationProperties.SetElementRole(TitleBar, WindowDecorationsElementRole.TitleBar);
        WindowDecorationProperties.SetElementRole(NewTabButton, WindowDecorationsElementRole.User);
        ToolTip.SetTip(NewTabButton, "Новая вкладка");
        NewTabButton.Click += (_, _) => _manager?.NewTab(this);
        MacTitleBar.Apply(this);

        if (_manager != null)
        {
            _manager.Commands.Attach(this);
            Activated += (_, _) =>
            {
                _manager.OnWindowActivated(this);
                // Пока окно было неактивно, папки могли поменять (git pull), а watcher — пропустить событие.
                foreach (var lease in _tabs.Select(x => x.Lease).OfType<Tasker.Web.Workspaces.WorkspaceLease>().DistinctBy(x => x.Location.Id))
                    _ = lease.Rescan();
            };
        }
    }

    public IReadOnlyList<TabModel> Tabs => _tabs;

    public TabModel? ActiveTab { get; private set; }

    public TabStripPanel Strip => TabStrip;

    public TabHeader HeaderOf(TabModel tab) => _headers[tab];

    /// <summary>Добавляет вкладку (в том числе перенесённую из другого окна — её WebView переезжает целиком).</summary>
    public void AddTab(TabModel tab, int? index = null, bool activate = true)
    {
        var at = Math.Clamp(index ?? _tabs.Count, 0, _tabs.Count);
        _tabs.Insert(at, tab);

        var header = new TabHeader(tab);
        header.CloseRequested += h => CloseTab(h.Tab);
        header.PointerPressed += OnHeaderPressed;
        _manager?.Drag.Attach(header);
        _headers[tab] = header;
        TabStrip.Children.Insert(at, header);

        tab.Content.IsVisible = false;
        ContentHost.Children.Add(tab.Content);
        tab.ContentChanged += OnTabContentChanged;

        if (activate || ActiveTab == null)
            ActivateTab(tab);
    }

    /// <summary>
    /// Убирает вкладку из окна, не закрывая её (для переноса в другое окно). Возвращает её индекс.
    /// Окно без вкладок решает <see cref="WindowManager"/>: закрыть или оставить пустую вкладку.
    /// </summary>
    public int DetachTab(TabModel tab)
    {
        var index = _tabs.IndexOf(tab);
        if (index < 0)
            return -1;

        tab.ContentChanged -= OnTabContentChanged;
        var header = _headers[tab];
        header.PointerPressed -= OnHeaderPressed;
        _manager?.Drag.Detach(header);
        _headers.Remove(tab);
        TabStrip.Children.Remove(header);
        ContentHost.Children.Remove(tab.Content);
        _tabs.RemoveAt(index);

        if (ActiveTab == tab)
        {
            ActiveTab = null;
            if (_tabs.Count > 0)
                ActivateTab(_tabs[Math.Min(index, _tabs.Count - 1)]);
        }

        if (_tabs.Count == 0)
            _manager?.OnWindowEmptied(this);

        return index;
    }

    /// <summary>Закрывает вкладку: убирает из окна и отпускает её рабочую область.</summary>
    public void CloseTab(TabModel tab)
    {
        if (DetachTab(tab) >= 0)
            tab.Close();
    }

    public void ActivateTab(TabModel tab)
    {
        ActiveTab = tab;
        foreach (var other in _tabs)
        {
            other.Content.IsVisible = other == tab;
            _headers[other].IsActive = other == tab;
        }

        Title = tab.IsEmpty ? "Tasker" : $"{tab.Title} — Tasker";
        tab.Content.Focus();
    }

    /// <summary>Соседняя вкладка по кругу: +1 — следующая, −1 — предыдущая.</summary>
    public void ActivateNext(int step)
    {
        if (ActiveTab == null || _tabs.Count < 2)
            return;

        var index = (_tabs.IndexOf(ActiveTab) + step + _tabs.Count) % _tabs.Count;
        ActivateTab(_tabs[index]);
    }

    /// <summary>Переставляет вкладку внутри окна (перетаскиванием).</summary>
    public void MoveTab(TabModel tab, int index)
    {
        var from = _tabs.IndexOf(tab);
        index = Math.Clamp(index, 0, _tabs.Count - 1);
        if (from < 0 || from == index)
            return;

        _tabs.RemoveAt(from);
        _tabs.Insert(index, tab);
        var header = _headers[tab];
        TabStrip.Children.Remove(header);
        TabStrip.Children.Insert(index, header);
    }

    /// <summary>Показывает маркер места вставки перетаскиваемой вкладки; null — убрать.</summary>
    public void ShowInsertMarker(int? index)
    {
        InsertMarker.IsVisible = index != null;
        if (index is { } i)
            InsertMarker.Margin = new Thickness(i * (TabStrip.TabWidth + TabStripPanel.Spacing) - 2, 8, 0, 6);
    }

    private void OnHeaderPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is TabHeader header && e.GetCurrentPoint(header).Properties.IsLeftButtonPressed)
            ActivateTab(header.Tab);
    }

    private void OnTabContentChanged(TabModel tab, Control previous)
    {
        var index = ContentHost.Children.IndexOf(previous);
        ContentHost.Children.RemoveAt(index);
        ContentHost.Children.Insert(index, tab.Content);
        _headers[tab].Update();
        tab.Content.IsVisible = tab == ActiveTab;
        if (tab == ActiveTab)
            ActivateTab(tab);
    }

    // Крестик последнего окна прячет его в трей, как раньше. Если окон несколько — окно закрывается
    // вместе со вкладками. Завершение приложения (меню, трей, Dock, выключение ОС) не отменяем,
    // иначе Avalonia отменит и всё завершение.
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (e.CloseReason is WindowCloseReason.WindowClosing or WindowCloseReason.Undefined
            && _manager?.Windows.Count == 1 && !e.IsProgrammatic)
        {
            e.Cancel = true;
            Hide();
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        foreach (var tab in _tabs.ToArray())
        {
            tab.ContentChanged -= OnTabContentChanged;
            tab.Close();
        }

        _tabs.Clear();
        _manager?.OnWindowClosed(this);
        base.OnClosed(e);
    }
}
