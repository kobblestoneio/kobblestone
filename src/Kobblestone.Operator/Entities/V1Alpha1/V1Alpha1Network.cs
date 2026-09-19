using k8s.Models;

using KubeOps.Abstractions.Entities;
using KubeOps.Abstractions.Entities.Attributes;

using KubernetesCRDModelGen.Models.gateway.networking.k8s.io;

namespace Kobblestone.Operator.Entities.V1Alpha1;

[KubernetesEntity(Group = "kobblestone.io", ApiVersion = "v1alpha1", Kind = "Network")]
public sealed class
    V1Alpha1Network : CustomKubernetesEntity<V1Alpha1Network.V1Alpha1NetworkSpec, V1Alpha1Network.V1Alpha1NetworkStatus>
{
    public record V1Alpha1NetworkSpec
    {
        public V1Alpha1NetworkProxy? Proxy { get; set; }

        public V1Alpha1NetworkRoute? Route { get; set; }

        public IList<V1Alpha1NetworkServer>? Servers { get; set; }

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
                     Whether a managed `TCPRoute` exists.
                     """)]
        public bool? TcpRoute { get; set; }
    }

    public record V1Alpha1NetworkProxy
    {
        [AdditionalPrinterColumn(name: "Version")]
        public string? Version { get; set; }

        [AdditionalPrinterColumn(name: "Replicas")]
        public int? Replicas { get; set; }

        public bool? Offline { get; set; }

        public V1ResourceRequirements? Resources { get; set; }

        public V1Alpha1NetworkProxyPod? Pod { get; set; }

        public string? Config { get; set; }

        public V1Alpha1NetworkProxyContent? Content { get; set; }

        public V1Alpha1NetworkProxyJvm? Jvm { get; set; }

        public V1SecretKeyRef? ForwardingSecretRef { get; set; }
    }

    public record V1Alpha1NetworkRoute
    {
        [Required] public V1LocalObjectReference ParentRef { get; set; } = null!;

        [Required]
        [AdditionalPrinterColumn(name: "Host")]
        public string Hostname { get; set; } = null!;
    }

    public record V1Alpha1NetworkServer
    {
        [Required] public string Name { get; set; } = null!;

        [Required] public V1TypedObjectReference TargetRef { get; set; } = null!;

        public string? Hostname { get; set; }
    }

    public record V1Alpha1NetworkService
    {
        public string? Type { get; set; }

        public int? Port { get; set; }

        public string? ExternalTrafficPolicy { get; set; }

        public string? IpFamilyPolicy { get; set; }

        public IDictionary<string, string>? Annotations { get; set; }
    }

    public record V1Alpha1NetworkProxyPod
    {
        public string? Image { get; set; }

        public string? ImagePullPolicy { get; set; }

        public V1Affinity? Affinity { get; set; }

        public string? NodeName { get; set; }

        public IDictionary<string, string>? NodeSelector { get; set; }

        public IList<V1Toleration>? Tolerations { get; set; }
    }

    public record V1Alpha1NetworkProxyPlugin
    {
        [Required] public string Url { get; set; } = null!;
    }

    public record V1Alpha1NetworkProxyContent
    {
        public IList<V1Alpha1NetworkProxyPlugin>? Plugins { get; set; }

        public string? IconUrl { get; set; }
    }

    public record V1Alpha1NetworkProxyJvm
    {
        public V1Alpha1NetworkProxyJvmMemory? Memory { get; set; }
    }

    public record V1Alpha1NetworkProxyJvmMemory
    {
        [RangeMinimum(1)] [RangeMaximum(99)] public int? HeapPercentage { get; set; }
    }

    public record V1Alpha1NetworkTcpRoute
    {
        [Description("""
                     References to gateways the `TCPRoute` should attach to.
                     """)]
        public IList<V1TCPRouteSpecParentRefs>? ParentRefs { get; set; }
    }
}