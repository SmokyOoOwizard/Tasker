namespace Tasker.Daemon;

public sealed class ProgramWatch(Func<string, bool>? exists = null)
{
    public static readonly TimeSpan Poll = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan Grace = TimeSpan.FromSeconds(60);

    private readonly Func<string, bool> _exists = exists ?? File.Exists;
    private DateTime? _missingSince;

    public bool IsGone(string program, DateTime now)
    {
        if (!Launcher.IsInAppBundle(program) || _exists(program))
        {
            _missingSince = null;
            return false;
        }

        _missingSince ??= now;
        return now - _missingSince >= Grace;
    }
}
