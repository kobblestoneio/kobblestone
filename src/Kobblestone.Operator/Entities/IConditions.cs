using k8s.Models;

namespace Kobblestone.Operator.Entities;

public interface IConditions<T>
    where T : V1Condition
{
    IList<T> Conditions { get; set; }
}