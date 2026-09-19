using k8s.Models;

using KubeOps.Abstractions.Entities;
using KubeOps.Abstractions.Entities.Attributes;

namespace Kobblestone.Operator.Entities.V1Alpha1;

[KubernetesEntity(Group = "kobblestone.io", ApiVersion = "v1alpha1", Kind = "BackupRepo")]
public sealed class
    V1Alpha1BackupRepo : CustomKubernetesEntity<V1Alpha1BackupRepo.V1Alpha1BackupRepoSpec,
    V1Alpha1BackupRepo.V1Alpha1BackupRepoStatus>
{
    public record V1Alpha1BackupRepoSpec
    {
        [Required]
        [AdditionalPrinterColumn(name: "Type")]
        public BackupRepoType Type { get; set; }

        public V1Alpha1BackupRepoPersistentVolumeClaim? PersistentVolumeClaim { get; set; }

        public V1Alpha1BackupRepoS3? S3 { get; set; }
    }

    public record V1Alpha1BackupRepoStatus
    {
        public IList<V1Condition> Conditions { get; set; } = [];
    }

    public record V1Alpha1BackupRepoPersistentVolumeClaim
    {
        [Required]
        [AdditionalPrinterColumn(name: "Claim")]
        public string Name { get; set; } = null!;

        public string? Size { get; set; }

        public string? StorageClassName { get; set; }

        public string? SubPath { get; set; }
    }

    public record V1Alpha1BackupRepoS3
    {
        [Required] public string Endpoint { get; set; } = null!;

        [Required] public string Bucket { get; set; } = null!;

        [Required] public V1LocalObjectReference CredentialsSecretRef { get; set; } = null!;
    }
}