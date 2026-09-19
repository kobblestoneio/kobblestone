using KubeOps.Abstractions.Entities.Attributes;

namespace Kobblestone.Operator.Entities;

public record V1SecretKeyRef
{
    [Description("""
                 Name of the `Secret`.
                 """)]
    [Required]
    public string Name { get; set; } = null!;

    [Description("""
                 Name of the key.
                 """)]
    [Required]
    public string Key { get; set; } = null!;
}