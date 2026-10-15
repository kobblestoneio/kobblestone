using k8s.Models;

using Kobblestone.Operator.Conditions;
using Kobblestone.Operator.Constants;
using Kobblestone.Operator.Entities.V1Alpha1;
using Kobblestone.Operator.Finalizer;
using Kobblestone.Operator.Utils;

using KubeOps.Abstractions.Entities;
using KubeOps.Abstractions.Events;
using KubeOps.Abstractions.Rbac;
using KubeOps.Abstractions.Reconciliation;
using KubeOps.Abstractions.Reconciliation.Controller;
using KubeOps.Abstractions.Reconciliation.Finalizer;
using KubeOps.KubernetesClient;

namespace Kobblestone.Operator.Controller;

[EntityRbac(typeof(V1Alpha1Backup), Verbs = RbacVerb.Watch | RbacVerb.Get | RbacVerb.List | RbacVerb.Update)]
[EntityRbac(typeof(V1Alpha1Restore), Verbs = RbacVerb.List)]
[EntityRbac(typeof(V1Alpha1BackupRepo), Verbs = RbacVerb.Get)]
[EntityRbac(typeof(V1Alpha1Server), Verbs = RbacVerb.Get)]
[GenericRbac(Groups = [V1PersistentVolumeClaim.KubeGroup], Resources = [V1PersistentVolumeClaim.KubePluralName],
    Verbs = RbacVerb.Get)]
[GenericRbac(Groups = [V1Job.KubeGroup], Resources = [V1Job.KubePluralName],
    Verbs = RbacVerb.Get | RbacVerb.List | RbacVerb.Create)]
public sealed class BackupController(
    IKubernetesClient client,
    EntityFinalizerAttacher<BackupFinalizer, V1Alpha1Backup> finalizer,
    EventPublisher emitEvent) : IEntityController<V1Alpha1Backup>
{
    public async Task<ReconciliationResult<V1Alpha1Backup>> ReconcileAsync(V1Alpha1Backup entity,
        CancellationToken cancellationToken)
    {
        // A finished backup is never touched again.
        if (entity.Status.Phase is BackupPhase.Completed or BackupPhase.Failed)
        {
            return ReconciliationResult<V1Alpha1Backup>.Success(entity);
        }

        entity = await finalizer(entity, cancellationToken).ConfigureAwait(false);

        var job = await client.GetAsync<V1Job>(entity.Name(), entity.Namespace(), cancellationToken)
            .ConfigureAwait(false);

        if (job is not null)
        {
            if (job.GetLabel(ServerOperations.BackupLabel) != entity.Name())
            {
                await emitEvent(entity,
                    "JobNameConflict",
                    $"A Job named {entity.Name()} already exists and does not belong to this Backup.",
                    EventType.Warning, cancellationToken).ConfigureAwait(false);

                return ReconciliationResult<V1Alpha1Backup>.Success(entity, TimeSpan.FromSeconds(10));
            }

            // The backup was started. Its status follows the Job, the BackupJobController queues this backup
            // whenever the Job changes.
            return await SyncStatusWithJobAsync(entity, job, cancellationToken).ConfigureAwait(false);
        }

        if (entity.Status.StartedAt is not null)
        {
            // The backup was started, but its Job is gone before it finished: it was deleted by hand. Fail the backup
            // instead of creating a new Job for a backup the user evidently wanted to abort.
            return await UpdateStatusAsync(entity, BackupPhase.Failed, "Job was deleted before it finished.",
                BackupConditions.Failed.WithReason("JobDeleted"), cancellationToken).ConfigureAwait(false);
        }

        var repo = await client
            .GetAsync<V1Alpha1BackupRepo>(entity.Spec.RepoRef.Name, entity.Namespace(), cancellationToken)
            .ConfigureAwait(false);

        if (repo is null)
        {
            await emitEvent(entity,
                "BackupRepoNotFound",
                $"BackupRepo {entity.Spec.RepoRef.Name} not found.",
                EventType.Warning, cancellationToken).ConfigureAwait(false);

            return ReconciliationResult<V1Alpha1Backup>.Success(entity, TimeSpan.FromSeconds(10));
        }

        var server = await client
            .GetAsync<V1Alpha1Server>(entity.Spec.TargetRef.Name, entity.Namespace(), cancellationToken)
            .ConfigureAwait(false);

        if (server is null)
        {
            await emitEvent(entity,
                "ServerNotFound",
                $"Server {entity.Spec.TargetRef.Name} not found.",
                EventType.Warning, cancellationToken).ConfigureAwait(false);

            return ReconciliationResult<V1Alpha1Backup>.Success(entity, TimeSpan.FromSeconds(10));
        }

        if (server.Spec.Storage is null)
        {
            await emitEvent(entity,
                "ServerHasNoStorage",
                $"Server {server.Name()} has no storage configured.",
                EventType.Warning, cancellationToken).ConfigureAwait(false);

            return ReconciliationResult<V1Alpha1Backup>.Success(entity, TimeSpan.FromSeconds(10));
        }

        if (!server.HasCondition(ServerConditions.RconAvailable))
        {
            await emitEvent(entity,
                "RconNotAvailable",
                $"RCON not available on server {server.Name()}.",
                EventType.Warning, cancellationToken).ConfigureAwait(false);

            return ReconciliationResult<V1Alpha1Backup>.Success(entity, TimeSpan.FromSeconds(10));
        }

        if (!server.HasAnyCondition(ServerConditions.Running, ServerConditions.Stopped))
        {
            await emitEvent(entity,
                "ServerNotReady",
                $"Server {server.Name()} must be in phase Running or Stopped to be ready for backup.",
                EventType.Normal, cancellationToken).ConfigureAwait(false);

            return ReconciliationResult<V1Alpha1Backup>.Success(entity, TimeSpan.FromSeconds(10));
        }

        var operations = await client.GetServerOperationsAsync(server.Namespace(), server.Name(), cancellationToken)
            .ConfigureAwait(false);

        if (operations.GetBlocker(ServerOperationKind.Backup, entity.Name()) is { } blocker)
        {
            var message = blocker.IsActive
                ? $"Waiting for {blocker} to finish."
                : $"Waiting for {blocker}, which was requested earlier.";

            if (entity.Status.Message != message)
            {
                entity.Status.Message = message;

                entity = await client.UpdateStatusAsync(entity, cancellationToken).ConfigureAwait(false);
            }

            await emitEvent(entity,
                "WaitingForServer",
                $"Server {server.Name()} is busy. {message}",
                EventType.Normal, cancellationToken).ConfigureAwait(false);

            return ReconciliationResult<V1Alpha1Backup>.Success(entity, TimeSpan.FromSeconds(10));
        }

        // Held until the Job has been created, so no other reconcile can claim the repo in between.
        using var repoLock = await BackupRepoUsage.LockAsync(repo, cancellationToken).ConfigureAwait(false);

        if (await client.FindJobBlockingRepoAsync(repo, cancellationToken).ConfigureAwait(false) is { } repoJob)
        {
            var message = $"Waiting for Job {repoJob.Name()} to release BackupRepo {repo.Name()}.";

            if (entity.Status.Message != message)
            {
                entity.Status.Message = message;

                entity = await client.UpdateStatusAsync(entity, cancellationToken).ConfigureAwait(false);
            }

            await emitEvent(entity,
                "WaitingForBackupRepo",
                $"BackupRepo {repo.Name()} cannot be mounted by more than one Job at a time. {message}",
                EventType.Normal, cancellationToken).ConfigureAwait(false);

            return ReconciliationResult<V1Alpha1Backup>.Success(entity, TimeSpan.FromSeconds(10));
        }

        job = new V1Job { Metadata = new V1ObjectMeta(), Spec = new V1JobSpec { Template = new V1PodTemplateSpec() } };

        ApplyJob(job, entity, server, repo);

        await client.CreateAsync(job, cancellationToken).ConfigureAwait(false);

        // From here on, the BackupJobController queues this backup and its server whenever the Job changes.
        var before = EntityQueueExtensions.Snapshot(entity.Status);

        entity.Status.StartedAt = DateTime.UtcNow;
        entity.Status.Repo = entity.Spec.RepoRef.Name;
        entity.Status.Target = entity.Spec.TargetRef.Name;

        return await UpdateStatusAsync(entity, BackupPhase.Pending, "Backup Job created.",
            null, cancellationToken, before).ConfigureAwait(false);
    }

    /// <summary>
    /// Derives the status of a started backup from its Job.
    /// </summary>
    private Task<ReconciliationResult<V1Alpha1Backup>> SyncStatusWithJobAsync(V1Alpha1Backup entity, V1Job job,
        CancellationToken cancellationToken)
    {
        var before = EntityQueueExtensions.Snapshot(entity.Status);

        // Covers Jobs that were created before the backup status could be updated (e.g. the operator restarted).
        entity.Status.StartedAt ??= job.Metadata.CreationTimestamp ?? DateTime.UtcNow;
        entity.Status.Repo = entity.Spec.RepoRef.Name;
        entity.Status.Target = entity.Spec.TargetRef.Name;

        var conditions = job.Status?.Conditions;

        if (conditions?.Any(c => c.Type is "Complete" && c.Status is "True") is true)
        {
            return UpdateStatusAsync(entity, BackupPhase.Completed, "Completed successfully.",
                BackupConditions.Completed, cancellationToken, before);
        }

        if (conditions?.FirstOrDefault(c => c.Type is "Failed" && c.Status is "True") is { } failed)
        {
            return UpdateStatusAsync(entity, BackupPhase.Failed, failed.Message ?? "Job failed.",
                BackupConditions.Failed.WithReason(failed.Reason ?? "JobFailed"), cancellationToken, before);
        }

        if (job.Status?.Ready > 0)
        {
            return UpdateStatusAsync(entity, BackupPhase.Running, "Actively running.",
                BackupConditions.Progressing.WithReason("Running").WithMessage("Backup is running."),
                cancellationToken, before);
        }

        return UpdateStatusAsync(entity, BackupPhase.Pending, "Waiting for the backup Pod to start.", null,
            cancellationToken, before);
    }

    /// <summary>
    /// Sets phase, message and the condition matching the phase (all other backup conditions are set to false) and
    /// writes the status, but only if anything changed compared to <paramref name="before"/> (default: the status as
    /// passed in).
    /// </summary>
    private async Task<ReconciliationResult<V1Alpha1Backup>> UpdateStatusAsync(V1Alpha1Backup entity,
        BackupPhase phase, string message, V1Condition? condition, CancellationToken cancellationToken,
        string? before = null)
    {
        before ??= EntityQueueExtensions.Snapshot(entity.Status);

        entity.Status.Phase = phase;
        entity.Status.Message = message;

        var conditions = new[]
        {
            BackupConditions.Completed.False(), BackupConditions.Failed.False(), BackupConditions.Progressing.False()
        };

        entity.WithConditions(
            conditions.Select(c => condition is not null && c.Type == condition.Type ? condition : c));

        if (EntityQueueExtensions.Snapshot(entity.Status) != before)
        {
            entity = await client.UpdateStatusAsync(entity, cancellationToken).ConfigureAwait(false);
        }

        return ReconciliationResult<V1Alpha1Backup>.Success(entity);
    }

    public Task<ReconciliationResult<V1Alpha1Backup>> DeletedAsync(V1Alpha1Backup entity,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(ReconciliationResult<V1Alpha1Backup>.Success(entity));
    }

    private static void ApplyJob(V1Job job, V1Alpha1Backup backup, V1Alpha1Server server, V1Alpha1BackupRepo repo)
    {
        job.Metadata.Name = backup.Name();
        job.Metadata.NamespaceProperty = backup.Namespace();
        // Online: the server is running and keeps its volume attached. The backup reads the data next to it after
        // flushing the world via RCON. Offline: the server is stopped (and held stopped by the ServerController while
        // this Job is active), so the volume is free and the backup can run on any node.
        var online = server.Status.Phase is ServerPhase.Running;

        var labels = job.Metadata.EnsureLabels();
        labels[ServerOperations.BackupLabel] = backup.Name();
        labels[ServerOperations.TargetServerLabel] = server.Name();
        labels[ServerOperations.BackupModeLabel] =
            online ? ServerOperations.BackupModeOnline : ServerOperations.BackupModeOffline;

        // Pods do not inherit the labels of their Job. This label is required by the Server's NetworkPolicy to
        // allow RCON access from the backup Pod.
        job.Spec.Template.Metadata = new V1ObjectMeta
        {
            Labels = new Dictionary<string, string> { { ServerOperations.BackupLabel, backup.Name() } }
        };

        job.Spec.BackoffLimit = 0;
        job.Spec.ActiveDeadlineSeconds = backup.Spec.Job?.ActiveDeadlineSeconds;

        var mcBackupContainer = new V1Container
        {
            Name = "backup",
            Image = backup.Spec.Job?.Image ?? ContainerImages.Backup,
            Env = new List<V1EnvVar>
            {
                new() { Name = "INITIAL_DELAY", Value = "0" },
                new() { Name = "DEST_DIR", Value = $"/mnt/backups/{backup.Uid()}" },
                new() { Name = "PRUNE_BACKUPS_DAYS", Value = "-1" },
                new() { Name = "BACKUP_INTERVAL", Value = "0" },
                new() { Name = "RCON_HOST", Value = $"{server.Name()}.{server.Namespace()}.svc.cluster.local" },
                new()
                {
                    Name = "RCON_PORT",
                    Value = (server.Spec.Rcon?.Port ?? MinecraftConstants.DefaultRconPort).ToString()
                },
                new() { Name = "RCON_PASSWORD_FILE", Value = "/secrets/rcon-password" }
            },
            VolumeMounts =
            [
                new V1VolumeMount { Name = "data", ReadOnlyProperty = true, MountPath = "/data" },
                new V1VolumeMount
                {
                    Name = "backups", MountPath = "/mnt/backups", SubPath = repo.Spec.PersistentVolumeClaim?.SubPath
                },
                new V1VolumeMount { Name = "secret", MountPath = "/secrets", ReadOnlyProperty = true }
            ]
        };

        var copyContainer = new V1Container
        {
            Name = "backup",
            Image = ContainerImages.Ubuntu,
            Command =
            [
                "/bin/sh", "-c", $"tar czf /mnt/backups/{backup.Uid()}/world.tar.gz -C /mnt/data ."
            ],
            VolumeMounts =
            [
                new V1VolumeMount { Name = "data", ReadOnlyProperty = true, MountPath = "/mnt/data" },
                new V1VolumeMount
                {
                    Name = "backups", MountPath = "/mnt/backups", SubPath = repo.Spec.PersistentVolumeClaim?.SubPath
                }
            ]
        };

        var uploadContainer = new V1Container
        {
            Name = "upload",
            Image = ContainerImages.AwsCli,
            Command =
            [
                "/bin/sh",
                "-c",
                $"""
                     set -e

                     BACKUP_FILE=$(ls /mnt/backups/{backup.Uid()}/*.tar.gz 2>/dev/null | head -1)
                     if [ -z "$BACKUP_FILE" ]; then
                     echo "ERROR: no tar.gz found in /backups/{backup.Uid()}/"
                     exit 1
                     fi

                     echo "INFO: uploading $BACKUP_FILE to S3..."
                     aws s3 cp "$BACKUP_FILE" s3://{repo.Spec.S3?.Bucket}/{backup.Uid()}.tar.gz
                     echo "INFO: upload complete."
                     """.ReplaceLineEndings("\n")
            ],
            VolumeMounts =
            [
                new V1VolumeMount
                {
                    Name = "backups",
                    MountPath = "/mnt/backups",
                    SubPath = repo.Spec.PersistentVolumeClaim?.SubPath,
                    ReadOnlyProperty = true
                }
            ],
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
                            Optional = false
                        }
                    }
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
                            Optional = false
                        }
                    }
                }
            ]
        };

        job.Spec.Template.Spec = new V1PodSpec
        {
            RestartPolicy = "Never",
            Resources = backup.Spec.Job?.Resources,
            Tolerations = backup.Spec.Job?.Tolerations,
            Volumes =
            [
                new V1Volume
                {
                    Name = "data",
                    PersistentVolumeClaim =
                        new V1PersistentVolumeClaimVolumeSource { ClaimName = server.Name(), ReadOnlyProperty = true }
                },
                new V1Volume
                {
                    Name = "secret",
                    Secret = new V1SecretVolumeSource { SecretName = server.Name(), Optional = false }
                }
            ],
            InitContainers = [],
            Containers = []
        };

        if (online)
        {
            // The server's volume is `ReadWriteOnce` and attached to the node of the running server Pod. A second Pod
            // can only mount it on that same node, anywhere else it fails with a Multi-Attach error.
            job.Spec.Template.Spec.Affinity = new V1Affinity
            {
                PodAffinity = new V1PodAffinity
                {
                    RequiredDuringSchedulingIgnoredDuringExecution =
                    [
                        new V1PodAffinityTerm
                        {
                            LabelSelector = new V1LabelSelector
                            {
                                MatchLabels = new Dictionary<string, string>
                                {
                                    { "kobblestone.io/server", server.Name() }
                                }
                            },
                            TopologyKey = "kubernetes.io/hostname"
                        }
                    ]
                }
            };
        }

        switch (repo.Spec.Type)
        {
            case BackupRepoType.PersistentVolumeClaim:
                job.Spec.Template.Spec.Containers.Add(online ? mcBackupContainer : copyContainer);

                job.Spec.Template.Spec.Volumes.Add(new V1Volume
                {
                    Name = "backups",
                    PersistentVolumeClaim = new V1PersistentVolumeClaimVolumeSource
                    {
                        ClaimName = repo.Spec.PersistentVolumeClaim?.Name
                    }
                });
                break;
            case BackupRepoType.S3:
                job.Spec.Template.Spec.InitContainers.Add(online ? mcBackupContainer : copyContainer);

                job.Spec.Template.Spec.Containers.Add(uploadContainer);

                job.Spec.Template.Spec.Volumes.Add(new V1Volume
                {
                    Name = "backups",
                    EmptyDir = new V1EmptyDirVolumeSource()
                });
                break;
        }

        job.WithOwnerReference(backup);
    }
}