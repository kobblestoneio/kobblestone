using k8s.Models;

using KubeOps.Abstractions.Entities;
using KubeOps.Abstractions.Entities.Attributes;

namespace Kobblestone.Operator.Entities.V1Alpha1;

[Description("""
             A `Backup` defines a request for a snapshot of a `Server`.

             The snapshot includes the servers data and configs. Transient data like `.jar` files is excluded.

             Backups can be restored via the `Restore` object.

             Live backups on running servers are supported and implemented using RCON.

             **Note**: Backups are not supported for servers that do **not** have storage configured.
             """)]
[KubernetesEntity(Group = "kobblestone.io", ApiVersion = "v1alpha1", Kind = "Backup")]
public sealed class
    V1Alpha1Backup : CustomKubernetesEntity<V1Alpha1Backup.V1BackupSpec, V1Alpha1Backup.V1Alpha1BackupStatus>,
    IConditionsStatus<V1Alpha1Backup.V1Alpha1BackupStatus, V1Condition>
{
    public record V1BackupSpec
    {
        [Required]
        [Title("Repository")]
        [Description("""
                     Reference to the `BackupRepo` that will store the `Backup`.
                     """)]
        public V1LocalObjectReference RepoRef { get; set; } = null!;

        [Required]
        [Title("Target")]
        [Description("""
                     Reference to the target `Server`.
                     """)]
        public V1TypedLocalObjectReference TargetRef { get; set; } = null!;

        [Title("Job")]
        [Description("""
                     Configures the spec of the managed `Job` object.
                     """)]
        public V1Alpha1BackupJob? Job { get; set; }

        [Title("Retain Policy")]
        [Description("""
                     Policy that determines what should happen with the physical snapshot data when the `Backup` is deleted.

                     Values:
                     - `Retain`: Snapshots are retained in their `BackupRepo`.
                     - `Delete`: An attempt is made to delete the snapshot from the `BackupRepo`. The result of that deletion is ignored and not awaited.
                     - `RequireDelete`: The snapshot must be deleted from the `BackupRepo` for the `Backup` to be finally removed. Deletion is retried until success.

                     Default is `Retain`.
                     """)]
        public BackupRetainPolicy? RetainPolicy { get; set; }
    }

    public record V1Alpha1BackupStatus : IConditions<V1Condition>
    {
        [Description("""
                     Current phase of the `Backup`.

                     Values:
                     - `Pending`: `Backup` is pending.
                     - `Running`: `Backup` is currently running.
                     - `Completed`: `Backup` completed successfully.
                     - `Failed`: `Backup` failed.
                     """)]
        [AdditionalPrinterColumn(name: "Status")]
        public BackupPhase Phase { get; set; }

        [Description("""
                     Message explaining the current state of the `Backup`.
                     """)]
        [AdditionalPrinterColumn(name: "Message")]
        public string? Message { get; set; }

        [Description("""
                     Name of the target.
                     """)]
        [AdditionalPrinterColumn(name: "Target")]
        public string? Target { get; set; }

        [Description("""
                     Name of the target `BackupRepo`.
                     """)]
        [AdditionalPrinterColumn(name: "Repo")]
        public string? Repo { get; set; }

        [Description("""
                     Conditions of the `Backup`.
                     """)]
        public IList<V1Condition> Conditions { get; set; } = [];
    }

    public record V1Alpha1BackupJob
    {
        [ExternalDocs("https://kubernetes.io/docs/reference/kubernetes-api/core/pod-v1/#PodSpec")]
        public V1ResourceRequirements? Resources { get; set; }

        [ExternalDocs("https://kubernetes.io/docs/reference/kubernetes-api/batch/job-v1/#JobSpec")]
        public int? ActiveDeadlineSeconds { get; set; }

        [ExternalDocs("https://kubernetes.io/docs/reference/kubernetes-api/core/pod-v1/#Container")]
        public string? Image { get; set; }

        [ExternalDocs("https://kubernetes.io/docs/reference/kubernetes-api/core/pod-v1/#PodSpec")]
        public IList<V1Toleration> Tolerations { get; set; } = new List<V1Toleration>();
    }
}