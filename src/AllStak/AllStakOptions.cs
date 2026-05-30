namespace AllStak;

/// <summary>
/// Configuration options for the AllStak SDK.
/// </summary>
public class AllStakOptions
{
    /// <summary>SDK identity sent on the wire as <c>sdk.name</c> / <c>sdk.version</c>.</summary>
    public const string SdkName = "allstak-dotnet";

    /// <summary>
    /// SDK version reported on the wire as <c>sdk.version</c>. Sourced from the
    /// assembly's informational version (driven by the csproj <c>&lt;Version&gt;</c>)
    /// so it can never drift from the published package. Falls back to a constant
    /// if the attribute is unavailable.
    /// </summary>
    public static readonly string SdkVersion = ResolveSdkVersion();

    private static string ResolveSdkVersion()
    {
        var asm = typeof(AllStakOptions).Assembly;
        // InformationalVersion mirrors csproj <Version> (e.g. "0.1.2"); it may
        // carry a "+<commit>" SourceLink suffix, which we strip for the wire.
        var info = asm
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            is object[] attrs && attrs.Length > 0
            ? ((System.Reflection.AssemblyInformationalVersionAttribute)attrs[0]).InformationalVersion
            : null;
        if (!string.IsNullOrEmpty(info))
        {
            var plus = info!.IndexOf('+');
            return plus >= 0 ? info[..plus] : info;
        }
        // Fall back to the file version (e.g. "0.1.2.0" → "0.1.2").
        var ver = asm.GetName().Version;
        if (ver != null)
            return $"{ver.Major}.{ver.Minor}.{ver.Build}";
        return "0.1.2";
    }


    /// <summary>
    /// API key sent as <c>X-AllStak-Key</c>. Required.
    /// Static convenience: if <see cref="ApiKeyProvider"/> is also set,
    /// the provider wins (used for rotation without restart).
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Optional dynamic API key provider for rotation without restart.
    /// Called per request when set. Return the current key; the transport
    /// reads from environment / vault / KMS each call (cheap in practice
    /// since most providers cache). If null, the static <see cref="ApiKey"/>
    /// is used. (P0-H)
    /// </summary>
    public Func<string>? ApiKeyProvider { get; set; }

    /// <summary>
    /// Invoked when the HTTP transport exhausts retries for a request.
    /// The SDK never re-throws to the caller — this callback is the
    /// supported way to observe lost telemetry events so they can be
    /// counted in your own metrics / logging pipeline. (P0-I)
    /// </summary>
    public Action<TransportErrorContext>? OnTransportError { get; set; }

    /// <summary>
    /// Base URL of the AllStak backend (without trailing slash).
    /// Default: <c>https://api.allstak.sa</c>. Override for self-hosted or local-dev use.
    /// </summary>
    public string Host { get; set; } = "https://api.allstak.sa";

    /// <summary>
    /// Deployment environment, e.g. <c>"production"</c>, <c>"staging"</c>.
    /// </summary>
    public string? Environment { get; set; }

    /// <summary>
    /// App version or release tag, e.g. <c>"taskflow@1.4.2"</c>.
    /// </summary>
    public string? Release { get; set; }

    /// <summary>
    /// Register the resolved release with AllStak at runtime startup via
    /// <c>/ingest/v1/releases</c>, without requiring CI/CD integration.
    /// Best-effort and fail-open. Default true.
    /// </summary>
    public bool AutoRegisterRelease { get; set; } = true;

    /// <summary>
    /// Enable release-health session tracking: one session per
    /// process / app-launch. On init the SDK posts <c>/ingest/v1/sessions/start</c>
    /// and on graceful shutdown it posts <c>/ingest/v1/sessions/end</c> with the
    /// final status (ok / errored / crashed). Sessions are never sampled. Default
    /// <c>true</c>; set <c>false</c> to opt out entirely. Automatically skipped
    /// under a unit-test host regardless of this flag.
    /// </summary>
    public bool EnableAutoSessionTracking { get; set; } = true;

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
    /// Enable the offline / persistent event queue: telemetry that
    /// cannot be delivered (network outage, retries exhausted, or process shutting
    /// down with events still buffered) is written — <b>already PII-scrubbed</b> —
    /// to a filesystem spool directory and replayed on the next SDK init. Survives a
    /// process / app restart. Default <c>true</c>; set <c>false</c> to keep the
    /// existing in-memory-only behavior. Always degrades silently to in-memory when
    /// the spool directory is not writable (read-only FS, serverless / sandboxed,
    /// edge runtimes). Session lifecycle and release-registration calls are never
    /// persisted (they are best-effort live-only).
    /// </summary>
    public bool EnableOfflineCache { get; set; } = true;

    /// <summary>
    /// Directory used for the offline event spool. When unset (default) the SDK
    /// uses a per-app folder under the OS local-app-data (or temp) directory. Point
    /// this at a writable, app-private path on hosts with a restricted filesystem.
    /// </summary>
    public string? CacheDirectoryPath { get; set; }

    /// <summary>
    /// Maximum number of envelopes retained in the offline spool. When full the
    /// OLDEST are dropped. Default 100.
    /// </summary>
    public int OfflineCacheMaxEnvelopes { get; set; } = 100;

    /// <summary>
    /// Maximum total bytes retained in the offline spool. When exceeded the OLDEST
    /// envelopes are dropped until the store fits. Default 5 MB.
    /// </summary>
    public long OfflineCacheMaxBytes { get; set; } = 5L * 1024 * 1024;

    /// <summary>
    /// Maximum age (hours) of a spooled envelope. Older entries are evicted on the
    /// next persist / drain so stale telemetry is never replayed. Default 48 h.
    /// </summary>
    public double OfflineCacheMaxAgeHours { get; set; } = 48;

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
    /// If <c>true</c>, automatically capture unhandled exceptions. This drives
    /// both the ASP.NET Core middleware (per-request route exceptions) and the
    /// process-wide <see cref="AppDomain.UnhandledException"/> handler that
    /// catches crashes in background threads / workers outside any request.
    /// </summary>
    public bool CaptureUnhandledExceptions { get; set; } = true;

    /// <summary>
    /// If <c>true</c>, subscribe to <see cref="System.Threading.Tasks.TaskScheduler.UnobservedTaskException"/>
    /// and capture faults from <see cref="System.Threading.Tasks.Task"/>s whose
    /// exceptions were never observed. Default <c>true</c>.
    /// </summary>
    public bool CaptureUnobservedTaskExceptions { get; set; } = true;

    /// <summary>
    /// If <c>true</c>, the SDK calls <c>e.SetObserved()</c> on captured
    /// <see cref="System.Threading.Tasks.TaskScheduler.UnobservedTaskException"/>
    /// events, suppressing the runtime's default escalation. Default <c>false</c>
    /// to preserve existing runtime behavior — the SDK observes telemetry without
    /// changing how the process reacts to unobserved task faults.
    /// </summary>
    public bool MarkUnobservedTaskExceptionsObserved { get; set; } = false;

    /// <summary>
    /// Milliseconds allotted to the best-effort synchronous flush performed when
    /// an <see cref="AppDomain.UnhandledException"/> fires (the process is likely
    /// terminating). Kept short so we don't block shutdown. Default 2000 ms.
    /// </summary>
    public int ShutdownFlushTimeoutMs { get; set; } = 2_000;

    /// <summary>
    /// Optional hook invoked once for every error / message event immediately
    /// before it is handed to the transport, after a first sanitizer pass.
    /// Return the (possibly mutated) sanitized event to send it, or <c>null</c>
    /// to drop it. The transport sanitizes the returned event again before
    /// persistence/network send, so hooks cannot reintroduce secrets. If the
    /// callback throws, the SDK fails open: it logs and sends the sanitized
    /// pre-callback event unchanged.
    /// </summary>
    public Func<AllStakEvent, AllStakEvent?>? BeforeSend { get; set; }

    /// <summary>
    /// Probability (0.0–1.0) that any given error / message event is kept.
    /// Applied at capture time with a deterministic random draw <i>before</i>
    /// <see cref="BeforeSend"/>. <c>1.0</c> (default) keeps everything; <c>0.0</c>
    /// drops everything. Values outside the range are clamped.
    /// </summary>
    public double SampleRate { get; set; } = 1.0;

    /// <summary>
    /// Probability (0.0–1.0) that a span / trace is sampled. When set, span
    /// creation is sampled and the propagated <c>traceparent</c> sampled flag
    /// reflects the decision. <c>null</c> (default) preserves existing behavior
    /// (every span recorded, <c>traceparent</c> always marked sampled).
    /// </summary>
    public double? TracesSampleRate { get; set; }

    /// <summary>
    /// Test seam: source of the [0,1) random draw used for sampling decisions.
    /// Defaults to a shared thread-safe RNG. Tests inject a deterministic value.
    /// </summary>
    internal Func<double> SampleRng { get; set; } = static () => Random.Shared.NextDouble();

    /// <summary>Returns true if an error/message event survives <see cref="SampleRate"/>.</summary>
    internal bool ShouldSampleEvent()
    {
        var rate = SampleRate;
        if (rate >= 1.0) return true;
        if (rate <= 0.0) return false;
        return SampleRng() < rate;
    }

    /// <summary>Returns true if a span survives <see cref="TracesSampleRate"/> (always true when null).</summary>
    internal bool ShouldSampleTrace()
    {
        var rate = TracesSampleRate;
        if (rate is null) return true;
        if (rate.Value >= 1.0) return true;
        if (rate.Value <= 0.0) return false;
        return SampleRng() < rate.Value;
    }

    /// <summary>
    /// If <c>true</c>, automatically capture inbound HTTP request telemetry via the middleware.
    /// </summary>
    public bool CaptureHttpRequests { get; set; } = true;

    /// <summary>
    /// If <c>true</c> (default), <c>AddAllStak()</c> auto-instruments <b>outbound</b>
    /// <c>HttpClient</c> calls on .NET 8+ by registering the AllStak delegating
    /// handler as a default for every named/typed client created through
    /// <c>IHttpClientFactory</c> (<c>ConfigureHttpClientDefaults</c>). This makes
    /// distributed trace propagation and outbound HTTP telemetry automatic with no
    /// per-client <c>AddHttpMessageHandler</c> call. Set <c>false</c> to opt out and
    /// wire the handler manually. The SDK's own internal transport never routes
    /// through <c>IHttpClientFactory</c>, so there is no self-instrumentation loop.
    /// </summary>
    public bool InstrumentOutboundHttp { get; set; } = true;

    /// <summary>
    /// If <c>true</c> (default), <c>AddAllStak()</c> registers the EF Core command
    /// interceptor as a shared singleton in DI. EF Core does not auto-apply an
    /// interceptor from the application service provider, so attach it per context
    /// with <c>o.UseSqlite(conn).UseAllStak(serviceProvider)</c> inside your
    /// <c>AddDbContext</c> options callback to record query telemetry (SQL,
    /// duration, rows, errors). Set <c>false</c> to skip registering the singleton.
    /// </summary>
    public bool InstrumentEntityFrameworkCore { get; set; } = true;

    /// <summary>
    /// If <c>true</c> (default), <c>AddAllStak()</c> registers
    /// <see cref="AllStak.Integrations.Logging.AllStakLoggerProvider"/> in the host
    /// logging pipeline so <c>ILogger</c> calls flow to <c>/ingest/v1/logs</c> and
    /// <c>LogError</c>/<c>LogCritical</c> with an exception are promoted to the error
    /// stream — without a separate <c>builder.Logging.AddAllStak()</c> call. Set
    /// <c>false</c> to opt out and wire the provider manually.
    /// </summary>
    public bool CaptureLogs { get; set; } = true;

    /// <summary>
    /// Minimum <see cref="Microsoft.Extensions.Logging.LogLevel"/> captured by the
    /// auto-registered <see cref="AllStak.Integrations.Logging.AllStakLoggerProvider"/>
    /// when <see cref="CaptureLogs"/> is on. Default <c>Information</c>.
    /// </summary>
    public Microsoft.Extensions.Logging.LogLevel CaptureLogsMinLevel { get; set; }
        = Microsoft.Extensions.Logging.LogLevel.Information;

    /// <summary>
    /// If <c>true</c>, attach user context from <c>HttpContext.User</c> to captured events.
    /// </summary>
    public bool CaptureUserContext { get; set; } = true;

    /// <summary>
    /// Send personally-identifiable information that the SDK would otherwise
    /// scrub from free-text values. Default <c>false</c> for safe-by-default
    /// data scrubbing.
    ///
    /// <para>When <c>false</c> (default): email addresses and IP addresses that
    /// appear inside error/log messages, metadata, breadcrumbs, and captured HTTP
    /// fields are replaced with <c>[REDACTED]</c>, and the auto-collected client IP
    /// (from <c>HttpContext.Connection.RemoteIpAddress</c>) is dropped. High-risk
    /// financial/identity data — credit-card numbers (Luhn-validated) and dashed
    /// US SSNs — is ALWAYS scrubbed regardless of this flag.</para>
    ///
    /// <para>When <c>true</c>: you have explicitly opted into PII, so the email /
    /// IP value scrubbers are disabled and the auto-collected client IP is allowed.
    /// This does NOT affect key-name redaction (password / token / cookie / …) or
    /// the always-on financial scrubbers.</para>
    ///
    /// <para>This flag never strips data you set explicitly via <c>SetUser</c>
    /// (id/email/ip) — that is intentional identification and ships as before.</para>
    /// </summary>
    public bool SendDefaultPii { get; set; } = false;

    /// <summary>
    /// Additional case-insensitive key substrings appended to the built-in
    /// PII / secret denylist. Any event key whose name contains one of these
    /// (e.g. <c>"x-internal-token"</c>, <c>"customer_pan"</c>) has its value
    /// replaced with <c>[REDACTED]</c> at the wire chokepoint. The built-in
    /// denylist (authorization / password / token / cookie / …) always applies;
    /// this only extends it. Empty by default.
    /// </summary>
    public IList<string> ExtraDenylist { get; set; } = new List<string>();

    // Release-tracking metadata. All optional; the SDK reads conventional CI
    // env vars (ALLSTAK_*, GIT_*, VERCEL_GIT_*) when these are unset so the
    // `Release` / `CommitSha` / `Branch` fields appear automatically.
    public string? Dist { get; set; }
    public string? CommitSha { get; set; }
    public string? Branch { get; set; }
    public string? Platform { get; set; } = "dotnet";
    public string? SdkNameOverride { get; set; }
    public string? SdkVersionOverride { get; set; }

    /// <summary>
    /// Gate automatic, CI-free release detection and the SDK-version fallback
    /// (steps 3 and 4 of release resolution). Default <c>true</c>. When
    /// <c>false</c>, <see cref="Release"/> stays as provided (explicit value or
    /// a conventional CI env var) and is left null when neither was set.
    /// </summary>
    public bool AutoDetectRelease { get; set; } = true;

    /// <summary>
    /// Test seam: supplier of the automatically-detected release (step 3).
    /// Defaults to the cached <c>git describe</c> shell-out. Tests inject a
    /// deterministic value so detection needs no real repo.
    /// </summary>
    internal Func<string?> DetectReleaseFn { get; set; } = ReleaseDetector.DetectCached;

    /// <summary>
    /// Apply auto-detection for release-tracking metadata. Called by the SDK
    /// during initialization.
    ///
    /// <para>Release resolution, highest precedence first:
    /// (1) explicit <see cref="Release"/> — always wins;
    /// (2) conventional CI env vars;
    /// (3) automatic <c>git describe</c> detection (CI-free);
    /// (4) the <see cref="SdkVersion"/> constant fallback, so Release is never
    /// empty. Steps 3 and 4 are gated by <see cref="AutoDetectRelease"/>.</para>
    /// </summary>
    public void ApplyReleaseAutodetect()
    {
        static string? FirstNonEmpty(params string[] keys)
        {
            foreach (var k in keys)
            {
                var v = System.Environment.GetEnvironmentVariable(k);
                if (!string.IsNullOrEmpty(v)) return v;
            }
            return null;
        }
        // Steps 1+2: explicit value (already set) or CI env vars.
        Release ??= FirstNonEmpty("ALLSTAK_RELEASE",
            "VERCEL_GIT_COMMIT_SHA", "RAILWAY_GIT_COMMIT_SHA", "RENDER_GIT_COMMIT");
        // Steps 3+4: automatic detection then version fallback (opt-out gated).
        if (string.IsNullOrEmpty(Release) && AutoDetectRelease)
        {
            var detected = DetectReleaseFn?.Invoke();
            Release = !string.IsNullOrEmpty(detected)
                ? detected
                : (SdkVersionOverride ?? SdkVersion);
        }
        CommitSha ??= FirstNonEmpty("ALLSTAK_COMMIT_SHA", "GIT_COMMIT",
            "VERCEL_GIT_COMMIT_SHA", "RAILWAY_GIT_COMMIT_SHA", "RENDER_GIT_COMMIT");
        Branch ??= FirstNonEmpty("ALLSTAK_BRANCH", "GIT_BRANCH",
            "VERCEL_GIT_COMMIT_REF", "RAILWAY_GIT_BRANCH");
        Environment ??= FirstNonEmpty("ALLSTAK_ENVIRONMENT", "ASPNETCORE_ENVIRONMENT", "DOTNET_ENVIRONMENT") ?? "production";
    }

    /// <summary>
    /// Release-tracking tags merged into every event payload's metadata.
    /// </summary>
    public IDictionary<string, string> ReleaseTags()
    {
        var d = new Dictionary<string, string>();
        d["sdk.name"] = SdkNameOverride ?? SdkName;
        d["sdk.version"] = SdkVersionOverride ?? SdkVersion;
        if (!string.IsNullOrEmpty(Platform)) d["platform"] = Platform!;
        if (!string.IsNullOrEmpty(Dist)) d["dist"] = Dist!;
        if (!string.IsNullOrEmpty(CommitSha)) d["commit.sha"] = CommitSha!;
        if (!string.IsNullOrEmpty(Branch)) d["commit.branch"] = Branch!;
        return d;
    }
}
