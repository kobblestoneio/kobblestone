using k8s.Models;

using Kobblestone.Operator.Constants;
using Kobblestone.Operator.Entities.V1Alpha1;
using Kobblestone.Operator.Finalizer;
using Kobblestone.Operator.Services;
using Kobblestone.Operator.Utils;

using KubeOps.Abstractions.Events;
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
    EntityFinalizerAttacher<RouteFinalizer, V1Alpha1Route> finalizer,
    EventPublisher emitEvent) : IEntityController<V1Alpha1Route>
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

        // Without `Registered`, a previous reconcile did not register the route on all router Pods yet.
        if (!syncRequired && entity.Status.Conditions.Any(c => c is { Type: RegisteredCondition, Status: "True" }))
        {
            return ReconciliationResult<V1Alpha1Route>.Success(entity);
        }

        var router = await client.GetAsync<V1Alpha1Router>(routerName, entity.Namespace(), cancellationToken)
            .ConfigureAwait(false);

        if (router is null)
        {
            await emitEvent(entity,
                "RouterNotFound",
                $"Router {routerName} not found.",
                EventType.Warning, cancellationToken).ConfigureAwait(false);

            return ReconciliationResult<V1Alpha1Route>.Success(entity, TimeSpan.FromSeconds(10));
        }

        if (!WildcardUtils.IsAllowedHost(router.Spec.Hostnames ?? [], entity.Spec.Hostname))
        {
            await emitEvent(entity,
                "HostnameNotAccepted",
                $"Hostname {entity.Spec.Hostname} is not accepted by router {routerName}.",
                EventType.Warning, cancellationToken).ConfigureAwait(false);

            return ReconciliationResult<V1Alpha1Route>.Success(entity, TimeSpan.FromSeconds(30));
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
                if (!IsSynced(pod))
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
            await emitEvent(entity,
                "BackendNotFound",
                $"Route cannot be installed because its backend {entity.Spec.BackendRef.Kind ?? "Server"} " +
                $"{entity.Spec.BackendRef.Name} was not found.",
                EventType.Warning, cancellationToken).ConfigureAwait(false);

            return ReconciliationResult<V1Alpha1Route>.Success(entity, TimeSpan.FromSeconds(10));
        }

        var previousHostname = entity.Status.Hostname;

        // The status is written before the router Pods are listed: a router Pod syncing its routes (from the routes'
        // status, see RouterPodController) either reads this status, or it is listed below. Pods that have not synced
        // yet may have read the routes before, so the route is registered on them once they have synced.
        if (syncRequired)
        {
            entity.Status.RouterName = router.Name();
            entity.Status.Hostname = entity.Spec.Hostname;
            entity.Status.Backend = backend;

            SetRegistered(entity, false, "Registering", "Registering the route on the router Pods.");

            entity = await client.UpdateStatusAsync(entity, cancellationToken).ConfigureAwait(false);
        }

        var routerPods = await client.ListAsync<V1Pod>(entity.Namespace(),
                new EqualsLabelSelector("kobblestone.io/router", routerName), cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var unsyncedPods = false;

        foreach (var routerPod in routerPods)
        {
            if (routerPod.Metadata.DeletionTimestamp is not null || routerPod.Status.Phase is "Succeeded" or "Failed")
            {
                continue;
            }

            if (!IsSynced(routerPod))
            {
                unsyncedPods = true;

                continue;
            }

            var routerApiBaseUrl = $"http://{routerPod.Status.PodIP}:8080";

            // Delete obsolete route
            if (previousHostname is not null && previousHostname != entity.Spec.Hostname)
            {
                await routerApi.DeleteRouteAsync(routerApiBaseUrl, previousHostname, cancellationToken)
                    .ConfigureAwait(false);
            }

            await routerApi.RegisterRouteAsync(routerApiBaseUrl, entity.Spec.Hostname, backend, cancellationToken)
                .ConfigureAwait(false);
        }

        if (unsyncedPods)
        {
            return ReconciliationResult<V1Alpha1Route>.Success(entity, TimeSpan.FromSeconds(5));
        }

        SetRegistered(entity, true, "Registered", "The route is registered on all router Pods.");

        entity = await client.UpdateStatusAsync(entity, cancellationToken).ConfigureAwait(false);

        return ReconciliationResult<V1Alpha1Route>.Success(entity);
    }

    private const string RegisteredCondition = "Registered";

    private static bool IsSynced(V1Pod routerPod) =>
        routerPod.Status.Conditions?.Any(c => c is { Type: "RoutesSynced", Status: "True" }) is true;

    private static void SetRegistered(V1Alpha1Route route, bool registered, string reason, string message)
    {
        var status = registered ? "True" : "False";
        var condition = route.Status.Conditions.FirstOrDefault(c => c.Type == RegisteredCondition);

        if (condition is null)
        {
            condition = new V1Condition { Type = RegisteredCondition };
            route.Status.Conditions.Add(condition);
        }

        if (condition.Status != status)
        {
            condition.LastTransitionTime = DateTime.UtcNow;
        }

        condition.Status = status;
        condition.Reason = reason;
        condition.Message = message;
        condition.ObservedGeneration = route.Generation();
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
                    : $"{ServiceExtensions.ServiceDnsName(server.Name(), server.Namespace())}:{(server.Spec.Service?.Port ?? MinecraftConstants.DefaultMinecraftPort)}";
            }
            case "Network":
            {
                var network = await client
                    .GetAsync<V1Alpha1Network>(backendRef.Name, backendRef.Namespace ?? route.Namespace(),
                        cancellationToken).ConfigureAwait(false);

                return network is null
                    ? null
                    : $"{ServiceExtensions.ServiceDnsName(network.Name(), network.Namespace())}:{(network.Spec.Service?.Port ?? MinecraftConstants.DefaultMinecraftPort)}";
            }
            case "Router":
            {
                var router = await client
                    .GetAsync<V1Alpha1Router>(backendRef.Name, backendRef.Namespace ?? route.Namespace(),
                        cancellationToken)
                    .ConfigureAwait(false);

                return router is null
                    ? null
                    : $"{ServiceExtensions.ServiceDnsName(router.Name(), router.Namespace())}:{(router.Spec.Service?.Port ?? MinecraftConstants.DefaultMinecraftPort)}";
            }
            case "Service":
                return
                    $"{ServiceExtensions.ServiceDnsName(backendRef.Name, backendRef.Namespace ?? route.Namespace())}:{(backendRef.Port ?? MinecraftConstants.DefaultMinecraftPort)}";
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
