using System.Text.Json;

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

[EntityRbac(typeof(V1Alpha1Bot), Verbs = RbacVerb.Watch | RbacVerb.Get | RbacVerb.Update)]
[GenericRbac(Groups = [V1Service.KubeGroup], Resources = [V1Service.KubePluralName],
    Verbs = RbacVerb.Get | RbacVerb.Create | RbacVerb.Update)]
[GenericRbac(Groups = [V1Deployment.KubeGroup], Resources = [V1Deployment.KubePluralName],
    Verbs = RbacVerb.Get | RbacVerb.Create | RbacVerb.Update)]
[GenericRbac(Groups = [V1HTTPRoute.KubeGroup], Resources = [V1HTTPRoute.KubePluralName],
    Verbs = RbacVerb.Get | RbacVerb.Create | RbacVerb.Update | RbacVerb.Delete)]
public sealed class BotController(IKubernetesClient client) : IEntityController<V1Alpha1Bot>
{
    private static readonly JsonSerializerOptions JsonSerializerOptions = new() { WriteIndented = true };

    public async Task<ReconciliationResult<V1Alpha1Bot>> ReconcileAsync(V1Alpha1Bot entity,
        CancellationToken cancellationToken)
    {
        var result = await EnsureConfigMapAsync(entity, cancellationToken).ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            return result;
        }

        await EnsureServiceAsync(entity, cancellationToken).ConfigureAwait(false);

        await EnsureHttpRouteAsync(entity, cancellationToken).ConfigureAwait(false);

        await EnsureDeploymentAsync(entity, cancellationToken).ConfigureAwait(false);

        entity = await client.UpdateStatusAsync(entity, cancellationToken).ConfigureAwait(false);

        return ReconciliationResult<V1Alpha1Bot>.Success(entity);
    }

    private async Task<V1HTTPRoute?> EnsureHttpRouteAsync(V1Alpha1Bot bot, CancellationToken cancellationToken)
    {
        var routeName = $"{bot.Name()}-viewer";

        if (bot.Spec.Viewer?.HttpRoute is null && bot.Status.HttpRoute is true)
        {
            var existingRoute = await client
                .GetAsync<V1HTTPRoute>(routeName, bot.Namespace(), cancellationToken).ConfigureAwait(false);

            if (existingRoute is null)
            {
                return null;
            }

            await client.DeleteAsync(existingRoute, cancellationToken).ConfigureAwait(false);

            bot.Status.HttpRoute = null;

            return null;
        }

        if (bot.Spec.Viewer?.HttpRoute is null)
        {
            return null;
        }

        var route = await client.GetAsync<V1HTTPRoute>(routeName, bot.Namespace(), cancellationToken)
            .ConfigureAwait(false);

        if (route is null)
        {
            route = new V1HTTPRoute { Metadata = new V1ObjectMeta(), Spec = new V1HTTPRouteSpec { Rules = [] } };

            ApplyHttpRoute(route, bot);

            route = await client.CreateAsync(route, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            ApplyHttpRoute(route, bot);

            route = await client.UpdateAsync(route, cancellationToken).ConfigureAwait(false);
        }

        bot.Status.HttpRoute = true;

        return route;
    }

    private static void ApplyHttpRoute(V1HTTPRoute route, V1Alpha1Bot bot)
    {
        route.Metadata.Name = $"{bot.Name()}-viewer";
        route.Metadata.NamespaceProperty = bot.Namespace();

        route.Spec.ParentRefs = bot.Spec.Viewer?.HttpRoute?.ParentRefs;
        route.Spec.Hostnames = bot.Spec.Viewer?.HttpRoute?.Hostnames;
        route.Spec.Rules =
        [
            new V1HTTPRouteSpecRules
            {
                Matches = bot.Spec.Viewer?.HttpRoute?.Matches ??
                [
                    new V1HTTPRouteSpecRulesMatches
                    {
                        Path = new V1HTTPRouteSpecRulesMatchesPath
                        {
                            Value = "/", Type = V1HTTPRouteSpecRulesMatchesPathTypeEnum.PathPrefix
                        }
                    }
                ],
                BackendRefs =
                [
                    new V1HTTPRouteSpecRulesBackendRefs { Name = bot.Name(), Port = bot.Spec.Service?.Port ?? 3000 }
                ]
            }
        ];

        route.WithOwnerReference(bot);
    }

    private async Task<ReconciliationResult<V1Alpha1Bot>> EnsureConfigMapAsync(V1Alpha1Bot bot,
        CancellationToken cancellationToken)
    {
        (string? address, int? port) = bot.Spec.Target.TargetRef switch
        {
            { Kind: null or "Server" } => await ResolveServerAddressAsync(bot.Spec.Target.TargetRef.Name,
                    bot.Spec.Target.TargetRef.NamespaceProperty ?? bot.Namespace(), cancellationToken)
                .ConfigureAwait(false),
            { Kind: "Network" } => await ResolveNetworkAddressAsync(bot.Spec.Target.TargetRef.Name,
                    bot.Spec.Target.TargetRef.NamespaceProperty ?? bot.Namespace(), cancellationToken)
                .ConfigureAwait(false),
            _ => (null, null)
        };

        if (bot.Spec.Target.Host is { } host)
        {
            address = host;
        }

        if (address is null)
        {
            return ReconciliationResult<V1Alpha1Bot>.Failure(bot, "Could not resolve target address",
                requeueAfter: TimeSpan.FromSeconds(5));
        }

        var cm = await client.GetAsync<V1ConfigMap>(bot.Name(), bot.Namespace(), cancellationToken)
            .ConfigureAwait(false);

        if (cm is null)
        {
            cm = new V1ConfigMap { Metadata = new V1ObjectMeta() };

            ApplyConfigMap(cm, bot, address, bot.Spec.Target.Port ?? port ?? MinecraftConstants.DefaultMinecraftPort);

            cm = await client.CreateAsync(cm, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            ApplyConfigMap(cm, bot, address, bot.Spec.Target.Port ?? port ?? MinecraftConstants.DefaultMinecraftPort);

            cm = await client.UpdateAsync(cm, cancellationToken).ConfigureAwait(false);
        }

        return ReconciliationResult<V1Alpha1Bot>.Success(bot);
    }

    private async Task<(string?, int?)> ResolveNetworkAddressAsync(string name, string ns,
        CancellationToken cancellationToken)
    {
        var network = await client.GetAsync<V1Alpha1Network>(name, ns, cancellationToken).ConfigureAwait(false);

        return network is null
            ? (null, null)
            : ($"{network.Name()}.{network.Namespace()}.svc.cluster.local",
                network.Spec.Service?.Port ?? MinecraftConstants.DefaultMinecraftPort);
    }

    private async Task<(string?, int?)> ResolveServerAddressAsync(string name, string ns,
        CancellationToken cancellationToken)
    {
        var server = await client.GetAsync<V1Alpha1Server>(name, ns, cancellationToken).ConfigureAwait(false);

        return server is null
            ? (null, null)
            : ($"{server.Name()}.{server.Namespace()}.svc.cluster.local",
                server.Spec.Service?.Port ?? MinecraftConstants.DefaultMinecraftPort);
    }

    private static void ApplyConfigMap(V1ConfigMap cm, V1Alpha1Bot bot, string address, int? port)
    {
        cm.Metadata.Name = bot.Name();
        cm.Metadata.NamespaceProperty = bot.Namespace();

        cm.Data = new Dictionary<string, string>();

        var config = new
        {
            host = address,
            port,
            username = bot.Spec.OfflineUsername,
            viewer = bot.Spec.Viewer is { } viewer
                ? new
                {
                    port = 3000,
                    firstPerson = viewer.Mode is V1Alpha1Bot.BotViewerMode.FirstPerson or null,
                    viewDistance = viewer.ViewDistance ?? 4
                }
                : null,
            behaviors = bot.Spec.Behaviors?.Select(b => new { name = b.BehaviorRef.Name, @params = b.Parameters }) ?? []
        };

        var configJson = JsonSerializer.Serialize(config, JsonSerializerOptions);

        cm.Data["config.json"] = configJson.ReplaceLineEndings("\n");

        cm.WithOwnerReference(bot);
    }

    private async Task<V1Service> EnsureServiceAsync(V1Alpha1Bot bot, CancellationToken cancellationToken)
    {
        var service = await client.GetAsync<V1Service>(bot.Name(), bot.Namespace(), cancellationToken)
            .ConfigureAwait(false);

        if (service is null)
        {
            service = new V1Service { Metadata = new V1ObjectMeta(), Spec = new V1ServiceSpec() };

            ApplyService(service, bot);

            service = await client.CreateAsync(service, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            ApplyService(service, bot);

            service = await client.UpdateAsync(service, cancellationToken).ConfigureAwait(false);
        }

        return service;
    }

    private static void ApplyService(V1Service service, V1Alpha1Bot bot)
    {
        service.Metadata.Name = bot.Name();
        service.Metadata.NamespaceProperty = bot.Namespace();
        service.Metadata.Annotations = bot.Spec.Service?.Annotations;
        service.Metadata.Labels = PodLabels(bot);

        service.Spec.Type = bot.Spec.Service?.Type ?? "ClusterIP";
        service.Spec.Selector = PodLabels(bot);
        service.Spec.ExternalTrafficPolicy = bot.Spec.Service?.ExternalTrafficPolicy;
        service.Spec.IpFamilyPolicy = bot.Spec.Service?.IpFamilyPolicy;
        service.Spec.Ports =
        [
            new V1ServicePort { Name = "status", Port = bot.Spec.Service?.Port ?? 8080, TargetPort = 8080 },
            new V1ServicePort { Name = "viewer", Port = bot.Spec.Viewer?.Port ?? 3000, TargetPort = 3000 }
        ];

        service.WithOwnerReference(bot);
    }

    private async Task<V1Deployment> EnsureDeploymentAsync(V1Alpha1Bot bot, CancellationToken cancellationToken)
    {
        var deploy = await client.GetAsync<V1Deployment>(bot.Name(), bot.Namespace(), cancellationToken)
            .ConfigureAwait(false);

        if (deploy is null)
        {
            deploy = new V1Deployment { Metadata = new V1ObjectMeta(), Spec = new V1DeploymentSpec() };

            ApplyDeployment(deploy, bot);

            deploy = await client.CreateAsync(deploy, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            ApplyDeployment(deploy, bot);

            deploy = await client.UpdateAsync(deploy, cancellationToken).ConfigureAwait(false);
        }

        return deploy;
    }


    private static void ApplyDeployment(V1Deployment deploy, V1Alpha1Bot bot)
    {
        deploy.Metadata.Name = bot.Name();
        deploy.Metadata.NamespaceProperty = bot.Namespace();

        deploy.Spec.Replicas = bot.Spec.Inactive is true ? 0 : 1;
        deploy.Spec.Selector = new V1LabelSelector { MatchLabels = PodLabels(bot) };
        deploy.Spec.Template = new V1PodTemplateSpec
        {
            Metadata = new V1ObjectMeta { Labels = PodLabels(bot) },
            Spec = new V1PodSpec
            {
                NodeName = bot.Spec.Pod?.NodeName,
                Affinity = bot.Spec.Pod?.Affinity,
                NodeSelector = bot.Spec.Pod?.NodeSelector,
                Tolerations = bot.Spec.Pod?.Tolerations,
                ImagePullSecrets =
                [
                    new V1LocalObjectReference { Name = "dockerconfig" }
                ],
                Volumes = bot.Spec.Behaviors?.Select(b => new V1Volume
                    {
                        Name = $"behavior-{b.BehaviorRef.Name}",
                        ConfigMap = new V1ConfigMapVolumeSource { Name = b.BehaviorRef.Name }
                    }).Concat([
                        new V1Volume { Name = "config", ConfigMap = new V1ConfigMapVolumeSource { Name = bot.Name() } }
                    ]).Concat(bot.Spec.Auth?.AccountRef is not null
                        ?
                        [
                            new V1Volume
                            {
                                Name = "account",
                                Secret = new V1SecretVolumeSource { SecretName = bot.Spec.Auth.AccountRef.Name }
                            }
                        ]
                        : [])
                    .ToList(),
                Containers =
                [
                    new V1Container
                    {
                        Name = "bot",
                        Image = bot.Spec.Pod?.Image ?? "ghcr.io/kobblestoneio/bot-runner:0.1.0",
                        ImagePullPolicy = bot.Spec.Pod?.ImagePullPolicy,
                        Resources = bot.Spec.Resources,
                        VolumeMounts = bot.Spec.Behaviors?.Select(b => new V1VolumeMount
                            {
                                Name = $"behavior-{b.BehaviorRef.Name}",
                                MountPath = $"/app/behaviors/{b.BehaviorRef.Name}",
                            }).Concat([
                                new V1VolumeMount
                                {
                                    Name = "config", MountPath = "/etc/bot/config.json", SubPath = "config.json"
                                }
                            ]).Concat(bot.Spec.Auth?.AccountRef is not null
                                ?
                                [
                                    new V1VolumeMount
                                    {
                                        Name = "account",
                                        MountPath = "/etc/bot/session/session.json",
                                        SubPath = "session.json"
                                    },
                                    new V1VolumeMount
                                    {
                                        Name = "account",
                                        MountPath = "/etc/bot/profile/profile.json",
                                        SubPath = "profile.json"
                                    },
                                    new V1VolumeMount
                                    {
                                        Name = "account",
                                        MountPath = "/etc/bot/entitlements/entitlements.json",
                                        SubPath = "entitlements.json"
                                    }
                                ]
                                : [])
                            .Concat(bot.Spec.Auth?.EnableChatSigning is true
                                ?
                                [
                                    new V1VolumeMount
                                    {
                                        Name = "account",
                                        MountPath = "/etc/bot/certs/certificates.json",
                                        SubPath = "certificates.json"
                                    }
                                ]
                                : [])
                            .ToList(),
                        Ports =
                        [
                            new V1ContainerPort { Name = "status", ContainerPort = 8080 },
                            new V1ContainerPort { Name = "viewer", ContainerPort = 3000 }
                        ]
                    }
                ]
            }
        };

        deploy.WithOwnerReference(bot);
    }

    public Task<ReconciliationResult<V1Alpha1Bot>> DeletedAsync(V1Alpha1Bot entity, CancellationToken cancellationToken)
    {
        return Task.FromResult(ReconciliationResult<V1Alpha1Bot>.Success(entity));
    }

    private static Dictionary<string, string> PodLabels(V1Alpha1Bot bot) =>
        new() { { "kobblestone.io/bot", bot.Name() } };
}