using System.Text.Json.Serialization;

namespace AllStak.Models;

/// <summary>Payload for <c>POST /ingest/v1/heartbeat</c>.</summary>
internal sealed class HeartbeatPayload
{
    [JsonPropertyName("slug")]
    public string Slug { get; set; } = "";

    [JsonPropertyName("status")]
    public string Status { get; set; } = "success"; // success | failed

    [JsonPropertyName("durationMs")]
    public long DurationMs { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }
}
