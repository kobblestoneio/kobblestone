using k8s.Models;

using Kobblestone.Operator.Constants;
using Kobblestone.Operator.Entities.V1Alpha1;

using KubeOps.Abstractions.Entities;
using KubeOps.Abstractions.Rbac;
using KubeOps.Abstractions.Reconciliation;
using KubeOps.Abstractions.Reconciliation.Controller;
using KubeOps.KubernetesClient;

using KubernetesCRDModelGen.Models.gateway.networking.k8s.io;

namespace Kobblestone.Operator.Controller;

[EntityRbac(typeof(V1Alpha1Router), Verbs = RbacVerb.Watch | RbacVerb.Get | RbacVerb.Update)]
[GenericRbac(Groups = [V1TCPRoute.KubeGroup], Resources = [V1TCPRoute.KubePluralName],
    Verbs = RbacVerb.Get | RbacVerb.Create | RbacVerb.Update | RbacVerb.Delete)]
[GenericRbac(Groups = [V1Service.KubeGroup], Resources = [V1Service.KubePluralName],
    Verbs = RbacVerb.Get | RbacVerb.Create | RbacVerb.Update)]
[GenericRbac(Groups = [V1Deployment.KubeGroup], Resources = [V1Deployment.KubePluralName],
    Verbs = RbacVerb.Get | RbacVerb.Create | RbacVerb.Update)]
public sealed class RouterController(IKubernetesClient client) : IEntityController<V1Alpha1Router>
{
    public async Task<ReconciliationResult<V1Alpha1Router>> ReconcileAsync(V1Alpha1Router entity,
        CancellationToken cancellationToken)
    {
        await EnsureServiceAsync(entity, cancellationToken).ConfigureAwait(false);

        await EnsureTcpRouteAsync(entity, cancellationToken).ConfigureAwait(false);

        await EnsureDeploymentAsync(entity, cancellationToken).ConfigureAwait(false);

        entity = await client.UpdateStatusAsync(entity, cancellationToken).ConfigureAwait(false);

        return ReconciliationResult<V1Alpha1Router>.Success(entity);
    }

    public Task<ReconciliationResult<V1Alpha1Router>> DeletedAsync(V1Alpha1Router entity,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(ReconciliationResult<V1Alpha1Router>.Success(entity));
    }

    private async Task<V1Service> EnsureServiceAsync(V1Alpha1Router router, CancellationToken cancellationToken)
    {
        var service = await client.GetAsync<V1Service>(router.Name(), router.Namespace(), cancellationToken)
            .ConfigureAwait(false);

        if (service is null)
        {
            service = new V1Service { Metadata = new V1ObjectMeta(), Spec = new V1ServiceSpec() };

            ApplyService(service, router);

            service = await client.CreateAsync(service, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            ApplyService(service, router);

            service = await client.UpdateAsync(service, cancellationToken).ConfigureAwait(false);
        }

        return service;
    }

    private async Task<V1TCPRoute?> EnsureTcpRouteAsync(V1Alpha1Router router, CancellationToken cancellationToken)
    {
        if (router.Spec.TcpRoute is null && router.Status.TcpRoute is true)
        {
            var existingRoute = await client
                .GetAsync<V1TCPRoute>(router.Name(), router.Namespace(), cancellationToken).ConfigureAwait(false);

            if (existingRoute is null)
            {
                return null;
            }

            await client.DeleteAsync(existingRoute, cancellationToken).ConfigureAwait(false);

            router.Status.TcpRoute = null;

            return null;
        }

        if (router.Spec.TcpRoute is null)
        {
            return null;
        }

        var route = await client.GetAsync<V1TCPRoute>(router.Name(), router.Namespace(), cancellationToken)
            .ConfigureAwait(false);

        if (route is null)
        {
            route = new V1TCPRoute { Metadata = new V1ObjectMeta(), Spec = new V1TCPRouteSpec { Rules = [] } };

            ApplyTcpRoute(route, router);

            route = await client.CreateAsync(route, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            ApplyTcpRoute(route, router);

            route = await client.UpdateAsync(route, cancellationToken).ConfigureAwait(false);
        }

        router.Status.TcpRoute = true;

        return route;
    }

    private async Task<V1Deployment> EnsureDeploymentAsync(V1Alpha1Router router, CancellationToken cancellationToken)
    {
        var deploy = await client.GetAsync<V1Deployment>(router.Name(), router.Namespace(), cancellationToken)
            .ConfigureAwait(false);

        if (deploy is null)
        {
            deploy = new V1Deployment { Metadata = new V1ObjectMeta(), Spec = new V1DeploymentSpec() };

            ApplyDeployment(deploy, router);

            deploy = await client.CreateAsync(deploy, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            ApplyDeployment(deploy, router);

            deploy = await client.UpdateAsync(deploy, cancellationToken).ConfigureAwait(false);
        }

        return deploy;
    }

    private static void ApplyTcpRoute(V1TCPRoute route, V1Alpha1Router router)
    {
        route.Metadata.Name = router.Name();
        route.Metadata.NamespaceProperty = router.Namespace();

        route.Spec.ParentRefs = router.Spec.TcpRoute?.ParentRefs;
        route.Spec.Rules =
        [
            new V1TCPRouteSpecRules
            {
                BackendRefs =
                [
                    new V1TCPRouteSpecRulesBackendRefs
                    {
                        Name = router.Name(),
                        Port = router.Spec.Service?.Port ?? MinecraftConstants.DefaultMinecraftPort
                    }
                ]
            }
        ];

        route.WithOwnerReference(router);
    }

    private static void ApplyDeployment(V1Deployment deploy, V1Alpha1Router router)
    {
        deploy.Metadata.Name = router.Name();
        deploy.Metadata.NamespaceProperty = router.Namespace();

        deploy.Spec.Replicas = router.Spec.Replicas;
        deploy.Spec.Selector = new V1LabelSelector { MatchLabels = PodLabels(router) };
        deploy.Spec.Template = new V1PodTemplateSpec
        {
            Metadata = new V1ObjectMeta { Labels = PodLabels(router) },
            Spec = new V1PodSpec
            {
                NodeName = router.Spec.Pod?.NodeName,
                Affinity = router.Spec.Pod?.Affinity,
                NodeSelector = router.Spec.Pod?.NodeSelector,
                Tolerations = router.Spec.Pod?.Tolerations,
                ReadinessGates =
                [
                    new V1PodReadinessGate { ConditionType = "RoutesSynced" }
                ],
                Containers =
                [
                    new V1Container
                    {
                        Name = "router",
                        Image = router.Spec.Pod?.Image ?? "itzg/mc-router:1.46.2",
                        ImagePullPolicy = router.Spec.Pod?.ImagePullPolicy,
                        Args = new List<string> { "--api-binding", ":8080" }
                            .Concat(router.Spec.Networking?.ProxyProtocol?.Receive is true
                                ? ["--receive-proxy-protocol"]
                                : [])
                            .Concat(router.Spec.Networking?.ProxyProtocol?.Send is true
                                ? ["--use-proxy-protocol"]
                                : [])
                            .Concat(router.Spec.Networking?.ProxyProtocol?.TrustedProxies is { } trustedProxies
                                ? ["--trusted-proxies", string.Join(",", trustedProxies)]
                                : [])
                            .Concat(router.Spec.Networking?.AllowAddresses is { } allowAddresses
                                ? ["--clients-to-allow", string.Join(",", allowAddresses)]
                                : [])
                            .Concat(router.Spec.Networking?.DenyAddresses is { } denyAddresses
                                ? ["--clients-to-deny", string.Join(",", denyAddresses)]
                                : [])
                            .Concat(router.Spec.Networking?.MaxConnectionsPerSecond is { } maxConnectionsPerSecond
                                ? ["--connection-rate-limit", maxConnectionsPerSecond.ToString()]
                                : [])
                            .Concat(router.Spec.Networking?.BackendDialTimeout is { } backendDialTimeout
                                ? ["--backend-dial-timeout", backendDialTimeout]
                                : [])
                            .ToList(),
                        Ports =
                        [
                            new V1ContainerPort
                            {
                                Name = "minecraft", ContainerPort = MinecraftConstants.DefaultMinecraftPort,
                            },
                            new V1ContainerPort { Name = "api", ContainerPort = 8080 }
                        ],
                        Resources = router.Spec.Resources,
                        Env =
                        [
                            new V1EnvVar
                            {
                                Name = "MAPPING",
                                Value = string.Join("\n",
                                    (router.Spec.StaticRoutes ?? []).Select(r => $"{r.Hostname}={r.Backend}"))
                            }
                        ]
                    }
                ]
            }
        };

        deploy.WithOwnerReference(router);
    }

    private static void ApplyService(V1Service service, V1Alpha1Router router)
    {
        service.Metadata.Name = router.Name();
        service.Metadata.NamespaceProperty = router.Namespace();
        service.Metadata.Annotations = router.Spec.Service?.Annotations;
        service.Metadata.Labels = PodLabels(router);

        service.Spec.Type = router.Spec.Service?.Type ?? "ClusterIP";
        service.Spec.Selector = PodLabels(router);
        service.Spec.ExternalTrafficPolicy = router.Spec.Service?.ExternalTrafficPolicy;
        service.Spec.IpFamilyPolicy = router.Spec.Service?.IpFamilyPolicy;
        service.Spec.Ports =
        [
            new V1ServicePort
            {
                Name = "minecraft",
                Port = router.Spec.Service?.Port ?? MinecraftConstants.DefaultMinecraftPort,
                TargetPort = MinecraftConstants.DefaultMinecraftPort
            }
        ];

        service.WithOwnerReference(router);
    }

    private static Dictionary<string, string> PodLabels(V1Alpha1Router router) =>
        new() { { "kobblestone.io/router", router.Name() } };
}