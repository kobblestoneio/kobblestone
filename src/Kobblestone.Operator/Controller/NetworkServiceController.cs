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
/// Watches the Services of `Network`s. It does not write any status itself: it only queues the `Network`, whose
/// controller derives its IP address from the Service (e.g. once a load balancer assigned an external IP).
/// </summary>
[GenericRbac(Groups = [V1Service.KubeGroup], Resources = [V1Service.KubePluralName],
    Verbs = RbacVerb.Watch)]
[EntityRbac(typeof(V1Alpha1Network), Verbs = RbacVerb.Get)]
public sealed class NetworkServiceController(
    IKubernetesClient client,
    EntityQueue<V1Alpha1Network> queueNetwork) : IEntityController<V1Service>
{
    public async Task<ReconciliationResult<V1Service>> ReconcileAsync(V1Service entity,
        CancellationToken cancellationToken)
    {
        await QueueNetworkAsync(entity, cancellationToken).ConfigureAwait(false);

        return ReconciliationResult<V1Service>.Success(entity);
    }

    public async Task<ReconciliationResult<V1Service>> DeletedAsync(V1Service entity,
        CancellationToken cancellationToken)
    {
        // The NetworkController recreates the Service.
        await QueueNetworkAsync(entity, cancellationToken).ConfigureAwait(false);

        return ReconciliationResult<V1Service>.Success(entity);
    }

    private async Task QueueNetworkAsync(V1Service service, CancellationToken cancellationToken)
    {
        if (service.GetLabel("kobblestone.io/network") is not { } networkName)
        {
            return;
        }

        await queueNetwork.RequeueAsync(client, networkName, service.Namespace(), cancellationToken)
            .ConfigureAwait(false);
    }
}