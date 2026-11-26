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
/// Watches the Deployments of `Router`s. It does not write any status itself: it only queues the `Router`, whose
/// controller takes the replica counts from the Deployment.
/// </summary>
[GenericRbac(Groups = [V1Deployment.KubeGroup], Resources = [V1Deployment.KubePluralName],
    Verbs = RbacVerb.Watch)]
[EntityRbac(typeof(V1Alpha1Router), Verbs = RbacVerb.Get)]
public sealed class RouterDeploymentController(
    IKubernetesClient client,
    EntityQueue<V1Alpha1Router> queueRouter) : IEntityController<V1Deployment>
{
    public async Task<ReconciliationResult<V1Deployment>> ReconcileAsync(V1Deployment entity,
        CancellationToken cancellationToken)
    {
        await QueueRouterAsync(entity, cancellationToken).ConfigureAwait(false);

        return ReconciliationResult<V1Deployment>.Success(entity);
    }

    public async Task<ReconciliationResult<V1Deployment>> DeletedAsync(V1Deployment entity,
        CancellationToken cancellationToken)
    {
        // The RouterController recreates the Deployment.
        await QueueRouterAsync(entity, cancellationToken).ConfigureAwait(false);

        return ReconciliationResult<V1Deployment>.Success(entity);
    }

    private async Task QueueRouterAsync(V1Deployment deployment, CancellationToken cancellationToken)
    {
        if (deployment.GetLabel("kobblestone.io/router") is not { } routerName)
        {
            return;
        }

        await queueRouter.RequeueAsync(client, routerName, deployment.Namespace(), cancellationToken)
            .ConfigureAwait(false);
    }
}
