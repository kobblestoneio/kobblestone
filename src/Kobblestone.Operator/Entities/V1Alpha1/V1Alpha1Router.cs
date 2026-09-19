using k8s.Models;

using KubeOps.Abstractions.Entities;
using KubeOps.Abstractions.Entities.Attributes;

using KubernetesCRDModelGen.Models.gateway.networking.k8s.io;

namespace Kobblestone.Operator.Entities.V1Alpha1;

[Description("""
             A `Router` defines a lightweight proxy routing Minecraft traffic based on the [Server Address](https://minecraft.wiki/w/Java_Edition_protocol/Packets#Handshake) field of handshakes.

             Usually, routers sit on the edge of your Minecraft infrastructure and may very well be exposed to the internet.

             Routes are configured via `Route` objects.
             """)]
[KubernetesEntity(Group = "kobblestone.io", ApiVersion = "v1alpha1", Kind = "Router")]
public sealed class
    V1Alpha1Router : CustomKubernetesEntity<V1Alpha1Router.V1Alpha1RouterSpec, V1Alpha1Router.V1Alpha1RouterStatus>
{
    public record V1Alpha1RouterSpec
    {
        [Title("Replicas")]
        [Description("""
                     Number of replicas this `Router` should run.
                     """)]
        [AdditionalPrinterColumn(name: "Replicas")]
        public int? Replicas { get; set; }

        [Title("Hostnames")]
        [Description("""
                     List of hostnames this `Router` accepts. Supports wildcard.

                     **Note**: An empty list does **not** mean "accept any", it means "accept none". Use `*` entry to accept any.
                     """)]
        [Example("[\"*.example.com\", \"*.foo.com\"]", Json = true)]
        [XListType(XListType.Set)]
        public IList<string>? Hostnames { get; set; }

        [Title("Service")]
        [Description("""
                     Configures the spec of the managed `Service` object.

                     By default, every `Router` creates `Service` of type `ClusterIP`.

                     If you want to expose your `Router` externally, you may switch to `LoadBalancer`.
                     """)]
        public V1Alpha1RouterService? Service { get; set; }

        [Title("Resources")]
        [Description("""
                     Resource requirements for the `Router` pods.
                     """)]
        [ExternalDocs("https://kubernetes.io/docs/reference/kubernetes-api/core/pod-v1/#PodSpec")]
        public V1ResourceRequirements? Resources { get; set; }

        [Title("Static Routes")]
        [Description("""
                     Statically defined routes that are always available on the `Router` (not defined by `Route` objects).
                     """)]
        public IList<V1Alpha1RouterStaticRoute>? StaticRoutes { get; set; }

        [Title("Networking")]
        [Description("""
                     Networking options.
                     """)]
        public V1Alpha1RouterNetworking? Networking { get; set; }

        [Title("Pod")]
        [Description("""
                     Configures the spec of the managed `Pod` objects.
                     """)]
        public V1Alpha1RouterPod? Pod { get; set; }

        [Title("Gateway API")]
        [Description("""
                     Configures an optional `TCPRoute` exposing the `Router`s Minecraft port.

                     **Note**: Using `TCPRoute` requires at least [Gateway API v1.6.0](https://gateway-api.sigs.k8s.io/reference/api-spec/1.6/spec/) to be installed on your cluster.
                     """)]
        public V1Alpha1RouterTcpRoute? TcpRoute { get; set; }
    }

    public record V1Alpha1RouterStatus
    {
        [Description("""
                     IP address of the `Router`.
                     """)]
        [AdditionalPrinterColumn(name: "Address")]
        public string? Address { get; set; }

        [Description("""
                     Conditions of the `Router`.
                     """)]
        public IList<V1Condition> Conditions { get; set; } = [];

        [Description("""
                     Whether a managed `TCPRoute` exists.
                     """)]
        public bool? TcpRoute { get; set; }
    }

    public record V1Alpha1RouterService
    {
        [ExternalDocs("https://kubernetes.io/docs/reference/kubernetes-api/core/service-v1/#ServiceSpec")]
        public string? Type { get; set; }

        [Description("""
                     Port on the `Service` that exposes Minecraft.

                     Default is `25565`.
                     """)]
        [RangeMinimum(1)]
        [RangeMaximum(ushort.MaxValue)]
        public int? Port { get; set; }

        [ExternalDocs("https://kubernetes.io/docs/reference/kubernetes-api/core/service-v1/#ServiceSpec")]
        public string? ExternalTrafficPolicy { get; set; }

        [ExternalDocs("https://kubernetes.io/docs/reference/kubernetes-api/core/service-v1/#ServiceSpec")]
        public string? IpFamilyPolicy { get; set; }

        [ExternalDocs(
            "https://kubernetes.io/docs/reference/kubernetes-api/definitions/object-meta-v1-meta/#ObjectMeta")]
        public IDictionary<string, string>? Annotations { get; set; }
    }

    public record V1Alpha1RouterStaticRoute
    {
        [Description("""
                     Hostname.
                     """)]
        [Example("server.example.com")]
        [Format("hostname")]
        [Required]
        public string Hostname { get; set; } = null!;

        [Description("""
                     Address (IP+port) of the target backend.
                     """)]
        [Example("server.example.svc.cluster.local:25565")]
        [Required]
        public string Backend { get; set; } = null!;
    }

    public record V1Alpha1RouterNetworking
    {
        [Description("""
                     Configures the max. number of connections per second this `Router` accepts.

                     Default is `1`.
                     """)]
        [RangeMinimum(1)]
        public int? MaxConnectionsPerSecond { get; set; }

        [Description("""
                     Configures a timeout when connecting to backend servers. Default is no timeout.
                     """)]
        [Example("5s")]
        public string? BackendDialTimeout { get; set; }

        [Description("""
                     List of IPs and networks (CIDR) to allow. Allows everything by default.
                     """)]
        [Example("[\"1.2.3.4\", \"0.0.0.0/0\"]", Json = true)]
        [XListType(XListType.Set)]
        public IList<string>? AllowAddresses { get; set; }

        [Description("""
                     List of IPs and networks (CIDR) to deny.
                     """)]
        [Example("[\"1.2.3.4\", \"0.0.0.0/0\"]", Json = true)]
        [XListType(XListType.Set)]
        public IList<string>? DenyAddresses { get; set; }

        [Description("""
                     Configures the PROXY protocol.
                     """)]
        public V1Alpha1RouterProxyProtocol? ProxyProtocol { get; set; }
    }

    public record V1Alpha1RouterProxyProtocol
    {
        [Description("""
                     Whether the `Router` should accept the PROXY protocol.

                     Default is `false`.
                     """)]
        public bool? Receive { get; set; }

        [Description("""
                     Whether the `Router` should send the PROXY protocol to backends.

                     Default is `false`.
                     """)]
        public bool? Send { get; set; }

        [Description("""
                     List of trusted proxy IPs and networks (CIDR).

                     An empty list accepts all IPs.
                     """)]
        [XListType(XListType.Set)]
        public IList<string>? TrustedProxies { get; set; }
    }

    public record V1Alpha1RouterPod
    {
        [Description("""
                     Allows specifying a custom image for the `Router` pod.
                     """)]
        [ExternalDocs("https://kubernetes.io/docs/reference/kubernetes-api/core/pod-v1/#Container")]
        public string? Image { get; set; }

        [ExternalDocs("https://kubernetes.io/docs/reference/kubernetes-api/core/pod-v1/#Container")]
        public string? ImagePullPolicy { get; set; }

        [ExternalDocs("https://kubernetes.io/docs/reference/kubernetes-api/core/pod-v1/#Affinity")]
        public V1Affinity? Affinity { get; set; }

        [ExternalDocs("https://kubernetes.io/docs/reference/kubernetes-api/core/pod-v1/#PodSpec")]
        public string? NodeName { get; set; }

        [ExternalDocs("https://kubernetes.io/docs/reference/kubernetes-api/core/pod-v1/#PodSpec")]
        public IDictionary<string, string>? NodeSelector { get; set; }

        [ExternalDocs("https://kubernetes.io/docs/reference/kubernetes-api/core/pod-v1/#PodSpec")]
        public IList<V1Toleration>? Tolerations { get; set; }
    }

    public record V1Alpha1RouterTcpRoute
    {
        [Description("""
                     References to gateways the `TCPRoute` should attach to.
                     """)]
        public IList<V1TCPRouteSpecParentRefs>? ParentRefs { get; set; }
    }
}