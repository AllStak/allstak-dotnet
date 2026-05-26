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
    /// before it is handed to the transport (and before the PII sanitizer runs).
    /// Return the (possibly mutated) event to send it, or <c>null</c> to drop it.
    /// If the callback throws, the SDK fails open: it logs and sends the original
    /// event unchanged.
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
    /// If <c>true</c>, attach user context from <c>HttpContext.User</c> to captured events.
    /// </summary>
    public bool CaptureUserContext { get; set; } = true;

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
    /// Apply env-var auto-detection for release-tracking metadata. Called by
    /// the SDK during initialization. Explicit user values always win.
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
        Release ??= FirstNonEmpty("ALLSTAK_RELEASE",
            "VERCEL_GIT_COMMIT_SHA", "RAILWAY_GIT_COMMIT_SHA", "RENDER_GIT_COMMIT");
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
