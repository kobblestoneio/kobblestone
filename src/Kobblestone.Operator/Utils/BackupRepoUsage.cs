using k8s.Models;

using Kobblestone.Operator.Entities.V1Alpha1;

using KubeOps.KubernetesClient;

namespace Kobblestone.Operator.Utils;

/// <summary>
/// A `PersistentVolumeClaim` repo whose volume is not `ReadWriteMany` can only be mounted from a single node at a time.
/// Backup, restore and cleanup Jobs for different servers would otherwise run in parallel on different nodes and fail
/// with Multi-Attach errors, so only one Job may use such a repo at a time.
/// <para>
/// Like the per-server serialization, this uses the Jobs as the source of truth: every unfinished Job in the namespace
/// that mounts the repo's claim counts, no matter which repo object, backup or user created it.
/// </para>
/// <para>
/// Backups, restores and cleanups stay without a Job while the repo is in use. A Job is only created once the repo is
/// free, under <see cref="LockAsync"/>, so that it cannot end up waiting for another Job.
/// </para>
/// </summary>
public static class BackupRepoUsage
{
    /// <summary>
    /// A single lock for all repos instead of one per repo: it is only held for a few API calls (check + create Job),
    /// so contention is negligible, and there is no per-repo state that would need to be cleaned up. It is never
    /// disposed on purpose: it lives as long as the process, and <see cref="SemaphoreSlim"/> only holds unmanaged
    /// resources once its <see cref="SemaphoreSlim.AvailableWaitHandle"/> has been accessed, which never happens here.
    /// </summary>
    private static readonly SemaphoreSlim Lock = new(1, 1);

    /// <summary>
    /// Serializes "check <see cref="FindJobBlockingRepoAsync"/> + create Job" across all controllers and finalizers of
    /// this operator instance. Without it, two reconciles (e.g. a Backup and a Restore of different servers) could both
    /// see the repo as free and both create a Job. Hold it until the Job has been created; once created, the Job is
    /// visible to every following check. A no-op for repos that can be used in parallel.
    /// </summary>
    /// <remarks>
    /// Only valid with a single active operator instance, which leader election guarantees.
    /// </remarks>
    public static async Task<IDisposable> LockAsync(
        V1Alpha1BackupRepo repo,
        CancellationToken cancellationToken = default
    )
    {
        if (repo.Spec is not { Type: BackupRepoType.PersistentVolumeClaim })
        {
            return new Releaser(null);
        }

        // If the wait is cancelled, it throws before a Releaser exists, so nothing is released that was not acquired.
        await Lock.WaitAsync(cancellationToken).ConfigureAwait(false);

        return new Releaser(Lock);
    }

    /// <summary>
    /// Releases the lock exactly once, even if disposed multiple times.
    /// </summary>
    private sealed class Releaser(SemaphoreSlim? semaphore) : IDisposable
    {
        private SemaphoreSlim? _semaphore = semaphore;

        public void Dispose() => Interlocked.Exchange(ref _semaphore, null)?.Release();
    }

    /// <summary>
    /// Returns an unfinished Job that currently uses the repo's volume, if the volume cannot be shared between nodes.
    /// Returns <c>null</c> if the repo can be used right now.
    /// </summary>
    /// <remarks>
    /// Unlike servers, repos are not handed out in FIFO order: a request that is still waiting for its server must not
    /// block the repo for requests of other servers. Call this while holding <see cref="LockAsync"/>.
    /// </remarks>
    public static async Task<V1Job?> FindJobBlockingRepoAsync(
        this IKubernetesClient client,
        V1Alpha1BackupRepo repo,
        CancellationToken cancellationToken = default
    )
    {
        if (repo.Spec is not { Type: BackupRepoType.PersistentVolumeClaim, PersistentVolumeClaim.Name: { } claimName })
        {
            return null;
        }

        var pvc = await client
            .GetAsync<V1PersistentVolumeClaim>(claimName, repo.Namespace(), cancellationToken)
            .ConfigureAwait(false);

        var accessModes = pvc?.Status?.AccessModes ?? pvc?.Spec?.AccessModes;

        if (pvc is null || accessModes?.Contains("ReadWriteMany") is true)
        {
            return null;
        }

        var jobs = await client
            .ListAsync<V1Job>(repo.Namespace(), cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return jobs.FirstOrDefault(j =>
            !j.IsFinished()
            && j.Spec?.Template?.Spec?.Volumes?.Any(v => v.PersistentVolumeClaim?.ClaimName == claimName) is true
        );
    }
}