using k8s.Models;

namespace Kobblestone.Operator.Entities;

public interface IConditionsStatus<TStatus, TCondition> : IStatus<TStatus>
    where TStatus : IConditions<TCondition> where TCondition : V1Condition;