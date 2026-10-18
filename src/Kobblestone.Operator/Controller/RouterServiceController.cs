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
/// Watches the Services of `Router`s. It does not write any status itself: it only queues the `Router`, whose
/// controller derives its address from the Service.
/// </summary>
[GenericRbac(Groups = [V1Service.KubeGroup], Resources = [V1Service.KubePluralName],
    Verbs = RbacVerb.Watch)]
[EntityRbac(typeof(V1Alpha1Router), Verbs = RbacVerb.Get)]
public sealed class RouterServiceController(
    IKubernetesClient client,
    EntityQueue<V1Alpha1Router> queueRouter) : IEntityController<V1Service>
{
    public async Task<ReconciliationResult<V1Service>> ReconcileAsync(V1Service entity,
        CancellationToken cancellationToken)
    {
        await QueueRouterAsync(entity, cancellationToken).ConfigureAwait(false);

        return ReconciliationResult<V1Service>.Success(entity);
    }

    public async Task<ReconciliationResult<V1Service>> DeletedAsync(V1Service entity,
        CancellationToken cancellationToken)
    {
        // The RouterController recreates the Service.
        await QueueRouterAsync(entity, cancellationToken).ConfigureAwait(false);

        return ReconciliationResult<V1Service>.Success(entity);
    }

    private async Task QueueRouterAsync(V1Service service, CancellationToken cancellationToken)
    {
        if (service.GetLabel("kobblestone.io/router") is not { } routerName)
        {
            return;
        }

        await queueRouter.RequeueAsync(client, routerName, service.Namespace(), cancellationToken)
            .ConfigureAwait(false);
    }
}
