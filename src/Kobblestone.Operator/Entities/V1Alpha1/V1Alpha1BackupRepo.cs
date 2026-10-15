using k8s.Models;

using KubeOps.Abstractions.Entities;
using KubeOps.Abstractions.Entities.Attributes;

namespace Kobblestone.Operator.Entities.V1Alpha1;

[Description("""
             A `BackupRepo` defines where `Backup`s store their snapshots.

             Types:
             - `PersistentVolumeClaim`: Snapshots are stored in a volume in the same namespace.
             - `S3`: Snapshots are uploaded to an S3-compatible bucket.

             **Note**: A volume that does not support `ReadWriteMany` can only be used by one backup, restore or snapshot cleanup at a time. Others wait until it is free.
             """)]
[KubernetesEntity(Group = "kobblestone.io", ApiVersion = "v1alpha1", Kind = "BackupRepo")]
public sealed class
    V1Alpha1BackupRepo : CustomKubernetesEntity<V1Alpha1BackupRepo.V1Alpha1BackupRepoSpec,
    V1Alpha1BackupRepo.V1Alpha1BackupRepoStatus>
{
    public record V1Alpha1BackupRepoSpec
    {
        [Description("""
                     Type of the repository.

                     Values:
                     - `PersistentVolumeClaim`: Configured via `persistentVolumeClaim`.
                     - `S3`: Configured via `s3`.
                     """)]
        [Required]
        [AdditionalPrinterColumn(name: "Type")]
        public BackupRepoType Type { get; set; }

        [Description("""
                     Configures the volume. Required for type `PersistentVolumeClaim`.
                     """)]
        public V1Alpha1BackupRepoPersistentVolumeClaim? PersistentVolumeClaim { get; set; }

        [Description("""
                     Configures the bucket. Required for type `S3`.
                     """)]
        public V1Alpha1BackupRepoS3? S3 { get; set; }
    }

    public record V1Alpha1BackupRepoStatus
    {
        [Description("""
                     Conditions of the `BackupRepo`.
                     """)]
        public IList<V1Condition> Conditions { get; set; } = [];
    }

    public record V1Alpha1BackupRepoPersistentVolumeClaim
    {
        [Description("""
                     Name of the `PersistentVolumeClaim`. It is created if it does not exist.
                     """)]
        [Required]
        [AdditionalPrinterColumn(name: "Claim")]
        public string Name { get; set; } = null!;

        [Description("""
                     Size of the `PersistentVolumeClaim`, e.g. `50Gi`.

                     Required if the claim does not exist yet. Only applied to claims created by the operator.
                     """)]
        public string? Size { get; set; }

        [Description("""
                     Storage class used if the operator creates the claim.

                     Default is the default storage class of the cluster.
                     """)]
        public string? StorageClassName { get; set; }

        [Description("""
                     Path inside the volume under which snapshots are stored.

                     Default is the root of the volume.
                     """)]
        public string? SubPath { get; set; }
    }

    public record V1Alpha1BackupRepoS3
    {
        [Description("""
                     URL of the S3 endpoint.
                     """)]
        [Required] public string Endpoint { get; set; } = null!;

        [Description("""
                     Name of the bucket.
                     """)]
        [Required] public string Bucket { get; set; } = null!;

        [Description("""
                     Reference to a `Secret` containing the credentials in the keys `access-key` and `secret-key`.
                     """)]
        [Required] public V1LocalObjectReference CredentialsSecretRef { get; set; } = null!;
    }
}