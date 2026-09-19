using k8s.Models;

using Kobblestone.Operator.Entities.V1Alpha1;

using KubeOps.Abstractions.Rbac;
using KubeOps.Abstractions.Reconciliation;
using KubeOps.Abstractions.Reconciliation.Controller;
using KubeOps.KubernetesClient;

namespace Kobblestone.Operator.Controller;

[GenericRbac(Groups = [V1Service.KubeGroup], Resources = [V1Service.KubePluralName],
    Verbs = RbacVerb.Watch)]
[EntityRbac(typeof(V1Alpha1Router), Verbs = RbacVerb.Get | RbacVerb.Update)]
public sealed class RouterServiceController(IKubernetesClient client) : IEntityController<V1Service>
{
    public async Task<ReconciliationResult<V1Service>> ReconcileAsync(V1Service entity,
        CancellationToken cancellationToken)
    {
        var routerName = entity.GetLabel("kobblestone.io/router");

        if (routerName is null)
        {
            return ReconciliationResult<V1Service>.Success(entity);
        }

        var router = await client.GetAsync<V1Alpha1Router>(routerName, entity.Namespace(), cancellationToken)
            .ConfigureAwait(false);

        if (router is null)
        {
            return ReconciliationResult<V1Service>.Success(entity);
        }

        var clusterIp = entity.Spec.ClusterIP;

        var externalIp = entity.Status.LoadBalancer?.Ingress?.FirstOrDefault()?.Ip;

        router.Status.Address = externalIp ?? clusterIp;

        await client.UpdateStatusAsync(router, cancellationToken).ConfigureAwait(false);

        return ReconciliationResult<V1Service>.Success(entity);
    }

    public async Task<ReconciliationResult<V1Service>> DeletedAsync(V1Service entity,
        CancellationToken cancellationToken)
    {
        var routerName = entity.GetLabel("kobblestone.io/router");

        if (routerName is null)
        {
            return ReconciliationResult<V1Service>.Success(entity);
        }

        var router = await client.GetAsync<V1Alpha1Router>(routerName, entity.Namespace(), cancellationToken)
            .ConfigureAwait(false);

        if (router is null)
        {
            return ReconciliationResult<V1Service>.Success(entity);
        }

        router.Status.Address = null;

        await client.UpdateStatusAsync(router, cancellationToken).ConfigureAwait(false);

        return ReconciliationResult<V1Service>.Success(entity);
    }
}