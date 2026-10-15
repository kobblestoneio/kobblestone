using k8s;
using k8s.Models;

using KubeOps.Abstractions.Reconciliation;
using KubeOps.Abstractions.Reconciliation.Queue;
using KubeOps.KubernetesClient;

namespace Kobblestone.Operator.Utils;

/// <summary>
/// Every resource's status is written by exactly one controller: the controller of that resource. When another
/// controller observes a change that matters to a resource it does not own (e.g. the Job of a Backup finished), it
/// queues that resource for reconciliation instead of updating its status directly. The owning controller then derives
/// its status from the current state of the cluster.
/// </summary>
public static class EntityQueueExtensions
{
    /// <summary>
    /// Fetches the latest version of an entity and queues it for immediate reconciliation. Does nothing if the entity
    /// does not exist or is being deleted (deletion is driven by its finalizers).
    /// </summary>
    public static async Task RequeueAsync<TEntity>(
        this EntityQueue<TEntity> queue,
        IKubernetesClient client,
        string name,
        string @namespace,
        CancellationToken cancellationToken = default
    )
        where TEntity : IKubernetesObject<V1ObjectMeta>
    {
        var entity = await client
            .GetAsync<TEntity>(name, @namespace, cancellationToken)
            .ConfigureAwait(false);

        if (entity is null || entity.Metadata.DeletionTimestamp is not null)
        {
            return;
        }

        await queue(
                entity,
                ReconciliationType.Modified,
                ReconciliationTriggerSource.Operator,
                TimeSpan.Zero,
                0,
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Serialized form of a status, used to skip status updates that would not change anything.
    /// </summary>
    public static string Snapshot(object status) => KubernetesJson.Serialize(status);
}