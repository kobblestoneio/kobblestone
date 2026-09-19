using System.Text;

using k8s.Models;

using Kobblestone.Operator.Constants;
using Kobblestone.Operator.Entities;
using Kobblestone.Operator.Entities.V1Alpha1;
using Kobblestone.Operator.Services;

using KubeOps.Abstractions.Entities;
using KubeOps.Abstractions.Rbac;
using KubeOps.Abstractions.Reconciliation;
using KubeOps.Abstractions.Reconciliation.Controller;
using KubeOps.KubernetesClient;

namespace Kobblestone.Operator.Controller;

[EntityRbac(typeof(V1Alpha1Command), Verbs = RbacVerb.Watch | RbacVerb.Get | RbacVerb.Create | RbacVerb.Update)]
[EntityRbac(typeof(V1Alpha1CommandGroup), Verbs = RbacVerb.Get | RbacVerb.Update)]
[EntityRbac(typeof(V1Alpha1Server), Verbs = RbacVerb.Get)]
[GenericRbac(Groups = [V1Service.KubeGroup], Resources = [V1Service.KubePluralName],
    Verbs = RbacVerb.Get)]
[GenericRbac(Groups = [V1Secret.KubeGroup], Resources = [V1Secret.KubePluralName],
    Verbs = RbacVerb.Get)]
public sealed class CommandController(
    IKubernetesClient client,
    IRconConnectionFactory rconConnectionFactory) : IEntityController<V1Alpha1Command>
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

            return ReconciliationResult<V1Alpha1Command>.Failure(entity,
                $"Failed to execute command due to reason {failureReason}", requeueAfter: requeueAfter);
        }

        if (host is null || port is null || password is null)
        {
            return ReconciliationResult<V1Alpha1Command>.Failure(entity,
                "Could not execute command due to missing parameters", requeueAfter: TimeSpan.FromSeconds(10));
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

        // From here on is CommandGroup handling
        if (entity.GetLabel("kobblestone.io/command-group") is not { } commandGroupName)
        {
            return ReconciliationResult<V1Alpha1Command>.Success(entity);
        }

        var commandGroup = await client
            .GetAsync<V1Alpha1CommandGroup>(commandGroupName, entity.Namespace(), cancellationToken)
            .ConfigureAwait(false);

        if (commandGroup is null)
        {
            return ReconciliationResult<V1Alpha1Command>.Success(entity);
        }

        if (!int.TryParse(entity.GetAnnotation("kobblestone.io/command-group-index"), out var currentCommandIndex))
        {
            return ReconciliationResult<V1Alpha1Command>.Success(entity);
        }

        var currentCommand = commandGroup.Spec.Commands.ElementAtOrDefault(currentCommandIndex);

        if (currentCommand is null)
        {
            // CommandGroup was probably modified in-between. We explicitly do not support this as we cannot properly handle this.
            return ReconciliationResult<V1Alpha1Command>.Success(entity);
        }

        commandGroup.Status.Commands.Add(new V1Alpha1CommandGroup.V1Alpha1CommandGroupStatusCommand
        {
            Command = entity.Spec.Command,
            Status = entity.Status.Phase is CommandPhase.Completed
                ? V1Alpha1CommandGroup.CommandResponseStatus.Completed
                : V1Alpha1CommandGroup.CommandResponseStatus.Failed,
            FailureReason = entity.Status.FailureReason,
            Response = entity.Status.Response
        });

        // Command in group has failed, and we cannot ignore it, so we skip the rest of the group
        if (entity.Status.Phase is CommandPhase.Failed && currentCommand.IgnoreFailure is not true)
        {
            foreach (var remainingCommand in commandGroup.Spec.Commands.Skip(currentCommandIndex + 1))
            {
                commandGroup.Status.Commands.Add(new V1Alpha1CommandGroup.V1Alpha1CommandGroupStatusCommand
                {
                    Command = remainingCommand.Command, Status = V1Alpha1CommandGroup.CommandResponseStatus.Skipped
                });
            }

            commandGroup.Status.Phase = V1Alpha1CommandGroup.CommandGroupPhase.Failed;
            commandGroup.Status.Message = "One or more commands failed.";

            await client.UpdateStatusAsync(commandGroup, cancellationToken).ConfigureAwait(false);

            return ReconciliationResult<V1Alpha1Command>.Success(entity);
        }

        var nextCommand = commandGroup.Spec.Commands.ElementAtOrDefault(currentCommandIndex + 1);

        if (nextCommand is null)
        {
            commandGroup.Status.Phase = V1Alpha1CommandGroup.CommandGroupPhase.Completed;
            commandGroup.Status.Message = "All commands executed.";
        }

        await client.UpdateStatusAsync(commandGroup, cancellationToken).ConfigureAwait(false);

        if (nextCommand is not null)
        {
            await client
                .CreateAsync(
                    new V1Alpha1Command
                    {
                        Metadata = new V1ObjectMeta
                        {
                            Name = $"{commandGroup.Name()}-{currentCommandIndex + 1}",
                            NamespaceProperty = commandGroup.Namespace(),
                            Labels =
                                new Dictionary<string, string>
                                {
                                    { "kobblestone.io/command-group", commandGroup.Name() }
                                },
                            Annotations =
                                new Dictionary<string, string>
                                {
                                    { "kobblestone.io/command-group-index", (currentCommandIndex + 1).ToString() }
                                }
                        },
                        Spec = new V1Alpha1Command.V1Alpha1CommandSpec
                        {
                            Command = nextCommand.Command,
                            TargetRef = commandGroup.Spec.TargetRef,
                            PasswordSecretRef = commandGroup.Spec.PasswordSecretRef,
                            TimeoutMilliseconds = commandGroup.Spec.TimeoutMilliseconds,
                            TrafficPolicy = commandGroup.Spec.TrafficPolicy
                        }
                    }.Initialize().WithOwnerReference(commandGroup), cancellationToken).ConfigureAwait(false);
        }

        return ReconciliationResult<V1Alpha1Command>.Success(entity);
    }

    public Task<ReconciliationResult<V1Alpha1Command>> DeletedAsync(V1Alpha1Command entity,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(ReconciliationResult<V1Alpha1Command>.Success(entity));
    }
}