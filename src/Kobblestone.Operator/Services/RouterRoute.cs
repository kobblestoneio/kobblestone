using System.Text.Json.Serialization;

namespace Kobblestone.Operator.Services;

public record RouterRoute
{
    [JsonPropertyName("backend")] public string Backend { get; set; } = null!;

    [JsonPropertyName("scalingTarget")] public string ScalingTarget { get; set; } = null!;
}