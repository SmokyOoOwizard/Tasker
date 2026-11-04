using Tasker.Core.Projects;

namespace Tasker.Cli.Interactive;

internal sealed class PendingStep(StepSpec spec) : Exception(spec.Title)
{
    public StepSpec Spec { get; } = spec;
}

internal sealed class FlowContext(Context session, Project? project, bool fresh)
{
    public Context Session { get; } = session;
    public Project? Project { get; } = project;
    public bool Fresh { get; } = fresh;

    public async Task<Guid> ProjectId() => Project?.Id ?? await Session.ProjectId();
}

internal sealed class Wizard(FlowContext ctx, IReadOnlyList<Answer> answers)
{
    private int _at;

    public FlowContext Ctx { get; } = ctx;
    public Context Session => Ctx.Session;
    public CancellationToken Ct => Ctx.Session.Ct;

    private Answer Take(StepSpec spec)
    {
        if (_at >= answers.Count)
            throw new PendingStep(spec);
        return answers[_at++];
    }

    private static CliException Unexpected() => new("Unexpected answer");

    public Item Select(string title, List<Item> items)
    {
        if (items.Count == 0)
            throw new CliException($"Nothing to choose from: {title.ToLowerInvariant()}");
        return Take(new SelectSpec(title, items)) is ChoiceAnswer choice ? choice.Item : throw Unexpected();
    }

    public Item Menu(string title, List<Item> items) => MenuWith(title, items, true);

    public Item PlainMenu(string title, List<Item> items) => MenuWith(title, items, false);

    private Item MenuWith(string title, List<Item> items, bool filterable) =>
        Take(new SelectSpec(title, items, null, filterable)) is ChoiceAnswer choice ? choice.Item : throw Unexpected();

    public List<Item> Multi(string title, List<Item> items, bool allowEmpty)
    {
        if (items.Count == 0 && !allowEmpty)
            throw new CliException($"Nothing to choose from: {title.ToLowerInvariant()}");
        return Take(new MultiSelectSpec(title, items, [], allowEmpty)) is ChoicesAnswer choices ? choices.Items : throw Unexpected();
    }

    public string Text(string title, string initial, Check check) => TextWith(title, initial, check, false, "");

    public string OptionalText(string title, string initial, Check check) =>
        TextWith(title, initial, check, true, "Enter with an empty value skips it");

    public string TextWith(string title, string initial, Check check, bool allowEmpty, string hint) =>
        Take(new TextSpec(title, initial, hint, check, allowEmpty)) is TextAnswer text ? text.Text : throw Unexpected();

    public string Editor(string title, string initial, string hint) =>
        Take(new EditorSpec(title, initial, hint)) is TextAnswer text ? text.Text : throw Unexpected();

    public List<FormValue> Form(string title, List<Section> sections, string submit) =>
        Take(new FormSpec(title, sections, submit)) is FormAnswer form ? form.Values : throw Unexpected();

    public void Confirm(string title, string text) => Take(new ConfirmSpec(title, text));

    public bool YesNo(string title) => Menu(title, [new Item("yes", "Yes"), new Item("no", "No")]).Key == "yes";

    public List<string> ExpectedVersion(Item item) =>
        item.Version is { } version && !Ctx.Fresh ? ["--expected-version", version] : [];

    public Task<Guid> ProjectId() => Ctx.ProjectId();
}
