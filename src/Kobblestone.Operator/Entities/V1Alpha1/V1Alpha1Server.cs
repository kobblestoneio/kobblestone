using k8s.Models;

using KubeOps.Abstractions.Entities;
using KubeOps.Abstractions.Entities.Attributes;

using KubernetesCRDModelGen.Models.gateway.networking.k8s.io;

namespace Kobblestone.Operator.Entities.V1Alpha1;

[Description(
    """
        A `Server` runs a single Minecraft server.

        The server is based on the [itzg/minecraft-server](https://docker-minecraft-server.readthedocs.io/) image and runs as a `StatefulSet` with one replica.
        The operator also manages its `Service`, RCON `Secret` and `NetworkPolicy` and, if configured, its `PersistentVolumeClaim`, `Route` and `TCPRoute`.

        Servers can be backed up with `Backup`s and restored with `Restore`s if they have storage configured.
        """
)]
[KubernetesEntity(Group = "kobblestone.io", ApiVersion = "v1alpha1", Kind = "Server")]
public sealed class V1Alpha1Server
    : CustomKubernetesEntity<
        V1Alpha1Server.V1Alpha1ServerSpec,
        V1Alpha1Server.V1Alpha1ServerStatus
    >,
        IConditionsStatus<V1Alpha1Server.V1Alpha1ServerStatus, V1Condition>
{
    public record V1Alpha1ServerSpec
    {
        [Description(
            """
                Desired state of the `Server`.

                Default is `Running`.

                Values:
                - `Running`: The server is started.
                - `Stopped`: The server is stopped. Its data is kept.

                **Note**: The server is kept stopped while a `Restore` or an offline `Backup` of it is running.
                """
        )]
        public ServerState? State { get; set; }

        [Description(
            """
                Type of the server software, e.g. a mod loader or plugin platform.

                Default is `Vanilla`.

                See [Server types](https://docker-minecraft-server.readthedocs.io/en/latest/types-and-platforms/) for details on each type.
                """
        )]
        public ServerType? Type { get; set; }

        [Description(
            """
                Minecraft version of the server.

                Use `LATEST` for the latest release or `SNAPSHOT` for the latest snapshot.
                """
        )]
        [AdditionalPrinterColumn(name: "Version")]
        [Required]
        public string Version { get; set; } = null!;

        [Description(
            """
                Whether you accept the [Minecraft EULA](https://www.minecraft.net/en-us/eula). The server does not start unless this is `true`.
                """
        )]
        [Required]
        public bool Eula { get; set; }

        [Description(
            """
                If set to `true`, the server runs in offline mode: player accounts are not verified.

                Default is `false`.

                **Warning**: Only use this for servers behind a proxy that verifies players (e.g. a `Network`), never for servers that players can reach directly.
                """
        )]
        public bool? Offline { get; set; }

        [Description(
            """
                Configures the server to accept players forwarded by a [Velocity](https://papermc.io/software/velocity) proxy ([modern forwarding](https://docs.papermc.io/velocity/player-information-forwarding/)). The server then runs in offline mode and trusts the player information sent by the proxy.

                Supported server types: `Paper`, `Pufferfish`, `Purpur`, `Folia` (built in), `Fabric` and `Quilt` (installs [FabricProxy-Lite](https://modrinth.com/mod/fabricproxy-lite) and Fabric API from Modrinth). For other types, it is ignored and the server keeps verifying players itself.

                **Warning**: For Paper-based servers, `config/paper-global.yml` is managed by Kobblestone and replaced on every start. Changes to it on the server's volume are lost.

                **Note**: Set and managed by the `Network` for its managed servers.
                """
        )]
        public V1Alpha1ServerForwarding? Forwarding { get; set; }

        [Description(
            """
                Additional entries for `server.properties`.

                See [server.properties](https://minecraft.wiki/w/Server.properties) for available properties.
                """
        )]
        public IDictionary<string, string?>? Properties { get; set; }

        [Description(
            """
                Configures settings related to the lifecycle of the `Server`.
                """
        )]
        public V1Alpha1ServerLifecycle? Lifecycle { get; set; }

        [Description(
            """
                Configures a persistent volume for the server's data (worlds, configuration, ...).

                Without storage, all data is lost when the server `Pod` is recreated, and `Backup`s and `Restore`s are not supported.

                **Warning**: Removing `storage` deletes the server's `PersistentVolumeClaim` and thus its data.
                """
        )]
        public V1Alpha1ServerStorage? Storage { get; set; }

        [Description(
            """
                Configures a `Route` that makes the server reachable through a `Router` under a hostname.

                Removing it deletes the `Route`.
                """
        )]
        public V1Alpha1ServerRoute? Route { get; set; }

        [Description(
            """
                Configures putting the server to sleep while no players are online.
                """
        )]
        public V1Alpha1ServerAutoSleep? AutoSleep { get; set; }

        [Description(
            """
                Configures the whitelist and the operators of the server.
                """
        )]
        public V1Alpha1ServerAccess? Access { get; set; }

        [Description(
            """
                Compute resources of the server container.
                """
        )]
        public V1ResourceRequirements? Resources { get; set; }

        [Description(
            """
                Configures the `Service` of the server.
                """
        )]
        public V1Alpha1ServerService? Service { get; set; }

        [Description(
            """
                Configures the server `Pod`.
                """
        )]
        public V1Alpha1ServerPod? Pod { get; set; }

        [Description(
            """
                Configures RCON, the remote console of the server.

                RCON is required for `Command`s, `CommandGroup`s, `autoCommands` and online `Backup`s.
                """
        )]
        public V1Alpha1ServerRcon? Rcon { get; set; }

        [Description(
            """
                Commands executed via RCON on certain events. Each entry is a single command.
                """
        )]
        public V1Alpha1ServerAutoCommands? AutoCommands { get; set; }

        [Description(
            """
                Configures plugins, mods, modpacks and other content installed on startup.
                """
        )]
        public V1Alpha1ServerContent? Content { get; set; }

        [Description(
            """
                Configures the JVM of the server.
                """
        )]
        public V1Alpha1ServerJvm? Jvm { get; set; }

        [Description(
            """
                Configures the health checks of the server.
                """
        )]
        public V1Alpha1ServerTimeouts? Timeouts { get; set; }

        [Description(
            """
                Configures the `NetworkPolicy` of the server.

                By default, it only allows Minecraft traffic from `Router`s, `Network`s and `Bot`s, RCON traffic from `Backup`s, and all traffic to the `Service` if it is of type `LoadBalancer`.
                """
        )]
        public V1Alpha1ServerNetworkPolicy? NetworkPolicy { get; set; }

        [Title("Gateway API")]
        [Description(
            """
                Configures an optional `TCPRoute` exposing the `Server`s Minecraft port.

                **Note**: Using `TCPRoute` requires at least [Gateway API v1.6.0](https://gateway-api.sigs.k8s.io/reference/api-spec/1.6/spec/) to be installed on your cluster.
                """
        )]
        public V1Alpha1ServerTcpRoute? TcpRoute { get; set; }
    }

    public record V1Alpha1ServerStatus : IConditions<V1Condition>
    {
        [Description(
            """
                Current phase of the `Server`.

                Values:
                - `Stopped`: The server is stopped.
                - `Starting`: The server is starting.
                - `Running`: The server is running and ready for players.
                - `Stopping`: The server is stopping.
                - `Hibernating`: The server was stopped by auto-sleep and is started again when a player connects.
                """
        )]
        [AdditionalPrinterColumn(name: "Status")]
        public ServerPhase Phase { get; set; } = ServerPhase.Stopped;

        [Description(
            """
                Type of the server software, see `spec.type`.
                """
        )]
        [AdditionalPrinterColumn(name: "Type")]
        public ServerType Type { get; set; }

        [Description(
            """
                IP address of the `Server`'s `Service`. External addresses are preferred: the IP assigned by a load balancer, then the first of `externalIPs`, then the cluster IP.
                """
        )]
        [AdditionalPrinterColumn(name: "Address")]
        public string? Address { get; set; }

        [Description(
            """
                Conditions of the `Server`.

                Types: `Running`, `Stopped`, `Progressing`, `Hibernating`, `RconAvailable`, `BackupInProgress`, `RestoreInProgress`, `Error`.
                """
        )]
        public IList<V1Condition> Conditions { get; set; } = [];

        [Description(
            """
                Whether a managed `TCPRoute` exists.
                """
        )]
        public bool? TcpRoute { get; set; }
    }

    public record V1Alpha1ServerForwarding
    {
        [Description(
            """
                Reference to a key of a `Secret` containing the forwarding secret of the proxy.
                """
        )]
        [Required]
        public V1SecretKeyRef SecretRef { get; set; } = null!;
    }

    public record V1Alpha1ServerStorage
    {
        [Description(
            """
                Storage class of the `PersistentVolumeClaim`.

                Default is the default storage class of the cluster.

                **Note**: Only applied when the claim is created.
                """
        )]
        public string? StorageClassName { get; set; }

        [Description(
            """
                Size of the `PersistentVolumeClaim`, e.g. `10Gi`.

                **Note**: Increasing the size requires a storage class that supports volume expansion.
                """
        )]
        [Required]
        public string Size { get; set; } = null!;
    }

    public record V1Alpha1ServerLifecycle
    {
        [Description(
            """
                Specifies when changes to `spec` are rolled out to running servers.

                Default is `Immediate`.

                Values:
                - `Immediate`: Changes to `spec` are rolled out immediately. For example, if you update `spec.version`, the server will be restarted.
                - `Manual`: Changes to `spec` are rolled out when the server `Pod` is recreated, e.g. when the server is stopped and started again or the `Pod` is deleted. **Note**: This does not apply to restarts of the server container after a crash.
                """
        )]
        public RolloutPolicy? RolloutPolicy { get; set; }
    }

    public record V1Alpha1ServerRoute
    {
        [Description(
            """
                Reference to the `Router` the `Route` is attached to.
                """
        )]
        [Required]
        public V1LocalObjectReference ParentRef { get; set; } = null!;

        [Description(
            """
                Hostname under which the server is reachable through the `Router`.
                """
        )]
        [Required]
        public string Hostname { get; set; } = null!;
    }

    public record V1Alpha1ServerAutoSleep
    {
        [Description(
            """
                How the server sleeps while no players are online.

                Default is `Pause`.

                Values:
                - `None`: The server never sleeps.
                - `Pause`: The server pauses ticking while it is empty (requires Minecraft 1.21.2 or later).
                - `Hibernate`: The server is stopped while it is empty and started again when a player connects through a `Router`.
                - `LegacyPause`: The server process is paused while it is empty, using the [autopause](https://docker-minecraft-server.readthedocs.io/en/latest/misc/autopause-autostop/autopause/) feature of the image. For older versions.
                """
        )]
        public AutoSleepMode? Mode { get; set; }

        [Description(
            """
                Time in seconds without players after which the server goes to sleep.

                Default is `60`.
                """
        )]
        [RangeMinimum(1)]
        public int? TimeoutSeconds { get; set; }
    }

    public record V1Alpha1ServerAccess
    {
        [Description(
            """
                Configures the whitelist. Only whitelisted players can join.
                """
        )]
        public V1Alpha1ServerAccessWhitelist? Whitelist { get; set; }

        [Description(
            """
                Configures the operators of the server.
                """
        )]
        public V1Alpha1ServerAccessOps? Ops { get; set; }
    }

    public record V1Alpha1ServerAccessWhitelist
    {
        [Description(
            """
                If set to `true`, online players who are not whitelisted are kicked when the whitelist changes.

                Default is `false`.
                """
        )]
        public bool? Enforce { get; set; }

        [Description(
            """
                Players allowed to join, as usernames or UUIDs.

                **Note**: The whitelist is synchronized: players not listed here are removed from it.
                """
        )]
        [XListType(XListType.Set)]
        public IList<string>? Users { get; set; }
    }

    public record V1Alpha1ServerAccessOps
    {
        [Description(
            """
                Players with operator permissions, as usernames or UUIDs.

                **Note**: The operators are synchronized: players not listed here lose their operator permissions.
                """
        )]
        [XListType(XListType.Set)]
        public IList<string>? Users { get; set; }
    }

    public record V1Alpha1ServerService
    {
        [Description(
            """
                Port on the `Service` that exposes Minecraft.

                Default is `25565`.
                """
        )]
        public int? Port { get; set; }

        [Description(
            """
                Type of the server's `Service`, e.g. `ClusterIP` or `LoadBalancer`.

                Default is `ClusterIP`.

                **Warning**: With `LoadBalancer`, the RCON port is reachable from anywhere as well, unless RCON is disabled.
                """
        )]
        public string? Type { get; set; }

        [Description(
            """
                External traffic policy of the server's `Service`.
                """
        )]
        public string? ExternalTrafficPolicy { get; set; }

        [Description(
            """
                IP family policy of the server's `Service`.
                """
        )]
        public string? IpFamilyPolicy { get; set; }

        [Description(
            """
                Annotations added to the server's `Service`, e.g. to configure a load balancer.
                """
        )]
        public IDictionary<string, string>? Annotations { get; set; }
    }

    public record V1Alpha1ServerPod
    {
        [Description(
            """
                Custom image for the server container.

                Default is `itzg/minecraft-server` with the Java version of `spec.jvm.version`.
                """
        )]
        public string? Image { get; set; }

        [Description(
            """
                Image pull policy of the server container.
                """
        )]
        public string? ImagePullPolicy { get; set; }

        [Description(
            """
                Affinity of the server `Pod`.
                """
        )]
        public V1Affinity? Affinity { get; set; }

        [Description(
            """
                Name of the node the server `Pod` is scheduled on.
                """
        )]
        public string? NodeName { get; set; }

        [Description(
            """
                Node selector of the server `Pod`.
                """
        )]
        public IDictionary<string, string>? NodeSelector { get; set; }

        [Description(
            """
                Tolerations of the server `Pod`.
                """
        )]
        public IList<V1Toleration>? Tolerations { get; set; }

        [Description(
            """
                Additional volumes of the server `Pod`.
                """
        )]
        public IList<V1Volume>? Volumes { get; set; }

        [Description(
            """
                Additional volume mounts of the server container.
                """
        )]
        public IList<V1VolumeMount>? VolumeMounts { get; set; }

        [Description(
            """
                Additional environment variables of the server container.

                Can be used to set any [option of the image](https://docker-minecraft-server.readthedocs.io/en/latest/variables/) that is not covered by the `Server` spec.
                """
        )]
        public IList<V1EnvVar>? Env { get; set; }
    }

    public record V1Alpha1ServerRcon
    {
        [Description(
            """
                If set to `true`, RCON is disabled.

                Default is `false`.

                **Note**: `Command`s, `CommandGroup`s, `autoCommands` and online `Backup`s require RCON.
                """
        )]
        public bool? Disabled { get; set; }

        [Description(
            """
                Port of RCON on the server's `Service`. Inside the `Pod`, RCON always listens on `25575`.

                Default is `25575`.
                """
        )]
        public int? Port { get; set; }

        [Description(
            """
                Reference to a key of a `Secret` containing the RCON password.

                If not set, a random password is generated.

                **Note**: Only read when the server's own `Secret` is created.
                """
        )]
        public V1SecretKeyRef? PasswordSecretRef { get; set; }
    }

    public record V1Alpha1ServerAutoCommands
    {
        [Description(
            """
                Commands executed once the server has started.
                """
        )]
        public IList<string>? OnStartup { get; set; }

        [Description(
            """
                Commands executed when a player connects.
                """
        )]
        public IList<string>? OnConnect { get; set; }

        [Description(
            """
                Commands executed when a player disconnects.
                """
        )]
        public IList<string>? OnDisconnect { get; set; }

        [Description(
            """
                Commands executed when the first player connects to the empty server.
                """
        )]
        public IList<string>? OnFirstConnect { get; set; }

        [Description(
            """
                Commands executed when the last player disconnects.
                """
        )]
        public IList<string>? OnLastDisconnect { get; set; }
    }

    public record V1Alpha1ServerContent
    {
        [Description(
            """
                Plugins to install, for server types that support plugins (e.g. `Paper`).
                """
        )]
        public IList<V1Alpha1ServerPlugin>? Plugins { get; set; }

        [Description(
            """
                Mods to install, for modded server types (e.g. `Fabric`).
                """
        )]
        public IList<V1Alpha1ServerMod>? Mods { get; set; }

        [Description(
            """
                Configures content from [CurseForge](https://www.curseforge.com/).
                """
        )]
        public V1Alpha1ServerCurseForge? CurseForge { get; set; }

        [Description(
            """
                Configures content from [Modrinth](https://modrinth.com/).
                """
        )]
        public V1Alpha1ServerModrinth? Modrinth { get; set; }

        [Description(
            """
                URL of a [packwiz](https://packwiz.infra.link/) `pack.toml` to install.
                """
        )]
        public string? PackwizUrl { get; set; }

        [Description(
            """
                IDs of [Spigot resources](https://www.spigotmc.org/resources/) to install.
                """
        )]
        [XListType(XListType.Set)]
        public IList<string>? SpigotResources { get; set; }

        [Description(
            """
                URL of an image used as the server icon.
                """
        )]
        public string? IconUrl { get; set; }

        [Description(
            """
                Configures a resource pack that players are prompted to download.
                """
        )]
        public V1Alpha1ServerResourcePack? ResourcePack { get; set; }

        [Description(
            """
                URL of a modpack ZIP file to install.
                """
        )]
        public string? ModPackUrl { get; set; }
    }

    public record V1Alpha1ServerPlugin
    {
        [Description(
            """
                URL of the plugin `.jar` file.
                """
        )]
        [Required]
        public string Url { get; set; } = null!;
    }

    public record V1Alpha1ServerMod
    {
        [Description(
            """
                URL of the mod `.jar` file.
                """
        )]
        [Required]
        public string Url { get; set; } = null!;
    }

    public record V1Alpha1ServerCurseForge
    {
        [Description(
            """
                Reference to a key of a `Secret` containing a CurseForge API key. Required to download from CurseForge.
                """
        )]
        public V1SecretKeyRef? ApiKeySecretRef { get; set; }

        [Description(
            """
                CurseForge projects or files to install, by slug, ID or URL.
                """
        )]
        [XListType(XListType.Set)]
        public IList<string>? Files { get; set; }

        [Description(
            """
                URL of the CurseForge page of a modpack to install. Requires the server type `AutoCurseforge`.
                """
        )]
        public string? ModPackUrl { get; set; }
    }

    public record V1Alpha1ServerModrinth
    {
        [Description(
            """
                Modrinth projects to install, by slug or ID.
                """
        )]
        [XListType(XListType.Set)]
        public IList<string>? Projects { get; set; }
    }

    public record V1Alpha1ServerResourcePack
    {
        [Description(
            """
                URL of the resource pack ZIP file.
                """
        )]
        [Required]
        public string Url { get; set; } = null!;

        [Description(
            """
                SHA-1 checksum of the resource pack. Lets clients cache the pack.
                """
        )]
        public string? Sha1 { get; set; }

        [Description(
            """
                If set to `true`, players who decline the resource pack are disconnected.

                Default is `false`.
                """
        )]
        public bool? Enforce { get; set; }
    }

    public record V1Alpha1ServerJvm
    {
        [Description(
            """
                Configures a specific version of the JVM to be used.

                Default is `java25`.

                **Note**: This corresponds to the Java part of the server image tag.
                See [Image tags](https://docker-minecraft-server.readthedocs.io/en/latest/versions/java/#image-tags).
                """
        )]
        [Example("java25")]
        public string? Version { get; set; }

        [Description(
            """
                Configures memory settings for the JVM.
                """
        )]
        public V1Alpha1ServerJvmMemory? Memory { get; set; }
    }

    public record V1Alpha1ServerJvmMemory
    {
        [Description(
            """
                Configures the percentage of memory reserved for the heap.

                Default is `75`.

                **Note**: For low-memory servers, you might have to lower this.
                """
        )]
        [RangeMinimum(1)]
        [RangeMaximum(99)]
        public int? HeapPercentage { get; set; }
    }

    public record V1Alpha1ServerTimeouts
    {
        [Description(
            """
                Maximum time in seconds the server may take to start before it is restarted.

                Default is `360`. Must be a multiple of `5`.
                """
        )]
        [MultipleOf(5)]
        public int? MaxStartupTimeSeconds { get; set; }

        [Description(
            """
                Maximum time in seconds a started server may be unresponsive before it is restarted.

                Default is `15`. Must be a multiple of `5`.
                """
        )]
        [MultipleOf(5)]
        public int? MaxUnavailabilityTimeSeconds { get; set; }
    }

    public record V1Alpha1ServerNetworkPolicy
    {
        [Description(
            """
                If set to `true`, the server's default `NetworkPolicy` will not be created.

                Default is `false`.
                """
        )]
        public bool? Disabled { get; set; }

        [Description(
            """
                Additional Ingress rules applied to the policy.
                """
        )]
        public IList<V1NetworkPolicyIngressRule>? ExtraIngressRules { get; set; }
    }

    public record V1Alpha1ServerTcpRoute
    {
        [Description(
            """
                References to gateways the `TCPRoute` should attach to.
                """
        )]
        public IList<V1TCPRouteSpecParentRefs>? ParentRefs { get; set; }
    }
}
