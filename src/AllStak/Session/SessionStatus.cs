namespace AllStak.Session;

/// <summary>
/// Lifecycle status of a release-health session.
///
/// <para>Vocabulary matches the AllStak backend's <c>/ingest/v1/sessions/end</c>
/// contract and Sentry's release-health conventions:</para>
///
/// <list type="bullet">
///   <item><see cref="Ok"/> — session ended normally with at most non-fatal logs.</item>
///   <item><see cref="Errored"/> — at least one captured event of level <c>error</c>
///       or higher landed during the session, but the process kept running.</item>
///   <item><see cref="Crashed"/> — an unhandled / fatal exception ended the process
///       (the SDK only reports this when it observes the uncaught exception itself).</item>
///   <item><see cref="Abnormal"/> — process ended without a normal flush. Reserved for
///       future shutdown telemetry.</item>
/// </list>
/// </summary>
public enum SessionStatus
{
    /// <summary>Session ended normally.</summary>
    Ok,
    /// <summary>At least one handled error landed during the session.</summary>
    Errored,
    /// <summary>An unhandled / fatal exception ended the process.</summary>
    Crashed,
    /// <summary>Process ended without a normal flush.</summary>
    Abnormal,
}

/// <summary>Maps <see cref="SessionStatus"/> values to their lower-case wire strings.</summary>
internal static class SessionStatusExtensions
{
    /// <summary>Backend wire value — lower-case string the <c>/sessions/end</c> DTO expects.</summary>
    public static string WireValue(this SessionStatus status) => status switch
    {
        SessionStatus.Ok => "ok",
        SessionStatus.Errored => "errored",
        SessionStatus.Crashed => "crashed",
        SessionStatus.Abnormal => "abnormal",
        _ => "ok",
    };
}
