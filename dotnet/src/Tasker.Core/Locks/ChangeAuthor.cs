namespace Tasker.Core.Locks;

public static class ChangeAuthor
{
    private static readonly AsyncLocal<Scope?> current = new();

    public static EditHolder? Current => current.Value?.Holder;

    public static IDisposable Begin(EditHolder holder)
    {
        var scope = new Scope(holder);
        current.Value = scope;
        return scope;
    }

    private sealed class Scope(EditHolder holder) : IDisposable
    {
        public EditHolder? Holder { get; private set; } = holder;

        public void Dispose() => Holder = null;
    }
}
