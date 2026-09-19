using k8s.Models;

using KubeOps.Abstractions.Entities;
using KubeOps.Abstractions.Entities.Attributes;

namespace Kobblestone.Operator.Entities.V1Alpha1;

[Description("""
             A `CommandGroup` manages a set of `Command` objects that are created/executed in sequential order.
             """)]
[KubernetesEntity(Group = "kobblestone.io", ApiVersion = "v1alpha1", Kind = "CommandGroup")]
public sealed class V1Alpha1CommandGroup : CustomKubernetesEntity<V1Alpha1CommandGroup.V1Alpha1CommandGroupSpec,
        V1Alpha1CommandGroup.V1Alpha1CommandGroupStatus>,
    IConditionsStatus<V1Alpha1CommandGroup.V1Alpha1CommandGroupStatus, V1Condition>
{
    public record V1Alpha1CommandGroupSpec
    {
        [Title("Target")]
        [Description("""
                     Reference to a target where the commands should be executed.

                     The following targets are supported:
                     - `Server`: Commands target a `Server` object.
                     - `Service`: Commands target a custom `Service` and port that exposes RCON.
                     """)]
        public V1Alpha1Command.V1Alpha1CommandTargetRef TargetRef { get; set; } = new();

        [Title("Commands")]
        [Description("""
                     List of commands that should be executed on the target.
                     """)]
        [Required]
        [RangeMinimum(1)]
        public IList<V1Alpha1CommandGroupCommand> Commands { get; set; } = null!;

        [Title("Timeout")]
        [Description("""
                     Timeout for the commands in milliseconds. Default is no timeout.

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

    public record V1Alpha1CommandGroupCommand
    {
        [Description("""
                     Raw command string that should be executed against the target via RCON.
                     """
        )]
        [Example("list")]
        [Required]
        public string Command { get; set; } = null!;

        [Description("""
                     Whether to ignore failures when executing the command.

                     If enabled, the `CommandGroup` will keep executing subsequent commands in case of failure.
                     Additionally, it will not have any negative impact o

                     Default is `false`.
                     """)]
        public bool? IgnoreFailure { get; set; }
    }

    public enum CommandResponseStatus
    {
        Completed,
        Failed,
        Skipped
    }

    public record V1Alpha1CommandGroupStatusCommand
    {
        [Description("""
                     Raw command that was executed.
                     """)]
        [Required]
        public string Command { get; set; } = null!;

        [Description("""
                     Status of the individual command response.

                     Values:
                     - `Completed`: The command was executed successfully.
                     - `Failed`: The command failed.
                     - `Skipped`: Command execution was skipped due to previous failure.
                     """)]
        public CommandResponseStatus Status { get; set; }

        [Description("""
                     Raw response of the server after executing the command.
                     """)]
        [Example("There are 0 of a max of 20 players online:")]
        public string? Response { get; set; }

        [Description("""
                     Reason of failure when command execution failed.
                     """)]
        [Example("Timeout")]
        public string? FailureReason { get; set; }
    }

    public enum CommandGroupPhase
    {
        Pending,
        Completed,
        Failed
    }

    public record V1Alpha1CommandGroupStatus : IConditions<V1Condition>
    {
        [Description("""
                     Current phase of the `CommandGroup`.

                     Values:
                     - `Pending`: Commands are pending or being executed.
                     - `Completed`: All commands executed successfully or failures have been explicitly ignored.
                     - `Failed`: A command has failed executing.
                     """)]
        [AdditionalPrinterColumn(name: "Status")]
        public CommandGroupPhase? Phase { get; set; }

        [Description("""
                     Message explaining the current state of the `CommandGroup`.
                     """)]
        [AdditionalPrinterColumn(name: "Message")]
        public string? Message { get; set; }

        [Description("""
                     Contains response information for every command that was executed (or not due to failure).
                     """)]
        public IList<V1Alpha1CommandGroupStatusCommand> Commands { get; set; } = [];

        [Description("""
                     Conditions of the `CommandGroup`.
                     """)]
        public IList<V1Condition> Conditions { get; set; } = [];
    }
}