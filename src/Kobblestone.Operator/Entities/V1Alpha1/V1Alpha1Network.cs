using k8s.Models;

using KubeOps.Abstractions.Entities;
using KubeOps.Abstractions.Entities.Attributes;

using KubernetesCRDModelGen.Models.gateway.networking.k8s.io;

namespace Kobblestone.Operator.Entities.V1Alpha1;

[Description("""
             A `Network` runs a [Velocity](https://papermc.io/software/velocity) proxy that connects players to multiple `Server`s or other `Network`s.

             The proxy runs as a `Deployment`. The operator also manages its `Service`, configuration and forwarding `Secret` and, if configured, its `Route` and `TCPRoute`.
             """)]
[KubernetesEntity(Group = "kobblestone.io", ApiVersion = "v1alpha1", Kind = "Network")]
public sealed class
    V1Alpha1Network : CustomKubernetesEntity<V1Alpha1Network.V1Alpha1NetworkSpec, V1Alpha1Network.V1Alpha1NetworkStatus>
{
    public record V1Alpha1NetworkSpec
    {
        [Description("""
                     Configures the Velocity proxy.
                     """)]
        public V1Alpha1NetworkProxy? Proxy { get; set; }

        [Description("""
                     Configures a `Route` that makes the network reachable through a `Router` under a hostname.

                     Removing it deletes the `Route`.
                     """)]
        public V1Alpha1NetworkRoute? Route { get; set; }

        [Description("""
                     Servers the proxy forwards players to.

                     Players join the first available server in the given order, unless a forced host applies.
                     """)]
        public IList<V1Alpha1NetworkServer>? Servers { get; set; }

        [Description("""
                     Configures the `Service` of the proxy.
                     """)]
        public V1Alpha1NetworkService? Service { get; set; }

        [Title("Gateway API")]
        [Description("""
                     Configures an optional `TCPRoute` exposing the `Network`s Minecraft port.

                     **Note**: Using `TCPRoute` requires at least [Gateway API v1.6.0](https://gateway-api.sigs.k8s.io/reference/api-spec/1.6/spec/) to be installed on your cluster.
                     """)]
        public V1Alpha1NetworkTcpRoute? TcpRoute { get; set; }
    }

    public record V1Alpha1NetworkStatus
    {
        [Description("""
                     IP address of the proxy's `Service`. External addresses are preferred: the IP assigned by a load balancer, then the first of `externalIPs`, then the cluster IP.
                     """)]
        [AdditionalPrinterColumn(name: "Address")]
        public string? Address { get; set; }

        [Description("""
                     Whether a managed `TCPRoute` exists.
                     """)]
        public bool? TcpRoute { get; set; }
    }

    public record V1Alpha1NetworkProxy
    {
        [Description("""
                     Velocity version.

                     Default is `LATEST`.
                     """)]
        [AdditionalPrinterColumn(name: "Version")]
        public string? Version { get; set; }

        [Description("""
                     Number of proxy replicas.

                     Default is `1`.
                     """)]
        [AdditionalPrinterColumn(name: "Replicas")]
        public int? Replicas { get; set; }

        [Description("""
                     If set to `true`, the proxy runs in offline mode (`online-mode = false`): player accounts are not verified.

                     Default is `false`.
                     """)]
        public bool? Offline { get; set; }

        [Description("""
                     Compute resources of the proxy container.
                     """)]
        public V1ResourceRequirements? Resources { get; set; }

        [Description("""
                     Configures the proxy `Pod`s.
                     """)]
        public V1Alpha1NetworkProxyPod? Pod { get; set; }

        [Description("""
                     Custom `velocity.toml`.

                     The operator always sets `servers`, `forced-hosts` and `forwarding-secret-file` (and `online-mode` if `offline` is `true`), overriding the values given here.
                     """)]
        public string? Config { get; set; }

        [Description("""
                     Configures plugins and the server icon of the proxy.
                     """)]
        public V1Alpha1NetworkProxyContent? Content { get; set; }

        [Description("""
                     Configures the JVM of the proxy.
                     """)]
        public V1Alpha1NetworkProxyJvm? Jvm { get; set; }

        [Description("""
                     Reference to a key of a `Secret` containing the [forwarding secret](https://docs.papermc.io/velocity/player-information-forwarding/) shared with the backend servers.

                     If not set, a random secret is generated.

                     **Note**: Only read when the network's own `Secret` is created.
                     """)]
        public V1SecretKeyRef? ForwardingSecretRef { get; set; }
    }

    public record V1Alpha1NetworkRoute
    {
        [Description("""
                     Reference to the `Router` the `Route` is attached to.
                     """)]
        [Required] public V1LocalObjectReference ParentRef { get; set; } = null!;

        [Description("""
                     Hostname under which the network is reachable through the `Router`.
                     """)]
        [Required]
        public string Hostname { get; set; } = null!;
    }

    public record V1Alpha1NetworkServer
    {
        [Description("""
                     Name of the server in the proxy, e.g. for `/server <name>`.
                     """)]
        [Required] public string Name { get; set; } = null!;

        [Description("""
                     Reference to the `Server` or `Network` to forward players to.

                     Supported kinds: `Server` (default), `Network`.
                     """)]
        [Required] public V1TypedObjectReference TargetRef { get; set; } = null!;

        [Description("""
                     Hostname that sends players directly to this server (forced host).
                     """)]
        public string? Hostname { get; set; }
    }

    public record V1Alpha1NetworkService
    {
        [Description("""
                     Type of the proxy's `Service`, e.g. `ClusterIP` or `LoadBalancer`.

                     Default is `ClusterIP`.
                     """)]
        public string? Type { get; set; }

        [Description("""
                     Port on the `Service` that exposes Minecraft.

                     Default is `25565`.
                     """)]
        public int? Port { get; set; }

        [Description("""
                     External traffic policy of the proxy's `Service`.
                     """)]
        public string? ExternalTrafficPolicy { get; set; }

        [Description("""
                     IP family policy of the proxy's `Service`.
                     """)]
        public string? IpFamilyPolicy { get; set; }

        [Description("""
                     Annotations added to the proxy's `Service`, e.g. to configure a load balancer.
                     """)]
        public IDictionary<string, string>? Annotations { get; set; }
    }

    public record V1Alpha1NetworkProxyPod
    {
        [Description("""
                     Custom image for the proxy container.

                     Default is `itzg/mc-proxy`.
                     """)]
        public string? Image { get; set; }

        [Description("""
                     Image pull policy of the proxy container.
                     """)]
        public string? ImagePullPolicy { get; set; }

        [Description("""
                     Affinity of the proxy `Pod`.
                     """)]
        public V1Affinity? Affinity { get; set; }

        [Description("""
                     Name of the node the proxy `Pod` is scheduled on.
                     """)]
        public string? NodeName { get; set; }

        [Description("""
                     Node selector of the proxy `Pod`.
                     """)]
        public IDictionary<string, string>? NodeSelector { get; set; }

        [Description("""
                     Tolerations of the proxy `Pod`.
                     """)]
        public IList<V1Toleration>? Tolerations { get; set; }
    }

    public record V1Alpha1NetworkProxyPlugin
    {
        [Description("""
                     URL of the plugin `.jar` file.
                     """)]
        [Required] public string Url { get; set; } = null!;
    }

    public record V1Alpha1NetworkProxyContent
    {
        [Description("""
                     Plugins to install on the proxy.
                     """)]
        public IList<V1Alpha1NetworkProxyPlugin>? Plugins { get; set; }

        [Description("""
                     URL of an image used as the server icon of the proxy.
                     """)]
        public string? IconUrl { get; set; }
    }

    public record V1Alpha1NetworkProxyJvm
    {
        [Description("""
                     Configures memory settings for the JVM.
                     """)]
        public V1Alpha1NetworkProxyJvmMemory? Memory { get; set; }
    }

    public record V1Alpha1NetworkProxyJvmMemory
    {
        [Description("""
                     Configures the percentage of memory reserved for the heap.

                     Default is `75`.

                     **Note**: For low-memory deployments, you might have to lower this.
                     """)]
        [RangeMinimum(1)][RangeMaximum(99)] public int? HeapPercentage { get; set; }
    }

    public record V1Alpha1NetworkTcpRoute
    {
        [Description("""
                     References to gateways the `TCPRoute` should attach to.
                     """)]
        public IList<V1TCPRouteSpecParentRefs>? ParentRefs { get; set; }
    }
}
