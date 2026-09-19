using k8s.Models;

using KubeOps.Abstractions.Entities;
using KubeOps.Abstractions.Entities.Attributes;

namespace Kobblestone.Operator.Entities.V1Alpha1;

[Description("""
             A `BotBehavior` defines a piece of logic/behavior for Minecraft bots.
             """)]
[KubernetesEntity(Group = "kobblestone.io", ApiVersion = "v1alpha1", Kind = "BotBehavior")]
public sealed class
    V1Alpha1BotBehavior : CustomKubernetesEntity<V1Alpha1BotBehavior.V1Alpha1BotBehaviorSpec,
    V1Alpha1BotBehavior.V1Alpha1BotBehaviorStatus>
{
    public record V1Alpha1BotBehaviorSpec
    {
        [Description("""
                     Programs the bot behavior directly using code.
                     """)]
        public V1Alpha1BotBehaviorCode? Code { get; set; }

        [Description("""
                     Describes the behavior's parameters with a JSON schema.
                     """)]
        [PreserveUnknownFields]
        public V1JSONSchemaProps? ParametersSchema { get; set; }
    }

    public record V1Alpha1BotBehaviorStatus
    {
    }

    public record V1Alpha1BotBehaviorCode
    {
        [Description("""
                     Inline code of the behavior.
                     """)]
        public string? Inline { get; set; }
    }
}