using k8s.Models;

using Kobblestone.Operator.Conditions;
using Kobblestone.Operator.Entities.V1Alpha1;

using KubeOps.Abstractions.Rbac;
using KubeOps.Abstractions.Reconciliation;
using KubeOps.Abstractions.Reconciliation.Controller;
using KubeOps.KubernetesClient;

namespace Kobblestone.Operator.Controller;

[GenericRbac(Groups = [V1Job.KubeGroup], Resources = [V1Job.KubePluralName],
    Verbs = RbacVerb.Watch)]
[EntityRbac(typeof(V1Alpha1Backup), Verbs = RbacVerb.Get | RbacVerb.Update)]
[EntityRbac(typeof(V1Alpha1Server), Verbs = RbacVerb.Get | RbacVerb.Update)]
public sealed class BackupJobController(IKubernetesClient client) : IEntityController<V1Job>
{
    public async Task<ReconciliationResult<V1Job>> ReconcileAsync(V1Job entity, CancellationToken cancellationToken)
    {
        var backupName = entity.GetLabel("kobblestone.io/backup");

        if (backupName is null)
        {
            return ReconciliationResult<V1Job>.Success(entity);
        }

        var backup = await client.GetAsync<V1Alpha1Backup>(backupName, entity.Namespace(), cancellationToken)
            .ConfigureAwait(false);

        if (backup is null)
        {
            return ReconciliationResult<V1Job>.Success(entity);
        }

        backup.Status.Repo = backup.Spec.RepoRef.Name;
        backup.Status.Target = backup.Spec.TargetRef.Name;

        var completeCondition =
            entity.Status.Conditions?.FirstOrDefault(c => c.Type is "Complete" && c.Status is "True");

        var failedCondition = entity.Status.Conditions?.FirstOrDefault(c => c.Type is "Failed" && c.Status is "True");

        if (completeCondition is not null)
        {
            backup.Status.Phase = BackupPhase.Completed;
            backup.Status.Message = "Completed successfully";

            backup.WithConditions(
                BackupConditions.Completed,
                BackupConditions.Failed.False(),
                BackupConditions.Progressing.False());
        }
        else if (failedCondition is not null)
        {
            backup.Status.Phase = BackupPhase.Failed;
            backup.Status.Message = null;

            backup.WithConditions(
                BackupConditions.Completed.False(),
                BackupConditions.Failed,
                BackupConditions.Progressing.False());
        }
        else if (entity.Status.Ready > 0)
        {
            backup.Status.Phase = BackupPhase.Running;
            backup.Status.Message = "Actively running";

            backup.WithConditions(
                BackupConditions.Completed.False(),
                BackupConditions.Failed.False(),
                BackupConditions.Progressing.WithReason("Running").WithMessage("Backup is running."));
        }
        else
        {
            backup.Status.Phase = BackupPhase.Pending;
            backup.Status.Message = "Backup is pending";

            backup.WithConditions(
                BackupConditions.Completed.False(),
                BackupConditions.Failed.False(),
                BackupConditions.Progressing.False());
        }

        await client.UpdateStatusAsync(backup, cancellationToken).ConfigureAwait(false);

        if (backup.Status.Phase is not (BackupPhase.Completed or BackupPhase.Failed) ||
            backup.Spec.TargetRef.Kind is not (null or "Server"))
        {
            return ReconciliationResult<V1Job>.Success(entity);
        }

        var server = await client
            .GetAsync<V1Alpha1Server>(backup.Spec.TargetRef.Name, backup.Namespace(), cancellationToken)
            .ConfigureAwait(false);

        if (server is null)
        {
            return ReconciliationResult<V1Job>.Success(entity);
        }

        server.WithCondition(ServerConditions.BackupInProgress.False());

        server = await client.UpdateStatusAsync(server, cancellationToken).ConfigureAwait(false);

        return ReconciliationResult<V1Job>.Success(entity);
    }

    public Task<ReconciliationResult<V1Job>> DeletedAsync(V1Job entity, CancellationToken cancellationToken)
    {
        return Task.FromResult(ReconciliationResult<V1Job>.Success(entity));
    }
}