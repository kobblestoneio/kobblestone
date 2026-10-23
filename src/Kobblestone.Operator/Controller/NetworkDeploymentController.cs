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
/// Watches the proxy Deployments of `Network`s. It does not write any status itself: it only queues the `Network`,
/// whose controller derives the proxy's readiness from the Deployment.
/// </summary>
[GenericRbac(Groups = [V1Deployment.KubeGroup], Resources = [V1Deployment.KubePluralName],
    Verbs = RbacVerb.Watch)]
[EntityRbac(typeof(V1Alpha1Network), Verbs = RbacVerb.Get)]
public sealed class NetworkDeploymentController(
    IKubernetesClient client,
    EntityQueue<V1Alpha1Network> queueNetwork) : IEntityController<V1Deployment>
{
    public async Task<ReconciliationResult<V1Deployment>> ReconcileAsync(V1Deployment entity,
        CancellationToken cancellationToken)
    {
        await QueueNetworkAsync(entity, cancellationToken).ConfigureAwait(false);

        return ReconciliationResult<V1Deployment>.Success(entity);
    }

    public async Task<ReconciliationResult<V1Deployment>> DeletedAsync(V1Deployment entity,
        CancellationToken cancellationToken)
    {
        // The NetworkController recreates the Deployment.
        await QueueNetworkAsync(entity, cancellationToken).ConfigureAwait(false);

        return ReconciliationResult<V1Deployment>.Success(entity);
    }

    private async Task QueueNetworkAsync(V1Deployment deployment, CancellationToken cancellationToken)
    {
        if (deployment.GetLabel(V1Alpha1Network.NetworkLabel) is not { } networkName)
        {
            return;
        }

        await queueNetwork.RequeueAsync(client, networkName, deployment.Namespace(), cancellationToken)
            .ConfigureAwait(false);
    }
}
