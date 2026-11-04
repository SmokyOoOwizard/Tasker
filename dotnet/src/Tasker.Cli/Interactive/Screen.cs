using System.Globalization;
using System.Text;

namespace Tasker.Cli.Interactive;

internal enum Color
{
    Default,
    White,
    Gray,
    DarkGray,
    Cyan,
    Yellow,
    Red,
    Green
}

internal readonly record struct Style(Color Fg = Color.Default, Color Bg = Color.Default, bool Bold = false, bool Reversed = false)
{
    public static readonly Style Plain = new();
    public Style WithFg(Color color) => this with { Fg = color };
}

internal readonly record struct Rect(int X, int Y, int Width, int Height)
{
    public Rect Inner => Width < 2 || Height < 2 ? new Rect(X, Y, 0, 0) : new Rect(X + 1, Y + 1, Width - 2, Height - 2);
    public Rect Row(int at) => new(X, Y + at, Width, at < Height ? 1 : 0);
    public Rect Below(int skip) => new(X, Y + skip, Width, Math.Max(0, Height - skip));
}

internal readonly record struct Span(string Text, Style Style)
{
    public static Span Raw(string text) => new(text, Style.Plain);
}

internal sealed class Screen
{
    private readonly string[] _text;
    private readonly Style[] _style;

    public Screen(int width, int height)
    {
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);
        _text = new string[Width * Height];
        _style = new Style[Width * Height];
        Array.Fill(_text, " ");
    }

    public int Width { get; }
    public int Height { get; }
    public Rect Area => new(0, 0, Width, Height);
    public (int X, int Y)? Cursor { get; set; }

    public static IEnumerable<string> Elements(string text)
    {
        var e = StringInfo.GetTextElementEnumerator(text);
        while (e.MoveNext())
            yield return (string)e.Current;
    }

    public void Fill(Rect area, Style style)
    {
        for (var y = area.Y; y < area.Y + area.Height && y < Height; y++)
        for (var x = area.X; x < area.X + area.Width && x < Width; x++)
        {
            _text[y * Width + x] = " ";
            _style[y * Width + x] = style;
        }
    }

    public int Write(int x, int y, Rect clip, string text, Style style)
    {
        if (y < clip.Y || y >= clip.Y + clip.Height || y >= Height)
            return x;
        foreach (var element in Elements(text))
        {
            if (element is "\n" or "\r" or "\r\n")
                continue;
            var width = element == "\t" ? 1 : Table.Width(element);
            if (width == 0)
                continue;
            if (x + width > clip.X + clip.Width || x + width > Width)
                return x;
            if (x >= clip.X)
            {
                var at = y * Width + x;
                _text[at] = element == "\t" ? " " : element;
                _style[at] = Merge(_style[at], style);
                for (var extra = 1; extra < width; extra++)
                {
                    _text[at + extra] = "";
                    _style[at + extra] = Merge(_style[at + extra], style);
                }
            }
            x += width;
        }
        return x;
    }

    private static Style Merge(Style under, Style over) => new(
        over.Fg != Color.Default ? over.Fg : under.Fg,
        over.Bg != Color.Default ? over.Bg : under.Bg,
        over.Bold || under.Bold,
        over.Reversed || under.Reversed);

    public void Line(Rect area, IEnumerable<Span> spans, Style lineStyle = default)
    {
        if (area.Height <= 0)
            return;
        if (lineStyle != default)
            Fill(new Rect(area.X, area.Y, area.Width, 1), lineStyle);
        var x = area.X;
        foreach (var span in spans)
            x = Write(x, area.Y, area, span.Text, span.Style);
    }

    public void Text(Rect area, string text, Style style = default) => Line(area, [new Span(text, style)]);

    public void Block(Rect area, string title, Style border = default)
    {
        if (area.Width < 2 || area.Height < 2)
            return;
        var right = area.X + area.Width - 1;
        var bottom = area.Y + area.Height - 1;
        for (var x = area.X + 1; x < right; x++)
        {
            Write(x, area.Y, area, "─", border);
            Write(x, bottom, area, "─", border);
        }
        for (var y = area.Y + 1; y < bottom; y++)
        {
            Write(area.X, y, area, "│", border);
            Write(right, y, area, "│", border);
        }
        Write(area.X, area.Y, area, "┌", border);
        Write(right, area.Y, area, "┐", border);
        Write(area.X, bottom, area, "└", border);
        Write(right, bottom, area, "┘", border);
        if (title.Length > 0)
            Write(area.X + 1, area.Y, new Rect(area.X + 1, area.Y, area.Width - 2, 1), title, border);
    }

    public static List<string> Wrap(string text, int width)
    {
        var lines = new List<string>();
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (width <= 0)
            {
                lines.Add(raw);
                continue;
            }
            var line = new StringBuilder();
            var lineWidth = 0;
            foreach (var word in SplitKeepingSpaces(raw))
            {
                var wordWidth = Table.Width(word);
                if (lineWidth + wordWidth > width && lineWidth > 0)
                {
                    lines.Add(line.ToString().TrimEnd());
                    line.Clear();
                    lineWidth = 0;
                    if (word.Trim().Length == 0)
                        continue;
                }
                if (wordWidth > width)
                {
                    foreach (var element in Elements(word))
                    {
                        var elementWidth = Table.Width(element);
                        if (lineWidth + elementWidth > width && lineWidth > 0)
                        {
                            lines.Add(line.ToString());
                            line.Clear();
                            lineWidth = 0;
                        }
                        line.Append(element);
                        lineWidth += elementWidth;
                    }
                    continue;
                }
                line.Append(word);
                lineWidth += wordWidth;
            }
            lines.Add(line.ToString());
        }
        return lines;
    }

    private static IEnumerable<string> SplitKeepingSpaces(string text)
    {
        var current = new StringBuilder();
        var space = false;
        foreach (var c in text)
        {
            var isSpace = c == ' ';
            if (current.Length > 0 && isSpace != space)
            {
                yield return current.ToString();
                current.Clear();
            }
            space = isSpace;
            current.Append(c);
        }
        if (current.Length > 0)
            yield return current.ToString();
    }

    public void Paragraph(Rect area, IReadOnlyList<(string Text, Style Style)> lines, int scroll = 0)
    {
        var wrapped = new List<(string Text, Style Style)>();
        foreach (var (text, style) in lines)
            wrapped.AddRange(Wrap(text, area.Width).Select(x => (x, style)));
        for (var row = 0; row < area.Height && scroll + row < wrapped.Count; row++)
        {
            var (text, style) = wrapped[scroll + row];
            Text(area.Row(row), text, style);
        }
    }

    public void List(Rect area, IReadOnlyList<List<Span>> rows, int? selected, ref int offset, Style highlight, string symbol)
    {
        if (area.Height <= 0)
            return;
        if (selected is { } at)
        {
            if (at < offset)
                offset = at;
            else if (at >= offset + area.Height)
                offset = at - area.Height + 1;
        }
        offset = Math.Max(0, Math.Min(offset, Math.Max(0, rows.Count - 1)));
        var pad = selected != null || symbol.Length == 0 ? new string(' ', Table.Width(symbol)) : "";
        for (var row = 0; row < area.Height && offset + row < rows.Count; row++)
        {
            var index = offset + row;
            var line = area.Row(row);
            var isSelected = index == selected;
            var spans = new List<Span> { new(isSelected ? symbol : pad, Style.Plain) };
            spans.AddRange(rows[index]);
            if (isSelected)
                Fill(new Rect(line.X, line.Y, line.Width, 1), highlight);
            Line(line, spans);
        }
    }

    public string Render()
    {
        var output = new StringBuilder("\x1b[?25l\x1b[H");
        var current = new Style(Color.White, Color.White, true, true);
        for (var y = 0; y < Height; y++)
        {
            output.Append(CultureInfo.InvariantCulture, $"\x1b[{y + 1};1H");
            for (var x = 0; x < Width; x++)
            {
                var at = y * Width + x;
                if (_text[at].Length == 0)
                    continue;
                if (_style[at] != current)
                {
                    current = _style[at];
                    output.Append(Sgr(current));
                }
                output.Append(_text[at]);
            }
        }
        output.Append("\x1b[0m");
        if (Cursor is var (cx, cy))
            output.Append(CultureInfo.InvariantCulture, $"\x1b[{cy + 1};{cx + 1}H\x1b[?25h");
        return output.ToString();
    }

    private static string Sgr(Style style)
    {
        var codes = new List<string> { "0" };
        if (style.Bold)
            codes.Add("1");
        if (style.Reversed)
            codes.Add("7");
        if (Code(style.Fg, false) is { } fg)
            codes.Add(fg);
        if (Code(style.Bg, true) is { } bg)
            codes.Add(bg);
        return $"\x1b[{string.Join(';', codes)}m";
    }

    private static string? Code(Color color, bool background)
    {
        var code = color switch
        {
            Color.White => 37,
            Color.Gray => 37,
            Color.DarkGray => 90,
            Color.Cyan => 36,
            Color.Yellow => 33,
            Color.Red => 31,
            Color.Green => 32,
            _ => 0
        };
        if (code == 0)
            return null;
        return (background ? code + 10 : code).ToString(CultureInfo.InvariantCulture);
    }
}
