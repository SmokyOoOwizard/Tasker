using Tasker.Core.Boards;
using Autofac;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tasker.Core;
using Tasker.Core.Dto;
using Tasker.Core.Fields;
using Tasker.Core.Locks;
using Tasker.Core.Projects;
using Tasker.Core.Tasks;
using Tasker.Core.TaskSeries;
using Tasker.Storage.Db;
using Tasker.Storage.Db.Configs.Tasker;
using Tasker.Storage.Files;
using Tasker.Tests.SeriesCore;
using Xunit;

namespace Tasker.Tests;

/// <summary>
/// Каталог полей и перечислений проекта (сервисы Core поверх настоящих хранилищ) — одно поведение для файлов и БД.
/// Каждый вызов <see cref="Run"/> получает свой скоуп (в БД — свой запрос и контекст), как отдельный запрос сервера.
/// </summary>
public abstract class FieldCatalogContract : IAsyncLifetime
{
    public abstract Task InitializeAsync();
    public abstract Task DisposeAsync();

    protected abstract Task<T> Run<T>(Func<ILifetimeScope, Task<T>> action);

    /// <summary>Кто сейчас правит: у блокировок разные держатели.</summary>
    private readonly FakeEditor _editor = new();

    private async Task<Guid> NewProject()
    {
        var id = Guid.NewGuid();
        await Run(async scope =>
        {
            await scope.Resolve<IProjectStorage>().Add(new Project { Id = id, Name = "P" + id.ToString("N")[..6], CreatedAt = DateTimeOffset.UtcNow, Version = "" });
            return 0;
        });
        return id;
    }

    private sealed record Services(FieldService Fields, FieldEnumService Enums, EditLockService Locks, EntityLockService EntityLocks);

    private Task<T> With<T>(Func<Services, Task<T>> action) => Run(scope =>
    {
        var locks = new EditLockService(scope.Resolve<IEditLockStorage>(), _editor, TimeProvider.System);
        var fields = scope.Resolve<IFieldStorage>();
        var enums = scope.Resolve<IFieldEnumStorage>();
        var writes = scope.Resolve<IWriteScope>();
        var entityLocks = new EntityLockService(locks, _editor, scope.Resolve<IProjectStorage>(), null!, null!, null!, null!, null!, null!, null!, null!, fields, enums);
        var types = scope.Resolve<ITaskTypeStorage>();
        var tasks = scope.Resolve<ITaskStorage>();
        return action(new Services(
            new FieldService(fields, enums, types, tasks, scope.Resolve<IBoardStorage>(), writes, TimeProvider.System, locks),
            new FieldEnumService(enums, fields, types, tasks, scope.Resolve<IBoardStorage>(), writes, TimeProvider.System, locks), locks, entityLocks));
    });

    private Task<FieldEnum> NewEnum(Guid project, string name = "Priority", params string[] values) =>
        With(s => s.Enums.Create(project, new CreateFieldEnum(name, values.Length > 0 ? values : ["Low", "High"])));

    private Task<FieldDefinition> NewField(Guid project, string name, FieldType type = FieldType.String, bool? multiple = null, Guid? enumId = null) =>
        With(s => s.Fields.Create(project, new CreateField(name, type, multiple, enumId)));

    private static async Task<TaskerConflictException> Conflict(Task task) => await Assert.ThrowsAsync<TaskerConflictException>(() => task);
    private static async Task<TaskerValidationException> Invalid(Task task) => await Assert.ThrowsAsync<TaskerValidationException>(() => task);

    // ---- перечисления ----

    [Fact]
    public async Task An_enum_is_created_with_values_that_have_ids_listed_by_name_and_read_back_by_id()
    {
        var project = await NewProject();
        var priority = await NewEnum(project, "Priority", "Low", "Medium", "High");
        await NewEnum(project, "Area", "UI", "API");

        Assert.Equal(["Low", "Medium", "High"], priority.Values.Select(x => x.Name).ToArray());
        Assert.Equal(3, priority.Values.Select(x => x.Id).Distinct().Count());
        Assert.DoesNotContain(Guid.Empty, priority.Values.Select(x => x.Id));
        Assert.False(string.IsNullOrEmpty(priority.Version));

        var all = await With(s => s.Enums.GetAll(project));
        Assert.Equal(["Area", "Priority"], all.Select(x => x.Name).ToArray());
        var page = await With(s => s.Enums.GetRange(project, new Page(1, 1)));
        Assert.Equal((2, 1, "Priority"), (page.TotalCount, page.Data.Length, page.Data[0].Name));

        var read = await With(s => s.Enums.GetById(project, priority.Id));
        Assert.Equal(priority.Values, read!.Values);
        Assert.Equal(priority.Version, read.Version);
        Assert.Same(null, await With(s => s.Enums.GetById(project, Guid.NewGuid())));

        // Перечисление принадлежит своему проекту.
        var other = await NewProject();
        Assert.Empty(await With(s => s.Enums.GetAll(other)));
        Assert.Null(await With(s => s.Enums.GetById(other, priority.Id)));
    }

    [Fact]
    public async Task An_enum_is_found_by_id_or_name_ignoring_case()
    {
        var project = await NewProject();
        var priority = await NewEnum(project, "Priority");

        Assert.Equal(priority.Id, (await With(s => s.Enums.Find(project, "priority")))!.Id);
        Assert.Equal(priority.Id, (await With(s => s.Enums.Find(project, priority.Id.ToString())))!.Id);
        Assert.Null(await With(s => s.Enums.Find(project, "nope")));
    }

    [Fact]
    public async Task Enum_names_are_unique_in_the_project_ignoring_case_but_not_across_projects()
    {
        var project = await NewProject();
        await NewEnum(project, "Priority");

        var error = await Conflict(NewEnum(project, "  priority "));
        Assert.Contains("already exists", error.Message);
        await NewEnum(await NewProject(), "Priority");

        // Переименование в занятое имя — тоже отказ, в своё же (смена регистра) — можно.
        var area = await NewEnum(project, "Area");
        await Conflict(With(s => s.Enums.Update(project, area.Id, new UpdateFieldEnum("PRIORITY", null, area.Version))));
        var renamed = (await With(s => s.Enums.Update(project, area.Id, new UpdateFieldEnum("AREA", null, area.Version))))!.Value;
        Assert.Equal("AREA", renamed!.Name);
    }

    [Fact]
    public async Task An_enum_without_values_or_with_repeated_or_empty_values_is_rejected()
    {
        var project = await NewProject();

        Assert.Contains("at least one", (await Invalid(With(s => s.Enums.Create(project, new CreateFieldEnum("E", []))))).Message);
        Assert.Contains("repeated", (await Invalid(With(s => s.Enums.Create(project, new CreateFieldEnum("E", ["Low", "low"]))))).Message);
        Assert.Contains("required", (await Invalid(With(s => s.Enums.Create(project, new CreateFieldEnum("E", ["Low", " "]))))).Message);
        Assert.Contains("required", (await Invalid(With(s => s.Enums.Create(project, new CreateFieldEnum(" ", ["Low"]))))).Message);
        Assert.Empty(await With(s => s.Enums.GetAll(project)));
    }

    [Fact]
    public async Task Updating_an_enum_renames_values_keeping_their_ids_adds_removes_and_reorders()
    {
        var project = await NewProject();
        var e = await NewEnum(project, "Priority", "Low", "Medium", "High");
        var (low, medium, high) = (e.Values[0], e.Values[1], e.Values[2]);

        var updated = (await With(s => s.Enums.Update(project, e.Id, new UpdateFieldEnum("Prio", [
            new FieldEnumValueInput(high.Id, "Critical"),   // переименовано и переставлено
            new FieldEnumValueInput(low.Id, "Low"),
            new FieldEnumValueInput(null, "Urgent")         // новое; Medium пропало
        ], e.Version))))!.Value;

        Assert.Equal("Prio", updated.Name);
        Assert.Equal(["Critical", "Low", "Urgent"], updated.Values.Select(x => x.Name).ToArray());
        Assert.Equal(high.Id, updated.Values[0].Id);
        Assert.Equal(low.Id, updated.Values[1].Id);
        Assert.DoesNotContain(medium.Id, updated.Values.Select(x => x.Id));
        Assert.DoesNotContain(updated.Values[2].Id, new[] { low.Id, medium.Id, high.Id });
        Assert.NotEqual(e.Version, updated.Version);

        var read = (await With(s => s.Enums.GetById(project, e.Id)))!;
        Assert.Equal(updated.Values, read.Values);
        Assert.Equal(updated.Version, read.Version);

        // Только имя: значения не трогаются.
        var renamedOnly = (await With(s => s.Enums.Update(project, e.Id, new UpdateFieldEnum("P2", null, updated.Version))))!.Value;
        Assert.Equal(updated.Values, renamedOnly.Values);
    }

    [Fact]
    public async Task Updating_an_enum_validates_value_ids_names_and_that_something_remains()
    {
        var project = await NewProject();
        var e = await NewEnum(project, "Priority", "Low", "High");

        Assert.Contains("not found in the enum", (await Invalid(With(s => s.Enums.Update(project, e.Id,
            new UpdateFieldEnum(null, [new FieldEnumValueInput(Guid.NewGuid(), "Ghost")], e.Version))))).Message);
        Assert.Contains("repeated", (await Invalid(With(s => s.Enums.Update(project, e.Id,
            new UpdateFieldEnum(null, [new FieldEnumValueInput(e.Values[0].Id, "A"), new FieldEnumValueInput(e.Values[0].Id, "B")], e.Version))))).Message);
        Assert.Contains("must be unique", (await Invalid(With(s => s.Enums.Update(project, e.Id,
            new UpdateFieldEnum(null, [new FieldEnumValueInput(e.Values[0].Id, "X"), new FieldEnumValueInput(null, "x")], e.Version))))).Message);
        Assert.Contains("at least one", (await Invalid(With(s => s.Enums.Update(project, e.Id, new UpdateFieldEnum(null, [], e.Version))))).Message);

        Assert.Equal(e.Values, (await With(s => s.Enums.GetById(project, e.Id)))!.Values);
        Assert.Null(await With(s => s.Enums.Update(project, Guid.NewGuid(), new UpdateFieldEnum("X", null, "v"))));
    }

    [Fact]
    public async Task Changing_or_deleting_an_enum_requires_the_current_version()
    {
        var project = await NewProject();
        var e = await NewEnum(project);

        var missing = await Invalid(With(s => s.Enums.Update(project, e.Id, new UpdateFieldEnum("X", null, null))));
        Assert.Contains("Version is required", missing.Message);

        var first = (await With(s => s.Enums.Update(project, e.Id, new UpdateFieldEnum("First", null, e.Version))))!.Value;
        var stale = await Conflict(With(s => s.Enums.Update(project, e.Id, new UpdateFieldEnum("Second", null, e.Version))));
        Assert.Equal(ConflictCode.Modified, stale.Code);
        Assert.Equal(ConflictCode.Modified, (await Conflict(With(s => s.Enums.Delete(project, e.Id, e.Version)))).Code);
        Assert.Equal("First", (await With(s => s.Enums.GetById(project, e.Id)))!.Name);

        Assert.True(await With(s => s.Enums.Delete(project, e.Id, first.Version)));
        Assert.Null(await With(s => s.Enums.GetById(project, e.Id)));
        Assert.False(await With(s => s.Enums.Delete(project, e.Id, first.Version)));
    }

    // ---- поля ----

    [Fact]
    public async Task A_field_is_created_for_each_type_listed_by_name_and_read_back()
    {
        var project = await NewProject();
        var priority = await NewEnum(project);
        var created = new List<FieldDefinition>
        {
            await NewField(project, "Note"),
            await NewField(project, "Estimate", FieldType.Int),
            await NewField(project, "Weight", FieldType.Float),
            await NewField(project, "Billable", FieldType.Bool),
            await NewField(project, "Due", FieldType.Date),
            await NewField(project, "Tags", FieldType.String, multiple: true),
            await NewField(project, "Level", FieldType.Enum, enumId: priority.Id),
            await NewField(project, "Levels", FieldType.Enum, multiple: true, enumId: priority.Id)
        };

        var all = await With(s => s.Fields.GetAll(project));
        Assert.Equal(created.Select(x => x.Name).Order(StringComparer.Ordinal).ToArray(), all.Select(x => x.Name).ToArray());
        foreach (var field in created)
        {
            var read = (await With(s => s.Fields.GetById(project, field.Id)))!;
            Assert.Equal(field, read);
            Assert.Equal(field, all.Single(x => x.Id == field.Id));
        }

        Assert.Equal((FieldType.Int, false, null), (created[1].Type, created[1].Multiple, created[1].EnumId));
        Assert.Equal((FieldType.String, true), (created[5].Type, created[5].Multiple));
        Assert.Equal((FieldType.Enum, false, priority.Id), (created[6].Type, created[6].Multiple, created[6].EnumId));

        var page = await With(s => s.Fields.GetRange(project, new Page(2, 3)));
        Assert.Equal((8, 3), (page.TotalCount, page.Data.Length));

        var other = await NewProject();
        Assert.Empty(await With(s => s.Fields.GetAll(other)));
        Assert.Null(await With(s => s.Fields.GetById(project, Guid.NewGuid())));
        Assert.Equal(created[6].Id, (await With(s => s.Fields.Find(project, "level")))!.Id);
        Assert.Equal(created[6].Id, (await With(s => s.Fields.Find(project, created[6].Id.ToString())))!.Id);
        Assert.Null(await With(s => s.Fields.Find(project, "nope")));
    }

    [Fact]
    public async Task An_enum_field_needs_an_existing_enum_of_the_same_project_and_other_fields_must_not_have_one()
    {
        var project = await NewProject();
        var priority = await NewEnum(project);
        var other = await NewProject();
        var foreign = await NewEnum(other, "Foreign");

        Assert.Contains("must refer to an enum", (await Invalid(NewField(project, "A", FieldType.Enum))).Message);
        Assert.Contains("enum not found", (await Invalid(NewField(project, "B", FieldType.Enum, enumId: Guid.NewGuid()))).Message);
        Assert.Contains("enum not found", (await Invalid(NewField(project, "C", FieldType.Enum, enumId: foreign.Id))).Message);
        Assert.Contains("only a field of type enum", (await Invalid(NewField(project, "D", FieldType.Int, enumId: priority.Id))).Message);
        Assert.Contains("unknown", (await Invalid(NewField(project, "E", (FieldType)99))).Message);
        Assert.Contains("required", (await Invalid(NewField(project, " "))).Message);
        Assert.Empty(await With(s => s.Fields.GetAll(project)));
    }

    [Fact]
    public async Task Field_names_are_unique_in_the_project_ignoring_case_but_not_across_projects()
    {
        var project = await NewProject();
        var estimate = await NewField(project, "Estimate", FieldType.Int);

        Assert.Contains("already exists", (await Conflict(NewField(project, " estimate ", FieldType.Float))).Message);
        await NewField(await NewProject(), "Estimate", FieldType.Int);

        var weight = await NewField(project, "Weight", FieldType.Float);
        await Conflict(With(s => s.Fields.Update(project, weight.Id, new UpdateField("ESTIMATE", weight.Version))));
        var renamed = (await With(s => s.Fields.Update(project, estimate.Id, new UpdateField("ESTIMATE", estimate.Version))))!;
        Assert.Equal("ESTIMATE", renamed.Name);
    }

    [Fact]
    public async Task Updating_a_field_renames_it_and_keeps_type_multiplicity_and_enum()
    {
        var project = await NewProject();
        var priority = await NewEnum(project);
        var field = await NewField(project, "Level", FieldType.Enum, multiple: true, enumId: priority.Id);

        var renamed = (await With(s => s.Fields.Update(project, field.Id, new UpdateField("Severity", field.Version))))!;

        Assert.Equal(field with { Name = "Severity", Version = renamed.Version }, renamed);
        Assert.NotEqual(field.Version, renamed.Version);
        Assert.Equal(renamed, await With(s => s.Fields.GetById(project, field.Id)));

        // Без новых значений ничего не меняется (но версия — меняется: запись была).
        Assert.Null(await With(s => s.Fields.Update(project, Guid.NewGuid(), new UpdateField("X", "v"))));
    }

    [Fact]
    public async Task Changing_or_deleting_a_field_requires_the_current_version()
    {
        var project = await NewProject();
        var field = await NewField(project, "Note");

        Assert.Contains("Version is required", (await Invalid(With(s => s.Fields.Update(project, field.Id, new UpdateField("X", null))))).Message);
        var first = (await With(s => s.Fields.Update(project, field.Id, new UpdateField("First", field.Version))))!;

        Assert.Equal(ConflictCode.Modified, (await Conflict(With(s => s.Fields.Update(project, field.Id, new UpdateField("Second", field.Version))))).Code);
        Assert.Equal(ConflictCode.Modified, (await Conflict(With(s => s.Fields.Delete(project, field.Id, field.Version)))).Code);
        Assert.Equal("First", (await With(s => s.Fields.GetById(project, field.Id)))!.Name);

        Assert.True(await With(s => s.Fields.Delete(project, field.Id, first.Version)));
        Assert.Null(await With(s => s.Fields.GetById(project, field.Id)));
        Assert.False(await With(s => s.Fields.Delete(project, field.Id, first.Version)));
    }

    // ---- «используется» ----

    [Fact]
    public async Task An_enum_used_by_a_field_cannot_be_deleted_until_the_field_is_gone()
    {
        var project = await NewProject();
        var priority = await NewEnum(project);
        var spare = await NewEnum(project, "Spare");
        var level = await NewField(project, "Level", FieldType.Enum, enumId: priority.Id);
        await NewField(project, "Level2", FieldType.Enum, multiple: true, enumId: priority.Id);

        var error = await Conflict(With(s => s.Enums.Delete(project, priority.Id, priority.Version)));
        Assert.Equal(ConflictCode.InUse, error.Code);
        Assert.Contains("field 'Level'", error.Message);
        Assert.Contains("field 'Level2'", error.Message);
        Assert.NotNull(await With(s => s.Enums.GetById(project, priority.Id)));

        // Переименование и правка значений занятого перечисления разрешены: поля ссылаются на id.
        var renamed = (await With(s => s.Enums.Update(project, priority.Id, new UpdateFieldEnum("Prio", null, priority.Version))))!.Value;
        Assert.Equal(priority.Id, (await With(s => s.Fields.GetById(project, level.Id)))!.EnumId);

        // Неиспользуемое удаляется.
        Assert.True(await With(s => s.Enums.Delete(project, spare.Id, spare.Version)));

        foreach (var field in await With(s => s.Fields.GetAll(project)))
            Assert.True(await With(s => s.Fields.Delete(project, field.Id, field.Version)));
        Assert.True(await With(s => s.Enums.Delete(project, priority.Id, renamed.Version)));
    }

    // ---- блокировки правки ----

    [Fact]
    public async Task A_field_or_enum_locked_by_someone_else_cannot_be_changed_or_deleted()
    {
        var project = await NewProject();
        var priority = await NewEnum(project);
        var field = await NewField(project, "Note");

        await With(s => s.Locks.Acquire(LockedEntity.Field, field.Id, "Field 'Note'", project));
        await With(s => s.Locks.Acquire(LockedEntity.Enum, priority.Id, "Enum 'Priority'", project));

        // Свой держатель правит свободно, чужой — нет.
        _editor.Holder = FakeEditor.Anna;
        var field409 = await Assert.ThrowsAsync<TaskerLockedException>(() => With(s => s.Fields.Update(project, field.Id, new UpdateField("X", field.Version))));
        Assert.Equal("Field 'Note' is being edited by Ivan", field409.Message);
        await Assert.ThrowsAsync<TaskerLockedException>(() => With(s => s.Fields.Delete(project, field.Id, field.Version)));
        var enum409 = await Assert.ThrowsAsync<TaskerLockedException>(() => With(s => s.Enums.Update(project, priority.Id, new UpdateFieldEnum("X", null, priority.Version))));
        Assert.Equal("Enum 'Priority' is being edited by Ivan", enum409.Message);
        await Assert.ThrowsAsync<TaskerLockedException>(() => With(s => s.Enums.Delete(project, priority.Id, priority.Version)));
        Assert.Equal("Note", (await With(s => s.Fields.GetById(project, field.Id)))!.Name);

        _editor.Holder = FakeEditor.Ivan;
        var renamed = (await With(s => s.Fields.Update(project, field.Id, new UpdateField("Memo", field.Version))))!;

        // Удаление сущности снимает её блокировку.
        Assert.True(await With(s => s.Fields.Delete(project, field.Id, renamed.Version)));
        Assert.Null(await With(s => s.Locks.Get(LockedEntity.Field, field.Id)));
    }

    [Fact]
    public async Task Entity_locks_name_fields_and_enums_and_do_not_find_missing_ones()
    {
        var project = await NewProject();
        var priority = await NewEnum(project);
        var field = await NewField(project, "Note");

        var fieldLock = await With(s => s.EntityLocks.Acquire(project, LockedEntity.Field, field.Id));
        var enumLock = await With(s => s.EntityLocks.Acquire(project, LockedEntity.Enum, priority.Id));
        Assert.Equal((LockedEntity.Field, field.Id, true), (fieldLock!.Entity, fieldLock.Id, fieldLock.Mine));
        Assert.Equal((LockedEntity.Enum, priority.Id, true), (enumLock!.Entity, enumLock.Id, enumLock.Mine));
        Assert.NotNull(await With(s => s.EntityLocks.Get(LockedEntity.Field, field.Id)));
        Assert.Equal(2, (await With(s => s.EntityLocks.GetByProject(project))).Length);

        Assert.Null(await With(s => s.EntityLocks.Acquire(project, LockedEntity.Field, Guid.NewGuid())));
        Assert.Null(await With(s => s.EntityLocks.Acquire(project, LockedEntity.Enum, field.Id))); // id поля — не перечисление
        Assert.True(await With(s => s.EntityLocks.Release(LockedEntity.Field, field.Id)));
        Assert.Null(await With(s => s.EntityLocks.Get(LockedEntity.Field, field.Id)));

        Assert.Equal(LockedEntity.Field, EntityLockService.ParseEntity("field"));
        Assert.Equal(LockedEntity.Enum, EntityLockService.ParseEntity("Enum"));
        Assert.True(EntityLockService.IsProjectScoped(LockedEntity.Field));
    }

    // ---- гонка имён ----

    [Fact]
    public async Task Concurrent_creation_of_the_same_name_leaves_exactly_one()
    {
        var project = await NewProject();

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            try
            {
                await NewField(project, "Same");
                return true;
            }
            catch (TaskerConflictException)
            {
                return false;
            }
        })));

        Assert.Equal(1, results.Count(x => x));
        Assert.Single(await With(s => s.Fields.GetAll(project)));
    }
}

public sealed class FilesFieldCatalogTests : FieldCatalogContract
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

    [Fact]
    public async Task Fields_and_enums_are_files_in_their_own_folders_with_the_format_version_and_a_fresh_process_reads_them()
    {
        var project = Guid.NewGuid();
        var storage = _container.Resolve<IFieldEnumStorage>();
        var priority = new FieldEnum
        {
            Id = Guid.NewGuid(), ProjectId = project, Name = "Приоритет",
            Values = [new FieldEnumValue(Guid.NewGuid(), "Низкий"), new FieldEnumValue(Guid.NewGuid(), "Высокий")], Version = ""
        };
        await storage.Add(priority);
        var level = new FieldDefinition { Id = Guid.NewGuid(), ProjectId = project, Name = "Level", Type = FieldType.Enum, Multiple = true, EnumId = priority.Id, Version = "" };
        var plain = new FieldDefinition { Id = Guid.NewGuid(), ProjectId = project, Name = "Note", Type = FieldType.String, Version = "" };
        await _container.Resolve<IFieldStorage>().Add(level);
        await _container.Resolve<IFieldStorage>().Add(plain);

        var enumText = await File.ReadAllTextAsync(FileFinder.In(Folder(project, "enums"), priority.Id));
        Assert.StartsWith($"formatVersion: {Tasker.Storage.Files.Storages.FormatVersions.Current}\n", enumText.ReplaceLineEndings("\n"));
        Assert.Contains("name: Приоритет", enumText);
        Assert.Contains($"id: {priority.Values[0].Id}", enumText);
        Assert.Contains("name: Низкий", enumText);

        var fieldText = await File.ReadAllTextAsync(FileFinder.In(Folder(project, "fields"), level.Id));
        Assert.StartsWith($"formatVersion: {Tasker.Storage.Files.Storages.FormatVersions.Current}\n", fieldText.ReplaceLineEndings("\n"));
        Assert.Contains("type: enum", fieldText);
        Assert.Contains($"enum: {priority.Id}", fieldText);
        Assert.Contains("multiple: true", fieldText);
        var plainText = await File.ReadAllTextAsync(FileFinder.In(Folder(project, "fields"), plain.Id));
        Assert.DoesNotContain("multiple:", plainText);
        Assert.DoesNotContain("enum:", plainText);

        // Новый процесс (контейнер) знает файлы только с диска.
        await using var fresh = Open();
        var fields = await fresh.Resolve<IFieldStorage>().GetAll(project);
        Assert.Equal(["Level", "Note"], fields.Select(x => x.Name).ToArray());
        Assert.Equal(level.EnumId, fields[0].EnumId);
        var enums = await fresh.Resolve<IFieldEnumStorage>().GetAll(project);
        Assert.Equal(priority.Values, enums.Single().Values);
    }

    [InProcess]
    [Fact]
    public async Task Migration_covers_the_field_and_enum_folders_and_reports_files_of_a_newer_format()
    {
        var project = Guid.NewGuid();
        Directory.CreateDirectory(Folder(project, "fields"));
        Directory.CreateDirectory(Folder(project, "enums"));
        var field = Guid.NewGuid();
        var value = Guid.NewGuid();
        var newer = Guid.NewGuid();
        var fieldPath = Path.Combine(Folder(project, "fields"), field + ".yaml");
        var enumPath = Path.Combine(Folder(project, "enums"), value + ".yaml");
        await File.WriteAllTextAsync(fieldPath, $"formatVersion: 2\nid: {field}\nname: Old\ntype: bool\n");
        await File.WriteAllTextAsync(enumPath, $"id: {value}\nname: E\nvalues:\n- id: {Guid.NewGuid()}\n  name: A\n");
        await File.WriteAllTextAsync(Path.Combine(Folder(project, "fields"), newer + ".yaml"), $"formatVersion: 99\nid: {newer}\nname: Future\ntype: int\n");

        var report = await _container.Resolve<Tasker.Core.Workspace.IFileMigration>().Run(new Tasker.Core.Workspace.MigrationOptions());

        Assert.Equal(9, report.CurrentFormat);
        Assert.Equal(2, report.Migrated.Length);
        Assert.Single(report.Newer);
        Assert.Contains($"formatVersion: {Tasker.Storage.Files.Storages.FormatVersions.Current}", await File.ReadAllTextAsync(FileFinder.In(Folder(project, "fields"), field)));
        Assert.StartsWith($"formatVersion: {Tasker.Storage.Files.Storages.FormatVersions.Current}", await File.ReadAllTextAsync(FileFinder.In(Folder(project, "enums"), value)));
        Assert.Equal("Old", (await _container.Resolve<IFieldStorage>().GetAll(project)).Single().Name);
        // Файл более нового формата не читается и в списки не попадает, а считается проблемой.
        Assert.Equal(1, await _container.Resolve<IFieldStorage>().CountUnreadable(project));
    }

    [Fact]
    public async Task Hand_written_and_unreadable_field_and_enum_files_are_indexed_or_reported_as_problems()
    {
        var project = Guid.NewGuid();
        var good = Guid.NewGuid();
        Directory.CreateDirectory(Folder(project, "fields"));
        Directory.CreateDirectory(Folder(project, "enums"));
        // Рукописный файл без formatVersion — формат 0, читается.
        await File.WriteAllTextAsync(Path.Combine(Folder(project, "fields"), good + ".yaml"), $"id: {good}\nname: Hand\ntype: INT\n");
        await File.WriteAllTextAsync(Path.Combine(Folder(project, "fields"), Guid.NewGuid() + ".yaml"), "<<<<<<< HEAD\n");
        await File.WriteAllTextAsync(Path.Combine(Folder(project, "fields"), Guid.NewGuid() + ".yaml"), "id: 1\nname: x\ntype: banana\n");
        await File.WriteAllTextAsync(Path.Combine(Folder(project, "enums"), Guid.NewGuid() + ".yaml"), "<<<<<<< HEAD\n");
        await File.WriteAllTextAsync(Path.Combine(Folder(project, "enums"), Guid.NewGuid() + ".yaml"), "formatVersion: 99\nid: x\n");
        await _container.Resolve<Tasker.Storage.Files.Index.WorkspaceIndex>().Rescan();

        var fields = await _container.Resolve<IFieldStorage>().GetAll(project);
        Assert.Equal(("Hand", FieldType.Int), (fields.Single().Name, fields.Single().Type));
        Assert.Equal(2, await _container.Resolve<IFieldStorage>().CountUnreadable(project));
        Assert.Equal(2, await _container.Resolve<IFieldEnumStorage>().CountUnreadable(project));
        Assert.Empty(await _container.Resolve<IFieldEnumStorage>().GetAll(project));
        Assert.Equal(0, await _container.Resolve<IFieldStorage>().CountUnreadable(Guid.NewGuid()));
    }
}

public sealed class DbFieldCatalogTests : FieldCatalogContract
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
    public async Task The_database_has_no_unreadable_files()
    {
        await using var scope = _container.BeginLifetimeScope();

        Assert.Equal(0, await scope.Resolve<IFieldStorage>().CountUnreadable(Guid.NewGuid()));
        Assert.Equal(0, await scope.Resolve<IFieldEnumStorage>().CountUnreadable(Guid.NewGuid()));
    }

    [Fact]
    public async Task The_database_refuses_to_delete_an_enum_that_a_field_refers_to_and_deletes_fields_and_enums_with_the_project()
    {
        var project = Guid.NewGuid();
        await using var scope = _container.BeginLifetimeScope();
        var projects = scope.Resolve<IProjectStorage>();
        var projectVersion = await projects.Add(new Project { Id = project, Name = "P", CreatedAt = DateTimeOffset.UtcNow, Version = "" });
        var enums = scope.Resolve<IFieldEnumStorage>();
        var fields = scope.Resolve<IFieldStorage>();
        var priority = new FieldEnum { Id = Guid.NewGuid(), ProjectId = project, Name = "P", Values = [new FieldEnumValue(Guid.NewGuid(), "Low")], Version = "" };
        var enumVersion = await enums.Add(priority);
        await fields.Add(new FieldDefinition { Id = Guid.NewGuid(), ProjectId = project, Name = "F", Type = FieldType.Enum, EnumId = priority.Id, Version = "" });

        // Сервис проверяет «используется» сам; а база — последняя линия защиты, если проверку обошли.
        await Assert.ThrowsAsync<SqliteException>(() => enums.Delete(project, priority.Id, enumVersion));

        // Проект — контейнер: удаляется вместе с полями и перечислениями.
        Assert.True(await projects.Delete(project, projectVersion));
        Assert.Empty(await fields.GetAll(project));
        Assert.Empty(await enums.GetAll(project));
    }
}
