using k8s.Models;

using Kobblestone.Operator.Conditions;

using KubeOps.Abstractions.Builder;
using KubeOps.Operator;

using Microsoft.Extensions.DependencyInjection.Extensions;

using Kobblestone.Operator.Controller;
using Kobblestone.Operator.Entities.V1Alpha1;
using Kobblestone.Operator.Finalizer;
using Kobblestone.Operator.Services;
using Kobblestone.Operator.Utils;
using Kobblestone.Operator.Webhooks;

using KubeOps.KubernetesClient;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpClient();

builder.Services.TryAddSingleton<IRouterApi, RouterApi>();
builder.Services.TryAddSingleton<IRconConnectionFactory, RconConnectionFactory>();

builder.Services
    .AddKubernetesOperator(settings =>
        settings
            .WithName("kobblestone")
            .WithReconcileStrategy(ReconcileStrategy.ByResourceVersion)
            .WithAutoAttachFinalizers(false))
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
    .AddController<BotBehaviorController, V1Alpha1BotBehavior>()
    .AddController<BotController, V1Alpha1Bot>()
    .AddController<AccountController, V1Alpha1Account>()
    .AddControllerWithLabelSelector<ServerStatefulSetController, V1StatefulSet, ServerLabelSelector<V1StatefulSet>>()
    .AddControllerWithLabelSelector<RouterPodController, V1Pod, RouterLabelSelector<V1Pod>>()
    .AddControllerWithLabelSelector<RouterServiceController, V1Service, RouterLabelSelector<V1Service>>()
    .AddControllerWithLabelSelector<BackupJobController, V1Job, BackupLabelSelector<V1Job>>()
    .AddControllerWithLabelSelector<RestoreJobController, V1Job, RestoreLabelSelector<V1Job>>()
    .AddFinalizer<ServerFinalizer, V1Alpha1Server>("kobblestone.io/deleteServer")
    .AddFinalizer<RouteFinalizer, V1Alpha1Route>("kobblestone.io/deleteRoute")
    .AddFinalizer<BackupFinalizer, V1Alpha1Backup>("kobblestone.io/deleteBackup");

var app = builder.Build();

app.MapPost("/webhooks/proxy/auto-scale/{serverNamespace}/{serverName}",
    async (string serverNamespace, string serverName, AutoScaleWebhookPayload payload, IKubernetesClient client,
        CancellationToken cancellationToken) =>
    {
        // This webhook only performs scale-down. Scale-up can only be performed by central router. 
        if (payload.Action is not "down")
        {
            return;
        }

        var server = await client.GetAsync<V1Alpha1Server>(serverNamespace, serverName, cancellationToken)
            .ConfigureAwait(false);

        if (server is not { Spec: { AutoSleep.Mode: AutoSleepMode.Hibernate, State: not ServerState.Stopped } })
        {
            return;
        }

        if (server.HasCondition(ServerConditions.Hibernating))
        {
            return;
        }

        server.WithCondition(ServerConditions.Hibernating.WithReason("AutoSleep")
            .WithMessage("Server is hibernating because of player inactivity.")
            .WithObservedGeneration(server.Generation()));

        await client.UpdateStatusAsync(server, cancellationToken).ConfigureAwait(false);
    });

app.Run();