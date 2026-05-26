namespace AllStak;

/// <summary>
/// A mutable, transport-agnostic view of an error or message event passed to
/// <see cref="AllStakOptions.BeforeSend"/> immediately before the event is
/// handed to the transport (and before the PII sanitizer runs).
///
/// <para>Mutating the fields here changes what is sent. Returning <c>null</c>
/// from <see cref="AllStakOptions.BeforeSend"/> drops the event entirely.</para>
/// </summary>
public sealed class AllStakEvent
{
    /// <summary>The kind of event — <c>"exception"</c> or <c>"message"</c>.</summary>
    public string EventType { get; }

    /// <summary>Exception type / class name (for exception events).</summary>
    public string? ExceptionClass { get; set; }

    /// <summary>The human-readable message / error message.</summary>
    public string Message { get; set; } = "";

    /// <summary>Severity level — e.g. <c>"error"</c>, <c>"warning"</c>, <c>"fatal"</c>.</summary>
    public string Level { get; set; } = "error";

    /// <summary>Associated trace id, if any.</summary>
    public string? TraceId { get; set; }

    /// <summary>
    /// Mutable metadata map sent with the event. Includes SDK release tags and
    /// any mechanism markers (e.g. <c>mechanism</c> / <c>handled</c> for global
    /// unhandled-exception capture).
    /// </summary>
    public Dictionary<string, object>? Metadata { get; set; }

    /// <summary>
    /// The original .NET exception when this is an exception event captured from
    /// a live <see cref="System.Exception"/>; <c>null</c> for message events or
    /// classless errors.
    /// </summary>
    public Exception? OriginalException { get; }

    internal AllStakEvent(string eventType, Exception? originalException = null)
    {
        EventType = eventType;
        OriginalException = originalException;
    }
}
