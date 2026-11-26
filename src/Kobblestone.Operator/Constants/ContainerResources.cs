using k8s.Models;

namespace Kobblestone.Operator.Constants;

/// <summary>
/// Default compute resources of the containers Kobblestone runs for itself, used when the resource does not configure
/// them. Every container gets requests and a memory limit, so namespaces whose `ResourceQuota` requires them accept the
/// `Pod`s. Workloads users size themselves (the Minecraft server, the Velocity proxy, `Router`s, `Bot`s) have no
/// defaults: a default memory limit would change their behavior, e.g. the server's heap size.
/// </summary>
/// <remarks>
/// Properties return new instances, since the models are mutable.
/// </remarks>
public static class ContainerResources
{
    /// <summary>
    /// Helpers: the containers of backup, restore, cleanup and authorization `Job`s and of `FileBrowser`s (mc-backup,
    /// ubuntu, aws-cli, SFTPGo). The largest is the aws-cli, which peaks at about 150 MiB while transferring an archive,
    /// independent of its size.
    /// </summary>
    public static V1ResourceRequirements Helper => new()
    {
        Requests = new Dictionary<string, ResourceQuantity> { ["cpu"] = new("10m"), ["memory"] = new("64Mi") },
        Limits = new Dictionary<string, ResourceQuantity> { ["memory"] = new("256Mi") }
    };

    /// <summary>
    /// The mc-router sidecar of `Server`s (auto-sleep and forwarding), which uses about 10 MiB.
    /// </summary>
    public static V1ResourceRequirements Sidecar => new()
    {
        Requests = new Dictionary<string, ResourceQuantity> { ["cpu"] = new("10m"), ["memory"] = new("32Mi") },
        Limits = new Dictionary<string, ResourceQuantity> { ["memory"] = new("128Mi") }
    };
}
