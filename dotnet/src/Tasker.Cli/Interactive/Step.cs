using System.Globalization;
using Tasker.Core.TaskSeries;

namespace Tasker.Cli.Interactive;

internal sealed record Item(string Key, string Label, string Detail = "", string? Version = null)
{
    public Item WithDetail(string detail) => this with { Detail = detail };
    public Item WithVersion(string version) => this with { Version = version };
}

internal enum Check
{
    Any,
    Required,
    Int,
    Float,
    Date,
    Color,
    Prefix
}

internal static partial class Checks
{
    [System.Text.RegularExpressions.GeneratedRegex(@"^[+-]?((?i:inf|infinity|nan)|([0-9]+\.?[0-9]*|\.[0-9]+)([eE][+-]?[0-9]+)?)$")]
    private static partial System.Text.RegularExpressions.Regex FloatPattern();

    public static string? Error(this Check check, string text, bool allowEmpty)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
            return allowEmpty || check == Check.Any ? null : "A value is required";

        switch (check)
        {
            case Check.Int:
                return ParseInt(trimmed) == null ? $"'{trimmed}' is not a whole number" : null;
            case Check.Float:
                return FloatPattern().IsMatch(trimmed) ? null : $"'{trimmed}' is not a number (use a dot for decimals)";
            case Check.Date:
                return trimmed.Length == 10 && Digits(trimmed, 0, 4) && trimmed[4] == '-' && Digits(trimmed, 5, 2) && trimmed[7] == '-'
                       && Digits(trimmed, 8, 2)
                    ? null
                    : $"'{trimmed}' is not a date (yyyy-MM-dd)";
            case Check.Color:
                var hex = trimmed.StartsWith('#') ? trimmed[1..] : "";
                return hex.Length == 6 && hex.All(char.IsAsciiHexDigit) ? null : $"'{trimmed}' is not a color in #RRGGBB format";
            case Check.Prefix:
                return SeriesPrefix.IsValid(trimmed) ? null : "Prefix must be 1-20 Latin letters or digits";
            default:
                return null;
        }
    }

    public static long? ParseInt(string text)
    {
        text = text.Trim();
        var digits = text.Length > 0 && text[0] is '+' or '-' ? text[1..] : text;
        if (digits.Length == 0 || !digits.All(char.IsAsciiDigit))
            return null;
        return long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    private static bool Digits(string text, int from, int count) =>
        text.Length >= from + count && text.AsSpan(from, count).IndexOfAnyExceptInRange('0', '9') < 0;
}

internal abstract record StepSpec(string Title)
{
    public StepSpec WithInitial(Answer answer) => (this, answer) switch
    {
        (SelectSpec select, ChoiceAnswer choice) => select with { Initial = choice.Item.Key },
        (MultiSelectSpec multi, ChoicesAnswer choices) => multi with { Initial = choices.Items.Select(x => x.Key).ToList() },
        (TextSpec text, TextAnswer value) => text with { Initial = value.Text },
        (EditorSpec editor, TextAnswer value) => editor with { Initial = value.Text },
        (FormSpec form, FormAnswer values) => form with { Sections = form.Sections.Select(x => x.Restored(values.Values)).ToList() },
        _ => this
    };
}

internal sealed record SelectSpec(string Title, List<Item> Items, string? Initial = null, bool Filterable = true) : StepSpec(Title);

internal sealed record MultiSelectSpec(string Title, List<Item> Items, List<string> Initial, bool AllowEmpty) : StepSpec(Title);

internal sealed record TextSpec(string Title, string Initial, string Hint, Check Check, bool AllowEmpty) : StepSpec(Title);

internal sealed record EditorSpec(string Title, string Initial, string Hint) : StepSpec(Title);

internal sealed record ConfirmSpec(string Title, string Text) : StepSpec(Title);

internal sealed record FormSpec(string Title, List<Section> Sections, string Submit) : StepSpec(Title);

internal sealed class Chain(StepSpec spec)
{
    public StepSpec Spec { get; } = spec;
    public List<(string Key, Chain Next)> Then { get; } = [];
    public bool IsPrefix { get; private set; }

    public Chain Next(string key, Chain next)
    {
        Then.Add((key, next));
        return this;
    }

    public Chain Prefix()
    {
        IsPrefix = true;
        return this;
    }
}

internal abstract record SectionKind;

internal sealed record MultiSection(List<Item> Items) : SectionKind;

internal sealed record SingleSection(List<Item> Items) : SectionKind;

internal sealed record ListSection(Chain Add, string AddLabel) : SectionKind;

internal sealed class Section(string key, string label, string empty, SectionKind kind)
{
    public string Key { get; } = key;
    public string Label { get; } = label;
    public string Empty { get; } = empty;
    public SectionKind Kind { get; } = kind;
    public List<string> Values { get; set; } = [];

    public Section WithValues(IEnumerable<string> values)
    {
        Values = values.ToList();
        return this;
    }

    public Section Copy() => new(Key, Label, Empty, Kind) { Values = [.. Values] };

    public string Summary()
    {
        if (Values.Count == 0)
            return Empty;
        return ChoiceItems() is { } items
            ? string.Join(", ", Values.Select(v => items.FirstOrDefault(i => i.Key == v)?.Label ?? v))
            : string.Join(", ", Values);
    }

    internal Section Restored(List<FormValue> values)
    {
        var copy = Copy();
        if (values.FirstOrDefault(v => v.Key == Key) is { } value)
            copy.Values = value.Values.Where(Accepts).ToList();
        return copy;
    }

    private bool Accepts(string value) => ChoiceItems() is not { } items || items.Any(i => i.Key == value);

    private List<Item>? ChoiceItems() => Kind switch
    {
        MultiSection multi => multi.Items,
        SingleSection single => single.Items,
        _ => null
    };
}

internal sealed record FormValue(string Key, string Label, List<string> Values, string Summary);

internal abstract record Answer;

internal sealed record ChoiceAnswer(Item Item) : Answer;

internal sealed record ChoicesAnswer(List<Item> Items) : Answer;

internal sealed record TextAnswer(string Text) : Answer;

internal sealed record ConfirmedAnswer : Answer;

internal sealed record FormAnswer(List<FormValue> Values) : Answer;

internal enum KeyKind
{
    Char,
    Enter,
    Esc,
    Up,
    Down,
    Left,
    Right,
    PageUp,
    PageDown,
    Home,
    End,
    Backspace,
    Delete,
    Tab,
    CtrlC,
    CtrlF,
    CtrlL
}

internal readonly record struct Key(KeyKind Kind, char Char = '\0')
{
    public static Key Of(char c) => new(KeyKind.Char, c);
    public static readonly Key Enter = new(KeyKind.Enter);
    public static readonly Key Esc = new(KeyKind.Esc);
    public static readonly Key Up = new(KeyKind.Up);
    public static readonly Key Down = new(KeyKind.Down);
    public static readonly Key Left = new(KeyKind.Left);
    public static readonly Key Right = new(KeyKind.Right);
    public static readonly Key PageUp = new(KeyKind.PageUp);
    public static readonly Key PageDown = new(KeyKind.PageDown);
    public static readonly Key Home = new(KeyKind.Home);
    public static readonly Key End = new(KeyKind.End);
    public static readonly Key Backspace = new(KeyKind.Backspace);
    public static readonly Key Delete = new(KeyKind.Delete);
    public static readonly Key Tab = new(KeyKind.Tab);
    public static readonly Key CtrlC = new(KeyKind.CtrlC);
    public static readonly Key CtrlF = new(KeyKind.CtrlF);
    public static readonly Key CtrlL = new(KeyKind.CtrlL);

    public bool IsChar(char c) => Kind == KeyKind.Char && Char == c;
}

internal abstract record Outcome
{
    public static readonly Outcome Nothing = new NothingOutcome();
    public static readonly Outcome Back = new BackOutcome();
}

internal sealed record NothingOutcome : Outcome;

internal sealed record BackOutcome : Outcome;

internal sealed record AnswerOutcome(Answer Answer) : Outcome;

internal sealed record OpenEditorOutcome(string Text) : Outcome;

internal abstract class StepState
{
    public static StepState FromSpec(StepSpec spec)
    {
        switch (spec)
        {
            case SelectSpec select:
            {
                var list = new ListStep([.. select.Items], select.Filterable);
                if (select.Initial is { } key && select.Items.FindIndex(i => i.Key == key) is var at and >= 0)
                    list.Cursor = at;
                return list;
            }
            case MultiSelectSpec multi:
                return new ListStep([.. multi.Items], true)
                {
                    Multi = true,
                    AllowEmpty = multi.AllowEmpty,
                    Picked = multi.Initial.Where(k => multi.Items.Any(i => i.Key == k)).ToList()
                };
            case TextSpec text:
                return new TextStep
                {
                    Value = text.Initial,
                    Cursor = CharCount(text.Initial),
                    Hint = text.Hint,
                    Check = text.Check,
                    AllowEmpty = text.AllowEmpty
                };
            case EditorSpec editor:
                return new EditorStep { Text = editor.Initial, Hint = editor.Hint };
            case ConfirmSpec confirm:
                return new ConfirmStep { Text = confirm.Text };
            case FormSpec form:
                return new FormStep { Sections = form.Sections.Select(x => x.Copy()).ToList(), Submit = form.Submit };
            default:
                throw new ArgumentOutOfRangeException(nameof(spec));
        }
    }

    internal static int CharCount(string text) => text.EnumerateRunes().Count();

    public abstract Outcome Handle(Key key, int page);
}

internal sealed class ListStep(List<Item> items, bool filterable) : StepState
{
    public List<Item> Items { get; } = items;
    public string Filter { get; set; } = "";
    public int Cursor { get; set; }
    public List<string> Picked { get; set; } = [];
    public bool Multi { get; init; }
    public bool AllowEmpty { get; init; } = true;
    public bool Filterable { get; } = filterable;
    public string? Message { get; set; }
    public int Offset { get; set; }

    public List<int> Visible()
    {
        var wanted = Filter.ToLowerInvariant();
        var result = new List<int>();
        for (var i = 0; i < Items.Count; i++)
        {
            var item = Items[i];
            if (wanted.Length == 0 || item.Label.ToLowerInvariant().Contains(wanted, StringComparison.Ordinal)
                                   || item.Detail.ToLowerInvariant().Contains(wanted, StringComparison.Ordinal))
                result.Add(i);
        }
        return result;
    }

    public Item? Current()
    {
        var visible = Visible();
        return Cursor < visible.Count ? Items[visible[Cursor]] : null;
    }

    public int? PickNumber(string key) => Picked.IndexOf(key) is var at and >= 0 ? at + 1 : null;

    private void Clamp()
    {
        var count = Visible().Count;
        Cursor = count == 0 ? 0 : Math.Min(Cursor, count - 1);
    }

    public override Outcome Handle(Key key, int page)
    {
        Message = null;
        var count = Visible().Count;
        switch (key.Kind)
        {
            case KeyKind.Up:
                Cursor = Math.Max(0, Cursor - 1);
                break;
            case KeyKind.Down:
                Cursor = Math.Min(Cursor + 1, Math.Max(0, count - 1));
                break;
            case KeyKind.PageUp:
                Cursor = Math.Max(0, Cursor - page);
                break;
            case KeyKind.PageDown:
                Cursor = Math.Min(Cursor + page, Math.Max(0, count - 1));
                break;
            case KeyKind.Home:
                Cursor = 0;
                break;
            case KeyKind.End:
                Cursor = Math.Max(0, count - 1);
                break;
            case KeyKind.Esc:
                return Outcome.Back;
            case KeyKind.Backspace:
                if (Filter.Length > 0)
                    Filter = RemoveLastRune(Filter);
                Clamp();
                break;
            case KeyKind.Char when Multi && key.Char == ' ':
            case KeyKind.Tab when Multi:
                if (Current() is { } item)
                {
                    if (Picked.IndexOf(item.Key) is var at and >= 0)
                        Picked.RemoveAt(at);
                    else
                        Picked.Add(item.Key);
                }
                break;
            case KeyKind.Enter:
                if (Multi)
                {
                    if (Picked.Count == 0 && !AllowEmpty)
                    {
                        Message = "Choose at least one (Space marks an item)";
                        return Outcome.Nothing;
                    }
                    var chosen = Picked.Select(k => Items.FirstOrDefault(i => i.Key == k)).OfType<Item>().ToList();
                    return new AnswerOutcome(new ChoicesAnswer(chosen));
                }
                if (Current() is { } current)
                    return new AnswerOutcome(new ChoiceAnswer(current));
                break;
            case KeyKind.Char when Filterable && !char.IsControl(key.Char):
                Filter += key.Char;
                Clamp();
                break;
        }
        return Outcome.Nothing;
    }

    private static string RemoveLastRune(string text)
    {
        var last = text.Length - 1;
        return char.IsLowSurrogate(text[last]) && last > 0 && char.IsHighSurrogate(text[last - 1]) ? text[..(last - 1)] : text[..last];
    }
}

internal sealed class TextStep : StepState
{
    public string Value { get; set; } = "";
    public int Cursor { get; set; }
    public string Hint { get; init; } = "";
    public Check Check { get; init; }
    public bool AllowEmpty { get; init; }
    public string? Error { get; set; }

    public int CursorColumn() => Table.Width(string.Concat(Value.EnumerateRunes().Take(Cursor)));

    private int IndexAt(int runes)
    {
        var index = 0;
        foreach (var rune in Value.EnumerateRunes())
        {
            if (runes-- == 0)
                return index;
            index += rune.Utf16SequenceLength;
        }
        return Value.Length;
    }

    public override Outcome Handle(Key key, int page)
    {
        Error = null;
        var length = CharCount(Value);
        switch (key.Kind)
        {
            case KeyKind.Char when !char.IsControl(key.Char):
                Value = Value.Insert(IndexAt(Cursor), key.Char.ToString());
                Cursor++;
                break;
            case KeyKind.Backspace when Cursor > 0:
            {
                var at = IndexAt(Cursor - 1);
                Value = Value.Remove(at, IndexAt(Cursor) - at);
                Cursor--;
                break;
            }
            case KeyKind.Delete when Cursor < length:
            {
                var at = IndexAt(Cursor);
                Value = Value.Remove(at, IndexAt(Cursor + 1) - at);
                break;
            }
            case KeyKind.Left:
                Cursor = Math.Max(0, Cursor - 1);
                break;
            case KeyKind.Right:
                Cursor = Math.Min(Cursor + 1, length);
                break;
            case KeyKind.Home:
                Cursor = 0;
                break;
            case KeyKind.End:
                Cursor = length;
                break;
            case KeyKind.Esc:
                return Outcome.Back;
            case KeyKind.Enter:
                if (Check.Error(Value, AllowEmpty) is { } error)
                    Error = error;
                else
                    return new AnswerOutcome(new TextAnswer(Value.Trim()));
                break;
        }
        return Outcome.Nothing;
    }
}

internal sealed class EditorStep : StepState
{
    public string Text { get; set; } = "";
    public string Hint { get; init; } = "";
    public string? Message { get; set; }

    public override Outcome Handle(Key key, int page)
    {
        Message = null;
        return key.Kind switch
        {
            KeyKind.Enter => new AnswerOutcome(new TextAnswer(Text)),
            KeyKind.Char when key.Char is 'e' or 'E' => new OpenEditorOutcome(Text),
            KeyKind.Esc => Outcome.Back,
            _ => Outcome.Nothing
        };
    }
}

internal sealed class ConfirmStep : StepState
{
    public string Text { get; init; } = "";

    public override Outcome Handle(Key key, int page) => key.Kind switch
    {
        KeyKind.Enter => new AnswerOutcome(new ConfirmedAnswer()),
        KeyKind.Char when key.Char is 'y' or 'Y' => new AnswerOutcome(new ConfirmedAnswer()),
        KeyKind.Esc => Outcome.Back,
        KeyKind.Char when key.Char is 'n' or 'N' => Outcome.Back,
        _ => Outcome.Nothing
    };
}

internal sealed class Layer(string title, StepState inner, List<(string Key, Chain Next)> then, bool prefix)
{
    public string Title { get; } = title;
    public StepState Inner { get; } = inner;
    public List<(string Key, Chain Next)> Then { get; } = then;
    public bool Prefix { get; } = prefix;

    public static Layer Of(StepSpec spec, List<(string Key, Chain Next)> then, bool prefix)
    {
        var inner = StepState.FromSpec(spec) switch
        {
            ListStep list => (StepState)list,
            TextStep text => text,
            _ => new TextStep { Check = Check.Any, AllowEmpty = true }
        };
        return new Layer(spec.Title, inner, then, prefix);
    }
}

internal sealed class Popup(int section, List<Layer> layers)
{
    public int Section { get; } = section;
    public List<Layer> Layers { get; set; } = layers;
    public List<(string Piece, bool Prefix)> Pieces { get; } = [];
    public Layer Top => Layers[^1];
}

internal sealed class FormStep : StepState
{
    public List<Section> Sections { get; init; } = [];
    public string Submit { get; init; } = "";
    public int Cursor { get; set; }
    public Popup? Popup { get; set; }

    public List<FormValue> Values() => Sections.Select(s => new FormValue(s.Key, s.Label, [.. s.Values], s.Summary())).ToList();

    private static Layer EntriesLayer(Section section)
    {
        var add = ((ListSection)section.Kind).AddLabel;
        var items = section.Values.Select((v, at) => new Item($"drop{at}", v).WithDetail("Enter removes it")).ToList();
        items.Add(new Item("add", $"+ {add}"));
        items.Add(new Item("done", "Done"));
        return new Layer(section.Label, new ListStep(items, true) { Cursor = section.Values.Count }, [], false);
    }

    public bool OpenSection(string key)
    {
        var at = Sections.FindIndex(s => s.Key == key);
        if (at < 0)
            return false;
        Cursor = at;
        Open();
        return true;
    }

    private void Open()
    {
        if (Cursor >= Sections.Count)
            return;
        var section = Sections[Cursor];
        var layer = section.Kind switch
        {
            MultiSection multi => new Layer(section.Label, new ListStep([.. multi.Items], true) { Multi = true, Picked = [.. section.Values] }, [], false),
            SingleSection single => new Layer(section.Label, new ListStep([.. single.Items], true)
            {
                Cursor = section.Values.FirstOrDefault() is { } value && single.Items.FindIndex(i => i.Key == value) is var at and >= 0 ? at : 0
            }, [], false),
            _ => EntriesLayer(section)
        };
        Popup = new Popup(Cursor, [layer]);
    }

    private void HandlePopup(Key key, int page)
    {
        if (Popup is not { } popup)
            return;
        var at = popup.Section;
        var outcome = popup.Top.Inner.Handle(key, page);
        switch (outcome)
        {
            case NothingOutcome or OpenEditorOutcome:
                return;
            case BackOutcome:
                popup.Layers.RemoveAt(popup.Layers.Count - 1);
                if (popup.Layers.Count == 0)
                    Popup = null;
                else
                {
                    var keep = Math.Max(0, popup.Layers.Count - 2);
                    if (popup.Pieces.Count > keep)
                        popup.Pieces.RemoveRange(keep, popup.Pieces.Count - keep);
                }
                return;
            case AnswerOutcome { Answer: ChoicesAnswer choices }:
                Sections[at].Values = choices.Items.Select(i => i.Key).ToList();
                Popup = null;
                return;
            case AnswerOutcome { Answer: ChoiceAnswer choice } when popup.Layers.Count == 1:
                switch (Sections[at].Kind)
                {
                    case SingleSection:
                        Sections[at].Values = [choice.Item.Key];
                        Popup = null;
                        break;
                    case ListSection list:
                        if (choice.Item.Key == "done")
                            Popup = null;
                        else if (choice.Item.Key == "add")
                            popup.Layers.Add(Layer.Of(list.Add.Spec, [.. list.Add.Then], list.Add.IsPrefix));
                        else if (choice.Item.Key.StartsWith("drop", StringComparison.Ordinal)
                                 && int.TryParse(choice.Item.Key[4..], NumberStyles.None, CultureInfo.InvariantCulture, out var drop)
                                 && drop < Sections[at].Values.Count)
                        {
                            Sections[at].Values.RemoveAt(drop);
                            popup.Layers = [EntriesLayer(Sections[at])];
                        }
                        break;
                }
                return;
            case AnswerOutcome { Answer: var answer }:
            {
                string piece;
                string? chosen;
                switch (answer)
                {
                    case ChoiceAnswer choice:
                        piece = choice.Item.Key;
                        chosen = choice.Item.Key;
                        break;
                    case TextAnswer text:
                        piece = text.Text;
                        chosen = null;
                        break;
                    default:
                        return;
                }
                var layer = popup.Top;
                var next = chosen == null ? null : layer.Then.FirstOrDefault(x => x.Key == chosen).Next;
                popup.Pieces.Add((piece, layer.Prefix));
                if (next != null)
                    popup.Layers.Add(Layer.Of(next.Spec, [.. next.Then], next.IsPrefix));
                else
                {
                    var value = string.Concat(popup.Pieces.Where(x => x.Prefix).Select(x => x.Piece))
                                + string.Concat(popup.Pieces.Where(x => !x.Prefix).Select(x => x.Piece));
                    popup.Pieces.Clear();
                    Sections[at].Values.Add(value);
                    popup.Layers = [EntriesLayer(Sections[at])];
                }
                return;
            }
        }
    }

    public override Outcome Handle(Key key, int page)
    {
        if (Popup != null)
        {
            HandlePopup(key, page);
            return Outcome.Nothing;
        }

        var rows = Sections.Count + 1;
        switch (key.Kind)
        {
            case KeyKind.Up:
                Cursor = Math.Max(0, Cursor - 1);
                break;
            case KeyKind.Down:
                Cursor = Math.Min(Cursor + 1, rows - 1);
                break;
            case KeyKind.PageUp or KeyKind.Home:
                Cursor = 0;
                break;
            case KeyKind.PageDown or KeyKind.End:
                Cursor = rows - 1;
                break;
            case KeyKind.Esc:
                return Outcome.Back;
            case KeyKind.Backspace or KeyKind.Delete:
                if (Cursor < Sections.Count)
                    Sections[Cursor].Values.Clear();
                break;
            case KeyKind.Enter:
                if (Cursor < Sections.Count)
                    Open();
                else
                    return new AnswerOutcome(new FormAnswer(Values()));
                break;
        }
        return Outcome.Nothing;
    }
}
