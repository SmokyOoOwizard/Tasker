using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Tasker.Desktop.Tabs;

/// <summary>Заголовок вкладки в строке заголовка окна: название, подсказка с путём и крестик. Вид — стили <c>tab</c> в App.axaml.</summary>
public sealed class TabHeader : Border
{
    private readonly TextBlock _title = new()
    {
        Classes = { "tabTitle" },
        TextTrimming = TextTrimming.CharacterEllipsis,
        VerticalAlignment = VerticalAlignment.Center
    };

    public TabHeader(TabModel tab)
    {
        Tab = tab;
        Classes.Add("tab");

        var close = new Button { Classes = { "tabClose" }, Content = "✕" };
        close.Click += (_, _) => CloseRequested?.Invoke(this);
        ToolTip.SetTip(close, "Закрыть вкладку");

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.Children.Add(_title);
        Grid.SetColumn(close, 1);
        grid.Children.Add(close);
        Child = grid;

        // В строке заголовка клики по вкладке — наши, а не перетаскивание окна.
        WindowDecorationProperties.SetElementRole(this, WindowDecorationsElementRole.User);
        Update();
    }

    public TabModel Tab { get; }

    // Стили App.axaml написаны для Border.tab: без этого селектор по типу Border наследника не видит.
    protected override Type StyleKeyOverride => typeof(Border);

    public event Action<TabHeader>? CloseRequested;

    public bool IsActive
    {
        get => Classes.Contains("active");
        set => Classes.Set("active", value);
    }

    public void Update()
    {
        _title.Text = Tab.Title;
        ToolTip.SetTip(this, Tab.Path ?? Tab.Title);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        // Средняя кнопка закрывает вкладку, как в браузерах.
        if (e.InitialPressMouseButton == MouseButton.Middle)
        {
            CloseRequested?.Invoke(this);
            e.Handled = true;
        }

        base.OnPointerReleased(e);
    }
}

/// <summary>
/// Раскладка заголовков вкладок: все одной ширины, делят доступное место (от <see cref="MinTabWidth"/>
/// до <see cref="MaxTabWidth"/>), последний дочерний элемент — кнопка «+» сразу за вкладками.
/// </summary>
public sealed class TabStripPanel : Panel
{
    public const double MinTabWidth = 72;
    public const double MaxTabWidth = 220;
    public const double Spacing = 2;

    public double TabWidth { get; private set; } = MaxTabWidth;

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Children.Count == 0)
            return default;

        var plus = Children[^1];
        plus.Measure(availableSize);

        var count = Children.Count - 1;
        var room = double.IsInfinity(availableSize.Width) ? count * MaxTabWidth : availableSize.Width - plus.DesiredSize.Width;
        TabWidth = count == 0 ? 0 : Math.Clamp(room / count - Spacing, MinTabWidth, MaxTabWidth);

        var height = plus.DesiredSize.Height;
        for (var i = 0; i < count; i++)
        {
            Children[i].Measure(new Size(TabWidth, availableSize.Height));
            height = Math.Max(height, Children[i].DesiredSize.Height);
        }

        return new Size(count * (TabWidth + Spacing) + plus.DesiredSize.Width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var x = 0.0;
        for (var i = 0; i < Children.Count - 1; i++)
        {
            Children[i].Arrange(new Rect(x, 0, TabWidth, finalSize.Height));
            x += TabWidth + Spacing;
        }

        if (Children.Count > 0)
        {
            var plus = Children[^1];
            plus.Arrange(new Rect(x, (finalSize.Height - plus.DesiredSize.Height) / 2, plus.DesiredSize.Width, plus.DesiredSize.Height));
        }

        return finalSize;
    }

    /// <summary>Индекс вставки для точки x (в координатах панели) — по серединам вкладок.</summary>
    public int InsertionIndex(double x, int tabCount) =>
        Math.Clamp((int)Math.Floor((x + (TabWidth + Spacing) / 2) / (TabWidth + Spacing)), 0, tabCount);
}
