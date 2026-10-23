using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using k8s;
using k8s.Autorest;
using k8s.Models;

using Kobblestone.Operator.Constants;
using Kobblestone.Operator.Entities;
using Kobblestone.Operator.Entities.V1Alpha1;
using Kobblestone.Operator.Finalizer;
using Kobblestone.Operator.Utils;

using KubeOps.Abstractions.Entities;
using KubeOps.Abstractions.Events;
using KubeOps.Abstractions.Rbac;
using KubeOps.Abstractions.Reconciliation;
using KubeOps.Abstractions.Reconciliation.Controller;
using KubeOps.Abstractions.Reconciliation.Finalizer;
using KubeOps.KubernetesClient;

using KubernetesCRDModelGen.Models.gateway.networking.k8s.io;

using Tomlyn;
using Tomlyn.Model;

namespace Kobblestone.Operator.Controller;

/// <summary>
/// Runs the Velocity proxy of a `Network` and manages its servers.
/// </summary>
/// <remarks>
/// Managed servers are ordinary `Server`s labelled with the network's name, rendered from `serverTemplate` and their
/// entry in `servers` (JSON merge patch). The network enforces what it depends on: forwarding with its secret (which
/// implies offline mode) and no direct exposure (`route`, `tcpRoute`, non-`ClusterIP` `Service`). They have no owner
/// reference, so deleting the network never deletes their data by accident; the NetworkFinalizer applies
/// `serverDeletionPolicy` instead. The ServerController queues the network whenever one of its servers changes.
/// </remarks>
[EntityRbac(typeof(V1Alpha1Network), Verbs = RbacVerb.Watch | RbacVerb.Get | RbacVerb.Update)]
[EntityRbac(typeof(V1Alpha1Server),
    Verbs = RbacVerb.Get | RbacVerb.List | RbacVerb.Create | RbacVerb.Update | RbacVerb.Delete)]
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
public sealed class NetworkController(
    IKubernetesClient client,
    EventPublisher emitEvent,
    EntityFinalizerAttacher<NetworkFinalizer, V1Alpha1Network> finalizer) : IEntityController<V1Alpha1Network>
{
    /// <summary>Key of the forwarding secret in the network's own `Secret`.</summary>
    public const string ForwardingSecretKey = "forwarding.secret";

    private static readonly TomlSerializerOptions TomlOptions = new() { WriteIndented = true };

    /// <summary>
    /// Format version of the generated <c>velocity.toml</c>, unless `proxy.config` sets one.
    /// </summary>
    private const string VelocityConfigVersion = "2.7";

    /// <summary>
    /// A server entry as the proxy sees it.
    /// </summary>
    /// <param name="Retry">The server could not be updated yet (conflict), the network should be reconciled again
    /// soon.</param>
    private sealed record Member(V1Alpha1Network.V1Alpha1NetworkServer Entry, string? Address,
        V1Alpha1Network.V1Alpha1NetworkServerStatus Status, bool Retry = false);

    public async Task<ReconciliationResult<V1Alpha1Network>> ReconcileAsync(V1Alpha1Network entity,
        CancellationToken cancellationToken)
    {
        entity = await finalizer(entity, cancellationToken).ConfigureAwait(false);

        var before = EntityQueueExtensions.Snapshot(entity.Status);

        var service = await EnsureServiceAsync(entity, cancellationToken).ConfigureAwait(false);

        await EnsureTcpRouteAsync(entity, cancellationToken).ConfigureAwait(false);

        await EnsureSecretAsync(entity, cancellationToken).ConfigureAwait(false);

        await EnsureRouteAsync(entity, cancellationToken).ConfigureAwait(false);

        var members = await ReconcileServersAsync(entity, cancellationToken).ConfigureAwait(false);

        var cm = await EnsureConfigMapAsync(entity, members, cancellationToken).ConfigureAwait(false);

        var deploy = await EnsureDeploymentAsync(entity, ConfigHash(cm), cancellationToken).ConfigureAwait(false);

        // The NetworkServiceController queues this network whenever the Service changes, e.g. when its load balancer
        // got an external IP.
        entity.Status.Address = service.GetIpAddress();
        entity.Status.Servers = members.Select(m => m.Status).ToList();
        // The NetworkDeploymentController queues this network whenever the proxy's readiness changes.
        entity.Status.ProxyReplicas = deploy.Spec.Replicas ?? 1;
        entity.Status.ProxyReadyReplicas = deploy.Status?.ReadyReplicas ?? 0;
        entity.Status.ProxyReady = $"{entity.Status.ProxyReadyReplicas}/{entity.Status.ProxyReplicas}";
        entity.Status.ServerCount = members.Count;
        entity.Status.ReadyServers = members.Count(m => m.Status.Phase == nameof(ServerPhase.Running));
        entity.Status.ServersReady = $"{entity.Status.ReadyServers}/{entity.Status.ServerCount}";
        entity.Status.Message = members.Select(m => m.Status.Message).FirstOrDefault(m => m is not null);

        if (EntityQueueExtensions.Snapshot(entity.Status) != before)
        {
            entity = await client.UpdateStatusAsync(entity, cancellationToken).ConfigureAwait(false);
        }

        // Unmanaged targets are not watched: refresh the status of targets that do not exist yet from time to time.
        return ReconciliationResult<V1Alpha1Network>.Success(entity,
            members.Any(m => m.Retry) ? TimeSpan.FromSeconds(2)
            : members.Any(m => m.Status is { Managed: false, Phase: "Missing" }) ? TimeSpan.FromSeconds(30)
            : null);
    }

    /// <summary>
    /// Creates and updates the managed servers, removes those that left the network, and resolves the address of
    /// every entry.
    /// </summary>
    private async Task<List<Member>> ReconcileServersAsync(V1Alpha1Network network,
        CancellationToken cancellationToken)
    {
        var entries = network.Spec.Servers ?? [];
        var errors = Validate(network);
        var members = new List<Member>();
        var managedNames = new HashSet<string>();

        foreach (var entry in entries)
        {
            var member = entry.Unmanaged is true
                ? await ResolveUnmanagedAsync(network, entry, errors.GetValueOrDefault(entry), cancellationToken)
                    .ConfigureAwait(false)
                : await ReconcileManagedAsync(network, entry, errors.GetValueOrDefault(entry), cancellationToken)
                    .ConfigureAwait(false);

            if (member.Status.Message is { } message)
            {
                await emitEvent(network, "InvalidServer", $"Server {entry.Name}: {message}", EventType.Warning,
                    cancellationToken).ConfigureAwait(false);
            }

            if (entry.Unmanaged is not true)
            {
                managedNames.Add(ServerName(network, entry));
            }

            members.Add(member);
        }

        foreach (var server in await NetworkMembers.ListAsync(client, network, cancellationToken)
                     .ConfigureAwait(false))
        {
            if (!managedNames.Contains(server.Name()))
            {
                await NetworkMembers.RemoveAsync(client, emitEvent, network, server, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        return members;
    }

    private async Task<Member> ReconcileManagedAsync(V1Alpha1Network network,
        V1Alpha1Network.V1Alpha1NetworkServer entry, string? validationError, CancellationToken cancellationToken)
    {
        var serverName = ServerName(network, entry);

        var existing = await client.GetAsync<V1Alpha1Server>(serverName, network.Namespace(), cancellationToken)
            .ConfigureAwait(false);

        var status = new V1Alpha1Network.V1Alpha1NetworkServerStatus
        {
            Name = entry.Name,
            Server = serverName,
            Managed = true,
            Phase = existing is null ? "Missing" : existing.Status.Phase.ToString()
        };

        var owner = existing?.GetLabel(V1Alpha1Network.NetworkLabel);

        // Derived from the spec only, whether the server exists or the entry is valid: the proxy's config (and with it
        // the proxy, which is restarted on changes) only changes when `servers` is changed. Invalid entries leave an
        // existing server as it is.
        var address = ManagedAddress(network, serverName);

        if (validationError is not null)
        {
            return new Member(entry, address, status with { Message = validationError });
        }

        if (existing is not null && owner is not null && owner != network.Name())
        {
            return new Member(entry, address,
                status with { Message = $"Server {serverName} already belongs to network {owner}." });
        }

        var (spec, error) = RenderServerSpec(network, entry);

        if (spec is null)
        {
            return new Member(entry, address, status with { Message = error });
        }

        if (existing is null)
        {
            var server = new V1Alpha1Server
            {
                Metadata = new V1ObjectMeta
                {
                    Name = serverName,
                    NamespaceProperty = network.Namespace(),
                    Labels = new Dictionary<string, string> { [V1Alpha1Network.NetworkLabel] = network.Name() }
                },
                Spec = spec
            }.Initialize();

            server = await client.CreateAsync(server, cancellationToken).ConfigureAwait(false);

            return new Member(entry, address, status with { Phase = server.Status.Phase.ToString() });
        }

        // The ServerController deletes the claim (and the world) when `storage` is removed. Never do that implicitly,
        // e.g. when an existing server joins without `storage` in the network's spec.
        if (existing.Spec.Storage is not null && spec.Storage is null)
        {
            return new Member(entry, address, status with
            {
                Message = $"The rendered spec has no `storage`, which would delete the data of Server {serverName}. " +
                          "Add `storage` to `serverTemplate` or the server's `spec`."
            });
        }

        var joining = owner is null;

        if (joining || !SpecEquals(existing.Spec, spec))
        {
            existing.Metadata.Labels ??= new Dictionary<string, string>();
            existing.Metadata.Labels[V1Alpha1Network.NetworkLabel] = network.Name();
            existing.Spec = spec;

            try
            {
                existing = await client.UpdateAsync(existing, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpOperationException e) when (e.Response.StatusCode is HttpStatusCode.Conflict)
            {
                // The server was changed concurrently (e.g. by the ServerController). Not an error: render again
                // against the latest version shortly.
                return new Member(entry, address, status, Retry: true);
            }

            if (joining)
            {
                await emitEvent(network, "ServerJoined", $"Server {serverName} joined the network.",
                    EventType.Normal, cancellationToken).ConfigureAwait(false);
            }
        }

        return new Member(entry, address, status);
    }

    private async Task<Member> ResolveUnmanagedAsync(V1Alpha1Network network,
        V1Alpha1Network.V1Alpha1NetworkServer entry, string? validationError, CancellationToken cancellationToken)
    {
        var status = new V1Alpha1Network.V1Alpha1NetworkServerStatus
        {
            Name = entry.Name,
            Server = entry.TargetRef?.Name,
            Managed = false,
            Message = validationError
        };

        if (validationError is not null || entry.TargetRef is not { } targetRef)
        {
            return new Member(entry, null, status);
        }

        var ns = targetRef.NamespaceProperty ?? network.Namespace();

        switch (targetRef.Kind)
        {
            case null or "Server":
                var server = await client.GetAsync<V1Alpha1Server>(targetRef.Name, ns, cancellationToken)
                    .ConfigureAwait(false);

                // The address does not depend on whether the target exists (see ReconcileManagedAsync).
                return new Member(entry,
                    ServiceAddress(targetRef.Name, ns, server?.Spec.Service?.Port),
                    status with { Phase = server?.Status.Phase.ToString() ?? "Missing" });
            case "Network":
                var target = await client.GetAsync<V1Alpha1Network>(targetRef.Name, ns, cancellationToken)
                    .ConfigureAwait(false);

                return new Member(entry,
                    ServiceAddress(targetRef.Name, ns, target?.Spec.Service?.Port),
                    status with { Phase = target is null ? "Missing" : "Network" });
            default:
                return new Member(entry, null,
                    status with { Message = $"Unsupported target kind {targetRef.Kind}." });
        }
    }

    /// <summary>
    /// Checks the server entries for constraints the CRD schema cannot express. Returns the error per entry.
    /// </summary>
    private static Dictionary<V1Alpha1Network.V1Alpha1NetworkServer, string> Validate(V1Alpha1Network network)
    {
        var errors = new Dictionary<V1Alpha1Network.V1Alpha1NetworkServer, string>(ReferenceEqualityComparer.Instance);
        var entries = network.Spec.Servers ?? [];

        foreach (var duplicate in entries.GroupBy(e => e.Name).Where(g => g.Count() > 1).SelectMany(g => g))
        {
            errors.TryAdd(duplicate, $"The name `{duplicate.Name}` is used by more than one server.");
        }

        foreach (var duplicate in entries.Where(e => e.Unmanaged is not true)
                     .GroupBy(e => ServerName(network, e)).Where(g => g.Count() > 1).SelectMany(g => g))
        {
            errors.TryAdd(duplicate, $"The Server `{ServerName(network, duplicate)}` is used by more than one server.");
        }

        foreach (var entry in entries)
        {
            if (entry.Unmanaged is true)
            {
                if (entry.TargetRef is null)
                {
                    errors.TryAdd(entry, "Unmanaged servers require `targetRef`.");
                }
                else if (entry.Spec is not null || entry.ServerName is not null)
                {
                    errors.TryAdd(entry, "Unmanaged servers must not set `spec` or `serverName`.");
                }
            }
            else if (entry.TargetRef is not null)
            {
                errors.TryAdd(entry, "`targetRef` is only allowed for unmanaged servers (`unmanaged: true`).");
            }
            else if (ServerName(network, entry).Length > 63)
            {
                errors.TryAdd(entry,
                    $"The Server name `{ServerName(network, entry)}` is longer than 63 characters. Set a shorter `serverName`.");
            }
        }

        return errors;
    }

    /// <summary>
    /// Renders the spec of a managed server: the entry's `spec` merged onto `serverTemplate`, plus the settings the
    /// network enforces. The network's types only contain settings of `Server` with the same names, so the merged
    /// JSON deserializes into the `Server` spec.
    /// </summary>
    private static (V1Alpha1Server.V1Alpha1ServerSpec? Spec, string? Error) RenderServerSpec(V1Alpha1Network network,
        V1Alpha1Network.V1Alpha1NetworkServer entry)
    {
        if (network.Spec.ServerTemplate is not { } template)
        {
            return (null, "Managed servers require `serverTemplate`.");
        }

        var merged = JsonMergePatch.WithoutNulls(JsonNode.Parse(KubernetesJson.Serialize(template)));

        // Without overrides, the template applies as it is (a null patch would replace the whole document).
        if (entry.Spec is { } overrides)
        {
            merged = JsonMergePatch.Apply(merged,
                JsonMergePatch.WithoutNulls(JsonNode.Parse(KubernetesJson.Serialize(overrides))));
        }

        merged ??= new JsonObject();

        V1Alpha1Server.V1Alpha1ServerSpec? spec;

        try
        {
            spec = KubernetesJson.Deserialize<V1Alpha1Server.V1Alpha1ServerSpec>(merged.ToJsonString());
        }
        catch (JsonException e)
        {
            return (null, $"Invalid `spec`: {e.Message}");
        }

        if (spec is null || string.IsNullOrWhiteSpace(spec.Version))
        {
            return (null, "`version` must not be empty.");
        }

        var type = spec.Type ?? ServerType.Vanilla;

        if (ServerForwarding.GetSupport(type) is ServerForwarding.Support.None)
        {
            return (null,
                $"Servers of type `{type}` cannot trust a Velocity proxy. Use Paper, Pufferfish, Purpur, Folia, Fabric or Quilt, or add the server as unmanaged and configure it yourself.");
        }

        if (spec.AutoSleep?.Mode is AutoSleepMode.Hibernate)
        {
            return (null,
                "`autoSleep.mode: Hibernate` is not supported behind a proxy: a hibernating server is only woken up by connections through a `Router`. Use `Pause` instead.");
        }

        spec.Forwarding = new V1Alpha1Server.V1Alpha1ServerForwarding
        {
            SecretRef = new V1SecretKeyRef { Name = network.Name(), Key = ForwardingSecretKey }
        };

        return (spec, null);
    }

    /// <summary>
    /// Compares specs semantically. The API server returns maps (e.g. `properties`) with sorted keys, so the
    /// serialized form of an unchanged spec can differ from the rendered one.
    /// </summary>
    private static bool SpecEquals(V1Alpha1Server.V1Alpha1ServerSpec a, V1Alpha1Server.V1Alpha1ServerSpec b) =>
        JsonNode.DeepEquals(
            JsonMergePatch.WithoutNulls(JsonNode.Parse(KubernetesJson.Serialize(a))),
            JsonMergePatch.WithoutNulls(JsonNode.Parse(KubernetesJson.Serialize(b))));

    private static string ServerName(V1Alpha1Network network, V1Alpha1Network.V1Alpha1NetworkServer entry) =>
        entry.ServerName ?? $"{network.Name()}-{entry.Name}";

    /// <summary>
    /// Managed servers have no `service` settings, so their `Service` always uses the default port.
    /// </summary>
    private static string ManagedAddress(V1Alpha1Network network, string serverName) =>
        ServiceAddress(serverName, network.Namespace(), null);

    private static string ServiceAddress(string name, string ns, int? port) =>
        $"{name}.{ns}.svc.cluster.local:{port ?? MinecraftConstants.DefaultMinecraftPort}";

    /// <summary>
    /// Velocity reads its configuration only on startup: the hash in the pod template restarts the proxy when the
    /// configuration changes, which only happens for changes of the network's spec.
    /// </summary>
    private static string ConfigHash(V1ConfigMap cm) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(cm.Data?["velocity.toml"] ?? string.Empty)))[..16];

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

    private async Task<V1ConfigMap> EnsureConfigMapAsync(V1Alpha1Network network, IReadOnlyList<Member> members,
        CancellationToken cancellationToken)
    {
        var cm = await client.GetAsync<V1ConfigMap>(network.Name(), network.Namespace(), cancellationToken)
            .ConfigureAwait(false);

        if (cm is null)
        {
            cm = new V1ConfigMap { Metadata = new V1ObjectMeta() };

            ApplyConfigMap(cm, network, members);

            return await client.CreateAsync(cm, cancellationToken).ConfigureAwait(false);
        }

        ApplyConfigMap(cm, network, members);

        return await client.UpdateAsync(cm, cancellationToken).ConfigureAwait(false);
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
                    secret.StringData[ForwardingSecretKey] = Encoding.UTF8.GetString(data);
                }
            }

            if (!secret.StringData.ContainsKey(ForwardingSecretKey))
            {
                secret.StringData[ForwardingSecretKey] = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
            }

            secret = await client.CreateAsync(secret, cancellationToken).ConfigureAwait(false);
        }

        return secret;
    }

    private async Task<V1Deployment> EnsureDeploymentAsync(V1Alpha1Network network, string configHash,
        CancellationToken cancellationToken)
    {
        var deploy = await client.GetAsync<V1Deployment>(network.Name(), network.Namespace(), cancellationToken)
            .ConfigureAwait(false);

        if (deploy is null)
        {
            deploy = new V1Deployment { Metadata = new V1ObjectMeta(), Spec = new V1DeploymentSpec() };

            ApplyDeployment(deploy, network, configHash);

            deploy = await client.CreateAsync(deploy, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            ApplyDeployment(deploy, network, configHash);

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

        // Only the Route created for spec.route belongs to the Network. A Route that merely has the same name (e.g. one
        // created by a user, or by a Server with the same name) must be neither deleted nor taken over.
        if (route is not null && route.GetOwnerReference(network) is null)
        {
            return null;
        }

        if (network.Spec.Route is null)
        {
            if (route is not null)
            {
                await client.DeleteAsync(route, cancellationToken).ConfigureAwait(false);
            }

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
                Name = network.Name(),
                Namespace = network.Namespace(),
                Kind = "Network"
            };

        route.WithOwnerReference(network);
    }

    private static void ApplyConfigMap(V1ConfigMap cm, V1Alpha1Network network, IReadOnlyList<Member> members)
    {
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

        // Only invalid unmanaged entries have no address. All others are included even if their server does not
        // exist (yet): Velocity tells players that it is unavailable.
        foreach (var (entry, address, _, _) in members.Where(m => m.Address is not null))
        {
            servers[entry.Name] = address!;

            if (entry.Try is not false)
            {
                serversTry.Add(entry.Name);
            }

            if (entry.Hostname is { } hostname)
            {
                forcedHosts.TryAdd(hostname, []);
                forcedHosts[hostname].Add(entry.Name);
            }
        }

        servers["try"] = serversTry;

        // Without a version, Velocity treats the file as a pre-2.0 config and migrates it on every start. The generated
        // config uses the format of the current Velocity 3.x (as in itzg's Velocity example).
        if (!config.ContainsKey("config-version"))
        {
            config["config-version"] = VelocityConfigVersion;
        }

        config["servers"] = servers;
        config["player-info-forwarding-mode"] = "modern";
        config["forwarding-secret-file"] = ForwardingSecretKey;

        if (network.Spec.Proxy?.Offline is true)
        {
            config["online-mode"] = false;
        }

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
    }

    private static void ApplyDeployment(V1Deployment deploy, V1Alpha1Network network, string configHash)
    {
        deploy.Metadata.Name = network.Name();
        deploy.Metadata.NamespaceProperty = network.Namespace();
        // Lets the NetworkDeploymentController find the network of this Deployment.
        deploy.Metadata.Labels = PodLabels(network);

        deploy.Spec.Replicas = network.Spec.Proxy?.Replicas;
        deploy.Spec.Selector = new V1LabelSelector { MatchLabels = PodLabels(network) };
        deploy.Spec.Template = new V1PodTemplateSpec
        {
            Metadata = new V1ObjectMeta
            {
                Labels = PodLabels(network),
                Annotations = new Dictionary<string, string> { ["kobblestone.io/config-hash"] = configHash }
            },
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
                        Image = network.Spec.Proxy?.Pod?.Image ?? ContainerImages.Proxy,
                        ImagePullPolicy = network.Spec.Proxy?.Pod?.ImagePullPolicy,
                        Ports =
                        [
                            new V1ContainerPort
                            {
                                Name = "minecraft", ContainerPort = MinecraftConstants.DefaultMinecraftPort,
                            }
                        ],
                        Resources = network.Spec.Proxy?.Resources,
                        // Ready once Velocity accepts connections, not as soon as the container started.
                        ReadinessProbe = new V1Probe
                        {
                            TcpSocket = new V1TCPSocketAction { Port = MinecraftConstants.DefaultMinecraftPort },
                            PeriodSeconds = 5
                        },
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
        new() { { V1Alpha1Network.NetworkLabel, network.Name() } };
}
