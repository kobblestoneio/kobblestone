using k8s.Models;

using KubeOps.Abstractions.Entities;
using KubeOps.Abstractions.Entities.Attributes;

namespace Kobblestone.Operator.Entities.V1Alpha1;

[Description("""
             A `Restore` restores the data of a `Backup` into a `Server`.

             The restore waits until the `Server` is in phase `Stopped` and keeps it stopped while it runs.
             `Backup`s and `Restore`s of the same `Server` run one after another, in the order they were created.
             """)]
[KubernetesEntity(Group = "kobblestone.io", ApiVersion = "v1alpha1", Kind = "Restore")]
public sealed class V1Alpha1Restore
    : CustomKubernetesEntity<V1Alpha1Restore.V1RestoreSpec, V1Alpha1Restore.V1Alpha1RestoreStatus>
{
    public record V1RestoreSpec
    {
        [Description("""
                     Reference to the `Backup` to restore.
                     """)]
        [Required]
        public V1LocalObjectReference BackupRef { get; set; } = null!;

        [Description("""
                     Reference to the target `Server`.

                     The `Server` must have storage configured.
                     """)]
        [Required]
        public V1TypedLocalObjectReference TargetRef { get; set; } = null!;

        [Description("""
                     Path inside the server's data volume to restore into.

                     Default is the root of the volume.
                     """)]
        public string? Path { get; set; }

        [Description("""
                     What happens if the target already contains data.

                     Default is `Fail`.

                     Values:
                     - `Fail`: The restore fails if the target is not empty.
                     - `Overwrite`: The existing data is replaced by the data of the backup.

                     **Warning**: With `Overwrite`, the existing data is deleted. Create a `Backup` first if you might still need it.
                     """)]
        public RestoreExistingDataPolicy? ExistingDataPolicy { get; set; }

        [Description("""
                     Configures the spec of the managed `Job` object.
                     """)]
        public V1Alpha1RestoreJob? Job { get; set; }
    }

    public record V1Alpha1RestoreStatus
    {
        [Description("""
                     Current phase of the `Restore`.

                     Values:
                     - `Pending`: The `Restore` is waiting to be started, or its `Pod` is starting.
                     - `Running`: The `Restore` is currently running.
                     - `Completed`: The `Restore` completed successfully.
                     - `Failed`: The `Restore` failed.
                     """)]
        [AdditionalPrinterColumn(name: "Status")]
        public RestorePhase Phase { get; set; }

        [Description("""
                     Message explaining the current state of the `Restore`.
                     """)]
        [AdditionalPrinterColumn(name: "Message")]
        public string? Message { get; set; }

        [Description("""
            Time at which the `Job` of the `Restore` was created.
            """)]
        public DateTime? StartedAt { get; set; }
    }

    public record V1Alpha1RestoreJob
    {
        [Description("""
                     Compute resources of the restore `Pod`.
                     """)]
        public V1ResourceRequirements? Resources { get; set; }

        [Description("""
                     Maximum duration of the restore `Job` in seconds. When it is exceeded, the `Job` and thus the `Restore` fails.

                     Default is no limit.
                     """)]
        public int? ActiveDeadlineSeconds { get; set; }

        [Description("""
                     Custom image for the restore containers. It must provide `sh`, `ls`, `tar` and `mv`.

                     Default is `ubuntu`.
                     """)]
        public string? Image { get; set; }

        [Description("""
                     Tolerations of the restore `Pod`.
                     """)]
        public IList<V1Toleration>? Tolerations { get; set; }
    }
}
