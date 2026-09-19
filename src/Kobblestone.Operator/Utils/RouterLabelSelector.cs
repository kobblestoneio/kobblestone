using k8s;
using k8s.Models;

using KubeOps.Abstractions.Entities;
using KubeOps.KubernetesClient.Selectors;

namespace Kobblestone.Operator.Utils;

public class RouterLabelSelector<T> : IEntityLabelSelector<T> where T : IKubernetesObject<V1ObjectMeta>
{
    public ValueTask<string?> GetLabelSelectorAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult<string?>(new ExistsLabelSelector("kobblestone.io/router"));
}