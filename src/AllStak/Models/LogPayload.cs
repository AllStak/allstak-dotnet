using System.Text.Json.Serialization;

namespace AllStak.Models;

/// <summary>Payload for <c>POST /ingest/v1/logs</c>.</summary>
internal sealed class LogPayload
{
    [JsonPropertyName("level")]
    public string Level { get; set; } = "info";  // debug|info|warn|error|fatal (NOT "warning")

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";

    [JsonPropertyName("service")]
    public string? Service { get; set; }

    [JsonPropertyName("environment")]
    public string? Environment { get; set; }

    [JsonPropertyName("traceId")]
    public string? TraceId { get; set; }

    [JsonPropertyName("spanId")]
    public string? SpanId { get; set; }

    [JsonPropertyName("requestId")]
    public string? RequestId { get; set; }

    [JsonPropertyName("userId")]
    public string? UserId { get; set; }

    [JsonPropertyName("errorId")]
    public string? ErrorId { get; set; }

    [JsonPropertyName("metadata")]
    public Dictionary<string, object>? Metadata { get; set; }
}
