using System.Text.Json.Serialization;

namespace AllStak.Models;

/// <summary>DB query record for <c>POST /ingest/v1/db</c>.</summary>
internal sealed class DbQueryPayload
{
    [JsonPropertyName("normalizedQuery")]
    public string NormalizedQuery { get; set; } = "";

    [JsonPropertyName("queryHash")]
    public string QueryHash { get; set; } = "";

    [JsonPropertyName("queryType")]
    public string QueryType { get; set; } = "OTHER";

    [JsonPropertyName("durationMs")]
    public long DurationMs { get; set; }

    [JsonPropertyName("timestampMillis")]
    public long TimestampMillis { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = "success";

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }

    [JsonPropertyName("databaseName")]
    public string? DatabaseName { get; set; }

    [JsonPropertyName("databaseType")]
    public string? DatabaseType { get; set; }

    [JsonPropertyName("service")]
    public string? Service { get; set; }

    [JsonPropertyName("environment")]
    public string? Environment { get; set; }

    [JsonPropertyName("traceId")]
    public string? TraceId { get; set; }

    [JsonPropertyName("spanId")]
    public string? SpanId { get; set; }

    [JsonPropertyName("rowsAffected")]
    public int RowsAffected { get; set; }
}

internal sealed class DbQueryBatch
{
    [JsonPropertyName("queries")]
    public List<DbQueryPayload> Queries { get; set; } = new();
}
