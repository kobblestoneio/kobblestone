using k8s.Models;

using Kobblestone.Operator.Entities.V1Alpha1;

namespace Kobblestone.Operator.Utils;

/// <summary>
/// Configures a `Server` for Velocity modern forwarding (`spec.forwarding`), depending on its server type.
/// </summary>
public static class ServerForwarding
{
    public enum Support
    {
        /// <summary>The server type cannot trust a Velocity proxy.</summary>
        None,

        /// <summary>Paper and its forks: built-in Velocity support in <c>config/paper-global.yml</c>.</summary>
        Paper,

        /// <summary>Fabric and Quilt: the FabricProxy-Lite mod.</summary>
        Fabric
    }

    public const string PaperGlobalConfigFile = "paper-global.yml";

    private const string PaperSecretEnvVar = "PAPER_VELOCITY_SECRET";

    private const string ConfigVolume = "forwarding";

    /// <summary>
    /// The `paper-global.yml` of servers behind a proxy, as in itzg's Velocity example (docker-mc-proxy,
    /// <c>docs/velocity</c>). It is mounted to <c>/config</c>, which the image copies to <c>/data/config</c> on every
    /// start (<c>SYNC_SKIP_NEWER_IN_DESTINATION=false</c>), so it also applies on the first start and to servers that
    /// join a network with their data. Paper adds all other settings with their defaults.
    /// </summary>
    /// <remarks>
    /// The secret is a placeholder: the image replaces variables in synced files (<c>REPLACE_ENV_DURING_SYNC</c>), and
    /// Paper also reads <c>PAPER_VELOCITY_SECRET</c> itself. Either way, the ConfigMap never contains the secret.
    /// </remarks>
    public const string PaperGlobalConfig = $$"""
                                             # Managed by Kobblestone (spec.forwarding): replaced on every start of the server.
                                             proxies:
                                               velocity:
                                                 enabled: true
                                                 online-mode: true
                                                 secret: '${{{PaperSecretEnvVar}}}'

                                             """;

    /// <summary>
    /// Modrinth projects installed on Fabric/Quilt servers for forwarding.
    /// </summary>
    public static readonly IReadOnlyList<string> FabricModrinthProjects = ["fabricproxy-lite", "fabric-api"];

    public static Support GetSupport(ServerType? type) => type switch
    {
        ServerType.Paper or ServerType.Pufferfish or ServerType.Purpur or ServerType.Folia => Support.Paper,
        ServerType.Fabric or ServerType.Quilt => Support.Fabric,
        _ => Support.None
    };

    /// <summary>
    /// How forwarding is applied to the server; <see cref="Support.None"/> if it is not configured or not supported
    /// by the server type (the server then keeps verifying players itself).
    /// </summary>
    public static Support GetEffectiveSupport(V1Alpha1Server server) =>
        server.Spec.Forwarding is null ? Support.None : GetSupport(server.Spec.Type);

    public static bool IsEnabled(V1Alpha1Server server) => GetEffectiveSupport(server) is not Support.None;

    /// <summary>
    /// Name of the ConfigMap with the Paper configuration (only for Paper-based servers with forwarding).
    /// </summary>
    public static string ConfigMapName(V1Alpha1Server server) => $"{server.Name()}-forwarding";

    public static IEnumerable<V1EnvVar> GetEnvVars(V1Alpha1Server server)
    {
        var secretRef = server.Spec.Forwarding?.SecretRef;

        V1EnvVar Secret(string name) => new()
        {
            Name = name,
            ValueFrom = new V1EnvVarSource
            {
                SecretKeyRef = new V1SecretKeySelector { Name = secretRef!.Name, Key = secretRef.Key }
            }
        };

        return GetEffectiveSupport(server) switch
        {
            Support.Paper =>
            [
                Secret(PaperSecretEnvVar),
                // Always copy the managed paper-global.yml, even if the server's file is newer.
                new V1EnvVar { Name = "SYNC_SKIP_NEWER_IN_DESTINATION", Value = "false" }
            ],
            Support.Fabric => [Secret("FABRIC_PROXY_SECRET")],
            _ => []
        };
    }

    public static IEnumerable<V1Volume> GetVolumes(V1Alpha1Server server) =>
        GetEffectiveSupport(server) is Support.Paper
            ?
            [
                new V1Volume
                {
                    Name = ConfigVolume, ConfigMap = new V1ConfigMapVolumeSource { Name = ConfigMapName(server) }
                }
            ]
            : [];

    public static IEnumerable<V1VolumeMount> GetVolumeMounts(V1Alpha1Server server) =>
        GetEffectiveSupport(server) is Support.Paper
            ?
            [
                new V1VolumeMount
                {
                    Name = ConfigVolume,
                    MountPath = $"/config/{PaperGlobalConfigFile}",
                    SubPath = PaperGlobalConfigFile,
                    ReadOnlyProperty = true
                }
            ]
            : [];
}
