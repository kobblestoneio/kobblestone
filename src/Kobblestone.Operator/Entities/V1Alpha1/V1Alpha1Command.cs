using k8s.Models;

using KubeOps.Abstractions.Entities;
using KubeOps.Abstractions.Entities.Attributes;

namespace Kobblestone.Operator.Entities.V1Alpha1;

[Description("""
             A `Command` object executes an arbitrary command on a Minecraft server via RCON.

             Commands are executed **directly by the operator** with zero overhead.
             Therefore, no additional resources such as `Job`/`Pod` will be allocated. 

             Target servers **must** have RCON enabled for commands to work (`spec.rcon.disabled=null|false`).

             The response of commands can be observed via `status.response`.
             """)]
[KubernetesEntity(Group = "kobblestone.io", ApiVersion = "v1alpha1", Kind = "Command")]
public sealed class V1Alpha1Command : CustomKubernetesEntity<V1Alpha1Command.V1Alpha1CommandSpec,
    V1Alpha1Command.V1Alpha1CommandStatus>
{
    public record V1Alpha1CommandSpec
    {
        [Title("Target")]
        [Description("""
                     Reference to a target where the command should be executed.

                     The following targets are supported:
                     - `Server`: Command targets a `Server` object.
                     - `Service`: Command target a custom `Service` and port that exposes RCON.
                     """)]
        public V1Alpha1CommandTargetRef TargetRef { get; set; } = new();

        [Title("Command")]
        [Description("""
                     Raw command string that should be executed against the target via RCON.
                     """
        )]
        [Example("list")]
        [Required]
        public string Command { get; set; } = null!;

        [Title("Timeout")]
        [Description("""
                     Timeout for the command in milliseconds. Default is no timeout.

                     The timeout is applied individually for opening the connection and executing the command.

                     When a command times out, `status.phase` will be `Failed` and `status.failureReason` will be `Timeout`. 
                     """)]
        [Example(5000)]
        [RangeMinimum(1)]
        public int? TimeoutMilliseconds { get; set; }

        [Title("Password")]
        [Description("""
                     Optional reference to a custom `Secret` holding the RCON password.

                     Automatically implied when targeting a `Server` object.
                     """)]
        public V1SecretKeyRef? PasswordSecretRef { get; set; }

        [Title("External Traffic")]
        [Description("""
                     Policy that determines how RCON traffic flows to the target.

                     Default is `Cluster`.

                     `Cluster` means that traffic will **always** be routed inside the cluster to known `ClusterIP`s.

                     `External` means that traffic may be routed to external IPs, such as `LoadBalancer` IPs.
                     This may be useful when direct connectivity from the operator to the target is not given.
                     **WARNING**: RCON connections are **unencrypted** and should **never** be routed through the internet.
                     Ensure your `LoadBalancer` is **not** exposed to the internet.
                     """)]
        public CommandTrafficPolicy? TrafficPolicy { get; set; }
    }

    public record V1Alpha1CommandStatus
    {
        [Description("""
                     Current phase of the command.

                     Values:
                     - `Pending`: Command execution is pending.
                     - `Completed`: Command executed successfully.
                     - `Failed`: Command execution failed.
                     """)]
        [AdditionalPrinterColumn(name: "Status")]
        public CommandPhase Phase { get; set; }

        [Description("""
                     Raw response of the server after executing the command.
                     """)]
        [Example("There are 0 of a max of 20 players online:")]
        [AdditionalPrinterColumn(name: "Response")]
        public string? Response { get; set; }

        [Description("""
                     Timestamp when the command was executed or execution was attempted.
                     """)]
        public DateTime? ExecutedAt { get; set; }

        [Description("""
                     Reason of failure when command execution failed.
                     """)]
        [Example("Timeout")]
        public string? FailureReason { get; set; }

        [Description("""
                     Message explaining the current state of the `Command`.
                     """)]
        [AdditionalPrinterColumn(name: "Message")]
        public string? Message { get; set; }

        [Description("""
                     Conditions of the `Command`.
                     """)]
        public IList<V1Condition> Conditions { get; set; } = [];
    }

    public record V1Alpha1CommandTargetRef
    {
        [Description("""
                     Name of the target object.
                     """)]
        [Example("my-server")]
        [AdditionalPrinterColumn(name: "Target")]
        [Required]
        public string Name { get; set; } = null!;

        [Description("""
                     Kind of the target object.

                     Supported values:
                     - `Server`
                     - `Service`

                     Default is `Server`.
                     """)]
        [Example("Server")]
        public string? Kind { get; set; }

        [Description("""
                     ApiVersion of the target object.
                     """)]
        public string? ApiVersion { get; set; }

        [Description("""
                     Target port that exposes RCON.

                     Default is `25575`.
                     """)]
        [RangeMinimum(1)]
        [RangeMaximum(ushort.MaxValue)]
        public int? Port { get; set; }
    }
}