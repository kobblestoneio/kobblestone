using k8s.Models;

using KubeOps.Abstractions.Entities;
using KubeOps.Abstractions.Entities.Attributes;

using KubernetesCRDModelGen.Models.gateway.networking.k8s.io;

namespace Kobblestone.Operator.Entities.V1Alpha1;

[Description("""
             A `Bot` represents a single instance of a Minecraft bot.

             If the `Bot` should connect to an online server, it needs to reference an authorized `Account` (which is allowed to access the server).

             The logic of `Bot`s is programmed by the `BotBehavior`s they are referencing (`behaviors`).
             """)]
[KubernetesEntity(Group = "kobblestone.io", ApiVersion = "v1alpha1", Kind = "Bot")]
[GenericAdditionalPrinterColumn(".spec.auth.accountRef.name", "Account", "string")]
public sealed class V1Alpha1Bot : CustomKubernetesEntity<V1Alpha1Bot.V1Alpha1BotSpec, V1Alpha1Bot.V1Alpha1BotStatus>
{
    public record V1Alpha1BotSpec
    {
        [Description("""
                     Whether the `Bot` should be inactive (not running).

                     Default is `false`.
                     """)]
        public bool? Inactive { get; set; }

        [Title("Target")]
        [Description("""
                     Target this `Bot` should be connected to.
                     """)]
        [Required]
        public V1Alpha1BotTarget Target { get; set; } = null!;

        [Title("Behavior")]
        [Description("""
                     Behaviors that should apply to this `Bot`.
                     """)]
        public IList<V1Alpha1BotBehaviorItem>? Behaviors { get; set; }

        [Title("Viewer")]
        [Description("""
                     Configures a live viewer that exposes the bots view as a web page via HTTP.
                     """)]
        public V1Alpha1BotViewer? Viewer { get; set; }

        [Description("""
                     Username of the `Bot` when connecting without authentication (offline).

                     Default is `Player`.
                     """)]
        public string? OfflineUsername { get; set; }

        [Title("Authentication")]
        [Description("""
                     Configures authentication for online servers.

                     If not specified, the `Bot` can only connect to offline servers.
                     """)]
        public V1Alpha1BotAuth? Auth { get; set; }

        [Title("Resources")]
        [Description("""
                     Resource requirements for the `Bot` pod.
                     """)]
        [ExternalDocs("https://kubernetes.io/docs/reference/kubernetes-api/core/pod-v1/#PodSpec")]
        public V1ResourceRequirements? Resources { get; set; }

        [Title("Service")]
        [Description("""
                     Configures the `Bot`s `Service`.
                     """)]
        public V1Alpha1BotService? Service { get; set; }

        [Title("Pod")]
        [Description("""
                     Configures the spec of the managed `Pod` object.
                     """)]
        public V1Alpha1BotPod? Pod { get; set; }
    }

    public record V1Alpha1BotStatus
    {
        [Description("""
                     Whether an `HTTPRoute` exists.
                     """)]
        public bool? HttpRoute { get; set; }
    }

    public record V1Alpha1BotTarget
    {
        [Description("""
                     Reference to a target object.

                     This may be one of the following types: `Server`, `Network`.

                     If you want to connect through `Router` instances, you must set the `host` parameter to a hostname that routes accordingly.
                     """)]
        public V1TypedObjectReference? TargetRef { get; set; }

        [Description("""
                     Custom host (IP or hostname) of the target server.

                     This is required when `targetRef` is not specified. Otherwise, it overrides the target host implied by `targetRef`.
                     """)]
        public string? Host { get; set; }

        [Description("""
                     Custom port of the target server.

                     Can be specified together with `targetRef` to override the implied port.
                     """)]
        public int? Port { get; set; }
    }

    public record V1Alpha1BotAuth
    {
        [Description("""
                     Reference to an `Account` object this `Bot` will use for authentication.

                     **Note**: The `Account` has to be authorized (`status.phase=Authorized`), otherwise the server connection will fail.
                     """)]
        [Required]
        public V1LocalObjectReference AccountRef { get; set; } = null!;

        [Description("""
                     If enabled, profile certificates from the referenced `Account` will be used to sign chat messages.

                     Default is `false`.
                     """)]
        public bool? EnableChatSigning { get; set; }
    }

    public record V1Alpha1BotBehaviorItem
    {
        [Description("""
                     Reference to the `BotBehavior` object.
                     """)]
        [Required]
        public V1LocalObjectReference BehaviorRef { get; set; } = null!;

        [Description("""
                     Parameters forwarded to the `BotBehavior`.
                     """)]
        public IDictionary<string, object?>? Parameters { get; set; }
    }

    public enum BotViewerMode
    {
        BirdsEye,
        FirstPerson
    }

    public record V1Alpha1BotViewerHttpRoute
    {
        [Description("""
                     Hostnames for the `HTTPRoute`.
                     """)]
        [XListType(XListType.Set)]
        public IList<string>? Hostnames { get; set; }

        [Description("""
                     Custom matches for the `HTTPRoute`.

                     If not specified all requests are matched.
                     """)]
        public IList<V1HTTPRouteSpecRulesMatches>? Matches { get; set; }

        [Description("""
                     References to gateways the `HTTPRoute` should attach to.
                     """)]
        public IList<V1HTTPRouteSpecParentRefs>? ParentRefs { get; set; }
    }

    public record V1Alpha1BotService
    {
        [Description("""
                     Port of the `Bot`s status endpoint.

                     Default is `8080`.
                     """)]
        public int? Port { get; set; }

        [ExternalDocs("https://kubernetes.io/docs/reference/kubernetes-api/core/service-v1/#ServiceSpec")]
        public string? Type { get; set; }

        [ExternalDocs("https://kubernetes.io/docs/reference/kubernetes-api/core/service-v1/#ServiceSpec")]
        public string? ExternalTrafficPolicy { get; set; }

        [ExternalDocs("https://kubernetes.io/docs/reference/kubernetes-api/core/service-v1/#ServiceSpec")]
        public string? IpFamilyPolicy { get; set; }

        [ExternalDocs(
            "https://kubernetes.io/docs/reference/kubernetes-api/definitions/object-meta-v1-meta/#ObjectMeta")]
        public IDictionary<string, string>? Annotations { get; set; }
    }

    public record V1Alpha1BotViewer
    {
        [Description("""
                     Determines in which mode mode the viewer launches.

                     Default is `BirdsEye`.
                     """)]
        public BotViewerMode? Mode { get; set; }

        [Description("""
                     View distance of the viewer.

                     Default is `4`.
                     """)]
        [RangeMinimum(1)]
        public int? ViewDistance { get; set; }

        [Description("""
                     Port of the viewer exposed on the `Bot`s `Service`.

                     Default is `3000`.
                     """)]
        public int? Port { get; set; }

        [Description("""
                     Configures an optional `HTTPRoute` exposing the live viewer.

                     **Note**: Using `HTTPRoute` requires at least [Gateway API v0.5.0](https://gateway-api.sigs.k8s.io/reference/api-types/httproute/) to be installed on your cluster.
                     """)]
        public V1Alpha1BotViewerHttpRoute? HttpRoute { get; set; }
    }

    public record V1Alpha1BotPod
    {
        [Description("""
                     Allows specifying a custom image for the `Bot` pod.
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
}