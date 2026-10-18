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
/// Watches the StatefulSets of `Server`s. It does not write any status itself: it only queues the owning `Server`,
/// whose controller derives its status (Running, Stopped, ...) from the StatefulSet.
/// </summary>
[GenericRbac(Groups = [V1StatefulSet.KubeGroup], Resources = [V1StatefulSet.KubePluralName],
    Verbs = RbacVerb.Watch)]
[EntityRbac(typeof(V1Alpha1Server), Verbs = RbacVerb.Get)]
public sealed class ServerStatefulSetController(
    IKubernetesClient client,
    EntityQueue<V1Alpha1Server> queueServer) : IEntityController<V1StatefulSet>
{
    public async Task<ReconciliationResult<V1StatefulSet>> ReconcileAsync(V1StatefulSet entity,
        CancellationToken cancellationToken)
    {
        await QueueServerAsync(entity, cancellationToken).ConfigureAwait(false);

        return ReconciliationResult<V1StatefulSet>.Success(entity);
    }

    public async Task<ReconciliationResult<V1StatefulSet>> DeletedAsync(V1StatefulSet entity,
        CancellationToken cancellationToken)
    {
        // The ServerController recreates the StatefulSet.
        await QueueServerAsync(entity, cancellationToken).ConfigureAwait(false);

        return ReconciliationResult<V1StatefulSet>.Success(entity);
    }

    private async Task QueueServerAsync(V1StatefulSet statefulSet, CancellationToken cancellationToken)
    {
        var serverRef = statefulSet.GetOwnerReference(e =>
            e.ApiVersion == "kobblestone.io/v1alpha1" && e.Kind == "Server");

        if (serverRef is null)
        {
            return;
        }

        await queueServer.RequeueAsync(client, serverRef.Name, statefulSet.Namespace(), cancellationToken)
            .ConfigureAwait(false);
    }
}
