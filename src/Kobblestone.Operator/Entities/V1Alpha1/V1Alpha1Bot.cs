using k8s.Models;

using KubeOps.Abstractions.Entities;
using KubeOps.Abstractions.Entities.Attributes;

namespace Kobblestone.Operator.Entities.V1Alpha1;

[Description("""
             A `Bot` is a single [Minecraft-Console-Client](https://github.com/MCCTeam/Minecraft-Console-Client) (MCC)
             instance connected to exactly one target.

             If the `Bot` should connect to an online server, it needs to reference an authorized `Account` (which is
             allowed to access the server). Otherwise it joins with `offlineUsername`.

             The `Bot` is programmed with MCC's own means: its configuration (`config`, TOML as in `MinecraftClient.ini`,
             e.g. to enable built-in ChatBots) and scripts (`scripts`, MCC command scripts or C# scripts).
             """)]
[KubernetesEntity(Group = "kobblestone.io", ApiVersion = "v1alpha1", Kind = "Bot")]
[GenericAdditionalPrinterColumn(".spec.auth.accountRef.name", "Account", "string")]
public sealed class V1Alpha1Bot :
    CustomKubernetesEntity<V1Alpha1Bot.V1Alpha1BotSpec, V1Alpha1Bot.V1Alpha1BotStatus>,
    IConditionsStatus<V1Alpha1Bot.V1Alpha1BotStatus, V1Condition>
{
    public record V1Alpha1BotSpec
    {
        [Description("""
                     Whether the `Bot` should be inactive (not running).

                     Default is `false`.
                     """)]
        public bool? Inactive { get; set; }

        [Title("Target")]
        [Description("""
                     Target this `Bot` connects to.
                     """)]
        [Required]
        public V1Alpha1BotTarget Target { get; set; } = null!;

        [Description("""
                     Username of the `Bot` when connecting without authentication (offline servers).

                     Ignored when `auth` is set. Default is the name of the `Bot`.
                     """)]
        public string? OfflineUsername { get; set; }

        [Title("Authentication")]
        [Description("""
                     Configures authentication for online servers.

                     If not specified, the `Bot` can only connect to offline servers.
                     """)]
        public V1Alpha1BotAuth? Auth { get; set; }

        [Title("Configuration")]
        [Description("""
                     MCC configuration in TOML (the format of `MinecraftClient.ini`).

                     Layers are merged in this order, later layers win: generated defaults, `inline`, `from` (in order),
                     generated `ScriptScheduler` tasks for `scripts` with `runOnLogin`, and finally the settings
                     managed by Kobblestone (`Main.General.Account`, `Main.General.Server`, `Main.General.AccountType`,
                     session caches and `Signature`), which cannot be overridden.

                     Tables are merged, arrays of tables (e.g. `ChatBot.ScriptScheduler.TaskList`) are concatenated.
                     """)]
        [ExternalDocs("https://mccteam.github.io/guide/configuration.html")]
        public V1Alpha1BotConfig? Config { get; set; }

        [Title("Scripts")]
        [Description("""
                     Script files placed in MCC's working directory, so they can be run with `/script <name>` or by
                     the `ScriptScheduler`.

                     Files ending with `.cs` are MCC C# scripts (starting with `//MCCScript 1.0`), all others MCC
                     command scripts.
                     """)]
        [ExternalDocs("https://mccteam.github.io/guide/creating-bots.html")]
        public IList<V1Alpha1BotScript>? Scripts { get; set; }

        [Title("Resources")]
        [Description("""
                     Resource requirements of the `Bot` container.
                     """)]
        [ExternalDocs("https://kubernetes.io/docs/reference/kubernetes-api/core/pod-v1/#resources")]
        public V1ResourceRequirements? Resources { get; set; }

        [Title("Pod")]
        [Description("""
                     Configures the spec of the managed `Pod`.
                     """)]
        public V1Alpha1BotPod? Pod { get; set; }
    }

    public record V1Alpha1BotStatus : IConditions<V1Condition>
    {
        [Description("""
                     Current phase of the `Bot`.

                     Values:
                     - `Inactive`: The `Bot` is inactive (`spec.inactive`).
                     - `Pending`: The `Bot` is starting or waiting for its `Account`.
                     - `Connected`: The `Bot` joined its target.
                     - `Disconnected`: The `Bot` lost its connection (kicked, connection lost or login rejected) and is
                       restarted with backoff. `message` contains the reason.
                     """)]
        [AdditionalPrinterColumn(name: "Status")]
        public BotPhase Phase { get; set; }

        [Description("""
                     Message explaining the current state of the `Bot`.
                     """)]
        [AdditionalPrinterColumn(name: "Message")]
        public string? Message { get; set; }

        [Description("""
                     Resolved address (`host:port`) of the target.
                     """)]
        [AdditionalPrinterColumn(name: "Target")]
        public string? Address { get; set; }

        [Description("""
                     Username the `Bot` joins with.
                     """)]
        public string? Username { get; set; }

        [Description("""
                     How often the bot container was restarted, e.g. after disconnects.
                     """)]
        public int? Restarts { get; set; }

        [Description("""
                     Conditions of the `Bot`.
                     """)]
        public IList<V1Condition> Conditions { get; set; } = [];
    }

    public enum BotPhase
    {
        Pending,
        Inactive,
        Connected,
        Disconnected
    }

    public record V1Alpha1BotTarget
    {
        [Description("""
                     Reference to a target object.

                     This may be one of the following types: `Server`, `Network`.

                     If you want to connect through `Router` instances, you must set the `host` parameter to a hostname
                     that routes accordingly.
                     """)]
        public V1TypedObjectReference? TargetRef { get; set; }

        [Description("""
                     Custom host (IP or hostname) of the target server.

                     This is required when `targetRef` is not specified. Otherwise, it overrides the target host implied
                     by `targetRef`.
                     """)]
        public string? Host { get; set; }

        [Description("""
                     Custom port of the target server.

                     Can be specified together with `targetRef` to override the implied port.
                     """)]
        [RangeMinimum(1)]
        [RangeMaximum(65535)]
        public int? Port { get; set; }
    }

    public record V1Alpha1BotAuth
    {
        [Description("""
                     Reference to an `Account` in the same namespace this `Bot` authenticates with.

                     The `Bot` mounts the `Account`'s session `Secret` and is restarted whenever the session is refreshed.
                     While the `Account` is not authorized, the `Bot` stays `Pending`.
                     """)]
        [Required]
        public V1LocalObjectReference AccountRef { get; set; } = null!;

        [Description("""
                     Whether chat messages are signed with the `Account`'s profile key (secure chat).

                     Default is `false`.
                     """)]
        public bool? SignChat { get; set; }
    }

    public record V1Alpha1BotConfig
    {
        [Description("""
                     Inline MCC configuration (TOML).
                     """)]
        public string? Inline { get; set; }

        [Description("""
                     MCC configuration (TOML) from keys of `ConfigMap`s or `Secret`s, merged after `inline` in order.

                     Use `Secret`s for confidential settings (e.g. tokens of chat bridges); they are mounted into the
                     `Bot` directly and never read by the operator.
                     """)]
        public IList<V1Alpha1BotFileSource>? From { get; set; }
    }

    public record V1Alpha1BotFileSource
    {
        [Description("""
                     Key of a `ConfigMap` in the same namespace.
                     """)]
        [ExternalDocs("https://kubernetes.io/docs/reference/kubernetes-api/core/pod-v1/#environment-variables")]
        public V1ConfigMapKeySelector? ConfigMapKeyRef { get; set; }

        [Description("""
                     Key of a `Secret` in the same namespace.
                     """)]
        [ExternalDocs("https://kubernetes.io/docs/reference/kubernetes-api/core/pod-v1/#environment-variables")]
        public V1SecretKeySelector? SecretKeyRef { get; set; }
    }

    public record V1Alpha1BotScript
    {
        [Description("""
                     File name of the script in MCC's working directory, e.g. `farm.cs` or `greet.txt`.
                     """)]
        [Required]
        [Pattern(@"^[A-Za-z0-9][A-Za-z0-9._-]*$")]
        public string Name { get; set; } = null!;

        [Description("""
                     Inline content of the script.

                     Exactly one of `inline`, `configMapKeyRef` and `secretKeyRef` must be set.
                     """)]
        public string? Inline { get; set; }

        [Description("""
                     Key of a `ConfigMap` in the same namespace holding the script.
                     """)]
        public V1ConfigMapKeySelector? ConfigMapKeyRef { get; set; }

        [Description("""
                     Key of a `Secret` in the same namespace holding the script.
                     """)]
        public V1SecretKeySelector? SecretKeyRef { get; set; }

        [Description("""
                     Whether the script is run after every login (via MCC's `ScriptScheduler`).

                     Default is `false`.
                     """)]
        public bool? RunOnLogin { get; set; }
    }

    public record V1Alpha1BotPod
    {
        [Description("""
                     Custom image for the bot container (e.g. to bundle another MCC version).

                     Default is `ghcr.io/kobblestoneio/bot-runner`.
                     """)]
        [ExternalDocs("https://kubernetes.io/docs/reference/kubernetes-api/core/pod-v1/#Container")]
        public string? Image { get; set; }

        [Description("""
                     Image pull policy of the bot container.
                     """)]
        [ExternalDocs("https://kubernetes.io/docs/reference/kubernetes-api/core/pod-v1/#Container")]
        public string? ImagePullPolicy { get; set; }

        [Description("""
                     Additional environment variables of the bot container (e.g. for C# scripts).
                     """)]
        [ExternalDocs("https://kubernetes.io/docs/reference/kubernetes-api/core/pod-v1/#environment-variables")]
        public IList<V1EnvVar>? Env { get; set; }

        [Description("""
                     Additional volumes of the bot `Pod`, e.g. a `PersistentVolumeClaim` with data files for scripts.

                     Names starting with `kobblestone-` are reserved for the volumes added by Kobblestone.
                     """)]
        [ExternalDocs("https://kubernetes.io/docs/reference/kubernetes-api/core/pod-v1/#volumes")]
        public IList<V1Volume>? Volumes { get; set; }

        [Description("""
                     Additional volume mounts of the bot container. MCC's working directory is `/data`, so mount paths
                     below it (e.g. `/data/files`) are accessible to scripts with relative paths.
                     """)]
        [ExternalDocs("https://kubernetes.io/docs/reference/kubernetes-api/core/pod-v1/#volumes-1")]
        public IList<V1VolumeMount>? VolumeMounts { get; set; }

        [Description("""
                     Affinity of the bot `Pod`.
                     """)]
        [ExternalDocs("https://kubernetes.io/docs/reference/kubernetes-api/core/pod-v1/#Affinity")]
        public V1Affinity? Affinity { get; set; }

        [Description("""
                     Name of the node the bot `Pod` is scheduled on.
                     """)]
        [ExternalDocs("https://kubernetes.io/docs/reference/kubernetes-api/core/pod-v1/#PodSpec")]
        public string? NodeName { get; set; }

        [Description("""
                     Node selector of the bot `Pod`.
                     """)]
        [ExternalDocs("https://kubernetes.io/docs/reference/kubernetes-api/core/pod-v1/#PodSpec")]
        public IDictionary<string, string>? NodeSelector { get; set; }

        [Description("""
                     Tolerations of the bot `Pod`.
                     """)]
        [ExternalDocs("https://kubernetes.io/docs/reference/kubernetes-api/core/pod-v1/#PodSpec")]
        public IList<V1Toleration>? Tolerations { get; set; }
    }
}
