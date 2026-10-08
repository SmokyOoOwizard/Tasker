using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace Tasker.Desktop.Tabs;

/// <summary>
/// Перетаскивание вкладок, как в браузерах:
/// <list type="bullet">
/// <item>по полосе вкладок — перестановка;</item>
/// <item>за полосу — вкладка «отрывается»: за курсором едет фантом (<see cref="GhostWindow"/>);
/// над полосой другого окна — маркер места вставки, отпустили — вкладка переезжает туда;
/// отпустили в пустом месте — новое окно под курсором;</item>
/// <item>единственная вкладка окна — за курсором едет само окно; над полосой другого окна — слияние.</item>
/// </list>
/// Esc отменяет перетаскивание. Указатель захвачен заголовком вкладки, и события движения приходят
/// в исходное окно, даже когда курсор далеко за ним.
/// <para>
/// Все расчёты — в единицах <see cref="Window.Position"/>: DIP окна × <see cref="TopLevel.DesktopScaling"/>
/// (на macOS это точки, а не пиксели Retina). Курсор — положение события относительно окна плюс положение окна:
/// так верно и за пределами окна, и когда окно само едет за курсором (ОС считает координаты события от текущей рамки).
/// </para>
/// </summary>
public sealed class TabDragController(WindowManager manager)
{
    // Сдвиг, после которого нажатие считается перетаскиванием, а не кликом (DIP).
    private const double StartThreshold = 5;
    // Насколько можно увести курсор от полосы вкладок по вертикали, прежде чем вкладка оторвётся (DIP).
    private const double DetachDistance = 28;

    private Drag? _drag;

    public void Attach(TabHeader header)
    {
        header.PointerPressed += OnPressed;
        header.PointerMoved += OnMoved;
        header.PointerReleased += OnReleased;
        header.PointerCaptureLost += OnCaptureLost;
    }

    public void Detach(TabHeader header)
    {
        header.PointerPressed -= OnPressed;
        header.PointerMoved -= OnMoved;
        header.PointerReleased -= OnReleased;
        header.PointerCaptureLost -= OnCaptureLost;
    }

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not TabHeader header || TopLevel.GetTopLevel(header) is not WorkspaceWindow window
            || !e.GetCurrentPoint(header).Properties.IsLeftButtonPressed)
            return;

        _drag = new Drag(header, window, e.GetPosition(window), Desktop(window, e), e.GetPosition(header), window.Position);
        e.Pointer.Capture(header);
        window.KeyDown += OnKeyDown;
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        if (_drag is not { } drag || sender != drag.Header)
            return;

        var source = drag.Source;
        var desktop = Desktop(source, e);

        if (!drag.Started)
        {
            var position = e.GetPosition(source);
            if (Math.Abs(position.X - drag.PressInWindow.X) < StartThreshold && Math.Abs(position.Y - drag.PressInWindow.Y) < StartThreshold)
                return;
            drag.Started = true;
        }

        // Вкладка ещё в своей полосе: переставляем, пока курсор рядом с полосой.
        if (drag.Mode == DragMode.Reorder)
        {
            if (StripIndexAt(source, desktop) is { } index)
            {
                source.MoveTab(drag.Tab, index > source.Tabs.ToList().IndexOf(drag.Tab) ? index - 1 : index);
                return;
            }

            if (source.Tabs.Count == 1)
            {
                drag.Mode = DragMode.MoveWindow;
            }
            else
            {
                drag.Mode = DragMode.Ghost;
                drag.Header.Opacity = 0.35;
                drag.Ghost = new GhostWindow(drag.Tab);
                drag.Ghost.Position = desktop - Offset(source, drag.GrabInHeader);
                drag.Ghost.Show();
            }
        }

        if (drag.Mode == DragMode.MoveWindow)
            source.Position = drag.WindowStart + (desktop - drag.PressDesktop);
        else if (drag.Ghost != null)
            drag.Ghost.Position = desktop - Offset(source, drag.GrabInHeader);

        // Над чьей полосой курсор (кроме окна, которое едет вместе с ним).
        var target = manager.Windows
            .Where(x => x.IsVisible && !(drag.Mode == DragMode.MoveWindow && x == source))
            .Select(x => (Window: x, Index: StripIndexAt(x, desktop)))
            .FirstOrDefault(x => x.Index != null);

        SetTarget(drag, target.Window, target.Index);

        // Фантом вернули на свою полосу — вкладка снова просто переставляется.
        if (drag.Mode == DragMode.Ghost && target.Window == source)
        {
            SetTarget(drag, null, null);
            drag.Ghost?.Close();
            drag.Ghost = null;
            drag.Header.Opacity = 1;
            drag.Mode = DragMode.Reorder;
        }
    }

    private async void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_drag is not { } drag || sender != drag.Header)
            return;

        var desktop = Desktop(drag.Source, e);
        var (target, index) = (drag.Target, drag.TargetIndex);
        Finish(drag);
        e.Pointer.Capture(null);

        if (!drag.Started || drag.Mode == DragMode.Reorder)
            return;

        if (target != null && index != null && target != drag.Source)
        {
            await manager.MoveTab(drag.Tab, drag.Source, target, index.Value);
            return;
        }

        if (drag.Mode == DragMode.Ghost)
        {
            // Новое окно такого же размера — так, чтобы заголовок вкладки оказался под курсором.
            var stripOrigin = drag.Source.Strip.TranslatePoint(default, drag.Source) ?? default;
            var window = manager.CreateWindow(desktop - Offset(drag.Source, drag.GrabInHeader + stripOrigin), drag.Source.ClientSize, withEmptyTab: false);
            window.Show();
            await manager.MoveTab(drag.Tab, drag.Source, window, 0);
        }
    }

    private void OnCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (_drag is { } drag && sender == drag.Header)
            Cancel(drag);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _drag is { } drag)
        {
            Cancel(drag);
            e.Handled = true;
        }
    }

    private void Cancel(Drag drag)
    {
        Finish(drag);
        if (drag.Mode == DragMode.MoveWindow)
            drag.Source.Position = drag.WindowStart;
    }

    private void Finish(Drag drag)
    {
        _drag = null;
        drag.Source.KeyDown -= OnKeyDown;
        SetTarget(drag, null, null);
        drag.Ghost?.Close();
        drag.Ghost = null;
        drag.Header.Opacity = 1;
    }

    private static void SetTarget(Drag drag, WorkspaceWindow? window, int? index)
    {
        if (drag.Target != null && drag.Target != window)
            drag.Target.ShowInsertMarker(null);

        drag.Target = window;
        drag.TargetIndex = index;

        // В своей полосе маркер не нужен — там вкладка переставляется сразу.
        window?.ShowInsertMarker(window == drag.Source ? null : index);
    }

    /// <summary>Курсор в единицах <see cref="Window.Position"/> — по положению относительно окна (работает и за его пределами).</summary>
    private static PixelPoint Desktop(Window window, PointerEventArgs e) =>
        window.Position + Offset(window, e.GetPosition(window));

    private static PixelPoint Offset(Window window, Point dip) => PixelPoint.FromPoint(dip, window.DesktopScaling);

    /// <summary>Индекс вставки, если точка экрана над полосой вкладок окна (с запасом по вертикали), иначе null.</summary>
    private static int? StripIndexAt(WorkspaceWindow window, PixelPoint desktop)
    {
        var host = (Visual)window.Strip.Parent!;
        var inWindow = (desktop - window.Position).ToPoint(window.DesktopScaling);
        if (window.TranslatePoint(inWindow, host) is not { } point)
            return null;

        var bounds = new Rect(host.Bounds.Size).Inflate(new Thickness(0, DetachDistance));
        return bounds.Contains(point) ? window.Strip.InsertionIndex(point.X, window.Tabs.Count) : null;
    }

    private enum DragMode
    {
        Reorder,
        Ghost,
        MoveWindow
    }

    private sealed class Drag(TabHeader header, WorkspaceWindow source, Point pressInWindow, PixelPoint pressDesktop, Point grabInHeader, PixelPoint windowStart)
    {
        public TabHeader Header { get; } = header;
        public TabModel Tab => Header.Tab;
        public WorkspaceWindow Source { get; } = source;
        public Point PressInWindow { get; } = pressInWindow;
        public PixelPoint PressDesktop { get; } = pressDesktop;
        public Point GrabInHeader { get; } = grabInHeader;
        public PixelPoint WindowStart { get; } = windowStart;

        public bool Started { get; set; }
        public DragMode Mode { get; set; } = DragMode.Reorder;
        public GhostWindow? Ghost { get; set; }
        public WorkspaceWindow? Target { get; set; }
        public int? TargetIndex { get; set; }
    }
}
