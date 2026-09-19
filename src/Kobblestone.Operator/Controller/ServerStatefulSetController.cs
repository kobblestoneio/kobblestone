using k8s.Models;

using Kobblestone.Operator.Conditions;
using Kobblestone.Operator.Entities.V1Alpha1;
using Kobblestone.Operator.Utils;

using KubeOps.Abstractions.Rbac;
using KubeOps.Abstractions.Reconciliation;
using KubeOps.Abstractions.Reconciliation.Controller;
using KubeOps.KubernetesClient;

namespace Kobblestone.Operator.Controller;

[GenericRbac(Groups = [V1StatefulSet.KubeGroup], Resources = [V1StatefulSet.KubePluralName],
    Verbs = RbacVerb.Watch)]
[EntityRbac(typeof(V1Alpha1Server), Verbs = RbacVerb.Get | RbacVerb.Update)]
public sealed class ServerStatefulSetController(IKubernetesClient client) : IEntityController<V1StatefulSet>
{
    public async Task<ReconciliationResult<V1StatefulSet>> ReconcileAsync(V1StatefulSet entity,
        CancellationToken cancellationToken)
    {
        var serverRef = entity.GetOwnerReference(e => e.ApiVersion == "kobblestone.io/v1alpha1" && e.Kind == "Server");

        if (serverRef is null)
        {
            return ReconciliationResult<V1StatefulSet>.Success(entity);
        }

        var server = await client.GetAsync<V1Alpha1Server>(serverRef.Name, entity.Namespace(), cancellationToken)
            .ConfigureAwait(false);

        if (server is null)
        {
            return ReconciliationResult<V1StatefulSet>.Success(entity);
        }

        var conditions = ServerUtils.GetConditions(entity);

        server.WithConditions(conditions);

        server.Status.Phase = server.GetPhase();

        await client.UpdateStatusAsync(server, cancellationToken).ConfigureAwait(false);

        return ReconciliationResult<V1StatefulSet>.Success(entity);
    }

    public Task<ReconciliationResult<V1StatefulSet>> DeletedAsync(V1StatefulSet entity,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(ReconciliationResult<V1StatefulSet>.Success(entity));
    }
}