using AllStak.Models;
using AllStak.Session;
using AllStak.Transport;
using Microsoft.Extensions.Logging;

namespace AllStak.Modules;

/// <summary>Captures exceptions and ships them to AllStak.</summary>
public sealed class ErrorModule
{
    private const string Path = "/ingest/v1/errors";
    private readonly HttpTransport _transport;
    private readonly AllStakOptions _options;
    private readonly ILogger _logger;
    private readonly object _breadcrumbLock = new();
    private readonly List<Breadcrumb> _breadcrumbs = new();
    private UserContext? _currentUser;

    /// <summary>
    /// Optional release-health session tracker. When set, the active session id
    /// is stamped on every error payload and the session's status is advanced
    /// (errored for handled captures, crashed for unhandled/fatal ones). Wired by
    /// <see cref="AllStakClient"/> after the tracker is created; null otherwise.
    /// </summary>
    internal SessionTracker? Session { get; set; }

    internal ErrorModule(HttpTransport transport, AllStakOptions options, ILogger logger)
    {
        _transport = transport;
        _options = options;
        _logger = logger;
    }

    /// <summary>Set a default user context attached to all subsequent error events.</summary>
    public void SetUser(string? id = null, string? email = null, string? ip = null)
    {
        _currentUser = new UserContext { Id = id, Email = email, Ip = ip };
    }

    /// <summary>Clear the current user context.</summary>
    public void ClearUser() => _currentUser = null;

    /// <summary>Id of the current user context, if any. Used to seed the session start envelope.</summary>
    internal string? CurrentUserId => _currentUser?.Id;

    /// <summary>Add a breadcrumb. Oldest are dropped beyond 50.</summary>
    public void AddBreadcrumb(string type, string message, string level = "info", Dictionary<string, object?>? data = null)
    {
        lock (_breadcrumbLock)
        {
            if (_breadcrumbs.Count >= 50)
                _breadcrumbs.RemoveAt(0);
            _breadcrumbs.Add(new Breadcrumb
            {
                Type = type,
                Message = message,
                Level = level,
                Data = data,
                Timestamp = DateTime.UtcNow.ToString("o"),
            });
        }
    }

    /// <summary>Capture a .NET exception and send it to AllStak. Never throws.</summary>
    public async Task<string?> CaptureExceptionAsync(
        Exception exc,
        string level = "error",
        UserContext? user = null,
        RequestContext? request = null,
        string? traceId = null,
        Dictionary<string, object>? metadata = null,
        CancellationToken ct = default)
    {
        if (_transport.IsDisabled) return null;
        try
        {
            List<Breadcrumb>? crumbs = null;
            lock (_breadcrumbLock)
            {
                if (_breadcrumbs.Count > 0)
                {
                    crumbs = new List<Breadcrumb>(_breadcrumbs);
                    _breadcrumbs.Clear();
                }
            }

            var payload = new ErrorPayload
            {
                ExceptionClass = exc.GetType().Name,
                Message = string.IsNullOrEmpty(exc.Message) ? exc.GetType().FullName ?? "" : exc.Message,
                StackTrace = ExtractFrames(exc),
                Level = level,
                Environment = _options.Environment,
                Release = _options.Release,
                SessionId = Session?.CurrentSessionId,
                TraceId = traceId,
                User = user ?? _currentUser,
                RequestContext = request,
                Metadata = MergeReleaseTags(metadata),
                Breadcrumbs = crumbs,
                // Phase 3 — v2 ingest contract: top-level identity + structured frames.
                SdkName = _options.SdkNameOverride ?? AllStak.AllStakOptions.SdkName,
                SdkVersion = _options.SdkVersionOverride ?? AllStak.AllStakOptions.SdkVersion,
                Platform = _options.Platform,
                Dist = _options.Dist,
                Frames = ExtractStructuredFrames(exc),
            };

            // Advance release-health session status: crashed for unhandled/fatal
            // captures (metadata handled=false), errored for handled ones.
            RecordSessionStatus(level, metadata);

            // SampleRate drop first, then BeforeSend. Sanitizer runs inside the
            // transport (PostAsync), so transport is strictly last.
            if (!ApplySamplingAndBeforeSend(payload, "exception", exc))
                return null;

            var (status, body) = await _transport.PostAsync(Path, payload, ct).ConfigureAwait(false);
            if (status == 202)
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(body);
                    if (doc.RootElement.TryGetProperty("data", out var data) &&
                        data.TryGetProperty("id", out var id))
                        return id.GetString();
                }
                catch { }
            }
            return null;
        }
        catch (AllStakAuthException) { return null; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[AllStak] capture_exception swallowed");
            return null;
        }
    }

    /// <summary>Capture an error by class + message without a .NET exception object.</summary>
    public async Task<string?> CaptureErrorAsync(
        string exceptionClass,
        string message,
        List<string>? stackTrace = null,
        string level = "error",
        UserContext? user = null,
        RequestContext? request = null,
        string? traceId = null,
        Dictionary<string, object>? metadata = null,
        CancellationToken ct = default)
    {
        if (_transport.IsDisabled) return null;
        try
        {
            List<Breadcrumb>? crumbs = null;
            lock (_breadcrumbLock)
            {
                if (_breadcrumbs.Count > 0)
                {
                    crumbs = new List<Breadcrumb>(_breadcrumbs);
                    _breadcrumbs.Clear();
                }
            }
            var payload = new ErrorPayload
            {
                ExceptionClass = exceptionClass,
                Message = message,
                StackTrace = stackTrace,
                Level = level,
                Environment = _options.Environment,
                Release = _options.Release,
                SessionId = Session?.CurrentSessionId,
                TraceId = traceId,
                User = user ?? _currentUser,
                RequestContext = request,
                // Merge release-tracking tags (sdk.name/version, platform, dist,
                // commit.sha/branch) under any caller-supplied entries so the
                // dashboard can group / filter by them on every event.
                Metadata = MergeReleaseTags(metadata),
                Breadcrumbs = crumbs,
            };

            RecordSessionStatus(level, metadata);

            if (!ApplySamplingAndBeforeSend(payload, "message", null))
                return null;

            var (status, _) = await _transport.PostAsync(Path, payload, ct).ConfigureAwait(false);
            return status == 202 ? exceptionClass : null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[AllStak] capture_error swallowed");
            return null;
        }
    }

    /// <summary>
    /// Advance the active release-health session's status for a captured event.
    /// An event is treated as a crash when it carries <c>handled = false</c>
    /// metadata (set by the global unhandled-exception handler) or a <c>fatal</c>
    /// level; otherwise it is a handled error and the session is marked errored.
    /// No-op when session tracking is disabled. Never throws.
    /// </summary>
    private void RecordSessionStatus(string level, Dictionary<string, object>? metadata)
    {
        var session = Session;
        if (session == null) return;
        try
        {
            var unhandled =
                string.Equals(level, "fatal", StringComparison.OrdinalIgnoreCase) ||
                (metadata != null
                 && metadata.TryGetValue("handled", out var handled)
                 && handled is bool b && !b);

            if (unhandled) session.RecordCrash();
            else session.RecordError();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[AllStak] session status update failed");
        }
    }

    /// <summary>
    /// Build the metadata map sent on every event. Release-tracking tags
    /// (sdk.name / sdk.version / platform / dist / commit.sha / commit.branch)
    /// flow in from <see cref="AllStakOptions.ReleaseTags"/>; caller metadata
    /// wins on key collision.
    /// </summary>
    private Dictionary<string, object>? MergeReleaseTags(Dictionary<string, object>? caller)
    {
        var tags = _options.ReleaseTags();
        if (tags.Count == 0) return caller;
        var merged = new Dictionary<string, object>(tags.Count + (caller?.Count ?? 0));
        foreach (var kv in tags) merged[kv.Key] = kv.Value;
        if (caller != null) foreach (var kv in caller) merged[kv.Key] = kv.Value;
        return merged;
    }

    /// <summary>
    /// Apply <see cref="AllStakOptions.SampleRate"/> (drop first) then
    /// <see cref="AllStakOptions.BeforeSend"/> to an outgoing error payload.
    /// Returns <c>false</c> when the event must be dropped. Mutations made by
    /// BeforeSend are copied back onto <paramref name="payload"/>. BeforeSend
    /// fails open: a throwing callback is logged and the original event is sent.
    /// </summary>
    private bool ApplySamplingAndBeforeSend(ErrorPayload payload, string eventType, Exception? original)
    {
        // 1) SampleRate — deterministic random drop at capture time.
        if (!_options.ShouldSampleEvent())
        {
            _logger.LogDebug("[AllStak] event dropped by SampleRate (type={Type})", eventType);
            return false;
        }

        // 2) BeforeSend — caller may mutate or drop (null).
        var beforeSend = _options.BeforeSend;
        if (beforeSend == null) return true;

        var evt = new AllStakEvent(eventType, original)
        {
            ExceptionClass = payload.ExceptionClass,
            Message = payload.Message,
            Level = payload.Level,
            TraceId = payload.TraceId,
            Metadata = payload.Metadata,
        };

        AllStakEvent? result;
        try
        {
            result = beforeSend(evt);
        }
        catch (Exception ex)
        {
            // Fail open: send the original event unchanged.
            _logger.LogWarning(ex, "[AllStak] BeforeSend threw; sending original event (type={Type})", eventType);
            return true;
        }

        if (result == null)
        {
            _logger.LogDebug("[AllStak] event dropped by BeforeSend (type={Type})", eventType);
            return false;
        }

        // Copy back mutable fields.
        payload.ExceptionClass = result.ExceptionClass ?? payload.ExceptionClass;
        payload.Message = result.Message;
        payload.Level = result.Level;
        payload.TraceId = result.TraceId;
        payload.Metadata = result.Metadata;
        return true;
    }

    private static List<string> ExtractFrames(Exception exc)
    {
        var frames = new List<string>();
        var st = exc.StackTrace;
        if (!string.IsNullOrEmpty(st))
        {
            foreach (var line in st.Split('\n'))
            {
                var trimmed = line.TrimEnd('\r').Trim();
                if (!string.IsNullOrEmpty(trimmed))
                    frames.Add(trimmed);
            }
        }
        return frames;
    }

    /// <summary>Phase 3 — structured frames via System.Diagnostics.StackTrace.
    /// Each .NET frame already has method, file, line — we just package them.</summary>
    private static List<Frame> ExtractStructuredFrames(Exception exc)
    {
        var out_ = new List<Frame>();
        try
        {
            var st = new System.Diagnostics.StackTrace(exc, fNeedFileInfo: true);
            foreach (var f in st.GetFrames())
            {
                var m = f.GetMethod();
                if (m == null) continue;
                var fileName = f.GetFileName();
                var typeName = m.DeclaringType?.FullName;
                var fullFn = typeName != null ? $"{typeName}.{m.Name}" : m.Name;
                bool inApp = !(typeName != null && (
                    typeName.StartsWith("System.") || typeName.StartsWith("Microsoft.") ||
                    typeName.StartsWith("AllStak.")));
                out_.Add(new Frame
                {
                    Filename = fileName,
                    AbsPath  = fileName,
                    Function = fullFn,
                    Lineno   = f.GetFileLineNumber(),
                    Colno    = f.GetFileColumnNumber(),
                    InApp    = inApp,
                    Platform = "dotnet",
                });
                if (out_.Count >= 50) break;
            }
        }
        catch { /* best-effort */ }
        return out_;
    }
}
