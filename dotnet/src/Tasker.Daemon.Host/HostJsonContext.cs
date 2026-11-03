using System.Text.Json.Serialization;

namespace Tasker.Daemon.Host;

[JsonSerializable(typeof(WireMessage))]
internal sealed partial class HostJsonContext : JsonSerializerContext
{
    public static void Install()
    {
        Tasker.Core.TaskerJson.GeneratedResolvers.Add(Default);
        Tasker.Core.TaskerJson.Options.TypeInfoResolverChain.Insert(0, Default);
    }
}
