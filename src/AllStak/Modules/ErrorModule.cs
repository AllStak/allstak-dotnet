using AllStak.Models;
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
                TraceId = traceId,
                User = user ?? _currentUser,
                RequestContext = request,
                // Merge release-tracking tags (sdk.name/version, platform, dist,
                // commit.sha/branch) under any caller-supplied entries so the
                // dashboard can group / filter by them on every event.
                Metadata = MergeReleaseTags(metadata),
                Breadcrumbs = crumbs,
            };
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
