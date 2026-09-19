using k8s.Models;

using KubeOps.Abstractions.Entities;
using KubeOps.Abstractions.Entities.Attributes;

using KubernetesCRDModelGen.Models.gateway.networking.k8s.io;

namespace Kobblestone.Operator.Entities.V1Alpha1;

[KubernetesEntity(Group = "kobblestone.io", ApiVersion = "v1alpha1", Kind = "Server")]
public sealed class
    V1Alpha1Server : CustomKubernetesEntity<V1Alpha1Server.V1Alpha1ServerSpec, V1Alpha1Server.V1Alpha1ServerStatus>,
    IConditionsStatus<V1Alpha1Server.V1Alpha1ServerStatus, V1Condition>
{
    public record V1Alpha1ServerSpec
    {
        public ServerState? State { get; set; }

        public ServerType? Type { get; set; }

        [AdditionalPrinterColumn(name: "Version")]
        [Required]
        public string Version { get; set; } = null!;

        [Required] public bool Eula { get; set; }

        public bool? Offline { get; set; }

        public IDictionary<string, string?>? Properties { get; set; }

        [Description("""
                     Configures settings related to the lifecycle of the `Server`.
                     """)]
        public V1Alpha1ServerLifecycle? Lifecycle { get; set; }

        public V1Alpha1ServerStorage? Storage { get; set; }

        public V1Alpha1ServerRoute? Route { get; set; }

        public V1Alpha1ServerAutoSleep? AutoSleep { get; set; }

        public V1Alpha1ServerAccess? Access { get; set; }

        public V1ResourceRequirements? Resources { get; set; }

        public V1Alpha1ServerService? Service { get; set; }

        public V1Alpha1ServerPod? Pod { get; set; }

        public V1Alpha1ServerRcon? Rcon { get; set; }

        public V1Alpha1ServerAutoCommands? AutoCommands { get; set; }

        public V1Alpha1ServerContent? Content { get; set; }

        public V1Alpha1ServerJvm? Jvm { get; set; }

        public V1Alpha1ServerTimeouts? Timeouts { get; set; }

        public V1Alpha1ServerNetworkPolicy? NetworkPolicy { get; set; }

        [Title("Gateway API")]
        [Description("""
                     Configures an optional `TCPRoute` exposing the `Server`s Minecraft port.

                     **Note**: Using `TCPRoute` requires at least [Gateway API v1.6.0](https://gateway-api.sigs.k8s.io/reference/api-spec/1.6/spec/) to be installed on your cluster.
                     """)]
        public V1Alpha1ServerTcpRoute? TcpRoute { get; set; }
    }

    public record V1Alpha1ServerStatus : IConditions<V1Condition>
    {
        [AdditionalPrinterColumn(name: "Status")]
        public ServerPhase Phase { get; set; } = ServerPhase.Stopped;

        [AdditionalPrinterColumn(name: "Type")]
        public ServerType Type { get; set; }

        public string? Address { get; set; }

        public IList<V1Condition> Conditions { get; set; } = [];

        [Description("""
                     Whether a managed `TCPRoute` exists.
                     """)]
        public bool? TcpRoute { get; set; }
    }

    public record V1Alpha1ServerStorage
    {
        public string? StorageClassName { get; set; }

        [Required] public string Size { get; set; } = null!;
    }

    public record V1Alpha1ServerLifecycle
    {
        [Description("""
                     Specifies when changes to `spec` are rolled out to running servers.

                     Default is `Immediate`.

                     Values:
                     - `Immediate`: Changes to `spec` are rolled out immediately. For example, if you update `spec.version`, the server will be restarted.
                     - `OnRestart`: Changes to `spec` are rolled out when the server is restarted. **Note**: This does not apply to restarts caused by the server crashing.
                     """)]
        public RolloutPolicy? RolloutPolicy { get; set; }
    }

    public record V1Alpha1ServerRoute
    {
        [Required] public V1LocalObjectReference ParentRef { get; set; } = null!;

        [Required]
        [AdditionalPrinterColumn(name: "Host")]
        public string Hostname { get; set; } = null!;
    }

    public record V1Alpha1ServerAutoSleep
    {
        public AutoSleepMode? Mode { get; set; }

        [RangeMinimum(1)] public int? TimeoutSeconds { get; set; }
    }

    public record V1Alpha1ServerAccess
    {
        public V1Alpha1ServerAccessWhitelist? Whitelist { get; set; }

        public V1Alpha1ServerAccessOps? Ops { get; set; }
    }

    public record V1Alpha1ServerAccessWhitelist
    {
        public bool? Enforce { get; set; }

        [XListType(XListType.Set)] public IList<string>? Users { get; set; }
    }

    public record V1Alpha1ServerAccessOps
    {
        [XListType(XListType.Set)] public IList<string>? Users { get; set; }
    }

    public record V1Alpha1ServerService
    {
        public int? Port { get; set; }

        public string? Type { get; set; }

        public string? ExternalTrafficPolicy { get; set; }

        public string? IpFamilyPolicy { get; set; }

        public IDictionary<string, string>? Annotations { get; set; }
    }

    public record V1Alpha1ServerPod
    {
        public string? Image { get; set; }

        public string? ImagePullPolicy { get; set; }

        public V1Affinity? Affinity { get; set; }

        public string? NodeName { get; set; }

        public IDictionary<string, string>? NodeSelector { get; set; }

        public IList<V1Toleration>? Tolerations { get; set; }

        public IList<V1Volume>? Volumes { get; set; }

        public IList<V1VolumeMount>? VolumeMounts { get; set; }

        public IList<V1EnvVar>? Env { get; set; }
    }

    public record V1Alpha1ServerRcon
    {
        public bool? Disabled { get; set; }

        public int? Port { get; set; }

        public V1SecretKeyRef? PasswordSecretRef { get; set; }
    }

    public record V1Alpha1ServerAutoCommands
    {
        public IList<string>? OnStartup { get; set; }

        public IList<string>? OnConnect { get; set; }

        public IList<string>? OnDisconnect { get; set; }

        public IList<string>? OnFirstConnect { get; set; }

        public IList<string>? OnLastDisconnect { get; set; }
    }

    public record V1Alpha1ServerContent
    {
        public IList<V1Alpha1ServerPlugin>? Plugins { get; set; }

        public IList<V1Alpha1ServerMod>? Mods { get; set; }

        public V1Alpha1ServerCurseForge? CurseForge { get; set; }

        public V1Alpha1ServerModrinth? Modrinth { get; set; }

        public string? PackwizUrl { get; set; }

        [XListType(XListType.Set)] public IList<string>? SpigotResources { get; set; }

        public string? IconUrl { get; set; }

        public V1Alpha1ServerResourcePack? ResourcePack { get; set; }

        public string? ModPackUrl { get; set; }
    }

    public record V1Alpha1ServerPlugin
    {
        [Required] public string Url { get; set; } = null!;
    }

    public record V1Alpha1ServerMod
    {
        [Required] public string Url { get; set; } = null!;
    }

    public record V1Alpha1ServerCurseForge
    {
        public V1SecretKeyRef? ApiKeySecretRef { get; set; }

        [XListType(XListType.Set)] public IList<string>? Files { get; set; }

        public string? ModPackUrl { get; set; }
    }

    public record V1Alpha1ServerModrinth
    {
        [XListType(XListType.Set)] public IList<string>? Projects { get; set; }
    }

    public record V1Alpha1ServerResourcePack
    {
        [Required] public string Url { get; set; } = null!;

        public string? Sha1 { get; set; }

        public bool? Enforce { get; set; }
    }

    public record V1Alpha1ServerJvm
    {
        [Description("""
                     Configures a specific version of the JVM to be used.

                     Default is `java25`.

                     **Note**: This corresponds to the Java part of the server image tag.
                     See [Image tags](https://docker-minecraft-server.readthedocs.io/en/latest/versions/java/#image-tags).
                     """)]
        [Example("java25")]
        public string? Version { get; set; }

        [Description("""
                     Configures memory settings for the JVM.
                     """)]
        public V1Alpha1ServerJvmMemory? Memory { get; set; }
    }

    public record V1Alpha1ServerJvmMemory
    {
        [Description("""
                     Configures the percentage of memory reserved for the heap.

                     Default is `75`.

                     **Note**: For low-memory servers, you might have to lower this.
                     """)]
        [RangeMinimum(1)]
        [RangeMaximum(99)]
        public int? HeapPercentage { get; set; }
    }

    public record V1Alpha1ServerTimeouts
    {
        [MultipleOf(5)] public int? MaxStartupTimeSeconds { get; set; }


        [MultipleOf(5)] public int? MaxUnavailabilityTimeSeconds { get; set; }
    }

    public record V1Alpha1ServerNetworkPolicy
    {
        [Description("""
                     If set to `true`, the server's default `NetworkPolicy` will not be created.

                     Default is `false`.
                     """)]
        public bool? Disabled { get; set; }

        [Description("""
                     Additional Ingress rules applied to the policy.
                     """)]
        public IList<V1NetworkPolicyIngressRule>? ExtraIngressRules { get; set; }
    }

    public record V1Alpha1ServerTcpRoute
    {
        [Description("""
                     References to gateways the `TCPRoute` should attach to.
                     """)]
        public IList<V1TCPRouteSpecParentRefs>? ParentRefs { get; set; }
    }
}