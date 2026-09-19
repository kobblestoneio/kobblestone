using k8s.Models;

using KubeOps.Abstractions.Entities;
using KubeOps.Abstractions.Entities.Attributes;

namespace Kobblestone.Operator.Entities.V1Alpha1;

[KubernetesEntity(Group = "kobblestone.io", ApiVersion = "v1alpha1", Kind = "Restore")]
public sealed class
    V1Alpha1Restore : CustomKubernetesEntity<V1Alpha1Restore.V1RestoreSpec, V1Alpha1Restore.V1Alpha1RestoreStatus>
{
    public record V1RestoreSpec
    {
        [Required] public V1LocalObjectReference BackupRef { get; set; } = null!;

        [Required] public V1TypedLocalObjectReference TargetRef { get; set; } = null!;

        public string? Path { get; set; }

        public RestoreExistingDataPolicy? ExistingDataPolicy { get; set; }

        public V1Alpha1RestoreJob? Job { get; set; }
    }

    public record V1Alpha1RestoreStatus
    {
        [AdditionalPrinterColumn(name: "Status")]
        public RestorePhase Phase { get; set; }

        [AdditionalPrinterColumn(name: "Message")]
        public string? Message { get; set; }
    }

    public record V1Alpha1RestoreJob
    {
        public V1ResourceRequirements? Resources { get; set; }

        public int? ActiveDeadlineSeconds { get; set; }

        public string? Image { get; set; }

        public IList<V1Toleration>? Tolerations { get; set; }
    }
}