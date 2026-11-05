namespace Tasker.Core;

/// <summary>Действие запрещено текущему пользователю (например, создание пользователей не админом). В API — 403.</summary>
public class TaskerForbiddenException : TaskerException
{
    internal TaskerForbiddenException(TaskerError error) : base(error)
    {
    }

    public override int Status => 403;
}
