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
/// Watches the Jobs of Backups. It does not write any status itself: it only queues the Backup and its `Server`,
/// whose controllers derive their status from the Job.
/// </summary>
[GenericRbac(Groups = [V1Job.KubeGroup], Resources = [V1Job.KubePluralName],
    Verbs = RbacVerb.Watch)]
[EntityRbac(typeof(V1Alpha1Backup), Verbs = RbacVerb.Get)]
[EntityRbac(typeof(V1Alpha1Server), Verbs = RbacVerb.Get)]
public sealed class BackupJobController(
    IKubernetesClient client,
    EntityQueue<V1Alpha1Backup> queueBackup,
    EntityQueue<V1Alpha1Server> queueServer) : IEntityController<V1Job>
{
    public async Task<ReconciliationResult<V1Job>> ReconcileAsync(V1Job entity, CancellationToken cancellationToken)
    {
        await QueueOwnersAsync(entity, cancellationToken).ConfigureAwait(false);

        return ReconciliationResult<V1Job>.Success(entity);
    }

    public async Task<ReconciliationResult<V1Job>> DeletedAsync(V1Job entity, CancellationToken cancellationToken)
    {
        // A Backup whose Job disappeared before it finished is failed by the BackupController, and the server is
        // released from being held stopped.
        await QueueOwnersAsync(entity, cancellationToken).ConfigureAwait(false);

        return ReconciliationResult<V1Job>.Success(entity);
    }

    private async Task QueueOwnersAsync(V1Job job, CancellationToken cancellationToken)
    {
        if (job.GetLabel(ServerOperations.BackupLabel) is { } backupName)
        {
            await queueBackup.RequeueAsync(client, backupName, job.Namespace(), cancellationToken)
                .ConfigureAwait(false);
        }

        if (job.GetLabel(ServerOperations.TargetServerLabel) is { } serverName)
        {
            await queueServer.RequeueAsync(client, serverName, job.Namespace(), cancellationToken)
                .ConfigureAwait(false);
        }
    }
}