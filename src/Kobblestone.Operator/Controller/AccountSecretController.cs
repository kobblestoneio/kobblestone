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
/// Watches the session and credentials `Secret`s of `Account`s, which are written by the auth `Job`. It does not
/// write any status itself: it only queues the `Account`, whose controller derives its status from the `Secret`s, and
/// the `Bot`s using the `Account`, which are rolled out with the new session.
/// </summary>
[GenericRbac(Groups = [V1Secret.KubeGroup], Resources = [V1Secret.KubePluralName], Verbs = RbacVerb.Watch)]
[EntityRbac(typeof(V1Alpha1Account), Verbs = RbacVerb.Get)]
[EntityRbac(typeof(V1Alpha1Bot), Verbs = RbacVerb.List | RbacVerb.Get)]
public sealed class AccountSecretController(
    IKubernetesClient client,
    EntityQueue<V1Alpha1Account> queueAccount,
    EntityQueue<V1Alpha1Bot> queueBot) : IEntityController<V1Secret>
{
    public async Task<ReconciliationResult<V1Secret>> ReconcileAsync(V1Secret entity,
        CancellationToken cancellationToken)
    {
        await QueueAccountAsync(entity, cancellationToken).ConfigureAwait(false);

        return ReconciliationResult<V1Secret>.Success(entity);
    }

    public async Task<ReconciliationResult<V1Secret>> DeletedAsync(V1Secret entity,
        CancellationToken cancellationToken)
    {
        // Recreated by the AccountController.
        await QueueAccountAsync(entity, cancellationToken).ConfigureAwait(false);

        return ReconciliationResult<V1Secret>.Success(entity);
    }

    private async Task QueueAccountAsync(V1Secret secret, CancellationToken cancellationToken)
    {
        if (secret.GetLabel(AccountLabels.Account) is { } accountName)
        {
            await queueAccount.RequeueAsync(client, accountName, secret.Namespace(), cancellationToken)
                .ConfigureAwait(false);

            // Only the session Secret (named like the Account) is relevant for Bots.
            if (secret.Name() != accountName)
            {
                return;
            }

            var bots = await client.ListAsync<V1Alpha1Bot>(secret.Namespace(), cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            foreach (var bot in bots.Where(b => b.Spec.Auth?.AccountRef.Name == accountName))
            {
                await queueBot.RequeueAsync(client, bot.Name(), bot.Namespace(), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }
}
