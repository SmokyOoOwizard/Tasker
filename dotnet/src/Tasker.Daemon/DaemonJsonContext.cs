using System.Text.Json.Serialization;
using Tasker.Global;

namespace Tasker.Daemon;

[JsonSerializable(typeof(GlobalSettings))]
[JsonSerializable(typeof(DaemonInfo))]
[JsonSerializable(typeof(DaemonStatus))]
[JsonSerializable(typeof(SyncRequest))]
[JsonSerializable(typeof(SyncResult))]
[JsonSerializable(typeof(UpgradeRequest))]
[JsonSerializable(typeof(UpgradeResult))]
public sealed partial class DaemonJsonContext : JsonSerializerContext
{
    public static void Install()
    {
        Tasker.Core.TaskerJson.GeneratedResolvers.Add(Default);
        Tasker.Core.TaskerJson.Options.TypeInfoResolverChain.Insert(0, Default);
    }
}
