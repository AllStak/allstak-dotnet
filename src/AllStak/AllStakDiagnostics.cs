namespace AllStak;

/// <summary>Privacy-safe SDK counters and queue sizes. Contains no telemetry payload data.</summary>
public sealed class AllStakDiagnostics
{
    public long EventsCaptured { get; init; }
    public long EventsSent { get; init; }
    public long EventsFailed { get; init; }
    public long EventsDropped { get; init; }
    public long EventsPersisted { get; init; }
    public long EventsReplayed { get; init; }
    public int QueueSize { get; init; }
    public long RetryAttempts { get; init; }
    public long RateLimitedCount { get; init; }
    public long CompressedPayloads { get; init; }
    public long UncompressedPayloads { get; init; }
    public long CompressionBytesSaved { get; init; }
    public long? SanitizerRedactionCount { get; init; }
    public int ActiveTraceCount { get; init; }
    public int ActiveSpanCount { get; init; }
    public int BreadcrumbCount { get; init; }
    public long SessionRecoveryCount { get; init; }
    public bool Disabled { get; init; }
}
