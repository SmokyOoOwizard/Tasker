using Autofac;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tasker.Core.Dto;
using Tasker.Core.TaskSeries;
using Tasker.Core.Tasks;
using Tasker.Storage.Files;
using Tasker.Storage.Files.Index;
using Tasker.Storage.Files.Storages;
using Xunit;

namespace Tasker.Tests;

/// <summary>Временная папка с файловыми хранилищами (как в <c>Session</c>, но без Core-сервисов): контейнеров на одну папку можно открыть несколько.</summary>
internal sealed class FilesHarness : IDisposable
{
    private readonly List<IContainer> _containers = [];

    public FilesHarness(string? root = null)
    {
        Root = root ?? Path.Combine(Path.GetTempPath(), "tasker-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Open();
    }

    public string Root { get; }
    public Guid ProjectId { get; } = Guid.NewGuid();
    public IContainer Container => _containers[0];
    public ISeriesStorage Series => Container.Resolve<ISeriesStorage>();
    public ITaskStorage Tasks => Container.Resolve<ITaskStorage>();
    public IWriteScope Scope => Container.Resolve<IWriteScope>();
    public TaskerDirectory Directory_ => Container.Resolve<TaskerDirectory>();
    public ProjectDirectory Project => Directory_.Project(ProjectId);

    /// <summary>Ещё один независимый контейнер (свой индекс в процессе) на ту же папку — как второй процесс.</summary>
    public IContainer Open()
    {
        var builder = new ContainerBuilder();
        builder.RegisterGeneric(typeof(NullLogger<>)).As(typeof(ILogger<>)).SingleInstance();
        builder.RegisterModule(new FileStorageModule(Root));
        var container = builder.Build();
        _containers.Add(container);
        return container;
    }

    public Series NewSeries(string prefix, string? name = null, Guid? projectId = null) => new()
    {
        Id = Guid.NewGuid(),
        ProjectId = projectId ?? ProjectId,
        Name = name ?? "Series " + prefix,
        Prefix = prefix,
        Version = ""
    };

    public TaskItem NewTask(string title = "t", DateTimeOffset? createdAt = null, params TaskSeriesNumber[] numbers) => new()
    {
        Id = Guid.NewGuid(),
        ProjectId = ProjectId,
        Title = title,
        TypeId = Guid.NewGuid(),
        StatusId = Guid.NewGuid(),
        CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
        UpdatedAt = createdAt ?? DateTimeOffset.UtcNow,
        SeriesNumbers = numbers,
        Version = ""
    };

    /// <summary>Закрывает контейнеры, оставляя папку: как перезапуск приложения.</summary>
    public void CloseContainers()
    {
        foreach (var container in _containers)
            container.Dispose();
        _containers.Clear();
    }

    public void Dispose()
    {
        CloseContainers();
        try
        {
            System.IO.Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

public class SeriesFilesStorageTests : IDisposable
{
    private readonly FilesHarness _h = new();

    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task Series_crud_with_version_conflicts()
    {
        var series = _h.NewSeries("TSK");
        var version = await _h.Series.Add(series);

        var read = await _h.Series.GetById(_h.ProjectId, series.Id);
        Assert.Equal(series with { Version = version }, read);

        var renamed = read! with { Name = "Renamed", Prefix = "TS2" };
        var version2 = await _h.Series.Update(renamed, version);
        Assert.NotNull(version2);
        Assert.NotEqual(version, version2);

        // Устаревшая версия: запись не проходит, файл не меняется.
        Assert.Null(await _h.Series.Update(read with { Name = "Stale" }, version));
        Assert.False(await _h.Series.Delete(_h.ProjectId, series.Id, version));
        Assert.Equal("Renamed", (await _h.Series.GetById(_h.ProjectId, series.Id))!.Name);

        // Индекс видит изменение сразу.
        Assert.Equal("TS2", Assert.Single(await _h.Series.GetAll(_h.ProjectId)).Prefix);

        Assert.True(await _h.Series.Delete(_h.ProjectId, series.Id, version2!));
        Assert.Null(await _h.Series.GetById(_h.ProjectId, series.Id));
        Assert.Empty(await _h.Series.GetAll(_h.ProjectId));
        Assert.Null(await _h.Series.Update(renamed, version2!));
    }

    [Fact]
    public async Task Series_are_isolated_by_project()
    {
        var other = Guid.NewGuid();
        await _h.Series.Add(_h.NewSeries("AAA"));
        await _h.Series.Add(_h.NewSeries("BBB", projectId: other));

        Assert.Equal("AAA", Assert.Single(await _h.Series.GetAll(_h.ProjectId)).Prefix);
        Assert.Equal("BBB", Assert.Single(await _h.Series.GetAll(other)).Prefix);
    }

    [Fact]
    public async Task Series_are_ordered_by_prefix_case_sensitively_then_by_id()
    {
        foreach (var prefix in new[] { "tsk", "TSK", "Zed", "abc", "ABC", "B2", "B10" })
            await _h.Series.Add(_h.NewSeries(prefix));
        // Один префикс у двух серий (после слияния веток): порядок — по id.
        var twin1 = _h.NewSeries("TSK");
        var twin2 = _h.NewSeries("TSK");
        await _h.Series.Add(twin1);
        await _h.Series.Add(twin2);

        var all = await _h.Series.GetAll(_h.ProjectId);
        Assert.Equal(["ABC", "B10", "B2", "TSK", "TSK", "TSK", "Zed", "abc", "tsk"], all.Select(x => x.Prefix));

        var twins = all.Where(x => x.Prefix == "TSK").Select(x => x.Id.ToString()).ToArray();
        Assert.Equal(twins.Order(StringComparer.Ordinal), twins);

        var page = await _h.Series.GetRange(_h.ProjectId, new Page { Offset = 3, Limit = 4 });
        Assert.Equal(9, page.TotalCount);
        Assert.Equal(all.Skip(3).Take(4).Select(x => x.Id), page.Data.Select(x => x.Id));
    }

    [Fact]
    public async Task Series_file_has_format_version_id_name_and_prefix_only()
    {
        var series = _h.NewSeries("TSK", "Разработка");
        await _h.Series.Add(series);

        var text = await File.ReadAllTextAsync(FileFinder.In(Path.Combine(_h.Project.Root, "series"), series.Id));
        Assert.Equal($"formatVersion: {FormatVersions.Current}\nid: {series.Id}\nname: Разработка\nprefix: TSK\n", text);
    }

    [Fact]
    public async Task Task_file_without_series_has_no_series_key()
    {
        var task = _h.NewTask();
        await _h.Tasks.Add(task);

        var text = await File.ReadAllTextAsync(_h.Project.FindTaskFile(task.Id)!);
        Assert.DoesNotContain("series", text);
        Assert.Equal(["formatVersion", "id", "title", "typeId", "statusId", "createdAt", "updatedAt"],
            text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Split(':')[0]));
        Assert.Empty((await _h.Tasks.GetById(_h.ProjectId, task.Id))!.SeriesNumbers);
    }

    [Fact]
    public async Task Task_round_trips_several_series_numbers_in_order()
    {
        var (a, b, c) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var numbers = new[] { new TaskSeriesNumber(c, 7), new TaskSeriesNumber(a, 1), new TaskSeriesNumber(b, 12) };
        var task = _h.NewTask(numbers: numbers);
        var version = await _h.Tasks.Add(task);

        var text = await File.ReadAllTextAsync(_h.Project.FindTaskFile(task.Id)!);
        Assert.EndsWith($"series:\n- seriesId: {c}\n  number: 7\n- seriesId: {a}\n  number: 1\n- seriesId: {b}\n  number: 12\n", text);

        var read = (await _h.Tasks.GetById(_h.ProjectId, task.Id))!;
        Assert.Equal(numbers, read.SeriesNumbers);
        var fromIndex = Assert.Single((await _h.Tasks.GetRange(_h.ProjectId, null, new Page { Limit = 10 })).Data);
        Assert.Equal(numbers, fromIndex.SeriesNumbers);

        // Убрали все номера: ключ series из файла исчезает.
        Assert.NotNull(await _h.Tasks.Update(read with { SeriesNumbers = [] }, version));
        Assert.DoesNotContain("series", await File.ReadAllTextAsync(_h.Project.FindTaskFile(task.Id)!));
    }

    [Fact]
    public async Task CountUnreadable_counts_broken_series_files_of_the_project_only()
    {
        var good = _h.NewSeries("OK");
        await _h.Series.Add(good);
        Assert.Equal(0, await _h.Series.CountUnreadable(_h.ProjectId));

        var broken = Guid.NewGuid();
        Directory.CreateDirectory(_h.Project.Series);
        Directory.CreateDirectory(_h.Project.Tasks);
        await File.WriteAllTextAsync(_h.Project.LegacyFile(EntityFolders.Series, broken),
            $"<<<<<<< HEAD\nid: {broken}\nprefix: A\n=======\nid: {broken}\nprefix: B\n>>>>>>> branch\n");
        var brokenYaml = Guid.NewGuid();
        await File.WriteAllTextAsync(_h.Project.LegacyFile(EntityFolders.Series, brokenYaml), "id: [oops\n");
        // Битая задача и битая серия чужого проекта не считаются.
        await File.WriteAllTextAsync(_h.Project.LegacyFile(EntityFolders.Tasks, Guid.NewGuid()), "<<<<<<< HEAD\n");
        var other = _h.Directory_.Project(Guid.NewGuid());
        Directory.CreateDirectory(other.Series);
        await File.WriteAllTextAsync(other.LegacyFile(EntityFolders.Series, Guid.NewGuid()), "<<<<<<< HEAD\n");

        await _h.Container.Resolve<WorkspaceIndex>().Rescan();

        Assert.Equal(2, await _h.Series.CountUnreadable(_h.ProjectId));
        Assert.Equal(good.Id, Assert.Single(await _h.Series.GetAll(_h.ProjectId)).Id);
    }
}
