using System.Text;

using k8s.Models;

using Kobblestone.Operator.Constants;
using Kobblestone.Operator.Entities;
using Kobblestone.Operator.Entities.V1Alpha1;
using Kobblestone.Operator.Services;
using Kobblestone.Operator.Utils;

using KubeOps.Abstractions.Entities;
using KubeOps.Abstractions.Events;
using KubeOps.Abstractions.Rbac;
using KubeOps.Abstractions.Reconciliation;
using KubeOps.Abstractions.Reconciliation.Controller;
using KubeOps.Abstractions.Reconciliation.Queue;
using KubeOps.KubernetesClient;

namespace Kobblestone.Operator.Controller;

[EntityRbac(typeof(V1Alpha1Command), Verbs = RbacVerb.Watch | RbacVerb.Get | RbacVerb.Update)]
[EntityRbac(typeof(V1Alpha1CommandGroup), Verbs = RbacVerb.Get)]
[EntityRbac(typeof(V1Alpha1Server), Verbs = RbacVerb.Get)]
[GenericRbac(Groups = [V1Service.KubeGroup], Resources = [V1Service.KubePluralName],
    Verbs = RbacVerb.Get)]
[GenericRbac(Groups = [V1Secret.KubeGroup], Resources = [V1Secret.KubePluralName],
    Verbs = RbacVerb.Get)]
public sealed class CommandController(
    IKubernetesClient client,
    IRconConnectionFactory rconConnectionFactory,
    EventPublisher emitEvent,
    EntityQueue<V1Alpha1CommandGroup> queueCommandGroup) : IEntityController<V1Alpha1Command>
{
    public async Task<ReconciliationResult<V1Alpha1Command>> ReconcileAsync(V1Alpha1Command entity,
        CancellationToken cancellationToken)
    {
        if (entity.Status.ExecutedAt is not null)
        {
            return ReconciliationResult<V1Alpha1Command>.Success(entity);
        }

        string? host = null;

        int? port = null;

        string? failureReason = null;

        TimeSpan? requeueAfter = null;

        V1SecretKeyRef? secretKeyRef = entity.Spec.PasswordSecretRef;

        string? password = null;

        if (entity.Spec.TargetRef is { Kind: null or "Server" })
        {
            var server = await client
                .GetAsync<V1Alpha1Server>(entity.Spec.TargetRef.Name, entity.Namespace(), cancellationToken)
                .ConfigureAwait(false);

            var service = await client
                .GetAsync<V1Service>(entity.Spec.TargetRef.Name, entity.Namespace(), cancellationToken)
                .ConfigureAwait(false);

            if (server is not null && service is not null)
            {
                secretKeyRef ??= new V1SecretKeyRef { Name = server.Name(), Key = "rcon-password" };

                if (server.Spec.Rcon?.Disabled is not true)
                {
                    if (server.Status.Phase is ServerPhase.Running)
                    {
                        if (service.Spec.Type is "LoadBalancer" &&
                            entity.Spec.TrafficPolicy is CommandTrafficPolicy.External)
                        {
                            if (service.Status?.LoadBalancer?.Ingress?.FirstOrDefault() is { Ip: { } lbIp })
                            {
                                host = lbIp;
                            }
                        }

                        host ??= service.Spec.ClusterIP;

                        port = entity.Spec.TargetRef.Port ??
                               service.Spec.Ports?.FirstOrDefault(p => p.Name == "rcon")?.Port;
                    }
                    else
                    {
                        failureReason = "ServerNotRunning";
                        entity.Status.Message = "Server is not running";
                        requeueAfter = TimeSpan.FromSeconds(5);
                    }
                }
                else
                {
                    failureReason = "RconDisabled";
                    entity.Status.Message = "RCON disabled on server";
                    requeueAfter = TimeSpan.FromSeconds(5);
                }
            }
            else
            {
                if (server is null)
                {
                    failureReason = "MissingTarget";
                    entity.Status.Message = "Server not found";
                }
                else if (service is null)
                {
                    failureReason = "MissingTarget";
                    entity.Status.Message = "Server service not found";
                }

                requeueAfter = TimeSpan.FromSeconds(5);
            }
        }
        else if (entity.Spec.TargetRef is { Kind: "Service" })
        {
            var service = await client
                .GetAsync<V1Service>(entity.Spec.TargetRef.Name, entity.Namespace(), cancellationToken)
                .ConfigureAwait(false);

            if (service is not null)
            {
                if (service.Spec.Type is "ExternalName")
                {
                    failureReason = "InvalidTarget";
                    entity.Status.Message = "Services of type `ExternalName` are not supported";
                }
                else
                {
                    if (service.Spec.Type is "LoadBalancer" &&
                        entity.Spec.TrafficPolicy is CommandTrafficPolicy.External)
                    {
                        if (service.Status?.LoadBalancer?.Ingress?.FirstOrDefault() is { Ip: { } lbIp })
                        {
                            host = lbIp;
                        }
                    }

                    host ??= service.Spec.ClusterIP;
                    port = entity.Spec.TargetRef.Port ?? MinecraftConstants.DefaultRconPort;
                }
            }
            else
            {
                failureReason = "MissingTarget";
                entity.Status.Message = "Service not found";
                requeueAfter = TimeSpan.FromSeconds(5);
            }
        }
        else
        {
            failureReason = "InvalidTarget";
            entity.Status.Message = "Invalid target";
            requeueAfter = TimeSpan.FromSeconds(5);
        }

        if (secretKeyRef is not null)
        {
            var secret = await client
                .GetAsync<V1Secret>(secretKeyRef.Name, entity.Namespace(), cancellationToken)
                .ConfigureAwait(false);

            if (secret is not null)
            {
                if (secret.Data.TryGetValue("rcon-password", out var rawPassword) is true)
                {
                    password = Encoding.UTF8.GetString(rawPassword);
                }
                else
                {
                    failureReason = "MissingPassword";
                    entity.Status.Message = "RCON password not found in secret";
                    requeueAfter = TimeSpan.FromSeconds(5);
                }
            }
            else
            {
                failureReason = "MissingSecret";
                entity.Status.Message = "Secret not found";
                requeueAfter = TimeSpan.FromSeconds(5);
            }
        }
        else
        {
            failureReason = "MissingSecret";
            entity.Status.Message = "Secret not found";
            requeueAfter = TimeSpan.FromSeconds(5);
        }

        if (failureReason is not null)
        {
            entity.Status.FailureReason = failureReason;

            entity = await client.UpdateStatusAsync(entity, cancellationToken).ConfigureAwait(false);

            // Waiting for the server to come up is expected, everything else points to a misconfiguration.
            await emitEvent(entity,
                failureReason,
                $"Command not executed yet: {entity.Status.Message ?? failureReason}.",
                failureReason is "ServerNotRunning" ? EventType.Normal : EventType.Warning,
                cancellationToken).ConfigureAwait(false);

            // Without requeueAfter (spec errors), the command is retried once its spec changes.
            return ReconciliationResult<V1Alpha1Command>.Success(entity, requeueAfter);
        }

        if (host is null || port is null || password is null)
        {
            var missing = host is null ? "host" : port is null ? "port" : "password";

            await emitEvent(entity,
                "MissingParameters",
                $"Command not executed yet: could not determine the {missing} of the target.",
                EventType.Warning, cancellationToken).ConfigureAwait(false);

            return ReconciliationResult<V1Alpha1Command>.Success(entity, TimeSpan.FromSeconds(10));
        }

        var timeout = TimeSpan.FromMilliseconds(entity.Spec.TimeoutMilliseconds ?? 5000);

        try
        {
            await using var rcon = await rconConnectionFactory.ConnectAsync(host, port.Value, password,
                timeout, cancellationToken).ConfigureAwait(false);

            var response = await rcon.SendCommandAsync(entity.Spec.Command, timeout, cancellationToken)
                .ConfigureAwait(false);

            entity.Status.FailureReason = null;
            entity.Status.Phase = CommandPhase.Completed;
            entity.Status.Response = response;
            entity.Status.Message = "Executed sucessfully";
        }
        catch (TimeoutException)
        {
            entity.Status.FailureReason = "Timeout";
            entity.Status.Message = "Command timed out";
            entity.Status.Phase = CommandPhase.Failed;
        }
        catch (Exception ex)
        {
            entity.Status.FailureReason = "UnknownError";
            entity.Status.Message = ex.GetType().Name;
            entity.Status.Phase = CommandPhase.Failed;
        }

        entity.Status.ExecutedAt = DateTime.UtcNow;

        entity = await client.UpdateStatusAsync(entity, cancellationToken).ConfigureAwait(false);

        // The CommandGroupController derives the group's status from its commands and starts the next one.
        if (entity.GetLabel(CommandGroupController.CommandGroupLabel) is { } commandGroupName)
        {
            await queueCommandGroup.RequeueAsync(client, commandGroupName, entity.Namespace(), cancellationToken)
                .ConfigureAwait(false);
        }

        return ReconciliationResult<V1Alpha1Command>.Success(entity);
    }

    public Task<ReconciliationResult<V1Alpha1Command>> DeletedAsync(V1Alpha1Command entity,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(ReconciliationResult<V1Alpha1Command>.Success(entity));
    }
}
