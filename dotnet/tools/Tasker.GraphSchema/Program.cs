using Tasker.Web.Graph;

var check = args.Contains("--check");
var path = args.FirstOrDefault(x => !x.StartsWith("--", StringComparison.Ordinal)) ?? DefaultPath();

var schema = await GraphSetup.Print();
var current = File.Exists(path) ? (await File.ReadAllTextAsync(path)).ReplaceLineEndings("\n") : null;

if (check)
{
    if (current == schema)
    {
        Console.WriteLine($"{path}: up to date");
        return 0;
    }

    Console.Error.WriteLine($"{path} differs from the schema in code: run dotnet run --project dotnet/tools/Tasker.GraphSchema and commit the file");
    return 1;
}

if (current == schema)
{
    Console.WriteLine($"{path}: unchanged");
    return 0;
}

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
await File.WriteAllTextAsync(path, schema);
Console.WriteLine($"{path}: written");
return 0;

static string DefaultPath()
{
    for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir != null; dir = dir.Parent)
    {
        if (File.Exists(Path.Combine(dir.FullName, "VERSION")) && Directory.Exists(Path.Combine(dir.FullName, "dotnet")))
            return Path.Combine(dir.FullName, "graphql", "schema.graphql");
    }

    throw new InvalidOperationException("Repository root not found: pass the schema path as an argument");
}
