using k8s.Models;

using Kobblestone.Operator.Entities.V1Alpha1;

using KubeOps.Abstractions.Entities;
using KubeOps.Abstractions.Rbac;
using KubeOps.Abstractions.Reconciliation;
using KubeOps.Abstractions.Reconciliation.Controller;
using KubeOps.KubernetesClient;

namespace Kobblestone.Operator.Controller;

[EntityRbac(typeof(V1Alpha1BotBehavior), Verbs = RbacVerb.Watch | RbacVerb.Get | RbacVerb.Update)]
[GenericRbac(Groups = [V1ConfigMap.KubeGroup], Resources = [V1ConfigMap.KubePluralName],
    Verbs = RbacVerb.Get | RbacVerb.Create | RbacVerb.Update)]
public sealed class BotBehaviorController(IKubernetesClient client) : IEntityController<V1Alpha1BotBehavior>
{
    private const string EmptyBehaviorCode = "module.exports = (params) => (bot) => {};";

    public async Task<ReconciliationResult<V1Alpha1BotBehavior>> ReconcileAsync(V1Alpha1BotBehavior entity,
        CancellationToken cancellationToken)
    {
        await EnsureConfigMapAsync(entity, cancellationToken).ConfigureAwait(false);

        return ReconciliationResult<V1Alpha1BotBehavior>.Success(entity);
    }

    private async Task<V1ConfigMap> EnsureConfigMapAsync(V1Alpha1BotBehavior behavior,
        CancellationToken cancellationToken)
    {
        var cm = await client.GetAsync<V1ConfigMap>(behavior.Name(), behavior.Namespace(), cancellationToken)
            .ConfigureAwait(false);

        if (cm is null)
        {
            cm = new V1ConfigMap { Metadata = new V1ObjectMeta() };

            ApplyConfigMap(cm, behavior);

            cm = await client.CreateAsync(cm, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            ApplyConfigMap(cm, behavior);

            cm = await client.UpdateAsync(cm, cancellationToken).ConfigureAwait(false);
        }

        return cm;
    }

    private static void ApplyConfigMap(V1ConfigMap cm, V1Alpha1BotBehavior behavior)
    {
        cm.Metadata.Name = behavior.Name();
        cm.Metadata.NamespaceProperty = behavior.Namespace();

        cm.Data = new Dictionary<string, string>();
        cm.Data["index.js"] = behavior.Spec.Code?.Inline ?? EmptyBehaviorCode;

        cm.WithOwnerReference(behavior);
    }

    public Task<ReconciliationResult<V1Alpha1BotBehavior>> DeletedAsync(V1Alpha1BotBehavior entity,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(ReconciliationResult<V1Alpha1BotBehavior>.Success(entity));
    }
}