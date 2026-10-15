using k8s.Models;

using Kobblestone.Operator.Entities.V1Alpha1;

using KubeOps.KubernetesClient;
using KubeOps.KubernetesClient.Selectors;

namespace Kobblestone.Operator.Utils;

public enum ServerOperationKind
{
    Backup,
    Restore,
}

/// <summary>
/// A `Backup` or `Restore` that needs exclusive access to the data of a `Server`.
/// </summary>
public sealed record ServerOperation(
    ServerOperationKind Kind,
    string Name,
    DateTime CreatedAt,
    V1Job? Job
)
{
    /// <summary>
    /// The operation holds the server, i.e. its `Job` exists and has not finished yet.
    /// </summary>
    public bool IsActive => Job is not null && !Job.IsFinished();

    public bool Is(ServerOperationKind kind, string name) => Kind == kind && Name == name;

    public override string ToString() => $"{Kind} {Name}";
}

/// <summary>
/// Snapshot of all operations targeting a single `Server`.
/// </summary>
/// <param name="Active">Operations whose `Job` is currently running.</param>
/// <param name="Queued">Operations waiting to be started, oldest first.</param>
public sealed record ServerOperationsState(
    IReadOnlyList<ServerOperation> Active,
    IReadOnlyList<ServerOperation> Queued
)
{
    /// <summary>
    /// Returns the operation that prevents the given operation from starting, or <c>null</c> if it may start now.
    /// An operation may start if no other operation is active and it is the oldest queued operation (FIFO).
    /// Because every reconciler derives the same order from the same API state, at most one operation per server
    /// can ever pass this check, even if the Backup and Restore controllers run concurrently.
    /// </summary>
    public ServerOperation? GetBlocker(ServerOperationKind kind, string name) =>
        Active.FirstOrDefault(o => !o.Is(kind, name))
        ?? Queued.TakeWhile(o => !o.Is(kind, name)).FirstOrDefault();
}

/// <summary>
/// Backups and restores are serialized per `Server` without a separate lock object. The state of the resources is
/// the source of truth:
/// <list type="bullet">
/// <item>An operation is <b>active</b> while its `Job` exists and has neither completed nor failed. A failed, deleted or
/// deadline-exceeded `Job` therefore releases the server automatically — there is nothing to unlock.</item>
/// <item>An operation is <b>queued</b> while it is `Pending`, has no `Job` yet and is not being deleted.</item>
/// </list>
/// </summary>
public static class ServerOperations
{
    public const string BackupLabel = "kobblestone.io/backup";

    public const string RestoreLabel = "kobblestone.io/restore";

    /// <summary>
    /// Set on every backup/restore `Job` so that all jobs touching a server can be listed with a single call.
    /// </summary>
    public const string TargetServerLabel = "kobblestone.io/target-server";

    /// <summary>
    /// Set on backup `Job`s. `online` backups run next to the running server (RCON save-off), `offline` backups copy the
    /// data of a stopped server.
    /// </summary>
    public const string BackupModeLabel = "kobblestone.io/backup-mode";

    public const string BackupModeOnline = "online";

    public const string BackupModeOffline = "offline";

    /// <summary>
    /// Restores and offline backups need the server to stay stopped until they have finished. Otherwise the server
    /// would modify the data during the operation and, with a `ReadWriteOnce` volume, could not even start if the job
    /// runs on a different node.
    /// </summary>
    public static bool RequiresStoppedServer(this V1Job job) =>
        job.GetLabel(RestoreLabel) is not null || job.GetLabel(BackupModeLabel) is BackupModeOffline;

    public static bool IsFinished(this V1Job job) =>
        job.Status?.Conditions?.Any(c => c.Type is "Complete" or "Failed" && c.Status is "True")
            is true;

    public static bool TargetsServer(
        this V1TypedLocalObjectReference targetRef,
        string serverName
    ) => targetRef.Kind is null or "Server" && targetRef.Name == serverName;

    /// <summary>
    /// Lists the unfinished backup/restore jobs of a server. Cheap (one call), used by the `Server` controller and
    /// finalizer.
    /// </summary>
    public static async Task<IList<V1Job>> ListActiveServerJobsAsync(
        this IKubernetesClient client,
        string @namespace,
        string serverName,
        CancellationToken cancellationToken = default
    )
    {
        var jobs = await client
            .ListAsync<V1Job>(
                @namespace,
                new EqualsLabelSelector(TargetServerLabel, serverName),
                cancellationToken: cancellationToken
            )
            .ConfigureAwait(false);

        return jobs.Where(j => !j.IsFinished()).ToList();
    }

    public static async Task<ServerOperationsState> GetServerOperationsAsync(
        this IKubernetesClient client,
        string @namespace,
        string serverName,
        CancellationToken cancellationToken = default
    )
    {
        // Order matters: operations must be read BEFORE jobs. An operation that is not `Pending` anymore always had
        // a job created before its status changed, so reading in this order guarantees that every operation is
        // seen either as queued (status) or as active (job) — it can never slip through both snapshots.
        var backupsTask = client.ListAsync<V1Alpha1Backup>(
            @namespace,
            cancellationToken: cancellationToken
        );
        var restoresTask = client.ListAsync<V1Alpha1Restore>(
            @namespace,
            cancellationToken: cancellationToken
        );

        await Task.WhenAll(backupsTask, restoresTask).ConfigureAwait(false);

        var jobs = await client
            .ListAsync<V1Job>(
                @namespace,
                new EqualsLabelSelector(TargetServerLabel, serverName),
                cancellationToken: cancellationToken
            )
            .ConfigureAwait(false);

        var backupJobs = jobs.Where(j => j.GetLabel(BackupLabel) is not null)
            .GroupBy(j => j.GetLabel(BackupLabel)!)
            .ToDictionary(g => g.Key, g => g.First());

        var restoreJobs = jobs.Where(j => j.GetLabel(RestoreLabel) is not null)
            .GroupBy(j => j.GetLabel(RestoreLabel)!)
            .ToDictionary(g => g.Key, g => g.First());

        var backups = backupsTask
            .Result.Where(b => b.Spec.TargetRef.TargetsServer(serverName))
            .Select(b =>
                (
                    Operation: new ServerOperation(
                        ServerOperationKind.Backup,
                        b.Name(),
                        b.Metadata.CreationTimestamp ?? DateTime.MaxValue,
                        backupJobs.GetValueOrDefault(b.Name())
                    ),
                    Queued: b.Status.Phase is BackupPhase.Pending
                        && b.Status.StartedAt is null
                        && b.Metadata.DeletionTimestamp is null
                )
            );

        var restores = restoresTask
            .Result.Where(r => r.Spec.TargetRef.TargetsServer(serverName))
            .Select(r =>
                (
                    Operation: new ServerOperation(
                        ServerOperationKind.Restore,
                        r.Name(),
                        r.Metadata.CreationTimestamp ?? DateTime.MaxValue,
                        restoreJobs.GetValueOrDefault(r.Name())
                    ),
                    Queued: r.Status.Phase is RestorePhase.Pending
                        && r.Status.StartedAt is null
                        && r.Metadata.DeletionTimestamp is null
                )
            );

        var operations = backups.Concat(restores).ToList();

        var active = operations.Select(o => o.Operation).Where(o => o.IsActive).ToList();

        // creationTimestamp only has second precision, so ties are broken deterministically by kind and name.
        var queued = operations
            .Where(o => o.Queued && o.Operation.Job is null)
            .Select(o => o.Operation)
            .OrderBy(o => o.CreatedAt)
            .ThenBy(o => o.Kind)
            .ThenBy(o => o.Name, StringComparer.Ordinal)
            .ToList();

        return new ServerOperationsState(active, queued);
    }
}