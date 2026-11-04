using System.Text.Json.Nodes;
using Microsoft.OpenApi;
using Tasker.Web;
using Xunit;

namespace Tasker.Tests;

public class ApiSchemaTests
{
    private static bool Updating => Environment.GetEnvironmentVariable("TASKER_GOLDEN_UPDATE") is { Length: > 0 } value && value != "0";

    private static string SchemaPath
    {
        get
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "dotnet", "Tasker.slnx")))
                    return Path.Combine(directory.FullName, "docs", "api", "openapi.json");
            }

            throw new InvalidOperationException("Repository root is not found");
        }
    }

    private static readonly Lazy<Task<string>> Generated = new(ApiSchemaGenerator.Build);

    [Fact]
    public async Task Committed_schema_matches_the_current_api()
    {
        var actual = await Generated.Value;
        if (Updating)
        {
            File.WriteAllText(SchemaPath, actual);
            return;
        }

        Assert.True(File.Exists(SchemaPath), $"{SchemaPath} is missing: run the tests with TASKER_GOLDEN_UPDATE=1");
        var expected = File.ReadAllText(SchemaPath).ReplaceLineEndings("\n");
        Assert.True(expected == actual, "docs/api/openapi.json differs from the current API: run the tests with TASKER_GOLDEN_UPDATE=1 and commit the file");
    }

    [Fact]
    public async Task Schema_passes_openapi_validation()
    {
        var result = OpenApiDocument.Parse(await Generated.Value, "json");

        Assert.NotNull(result.Document);
        Assert.Equal(0, result.Diagnostic?.Errors.Count ?? 0);
        Assert.Equal(0, result.Document!.Validate(ValidationRuleSet.GetDefaultRuleSet())?.Count() ?? 0);
    }

    [Fact]
    public async Task Every_route_is_described()
    {
        var document = JsonNode.Parse(await Generated.Value)!;
        var described = ((JsonObject)document["paths"]!)
            .SelectMany(item => ((JsonObject)item.Value!).Where(x => x.Key != "servers").Select(x => $"{x.Key} {item.Key}"))
            .ToHashSet();

        var routes = (await ApiSchemaGenerator.Routes(TaskerMode.Server))
            .Concat(await ApiSchemaGenerator.Routes(TaskerMode.Local))
            .Concat(await ApiSchemaGenerator.DaemonRoutes())
            .Where(x => !x.Contains("/mcp", StringComparison.Ordinal))
            .Distinct()
            .ToArray();

        Assert.All(routes, route => Assert.Contains(route, described));
        Assert.True(routes.Length > 100, $"only {routes.Length} routes found");
    }

    [Fact]
    public async Task Operations_have_unique_ids_and_references_resolve()
    {
        var document = JsonNode.Parse(await Generated.Value)!;
        var ids = new List<string>();
        foreach (var (_, item) in (JsonObject)document["paths"]!)
        {
            foreach (var (method, node) in (JsonObject)item!)
            {
                if (method != "servers")
                    ids.Add(node!["operationId"]!.GetValue<string>());
            }
        }

        Assert.Equal(ids.Count, ids.Distinct().Count());

        var schemas = (JsonObject)document["components"]!["schemas"]!;
        var unresolved = new List<string>();
        void Visit(JsonNode? node)
        {
            switch (node)
            {
                case JsonArray array:
                    foreach (var item in array)
                        Visit(item);
                    break;
                case JsonObject obj:
                    foreach (var (key, value) in obj)
                    {
                        if (key == "$ref")
                        {
                            if (!schemas.ContainsKey(value!.GetValue<string>()["#/components/schemas/".Length..]))
                                unresolved.Add(value.GetValue<string>());
                        }
                        else
                        {
                            Visit(value);
                        }
                    }
                    break;
            }
        }

        Visit(document);
        Assert.Empty(unresolved);
        Assert.DoesNotContain(schemas, x => x.Value is JsonObject { Count: 0 });
    }
}
