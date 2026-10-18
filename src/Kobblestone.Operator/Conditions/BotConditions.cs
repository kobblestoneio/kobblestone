using k8s.Models;

namespace Kobblestone.Operator.Conditions;

public static class BotConditions
{
    public static V1Condition Connected => new() { Type = "Connected", Status = "True" };
}
