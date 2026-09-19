using k8s.Models;

namespace Kobblestone.Operator.Conditions;

public static class BackupConditions
{
    public static IList<V1Condition> Default =>
    [
        Completed.False(),
        Failed.False(),
        Progressing.False()
    ];

    public static V1Condition Completed => new() { Type = "Completed", Status = "True" };

    public static V1Condition Failed => new() { Type = "Failed", Status = "True" };

    public static V1Condition Progressing => new() { Type = "Progressing", Status = "True" };
}