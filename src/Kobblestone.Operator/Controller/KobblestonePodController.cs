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
/// Watches the `Pod`s Kobblestone creates that mount server or repo volumes (registered once per label selector:
/// servers, backup, restore and cleanup `Job`s, file managers). Whenever one changes (e.g. it is scheduled to a node)
/// or is deleted, it queues the `FileBrowser`s that mount the same servers' or repos' volumes, so their file manager
/// follows it immediately. It keeps no state and writes no status: the servers and repos come from the `Pod`'s labels
/// (see <see cref="VolumeUsers"/>), the `FileBrowser`s from the <see cref="FileBrowserIndex"/>.
/// </summary>
[GenericRbac(Groups = [V1Pod.KubeGroup], Resources = [V1Pod.KubePluralName], Verbs = RbacVerb.Watch)]
[EntityRbac(typeof(V1Alpha1FileBrowser), Verbs = RbacVerb.Get)]
public sealed class KobblestonePodController(
    IKubernetesClient client,
    FileBrowserIndex fileBrowserIndex,
    EntityQueue<V1Alpha1FileBrowser> queueFileBrowser) : IEntityController<V1Pod>
{
    public async Task<ReconciliationResult<V1Pod>> ReconcileAsync(V1Pod entity, CancellationToken cancellationToken)
    {
        await QueueFileBrowsersAsync(entity, cancellationToken).ConfigureAwait(false);

        return ReconciliationResult<V1Pod>.Success(entity);
    }

    public async Task<ReconciliationResult<V1Pod>> DeletedAsync(V1Pod entity, CancellationToken cancellationToken)
    {
        await QueueFileBrowsersAsync(entity, cancellationToken).ConfigureAwait(false);

        return ReconciliationResult<V1Pod>.Success(entity);
    }

    private async Task QueueFileBrowsersAsync(V1Pod pod, CancellationToken cancellationToken)
    {
        // A Pod only pins file managers once it is scheduled, or frees the volume when it is gone.
        if (string.IsNullOrEmpty(pod.Spec.NodeName))
        {
            return;
        }

        var (servers, repos) = VolumeUsers.Of(pod);

        // A file manager does not have to follow itself.
        var own = pod.GetLabel(V1Alpha1FileBrowser.FileBrowserLabel);

        var fileBrowsers = servers
            .SelectMany(server => fileBrowserIndex.FileBrowsersOf(pod.Namespace(), server))
            .Concat(repos.SelectMany(repo => fileBrowserIndex.FileBrowsersOfRepo(pod.Namespace(), repo)))
            .Where(f => f != own)
            .Distinct();

        foreach (var fileBrowser in fileBrowsers)
        {
            await queueFileBrowser.RequeueAsync(client, fileBrowser, pod.Namespace(), cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
