using k8s.Models;

using Kobblestone.Operator.Entities.V1Alpha1;

using KubeOps.Abstractions.Entities;
using KubeOps.Abstractions.Rbac;
using KubeOps.Abstractions.Reconciliation;
using KubeOps.Abstractions.Reconciliation.Controller;
using KubeOps.KubernetesClient;

namespace Kobblestone.Operator.Controller;

[EntityRbac(typeof(V1Alpha1CommandGroup), Verbs = RbacVerb.Watch | RbacVerb.Get | RbacVerb.Update)]
[EntityRbac(typeof(V1Alpha1Command), Verbs = RbacVerb.Create)]
public sealed class CommandGroupController(IKubernetesClient client) : IEntityController<V1Alpha1CommandGroup>
{
    public async Task<ReconciliationResult<V1Alpha1CommandGroup>> ReconcileAsync(V1Alpha1CommandGroup entity,
        CancellationToken cancellationToken)
    {
        if (entity.Status.Phase is not null)
        {
            return ReconciliationResult<V1Alpha1CommandGroup>.Success(entity);
        }

        var firstCommand = entity.Spec.Commands.FirstOrDefault();

        if (firstCommand is null)
        {
            return ReconciliationResult<V1Alpha1CommandGroup>.Success(entity);
        }

        var command = new V1Alpha1Command
        {
            Metadata = new V1ObjectMeta
            {
                Name = $"{entity.Name()}-0",
                NamespaceProperty = entity.Namespace(),
                Labels = new Dictionary<string, string> { { "kobblestone.io/command-group", entity.Name() } },
                Annotations = new Dictionary<string, string> { { "kobblestone.io/command-group-index", "0" } }
            },
            Spec = new V1Alpha1Command.V1Alpha1CommandSpec
            {
                Command = firstCommand.Command,
                TargetRef = entity.Spec.TargetRef,
                PasswordSecretRef = entity.Spec.PasswordSecretRef,
                TimeoutMilliseconds = entity.Spec.TimeoutMilliseconds,
                TrafficPolicy = entity.Spec.TrafficPolicy
            }
        }.Initialize();

        command.WithOwnerReference(entity);

        await client.CreateAsync(command, cancellationToken).ConfigureAwait(false);

        entity.Status.Phase = V1Alpha1CommandGroup.CommandGroupPhase.Pending;

        entity = await client.UpdateStatusAsync(entity, cancellationToken).ConfigureAwait(false);

        return ReconciliationResult<V1Alpha1CommandGroup>.Success(entity);
    }

    public Task<ReconciliationResult<V1Alpha1CommandGroup>> DeletedAsync(V1Alpha1CommandGroup entity,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(ReconciliationResult<V1Alpha1CommandGroup>.Success(entity));
    }
}