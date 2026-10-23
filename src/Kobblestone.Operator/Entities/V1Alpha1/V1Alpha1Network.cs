using k8s.Models;

using KubeOps.Abstractions.Entities;
using KubeOps.Abstractions.Entities.Attributes;

using KubernetesCRDModelGen.Models.gateway.networking.k8s.io;

namespace Kobblestone.Operator.Entities.V1Alpha1;

[Description("""
             A `Network` runs a [Velocity](https://papermc.io/software/velocity) proxy and the `Server`s behind it.

             Managed servers (the default) are `Server` resources created and kept compatible by the `Network`: they are rendered from `serverTemplate` and their entry in `servers`, and always trust the proxy (modern forwarding with the network's secret, offline mode, reachable only through the proxy). An existing `Server` joins the network, keeping its data, by naming it in `servers[].serverName`.

             Unmanaged entries (`unmanaged: true`) only add an existing `Server` or `Network` to the proxy's configuration, without configuring it.

             The proxy runs as a `Deployment`. The operator also manages its `Service`, configuration and forwarding `Secret` and, if configured, its `Route` and `TCPRoute`.
             """)]
[KubernetesEntity(Group = "kobblestone.io", ApiVersion = "v1alpha1", Kind = "Network")]
public sealed class
    V1Alpha1Network : CustomKubernetesEntity<V1Alpha1Network.V1Alpha1NetworkSpec, V1Alpha1Network.V1Alpha1NetworkStatus>
{
    /// <summary>
    /// Label on the managed `Server`s of a `Network` (value: the network's name). Also set on the proxy `Pod`s and
    /// `Service`.
    /// </summary>
    public const string NetworkLabel = "kobblestone.io/network";

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
                     The software, version and EULA shared by all managed servers, and defaults for their other settings. Each managed server's `spec` is merged onto it.

                     Required if the network has managed servers.
                     """)]
        public V1Alpha1NetworkServerTemplate? ServerTemplate { get; set; }

        [Description("""
                     Servers of the network.

                     The order matters: players join the first available server with `try` enabled, in the order of this list, unless a forced host applies. If several servers share a `hostname`, they are tried in this order as well.

                     **Note**: The proxy only reads its configuration on startup, so it is restarted whenever this list changes in a way that affects it: servers added, removed, renamed or reordered, or a changed `hostname` or `try`. Players connected to the proxy are disconnected. Servers that are created, deleted or restarted do not restart the proxy.
                     """)]
        public IList<V1Alpha1NetworkServer>? Servers { get; set; }

        [Description("""
                     What happens to a managed `Server` when it is removed from `servers` or the `Network` is deleted.

                     Default is `Retain`.

                     Values:
                     - `Retain`: The `Server` and its data are kept. It leaves the network and runs as a standalone server again (forwarding is removed, so it verifies players itself).
                     - `Delete`: The `Server` is deleted, **including its data**.
                     """)]
        public NetworkServerDeletionPolicy? ServerDeletionPolicy { get; set; }

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
                     Ready proxy replicas out of the desired ones, e.g. `1/1`.
                     """)]
        [AdditionalPrinterColumn(name: "Proxy")]
        public string? ProxyReady { get; set; }

        [Description("""
                     Running servers out of all servers of the network, e.g. `2/3`.
                     """)]
        [AdditionalPrinterColumn(name: "Servers")]
        public string? ServersReady { get; set; }

        [Description("""
                     Desired number of proxy replicas (`spec.proxy.replicas`).
                     """)]
        public int? ProxyReplicas { get; set; }

        [Description("""
                     Number of proxy replicas that are ready to accept players.
                     """)]
        public int? ProxyReadyReplicas { get; set; }

        [Description("""
                     Number of servers of the network.
                     """)]
        public int? ServerCount { get; set; }

        [Description("""
                     Number of running servers of the network.
                     """)]
        public int? ReadyServers { get; set; }

        [Description("""
                     Message explaining the current state of the `Network`, e.g. why a server cannot be managed.
                     """)]
        [AdditionalPrinterColumn(name: "Message")]
        public string? Message { get; set; }

        [Description("""
                     State of each server of the network.
                     """)]
        public IList<V1Alpha1NetworkServerStatus>? Servers { get; set; }

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

                     The operator always sets `servers`, `forced-hosts`, `player-info-forwarding-mode` (`modern`) and `forwarding-secret-file` (and `online-mode` if `offline` is `true`), overriding the values given here.
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

                     If not set, a random secret is generated. Managed servers are configured with it automatically; unmanaged servers have to be configured with it manually.

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

                     For managed servers, the `Server` resource is named `<network>-<name>` unless `serverName` is set.
                     """)]
        [Required]
        [Pattern("^[a-z0-9]([-a-z0-9]*[a-z0-9])?$")]
        [Length(maxLength: 63)]
        public string Name { get; set; } = null!;

        [Description("""
                     If set to `true`, the server is not managed by the network: the proxy only forwards players to `targetRef`, and the target has to be configured to trust the proxy manually.

                     `targetRef` is required, `serverName` and `spec` must not be set.

                     Default is `false`.
                     """)]
        public bool? Unmanaged { get; set; }

        [Description("""
                     Name of the managed `Server` resource in the network's namespace.

                     Default is `<network>-<name>`. If a `Server` with this name already exists and does not belong to another network, it joins the network and keeps its data.

                     Only for managed servers.
                     """)]
        [Pattern("^[a-z0-9]([-a-z0-9]*[a-z0-9])?$")]
        [Length(maxLength: 63)]
        public string? ServerName { get; set; }

        [Description("""
                     Settings of this managed server, merged onto `serverTemplate`: objects are merged field by field, all other values (including lists) replace the template's value.

                     `type`, `version` and `eula` are shared by all servers and only set in `serverTemplate`.

                     Only for managed servers.
                     """)]
        public V1Alpha1NetworkServerSpec? Spec { get; set; }

        [Description("""
                     Reference to the `Server` or `Network` to forward players to.

                     Supported kinds: `Server` (default), `Network`.

                     Only for unmanaged servers.
                     """)]
        public V1TypedObjectReference? TargetRef { get; set; }

        [Description("""
                     Hostname that sends players directly to this server (forced host).

                     If several servers have the same hostname, players join the first available one, in the order of `servers`.
                     """)]
        public string? Hostname { get; set; }

        [Description("""
                     Whether players are sent to this server when they join the network (Velocity's `try` list, in the order of `servers`). Servers with `try` disabled are only reachable via `/server` or their `hostname`.

                     Default is `true`.
                     """)]
        public bool? Try { get; set; }
    }

    /// <summary>
    /// Settings of a single managed server (`servers[].spec`), which also serve as defaults in `serverTemplate`. The
    /// names match `Server`'s spec, which they are rendered into. Settings the network owns (`forwarding`, `offline`,
    /// `route`, `tcpRoute`, `service`) and those that define the whole network (`type`, `version`, `eula`, only in
    /// `serverTemplate`) are not available.
    /// </summary>
    public record V1Alpha1NetworkServerSpec
    {
        [Description("""
                     Desired state of the server, see `Server` `spec.state`.

                     Default is `Running`.
                     """)]
        public ServerState? State { get; set; }

        [Description("""
                     Additional entries for `server.properties`, see `Server` `spec.properties`.
                     """)]
        public IDictionary<string, string?>? Properties { get; set; }

        [Description("""
                     Lifecycle settings, see `Server` `spec.lifecycle`.
                     """)]
        public V1Alpha1Server.V1Alpha1ServerLifecycle? Lifecycle { get; set; }

        [Description("""
                     Persistent volume for the server's data, see `Server` `spec.storage`.

                     **Warning**: Without storage, the server's world is lost whenever its `Pod` is recreated.
                     """)]
        public V1Alpha1Server.V1Alpha1ServerStorage? Storage { get; set; }

        [Description("""
                     Sleeping while no players are online, see `Server` `spec.autoSleep`.

                     **Note**: `Hibernate` is not supported behind a proxy, because nothing wakes the server up again.
                     """)]
        public V1Alpha1Server.V1Alpha1ServerAutoSleep? AutoSleep { get; set; }

        [Description("""
                     Whitelist and operators, see `Server` `spec.access`.
                     """)]
        public V1Alpha1Server.V1Alpha1ServerAccess? Access { get; set; }

        [Description("""
                     Compute resources of the server container.
                     """)]
        public V1ResourceRequirements? Resources { get; set; }

        [Description("""
                     Configures the server `Pod`, see `Server` `spec.pod`.
                     """)]
        public V1Alpha1Server.V1Alpha1ServerPod? Pod { get; set; }

        [Description("""
                     Remote console of the server, see `Server` `spec.rcon`.
                     """)]
        public V1Alpha1Server.V1Alpha1ServerRcon? Rcon { get; set; }

        [Description("""
                     Commands executed via RCON on certain events, see `Server` `spec.autoCommands`.
                     """)]
        public V1Alpha1Server.V1Alpha1ServerAutoCommands? AutoCommands { get; set; }

        [Description("""
                     Plugins, mods, modpacks and other content, see `Server` `spec.content`.
                     """)]
        public V1Alpha1Server.V1Alpha1ServerContent? Content { get; set; }

        [Description("""
                     JVM settings, see `Server` `spec.jvm`.
                     """)]
        public V1Alpha1Server.V1Alpha1ServerJvm? Jvm { get; set; }

        [Description("""
                     Health checks, see `Server` `spec.timeouts`.
                     """)]
        public V1Alpha1Server.V1Alpha1ServerTimeouts? Timeouts { get; set; }

        [Description("""
                     The server's `NetworkPolicy`, see `Server` `spec.networkPolicy`.
                     """)]
        public V1Alpha1Server.V1Alpha1ServerNetworkPolicy? NetworkPolicy { get; set; }
    }

    /// <summary>
    /// The servers of the network: the software, version and EULA all of them share, and defaults for their other
    /// settings.
    /// </summary>
    public record V1Alpha1NetworkServerTemplate : V1Alpha1NetworkServerSpec
    {
        [Description("""
                     Type of the server software of all servers, see `Server` `spec.type`.

                     Only types that can trust a Velocity proxy are supported: `Paper`, `Pufferfish`, `Purpur`, `Folia`, `Fabric`, `Quilt`.
                     """)]
        [Required]
        public ServerType Type { get; set; }

        [Description("""
                     Minecraft version of all servers, see `Server` `spec.version`.
                     """)]
        [Required]
        public string Version { get; set; } = null!;

        [Description("""
                     Whether you accept the [Minecraft EULA](https://www.minecraft.net/en-us/eula) for all servers of the network. The servers do not start unless this is `true`.
                     """)]
        [Required]
        public bool Eula { get; set; }
    }

    public record V1Alpha1NetworkServerStatus
    {
        [Description("""
                     Name of the server in the proxy.
                     """)]
        public string Name { get; set; } = null!;

        [Description("""
                     Name of the `Server` resource, or of the target of an unmanaged entry.
                     """)]
        public string? Server { get; set; }

        [Description("""
                     Whether the server is managed by the network.
                     """)]
        public bool Managed { get; set; }

        [Description("""
                     Phase of the `Server` (`status.phase`), or `Missing` if it does not exist.
                     """)]
        public string? Phase { get; set; }

        [Description("""
                     Why the server cannot be managed, if so.
                     """)]
        public string? Message { get; set; }
    }

    public enum NetworkServerDeletionPolicy
    {
        Retain,
        Delete
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
