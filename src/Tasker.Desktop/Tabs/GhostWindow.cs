using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Tasker.Desktop.Tabs;

/// <summary>
/// Фантом перетаскиваемой вкладки: окно без рамки поверх остальных, которое едет за курсором.
/// Снимок WebView сделать нельзя (нативный контрол не рисуется в RenderTargetBitmap), поэтому
/// в фантоме — заголовок вкладки и карточка с путём папки.
/// </summary>
public sealed class GhostWindow : Window
{
    public const double GhostWidth = 280;

    public GhostWindow(TabModel tab)
    {
        WindowDecorations = WindowDecorations.None;
        Topmost = true;
        ShowActivated = false;
        ShowInTaskbar = false;
        CanResize = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Background = Brushes.Transparent;
        Opacity = 0.94;

        var header = new Border
        {
            Classes = { "tab", "active", "ghostTab" },
            Width = 200,
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = new TextBlock
            {
                Classes = { "tabTitle" },
                Text = tab.Title,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            }
        };

        var body = new Border
        {
            Classes = { "ghostBody" },
            Child = new StackPanel
            {
                Spacing = 6,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new TextBlock { Classes = { "startTitle" }, FontSize = 15, Text = tab.Title, TextTrimming = TextTrimming.CharacterEllipsis },
                    new TextBlock { Classes = { "startHint" }, FontSize = 12, Text = tab.Path ?? "Пустая вкладка", TextWrapping = TextWrapping.Wrap, MaxHeight = 64 }
                }
            }
        };

        Content = new Border
        {
            Classes = { "ghostFrame" },
            Width = GhostWidth,
            Child = new StackPanel { Children = { header, body } }
        };
    }
}
