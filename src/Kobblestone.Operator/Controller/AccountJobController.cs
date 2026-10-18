using k8s.Models;

using Kobblestone.Operator.Entities.V1Alpha1;
using Kobblestone.Operator.Utils;
using Kobblestone.Sessions;

using KubeOps.Abstractions.Rbac;
using KubeOps.Abstractions.Reconciliation;
using KubeOps.Abstractions.Reconciliation.Controller;
using KubeOps.Abstractions.Reconciliation.Queue;
using KubeOps.KubernetesClient;

namespace Kobblestone.Operator.Controller;

/// <summary>
/// Watches the auth `Job`s of `Account`s. It does not write any status itself: it only queues the `Account`, whose
/// controller derives its status from the `Job` and the `Secret`s.
/// </summary>
[GenericRbac(Groups = [V1Job.KubeGroup], Resources = [V1Job.KubePluralName], Verbs = RbacVerb.Watch)]
[EntityRbac(typeof(V1Alpha1Account), Verbs = RbacVerb.Get)]
public sealed class AccountJobController(IKubernetesClient client, EntityQueue<V1Alpha1Account> queueAccount)
    : IEntityController<V1Job>
{
    public async Task<ReconciliationResult<V1Job>> ReconcileAsync(V1Job entity,
        CancellationToken cancellationToken)
    {
        await QueueAccountAsync(entity, cancellationToken).ConfigureAwait(false);

        return ReconciliationResult<V1Job>.Success(entity);
    }

    public async Task<ReconciliationResult<V1Job>> DeletedAsync(V1Job entity,
        CancellationToken cancellationToken)
    {
        // A new Job is started by the AccountController if required.
        await QueueAccountAsync(entity, cancellationToken).ConfigureAwait(false);

        return ReconciliationResult<V1Job>.Success(entity);
    }

    private async Task QueueAccountAsync(V1Job job, CancellationToken cancellationToken)
    {
        if (job.GetLabel(AccountLabels.Account) is { } accountName)
        {
            await queueAccount.RequeueAsync(client, accountName, job.Namespace(), cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
