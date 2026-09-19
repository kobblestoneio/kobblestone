using Kobblestone.Operator.Conditions;
using Kobblestone.Operator.Entities.V1Alpha1;

using KubeOps.Abstractions.Reconciliation;
using KubeOps.Abstractions.Reconciliation.Finalizer;
using KubeOps.KubernetesClient;

namespace Kobblestone.Operator.Finalizer;

public sealed class ServerFinalizer(
    IKubernetesClient client) : IEntityFinalizer<V1Alpha1Server>
{
    public async Task<ReconciliationResult<V1Alpha1Server>> FinalizeAsync(V1Alpha1Server entity,
        CancellationToken cancellationToken)
    {
        if (entity.HasCondition(ServerConditions.BackupInProgress))
        {
            return ReconciliationResult<V1Alpha1Server>.Failure(entity,
                "Server has backup in-progress. Retrying in 5 seconds.", requeueAfter: TimeSpan.FromSeconds(5));
        }

        return ReconciliationResult<V1Alpha1Server>.Success(entity);
    }
}