using k8s.Models;

using Kobblestone.Operator.Entities.V1Alpha1;
using Kobblestone.Operator.Utils;

using KubeOps.Abstractions.Rbac;
using KubeOps.Abstractions.Reconciliation;
using KubeOps.Abstractions.Reconciliation.Controller;
using KubeOps.Abstractions.Reconciliation.Queue;
using KubeOps.KubernetesClient;

namespace Kobblestone.Operator.Controller;

/// <summary>
/// Watches the Jobs of Restores. It does not write any status itself: it only queues the Restore and its `Server`,
/// whose controllers derive their status from the Job.
/// </summary>
[GenericRbac(Groups = [V1Job.KubeGroup], Resources = [V1Job.KubePluralName],
    Verbs = RbacVerb.Watch)]
[EntityRbac(typeof(V1Alpha1Restore), Verbs = RbacVerb.Get)]
[EntityRbac(typeof(V1Alpha1Server), Verbs = RbacVerb.Get)]
public sealed class RestoreJobController(
    IKubernetesClient client,
    EntityQueue<V1Alpha1Restore> queueRestore,
    EntityQueue<V1Alpha1Server> queueServer) : IEntityController<V1Job>
{
    public async Task<ReconciliationResult<V1Job>> ReconcileAsync(V1Job entity, CancellationToken cancellationToken)
    {
        await QueueOwnersAsync(entity, cancellationToken).ConfigureAwait(false);

        return ReconciliationResult<V1Job>.Success(entity);
    }

    public async Task<ReconciliationResult<V1Job>> DeletedAsync(V1Job entity, CancellationToken cancellationToken)
    {
        // A Restore whose Job disappeared before it finished is failed by the RestoreController, and the server is
        // released from being held stopped.
        await QueueOwnersAsync(entity, cancellationToken).ConfigureAwait(false);

        return ReconciliationResult<V1Job>.Success(entity);
    }

    private async Task QueueOwnersAsync(V1Job job, CancellationToken cancellationToken)
    {
        if (job.GetLabel(ServerOperations.RestoreLabel) is { } restoreName)
        {
            await queueRestore.RequeueAsync(client, restoreName, job.Namespace(), cancellationToken)
                .ConfigureAwait(false);
        }

        if (job.GetLabel(ServerOperations.TargetServerLabel) is { } serverName)
        {
            await queueServer.RequeueAsync(client, serverName, job.Namespace(), cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
