namespace AllStak;

/// <summary>
/// Configuration options for the AllStak SDK.
/// </summary>
public class AllStakOptions
{
    /// <summary>
    /// API key sent as <c>X-AllStak-Key</c>. Required.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Base URL of the AllStak backend (without trailing slash).
    /// Default: <c>http://localhost:8080</c>. Set to your production ingest host in prod.
    /// </summary>
    public string Host { get; set; } = "http://localhost:8080";

    /// <summary>
    /// Deployment environment, e.g. <c>"production"</c>, <c>"staging"</c>.
    /// </summary>
    public string? Environment { get; set; }

    /// <summary>
    /// App version or release tag, e.g. <c>"taskflow@1.4.2"</c>.
    /// </summary>
    public string? Release { get; set; }

    /// <summary>
    /// Logical service name attached to spans and logs.
    /// </summary>
    public string ServiceName { get; set; } = "dotnet-service";

    /// <summary>
    /// Background flush interval in milliseconds. Default 2000 ms.
    /// </summary>
    public int FlushIntervalMs { get; set; } = 2_000;

    /// <summary>
    /// Maximum number of items held per feature buffer. Default 500.
    /// </summary>
    public int BufferSize { get; set; } = 500;

    /// <summary>
    /// Enable verbose SDK debug logging to ILogger. Default false.
    /// </summary>
    public bool Debug { get; set; } = false;

    /// <summary>
    /// Connect timeout (ms) for transport calls. Default 3000 ms.
    /// </summary>
    public int ConnectTimeoutMs { get; set; } = 3_000;

    /// <summary>
    /// Read timeout (ms) for transport calls. Default 3000 ms.
    /// </summary>
    public int ReadTimeoutMs { get; set; } = 3_000;

    /// <summary>
    /// Maximum transport attempts per event. Default 5.
    /// </summary>
    public int MaxRetries { get; set; } = 5;

    /// <summary>
    /// If <c>true</c>, automatically capture unhandled exceptions from ASP.NET Core routes.
    /// </summary>
    public bool CaptureUnhandledExceptions { get; set; } = true;

    /// <summary>
    /// If <c>true</c>, automatically capture inbound HTTP request telemetry via the middleware.
    /// </summary>
    public bool CaptureHttpRequests { get; set; } = true;

    /// <summary>
    /// If <c>true</c>, attach user context from <c>HttpContext.User</c> to captured events.
    /// </summary>
    public bool CaptureUserContext { get; set; } = true;
}
