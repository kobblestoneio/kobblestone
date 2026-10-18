using k8s.Models;
using Kobblestone.Operator.Conditions;
using Kobblestone.Operator.Constants;
using Kobblestone.Operator.Entities.V1Alpha1;
using Kobblestone.Operator.Utils;
using Kobblestone.Sessions;
using KubeOps.Abstractions.Entities;
using KubeOps.Abstractions.Rbac;
using KubeOps.Abstractions.Reconciliation;
using KubeOps.Abstractions.Reconciliation.Controller;
using KubeOps.KubernetesClient;
using KubeOps.KubernetesClient.Selectors;

namespace Kobblestone.Operator.Controller;

/// <summary>
/// Manages the session of an `Account`.
/// </summary>
/// <remarks>
/// The auth `Job` (Kobblestone.AccountAuth) only writes the two `Secret`s of the `Account`:
/// <list type="bullet">
/// <item><c>&lt;account&gt;</c>: the session in the standard format (<see cref="SessionSecret"/>), shareable with
/// workloads.</item>
/// <item><c>&lt;account&gt;-credentials</c>: refresh token and pending device flow (<see cref="CredentialsSecret"/>),
/// internal.</item>
/// </list>
/// This controller derives the whole `Account` status from these `Secret`s and the `Job`. The AccountSecretController
/// and AccountJobController queue the `Account` whenever one of them changes.
/// </remarks>
[EntityRbac(typeof(V1Alpha1Account), Verbs = RbacVerb.Watch | RbacVerb.Get | RbacVerb.Update)]
// Patch is not used by the operator itself, but Kubernetes only allows granting permissions the operator holds: the
// Role of the auth Job (see ApplyRole) allows patching the Account's Secrets.
[GenericRbac(
    Groups = [V1Secret.KubeGroup],
    Resources = [V1Secret.KubePluralName],
    Verbs = RbacVerb.Get | RbacVerb.Create | RbacVerb.Update | RbacVerb.Patch | RbacVerb.Delete
)]
[GenericRbac(
    Groups = [V1ServiceAccount.KubeGroup],
    Resources = [V1ServiceAccount.KubePluralName],
    Verbs = RbacVerb.Get | RbacVerb.Create | RbacVerb.Update
)]
[GenericRbac(
    Groups = [V1Role.KubeGroup],
    Resources = [V1Role.KubePluralName],
    Verbs = RbacVerb.Get | RbacVerb.Create | RbacVerb.Update
)]
[GenericRbac(
    Groups = [V1RoleBinding.KubeGroup],
    Resources = [V1RoleBinding.KubePluralName],
    Verbs = RbacVerb.Get | RbacVerb.Create | RbacVerb.Update
)]
[GenericRbac(
    Groups = [V1Job.KubeGroup],
    Resources = [V1Job.KubePluralName],
    Verbs = RbacVerb.Get | RbacVerb.List | RbacVerb.Create | RbacVerb.Delete
)]
[GenericRbac(Groups = [V1Pod.KubeGroup], Resources = [V1Pod.KubePluralName], Verbs = RbacVerb.List)]
public sealed class AccountController(IKubernetesClient client) : IEntityController<V1Alpha1Account>
{
    /// <summary>
    /// A failed authorization (e.g. the device code expired unused) is retried after this delay.
    /// </summary>
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Finished auth `Job`s are kept this long, so their result and termination message can be inspected.
    /// </summary>
    private const int JobTtlSecondsAfterFinished = 3600;

    public async Task<ReconciliationResult<V1Alpha1Account>> ReconcileAsync(
        V1Alpha1Account entity,
        CancellationToken cancellationToken
    )
    {
        var sessionSecret = await EnsureSessionSecretAsync(entity, cancellationToken)
            .ConfigureAwait(false);

        var credentialsSecret = await EnsureCredentialsSecretAsync(entity, cancellationToken)
            .ConfigureAwait(false);

        await EnsureServiceAccountAsync(entity, cancellationToken).ConfigureAwait(false);

        await EnsureRoleAsync(entity, cancellationToken).ConfigureAwait(false);

        await EnsureRoleBindingAsync(entity, cancellationToken).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        var session = SessionSecret.Read(sessionSecret.Data);
        var deviceFlow = CredentialsSecret.ReadDeviceFlow(credentialsSecret.Data);

        var job = await EnsureJobAsync(entity, session, now, cancellationToken)
            .ConfigureAwait(false);

        var before = EntityQueueExtensions.Snapshot(entity.Status);

        await DeriveStatusAsync(entity, session, deviceFlow, job, now, cancellationToken)
            .ConfigureAwait(false);

        if (EntityQueueExtensions.Snapshot(entity.Status) != before)
        {
            entity = await client
                .UpdateStatusAsync(entity, cancellationToken)
                .ConfigureAwait(false);
        }

        // Re-check periodically: the session has to be refreshed before it expires, and expired device flows and
        // failed Jobs have to be retried. Secret and Job changes queue the Account immediately anyway.
        return ReconciliationResult<V1Alpha1Account>.Success(entity, TimeSpan.FromMinutes(5));
    }

    public Task<ReconciliationResult<V1Alpha1Account>> DeletedAsync(
        V1Alpha1Account entity,
        CancellationToken cancellationToken
    )
    {
        return Task.FromResult(ReconciliationResult<V1Alpha1Account>.Success(entity));
    }

    private async Task DeriveStatusAsync(
        V1Alpha1Account account,
        MinecraftSession? session,
        DeviceFlow? deviceFlow,
        V1Job? job,
        DateTimeOffset now,
        CancellationToken cancellationToken
    )
    {
        var status = account.Status;

        var validSession = session is not null && !session.IsExpired(now) ? session : null;

        status.Profile = validSession is null
            ? null
            : new V1Alpha1Account.V1Alpha1AccountProfile
            {
                Name = validSession.Username,
                Uuid = validSession.Uuid.ToString("D"),
            };
        status.AuthorizedAt = validSession?.IssuedAt.UtcDateTime;
        status.SessionExpiresAt = validSession?.ExpiresAt.UtcDateTime;
        status.DeviceFlow = null;

        var jobActive = job is not null && !job.IsFinished();

        if (account.Spec.Inactive is true)
        {
            status.Profile = null;
            status.AuthorizedAt = null;
            status.SessionExpiresAt = null;

            SetPhase(
                account,
                V1Alpha1Account.AccountPhase.Unauthorized,
                "Account inactive",
                "Inactive"
            );

            return;
        }

        if (validSession is not null)
        {
            SetPhase(
                account,
                V1Alpha1Account.AccountPhase.Authorized,
                $"Authorized as {validSession.Username}",
                "SessionValid"
            );

            return;
        }

        if (deviceFlow is not null && deviceFlow.ExpiresAt > now && jobActive)
        {
            status.DeviceFlow = new V1Alpha1Account.V1Alpha1AccountStatusDeviceFlow
            {
                UserCode = deviceFlow.UserCode,
                VerificationUri = deviceFlow.VerificationUri,
                ExpiresAt = deviceFlow.ExpiresAt.UtcDateTime,
                Message = deviceFlow.Message,
            };

            SetPhase(
                account,
                V1Alpha1Account.AccountPhase.AwaitingAuthorization,
                deviceFlow.Message
                    ?? $"Open {deviceFlow.VerificationUri} and enter the code {deviceFlow.UserCode}",
                "DeviceFlowPending"
            );

            return;
        }

        if (job is not null && job.IsFailed())
        {
            var reason = await GetTerminationMessageAsync(job, cancellationToken)
                .ConfigureAwait(false);

            SetPhase(
                account,
                V1Alpha1Account.AccountPhase.Unauthorized,
                $"Authorization failed: {reason ?? "see the logs of Job " + job.Name()}",
                "AuthorizationFailed"
            );

            return;
        }

        SetPhase(
            account,
            V1Alpha1Account.AccountPhase.Unauthorized,
            jobActive ? "Authorization in progress" : "Not authorized",
            jobActive ? "Authorizing" : "NotAuthorized"
        );
    }

    private static void SetPhase(
        V1Alpha1Account account,
        V1Alpha1Account.AccountPhase phase,
        string message,
        string reason
    )
    {
        account.Status.Phase = phase;
        account.Status.Message = message;

        account.WithConditions(
            phase is V1Alpha1Account.AccountPhase.Authorized
                ? AccountConditions.Authorized.WithReason(reason).WithMessage(message)
                : AccountConditions.Authorized.False().WithReason(reason).WithMessage(message),
            phase is V1Alpha1Account.AccountPhase.AwaitingAuthorization
                ? AccountConditions.AwaitingAuthorization.WithReason(reason).WithMessage(message)
                : AccountConditions.AwaitingAuthorization.False().WithReason(reason)
        );
    }

    private async Task<string?> GetTerminationMessageAsync(
        V1Job job,
        CancellationToken cancellationToken
    )
    {
        var pods = await client
            .ListAsync<V1Pod>(
                job.Namespace(),
                new EqualsLabelSelector("job-name", job.Name()),
                cancellationToken: cancellationToken
            )
            .ConfigureAwait(false);

        return pods.SelectMany(p => p.Status?.ContainerStatuses ?? [])
            .Select(s => s.State?.Terminated?.Message)
            .FirstOrDefault(m => !string.IsNullOrWhiteSpace(m))
            ?.Trim();
    }

    /// <summary>
    /// Starts a new auth `Job` when the session is missing or has to be refreshed, and returns the current one.
    /// </summary>
    private async Task<V1Job?> EnsureJobAsync(
        V1Alpha1Account account,
        MinecraftSession? session,
        DateTimeOffset now,
        CancellationToken cancellationToken
    )
    {
        var jobs = await client
            .ListAsync<V1Job>(
                account.Namespace(),
                new EqualsLabelSelector(AccountLabels.Account, account.Name()),
                cancellationToken: cancellationToken
            )
            .ConfigureAwait(false);

        var job = jobs.Where(j => j.GetOwnerReference(account) is not null)
            .MaxBy(j => j.Metadata.CreationTimestamp);

        if (account.Spec.Inactive is true)
        {
            if (job is not null && !job.IsFinished())
            {
                await client.DeleteAsync(job, cancellationToken).ConfigureAwait(false);
            }

            return null;
        }

        var refreshBefore = TimeSpan.FromMinutes(account.Spec.RefreshBeforeExpiryMinutes ?? 60);
        var authorizationRequired = session is null || now >= session.ExpiresAt - refreshBefore;

        if (!authorizationRequired)
        {
            return job;
        }

        var startNewJob = job switch
        {
            null => true,
            _ when !job.IsFinished() => false,
            // A successful Job without a usable session (e.g. the session was refreshed a while ago): refresh again.
            _ when !job.IsFailed() => true,
            // Do not hammer the services (or the user with new device codes) after a failure.
            _ => job.Metadata.CreationTimestamp is { } created
                && now - created >= RetryAfterFailure,
        };

        if (!startNewJob)
        {
            return job;
        }

        job = new V1Job { Metadata = new V1ObjectMeta(), Spec = new V1JobSpec() };

        ApplyJob(job, account, now);

        return await client.CreateAsync(job, cancellationToken).ConfigureAwait(false);
    }

    private static void ApplyJob(V1Job job, V1Alpha1Account account, DateTimeOffset now)
    {
        // Unique names: finished Jobs are kept for a while (TTL) to inspect their result.
        job.Metadata.Name = $"{account.Name()}-auth-{now.ToUnixTimeSeconds()}";
        job.Metadata.NamespaceProperty = account.Namespace();
        job.Metadata.Labels = new Dictionary<string, string>
        {
            [AccountLabels.Account] = account.Name(),
        };

        job.Spec.BackoffLimit = 0;
        job.Spec.TtlSecondsAfterFinished = JobTtlSecondsAfterFinished;
        job.Spec.Template = new V1PodTemplateSpec
        {
            Metadata = new V1ObjectMeta
            {
                Labels = new Dictionary<string, string>
                {
                    [AccountLabels.Account] = account.Name(),
                },
            },
            Spec = new V1PodSpec
            {
                NodeName = account.Spec.Authorization?.Pod?.NodeName,
                NodeSelector = account.Spec.Authorization?.Pod?.NodeSelector,
                Tolerations = account.Spec.Authorization?.Pod?.Tolerations,
                Affinity = account.Spec.Authorization?.Pod?.Affinity,
                ServiceAccountName = account.Name(),
                AutomountServiceAccountToken = true,
                RestartPolicy = "Never",
                ImagePullSecrets = [new V1LocalObjectReference { Name = "dockerconfig" }],
                Containers =
                [
                    new V1Container
                    {
                        Name = "auth",
                        Image =
                            account.Spec.Authorization?.Pod?.Image ?? ContainerImages.AccountAuth,
                        ImagePullPolicy = account.Spec.Authorization?.Pod?.ImagePullPolicy,
                        TerminationMessagePolicy = "FallbackToLogsOnError",
                        Env =
                        [
                            // Options of Kobblestone.AccountAuth (AccountAuthOptions, prefix AUTH_).
                            new V1EnvVar { Name = "AUTH_NAMESPACE", Value = account.Namespace() },
                            new V1EnvVar
                            {
                                Name = "AUTH_SESSIONSECRETNAME",
                                Value = account.Name(),
                            },
                            new V1EnvVar
                            {
                                Name = "AUTH_CREDENTIALSSECRETNAME",
                                Value = CredentialsSecret.NameFor(account.Name()),
                            },
                        ],
                    },
                ],
            },
        };

        job.WithOwnerReference(account);
    }

    private async Task<V1Secret> EnsureSessionSecretAsync(
        V1Alpha1Account account,
        CancellationToken cancellationToken
    )
    {
        var secret = await client
            .GetAsync<V1Secret>(account.Name(), account.Namespace(), cancellationToken)
            .ConfigureAwait(false);

        // The type of a Secret is immutable. Secrets of earlier versions (Opaque, prismarine-auth caches) cannot be
        // used anymore and are replaced; the Account has to be authorized again once.
        if (secret is not null && secret.Type != SessionSecret.Type)
        {
            await client.DeleteAsync(secret, cancellationToken).ConfigureAwait(false);

            secret = null;
        }

        if (secret is not null)
        {
            return secret;
        }

        secret = new V1Secret
        {
            Metadata = new V1ObjectMeta
            {
                Name = account.Name(),
                NamespaceProperty = account.Namespace(),
                Labels = new Dictionary<string, string>
                {
                    [AccountLabels.Account] = account.Name(),
                },
            },
            Type = SessionSecret.Type,
        };

        secret.WithOwnerReference(account);

        return await client.CreateAsync(secret, cancellationToken).ConfigureAwait(false);
    }

    private async Task<V1Secret> EnsureCredentialsSecretAsync(
        V1Alpha1Account account,
        CancellationToken cancellationToken
    )
    {
        var name = CredentialsSecret.NameFor(account.Name());

        var secret = await client
            .GetAsync<V1Secret>(name, account.Namespace(), cancellationToken)
            .ConfigureAwait(false);

        if (secret is not null)
        {
            return secret;
        }

        secret = new V1Secret
        {
            Metadata = new V1ObjectMeta
            {
                Name = name,
                NamespaceProperty = account.Namespace(),
                Labels = new Dictionary<string, string>
                {
                    [AccountLabels.Account] = account.Name(),
                },
            },
            Type = "Opaque",
        };

        secret.WithOwnerReference(account);

        return await client.CreateAsync(secret, cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureServiceAccountAsync(
        V1Alpha1Account account,
        CancellationToken cancellationToken
    )
    {
        var serviceAccount = await client
            .GetAsync<V1ServiceAccount>(account.Name(), account.Namespace(), cancellationToken)
            .ConfigureAwait(false);

        if (serviceAccount is null)
        {
            serviceAccount = new V1ServiceAccount { Metadata = new V1ObjectMeta() };

            ApplyServiceAccount(serviceAccount, account);

            await client.CreateAsync(serviceAccount, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            ApplyServiceAccount(serviceAccount, account);

            await client.UpdateAsync(serviceAccount, cancellationToken).ConfigureAwait(false);
        }
    }

    private static void ApplyServiceAccount(
        V1ServiceAccount serviceAccount,
        V1Alpha1Account account
    )
    {
        serviceAccount.Metadata.Name = account.Name();
        serviceAccount.Metadata.NamespaceProperty = account.Namespace();

        serviceAccount.AutomountServiceAccountToken = true;

        serviceAccount.WithOwnerReference(account);
    }

    private async Task EnsureRoleAsync(V1Alpha1Account account, CancellationToken cancellationToken)
    {
        var role = await client
            .GetAsync<V1Role>(account.Name(), account.Namespace(), cancellationToken)
            .ConfigureAwait(false);

        if (role is null)
        {
            role = new V1Role { Metadata = new V1ObjectMeta() };

            ApplyRole(role, account);

            await client.CreateAsync(role, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            ApplyRole(role, account);

            await client.UpdateAsync(role, cancellationToken).ConfigureAwait(false);
        }
    }

    private static void ApplyRole(V1Role role, V1Alpha1Account account)
    {
        role.Metadata.Name = account.Name();
        role.Metadata.NamespaceProperty = account.Namespace();

        // The auth Job only writes the Account's two Secrets, never the Account itself.
        role.Rules =
        [
            new V1PolicyRule
            {
                ApiGroups = [""],
                Resources = ["secrets"],
                ResourceNames = [account.Name(), CredentialsSecret.NameFor(account.Name())],
                Verbs = ["get", "update", "patch"],
            },
        ];

        role.WithOwnerReference(account);
    }

    private async Task EnsureRoleBindingAsync(
        V1Alpha1Account account,
        CancellationToken cancellationToken
    )
    {
        var binding = await client
            .GetAsync<V1RoleBinding>(account.Name(), account.Namespace(), cancellationToken)
            .ConfigureAwait(false);

        if (binding is null)
        {
            binding = new V1RoleBinding { Metadata = new V1ObjectMeta() };

            ApplyRoleBinding(binding, account);

            await client.CreateAsync(binding, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            ApplyRoleBinding(binding, account);

            await client.UpdateAsync(binding, cancellationToken).ConfigureAwait(false);
        }
    }

    private static void ApplyRoleBinding(V1RoleBinding binding, V1Alpha1Account account)
    {
        binding.Metadata.Name = account.Name();
        binding.Metadata.NamespaceProperty = account.Namespace();

        binding.RoleRef = new V1RoleRef { Name = account.Name(), Kind = "Role" };
        binding.Subjects =
        [
            new Rbacv1Subject
            {
                Name = account.Name(),
                NamespaceProperty = account.Namespace(),
                Kind = "ServiceAccount",
            },
        ];

        binding.WithOwnerReference(account);
    }
}
