using k8s;
using k8s.Models;

using Kobblestone.Sessions;

using KubeOps.Abstractions.Entities;
using KubeOps.KubernetesClient.Selectors;

namespace Kobblestone.Operator.Utils;

public class AccountLabelSelector<T> : IEntityLabelSelector<T> where T : IKubernetesObject<V1ObjectMeta>
{
    public ValueTask<string?> GetLabelSelectorAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult<string?>(new ExistsLabelSelector(AccountLabels.Account));
}
