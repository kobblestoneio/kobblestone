using k8s.Models;

namespace Kobblestone.Operator.Conditions;

public static class AccountConditions
{
    public static IList<V1Condition> Default =>
    [
        Authorized.False(),
        AwaitingAuthorization.False()
    ];

    public static V1Condition Authorized => new() { Type = "Authorized", Status = "True" };

    public static V1Condition AwaitingAuthorization => new() { Type = "AwaitingAuthorization", Status = "True" };
}