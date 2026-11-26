using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

using k8s;
using k8s.Models;

using Kobblestone.Operator.Constants;
using Kobblestone.Operator.Entities.V1Alpha1;
using Kobblestone.Operator.Utils;

using KubeOps.Abstractions.Entities;
using KubeOps.Abstractions.Events;
using KubeOps.Abstractions.Rbac;
using KubeOps.Abstractions.Reconciliation;
using KubeOps.Abstractions.Reconciliation.Controller;
using KubeOps.KubernetesClient;
using KubeOps.KubernetesClient.Selectors;

using KubernetesCRDModelGen.Models.gateway.networking.k8s.io;

using static Kobblestone.Operator.Entities.V1Alpha1.V1Alpha1FileBrowser;

namespace Kobblestone.Operator.Controller;

/// <summary>
/// Runs the file manager of a `FileBrowser` (SFTPGo) next to the data of its targets and exposes its interfaces.
/// </summary>
/// <remarks>
/// <para>
/// The file manager mounts the volumes of its targets for its whole lifetime. A `ReadWriteOnce` volume can only be
/// attached to one node, so the file manager follows the other users of its volumes: while a target server runs, the
/// `Deployment` has a pod affinity to the server's `Pod`, and while another `Pod` (e.g. a backup `Job`) uses a volume,
/// it is pinned to that `Pod`'s node. File manager `Pod`s on another node are deleted, so they never block the volume.
/// If the volumes are in use on different nodes, the `FileBrowser` is `Failed` until that resolves.
/// </para>
/// <para>
/// The ServerStatefulSetController queues the `FileBrowser`s of a server (found in the <see cref="FileBrowserIndex"/>,
/// which this controller maintains) whenever the server starts, stops or moves; the
/// FileBrowserDeploymentController queues them whenever their file manager's readiness changes; the
/// KobblestonePodController queues them whenever another of Kobblestone's `Pod`s using the same servers' or repos'
/// volumes changes. The other `Pod`s are found with their labels (see <see cref="VolumeUsers"/>). Everything else (user
/// `Secret`s, `Backup`s) is picked up by periodic reconciles.
/// </para>
/// </remarks>
[EntityRbac(typeof(V1Alpha1FileBrowser), Verbs = RbacVerb.Watch | RbacVerb.Get | RbacVerb.Update)]
[EntityRbac(typeof(V1Alpha1Server), Verbs = RbacVerb.Get)]
[EntityRbac(typeof(V1Alpha1Backup), Verbs = RbacVerb.Get)]
[EntityRbac(typeof(V1Alpha1BackupRepo), Verbs = RbacVerb.Get | RbacVerb.List)]
[GenericRbac(Groups = [V1StatefulSet.KubeGroup], Resources = [V1StatefulSet.KubePluralName], Verbs = RbacVerb.Get)]
[GenericRbac(Groups = [V1Pod.KubeGroup], Resources = [V1Pod.KubePluralName],
    Verbs = RbacVerb.List | RbacVerb.Delete)]
[GenericRbac(Groups = [V1PersistentVolumeClaim.KubeGroup], Resources = [V1PersistentVolumeClaim.KubePluralName],
    Verbs = RbacVerb.Get)]
[GenericRbac(Groups = [V1Deployment.KubeGroup], Resources = [V1Deployment.KubePluralName],
    Verbs = RbacVerb.Get | RbacVerb.Create | RbacVerb.Update | RbacVerb.Delete)]
[GenericRbac(Groups = [V1Service.KubeGroup], Resources = [V1Service.KubePluralName],
    Verbs = RbacVerb.Get | RbacVerb.Create | RbacVerb.Update | RbacVerb.Delete)]
[GenericRbac(Groups = [V1Secret.KubeGroup], Resources = [V1Secret.KubePluralName],
    Verbs = RbacVerb.Get | RbacVerb.Create | RbacVerb.Update | RbacVerb.Delete)]
[GenericRbac(Groups = [V1NetworkPolicy.KubeGroup], Resources = [V1NetworkPolicy.KubePluralName],
    Verbs = RbacVerb.Get | RbacVerb.Create | RbacVerb.Update | RbacVerb.Delete)]
[GenericRbac(Groups = [V1HTTPRoute.KubeGroup], Resources = [V1HTTPRoute.KubePluralName],
    Verbs = RbacVerb.Get | RbacVerb.Create | RbacVerb.Update | RbacVerb.Delete)]
[GenericRbac(Groups = [V1TCPRoute.KubeGroup], Resources = [V1TCPRoute.KubePluralName],
    Verbs = RbacVerb.Get | RbacVerb.Create | RbacVerb.Update | RbacVerb.Delete)]
public sealed class FileBrowserController(
    IKubernetesClient client,
    EventPublisher emitEvent,
    FileBrowserIndex index)
    : IEntityController<V1Alpha1FileBrowser>
{
    private const int WebPort = 8080;

    private const int WebDavPort = 8081;

    private const int ApiPort = 8082;

    private const int SftpPort = 2022;

    private const string ConfigDirectory = "/etc/kobblestone/sftpgo";

    private const string HostKeyDirectory = "/etc/kobblestone/sftpgo-keys";

    /// <summary>Targets on volumes are mounted at <c>/mnt/targets/&lt;target name&gt;</c>.</summary>
    private const string TargetsDirectory = "/mnt/targets";

    /// <summary>
    /// UID and GID of the itzg server images, which own the files of a server. SFTPGo's image runs with the same IDs,
    /// so files created or edited in the file manager keep their owner.
    /// </summary>
    private const long ServerUserId = 1000;

    /// <summary>Name of the user generated without <c>users</c>.</summary>
    private const string GeneratedUsername = "minecraft";

    private const string PasswordKey = "password";

    private const string AdminPasswordKey = "admin-password";

    /// <summary>Key of the session signing key in the credentials `Secret`.</summary>
    private const string SigningPassphraseKey = "signing-passphrase";

    private const string HostKeyKey = "sftp-host-key";

    private const string ApiTokenKey = "api-token";

    private const string RandomChars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

    private static readonly Regex ApiTokenPattern = new("^[A-Za-z0-9_-]{32,}$");

    private static readonly TimeSpan ReadyResync = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan PendingResync = TimeSpan.FromSeconds(10);

    /// <summary>
    /// A resolved target. <see cref="Error"/> makes it (and the `FileBrowser`) `Failed`, <see cref="Waiting"/> keeps it
    /// `Pending`.
    /// </summary>
    private sealed record Target(string Name, FileBrowserTargetKind Kind, bool ReadOnly)
    {
        public V1Alpha1Server? Server { get; init; }

        public V1Alpha1Backup? Backup { get; init; }

        public V1Alpha1BackupRepo? Repo { get; init; }

        /// <summary>The volume of a `Server` or a `BackupRepo` volume target.</summary>
        public string? ClaimName { get; init; }

        public string? ClaimSubPath { get; init; }

        /// <summary>A `BackupRepo` S3 target, or the repo a `Backup` target is downloaded from.</summary>
        public SftpGoS3? S3 { get; init; }

        public string? Error { get; init; }

        public string? Waiting { get; init; }

        /// <summary>Where SFTPGo finds the files of a target on a volume.</summary>
        public string? LocalPath => Kind is FileBrowserTargetKind.BackupRepo && S3 is not null
            ? null
            : $"{TargetsDirectory}/{Name}";
    }

    /// <summary>
    /// An interface of the file manager and how it is exposed.
    /// </summary>
    private sealed record Interface(
        string Name,
        int ContainerPort,
        int DefaultServicePort,
        bool Enabled,
        V1Alpha1FileBrowserService? Service,
        IList<V1NetworkPolicyPeer>? AllowedFrom,
        V1Alpha1FileBrowserHttpRoute? HttpRoute);

    private sealed record Credentials(string AdminPassword, string? Password, string? ApiToken);

    /// <summary>
    /// Where the file manager has to run: next to the running target servers, and on the node of other `Pod`s using its
    /// `ReadWriteOnce` volumes.
    /// </summary>
    private sealed record Placement(IReadOnlyList<string> RunningServers, string? Node);

    public async Task<ReconciliationResult<V1Alpha1FileBrowser>> ReconcileAsync(V1Alpha1FileBrowser entity,
        CancellationToken cancellationToken)
    {
        // Lets the ServerStatefulSetController find the FileBrowsers of a server.
        index.Set(entity);

        var before = EntityQueueExtensions.Snapshot(entity.Status);
        var previousMessage = entity.Status.Message;
        var now = DateTime.UtcNow;

        var expiresAt = entity.Spec.ExpiresAfterSeconds is { } seconds
            ? (entity.Metadata.CreationTimestamp ?? now).ToUniversalTime().AddSeconds(seconds)
            : (DateTime?)null;

        entity.Status.ExpiresAt = expiresAt;

        if (expiresAt <= now)
        {
            await DeleteResourcesAsync(entity, cancellationToken).ConfigureAwait(false);

            entity.Status.Phase = FileBrowserPhase.Expired;
            entity.Status.Message = $"Expired at {expiresAt:u}.";
            entity.Status.Targets = [];
            entity.Status.CredentialsSecretRef = null;
            ClearAddresses(entity);
        }
        else
        {
            await ServeAsync(entity, cancellationToken).ConfigureAwait(false);
        }

        if (entity.Status.Phase is FileBrowserPhase.Failed && entity.Status.Message != previousMessage &&
            entity.Status.Message is { } message)
        {
            await emitEvent(entity, "Failed", message, EventType.Warning, cancellationToken).ConfigureAwait(false);
        }

        if (EntityQueueExtensions.Snapshot(entity.Status) != before)
        {
            entity = await client.UpdateStatusAsync(entity, cancellationToken).ConfigureAwait(false);
        }

        if (entity.Status.Phase is FileBrowserPhase.Expired)
        {
            return ReconciliationResult<V1Alpha1FileBrowser>.Success(entity);
        }

        var resync = entity.Status.Phase is FileBrowserPhase.Ready ? ReadyResync : PendingResync;

        return ReconciliationResult<V1Alpha1FileBrowser>.Success(entity,
            expiresAt - now + TimeSpan.FromSeconds(1) is { } untilExpiry && untilExpiry < resync
                ? untilExpiry
                : resync);
    }

    public Task<ReconciliationResult<V1Alpha1FileBrowser>> DeletedAsync(V1Alpha1FileBrowser entity,
        CancellationToken cancellationToken)
    {
        index.Remove(entity.Namespace(), entity.Name());

        // All resources are owned by the FileBrowser and garbage collected.
        return Task.FromResult(ReconciliationResult<V1Alpha1FileBrowser>.Success(entity));
    }

    private async Task ServeAsync(V1Alpha1FileBrowser entity, CancellationToken cancellationToken)
    {
        var credentials = await EnsureCredentialsAsync(entity, cancellationToken).ConfigureAwait(false);
        entity.Status.CredentialsSecretRef = new V1LocalObjectReference { Name = CredentialsName(entity) };

        var targets = await ResolveTargetsAsync(entity, cancellationToken).ConfigureAwait(false);

        // Lets the KobblestonePodController find this FileBrowser when another Pod uses the repos' volumes.
        index.SetRepos(entity.Namespace(), entity.Name(), RepoVolumes(targets));

        var (users, apiToken, configError) =
            await ResolveUsersAsync(entity, targets, credentials, cancellationToken).ConfigureAwait(false);

        if (configError is null && targets.All(t => t.Error is null && t.Waiting is null))
        {
            var (placement, conflict) = await PlaceAsync(entity, targets, cancellationToken).ConfigureAwait(false);

            if (conflict is not null)
            {
                targets = targets.Select(t => t.ClaimName is not null || t.Kind is FileBrowserTargetKind.Backup
                    ? t with { Error = conflict }
                    : t).ToList();
            }
            else
            {
                await RunAsync(entity, targets, placement!, users, apiToken, credentials, cancellationToken)
                    .ConfigureAwait(false);

                return;
            }
        }

        var failed = configError ?? targets.Select(t => t.Error).FirstOrDefault(e => e is not null);

        if (failed is not null)
        {
            // Release the volumes: nothing may hold them while the FileBrowser cannot be served.
            await DeleteOwnedAsync<V1Deployment>(entity, entity.Name(), cancellationToken).ConfigureAwait(false);

            SetTargets(entity, targets, t => t.Error is not null
                ? (FileBrowserTargetPhase.Failed, t.Error)
                : (FileBrowserTargetPhase.Pending, t.Waiting));

            entity.Status.Phase = FileBrowserPhase.Failed;
            entity.Status.Message = failed;
            ClearAddresses(entity);

            return;
        }

        // A target is not available yet (a Backup that has not completed). A running file manager keeps serving its
        // previous targets until then.
        SetTargets(entity, targets, t => (FileBrowserTargetPhase.Pending, t.Waiting));

        entity.Status.Phase = FileBrowserPhase.Pending;
        entity.Status.Message = targets.Select(t => t.Waiting).First(w => w is not null);
    }

    /// <summary>
    /// Runs the file manager for resolved targets and exposes its interfaces.
    /// </summary>
    private async Task RunAsync(V1Alpha1FileBrowser entity, IReadOnlyList<Target> targets, Placement placement,
        IReadOnlyList<SftpGoUser> users, string? apiToken, Credentials credentials,
        CancellationToken cancellationToken)
    {
        var dump = SftpGoDump.Render(
            targets.Select(t => new SftpGoTarget(t.Name, t.ReadOnly, t.LocalPath,
                t.Kind is FileBrowserTargetKind.BackupRepo ? t.S3 : null)).ToList(),
            users, credentials.AdminPassword, apiToken);

        await EnsureConfigAsync(entity, dump, cancellationToken).ConfigureAwait(false);

        var interfaces = Interfaces(entity);

        var deploy = await EnsureAsync(entity, entity.Name(), true,
                () => new V1Deployment { Metadata = new V1ObjectMeta(), Spec = new V1DeploymentSpec() },
                d => ApplyDeployment(d, entity, targets, interfaces, placement, ConfigHash(dump)), cancellationToken)
            .ConfigureAwait(false);

        var waitingForNode = await EvictMisplacedPodsAsync(entity, placement, cancellationToken)
            .ConfigureAwait(false);

        var addresses = new Dictionary<string, string?>();

        foreach (var @interface in interfaces)
        {
            addresses[@interface.Name] = await ExposeAsync(entity, @interface, cancellationToken)
                .ConfigureAwait(false);
        }

        entity.Status.Web = addresses["web"];
        entity.Status.Sftp = addresses["sftp"];
        entity.Status.Webdav = addresses["webdav"];
        entity.Status.Api = addresses["api"];

        await EnsureAsync(entity, entity.Name(), interfaces.Any(i => i.Enabled && i.AllowedFrom is not null),
            () => new V1NetworkPolicy { Metadata = new V1ObjectMeta(), Spec = new V1NetworkPolicySpec() },
            p => ApplyNetworkPolicy(p, entity, interfaces), cancellationToken).ConfigureAwait(false);

        var ready = deploy?.Status?.ReadyReplicas > 0 && !waitingForNode;

        var pod = ready
            ? null
            : (await client.ListAsync<V1Pod>(entity.Namespace(),
                    labelSelector: new EqualsLabelSelector(FileBrowserLabel, entity.Name()),
                    cancellationToken: cancellationToken).ConfigureAwait(false))
                .Where(p => p.Metadata.DeletionTimestamp is null)
                .MaxBy(p => p.Metadata.CreationTimestamp);

        SetTargets(entity, targets, t => ready ? (FileBrowserTargetPhase.Ready, null) : TargetState(t, pod));

        var states = entity.Status.Targets;

        if (states.FirstOrDefault(t => t.Phase is FileBrowserTargetPhase.Failed) is { } failedTarget)
        {
            entity.Status.Phase = FileBrowserPhase.Failed;
            entity.Status.Message = failedTarget.Message;
        }
        else if (ready)
        {
            entity.Status.Phase = FileBrowserPhase.Ready;
            entity.Status.Message = null;
        }
        else if (states.Any(t => t.Phase is FileBrowserTargetPhase.Preparing))
        {
            entity.Status.Phase = FileBrowserPhase.Preparing;
            entity.Status.Message = "Extracting backups.";
        }
        else
        {
            entity.Status.Phase = FileBrowserPhase.Pending;
            entity.Status.Message = placement.Node is { } node
                ? $"Starting the file manager on node {node}, where its volumes are in use."
                : placement.RunningServers.Count > 0
                    ? $"Starting the file manager on the node of Server {string.Join(", ", placement.RunningServers)}."
                    : "Starting the file manager.";
        }
    }

    /// <summary>
    /// State of a target while the file manager is not ready: `Backup` targets are extracted by init containers.
    /// </summary>
    private static (FileBrowserTargetPhase, string?) TargetState(Target target, V1Pod? pod)
    {
        if (pod is null)
        {
            return (FileBrowserTargetPhase.Pending, null);
        }

        if (pod.Status?.Phase is "Failed")
        {
            // E.g. evicted, because the scratch volume ran out of space.
            return target.Kind is FileBrowserTargetKind.Backup
                ? (FileBrowserTargetPhase.Failed,
                    $"The file manager Pod failed: {pod.Status.Reason} {pod.Status.Message} Check pod.scratchSize; retrying.")
                : (FileBrowserTargetPhase.Pending, null);
        }

        if (target.Kind is not FileBrowserTargetKind.Backup)
        {
            return (FileBrowserTargetPhase.Pending, null);
        }

        var containers = new[] { DownloadContainerName(target), ExtractContainerName(target) };
        var statuses = pod.Status?.InitContainerStatuses?.Where(s => containers.Contains(s.Name)).ToList() ?? [];

        if (statuses.FirstOrDefault(s => s.LastState?.Terminated?.ExitCode is not null and not 0) is { } failed)
        {
            return (FileBrowserTargetPhase.Failed,
                $"Extracting Backup {target.Backup!.Name()} failed ({failed.Name}: exit code {failed.LastState.Terminated.ExitCode}); retrying. See the logs of the container {failed.Name}.");
        }

        if (statuses.Count > 0 && statuses.All(s => s.State?.Terminated?.ExitCode is 0))
        {
            return (FileBrowserTargetPhase.Pending, null);
        }

        return statuses.Any(s => s.State?.Running is not null || s.State?.Terminated is not null)
            ? (FileBrowserTargetPhase.Preparing, $"Extracting Backup {target.Backup!.Name()}.")
            : (FileBrowserTargetPhase.Pending, null);
    }

    private static void SetTargets(V1Alpha1FileBrowser entity, IReadOnlyList<Target> targets,
        Func<Target, (FileBrowserTargetPhase Phase, string? Message)> state)
    {
        entity.Status.Targets = targets.Select(t =>
        {
            var (phase, message) = state(t);

            return new V1Alpha1FileBrowserTargetStatus { Name = t.Name, Phase = phase, Message = message };
        }).ToList();
    }

    private static void ClearAddresses(V1Alpha1FileBrowser entity)
    {
        entity.Status.Web = null;
        entity.Status.Sftp = null;
        entity.Status.Webdav = null;
        entity.Status.Api = null;
    }

    /// <summary>
    /// Resolves the targets and checks them for what the CRD schema cannot express.
    /// </summary>
    private async Task<List<Target>> ResolveTargetsAsync(V1Alpha1FileBrowser entity,
        CancellationToken cancellationToken)
    {
        var targets = new List<Target>();

        foreach (var spec in entity.Spec.Targets)
        {
            var name = spec.Name ?? spec.TargetRef.Name;
            var kind = spec.TargetRef.Kind ?? FileBrowserTargetKind.Server;

            targets.Add(kind switch
            {
                FileBrowserTargetKind.Server => await ResolveServerAsync(entity, spec, name, cancellationToken)
                    .ConfigureAwait(false),
                FileBrowserTargetKind.Backup => await ResolveBackupAsync(entity, spec, name, cancellationToken)
                    .ConfigureAwait(false),
                _ => await ResolveRepoAsync(entity, spec, name, cancellationToken).ConfigureAwait(false)
            });
        }

        return targets;
    }

    private async Task<Target> ResolveServerAsync(V1Alpha1FileBrowser entity, V1Alpha1FileBrowserTarget spec,
        string name, CancellationToken cancellationToken)
    {
        var target = new Target(name, FileBrowserTargetKind.Server, spec.ReadOnly is true);

        var server = await client.GetAsync<V1Alpha1Server>(spec.TargetRef.Name, entity.Namespace(), cancellationToken)
            .ConfigureAwait(false);

        if (server is null)
        {
            return target with { Error = $"Server {spec.TargetRef.Name} does not exist." };
        }

        if (server.Spec.Storage is null)
        {
            return target with { Error = $"Server {server.Name()} has no `storage`, so its files are not kept." };
        }

        return target with { Server = server, ClaimName = server.Name() };
    }

    private async Task<Target> ResolveBackupAsync(V1Alpha1FileBrowser entity, V1Alpha1FileBrowserTarget spec,
        string name, CancellationToken cancellationToken)
    {
        var target = new Target(name, FileBrowserTargetKind.Backup, true);

        var backup = await client.GetAsync<V1Alpha1Backup>(spec.TargetRef.Name, entity.Namespace(), cancellationToken)
            .ConfigureAwait(false);

        if (backup is null)
        {
            return target with { Error = $"Backup {spec.TargetRef.Name} does not exist." };
        }

        switch (backup.Status.Phase)
        {
            case BackupPhase.Failed:
                return target with { Error = $"Backup {backup.Name()} failed, it has no content." };
            case not BackupPhase.Completed:
                return target with { Waiting = $"Waiting for Backup {backup.Name()} to complete." };
        }

        var repoName = backup.Status.Repo ?? backup.Spec.RepoRef.Name;

        var repo = await client.GetAsync<V1Alpha1BackupRepo>(repoName, entity.Namespace(), cancellationToken)
            .ConfigureAwait(false);

        if (repo is null)
        {
            return target with { Error = $"BackupRepo {repoName} of Backup {backup.Name()} does not exist." };
        }

        target = target with { Backup = backup, Repo = repo };

        return await WithRepoAccessAsync(target, repo, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Target> ResolveRepoAsync(V1Alpha1FileBrowser entity, V1Alpha1FileBrowserTarget spec,
        string name, CancellationToken cancellationToken)
    {
        // Backups are not meant to be changed: read-only unless explicitly writable.
        var target = new Target(name, FileBrowserTargetKind.BackupRepo, spec.ReadOnly is not false);

        var repo = await client.GetAsync<V1Alpha1BackupRepo>(spec.TargetRef.Name, entity.Namespace(),
            cancellationToken).ConfigureAwait(false);

        if (repo is null)
        {
            return target with { Error = $"BackupRepo {spec.TargetRef.Name} does not exist." };
        }

        return await WithRepoAccessAsync(target with { Repo = repo }, repo, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Adds how the archives of a repo are accessed: its volume, or its bucket with the credentials.
    /// </summary>
    private async Task<Target> WithRepoAccessAsync(Target target, V1Alpha1BackupRepo repo,
        CancellationToken cancellationToken)
    {
        switch (repo.Spec.Type)
        {
            case BackupRepoType.PersistentVolumeClaim when repo.Spec.PersistentVolumeClaim is { } pvc:
                return target with { ClaimName = pvc.Name, ClaimSubPath = pvc.SubPath };
            case BackupRepoType.S3 when repo.Spec.S3 is { } s3:
            {
                var secret = await client.GetAsync<V1Secret>(s3.CredentialsSecretRef.Name, repo.Namespace(),
                    cancellationToken).ConfigureAwait(false);

                string? Get(string key) =>
                    secret?.Data?.TryGetValue(key, out var value) is true ? Encoding.UTF8.GetString(value) : null;

                if (Get("access-key") is not { } accessKey || Get("secret-key") is not { } secretKey)
                {
                    return target with
                    {
                        Error =
                        $"Secret {s3.CredentialsSecretRef.Name} of BackupRepo {repo.Name()} is missing or lacks the keys access-key and secret-key."
                    };
                }

                return target with { S3 = new SftpGoS3(s3.Endpoint, s3.Bucket, accessKey, secretKey) };
            }
            default:
                return target with { Error = $"BackupRepo {repo.Name()} is not configured for its type." };
        }
    }

    /// <summary>
    /// Resolves the users (the generated one, or those of the spec with their `Secret`s) and the API token. Returns an
    /// error for missing or invalid `Secret`s.
    /// </summary>
    private async Task<(IReadOnlyList<SftpGoUser> Users, string? ApiToken, string? Error)> ResolveUsersAsync(
        V1Alpha1FileBrowser entity, IReadOnlyList<Target> targets, Credentials credentials,
        CancellationToken cancellationToken)
    {
        var secrets = new Dictionary<string, V1Secret?>();

        async Task<(string? Value, string? Error)> ReadAsync(Entities.V1SecretKeyRef reference, string purpose)
        {
            if (!secrets.TryGetValue(reference.Name, out var secret))
            {
                secret = await client.GetAsync<V1Secret>(reference.Name, entity.Namespace(), cancellationToken)
                    .ConfigureAwait(false);
                secrets[reference.Name] = secret;
            }

            return secret?.Data?.TryGetValue(reference.Key, out var value) is true
                ? (Encoding.UTF8.GetString(value), null)
                : (null, $"Secret {reference.Name} with key {reference.Key} ({purpose}) not found.");
        }

        string? apiToken = null;

        if (entity.Spec.Api?.Enabled is true)
        {
            if (entity.Spec.Api.TokenSecretRef is { } tokenRef)
            {
                var (token, error) = await ReadAsync(tokenRef, "API token").ConfigureAwait(false);

                if (error is not null)
                {
                    return ([], null, error);
                }

                apiToken = token!.Trim();

                if (!ApiTokenPattern.IsMatch(apiToken))
                {
                    return ([], null,
                        $"The API token in Secret {tokenRef.Name} must have at least 32 characters, only letters, digits, _ and -.");
                }
            }
            else
            {
                apiToken = credentials.ApiToken;
            }
        }

        if (entity.Spec.Users is not { } specUsers)
        {
            return ([new SftpGoUser(GeneratedUsername, credentials.Password, [], null)], apiToken, null);
        }

        var users = new List<SftpGoUser>();

        foreach (var user in specUsers)
        {
            string? password = null;
            IReadOnlyList<string> keys = [];

            if (user.PasswordSecretRef is { } passwordRef)
            {
                var (value, error) = await ReadAsync(passwordRef, $"password of user {user.Name}")
                    .ConfigureAwait(false);

                if (error is not null)
                {
                    return ([], null, error);
                }

                password = value!.TrimEnd('\r', '\n');
            }

            if (user.AuthorizedKeysSecretRef is { } keysRef)
            {
                var (value, error) = await ReadAsync(keysRef, $"SSH keys of user {user.Name}")
                    .ConfigureAwait(false);

                if (error is not null)
                {
                    return ([], null, error);
                }

                keys = value!.Split('\n')
                    .Select(l => l.Trim())
                    .Where(l => l.Length > 0 && !l.StartsWith('#'))
                    .ToList();
            }

            var resolved = new SftpGoUser(user.Name, password, keys, user.Permissions);

            var sftpGoTargets = targets.Select(t => new SftpGoTarget(t.Name, t.ReadOnly, t.LocalPath, null)).ToList();

            if (SftpGoDump.Validate(sftpGoTargets, resolved) is { } permissionError)
            {
                return ([], null, permissionError);
            }

            users.Add(resolved);
        }

        return (users, apiToken, null);
    }

    /// <summary>
    /// Determines where the file manager has to run, from the other `Pod`s using its volumes. Returns a conflict if they
    /// are in use on different nodes, or a `ReadWriteOncePod` volume is in use at all.
    /// </summary>
    private async Task<(Placement? Placement, string? Conflict)> PlaceAsync(V1Alpha1FileBrowser entity,
        IReadOnlyList<Target> targets, CancellationToken cancellationToken)
    {
        var runningServers = new List<string>();

        foreach (var server in targets.Select(t => t.Server).OfType<V1Alpha1Server>().DistinctBy(s => s.Name()))
        {
            var statefulSet = await client
                .GetAsync<V1StatefulSet>(server.Name(), server.Namespace(), cancellationToken)
                .ConfigureAwait(false);

            if (statefulSet?.Spec.Replicas > 0)
            {
                runningServers.Add(server.Name());
            }
        }

        var nodes = new Dictionary<string, string>();
        var repos = RepoVolumes(targets).ToHashSet();

        foreach (var claim in targets.Select(t => t.ClaimName).OfType<string>().Distinct())
        {
            // The other scheduled Pods of Kobblestone using the volume, found by their labels (see VolumeUsers; Pods
            // created by users are not known). The file manager's own Pods do not count.
            var users = (await UsersOfClaimAsync(entity, targets, claim, repos, cancellationToken)
                    .ConfigureAwait(false))
                .Where(p => p.GetLabel(FileBrowserLabel) != entity.Name() &&
                            !string.IsNullOrEmpty(p.Spec.NodeName) &&
                            p.Status?.Phase is not ("Succeeded" or "Failed"))
                .ToList();

            if (users.Count == 0)
            {
                continue;
            }

            var pvc = await client.GetAsync<V1PersistentVolumeClaim>(claim, entity.Namespace(), cancellationToken)
                .ConfigureAwait(false);
            var accessModes = pvc?.Status?.AccessModes ?? pvc?.Spec.AccessModes ?? [];

            if (accessModes.Contains("ReadWriteOncePod"))
            {
                return (null,
                    $"Volume {claim} is ReadWriteOncePod and in use by Pod {users[0].Name()}, so it cannot be shared; retrying.");
            }

            // Volumes that can be attached to several nodes do not pin the file manager.
            if (accessModes.Contains("ReadWriteMany") || accessModes.Contains("ReadOnlyMany"))
            {
                continue;
            }

            foreach (var user in users)
            {
                nodes[user.Name()] = user.Spec.NodeName;
            }
        }

        // Also the repos sharing a volume with the targets, so their Jobs queue this FileBrowser as well.
        index.SetRepos(entity.Namespace(), entity.Name(), repos);

        var distinct = nodes.Values.Distinct().ToList();

        if (distinct.Count > 1)
        {
            return (null,
                $"The volumes of the targets are in use on different nodes ({string.Join(", ", nodes.Select(n => $"Pod {n.Key} on {n.Value}"))}), so they cannot be mounted together; retrying.");
        }

        return (new Placement(runningServers, distinct.SingleOrDefault()), null);
    }

    /// <summary>
    /// The `Pod`s using a volume of the targets: a server's volume, or a repo's volume, which other repos may share with
    /// another `subPath` (found with a selectable field and added to <paramref name="repos"/>).
    /// </summary>
    private async Task<IReadOnlyList<V1Pod>> UsersOfClaimAsync(V1Alpha1FileBrowser entity,
        IReadOnlyList<Target> targets, string claim, ISet<string> repos, CancellationToken cancellationToken)
    {
        if (targets.FirstOrDefault(t => t.ClaimName == claim && t.Kind is FileBrowserTargetKind.Server) is
            { Server: { } server })
        {
            return await VolumeUsers.OfServerAsync(client, entity.Namespace(), server.Name(), cancellationToken)
                .ConfigureAwait(false);
        }

        var sharing = await client.ListAsync<V1Alpha1BackupRepo>(entity.Namespace(),
            fieldSelector: SelectableFields.EqualTo(SelectableFields.RepoClaimName, claim),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var pods = new List<V1Pod>();

        foreach (var repo in sharing.Select(r => r.Name()).Distinct())
        {
            repos.Add(repo);

            pods.AddRange(await VolumeUsers.OfRepoAsync(client, entity.Namespace(), repo, cancellationToken)
                .ConfigureAwait(false));
        }

        return pods.DistinctBy(p => p.Name()).ToList();
    }

    /// <summary>
    /// Deletes file manager `Pod`s that are not on the node the volumes are in use on, e.g. because a server moved to
    /// another node or was started while the file manager was running elsewhere: they would hold the volumes on the
    /// wrong node. The `Deployment` recreates them on the right node. Returns whether a `Pod` was deleted.
    /// </summary>
    private async Task<bool> EvictMisplacedPodsAsync(V1Alpha1FileBrowser entity, Placement placement,
        CancellationToken cancellationToken)
    {
        if (placement.Node is not { } node)
        {
            return false;
        }

        var misplaced = (await client.ListAsync<V1Pod>(entity.Namespace(),
                    labelSelector: new EqualsLabelSelector(FileBrowserLabel, entity.Name()),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false))
            .Where(p => !string.IsNullOrEmpty(p.Spec.NodeName) && p.Spec.NodeName != node &&
                        p.Metadata.DeletionTimestamp is null)
            .ToList();

        foreach (var pod in misplaced)
        {
            await client.DeleteAsync(pod, cancellationToken).ConfigureAwait(false);
        }

        return misplaced.Count > 0;
    }

    /// <summary>
    /// Deletes everything the `FileBrowser` serves with. The `FileBrowser` itself and its status are kept.
    /// </summary>
    private async Task DeleteResourcesAsync(V1Alpha1FileBrowser entity, CancellationToken cancellationToken)
    {
        foreach (var @interface in Interfaces(entity))
        {
            await DeleteOwnedAsync<V1HTTPRoute>(entity, InterfaceName(entity, @interface), cancellationToken)
                .ConfigureAwait(false);
            await DeleteOwnedAsync<V1TCPRoute>(entity, InterfaceName(entity, @interface), cancellationToken)
                .ConfigureAwait(false);
            await DeleteOwnedAsync<V1Service>(entity, InterfaceName(entity, @interface), cancellationToken)
                .ConfigureAwait(false);
        }

        await DeleteOwnedAsync<V1NetworkPolicy>(entity, entity.Name(), cancellationToken).ConfigureAwait(false);
        await DeleteOwnedAsync<V1Deployment>(entity, entity.Name(), cancellationToken).ConfigureAwait(false);
        await DeleteOwnedAsync<V1Secret>(entity, ConfigName(entity), cancellationToken).ConfigureAwait(false);
        await DeleteOwnedAsync<V1Secret>(entity, CredentialsName(entity), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes a resource of the `FileBrowser`. Resources that merely have the same name are left alone.
    /// </summary>
    private async Task DeleteOwnedAsync<T>(V1Alpha1FileBrowser entity, string name,
        CancellationToken cancellationToken) where T : IKubernetesObject<V1ObjectMeta>
    {
        var resource = await client.GetAsync<T>(name, entity.Namespace(), cancellationToken).ConfigureAwait(false);

        if (resource is not null && resource.GetOwnerReference(entity) is not null)
        {
            await client.DeleteAsync(resource, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Creates or updates a resource of the `FileBrowser` if <paramref name="wanted"/>, or else deletes it. Resources
    /// that merely have the same name are left alone. Returns the resource, or <c>null</c>.
    /// </summary>
    private async Task<T?> EnsureAsync<T>(V1Alpha1FileBrowser entity, string name, bool wanted, Func<T> create,
        Action<T> apply, CancellationToken cancellationToken) where T : class, IKubernetesObject<V1ObjectMeta>
    {
        var resource = await client.GetAsync<T>(name, entity.Namespace(), cancellationToken).ConfigureAwait(false);

        if (resource is not null && resource.GetOwnerReference(entity) is null)
        {
            return null;
        }

        if (!wanted)
        {
            if (resource is not null)
            {
                await client.DeleteAsync(resource, cancellationToken).ConfigureAwait(false);
            }

            return null;
        }

        if (resource is null)
        {
            resource = create();

            apply(resource);

            return await client.CreateAsync(resource, cancellationToken).ConfigureAwait(false);
        }

        apply(resource);

        return await client.UpdateAsync(resource, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Creates or removes the `Service` and route of an interface. Returns its address.
    /// </summary>
    private async Task<string?> ExposeAsync(V1Alpha1FileBrowser entity, Interface @interface,
        CancellationToken cancellationToken)
    {
        var name = InterfaceName(entity, @interface);

        var service = await EnsureAsync(entity, name, @interface.Enabled,
            () => new V1Service { Metadata = new V1ObjectMeta(), Spec = new V1ServiceSpec() },
            s => ApplyService(s, entity, @interface), cancellationToken).ConfigureAwait(false);

        var httpRoute = await EnsureAsync(entity, name, @interface.Enabled && @interface.HttpRoute is not null,
            () => new V1HTTPRoute { Metadata = new V1ObjectMeta(), Spec = new V1HTTPRouteSpec() },
            r => ApplyHttpRoute(r, entity, @interface), cancellationToken).ConfigureAwait(false);

        if (@interface.Name is "sftp")
        {
            await EnsureAsync(entity, name, @interface.Enabled && entity.Spec.Sftp?.TcpRoute is not null,
                () => new V1TCPRoute { Metadata = new V1ObjectMeta(), Spec = new V1TCPRouteSpec { Rules = [] } },
                r => ApplyTcpRoute(r, entity, @interface), cancellationToken).ConfigureAwait(false);
        }

        return httpRoute is not null
            ? @interface.HttpRoute!.Hostname
            : service is not null
                ? $"{service.GetAddress()}:{ServicePort(@interface)}"
                : null;
    }

    /// <summary>
    /// What the operator generates, created once and kept for the lifetime of the `FileBrowser`: the password of the
    /// generated user, the password of SFTPGo's internal admin, the key SFTPGo signs login sessions with (so sessions
    /// survive restarts of the file manager), the SFTP host key (so clients do not see a changed host) and the API token.
    /// </summary>
    private async Task<Credentials> EnsureCredentialsAsync(V1Alpha1FileBrowser entity,
        CancellationToken cancellationToken)
    {
        var secret = await client.GetAsync<V1Secret>(CredentialsName(entity), entity.Namespace(), cancellationToken)
            .ConfigureAwait(false);

        if (secret is not null && secret.GetOwnerReference(entity) is null)
        {
            throw new InvalidOperationException(
                $"Secret {CredentialsName(entity)} exists and does not belong to FileBrowser {entity.Name()}.");
        }

        var data = secret?.Data?.ToDictionary(d => d.Key, d => Encoding.UTF8.GetString(d.Value)) ?? [];
        var changed = false;

        void Ensure(string key, Func<string> generate)
        {
            if (!data.ContainsKey(key))
            {
                data[key] = generate();
                changed = true;
            }
        }

        Ensure(AdminPasswordKey, () => RandomNumberGenerator.GetString(RandomChars, 32));
        Ensure(SigningPassphraseKey, () => RandomNumberGenerator.GetString(RandomChars, 64));

        if (entity.Spec.Users is null)
        {
            Ensure("username", () => GeneratedUsername);
            Ensure(PasswordKey, () => RandomNumberGenerator.GetString(RandomChars, 24));
        }

        if (entity.Spec.Sftp?.Enabled is true)
        {
            Ensure(HostKeyKey, () =>
            {
                using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                return key.ExportECPrivateKeyPem() + "\n";
            });
        }

        if (entity.Spec.Api?.Enabled is true && entity.Spec.Api.TokenSecretRef is null)
        {
            Ensure(ApiTokenKey, () => RandomNumberGenerator.GetString(RandomChars, 40));
        }

        if (changed)
        {
            if (secret is null)
            {
                secret = new V1Secret
                {
                    Metadata = new V1ObjectMeta
                    {
                        Name = CredentialsName(entity),
                        NamespaceProperty = entity.Namespace(),
                        Labels = PodLabels(entity)
                    },
                    StringData = data
                };

                secret.WithOwnerReference(entity);

                await client.CreateAsync(secret, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                secret.Data = null;
                secret.StringData = data;

                await client.UpdateAsync(secret, cancellationToken).ConfigureAwait(false);
            }
        }

        return new Credentials(data[AdminPasswordKey], data.GetValueOrDefault(PasswordKey),
            data.GetValueOrDefault(ApiTokenKey));
    }

    /// <summary>
    /// SFTPGo's users, folders and API key, loaded on startup (a dump for its in-memory data provider). It holds
    /// passwords, so it is a `Secret`. Only written when it changed.
    /// </summary>
    private async Task EnsureConfigAsync(V1Alpha1FileBrowser entity, string dump,
        CancellationToken cancellationToken)
    {
        var secret = await client.GetAsync<V1Secret>(ConfigName(entity), entity.Namespace(), cancellationToken)
            .ConfigureAwait(false);

        if (secret?.Data?.TryGetValue("users.json", out var current) is true &&
            Encoding.UTF8.GetString(current) == dump && secret.GetOwnerReference(entity) is not null)
        {
            return;
        }

        var exists = secret is not null;

        secret ??= new V1Secret { Metadata = new V1ObjectMeta() };
        secret.Metadata.Name = ConfigName(entity);
        secret.Metadata.NamespaceProperty = entity.Namespace();
        secret.Metadata.Labels = PodLabels(entity);
        secret.Data = null;
        secret.StringData = new Dictionary<string, string> { ["users.json"] = dump };
        secret.WithOwnerReference(entity);

        if (exists)
        {
            await client.UpdateAsync(secret, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await client.CreateAsync(secret, cancellationToken).ConfigureAwait(false);
        }
    }

    private static void ApplyDeployment(V1Deployment deploy, V1Alpha1FileBrowser entity,
        IReadOnlyList<Target> targets, IReadOnlyList<Interface> interfaces, Placement placement, string configHash)
    {
        deploy.Metadata.Name = entity.Name();
        deploy.Metadata.NamespaceProperty = entity.Namespace();
        // Lets the FileBrowserDeploymentController find the FileBrowser of this Deployment.
        deploy.Metadata.Labels = PodLabels(entity);

        var volumes = new List<V1Volume>
        {
            new() { Name = "config", Secret = new V1SecretVolumeSource { SecretName = ConfigName(entity) } },
            // The home of the users with several targets, which only contains the target folders.
            new() { Name = "home", EmptyDir = new V1EmptyDirVolumeSource() }
        };

        var mounts = new List<V1VolumeMount>
        {
            new() { Name = "config", MountPath = ConfigDirectory, ReadOnlyProperty = true },
            new() { Name = "home", MountPath = SftpGoDump.HomeDirectory }
        };

        // One volume per claim, even if several targets use it (e.g. a BackupRepo and a Backup from it). It is only
        // writable if a target writes to it; Backup targets only read their archive from it.
        var claims = targets.Where(t => t.ClaimName is not null)
            .GroupBy(t => t.ClaimName!)
            .Select((g, i) => (Claim: g.Key, Volume: $"claim-{i}", ReadOnly: g.All(t => t.ReadOnly)))
            .ToList();

        string VolumeOf(string claim) => claims.First(c => c.Claim == claim).Volume;

        volumes.AddRange(claims.Select(c => new V1Volume
        {
            Name = c.Volume,
            PersistentVolumeClaim = new V1PersistentVolumeClaimVolumeSource
            {
                ClaimName = c.Claim, ReadOnlyProperty = c.ReadOnly
            }
        }));

        var initContainers = new List<V1Container>();

        if (targets.Any(t => t.Kind is FileBrowserTargetKind.Backup))
        {
            volumes.Add(new V1Volume
            {
                Name = "scratch",
                EmptyDir = new V1EmptyDirVolumeSource
                {
                    SizeLimit = entity.Spec.Pod?.ScratchSize is { } size ? new ResourceQuantity(size) : null
                }
            });
        }

        foreach (var target in targets)
        {
            var path = $"{TargetsDirectory}/{target.Name}";

            switch (target.Kind)
            {
                case FileBrowserTargetKind.Server:
                    mounts.Add(new V1VolumeMount
                    {
                        Name = VolumeOf(target.ClaimName!), MountPath = path, ReadOnlyProperty = target.ReadOnly
                    });
                    break;
                case FileBrowserTargetKind.BackupRepo when target.ClaimName is not null:
                    mounts.Add(new V1VolumeMount
                    {
                        Name = VolumeOf(target.ClaimName),
                        MountPath = path,
                        SubPath = target.ClaimSubPath,
                        ReadOnlyProperty = target.ReadOnly
                    });
                    break;
                case FileBrowserTargetKind.Backup:
                    initContainers.AddRange(ExtractContainers(target, target.ClaimName is { } claim
                        ? VolumeOf(claim)
                        : null, Resources(entity)));
                    mounts.Add(new V1VolumeMount
                    {
                        Name = "scratch", MountPath = path, SubPath = $"{target.Name}/data", ReadOnlyProperty = true
                    });
                    break;
            }
        }

        var sftp = entity.Spec.Sftp?.Enabled is true;

        if (sftp)
        {
            volumes.Add(new V1Volume
            {
                Name = "host-key",
                Secret = new V1SecretVolumeSource
                {
                    SecretName = CredentialsName(entity),
                    Items = [new V1KeyToPath { Key = HostKeyKey, Path = "host_key" }],
                    // SFTPGo runs as UID 1000, the file is owned by root.
                    DefaultMode = 0x124 // 0444
                }
            });
            mounts.Add(new V1VolumeMount { Name = "host-key", MountPath = HostKeyDirectory, ReadOnlyProperty = true });
        }

        var web = interfaces.Single(i => i.Name is "web");
        var api = interfaces.Single(i => i.Name is "api");
        var webDav = interfaces.Single(i => i.Name is "webdav");

        var frameAncestors = entity.Spec.Web?.Embedding?.AllowedOrigins is { Count: > 0 } origins
            ? string.Join(' ', origins)
            : "'none'";

        var env = new List<V1EnvVar>
        {
            // Users only live in memory, loaded from the config Secret on every start.
            new() { Name = "SFTPGO_DATA_PROVIDER__DRIVER", Value = "memory" },
            new() { Name = "SFTPGO_DATA_PROVIDER__NAME", Value = $"{ConfigDirectory}/users.json" },
            // Binding 0: the web client (never the admin interface). A binding with port 0 is not started.
            new() { Name = "SFTPGO_HTTPD__BINDINGS__0__PORT", Value = web.Enabled ? $"{WebPort}" : "0" },
            new() { Name = "SFTPGO_HTTPD__BINDINGS__0__ENABLE_WEB_ADMIN", Value = "false" },
            new() { Name = "SFTPGO_HTTPD__BINDINGS__0__ENABLE_WEB_CLIENT", Value = "true" },
            new() { Name = "SFTPGO_HTTPD__BINDINGS__0__ENABLE_REST_API", Value = "false" },
            new() { Name = "SFTPGO_HTTPD__BINDINGS__0__SECURITY__ENABLED", Value = "true" },
            new()
            {
                Name = "SFTPGO_HTTPD__BINDINGS__0__SECURITY__CONTENT_SECURITY_POLICY",
                Value = $"frame-ancestors {frameAncestors}"
            },
            // Binding 1: the REST API, on its own port so it gets its own Service and NetworkPolicy rule.
            new() { Name = "SFTPGO_HTTPD__BINDINGS__1__PORT", Value = api.Enabled ? $"{ApiPort}" : "0" },
            new() { Name = "SFTPGO_HTTPD__BINDINGS__1__ENABLE_WEB_ADMIN", Value = "false" },
            new() { Name = "SFTPGO_HTTPD__BINDINGS__1__ENABLE_WEB_CLIENT", Value = "false" },
            new() { Name = "SFTPGO_HTTPD__BINDINGS__1__ENABLE_REST_API", Value = "true" },
            new() { Name = "SFTPGO_WEBDAVD__BINDINGS__0__PORT", Value = webDav.Enabled ? $"{WebDavPort}" : "0" },
            new() { Name = "SFTPGO_SFTPD__BINDINGS__0__PORT", Value = sftp ? $"{SftpPort}" : "0" },
            // Sessions are bound to the client IP by default. Behind Services, gateways and SNAT, the IP the file
            // manager sees is a node's or the gateway's, which changes when the file manager moves to another node,
            // and would log users out.
            new() { Name = "SFTPGO_HTTPD__TOKEN_VALIDATION", Value = "1" },
            // A fixed key for login sessions, so restarts do not log users out.
            new()
            {
                Name = "SFTPGO_HTTPD__SIGNING_PASSPHRASE",
                ValueFrom = new V1EnvVarSource
                {
                    SecretKeyRef = new V1SecretKeySelector
                    {
                        Name = CredentialsName(entity), Key = SigningPassphraseKey
                    }
                }
            }
        };

        if (sftp)
        {
            env.Add(new V1EnvVar { Name = "SFTPGO_SFTPD__HOST_KEYS", Value = $"{HostKeyDirectory}/host_key" });
        }

        // /healthz is served by every HTTP binding. Without one, the first enabled port is checked.
        var health = web.Enabled
            ? new V1Probe { HttpGet = new V1HTTPGetAction { Path = "/healthz", Port = WebPort } }
            : api.Enabled
                ? new V1Probe { HttpGet = new V1HTTPGetAction { Path = "/healthz", Port = ApiPort } }
                : new V1Probe
                {
                    TcpSocket = new V1TCPSocketAction { Port = sftp ? SftpPort : WebDavPort }
                };

        V1Probe Probe(int period, int initialDelay = 0, int failureThreshold = 3) => new()
        {
            HttpGet = health.HttpGet,
            TcpSocket = health.TcpSocket,
            PeriodSeconds = period,
            InitialDelaySeconds = initialDelay,
            FailureThreshold = failureThreshold
        };

        deploy.Spec.Replicas = 1;
        // Never two Pods on the same ReadWriteOnce volumes, not even during a rollout.
        deploy.Spec.Strategy = new V1DeploymentStrategy { Type = "Recreate" };
        deploy.Spec.Selector = new V1LabelSelector { MatchLabels = PodLabels(entity) };
        deploy.Spec.Template = new V1PodTemplateSpec
        {
            Metadata = new V1ObjectMeta
            {
                // The volumes it mounts, so other FileBrowsers can find it (see VolumeUsers).
                Labels = PodLabels(entity)
                    .Concat(targets.Where(t => t.ClaimName is not null).Select(t => t.Server)
                        .OfType<V1Alpha1Server>()
                        .Select(s => KeyValuePair.Create(VolumeUsers.FileBrowserServerPrefix + s.Name(), "")))
                    .Concat(RepoVolumes(targets)
                        .Select(r => KeyValuePair.Create(VolumeUsers.FileBrowserRepoPrefix + r, "")))
                    .DistinctBy(l => l.Key)
                    .ToDictionary(),
                Annotations = new Dictionary<string, string> { ["kobblestone.io/config-hash"] = configHash }
            },
            Spec = new V1PodSpec
            {
                Affinity = Affinity(placement),
                NodeSelector = placement.Node is null && placement.RunningServers.Count == 0
                    ? entity.Spec.Pod?.NodeSelector
                    : null,
                Tolerations = entity.Spec.Pod?.Tolerations,
                AutomountServiceAccountToken = false,
                EnableServiceLinks = false,
                SecurityContext = new V1PodSecurityContext
                {
                    RunAsNonRoot = true,
                    RunAsUser = ServerUserId,
                    RunAsGroup = ServerUserId,
                    SeccompProfile = new V1SeccompProfile { Type = "RuntimeDefault" }
                },
                Volumes = volumes,
                InitContainers = initContainers,
                Containers =
                [
                    new V1Container
                    {
                        Name = "sftpgo",
                        Image = entity.Spec.Pod?.Image ?? ContainerImages.FileBrowser,
                        ImagePullPolicy = entity.Spec.Pod?.ImagePullPolicy,
                        Ports = interfaces.Where(i => i.Enabled)
                            .Select(i => new V1ContainerPort { Name = i.Name, ContainerPort = i.ContainerPort })
                            .ToList(),
                        Env = env,
                        Resources = Resources(entity),
                        ReadinessProbe = Probe(5),
                        LivenessProbe = Probe(10, 30, 6),
                        SecurityContext = ContainerSecurityContext(),
                        VolumeMounts = mounts
                    }
                ]
            }
        };

        deploy.WithOwnerReference(entity);
    }

    /// <summary>
    /// Pod affinity to the running target servers (a pod affinity to a `Pod` that does not exist could never be
    /// satisfied, so only while they run), and the node the volumes are in use on.
    /// </summary>
    private static V1Affinity? Affinity(Placement placement)
    {
        if (placement.RunningServers.Count == 0 && placement.Node is null)
        {
            return null;
        }

        return new V1Affinity
        {
            PodAffinity = placement.RunningServers.Count == 0
                ? null
                : new V1PodAffinity
                {
                    RequiredDuringSchedulingIgnoredDuringExecution = placement.RunningServers
                        .Select(server => new V1PodAffinityTerm
                        {
                            LabelSelector = new V1LabelSelector
                            {
                                MatchLabels = new Dictionary<string, string>
                                {
                                    [VolumeUsers.ServerLabel] = server
                                }
                            },
                            TopologyKey = "kubernetes.io/hostname"
                        })
                        .ToList()
                },
            NodeAffinity = placement.Node is not { } node
                ? null
                : new V1NodeAffinity
                {
                    RequiredDuringSchedulingIgnoredDuringExecution = new V1NodeSelector
                    {
                        NodeSelectorTerms =
                        [
                            new V1NodeSelectorTerm
                            {
                                MatchExpressions =
                                [
                                    new V1NodeSelectorRequirement
                                    {
                                        Key = "kubernetes.io/hostname", OperatorProperty = "In", Values = [node]
                                    }
                                ]
                            }
                        ]
                    }
                }
        };
    }

    /// <summary>
    /// Resources of every container of the file manager `Pod`: `pod.resources`, or else a small default. The init
    /// containers get the same, so namespaces whose `ResourceQuota` requires requests and limits accept the `Pod`. This
    /// does not raise the `Pod`'s footprint: init containers run one after another before the file manager, so the
    /// `Pod` is scheduled for the largest of them, not their sum.
    /// </summary>
    private static V1ResourceRequirements Resources(V1Alpha1FileBrowser entity) =>
        entity.Spec.Pod?.Resources ?? ContainerResources.Helper;

    /// <summary>
    /// Init containers extracting a `Backup` target into the scratch volume (<c>/scratch/&lt;target&gt;/data</c>). The
    /// marker file keeps them from extracting again when the Pod's containers restart.
    /// </summary>
    private static IEnumerable<V1Container> ExtractContainers(Target target, string? repoVolume,
        V1ResourceRequirements resources)
    {
        var backup = target.Backup!;
        var directory = $"/scratch/{target.Name}";
        var marker = $"{directory}/.extracted";
        var mounts = new List<V1VolumeMount> { new() { Name = "scratch", MountPath = "/scratch" } };

        string archive;

        if (repoVolume is not null)
        {
            mounts.Add(new V1VolumeMount
            {
                Name = repoVolume, MountPath = "/mnt/repo", SubPath = target.ClaimSubPath, ReadOnlyProperty = true
            });

            archive = $"$(ls /mnt/repo/{backup.Uid()}/*.tar.gz 2>/dev/null | head -1)";
        }
        else
        {
            var repo = target.Repo!.Spec.S3!;

            archive = $"{directory}.tar.gz";

            yield return new V1Container
            {
                Name = DownloadContainerName(target),
                Image = ContainerImages.AwsCli,
                Command =
                [
                    "/bin/sh",
                    "-c",
                    $"""
                     set -e
                     if [ -f {marker} ]; then echo "INFO: already extracted"; exit 0; fi
                     echo "INFO: downloading Backup {backup.Name()}..."
                     aws s3 cp s3://{repo.Bucket}/{backup.Uid()}.tar.gz {archive}
                     """.ReplaceLineEndings("\n")
                ],
                Env =
                [
                    // The image's home is not writable for UID 1000.
                    new V1EnvVar { Name = "HOME", Value = "/tmp" },
                    new V1EnvVar { Name = "AWS_ENDPOINT_URL", Value = repo.Endpoint },
                    // Since AWS CLI 2.23.0, checksums are sent by default, which many S3-compatible providers reject.
                    new V1EnvVar { Name = "AWS_REQUEST_CHECKSUM_CALCULATION", Value = "when_required" },
                    new V1EnvVar { Name = "AWS_RESPONSE_CHECKSUM_VALIDATION", Value = "when_required" },
                    new V1EnvVar
                    {
                        Name = "AWS_ACCESS_KEY_ID",
                        ValueFrom = new V1EnvVarSource
                        {
                            SecretKeyRef = new V1SecretKeySelector
                            {
                                Name = repo.CredentialsSecretRef.Name, Key = "access-key"
                            }
                        }
                    },
                    new V1EnvVar
                    {
                        Name = "AWS_SECRET_ACCESS_KEY",
                        ValueFrom = new V1EnvVarSource
                        {
                            SecretKeyRef = new V1SecretKeySelector
                            {
                                Name = repo.CredentialsSecretRef.Name, Key = "secret-key"
                            }
                        }
                    }
                ],
                Resources = resources,
                SecurityContext = ContainerSecurityContext(),
                VolumeMounts = [new V1VolumeMount { Name = "scratch", MountPath = "/scratch" }]
            };
        }

        // Same layout as restores: the archive has one top-level folder (the server's data).
        yield return new V1Container
        {
            Name = ExtractContainerName(target),
            Image = ContainerImages.Ubuntu,
            Command =
            [
                "/bin/sh",
                "-c",
                $"""
                 set -e
                 if [ -f {marker} ]; then echo "INFO: already extracted"; exit 0; fi
                 ARCHIVE={archive}
                 if [ -z "$ARCHIVE" ] || [ ! -f "$ARCHIVE" ]; then
                   echo "ERROR: no archive found for Backup {backup.Name()}"
                   exit 1
                 fi
                 echo "INFO: extracting $ARCHIVE..."
                 rm -rf {directory}/data
                 mkdir -p {directory}/data
                 tar --strip-components=1 --no-same-owner -xzf "$ARCHIVE" -C {directory}/data
                 {(repoVolume is null ? $"rm -f {archive}" : "")}
                 touch {marker}
                 echo "INFO: extracted."
                 """.ReplaceLineEndings("\n")
            ],
            Resources = resources,
            SecurityContext = ContainerSecurityContext(),
            VolumeMounts = mounts
        };
    }

    private static V1SecurityContext ContainerSecurityContext() => new()
    {
        AllowPrivilegeEscalation = false, Capabilities = new V1Capabilities { Drop = ["ALL"] }
    };

    private static void ApplyService(V1Service service, V1Alpha1FileBrowser entity, Interface @interface)
    {
        service.Metadata.Name = InterfaceName(entity, @interface);
        service.Metadata.NamespaceProperty = entity.Namespace();
        service.Metadata.Annotations = @interface.Service?.Annotations;
        service.Metadata.Labels = PodLabels(entity);

        service.Spec.Type = @interface.Service?.Type ?? "ClusterIP";
        service.Spec.Selector = PodLabels(entity);
        service.Spec.Ports =
        [
            new V1ServicePort
            {
                Name = @interface.Name, Port = ServicePort(@interface), TargetPort = @interface.ContainerPort
            }
        ];

        service.WithOwnerReference(entity);
    }

    private static void ApplyHttpRoute(V1HTTPRoute route, V1Alpha1FileBrowser entity, Interface @interface)
    {
        route.Metadata.Name = InterfaceName(entity, @interface);
        route.Metadata.NamespaceProperty = entity.Namespace();
        route.Metadata.Labels = PodLabels(entity);

        route.Spec.ParentRefs = @interface.HttpRoute!.ParentRefs;
        route.Spec.Hostnames = [@interface.HttpRoute.Hostname];
        route.Spec.Rules =
        [
            new V1HTTPRouteSpecRules
            {
                BackendRefs =
                [
                    new V1HTTPRouteSpecRulesBackendRefs
                    {
                        Name = InterfaceName(entity, @interface), Port = ServicePort(@interface)
                    }
                ]
            }
        ];

        route.WithOwnerReference(entity);
    }

    private static void ApplyTcpRoute(V1TCPRoute route, V1Alpha1FileBrowser entity, Interface @interface)
    {
        route.Metadata.Name = InterfaceName(entity, @interface);
        route.Metadata.NamespaceProperty = entity.Namespace();
        route.Metadata.Labels = PodLabels(entity);

        route.Spec.ParentRefs = entity.Spec.Sftp!.TcpRoute!.ParentRefs;
        route.Spec.Rules =
        [
            new V1TCPRouteSpecRules
            {
                BackendRefs =
                [
                    new V1TCPRouteSpecRulesBackendRefs
                    {
                        Name = InterfaceName(entity, @interface), Port = ServicePort(@interface)
                    }
                ]
            }
        ];

        route.WithOwnerReference(entity);
    }

    /// <summary>
    /// Restricts the interfaces with <c>allowedFrom</c> to those peers. The other enabled interfaces stay reachable from
    /// everywhere, since the policy blocks every port it does not allow.
    /// </summary>
    private static void ApplyNetworkPolicy(V1NetworkPolicy policy, V1Alpha1FileBrowser entity,
        IReadOnlyList<Interface> interfaces)
    {
        policy.Metadata.Name = entity.Name();
        policy.Metadata.NamespaceProperty = entity.Namespace();
        policy.Metadata.Labels = PodLabels(entity);

        policy.Spec.PodSelector = new V1LabelSelector { MatchLabels = PodLabels(entity) };
        policy.Spec.PolicyTypes = ["Ingress"];
        // NetworkPolicy ports are the ports of the Pod, not of the Services.
        policy.Spec.Ingress = interfaces.Where(i => i.Enabled)
            .Select(i => new V1NetworkPolicyIngressRule
            {
                FromProperty = i.AllowedFrom,
                Ports = [new V1NetworkPolicyPort { Port = i.ContainerPort, Protocol = "TCP" }]
            })
            .ToList();

        policy.WithOwnerReference(entity);
    }

    private static IReadOnlyList<Interface> Interfaces(V1Alpha1FileBrowser entity) =>
    [
        new("web", WebPort, 80, entity.Spec.Web?.Enabled is true, entity.Spec.Web?.Service,
            entity.Spec.Web?.AllowedFrom, entity.Spec.Web?.HttpRoute),
        new("sftp", SftpPort, 22, entity.Spec.Sftp?.Enabled is true, entity.Spec.Sftp?.Service,
            entity.Spec.Sftp?.AllowedFrom, null),
        new("webdav", WebDavPort, 80, entity.Spec.Webdav?.Enabled is true, entity.Spec.Webdav?.Service,
            entity.Spec.Webdav?.AllowedFrom, entity.Spec.Webdav?.HttpRoute),
        new("api", ApiPort, 80, entity.Spec.Api?.Enabled is true, entity.Spec.Api?.Service,
            entity.Spec.Api?.AllowedFrom, null)
    ];

    /// <summary>
    /// SFTPGo loads its users only on startup: the hash in the pod template restarts it when they change.
    /// </summary>
    private static string ConfigHash(string dump) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(dump)))[..16];

    private static int ServicePort(Interface @interface) =>
        @interface.Service?.Port ?? @interface.DefaultServicePort;

    private static string InterfaceName(V1Alpha1FileBrowser entity, Interface @interface) =>
        $"{entity.Name()}-{@interface.Name}";

    private static string DownloadContainerName(Target target) => $"download-{target.Name}";

    private static string ExtractContainerName(Target target) => $"extract-{target.Name}";

    private static string CredentialsName(V1Alpha1FileBrowser entity) => $"{entity.Name()}-credentials";

    private static string ConfigName(V1Alpha1FileBrowser entity) => $"{entity.Name()}-config";

    /// <summary>
    /// The `BackupRepo`s whose volumes the targets mount (`BackupRepo` targets and `Backup`s from volume repos).
    /// </summary>
    private static IEnumerable<string> RepoVolumes(IEnumerable<Target> targets) =>
        targets.Where(t => t.ClaimName is not null && t.Kind is not FileBrowserTargetKind.Server)
            .Select(t => t.Repo?.Name())
            .OfType<string>()
            .Distinct();

    private static Dictionary<string, string> PodLabels(V1Alpha1FileBrowser entity) =>
        new() { [FileBrowserLabel] = entity.Name() };
}
