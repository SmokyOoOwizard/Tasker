using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Tasker.Core;
using Tasker.Core.Locks;
using Xunit;

namespace Tasker.Tests;

public partial class ErrorCatalogTests
{
    private static bool Updating => Environment.GetEnvironmentVariable("TASKER_GOLDEN_UPDATE") is { Length: > 0 } value && value != "0";

    private static string RepositoryRoot
    {
        get
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "dotnet", "Tasker.slnx")))
                    return directory.FullName;
            }

            throw new InvalidOperationException("Repository root is not found");
        }
    }

    private static string Serialized() =>
        JsonSerializer.Serialize(new
        {
            codes = ErrorCatalog.All.ToDictionary(x => x.Code, x => new { status = x.Status, @params = x.Params, usages = x.HasUsages }),
            fields = ErrorCatalog.Fields,
            usageKinds = ErrorUsage.Kinds
        }, new JsonSerializerOptions { WriteIndented = true }) + "\n";

    [Fact]
    public void Committed_catalog_matches_the_code()
    {
        var path = Path.Combine(RepositoryRoot, "docs", "api", "error-codes.json");
        var actual = Serialized();
        if (Updating)
        {
            File.WriteAllText(path, actual);
            return;
        }

        Assert.True(File.Exists(path), $"{path} is missing: run the tests with TASKER_GOLDEN_UPDATE=1");
        Assert.True(File.ReadAllText(path).ReplaceLineEndings("\n") == actual,
            "docs/api/error-codes.json differs from the code: renaming a code breaks clients; if it is intended, run the tests with TASKER_GOLDEN_UPDATE=1 and update design/strings.json");
    }

    [Fact]
    public void Codes_are_unique_and_well_formed()
    {
        var codes = ErrorCatalog.All.Select(x => x.Code).ToArray();
        Assert.Equal(codes.Length, codes.Distinct().Count());
        Assert.All(codes, code => Assert.Matches(CodeFormat(), code));
    }

    public static TheoryData<string> Factories()
    {
        var data = new TheoryData<string>();
        foreach (var definition in ErrorCatalog.All)
            data.Add(definition.Code);
        return data;
    }

    [Theory]
    [MemberData(nameof(Factories))]
    public void Factory_gives_the_declared_code_status_and_params(string code)
    {
        var definition = ErrorCatalog.All.Single(x => x.Code == code);
        var arguments = definition.Factory.GetParameters().Select(Sample).ToArray();

        var error = Assert.IsAssignableFrom<TaskerException>(definition.Factory.Invoke(null, arguments));

        Assert.Equal(code, error.Code);
        Assert.Equal(definition.Status, error.Status);
        Assert.Equal(definition.Params, error.Error.Params.Keys);
        Assert.NotEmpty(error.Message);
        if (definition.HasUsages)
            Assert.NotEmpty(error.Error.Usages);
    }

    [Fact]
    public void Refusals_are_made_only_by_the_catalog()
    {
        var source = Path.Combine(RepositoryRoot, "dotnet", "src");
        var offenders = Directory.EnumerateFiles(source, "*.cs", SearchOption.AllDirectories)
            .Where(x => !x.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !x.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(x => Path.GetFileName(x) != "TaskerErrors.cs")
            .Where(x => DirectThrow().IsMatch(File.ReadAllText(x)))
            .ToArray();

        Assert.Empty(offenders);
    }

    private static object? Sample(ParameterInfo parameter)
    {
        var type = parameter.ParameterType;
        if (type == typeof(string))
            return "x";
        if (type == typeof(int))
            return 1;
        if (type == typeof(string[]))
            return new[] { "x" };
        if (type == typeof(Guid))
            return Guid.Empty;
        if (type == typeof(ErrorField))
            return ErrorField.Title;
        if (type == typeof(ErrorSubject))
            return ErrorSubject.Status("x");
        if (type == typeof(EditLock))
            return new EditLock(LockedEntity.Status, Guid.Empty, new EditHolder("k", "n"), DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        if (type == typeof(Usages))
        {
            var usages = new Usages(ErrorSubject.Status("x"));
            usages.AddTasks(1);
            return usages;
        }

        throw new InvalidOperationException($"{parameter.Name}: no sample for {type.Name}");
    }

    [GeneratedRegex(@"^[a-z][a-zA-Z0-9]*(\.[a-z][a-zA-Z0-9]*)+$")]
    private static partial Regex CodeFormat();

    [GeneratedRegex(@"new\s+(Tasker\.Core\.)?(TaskerValidationException|TaskerConflictException|TaskerNotFoundException|TaskerForbiddenException|TaskerLockedException|TaskerRequiredFieldsException|TaskerUnauthenticatedException|TaskerInternalException|UnsupportedFormatException)\s*\(")]
    private static partial Regex DirectThrow();
}
