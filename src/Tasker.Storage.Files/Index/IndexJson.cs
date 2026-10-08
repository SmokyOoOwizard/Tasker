using System.Text.Json;
using System.Text.Json.Serialization;
using Tasker.Core.Boards;
using Tasker.Core.Fields;
using Tasker.Core.Links;
using Tasker.Core.Projects;
using Tasker.Core.Statuses;
using Tasker.Core.Tasks;
using Tasker.Core.TaskSeries;
using Tasker.Core.Users;

namespace Tasker.Storage.Files.Index;

/// <summary>
/// JSON сущностей в индексе (<c>files.data</c>), сгенерированный на этапе сборки. Отражение System.Text.Json на каждый вызов <c>tasker</c>
/// стоило десятков миллисекунд (создание описаний типов, динамические методы, JIT); формат тот же, что у
/// <c>new JsonSerializerOptions(JsonSerializerDefaults.General)</c>: схему индекса поднимать не нужно.
/// </summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.General)]
[JsonSerializable(typeof(Project))]
[JsonSerializable(typeof(User))]
[JsonSerializable(typeof(Status))]
[JsonSerializable(typeof(StatusSet))]
[JsonSerializable(typeof(TaskType))]
[JsonSerializable(typeof(Board))]
[JsonSerializable(typeof(Series))]
[JsonSerializable(typeof(TaskItem))]
[JsonSerializable(typeof(LinkType))]
[JsonSerializable(typeof(FieldDefinition))]
[JsonSerializable(typeof(FieldEnum))]
internal sealed partial class IndexJsonContext : JsonSerializerContext
{
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, typeof(T), Default);

    public static T Deserialize<T>(string json) => (T)JsonSerializer.Deserialize(json, typeof(T), Default)!;

    /// <summary>Массив строк (id в параметрах запросов) — без отражения.</summary>
    public static string Array(IEnumerable<string> values) => "[" + string.Join(",", values.Select(x => "\"" + x + "\"")) + "]";
}
