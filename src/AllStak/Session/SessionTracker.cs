using AllStak.Models;
using AllStak.Transport;
using Microsoft.Extensions.Logging;

namespace AllStak.Session;

/// <summary>
/// Server-mode "single session" tracker (Sentry-style release health).
///
/// <para>On <see cref="Start()"/> the SDK posts a <c>sessions/start</c> envelope
/// with the process's distinct id, the resolved release, and an SDK identifier.
/// On <see cref="End"/> it posts <c>sessions/end</c> with the final status + total
/// duration. Errored / crashed transitions are recorded in-memory; only the
/// terminal call carries the status so per-error latency stays unaffected.</para>
///
/// <para>One instance per <see cref="AllStakClient"/>. Re-entrancy safe: once
/// started a second <see cref="Start()"/> is a no-op; once ended the tracker does
/// not re-arm. Fully fail-open — network failures never propagate to the caller.</para>
/// </summary>
internal sealed class SessionTracker
{
    private const string PathStart = "/ingest/v1/sessions/start";
    private const string PathEnd = "/ingest/v1/sessions/end";

    private readonly AllStakOptions _options;
    private readonly HttpTransport _transport;
    private readonly ILogger _logger;

    private Session? _active;
    private readonly object _lock = new();
    private volatile bool _ended;

    public SessionTracker(AllStakOptions options, HttpTransport transport, ILogger logger)
    {
        _options = options;
        _transport = transport;
        _logger = logger;
    }

    /// <summary>The active session, or <c>null</c> if not started or already ended.</summary>
    public Session? Current => _ended ? null : Volatile.Read(ref _active);

    /// <summary>
    /// The id of the active session, or <c>null</c> when no session is open.
    /// Attached to every captured error/event payload so the backend's error
    /// consumer can mark the session errored / crashed server-side.
    /// </summary>
    public string? CurrentSessionId => Current?.Id;

    /// <summary>Idempotent. Same as <see cref="Start()"/> with no user id.</summary>
    public Session Start() => Start(null);

    /// <summary>
    /// Idempotent. Returns the session that became active (or the existing one).
    /// The <c>/sessions/start</c> POST runs on a background task so SDK init never
    /// blocks the host application's startup on a network round-trip. Attaches
    /// <paramref name="userId"/> to the start envelope when a user is set at init.
    ///
    /// <para>Release-health sessions are <b>never sampled</b>: the start POST is
    /// always attempted (subject only to the transport being enabled). When no
    /// release is resolved the SDK falls back to the SDK version so the session is
    /// still attributable rather than dropped.</para>
    /// </summary>
    public Session Start(string? userId)
    {
        var candidate = new Session();
        lock (_lock)
        {
            if (_ended || _active != null)
                return _active ?? candidate;
            _active = candidate;
        }

        if (_transport.IsDisabled)
        {
            // Transport explicitly disabled (e.g. missing/blank key). Keep the
            // in-memory tracker so errored/crashed transitions still set a
            // sensible final status, but skip the network call.
            return candidate;
        }

        var payload = new SessionStartPayload
        {
            SessionId = candidate.Id,
            Release = ResolveRelease(),
            Environment = _options.Environment,
            UserId = userId,
            SdkName = _options.SdkNameOverride ?? AllStakOptions.SdkName,
            SdkVersion = _options.SdkVersionOverride ?? AllStakOptions.SdkVersion,
            Platform = _options.Platform,
        };

        _ = Task.Run(async () =>
        {
            try
            {
                await _transport.PostAsync(PathStart, payload).ConfigureAwait(false);
                _logger.LogDebug("[AllStak] session started: {SessionId}", candidate.Id);
            }
            catch (Exception ex)
            {
                // Network failure must not crash app boot.
                _logger.LogDebug(ex, "[AllStak] session start failed");
            }
        });
        return candidate;
    }

    /// <summary>Record a handled error against the active session. No I/O.</summary>
    public void RecordError() => Current?.RecordError();

    /// <summary>Record a crash. No I/O — the end-of-session POST carries the status.</summary>
    public void RecordCrash() => Current?.RecordCrash();

    /// <summary>
    /// Terminate the session and POST <c>sessions/end</c>. Idempotent and
    /// best-effort. If <paramref name="finalStatus"/> is <c>null</c>, the session's
    /// own accumulated status is used (Ok / Errored / Crashed / Abnormal). The
    /// network call is bounded so it never blocks process shutdown indefinitely.
    /// </summary>
    public void End(SessionStatus? finalStatus = null)
    {
        Session? s;
        lock (_lock)
        {
            if (_ended) return;
            s = _active;
            _active = null;
            _ended = true;
        }
        if (s == null) return;

        var status = finalStatus ?? s.Status;
        if (_transport.IsDisabled) return;

        var payload = new SessionEndPayload
        {
            SessionId = s.Id,
            DurationMs = s.DurationMs(),
            Status = status.WireValue(),
        };

        try
        {
            // Bounded so a hung connection cannot stall ProcessExit / Dispose.
            var timeout = TimeSpan.FromMilliseconds(Math.Max(0, _options.ShutdownFlushTimeoutMs));
            using var cts = new CancellationTokenSource(timeout);
            _transport.PostAsync(PathEnd, payload, cts.Token).GetAwaiter().GetResult();
            _logger.LogDebug("[AllStak] session ended: {SessionId} status={Status} errors={Errors}",
                s.Id, payload.Status, s.ErrorCount);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[AllStak] session end failed");
        }
    }

    /// <summary>
    /// The release identifier carried on the session envelope. Falls back to the
    /// SDK version when no release was resolved so a release-health session is
    /// never dropped for lack of a release (the <c>/sessions/start</c> contract
    /// requires a non-null release).
    /// </summary>
    private string ResolveRelease()
    {
        var release = _options.Release;
        if (!string.IsNullOrWhiteSpace(release)) return release!;
        return _options.SdkVersionOverride ?? AllStakOptions.SdkVersion;
    }
}
