using k8s.Models;

using Kobblestone.Operator.Entities.V1Alpha1;
using Kobblestone.Operator.Services;
using Kobblestone.Operator.Utils;

using KubeOps.Abstractions.Rbac;
using KubeOps.Abstractions.Reconciliation;
using KubeOps.Abstractions.Reconciliation.Controller;
using KubeOps.KubernetesClient;

namespace Kobblestone.Operator.Controller;

[GenericRbac(Groups = [V1Pod.KubeGroup], Resources = [V1Pod.KubePluralName, $"{V1Pod.KubePluralName}/status"],
    Verbs = RbacVerb.Watch | RbacVerb.Get | RbacVerb.Update)]
[EntityRbac(typeof(V1Alpha1Server), Verbs = RbacVerb.List | RbacVerb.Update)]
[EntityRbac(typeof(V1Alpha1Router), Verbs = RbacVerb.Get)]
[EntityRbac(typeof(V1Alpha1Route), Verbs = RbacVerb.List)]
public class RouterPodController(
    IKubernetesClient client,
    IRouterApi routerApi) : IEntityController<V1Pod>
{
    public async Task<ReconciliationResult<V1Pod>> ReconcileAsync(V1Pod entity, CancellationToken cancellationToken)
    {
        var routerName = entity.GetLabel("kobblestone.io/router");

        if (routerName is null)
        {
            return ReconciliationResult<V1Pod>.Success(entity);
        }

        var router = await client.GetAsync<V1Alpha1Router>(routerName, entity.Namespace(), cancellationToken)
            .ConfigureAwait(false);

        if (router is null)
        {
            return ReconciliationResult<V1Pod>.Failure(entity, "Router object not found");
        }

        var containersReadyCondition = entity.Status.Conditions?.FirstOrDefault(c => c.Type == "ContainersReady");

        var routesCondition = entity.Status.Conditions?.FirstOrDefault(c => c.Type == "RoutesSynced");

        // If containers are not ready for some reason and the routes have been synced before, they need to be resynced.
        if (containersReadyCondition is not { Status: "True" } && routesCondition is { Status: "True" })
        {
            var now = DateTime.UtcNow;

            routesCondition.Status = "False";
            routesCondition.Reason = "ContainersNotReady";
            routesCondition.LastProbeTime = now;
            routesCondition.LastTransitionTime = now;
            routesCondition.Message = "Routes need to be resynced because of unready containers";

            await client.UpdateStatusAsync(entity, cancellationToken).ConfigureAwait(false);

            return ReconciliationResult<V1Pod>.Success(entity);
        }

        if (routesCondition is { Status: "True" })
        {
            return ReconciliationResult<V1Pod>.Success(entity);
        }

        var routerApiBaseUrl = $"http://{entity.Status.PodIP}:8080";

        var routes = await client.ListAsync<V1Alpha1Route>(entity.Namespace(), cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var currentRoutes = await routerApi.GetRoutesAsync(routerApiBaseUrl, cancellationToken).ConfigureAwait(false);

        var targetRoutes = new Dictionary<string, string>();

        foreach (var route in routes)
        {
            // Check if route should attach to this router
            if (route.Status.RouterName != router.Name())
            {
                continue;
            }

            if (route.Status is not { Hostname: { } hostname, Backend: { } backend })
            {
                continue;
            }

            if (!WildcardUtils.IsAllowedHost(router.Spec.Hostnames ?? [], hostname))
            {
                continue;
            }

            targetRoutes[hostname] = backend;
        }

        foreach (var deleteRoute in currentRoutes.Where(c => !targetRoutes.ContainsKey(c.Key)))
        {
            await routerApi.DeleteRouteAsync(routerApiBaseUrl, deleteRoute.Key, cancellationToken)
                .ConfigureAwait(false);
        }

        foreach (var route in targetRoutes)
        {
            await routerApi.RegisterRouteAsync(routerApiBaseUrl, route.Key, route.Value, cancellationToken)
                .ConfigureAwait(false);
        }

        var refreshed = await client.GetAsync<V1Pod>(entity.Name(), entity.Namespace(), cancellationToken)
            .ConfigureAwait(false);

        if (refreshed is null)
        {
            return ReconciliationResult<V1Pod>.Failure(entity, "Pod was deleted during reconciling");
        }

        routesCondition = refreshed.Status.Conditions?.FirstOrDefault(c => c.Type == "RoutesSynced");

        if (routesCondition is null)
        {
            refreshed.Status.Conditions ??= [];

            routesCondition = new V1PodCondition { Type = "RoutesSynced", };

            refreshed.Status.Conditions.Add(routesCondition);
        }

        var syncedAt = DateTime.UtcNow;

        routesCondition.LastProbeTime = syncedAt;
        routesCondition.LastTransitionTime = syncedAt;
        routesCondition.Reason = "Synced";
        routesCondition.Status = "True";
        routesCondition.Message = "Routes synced successfully";

        await client.UpdateStatusAsync(refreshed, cancellationToken).ConfigureAwait(false);

        return ReconciliationResult<V1Pod>.Success(refreshed);
    }

    public Task<ReconciliationResult<V1Pod>> DeletedAsync(V1Pod entity, CancellationToken cancellationToken)
    {
        return Task.FromResult(ReconciliationResult<V1Pod>.Success(entity));
    }
}