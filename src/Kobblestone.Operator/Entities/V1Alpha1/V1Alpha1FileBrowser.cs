using k8s.Models;

using KubeOps.Abstractions.Entities;
using KubeOps.Abstractions.Entities.Attributes;

using KubernetesCRDModelGen.Models.gateway.networking.k8s.io;

namespace Kobblestone.Operator.Entities.V1Alpha1;

[Description("""
             A `FileBrowser` gives access to files: the data of a `Server`, the content of a `Backup`, or the archives in a `BackupRepo`. The operator runs a file manager ([SFTPGo](https://github.com/drakkan/sftpgo)) next to the data and exposes the interfaces you enable: a browser interface (`web`), `sftp`, `webdav` and a REST `api`.

             While a target `Server` is running, the file manager runs on the server's node, since the server's volume can only be mounted there. The server is not restarted or changed for this.

             **Note**: Access to a server's files is effectively control over the server (plugins, mods, configuration). Restrict who can create `FileBrowser`s with RBAC, and only expose them to people you trust.
             """)]
[KubernetesEntity(Group = "kobblestone.io", ApiVersion = "v1alpha1", Kind = "FileBrowser")]
public sealed class V1Alpha1FileBrowser : CustomKubernetesEntity<V1Alpha1FileBrowser.V1Alpha1FileBrowserSpec,
    V1Alpha1FileBrowser.V1Alpha1FileBrowserStatus>
{
    /// <summary>
    /// Label on the resources of a `FileBrowser` (value: the file browser's name).
    /// </summary>
    public const string FileBrowserLabel = "kobblestone.io/file-browser";

    [ValidationRule(
        "(has(self.web) && self.web.enabled) || (has(self.sftp) && self.sftp.enabled) || (has(self.webdav) && self.webdav.enabled) || (has(self.api) && self.api.enabled)",
        message: "At least one interface (web, sftp, webdav or api) must be enabled.")]
    [ValidationRule(
        "!has(self.users) || !self.users.exists(u, has(u.authorizedKeysSecretRef)) || (has(self.sftp) && self.sftp.enabled)",
        message: "users[].authorizedKeysSecretRef requires sftp.enabled.")]
    public record V1Alpha1FileBrowserSpec
    {
        [Description("""
                     What to browse. With a single target, users see its files directly; with several, each target is a folder named after the target.
                     """)]
        [Items(1, 16)]
        [ValidationRule(
            "self.all(a, self.exists_one(b, (has(b.name) ? b.name : b.targetRef.name) == (has(a.name) ? a.name : a.targetRef.name)))",
            message: "Target names must be unique. Set `name` on targets that reference objects of the same name.")]
        [Required]
        public IList<V1Alpha1FileBrowserTarget> Targets { get; set; } = [];

        [Description("""
                     Seconds after which the `FileBrowser` expires, counted from its creation, e.g. `7200` for two hours. An expired `FileBrowser` stops serving: its file manager, `Service`s, routes, `NetworkPolicy` and credentials are deleted, and `status.phase` is `Expired`. The `FileBrowser` itself is kept until it is deleted.

                     Without it, access is permanent. Changing it moves `status.expiresAt`; extending an expired `FileBrowser` brings it back.
                     """)]
        [RangeMinimum(1)]
        [Example(7200)]
        public long? ExpiresAfterSeconds { get; set; }

        [Description("""
                     Users for all interfaces.

                     Without it, one user with full access is generated: its login is in the `Secret` named in `status.credentialsSecretRef` (keys `username` and `password`).
                     """)]
        [Items(1, 32)]
        [XListType(XListType.Map)]
        [XListMapKeys(["name"])]
        public IList<V1Alpha1FileBrowserUser>? Users { get; set; }

        [Description("""
                     Browser interface: a web client to browse, upload, download and edit files, always with login.
                     """)]
        public V1Alpha1FileBrowserWeb? Web { get; set; }

        [Title("SFTP")]
        [Description("""
                     SFTP, for clients like WinSCP, FileZilla or `sftp`/`rsync` over SSH. Users log in with their password or SSH key. The host key is generated once and kept in the credentials `Secret`.
                     """)]
        public V1Alpha1FileBrowserSftp? Sftp { get; set; }

        [Title("WebDAV")]
        [Description("""
                     WebDAV, e.g. to mount the files as a network drive in Windows or Finder. Users log in with their password.

                     **Note**: WebDAV sends the password with every request. Only expose it via HTTPS (e.g. an `httpRoute` on a gateway with TLS); Windows also refuses to use it without HTTPS by default.
                     """)]
        public V1Alpha1FileBrowserWebDav? Webdav { get; set; }

        [Title("API")]
        [Description("""
                     REST API ([SFTPGo's user API](https://sftpgo.stoplight.io/docs/sftpgo/openapi.yaml)) for applications that build their own interface, e.g. a hosting panel. Requests act as a user, with that user's permissions: `X-SFTPGO-API-KEY: kobblestone.<token>.<user>`.

                     The API is only reachable through its `Service`; there is no route for it.
                     """)]
        public V1Alpha1FileBrowserApi? Api { get; set; }

        [Description("""
                     Configures the file manager's `Pod`.
                     """)]
        public V1Alpha1FileBrowserPod? Pod { get; set; }
    }

    public record V1Alpha1FileBrowserStatus
    {
        [Description("""
                     Current phase of the `FileBrowser`.

                     Values:
                     - `Pending`: The file manager is starting, or waiting for its node.
                     - `Preparing`: `Backup` targets are being extracted.
                     - `Ready`: All targets are ready, the interfaces are serving.
                     - `Expired`: `expiresAfterSeconds` has passed. The file manager and its resources are deleted.
                     - `Failed`: A target cannot be served, see `message` and `targets`.
                     """)]
        [AdditionalPrinterColumn(name: "Status")]
        public FileBrowserPhase Phase { get; set; }

        [Description("""
                     Address of the web interface: the `httpRoute` hostname, or else the address and port of its `Service`. External addresses are preferred: the IP or hostname assigned by a load balancer, then the first of `externalIPs`, then the DNS name of the `Service` in the cluster (`<name>.<namespace>.svc.cluster.local`).
                     """)]
        [AdditionalPrinterColumn(name: "Web")]
        public string? Web { get; set; }

        [Title("SFTP")]
        [Description("""
                     Address of the SFTP interface (address and port of its `Service`, preferring external addresses as for `web`).
                     """)]
        [AdditionalPrinterColumn(PrinterColumnPriority.WideView, "SFTP")]
        public string? Sftp { get; set; }

        [Title("WebDAV")]
        [Description("""
                     Address of the WebDAV interface: the `httpRoute` hostname, or else the address and port of its `Service`, preferring external addresses as for `web`.
                     """)]
        [AdditionalPrinterColumn(PrinterColumnPriority.WideView, "WebDAV")]
        public string? Webdav { get; set; }

        [Title("API")]
        [Description("""
                     Address of the REST API (DNS name and port of its `Service` in the cluster, or an external address as for `web`).
                     """)]
        [AdditionalPrinterColumn(PrinterColumnPriority.WideView, "API")]
        public string? Api { get; set; }

        [Description("""
                     Time when the `FileBrowser` expires (`expiresAfterSeconds`).
                     """)]
        [AdditionalPrinterColumn(name: "Expires")]
        public DateTime? ExpiresAt { get; set; }

        [Description("""
                     Message explaining the current state of the `FileBrowser`.
                     """)]
        [AdditionalPrinterColumn(name: "Message")]
        public string? Message { get; set; }

        [Description("""
                     State of each target.
                     """)]
        public IList<V1Alpha1FileBrowserTargetStatus> Targets { get; set; } = [];

        [Description("""
                     `Secret` with what the operator generated: the login of the generated user (keys `username` and `password`, without `users`) and the API token (key `api-token`, without `api.tokenSecretRef`).
                     """)]
        public V1LocalObjectReference? CredentialsSecretRef { get; set; }
    }

    public enum FileBrowserPhase
    {
        Pending,
        Preparing,
        Ready,
        Expired,
        Failed
    }

    public record V1Alpha1FileBrowserTargetStatus
    {
        [Description("""
                     Name of the target.
                     """)]
        public string Name { get; set; } = null!;

        [Description("""
                     Current phase of the target.

                     Values:
                     - `Pending`: The file manager is starting.
                     - `Preparing`: The `Backup` is being extracted.
                     - `Ready`: The target is served.
                     - `Failed`: The target cannot be served, see `message`.
                     """)]
        public FileBrowserTargetPhase Phase { get; set; }

        [Description("""
                     Message explaining the state of the target.
                     """)]
        public string? Message { get; set; }
    }

    public enum FileBrowserTargetPhase
    {
        Pending,
        Preparing,
        Ready,
        Failed
    }

    [ValidationRule(
        "!has(self.readOnly) || self.readOnly || !has(self.targetRef.kind) || self.targetRef.kind != 'Backup'",
        message: "Backup targets are always read-only.")]
    public record V1Alpha1FileBrowserTarget
    {
        [Description("""
                     Name of the target: the folder users see it as when there are several targets.

                     Default is the name of the referenced object.
                     """)]
        [Pattern("^[a-z0-9]([-a-z0-9]*[a-z0-9])?$")]
        [Length(1, 63)]
        public string? Name { get; set; }

        [Description("""
                     Reference to the target in the same namespace.
                     """)]
        [Required]
        public V1Alpha1FileBrowserTargetRef TargetRef { get; set; } = new();

        [Description("""
                     Whether the target can only be read. For volumes, enforced on the mount, so nothing can be changed, uploaded or deleted. `users[].permissions` can only restrict access further.

                     Default is `false` for `Server` targets and `true` for `BackupRepo` targets. `Backup` targets are always read-only.
                     """)]
        public bool? ReadOnly { get; set; }
    }

    public record V1Alpha1FileBrowserTargetRef
    {
        [Description("""
                     Kind of the target.

                     Values:
                     - `Server`: The data of a `Server` (its `/data` folder). The `Server` must have `storage`. Do not edit world or player data while the server runs: it keeps the world in memory and overwrites the files when it saves. Configs, plugins and mods can be edited any time and apply on the next restart.
                     - `Backup`: The content of a `Completed` `Backup`: the server's data at backup time. The archive is extracted into the file manager's scratch volume (`pod.scratchSize`) before the interfaces start.
                     - `BackupRepo`: The archives in a `BackupRepo`: one folder per backup (`<backup UID>/*.tar.gz`) in volume repos, one object per backup (`<backup UID>.tar.gz`) in S3 repos.

                     Default is `Server`.
                     """)]
        public FileBrowserTargetKind? Kind { get; set; }

        [Description("""
                     Name of the target.
                     """)]
        [Length(1, 253)]
        [Required]
        public string Name { get; set; } = null!;
    }

    public enum FileBrowserTargetKind
    {
        Server,
        Backup,
        BackupRepo
    }

    [ValidationRule("has(self.passwordSecretRef) || has(self.authorizedKeysSecretRef)",
        message: "A user needs a passwordSecretRef or an authorizedKeysSecretRef.")]
    public record V1Alpha1FileBrowserUser
    {
        [Description("""
                     Name the user logs in with.
                     """)]
        [Pattern("^[a-zA-Z0-9][a-zA-Z0-9._-]*$")]
        [Length(1, 63)]
        [Required]
        public string Name { get; set; } = null!;

        [Description("""
                     Reference to the key of a `Secret` holding the user's password. Changes are applied within a minute.
                     """)]
        public V1SecretKeyRef? PasswordSecretRef { get; set; }

        [Description("""
                     Reference to the key of a `Secret` holding the user's SSH public keys for SFTP, one per line (`authorized_keys` format).
                     """)]
        public V1SecretKeyRef? AuthorizedKeysSecretRef { get; set; }

        [Description("""
                     Permissions of the user. Paths are the paths the user sees: with several targets, they start with the target's folder (e.g. `/live/plugins`). The most specific path wins.

                     Default is full access, limited by `targets[].readOnly`. Permissions can only restrict, never grant more than a target's `readOnly` allows.
                     """)]
        [Items(maxItems: 64)]
        public IList<V1Alpha1FileBrowserPermission>? Permissions { get; set; }
    }

    [ValidationRule("self.path != '/' || self.access != 'None'",
        message: "The root `/` cannot have access `None`; restrict the paths below it instead.")]
    public record V1Alpha1FileBrowserPermission
    {
        [Description("""
                     Path the permission applies to, including everything below it.
                     """)]
        [Example("/plugins")]
        [Pattern("^/.*")]
        [Length(1, 1024)]
        [Required]
        public string Path { get; set; } = null!;

        [Description("""
                     Access to the path.

                     Values:
                     - `None`: The path cannot be listed or read.
                     - `ReadOnly`: Files can be listed and downloaded.
                     - `ReadWrite`: Full access.
                     """)]
        [Required]
        public FileBrowserAccess Access { get; set; }
    }

    public enum FileBrowserAccess
    {
        None,
        ReadOnly,
        ReadWrite
    }

    [ValidationRule("self.enabled || (!has(self.embedding) && !has(self.allowedFrom))",
        message: "embedding and allowedFrom require enabled.")]
    public record V1Alpha1FileBrowserWeb
    {
        [Description("""
                     Whether the web interface is enabled.
                     """)]
        [Required]
        public bool Enabled { get; set; }

        [Description("""
                     Only these peers may reach the web interface (a `NetworkPolicy` for the file manager `Pod`). Without it, the interface is reachable from everywhere its `Service` is.
                     """)]
        public IList<V1NetworkPolicyPeer>? AllowedFrom { get; set; }

        [Description("""
                     Allows embedding the web interface into other sites (`Content-Security-Policy: frame-ancestors`). Without it, embedding is forbidden.

                     **Note**: The login session cookie is `SameSite=Strict`, so the embedding site must be on the same site (registrable domain) as the web interface, e.g. `panel.example.com` and `files.example.com`.
                     """)]
        public V1Alpha1FileBrowserEmbedding? Embedding { get; set; }

        [Description("""
                     Configures the `Service` of the web interface (`<name>-web`).

                     Default port is `80`.
                     """)]
        public V1Alpha1FileBrowserService? Service { get; set; }

        [Title("Gateway API")]
        [Description("""
                     Configures an optional `HTTPRoute` (`<name>-web`) exposing the web interface under a hostname.

                     Removing it deletes the `HTTPRoute`.
                     """)]
        public V1Alpha1FileBrowserHttpRoute? HttpRoute { get; set; }
    }

    public record V1Alpha1FileBrowserEmbedding
    {
        [Description("""
                     Origins that may embed the web interface.
                     """)]
        [Example("https://panel.example.com")]
        [Items(1, 16)]
        [Required]
        public IList<string> AllowedOrigins { get; set; } = [];
    }

    [ValidationRule("self.enabled || !has(self.allowedFrom)", message: "allowedFrom requires enabled.")]
    public record V1Alpha1FileBrowserSftp
    {
        [Description("""
                     Whether SFTP is enabled.
                     """)]
        [Required]
        public bool Enabled { get; set; }

        [Description("""
                     Only these peers may reach SFTP (a `NetworkPolicy` for the file manager `Pod`). Without it, SFTP is reachable from everywhere its `Service` is.
                     """)]
        public IList<V1NetworkPolicyPeer>? AllowedFrom { get; set; }

        [Description("""
                     Configures the `Service` of SFTP (`<name>-sftp`).

                     Default port is `22`.
                     """)]
        public V1Alpha1FileBrowserService? Service { get; set; }

        [Title("Gateway API")]
        [Description("""
                     Configures an optional `TCPRoute` (`<name>-sftp`) exposing SFTP, an alternative to a `LoadBalancer` `Service`.

                     **Note**: Using `TCPRoute` requires at least [Gateway API v1.6.0](https://gateway-api.sigs.k8s.io/reference/api-spec/1.6/spec/) to be installed on your cluster.
                     """)]
        public V1Alpha1FileBrowserTcpRoute? TcpRoute { get; set; }
    }

    [ValidationRule("self.enabled || !has(self.allowedFrom)", message: "allowedFrom requires enabled.")]
    public record V1Alpha1FileBrowserWebDav
    {
        [Description("""
                     Whether WebDAV is enabled.
                     """)]
        [Required]
        public bool Enabled { get; set; }

        [Description("""
                     Only these peers may reach WebDAV (a `NetworkPolicy` for the file manager `Pod`). Without it, WebDAV is reachable from everywhere its `Service` is.
                     """)]
        public IList<V1NetworkPolicyPeer>? AllowedFrom { get; set; }

        [Description("""
                     Configures the `Service` of WebDAV (`<name>-webdav`).

                     Default port is `80`.
                     """)]
        public V1Alpha1FileBrowserService? Service { get; set; }

        [Title("Gateway API")]
        [Description("""
                     Configures an optional `HTTPRoute` (`<name>-webdav`) exposing WebDAV under a hostname.

                     Removing it deletes the `HTTPRoute`.
                     """)]
        public V1Alpha1FileBrowserHttpRoute? HttpRoute { get; set; }
    }

    [ValidationRule("self.enabled || (!has(self.allowedFrom) && !has(self.tokenSecretRef))",
        message: "allowedFrom and tokenSecretRef require enabled.")]
    public record V1Alpha1FileBrowserApi
    {
        [Description("""
                     Whether the REST API is enabled.
                     """)]
        [Required]
        public bool Enabled { get; set; }

        [Description("""
                     Reference to the key of a `Secret` holding the API token (letters, digits, `_` and `-`, at least 32 characters). Changes are applied within a minute.

                     Without it, a token is generated (key `api-token` in the `Secret` named in `status.credentialsSecretRef`).
                     """)]
        public V1SecretKeyRef? TokenSecretRef { get; set; }

        [Description("""
                     Only these peers may reach the API (a `NetworkPolicy` for the file manager `Pod`). Without it, the API is reachable from everywhere its `Service` is.
                     """)]
        public IList<V1NetworkPolicyPeer>? AllowedFrom { get; set; }

        [Description("""
                     Configures the `Service` of the API (`<name>-api`).

                     Default port is `80`.
                     """)]
        public V1Alpha1FileBrowserService? Service { get; set; }
    }

    public record V1Alpha1FileBrowserService
    {
        [Description("""
                     Type of the `Service`.

                     Default is `ClusterIP`.
                     """)]
        [ExternalDocs("https://kubernetes.io/docs/concepts/services-networking/service/#publishing-services-service-types")]
        public string? Type { get; set; }

        [Description("""
                     Port of the `Service`.
                     """)]
        [RangeMinimum(1)]
        [RangeMaximum(65535)]
        public int? Port { get; set; }

        [Description("""
                     Annotations of the `Service`, e.g. for load balancer settings.
                     """)]
        public IDictionary<string, string>? Annotations { get; set; }
    }

    public record V1Alpha1FileBrowserHttpRoute
    {
        [Description("""
                     Gateways (or other parents) the `HTTPRoute` attaches to.
                     """)]
        [ExternalDocs("https://gateway-api.sigs.k8s.io/reference/spec/#httproute")]
        [Required]
        public IList<V1HTTPRouteSpecParentRefs> ParentRefs { get; set; } = [];

        [Description("""
                     Hostname under which the interface is reachable.
                     """)]
        [Example("files.example.com")]
        [Required]
        public string Hostname { get; set; } = null!;
    }

    public record V1Alpha1FileBrowserTcpRoute
    {
        [Description("""
                     Gateways (or other parents) the `TCPRoute` attaches to, e.g. a listener of the gateway for SFTP (`sectionName`).
                     """)]
        [Required]
        public IList<V1TCPRouteSpecParentRefs> ParentRefs { get; set; } = [];
    }

    public record V1Alpha1FileBrowserPod
    {
        [Description("""
                     Custom image of the file manager.

                     Default is `drakkan/sftpgo` ([SFTPGo](https://github.com/drakkan/sftpgo)) in the version bundled with Kobblestone. Other images must be compatible with it.
                     """)]
        public string? Image { get; set; }

        [Description("""
                     Image pull policy of the file manager.
                     """)]
        public string? ImagePullPolicy { get; set; }

        [Description("""
                     Resource requirements of the file manager container, also used by the containers that extract `Backup` targets before it starts (they run one after another, so the `Pod` does not need more).

                     Default is a request of `10m` CPU and `64Mi` memory, and a limit of `256Mi` memory.
                     """)]
        [ExternalDocs("https://kubernetes.io/docs/reference/kubernetes-api/core/pod-v1/#resources")]
        public V1ResourceRequirements? Resources { get; set; }

        [Description("""
                     Size limit of the scratch volume (`emptyDir`) that `Backup` targets are extracted into, e.g. `10Gi`. It must hold the extracted worlds of all `Backup` targets, and, for `S3` repos, also the archive while it is extracted. A too small volume makes the target `Failed`.

                     Default is no limit (the node's free space).
                     """)]
        [Example("10Gi")]
        public string? ScratchSize { get; set; }

        [Description("""
                     Node selector of the file manager `Pod`.

                     Only applies while no target pins the `Pod` to a node: the volume of a running `Server`, or another `Pod` using a `ReadWriteOnce` volume of a target, always pins it to that node.
                     """)]
        public IDictionary<string, string>? NodeSelector { get; set; }

        [Description("""
                     Tolerations of the file manager `Pod`.
                     """)]
        public IList<V1Toleration>? Tolerations { get; set; }
    }
}
