namespace AllStak.Session;

/// <summary>
/// A single release-health session. One-per-process in the default
/// single-mode deployment. Status / error-count mutate through interlocked
/// operations so multiple threads can mark the session errored / crashed
/// without locking.
/// </summary>
public sealed class Session
{
    private int _status = (int)SessionStatus.Ok;
    private int _errorCount;

    /// <summary>Stable session id, attached to every captured error/event payload.</summary>
    public string Id { get; }

    /// <summary>UTC instant the session became active.</summary>
    public DateTimeOffset StartedAt { get; }

    /// <summary>Current accumulated status.</summary>
    public SessionStatus Status => (SessionStatus)Volatile.Read(ref _status);

    /// <summary>Number of errors (handled + crash) recorded against this session.</summary>
    public int ErrorCount => Volatile.Read(ref _errorCount);

    /// <summary>Create a session with a fresh id starting now.</summary>
    public Session() : this(Guid.NewGuid().ToString(), DateTimeOffset.UtcNow) { }

    /// <summary>Create a session with an explicit id / start time (test seam).</summary>
    public Session(string id, DateTimeOffset startedAt)
    {
        Id = id;
        StartedAt = startedAt;
    }

    /// <summary>
    /// Increment the error counter and bump status to <see cref="SessionStatus.Errored"/>
    /// unless the session has already escalated to a terminal status.
    /// </summary>
    public void RecordError()
    {
        Interlocked.Increment(ref _errorCount);
        // Only promote OK -> ERRORED; never downgrade a terminal status.
        Interlocked.CompareExchange(ref _status, (int)SessionStatus.Errored, (int)SessionStatus.Ok);
    }

    /// <summary>Mark a terminal crashed status (overrides errored). Used by the uncaught handler.</summary>
    public void RecordCrash()
    {
        Volatile.Write(ref _status, (int)SessionStatus.Crashed);
        Interlocked.Increment(ref _errorCount);
    }

    /// <summary>Promote to <see cref="SessionStatus.Abnormal"/> only if still OK or ERRORED.</summary>
    public void RecordAbnormalExit()
    {
        Interlocked.CompareExchange(ref _status, (int)SessionStatus.Abnormal, (int)SessionStatus.Ok);
        Interlocked.CompareExchange(ref _status, (int)SessionStatus.Abnormal, (int)SessionStatus.Errored);
    }

    /// <summary>Duration from start to now in milliseconds, floored at 0.</summary>
    public long DurationMs()
    {
        var d = (long)(DateTimeOffset.UtcNow - StartedAt).TotalMilliseconds;
        return d < 0 ? 0 : d;
    }
}
