using k8s.Models;

using Kobblestone.Operator.Entities.V1Alpha1;
using Kobblestone.Operator.Utils;

using KubeOps.Abstractions.Events;
using KubeOps.Abstractions.Reconciliation;
using KubeOps.Abstractions.Reconciliation.Finalizer;
using KubeOps.KubernetesClient;

namespace Kobblestone.Operator.Finalizer;

public sealed class ServerFinalizer(
    IKubernetesClient client,
    EventPublisher emitEvent) : IEntityFinalizer<V1Alpha1Server>
{
    public async Task<ReconciliationResult<V1Alpha1Server>> FinalizeAsync(V1Alpha1Server entity,
        CancellationToken cancellationToken)
    {
        // Derived from the Jobs instead of the informational BackupInProgress/RestoreInProgress conditions, so a
        // stale condition can never block the deletion of a server.
        var activeJobs = await client.ListActiveServerJobsAsync(entity.Namespace(), entity.Name(), cancellationToken)
            .ConfigureAwait(false);

        if (activeJobs.Count > 0)
        {
            await emitEvent(entity,
                "WaitingForBackupOrRestore",
                "Deletion is delayed until the running backup or restore has finished.",
                EventType.Normal, cancellationToken).ConfigureAwait(false);

            // Finalizers must keep returning Failure while waiting: Success would remove the finalizer.
            return ReconciliationResult<V1Alpha1Server>.Failure(entity,
                "Server has backup or restore in-progress. Retrying in 5 seconds.",
                requeueAfter: TimeSpan.FromSeconds(5));
        }

        return ReconciliationResult<V1Alpha1Server>.Success(entity);
    }
}
