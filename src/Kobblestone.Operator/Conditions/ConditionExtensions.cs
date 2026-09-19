using k8s;
using k8s.Models;

using Kobblestone.Operator.Entities;

namespace Kobblestone.Operator.Conditions;

public static class ConditionExtensions
{
    extension(V1Condition condition)
    {
        public V1Condition WithReason(string reason)
        {
            condition.Reason = reason;

            return condition;
        }

        public V1Condition WithMessage(string message)
        {
            condition.Message = message;

            return condition;
        }

        public V1Condition WithObservedGeneration(long? observedGeneration)
        {
            condition.ObservedGeneration = observedGeneration;

            return condition;
        }

        public V1Condition WithStatus(string status)
        {
            condition.Status = status;

            return condition;
        }

        public V1Condition True()
            => condition.WithStatus("True");

        public V1Condition False()
            => condition.WithStatus("False");

        public V1Condition Unknown()
            => condition.WithStatus("Unknown");
    }

    extension<TStatus, TCondition>(IConditionsStatus<TStatus, TCondition> entity)
        where TStatus : IConditions<TCondition> where TCondition : V1Condition
    {
        public bool HasAnyCondition(params TCondition[] conditions)
            => conditions.Any(entity.HasCondition);

        public bool HasCondition(TCondition condition)
            => entity.HasCondition(condition.Type, condition.Status, condition.Reason);

        public bool HasCondition(string type, string? status = null, string? reason = null)
            => entity.Status.Conditions.Any(c =>
                c.Type == type && (status is null || c.Status == status) && (reason is null || c.Reason == reason));

        public TCondition? GetCondition(TCondition condition)
            => entity.GetCondition(condition.Type);

        public TCondition? GetCondition(string type)
            => entity.Status.Conditions.FirstOrDefault(c => c.Type == type);

        public void WithCondition(TCondition condition)
        {
            var existing = entity.Status.Conditions.FirstOrDefault(c => c.Type == condition.Type);

            var generation = condition.ObservedGeneration ?? (entity as IKubernetesObject<V1ObjectMeta>)?.Generation();

            var lastTransitionTime =
                condition.LastTransitionTime == default ? DateTime.UtcNow : condition.LastTransitionTime;

            if (existing is null)
            {
                entity.Status.Conditions.Add(condition with
                {
                    LastTransitionTime = lastTransitionTime, ObservedGeneration = generation
                });

                return;
            }

            if (existing.Status == condition.Status)
            {
                return;
            }

            existing.Status = condition.Status;
            existing.LastTransitionTime = lastTransitionTime;
            existing.ObservedGeneration = generation;
            existing.Message = condition.Message;
            existing.Reason = condition.Reason;
        }

        public void WithConditions(params TCondition[] conditions)
            => entity.WithConditions((IEnumerable<TCondition>)conditions);

        public void WithConditions(IEnumerable<TCondition> conditions)
        {
            foreach (var condition in conditions)
            {
                entity.WithCondition(condition);
            }
        }
    }
}