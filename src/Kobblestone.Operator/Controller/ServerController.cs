using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using k8s.Models;

using Kobblestone.Operator.Conditions;
using Kobblestone.Operator.Constants;
using Kobblestone.Operator.Entities.V1Alpha1;
using Kobblestone.Operator.Finalizer;
using Kobblestone.Operator.Utils;

using KubeOps.Abstractions.Entities;
using KubeOps.Abstractions.Rbac;
using KubeOps.Abstractions.Reconciliation;
using KubeOps.Abstractions.Reconciliation.Controller;
using KubeOps.Abstractions.Reconciliation.Finalizer;
using KubeOps.KubernetesClient;

using KubernetesCRDModelGen.Models.gateway.networking.k8s.io;

namespace Kobblestone.Operator.Controller;

[EntityRbac(typeof(V1Alpha1Server), Verbs = RbacVerb.Watch | RbacVerb.Get | RbacVerb.Update)]
[EntityRbac(typeof(V1Alpha1Route), Verbs = RbacVerb.Get | RbacVerb.Create | RbacVerb.Update | RbacVerb.Delete)]
[GenericRbac(Groups = [V1TCPRoute.KubeGroup], Resources = [V1TCPRoute.KubePluralName],
    Verbs = RbacVerb.Get | RbacVerb.Create | RbacVerb.Update | RbacVerb.Delete)]
[GenericRbac(Groups = [V1Service.KubeGroup], Resources = [V1Service.KubePluralName],
    Verbs = RbacVerb.Get | RbacVerb.Create | RbacVerb.Update)]
[GenericRbac(Groups = [V1Secret.KubeGroup], Resources = [V1Secret.KubePluralName],
    Verbs = RbacVerb.Get | RbacVerb.Create | RbacVerb.Update)]
[GenericRbac(Groups = [V1NetworkPolicy.KubeGroup], Resources = [V1NetworkPolicy.KubePluralName],
    Verbs = RbacVerb.Get | RbacVerb.Create | RbacVerb.Update | RbacVerb.Delete)]
[GenericRbac(Groups = [V1PersistentVolumeClaim.KubeGroup], Resources = [V1PersistentVolumeClaim.KubePluralName],
    Verbs = RbacVerb.Get | RbacVerb.Create | RbacVerb.Update | RbacVerb.Delete)]
[GenericRbac(Groups = [V1StatefulSet.KubeGroup], Resources = [V1StatefulSet.KubePluralName],
    Verbs = RbacVerb.Get | RbacVerb.Create | RbacVerb.Update)]
public sealed class ServerController(
    IKubernetesClient client,
    EntityFinalizerAttacher<ServerFinalizer, V1Alpha1Server> finalizer) : IEntityController<V1Alpha1Server>
{
    public async Task<ReconciliationResult<V1Alpha1Server>> ReconcileAsync(V1Alpha1Server entity,
        CancellationToken cancellationToken)
    {
        entity = await finalizer(entity, cancellationToken).ConfigureAwait(false);

        if (!entity.Status.Conditions.Any())
        {
            entity.WithConditions(ServerConditions.Default);

            entity = await client.UpdateStatusAsync(entity, cancellationToken).ConfigureAwait(false);

            return ReconciliationResult<V1Alpha1Server>.Success(entity);
        }

        // Wake up server from hibernation
        if (entity.GetAnnotation("kobblestone.io/wake-up") is "true")
        {
            entity.WithCondition(ServerConditions.Hibernating.False());

            entity.Annotations().Remove("kobblestone.io/wake-up");
        }

        var service = await EnsureServiceAsync(entity, cancellationToken).ConfigureAwait(false);

        var tcpRoute = await EnsureTcpRouteAsync(entity, cancellationToken).ConfigureAwait(false);

        await EnsurePvcAsync(entity, cancellationToken).ConfigureAwait(false);

        await EnsureSecretAsync(entity, cancellationToken).ConfigureAwait(false);

        await EnsureRouteAsync(entity, cancellationToken).ConfigureAwait(false);

        await EnsureNetworkPolicyAsync(entity, cancellationToken).ConfigureAwait(false);

        var ss = await EnsureStatefulSetAsync(entity, cancellationToken).ConfigureAwait(false);

        var refreshed = await client.GetAsync<V1Alpha1Server>(entity.Name(), entity.Namespace(), cancellationToken)
            .ConfigureAwait(false);

        if (refreshed is null)
        {
            return ReconciliationResult<V1Alpha1Server>.Failure(entity, "Server was deleted while reconciling");
        }

        var address = $"{service.Name()}.{service.Namespace()}.svc.cluster.local:{service.Spec.Ports[0].Port}";

        var conditions = ServerUtils.GetConditions(ss);

        refreshed.WithConditions(conditions);

        var phase = refreshed.GetPhase();

        if (refreshed.Spec.Rcon?.Disabled is not true)
        {
            refreshed.WithCondition(ServerConditions.RconAvailable);
        }

        refreshed.Status.Phase = phase;
        refreshed.Status.Address = address;
        refreshed.Status.TcpRoute = (tcpRoute is not null ? true : null);
        refreshed.Status.Type = entity.Spec.Type ?? ServerType.Vanilla;

        entity = await client.UpdateStatusAsync(refreshed, cancellationToken).ConfigureAwait(false);

        return ReconciliationResult<V1Alpha1Server>.Success(entity);
    }

    public Task<ReconciliationResult<V1Alpha1Server>> DeletedAsync(V1Alpha1Server entity,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(ReconciliationResult<V1Alpha1Server>.Success(entity));
    }

    private async Task<V1NetworkPolicy?> EnsureNetworkPolicyAsync(V1Alpha1Server server,
        CancellationToken cancellationToken)
    {
        var policy = await client.GetAsync<V1NetworkPolicy>(server.Name(), server.Namespace(), cancellationToken)
            .ConfigureAwait(false);

        if (server.Spec.NetworkPolicy is { Disabled: true } && policy is not null)
        {
            await client.DeleteAsync(policy, cancellationToken).ConfigureAwait(false);

            return null;
        }

        if (policy is null)
        {
            policy = new V1NetworkPolicy { Metadata = new V1ObjectMeta(), Spec = new V1NetworkPolicySpec() };

            await ApplyNetworkPolicyAsync(policy, server, cancellationToken).ConfigureAwait(false);

            policy = await client.CreateAsync(policy, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await ApplyNetworkPolicyAsync(policy, server, cancellationToken).ConfigureAwait(false);

            policy = await client.UpdateAsync(policy, cancellationToken).ConfigureAwait(false);
        }

        return policy;
    }

    private async Task<V1Alpha1Route?> EnsureRouteAsync(V1Alpha1Server server, CancellationToken cancellationToken)
    {
        var route = await client.GetAsync<V1Alpha1Route>(server.Name(), server.Namespace(), cancellationToken)
            .ConfigureAwait(false);

        switch (server.Spec.Route)
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

            ApplyRoute(route, server);

            route = await client.CreateAsync(route, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            ApplyRoute(route, server);

            route = await client.UpdateAsync(route, cancellationToken).ConfigureAwait(false);
        }

        return route;
    }

    private async Task<V1Secret> EnsureSecretAsync(V1Alpha1Server server, CancellationToken cancellationToken)
    {
        var secret = await client.GetAsync<V1Secret>(server.Name(), server.Namespace(), cancellationToken)
            .ConfigureAwait(false);

        if (secret is null)
        {
            secret = new V1Secret
            {
                Metadata = new V1ObjectMeta { Name = server.Name(), NamespaceProperty = server.Namespace() },
                StringData = new Dictionary<string, string>()
            };

            if (server.Spec.Rcon?.PasswordSecretRef is { } rconSecretRef)
            {
                var rconSecret = await client
                    .GetAsync<V1Secret>(rconSecretRef.Name, server.Namespace(), cancellationToken)
                    .ConfigureAwait(false);

                if (rconSecret is not null && rconSecret.Data.TryGetValue(rconSecretRef.Key, out var password))
                {
                    secret.StringData["rcon-password"] = Encoding.UTF8.GetString(password);
                }
            }

            if (!secret.StringData.ContainsKey("rcon-password"))
            {
                secret.StringData["rcon-password"] = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
            }

            secret = await client.CreateAsync(secret, cancellationToken).ConfigureAwait(false);
        }

        return secret;
    }

    private async Task<V1PersistentVolumeClaim?> EnsurePvcAsync(V1Alpha1Server server,
        CancellationToken cancellationToken)
    {
        var pvc = await client.GetAsync<V1PersistentVolumeClaim>(server.Name(), server.Namespace(), cancellationToken)
            .ConfigureAwait(false);

        switch (server.Spec.Storage)
        {
            case null when pvc is not null:
                await client.DeleteAsync(pvc, cancellationToken).ConfigureAwait(false);

                return null;
            case null:
                return null;
        }

        if (pvc is null)
        {
            pvc = new V1PersistentVolumeClaim
            {
                Metadata = new V1ObjectMeta(), Spec = new V1PersistentVolumeClaimSpec()
            };

            ApplyPvc(pvc, server);

            pvc = await client.CreateAsync(pvc, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            ApplyPvc(pvc, server);

            pvc = await client.UpdateAsync(pvc, cancellationToken).ConfigureAwait(false);
        }

        return pvc;
    }

    private async Task<V1Service> EnsureServiceAsync(V1Alpha1Server server, CancellationToken cancellationToken)
    {
        var service = await client.GetAsync<V1Service>(server.Name(), server.Namespace(), cancellationToken)
            .ConfigureAwait(false);

        if (service is null)
        {
            service = new V1Service { Metadata = new V1ObjectMeta(), Spec = new V1ServiceSpec() };

            ApplyService(service, server);

            service = await client.CreateAsync(service, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            ApplyService(service, server);

            service = await client.UpdateAsync(service, cancellationToken).ConfigureAwait(false);
        }

        return service;
    }

    private async Task<V1TCPRoute?> EnsureTcpRouteAsync(V1Alpha1Server server, CancellationToken cancellationToken)
    {
        if (server.Spec.TcpRoute is null && server.Status.TcpRoute is true)
        {
            var existingRoute = await client
                .GetAsync<V1TCPRoute>(server.Name(), server.Namespace(), cancellationToken).ConfigureAwait(false);

            if (existingRoute is null)
            {
                return null;
            }

            await client.DeleteAsync(existingRoute, cancellationToken).ConfigureAwait(false);

            return null;
        }

        if (server.Spec.TcpRoute is null)
        {
            return null;
        }

        var route = await client.GetAsync<V1TCPRoute>(server.Name(), server.Namespace(), cancellationToken)
            .ConfigureAwait(false);

        if (route is null)
        {
            route = new V1TCPRoute { Metadata = new V1ObjectMeta(), Spec = new V1TCPRouteSpec { Rules = [] } };

            ApplyTcpRoute(route, server);

            route = await client.CreateAsync(route, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            ApplyTcpRoute(route, server);

            route = await client.UpdateAsync(route, cancellationToken).ConfigureAwait(false);
        }

        return route;
    }

    private static void ApplyTcpRoute(V1TCPRoute route, V1Alpha1Server server)
    {
        route.Metadata.Name = server.Name();
        route.Metadata.NamespaceProperty = server.Namespace();

        route.Spec.ParentRefs = server.Spec.TcpRoute?.ParentRefs;
        route.Spec.Rules =
        [
            new V1TCPRouteSpecRules
            {
                BackendRefs =
                [
                    new V1TCPRouteSpecRulesBackendRefs
                    {
                        Name = server.Name(),
                        Port = server.Spec.Service?.Port ?? MinecraftConstants.DefaultMinecraftPort
                    }
                ]
            }
        ];

        route.WithOwnerReference(server);
    }

    private async Task<V1StatefulSet> EnsureStatefulSetAsync(V1Alpha1Server server, CancellationToken cancellationToken)
    {
        var ss = await client.GetAsync<V1StatefulSet>(server.Name(), server.Namespace(), cancellationToken)
            .ConfigureAwait(false);

        if (ss is null)
        {
            ss = new V1StatefulSet { Metadata = new V1ObjectMeta(), Spec = new V1StatefulSetSpec() };

            ApplyStatefulSet(ss, server);

            ss = await client.CreateAsync(ss, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            ApplyStatefulSet(ss, server);

            ss = await client.UpdateAsync(ss, cancellationToken).ConfigureAwait(false);
        }

        return ss;
    }

    private Task ApplyNetworkPolicyAsync(V1NetworkPolicy policy, V1Alpha1Server server,
        CancellationToken cancellationToken)
    {
        policy.Metadata.Name = server.Name();
        policy.Metadata.NamespaceProperty = server.Namespace();

        policy.Spec.PodSelector = new V1LabelSelector
        {
            MatchLabels = new Dictionary<string, string> { { "kobblestone.io/server", server.Name() } }
        };
        policy.Spec.PolicyTypes = ["Ingress"];
        policy.Spec.Ingress =
        [
            // Allow Minecraft from any Router, Network and Bot Pod (TODO: further restrict Network Pods in the future)
            new V1NetworkPolicyIngressRule
            {
                FromProperty =
                [
                    new V1NetworkPolicyPeer
                    {
                        NamespaceSelector = new V1LabelSelector(),
                        PodSelector = new V1LabelSelector
                        {
                            MatchExpressions =
                            [
                                new V1LabelSelectorRequirement
                                {
                                    Key = "kobblestone.io/router", OperatorProperty = "Exists"
                                }
                            ]
                        }
                    },
                    new V1NetworkPolicyPeer
                    {
                        NamespaceSelector = new V1LabelSelector(),
                        PodSelector = new V1LabelSelector
                        {
                            MatchExpressions =
                            [
                                new V1LabelSelectorRequirement
                                {
                                    Key = "kobblestone.io/network", OperatorProperty = "Exists"
                                }
                            ]
                        }
                    },
                    new V1NetworkPolicyPeer
                    {
                        NamespaceSelector = new V1LabelSelector(),
                        PodSelector = new V1LabelSelector
                        {
                            MatchExpressions =
                            [
                                new V1LabelSelectorRequirement
                                {
                                    Key = "kobblestone.io/bot", OperatorProperty = "Exists"
                                }
                            ]
                        }
                    }
                ],
                Ports =
                [
                    new V1NetworkPolicyPort
                    {
                        Port = server.Spec.Service?.Port ?? MinecraftConstants.DefaultMinecraftPort,
                        Protocol = "TCP"
                    }
                ]
            },
            // Allow RCON from any Backup Pod
            new V1NetworkPolicyIngressRule
            {
                FromProperty =
                [
                    new V1NetworkPolicyPeer
                    {
                        NamespaceSelector = new V1LabelSelector(),
                        PodSelector = new V1LabelSelector
                        {
                            MatchExpressions =
                            [
                                new V1LabelSelectorRequirement
                                {
                                    Key = "kobblestone.io/backup", OperatorProperty = "Exists"
                                }
                            ]
                        }
                    }
                ],
                Ports =
                [
                    new V1NetworkPolicyPort
                    {
                        Port = server.Spec.Rcon?.Port ?? MinecraftConstants.DefaultRconPort, Protocol = "TCP"
                    }
                ]
            }
        ];

        // Allow Minecraft from Gateway Pods
        if (server.Spec.TcpRoute is { ParentRefs: { } tcpRouteParentRefs })
        {
            foreach (var tcpRouteParentRef in tcpRouteParentRefs)
            {
                policy.Spec.Ingress.Add(new V1NetworkPolicyIngressRule
                {
                    FromProperty =
                    [
                        new V1NetworkPolicyPeer
                        {
                            NamespaceSelector =
                                tcpRouteParentRef.Namespace is { } ns
                                    ? new V1LabelSelector
                                    {
                                        MatchLabels = new Dictionary<string, string>
                                        {
                                            { "kubernetes.io/metadata.name", ns }
                                        }
                                    }
                                    : new V1LabelSelector(),
                            PodSelector = new V1LabelSelector
                            {
                                MatchLabels = new Dictionary<string, string>
                                {
                                    { "gateway.networking.k8s.io/gateway-name", tcpRouteParentRef.Name }
                                }
                            }
                        }
                    ],
                    Ports =
                    [
                        new V1NetworkPolicyPort
                        {
                            Port = server.Spec.Service?.Port ?? MinecraftConstants.DefaultMinecraftPort,
                            Protocol = "TCP"
                        }
                    ]
                });
            }
        }

        if (server.Spec.Service?.Type is "LoadBalancer")
        {
            // Allow RCON and Minecraft from any IP since we have a LoadBalancer (RCON should be disabled most of the time)
            policy.Spec.Ingress.Add(new V1NetworkPolicyIngressRule
            {
                FromProperty =
                [
                    new V1NetworkPolicyPeer { IpBlock = new V1IPBlock { Cidr = "0.0.0.0/0" } },
                    new V1NetworkPolicyPeer { IpBlock = new V1IPBlock { Cidr = "::/0" } }
                ],
                Ports =
                [
                    new V1NetworkPolicyPort
                    {
                        Port = server.Spec.Service?.Port ?? MinecraftConstants.DefaultMinecraftPort,
                        Protocol = "TCP"
                    },
                    new V1NetworkPolicyPort
                    {
                        Port = server.Spec.Rcon?.Port ?? MinecraftConstants.DefaultRconPort, Protocol = "TCP"
                    }
                ]
            });
        }

        foreach (var extraIngressRule in server.Spec.NetworkPolicy?.ExtraIngressRules ?? [])
        {
            policy.Spec.Ingress.Add(extraIngressRule);
        }

        policy.WithOwnerReference(server);

        return Task.CompletedTask;
    }

    private static void ApplyRoute(V1Alpha1Route route, V1Alpha1Server server)
    {
        route.Metadata.Name = server.Name();
        route.Metadata.NamespaceProperty = server.Namespace();

        route.Spec.ParentRef = server.Spec.Route!.ParentRef;
        route.Spec.Hostname = server.Spec.Route!.Hostname;
        route.Spec.BackendRef =
            new V1Alpha1Route.V1Alpha1RouteBackendRef { Name = server.Name(), Namespace = server.Namespace() };

        route.WithOwnerReference(server);
    }

    private static void ApplyStatefulSet(V1StatefulSet ss, V1Alpha1Server server)
    {
        ss.Metadata.Name = server.Name();
        ss.Metadata.NamespaceProperty = server.Namespace();
        ss.SetLabel("kobblestone.io/server", server.Name());

        ss.Spec.Replicas = server.Spec.State is null or ServerState.Running
            ? (server.HasCondition(ServerConditions.Hibernating) ? 0 : 1)
            : 0;

        ss.Spec.ServiceName = server.Name();
        ss.Spec.Selector = new V1LabelSelector { MatchLabels = PodLabels(server) };
        ss.Spec.UpdateStrategy = new V1StatefulSetUpdateStrategy
        {
            Type = server.Spec.Lifecycle?.RolloutPolicy is RolloutPolicy.Manual ? "OnDelete" : "RollingUpdate"
        };
        ss.Spec.Template = new V1PodTemplateSpec
        {
            Metadata = new V1ObjectMeta { Labels = PodLabels(server) },
            Spec = new V1PodSpec
            {
                Affinity = server.Spec.Pod?.Affinity,
                NodeName = server.Spec.Pod?.NodeName,
                NodeSelector = server.Spec.Pod?.NodeSelector,
                Tolerations = server.Spec.Pod?.Tolerations,
                Resources = server.Spec.Resources,
                Volumes = new List<V1Volume>
                {
                    new()
                    {
                        Name = "secret",
                        Secret = new V1SecretVolumeSource { SecretName = server.Name(), Optional = false }
                    }
                }.Concat(server.Spec.Storage is null
                    ? []
                    :
                    [
                        new V1Volume
                        {
                            Name = "data",
                            PersistentVolumeClaim = new V1PersistentVolumeClaimVolumeSource
                            {
                                ClaimName = server.Name()
                            }
                        }
                    ]).Concat(server.Spec.Pod?.Volumes ?? []).ToList(),
                InitContainers =
                [
                    new V1Container
                    {
                        Name = "proxy",
                        Image = "itzg/mc-router:1.46.2",
                        RestartPolicy = "Always",
                        Ports =
                        [
                            new V1ContainerPort
                            {
                                Name = "minecraft", ContainerPort = MinecraftConstants.DefaultMinecraftPort
                            },
                            new V1ContainerPort { Name = "metrics", ContainerPort = 8080 }
                        ],
                        Env = new List<V1EnvVar>()
                            .Concat([
                                new V1EnvVar { Name = "API_BINDING", Value = "0.0.0.0:8080" },
                                new V1EnvVar { Name = "DEFAULT", Value = "localhost:25566" },
                                new V1EnvVar { Name = "LOG_LEVEL", Value = "error" },
                                new V1EnvVar { Name = "METRICS_BACKEND", Value = "prometheus" },
                                new V1EnvVar { Name = "RECORD_LOGINS", Value = "true" },
                                new V1EnvVar { Name = "WEBHOOK_REQUIRE_USER", Value = "true" },
                                new V1EnvVar { Name = "AUTO_SCALE_DOWN", Value = "true" },
                                new V1EnvVar
                                {
                                    Name = "AUTO_SCALE_DOWN_AFTER",
                                    Value = $"{(server.Spec.AutoSleep?.TimeoutSeconds ?? 60)}s"
                                },
                                new V1EnvVar { Name = "AUTO_SCALE_WEBHOOK_URL", Value = "" },
                            ])
                            .ToList(),
                    }
                ],
                Containers =
                [
                    new V1Container
                    {
                        Name = "server",
                        Image =
                            server.Spec.Pod?.Image ??
                            $"itzg/minecraft-server:2026.8.0-{(server.Spec.Jvm?.Version ?? "java25")}",
                        ImagePullPolicy = server.Spec.Pod?.ImagePullPolicy,
                        VolumeMounts = new List<V1VolumeMount>
                            {
                                new() { Name = "secret", MountPath = "/secrets", ReadOnlyProperty = true }
                            }
                            .Concat(server.Spec.Storage is null
                                ? []
                                :
                                [
                                    new V1VolumeMount { Name = "data", MountPath = "/data" }
                                ]).Concat(server.Spec.Pod?.VolumeMounts ?? []).ToList(),
                        Env = new List<V1EnvVar>()
                            .Concat(server.Spec.Content?.CurseForge?.ApiKeySecretRef is { } cfApiKeySecretRef
                                ?
                                [
                                    new V1EnvVar
                                    {
                                        Name = "CF_API_KEY",
                                        ValueFrom = new V1EnvVarSource
                                        {
                                            SecretKeyRef = new V1SecretKeySelector
                                            {
                                                Name = cfApiKeySecretRef.Name,
                                                Key = cfApiKeySecretRef.Key,
                                                Optional = true
                                            }
                                        }
                                    }
                                ]
                                : [])
                            .Concat(GetEnvVars(server))
                            .Concat(server.Spec.Pod?.Env ?? []).ToList(),
                        StartupProbe =
                            new V1Probe
                            {
                                Exec = new V1ExecAction
                                {
                                    Command = ["/usr/local/bin/mc-monitor", "status", "--host", "localhost"]
                                },
                                PeriodSeconds = 5,
                                FailureThreshold =
                                    Math.Max((server.Spec.Timeouts?.MaxStartupTimeSeconds ?? 360) / 5, 1)
                            },
                        ReadinessProbe =
                            new V1Probe
                            {
                                Exec = new V1ExecAction
                                {
                                    Command = ["/usr/local/bin/mc-monitor", "status", "--host", "localhost"]
                                },
                                PeriodSeconds = 5,
                                FailureThreshold = 3
                            },
                        LivenessProbe
                            = new V1Probe
                            {
                                Exec = new V1ExecAction
                                {
                                    Command = ["/usr/local/bin/mc-monitor", "status", "--host", "localhost"]
                                },
                                PeriodSeconds = 5,
                                FailureThreshold =
                                    Math.Max((server.Spec.Timeouts?.MaxUnavailabilityTimeSeconds ?? 15) / 5, 1)
                            }
                    }
                ]
            }
        };

        ss.WithOwnerReference(server);
    }

    private static void ApplyPvc(V1PersistentVolumeClaim pvc, V1Alpha1Server server)
    {
        pvc.Metadata.Name = server.Name();
        pvc.Metadata.NamespaceProperty = server.Namespace();

        pvc.Spec.AccessModes = ["ReadWriteOnce"];

        pvc.Spec.StorageClassName ??= server.Spec.Storage?.StorageClassName;

        pvc.Spec.Resources = new V1VolumeResourceRequirements
        {
            Requests = new Dictionary<string, ResourceQuantity> { { "storage", server.Spec.Storage!.Size } }
        };

        pvc.WithOwnerReference(server);
    }

    private static List<V1EnvVar> GetEnvVars(V1Alpha1Server server)
    {
        var envVars = new Dictionary<string, string?>();

        if (server.Spec.Eula)
        {
            envVars["EULA"] = "true";
        }
        else
        {
            envVars["EULA"] = "false";
        }

        if (server.Spec.Offline is true)
        {
            envVars["ONLINE_MODE"] = "false";
        }
        else
        {
            envVars["ONLINE_MODE"] = "true";
        }

        envVars["SERVER_PORT"] = "25566";
        envVars["REPLACE_ENV_DURING_SYNC"] = "true";
        envVars["REPLACE_ENV_VARIABLE_PREFIX"] = "";
        envVars["MEMORY"] = $"{(server.Spec.Jvm?.Memory?.HeapPercentage ?? 75)}%";
        envVars["TYPE"] =
            JsonNamingPolicy.SnakeCaseUpper.ConvertName((server.Spec.Type ?? ServerType.Vanilla).ToString());
        envVars["VERSION"] = server.Spec.Version ?? "LATEST";
        envVars["RCON_CMDS_STARTUP"] = string.Join("\n", server.Spec.AutoCommands?.OnStartup ?? []);
        envVars["RCON_CMDS_ON_CONNECT"] = string.Join("\n", server.Spec.AutoCommands?.OnConnect ?? []);
        envVars["RCON_CMDS_ON_DISCONNECT"] = string.Join("\n", server.Spec.AutoCommands?.OnDisconnect ?? []);
        envVars["RCON_CMDS_FIRST_CONNECT"] = string.Join("\n", server.Spec.AutoCommands?.OnFirstConnect ?? []);
        envVars["RCON_CMDS_LAST_DISCONNECT"] = string.Join("\n", server.Spec.AutoCommands?.OnLastDisconnect ?? []);
        envVars["ICON"] = server.Spec.Content?.IconUrl;
        envVars["PACKWIZ_URL"] = server.Spec.Content?.PackwizUrl;
        envVars["PLUGINS"] = string.Join(",", server.Spec.Content?.Plugins?.Select(p => p.Url) ?? []);
        envVars["MODS"] = string.Join(",", server.Spec.Content?.Mods?.Select(p => p.Url) ?? []);
        envVars["MODPACK"] = server.Spec.Content?.ModPackUrl;
        envVars["MODRINTH_PROJECTS"] = string.Join(",", server.Spec.Content?.Modrinth?.Projects ?? []);
        envVars["CURSEFORGE_FILES"] = string.Join(",", server.Spec.Content?.CurseForge?.Files ?? []);
        envVars["CF_PAGE_URL"] = server.Spec.Content?.CurseForge?.ModPackUrl;
        envVars["SPIGET_RESOURCES"] = string.Join(",", server.Spec.Content?.SpigotResources ?? []);
        envVars["RESOURCE_PACK"] = server.Spec.Content?.ResourcePack?.Url;
        envVars["RESOURCE_PACK_SHA1"] = server.Spec.Content?.ResourcePack?.Sha1;

        if (server.Spec.Properties is { Count: > 0 })
        {
            envVars["CUSTOM_SERVER_PROPERTIES"] =
                string.Join("\n", server.Spec.Properties.Select(p => $"{p.Key}={p.Value}") ?? []);
        }

        if (server.Spec.AutoSleep is { Mode: null or AutoSleepMode.Pause })
        {
            envVars["PAUSE_WHEN_EMPTY_SECONDS"] = server.Spec.AutoSleep.TimeoutSeconds.ToString();
        }
        else if (server.Spec.AutoSleep is { Mode: AutoSleepMode.LegacyPause })
        {
            envVars["ENABLE_AUTOPAUSE"] = "true";
            envVars["AUTOPAUSE_TIMEOUT_EST"] = server.Spec.AutoSleep.TimeoutSeconds.ToString();
            envVars["AUTOPAUSE_TIMEOUT_INIT"] = server.Spec.AutoSleep.TimeoutSeconds.ToString();
        }
        else
        {
            envVars["PAUSE_WHEN_EMPTY_SECONDS"] = "-1";
            envVars["ENABLE_AUTOPAUSE"] = "false";
        }

        if (server.Spec.Content?.ResourcePack?.Enforce is true)
        {
            envVars["RESOURCE_PACK_ENFORCE"] = "true";
        }
        else
        {
            envVars["RESOURCE_PACK_ENFORCE"] = "false";
        }

        envVars["EXISTING_WHITELIST_FILE"] = "SYNCHRONIZE";
        envVars["EXISTING_OPS_FILE"] = "SYNCHRONIZE";
        envVars["ENABLE_WHITELIST"] = "false";

        if (server.Spec.Access is { } access)
        {
            if (access.Whitelist is { } whitelist)
            {
                if (whitelist.Enforce is true)
                {
                    envVars["ENFORCE_WHITELIST"] = "true";
                }

                envVars["WHITELIST"] = string.Join(",", whitelist.Users ?? []);
            }

            if (access.Ops is { } ops)
            {
                envVars["OPS"] = string.Join(",", ops.Users ?? []);
            }
        }

        if (server.Spec.Rcon?.Disabled is true)
        {
            envVars["ENABLE_RCON"] = "false";
        }
        else
        {
            envVars["ENABLE_RCON"] = "true";
        }

        envVars["RCON_PASSWORD_FILE"] = "/secrets/rcon-password";

        return envVars.Select(e => new V1EnvVar { Name = e.Key, Value = e.Value }).ToList();
    }

    private static void ApplyService(V1Service service, V1Alpha1Server server)
    {
        service.Metadata.Name = server.Name();
        service.Metadata.NamespaceProperty = server.Namespace();
        service.Metadata.Annotations = server.Spec.Service?.Annotations;

        service.Spec.Type = server.Spec.Service?.Type ?? "ClusterIP";
        service.Spec.Selector = PodLabels(server);
        service.Spec.ExternalTrafficPolicy = server.Spec.Service?.ExternalTrafficPolicy;
        service.Spec.IpFamilyPolicy = server.Spec.Service?.IpFamilyPolicy;
        service.Spec.Ports =
        [
            new V1ServicePort
            {
                Name = "minecraft",
                Port = server.Spec.Service?.Port ?? MinecraftConstants.DefaultMinecraftPort,
                TargetPort = MinecraftConstants.DefaultMinecraftPort
            }
        ];

        if (server.Spec.Rcon?.Disabled is not true)
        {
            service.Spec.Ports.Add(new V1ServicePort
            {
                Name = "rcon",
                Port = server.Spec.Rcon?.Port ?? MinecraftConstants.DefaultRconPort,
                TargetPort = MinecraftConstants.DefaultRconPort
            });
        }

        service.WithOwnerReference(server);
    }

    private static Dictionary<string, string> PodLabels(V1Alpha1Server server) =>
        new() { { "kobblestone.io/server", server.Name() } };
}