using k8s.Models;

using Kobblestone.Operator.Conditions;
using Kobblestone.Operator.Constants;
using Kobblestone.Operator.Entities;
using Kobblestone.Operator.Entities.V1Alpha1;
using Kobblestone.Operator.Utils;

using KubeOps.Abstractions.Entities;
using KubeOps.Abstractions.Events;
using KubeOps.Abstractions.Reconciliation;
using KubeOps.Abstractions.Reconciliation.Finalizer;
using KubeOps.KubernetesClient;

namespace Kobblestone.Operator.Finalizer;

public sealed class BackupFinalizer(IKubernetesClient client, EventPublisher emitEvent)
    : IEntityFinalizer<V1Alpha1Backup>
{
    public async Task<ReconciliationResult<V1Alpha1Backup>> FinalizeAsync(V1Alpha1Backup entity,
        CancellationToken cancellationToken)
    {
        // Backup has not been started yet, so we don't need to finalize. The conditions cover backups that were started
        // before `status.startedAt` existed.
        if (entity.Status.StartedAt is null &&
            !entity.HasAnyCondition(BackupConditions.Completed, BackupConditions.Failed, BackupConditions.Progressing))
        {
            return ReconciliationResult<V1Alpha1Backup>.Success(entity);
        }

        if (entity.Spec.RetainPolicy is null or BackupRetainPolicy.Retain)
        {
            return ReconciliationResult<V1Alpha1Backup>.Success(entity);
        }

        var jobName = $"{entity.Name()}-delete";

        var job = await client.GetAsync<V1Job>(jobName, entity.Namespace(), cancellationToken).ConfigureAwait(false);

        // Job already exists, so the finalizers job is done
        if (job is not null && entity.Spec.RetainPolicy is BackupRetainPolicy.Delete)
        {
            return ReconciliationResult<V1Alpha1Backup>.Success(entity);
        }

        if (job is not null && entity.Spec.RetainPolicy is BackupRetainPolicy.RequireDelete)
        {
            var completeCondition =
                job.Status.Conditions?.FirstOrDefault(c => c.Type is "Complete" && c.Status is "True");

            var failedCondition = job.Status.Conditions?.FirstOrDefault(c => c.Type is "Failed" && c.Status is "True");

            if (completeCondition is not null)
            {
                return ReconciliationResult<V1Alpha1Backup>.Success(entity);
            }

            if (failedCondition is not null)
            {
                await emitEvent(entity,
                    "CleanupFailed",
                    $"Cleanup Job {jobName} failed. The Backup is not removed because of retainPolicy " +
                    $"RequireDelete. Delete the Job to retry the cleanup.",
                    EventType.Warning, cancellationToken).ConfigureAwait(false);

                // Requeue so that the cleanup is retried once the failed Job has been deleted.
                return ReconciliationResult<V1Alpha1Backup>.Failure(entity,
                    "Cleanup job failed. Backup will not be finalized due to retain-policy=RequireDelete.",
                    requeueAfter: TimeSpan.FromSeconds(60));
            }

            return ReconciliationResult<V1Alpha1Backup>.Failure(entity, "Waiting for cleanup job to finish.",
                requeueAfter: TimeSpan.FromSeconds(5));
        }

        var repo = await client
            .GetAsync<V1Alpha1BackupRepo>(entity.Spec.RepoRef.Name, entity.Namespace(), cancellationToken)
            .ConfigureAwait(false);

        if (repo is null)
        {
            return ReconciliationResult<V1Alpha1Backup>.Success(entity);
        }

        // Like backups and restores, the cleanup only gets a Job once the repo is free.
        using var repoLock = await BackupRepoUsage.LockAsync(repo, cancellationToken).ConfigureAwait(false);

        if (await client.FindJobBlockingRepoAsync(repo, cancellationToken).ConfigureAwait(false) is { } repoJob)
        {
            await emitEvent(entity,
                "WaitingForBackupRepo",
                $"Cleanup is waiting for Job {repoJob.Name()} to release BackupRepo {repo.Name()}.",
                EventType.Normal, cancellationToken).ConfigureAwait(false);

            // Finalizers must keep returning Failure while waiting: Success would remove the finalizer.
            return ReconciliationResult<V1Alpha1Backup>.Failure(entity,
                "Waiting for BackupRepo to be released.", requeueAfter: TimeSpan.FromSeconds(10));
        }

        var deletePvcContainer = new V1Container
        {
            Name = "delete",
            Image = ContainerImages.Ubuntu,
            Command =
            [
                "/bin/sh", "-c", $"rm -rf /backups/{entity.Uid()}"
            ],
            VolumeMounts =
            [
                new V1VolumeMount
                {
                    Name = "backups", MountPath = "/backups", SubPath = repo.Spec.PersistentVolumeClaim?.SubPath
                }
            ]
        };

        var deleteS3Container = new V1Container
        {
            Name = "delete",
            Image = ContainerImages.AwsCli,
            Command =
            [
                "/bin/sh", "-c", $"aws s3 rm s3://{repo.Spec.S3?.Bucket}/{entity.Uid()}.tar.gz"
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

        job = new V1Job
        {
            Metadata = new V1ObjectMeta { Name = jobName, NamespaceProperty = entity.Namespace() },
            Spec = new V1JobSpec
            {
                Template = new V1PodTemplateSpec
                {
                    Spec = new V1PodSpec
                    {
                        RestartPolicy = "Never",
                        Volumes = repo.Spec.Type is BackupRepoType.PersistentVolumeClaim
                            ?
                            [
                                new V1Volume
                                {
                                    Name = "backups",
                                    PersistentVolumeClaim = new V1PersistentVolumeClaimVolumeSource
                                    {
                                        ClaimName = repo.Spec.PersistentVolumeClaim?.Name
                                    }
                                }
                            ]
                            : [],
                        Containers = repo.Spec.Type is BackupRepoType.PersistentVolumeClaim
                            ? [deletePvcContainer]
                            : [deleteS3Container]
                    }
                }
            }
        };

        if (entity.Spec.RetainPolicy is BackupRetainPolicy.RequireDelete)
        {
            job.WithOwnerReference(entity);
        }

        job = await client.CreateAsync(job, cancellationToken).ConfigureAwait(false);

        if (entity.Spec.RetainPolicy is BackupRetainPolicy.Delete)
        {
            return ReconciliationResult<V1Alpha1Backup>.Success(entity);
        }

        await emitEvent(entity,
            "WaitingForCleanup",
            $"Waiting for cleanup Job {jobName} to delete the snapshot from BackupRepo {repo.Name()}.",
            EventType.Normal, cancellationToken).ConfigureAwait(false);

        // Finalizers must keep returning Failure while waiting: Success would remove the finalizer.
        return ReconciliationResult<V1Alpha1Backup>.Failure(entity,
            "Waiting for cleanup job to finish.", requeueAfter: TimeSpan.FromSeconds(5));
    }
}
