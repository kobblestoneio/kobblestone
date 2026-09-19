using k8s.Models;

using Kobblestone.Operator.Conditions;
using Kobblestone.Operator.Constants;
using Kobblestone.Operator.Entities.V1Alpha1;
using Kobblestone.Operator.Finalizer;

using KubeOps.Abstractions.Entities;
using KubeOps.Abstractions.Rbac;
using KubeOps.Abstractions.Reconciliation;
using KubeOps.Abstractions.Reconciliation.Controller;
using KubeOps.Abstractions.Reconciliation.Finalizer;
using KubeOps.KubernetesClient;

namespace Kobblestone.Operator.Controller;

[EntityRbac(typeof(V1Alpha1Backup), Verbs = RbacVerb.Watch | RbacVerb.Get | RbacVerb.Update)]
[EntityRbac(typeof(V1Alpha1BackupRepo), Verbs = RbacVerb.Get)]
[EntityRbac(typeof(V1Alpha1Server), Verbs = RbacVerb.Get | RbacVerb.Update)]
[GenericRbac(Groups = [V1Job.KubeGroup], Resources = [V1Job.KubePluralName],
    Verbs = RbacVerb.Get | RbacVerb.Create | RbacVerb.Update)]
public sealed class BackupController(
    IKubernetesClient client,
    EntityFinalizerAttacher<BackupFinalizer, V1Alpha1Backup> finalizer) : IEntityController<V1Alpha1Backup>
{
    public async Task<ReconciliationResult<V1Alpha1Backup>> ReconcileAsync(V1Alpha1Backup entity,
        CancellationToken cancellationToken)
    {
        if (entity.Status.Phase is not BackupPhase.Pending)
        {
            return ReconciliationResult<V1Alpha1Backup>.Success(entity);
        }

        entity = await finalizer(entity, cancellationToken).ConfigureAwait(false);

        var repo = await client
            .GetAsync<V1Alpha1BackupRepo>(entity.Spec.RepoRef.Name, entity.Namespace(), cancellationToken)
            .ConfigureAwait(false);

        if (repo is null)
        {
            return ReconciliationResult<V1Alpha1Backup>.Failure(entity, "Repo not found");
        }

        var server = await client
            .GetAsync<V1Alpha1Server>(entity.Spec.TargetRef.Name, entity.Namespace(), cancellationToken)
            .ConfigureAwait(false);

        if (server is null)
        {
            return ReconciliationResult<V1Alpha1Backup>.Failure(entity, "Server not found");
        }

        if (server.Spec.Storage is null)
        {
            return ReconciliationResult<V1Alpha1Backup>.Failure(entity, "Server has no storage configured");
        }

        if (!server.HasCondition(ServerConditions.RconAvailable))
        {
            return ReconciliationResult<V1Alpha1Backup>.Failure(entity,
                "Server does not have RCON available. Retrying in 5 seconds",
                requeueAfter: TimeSpan.FromSeconds(5));
        }

        if (!server.HasAnyCondition(ServerConditions.Running, ServerConditions.Stopped))
        {
            return ReconciliationResult<V1Alpha1Backup>.Failure(entity,
                "Server must be stopped or running for backup. Retrying in 5 seconds",
                requeueAfter: TimeSpan.FromSeconds(5));
        }

        if (server.HasAnyCondition(ServerConditions.BackupInProgress, ServerConditions.RestoreInProgress))
        {
            return ReconciliationResult<V1Alpha1Backup>.Failure(entity,
                "Server already has backup or restore in progress. Retrying in 5 seconds",
                requeueAfter: TimeSpan.FromSeconds(5));
        }

        server.WithCondition(ServerConditions.BackupInProgress.WithReason("Backup"));

        server = await client.UpdateStatusAsync(server, cancellationToken).ConfigureAwait(false);

        var job = await client.GetAsync<V1Job>(entity.Name(), entity.Namespace(), cancellationToken)
            .ConfigureAwait(false);

        if (job is null)
        {
            job = new V1Job
            {
                Metadata = new V1ObjectMeta(), Spec = new V1JobSpec { Template = new V1PodTemplateSpec() }
            };

            ApplyJob(job, entity, server, repo);

            job = await client.CreateAsync(job, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            ApplyJob(job, entity, server, repo);

            job = await client.UpdateAsync(job, cancellationToken).ConfigureAwait(false);
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
        job.Metadata.EnsureLabels()["kobblestone.io/backup"] = backup.Name();

        job.Spec.BackoffLimit = 0;
        job.Spec.ActiveDeadlineSeconds = backup.Spec.Job?.ActiveDeadlineSeconds;

        var mcBackupContainer = new V1Container
        {
            Name = "backup",
            Image = backup.Spec.Job?.Image ?? "itzg/mc-backup:2026.7.3",
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
            Image = "ubuntu:25.04",
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
            Image = "amazon/aws-cli:2.22.32",
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

        switch (repo.Spec.Type)
        {
            case BackupRepoType.PersistentVolumeClaim:
                job.Spec.Template.Spec.Containers.Add(server.Status.Phase is ServerPhase.Running
                    ? mcBackupContainer
                    : copyContainer);

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
                job.Spec.Template.Spec.InitContainers.Add(server.Status.Phase is ServerPhase.Running
                    ? mcBackupContainer
                    : copyContainer);

                job.Spec.Template.Spec.Containers.Add(uploadContainer);

                job.Spec.Template.Spec.Volumes.Add(new V1Volume
                {
                    Name = "backups", EmptyDir = new V1EmptyDirVolumeSource()
                });
                break;
        }

        job.WithOwnerReference(backup);
    }
}