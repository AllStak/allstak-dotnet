namespace AllStak;

/// <summary>
/// Context for an exhausted-retries transport failure. Surfaced via
/// <see cref="AllStakOptions.OnTransportError"/> so host apps can count
/// lost telemetry events in their own metrics pipeline. (P0-I)
/// </summary>
public sealed class TransportErrorContext
{
    public TransportErrorContext(string path, int lastStatus, Exception? lastException, int attempts)
    {
        Path = path;
        LastStatus = lastStatus;
        LastException = lastException;
        Attempts = attempts;
    }

    /// <summary>Ingest path that failed, e.g. <c>"/ingest/v1/errors"</c>.</summary>
    public string Path { get; }

    /// <summary>Last HTTP status seen (0 if no response was received).</summary>
    public int LastStatus { get; }

    /// <summary>Last exception caught (HttpRequestException, TaskCanceledException, etc.).</summary>
    public Exception? LastException { get; }

    /// <summary>Total number of attempts made.</summary>
    public int Attempts { get; }
}
