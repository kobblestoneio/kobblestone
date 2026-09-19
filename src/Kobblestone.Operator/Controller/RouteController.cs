using k8s.Models;

using Kobblestone.Operator.Constants;
using Kobblestone.Operator.Entities.V1Alpha1;
using Kobblestone.Operator.Finalizer;
using Kobblestone.Operator.Services;
using Kobblestone.Operator.Utils;

using KubeOps.Abstractions.Rbac;
using KubeOps.Abstractions.Reconciliation;
using KubeOps.Abstractions.Reconciliation.Controller;
using KubeOps.Abstractions.Reconciliation.Finalizer;
using KubeOps.KubernetesClient;
using KubeOps.KubernetesClient.Selectors;

namespace Kobblestone.Operator.Controller;

[EntityRbac(typeof(V1Alpha1Route), Verbs = RbacVerb.Watch | RbacVerb.Get | RbacVerb.Update)]
[EntityRbac(typeof(V1Alpha1Server), Verbs = RbacVerb.Get)]
[EntityRbac(typeof(V1Alpha1Router), Verbs = RbacVerb.Get)]
[EntityRbac(typeof(V1Alpha1Network), Verbs = RbacVerb.Get)]
[GenericRbac(Groups = [V1Pod.KubeGroup], Resources = [V1Pod.KubePluralName],
    Verbs = RbacVerb.List | RbacVerb.Update)]
public sealed class RouteController(
    IKubernetesClient client,
    IRouterApi routerApi,
    EntityFinalizerAttacher<RouteFinalizer, V1Alpha1Route> finalizer) : IEntityController<V1Alpha1Route>
{
    public async Task<ReconciliationResult<V1Alpha1Route>> ReconcileAsync(V1Alpha1Route entity,
        CancellationToken cancellationToken)
    {
        entity = await finalizer(entity, cancellationToken).ConfigureAwait(false);

        var routerName = entity.Spec.ParentRef.Name;

        var prevRouterName = entity.Status.RouterName;

        var backend = await ResolveBackendAsync(entity, cancellationToken).ConfigureAwait(false);

        var syncRequired = prevRouterName != routerName
                           || entity.Status.Hostname != entity.Spec.Hostname
                           || entity.Status.Backend != backend;

        if (!syncRequired)
        {
            return ReconciliationResult<V1Alpha1Route>.Success(entity);
        }

        var router = await client.GetAsync<V1Alpha1Router>(routerName, entity.Namespace(), cancellationToken)
            .ConfigureAwait(false);

        if (router is null)
        {
            return ReconciliationResult<V1Alpha1Route>.Failure(entity, "Router not found");
        }

        if (!WildcardUtils.IsAllowedHost(router.Spec.Hostnames ?? [], entity.Spec.Hostname))
        {
            return ReconciliationResult<V1Alpha1Route>.Failure(entity, "Route hostname not accepted by router");
        }

        // Uninstall route on the previous router
        if (!string.IsNullOrEmpty(entity.Status.RouterName) && prevRouterName != routerName &&
            !string.IsNullOrEmpty(entity.Status.Hostname))
        {
            var prevRouterPods = await client.ListAsync<V1Pod>(entity.Namespace(),
                    new EqualsLabelSelector("kobblestone.io/router", entity.Status.RouterName),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            foreach (var pod in prevRouterPods)
            {
                // Skip Pods that have not been synced at all yet
                if (!pod.Status.Conditions.Any(c => c.Type is "RoutesSynced" && c.Status is "True"))
                {
                    continue;
                }

                var routerApiBaseUrl = $"http://{pod.Status.PodIP}:8080";

                await routerApi.DeleteRouteAsync(routerApiBaseUrl, entity.Status.Hostname, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        if (backend is null)
        {
            return ReconciliationResult<V1Alpha1Route>.Failure(entity,
                "Route cannot be installed due to missing backend",
                requeueAfter: TimeSpan.FromSeconds(10));
        }

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

            // Delete obsolete route
            if (entity.Status.Hostname is { } previousHostname && previousHostname != entity.Spec.Hostname)
            {
                await routerApi.DeleteRouteAsync(routerApiBaseUrl, previousHostname, cancellationToken)
                    .ConfigureAwait(false);
            }

            await routerApi.RegisterRouteAsync(routerApiBaseUrl, entity.Spec.Hostname, backend, cancellationToken)
                .ConfigureAwait(false);
        }

        entity.Status.RouterName = router.Name();
        entity.Status.Hostname = entity.Spec.Hostname;
        entity.Status.Backend = backend;

        entity = await client.UpdateStatusAsync(entity, cancellationToken).ConfigureAwait(false);

        return ReconciliationResult<V1Alpha1Route>.Success(entity);
    }

    private async Task<string?> ResolveBackendAsync(V1Alpha1Route route, CancellationToken cancellationToken)
    {
        var backendRef = route.Spec.BackendRef;

        switch (backendRef.Kind)
        {
            case null or "Server":
                {
                    var server = await client
                        .GetAsync<V1Alpha1Server>(backendRef.Name, backendRef.Namespace ?? route.Namespace(),
                            cancellationToken)
                        .ConfigureAwait(false);

                    return server is null
                        ? null
                        : $"{server.Name()}.{server.Namespace()}.svc.cluster.local:{(server.Spec.Service?.Port ?? MinecraftConstants.DefaultMinecraftPort)}";
                }
            case "Network":
                {
                    var network = await client
                        .GetAsync<V1Alpha1Network>(backendRef.Name, backendRef.Namespace ?? route.Namespace(),
                            cancellationToken).ConfigureAwait(false);

                    return network is null
                        ? null
                        : $"{network.Name()}.{network.Namespace()}.svc.cluster.local:{(network.Spec.Service?.Port ?? MinecraftConstants.DefaultMinecraftPort)}";
                }
            case "Router":
                {
                    var router = await client
                        .GetAsync<V1Alpha1Router>(backendRef.Name, backendRef.Namespace ?? route.Namespace(),
                            cancellationToken)
                        .ConfigureAwait(false);

                    return router is null
                        ? null
                        : $"{router.Name()}.{router.Namespace()}.svc.cluster.local:{(router.Spec.Service?.Port ?? MinecraftConstants.DefaultMinecraftPort)}";
                }
            case "Service":
                return
                    $"{backendRef.Name}.{backendRef.Namespace ?? route.Namespace()}.svc.cluster.local:{(backendRef.Port ?? MinecraftConstants.DefaultMinecraftPort)}";
            default:
                return null;
        }
    }

    public Task<ReconciliationResult<V1Alpha1Route>> DeletedAsync(V1Alpha1Route entity,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(ReconciliationResult<V1Alpha1Route>.Success(entity));
    }
}