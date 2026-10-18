using k8s.Models;

using Kobblestone.Operator.Entities.V1Alpha1;
using Kobblestone.Operator.Utils;

using KubeOps.Abstractions.Entities;
using KubeOps.Abstractions.Rbac;
using KubeOps.Abstractions.Reconciliation;
using KubeOps.Abstractions.Reconciliation.Controller;
using KubeOps.KubernetesClient;
using KubeOps.KubernetesClient.Selectors;

namespace Kobblestone.Operator.Controller;

/// <summary>
/// Runs the commands of a `CommandGroup` one after another as `Command`s and derives the group's status from them.
/// The CommandController queues the group whenever one of its commands was executed.
/// </summary>
[EntityRbac(typeof(V1Alpha1CommandGroup), Verbs = RbacVerb.Watch | RbacVerb.Get | RbacVerb.Update)]
[EntityRbac(typeof(V1Alpha1Command), Verbs = RbacVerb.List | RbacVerb.Create)]
public sealed class CommandGroupController(IKubernetesClient client) : IEntityController<V1Alpha1CommandGroup>
{
    public const string CommandGroupLabel = "kobblestone.io/command-group";

    public const string CommandGroupIndexAnnotation = "kobblestone.io/command-group-index";

    public async Task<ReconciliationResult<V1Alpha1CommandGroup>> ReconcileAsync(V1Alpha1CommandGroup entity,
        CancellationToken cancellationToken)
    {
        // A finished group is never touched again.
        if (entity.Status.Phase is V1Alpha1CommandGroup.CommandGroupPhase.Completed
            or V1Alpha1CommandGroup.CommandGroupPhase.Failed)
        {
            return ReconciliationResult<V1Alpha1CommandGroup>.Success(entity);
        }

        var commands = await client.ListAsync<V1Alpha1Command>(entity.Namespace(),
                new EqualsLabelSelector(CommandGroupLabel, entity.Name()), cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        // Only commands owned by this group count: a previous group with the same name may have left some behind.
        var commandsByIndex = commands
            .Where(c => c.GetOwnerReference(entity) is not null)
            .Select(c => (
                Index: int.TryParse(c.GetAnnotation(CommandGroupIndexAnnotation), out var index) ? index : -1,
                Command: c))
            .Where(c => c.Index >= 0)
            .GroupBy(c => c.Index)
            .ToDictionary(g => g.Key, g => g.First().Command);

        var total = entity.Spec.Commands.Count;
        var statusCommands = new List<V1Alpha1CommandGroup.V1Alpha1CommandGroupStatusCommand>();
        var phase = V1Alpha1CommandGroup.CommandGroupPhase.Completed;
        var message = "All commands executed.";
        int? nextIndex = null;

        for (var index = 0; index < total; index++)
        {
            if (!commandsByIndex.TryGetValue(index, out var command))
            {
                // Commands run one after another: the next one is only created once its predecessor was executed.
                nextIndex = index;
                phase = V1Alpha1CommandGroup.CommandGroupPhase.Pending;
                message = $"Executing command {index + 1} of {total}.";
                break;
            }

            if (command.Status.ExecutedAt is null)
            {
                phase = V1Alpha1CommandGroup.CommandGroupPhase.Pending;
                message = $"Executing command {index + 1} of {total}.";
                break;
            }

            var failed = command.Status.Phase is not CommandPhase.Completed;

            statusCommands.Add(new V1Alpha1CommandGroup.V1Alpha1CommandGroupStatusCommand
            {
                Command = command.Spec.Command,
                Status = failed
                    ? V1Alpha1CommandGroup.CommandResponseStatus.Failed
                    : V1Alpha1CommandGroup.CommandResponseStatus.Completed,
                FailureReason = command.Status.FailureReason,
                Response = command.Status.Response
            });

            // A failed command that may not be ignored skips the rest of the group.
            if (failed && entity.Spec.Commands[index].IgnoreFailure is not true)
            {
                statusCommands.AddRange(entity.Spec.Commands.Skip(index + 1).Select(c =>
                    new V1Alpha1CommandGroup.V1Alpha1CommandGroupStatusCommand
                    {
                        Command = c.Command,
                        Status = V1Alpha1CommandGroup.CommandResponseStatus.Skipped
                    }));

                phase = V1Alpha1CommandGroup.CommandGroupPhase.Failed;
                message = "One or more commands failed.";
                break;
            }
        }

        if (nextIndex is { } i)
        {
            await client.CreateAsync(BuildCommand(entity, i), cancellationToken).ConfigureAwait(false);
        }

        var before = EntityQueueExtensions.Snapshot(entity.Status);

        entity.Status.Phase = phase;
        entity.Status.Message = message;
        entity.Status.Commands = statusCommands;

        if (EntityQueueExtensions.Snapshot(entity.Status) != before)
        {
            entity = await client.UpdateStatusAsync(entity, cancellationToken).ConfigureAwait(false);
        }

        return ReconciliationResult<V1Alpha1CommandGroup>.Success(entity);
    }

    public Task<ReconciliationResult<V1Alpha1CommandGroup>> DeletedAsync(V1Alpha1CommandGroup entity,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(ReconciliationResult<V1Alpha1CommandGroup>.Success(entity));
    }

    private static V1Alpha1Command BuildCommand(V1Alpha1CommandGroup group, int index)
    {
        var command = new V1Alpha1Command
        {
            Metadata = new V1ObjectMeta
            {
                Name = $"{group.Name()}-{index}",
                NamespaceProperty = group.Namespace(),
                Labels = new Dictionary<string, string> { { CommandGroupLabel, group.Name() } },
                Annotations = new Dictionary<string, string> { { CommandGroupIndexAnnotation, index.ToString() } }
            },
            Spec = new V1Alpha1Command.V1Alpha1CommandSpec
            {
                Command = group.Spec.Commands[index].Command,
                TargetRef = group.Spec.TargetRef,
                PasswordSecretRef = group.Spec.PasswordSecretRef,
                TimeoutMilliseconds = group.Spec.TimeoutMilliseconds,
                TrafficPolicy = group.Spec.TrafficPolicy
            }
        }.Initialize();

        command.WithOwnerReference(group);

        return command;
    }
}
