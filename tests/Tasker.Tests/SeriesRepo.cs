using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Xunit;

namespace Tasker.Tests;

/// <summary>
/// Репозиторий git с рабочей папкой Tasker (проект Demo, тип Bug, серия TSK, задача Base) для проверки серий после слияния веток.
/// Команды идут в этом же процессе; каталог данных Tasker изолирован, чтобы не задеть настоящий демон.
/// </summary>
public sealed class SeriesRepo : IDisposable
{
    private readonly IsolatedHome? _home;

    public SeriesRepo(bool isolate = true)
    {
        _home = isolate ? new IsolatedHome() : null;
        Repo = new GitRepo();
        Folder = Repo.Root;
    }

    public GitRepo Repo { get; }

    public string Folder { get; }

    public static async Task<CliResult> Ok(Task<CliResult> run)
    {
        var result = await run;
        Assert.True(result.Code == 0, $"exit {result.Code}: {result.Err}");
        return result;
    }

    /// <summary>Команда в рабочей папке (без проекта).</summary>
    public Task<CliResult> Cli(params string[] args) => TestWorkspace.Invoke([.. args, "-w", Folder]);

    /// <summary>Команда в проекте Demo.</summary>
    public Task<CliResult> P(params string[] args) => TestWorkspace.Invoke([.. args, "-p", "Demo", "-w", Folder]);

    /// <summary>Проект Demo с типом Bug; серия TSK и задача Base (TSK-1), если <paramref name="withSeries"/>. Закоммичено в main.</summary>
    public async Task Seed(bool withSeries = true)
    {
        await Ok(Cli("project", "create", "Demo"));
        await Ok(P("status", "create", "Todo"));
        await Ok(P("status", "create", "Done"));
        await Ok(P("status-set", "create", "Flow", "--status", "Todo", "Done"));
        await Ok(P("task-type", "create", "Bug", "--status-set", "Flow"));
        if (withSeries)
        {
            await Ok(P("series", "create", "Tasks", "--prefix", "TSK"));
            await Ok(P("task", "create", "Base", "--type", "Bug", "--series", "TSK"));
        }

        Repo.Commit("seed");
    }

    public async Task<JsonNode> NewTask(string title, params string[] series) =>
        (await Ok(P(["task", "create", title, "--type", "Bug", .. series.SelectMany(x => new[] { "--series", x }), "--json"]))).Json;

    /// <summary>Ветка от main с одним коммитом, который делает <paramref name="change"/>; после неё снова main.</summary>
    public async Task Branch(string name, Func<Task> change)
    {
        Repo.Git("checkout", "-q", "-b", name, "main");
        await change();
        Repo.Commit(name);
        Repo.Git("checkout", "-q", "main");
    }

    public void Merge(params string[] branches)
    {
        foreach (var branch in branches)
            Repo.Git("merge", "-q", "--no-edit", branch);
    }

    /// <summary>Две ветки создают по задаче в TSK (обе получают TSK-2), main их сливает. Возвращает id: (созданной раньше, созданной позже).</summary>
    public async Task<(Guid Earlier, Guid Later)> MergeDuplicateNumbers()
    {
        Guid earlier = default, later = default;
        await Branch("a", async () => earlier = (await NewTask("From a", "TSK"))["id"]!.GetValue<Guid>());
        await Branch("b", async () => later = (await NewTask("From b", "TSK"))["id"]!.GetValue<Guid>());
        Merge("a", "b");
        return (earlier, later);
    }

    /// <summary>
    /// Задачи TSK-1 (Base) и TSK-2 (Other), типы связей уже сохранены и закоммичены. Одна ветка удаляет Other, другая связывает Base с Other
    /// (<paramref name="linkPhrase"/>), main их сливает: у Base остаётся связь на задачу, которой нет. Возвращает id: (Base, Other).
    /// </summary>
    public async Task<(Guid Source, Guid Target)> MergeDanglingTargetLink(string linkPhrase = "blocks")
    {
        var other = (await NewTask("Other", "TSK"))["id"]!.GetValue<Guid>();
        await SaveLinkTypes();
        var source = (await Ok(P("task", "get", "TSK-1", "--json"))).Json["id"]!.GetValue<Guid>();

        await Branch("link", async () => await Ok(P("task", "link", "TSK-1", linkPhrase, "TSK-2")));
        await Branch("del", async () => await Ok(P("task", "delete", "TSK-2")));
        Merge("link", "del");
        return (source, other);
    }

    /// <summary>
    /// Одна ветка удаляет тип связи Duplicate (по которому ничего нет), другая связывает Base с Other этим типом; main их сливает:
    /// у Base остаётся связь типа, которого нет. Возвращает id Base.
    /// </summary>
    public async Task<Guid> MergeDanglingTypeLink()
    {
        await Ok(P("task", "create", "Other", "--type", "Bug", "--series", "TSK"));
        await SaveLinkTypes();
        var source = (await Ok(P("task", "get", "TSK-1", "--json"))).Json["id"]!.GetValue<Guid>();

        await Branch("link", async () => await Ok(P("task", "link", "TSK-1", "duplicates", "TSK-2")));
        await Branch("deltype", async () => await Ok(P("link-type", "delete", "Duplicate")));
        Merge("link", "deltype");
        return source;
    }

    /// <summary>Первая связь сохраняет типы связей по умолчанию в файлы; её тут же убираем и коммитим общее состояние.</summary>
    private async Task SaveLinkTypes()
    {
        await Ok(P("task", "link", "TSK-1", "relates to", "TSK-2"));
        await Ok(P("task", "unlink", "TSK-1", "relates to", "TSK-2"));
        Repo.Commit("link types");
    }

    /// <summary>Файлы данных (без кэша) и их содержимое: чтобы проверять, что команда ничего не записала.</summary>
    public Dictionary<string, string> Snapshot()
    {
        var root = Path.Combine(Folder, ".tasker");
        return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(x => !x.StartsWith(Path.Combine(root, ".cache"), StringComparison.Ordinal))
            .ToDictionary(x => Path.GetRelativePath(root, x), x => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(x))));
    }

    /// <summary>Файл сущности проекта: <c>tasks/&lt;заголовок&gt;-&lt;id8&gt;.yaml</c> (или старое <c>tasks/&lt;id&gt;.yaml</c>), <c>series/&lt;id&gt;.yaml</c>.</summary>
    public string FileOf(string kind, Guid id) => FileFinder.Find(Folder, kind, id);

    public Guid SeriesId(string prefix)
    {
        foreach (var file in Directory.GetFiles(Path.Combine(Folder, ".tasker"), "*.yaml", SearchOption.AllDirectories))
            if (Path.GetFileName(Path.GetDirectoryName(file)) == "series" && File.ReadAllText(file).Contains($"prefix: {prefix}\n"))
                return Guid.Parse(File.ReadAllLines(file).First(x => x.StartsWith("id: ")).Substring(4).Trim());
        throw new InvalidOperationException($"No series {prefix}");
    }

    public void Dispose()
    {
        Repo.Dispose();
        _home?.Dispose();
    }
}

/// <summary>Поиск файла сущности в .tasker по id: у задач имя по заголовку («заголовок-id8.yaml»), у остальных — Guid.</summary>
internal static class FileFinder
{
    /// <summary>Файл сущности в папке (<c>.../statuses</c>) по id: под старым именем (Guid) или новым (название и 8 знаков id).</summary>
    public static string In(string folder, Guid id) =>
        Directory.GetFiles(folder, "*.yaml").Single(x =>
            Path.GetFileName(x) == $"{id}.yaml" || Path.GetFileName(x).EndsWith($"-{id.ToString("N")[..8]}.yaml", StringComparison.Ordinal));

    public static string Find(string workspaceRoot, string kind, Guid id)
    {
        var id8 = id.ToString("N")[..8];
        var separator = Path.DirectorySeparatorChar;
        return Directory.GetFiles(Path.Combine(workspaceRoot, ".tasker", "projects"), "*.yaml", SearchOption.AllDirectories)
            .Single(x => x.Contains(separator + kind + separator)
                         && (Path.GetFileName(x) == $"{id}.yaml" || Path.GetFileName(x).EndsWith($"-{id8}.yaml", StringComparison.Ordinal)));
    }
}
