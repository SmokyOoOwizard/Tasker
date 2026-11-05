namespace Tasker.Core;

/// <summary>Неверные входные данные (пустое имя, ссылка на чужой или несуществующий статус и т.п.). В API — 400.</summary>
public class TaskerValidationException : TaskerException
{
    internal TaskerValidationException(TaskerError error) : base(error)
    {
    }

    public override int Status => 400;

    public TaskerValidationException Reworded(string message, params (string Name, object? Value)[] context) =>
        new(Error.Reworded(message, context));
}
