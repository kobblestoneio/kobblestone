using k8s;
using k8s.Models;

namespace Kobblestone.Operator.Utils;

public static class OwnershipExtensions
{
    /// <summary>
    /// Whether <paramref name="owner"/> is an owner of <paramref name="resource"/>. Kobblestone only uses and changes
    /// resources it created for an owner, never resources of others that happen to have the expected name.
    /// </summary>
    public static bool IsOwnedBy(this IKubernetesObject<V1ObjectMeta> resource, IKubernetesObject<V1ObjectMeta> owner) =>
        resource.OwnerReferences()?.Any(o => o.Uid == owner.Uid()) is true;
}
