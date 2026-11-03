using System.Diagnostics;
using Tasker.Core.Projects;
using Tasker.Core.Tasks;
using Tasker.Storage.Files.Workspaces;

namespace Tasker.Cli.Interactive;

internal enum FlowAction
{
    View,
    Create,
    Change,
    Delete
}

internal enum EntityKind
{
    Project,
    Task,
    Status,
    StatusSet,
    TaskType,
    Series,
    Field,
    Enum,
    Board,
    LinkType,
    User,
    Agent
}

internal static class Kinds
{
    public static readonly EntityKind[] All =
    [
        EntityKind.Project, EntityKind.Task, EntityKind.Status, EntityKind.StatusSet, EntityKind.TaskType, EntityKind.Series,
        EntityKind.Field, EntityKind.Enum, EntityKind.Board, EntityKind.LinkType, EntityKind.User, EntityKind.Agent
    ];

    public static string Label(this FlowAction action) => action switch
    {
        FlowAction.View => "Browse",
        FlowAction.Create => "Create",
        FlowAction.Change => "Change",
        _ => "Delete"
    };

    public static string Key(this EntityKind kind) => kind switch
    {
        EntityKind.Project => "project",
        EntityKind.Task => "task",
        EntityKind.Status => "status",
        EntityKind.StatusSet => "status-set",
        EntityKind.TaskType => "task-type",
        EntityKind.Series => "series",
        EntityKind.Field => "field",
        EntityKind.Enum => "enum",
        EntityKind.Board => "board",
        EntityKind.LinkType => "link-type",
        EntityKind.User => "user",
        _ => "agent"
    };

    public static string Label(this EntityKind kind) => kind switch
    {
        EntityKind.Project => "Project",
        EntityKind.Task => "Task",
        EntityKind.Status => "Status",
        EntityKind.StatusSet => "Status set",
        EntityKind.TaskType => "Task type",
        EntityKind.Series => "Series",
        EntityKind.Field => "Field",
        EntityKind.Enum => "Enum",
        EntityKind.Board => "Board",
        EntityKind.LinkType => "Link type",
        EntityKind.User => "User",
        _ => "Agent"
    };

    public static EntityKind? FromKey(string key) => All.Cast<EntityKind?>().FirstOrDefault(k => k!.Value.Key() == key);
}

internal abstract record Flow
{
    public bool IsMenu => this is MenuFlow or KindsFlow or SwitchProjectFlow;

    public string Title => this switch
    {
        KindsFlow kinds => kinds.Action.Label(),
        EntityFlow entity => entity.Kind.Label(),
        SwitchProjectFlow => "Project",
        CardFlow => "Linked task",
        _ => ""
    };
}

internal sealed record MenuFlow : Flow;

internal sealed record KindsFlow(FlowAction Action) : Flow;

internal sealed record EntityFlow(FlowAction Action, EntityKind Kind) : Flow;

internal sealed record SwitchProjectFlow : Flow;

internal sealed record PickFlow : Flow;

internal sealed record CardFlow(Guid Id) : Flow;

internal abstract record Next;

internal sealed record StepNext(StepSpec Spec) : Next;

internal sealed record EnterNext(Flow Flow) : Next;

internal sealed record RunNext(List<string> Argv) : Next;

internal sealed record SwitchProjectNext(Project Project) : Next;

internal sealed record JumpToNext(int Index) : Next;

internal sealed record QuitNext : Next;

internal sealed record FailedNext(string Text) : Next;

internal abstract record Effect
{
    public static readonly Effect None = new NoEffect();
    public static readonly Effect Quit = new QuitEffect();
}

internal sealed record NoEffect : Effect;

internal sealed record QuitEffect : Effect;

internal sealed record RunEffect(List<string> Argv) : Effect;

internal sealed record OpenEditorEffect(string Text) : Effect;

internal enum ResultKind
{
    Done,
    View,
    Failed,
    Retryable
}

internal sealed class ResultView
{
    public string Text { get; init; } = "";
    public ResultKind Kind { get; init; }
    public int Scroll { get; set; }
    public long At { get; init; }
    public List<(int Line, Guid Id)> Related { get; init; } = [];
    public int? Selected { get; set; }
}

internal sealed class Frame(Flow flow, StepSpec spec, StepState state)
{
    public Flow Flow { get; } = flow;
    public List<Answer> Answers { get; set; } = [];
    public List<string> Titles { get; set; } = [];
    public List<(string Title, Answer Answer)> Tail { get; set; } = [];
    public bool Replay { get; set; }
    public bool Jumped { get; set; }
    public (string Title, Answer Answer)? Current { get; set; }
    public StepSpec Spec { get; set; } = spec;
    public StepState State { get; set; } = state;
}

internal sealed class App
{
    private readonly List<Frame> _frames = [];
    private readonly List<int?> _remembered = [];
    private long _lastRefresh = Stopwatch.GetTimestamp();
    private int? _restore;

    private App(Session session, Context context, WorkspaceLocation location, Project? project, TimeSpan refresh)
    {
        Session = session;
        Context = context;
        Location = location;
        Project = project;
        Refresh = refresh;
    }

    public Session Session { get; }
    public Context Context { get; }
    public WorkspaceLocation Location { get; }
    public Project? Project { get; private set; }
    public ResultView? Result { get; private set; }
    public int Page { get; set; } = 10;
    public TimeSpan Refresh { get; }

    public static async Task<App> Create(Session session, WorkspaceLocation location, Project? project, TimeSpan refresh, CancellationToken ct)
    {
        var context = new Context(session, TextWriter.Null, TextWriter.Null, false, null, ct);
        var app = new App(session, context, location, project, refresh);
        await app.Enter(new MenuFlow());
        if (app.Project == null && (await context.AllProjects()).Count > 1)
            await app.Enter(new SwitchProjectFlow());
        return app;
    }

    public string WorkspaceLabel => Location.Path;

    private static string AnswerSummary(Answer answer) => answer switch
    {
        ChoiceAnswer choice => choice.Item.Label,
        ChoicesAnswer { Items.Count: 0 } => "(none)",
        ChoicesAnswer choices => string.Join(", ", choices.Items.Select(i => i.Label)),
        TextAnswer { Text.Length: 0 } => "(empty)",
        TextAnswer text => text.Text.Split('\n')[0].TrimEnd('\r'),
        ConfirmedAnswer => "yes",
        FormAnswer form => form.Values.Where(v => v.Values.Count > 0).Select(v => $"{v.Label}: {v.Summary}").ToList() is { Count: > 0 } set
            ? string.Join("; ", set)
            : "(defaults)",
        _ => ""
    };

    private static bool Fits(StepSpec spec, Answer answer) => (spec, answer) switch
    {
        (SelectSpec select, ChoiceAnswer choice) => select.Items.Any(i => i.Key == choice.Item.Key),
        (MultiSelectSpec multi, ChoicesAnswer chosen) => (multi.AllowEmpty || chosen.Items.Count > 0)
                                                         && chosen.Items.All(c => multi.Items.Any(i => i.Key == c.Key)),
        (TextSpec text, TextAnswer value) => text.Check.Error(value.Text, text.AllowEmpty) == null,
        (EditorSpec, TextAnswer) => true,
        (FormSpec form, FormAnswer values) => form.Sections.Count == values.Values.Count
                                             && form.Sections.Zip(values.Values).All(x => x.First.Key == x.Second.Key),
        _ => false
    };

    public string Breadcrumb()
    {
        var parts = new List<string>();
        for (var at = 0; at < _frames.Count; at++)
        {
            var frame = _frames[at];
            var title = frame.Flow.Title;
            if (title.Length > 0)
                parts.Add(title);
            var card = frame.Flow is CardFlow;
            var beforeCard = at + 1 < _frames.Count && _frames[at + 1].Flow is CardFlow;
            if (!card && (at + 1 == _frames.Count || beforeCard))
                parts.Add(frame.Spec.Title);
        }
        return string.Join(" › ", parts);
    }

    public StepState? State => _frames.Count > 0 ? _frames[^1].State : null;
    public bool InMenu => _frames.Count > 0 && _frames[^1].Flow.IsMenu;
    public StepSpec? CurrentSpec => _frames.Count > 0 ? _frames[^1].Spec : null;

    public bool HasFilters => Result == null && _frames.Count > 0 && _frames[^1].Answers.Any(a => a is FormAnswer);

    public bool HasColumns => Result == null && _frames.Count > 0
                                             && _frames[^1].Answers.Any(a => a is FormAnswer form && form.Values.Any(v => v.Key == "columns"));

    private async Task BackToColumns()
    {
        if (!HasColumns)
            return;
        await BackToFilters();
        if (State is FormStep form)
            form.OpenSection("columns");
    }

    private async Task BackToFilters()
    {
        if (!HasFilters)
            return;
        while (true)
        {
            if (_frames.Count == 0 || _frames[^1].Answers.Count == 0)
                return;
            var form = _frames[^1].Answers[^1] is FormAnswer;
            await Back();
            if (form)
                return;
        }
    }

    private async Task<Next> NextOf(Flow flow, List<Answer> answers, bool fresh)
    {
        var wizard = new Wizard(new FlowContext(Context, Project, fresh), answers);
        try
        {
            switch (flow)
            {
                case MenuFlow:
                {
                    var choice = wizard.PlainMenu("What to do",
                    [
                        new Item("browse", "Browse", "lists with filters, cards, boards"),
                        new Item("create", "Create", "a project, task, status, set, type, series, field, enum, board, link type, user or agent"),
                        new Item("change", "Change", "the same entities"),
                        new Item("delete", "Delete", "the same entities, with confirmation"),
                        new Item("project", "Switch project", Project != null ? $"now: {Project.Name}" : "none chosen"),
                        new Item("quit", "Quit", "q or Ctrl+C")
                    ]);
                    return choice.Key switch
                    {
                        "browse" => new EnterNext(new KindsFlow(FlowAction.View)),
                        "create" => new EnterNext(new KindsFlow(FlowAction.Create)),
                        "change" => new EnterNext(new KindsFlow(FlowAction.Change)),
                        "delete" => new EnterNext(new KindsFlow(FlowAction.Delete)),
                        "project" => new EnterNext(new SwitchProjectFlow()),
                        _ => new QuitNext()
                    };
                }
                case KindsFlow kinds:
                {
                    var choice = wizard.Select($"{kinds.Action.Label()} what", Kinds.All.Select(k => new Item(k.Key(), k.Label())).ToList());
                    return new EnterNext(new EntityFlow(kinds.Action, Kinds.FromKey(choice.Key) ?? EntityKind.Task));
                }
                case SwitchProjectFlow:
                {
                    var projects = await Context.AllProjects();
                    var choice = wizard.Select("Choose a project", projects.Select(p => new Item(p.Id.ToString("D"), p.Name)).ToList());
                    var project = projects.FirstOrDefault(p => p.Id.ToString("D") == choice.Key) ?? throw new CliException("No such project");
                    return new SwitchProjectNext(project);
                }
                case EntityFlow entity:
                    return new RunNext(await Flows.Run(wizard, entity.Action, entity.Kind));
                case CardFlow card:
                    return new RunNext(["task", "get", card.Id.ToString("D")]);
                case PickFlow:
                {
                    var parent = _frames.AsEnumerable().Reverse().FirstOrDefault(f => f.Flow is EntityFlow)
                                 ?? throw new CliException("Nothing to change");
                    var items = parent.Titles.Zip(parent.Answers)
                        .Select((x, index) => new Item(index.ToString(), x.First, AnswerSummary(x.Second)))
                        .ToList();
                    var choice = wizard.Menu("Which setting to change", items);
                    return new JumpToNext(int.TryParse(choice.Key, out var index) ? index : 0);
                }
                default:
                    throw new ArgumentOutOfRangeException(nameof(flow));
            }
        }
        catch (PendingStep pending)
        {
            return new StepNext(pending.Spec);
        }
        catch (Exception e) when (GlobalOptions.IsCommandError(e))
        {
            return new FailedNext(GlobalOptions.ErrorText(e));
        }
    }

    private async Task<Effect> Enter(Flow flow)
    {
        void Push(StepSpec spec) => _frames.Add(new Frame(flow, spec, StepState.FromSpec(spec)));

        switch (await NextOf(flow, [], false))
        {
            case StepNext step:
                Push(step.Spec);
                return Effect.None;
            case RunNext run:
                Push(new ConfirmSpec(flow.Title, ""));
                return new RunEffect(WithGlobals(run.Argv));
            case FailedNext failed:
                Fail(failed.Text);
                return Effect.None;
            default:
                return Effect.None;
        }
    }

    private void Fail(string text) =>
        Result = new ResultView { Text = text, Kind = ResultKind.Failed, At = Stopwatch.GetTimestamp() };

    private async Task<Effect> Advance(Answer answer)
    {
        if (_frames.Count == 0)
            return Effect.None;
        var frame = _frames[^1];
        var flow = frame.Flow;
        var replay = frame.Replay;
        var answers = new List<Answer>(frame.Answers);
        var titles = new List<string>(frame.Titles);
        var tail = frame.Tail;
        frame.Tail = [];
        frame.Current = null;
        frame.Jumped = false;
        titles.Add(frame.Spec.Title);
        answers.Add(answer);
        while (true)
        {
            var next = await NextOf(flow, answers, false);
            if (next is StepNext { Spec: var spec } && tail.FindIndex(x => x.Title == spec.Title && Fits(spec, x.Answer)) is var at and >= 0)
            {
                tail.RemoveRange(0, at);
                var (title, old) = tail[0];
                tail.RemoveAt(0);
                if (replay)
                {
                    titles.Add(title);
                    answers.Add(old);
                    continue;
                }
                var restored = spec.WithInitial(old);
                frame.Answers = answers;
                frame.Titles = titles;
                frame.Tail = tail;
                frame.State = StepState.FromSpec(restored);
                frame.Spec = restored;
                frame.Current = (title, old);
                return Effect.None;
            }

            frame.Answers = answers;
            frame.Titles = titles;
            frame.Replay = false;
            switch (next)
            {
                case StepNext step:
                    frame.Tail = tail;
                    frame.State = StepState.FromSpec(step.Spec);
                    frame.Spec = step.Spec;
                    return Effect.None;
                case EnterNext enter:
                    return await Enter(enter.Flow);
                case RunNext run:
                    return new RunEffect(WithGlobals(run.Argv));
                case SwitchProjectNext switchTo:
                    Project = switchTo.Project;
                    _frames.RemoveAt(_frames.Count - 1);
                    await RefreshMenu();
                    return Effect.None;
                case JumpToNext jump:
                    _frames.RemoveAt(_frames.Count - 1);
                    await JumpTo(jump.Index);
                    return Effect.None;
                case QuitNext:
                    return Effect.Quit;
                case FailedNext failed:
                    Fail(failed.Text);
                    return Effect.None;
                default:
                    return Effect.None;
            }
        }
    }

    private async Task JumpTo(int index)
    {
        if (_frames.Count == 0)
            return;
        var frame = _frames[^1];
        if (index >= frame.Answers.Count)
            return;
        var rest = frame.Titles.Skip(index).Zip(frame.Answers.Skip(index)).Select(x => (x.First, x.Second)).ToList();
        frame.Answers = frame.Answers.Take(index).ToList();
        frame.Titles = frame.Titles.Take(index).ToList();
        var (title, last) = rest[0];
        rest.RemoveAt(0);
        frame.Tail = rest;
        frame.Replay = true;
        frame.Jumped = true;
        await ShowStep(title, last);
    }

    private void RestoreAfterJump()
    {
        if (_frames.Count == 0)
            return;
        var frame = _frames[^1];
        if (frame.Current is var (title, answer))
        {
            frame.Titles.Add(title);
            frame.Answers.Add(answer);
            frame.Current = null;
        }
        foreach (var (t, a) in frame.Tail)
        {
            frame.Titles.Add(t);
            frame.Answers.Add(a);
        }
        frame.Tail = [];
        frame.Replay = false;
        frame.Jumped = false;
    }

    private async Task ShowStep(string title, Answer last)
    {
        var frame = _frames[^1];
        switch (await NextOf(frame.Flow, frame.Answers, false))
        {
            case StepNext step:
                var spec = step.Spec.WithInitial(last);
                frame.State = StepState.FromSpec(spec);
                frame.Spec = spec;
                frame.Current = (title, last);
                break;
            case FailedNext failed:
                Fail(failed.Text);
                break;
        }
    }

    private async Task RefreshMenu()
    {
        if (_frames.Count == 0)
            return;
        var frame = _frames[^1];
        if (frame.Flow is not MenuFlow)
            return;
        frame.Answers.Clear();
        frame.Titles.Clear();
        frame.Tail.Clear();
        frame.Current = null;
        var cursor = frame.State is ListStep list ? list.Cursor : 0;
        if (await NextOf(new MenuFlow(), [], false) is StepNext step)
        {
            var state = StepState.FromSpec(step.Spec);
            if (state is ListStep fresh)
                fresh.Cursor = cursor;
            frame.State = state;
            frame.Spec = step.Spec;
        }
    }

    private async Task Back()
    {
        if (_frames.Count == 0)
            return;
        var frame = _frames[^1];
        if (frame.Answers.Count == 0)
        {
            if (_frames.Count > 1)
            {
                _frames.RemoveAt(_frames.Count - 1);
                await Back();
            }
            return;
        }
        if (frame.Current is { } known)
        {
            frame.Tail.Insert(0, known);
            frame.Current = null;
        }
        frame.Replay = false;
        frame.Jumped = false;
        var last = frame.Answers[^1];
        frame.Answers.RemoveAt(frame.Answers.Count - 1);
        var title = "";
        if (frame.Titles.Count > 0)
        {
            title = frame.Titles[^1];
            frame.Titles.RemoveAt(frame.Titles.Count - 1);
        }
        await ShowStep(title, last);
    }

    private async Task ReturnToMenu()
    {
        while (_frames.Count > 0 && _frames[^1].Flow is EntityFlow or KindsFlow or PickFlow or CardFlow)
            _frames.RemoveAt(_frames.Count - 1);
        await RefreshMenu();
    }

    private List<string> WithGlobals(List<string> argv)
    {
        var full = new List<string> { "-w", Location.Path };
        if (Project != null)
        {
            full.Add("-p");
            full.Add(Project.Id.ToString("D"));
        }
        full.AddRange(argv);
        return full;
    }

    public Task<List<string>?> RetryArgv() => RerunArgv(true);

    public Task<List<string>?> RefreshArgv() => RerunArgv(false);

    private async Task<List<string>?> RerunArgv(bool fresh)
    {
        if (_frames.Count == 0)
            return null;
        var frame = _frames[^1];
        return await NextOf(frame.Flow, frame.Answers, fresh) is RunNext run ? WithGlobals(run.Argv) : null;
    }

    private static TimeSpan Since(long timestamp) => Stopwatch.GetElapsedTime(timestamp);

    private static TimeSpan Left(TimeSpan total, TimeSpan elapsed) => elapsed >= total ? TimeSpan.Zero : total - elapsed;

    public TimeSpan? RefreshDue()
    {
        if (Result != null)
            return Result.Kind == ResultKind.View ? Left(Refresh, Since(Result.At)) : null;
        return ListRefreshes ? Left(Refresh, Since(_lastRefresh)) : null;
    }

    public bool ListRefreshes => Result == null && _frames.Count > 0 && _frames[^1].Flow is EntityFlow && _frames[^1].State is ListStep;

    public async Task RefreshStep()
    {
        _lastRefresh = Stopwatch.GetTimestamp();
        if (_frames.Count == 0)
            return;
        var frame = _frames[^1];
        if (await NextOf(frame.Flow, frame.Answers, false) is not StepNext { Spec: var fresh } || fresh.Title != frame.Spec.Title)
            return;
        var items = fresh switch
        {
            SelectSpec select => select.Items,
            MultiSelectSpec multi => multi.Items,
            _ => null
        };
        if (items == null || frame.State is not ListStep list || list.Items.SequenceEqual(items))
            return;
        var underCursor = list.Current()?.Key;
        list.Items.Clear();
        list.Items.AddRange(items);
        list.Picked.RemoveAll(k => list.Items.All(i => i.Key != k));
        var visible = list.Visible();
        var position = underCursor == null ? -1 : visible.FindIndex(at => list.Items[at].Key == underCursor);
        list.Cursor = position >= 0 ? position : Math.Min(list.Cursor, Math.Max(0, visible.Count - 1));
        frame.Spec = fresh;
    }

    private bool Viewing => _frames.Count > 0 && _frames[^1].Flow is EntityFlow { Action: FlowAction.View } or CardFlow;

    private Guid? CardTask()
    {
        if (_frames.Count == 0)
            return null;
        var frame = _frames[^1];
        return frame.Flow switch
        {
            CardFlow card => card.Id,
            EntityFlow { Action: FlowAction.View, Kind: EntityKind.Task } when frame.Answers.Count > 0
                                                                               && frame.Answers[0] is ChoiceAnswer { Item.Key: "card" }
                                                                               && frame.Answers[^1] is ChoiceAnswer task =>
                Guid.TryParse(task.Item.Key, out var id) ? id : null,
            _ => null
        };
    }

    private async Task<List<(int Line, Guid Id)>> RelatedTasks(string text)
    {
        if (CardTask() is not { } id || Project == null)
            return [];
        try
        {
            var tasks = Context.Get<TaskService>();
            if (await Context.Get<ITaskStorage>().GetById(Project.Id, id, Context.Ct) is not { } task)
                return [];
            var details = await tasks.Describe(Project.Id, task, Context.Ct);
            var lines = text.Split('\n');
            var header = Array.IndexOf(lines, "links:");
            if (header < 0)
                return [];
            return details.LinkViews
                .Select((view, at) => (Line: header + 1 + at, view.Task.Id))
                .Where(x => x.Line < lines.Length && lines[x.Line].StartsWith("  ", StringComparison.Ordinal)
                                                  && !lines[x.Line].StartsWith("  ...", StringComparison.Ordinal))
                .ToList();
        }
        catch (Exception e) when (GlobalOptions.IsCommandError(e) || e is IOException)
        {
            return [];
        }
    }

    public async Task SetResult(string stdout, string stderr, int code)
    {
        var text = stdout;
        if (stderr.Length > 0)
        {
            if (text.Length > 0 && !text.EndsWith('\n'))
                text += "\n";
            text += stderr;
        }
        text = text.TrimEnd();
        var kind = code == 0 && Viewing
            ? ResultKind.View
            : code == 0
                ? ResultKind.Done
                : stderr.StartsWith("Modified by someone else:", StringComparison.Ordinal) || stderr.StartsWith("Locked:", StringComparison.Ordinal)
                    ? ResultKind.Retryable
                    : ResultKind.Failed;
        var related = kind == ResultKind.View ? await RelatedTasks(text) : [];
        int scroll;
        int? selected;
        var restore = _restore;
        _restore = null;
        if (restore is { } at)
        {
            scroll = 0;
            selected = at < related.Count ? at : null;
        }
        else if (Result is { Kind: ResultKind.View } previous && kind == ResultKind.View)
        {
            scroll = previous.Scroll;
            selected = previous.Selected is { } was && was < related.Count && was < previous.Related.Count && previous.Related[was].Id == related[was].Id
                ? was
                : null;
        }
        else
        {
            scroll = 0;
            selected = null;
        }
        Result = new ResultView { Text = text, Kind = kind, Scroll = scroll, At = Stopwatch.GetTimestamp(), Related = related, Selected = selected };
    }

    public void EditorDone(string? text)
    {
        if (State is not EditorStep editor)
            return;
        if (text != null)
            editor.Text = text;
        else
            editor.Message = "The editor did not finish: the text is unchanged";
    }

    private async Task<Effect> CloseResult()
    {
        if (Result is not { } result)
            return Effect.None;
        Result = null;
        if (_frames.Count > 0 && _frames[^1].Flow is CardFlow)
        {
            _frames.RemoveAt(_frames.Count - 1);
            if (_remembered.Count > 0)
            {
                _restore = _remembered[^1];
                _remembered.RemoveAt(_remembered.Count - 1);
            }
            else
                _restore = null;
            return await RefreshArgv() is { } argv ? new RunEffect(argv) : Effect.None;
        }
        if (result.Kind == ResultKind.Done)
            await ReturnToMenu();
        else
            await Back();
        return Effect.None;
    }

    private void SelectRelated(bool down)
    {
        if (Result is not { } result)
            return;
        var count = result.Related.Count;
        result.Selected = (result.Selected, down) switch
        {
            (null, true) => 0,
            ({ } at, true) => Math.Min(at + 1, count - 1),
            (0 or null, false) => null,
            ({ } at, false) => at - 1
        };
        if (result.Selected is { } selected)
        {
            var line = result.Related[selected].Line;
            if (line < result.Scroll)
                result.Scroll = line;
            else if (line >= result.Scroll + Page)
                result.Scroll = line + 1 - Page;
        }
    }

    public async Task<Effect> Handle(Key key)
    {
        _lastRefresh = Stopwatch.GetTimestamp();
        if (key == Key.CtrlC)
            return Effect.Quit;

        if (Result is { } result)
        {
            switch (key.Kind)
            {
                case KeyKind.Up or KeyKind.Down when result.Related.Count > 0:
                    SelectRelated(key == Key.Down);
                    break;
                case KeyKind.Up:
                    result.Scroll = Math.Max(0, result.Scroll - 1);
                    break;
                case KeyKind.Down:
                    result.Scroll = Math.Min(ushort.MaxValue, result.Scroll + 1);
                    break;
                case KeyKind.Enter when result.Kind == ResultKind.View && result.Selected is { } chosen:
                    _remembered.Add(result.Selected);
                    return await Enter(new CardFlow(result.Related[chosen].Id));
                case KeyKind.PageUp:
                    result.Scroll = Math.Max(0, result.Scroll - Page);
                    break;
                case KeyKind.PageDown:
                    result.Scroll = Math.Min(ushort.MaxValue, result.Scroll + Page);
                    break;
                case KeyKind.Char when key.Char is 'r' or 'R' && result.Kind == ResultKind.Retryable:
                    if (await RetryArgv() is { } retry)
                    {
                        Result = null;
                        return new RunEffect(retry);
                    }
                    break;
                case KeyKind.Char when key.Char is 'r' or 'R' && result.Kind == ResultKind.View:
                case KeyKind.Enter when result.Kind == ResultKind.View:
                    if (await RefreshArgv() is { } refresh)
                        return new RunEffect(refresh);
                    break;
                case KeyKind.Char when key.Char is 'f' or 'F' && result.Kind == ResultKind.View && _frames.Count > 0 && _frames[^1].Answers.Count > 0:
                    Result = null;
                    return await Enter(new PickFlow());
                case KeyKind.Char when key.Char is 'q' or 'Q' && result.Kind == ResultKind.View:
                    Result = null;
                    _remembered.Clear();
                    await ReturnToMenu();
                    break;
                case KeyKind.Enter or KeyKind.Esc:
                case KeyKind.Char when key.Char == 'q':
                    return await CloseResult();
            }
            return Effect.None;
        }

        if (InMenu && key.Kind == KeyKind.Char && key.Char is 'q' or 'Q')
            return Effect.Quit;
        if (key == Key.CtrlF)
        {
            await BackToFilters();
            return Effect.None;
        }
        if (key == Key.CtrlL)
        {
            await BackToColumns();
            return Effect.None;
        }
        if (State is not { } state)
            return Effect.None;

        switch (state.Handle(key, Page))
        {
            case BackOutcome:
                if (_frames.Count > 0 && _frames[^1].Flow is PickFlow)
                {
                    _frames.RemoveAt(_frames.Count - 1);
                    return await RefreshArgv() is { } argv ? new RunEffect(argv) : Effect.None;
                }
                if (_frames.Count > 0 && _frames[^1].Jumped)
                {
                    RestoreAfterJump();
                    return await RefreshArgv() is { } argv ? new RunEffect(argv) : Effect.None;
                }
                await Back();
                return Effect.None;
            case OpenEditorOutcome editor:
                return new OpenEditorEffect(editor.Text);
            case AnswerOutcome answer:
                return await Advance(answer.Answer);
            default:
                return Effect.None;
        }
    }
}
