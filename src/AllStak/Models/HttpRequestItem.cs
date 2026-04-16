using System.Text.Json.Serialization;

namespace AllStak.Models;

/// <summary>Single HTTP request/response record for <c>POST /ingest/v1/http-requests</c>.</summary>
internal sealed class HttpRequestItem
{
    [JsonPropertyName("traceId")]
    public string TraceId { get; set; } = "";

    [JsonPropertyName("direction")]
    public string Direction { get; set; } = "inbound"; // or "outbound"

    [JsonPropertyName("method")]
    public string Method { get; set; } = "GET";

    [JsonPropertyName("host")]
    public string Host { get; set; } = "";

    [JsonPropertyName("path")]
    public string Path { get; set; } = "/";

    [JsonPropertyName("statusCode")]
    public int StatusCode { get; set; }

    [JsonPropertyName("durationMs")]
    public long DurationMs { get; set; }

    [JsonPropertyName("requestSize")]
    public long RequestSize { get; set; }

    [JsonPropertyName("responseSize")]
    public long ResponseSize { get; set; }

    [JsonPropertyName("timestamp")]
    public string Timestamp { get; set; } = DateTime.UtcNow.ToString("o");

    [JsonPropertyName("userId")]
    public string? UserId { get; set; }

    [JsonPropertyName("errorFingerprint")]
    public string? ErrorFingerprint { get; set; }

    [JsonPropertyName("spanId")]
    public string? SpanId { get; set; }

    [JsonPropertyName("parentSpanId")]
    public string? ParentSpanId { get; set; }

    [JsonPropertyName("environment")]
    public string? Environment { get; set; }

    [JsonPropertyName("release")]
    public string? Release { get; set; }
}

/// <summary>Batch wrapper for HTTP request ingestion (max 100 items).</summary>
internal sealed class HttpRequestBatch
{
    [JsonPropertyName("requests")]
    public List<HttpRequestItem> Requests { get; set; } = new();
}
