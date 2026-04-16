using System.Text.Json.Serialization;

namespace AllStak.Models;

/// <summary>Single span for <c>POST /ingest/v1/spans</c>.</summary>
internal sealed class SpanPayload
{
    [JsonPropertyName("traceId")]
    public string TraceId { get; set; } = "";

    [JsonPropertyName("spanId")]
    public string SpanId { get; set; } = "";

    [JsonPropertyName("parentSpanId")]
    public string ParentSpanId { get; set; } = "";

    [JsonPropertyName("operation")]
    public string Operation { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("status")]
    public string Status { get; set; } = "ok"; // ok | error | timeout

    [JsonPropertyName("durationMs")]
    public long DurationMs { get; set; }

    [JsonPropertyName("startTimeMillis")]
    public long StartTimeMillis { get; set; }

    [JsonPropertyName("endTimeMillis")]
    public long EndTimeMillis { get; set; }

    [JsonPropertyName("service")]
    public string Service { get; set; } = "";

    [JsonPropertyName("environment")]
    public string Environment { get; set; } = "";

    [JsonPropertyName("tags")]
    public Dictionary<string, string> Tags { get; set; } = new();

    [JsonPropertyName("data")]
    public string Data { get; set; } = "";
}

internal sealed class SpanBatch
{
    [JsonPropertyName("spans")]
    public List<SpanPayload> Spans { get; set; } = new();
}
