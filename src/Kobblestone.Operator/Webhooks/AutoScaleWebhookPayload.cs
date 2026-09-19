using System.Text.Json.Serialization;

namespace Kobblestone.Operator.Webhooks;

public record AutoScaleWebhookPayload
{
    [JsonPropertyName("action")] public string Action { get; set; } = null!;
}