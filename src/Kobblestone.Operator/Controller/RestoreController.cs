using k8s.Models;

using Kobblestone.Operator.Constants;
using Kobblestone.Operator.Entities.V1Alpha1;
using Kobblestone.Operator.Utils;

using KubeOps.Abstractions.Entities;
using KubeOps.Abstractions.Events;
using KubeOps.Abstractions.Rbac;
using KubeOps.Abstractions.Reconciliation;
using KubeOps.Abstractions.Reconciliation.Controller;
using KubeOps.KubernetesClient;

namespace Kobblestone.Operator.Controller;

[EntityRbac(
    typeof(V1Alpha1Restore),
    Verbs = RbacVerb.Watch | RbacVerb.Get | RbacVerb.List | RbacVerb.Update
)]
[EntityRbac(typeof(V1Alpha1Backup), Verbs = RbacVerb.Get | RbacVerb.List)]
[EntityRbac(typeof(V1Alpha1BackupRepo), Verbs = RbacVerb.Get)]
[EntityRbac(typeof(V1Alpha1Server), Verbs = RbacVerb.Get)]
[GenericRbac(
    Groups = [V1PersistentVolumeClaim.KubeGroup],
    Resources = [V1PersistentVolumeClaim.KubePluralName],
    Verbs = RbacVerb.Get
)]
[GenericRbac(
    Groups = [V1Job.KubeGroup],
    Resources = [V1Job.KubePluralName],
    Verbs = RbacVerb.Get | RbacVerb.List | RbacVerb.Create
)]
public sealed class RestoreController(IKubernetesClient client, EventPublisher emitEvent)
    : IEntityController<V1Alpha1Restore>
{
    /// <summary>
    /// The restore cannot proceed yet. This is not an error of the operator: the reason is reported via the status
    /// message (current state) and an event (history), and the restore is retried later because the cluster state might
    /// have changed by then.
    /// </summary>
    private async Task<ReconciliationResult<V1Alpha1Restore>> RequeueAsync(
        V1Alpha1Restore entity,
        string reason,
        string message,
        EventType type,
        TimeSpan requeueAfter,
        CancellationToken cancellationToken
    )
    {
        if (entity.Status.Message != message)
        {
            entity.Status.Message = message;

            entity = await client
                .UpdateStatusAsync(entity, cancellationToken)
                .ConfigureAwait(false);
        }

        await emitEvent(entity, reason, message, type, cancellationToken).ConfigureAwait(false);

        return ReconciliationResult<V1Alpha1Restore>.Success(entity, requeueAfter);
    }

    public async Task<ReconciliationResult<V1Alpha1Restore>> ReconcileAsync(
        V1Alpha1Restore entity,
        CancellationToken cancellationToken
    )
    {
        // A finished restore is never touched again.
        if (entity.Status.Phase is RestorePhase.Completed or RestorePhase.Failed)
        {
            return ReconciliationResult<V1Alpha1Restore>.Success(entity);
        }

        var existingJob = await client
            .GetAsync<V1Job>(entity.Name(), entity.Namespace(), cancellationToken)
            .ConfigureAwait(false);

        if (existingJob is not null)
        {
            if (existingJob.GetLabel(ServerOperations.RestoreLabel) == entity.Name())
            {
                // The restore was started. Its status follows the Job, the RestoreJobController queues this restore
                // whenever the Job changes.
                return await SyncStatusWithJobAsync(entity, existingJob, cancellationToken)
                    .ConfigureAwait(false);
            }

            var conflictMessage =
                $"A Job named {entity.Name()} already exists and does not belong to this Restore";

            return await RequeueAsync(
                    entity,
                    "JobNameConflict",
                    conflictMessage,
                    EventType.Warning,
                    TimeSpan.FromSeconds(10),
                    cancellationToken
                )
                .ConfigureAwait(false);
        }

        if (entity.Status.StartedAt is not null)
        {
            // The restore was started, but its Job is gone before it finished: it was deleted by hand. Fail the
            // restore instead of running it again.
            return await UpdateStatusAsync(
                    entity,
                    RestorePhase.Failed,
                    "Job was deleted before it finished",
                    cancellationToken
                )
                .ConfigureAwait(false);
        }

        var backup = await client
            .GetAsync<V1Alpha1Backup>(
                entity.Spec.BackupRef.Name,
                entity.Namespace(),
                cancellationToken
            )
            .ConfigureAwait(false);

        if (backup is null)
        {
            return await RequeueAsync(
                    entity,
                    "BackupNotFound",
                    $"Backup {entity.Spec.BackupRef.Name} not found",
                    EventType.Warning,
                    TimeSpan.FromSeconds(10),
                    cancellationToken
                )
                .ConfigureAwait(false);
        }

        var repo = await client
            .GetAsync<V1Alpha1BackupRepo>(
                backup.Spec.RepoRef.Name,
                entity.Namespace(),
                cancellationToken
            )
            .ConfigureAwait(false);

        if (repo is null)
        {
            return await RequeueAsync(
                    entity,
                    "BackupRepoNotFound",
                    $"BackupRepo {backup.Spec.RepoRef.Name} not found",
                    EventType.Warning,
                    TimeSpan.FromSeconds(10),
                    cancellationToken
                )
                .ConfigureAwait(false);
        }

        V1PersistentVolumeClaim? pvc = null;
        V1Alpha1Server? server = null;

        if (entity.Spec.TargetRef.Kind is null or "Server")
        {
            server = await client
                .GetAsync<V1Alpha1Server>(
                    entity.Spec.TargetRef.Name,
                    entity.Namespace(),
                    cancellationToken
                )
                .ConfigureAwait(false);

            if (server is null)
            {
                return await RequeueAsync(
                        entity,
                        "ServerNotFound",
                        $"Server {entity.Spec.TargetRef.Name} not found",
                        EventType.Warning,
                        TimeSpan.FromSeconds(10),
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            }

            if (server.Status.Phase is not ServerPhase.Stopped)
            {
                return await RequeueAsync(
                        entity,
                        "ServerNotStopped",
                        $"Waiting for server {server.Name()} to be in phase `Stopped`",
                        EventType.Normal,
                        TimeSpan.FromSeconds(5),
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            }

            if (server.Spec.Storage is null)
            {
                return await RequeueAsync(
                        entity,
                        "ServerHasNoStorage",
                        $"Server {server.Name()} has no storage configured",
                        EventType.Warning,
                        TimeSpan.FromSeconds(10),
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            }

            var operations = await client
                .GetServerOperationsAsync(server.Namespace(), server.Name(), cancellationToken)
                .ConfigureAwait(false);

            if (operations.GetBlocker(ServerOperationKind.Restore, entity.Name()) is { } blocker)
            {
                var waitingMessage = blocker.IsActive
                    ? $"Waiting for {blocker} to finish"
                    : $"Waiting for {blocker}, which was requested earlier";

                return await RequeueAsync(
                        entity,
                        "WaitingForServer",
                        waitingMessage,
                        EventType.Normal,
                        TimeSpan.FromSeconds(5),
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            }

            pvc = await client
                .GetAsync<V1PersistentVolumeClaim>(
                    server.Name(),
                    server.Namespace(),
                    cancellationToken
                )
                .ConfigureAwait(false);
        }

        if (pvc is null)
        {
            return await RequeueAsync(
                    entity,
                    "TargetNotFound",
                    "Target could not be determined",
                    EventType.Warning,
                    TimeSpan.FromSeconds(10),
                    cancellationToken
                )
                .ConfigureAwait(false);
        }

        // Held until the Job has been created, so no other reconcile can claim the repo in between.
        using var repoLock = await BackupRepoUsage
            .LockAsync(repo, cancellationToken)
            .ConfigureAwait(false);

        if (
            await client.FindJobBlockingRepoAsync(repo, cancellationToken).ConfigureAwait(false) is
            { } repoJob
        )
        {
            return await RequeueAsync(
                    entity,
                    "WaitingForBackupRepo",
                    $"Waiting for Job {repoJob.Name()} to release BackupRepo {repo.Name()}",
                    EventType.Normal,
                    TimeSpan.FromSeconds(10),
                    cancellationToken
                )
                .ConfigureAwait(false);
        }

        var job = new V1Job
        {
            Metadata = new V1ObjectMeta
            {
                Name = entity.Name(),
                NamespaceProperty = entity.Namespace(),
                Labels = new Dictionary<string, string>
                {
                    { ServerOperations.RestoreLabel, entity.Name() },
                    { ServerOperations.TargetServerLabel, entity.Spec.TargetRef.Name },
                },
            },
            Spec = new V1JobSpec
            {
                BackoffLimit = 0,
                ActiveDeadlineSeconds = entity.Spec.Job?.ActiveDeadlineSeconds,
                Template = new V1PodTemplateSpec
                {
                    Spec = new V1PodSpec
                    {
                        RestartPolicy = "Never",
                        Resources = entity.Spec.Job?.Resources,
                        Tolerations = entity.Spec.Job?.Tolerations,
                        Volumes =
                        [
                            new V1Volume
                            {
                                Name = "data",
                                PersistentVolumeClaim = new V1PersistentVolumeClaimVolumeSource
                                {
                                    ClaimName = pvc.Name(),
                                },
                            },
                            new V1Volume
                            {
                                Name = "stage",
                                EmptyDir = new V1EmptyDirVolumeSource(),
                            },
                        ],
                        InitContainers = [],
                        Containers =
                        [
                            new V1Container
                            {
                                Name = "restore",
                                Image = entity.Spec.Job?.Image ?? ContainerImages.Ubuntu,
                                Command =
                                [
                                    "/bin/sh",
                                    "-c",
                                    $"""
                                        set -e

                                        BACKUP_FILE=$(ls /mnt/backups/{backup.Uid()}/*.tar.gz 2>/dev/null | head -1)
                                        if [ -z "$BACKUP_FILE" ]; then
                                            echo "ERROR: no tar.gz found in /mnt/backups/{backup.Uid()}/"
                                            exit 1
                                        fi

                                        echo "INFO: restoring from $BACKUP_FILE"

                                        echo "INFO: extracting backup file to staging dir..."
                                        mkdir -p /mnt/stage/data
                                        tar --strip-components=1 -xzf "$BACKUP_FILE" -C /mnt/stage/data

                                        echo "INFO: swapping data..."
                                        mkdir -p /mnt/stage/data-old
                                        if [ "$(ls -A /mnt/data 2>/dev/null)" ]; then
                                            mv /mnt/data/* /mnt/stage/data-old/
                                        fi
                                        mv /mnt/stage/data/* /mnt/data/

                                        echo "INFO: restore complete."
                                        """.ReplaceLineEndings("\n"),
                                ],
                                VolumeMounts =
                                [
                                    new V1VolumeMount
                                    {
                                        Name = "data",
                                        MountPath = "/mnt/data",
                                        SubPath = entity.Spec.Path,
                                    },
                                    new V1VolumeMount { Name = "stage", MountPath = "/mnt/stage" },
                                    new V1VolumeMount
                                    {
                                        Name = "backups",
                                        MountPath = "/mnt/backups",
                                        SubPath = repo.Spec.PersistentVolumeClaim?.SubPath,
                                        ReadOnlyProperty = true,
                                    },
                                ],
                            },
                        ],
                    },
                },
            },
        };

        var failOnExistingDataContainer = new V1Container
        {
            Name = "ensure-empty-target",
            Image = entity.Spec.Job?.Image ?? ContainerImages.Ubuntu,
            Command =
            [
                "/bin/sh",
                "-c",
                """
                    set -e

                    if [ "$(ls -A /mnt/data 2>/dev/null)" ]; then
                    echo "ERROR: Target is non-empty and policy for existing data is Fail."
                    exit 1
                    fi
                    echo "OK: Target is empty, proceeding."
                    """.ReplaceLineEndings("\n"),
            ],
            VolumeMounts =
            [
                new V1VolumeMount
                {
                    Name = "data",
                    MountPath = "/mnt/data",
                    SubPath = entity.Spec.Path,
                    ReadOnlyProperty = true,
                },
            ],
        };

        if (entity.Spec.ExistingDataPolicy is null or RestoreExistingDataPolicy.Fail)
        {
            job.Spec.Template.Spec.InitContainers.Insert(0, failOnExistingDataContainer);
        }

        if (repo.Spec.Type is BackupRepoType.S3)
        {
            var s3DownloadContainer = new V1Container
            {
                Name = "download",
                Image = ContainerImages.AwsCli,
                Command =
                [
                    "/bin/sh",
                    "-c",
                    $"aws s3 cp s3://{repo.Spec.S3?.Bucket}/{backup.Uid()}.tar.gz /mnt/backups/{backup.Uid()}/world.tar.gz",
                ],
                VolumeMounts = [new V1VolumeMount { Name = "backups", MountPath = "/mnt/backups" }],
                Env =
                [
                    new V1EnvVar { Name = "AWS_ENDPOINT_URL", Value = repo.Spec.S3?.Endpoint },
                    // Since AWS CLI 2.23.0, checksums are sent by default, which many S3-compatible providers reject.
                    new V1EnvVar { Name = "AWS_REQUEST_CHECKSUM_CALCULATION", Value = "when_required" },
                    new V1EnvVar { Name = "AWS_RESPONSE_CHECKSUM_VALIDATION", Value = "when_required" },
                    new V1EnvVar
                    {
                        Name = "AWS_ACCESS_KEY_ID",
                        ValueFrom = new V1EnvVarSource
                        {
                            SecretKeyRef = new V1SecretKeySelector
                            {
                                Name = repo.Spec.S3?.CredentialsSecretRef.Name,
                                Key = "access-key",
                                Optional = false,
                            },
                        },
                    },
                    new V1EnvVar
                    {
                        Name = "AWS_SECRET_ACCESS_KEY",
                        ValueFrom = new V1EnvVarSource
                        {
                            SecretKeyRef = new V1SecretKeySelector
                            {
                                Name = repo.Spec.S3?.CredentialsSecretRef.Name,
                                Key = "secret-key",
                                Optional = false,
                            },
                        },
                    },
                ],
            };

            job.Spec.Template.Spec.InitContainers.Add(s3DownloadContainer);

            job.Spec.Template.Spec.Volumes.Add(
                new V1Volume { Name = "backups", EmptyDir = new V1EmptyDirVolumeSource() }
            );
        }
        else
        {
            job.Spec.Template.Spec.Volumes.Add(
                new V1Volume
                {
                    Name = "backups",
                    PersistentVolumeClaim = new V1PersistentVolumeClaimVolumeSource
                    {
                        ClaimName = repo.Spec.PersistentVolumeClaim?.Name,
                        ReadOnlyProperty = true,
                    },
                }
            );
        }

        job.WithOwnerReference(entity);

        await client.CreateAsync(job, cancellationToken).ConfigureAwait(false);

        // From here on, the RestoreJobController queues this restore and its server whenever the Job changes. The
        // ServerController keeps the server stopped while the Job is active.
        var before = EntityQueueExtensions.Snapshot(entity.Status);

        entity.Status.StartedAt = DateTime.UtcNow;

        return await UpdateStatusAsync(
                entity,
                RestorePhase.Pending,
                "Restore Job created",
                cancellationToken,
                before
            )
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Derives the status of a started restore from its Job.
    /// </summary>
    private Task<ReconciliationResult<V1Alpha1Restore>> SyncStatusWithJobAsync(
        V1Alpha1Restore entity,
        V1Job job,
        CancellationToken cancellationToken
    )
    {
        var before = EntityQueueExtensions.Snapshot(entity.Status);

        // Covers Jobs that were created before the restore status could be updated (e.g. the operator restarted).
        entity.Status.StartedAt ??= job.Metadata.CreationTimestamp ?? DateTime.UtcNow;

        var conditions = job.Status?.Conditions;

        if (conditions?.Any(c => c.Type is "Complete" && c.Status is "True") is true)
        {
            return UpdateStatusAsync(
                entity,
                RestorePhase.Completed,
                "Completed successfully",
                cancellationToken,
                before
            );
        }

        if (conditions?.FirstOrDefault(c => c.Type is "Failed" && c.Status is "True") is { } failed)
        {
            return UpdateStatusAsync(
                entity,
                RestorePhase.Failed,
                failed.Message ?? "Job failed",
                cancellationToken,
                before
            );
        }

        if (job.Status?.Ready > 0)
        {
            return UpdateStatusAsync(
                entity,
                RestorePhase.Running,
                "Actively running",
                cancellationToken,
                before
            );
        }

        return UpdateStatusAsync(
            entity,
            RestorePhase.Pending,
            "Waiting for the restore Pod to start",
            cancellationToken,
            before
        );
    }

    /// <summary>
    /// Sets phase and message and writes the status, but only if anything changed compared to
    /// <paramref name="before"/> (default: the status as passed in).
    /// </summary>
    private async Task<ReconciliationResult<V1Alpha1Restore>> UpdateStatusAsync(
        V1Alpha1Restore entity,
        RestorePhase phase,
        string message,
        CancellationToken cancellationToken,
        string? before = null
    )
    {
        before ??= EntityQueueExtensions.Snapshot(entity.Status);

        entity.Status.Phase = phase;
        entity.Status.Message = message;

        if (EntityQueueExtensions.Snapshot(entity.Status) != before)
        {
            entity = await client
                .UpdateStatusAsync(entity, cancellationToken)
                .ConfigureAwait(false);
        }

        return ReconciliationResult<V1Alpha1Restore>.Success(entity);
    }

    public Task<ReconciliationResult<V1Alpha1Restore>> DeletedAsync(
        V1Alpha1Restore entity,
        CancellationToken cancellationToken
    )
    {
        return Task.FromResult(ReconciliationResult<V1Alpha1Restore>.Success(entity));
    }
}