using k8s;
using k8s.Models;

using Kobblestone.Operator.Controller;

using KubeOps.Abstractions.Entities;
using KubeOps.KubernetesClient.Selectors;

namespace Kobblestone.Operator.Utils;

public class BotLabelSelector<T> : IEntityLabelSelector<T> where T : IKubernetesObject<V1ObjectMeta>
{
    public ValueTask<string?> GetLabelSelectorAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult<string?>(new ExistsLabelSelector(BotController.BotLabel));
}
