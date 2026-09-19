using System.Security.Cryptography;
using System.Text;

using k8s.Models;

using Kobblestone.Operator.Constants;
using Kobblestone.Operator.Entities.V1Alpha1;

using KubeOps.Abstractions.Entities;
using KubeOps.Abstractions.Rbac;
using KubeOps.Abstractions.Reconciliation;
using KubeOps.Abstractions.Reconciliation.Controller;
using KubeOps.KubernetesClient;

using KubernetesCRDModelGen.Models.gateway.networking.k8s.io;

using Tomlyn;
using Tomlyn.Model;

namespace Kobblestone.Operator.Controller;

[EntityRbac(typeof(V1Alpha1Network), Verbs = RbacVerb.Watch | RbacVerb.Get | RbacVerb.Update)]
[EntityRbac(typeof(V1Alpha1Route), Verbs = RbacVerb.Get | RbacVerb.Create | RbacVerb.Update | RbacVerb.Delete)]
[GenericRbac(Groups = [V1TCPRoute.KubeGroup], Resources = [V1TCPRoute.KubePluralName],
    Verbs = RbacVerb.Get | RbacVerb.Create | RbacVerb.Update | RbacVerb.Delete)]
[GenericRbac(Groups = [V1Service.KubeGroup], Resources = [V1Service.KubePluralName],
    Verbs = RbacVerb.Get | RbacVerb.Create | RbacVerb.Update)]
[GenericRbac(Groups = [V1ConfigMap.KubeGroup], Resources = [V1ConfigMap.KubePluralName],
    Verbs = RbacVerb.Get | RbacVerb.Create | RbacVerb.Update)]
[GenericRbac(Groups = [V1Secret.KubeGroup], Resources = [V1Secret.KubePluralName],
    Verbs = RbacVerb.Get | RbacVerb.Create | RbacVerb.Update)]
[GenericRbac(Groups = [V1Deployment.KubeGroup], Resources = [V1Deployment.KubePluralName],
    Verbs = RbacVerb.Get | RbacVerb.Create | RbacVerb.Update)]
public sealed class NetworkController(IKubernetesClient client) : IEntityController<V1Alpha1Network>
{
    private static readonly TomlSerializerOptions TomlOptions = new() { WriteIndented = true };

    public async Task<ReconciliationResult<V1Alpha1Network>> ReconcileAsync(V1Alpha1Network entity,
        CancellationToken cancellationToken)
    {
        await EnsureServiceAsync(entity, cancellationToken).ConfigureAwait(false);

        await EnsureTcpRouteAsync(entity, cancellationToken).ConfigureAwait(false);

        (V1ConfigMap cm, TimeSpan? requeueAfter) =
            await EnsureConfigMapAsync(entity, cancellationToken).ConfigureAwait(false);

        await EnsureSecretAsync(entity, cancellationToken).ConfigureAwait(false);

        await EnsureRouteAsync(entity, cancellationToken).ConfigureAwait(false);

        await EnsureDeploymentAsync(entity, cancellationToken).ConfigureAwait(false);

        entity = await client.UpdateStatusAsync(entity, cancellationToken).ConfigureAwait(false);

        return ReconciliationResult<V1Alpha1Network>.Success(entity, requeueAfter);
    }

    public Task<ReconciliationResult<V1Alpha1Network>> DeletedAsync(V1Alpha1Network entity,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(ReconciliationResult<V1Alpha1Network>.Success(entity));
    }

    private async Task<V1TCPRoute?> EnsureTcpRouteAsync(V1Alpha1Network network, CancellationToken cancellationToken)
    {
        if (network.Spec.TcpRoute is null && network.Status.TcpRoute is true)
        {
            var existingRoute = await client
                .GetAsync<V1TCPRoute>(network.Name(), network.Namespace(), cancellationToken).ConfigureAwait(false);

            if (existingRoute is null)
            {
                return null;
            }

            await client.DeleteAsync(existingRoute, cancellationToken).ConfigureAwait(false);

            network.Status.TcpRoute = null;

            return null;
        }

        if (network.Spec.TcpRoute is null)
        {
            return null;
        }

        var route = await client.GetAsync<V1TCPRoute>(network.Name(), network.Namespace(), cancellationToken)
            .ConfigureAwait(false);

        if (route is null)
        {
            route = new V1TCPRoute { Metadata = new V1ObjectMeta(), Spec = new V1TCPRouteSpec { Rules = [] } };

            ApplyTcpRoute(route, network);

            route = await client.CreateAsync(route, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            ApplyTcpRoute(route, network);

            route = await client.UpdateAsync(route, cancellationToken).ConfigureAwait(false);
        }

        network.Status.TcpRoute = true;

        return route;
    }

    private static void ApplyTcpRoute(V1TCPRoute route, V1Alpha1Network network)
    {
        route.Metadata.Name = network.Name();
        route.Metadata.NamespaceProperty = network.Namespace();

        route.Spec.ParentRefs = network.Spec.TcpRoute?.ParentRefs;
        route.Spec.Rules =
        [
            new V1TCPRouteSpecRules
            {
                BackendRefs =
                [
                    new V1TCPRouteSpecRulesBackendRefs
                    {
                        Name = network.Name(),
                        Port = network.Spec.Service?.Port ?? MinecraftConstants.DefaultMinecraftPort
                    }
                ]
            }
        ];

        route.WithOwnerReference(network);
    }

    private async Task<(V1ConfigMap, TimeSpan?)> EnsureConfigMapAsync(V1Alpha1Network network,
        CancellationToken cancellationToken)
    {
        TimeSpan? requeueAfter;

        var cm = await client.GetAsync<V1ConfigMap>(network.Name(), network.Namespace(), cancellationToken)
            .ConfigureAwait(false);

        if (cm is null)
        {
            cm = new V1ConfigMap { Metadata = new V1ObjectMeta() };

            requeueAfter = await ApplyConfigMapAsync(cm, network, cancellationToken).ConfigureAwait(false);

            cm = await client.CreateAsync(cm, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            requeueAfter = await ApplyConfigMapAsync(cm, network, cancellationToken).ConfigureAwait(false);

            cm = await client.UpdateAsync(cm, cancellationToken).ConfigureAwait(false);
        }

        return (cm, requeueAfter);
    }

    private async Task<V1Secret> EnsureSecretAsync(V1Alpha1Network network, CancellationToken cancellationToken)
    {
        var secret = await client.GetAsync<V1Secret>(network.Name(), network.Namespace(), cancellationToken)
            .ConfigureAwait(false);

        if (secret is null)
        {
            secret = new V1Secret
            {
                Metadata = new V1ObjectMeta { Name = network.Name(), NamespaceProperty = network.Namespace() },
                StringData = new Dictionary<string, string>()
            };

            if (network.Spec.Proxy?.ForwardingSecretRef is { } forwardingSecretRef)
            {
                var forwardingSecret = await client
                    .GetAsync<V1Secret>(forwardingSecretRef.Name, network.Namespace(), cancellationToken)
                    .ConfigureAwait(false);

                if (forwardingSecret is not null &&
                    forwardingSecret.Data.TryGetValue(forwardingSecretRef.Key, out var data))
                {
                    secret.StringData["forwarding.secret"] = Encoding.UTF8.GetString(data);
                }
            }

            if (!secret.StringData.ContainsKey("forwarding.secret"))
            {
                secret.StringData["forwarding.secret"] = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
            }

            secret = await client.CreateAsync(secret, cancellationToken).ConfigureAwait(false);
        }

        return secret;
    }

    private async Task<V1Deployment> EnsureDeploymentAsync(V1Alpha1Network network, CancellationToken cancellationToken)
    {
        var deploy = await client.GetAsync<V1Deployment>(network.Name(), network.Namespace(), cancellationToken)
            .ConfigureAwait(false);

        if (deploy is null)
        {
            deploy = new V1Deployment { Metadata = new V1ObjectMeta(), Spec = new V1DeploymentSpec() };

            ApplyDeployment(deploy, network);

            deploy = await client.CreateAsync(deploy, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            ApplyDeployment(deploy, network);

            deploy = await client.UpdateAsync(deploy, cancellationToken).ConfigureAwait(false);
        }

        return deploy;
    }

    private async Task<V1Service> EnsureServiceAsync(V1Alpha1Network network, CancellationToken cancellationToken)
    {
        var service = await client.GetAsync<V1Service>(network.Name(), network.Namespace(), cancellationToken)
            .ConfigureAwait(false);

        if (service is null)
        {
            service = new V1Service { Metadata = new V1ObjectMeta(), Spec = new V1ServiceSpec() };

            ApplyService(service, network);

            service = await client.CreateAsync(service, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            ApplyService(service, network);

            service = await client.UpdateAsync(service, cancellationToken).ConfigureAwait(false);
        }

        return service;
    }

    private async Task<V1Alpha1Route?> EnsureRouteAsync(V1Alpha1Network network, CancellationToken cancellationToken)
    {
        var route = await client.GetAsync<V1Alpha1Route>(network.Name(), network.Namespace(), cancellationToken)
            .ConfigureAwait(false);

        switch (network.Spec.Route)
        {
            case null when route is not null:
                await client.DeleteAsync(route, cancellationToken).ConfigureAwait(false);

                return null;
            case null:
                return null;
        }

        if (route is null)
        {
            route = new V1Alpha1Route { Metadata = new V1ObjectMeta(), Spec = new V1Alpha1Route.V1Alpha1RouteSpec() }
                .Initialize();

            ApplyRoute(route, network);

            route = await client.CreateAsync(route, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            ApplyRoute(route, network);

            route = await client.UpdateAsync(route, cancellationToken).ConfigureAwait(false);
        }

        return route;
    }

    private static void ApplyRoute(V1Alpha1Route route, V1Alpha1Network network)
    {
        route.Metadata.Name = network.Name();
        route.Metadata.NamespaceProperty = network.Namespace();

        route.Spec.ParentRef = network.Spec.Route!.ParentRef;
        route.Spec.Hostname = network.Spec.Route!.Hostname;
        route.Spec.BackendRef =
            new V1Alpha1Route.V1Alpha1RouteBackendRef
            {
                Name = network.Name(), Namespace = network.Namespace(), Kind = "Network"
            };

        route.WithOwnerReference(network);
    }

    private async Task<TimeSpan?> ApplyConfigMapAsync(V1ConfigMap cm, V1Alpha1Network network,
        CancellationToken cancellationToken)
    {
        TimeSpan? requeueAfter = null;

        cm.Metadata.Name = network.Name();
        cm.Metadata.NamespaceProperty = network.Namespace();

        cm.Data = new Dictionary<string, string>();

        var config = new TomlTable();

        if (network.Spec.Proxy?.Config is { } userConfig &&
            TomlSerializer.TryDeserialize(userConfig, out TomlTable? parsedUserConfig))
        {
            config = parsedUserConfig;
        }

        var forcedHosts = new Dictionary<string, List<string>>();

        var servers = new TomlTable();

        var serversTry = new TomlArray();

        foreach (var serverDef in network.Spec.Servers ?? [])
        {
            var address = serverDef.TargetRef switch
            {
                { Kind: null or "Server" } => await ResolveServerAddressAsync(serverDef.TargetRef.Name,
                        serverDef.TargetRef.NamespaceProperty ?? network.Namespace(), cancellationToken)
                    .ConfigureAwait(false),
                { Kind: "Network" } => await ResolveNetworkAddressAsync(serverDef.TargetRef.Name,
                        serverDef.TargetRef.NamespaceProperty ?? network.Namespace(), cancellationToken)
                    .ConfigureAwait(false),
                _ => null
            };

            if (address is null)
            {
                // Requeue to eventually get knowledge of the target when it arrives
                requeueAfter = TimeSpan.FromSeconds(5);

                continue;
            }

            servers[serverDef.Name] = address;
            serversTry.Add(serverDef.Name);

            if (serverDef.Hostname is not { } hostname)
            {
                continue;
            }

            forcedHosts.TryAdd(hostname, []);
            forcedHosts[hostname].Add(serverDef.Name);
        }

        servers["try"] = serversTry;

        config["servers"] = servers;
        config["forwarding-secret-file"] = "forwarding.secret";

        var forcedHostsTable = new TomlTable();

        foreach (var forcedHost in forcedHosts)
        {
            var tomlArray = new TomlArray();

            foreach (var name in forcedHost.Value.Distinct())
            {
                tomlArray.Add(name);
            }

            forcedHostsTable[forcedHost.Key] = tomlArray;
        }

        config["forced-hosts"] = forcedHostsTable;

        cm.Data["velocity.toml"] = TomlSerializer.Serialize(config, TomlOptions).ReplaceLineEndings("\n");

        cm.WithOwnerReference(network);

        return requeueAfter;
    }

    private async Task<string?> ResolveServerAddressAsync(string name, string ns, CancellationToken cancellationToken)
    {
        var server = await client.GetAsync<V1Alpha1Server>(name, ns, cancellationToken).ConfigureAwait(false);

        return server is null
            ? null
            : $"{server.Name()}.{server.Namespace()}.svc.cluster.local:{(server.Spec.Service?.Port ?? MinecraftConstants.DefaultMinecraftPort)}";
    }

    private async Task<string?> ResolveNetworkAddressAsync(string name, string ns, CancellationToken cancellationToken)
    {
        var network = await client.GetAsync<V1Alpha1Network>(name, ns, cancellationToken).ConfigureAwait(false);

        return network is null
            ? null
            : $"{network.Name()}.{network.Namespace()}.svc.cluster.local:{(network.Spec.Service?.Port ?? MinecraftConstants.DefaultMinecraftPort)}";
    }

    private static void ApplyDeployment(V1Deployment deploy, V1Alpha1Network network)
    {
        deploy.Metadata.Name = network.Name();
        deploy.Metadata.NamespaceProperty = network.Namespace();

        deploy.Spec.Replicas = network.Spec.Proxy?.Replicas;
        deploy.Spec.Selector = new V1LabelSelector { MatchLabels = PodLabels(network) };
        deploy.Spec.Template = new V1PodTemplateSpec
        {
            Metadata = new V1ObjectMeta { Labels = PodLabels(network) },
            Spec = new V1PodSpec
            {
                Affinity = network.Spec.Proxy?.Pod?.Affinity,
                NodeName = network.Spec.Proxy?.Pod?.NodeName,
                NodeSelector = network.Spec.Proxy?.Pod?.NodeSelector,
                Tolerations = network.Spec.Proxy?.Pod?.Tolerations,
                Volumes =
                [
                    new V1Volume { Name = "config", ConfigMap = new V1ConfigMapVolumeSource { Name = network.Name() } },
                    new V1Volume { Name = "secret", Secret = new V1SecretVolumeSource { SecretName = network.Name() } }
                ],
                Containers =
                [
                    new V1Container
                    {
                        Name = "proxy",
                        Image = network.Spec.Proxy?.Pod?.Image ?? "itzg/mc-proxy:java25",
                        ImagePullPolicy = network.Spec.Proxy?.Pod?.ImagePullPolicy,
                        Ports =
                        [
                            new V1ContainerPort
                            {
                                Name = "minecraft", ContainerPort = MinecraftConstants.DefaultMinecraftPort,
                            }
                        ],
                        Resources = network.Spec.Proxy?.Resources,
                        VolumeMounts =
                        [
                            new V1VolumeMount
                            {
                                Name = "config", MountPath = "/config/velocity.toml", SubPath = "velocity.toml"
                            },
                            new V1VolumeMount
                            {
                                Name = "secret",
                                MountPath = "/config/forwarding.secret",
                                SubPath = "forwarding.secret"
                            }
                        ],
                        Env =
                        [
                            new V1EnvVar { Name = "TYPE", Value = "VELOCITY" },
                            new V1EnvVar { Name = "VELOCITY_VERSION", Value = network.Spec.Proxy?.Version ?? "LATEST" },
                            new V1EnvVar { Name = "ICON", Value = network.Spec.Proxy?.Content?.IconUrl },
                            new V1EnvVar { Name = "MEMORY", Value = "" },
                            new V1EnvVar
                            {
                                Name = "JVM_XX_OPTS",
                                Value =
                                    $"-XX:MaxRAMPercentage={(network.Spec.Proxy?.Jvm?.Memory?.HeapPercentage ?? 75)}"
                            },
                            new V1EnvVar
                            {
                                Name = "PLUGINS",
                                Value = string.Join(",",
                                    network.Spec.Proxy?.Content?.Plugins?.Select(p => p.Url) ?? [])
                            }
                        ]
                    }
                ]
            }
        };

        deploy.WithOwnerReference(network);
    }

    private static void ApplyService(V1Service service, V1Alpha1Network network)
    {
        service.Metadata.Name = network.Name();
        service.Metadata.NamespaceProperty = network.Namespace();
        service.Metadata.Annotations = network.Spec.Service?.Annotations;
        service.Metadata.Labels = PodLabels(network);

        service.Spec.Type = network.Spec.Service?.Type ?? "ClusterIP";
        service.Spec.Selector = PodLabels(network);
        service.Spec.ExternalTrafficPolicy = network.Spec.Service?.ExternalTrafficPolicy;
        service.Spec.IpFamilyPolicy = network.Spec.Service?.IpFamilyPolicy;
        service.Spec.Ports =
        [
            new V1ServicePort
            {
                Name = "minecraft",
                Port = network.Spec.Service?.Port ?? MinecraftConstants.DefaultMinecraftPort,
                TargetPort = MinecraftConstants.DefaultMinecraftPort
            }
        ];

        service.WithOwnerReference(network);
    }

    private static Dictionary<string, string> PodLabels(V1Alpha1Network network) =>
        new() { { "kobblestone.io/network", network.Name() } };
}