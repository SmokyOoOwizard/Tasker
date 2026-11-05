namespace Tasker.Core.Tasks;

/// <summary>
/// У задачи нет значений обязательных полей (при создании и правке содержимого). Для клиентов это обычная ошибка
/// входных данных (400); отдельный тип нужен, чтобы консоль могла подсказать, чем поля задать.
/// </summary>
public class TaskerRequiredFieldsException : TaskerValidationException
{
    internal TaskerRequiredFieldsException(TaskerError error, string[] names) : base(error)
    {
        FieldNames = names;
    }

    public string[] FieldNames { get; }
}
