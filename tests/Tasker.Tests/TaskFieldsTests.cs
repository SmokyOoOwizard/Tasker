using Tasker.Core.Boards;
using Autofac;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tasker.Core;
using Tasker.Core.Fields;
using Tasker.Core.Links;
using Tasker.Core.Locks;
using Tasker.Core.Projects;
using Tasker.Core.Statuses;
using Tasker.Core.TaskSeries;
using Tasker.Core.Tasks;
using Tasker.Storage.Db;
using Tasker.Storage.Db.Configs.Tasker;
using Tasker.Storage.Files;
using Tasker.Tests.SeriesCore;
using Xunit;

namespace Tasker.Tests;

/// <summary>
/// Поля у типов задач и значения у задач (сервисы Core поверх настоящих хранилищ) — одно поведение для файлов и БД.
/// Каждый вызов <see cref="Run"/> получает свой скоуп (в БД — свой запрос и контекст), как отдельный запрос сервера.
/// </summary>
public abstract class TaskFieldsContract : IAsyncLifetime
{
    public abstract Task InitializeAsync();
    public abstract Task DisposeAsync();

    protected abstract Task<T> Run<T>(Func<ILifetimeScope, Task<T>> action);

    private readonly FakeEditor _editor = new();

    /// <summary>Сколько массовая правка ждёт чужую блокировку задачи; тесты, которым нужно дождаться снятия, увеличивают.</summary>
    private TimeSpan _wait = TimeSpan.FromMilliseconds(300);

    private static readonly FakeEditor AnnaEditor = new() { Holder = FakeEditor.Anna };

    /// <summary>Блокировка задачи другим держателем (Анной) и её снятие.</summary>
    private Task<int> AnnaLocks(Ctx ctx, TaskItem task, bool release = false) => Run(async scope =>
    {
        var locks = new EditLockService(scope.Resolve<IEditLockStorage>(), AnnaEditor, TimeProvider.System);
        if (release)
            await locks.Release(LockedEntity.Task, task.Id);
        else
            await locks.Acquire(LockedEntity.Task, task.Id, "Task", ctx.Project);
        return 0;
    });

    private sealed record Svc(
        TaskService Tasks, TaskTypeService Types, FieldService Fields, FieldEnumService Enums, ITaskStorage TaskStorage, ITaskTypeStorage TypeStorage);

    private Task<T> With<T>(Func<Svc, Task<T>> action) => Run(scope =>
    {
        var locks = new EditLockService(scope.Resolve<IEditLockStorage>(), _editor, TimeProvider.System) { CascadeTimeout = _wait };
        var fields = scope.Resolve<IFieldStorage>();
        var enums = scope.Resolve<IFieldEnumStorage>();
        var writes = scope.Resolve<IWriteScope>();
        var tasks = scope.Resolve<ITaskStorage>();
        var types = scope.Resolve<ITaskTypeStorage>();
        var sets = scope.Resolve<IStatusSetStorage>();
        var linkTypes = new LinkTypeService(scope.Resolve<ILinkTypeStorage>(), tasks, locks);
        var links = new TaskLinkService(tasks, linkTypes, TimeProvider.System, locks, scope.Resolve<ISeriesStorage>(), writes);
        return action(new Svc(
            new TaskService(tasks, types, sets, TimeProvider.System, scope.Resolve<ISeriesStorage>(), writes, locks, fields, enums, links, scope.Resolve<IStatusStorage>()),
            new TaskTypeService(types, sets, tasks, fields, writes, TimeProvider.System, locks),
            new FieldService(fields, enums, types, tasks, scope.Resolve<IBoardStorage>(), writes, TimeProvider.System, locks),
            new FieldEnumService(enums, fields, types, tasks, scope.Resolve<IBoardStorage>(), writes, TimeProvider.System, locks),
            tasks, types));
    });

    /// <summary>Проект с двумя статусами и набором из них.</summary>
    private sealed record Ctx(Guid Project, Guid SetId, Guid Todo, Guid Done);

    private async Task<Ctx> NewProject()
    {
        var ctx = new Ctx(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await Run(async scope =>
        {
            await scope.Resolve<IProjectStorage>().Add(new Project { Id = ctx.Project, Name = "P" + ctx.Project.ToString("N")[..6], CreatedAt = DateTimeOffset.UtcNow, Version = "" });
            var statuses = scope.Resolve<IStatusStorage>();
            await statuses.Add(new Status { Id = ctx.Todo, ProjectId = ctx.Project, Name = "Todo", Color = "#111111", Version = "" });
            await statuses.Add(new Status { Id = ctx.Done, ProjectId = ctx.Project, Name = "Done", Color = "#222222", Version = "" });
            await scope.Resolve<IStatusSetStorage>().Add(new StatusSet { Id = ctx.SetId, ProjectId = ctx.Project, Name = "Flow", StatusIds = [ctx.Todo, ctx.Done], Version = "" });
            return 0;
        });
        return ctx;
    }

    private Task<TaskType> NewType(Ctx ctx, string name, params TaskTypeField[] fields) =>
        With(s => s.Types.Create(ctx.Project, new CreateTaskType(name, ctx.SetId, fields)));

    private Task<FieldDefinition> NewField(Ctx ctx, string name, FieldType type = FieldType.String, bool multiple = false, Guid? enumId = null) =>
        With(s => s.Fields.Create(ctx.Project, new CreateField(name, type, multiple, enumId)));

    private Task<FieldEnum> NewEnum(Ctx ctx, string name = "Priority", params string[] values) =>
        With(s => s.Enums.Create(ctx.Project, new CreateFieldEnum(name, values.Length > 0 ? values : ["Low", "Medium", "High"])));

    private Task<TaskItem> NewTask(Ctx ctx, TaskType type, string title = "Task", TaskFieldChanges? fields = null) =>
        With(s => s.Tasks.Create(ctx.Project, new CreateTask(title, null, type.Id, null, null, fields)));

    private Task<TaskItem?> Edit(Ctx ctx, TaskItem task, TaskFieldChanges? fields = null, string? title = null, Guid? typeId = null, Guid? statusId = null) =>
        With(s => s.Tasks.Update(ctx.Project, task.Id, new UpdateTask(title, null, typeId, statusId, task.Version, fields)));

    private async Task<TaskItem> Reload(Ctx ctx, TaskItem task) =>
        (await With(s => s.TaskStorage.GetById(ctx.Project, task.Id)))!;

    private static TaskFieldChanges Set(FieldDefinition field, params string[] values) =>
        new() { Values = [new TaskFieldValueInput(field.Id, values)] };

    private static TaskFieldChanges Set(params (FieldDefinition Field, string[] Values)[] values) =>
        new() { Values = values.Select(x => new TaskFieldValueInput(x.Field.Id, x.Values)).ToArray() };

    private static string[] Values(TaskFieldView[] view, string name) => view.Single(x => x.Name == name).Values.ToArray();

    private static async Task<TaskerConflictException> Conflict(Task task) => await Assert.ThrowsAnyAsync<TaskerConflictException>(() => task);
    private static async Task<TaskerValidationException> Invalid(Task task) => await Assert.ThrowsAnyAsync<TaskerValidationException>(() => task);

    // ---- поля типа ----

    [Fact]
    public async Task A_task_type_keeps_its_fields_in_order_with_the_required_flag_and_they_are_read_back()
    {
        var ctx = await NewProject();
        var estimate = await NewField(ctx, "Estimate", FieldType.Int);
        var note = await NewField(ctx, "Note");

        var type = await NewType(ctx, "Bug", new TaskTypeField(note.Id, false), new TaskTypeField(estimate.Id, true));
        Assert.Equal([note.Id, estimate.Id], type.Fields.Select(x => x.FieldId).ToArray());

        var read = (await With(s => s.TypeStorage.GetById(ctx.Project, type.Id)))!;
        Assert.Equal([new TaskTypeField(note.Id, false), new TaskTypeField(estimate.Id, true)], read.Fields.ToArray());
        Assert.Equal(type.Version, read.Version);
        Assert.Equal(2, (await With(s => s.TypeStorage.GetAll(ctx.Project))).Single().Fields.Count);
        Assert.Equal(2, (await With(s => s.TypeStorage.GetRange(ctx.Project, new Tasker.Core.Dto.Page(0, 10)))).Data.Single().Fields.Count);

        // Список заменяется целиком, порядок — как передан; без списка поля не трогаются.
        var updated = (await With(s => s.Types.Update(ctx.Project, type.Id,
            new UpdateTaskType("Bug2", null, type.Version, [new TaskTypeField(estimate.Id, false), new TaskTypeField(note.Id, true)]))))!.Value;
        Assert.Equal([new TaskTypeField(estimate.Id, false), new TaskTypeField(note.Id, true)], updated.Fields.ToArray());
        var renamed = (await With(s => s.Types.Update(ctx.Project, type.Id, new UpdateTaskType("Bug3", null, updated.Version))))!.Value;
        Assert.Equal(updated.Fields.ToArray(), renamed.Fields.ToArray());
        Assert.Equal(renamed.Fields.ToArray(), (await With(s => s.TypeStorage.GetById(ctx.Project, type.Id)))!.Fields.ToArray());
    }

    [Fact]
    public async Task Type_fields_must_exist_in_the_catalog_and_not_repeat()
    {
        var ctx = await NewProject();
        var field = await NewField(ctx, "Note");
        var other = await NewProject();
        var foreign = await NewField(other, "Foreign");

        Assert.Contains("not found in the project", (await Invalid(NewType(ctx, "A", new TaskTypeField(Guid.NewGuid(), false)))).Message);
        Assert.Contains("not found in the project", (await Invalid(NewType(ctx, "A", new TaskTypeField(foreign.Id, false)))).Message);
        Assert.Contains("duplicates", (await Invalid(NewType(ctx, "A", new TaskTypeField(field.Id, false), new TaskTypeField(field.Id, true)))).Message);
        Assert.Empty(await With(s => s.TypeStorage.GetAll(ctx.Project)));
    }

    // ---- значения ----

    [Fact]
    public async Task Values_are_checked_by_type_stored_in_canonical_form_and_shown_with_enum_names()
    {
        var ctx = await NewProject();
        var priority = await NewEnum(ctx);
        var number = await NewField(ctx, "Number", FieldType.Int);
        var ratio = await NewField(ctx, "Ratio", FieldType.Float);
        var flag = await NewField(ctx, "Flag", FieldType.Bool);
        var due = await NewField(ctx, "Due", FieldType.Date);
        var level = await NewField(ctx, "Level", FieldType.Enum, enumId: priority.Id);
        var note = await NewField(ctx, "Note");
        var type = await NewType(ctx, "Bug", new[] { number, ratio, flag, due, level, note }.Select(x => new TaskTypeField(x.Id, false)).ToArray());

        var task = await NewTask(ctx, type, fields: Set(
            (number, ["007"]), (ratio, [" 2.50 "]), (flag, ["TRUE"]), (due, ["2026-10-02"]), (level, ["high"]), (note, ["  text  "])));

        var read = await Reload(ctx, task);
        var view = await With(s => s.Tasks.GetFields(ctx.Project, read.Id)) ?? [];
        Assert.Equal(["7"], Values(view, "Number"));
        Assert.Equal(["2.5"], Values(view, "Ratio"));
        Assert.Equal(["true"], Values(view, "Flag"));
        Assert.Equal(["2026-10-02"], Values(view, "Due"));
        Assert.Equal([priority.Values[2].Id.ToString("D")], Values(view, "Level"));
        Assert.Equal(["High"], view.Single(x => x.Name == "Level").Texts.ToArray());
        Assert.Equal(["text"], Values(view, "Note"));
        Assert.All(view, x => Assert.Equal(TaskFieldSource.Type, x.Source));
        Assert.Equal(["Number", "Ratio", "Flag", "Due", "Level", "Note"], view.Select(x => x.Name).ToArray());

        // Значение enum можно задать и по id.
        var byId = await Edit(ctx, read, Set(level, priority.Values[0].Id.ToString()));
        Assert.Equal(["Low"], (await With(s => s.Tasks.GetFields(ctx.Project, byId!.Id)))!.Single(x => x.Name == "Level").Texts.ToArray());
    }

    [Fact]
    public async Task Wrong_values_are_rejected_and_several_values_need_a_multiple_field()
    {
        var ctx = await NewProject();
        var priority = await NewEnum(ctx);
        var number = await NewField(ctx, "Number", FieldType.Int);
        var ratio = await NewField(ctx, "Ratio", FieldType.Float);
        var flag = await NewField(ctx, "Flag", FieldType.Bool);
        var due = await NewField(ctx, "Due", FieldType.Date);
        var level = await NewField(ctx, "Level", FieldType.Enum, enumId: priority.Id);
        var tags = await NewField(ctx, "Tags", FieldType.String, multiple: true);
        var type = await NewType(ctx, "Bug", new[] { number, ratio, flag, due, level, tags }.Select(x => new TaskTypeField(x.Id, false)).ToArray());

        Assert.Contains("is not an integer", (await Invalid(NewTask(ctx, type, fields: Set(number, "1.5")))).Message);
        Assert.Contains("is not an integer", (await Invalid(NewTask(ctx, type, fields: Set(number, "abc")))).Message);
        Assert.Contains("is not a number", (await Invalid(NewTask(ctx, type, fields: Set(ratio, "NaN")))).Message);
        Assert.Contains("is not a number", (await Invalid(NewTask(ctx, type, fields: Set(ratio, "1,5")))).Message);
        Assert.Contains("true or false", (await Invalid(NewTask(ctx, type, fields: Set(flag, "yes")))).Message);
        Assert.Contains("yyyy-MM-dd", (await Invalid(NewTask(ctx, type, fields: Set(due, "02.10.2026")))).Message);
        Assert.Contains("yyyy-MM-dd", (await Invalid(NewTask(ctx, type, fields: Set(due, "2026-02-30")))).Message);
        Assert.Contains("is not a value of enum 'Priority'", (await Invalid(NewTask(ctx, type, fields: Set(level, "Urgent")))).Message);
        Assert.Contains("must not be empty", (await Invalid(NewTask(ctx, type, fields: Set(tags, " ")))).Message);
        Assert.Contains("single value", (await Invalid(NewTask(ctx, type, fields: Set(number, "1", "2")))).Message);
        Assert.Contains("repeated", (await Invalid(NewTask(ctx, type, fields: Set(tags, "a", "A", "a")))).Message);
        Assert.Contains("the task has no field", (await Invalid(NewTask(ctx, type, fields: new TaskFieldChanges { Values = [new TaskFieldValueInput(Guid.NewGuid(), ["x"])] }))).Message);
        Assert.Contains("duplicates", (await Invalid(NewTask(ctx, type, fields: new TaskFieldChanges
            { Values = [new TaskFieldValueInput(tags.Id, ["a"]), new TaskFieldValueInput(tags.Id, ["b"])] }))).Message);
        Assert.Contains("no fields to remove", (await Invalid(NewTask(ctx, type, fields: new TaskFieldChanges { RemoveFields = [tags.Id] }))).Message);

        // Несколько значений — у множественного поля, порядок сохраняется.
        var task = await NewTask(ctx, type, fields: Set(tags, "b", "a", "c"));
        Assert.Equal(["b", "a", "c"], Values((await With(s => s.Tasks.GetFields(ctx.Project, task.Id)))!, "Tags"));
        Assert.Empty(await With(s => s.TaskStorage.GetAll(ctx.Project, new TaskFilter { TypeIds = [Guid.NewGuid()] })));
        Assert.Single(await With(s => s.TaskStorage.GetAll(ctx.Project, new TaskFilter { TypeIds = [type.Id] })));
    }

    [Fact]
    public async Task Clearing_a_value_removes_it_and_an_unset_type_field_is_not_stored_in_the_task()
    {
        var ctx = await NewProject();
        var note = await NewField(ctx, "Note");
        var type = await NewType(ctx, "Bug", new TaskTypeField(note.Id, false));
        var task = await NewTask(ctx, type);
        Assert.Empty((await Reload(ctx, task)).Fields);

        var set = (await Edit(ctx, task, Set(note, "x")))!;
        Assert.Single((await Reload(ctx, set)).Fields);

        var cleared = (await Edit(ctx, set, Set(note)))!;
        Assert.Empty((await Reload(ctx, cleared)).Fields);
        Assert.Empty((await With(s => s.Tasks.GetFields(ctx.Project, task.Id)))!.Single().Values);
    }

    // ---- обязательные поля ----

    [Fact]
    public async Task A_required_field_without_a_value_fails_creation_listing_the_fields_and_creation_with_values_works()
    {
        var ctx = await NewProject();
        var estimate = await NewField(ctx, "Estimate", FieldType.Int);
        var owner = await NewField(ctx, "Owner");
        var note = await NewField(ctx, "Note");
        var type = await NewType(ctx, "Bug", new TaskTypeField(estimate.Id, true), new TaskTypeField(owner.Id, true), new TaskTypeField(note.Id, false));

        var error = await Invalid(NewTask(ctx, type, fields: Set(owner, "Ann")));
        Assert.Contains("'Estimate'", error.Message);
        Assert.DoesNotContain("'Owner'", error.Message);
        var both = await Invalid(NewTask(ctx, type));
        Assert.Contains("'Estimate', 'Owner'", both.Message);
        Assert.Empty((await With(s => s.TaskStorage.GetRange(ctx.Project, null, new Tasker.Core.Dto.Page(0, 10)))).Data);

        var task = await NewTask(ctx, type, fields: Set((estimate, ["3"]), (owner, ["Ann"])));
        Assert.Equal(2, (await Reload(ctx, task)).Fields.Count);
    }

    [Fact]
    public async Task A_required_field_added_later_leaves_tasks_alone_until_they_are_edited_and_a_status_change_never_checks_it()
    {
        var ctx = await NewProject();
        var estimate = await NewField(ctx, "Estimate", FieldType.Int);
        var type = await NewType(ctx, "Bug");
        var task = await NewTask(ctx, type, "Old");

        await With(s => s.Types.Update(ctx.Project, type.Id, new UpdateTaskType(null, null, type.Version, [new TaskTypeField(estimate.Id, true)])));
        Assert.Empty((await Reload(ctx, task)).Fields);

        // Смена статуса (так же переносит по доске move_task) обязательное поле не проверяет.
        var moved = (await Edit(ctx, task, statusId: ctx.Done))!;
        Assert.Equal(ctx.Done, moved.StatusId);

        // Правка содержимого обязана заполнить поле в той же операции.
        var error = await Invalid(Edit(ctx, moved, title: "Renamed"));
        Assert.Contains("'Estimate'", error.Message);
        Assert.Equal("Old", (await Reload(ctx, moved)).Title);

        var fixedTask = (await Edit(ctx, moved, Set(estimate, "5"), title: "Renamed"))!;
        Assert.Equal("Renamed", fixedTask.Title);
        Assert.Equal(["5"], Values((await With(s => s.Tasks.GetFields(ctx.Project, fixedTask.Id)))!, "Estimate"));

        // Очистить обязательное поле при правке содержимого нельзя.
        Assert.Contains("'Estimate'", (await Invalid(Edit(ctx, fixedTask, Set(estimate)))).Message);
    }

    [Fact]
    public async Task Creating_a_task_validates_status_and_values_in_one_step_and_a_failed_creation_leaves_nothing()
    {
        var ctx = await NewProject();
        var estimate = await NewField(ctx, "Estimate", FieldType.Int);
        var type = await NewType(ctx, "Bug", new TaskTypeField(estimate.Id, true));

        await Invalid(NewTask(ctx, type, fields: Set(estimate, "x")));
        await Invalid(With(s => s.Tasks.Create(ctx.Project, new CreateTask("T", null, type.Id, Guid.NewGuid(), null, Set(estimate, "1")))));
        Assert.Empty((await With(s => s.TaskStorage.GetRange(ctx.Project, null, new Tasker.Core.Dto.Page(0, 10)))).Data);
    }

    // ---- дополнительные и собственные поля ----

    [Fact]
    public async Task A_task_gets_extra_catalog_fields_and_own_fields_and_can_remove_them_but_not_type_fields()
    {
        var ctx = await NewProject();
        var priority = await NewEnum(ctx);
        var typed = await NewField(ctx, "Typed");
        var extra = await NewField(ctx, "Extra", FieldType.Int);
        var valued = await NewField(ctx, "Valued");
        var type = await NewType(ctx, "Bug", new TaskTypeField(typed.Id, false));
        var task = await NewTask(ctx, type);

        var changed = (await Edit(ctx, task, new TaskFieldChanges
        {
            AddFields = [extra.Id, typed.Id],
            Values = [new TaskFieldValueInput(valued.Id, ["v"])],
            NewOwnFields =
            [
                new NewOwnField("Hours", FieldType.Float, ["1.5", "2"], Required: true, Multiple: true),
                new NewOwnField("Mood", FieldType.Enum, ["low"], EnumId: priority.Id)
            ]
        }))!;

        var view = (await With(s => s.Tasks.GetFields(ctx.Project, changed.Id)))!;
        Assert.Equal(["Typed", "Extra", "Valued", "Hours", "Mood"], view.Select(x => x.Name).ToArray());
        Assert.Equal(
            [TaskFieldSource.Type, TaskFieldSource.Extra, TaskFieldSource.Extra, TaskFieldSource.Own, TaskFieldSource.Own],
            view.Select(x => x.Source).ToArray());
        var hours = view.Single(x => x.Name == "Hours");
        Assert.Equal((FieldType.Float, true, true), (hours.Type, hours.Required, hours.Multiple));
        Assert.Equal(["1.5", "2"], hours.Values.ToArray());
        Assert.Equal(priority.Id, view.Single(x => x.Name == "Mood").EnumId);
        Assert.Equal(["Low"], view.Single(x => x.Name == "Mood").Texts.ToArray());
        Assert.Empty(view.Single(x => x.Name == "Extra").Values);

        // Собственное поле хранится в задаче целиком — и читается обратно из хранилища.
        var read = await Reload(ctx, changed);
        Assert.Equal(new OwnField("Hours", FieldType.Float, true, true, null), read.Fields.Single(x => x.Own?.Name == "Hours").Own);
        Assert.Equal(new OwnField("Mood", FieldType.Enum, false, false, priority.Id), read.Fields.Single(x => x.Own?.Name == "Mood").Own);

        // Поле типа убрать нельзя, дополнительные и собственные — можно.
        var hoursId = hours.FieldId;
        Assert.Contains("cannot be removed", (await Invalid(Edit(ctx, changed, new TaskFieldChanges { RemoveFields = [typed.Id] }))).Message);
        Assert.Contains("no additional or own field", (await Invalid(Edit(ctx, changed, new TaskFieldChanges { RemoveFields = [Guid.NewGuid()] }))).Message);
        var removed = (await Edit(ctx, changed, new TaskFieldChanges { RemoveFields = [extra.Id, hoursId] }))!;
        Assert.Equal(["Typed", "Valued", "Mood"], (await With(s => s.Tasks.GetFields(ctx.Project, removed.Id)))!.Select(x => x.Name).ToArray());
    }

    [Fact]
    public async Task Own_fields_are_validated_names_stay_unique_in_the_task_and_a_required_own_field_needs_a_value()
    {
        var ctx = await NewProject();
        var priority = await NewEnum(ctx);
        var note = await NewField(ctx, "Note");
        var type = await NewType(ctx, "Bug", new TaskTypeField(note.Id, false));
        var task = await NewTask(ctx, type);

        Assert.Contains("already has a field with this name", (await Invalid(Edit(ctx, task, new TaskFieldChanges { NewOwnFields = [new NewOwnField("note", FieldType.Int)] }))).Message);
        Assert.Contains("required", (await Invalid(Edit(ctx, task, new TaskFieldChanges { NewOwnFields = [new NewOwnField(" ", FieldType.Int)] }))).Message);
        Assert.Contains("only a field of type enum", (await Invalid(Edit(ctx, task, new TaskFieldChanges { NewOwnFields = [new NewOwnField("X", FieldType.Int, EnumId: priority.Id)] }))).Message);
        Assert.Contains("must refer to an enum", (await Invalid(Edit(ctx, task, new TaskFieldChanges { NewOwnFields = [new NewOwnField("X", FieldType.Enum)] }))).Message);
        Assert.Contains("enum not found", (await Invalid(Edit(ctx, task, new TaskFieldChanges { NewOwnFields = [new NewOwnField("X", FieldType.Enum, EnumId: Guid.NewGuid())] }))).Message);
        Assert.Contains("already has a field with this name", (await Invalid(Edit(ctx, task, new TaskFieldChanges
            { NewOwnFields = [new NewOwnField("Same", FieldType.Int), new NewOwnField("SAME", FieldType.Bool)] }))).Message);
        Assert.Contains("is not an integer", (await Invalid(Edit(ctx, task, new TaskFieldChanges { NewOwnFields = [new NewOwnField("X", FieldType.Int, ["z"])] }))).Message);
        Assert.Empty((await Reload(ctx, task)).Fields);

        // Обязательное собственное поле без значения не даёт править содержимое, пока значение не задано.
        Assert.Contains("'Hours'", (await Invalid(Edit(ctx, task, new TaskFieldChanges { NewOwnFields = [new NewOwnField("Hours", FieldType.Int, null, Required: true)] }))).Message);
        var withValue = (await Edit(ctx, task, new TaskFieldChanges { NewOwnFields = [new NewOwnField("Hours", FieldType.Int, ["4"], Required: true)] }))!;
        var hours = (await With(s => s.Tasks.GetFields(ctx.Project, withValue.Id)))!.Single(x => x.Name == "Hours");
        Assert.Contains("'Hours'", (await Invalid(Edit(ctx, withValue, Set(new FieldDefinition { Id = hours.FieldId, ProjectId = ctx.Project, Name = "Hours", Type = FieldType.Int, Version = "" })))).Message);
        Assert.NotNull(await Edit(ctx, withValue, statusId: ctx.Done));
    }

    [Fact]
    public async Task A_catalog_field_that_is_not_the_tasks_becomes_an_extra_field_when_a_value_is_set()
    {
        var ctx = await NewProject();
        var extra = await NewField(ctx, "Extra", FieldType.Int);
        var type = await NewType(ctx, "Bug");
        var task = await NewTask(ctx, type);

        var changed = (await Edit(ctx, task, Set(extra, "4")))!;
        var field = (await With(s => s.Tasks.GetFields(ctx.Project, changed.Id)))!.Single();
        Assert.Equal((TaskFieldSource.Extra, false), (field.Source, field.Required));
        Assert.Equal(["4"], field.Values.ToArray());

        // Пустые значения у поля, которого у задачи нет, ничего не добавляют.
        var other = await NewField(ctx, "Other");
        var same = (await Edit(ctx, changed, Set(other)))!;
        Assert.Single((await Reload(ctx, same)).Fields);
    }

    // ---- убрали поле из типа ----

    [Fact]
    public async Task Removing_a_type_field_without_values_needs_no_choice_but_with_values_it_needs_one()
    {
        var ctx = await NewProject();
        var note = await NewField(ctx, "Note");
        var area = await NewField(ctx, "Area");
        var type = await NewType(ctx, "Bug", new TaskTypeField(note.Id, true), new TaskTypeField(area.Id, false));
        var one = await NewTask(ctx, type, "One", Set(note, "n1"));
        var two = await NewTask(ctx, type, "Two", Set((note, ["n2"]), (area, ["a2"])));

        // У поля Area значения есть только у одной задачи, но убрать без выбора нельзя; ошибка называет число затронутых задач.
        var error = await Conflict(With(s => s.Types.Update(ctx.Project, type.Id, new UpdateTaskType(null, null, type.Version, [new TaskTypeField(note.Id, true)]))));
        Assert.Equal(ConflictCode.InUse, error.Code);
        Assert.Contains("1 task(s)", error.Message);
        Assert.Contains("'Area'", error.Message);
        Assert.Equal(2, (await With(s => s.TypeStorage.GetById(ctx.Project, type.Id)))!.Fields.Count);

        // Поле, у которого значений нет ни у одной задачи, убирается без выбора.
        var cleared = (await Edit(ctx, two, Set(area)))!;
        var removedResult = (await With(s => s.Types.Update(ctx.Project, type.Id, new UpdateTaskType(null, null, type.Version, [new TaskTypeField(note.Id, true)]))))!;
        Assert.Equal(0, removedResult.AffectedTasks);
        var removed = removedResult.Value;
        Assert.Equal([note.Id], removed.Fields.Select(x => x.FieldId).ToArray());
        Assert.Equal(cleared.Version, (await Reload(ctx, cleared)).Version);
        Assert.Equal(["n1"], (await Reload(ctx, one)).Fields.Single().Values.ToArray());
    }

    [Fact]
    public async Task Removing_a_type_field_with_the_choice_to_clear_removes_the_values_of_all_tasks_of_the_type_only()
    {
        var ctx = await NewProject();
        var note = await NewField(ctx, "Note");
        var type = await NewType(ctx, "Bug", new TaskTypeField(note.Id, false));
        var sibling = await NewType(ctx, "Story", new TaskTypeField(note.Id, false));
        var tasks = new List<TaskItem>();
        for (var i = 0; i < 3; i++)
            tasks.Add(await NewTask(ctx, type, "Bug" + i, Set(note, "v" + i)));
        var untouched = await NewTask(ctx, sibling, "Story", Set(note, "keep"));
        var extraOfOther = await NewTask(ctx, await NewType(ctx, "Plain"), "Plain", Set(note, "extra"));

        var error = await Conflict(With(s => s.Types.Update(ctx.Project, type.Id, new UpdateTaskType(null, null, type.Version, [], null))));
        Assert.Contains("3 task(s)", error.Message);
        foreach (var task in tasks)
            Assert.Single((await Reload(ctx, task)).Fields);

        var before = await Versions(ctx, [.. tasks, untouched, extraOfOther]);
        var result = (await With(s => s.Types.Update(ctx.Project, type.Id, new UpdateTaskType(null, null, type.Version, [], RemovedFieldValues.Clear))))!;
        var updated = result.Value;
        Assert.Empty(updated.Fields);
        foreach (var task in tasks)
            Assert.Empty((await Reload(ctx, task)).Fields);
        // Число совпадает с реально переписанными задачами: три задачи типа, задачи других типов не тронуты.
        Assert.Equal(3, result.AffectedTasks);
        Assert.Equal(result.AffectedTasks, (await Versions(ctx, [.. tasks, untouched, extraOfOther])).Where((v, i) => v != before[i]).Count());
        Assert.Equal(["keep"], (await Reload(ctx, untouched)).Fields.Single().Values.ToArray());
        Assert.Equal(["extra"], (await Reload(ctx, extraOfOther)).Fields.Single().Values.ToArray());
    }

    [Fact]
    public async Task Removing_a_type_field_with_the_choice_to_keep_turns_it_into_an_optional_additional_field_of_each_task_with_a_value()
    {
        var ctx = await NewProject();
        var note = await NewField(ctx, "Note");
        var type = await NewType(ctx, "Bug", new TaskTypeField(note.Id, true));
        var withValue = await NewTask(ctx, type, "With", Set(note, "v"));
        var empty = await NewTask(ctx, type, "Empty", Set(note, "tmp"));
        // Пустое обязательное поле через сервис не получить (правка содержимого его требует): так задача выглядит после слияния веток.
        var emptyVersion = (await With(s => s.TaskStorage.Update(empty with { Fields = [] }, empty.Version)))!;
        empty = empty with { Fields = [], Version = emptyVersion };

        var keptResult = (await With(s => s.Types.Update(ctx.Project, type.Id, new UpdateTaskType(null, null, type.Version, [], RemovedFieldValues.Keep))))!;
        var updated = keptResult.Value;
        Assert.Empty(updated.Fields);
        // Затронута одна задача (у неё поле стало дополнительным); у пустой значений нет. Задачи не переписаны.
        Assert.Equal(1, keptResult.AffectedTasks);
        Assert.Equal(withValue.Version, (await Reload(ctx, withValue)).Version);

        var field = (await With(s => s.Tasks.GetFields(ctx.Project, withValue.Id)))!.Single();
        Assert.Equal((TaskFieldSource.Extra, false), (field.Source, field.Required));
        Assert.Equal(["v"], field.Values.ToArray());
        Assert.Empty((await With(s => s.Tasks.GetFields(ctx.Project, empty.Id)))!);

        // Такое поле можно убрать у задачи вручную, а правка содержимого больше не требует его.
        var edited = (await Edit(ctx, withValue, new TaskFieldChanges { RemoveFields = [note.Id] }, title: "Renamed"))!;
        Assert.Empty((await Reload(ctx, edited)).Fields);
        Assert.NotNull(await Edit(ctx, empty, title: "Renamed too"));
    }

    private async Task<string[]> Versions(Ctx ctx, params TaskItem[] tasks)
    {
        var result = new List<string>();
        foreach (var task in tasks)
            result.Add((await Reload(ctx, task)).Version);
        return result.ToArray();
    }

    // ---- смена типа ----

    [Fact]
    public async Task Changing_the_type_keeps_old_fields_as_additional_ones_keeps_shared_values_and_requires_the_new_required_fields()
    {
        var ctx = await NewProject();
        var shared = await NewField(ctx, "Shared");
        var oldOnly = await NewField(ctx, "OldOnly");
        var needed = await NewField(ctx, "Needed", FieldType.Int);
        var oldType = await NewType(ctx, "Old", new TaskTypeField(shared.Id, false), new TaskTypeField(oldOnly.Id, false));
        var newType = await NewType(ctx, "New", new TaskTypeField(shared.Id, true), new TaskTypeField(needed.Id, true));
        var task = await NewTask(ctx, oldType, fields: Set((shared, ["s"]), (oldOnly, ["o"])));

        var error = await Invalid(Edit(ctx, task, typeId: newType.Id));
        Assert.Contains("'Needed'", error.Message);
        Assert.DoesNotContain("'Shared'", error.Message);
        Assert.Equal(oldType.Id, (await Reload(ctx, task)).TypeId);

        var changed = (await Edit(ctx, task, Set(needed, "9"), typeId: newType.Id))!;
        Assert.Equal(newType.Id, changed.TypeId);
        var view = (await With(s => s.Tasks.GetFields(ctx.Project, changed.Id)))!;
        Assert.Equal(["Shared", "Needed", "OldOnly"], view.Select(x => x.Name).ToArray());
        Assert.Equal(["s"], Values(view, "Shared"));
        Assert.Equal([TaskFieldSource.Type, TaskFieldSource.Type, TaskFieldSource.Extra], view.Select(x => x.Source).ToArray());
        Assert.Equal([true, true, false], view.Select(x => x.Required).ToArray());
        Assert.Equal(["o"], Values(view, "OldOnly"));
    }

    // ---- значения перечисления ----

    [Fact]
    public async Task Removing_an_enum_value_that_no_task_uses_needs_no_choice_and_one_that_tasks_use_needs_one()
    {
        var ctx = await NewProject();
        var priority = await NewEnum(ctx);
        var (low, medium, high) = (priority.Values[0], priority.Values[1], priority.Values[2]);
        var level = await NewField(ctx, "Level", FieldType.Enum, enumId: priority.Id);
        var type = await NewType(ctx, "Bug", new TaskTypeField(level.Id, false));
        await NewTask(ctx, type, "A", Set(level, "High"));
        await NewTask(ctx, type, "B", Set(level, "High"));

        UpdateFieldEnum Without(FieldEnumValue removed, RemovedEnumValues? choice = null, string? version = null) => new(
            null, priority.Values.Where(x => x.Id != removed.Id).Select(x => new FieldEnumValueInput(x.Id, x.Name)).ToArray(), version ?? priority.Version, choice);

        // Medium не выбран нигде — выбор не нужен.
        var mediumResult = (await With(s => s.Enums.Update(ctx.Project, priority.Id, Without(medium))))!;
        Assert.Equal(0, mediumResult.AffectedTasks);
        var afterMedium = mediumResult.Value;
        Assert.Equal(["Low", "High"], afterMedium.Values.Select(x => x.Name).ToArray());

        // High выбран у двух задач: без выбора — In use с числом задач, и ничего не меняется.
        var error = await Conflict(With(s => s.Enums.Update(ctx.Project, priority.Id,
            new UpdateFieldEnum(null, [new FieldEnumValueInput(low.Id, "Low")], afterMedium.Version))));
        Assert.Equal(ConflictCode.InUse, error.Code);
        Assert.Contains("2 task(s)", error.Message);
        Assert.Contains("'High'", error.Message);
        Assert.Equal(afterMedium.Version, (await With(s => s.Enums.GetById(ctx.Project, priority.Id)))!.Version);
        Assert.Equal(high.Id.ToString("D"), (await With(s => s.TaskStorage.GetAll(ctx.Project, new TaskFilter { FieldIds = [level.Id] }))).First().Fields.Single().Values.Single());
    }

    [Fact]
    public async Task Removing_a_used_enum_value_can_clear_it_from_the_tasks_including_required_fields_and_own_fields()
    {
        var ctx = await NewProject();
        var priority = await NewEnum(ctx);
        var (low, high) = (priority.Values[0], priority.Values[2]);
        var level = await NewField(ctx, "Level", FieldType.Enum, multiple: true, enumId: priority.Id);
        var type = await NewType(ctx, "Bug", new TaskTypeField(level.Id, true));
        var plain = await NewType(ctx, "Plain");
        var multi = await NewTask(ctx, type, "Multi", Set(level, "Low", "High"));
        var only = await NewTask(ctx, type, "Only", Set(level, "High"));
        var own = await NewTask(ctx, plain, "Own", new TaskFieldChanges { NewOwnFields = [new NewOwnField("Mood", FieldType.Enum, ["High"], EnumId: priority.Id)] });
        var untouched = await NewTask(ctx, type, "Untouched", Set(level, "Low"));

        var before = await Versions(ctx, multi, only, own, untouched);
        var result = (await With(s => s.Enums.Update(ctx.Project, priority.Id, new UpdateFieldEnum(null,
            priority.Values.Where(x => x.Id != high.Id).Select(x => new FieldEnumValueInput(x.Id, x.Name)).ToArray(), priority.Version, new RemovedEnumValues(Clear: true)))))!;
        var updated = result.Value;
        // Число совпадает с реально переписанными задачами: Multi, Only и Own (собственное поле); Untouched не тронута.
        Assert.Equal(3, result.AffectedTasks);
        Assert.Equal(result.AffectedTasks, (await Versions(ctx, multi, only, own, untouched)).Where((v, i) => v != before[i]).Count());
        Assert.DoesNotContain(high.Id, updated.Values.Select(x => x.Id));

        Assert.Equal([low.Id.ToString("D")], (await Reload(ctx, multi)).Fields.Single().Values.ToArray());
        // Обязательное поле осталось пустым: это не правка пользователем, она не блокируется. Поле типа без значения в задаче не пишется.
        Assert.Empty((await Reload(ctx, only)).Fields);
        Assert.Empty((await With(s => s.Tasks.GetFields(ctx.Project, only.Id)))!.Single().Values);
        // Собственное поле остаётся у задачи, значение убрано.
        var ownField = (await Reload(ctx, own)).Fields.Single();
        Assert.Equal("Mood", ownField.Own!.Name);
        Assert.Empty(ownField.Values);
        Assert.Equal([low.Id.ToString("D")], (await Reload(ctx, untouched)).Fields.Single().Values.ToArray());
        // Пустое обязательное поле по-прежнему требует значение при правке содержимого.
        Assert.Contains("'Level'", (await Invalid(Edit(ctx, await Reload(ctx, only), title: "Renamed"))).Message);
    }

    [Fact]
    public async Task Removing_a_used_enum_value_can_reassign_it_without_duplicates()
    {
        var ctx = await NewProject();
        var priority = await NewEnum(ctx);
        var (low, medium, high) = (priority.Values[0], priority.Values[1], priority.Values[2]);
        var level = await NewField(ctx, "Level", FieldType.Enum, multiple: true, enumId: priority.Id);
        var type = await NewType(ctx, "Bug", new TaskTypeField(level.Id, false));
        var both = await NewTask(ctx, type, "Both", Set(level, "High", "Low"));
        var single = await NewTask(ctx, type, "Single", Set(level, "High"));
        var ownOnly = await NewTask(ctx, await NewType(ctx, "Plain"), "Own", new TaskFieldChanges
            { NewOwnFields = [new NewOwnField("Mood", FieldType.Enum, ["High", "Medium"], Multiple: true, EnumId: priority.Id)] });

        UpdateFieldEnum Remove(Guid removed, RemovedEnumValues choice, string version) => new(
            null, priority.Values.Where(x => x.Id != removed).Select(x => new FieldEnumValueInput(x.Id, x.Name)).ToArray(), version, choice);

        // Целевое значение должно остаться в перечислении; выбор — ровно один.
        Assert.Contains("not among the values the enum keeps", (await Invalid(With(s => s.Enums.Update(ctx.Project, priority.Id, Remove(high.Id, new RemovedEnumValues(ReassignTo: high.Id), priority.Version))))).Message);
        Assert.Contains("not among the values the enum keeps", (await Invalid(With(s => s.Enums.Update(ctx.Project, priority.Id, Remove(high.Id, new RemovedEnumValues(ReassignTo: Guid.NewGuid()), priority.Version))))).Message);
        Assert.Contains("exactly one", (await Invalid(With(s => s.Enums.Update(ctx.Project, priority.Id, Remove(high.Id, new RemovedEnumValues(Clear: true, ReassignTo: low.Id), priority.Version))))).Message);
        Assert.Contains("exactly one", (await Invalid(With(s => s.Enums.Update(ctx.Project, priority.Id, Remove(high.Id, new RemovedEnumValues(), priority.Version))))).Message);
        Assert.Equal(priority.Version, (await With(s => s.Enums.GetById(ctx.Project, priority.Id)))!.Version);

        var reassigned = (await With(s => s.Enums.Update(ctx.Project, priority.Id, Remove(high.Id, new RemovedEnumValues(ReassignTo: low.Id), priority.Version))))!;
        var updated = reassigned.Value;
        Assert.Equal(["Low", "Medium"], updated.Values.Select(x => x.Name).ToArray());
        Assert.Equal(3, reassigned.AffectedTasks); // Both, Single и собственное поле Own

        Assert.Equal([low.Id.ToString("D")], (await Reload(ctx, both)).Fields.Single().Values.ToArray());
        Assert.Equal([low.Id.ToString("D")], (await Reload(ctx, single)).Fields.Single().Values.ToArray());
        Assert.Equal([low.Id.ToString("D"), medium.Id.ToString("D")], (await Reload(ctx, ownOnly)).Fields.Single().Values.ToArray());
    }

    [Fact]
    public async Task Renaming_an_enum_value_keeps_the_tasks_values()
    {
        var ctx = await NewProject();
        var priority = await NewEnum(ctx);
        var level = await NewField(ctx, "Level", FieldType.Enum, enumId: priority.Id);
        var type = await NewType(ctx, "Bug", new TaskTypeField(level.Id, false));
        var task = await NewTask(ctx, type, "T", Set(level, "High"));

        var renamed = (await With(s => s.Enums.Update(ctx.Project, priority.Id, new UpdateFieldEnum(null,
            priority.Values.Select(x => new FieldEnumValueInput(x.Id, x.Name == "High" ? "Critical" : x.Name)).ToArray(), priority.Version))))!.Value;

        Assert.Equal("Critical", renamed.Values[2].Name);
        Assert.Equal(["Critical"], (await With(s => s.Tasks.GetFields(ctx.Project, task.Id)))!.Single().Texts.ToArray());
    }

    // ---- «используется» при удалении поля и перечисления ----

    [Fact]
    public async Task A_field_used_by_a_type_or_a_task_cannot_be_deleted_and_an_enum_used_by_an_own_field_cannot_be_deleted()
    {
        var ctx = await NewProject();
        var priority = await NewEnum(ctx);
        var typed = await NewField(ctx, "Typed");
        var added = await NewField(ctx, "Added");
        var free = await NewField(ctx, "Free");
        var type = await NewType(ctx, "Bug", new TaskTypeField(typed.Id, false));
        var task = await NewTask(ctx, type, "T", new TaskFieldChanges
        {
            AddFields = [added.Id],
            NewOwnFields = [new NewOwnField("Mood", FieldType.Enum, EnumId: priority.Id)]
        });

        var byType = await Conflict(With(s => s.Fields.Delete(ctx.Project, typed.Id, typed.Version)));
        Assert.Equal(ConflictCode.InUse, byType.Code);
        Assert.Contains("task type 'Bug'", byType.Message);
        var byTask = await Conflict(With(s => s.Fields.Delete(ctx.Project, added.Id, added.Version)));
        Assert.Contains("1 task(s)", byTask.Message);
        var byOwn = await Conflict(With(s => s.Enums.Delete(ctx.Project, priority.Id, priority.Version)));
        Assert.Contains("own fields of 1 task(s)", byOwn.Message);

        Assert.True(await With(s => s.Fields.Delete(ctx.Project, free.Id, free.Version)));

        // Убрали ссылки — удалить можно.
        var ownId = (await With(s => s.Tasks.GetFields(ctx.Project, task.Id)))!.Single(x => x.Name == "Mood").FieldId;
        var cleaned = (await Edit(ctx, task, new TaskFieldChanges { RemoveFields = [added.Id, ownId] }))!;
        Assert.True(await With(s => s.Enums.Delete(ctx.Project, priority.Id, priority.Version)));
        Assert.True(await With(s => s.Fields.Delete(ctx.Project, added.Id, added.Version)));
        var plainType = (await With(s => s.Types.Update(ctx.Project, type.Id, new UpdateTaskType(null, null, type.Version, []))))!.Value;
        Assert.True(await With(s => s.Fields.Delete(ctx.Project, typed.Id, typed.Version)));
        Assert.NotNull(cleaned);
        Assert.NotNull(plainType);
    }

    // ---- блокировки ----

    [Fact]
    public async Task Changing_fields_uses_the_version_and_edit_locks_of_the_task_and_the_type()
    {
        var ctx = await NewProject();
        var note = await NewField(ctx, "Note");
        var type = await NewType(ctx, "Bug", new TaskTypeField(note.Id, false));
        var task = await NewTask(ctx, type, "T", Set(note, "a"));

        // Устаревшая версия задачи.
        var first = (await Edit(ctx, task, Set(note, "b")))!;
        var stale = await Conflict(Edit(ctx, task, Set(note, "c")));
        Assert.Equal(ConflictCode.Modified, stale.Code);
        Assert.Equal(["b"], (await Reload(ctx, first)).Fields.Single().Values.ToArray());

        // Устаревшая версия типа — и значения задач при выборе «убрать» не теряются.
        var staleType = await Conflict(With(s => s.Types.Update(ctx.Project, type.Id, new UpdateTaskType(null, null, "nope", [], RemovedFieldValues.Clear))));
        Assert.Equal(ConflictCode.Modified, staleType.Code);
        Assert.Single((await Reload(ctx, first)).Fields);

        // Блокировка задачи другим держателем: правка полей отклоняется.
        await Run(async scope =>
        {
            var locks = new EditLockService(scope.Resolve<IEditLockStorage>(), new FakeEditor { Holder = FakeEditor.Anna }, TimeProvider.System);
            await locks.Acquire(LockedEntity.Task, task.Id, "Task", ctx.Project);
            await locks.Acquire(LockedEntity.TaskType, type.Id, "Type", ctx.Project);
            return 0;
        });
        Assert.Equal(ConflictCode.Locked, (await Conflict(Edit(ctx, first, Set(note, "d")))).Code);
        Assert.Equal(ConflictCode.Locked, (await Conflict(With(s => s.Types.Update(ctx.Project, type.Id, new UpdateTaskType(null, null, type.Version, []))))).Code);
    }

    [Fact]
    public async Task Concurrent_edits_of_different_values_of_one_task_do_not_lose_the_task_and_exactly_one_stale_edit_wins()
    {
        var ctx = await NewProject();
        var note = await NewField(ctx, "Note");
        var type = await NewType(ctx, "Bug", new TaskTypeField(note.Id, false));
        var task = await NewTask(ctx, type, "T", Set(note, "start"));

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(i => Task.Run(async () =>
        {
            try
            {
                await Edit(ctx, task, Set(note, "v" + i));
                return true;
            }
            catch (TaskerConflictException)
            {
                return false;
            }
        })));

        Assert.Equal(1, results.Count(x => x));
        Assert.StartsWith("v", (await Reload(ctx, task)).Fields.Single().Values.Single());
    }

    // ---- массовые правки и блокировки отдельных задач ----

    private async Task<(Ctx Ctx, FieldDefinition Note, TaskType Type, TaskItem[] Tasks)> NoteTasks(int count)
    {
        var ctx = await NewProject();
        var note = await NewField(ctx, "Note");
        var type = await NewType(ctx, "Bug", new TaskTypeField(note.Id, false));
        var tasks = new List<TaskItem>();
        for (var i = 0; i < count; i++)
            tasks.Add(await NewTask(ctx, type, "Bug" + i, Set(note, "v")));
        return (ctx, note, type, tasks.ToArray());
    }

    [Fact]
    public async Task A_clear_waits_out_the_timeout_on_a_task_locked_by_someone_else_then_fails_with_locked_listing_them_and_writes_nothing()
    {
        var (ctx, _, type, tasks) = await NoteTasks(3);
        await AnnaLocks(ctx, tasks[0]);
        await AnnaLocks(ctx, tasks[2]);

        var error = await Assert.ThrowsAsync<TaskerLockedException>(() =>
            With(s => s.Types.Update(ctx.Project, type.Id, new UpdateTaskType(null, null, type.Version, [], RemovedFieldValues.Clear))));

        Assert.Contains("Task 'Bug0'", error.Message);
        Assert.Contains("Task 'Bug2'", error.Message);
        Assert.DoesNotContain("Bug1", error.Message);
        Assert.Contains("Anna", error.Message);
        // Ничего не записано, даже у свободной задачи: проверка — до первой записи.
        foreach (var task in tasks)
            Assert.Equal(task.Version, (await Reload(ctx, task)).Version);
        Assert.Single((await With(s => s.TypeStorage.GetById(ctx.Project, type.Id)))!.Fields);
    }

    [Fact]
    public async Task A_clear_waits_until_the_lock_is_released_and_then_finishes()
    {
        var (ctx, _, type, tasks) = await NoteTasks(2);
        _wait = TimeSpan.FromSeconds(20);
        await AnnaLocks(ctx, tasks[1]);

        var clear = With(s => s.Types.Update(ctx.Project, type.Id, new UpdateTaskType(null, null, type.Version, [], RemovedFieldValues.Clear)));
        await Task.Delay(500);
        Assert.False(clear.IsCompleted);
        Assert.Single((await Reload(ctx, tasks[0])).Fields);

        await AnnaLocks(ctx, tasks[1], release: true);
        Assert.Empty((await clear)!.Value.Fields);
        foreach (var task in tasks)
            Assert.Empty((await Reload(ctx, task)).Fields);
    }

    [Fact]
    public async Task A_locked_task_that_the_clear_does_not_touch_does_not_get_in_the_way()
    {
        var (ctx, note, type, tasks) = await NoteTasks(1);
        var withoutValue = await NewTask(ctx, type, "Empty");
        var otherType = await NewType(ctx, "Other", new TaskTypeField(note.Id, false));
        var otherTask = await NewTask(ctx, otherType, "Other", Set(note, "keep"));
        await AnnaLocks(ctx, withoutValue);
        await AnnaLocks(ctx, otherTask);

        var updated = await With(s => s.Types.Update(ctx.Project, type.Id, new UpdateTaskType(null, null, type.Version, [], RemovedFieldValues.Clear)));

        Assert.Empty(updated!.Value.Fields);
        Assert.Empty((await Reload(ctx, tasks[0])).Fields);
        Assert.Single((await Reload(ctx, otherTask)).Fields);
    }

    [Fact]
    public async Task A_reassign_of_an_enum_value_follows_the_same_lock_rule()
    {
        var ctx = await NewProject();
        var priority = await NewEnum(ctx);
        var level = await NewField(ctx, "Level", FieldType.Enum, enumId: priority.Id);
        var type = await NewType(ctx, "Bug", new TaskTypeField(level.Id, false));
        var locked = await NewTask(ctx, type, "Locked", Set(level, "High"));
        var free = await NewTask(ctx, type, "Free", Set(level, "High"));
        var unrelated = await NewTask(ctx, type, "Unrelated", Set(level, "Low"));
        await AnnaLocks(ctx, locked);
        await AnnaLocks(ctx, unrelated);

        var command = new UpdateFieldEnum(null,
            priority.Values.Where(x => x.Name != "High").Select(x => new FieldEnumValueInput(x.Id, x.Name)).ToArray(), priority.Version,
            new RemovedEnumValues(ReassignTo: priority.Values[0].Id));
        var error = await Assert.ThrowsAsync<TaskerLockedException>(() => With(s => s.Enums.Update(ctx.Project, priority.Id, command)));

        Assert.Contains("Task 'Locked'", error.Message);
        Assert.DoesNotContain("Unrelated", error.Message);
        Assert.Equal(free.Version, (await Reload(ctx, free)).Version);
        Assert.Equal(priority.Version, (await With(s => s.Enums.GetById(ctx.Project, priority.Id)))!.Version);

        await AnnaLocks(ctx, locked, release: true);
        Assert.NotNull(await With(s => s.Enums.Update(ctx.Project, priority.Id, command)));
        Assert.Equal([priority.Values[0].Id.ToString("D")], (await Reload(ctx, free)).Fields.Single().Values.ToArray());
    }

    // ---- «выбор» атомарен ----

    [Fact]
    public async Task A_clear_with_a_stale_type_version_changes_nothing_and_the_same_call_can_be_repeated()
    {
        var ctx = await NewProject();
        var note = await NewField(ctx, "Note");
        var type = await NewType(ctx, "Bug", new TaskTypeField(note.Id, false));
        var tasks = new List<TaskItem>();
        for (var i = 0; i < 3; i++)
            tasks.Add(await NewTask(ctx, type, "T" + i, Set(note, "v")));

        // Тип изменили «с другой стороны» — операция отклонена целиком, значения остались.
        var stale = type.Version;
        await With(s => s.Types.Update(ctx.Project, type.Id, new UpdateTaskType("Renamed", null, type.Version)));
        await Conflict(With(s => s.Types.Update(ctx.Project, type.Id, new UpdateTaskType(null, null, stale, [], RemovedFieldValues.Clear))));
        foreach (var task in tasks)
            Assert.Single((await Reload(ctx, task)).Fields);

        var current = (await With(s => s.TypeStorage.GetById(ctx.Project, type.Id)))!;
        await With(s => s.Types.Update(ctx.Project, type.Id, new UpdateTaskType(null, null, current.Version, [], RemovedFieldValues.Clear)));
        foreach (var task in tasks)
            Assert.Empty((await Reload(ctx, task)).Fields);
    }

    // ---- фильтр по значениям полей ----

    private Task<Tasker.Core.Dto.ListDto<TaskListItem>> ListBy(Ctx ctx, int offset = 0, int limit = 50, params string[] filters) =>
        With(s => s.Tasks.List(ctx.Project, null, filters, Tasker.Core.Dto.Page.Of(offset, limit)));

    private async Task<string[]> TitlesBy(Ctx ctx, params string[] filters) =>
        (await ListBy(ctx, 0, 50, filters)).Data.Select(x => x.Title).Order().ToArray();

    [Fact]
    public async Task Tasks_are_filtered_by_field_values_with_and_semantics_paging_and_total_count()
    {
        var ctx = await NewProject();
        var levels = await NewEnum(ctx, "Levels", "Low", "High");
        var estimate = await NewField(ctx, "Estimate", FieldType.Int);
        var tags = await NewField(ctx, "Tags", FieldType.String, multiple: true);
        var level = await NewField(ctx, "Level", FieldType.Enum, enumId: levels.Id);
        var due = await NewField(ctx, "Due", FieldType.Date);
        var ratio = await NewField(ctx, "Ratio", FieldType.Float);
        var type = await NewType(ctx, "Bug", new TaskTypeField(estimate.Id, false), new TaskTypeField(tags.Id, false), new TaskTypeField(level.Id, false));

        await NewTask(ctx, type, "a", Set((estimate, ["3"]), (tags, ["ui", "api"]), (level, ["High"]), (due, ["2026-10-02"]), (ratio, ["0.5"])));
        await NewTask(ctx, type, "b", Set((estimate, ["5"]), (tags, ["api"]), (level, ["Low"])));
        await NewTask(ctx, type, "c", Set((estimate, ["3"]), (tags, ["db"]), (level, ["High"])));
        await NewTask(ctx, type, "d");

        Assert.Equal(["a", "c"], await TitlesBy(ctx, "Estimate=3"));
        Assert.Equal(["a", "c"], await TitlesBy(ctx, "estimate=003"));
        // Множественное поле — «содержит значение»; значение enum — по названию (любой регистр).
        Assert.Equal(["a", "b"], await TitlesBy(ctx, "Tags=api"));
        Assert.Equal(["a", "c"], await TitlesBy(ctx, "Level=high"));
        Assert.Equal(["a"], await TitlesBy(ctx, "Due=2026-10-02"));
        Assert.Equal(["a"], await TitlesBy(ctx, "Ratio=0.50"));
        // Повтор — И, в том числе у одного поля с несколькими значениями.
        Assert.Equal(["a"], await TitlesBy(ctx, "Estimate=3", "Level=High", "Tags=ui"));
        Assert.Equal(["a"], await TitlesBy(ctx, "Tags=ui", "Tags=api"));
        Assert.Empty(await TitlesBy(ctx, "Estimate=5", "Level=High"));
        Assert.Empty(await TitlesBy(ctx, "Estimate=4"));
        Assert.Equal(4, (await ListBy(ctx)).TotalCount);

        // Страницы: totalCount — всех подходящих, а не страницы.
        var first = await ListBy(ctx, 0, 1, "Estimate=3");
        Assert.Equal((2, 1), (first.TotalCount, first.Data.Length));
        var second = await ListBy(ctx, 1, 1, "Estimate=3");
        Assert.Equal((2, 1), (second.TotalCount, second.Data.Length));
        Assert.Equal(["a", "c"], first.Data.Concat(second.Data).Select(x => x.Title).ToArray());

        // Правка и снятие значения сразу видны в фильтре.
        var b = (await ListBy(ctx, 0, 50, "Estimate=5")).Data.Single();
        await Edit(ctx, b, Set(estimate, "3"));
        Assert.Equal(["a", "b", "c"], await TitlesBy(ctx, "Estimate=3"));
        await Edit(ctx, await Reload(ctx, b), Set(estimate));
        Assert.Equal(["a", "c"], await TitlesBy(ctx, "Estimate=3"));
        var doomed = (await ListBy(ctx, 0, 50, "Estimate=3")).Data[0];
        Assert.True(await With(s => s.Tasks.Delete(ctx.Project, doomed.Id, doomed.Version)));
        Assert.Single(await TitlesBy(ctx, "Estimate=3"));
    }

    [Fact]
    public async Task A_field_filter_covers_extra_catalog_fields_and_own_fields_of_the_same_name_and_type_and_bad_filters_are_rejected()
    {
        var ctx = await NewProject();
        var note = await NewField(ctx, "Note");
        var type = await NewType(ctx, "Plain");
        var enumeration = await NewEnum(ctx);
        var level = await NewField(ctx, "Level", FieldType.Enum, enumId: enumeration.Id);

        await NewTask(ctx, type, "extra", Set(note, "x=y"));
        await NewTask(ctx, type, "own", new TaskFieldChanges { NewOwnFields = [new NewOwnField("Note", FieldType.String, ["x=y"])] });

        // Дополнительное поле каталога и собственное поле с тем же именем и типом подходят; значение — всё после первого «=».
        Assert.Equal(["extra", "own"], await TitlesBy(ctx, "Note=x=y"));
        Assert.Equal(["extra", "own"], await TitlesBy(ctx, "NOTE=x=y"));

        Assert.Contains("expected 'Name=value'", (await Invalid(ListBy(ctx, 0, 50, "Note"))).Message);
        Assert.Contains("no field 'Nope'", (await Invalid(ListBy(ctx, 0, 50, "Nope=1"))).Message);
        Assert.Contains("is not a value of enum", (await Invalid(ListBy(ctx, 0, 50, "Level=Huge"))).Message);
        Assert.Contains("must not be empty", (await Invalid(ListBy(ctx, 0, 50, "Note="))).Message);
        var number = await NewField(ctx, "Number", FieldType.Int);
        Assert.Contains("is not an integer", (await Invalid(ListBy(ctx, 0, 50, "Number=abc"))).Message);
    }

    [Fact]
    public async Task Comparisons_work_for_int_float_and_date_alone_and_as_a_range_of_two_conditions()
    {
        var ctx = await NewProject();
        var estimate = await NewField(ctx, "Estimate", FieldType.Int);
        var ratio = await NewField(ctx, "Ratio", FieldType.Float);
        var due = await NewField(ctx, "Due", FieldType.Date);
        var scores = await NewField(ctx, "Scores", FieldType.Int, multiple: true);
        var type = await NewType(ctx, "Bug", new TaskTypeField(estimate.Id, false));

        await NewTask(ctx, type, "a", Set((estimate, ["-5"]), (ratio, ["0.1"]), (due, ["2026-01-09"]), (scores, ["1", "20"])));
        await NewTask(ctx, type, "b", Set((estimate, ["3"]), (ratio, ["0.25"]), (due, ["2026-10-02"]), (scores, ["5"])));
        await NewTask(ctx, type, "c", Set((estimate, ["10"]), (ratio, ["1e2"]), (due, ["2027-03-01"])));
        await NewTask(ctx, type, "d");

        // Числа сравниваются как числа (10 > 3, а не как тексты), в том числе отрицательные.
        Assert.Equal(["c"], await TitlesBy(ctx, "Estimate>3"));
        Assert.Equal(["b", "c"], await TitlesBy(ctx, "Estimate>=3"));
        Assert.Equal(["a"], await TitlesBy(ctx, "Estimate<3"));
        Assert.Equal(["a", "b"], await TitlesBy(ctx, "Estimate<=3"));
        Assert.Equal(["a"], await TitlesBy(ctx, "Estimate<0"));
        Assert.Equal(["b"], await TitlesBy(ctx, "Estimate>=003", "Estimate<=0003"));
        // Диапазон — два условия по И, у одного поля.
        Assert.Equal(["b"], await TitlesBy(ctx, "Estimate>=3", "Estimate<=8"));
        Assert.Empty(await TitlesBy(ctx, "Estimate>5", "Estimate<5"));

        // float: дробные значения, порядок по числу (1e2 = 100), канонический вид значения в условии.
        Assert.Equal(["b", "c"], await TitlesBy(ctx, "Ratio>0.2"));
        Assert.Equal(["a", "b"], await TitlesBy(ctx, "Ratio<1"));
        Assert.Equal(["b"], await TitlesBy(ctx, "Ratio>=0.250", "Ratio<=0.25"));
        Assert.Equal(["c"], await TitlesBy(ctx, "Ratio=100"));

        // date: по тексту yyyy-MM-dd.
        Assert.Equal(["b", "c"], await TitlesBy(ctx, "Due>2026-01-09"));
        Assert.Equal(["a", "b"], await TitlesBy(ctx, "Due<2027-01-01"));
        Assert.Equal(["a", "b"], await TitlesBy(ctx, "Due>=2026-01-09", "Due<=2026-10-02"));
        Assert.Equal(["a"], await TitlesBy(ctx, "Due<2026-02-01"));

        // Несколько значений: «хотя бы одно подходит»; два условия по одному полю могут выполнять разные значения.
        Assert.Equal(["a"], await TitlesBy(ctx, "Scores>10"));
        Assert.Equal(["a", "b"], await TitlesBy(ctx, "Scores>=5"));
        Assert.Equal(["a"], await TitlesBy(ctx, "Scores<2", "Scores>15"));

        // Сравнение сочетается с остальными условиями и с равенством.
        Assert.Equal(["b"], await TitlesBy(ctx, "Estimate>=3", "Due<2027-01-01", "Scores=5"));
        Assert.Equal(["a", "b", "c", "d"], await TitlesBy(ctx));
    }

    [Fact]
    public async Task Comparisons_are_refused_for_string_enum_and_bool_and_bad_operands_are_rejected()
    {
        var ctx = await NewProject();
        var levels = await NewEnum(ctx, "Levels", "Low", "High");
        await NewField(ctx, "Note");
        await NewField(ctx, "Level", FieldType.Enum, enumId: levels.Id);
        await NewField(ctx, "Flag", FieldType.Bool);
        await NewField(ctx, "Estimate", FieldType.Int);
        await NewField(ctx, "Due", FieldType.Date);

        Assert.Contains("'>' does not apply to field 'Note' of type string", (await Invalid(ListBy(ctx, 0, 50, "Note>a"))).Message);
        Assert.Contains("of type enum", (await Invalid(ListBy(ctx, 0, 50, "Level>=High"))).Message);
        Assert.Contains("of type bool", (await Invalid(ListBy(ctx, 0, 50, "Flag<true"))).Message);
        Assert.Contains("is not an integer", (await Invalid(ListBy(ctx, 0, 50, "Estimate>2.5"))).Message);
        Assert.Contains("is not an integer", (await Invalid(ListBy(ctx, 0, 50, "Estimate>=x"))).Message);
        Assert.Contains("yyyy-MM-dd", (await Invalid(ListBy(ctx, 0, 50, "Due<tomorrow"))).Message);
        Assert.Contains("must not be empty", (await Invalid(ListBy(ctx, 0, 50, "Estimate>"))).Message);
        Assert.Contains("is not a value of enum", (await Invalid(ListBy(ctx, 0, 50, "Level!=Huge"))).Message);
        Assert.Contains("unknown ':soon'", (await Invalid(ListBy(ctx, 0, 50, "Estimate:soon"))).Message);
        Assert.Contains("expected 'Name=value'", (await Invalid(ListBy(ctx, 0, 50, ">=3"))).Message);
        Assert.Contains("expected one of", (await Invalid(ListBy(ctx, 0, 50, "Estimate!3"))).Message);
        Assert.Contains("no field 'Nope'", (await Invalid(ListBy(ctx, 0, 50, "Nope:set"))).Message);

        // Равенство и неравенство годятся любому типу, ключевые слова нечувствительны к регистру.
        await ListBy(ctx, 0, 50, "Note!=a", "Level!=low", "Flag=TRUE", "Estimate:SET", "Due:Unset");
    }

    [Fact]
    public async Task Not_equal_means_no_value_equals_it_and_includes_tasks_without_a_value_and_complements_equal()
    {
        var ctx = await NewProject();
        var levels = await NewEnum(ctx, "Levels", "Low", "High");
        var estimate = await NewField(ctx, "Estimate", FieldType.Int);
        var tags = await NewField(ctx, "Tags", FieldType.String, multiple: true);
        var level = await NewField(ctx, "Level", FieldType.Enum, enumId: levels.Id);
        var type = await NewType(ctx, "Bug", new TaskTypeField(estimate.Id, false), new TaskTypeField(tags.Id, false));
        var plain = await NewType(ctx, "Plain");

        await NewTask(ctx, type, "a", Set((estimate, ["3"]), (tags, ["ui", "api"]), (level, ["High"])));
        await NewTask(ctx, type, "b", Set((estimate, ["5"]), (tags, ["api"])));
        await NewTask(ctx, type, "empty");
        await NewTask(ctx, plain, "detached");

        // Задачи без значения и без подключённого поля подходят под «!=».
        Assert.Equal(["b", "detached", "empty"], await TitlesBy(ctx, "Estimate!=3"));
        Assert.Equal(["a"], await TitlesBy(ctx, "Estimate=3"));
        // Множественное поле: «ни одно значение не равно» — у «a» есть api, поэтому она не подходит.
        Assert.Equal(["detached", "empty"], await TitlesBy(ctx, "Tags!=api"));
        Assert.Equal(["b", "detached", "empty"], await TitlesBy(ctx, "Tags!=ui"));
        Assert.Equal(["b", "detached", "empty"], await TitlesBy(ctx, "Level!=high"));
        // «=» и «!=» вместе дают все задачи и не пересекаются.
        Assert.Equal(4, (await TitlesBy(ctx, "Estimate=3")).Length + (await TitlesBy(ctx, "Estimate!=3")).Length);
        Assert.Empty(await TitlesBy(ctx, "Estimate=3", "Estimate!=3"));
        // «!=» складывается с другими условиями по И, в том числе по тому же полю.
        Assert.Equal(["b"], await TitlesBy(ctx, "Estimate!=3", "Estimate>=5"));
        Assert.Equal(["b"], await TitlesBy(ctx, "Tags!=ui", "Tags=api"));
        Assert.Equal(["empty"], await TitlesBy(ctx, "Estimate!=3", "Tags:unset", "Estimate:attached"));
    }

    [Fact]
    public async Task Presence_predicates_tell_values_from_connection_and_follow_task_type_and_field_changes()
    {
        var ctx = await NewProject();
        var estimate = await NewField(ctx, "Estimate", FieldType.Int);
        var note = await NewField(ctx, "Note");
        await NewField(ctx, "Tags", FieldType.String, multiple: true);
        var withEstimate = await NewType(ctx, "WithEstimate", new TaskTypeField(estimate.Id, false));
        var plain = await NewType(ctx, "Plain");

        await NewTask(ctx, withEstimate, "valued", Set(estimate, "3"));
        await NewTask(ctx, withEstimate, "typed-empty");
        await NewTask(ctx, plain, "none");
        await NewTask(ctx, plain, "extra-valued", Set(estimate, "7"));
        await NewTask(ctx, plain, "extra-empty", new TaskFieldChanges { AddFields = [estimate.Id] });
        await NewTask(ctx, plain, "own", new TaskFieldChanges { NewOwnFields = [new NewOwnField("Estimate", FieldType.Int, ["1"])] });

        // :set — есть значение, :unset — обратное (в том числе когда поле не подключено).
        Assert.Equal(["extra-valued", "own", "valued"], await TitlesBy(ctx, "Estimate:set"));
        Assert.Equal(["extra-empty", "none", "typed-empty"], await TitlesBy(ctx, "Estimate:unset"));
        // :attached — поле в типе или добавлено задаче (даже без значения) или собственное поле с тем же именем и типом.
        Assert.Equal(["extra-empty", "extra-valued", "own", "typed-empty", "valued"], await TitlesBy(ctx, "Estimate:attached"));
        Assert.Equal(["none"], await TitlesBy(ctx, "Estimate:detached"));
        // «Подключено, но не заполнено».
        Assert.Equal(["extra-empty", "typed-empty"], await TitlesBy(ctx, "Estimate:attached", "Estimate:unset"));
        Assert.Equal(["extra-valued", "own", "valued"], await TitlesBy(ctx, "Estimate:set", "Estimate:attached"));
        Assert.Empty(await TitlesBy(ctx, "Estimate:set", "Estimate:detached"));
        // Поле, не подключённое ни к одному типу и ни к одной задаче.
        Assert.Empty(await TitlesBy(ctx, "Note:attached"));
        Assert.Equal(6, (await TitlesBy(ctx, "Note:detached")).Length);
        Assert.Equal(6, (await TitlesBy(ctx, "Tags:unset")).Length);

        // Смена типа: поле нового типа подключено, поле прежнего типа со значением остаётся дополнительным, без значения — отпадает.
        var typed = (await ListBy(ctx, 0, 50, "Estimate:attached", "Estimate:unset")).Data.Single(x => x.Title == "typed-empty");
        await Edit(ctx, typed, typeId: plain.Id);
        Assert.Equal(["extra-empty"], await TitlesBy(ctx, "Estimate:attached", "Estimate:unset"));
        var valued = (await ListBy(ctx, 0, 50, "Estimate=3")).Data.Single();
        await Edit(ctx, valued, typeId: plain.Id);
        Assert.Equal(["extra-valued", "own", "valued"], await TitlesBy(ctx, "Estimate:attached", "Estimate:set"));
        Assert.Contains("typed-empty", await TitlesBy(ctx, "Estimate:detached"));
        var none = (await ListBy(ctx, 0, 50, "Estimate:detached")).Data.Single(x => x.Title == "none");
        await Edit(ctx, none, typeId: withEstimate.Id);
        Assert.Contains("none", await TitlesBy(ctx, "Estimate:attached", "Estimate:unset"));
        Assert.DoesNotContain("none", await TitlesBy(ctx, "Estimate:detached"));

        // Поле добавлено типу — оно подключено ко всем его задачам; убрано (значения оставлены) — подключено только там, где есть запись.
        var current = (await With(s => s.TypeStorage.GetById(ctx.Project, plain.Id)))!;
        await With(s => s.Types.Update(ctx.Project, plain.Id, new UpdateTaskType(null, null, current.Version, [new TaskTypeField(note.Id, false)])));
        Assert.Equal(
            (await ListBy(ctx)).Data.Where(x => x.TypeId == plain.Id).Select(x => x.Title).Order().ToArray(),
            await TitlesBy(ctx, "Note:attached"));
        current = (await With(s => s.TypeStorage.GetById(ctx.Project, plain.Id)))!;
        await With(s => s.Types.Update(ctx.Project, plain.Id, new UpdateTaskType(null, null, current.Version, [], RemovedFieldValues.Keep)));
        Assert.Empty(await TitlesBy(ctx, "Note:attached"));

        // Убрать дополнительное поле у задачи — оно больше не подключено.
        var extra = (await ListBy(ctx, 0, 50, "Estimate:set")).Data.Single(x => x.Title == "extra-valued");
        await Edit(ctx, extra, new TaskFieldChanges { RemoveFields = [estimate.Id] });
        Assert.DoesNotContain("extra-valued", await TitlesBy(ctx, "Estimate:attached"));
        Assert.Contains("extra-valued", await TitlesBy(ctx, "Estimate:detached"));
    }

    private static TaskFieldChanges Own(params NewOwnField[] fields) => new() { NewOwnFields = fields };

    [Fact]
    public async Task A_filter_by_a_catalog_field_name_also_finds_own_fields_of_the_same_name_and_type_and_skips_other_types()
    {
        var ctx = await NewProject();
        var estimate = await NewField(ctx, "Estimate", FieldType.Int);
        var plain = await NewType(ctx, "Plain");
        var withEstimate = await NewType(ctx, "WithEstimate", new TaskTypeField(estimate.Id, false));

        await NewTask(ctx, withEstimate, "catalog3", Set(estimate, "3"));
        await NewTask(ctx, withEstimate, "catalog-empty");
        await NewTask(ctx, plain, "own10", Own(new NewOwnField("estimate", FieldType.Int, ["10"])));
        await NewTask(ctx, plain, "own3", Own(new NewOwnField("ESTIMATE", FieldType.Int, ["3"])));
        await NewTask(ctx, plain, "own-empty", Own(new NewOwnField("Estimate", FieldType.Int)));
        await NewTask(ctx, plain, "own-string", Own(new NewOwnField("Estimate", FieldType.String, ["3"])));
        await NewTask(ctx, plain, "none");

        // Значение разбирается по типу поля каталога (int); собственное поле другого типа (string) пропускается — даже со значением «3».
        Assert.Equal(["catalog3", "own3"], await TitlesBy(ctx, "Estimate=3"));
        Assert.Equal(["catalog-empty", "none", "own-empty", "own-string", "own10"], await TitlesBy(ctx, "Estimate!=3"));
        Assert.Equal(["own10"], await TitlesBy(ctx, "Estimate>3"));
        Assert.Equal(["catalog3", "own10", "own3"], await TitlesBy(ctx, "Estimate>=3"));
        Assert.Equal(["catalog3", "own3"], await TitlesBy(ctx, "estimate>=3", "ESTIMATE<=3"));
        Assert.Equal(["catalog3", "own10", "own3"], await TitlesBy(ctx, "Estimate:set"));
        Assert.Equal(["catalog-empty", "none", "own-empty", "own-string"], await TitlesBy(ctx, "Estimate:unset"));
        // :attached — поле в типе, добавлено или собственное поле того же типа; «подключено, но не заполнено» работает и для собственных.
        Assert.Equal(["catalog-empty", "catalog3", "own-empty", "own10", "own3"], await TitlesBy(ctx, "Estimate:attached"));
        Assert.Equal(["none", "own-string"], await TitlesBy(ctx, "Estimate:detached"));
        Assert.Equal(["catalog-empty", "own-empty"], await TitlesBy(ctx, "Estimate:attached", "Estimate:unset"));
        Assert.Contains("is not an integer", (await Invalid(ListBy(ctx, 0, 50, "Estimate=abc"))).Message);
    }

    [Fact]
    public async Task A_filter_by_an_own_field_name_without_a_catalog_field_reads_the_value_by_its_type_and_refuses_several_types()
    {
        var ctx = await NewProject();
        var enumeration = await NewEnum(ctx, "Levels", "Low", "High");
        var plain = await NewType(ctx, "Plain");

        await NewTask(ctx, plain, "h1", Own(new NewOwnField("Hours", FieldType.Float, ["1.5", "8"], Multiple: true), new NewOwnField("Mood", FieldType.Enum, ["High"], EnumId: enumeration.Id)));
        await NewTask(ctx, plain, "h2", Own(new NewOwnField("hours", FieldType.Float, ["2"]), new NewOwnField("Done", FieldType.Bool, ["true"])));
        await NewTask(ctx, plain, "h3", Own(new NewOwnField("Hours", FieldType.Float)));
        await NewTask(ctx, plain, "x", Own(new NewOwnField("Code", FieldType.Int, ["5"])));
        await NewTask(ctx, plain, "y", Own(new NewOwnField("Code", FieldType.String, ["abc"])));
        await NewTask(ctx, plain, "z");

        // Один тип у всех собственных полей: значение читается по нему, регистр имени не важен, значений у задачи может быть несколько.
        Assert.Equal(["h1"], await TitlesBy(ctx, "Hours=1.50"));
        Assert.Equal(["h1", "h2"], await TitlesBy(ctx, "HOURS>=2"));
        Assert.Equal(["h1"], await TitlesBy(ctx, "Hours>5"));
        Assert.Equal(["h1"], await TitlesBy(ctx, "Hours<2", "Hours>7"));
        Assert.Equal(["h2", "h3", "x", "y", "z"], await TitlesBy(ctx, "Hours!=1.5"));
        Assert.Equal(["h1", "h2"], await TitlesBy(ctx, "Hours:set"));
        Assert.Equal(["h3", "x", "y", "z"], await TitlesBy(ctx, "Hours:unset"));
        Assert.Equal(["h1", "h2", "h3"], await TitlesBy(ctx, "Hours:attached"));
        Assert.Equal(["x", "y", "z"], await TitlesBy(ctx, "Hours:detached"));
        Assert.Equal(["h3"], await TitlesBy(ctx, "Hours:attached", "Hours:unset"));
        Assert.Equal(["h2"], await TitlesBy(ctx, "Done=TRUE"));
        Assert.Equal(["h1"], await TitlesBy(ctx, "Mood=high"));
        Assert.Contains("is not a number", (await Invalid(ListBy(ctx, 0, 50, "Hours=abc"))).Message);
        Assert.Contains("does not apply", (await Invalid(ListBy(ctx, 0, 50, "Done>true"))).Message);

        // Нет в каталоге и разные типы: ошибка со списком типов; нет ни там, ни там — прежняя ошибка.
        var several = (await Invalid(ListBy(ctx, 0, 50, "Code=5"))).Message;
        Assert.Contains("several types (int, string)", several);
        Assert.Contains("no field 'Nothing'", (await Invalid(ListBy(ctx, 0, 50, "Nothing=1"))).Message);

        // Когда поле заведено в каталоге, значение читается по его типу, а собственные поля другого типа пропускаются.
        await NewField(ctx, "Code", FieldType.String);
        Assert.Equal(["y"], await TitlesBy(ctx, "Code=abc"));
        Assert.Equal(["y"], await TitlesBy(ctx, "Code:attached"));
    }

    [Fact]
    public async Task Own_field_values_follow_the_task_when_it_is_edited_and_deleted_and_own_field_names_are_letters_and_digits()
    {
        var ctx = await NewProject();
        var plain = await NewType(ctx, "Plain");
        var task = await NewTask(ctx, plain, "t", Own(new NewOwnField("Hours", FieldType.Int, ["4"])));
        Assert.Equal(["t"], await TitlesBy(ctx, "Hours=4"));

        // Значение правится — старое пропадает из поиска; поле убирают — условие больше не подходит.
        var ownId = (await With(s => s.Tasks.GetFields(ctx.Project, task.Id)))!.Single().FieldId;
        var edited = (await Edit(ctx, task, new TaskFieldChanges { Values = [new TaskFieldValueInput(ownId, ["9"])] }))!;
        Assert.Empty(await TitlesBy(ctx, "Hours=4"));
        Assert.Equal(["t"], await TitlesBy(ctx, "Hours=9"));
        var removed = (await Edit(ctx, edited, new TaskFieldChanges { RemoveFields = [ownId] }))!;
        Assert.Contains("no field 'Hours'", (await Invalid(ListBy(ctx, 0, 50, "Hours=9"))).Message);

        // Имя собственного поля — из букв и цифр, как в каталоге.
        Assert.Contains("letters and digits", (await Invalid(Edit(ctx, removed, Own(new NewOwnField("Story points", FieldType.Int))))).Message);
        Assert.Contains("letters and digits", (await Invalid(Edit(ctx, removed, Own(new NewOwnField("A:B", FieldType.Int))))).Message);

        // Задача удалена — её собственные поля не находятся.
        var again = (await Edit(ctx, removed, Own(new NewOwnField("Hours", FieldType.Int, ["1"]))))!;
        Assert.Equal(["t"], await TitlesBy(ctx, "Hours=1"));
        Assert.True(await With(s => s.Tasks.Delete(ctx.Project, again.Id, again.Version)));
        Assert.Contains("no field 'Hours'", (await Invalid(ListBy(ctx, 0, 50, "Hours=1"))).Message);
    }

    [Fact]
    public async Task Field_names_consist_of_letters_and_digits_and_a_legacy_name_stays_usable()
    {
        var ctx = await NewProject();

        foreach (var good in new[] { "Estimate", "Оценка", "Story2", "Ёж" })
            await NewField(ctx, good);
        foreach (var bad in new[] { "Story points", "Story_points", "Story-points", "A=B", "A!", "A<B", "A>B", "A:B", "A.B", "A,B", "Оценка!" })
            Assert.Contains("letters and digits", (await Invalid(NewField(ctx, bad))).Message);
        // Пробелы по краям обрезаются, как у любого имени.
        Assert.Equal("Padded", (await NewField(ctx, "  Padded ")).Name);

        // Переименование проверяется так же.
        var field = await NewField(ctx, "Renamable");
        Assert.Contains("letters and digits", (await Invalid(With(s => s.Fields.Update(ctx.Project, field.Id, new UpdateField("Re named", field.Version))))).Message);
        var renamed = (await With(s => s.Fields.Update(ctx.Project, field.Id, new UpdateField("Новое", field.Version))))!;
        Assert.Equal("Новое", renamed.Name);

        // Поле, созданное раньше правила (напрямую в хранилище), читается, правится без смены имени и фильтруется по имени без знаков условий.
        var legacy = new FieldDefinition
        {
            Id = Guid.NewGuid(), ProjectId = ctx.Project, Name = "Story points", Type = FieldType.Int, Version = ""
        };
        legacy = legacy with { Version = await Run(scope => scope.Resolve<IFieldStorage>().Add(legacy)) };
        var type = await NewType(ctx, "Old", new TaskTypeField(legacy.Id, false));
        await NewTask(ctx, type, "five", Set(legacy, "5"));
        await NewTask(ctx, type, "two", Set(legacy, "2"));
        Assert.Equal(["five"], await TitlesBy(ctx, "Story points>=3"));
        Assert.Equal(["two"], await TitlesBy(ctx, "story points!=5", "Story points:set"));

        var same = (await With(s => s.Fields.Update(ctx.Project, legacy.Id, new UpdateField("Story points", legacy.Version, FieldType.Float))))!;
        Assert.Equal(("Story points", FieldType.Float), (same.Name, same.Type));
        Assert.Contains("letters and digits", (await Invalid(With(s => s.Fields.Update(ctx.Project, legacy.Id, new UpdateField("Story  points", same.Version))))).Message);
    }

    // ---- правка определения поля: тип, множественность, перечисление ----

    private Task<FieldDefinition?> Change(Ctx ctx, FieldDefinition field, FieldType? type = null, bool? multiple = null, Guid? enumId = null,
        FieldChangeChoice? choice = null, string? version = null) =>
        With(s => s.Fields.Update(ctx.Project, field.Id, new UpdateField(null, version ?? field.Version, type, multiple, enumId, choice)));

    private async Task<FieldDefinition> Current(Ctx ctx, FieldDefinition field) =>
        (await With(s => s.Fields.GetById(ctx.Project, field.Id)))!;

    private async Task<string[]> ValuesOf(Ctx ctx, TaskItem task) =>
        (await Reload(ctx, task)).Fields.SingleOrDefault()?.Values.ToArray() ?? [];

    [Fact]
    public async Task An_int_field_becomes_float_and_any_field_becomes_string_keeping_the_values_readable()
    {
        var ctx = await NewProject();
        var count = await NewField(ctx, "Count", FieldType.Int, multiple: true);
        var type = await NewType(ctx, "Bug", new TaskTypeField(count.Id, false));
        var task = await NewTask(ctx, type, "A", Set(count, "7", "-2"));

        var asFloat = (await Change(ctx, count, FieldType.Float))!;
        Assert.Equal((FieldType.Float, true), (asFloat.Type, asFloat.Multiple));
        Assert.Equal(["7", "-2"], await ValuesOf(ctx, task));

        var asString = (await Change(ctx, asFloat, FieldType.String))!;
        Assert.Equal(FieldType.String, (await Current(ctx, count)).Type);
        Assert.Equal(asString.Version, (await Current(ctx, count)).Version);
        Assert.Equal(["7", "-2"], await ValuesOf(ctx, task));
        Assert.Equal(["7", "-2"], (await With(s => s.Tasks.GetFields(ctx.Project, task.Id)))!.Single().Texts.ToArray());
    }

    [Fact]
    public async Task A_type_change_that_would_lose_data_is_refused_and_has_to_go_through_string()
    {
        var ctx = await NewProject();
        var weight = await NewField(ctx, "Weight", FieldType.Float);
        var flag = await NewField(ctx, "Flag", FieldType.Bool);

        Assert.Contains("string first", (await Invalid(Change(ctx, weight, FieldType.Int))).Message);
        Assert.Contains("string first", (await Invalid(Change(ctx, flag, FieldType.Date))).Message);
        Assert.Contains("string first", (await Invalid(Change(ctx, weight, FieldType.Enum, enumId: (await NewEnum(ctx)).Id))).Message);
        Assert.Equal(weight.Version, (await Current(ctx, weight)).Version);
    }

    [Fact]
    public async Task A_string_field_becomes_int_when_all_values_parse_and_equal_values_merge()
    {
        var ctx = await NewProject();
        var field = await NewField(ctx, "Code", multiple: true);
        var type = await NewType(ctx, "Bug", new TaskTypeField(field.Id, false));
        var task = await NewTask(ctx, type, "A", Set(field, "007", "7", "12"));
        var other = await NewTask(ctx, type, "B", Set(field, "3"));
        await NewTask(ctx, type, "No value");

        var changed = (await Change(ctx, field, FieldType.Int))!;
        Assert.Equal(FieldType.Int, changed.Type);
        Assert.Equal(["7", "12"], await ValuesOf(ctx, task));
        Assert.Equal(["3"], await ValuesOf(ctx, other));
    }

    [Fact]
    public async Task Values_that_do_not_parse_need_an_explicit_choice_and_nothing_changes_without_it()
    {
        var ctx = await NewProject();
        var field = await NewField(ctx, "Code", multiple: true);
        var type = await NewType(ctx, "Bug", new TaskTypeField(field.Id, true));
        var mixed = await NewTask(ctx, type, "Mixed", Set(field, "5", "abc"));
        var bad = await NewTask(ctx, type, "Bad", Set(field, "1.5"));
        var good = await NewTask(ctx, type, "Good", Set(field, "9"));

        var error = await Conflict(Change(ctx, field, FieldType.Int));
        Assert.Equal(ConflictCode.InUse, error.Code);
        Assert.Contains("2 task(s)", error.Message);
        Assert.Contains("'abc'", error.Message);
        Assert.Contains("'1.5'", error.Message);
        Assert.Equal(field.Version, (await Current(ctx, field)).Version);
        Assert.Equal(FieldType.String, (await Current(ctx, field)).Type);
        Assert.Equal(["5", "abc"], await ValuesOf(ctx, mixed));

        var changed = (await Change(ctx, field, FieldType.Int, choice: new FieldChangeChoice(ClearUnconvertible: true)))!;
        Assert.Equal(FieldType.Int, changed.Type);
        Assert.Equal(["5"], await ValuesOf(ctx, mixed));
        // Обязательное поле осталось пустым: это не правка пользователем; поле типа без значения не записывается.
        Assert.Empty((await Reload(ctx, bad)).Fields);
        Assert.Equal(["9"], await ValuesOf(ctx, good));
    }

    [Fact]
    public async Task A_choice_is_not_needed_when_no_task_is_affected_and_an_extra_field_stays_when_emptied()
    {
        var ctx = await NewProject();
        var field = await NewField(ctx, "Code");
        var type = await NewType(ctx, "Bug");
        // Поле добавлено задаче без значений, у другой — дополнительное со значением, которое не разбирается.
        var added = await NewTask(ctx, type, "Added", new TaskFieldChanges { AddFields = [field.Id] });
        var extra = await NewTask(ctx, type, "Extra", Set(field, "x"));

        Assert.Contains("1 task(s)", (await Conflict(Change(ctx, field, FieldType.Date))).Message);
        await Change(ctx, field, FieldType.Date, choice: new FieldChangeChoice(ClearUnconvertible: true));

        Assert.Equal(field.Id, (await Reload(ctx, added)).Fields.Single().FieldId);
        var kept = (await Reload(ctx, extra)).Fields.Single();
        Assert.Equal(field.Id, kept.FieldId);
        Assert.Empty(kept.Values);
        Assert.Equal(FieldType.Date, (await Current(ctx, field)).Type);

        var unused = await NewField(ctx, "Unused");
        Assert.Equal(FieldType.Bool, (await Change(ctx, unused, FieldType.Bool))!.Type);
    }

    [Fact]
    public async Task A_string_field_becomes_enum_matching_values_by_name_or_by_an_explicit_mapping()
    {
        var ctx = await NewProject();
        var priority = await NewEnum(ctx);
        var field = await NewField(ctx, "Level", multiple: true);
        var type = await NewType(ctx, "Bug", new TaskTypeField(field.Id, false));
        var task = await NewTask(ctx, type, "A", Set(field, "high", "Urgent"));

        var error = await Conflict(Change(ctx, field, FieldType.Enum, enumId: priority.Id));
        Assert.Contains("1 task(s)", error.Message);
        Assert.Contains("'Urgent'", error.Message);

        Assert.Contains("is not a value", (await Invalid(Change(ctx, field, FieldType.Enum, enumId: priority.Id,
            choice: new FieldChangeChoice(Mapping: [new FieldValueMapping("Urgent", "Nope")])))).Message);
        Assert.Contains("only when the new type is enum", (await Invalid(Change(ctx, field, FieldType.String,
            choice: new FieldChangeChoice(Mapping: [new FieldValueMapping("Urgent", "High")])))).Message);

        var changed = (await Change(ctx, field, FieldType.Enum, enumId: priority.Id,
            choice: new FieldChangeChoice(Mapping: [new FieldValueMapping("Urgent", "Medium")])))!;
        Assert.Equal((FieldType.Enum, priority.Id), (changed.Type, changed.EnumId));
        Assert.Equal([priority.Values[2].Id.ToString("D"), priority.Values[1].Id.ToString("D")], await ValuesOf(ctx, task));
    }

    [Fact]
    public async Task An_enum_field_moves_to_another_enum_by_name_by_mapping_or_clearing_and_becomes_string_with_names()
    {
        var ctx = await NewProject();
        var priority = await NewEnum(ctx, "Priority", "Low", "Medium", "High");
        var severity = await NewEnum(ctx, "Severity", "low", "Critical");
        var field = await NewField(ctx, "Level", FieldType.Enum, multiple: true, enumId: priority.Id);
        var type = await NewType(ctx, "Bug", new TaskTypeField(field.Id, false));
        var task = await NewTask(ctx, type, "A", Set(field, "Low", "High"));
        var lowOnly = await NewTask(ctx, type, "B", Set(field, "Low"));
        var (lowNew, criticalNew) = (severity.Values[0].Id.ToString("D"), severity.Values[1].Id.ToString("D"));

        Assert.Contains("'High'", (await Conflict(Change(ctx, field, enumId: severity.Id))).Message);
        Assert.Contains("is not a value of enum 'Priority'", (await Invalid(Change(ctx, field, enumId: severity.Id,
            choice: new FieldChangeChoice(Mapping: [new FieldValueMapping("Nope", "Critical")])))).Message);
        Assert.Equal(priority.Id, (await Current(ctx, field)).EnumId);

        var mapped = (await Change(ctx, field, enumId: severity.Id, choice: new FieldChangeChoice(Mapping: [new FieldValueMapping("High", "Critical")])))!;
        Assert.Equal(severity.Id, mapped.EnumId);
        Assert.Equal([lowNew, criticalNew], await ValuesOf(ctx, task));
        Assert.Equal([lowNew], await ValuesOf(ctx, lowOnly));

        // Другое перечисление без соответствия для Critical — очистить.
        var back = (await Change(ctx, mapped, enumId: priority.Id, choice: new FieldChangeChoice(ClearUnconvertible: true)))!;
        Assert.Equal([priority.Values[0].Id.ToString("D")], await ValuesOf(ctx, task));

        var text = (await Change(ctx, back, FieldType.String))!;
        Assert.Equal(FieldType.String, text.Type);
        Assert.Null(text.EnumId);
        Assert.Equal(["Low"], await ValuesOf(ctx, task));
        Assert.Equal(["Low"], await ValuesOf(ctx, lowOnly));
    }

    [Fact]
    public async Task The_enum_of_own_fields_and_the_enum_itself_are_not_touched_by_a_field_change()
    {
        var ctx = await NewProject();
        var priority = await NewEnum(ctx);
        var field = await NewField(ctx, "Level", FieldType.Enum, enumId: priority.Id);
        var plain = await NewType(ctx, "Plain");
        var own = await NewTask(ctx, plain, "Own", new TaskFieldChanges { NewOwnFields = [new NewOwnField("Mood", FieldType.Enum, ["High"], EnumId: priority.Id)] });

        await Change(ctx, field, FieldType.String);

        var entry = (await Reload(ctx, own)).Fields.Single();
        Assert.Equal(priority.Values[2].Id.ToString("D"), entry.Values.Single());
        Assert.Equal(FieldType.Enum, entry.Own!.Type);
        Assert.Equal(priority.Version, (await With(s => s.Enums.GetById(ctx.Project, priority.Id)))!.Version);
    }

    [Fact]
    public async Task Multiplicity_one_to_several_keeps_values_and_several_to_one_needs_a_choice_for_tasks_with_several()
    {
        var ctx = await NewProject();
        var field = await NewField(ctx, "Tags");
        var type = await NewType(ctx, "Bug", new TaskTypeField(field.Id, false));
        var one = await NewTask(ctx, type, "One", Set(field, "a"));

        var multi = (await Change(ctx, field, multiple: true))!;
        Assert.True(multi.Multiple);
        Assert.Equal(["a"], await ValuesOf(ctx, one));

        var two = await NewTask(ctx, type, "Two", Set(field, "b", "c"));
        var error = await Conflict(Change(ctx, multi, multiple: false));
        Assert.Equal(ConflictCode.InUse, error.Code);
        Assert.Contains("1 task(s)", error.Message);
        Assert.True((await Current(ctx, field)).Multiple);
        Assert.Equal(["b", "c"], await ValuesOf(ctx, two));

        var first = (await Change(ctx, multi, multiple: false, choice: new FieldChangeChoice(Several: SeveralValues.KeepFirst)))!;
        Assert.False(first.Multiple);
        Assert.Equal(["b"], await ValuesOf(ctx, two));
        Assert.Equal(["a"], await ValuesOf(ctx, one));

        var again = (await Change(ctx, first, multiple: true))!;
        var three = await NewTask(ctx, type, "Three", Set(field, "x", "y"));
        await Change(ctx, again, multiple: false, choice: new FieldChangeChoice(Several: SeveralValues.Clear));
        Assert.Empty(await ValuesOf(ctx, three));
        Assert.Equal(["b"], await ValuesOf(ctx, two));
    }

    [Fact]
    public async Task Type_and_multiplicity_can_change_together_and_the_converted_values_decide_whether_several_remain()
    {
        var ctx = await NewProject();
        var field = await NewField(ctx, "Code", multiple: true);
        var type = await NewType(ctx, "Bug", new TaskTypeField(field.Id, false));
        var merged = await NewTask(ctx, type, "Merged", Set(field, "07", "7"));
        var split = await NewTask(ctx, type, "Split", Set(field, "1", "2"));

        // «07» и «7» после преобразования — одно значение: у первой задачи несколько значений не остаётся.
        var error = await Conflict(Change(ctx, field, FieldType.Int, multiple: false));
        Assert.Contains("1 task(s)", error.Message);
        await Change(ctx, field, FieldType.Int, multiple: false, choice: new FieldChangeChoice(Several: SeveralValues.KeepFirst));
        Assert.Equal(["7"], await ValuesOf(ctx, merged));
        Assert.Equal(["1"], await ValuesOf(ctx, split));
    }

    [Fact]
    public async Task A_field_change_is_validated_against_the_enum_and_the_version()
    {
        var ctx = await NewProject();
        var priority = await NewEnum(ctx);
        var field = await NewField(ctx, "Level");
        var enumField = await NewField(ctx, "Prio", FieldType.Enum, enumId: priority.Id);

        Assert.Contains("must refer to an enum", (await Invalid(Change(ctx, field, FieldType.Enum))).Message);
        Assert.Contains("only a field of type enum", (await Invalid(Change(ctx, field, enumId: priority.Id))).Message);
        Assert.Contains("only a field of type enum", (await Invalid(Change(ctx, enumField, FieldType.String, enumId: priority.Id))).Message);
        Assert.Contains("enum not found", (await Invalid(Change(ctx, field, FieldType.Enum, enumId: Guid.NewGuid()))).Message);
        Assert.Contains("unknown", (await Invalid(Change(ctx, field, (FieldType)99))).Message);
        Assert.Contains("Version is required", (await Invalid(With(s => s.Fields.Update(ctx.Project, field.Id, new UpdateField(null, null, FieldType.Int))))).Message);
        Assert.Equal(ConflictCode.Modified, (await Conflict(Change(ctx, field, FieldType.Int, version: "stale"))).Code);
        Assert.Null(await With(s => s.Fields.Update(ctx.Project, Guid.NewGuid(), new UpdateField(null, "v", FieldType.Int))));

        // Тип enum: перечисление прежнее, если не передано другое.
        Assert.Equal(priority.Id, (await Change(ctx, enumField, multiple: true))!.EnumId);
        Assert.Equal(FieldType.String, (await Current(ctx, field)).Type);
    }

    [Fact]
    public async Task A_field_change_that_hits_a_locked_task_changes_nothing_and_a_stale_version_leaves_the_tasks_alone()
    {
        var ctx = await NewProject();
        var field = await NewField(ctx, "Code");
        var type = await NewType(ctx, "Bug", new TaskTypeField(field.Id, false));
        var locked = await NewTask(ctx, type, "Locked", Set(field, "001"));
        var free = await NewTask(ctx, type, "Free", Set(field, "002"));
        var unrelated = await NewTask(ctx, type, "Unrelated");
        await AnnaLocks(ctx, locked);
        await AnnaLocks(ctx, unrelated);

        var error = await Assert.ThrowsAsync<TaskerLockedException>(() => Change(ctx, field, FieldType.Int));
        Assert.Contains("Task 'Locked'", error.Message);
        Assert.DoesNotContain("Unrelated", error.Message);
        Assert.Equal(FieldType.String, (await Current(ctx, field)).Type);
        Assert.Equal(free.Version, (await Reload(ctx, free)).Version);

        await Conflict(Change(ctx, field, FieldType.Int, version: "stale"));
        Assert.Equal(free.Version, (await Reload(ctx, free)).Version);

        await AnnaLocks(ctx, locked, release: true);
        Assert.Equal(FieldType.Int, (await Change(ctx, field, FieldType.Int))!.Type);
        Assert.Equal(["1"], await ValuesOf(ctx, locked));
        Assert.Equal(["2"], await ValuesOf(ctx, free));
    }
}

public sealed class FilesTaskFieldsTests : TaskFieldsContract
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tasker-tests-" + Guid.NewGuid().ToString("N"));
    private IContainer _container = null!;

    public override Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _container = Open();
        return Task.CompletedTask;
    }

    private IContainer Open()
    {
        var builder = new ContainerBuilder();
        builder.RegisterGeneric(typeof(NullLogger<>)).As(typeof(ILogger<>)).SingleInstance();
        builder.RegisterModule(new FileStorageModule(_root));
        return builder.Build();
    }

    public override async Task DisposeAsync()
    {
        await _container.DisposeAsync();
        Directory.Delete(_root, recursive: true);
    }

    protected override Task<T> Run<T>(Func<ILifetimeScope, Task<T>> action) => action(_container);

    private string Folder(Guid project, string name) => Path.Combine(_root, ".tasker", "projects", project.ToString(), name);

    private static string Normalized(string text) => text.ReplaceLineEndings("\n");

    [Fact]
    public async Task Tasks_and_types_keep_their_fields_in_yaml_that_a_fresh_process_reads_and_the_index_finds_by_field_and_enum()
    {
        var project = Guid.NewGuid();
        var type = Guid.NewGuid();
        var field = Guid.NewGuid();
        var own = Guid.NewGuid();
        var enumId = Guid.NewGuid();
        var task = new TaskItem
        {
            Id = Guid.NewGuid(), ProjectId = project, Title = "Fields", TypeId = type, StatusId = Guid.NewGuid(),
            Fields =
            [
                new TaskField(field, ["null", "true", "007", "~", "a: b", "- x", "multi\nline"]),
                new TaskField(own, ["3", "8"], new OwnField("Оценка", FieldType.Int, true, true, null)),
                new TaskField(Guid.NewGuid(), [Guid.NewGuid().ToString()], new OwnField("Mood", FieldType.Enum, false, false, enumId)),
                new TaskField(Guid.NewGuid(), [], new OwnField("Empty", FieldType.String, false, false, null))
            ],
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, Version = ""
        };
        await _container.Resolve<ITaskStorage>().Add(task);
        await _container.Resolve<ITaskTypeStorage>().Add(new TaskType
        {
            Id = type, ProjectId = project, Name = "Баг", StatusSetId = Guid.NewGuid(),
            Fields = [new TaskTypeField(field, true), new TaskTypeField(own, false)], Version = ""
        });

        var typeText = Normalized(await File.ReadAllTextAsync(FileFinder.In(Folder(project, "task-types"), type)));
        Assert.StartsWith($"formatVersion: {Tasker.Storage.Files.Storages.FormatVersions.Current}\n", typeText);
        Assert.Contains($"fields:\n- field: {field}\n  required: true\n- field: {own}\n", typeText);

        var taskText = Normalized(await File.ReadAllTextAsync(Directory.GetFiles(Folder(project, "tasks")).Single()));
        Assert.StartsWith($"formatVersion: {Tasker.Storage.Files.Storages.FormatVersions.Current}\n", taskText);
        Assert.Contains($"fields:\n- id: {field}\n  values:\n", taskText);
        Assert.Contains("  name: Оценка\n  type: int\n  required: true\n  multiple: true\n  values:\n", taskText);
        Assert.Contains($"  enum: {enumId}\n", taskText);

        // Новый процесс (контейнер) знает файлы только с диска: все значения вернулись теми же, в том же порядке, в том же виде.
        await using var fresh = Open();
        var read = (await fresh.Resolve<ITaskStorage>().GetById(project, task.Id))!;
        Assert.Equal(task.Fields.Count, read.Fields.Count);
        for (var i = 0; i < task.Fields.Count; i++)
        {
            Assert.Equal(task.Fields[i].FieldId, read.Fields[i].FieldId);
            Assert.Equal(task.Fields[i].Values.ToArray(), read.Fields[i].Values.ToArray());
            Assert.Equal(task.Fields[i].Own, read.Fields[i].Own);
        }
        Assert.Equal([new TaskTypeField(field, true), new TaskTypeField(own, false)], (await fresh.Resolve<ITaskTypeStorage>().GetById(project, type))!.Fields.ToArray());

        // Индекс знает, у каких задач записано поле и у каких есть собственное поле с перечислением.
        var tasks = fresh.Resolve<ITaskStorage>();
        Assert.Equal(task.Id, (await tasks.GetAll(project, new TaskFilter { FieldIds = [field] })).Single().Id);
        Assert.Equal(task.Id, (await tasks.GetAll(project, new TaskFilter { FieldIds = [Guid.NewGuid(), own] })).Single().Id);
        Assert.Empty(await tasks.GetAll(project, new TaskFilter { FieldIds = [Guid.NewGuid()] }));
        Assert.Equal(1, await tasks.Count(project, new TaskFilter { EnumIds = [enumId] }));
        Assert.Equal(0, await tasks.Count(project, new TaskFilter { EnumIds = [Guid.NewGuid()] }));
        Assert.Empty(await tasks.GetAll(project, new TaskFilter { FieldIds = [] }));
        Assert.Equal(0, await tasks.Count(Guid.NewGuid(), new TaskFilter { FieldIds = [field] }));
    }

    [Fact]
    public async Task Hand_written_fields_are_read_and_a_removed_field_leaves_the_index()
    {
        var project = Guid.NewGuid();
        var type = Guid.NewGuid();
        var field = Guid.NewGuid();
        var id = Guid.NewGuid();
        Directory.CreateDirectory(Folder(project, "tasks"));
        Directory.CreateDirectory(Folder(project, "task-types"));
        await File.WriteAllTextAsync(Path.Combine(Folder(project, "tasks"), $"hand-{id.ToString("N")[..8]}.yaml"),
            $"formatVersion: 4\nid: {id}\ntitle: Hand\ntypeId: {type}\nstatusId: {Guid.NewGuid()}\ncreatedAt: 2026-10-02T10:00:00.0000000+00:00\n" +
            $"updatedAt: 2026-10-02T10:00:00.0000000+00:00\nfields:\n- id: {field}\n  values:\n  - 5\n  - true\n  - '2026-10-02'\n- id: {Guid.NewGuid()}\n  name: Own\n  type: DATE\n");
        await File.WriteAllTextAsync(Path.Combine(Folder(project, "task-types"), type + ".yaml"),
            $"id: {type}\nname: Hand\nstatusSetId: {Guid.NewGuid()}\nfields:\n- field: {field}\n");

        var tasks = _container.Resolve<ITaskStorage>();
        var read = (await tasks.GetAll(project, new TaskFilter { FieldIds = [field] })).Single();
        Assert.Equal(["5", "true", "2026-10-02"], read.Fields[0].Values.ToArray());
        Assert.Equal(new OwnField("Own", FieldType.Date, false, false, null), read.Fields[1].Own);
        Assert.Equal([new TaskTypeField(field, false)], (await _container.Resolve<ITaskTypeStorage>().GetAll(project)).Single().Fields.ToArray());

        File.Delete(Directory.GetFiles(Folder(project, "tasks")).Single());
        await _container.Resolve<Tasker.Storage.Files.Index.WorkspaceIndex>().Rescan();
        Assert.Equal(0, await tasks.Count(project, new TaskFilter { FieldIds = [field] }));
    }

    [InProcess]
    [Fact]
    public async Task Files_of_format_3_are_read_without_fields_and_migrated_to_4_keeping_their_content()
    {
        var project = Guid.NewGuid();
        var type = Guid.NewGuid();
        var set = Guid.NewGuid();
        Directory.CreateDirectory(Folder(project, "task-types"));
        var path = Path.Combine(Folder(project, "task-types"), type + ".yaml");
        await File.WriteAllTextAsync(path, $"formatVersion: 3\nid: {type}\nname: Old\nstatusSetId: {set}\n");

        var read = (await _container.Resolve<ITaskTypeStorage>().GetById(project, type))!;
        Assert.Empty(read.Fields);
        Assert.StartsWith("formatVersion: 3", await File.ReadAllTextAsync(path));

        var report = await _container.Resolve<Tasker.Core.Workspace.IFileMigration>().Run(new Tasker.Core.Workspace.MigrationOptions());
        Assert.Equal(9, report.CurrentFormat);
        Assert.Single(report.Migrated);
        Assert.Equal($"formatVersion: {Tasker.Storage.Files.Storages.FormatVersions.Current}\nid: {type}\nname: Old\nstatusSetId: {set}\n", Normalized(await File.ReadAllTextAsync(FileFinder.In(Folder(project, "task-types"), type))));
    }
}

public sealed class DbTaskFieldsTests : TaskFieldsContract
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tasker-tests-" + Guid.NewGuid().ToString("N"));
    private IContainer _container = null!;

    public override async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        var builder = new ContainerBuilder();
        builder.RegisterModule(new DbStorageModule(new DbConfigs { SqliteFile = Path.Combine(_root, "tasker.db") }));
        _container = builder.Build();
        await _container.Resolve<IStorageLifecycle>().Start(default);
    }

    public override async Task DisposeAsync()
    {
        await _container.DisposeAsync();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_root, recursive: true);
    }

    protected override async Task<T> Run<T>(Func<ILifetimeScope, Task<T>> action)
    {
        await using var scope = _container.BeginLifetimeScope();
        return await action(scope);
    }

    [Fact]
    public async Task The_database_keeps_values_in_order_filters_by_field_and_enum_and_deletes_them_with_the_task_and_the_project()
    {
        var project = Guid.NewGuid();
        await using var scope = _container.BeginLifetimeScope();
        var projects = scope.Resolve<IProjectStorage>();
        var projectVersion = await projects.Add(new Project { Id = project, Name = "P", CreatedAt = DateTimeOffset.UtcNow, Version = "" });
        var statusId = Guid.NewGuid();
        await scope.Resolve<IStatusStorage>().Add(new Status { Id = statusId, ProjectId = project, Name = "S", Color = "#111111", Version = "" });
        var setId = Guid.NewGuid();
        await scope.Resolve<IStatusSetStorage>().Add(new StatusSet { Id = setId, ProjectId = project, Name = "Set", StatusIds = [statusId], Version = "" });
        var enums = scope.Resolve<IFieldEnumStorage>();
        var priority = new FieldEnum { Id = Guid.NewGuid(), ProjectId = project, Name = "P", Values = [new FieldEnumValue(Guid.NewGuid(), "Low")], Version = "" };
        var enumVersion = await enums.Add(priority);
        var field = new FieldDefinition { Id = Guid.NewGuid(), ProjectId = project, Name = "F", Type = FieldType.String, Multiple = true, Version = "" };
        await scope.Resolve<IFieldStorage>().Add(field);
        var typeId = Guid.NewGuid();
        var types = scope.Resolve<ITaskTypeStorage>();
        await types.Add(new TaskType { Id = typeId, ProjectId = project, Name = "T", StatusSetId = setId, Fields = [new TaskTypeField(field.Id, true)], Version = "" });

        var tasks = scope.Resolve<ITaskStorage>();
        var task = new TaskItem
        {
            Id = Guid.NewGuid(), ProjectId = project, Title = "T", TypeId = typeId, StatusId = statusId,
            Fields =
            [
                new TaskField(field.Id, ["c", "a", "b"]),
                new TaskField(Guid.NewGuid(), ["x"], new OwnField("Own", FieldType.Enum, true, false, priority.Id))
            ],
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, Version = ""
        };
        var version = await tasks.Add(task);

        var read = (await tasks.GetById(project, task.Id))!;
        Assert.Equal(["c", "a", "b"], read.Fields[0].Values.ToArray());
        Assert.Equal(task.Fields[1].Own, read.Fields[1].Own);
        Assert.Equal(task.Id, (await tasks.GetAll(project, new TaskFilter { FieldIds = [field.Id] })).Single().Id);
        Assert.Equal(1, await tasks.Count(project, new TaskFilter { EnumIds = [priority.Id] }));
        Assert.Equal(1, (await tasks.GetRange(project, new TaskFilter { FieldIds = [field.Id] }, new Tasker.Core.Dto.Page(0, 10))).TotalCount);
        Assert.Equal(0, await tasks.Count(project, new TaskFilter { FieldIds = [Guid.NewGuid()] }));

        // Перечисление, на которое ссылается собственное поле задачи, база не даёт удалить, как и поле, подключённое к типу.
        await Assert.ThrowsAsync<SqliteException>(() => enums.Delete(project, priority.Id, enumVersion));
        await Assert.ThrowsAsync<SqliteException>(() => scope.Resolve<IFieldStorage>().Delete(project, field.Id, "1"));

        // Правка заменяет поля и значения целиком.
        var updated = task with { Fields = [new TaskField(field.Id, ["z"])], Version = version };
        Assert.NotNull(await tasks.Update(updated, version));
        var again = (await tasks.GetById(project, task.Id))!;
        Assert.Equal(["z"], again.Fields.Single().Values.ToArray());
        Assert.Equal(0, await tasks.Count(project, new TaskFilter { EnumIds = [priority.Id] }));

        // Задача удаляется вместе со значениями, проект — вместе со всем.
        Assert.True(await tasks.Delete(project, task.Id, again.Version));
        Assert.Empty(await tasks.GetAll(project, new TaskFilter { FieldIds = [field.Id] }));
        Assert.True(await projects.Delete(project, projectVersion));
        Assert.Empty(await types.GetAll(project));
    }
}
