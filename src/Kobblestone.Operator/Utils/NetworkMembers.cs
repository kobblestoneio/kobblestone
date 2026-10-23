using k8s.Models;

using Kobblestone.Operator.Entities.V1Alpha1;

using KubeOps.Abstractions.Events;
using KubeOps.KubernetesClient;
using KubeOps.KubernetesClient.Selectors;

namespace Kobblestone.Operator.Utils;

/// <summary>
/// Managed `Server`s of a `Network`: the `Server`s labelled with the network's name.
/// </summary>
public static class NetworkMembers
{
    public static async Task<List<V1Alpha1Server>> ListAsync(IKubernetesClient client, V1Alpha1Network network,
        CancellationToken cancellationToken)
    {
        var servers = await client.ListAsync<V1Alpha1Server>(network.Namespace(),
                new EqualsLabelSelector(V1Alpha1Network.NetworkLabel, network.Name()),
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return servers.ToList();
    }

    /// <summary>
    /// Applies the network's `serverDeletionPolicy` to a server that left the network (removed from `servers`, or
    /// the network is deleted).
    /// </summary>
    public static async Task RemoveAsync(IKubernetesClient client, EventPublisher emitEvent, V1Alpha1Network network,
        V1Alpha1Server server, CancellationToken cancellationToken)
    {
        if (network.Spec.ServerDeletionPolicy is V1Alpha1Network.NetworkServerDeletionPolicy.Delete)
        {
            await client.DeleteAsync(server, cancellationToken).ConfigureAwait(false);

            await emitEvent(network, "ServerDeleted", $"Server {server.Name()} left the network and was deleted.",
                EventType.Normal, cancellationToken).ConfigureAwait(false);

            return;
        }

        // Retain: the server keeps its data and runs standalone again. Without forwarding it verifies players itself,
        // so it is safe even if it becomes reachable directly.
        server.Metadata.Labels?.Remove(V1Alpha1Network.NetworkLabel);
        server.Spec.Forwarding = null;

        await client.UpdateAsync(server, cancellationToken).ConfigureAwait(false);

        await emitEvent(network, "ServerReleased",
            $"Server {server.Name()} left the network. It was kept with its data and runs standalone.",
            EventType.Normal, cancellationToken).ConfigureAwait(false);
    }
}
