using k8s.Models;

using Kobblestone.Operator.Entities.V1Alpha1;

using KubeOps.Abstractions.Rbac;
using KubeOps.Abstractions.Reconciliation;
using KubeOps.Abstractions.Reconciliation.Controller;
using KubeOps.KubernetesClient;

namespace Kobblestone.Operator.Controller;

[GenericRbac(Groups = [V1Job.KubeGroup], Resources = [V1Job.KubePluralName],
    Verbs = RbacVerb.Watch)]
[EntityRbac(typeof(V1Alpha1Restore), Verbs = RbacVerb.Get | RbacVerb.Update)]
public sealed class RestoreJobController(IKubernetesClient client) : IEntityController<V1Job>
{
    public async Task<ReconciliationResult<V1Job>> ReconcileAsync(V1Job entity, CancellationToken cancellationToken)
    {
        var restoreName = entity.GetLabel("kobblestone.io/restore");

        if (restoreName is null)
        {
            return ReconciliationResult<V1Job>.Success(entity);
        }

        var restore = await client.GetAsync<V1Alpha1Restore>(restoreName, entity.Namespace(), cancellationToken)
            .ConfigureAwait(false);

        if (restore is null)
        {
            return ReconciliationResult<V1Job>.Success(entity);
        }

        var completeCondition =
            entity.Status.Conditions?.FirstOrDefault(c => c.Type is "Complete" && c.Status is "True");

        var failedCondition = entity.Status.Conditions?.FirstOrDefault(c => c.Type is "Failed" && c.Status is "True");

        if (completeCondition is not null)
        {
            restore.Status.Phase = RestorePhase.Completed;
            restore.Status.Message = "Completed successfully";
        }
        else if (failedCondition is not null)
        {
            restore.Status.Phase = RestorePhase.Failed;
            restore.Status.Message = null;
        }
        else if (entity.Status.Ready > 0)
        {
            restore.Status.Phase = RestorePhase.Running;
            restore.Status.Message = "Actively running";
        }
        else
        {
            restore.Status.Phase = RestorePhase.Pending;
            restore.Status.Message = "Restore is pending";
        }

        await client.UpdateStatusAsync(restore, cancellationToken).ConfigureAwait(false);

        return ReconciliationResult<V1Job>.Success(entity);
    }

    public Task<ReconciliationResult<V1Job>> DeletedAsync(V1Job entity, CancellationToken cancellationToken)
    {
        return Task.FromResult(ReconciliationResult<V1Job>.Success(entity));
    }
}