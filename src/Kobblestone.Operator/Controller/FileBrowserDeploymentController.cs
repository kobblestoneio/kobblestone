using k8s.Models;

using Kobblestone.Operator.Entities.V1Alpha1;
using Kobblestone.Operator.Utils;

using KubeOps.Abstractions.Rbac;
using KubeOps.Abstractions.Reconciliation;
using KubeOps.Abstractions.Reconciliation.Controller;
using KubeOps.Abstractions.Reconciliation.Queue;
using KubeOps.KubernetesClient;

namespace Kobblestone.Operator.Controller;

/// <summary>
/// Watches the file manager Deployments of `FileBrowser`s. It does not write any status itself: it only queues the
/// `FileBrowser`, whose controller derives its readiness from the Deployment.
/// </summary>
[GenericRbac(Groups = [V1Deployment.KubeGroup], Resources = [V1Deployment.KubePluralName],
    Verbs = RbacVerb.Watch)]
[EntityRbac(typeof(V1Alpha1FileBrowser), Verbs = RbacVerb.Get)]
public sealed class FileBrowserDeploymentController(
    IKubernetesClient client,
    EntityQueue<V1Alpha1FileBrowser> queueFileBrowser) : IEntityController<V1Deployment>
{
    public async Task<ReconciliationResult<V1Deployment>> ReconcileAsync(V1Deployment entity,
        CancellationToken cancellationToken)
    {
        await QueueFileBrowserAsync(entity, cancellationToken).ConfigureAwait(false);

        return ReconciliationResult<V1Deployment>.Success(entity);
    }

    public async Task<ReconciliationResult<V1Deployment>> DeletedAsync(V1Deployment entity,
        CancellationToken cancellationToken)
    {
        // The FileBrowserController recreates the Deployment, unless the FileBrowser expired.
        await QueueFileBrowserAsync(entity, cancellationToken).ConfigureAwait(false);

        return ReconciliationResult<V1Deployment>.Success(entity);
    }

    private async Task QueueFileBrowserAsync(V1Deployment deployment, CancellationToken cancellationToken)
    {
        if (deployment.GetLabel(V1Alpha1FileBrowser.FileBrowserLabel) is not { } name)
        {
            return;
        }

        await queueFileBrowser.RequeueAsync(client, name, deployment.Namespace(), cancellationToken)
            .ConfigureAwait(false);
    }
}
