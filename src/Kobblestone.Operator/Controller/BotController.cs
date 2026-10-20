using System.Security.Cryptography;
using System.Text;

using k8s.Models;

using Kobblestone.Operator.Conditions;
using Kobblestone.Operator.Constants;
using Kobblestone.Operator.Entities.V1Alpha1;
using Kobblestone.Operator.Utils;
using Kobblestone.Sessions;

using KubeOps.Abstractions.Entities;
using KubeOps.Abstractions.Events;
using KubeOps.Abstractions.Rbac;
using KubeOps.Abstractions.Reconciliation;
using KubeOps.Abstractions.Reconciliation.Controller;
using KubeOps.KubernetesClient;
using KubeOps.KubernetesClient.Selectors;

using Tomlyn;
using Tomlyn.Model;

namespace Kobblestone.Operator.Controller;

/// <summary>
/// Runs a `Bot` as a single Minecraft-Console-Client (MCC) process in a `Deployment`.
/// </summary>
/// <remarks>
/// The operator only renders MCC configuration layers into a `ConfigMap`; referenced `ConfigMap`s and `Secret`s are
/// projected into the `Pod` directly. Kobblestone.BotRunner merges the layers, converts the mounted session `Secret`
/// of the `Account` into MCC's session cache and runs MCC. The status is derived from the bot `Pod`s, which are
/// watched by the BotPodController.
/// </remarks>
[EntityRbac(typeof(V1Alpha1Bot), Verbs = RbacVerb.Watch | RbacVerb.Get | RbacVerb.Update)]
[EntityRbac(typeof(V1Alpha1Account), Verbs = RbacVerb.Get)]
[EntityRbac(typeof(V1Alpha1Server), Verbs = RbacVerb.Get)]
[EntityRbac(typeof(V1Alpha1Network), Verbs = RbacVerb.Get)]
[GenericRbac(Groups = [V1ConfigMap.KubeGroup], Resources = [V1ConfigMap.KubePluralName],
    Verbs = RbacVerb.Get | RbacVerb.Create | RbacVerb.Update)]
[GenericRbac(Groups = [V1Secret.KubeGroup], Resources = [V1Secret.KubePluralName], Verbs = RbacVerb.Get)]
[GenericRbac(Groups = [V1Deployment.KubeGroup], Resources = [V1Deployment.KubePluralName],
    Verbs = RbacVerb.Get | RbacVerb.Create | RbacVerb.Update)]
[GenericRbac(Groups = [V1Pod.KubeGroup], Resources = [V1Pod.KubePluralName], Verbs = RbacVerb.List)]
public sealed class BotController(IKubernetesClient client, EventPublisher emitEvent) : IEntityController<V1Alpha1Bot>
{
    public const string BotLabel = "kobblestone.io/bot";

    /// <summary>Hash of the rendered configuration; changes roll out the bot.</summary>
    private const string ConfigHashAnnotation = "kobblestone.io/config-hash";

    /// <summary>Version of the session `Secret`; a refreshed session rolls out the bot.</summary>
    private const string SessionVersionAnnotation = "kobblestone.io/session-version";

    // File system layout of the bot Pod, see Kobblestone.BotRunner's BotRunnerOptions.
    private const string ConfigDirectory = "/etc/kobblestone/bot/config";

    private const string ScriptsDirectory = "/etc/kobblestone/bot/scripts";

    private const string SessionDirectory = "/etc/kobblestone/session";

    private const string ReadyFile = "/tmp/kobblestone-ready";

    // Names of the volumes the operator adds to the bot Pod. Prefixed, so they cannot clash with `spec.pod.volumes`.
    private const string ReservedVolumePrefix = "kobblestone-";

    private const string ConfigVolume = ReservedVolumePrefix + "config";

    private const string ScriptsVolume = ReservedVolumePrefix + "scripts";

    private const string DataVolume = ReservedVolumePrefix + "data";

    private const string SessionVolume = ReservedVolumePrefix + "session";

    private const string ScriptKeyPrefix = "script.";

    private static readonly TomlSerializerOptions TomlOptions = new() { WriteIndented = true };

    public async Task<ReconciliationResult<V1Alpha1Bot>> ReconcileAsync(V1Alpha1Bot entity,
        CancellationToken cancellationToken)
    {
        var before = EntityQueueExtensions.Snapshot(entity.Status);

        if (Validate(entity) is { } invalid)
        {
            await emitEvent(entity, "InvalidSpec", invalid, EventType.Warning, cancellationToken).ConfigureAwait(false);

            SetPhase(entity, V1Alpha1Bot.BotPhase.Pending, invalid, "InvalidSpec");

            return await UpdateStatusAsync(entity, before, null, cancellationToken).ConfigureAwait(false);
        }

        var (address, port) = await ResolveTargetAsync(entity, cancellationToken).ConfigureAwait(false);

        if (address is null)
        {
            await emitEvent(entity,
                "TargetNotResolved",
                entity.Spec.Target.TargetRef is { } targetRef
                    ? $"Could not resolve the address of target {targetRef.Kind ?? "Server"} {targetRef.Name}."
                    : "Neither `targetRef` nor `host` is set.",
                EventType.Warning, cancellationToken).ConfigureAwait(false);

            SetPhase(entity, V1Alpha1Bot.BotPhase.Pending, "Target could not be resolved.", "TargetNotResolved");

            return await UpdateStatusAsync(entity, before, TimeSpan.FromSeconds(30), cancellationToken)
                .ConfigureAwait(false);
        }

        entity.Status.Address = $"{address}:{port}";

        var session = await GetSessionAsync(entity, cancellationToken).ConfigureAwait(false);

        // Without a valid session MCC would only fail to log in: keep the bot scaled down until the Account is
        // authorized. The AccountSecretController queues the Bot when the session changes.
        var waitingForAccount = entity.Spec.Auth is not null && session.Session is null;

        entity.Status.Username = entity.Spec.Auth is null
            ? entity.Spec.OfflineUsername ?? entity.Name()
            : session.Session?.Username;

        var configMap = await EnsureConfigMapAsync(entity, address, port, cancellationToken).ConfigureAwait(false);

        await EnsureDeploymentAsync(entity, configMap, session.ResourceVersion, waitingForAccount, cancellationToken)
            .ConfigureAwait(false);

        if (entity.Spec.Inactive is true)
        {
            SetPhase(entity, V1Alpha1Bot.BotPhase.Inactive, "Bot inactive.", "Inactive");
        }
        else if (waitingForAccount)
        {
            SetPhase(entity, V1Alpha1Bot.BotPhase.Pending,
                $"Waiting for Account {entity.Spec.Auth!.AccountRef.Name} to be authorized.", "AccountNotAuthorized");
        }
        else
        {
            await DeriveStatusFromPodsAsync(entity, cancellationToken).ConfigureAwait(false);
        }

        return await UpdateStatusAsync(entity, before, null, cancellationToken).ConfigureAwait(false);
    }

    public Task<ReconciliationResult<V1Alpha1Bot>> DeletedAsync(V1Alpha1Bot entity,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(ReconciliationResult<V1Alpha1Bot>.Success(entity));
    }

    /// <summary>
    /// Checks constraints the CRD schema cannot express. Returns an error message or <c>null</c>.
    /// </summary>
    private static string? Validate(V1Alpha1Bot bot)
    {
        foreach (var (source, index) in (bot.Spec.Config?.From ?? []).Select((s, i) => (s, i)))
        {
            if ((source.ConfigMapKeyRef is null) == (source.SecretKeyRef is null))
            {
                return $"`config.from[{index}]` must set exactly one of `configMapKeyRef` and `secretKeyRef`.";
            }
        }

        var scripts = bot.Spec.Scripts ?? [];

        foreach (var script in scripts)
        {
            var sources = new object?[] { script.Inline, script.ConfigMapKeyRef, script.SecretKeyRef };

            if (sources.Count(x => x is not null) != 1)
            {
                return $"Script `{script.Name}` must set exactly one of `inline`, `configMapKeyRef` and `secretKeyRef`.";
            }
        }

        if (scripts.GroupBy(s => s.Name).FirstOrDefault(g => g.Count() > 1) is { } duplicate)
        {
            return $"Script `{duplicate.Key}` is defined more than once.";
        }

        var volumes = bot.Spec.Pod?.Volumes ?? [];

        if (volumes.FirstOrDefault(v => v.Name.StartsWith(ReservedVolumePrefix, StringComparison.Ordinal)) is { } reserved)
        {
            return $"Volume `{reserved.Name}` uses the prefix `{ReservedVolumePrefix}`, which is reserved for the volumes of Kobblestone.";
        }

        if (volumes.GroupBy(v => v.Name).FirstOrDefault(g => g.Count() > 1) is { } duplicateVolume)
        {
            return $"Volume `{duplicateVolume.Key}` is defined more than once.";
        }

        string[] reservedPaths = [ConfigDirectory, ScriptsDirectory, SessionDirectory, "/data"];

        if ((bot.Spec.Pod?.VolumeMounts ?? []).FirstOrDefault(m => reservedPaths.Contains(m.MountPath.TrimEnd('/'))) is { } mount)
        {
            return $"Volume mount `{mount.Name}` uses the path `{mount.MountPath}`, which is managed by Kobblestone. Mount below `/data` instead, e.g. `/data/files`.";
        }

        return null;
    }

    private async Task<ReconciliationResult<V1Alpha1Bot>> UpdateStatusAsync(V1Alpha1Bot entity, string before,
        TimeSpan? requeueAfter, CancellationToken cancellationToken)
    {
        if (EntityQueueExtensions.Snapshot(entity.Status) != before)
        {
            entity = await client.UpdateStatusAsync(entity, cancellationToken).ConfigureAwait(false);
        }

        return ReconciliationResult<V1Alpha1Bot>.Success(entity, requeueAfter);
    }

    private static void SetPhase(V1Alpha1Bot bot, V1Alpha1Bot.BotPhase phase, string message, string reason)
    {
        bot.Status.Phase = phase;
        bot.Status.Message = message;

        bot.WithConditions(phase is V1Alpha1Bot.BotPhase.Connected
            ? BotConditions.Connected.WithReason(reason).WithMessage(message)
            : BotConditions.Connected.False().WithReason(reason).WithMessage(message));
    }

    private async Task DeriveStatusFromPodsAsync(V1Alpha1Bot bot, CancellationToken cancellationToken)
    {
        var pods = await client.ListAsync<V1Pod>(bot.Namespace(), new EqualsLabelSelector(BotLabel, bot.Name()),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var pod = pods
            .Where(p => p.Metadata.DeletionTimestamp is null)
            .MaxBy(p => p.Metadata.CreationTimestamp);

        var container = pod?.Status?.ContainerStatuses?.FirstOrDefault(c => c.Name == "bot");

        bot.Status.Restarts = container?.RestartCount;

        if (container is null)
        {
            SetPhase(bot, V1Alpha1Bot.BotPhase.Pending, "Bot Pod is starting.", "Starting");

            return;
        }

        if (container.Ready)
        {
            SetPhase(bot, V1Alpha1Bot.BotPhase.Connected, $"Connected to {bot.Status.Address}.", "Connected");

            return;
        }

        // Kobblestone.BotRunner reports why MCC exited in the termination message.
        var lastTermination = container.State?.Terminated ?? container.LastState?.Terminated;

        if (lastTermination is not null)
        {
            SetPhase(bot, V1Alpha1Bot.BotPhase.Disconnected,
                lastTermination.Message?.Trim() is { Length: > 0 } message
                    ? message
                    : $"Bot exited with code {lastTermination.ExitCode}.",
                "Disconnected");

            return;
        }

        SetPhase(bot, V1Alpha1Bot.BotPhase.Pending, "Bot is connecting.", "Connecting");
    }

    private async Task<(string? Address, int Port)> ResolveTargetAsync(V1Alpha1Bot bot,
        CancellationToken cancellationToken)
    {
        string? address = null;
        int? port = null;

        if (bot.Spec.Target.TargetRef is { } targetRef)
        {
            var ns = targetRef.NamespaceProperty ?? bot.Namespace();

            switch (targetRef.Kind)
            {
                case null or "Server":
                    var server = await client.GetAsync<V1Alpha1Server>(targetRef.Name, ns, cancellationToken)
                        .ConfigureAwait(false);

                    if (server is not null)
                    {
                        address = $"{server.Name()}.{server.Namespace()}.svc.cluster.local";
                        port = server.Spec.Service?.Port;
                    }

                    break;
                case "Network":
                    var network = await client.GetAsync<V1Alpha1Network>(targetRef.Name, ns, cancellationToken)
                        .ConfigureAwait(false);

                    if (network is not null)
                    {
                        address = $"{network.Name()}.{network.Namespace()}.svc.cluster.local";
                        port = network.Spec.Service?.Port;
                    }

                    break;
            }
        }

        return (bot.Spec.Target.Host ?? address,
            bot.Spec.Target.Port ?? port ?? MinecraftConstants.DefaultMinecraftPort);
    }

    private async Task<(MinecraftSession? Session, string? ResourceVersion)> GetSessionAsync(V1Alpha1Bot bot,
        CancellationToken cancellationToken)
    {
        if (bot.Spec.Auth?.AccountRef.Name is not { } accountName)
        {
            return (null, null);
        }

        // The session Secret has the Account's name and the standard format (Kobblestone.Sessions).
        var secret = await client.GetAsync<V1Secret>(accountName, bot.Namespace(), cancellationToken)
            .ConfigureAwait(false);

        if (secret?.Type != SessionSecret.Type)
        {
            return (null, null);
        }

        var session = SessionSecret.Read(secret.Data);

        return session is null || session.IsExpired(DateTimeOffset.UtcNow)
            ? (null, null)
            : (session, secret.ResourceVersion());
    }

    private async Task<V1ConfigMap> EnsureConfigMapAsync(V1Alpha1Bot bot, string address, int port,
        CancellationToken cancellationToken)
    {
        var cm = await client.GetAsync<V1ConfigMap>(bot.Name(), bot.Namespace(), cancellationToken)
            .ConfigureAwait(false);

        if (cm is null)
        {
            cm = new V1ConfigMap { Metadata = new V1ObjectMeta() };

            ApplyConfigMap(cm, bot, address, port);

            return await client.CreateAsync(cm, cancellationToken).ConfigureAwait(false);
        }

        ApplyConfigMap(cm, bot, address, port);

        return await client.UpdateAsync(cm, cancellationToken).ConfigureAwait(false);
    }

    private static void ApplyConfigMap(V1ConfigMap cm, V1Alpha1Bot bot, string address, int port)
    {
        cm.Metadata.Name = bot.Name();
        cm.Metadata.NamespaceProperty = bot.Namespace();
        cm.Metadata.Labels = new Dictionary<string, string> { [BotLabel] = bot.Name() };

        cm.Data = new Dictionary<string, string>
        {
            ["00-base.toml"] = Serialize(BaseLayer()),
            ["30-scheduler.toml"] = Serialize(SchedulerLayer(bot)),
            ["99-managed.toml"] = Serialize(ManagedLayer(bot, address, port))
        };

        if (bot.Spec.Config?.Inline is { } inline)
        {
            cm.Data["10-inline.toml"] = inline.ReplaceLineEndings("\n");
        }

        foreach (var script in bot.Spec.Scripts ?? [])
        {
            if (script.Inline is { } content)
            {
                cm.Data[ScriptKeyPrefix + script.Name] = content.ReplaceLineEndings("\n");
            }
        }

        cm.WithOwnerReference(bot);
    }

    /// <summary>
    /// Defaults for running MCC unattended in a container. Users may override them.
    /// </summary>
    private static TomlTable BaseLayer() => new()
    {
        // TODO(verify): Setting names against the bundled MCC version.
        ["Main"] = new TomlTable
        {
            ["Advanced"] = new TomlTable
            {
                ["Language"] = "en_us",
                ["MinecraftVersion"] = "auto",
                // Exit instead of prompting on failures; Kubernetes restarts the container.
                ["ExitOnFailure"] = true
            }
        },
        ["Console"] = new TomlTable { ["General"] = new TomlTable { ["Display_Input"] = false } }
    };

    /// <summary>
    /// <c>ScriptScheduler</c> tasks for scripts with <c>runOnLogin</c>.
    /// </summary>
    private static TomlTable SchedulerLayer(V1Alpha1Bot bot)
    {
        var tasks = new TomlTableArray();

        foreach (var script in bot.Spec.Scripts?.Where(s => s.RunOnLogin is true) ?? [])
        {
            tasks.Add(new TomlTable
            {
                ["Task_Name"] = $"run-{script.Name}",
                ["Trigger_On_Login"] = true,
                ["Action"] = $"script {script.Name}"
            });
        }

        if (tasks.Count == 0)
        {
            return new TomlTable();
        }

        return new TomlTable
        {
            ["ChatBot"] = new TomlTable
            {
                ["ScriptScheduler"] = new TomlTable { ["Enabled"] = true, ["TaskList"] = tasks }
            }
        };
    }

    /// <summary>
    /// Settings owned by Kobblestone. The account of online bots is set by Kobblestone.BotRunner from the session.
    /// </summary>
    private static TomlTable ManagedLayer(V1Alpha1Bot bot, string address, int port)
    {
        var signChat = bot.Spec.Auth?.SignChat is true;

        var general = new TomlTable
        {
            ["Server"] = new TomlTable { ["Host"] = address, ["Port"] = port },
            ["AccountType"] = bot.Spec.Auth is null ? "mojang" : "microsoft"
        };

        if (bot.Spec.Auth is null)
        {
            // A password of "-" makes MCC join in offline mode.
            general["Account"] = new TomlTable
            {
                ["Login"] = bot.Spec.OfflineUsername ?? bot.Name(),
                ["Password"] = "-"
            };
        }

        // TODO(verify): Setting names against the bundled MCC version.
        return new TomlTable
        {
            ["Main"] = new TomlTable
            {
                ["General"] = general,
                ["Advanced"] = new TomlTable { ["SessionCache"] = "Disk", ["ProfileKeyCache"] = "Disk" }
            },
            ["Signature"] = new TomlTable
            {
                ["LoginWithSecureProfile"] = signChat,
                ["SignChat"] = signChat,
                ["SignMessageInCommand"] = signChat
            }
        };
    }

    private static string Serialize(TomlTable table) =>
        TomlSerializer.Serialize(table, TomlOptions).ReplaceLineEndings("\n");

    private async Task EnsureDeploymentAsync(V1Alpha1Bot bot, V1ConfigMap configMap, string? sessionVersion,
        bool waitingForAccount, CancellationToken cancellationToken)
    {
        var deploy = await client.GetAsync<V1Deployment>(bot.Name(), bot.Namespace(), cancellationToken)
            .ConfigureAwait(false);

        if (deploy is null)
        {
            deploy = new V1Deployment { Metadata = new V1ObjectMeta(), Spec = new V1DeploymentSpec() };

            ApplyDeployment(deploy, bot, configMap, sessionVersion, waitingForAccount);

            await client.CreateAsync(deploy, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            ApplyDeployment(deploy, bot, configMap, sessionVersion, waitingForAccount);

            await client.UpdateAsync(deploy, cancellationToken).ConfigureAwait(false);
        }
    }

    private static void ApplyDeployment(V1Deployment deploy, V1Alpha1Bot bot, V1ConfigMap configMap,
        string? sessionVersion, bool waitingForAccount)
    {
        var labels = new Dictionary<string, string> { [BotLabel] = bot.Name() };

        var annotations = new Dictionary<string, string> { [ConfigHashAnnotation] = Hash(configMap.Data) };

        if (sessionVersion is not null)
        {
            annotations[SessionVersionAnnotation] = sessionVersion;
        }

        var volumes = new List<V1Volume>
        {
            new() { Name = ConfigVolume, Projected = new V1ProjectedVolumeSource { Sources = ConfigSources(bot) } },
            bot.Spec.Scripts is { Count: > 0 }
                ? new() { Name = ScriptsVolume, Projected = new V1ProjectedVolumeSource { Sources = ScriptSources(bot) } }
                : new() { Name = ScriptsVolume, EmptyDir = new V1EmptyDirVolumeSource() },
            new() { Name = DataVolume, EmptyDir = new V1EmptyDirVolumeSource() }
        };

        var volumeMounts = new List<V1VolumeMount>
        {
            new() { Name = ConfigVolume, MountPath = ConfigDirectory, ReadOnlyProperty = true },
            new() { Name = ScriptsVolume, MountPath = ScriptsDirectory, ReadOnlyProperty = true },
            new() { Name = DataVolume, MountPath = "/data" }
        };

        if (bot.Spec.Auth?.AccountRef.Name is { } accountName)
        {
            volumes.Add(new V1Volume
            {
                Name = SessionVolume,
                Secret = new V1SecretVolumeSource { SecretName = accountName }
            });

            volumeMounts.Add(new V1VolumeMount
            {
                Name = SessionVolume,
                MountPath = SessionDirectory,
                ReadOnlyProperty = true
            });
        }

        volumes.AddRange(bot.Spec.Pod?.Volumes ?? []);
        volumeMounts.AddRange(bot.Spec.Pod?.VolumeMounts ?? []);

        deploy.Metadata.Name = bot.Name();
        deploy.Metadata.NamespaceProperty = bot.Namespace();
        deploy.Metadata.Labels = labels;

        deploy.Spec.Replicas = bot.Spec.Inactive is true || waitingForAccount ? 0 : 1;
        // Never run two instances: a second login with the same account kicks the first one.
        deploy.Spec.Strategy = new V1DeploymentStrategy { Type = "Recreate" };
        deploy.Spec.Selector = new V1LabelSelector { MatchLabels = labels };
        deploy.Spec.Template = new V1PodTemplateSpec
        {
            Metadata = new V1ObjectMeta { Labels = labels, Annotations = annotations },
            Spec = new V1PodSpec
            {
                NodeName = bot.Spec.Pod?.NodeName,
                Affinity = bot.Spec.Pod?.Affinity,
                NodeSelector = bot.Spec.Pod?.NodeSelector,
                Tolerations = bot.Spec.Pod?.Tolerations,
                ImagePullSecrets = [new V1LocalObjectReference { Name = "dockerconfig" }],
                AutomountServiceAccountToken = false,
                Volumes = volumes,
                Containers =
                [
                    new V1Container
                    {
                        Name = "bot",
                        Image = bot.Spec.Pod?.Image ?? ContainerImages.BotRunner,
                        ImagePullPolicy = bot.Spec.Pod?.ImagePullPolicy,
                        Resources = bot.Spec.Resources,
                        // MCC reads commands from stdin; Kobblestone.BotRunner owns it.
                        Stdin = true,
                        TerminationMessagePolicy = "FallbackToLogsOnError",
                        Env =
                        [
                            // Options of Kobblestone.BotRunner (BotRunnerOptions, prefix BOT_).
                            new V1EnvVar { Name = "BOT_CONFIGDIRECTORY", Value = ConfigDirectory },
                            new V1EnvVar { Name = "BOT_SCRIPTSDIRECTORY", Value = ScriptsDirectory },
                            new V1EnvVar { Name = "BOT_SESSIONDIRECTORY", Value = SessionDirectory },
                            new V1EnvVar { Name = "BOT_READYFILE", Value = ReadyFile },
                            .. bot.Spec.Pod?.Env ?? []
                        ],
                        VolumeMounts = volumeMounts,
                        ReadinessProbe = new V1Probe
                        {
                            Exec = new V1ExecAction { Command = ["test", "-f", ReadyFile] },
                            PeriodSeconds = 5
                        }
                    }
                ]
            }
        };

        deploy.WithOwnerReference(bot);
    }

    /// <summary>
    /// Configuration layers: the operator's <c>ConfigMap</c> plus the referenced keys, in the order they are merged.
    /// </summary>
    private static List<V1VolumeProjection> ConfigSources(V1Alpha1Bot bot)
    {
        var sources = new List<V1VolumeProjection>
        {
            new()
            {
                ConfigMap = new V1ConfigMapProjection
                {
                    Name = bot.Name(),
                    Items = new[] { "00-base.toml", "10-inline.toml", "30-scheduler.toml", "99-managed.toml" }
                        .Where(key => key != "10-inline.toml" || bot.Spec.Config?.Inline is not null)
                        .Select(key => new V1KeyToPath { Key = key, Path = key })
                        .ToList()
                }
            }
        };

        var from = bot.Spec.Config?.From ?? [];

        for (var i = 0; i < from.Count; i++)
        {
            sources.Add(Project(from[i].ConfigMapKeyRef, from[i].SecretKeyRef, $"20-from-{i:D3}.toml"));
        }

        return sources;
    }

    private static List<V1VolumeProjection> ScriptSources(V1Alpha1Bot bot)
    {
        var scripts = bot.Spec.Scripts ?? [];

        var sources = new List<V1VolumeProjection>();

        // Only with items: a projection without items would project all keys of the ConfigMap.
        if (scripts.Any(s => s.Inline is not null))
        {
            sources.Add(new V1VolumeProjection
            {
                ConfigMap = new V1ConfigMapProjection
                {
                    Name = bot.Name(),
                    Items = scripts
                        .Where(s => s.Inline is not null)
                        .Select(s => new V1KeyToPath { Key = ScriptKeyPrefix + s.Name, Path = s.Name })
                        .ToList()
                }
            });
        }

        sources.AddRange(scripts
            .Where(s => s.Inline is null)
            .Select(s => Project(s.ConfigMapKeyRef, s.SecretKeyRef, s.Name)));

        return sources;
    }

    private static V1VolumeProjection Project(V1ConfigMapKeySelector? configMapKey, V1SecretKeySelector? secretKey,
        string path) =>
        configMapKey is not null
            ? new V1VolumeProjection
            {
                ConfigMap = new V1ConfigMapProjection
                {
                    Name = configMapKey.Name,
                    Optional = configMapKey.Optional,
                    Items = [new V1KeyToPath { Key = configMapKey.Key, Path = path }]
                }
            }
            : new V1VolumeProjection
            {
                Secret = new V1SecretProjection
                {
                    Name = secretKey?.Name,
                    Optional = secretKey?.Optional,
                    Items = [new V1KeyToPath { Key = secretKey?.Key, Path = path }]
                }
            };

    /// <summary>
    /// Changes of the rendered configuration restart the bot. Changes of referenced <c>ConfigMap</c>s and
    /// <c>Secret</c>s are not tracked: restart the bot to apply them (MCC reads its configuration once).
    /// </summary>
    private static string Hash(IDictionary<string, string>? data)
    {
        var builder = new StringBuilder();

        foreach (var (key, value) in (data ?? new Dictionary<string, string>()).OrderBy(e => e.Key,
                     StringComparer.Ordinal))
        {
            builder.Append(key).Append('\0').Append(value).Append('\0');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))[..16];
    }
}
