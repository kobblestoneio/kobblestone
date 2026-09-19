using k8s.Models;

using KubeOps.Abstractions.Entities;
using KubeOps.Abstractions.Entities.Attributes;

namespace Kobblestone.Operator.Entities.V1Alpha1;

[Description("""
             A `Route` defines a mapping from a hostname (e.g. `server.example.com`) to a Minecraft backend (`Server`, `Network, `Router` or `Service`).

             Routes will be attached and installed on their parent `Router` instance, through which the hostname will be routable.
             """)]
[KubernetesEntity(Group = "kobblestone.io", ApiVersion = "v1alpha1", Kind = "Route")]
public sealed class
    V1Alpha1Route : CustomKubernetesEntity<V1Alpha1Route.V1Alpha1RouteSpec, V1Alpha1Route.V1Alpha1RouteStatus>
{
    public record V1Alpha1RouteSpec
    {
        [Title("Parent")]
        [Description("""
                     Reference to the parent `Router` this `Route` should be attached to.
                     """)]
        [Required]
        public V1LocalObjectReference ParentRef { get; set; } = null!;

        [Title("Hostname")]
        [Description("""
                     Hostname that should be routable.
                     """)]
        [Example("server.example.com")]
        [Format("hostname")]
        [Required]
        public string Hostname { get; set; } = null!;

        [Title("Backend")]
        [Description("""
                     Reference to the backend where the hostname should be routed to.
                     """)]
        [Required]
        public V1Alpha1RouteBackendRef BackendRef { get; set; } = null!;
    }

    public record V1Alpha1RouteStatus
    {
        [Description("""
                     Name of the `Router` this `Route` is currently attached to.
                     """)]
        [AdditionalPrinterColumn(name: "Router")]
        public string? RouterName { get; set; }

        [Description("""
                     Current hostname that is routed.
                     """)]
        [AdditionalPrinterColumn(name: "Host")]
        public string? Hostname { get; set; } = null!;

        [Description("""
                     Resolved address (IP+port) of the target.
                     """)]
        [AdditionalPrinterColumn(name: "Backend")]
        public string? Backend { get; set; }

        [Description("""
                     Conditions of the `Route`.
                     """)]
        public IList<V1Condition> Conditions { get; set; } = [];
    }

    public record V1Alpha1RouteBackendRef
    {
        [Description("""
                     Name of the backend object.
                     """)]
        [Example("my-server")]
        [Required]
        public string Name { get; set; } = null!;

        [Description("""
                     Namespace of the backend object. Defaults to the namespace of the `Route`.
                     """)]
        public string? Namespace { get; set; }

        [Description("""
                     ApiVersion of the backend object.
                     """)]
        public string? ApiVersion { get; set; }

        [Description("""
                     Kind of the backend object.

                     Supported values:
                     - `Server`
                     - `Network`
                     - `Router`
                     - `Service`

                     Default is `Server`.
                     """)]
        [Example("Server")]
        public string? Kind { get; set; }

        [Description("""
                     Target port that exposes Minecraft.

                     Default is `25565`.
                     """)]
        [RangeMinimum(1)]
        [RangeMaximum(ushort.MaxValue)]
        public int? Port { get; set; }
    }
}