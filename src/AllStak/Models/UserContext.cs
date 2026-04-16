using System.Text.Json.Serialization;

namespace AllStak.Models;

/// <summary>Optional user context attached to captured events.</summary>
public sealed class UserContext
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("email")]
    public string? Email { get; set; }

    [JsonPropertyName("ip")]
    public string? Ip { get; set; }
}

/// <summary>Optional HTTP request context attached to error events.</summary>
public sealed class RequestContext
{
    [JsonPropertyName("method")]
    public string? Method { get; set; }

    [JsonPropertyName("path")]
    public string? Path { get; set; }

    [JsonPropertyName("host")]
    public string? Host { get; set; }

    [JsonPropertyName("statusCode")]
    public int? StatusCode { get; set; }

    [JsonPropertyName("userAgent")]
    public string? UserAgent { get; set; }
}
