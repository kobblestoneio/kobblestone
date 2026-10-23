using Kobblestone.Operator.Entities.V1Alpha1;
using Kobblestone.Operator.Utils;

using KubeOps.Abstractions.Events;
using KubeOps.Abstractions.Reconciliation;
using KubeOps.Abstractions.Reconciliation.Finalizer;
using KubeOps.KubernetesClient;

namespace Kobblestone.Operator.Finalizer;

/// <summary>
/// Managed `Server`s are not owned by their `Network` (an owner reference would delete them, and with them their data,
/// together with the network). Instead, `serverDeletionPolicy` is applied to them when the network is deleted.
/// </summary>
public sealed class NetworkFinalizer(IKubernetesClient client, EventPublisher emitEvent)
    : IEntityFinalizer<V1Alpha1Network>
{
    public async Task<ReconciliationResult<V1Alpha1Network>> FinalizeAsync(V1Alpha1Network entity,
        CancellationToken cancellationToken)
    {
        foreach (var server in await NetworkMembers.ListAsync(client, entity, cancellationToken).ConfigureAwait(false))
        {
            await NetworkMembers.RemoveAsync(client, emitEvent, entity, server, cancellationToken)
                .ConfigureAwait(false);
        }

        return ReconciliationResult<V1Alpha1Network>.Success(entity);
    }
}
