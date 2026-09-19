using k8s.Models;

using Kobblestone.Operator.Entities.V1Alpha1;

using KubeOps.Abstractions.Entities;
using KubeOps.Abstractions.Rbac;
using KubeOps.Abstractions.Reconciliation;
using KubeOps.Abstractions.Reconciliation.Controller;
using KubeOps.KubernetesClient;

namespace Kobblestone.Operator.Controller;

[EntityRbac(typeof(V1Alpha1BackupRepo), Verbs = RbacVerb.Watch | RbacVerb.Get | RbacVerb.Update)]
[GenericRbac(Groups = [V1PersistentVolumeClaim.KubeGroup], Resources = [V1PersistentVolumeClaim.KubePluralName],
    Verbs = RbacVerb.Get | RbacVerb.Create | RbacVerb.Update)]
public sealed class BackupRepoController(IKubernetesClient client) : IEntityController<V1Alpha1BackupRepo>
{
    public async Task<ReconciliationResult<V1Alpha1BackupRepo>> ReconcileAsync(V1Alpha1BackupRepo entity,
        CancellationToken cancellationToken)
    {
        await EnsurePvcAsync(entity, cancellationToken).ConfigureAwait(false);

        return ReconciliationResult<V1Alpha1BackupRepo>.Success(entity);
    }

    public Task<ReconciliationResult<V1Alpha1BackupRepo>> DeletedAsync(V1Alpha1BackupRepo entity,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(ReconciliationResult<V1Alpha1BackupRepo>.Success(entity));
    }

    private async Task<V1PersistentVolumeClaim?> EnsurePvcAsync(V1Alpha1BackupRepo repo,
        CancellationToken cancellationToken)
    {
        if (repo is not { Spec: { Type: BackupRepoType.PersistentVolumeClaim, PersistentVolumeClaim: { } spec } })
        {
            return null;
        }

        var pvc = await client.GetAsync<V1PersistentVolumeClaim>(spec.Name, repo.Namespace(), cancellationToken)
            .ConfigureAwait(false);

        if (pvc is null)
        {
            pvc = new V1PersistentVolumeClaim
            {
                Metadata = new V1ObjectMeta { Name = repo.Name(), NamespaceProperty = repo.Namespace() },
                Spec = new V1PersistentVolumeClaimSpec
                {
                    AccessModes = ["ReadWriteOnce"],
                    StorageClassName = spec.StorageClassName,
                    Resources = new V1VolumeResourceRequirements
                    {
                        Requests = new Dictionary<string, ResourceQuantity> { { "storage", spec.Size } }
                    }
                }
            };

            pvc.WithOwnerReference(repo);

            pvc = await client.CreateAsync(pvc, cancellationToken).ConfigureAwait(false);
        }
        else if (pvc.GetOwnerReference(repo) is not null)
        {
            pvc.Spec.Resources.Requests = new Dictionary<string, ResourceQuantity> { { "storage", spec.Size } };

            pvc = await client.UpdateAsync(pvc, cancellationToken).ConfigureAwait(false);
        }

        return pvc;
    }
}