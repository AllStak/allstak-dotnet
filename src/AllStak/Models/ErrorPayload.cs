using System.Text.Json.Serialization;

namespace AllStak.Models;

/// <summary>Payload for <c>POST /ingest/v1/errors</c>.</summary>
internal sealed class ErrorPayload
{
    [JsonPropertyName("exceptionClass")]
    public string ExceptionClass { get; set; } = "";

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";

    [JsonPropertyName("stackTrace")]
    public List<string>? StackTrace { get; set; }

    [JsonPropertyName("level")]
    public string Level { get; set; } = "error";

    [JsonPropertyName("environment")]
    public string? Environment { get; set; }

    [JsonPropertyName("release")]
    public string? Release { get; set; }

    [JsonPropertyName("sessionId")]
    public string? SessionId { get; set; }

    [JsonPropertyName("traceId")]
    public string? TraceId { get; set; }

    [JsonPropertyName("user")]
    public UserContext? User { get; set; }

    [JsonPropertyName("requestContext")]
    public RequestContext? RequestContext { get; set; }

    [JsonPropertyName("metadata")]
    public Dictionary<string, object>? Metadata { get; set; }

    [JsonPropertyName("breadcrumbs")]
    public List<Breadcrumb>? Breadcrumbs { get; set; }
}

/// <summary>Breadcrumb attached to the next captured error.</summary>
public sealed class Breadcrumb
{
    [JsonPropertyName("timestamp")]
    public string Timestamp { get; set; } = DateTime.UtcNow.ToString("o");

    [JsonPropertyName("type")]
    public string Type { get; set; } = "default";

    [JsonPropertyName("category")]
    public string? Category { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";

    [JsonPropertyName("level")]
    public string Level { get; set; } = "info";

    [JsonPropertyName("data")]
    public Dictionary<string, object?>? Data { get; set; }
}
