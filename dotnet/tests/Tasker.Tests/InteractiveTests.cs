using Tasker.Cli;
using Tasker.Cli.Interactive;
using Xunit;

namespace Tasker.Tests;

public class InteractiveStepTests
{
    private static StepState Select(params string[] labels) =>
        StepState.FromSpec(new SelectSpec("Pick", labels.Select(l => new Item(l.ToLowerInvariant(), l)).ToList()));

    [Fact]
    public void Typing_filters_the_list_and_enter_picks_the_item_under_the_cursor()
    {
        var step = Select("Backlog", "Done", "In progress");
        foreach (var c in "pro")
            step.Handle(Key.Of(c), 10);
        Assert.Equal([2], ((ListStep)step).Visible());
        Assert.Equal("In progress", Assert.IsType<ChoiceAnswer>(Assert.IsType<AnswerOutcome>(step.Handle(Key.Enter, 10)).Answer).Item.Label);

        step = Select("Backlog", "Done");
        step.Handle(Key.Down, 10);
        step.Handle(Key.Down, 10);
        step.Handle(Key.Of('z'), 10);
        Assert.IsType<NothingOutcome>(step.Handle(Key.Enter, 10));
        step.Handle(Key.Backspace, 10);
        step.Handle(Key.End, 10);
        Assert.Equal("Done", Assert.IsType<ChoiceAnswer>(Assert.IsType<AnswerOutcome>(step.Handle(Key.Enter, 10)).Answer).Item.Label);
    }

    [Fact]
    public void A_multi_select_keeps_the_order_of_picking_and_may_require_one()
    {
        var step = StepState.FromSpec(new MultiSelectSpec("Statuses", [new Item("a", "A"), new Item("b", "B"), new Item("c", "C")], [], false));
        Assert.IsType<NothingOutcome>(step.Handle(Key.Enter, 10));
        step.Handle(Key.End, 10);
        step.Handle(Key.Of(' '), 10);
        step.Handle(Key.Home, 10);
        step.Handle(Key.Of(' '), 10);
        var answer = Assert.IsType<ChoicesAnswer>(Assert.IsType<AnswerOutcome>(step.Handle(Key.Enter, 10)).Answer);
        Assert.Equal(["c", "a"], answer.Items.Select(i => i.Key));
    }

    [Fact]
    public void Text_input_edits_at_the_cursor_and_checks_the_value_on_enter()
    {
        var step = StepState.FromSpec(new TextSpec("Color", "#80", "", Check.Color, false));
        Assert.IsType<NothingOutcome>(step.Handle(Key.Enter, 10));
        Assert.Equal("'#80' is not a color in #RRGGBB format", ((TextStep)step).Error);
        foreach (var c in "8080")
            step.Handle(Key.Of(c), 10);
        step.Handle(Key.Home, 10);
        step.Handle(Key.Delete, 10);
        step.Handle(Key.Of('#'), 10);
        step.Handle(Key.Right, 10);
        step.Handle(Key.Backspace, 10);
        step.Handle(Key.Of('8'), 10);
        step.Handle(Key.End, 10);
        Assert.Equal("#808080", Assert.IsType<TextAnswer>(Assert.IsType<AnswerOutcome>(step.Handle(Key.Enter, 10)).Answer).Text);
    }

    [Fact]
    public void Checks()
    {
        Assert.Equal("A value is required", Check.Required.Error("  ", false));
        Assert.Null(Check.Required.Error("  ", true));
        Assert.Null(Check.Int.Error("12", false));
        Assert.Equal("'1.5' is not a whole number", Check.Int.Error("1.5", false));
        Assert.Null(Check.Float.Error("1.5", false));
        Assert.Null(Check.Date.Error("2026-10-28", false));
        Assert.Equal("'28.10.2026' is not a date (yyyy-MM-dd)", Check.Date.Error("28.10.2026", false));
        Assert.Null(Check.Prefix.Error("TSK", false));
        Assert.NotNull(Check.Prefix.Error("T-K", false));
    }
}

public class InteractiveAppTests
{
    private sealed record Snapshot(string Crumb, List<string> Items, string Value);

    private sealed class Opened(App app, Tasker.Cli.Session session, string workspace) : IAsyncDisposable
    {
        public App App { get; } = app;
        public string Workspace { get; } = workspace;

        public async ValueTask DisposeAsync()
        {
            await session.DisposeAsync();
            try
            {
                Directory.Delete(Path.GetDirectoryName(Workspace)!, true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static string GoldenCopy()
    {
        string? golden = null;
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "tests", "golden", "workspace");
            if (Directory.Exists(path))
            {
                golden = path;
                break;
            }
        }
        if (golden == null)
            throw new DirectoryNotFoundException("tests/golden/workspace not found above the test binaries");

        var root = Path.Combine(Path.GetTempPath(), "tasker-interactive-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "golden");
        Copy(golden, workspace);
        return new DirectoryInfo(workspace).ResolveLinkTarget(true)?.FullName ?? Path.GetFullPath(workspace);
    }

    private static void Copy(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from))
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        foreach (var directory in Directory.GetDirectories(from))
            Copy(directory, Path.Combine(to, Path.GetFileName(directory)));
    }

    private static async Task<Opened> Open(string workspace, string project = "Golden")
    {
        var settings = new Tasker.Cli.WorkspaceSettings(workspace);
        var session = await Tasker.Cli.Session.Open(settings, CancellationToken.None);
        var context = new Context(session, TextWriter.Null, TextWriter.Null, false, null, CancellationToken.None);
        var found = await context.FindProject(project);
        var app = await App.Create(session, Tasker.Cli.Session.Locate(settings), found, TimeSpan.FromSeconds(10), CancellationToken.None);
        return new Opened(app, session, workspace);
    }

    private static Snapshot Snap(App app)
    {
        var crumb = app.Breadcrumb();
        return app.State switch
        {
            ListStep list => new Snapshot(crumb, list.Items.Select(i => i.Key).ToList(),
                list.Multi ? string.Join(",", list.Picked) : list.Current()?.Key ?? ""),
            TextStep text => new Snapshot(crumb, [], text.Value),
            EditorStep editor => new Snapshot(crumb, [], editor.Text),
            FormStep form => new Snapshot(crumb, form.Sections.Select(s => s.Key).ToList(),
                string.Join(";", form.Sections.Select(s => $"{s.Key}={string.Join("|", s.Values)}"))),
            _ => new Snapshot(crumb, [], "")
        };
    }

    private static string ValidText(Check check) => check switch
    {
        Check.Int => "7",
        Check.Float => "1.5",
        Check.Date => "2026-01-02",
        Check.Color => "#123456",
        Check.Prefix => "WLK",
        _ => "walk"
    };

    private static bool IsBranching(ListStep list) => !list.Multi && list.Items.Count <= 12 && list.Items.All(i => i.Version == null);

    private static async Task Prepare(App app, int choice)
    {
        switch (app.State)
        {
            case ListStep list:
                if (list.Multi)
                {
                    if (list.Picked.Count == 0 && !list.AllowEmpty)
                        await app.Handle(Key.Of(' '));
                }
                else
                    list.Cursor = choice;
                break;
            case TextStep text:
                if (text.Value.Length == 0 && !text.AllowEmpty)
                    foreach (var c in ValidText(text.Check))
                        await app.Handle(Key.Of(c));
                break;
            case FormStep form:
                form.Cursor = form.Sections.Count;
                break;
        }
    }

    private sealed record Walk(List<Snapshot> Trail, List<(string Crumb, int Size)> Branches, bool Done);

    private static async Task<Walk> WalkPath(App app, List<int> path)
    {
        var trail = new List<Snapshot>();
        var branches = new List<(string, int)>();
        for (var i = 0; i < 60; i++)
        {
            if (app.Result != null)
                return new Walk(trail, branches, false);
            if (trail.Count > 0 && app.Breadcrumb() == "What to do")
                return new Walk(trail, branches, true);
            var choice = 0;
            if (app.State is ListStep list && IsBranching(list))
            {
                choice = branches.Count < path.Count ? path[branches.Count] : list.Items.Count - 1;
                branches.Add((app.Breadcrumb(), list.Items.Count));
            }
            await Prepare(app, choice);
            trail.Add(Snap(app));
            if (await app.Handle(Key.Enter) is RunEffect or QuitEffect)
                return new Walk(trail, branches, false);
        }
        throw new Exception($"the walk [{string.Join(",", path)}] did not end in 60 steps: {app.Breadcrumb()}");
    }

    private static async Task BackToMenu(App app, List<Snapshot> forward, List<int> path)
    {
        var expected = new List<Snapshot>(forward);
        var name = string.Join(",", path);
        while (expected.Count > 0)
        {
            var step = expected[^1];
            expected.RemoveAt(expected.Count - 1);
            await app.Handle(Key.Esc);
            Assert.True(app.Result == null, $"[{name}]: a result appeared while going back from {step.Crumb}");
            var now = Snap(app);
            Assert.True(now.Crumb == step.Crumb, $"[{name}]: Esc skipped or repeated a step: {now.Crumb} instead of {step.Crumb}");
            Assert.True(now.Items.SequenceEqual(step.Items), $"[{name}]: the list at {step.Crumb} shows other items after Esc");
            Assert.True(now.Value == step.Value, $"[{name}]: the value at {step.Crumb} was lost after Esc: {now.Value} instead of {step.Value}");
        }
        Assert.Equal("What to do", app.Breadcrumb());
        Assert.True(app.InMenu);
    }

    private static List<string> MenuEntries(App app) => app.State is ListStep list ? list.Items.Select(i => i.Key).ToList() : [];

    [Fact]
    public async Task Every_flow_walks_back_step_by_step_with_the_same_steps_items_and_values()
    {
        await using var opened = await Open(GoldenCopy());
        var app = opened.App;
        var entries = MenuEntries(app);
        var pending = entries.Select((k, i) => (k, i)).Where(x => x.k != "quit").Select(x => new List<int> { x.i }).ToList();
        var walked = 0;
        var explored = new HashSet<string>();
        while (pending.Count > 0)
        {
            var path = pending[^1];
            pending.RemoveAt(pending.Count - 1);
            Assert.Equal("What to do", app.Breadcrumb());
            var walk = await WalkPath(app, path);
            walked++;
            Assert.True(walked < 2000, "too many flows");
            if (walk.Done)
            {
                await app.Handle(Key.Esc);
                Assert.Equal("What to do", app.Breadcrumb());
            }
            else
            {
                await app.Handle(Key.Esc);
                var forward = new List<Snapshot>(walk.Trail);
                var landed = Snap(app);
                var last = forward[^1];
                forward.RemoveAt(forward.Count - 1);
                Assert.True(landed.Crumb == last.Crumb, $"[{string.Join(",", path)}]: after the end Esc must show the last step again");
                Assert.True(landed.Value == last.Value, $"[{string.Join(",", path)}]: the last step lost its value");
                await BackToMenu(app, forward, path);
            }

            for (var at = path.Count; at < walk.Branches.Count; at++)
            {
                var (crumb, size) = walk.Branches[at];
                if (!explored.Add(crumb))
                    continue;
                for (var choice = 0; choice < size - 1; choice++)
                {
                    var next = new List<int>(path);
                    next.AddRange(walk.Branches.Skip(path.Count).Take(at - path.Count).Select(x => x.Size - 1));
                    next.Add(choice);
                    pending.Add(next);
                }
            }
            for (var at = 0; at < Math.Min(path.Count, walk.Branches.Count); at++)
                Assert.True(path[at] < walk.Branches[at].Size, $"[{string.Join(",", path)}]: the branch {at} has only {walk.Branches[at].Size} items");
        }
        Assert.True(walked > 100, $"only {walked} flows were walked");
    }

    private static async Task Execute(App app, Effect effect)
    {
        var argv = Assert.IsType<RunEffect>(effect).Argv;
        var output = new StringWriter();
        var error = new StringWriter();
        var code = await CliApp.Run([.. argv], output, error, TextReader.Null, false);
        await app.SetResult(output.ToString(), error.ToString(), code);
    }

    private static async Task<Effect> Choose(App app, string key)
    {
        var list = Assert.IsType<ListStep>(app.State);
        var at = list.Items.FindIndex(i => i.Key == key);
        Assert.True(at >= 0, $"no item {key} in [{string.Join(", ", list.Items.Select(i => i.Key))}]");
        list.Cursor = at;
        return await app.Handle(Key.Enter);
    }

    private static async Task TypeIn(App app, string text)
    {
        foreach (var c in text)
            await app.Handle(Key.Of(c));
    }

    private static async Task<List<string>> CrumbsBackToMenu(App app)
    {
        var seen = new List<string>();
        for (var i = 0; i < 20; i++)
        {
            await app.Handle(Key.Esc);
            seen.Add(app.Breadcrumb());
            if (app.InMenu && app.Breadcrumb() == "What to do")
                return seen;
        }
        throw new Exception($"Esc never reached the menu: {string.Join(" | ", seen)}");
    }

    private static Task<int> RunCli(params string[] args) => CliApp.Run(args, new StringWriter(), new StringWriter(), TextReader.Null, false);

    [Fact]
    public async Task After_a_failed_command_esc_walks_back_through_each_step_once()
    {
        await using var opened = await Open(GoldenCopy());
        var app = opened.App;
        await Choose(app, "create");
        await Choose(app, "series");
        Assert.Equal("Create › Series › Series name", app.Breadcrumb());
        await TypeIn(app, "Second golden");
        await app.Handle(Key.Enter);
        Assert.Equal("Create › Series › Prefix (Latin letters and digits, e.g. TSK)", app.Breadcrumb());
        await TypeIn(app, "GLD");
        await Execute(app, await app.Handle(Key.Enter));
        Assert.Equal(ResultKind.Failed, app.Result?.Kind);
        await app.Handle(Key.Esc);
        Assert.Equal("Create › Series › Prefix (Latin letters and digits, e.g. TSK)", app.Breadcrumb());
        Assert.Equal("GLD", Snap(app).Value);
        Assert.Equal(["Create › Series › Series name", "Create › Create what", "What to do"], await CrumbsBackToMenu(app));
        Assert.Equal("create", Snap(app).Value);
    }

    [Fact]
    public async Task A_flow_that_cannot_start_returns_to_the_kind_list_once_not_twice()
    {
        var workspace = GoldenCopy();
        Assert.Equal(0, await RunCli("-w", workspace, "project", "create", "Empty"));
        await using var opened = await Open(workspace, "Empty");
        var app = opened.App;
        await Choose(app, "change");
        await Choose(app, "status");
        Assert.Equal(ResultKind.Failed, app.Result?.Kind);
        Assert.Contains("Nothing to choose from", app.Result!.Text);
        await app.Handle(Key.Esc);
        Assert.Equal("Change › Change what", app.Breadcrumb());
        Assert.Equal("status", Snap(app).Value);
        Assert.Equal(["What to do"], await CrumbsBackToMenu(app));
    }

    private static FormStep Form(App app) => Assert.IsType<FormStep>(app.State);

    private static async Task OpenSection(App app, string key)
    {
        var form = Form(app);
        form.Cursor = form.Sections.FindIndex(s => s.Key == key);
        await app.Handle(Key.Enter);
    }

    private static (string Title, List<string> Items) PopupOf(App app)
    {
        var layer = Form(app).Popup?.Top ?? throw new Exception("no open popup");
        return (layer.Title, layer.Inner switch
        {
            ListStep list => list.Items.Select(i => i.Key).ToList(),
            TextStep text => [text.Value],
            _ => []
        });
    }

    private static async Task PickInPopup(App app, string key)
    {
        var list = Assert.IsType<ListStep>(Form(app).Popup?.Top.Inner);
        var at = list.Items.FindIndex(i => i.Key == key);
        Assert.True(at >= 0, $"no item {key} in [{string.Join(", ", list.Items.Select(i => i.Key))}]");
        list.Cursor = at;
        await app.Handle(Key.Enter);
    }

    private static List<string> SectionValues(App app, string key) => Form(app).Sections.First(s => s.Key == key).Values;

    [Fact]
    public async Task The_list_settings_form_edits_each_section_in_a_popup_and_esc_in_a_popup_only_closes_it()
    {
        await using var opened = await Open(GoldenCopy());
        var app = opened.App;
        await Choose(app, "browse");
        await Choose(app, "task");
        await Choose(app, "list");
        Assert.Equal("Browse › Task › List settings", app.Breadcrumb());
        Assert.Equal(["type", "status", "series", "field", "sort", "shape"], Snap(app).Items);
        Assert.Equal("type=;status=;series=;field=;sort=;shape=tree", Snap(app).Value);

        await OpenSection(app, "status");
        var (title, items) = PopupOf(app);
        Assert.Equal("Statuses", title);
        Assert.True(items.Count >= 4);
        await app.Handle(Key.Of(' '));
        await app.Handle(Key.Esc);
        Assert.Null(Form(app).Popup);
        Assert.Empty(SectionValues(app, "status"));
        await OpenSection(app, "status");
        await app.Handle(Key.Of(' '));
        await app.Handle(Key.Enter);
        Assert.Single(SectionValues(app, "status"));

        await OpenSection(app, "field");
        (var fieldTitle, var fieldItems) = PopupOf(app);
        Assert.Equal("Field conditions", fieldTitle);
        Assert.Equal(["add", "done"], fieldItems);
        await PickInPopup(app, "add");
        (title, items) = PopupOf(app);
        Assert.Equal("Field", title);
        Assert.Contains("Priority", items);
        Assert.Contains("Estimate", items);
        await PickInPopup(app, "Priority");
        (title, items) = PopupOf(app);
        Assert.Equal("Comparison for Priority", title);
        Assert.Equal(["=", "!=", ":set", ":unset", ":attached", ":detached"], items);
        await PickInPopup(app, "=");
        (title, items) = PopupOf(app);
        Assert.Equal("Value of Priority", title);
        Assert.Contains("High", items);
        await app.Handle(Key.Esc);
        Assert.Equal("Comparison for Priority", PopupOf(app).Title);
        await PickInPopup(app, "!=");
        await PickInPopup(app, "High");
        Assert.Equal(["drop0", "add", "done"], PopupOf(app).Items);
        Assert.Equal(["Priority!=High"], SectionValues(app, "field"));
        await PickInPopup(app, "add");
        await PickInPopup(app, "Estimate");
        Assert.Contains(">", PopupOf(app).Items);
        await PickInPopup(app, ">");
        (title, items) = PopupOf(app);
        Assert.Equal("Value of Estimate", title);
        Assert.Equal([""], items);
        await TypeIn(app, "x");
        await app.Handle(Key.Enter);
        Assert.Equal("Value of Estimate", PopupOf(app).Title);
        await app.Handle(Key.Backspace);
        await TypeIn(app, "3");
        await app.Handle(Key.Enter);
        Assert.Equal(["Priority!=High", "Estimate>3"], SectionValues(app, "field"));
        await PickInPopup(app, "drop0");
        Assert.Equal(["Estimate>3"], SectionValues(app, "field"));
        await PickInPopup(app, "done");
        Assert.Null(Form(app).Popup);

        await OpenSection(app, "sort");
        await PickInPopup(app, "add");
        await PickInPopup(app, "created");
        (title, items) = PopupOf(app);
        Assert.Equal("Direction for created", title);
        Assert.Equal(["", "-"], items);
        await PickInPopup(app, "-");
        await PickInPopup(app, "add");
        await PickInPopup(app, "title");
        await PickInPopup(app, "");
        Assert.Equal(["-created", "title"], SectionValues(app, "sort"));
        await app.Handle(Key.Esc);
        Assert.Null(Form(app).Popup);

        await OpenSection(app, "shape");
        await PickInPopup(app, "flat");
        Assert.Equal(["flat"], SectionValues(app, "shape"));
        Form(app).Cursor = 5;
        await app.Handle(Key.Backspace);
        Assert.Empty(SectionValues(app, "shape"));
        await OpenSection(app, "shape");
        await PickInPopup(app, "flat");

        await app.Handle(Key.End);
        var effect = await app.Handle(Key.Enter);
        var argv = Assert.IsType<RunEffect>(effect).Argv;
        var tail = argv.SkipWhile(a => a != "task").ToList();
        Assert.Equal(["task", "list", "--all", "--status", tail[4]], tail.Take(5));
        Assert.Equal(["--field", "Estimate>3", "--flat", "--sort", "-created,title"], tail.Skip(5));
        await Execute(app, effect);
        Assert.Equal(ResultKind.View, app.Result?.Kind);
    }

    private static void PointAt(App app, string label)
    {
        var list = Assert.IsType<ListStep>(Form(app).Popup?.Top.Inner);
        list.Cursor = list.Items.FindIndex(i => i.Label == label);
        Assert.True(list.Cursor >= 0, $"no {label}");
    }

    private static List<string> Cells(string label) => label.Split("  ").Where(c => c.Length > 0).Select(c => c.Trim()).ToList();

    [Fact]
    public async Task The_card_of_a_task_is_chosen_from_a_list_narrowed_by_the_same_filters()
    {
        await using var opened = await Open(GoldenCopy());
        var app = opened.App;
        await Choose(app, "browse");
        await Choose(app, "task");
        await Choose(app, "card");
        Assert.Equal("Browse › Task › Task filters", app.Breadcrumb());
        Assert.Equal(["type", "status", "series", "field", "sort", "columns", "shape"], Snap(app).Items);
        await OpenSection(app, "status");
        PointAt(app, "In Progress");
        await app.Handle(Key.Of(' '));
        await app.Handle(Key.Enter);
        await OpenSection(app, "field");
        await PickInPopup(app, "add");
        await PickInPopup(app, "Priority");
        await PickInPopup(app, "=");
        await PickInPopup(app, "High");
        await PickInPopup(app, "done");
        await app.Handle(Key.End);
        await app.Handle(Key.Enter);
        Assert.True(app.Breadcrumb() == "Browse › Task › Task", app.Result?.Text);
        var list = Assert.IsType<ListStep>(app.State);
        Assert.Equal(["BUG-1  In Progress  Bug  Bug with all fields (renamed)"], list.Items.Select(i => i.Label));
        Assert.True(app.HasFilters);
        await app.Handle(Key.CtrlF);
        Assert.Equal("Browse › Task › Task filters", app.Breadcrumb());
        Assert.Contains("field=Priority=High", Snap(app).Value);
        Assert.False(app.HasFilters);
        await app.Handle(Key.CtrlF);
        Assert.Equal("Browse › Task › Task filters", app.Breadcrumb());
        await OpenSection(app, "field");
        await PickInPopup(app, "drop0");
        await PickInPopup(app, "done");
        await app.Handle(Key.End);
        await app.Handle(Key.Enter);
        Assert.Equal("Browse › Task › Task", app.Breadcrumb());
        list = Assert.IsType<ListStep>(app.State);
        Assert.All(list.Items, i => Assert.Contains("  In Progress  ", i.Label));
        Assert.True(app.ListRefreshes);
        var before = list.Items.Count;
        Assert.Equal(0, await RunCli("-w", opened.Workspace, "-p", "Golden", "task", "create", "Fresh in the list", "--type", "Bug",
            "--field", "Priority=High", "--status", "In Progress"));
        await app.RefreshStep();
        list = Assert.IsType<ListStep>(app.State);
        Assert.Equal(before + 1, list.Items.Count);
        Assert.Contains(list.Items, i => i.Label.EndsWith("Fresh in the list", StringComparison.Ordinal));
        Assert.Contains("Bug with all fields", list.Current()?.Label);
        Assert.True(app.HasColumns);
        await app.Handle(Key.CtrlL);
        Assert.Equal("Browse › Task › Task filters", app.Breadcrumb());
        var (title, items) = PopupOf(app);
        Assert.Equal("Extra columns", title);
        Assert.Contains("Priority", items);
        Assert.Contains("Estimate", items);
        foreach (var label in new[] { "Priority", "Estimate" })
        {
            PointAt(app, label);
            await app.Handle(Key.Of(' '));
        }
        await app.Handle(Key.Enter);
        Assert.Equal(["Priority", "Estimate"], SectionValues(app, "columns"));
        await app.Handle(Key.End);
        await app.Handle(Key.Enter);
        Assert.Equal("Browse › Task › Task", app.Breadcrumb());
        list = Assert.IsType<ListStep>(app.State);
        var bug = list.Items.First(i => i.Label.Contains("Bug with all fields"));
        Assert.Equal(["BUG-1", "In Progress", "Bug", "High", "7"], Cells(bug.Label).Take(5));
        var fresh = list.Items.First(i => i.Label.EndsWith("Fresh in the list", StringComparison.Ordinal));
        Assert.Equal(["In Progress", "Bug", "High", "-"], Cells(fresh.Label).Skip(1).Take(4));
        Assert.DoesNotContain("Priority=High", Snap(app).Value);
        var first = list.Items[0].Key;
        var effect = await Choose(app, first);
        var argv = Assert.IsType<RunEffect>(effect).Argv;
        Assert.Contains("get", argv);
        Assert.Contains(first, argv);
        await Execute(app, effect);
        Assert.Equal(ResultKind.View, app.Result?.Kind);
        await app.Handle(Key.Esc);
        Assert.Equal("Browse › Task › Task", app.Breadcrumb());
        await app.Handle(Key.Esc);
        Assert.Equal("Browse › Task › Task filters", app.Breadcrumb());
        Assert.Contains("status=0000", Snap(app).Value);
        Assert.Contains("field=;", Snap(app).Value);
    }

    [Fact]
    public async Task The_task_picker_is_a_tree_and_the_card_opens_linked_tasks_with_arrows_and_returns_on_esc()
    {
        await using var opened = await Open(GoldenCopy());
        var app = opened.App;
        await Choose(app, "browse");
        await Choose(app, "task");
        await Choose(app, "card");
        Assert.Equal(["type", "status", "series", "field", "sort", "columns", "shape"], Snap(app).Items);
        await app.Handle(Key.End);
        await app.Handle(Key.Enter);
        Assert.Equal("Browse › Task › Task", app.Breadcrumb());
        var list = Assert.IsType<ListStep>(app.State);
        var labels = list.Items.Select(i => i.Label).ToList();
        var parent = labels.FindIndex(l => l.Contains("Epic: hierarchy"));
        Assert.True(parent >= 0);
        Assert.StartsWith("GLD-54", labels[parent]);
        var subtree = labels.Skip(parent + 1).Take(3).ToList();
        Assert.True(subtree.Any(l => l.StartsWith("    GLD-5", StringComparison.Ordinal) && l.Contains("Child one"))
                    && subtree.Any(l => l.StartsWith("    GLD-5", StringComparison.Ordinal) && l.Contains("Child two"))
                    && subtree.Any(l => l.StartsWith("        GLD-57", StringComparison.Ordinal) && l.Contains("Grandchild")),
            string.Join(" | ", subtree));
        Assert.Contains(labels, l => l.Contains("(+)"));
        var bug = list.Items.First(i => i.Label.Contains("Bug with all fields")).Key;

        await Execute(app, await Choose(app, bug));
        var result = app.Result!;
        Assert.Equal(ResultKind.View, result.Kind);
        Assert.True(result.Related.Count == 4, result.Text);
        Assert.Null(result.Selected);
        var lines = result.Text.Split('\n');
        Assert.StartsWith("  blocks", lines[result.Related[0].Line]);
        Assert.Contains("Bug with own fields", lines[result.Related[0].Line]);
        Assert.IsType<RunEffect>(await app.Handle(Key.Enter));
        await app.Handle(Key.Down);
        await app.Handle(Key.Down);
        Assert.Equal(1, app.Result!.Selected);
        await app.Handle(Key.Up);
        Assert.Equal(0, app.Result!.Selected);
        var effect = await app.Handle(Key.Enter);
        var argv = Assert.IsType<RunEffect>(effect).Argv;
        Assert.Equal(["task", "get", app.Result!.Related[0].Id.ToString("D")], argv.TakeLast(3));
        await Execute(app, effect);
        Assert.Equal("Browse › Task › Task › Linked task", app.Breadcrumb());
        result = app.Result!;
        Assert.Equal(ResultKind.View, result.Kind);
        Assert.Contains("title:        Bug with own fields", result.Text);
        Assert.NotEmpty(result.Related);
        await app.Handle(Key.Down);
        await Execute(app, await app.Handle(Key.Enter));
        Assert.Equal("Browse › Task › Task › Linked task › Linked task", app.Breadcrumb());

        effect = await app.Handle(Key.Esc);
        Assert.IsType<RunEffect>(effect);
        await Execute(app, effect);
        Assert.Equal("Browse › Task › Task › Linked task", app.Breadcrumb());
        Assert.Contains("title:        Bug with own fields", app.Result!.Text);
        await Execute(app, await app.Handle(Key.Esc));
        Assert.Equal("Browse › Task › Task", app.Breadcrumb());
        Assert.Contains("title:        Bug with all fields", app.Result!.Text);
        Assert.Equal(0, app.Result!.Selected);
        Assert.IsType<NoEffect>(await app.Handle(Key.Esc));
        Assert.Equal("Browse › Task › Task", app.Breadcrumb());
        Assert.Null(app.Result);
    }

    [Fact]
    public async Task Esc_from_a_view_shows_the_settings_again_with_their_values_and_f_reruns_with_one_change()
    {
        await using var opened = await Open(GoldenCopy());
        var app = opened.App;
        await Choose(app, "browse");
        await Choose(app, "task");
        await Choose(app, "list");
        Assert.Equal("Browse › Task › List settings", app.Breadcrumb());
        await OpenSection(app, "sort");
        await PickInPopup(app, "add");
        await PickInPopup(app, "created");
        await PickInPopup(app, "-");
        await PickInPopup(app, "done");
        await OpenSection(app, "shape");
        await PickInPopup(app, "flat");
        await app.Handle(Key.End);
        var effect = await app.Handle(Key.Enter);
        var argv = Assert.IsType<RunEffect>(effect).Argv;
        Assert.Contains(argv.Zip(argv.Skip(1)), x => x.First == "--sort" && x.Second == "-created");
        await Execute(app, effect);
        Assert.Equal(ResultKind.View, app.Result?.Kind);

        await app.Handle(Key.Esc);
        Assert.Equal("Browse › Task › List settings", app.Breadcrumb());
        Assert.Equal("type=;status=;series=;field=;sort=-created;shape=flat", Snap(app).Value);
        Assert.Null(Form(app).Popup);
        await OpenSection(app, "field");
        await PickInPopup(app, "add");
        await PickInPopup(app, "Priority");
        await PickInPopup(app, "=");
        await PickInPopup(app, "High");
        await PickInPopup(app, "done");
        await app.Handle(Key.End);
        effect = await app.Handle(Key.Enter);
        argv = Assert.IsType<RunEffect>(effect).Argv;
        Assert.Contains(argv.Zip(argv.Skip(1)), x => x.First == "--field" && x.Second == "Priority=High");
        Assert.Contains("--flat", argv);
        await Execute(app, effect);

        await app.Handle(Key.Of('f'));
        Assert.Equal("Browse › Task › Which setting to change", app.Breadcrumb());
        var list = Assert.IsType<ListStep>(app.State);
        Assert.Equal(
            ["Tasks: what to show: List with filters", "List settings: Field conditions: Priority=High; Sort: -created; Shape: Flat"],
            list.Items.Select(i => $"{i.Label}: {i.Detail}"));
        await Choose(app, "1");
        Assert.Equal("Browse › Task › List settings", app.Breadcrumb());
        Assert.Equal("type=;status=;series=;field=Priority=High;sort=-created;shape=flat", Snap(app).Value);
        await OpenSection(app, "shape");
        await PickInPopup(app, "tree");
        await app.Handle(Key.End);
        effect = await app.Handle(Key.Enter);
        argv = Assert.IsType<RunEffect>(effect).Argv;
        Assert.DoesNotContain("--flat", argv);
        Assert.Contains("Priority=High", argv);
        await Execute(app, effect);

        await app.Handle(Key.Esc);
        Assert.Equal("Browse › Task › List settings", app.Breadcrumb());
        Assert.Equal(["Browse › Task › Tasks: what to show", "Browse › Browse what", "What to do"], await CrumbsBackToMenu(app));
    }
}
