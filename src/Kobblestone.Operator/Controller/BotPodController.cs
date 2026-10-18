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
/// Watches the `Pod`s of `Bot`s (readiness, restarts and termination messages). It does not write any status itself:
/// it only queues the `Bot`, whose controller derives its status from the `Pod`s.
/// </summary>
[GenericRbac(Groups = [V1Pod.KubeGroup], Resources = [V1Pod.KubePluralName], Verbs = RbacVerb.Watch)]
[EntityRbac(typeof(V1Alpha1Bot), Verbs = RbacVerb.Get)]
public sealed class BotPodController(IKubernetesClient client, EntityQueue<V1Alpha1Bot> queueBot)
    : IEntityController<V1Pod>
{
    public async Task<ReconciliationResult<V1Pod>> ReconcileAsync(V1Pod entity, CancellationToken cancellationToken)
    {
        await QueueBotAsync(entity, cancellationToken).ConfigureAwait(false);

        return ReconciliationResult<V1Pod>.Success(entity);
    }

    public async Task<ReconciliationResult<V1Pod>> DeletedAsync(V1Pod entity, CancellationToken cancellationToken)
    {
        await QueueBotAsync(entity, cancellationToken).ConfigureAwait(false);

        return ReconciliationResult<V1Pod>.Success(entity);
    }

    private async Task QueueBotAsync(V1Pod pod, CancellationToken cancellationToken)
    {
        if (pod.GetLabel(BotController.BotLabel) is { } botName)
        {
            await queueBot.RequeueAsync(client, botName, pod.Namespace(), cancellationToken).ConfigureAwait(false);
        }
    }
}
