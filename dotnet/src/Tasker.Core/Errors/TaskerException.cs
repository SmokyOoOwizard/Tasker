namespace Tasker.Core;

public abstract class TaskerException(TaskerError error) : Exception(error.Message)
{
    public TaskerError Error { get; } = error;

    public abstract int Status { get; }

    public string Code => Error.Code;
}

public sealed class TaskerUnauthenticatedException : TaskerException
{
    internal TaskerUnauthenticatedException(TaskerError error) : base(error)
    {
    }

    public override int Status => 401;
}

public sealed class TaskerInternalException : TaskerException
{
    internal TaskerInternalException(TaskerError error) : base(error)
    {
    }

    public override int Status => 500;
}
