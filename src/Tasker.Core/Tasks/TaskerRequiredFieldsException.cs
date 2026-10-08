namespace Tasker.Core.Tasks;

/// <summary>
/// У задачи нет значений обязательных полей (при создании и правке содержимого). Для клиентов это обычная ошибка
/// входных данных (400); отдельный тип нужен, чтобы консоль могла подсказать, чем поля задать.
/// </summary>
/// <param name="names">Имена полей без значений.</param>
public class TaskerRequiredFieldsException(string[] names)
    : TaskerValidationException($"Required fields have no value: {string.Join(", ", names.Select(x => $"'{x}'"))}")
{
    public string[] FieldNames { get; } = names;
}
