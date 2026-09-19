using k8s.Models;

using Kobblestone.Operator.Conditions;
using Kobblestone.Operator.Entities.V1Alpha1;

namespace Kobblestone.Operator.Utils;

public static class ServerExtensions
{
    public static ServerPhase GetPhase(this V1Alpha1Server server)
    {
        if (server.HasCondition(ServerConditions.Hibernating))
        {
            return ServerPhase.Hibernating;
        }

        if (server.HasCondition(ServerConditions.Progressing.WithReason("Starting")))
        {
            return ServerPhase.Starting;
        }

        if (server.HasCondition(ServerConditions.Progressing.WithReason("Stopping")))
        {
            return ServerPhase.Stopping;
        }

        if (server.HasCondition(ServerConditions.Stopped))
        {
            return ServerPhase.Stopped;
        }

        if (server.HasCondition(ServerConditions.Running))
        {
            return ServerPhase.Running;
        }

        return ServerPhase.Stopped;
    }
}

public static class ServerUtils
{
    public static IEnumerable<V1Condition> GetConditions(V1StatefulSet ss)
        => ss switch
        {
            { Spec.Replicas: 0, Status.Replicas: > 0 } =>
            [
                ServerConditions.Running.False(),
                ServerConditions.Progressing.WithReason("Stopping"),
                ServerConditions.Stopped.False()
            ],
            { Spec.Replicas: 0, Status.Replicas: 0 } =>
            [
                ServerConditions.Running.False(),
                ServerConditions.Progressing.False(),
                ServerConditions.Stopped
            ],
            { Spec.Replicas: 1, Status.ReadyReplicas: > 0 } =>
            [
                ServerConditions.Running.True(),
                ServerConditions.Progressing.False(),
                ServerConditions.Stopped.False()
            ],
            { Spec.Replicas: 1, Status.ReadyReplicas: not > 0 } =>
            [
                ServerConditions.Running.False(),
                ServerConditions.Progressing.WithReason("Starting"),
                ServerConditions.Stopped.False()
            ],
            _ => []
        };
}