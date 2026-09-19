using k8s.Models;

namespace Kobblestone.Operator.Conditions;

public static class ServerConditions
{
    public static IList<V1Condition> Default =>
    [
        Stopped,
        Progressing.False(),
        Running.False(),
        Error.False(),
        Hibernating.False(),
        BackupInProgress.False(),
        RestoreInProgress.False(),
        RconAvailable.False()
    ];

    public static V1Condition Running => new() { Type = "Running", Status = "True" };

    public static V1Condition Stopped => new() { Type = "Stopped", Status = "True" };

    public static V1Condition Progressing => new() { Type = "Progressing", Status = "True" };

    public static V1Condition Error => new() { Type = "Error", Status = "True" };

    public static V1Condition Hibernating => new() { Type = "Hibernating", Status = "True" };

    public static V1Condition BackupInProgress => new() { Type = "BackupInProgress", Status = "True" };

    public static V1Condition RestoreInProgress => new() { Type = "RestoreInProgress", Status = "True" };

    public static V1Condition RconAvailable => new() { Type = "RconAvailable", Status = "True" };
}