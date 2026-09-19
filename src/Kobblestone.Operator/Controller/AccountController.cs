using k8s.Models;

using Kobblestone.Operator.Conditions;
using Kobblestone.Operator.Entities.V1Alpha1;

using KubeOps.Abstractions.Entities;
using KubeOps.Abstractions.Rbac;
using KubeOps.Abstractions.Reconciliation;
using KubeOps.Abstractions.Reconciliation.Controller;
using KubeOps.KubernetesClient;

namespace Kobblestone.Operator.Controller;

[EntityRbac(typeof(V1Alpha1Account), Verbs = RbacVerb.Watch | RbacVerb.Get | RbacVerb.Update)]
[GenericRbac(Groups = [V1Secret.KubeGroup], Resources = [V1Secret.KubePluralName],
    Verbs = RbacVerb.Get | RbacVerb.Create | RbacVerb.Update)]
[GenericRbac(Groups = [V1ServiceAccount.KubeGroup], Resources = [V1ServiceAccount.KubePluralName],
    Verbs = RbacVerb.Get | RbacVerb.Create | RbacVerb.Update)]
[GenericRbac(Groups = [V1Role.KubeGroup], Resources = [V1Role.KubePluralName],
    Verbs = RbacVerb.Get | RbacVerb.Create | RbacVerb.Update)]
[GenericRbac(Groups = [V1RoleBinding.KubeGroup], Resources = [V1RoleBinding.KubePluralName],
    Verbs = RbacVerb.Get | RbacVerb.Create | RbacVerb.Update)]
[GenericRbac(Groups = [V1Job.KubeGroup], Resources = [V1Job.KubePluralName],
    Verbs = RbacVerb.Get | RbacVerb.Create | RbacVerb.Update | RbacVerb.Delete)]
public sealed class AccountController(IKubernetesClient client) : IEntityController<V1Alpha1Account>
{
    public async Task<ReconciliationResult<V1Alpha1Account>> ReconcileAsync(V1Alpha1Account entity,
        CancellationToken cancellationToken)
    {
        if (!entity.Status.Conditions.Any())
        {
            entity.WithConditions(AccountConditions.Default);

            entity = await client.UpdateStatusAsync(entity, cancellationToken).ConfigureAwait(false);

            return ReconciliationResult<V1Alpha1Account>.Success(entity);
        }

        await EnsureSecretAsync(entity, cancellationToken).ConfigureAwait(false);

        await EnsureServiceAccountAsync(entity, cancellationToken).ConfigureAwait(false);

        await EnsureRoleAsync(entity, cancellationToken).ConfigureAwait(false);

        await EnsureRoleBindingAsync(entity, cancellationToken).ConfigureAwait(false);

        await EnsureJobAsync(entity, cancellationToken).ConfigureAwait(false);

        entity = await client.UpdateStatusAsync(entity, cancellationToken).ConfigureAwait(false);

        // We reconcile every Account every 5 minutes to check whether they need a session refresh (saves us a CronJob)
        return ReconciliationResult<V1Alpha1Account>.Success(entity,
            requeueAfter: TimeSpan.FromMinutes(5));
    }

    public Task<ReconciliationResult<V1Alpha1Account>> DeletedAsync(V1Alpha1Account entity,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(ReconciliationResult<V1Alpha1Account>.Success(entity));
    }

    private async Task<V1RoleBinding> EnsureRoleBindingAsync(V1Alpha1Account account,
        CancellationToken cancellationToken)
    {
        var binding = await client.GetAsync<V1RoleBinding>(account.Name(), account.Namespace(), cancellationToken)
            .ConfigureAwait(false);

        if (binding is null)
        {
            binding = new V1RoleBinding { Metadata = new V1ObjectMeta() };

            ApplyRoleBinding(binding, account);

            binding = await client.CreateAsync(binding, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            ApplyRoleBinding(binding, account);

            binding = await client.UpdateAsync(binding, cancellationToken).ConfigureAwait(false);
        }

        return binding;
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
                Name = account.Name(), NamespaceProperty = account.Namespace(), Kind = "ServiceAccount"
            }
        ];

        binding.WithOwnerReference(account);
    }

    private async Task<V1Role> EnsureRoleAsync(V1Alpha1Account account, CancellationToken cancellationToken)
    {
        var role = await client.GetAsync<V1Role>(account.Name(), account.Namespace(), cancellationToken)
            .ConfigureAwait(false);

        if (role is null)
        {
            role = new V1Role { Metadata = new V1ObjectMeta() };

            ApplyRole(role, account);

            role = await client.CreateAsync(role, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            ApplyRole(role, account);

            role = await client.UpdateAsync(role, cancellationToken).ConfigureAwait(false);
        }

        return role;
    }

    private static void ApplyRole(V1Role role, V1Alpha1Account account)
    {
        role.Metadata.Name = account.Name();
        role.Metadata.NamespaceProperty = account.Namespace();

        role.Rules =
        [
            new V1PolicyRule
            {
                ApiGroups = [""],
                Resources = ["secrets"],
                ResourceNames = [account.Name()],
                Verbs = ["get", "patch"]
            },
            new V1PolicyRule
            {
                ApiGroups = ["kobblestone.io"],
                Resources = ["accounts/status"],
                ResourceNames = [account.Name()],
                Verbs = ["patch"]
            }
        ];

        role.WithOwnerReference(account);
    }

    private async Task<V1ServiceAccount> EnsureServiceAccountAsync(V1Alpha1Account account,
        CancellationToken cancellationToken)
    {
        var serviceAccount = await client
            .GetAsync<V1ServiceAccount>(account.Name(), account.Namespace(), cancellationToken).ConfigureAwait(false);

        if (serviceAccount is null)
        {
            serviceAccount = new V1ServiceAccount { Metadata = new V1ObjectMeta() };

            ApplyServiceAccount(serviceAccount, account);

            serviceAccount = await client.CreateAsync(serviceAccount, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            ApplyServiceAccount(serviceAccount, account);

            serviceAccount = await client.UpdateAsync(serviceAccount, cancellationToken).ConfigureAwait(false);
        }

        return serviceAccount;
    }

    private static void ApplyServiceAccount(V1ServiceAccount serviceAccount, V1Alpha1Account account)
    {
        serviceAccount.Metadata.Name = account.Name();
        serviceAccount.Metadata.NamespaceProperty = account.Namespace();

        serviceAccount.AutomountServiceAccountToken = true;

        serviceAccount.WithOwnerReference(account);
    }

    private async Task<V1Secret> EnsureSecretAsync(V1Alpha1Account account, CancellationToken cancellationToken)
    {
        var secret = await client.GetAsync<V1Secret>(account.Name(), account.Namespace(), cancellationToken)
            .ConfigureAwait(false);

        if (secret is not null)
        {
            return secret;
        }

        secret = new V1Secret
        {
            Metadata = new V1ObjectMeta { Name = account.Name(), NamespaceProperty = account.Namespace() }
        };

        secret.WithOwnerReference(account);

        secret = await client.CreateAsync(secret, cancellationToken).ConfigureAwait(false);

        return secret;
    }

    private async Task<V1Job?> EnsureJobAsync(V1Alpha1Account account, CancellationToken cancellationToken)
    {
        var job = await client.GetAsync<V1Job>(account.Name(), account.Namespace(), cancellationToken)
            .ConfigureAwait(false);

        if (account.Spec.Inactive is true)
        {
            if (job is not null)
            {
                await client.DeleteAsync(job, cancellationToken).ConfigureAwait(false);
            }

            account.Status.Phase = V1Alpha1Account.AccountPhase.Unauthorized;
            account.Status.AuthorizedAt = null;
            account.Status.SessionExpiresAt = null;
            account.Status.DeviceFlow = null;
            account.Status.Profile = null;
            account.Status.Message = "Account inactive";

            account.WithConditions(
                AccountConditions.Authorized.False().WithReason("Inactive"),
                AccountConditions.AwaitingAuthorization.False().WithReason("Inactive"));

            return null;
        }

        var sessionExpiresAt = account.Status.SessionExpiresAt;

        var refreshAfter =
            sessionExpiresAt?.Subtract(TimeSpan.FromMinutes(account.Spec.RefreshBeforeExpiryMinutes ?? 60));

        var refreshRequired = DateTime.UtcNow >= refreshAfter;

        switch (job)
        {
            case null when (sessionExpiresAt is null || refreshRequired):
                job = new V1Job { Metadata = new V1ObjectMeta(), Spec = new V1JobSpec() };

                ApplyJob(job, account);

                job = await client.CreateAsync(job, cancellationToken).ConfigureAwait(false);

                break;
            case { Status.Succeeded: 1 } when (sessionExpiresAt is null || refreshRequired):
                await client.DeleteAsync(job, cancellationToken).ConfigureAwait(false);

                job = new V1Job { Metadata = new V1ObjectMeta(), Spec = new V1JobSpec() };

                ApplyJob(job, account);

                job = await client.CreateAsync(job, cancellationToken).ConfigureAwait(false);

                break;
        }

        return job;
    }

    private static void ApplyJob(V1Job job, V1Alpha1Account account)
    {
        job.Metadata.Name = account.Name();
        job.Metadata.NamespaceProperty = account.Namespace();

        job.Spec.Template = new V1PodTemplateSpec
        {
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
                        Image = account.Spec.Authorization?.Pod?.Image ??
                                "ghcr.io/kobblestoneio/account-auth:0.1.0",
                        ImagePullPolicy = account.Spec.Authorization?.Pod?.ImagePullPolicy,
                        Env =
                        [
                            new V1EnvVar { Name = "ACCOUNT_NAME", Value = account.Name() },
                            new V1EnvVar { Name = "SECRET_NAME", Value = account.Name() },
                            new V1EnvVar { Name = "NAMESPACE", Value = account.Namespace() },
                        ]
                    }
                ]
            }
        };

        job.WithOwnerReference(account);
    }
}