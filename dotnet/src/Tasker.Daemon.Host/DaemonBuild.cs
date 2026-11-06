using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace Tasker.Daemon.Host;

internal static class DaemonBuild
{
    public static string Version { get; } =
        typeof(DaemonBuild).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion is { Length: > 0 } version
            ? version.Split('+')[0]
            : typeof(DaemonBuild).Assembly.GetName().Version?.ToString() ?? "unknown";

    public static string Id { get; } = Compute();

    private static string Compute()
    {
        var seen = new Dictionary<string, Guid>();
        var pending = new Stack<Assembly>([typeof(DaemonBuild).Assembly]);
        while (pending.TryPop(out var assembly))
        {
            if (!seen.TryAdd(assembly.GetName().Name ?? assembly.FullName ?? "", assembly.ManifestModule.ModuleVersionId))
                continue;

            foreach (var reference in assembly.GetReferencedAssemblies())
            {
                if (reference.Name is { } name && name.StartsWith("Tasker", StringComparison.OrdinalIgnoreCase) && !seen.ContainsKey(name))
                    pending.Push(Assembly.Load(reference));
            }
        }

        var text = string.Join('\n', seen.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => $"{x.Key}:{x.Value:N}"));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..8];
    }
}
