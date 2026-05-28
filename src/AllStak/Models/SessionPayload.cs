using System.Text.Json.Serialization;

namespace AllStak.Models;

/// <summary>Payload for <c>POST /ingest/v1/sessions/start</c>.</summary>
internal sealed class SessionStartPayload
{
    [JsonPropertyName("sessionId")]
    public string SessionId { get; set; } = "";

    [JsonPropertyName("release")]
    public string Release { get; set; } = "";

    [JsonPropertyName("environment")]
    public string? Environment { get; set; }

    [JsonPropertyName("userId")]
    public string? UserId { get; set; }

    [JsonPropertyName("sdkName")]
    public string? SdkName { get; set; }

    [JsonPropertyName("sdkVersion")]
    public string? SdkVersion { get; set; }

    [JsonPropertyName("platform")]
    public string? Platform { get; set; }
}

/// <summary>Payload for <c>POST /ingest/v1/sessions/end</c>.</summary>
internal sealed class SessionEndPayload
{
    [JsonPropertyName("sessionId")]
    public string SessionId { get; set; } = "";

    [JsonPropertyName("durationMs")]
    public long DurationMs { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = "ok";
}
