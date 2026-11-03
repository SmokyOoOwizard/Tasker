using System.Diagnostics;

namespace Tasker.Cli.Interactive;

internal static class Ui
{
    private static readonly Style Detail = new(Color.Gray);
    private static readonly Style Hint = new(Color.DarkGray);
    private static readonly Style Highlight = new(Reversed: true);

    public static void Draw(Screen screen, App app)
    {
        var area = screen.Area;
        var header = area.Row(0);
        var crumbs = area.Row(1);
        var body = new Rect(area.X, area.Y + 2, area.Width, Math.Max(0, area.Height - 3));
        var footer = area.Row(area.Height - 1);

        var project = app.Project?.Name ?? "none";
        screen.Line(header,
        [
            new Span(" tasker -i ", new Style(Bold: true)),
            Span.Raw($" project: {project}   workspace: {app.WorkspaceLabel}")
        ], new Style(Color.White, Color.DarkGray));
        screen.Text(crumbs, $" {app.Breadcrumb()}", new Style(Color.Cyan));
        app.Page = Math.Max(1, body.Height - 4);

        if (app.Result != null)
        {
            DrawResult(screen, app, body, footer);
            return;
        }

        var title = app.CurrentSpec?.Title ?? "";
        var filters = app.HasFilters;
        var columns = app.HasColumns;
        int? refreshes = app.ListRefreshes ? (int)Math.Ceiling(app.Refresh.TotalSeconds) : null;
        if (app.State is not { } state)
            return;
        screen.Block(body, refreshes is { } seconds ? $" {title}  refreshes every {seconds} s " : $" {title} ");
        var inner = body.Inner;
        switch (state)
        {
            case ListStep list:
            {
                var hint = DrawList(screen, list, inner);
                if (filters)
                    hint += "   Ctrl+F filters";
                if (columns)
                    hint += "   Ctrl+L columns";
                screen.Text(footer, hint, Hint);
                break;
            }
            case TextStep text:
                DrawText(screen, text, inner);
                screen.Text(footer, " Enter accept   Esc back", Hint);
                break;
            case FormStep form:
                DrawForm(screen, form, inner, footer);
                break;
            case EditorStep editor:
            {
                screen.Text(inner.Row(0), $" {editor.Message ?? editor.Hint}", Hint);
                screen.Paragraph(inner.Below(1), [(editor.Text.Length == 0 ? "(empty)" : editor.Text, Style.Plain)]);
                screen.Text(footer, " e edit in $EDITOR   Enter accept as shown   Esc back", Hint);
                break;
            }
            case ConfirmStep confirm:
                screen.Paragraph(inner, [(confirm.Text, Style.Plain)]);
                screen.Text(footer, " y / Enter confirm   n / Esc back", Hint);
                break;
        }
    }

    private static void DrawForm(Screen screen, FormStep form, Rect inner, Rect footer)
    {
        var rows = form.Sections
            .Select(s => new List<Span> { Span.Raw(s.Label.PadRight(18)), new(s.Summary(), Detail) })
            .Append([new Span($"▶ {form.Submit}", new Style(Bold: true))])
            .ToList();
        var offset = 0;
        if (form.Popup == null)
            screen.List(inner, rows, form.Cursor, ref offset, Highlight, "› ");
        else
            screen.List(inner, rows, null, ref offset, Highlight, "");

        string hint;
        if (form.Popup is not { } popup)
            hint = " ↑↓ move   Enter open a setting or show   Backspace clear   Esc back";
        else
        {
            var layer = popup.Top;
            var height = layer.Inner is ListStep items ? items.Items.Count + 3 : 5;
            var width = Math.Max(inner.Width * 4 / 5, Math.Min(30, inner.Width));
            height = Math.Min(height, inner.Height);
            var area = new Rect(inner.X + (inner.Width - width) / 2, inner.Y + (inner.Height - height) / 2, width, height);
            screen.Fill(area, Style.Plain);
            screen.Block(area, $" {layer.Title} ", new Style(Color.Cyan));
            var popupInner = area.Inner;
            switch (layer.Inner)
            {
                case ListStep list:
                {
                    var listHint = DrawList(screen, list, popupInner);
                    hint = list.Multi
                        ? listHint.Replace("Enter done", "Enter apply").Replace("Esc back", "Esc cancel")
                        : listHint.Replace("Esc back", "Esc close");
                    break;
                }
                case TextStep text:
                    DrawText(screen, text, popupInner);
                    hint = " Enter accept   Esc close";
                    break;
                default:
                    hint = "";
                    break;
            }
        }
        screen.Text(footer, hint, Hint);
    }

    private static string DrawList(Screen screen, ListStep list, Rect inner)
    {
        var filterArea = inner.Row(0);
        var listArea = inner.Below(1);
        var filterText = list.Filterable ? list.Filter.Length == 0 ? " Type to filter" : $" Filter: {list.Filter}" : "";
        screen.Line(filterArea, [new Span(filterText, new Style(Color.Yellow)), new Span(list.Message is { } m ? $"   {m}" : "", new Style(Color.Red))]);

        var visible = list.Visible();
        var rows = visible.Select(index =>
        {
            var item = list.Items[index];
            var mark = list.Multi ? list.PickNumber(item.Key) is { } n ? $"[{n}] " : "[ ] " : "";
            var spans = new List<Span> { Span.Raw(mark), Span.Raw(item.Label) };
            if (item.Detail.Length > 0)
                spans.Add(new Span($"  {item.Detail}", Detail));
            return spans;
        }).ToList();
        var offset = list.Offset;
        screen.List(listArea, rows, rows.Count == 0 ? null : list.Cursor, ref offset, Highlight, "› ");
        list.Offset = offset;
        if (rows.Count == 0)
            screen.Text(listArea.Row(0), "  Nothing matches the filter", Detail);

        return list.Multi
            ? " ↑↓ move   Space mark   Enter done   type to filter   Esc back"
            : list.Filterable
                ? " ↑↓ move   Enter choose   type to filter   Esc back"
                : " ↑↓ move   Enter choose   Esc back   q quit";
    }

    private static void DrawText(Screen screen, TextStep text, Rect inner)
    {
        screen.Text(inner.Row(0), $" {text.Hint}", Hint);
        var input = inner.Row(1);
        screen.Text(input, $" > {text.Value}");
        if (input.Height > 0)
            screen.Cursor = (input.X + 3 + text.CursorColumn(), input.Y);
        if (text.Error is { } error)
            screen.Text(inner.Row(2), $" {error}", new Style(Color.Red));
    }

    private static void DrawResult(Screen screen, App app, Rect body, Rect footer)
    {
        if (app.Result is not { } result)
            return;
        var (title, color) = result.Kind switch
        {
            ResultKind.Done => (" Done ", Color.Green),
            ResultKind.View => ($" View  updated {(int)Stopwatch.GetElapsedTime(result.At).TotalSeconds} s ago, refreshes every {(int)Math.Ceiling(app.Refresh.TotalSeconds)} s ",
                Color.Cyan),
            ResultKind.Failed => (" Error ", Color.Red),
            _ => (" Conflict ", Color.Yellow)
        };
        screen.Block(body, title, new Style(color));
        int? selected = result.Selected is { } at && at < result.Related.Count ? result.Related[at].Line : null;
        var lines = result.Text.Split('\n').Select((text, at) =>
            (text.TrimEnd('\r'), selected == at
                ? new Style(Bold: true, Reversed: true)
                : result.Related.Any(r => r.Line == at)
                    ? new Style(Color.Cyan)
                    : Style.Plain)).ToList();
        screen.Paragraph(body.Inner, lines, result.Scroll);
        var hint = result.Kind switch
        {
            ResultKind.Done => " Enter back to the menu   ↑↓ scroll",
            ResultKind.View when result.Related.Count > 0 =>
                " ↑↓ linked task   Enter open it   Esc back   PgUp PgDn scroll   r refresh   f setting   q menu",
            ResultKind.View => " Esc previous step   f change a setting   r refresh now   q menu   ↑↓ scroll",
            ResultKind.Failed => " Enter back to the steps (Esc there returns to the previous step)   ↑↓ scroll",
            _ => " r retry with the current data   Enter back to the steps   ↑↓ scroll"
        };
        screen.Text(footer, hint, Hint);
    }
}
