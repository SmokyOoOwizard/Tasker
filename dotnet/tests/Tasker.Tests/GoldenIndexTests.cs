using System.Diagnostics;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Tasker.Storage.Db;
using Xunit;

namespace Tasker.Tests;

public class GoldenIndexTests
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private static string Golden
    {
        get
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            {
                if (Directory.Exists(Path.Combine(directory.FullName, "tests", "golden", "workspace")))
                    return Path.Combine(directory.FullName, "tests", "golden");
            }
            throw new DirectoryNotFoundException("tests/golden not found above the test binaries");
        }
    }

    private static bool Updating => Environment.GetEnvironmentVariable("TASKER_GOLDEN_UPDATE") is { Length: > 0 } value && value != "0";

    private static void Copy(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from))
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        foreach (var directory in Directory.GetDirectories(from))
            Copy(directory, Path.Combine(to, Path.GetFileName(directory)));
    }

    private static SqliteConnection Open(string file)
    {
        var connection = new SqliteConnection($"Data Source={file};Pooling=False");
        connection.Open();
        return connection;
    }

    private static string DotSchema(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT type, sql FROM sqlite_master WHERE sql IS NOT NULL ORDER BY rowid";
        using var reader = command.ExecuteReader();
        var text = new StringBuilder();
        while (reader.Read())
        {
            var kind = reader.GetString(0);
            var sql = reader.GetString(1);
            if (kind == "table" && sql.StartsWith("CREATE TABLE ", StringComparison.Ordinal))
                sql = "CREATE TABLE IF NOT EXISTS " + sql["CREATE TABLE ".Length..];
            text.Append(sql).Append(";\n");
        }
        return text.ToString();
    }

    private static long NetMtime(string file) => new FileInfo(file).LastWriteTimeUtc.Ticks;

    private static string DumpIndex(string workspace)
    {
        var root = Path.Combine(workspace, ".tasker");
        var cache = Path.Combine(root, ".cache");
        var names = Directory.GetFileSystemEntries(cache).Select(x => Path.GetFileName(x)!).Order(StringComparer.Ordinal).ToArray();
        using var connection = Open(Path.Combine(cache, "index.db"));

        string Pragma(string name)
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA {name}";
            return Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture)!;
        }

        var lines = new List<string> { $"user_version {Pragma("user_version")}", $"journal_mode {Pragma("journal_mode")}" };
        foreach (var table in new[] { "files", "task_series", "task_links", "task_fields", "task_field_values" })
        {
            var columns = new List<string>();
            using (var info = connection.CreateCommand())
            {
                info.CommandText = $"PRAGMA table_info(\"{table}\")";
                using var reader = info.ExecuteReader();
                while (reader.Read())
                    columns.Add(reader.GetString(1));
            }
            lines.Add($"table {table} ({string.Join(", ", columns)})");
            var quoted = string.Join(", ", columns.Select(x => $"quote(\"{x}\")"));
            var order = table == "files" ? "path" : "path, rowid";
            using var select = connection.CreateCommand();
            select.CommandText = $"SELECT path, {quoted} FROM \"{table}\" ORDER BY {order}";
            using var rows = select.ExecuteReader();
            while (rows.Read())
            {
                var path = rows.GetString(0);
                var values = Enumerable.Range(1, columns.Count).Select(rows.GetString).ToArray();
                if (table == "files")
                {
                    var file = path.Split('/').Aggregate(root, Path.Combine);
                    var at = columns.IndexOf("mtime");
                    if (File.Exists(file) && values[at] == NetMtime(file).ToString(System.Globalization.CultureInfo.InvariantCulture))
                        values[at] = "<MTIME>";
                }
                lines.Add(string.Join(",", values));
            }
        }
        lines.Add("cache " + string.Join(" ", names));
        return string.Join("\n", lines) + "\n";
    }

    [Fact]
    public async Task The_cache_is_the_corpus_index_byte_for_byte()
    {
        var root = Directory.CreateTempSubdirectory("tasker-golden-index-").FullName;
        try
        {
            var home = Directory.CreateDirectory(Path.Combine(root, "home")).FullName;
            var workspace = Path.Combine(root, "golden");
            Copy(Path.Combine(Golden, "workspace"), workspace);

            var info = new ProcessStartInfo(TaskerProcess.Binary)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = root
            };
            foreach (var name in new[] { "TASKER_PROJECT", "TASKER_WIDTH", "TASKER_PROFILE", "TASKER_LANG", "COLUMNS", "LINES", "TASKER_GOLDEN_UPDATE" })
                info.Environment.Remove(name);
            info.Environment["TASKER_HOME"] = home;
            info.ArgumentList.Add("sync");
            info.ArgumentList.Add("-w");
            info.ArgumentList.Add(workspace);
            using (var process = Process.Start(info)!)
            {
                var output = process.StandardOutput.ReadToEndAsync();
                var error = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();
                await output;
                Assert.True(process.ExitCode == 0, await error);
            }

            var schemaFile = Path.Combine(Golden, "index", "schema.sql");
            var indexFile = Path.Combine(Golden, "index", "index.txt");
            string schema;
            using (var connection = Open(Path.Combine(workspace, ".tasker", ".cache", "index.db")))
                schema = DotSchema(connection);
            var dump = DumpIndex(workspace);
            if (Updating)
            {
                File.WriteAllText(schemaFile, schema, Utf8);
                File.WriteAllText(indexFile, dump, Utf8);
            }

            Assert.Equal(File.ReadAllText(schemaFile, Utf8), schema);
            var expected = File.ReadAllText(indexFile, Utf8).Split('\n');
            var actual = dump.Split('\n');
            var differing = Enumerable.Range(0, Math.Max(expected.Length, actual.Length))
                .Where(i => (i < expected.Length ? expected[i] : null) != (i < actual.Length ? actual[i] : null))
                .Take(5)
                .Select(i => $"line {i + 1}:\n  expected {(i < expected.Length ? expected[i] : "<none>")}\n  actual   {(i < actual.Length ? actual[i] : "<none>")}")
                .ToArray();
            Assert.True(differing.Length == 0, "index.txt differs:\n" + string.Join("\n", differing));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task A_new_sqlite_file_gets_the_schema_of_the_corpus()
    {
        var directory = Directory.CreateTempSubdirectory("tasker-golden-schema-").FullName;
        try
        {
            var file = Path.Combine(directory, "new.db");
            var options = new DbContextOptionsBuilder().UseSqlite(new SqliteConnectionStringBuilder { DataSource = file, Pooling = false }.ToString()).Options;
            await using (var context = new AppDbContext(options))
                await context.Database.EnsureCreatedAsync();

            string actual;
            using (var connection = Open(file))
                actual = DotSchema(connection);
            var schemaFile = Path.Combine(Golden, "sqlite", "schema.sql");
            if (Updating)
                File.WriteAllText(schemaFile, actual, Utf8);
            Assert.Equal(File.ReadAllText(schemaFile, Utf8), actual);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
