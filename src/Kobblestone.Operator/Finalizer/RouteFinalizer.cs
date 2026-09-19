using k8s.Models;

using Kobblestone.Operator.Entities.V1Alpha1;
using Kobblestone.Operator.Services;

using KubeOps.Abstractions.Reconciliation;
using KubeOps.Abstractions.Reconciliation.Finalizer;
using KubeOps.KubernetesClient;
using KubeOps.KubernetesClient.Selectors;

namespace Kobblestone.Operator.Finalizer;

public sealed class RouteFinalizer(
    IKubernetesClient client,
    IRouterApi routerApi) : IEntityFinalizer<V1Alpha1Route>
{
    public async Task<ReconciliationResult<V1Alpha1Route>> FinalizeAsync(V1Alpha1Route entity,
        CancellationToken cancellationToken)
    {
        var routerName = entity.Spec.ParentRef.Name;

        var routerPods = await client.ListAsync<V1Pod>(entity.Namespace(),
                new EqualsLabelSelector("kobblestone.io/router", routerName), cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        foreach (var routerPod in routerPods)
        {
            // Skip Pods that have not been synced at all yet
            if (!routerPod.Status.Conditions.Any(c => c.Type is "RoutesSynced" && c.Status is "True"))
            {
                continue;
            }

            var routerApiBaseUrl = $"http://{routerPod.Status.PodIP}:8080";

            await routerApi.DeleteRouteAsync(routerApiBaseUrl, entity.Spec.Hostname, cancellationToken)
                .ConfigureAwait(false);
        }

        return ReconciliationResult<V1Alpha1Route>.Success(entity);
    }
}