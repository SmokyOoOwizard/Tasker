using System.Diagnostics;
using Autofac;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tasker.Core;
using Tasker.Core.Projects;
using Tasker.Core.Statuses;
using Tasker.Core.Tasks;
using Tasker.Core.Tasks.Search;
using Tasker.Core.TaskSeries;
using Tasker.Search.Lucene;
using Tasker.Storage.Db;
using Tasker.Storage.Db.Configs.Tasker;
using Tasker.Storage.Files;
using Xunit;
using Xunit.Abstractions;

namespace Tasker.Tests;

public sealed class TaskSearchTests(ITestOutputHelper output)
{
    private const string PostgresVariable = "TASKER_TEST_POSTGRES";
    private const string PerfVariable = "TASKER_TEST_SEARCH_PERF";

    public static TheoryData<string> Backends => new() { "files", "sqlite", "postgres" };

    private sealed class Bench : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "tasker-tests-" + Guid.NewGuid().ToString("N"));
        private readonly IContainer _container;
        private readonly ILifetimeScope _scope;
        private readonly DateTimeOffset _start = DateTimeOffset.UtcNow.AddDays(-1);
        private int _created;

        public Bench(string backend, string? postgres)
        {
            Directory.CreateDirectory(_root);
            var builder = new ContainerBuilder();
            builder.RegisterGeneric(typeof(NullLogger<>)).As(typeof(ILogger<>)).SingleInstance();
            if (backend == "files")
                builder.RegisterModule(new FileStorageModule(_root));
            else
                builder.RegisterModule(new DbStorageModule(backend == "sqlite"
                    ? new DbConfigs { SqliteFile = Path.Combine(_root, "tasker.db") }
                    : new DbConfigs { PostgresConnectionString = postgres }));
            _container = builder.Build();
            _scope = _container.BeginLifetimeScope();
        }

        public Guid ProjectId { get; } = Guid.NewGuid();
        public Guid TypeId { get; } = Guid.NewGuid();
        public Guid StatusId { get; } = Guid.NewGuid();
        public ITaskStorage Tasks => _scope.Resolve<ITaskStorage>();
        public ISeriesStorage SeriesStorage => _scope.Resolve<ISeriesStorage>();
        public LuceneTaskSearchIndex Index { get; } = new(null);
        public TaskSearchService Search => new(Tasks, SeriesStorage, Index);

        public async Task Start()
        {
            if (_scope.ResolveOptional<IStorageLifecycle>() is { } lifecycle)
                await lifecycle.Start(default);

            var set = Guid.NewGuid();
            await _scope.Resolve<IProjectStorage>().Add(new Project { Id = ProjectId, Name = "P" + ProjectId.ToString("N")[..8], CreatedAt = DateTimeOffset.UtcNow, Version = "" });
            await _scope.Resolve<IStatusStorage>().Add(new Status { Id = StatusId, ProjectId = ProjectId, Name = "Open", Color = "#000000", Version = "" });
            await _scope.Resolve<IStatusSetStorage>().Add(new StatusSet { Id = set, ProjectId = ProjectId, Name = "S", StatusIds = [StatusId], Version = "" });
            await _scope.Resolve<ITaskTypeStorage>().Add(new TaskType { Id = TypeId, ProjectId = ProjectId, Name = "T", StatusSetId = set, Version = "" });
        }

        public async Task<Guid> AddSeries(string prefix)
        {
            var series = new Series { Id = Guid.NewGuid(), ProjectId = ProjectId, Name = "Series " + prefix, Prefix = prefix, Version = "" };
            await SeriesStorage.Add(series);
            return series.Id;
        }

        public async Task<TaskItem> AddTask(string title, string? description = "", params TaskSeriesNumber[] numbers)
        {
            var created = _start.AddSeconds(++_created);
            var task = new TaskItem
            {
                Id = Guid.NewGuid(),
                ProjectId = ProjectId,
                Title = title,
                Description = description,
                TypeId = TypeId,
                StatusId = StatusId,
                SeriesNumbers = numbers,
                CreatedAt = created,
                UpdatedAt = created,
                Version = ""
            };
            var version = await Tasks.Add(task);
            return new TaskItem
            {
                Id = task.Id, ProjectId = task.ProjectId, Title = task.Title, Description = task.Description, TypeId = task.TypeId, StatusId = task.StatusId,
                SeriesNumbers = task.SeriesNumbers, CreatedAt = task.CreatedAt, UpdatedAt = task.UpdatedAt, Version = version
            };
        }

        public async ValueTask DisposeAsync()
        {
            Index.Dispose();
            await _scope.DisposeAsync();
            await _container.DisposeAsync();
            SqliteConnection.ClearAllPools();
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    Directory.Delete(_root, recursive: true);
                    return;
                }
                catch (IOException) when (attempt < 20)
                {
                    await Task.Delay(50);
                }
            }
        }
    }

    private static async Task<Bench?> Open(string backend)
    {
        var postgres = Environment.GetEnvironmentVariable(PostgresVariable);
        if (backend == "postgres" && string.IsNullOrWhiteSpace(postgres))
            return null;

        var bench = new Bench(backend, postgres);
        await bench.Start();
        return bench;
    }

    private static async Task<string[]> Titles(Bench bench, string query) =>
        (await bench.Search.Search(bench.ProjectId, query)).Select(x => x.Task.Title).ToArray();

    [Theory]
    [MemberData(nameof(Backends))]
    public async Task A_number_comes_first_then_title_then_description(string backend)
    {
        await using var bench = await Open(backend);
        if (bench == null)
            return;

        var series = await bench.AddSeries("TSK");
        await bench.AddTask("В описании", "смотри пункт 133 в списке", new TaskSeriesNumber(series, 1));
        await bench.AddTask("Версия 133 приложения", "", new TaskSeriesNumber(series, 2));
        await bench.AddTask("Следующая", "", new TaskSeriesNumber(series, 1330));
        await bench.AddTask("Нужная", "", new TaskSeriesNumber(series, 133));
        await bench.AddTask("Посторонняя", "ничего общего", new TaskSeriesNumber(series, 7));

        var found = await bench.Search.Search(bench.ProjectId, "133");

        Assert.Equal(["Нужная", "Следующая", "Версия 133 приложения", "В описании"], found.Select(x => x.Task.Title));
        Assert.Equal([TaskSearchField.Number, TaskSearchField.Number, TaskSearchField.Title, TaskSearchField.Description], found.Select(x => x.Field));
        Assert.Equal(1.0, found[0].Score);
        Assert.Equal(TaskSearchMatch.Exact, found[0].Match);
        Assert.Equal(TaskSearchMatch.Prefix, found[1].Match);
        Assert.All(found.Zip(found.Skip(1)), x => Assert.True(x.First.Score > x.Second.Score));
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public async Task A_reference_is_found_in_any_spelling(string backend)
    {
        await using var bench = await Open(backend);
        if (bench == null)
            return;

        var series = await bench.AddSeries("TSK");
        var other = await bench.AddSeries("OTH");
        await bench.AddTask("Нужная", "", new TaskSeriesNumber(series, 133));
        await bench.AddTask("Другая серия", "", new TaskSeriesNumber(other, 133));

        foreach (var query in new[] { "TSK-133", "tsk-133", "tsk133", "tsk 133", "еыл-133" })
            Assert.Equal(["Нужная"], await Titles(bench, query));

        Assert.Equal(2, (await Titles(bench, "133")).Length);
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public async Task Text_ignores_case_order_and_yo_and_finds_parts_of_words(string backend)
    {
        await using var bench = await Open(backend);
        if (bench == null)
            return;

        await bench.AddTask("Палитра команд", "");
        await bench.AddTask("Ёлка на Новый год", "");
        await bench.AddTask("Upper CASE Title", "");

        Assert.Equal(["Палитра команд"], await Titles(bench, "ПАЛИТРА"));
        Assert.Equal(["Палитра команд"], await Titles(bench, "команд палитра"));
        Assert.Equal(["Палитра команд"], await Titles(bench, "литр"));
        Assert.Equal(["Ёлка на Новый год"], await Titles(bench, "елка"));
        Assert.Equal(["Ёлка на Новый год"], await Titles(bench, "ЁЛКА"));
        Assert.Equal(["Upper CASE Title"], await Titles(bench, "case"));
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public async Task Typos_and_a_wrong_layout_are_forgiven_and_rank_below_exact(string backend)
    {
        await using var bench = await Open(backend);
        if (bench == null)
            return;

        await bench.AddTask("Палитра команд", "");
        await bench.AddTask("Посторонняя", "палитра упомянута в описании");
        await bench.AddTask("Палитрв с опечаткой", "");

        var typo = await bench.Search.Search(bench.ProjectId, "палитрв");
        Assert.Equal("Палитрв с опечаткой", typo[0].Task.Title);
        Assert.Contains(typo, x => x.Task.Title == "Палитра команд" && x.Match == TaskSearchMatch.Typo);

        var layout = await bench.Search.Search(bench.ProjectId, "gfkbnhf");
        Assert.Equal("Палитра команд", layout[0].Task.Title);
        Assert.True(layout[0].SwitchedLayout);

        var exact = await bench.Search.Search(bench.ProjectId, "палитра");
        Assert.Equal(["Палитра команд", "Палитрв с опечаткой", "Посторонняя"], exact.Select(x => x.Task.Title));
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public async Task A_match_in_the_description_comes_with_a_snippet(string backend)
    {
        await using var bench = await Open(backend);
        if (bench == null)
            return;

        var text = string.Join(' ', Enumerable.Repeat("наполнитель", 200)) + " Редкое слово здесь " + string.Join(' ', Enumerable.Repeat("наполнитель", 200));
        await bench.AddTask("Длинная", text);

        var hit = Assert.Single(await bench.Search.Search(bench.ProjectId, "редкое"));

        Assert.Equal(TaskSearchField.Description, hit.Field);
        var snippet = Assert.IsType<TaskSearchSnippet>(hit.Snippet);
        var range = Assert.Single(snippet.Ranges);
        Assert.Equal("Редкое", snippet.Text.Substring(range.Start, range.Length));
        Assert.StartsWith("…", snippet.Text);
        Assert.EndsWith("…", snippet.Text);
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public async Task Changes_of_tasks_are_seen_by_the_next_search(string backend)
    {
        await using var bench = await Open(backend);
        if (bench == null)
            return;

        var task = await bench.AddTask("Старое имя", "");
        Assert.Equal(["Старое имя"], await Titles(bench, "старое"));

        var renamed = new TaskItem
        {
            Id = task.Id, ProjectId = task.ProjectId, Title = "Новое имя", Description = task.Description, TypeId = task.TypeId, StatusId = task.StatusId,
            SeriesNumbers = task.SeriesNumbers, CreatedAt = task.CreatedAt, UpdatedAt = task.UpdatedAt.AddSeconds(1), Version = task.Version
        };
        var version = await bench.Tasks.Update(renamed, task.Version);
        Assert.NotNull(version);
        Assert.Empty(await Titles(bench, "старое"));
        Assert.Equal(["Новое имя"], await Titles(bench, "новое"));

        Assert.True(await bench.Tasks.Delete(task.ProjectId, task.Id, version!));
        Assert.Empty(await Titles(bench, "новое"));
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public async Task A_filter_narrows_the_hits(string backend)
    {
        await using var bench = await Open(backend);
        if (bench == null)
            return;

        await bench.AddTask("Палитра команд", "");

        var none = await bench.Search.Search(bench.ProjectId, "палитра", null, new TaskFilter { StatusIds = [Guid.NewGuid()] });
        var all = await bench.Search.Search(bench.ProjectId, "палитра", null, new TaskFilter { StatusIds = [bench.StatusId] });

        Assert.Empty(none);
        Assert.Single(all);
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public async Task A_task_without_a_description_is_found(string backend)
    {
        await using var bench = await Open(backend);
        if (bench == null)
            return;

        await bench.AddTask("Без описания", null);

        Assert.Equal(["Без описания"], await Titles(bench, "описания"));
    }

    [Fact]
    public async Task Both_storages_give_the_same_order()
    {
        await using var files = (await Open("files"))!;
        await using var sqlite = (await Open("sqlite"))!;
        foreach (var bench in new[] { files, sqlite })
        {
            var series = await bench.AddSeries("TSK");
            await bench.AddTask("Поиск по палитре", "текст", new TaskSeriesNumber(series, 133));
            await bench.AddTask("Палитра 133", "про поиск", new TaskSeriesNumber(series, 7));
            await bench.AddTask("Что-то ещё", "палитра и поиск 133", new TaskSeriesNumber(series, 8));
            await bench.AddTask("Палитрв", "опечатка", new TaskSeriesNumber(series, 1330));
        }

        foreach (var query in new[] { "133", "палитра", "поиск палитра", "gfkbnhf", "палитрв", "tsk-13" })
            Assert.Equal(await Titles(files, query), await Titles(sqlite, query));
    }

    [Fact]
    public async Task An_index_on_disk_survives_a_restart_and_catches_up()
    {
        var folder = Path.Combine(Path.GetTempPath(), "tasker-tests-" + Guid.NewGuid().ToString("N"));
        await using var bench = (await Open("files"))!;
        try
        {
            await bench.AddTask("Первая задача", "");
            using (var first = new LuceneTaskSearchIndex(folder))
                Assert.Single(await new TaskSearchService(bench.Tasks, bench.SeriesStorage, first).Search(bench.ProjectId, "первая"));

            await bench.AddTask("Вторая задача", "");
            using (var second = new LuceneTaskSearchIndex(folder))
            {
                var seen = 0;
                await second.Update(bench.ProjectId, (known, _) =>
                {
                    seen = known.Count;
                    return Task.FromResult(TaskSearchChanges.None);
                });
                Assert.Equal(1, seen);
                Assert.Equal(2, (await new TaskSearchService(bench.Tasks, bench.SeriesStorage, second).Search(bench.ProjectId, "задача")).Length);
            }
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task A_busy_folder_falls_back_to_memory()
    {
        var folder = Path.Combine(Path.GetTempPath(), "tasker-tests-" + Guid.NewGuid().ToString("N"));
        await using var bench = (await Open("files"))!;
        try
        {
            await bench.AddTask("Первая задача", "");
            using var owner = new LuceneTaskSearchIndex(folder);
            using var other = new LuceneTaskSearchIndex(folder);
            Assert.Single(await new TaskSearchService(bench.Tasks, bench.SeriesStorage, owner).Search(bench.ProjectId, "первая"));
            Assert.Single(await new TaskSearchService(bench.Tasks, bench.SeriesStorage, other).Search(bench.ProjectId, "первая"));
            Assert.True(Directory.Exists(folder));
            await bench.AddTask("Первая задача ещё раз", "");
            Assert.Equal(2, (await new TaskSearchService(bench.Tasks, bench.SeriesStorage, owner).Search(bench.ProjectId, "первая")).Length);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public async Task Ten_thousand_tasks_answer_without_a_noticeable_delay(string backend)
    {
        if (Environment.GetEnvironmentVariable(PerfVariable) is not { Length: > 0 })
            return;

        await using var bench = await Open(backend);
        if (bench == null)
            return;

        var series = await bench.AddSeries("TSK");
        var words = "сервер демон палитра поиск индекс задача описание номер заголовок приложение клиент хранилище фильтр доска серия статус".Split(' ');
        var random = new Random(225);
        var total = Stopwatch.StartNew();
        for (var i = 1; i <= 10_000; i++)
        {
            var description = string.Join(' ', Enumerable.Range(0, 300).Select(_ => words[random.Next(words.Length)]));
            await bench.AddTask($"{words[random.Next(words.Length)]} {words[random.Next(words.Length)]} {i}", description, new TaskSeriesNumber(series, i));
        }
        output.WriteLine($"{backend}: filled in {total.ElapsedMilliseconds} ms");

        total.Restart();
        await bench.Search.Search(bench.ProjectId, "палитра");
        output.WriteLine($"{backend}: first search (builds the index) {total.ElapsedMilliseconds} ms");

        foreach (var query in new[] { "133", "TSK-133", "палитра", "палитрв", "gfkbnhf", "поиск палитра индекс", "литр", "несуществующее" })
        {
            var timings = new List<long>();
            var hits = 0;
            for (var run = 0; run < 5; run++)
            {
                total.Restart();
                hits = (await bench.Search.Search(bench.ProjectId, query)).Length;
                timings.Add(total.ElapsedMilliseconds);
            }
            output.WriteLine($"{backend}: «{query}» {hits} hits, {timings.Min()}-{timings.Max()} ms");
            Assert.True(timings.Order().ElementAt(2) < 300, $"{query}: {string.Join(", ", timings)} ms");
        }
    }
}
