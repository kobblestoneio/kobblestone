using k8s.Models;
using Kobblestone.Operator.Conditions;
using Kobblestone.Operator.Constants;
using Kobblestone.Operator.Controller;
using Kobblestone.Operator.Entities.V1Alpha1;
using Kobblestone.Operator.Finalizer;
using Kobblestone.Operator.Services;
using Kobblestone.Operator.Utils;
using Kobblestone.Operator.Webhooks;
using KubeOps.Abstractions.Builder;
using KubeOps.KubernetesClient;
using KubeOps.Operator;
using Microsoft.Extensions.DependencyInjection.Extensions;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpClient();

builder.Services.TryAddSingleton<IRouterApi, RouterApi>();
builder.Services.TryAddSingleton<IRconConnectionFactory, RconConnectionFactory>();

builder
    .Services.AddKubernetesOperator(settings =>
        settings
            .WithName("kobblestone")
            .WithReconcileStrategy(ReconcileStrategy.ByResourceVersion)
            // Only one operator instance may reconcile at a time. Several controllers rely on this, e.g. the
            // in-process lock in BackupRepoUsage that prevents two Jobs from claiming the same BackupRepo volume.
            .WithLeaderElection(LeaderElectionType.Single)
            .WithAutoAttachFinalizers(false)
    )
#if DEBUG
    .AddCrdInstaller(c =>
    {
        // Careful, these can be very destructive.
        // c.WithOverwriteExisting()
        //     .WithDeleteOnShutdown();
    })
#endif
    .AddController<ServerController, V1Alpha1Server>()
    .AddController<RouterController, V1Alpha1Router>()
    .AddController<BackupRepoController, V1Alpha1BackupRepo>()
    .AddController<BackupController, V1Alpha1Backup>()
    .AddController<RouteController, V1Alpha1Route>()
    .AddController<CommandController, V1Alpha1Command>()
    .AddController<CommandGroupController, V1Alpha1CommandGroup>()
    .AddController<RestoreController, V1Alpha1Restore>()
    .AddController<NetworkController, V1Alpha1Network>()
    .AddController<BotController, V1Alpha1Bot>()
    .AddController<AccountController, V1Alpha1Account>()
    .AddControllerWithLabelSelector<
        ServerStatefulSetController,
        V1StatefulSet,
        ServerLabelSelector<V1StatefulSet>
    >()
    .AddControllerWithLabelSelector<RouterPodController, V1Pod, RouterLabelSelector<V1Pod>>()
    .AddControllerWithLabelSelector<
        RouterServiceController,
        V1Service,
        RouterLabelSelector<V1Service>
    >()
    .AddControllerWithLabelSelector<
        ServerServiceController,
        V1Service,
        ServerLabelSelector<V1Service>
    >()
    .AddControllerWithLabelSelector<
        NetworkServiceController,
        V1Service,
        NetworkLabelSelector<V1Service>
    >()
    .AddControllerWithLabelSelector<BackupJobController, V1Job, BackupLabelSelector<V1Job>>()
    .AddControllerWithLabelSelector<RestoreJobController, V1Job, RestoreLabelSelector<V1Job>>()
    .AddControllerWithLabelSelector<AccountJobController, V1Job, AccountLabelSelector<V1Job>>()
    .AddControllerWithLabelSelector<
        AccountSecretController,
        V1Secret,
        AccountLabelSelector<V1Secret>
    >()
    .AddControllerWithLabelSelector<BotPodController, V1Pod, BotLabelSelector<V1Pod>>()
    .AddFinalizer<ServerFinalizer, V1Alpha1Server>("kobblestone.io/deleteServer")
    .AddFinalizer<RouteFinalizer, V1Alpha1Route>("kobblestone.io/deleteRoute")
    .AddFinalizer<BackupFinalizer, V1Alpha1Backup>("kobblestone.io/deleteBackup");

var app = builder.Build();

app.MapPost(
    "/webhooks/proxy/auto-scale/{serverNamespace}/{serverName}",
    async (
        string serverNamespace,
        string serverName,
        AutoScaleWebhookPayload payload,
        IKubernetesClient client,
        CancellationToken cancellationToken
    ) =>
    {
        // This webhook only performs scale-down. Scale-up can only be performed by central router.
        if (payload.Action is not "down")
        {
            return;
        }

        var server = await client
            .GetAsync<V1Alpha1Server>(serverName, serverNamespace, cancellationToken)
            .ConfigureAwait(false);

        if (
            server
            is not
            {
                Spec: { AutoSleep.Mode: AutoSleepMode.Hibernate, State: not ServerState.Stopped }
            }
        )
        {
            return;
        }

        if (
            server.HasCondition(ServerConditions.Hibernating)
            || server.GetAnnotation(ServerAnnotations.Hibernate) is "true"
        )
        {
            return;
        }

        // Only request hibernation. The ServerController is the only writer of the server's status: it records the
        // request in the Hibernating condition, scales the server down and removes the annotation again.
        server.SetAnnotation(ServerAnnotations.Hibernate, "true");

        await client.UpdateAsync(server, cancellationToken).ConfigureAwait(false);
    }
);

app.Run();
