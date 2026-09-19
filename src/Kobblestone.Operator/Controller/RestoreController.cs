using k8s.Models;

using Kobblestone.Operator.Entities.V1Alpha1;

using KubeOps.Abstractions.Entities;
using KubeOps.Abstractions.Rbac;
using KubeOps.Abstractions.Reconciliation;
using KubeOps.Abstractions.Reconciliation.Controller;
using KubeOps.KubernetesClient;

namespace Kobblestone.Operator.Controller;

[EntityRbac(typeof(V1Alpha1Restore), Verbs = RbacVerb.Watch | RbacVerb.Get | RbacVerb.Update)]
[EntityRbac(typeof(V1Alpha1Backup), Verbs = RbacVerb.Get)]
[EntityRbac(typeof(V1Alpha1BackupRepo), Verbs = RbacVerb.Get)]
[EntityRbac(typeof(V1Alpha1Server), Verbs = RbacVerb.Get)]
[GenericRbac(Groups = [V1PersistentVolumeClaim.KubeGroup], Resources = [V1PersistentVolumeClaim.KubePluralName],
    Verbs = RbacVerb.Get)]
[GenericRbac(Groups = [V1Job.KubeGroup], Resources = [V1Job.KubePluralName],
    Verbs = RbacVerb.Create)]
public sealed class RestoreController(IKubernetesClient client) : IEntityController<V1Alpha1Restore>
{
    public async Task<ReconciliationResult<V1Alpha1Restore>> ReconcileAsync(V1Alpha1Restore entity,
        CancellationToken cancellationToken)
    {
        // This controllers job was done before
        if (entity.Status.Phase is not RestorePhase.Pending)
        {
            return ReconciliationResult<V1Alpha1Restore>.Success(entity);
        }

        var backup = await client
            .GetAsync<V1Alpha1Backup>(entity.Spec.BackupRef.Name, entity.Namespace(), cancellationToken)
            .ConfigureAwait(false);

        if (backup is null)
        {
            entity.Status.Message = "Backup not found";

            entity = await client.UpdateStatusAsync(entity, cancellationToken).ConfigureAwait(false);

            return ReconciliationResult<V1Alpha1Restore>.Failure(entity, "Backup not found",
                requeueAfter: TimeSpan.FromSeconds(5));
        }

        var repo = await client
            .GetAsync<V1Alpha1BackupRepo>(backup.Spec.RepoRef.Name, entity.Namespace(), cancellationToken)
            .ConfigureAwait(false);

        if (repo is null)
        {
            entity.Status.Message = "BackupRepo not found";

            entity = await client.UpdateStatusAsync(entity, cancellationToken).ConfigureAwait(false);

            return ReconciliationResult<V1Alpha1Restore>.Failure(entity, "BackupRepo not found",
                requeueAfter: TimeSpan.FromSeconds(5));
        }

        V1PersistentVolumeClaim? pvc = null;

        if (entity.Spec.TargetRef.Kind is null or "Server")
        {
            var server = await client
                .GetAsync<V1Alpha1Server>(entity.Spec.TargetRef.Name, entity.Namespace(), cancellationToken)
                .ConfigureAwait(false);

            if (server is null)
            {
                entity.Status.Message = "Server not found";

                entity = await client.UpdateStatusAsync(entity, cancellationToken).ConfigureAwait(false);

                return ReconciliationResult<V1Alpha1Restore>.Failure(entity, "Server not found",
                    requeueAfter: TimeSpan.FromSeconds(5));
            }

            if (server.Status.Phase is not ServerPhase.Stopped)
            {
                entity.Status.Message = "Waiting for server to be in phase `Stopped`";

                entity = await client.UpdateStatusAsync(entity, cancellationToken).ConfigureAwait(false);

                return ReconciliationResult<V1Alpha1Restore>.Failure(entity,
                    "Waiting for server to be in phase `Stopped`",
                    requeueAfter: TimeSpan.FromSeconds(5));
            }

            if (server.Spec.Storage is null)
            {
                entity.Status.Message = "Server has no storage configured";

                entity = await client.UpdateStatusAsync(entity, cancellationToken).ConfigureAwait(false);

                return ReconciliationResult<V1Alpha1Restore>.Failure(entity, "Server has no storage configured",
                    requeueAfter: TimeSpan.FromSeconds(5));
            }

            pvc = await client.GetAsync<V1PersistentVolumeClaim>(server.Name(), server.Namespace(), cancellationToken)
                .ConfigureAwait(false);
        }

        if (pvc is null)
        {
            entity.Status.Message = "Target could not be determined";

            entity = await client.UpdateStatusAsync(entity, cancellationToken).ConfigureAwait(false);

            return ReconciliationResult<V1Alpha1Restore>.Failure(entity, "Target could not be determined",
                requeueAfter: TimeSpan.FromSeconds(5));
        }

        var job = new V1Job
        {
            Metadata = new V1ObjectMeta
            {
                Name = entity.Name(),
                NamespaceProperty = entity.Namespace(),
                Labels = new Dictionary<string, string> { { "kobblestone.io/restore", entity.Name() } }
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
                                    ClaimName = pvc.Name()
                                }
                            },
                            new V1Volume { Name = "stage", EmptyDir = new V1EmptyDirVolumeSource() }
                        ],
                        InitContainers = [],
                        Containers =
                        [
                            new V1Container
                            {
                                Name = "restore",
                                Image = "ubuntu:25.04",
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
                                         """.ReplaceLineEndings("\n")
                                ],
                                VolumeMounts =
                                [
                                    new V1VolumeMount
                                    {
                                        Name = "data", MountPath = "/mnt/data", SubPath = entity.Spec.Path
                                    },
                                    new V1VolumeMount { Name = "stage", MountPath = "/mnt/stage" },
                                    new V1VolumeMount
                                    {
                                        Name = "backups",
                                        MountPath = "/mnt/backups",
                                        SubPath = repo.Spec.PersistentVolumeClaim?.SubPath,
                                        ReadOnlyProperty = true
                                    }
                                ]
                            }
                        ]
                    }
                }
            }
        };

        var failOnExistingDataContainer = new V1Container
        {
            Name = "ensure-empty-target",
            Image = "ubuntu:25.04",
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
                    """.ReplaceLineEndings("\n")
            ],
            VolumeMounts =
            [
                new V1VolumeMount
                {
                    Name = "data", MountPath = "/mnt/data", SubPath = entity.Spec.Path, ReadOnlyProperty = true
                }
            ]
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
                Image = "amazon/aws-cli:2.22.32",
                Command =
                [
                    "/bin/sh",
                    "-c",
                    $"aws s3 cp s3://{repo.Spec.S3?.Bucket}/{backup.Uid()}.tar.gz /mnt/backups/{backup.Uid()}/world.tar.gz"
                ],
                VolumeMounts =
                [
                    new V1VolumeMount { Name = "backups", MountPath = "/mnt/backups" }
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

            job.Spec.Template.Spec.InitContainers.Add(s3DownloadContainer);

            job.Spec.Template.Spec.Volumes.Add(new V1Volume
            {
                Name = "backups", EmptyDir = new V1EmptyDirVolumeSource()
            });
        }
        else
        {
            job.Spec.Template.Spec.Volumes.Add(new V1Volume
            {
                Name = "backups",
                PersistentVolumeClaim = new V1PersistentVolumeClaimVolumeSource
                {
                    ClaimName = repo.Spec.PersistentVolumeClaim?.Name, ReadOnlyProperty = true
                }
            });
        }

        job.WithOwnerReference(entity);

        job = await client.CreateAsync(job, cancellationToken).ConfigureAwait(false);

        return ReconciliationResult<V1Alpha1Restore>.Success(entity);
    }

    public Task<ReconciliationResult<V1Alpha1Restore>> DeletedAsync(V1Alpha1Restore entity,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(ReconciliationResult<V1Alpha1Restore>.Success(entity));
    }
}