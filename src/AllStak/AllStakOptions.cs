namespace AllStak;

/// <summary>
/// Configuration options for the AllStak SDK.
/// </summary>
public class AllStakOptions
{
    /// <summary>SDK identity sent on the wire as <c>sdk.name</c> / <c>sdk.version</c>.</summary>
    public const string SdkName = "allstak-dotnet";
    public const string SdkVersion = "1.2.0";


    /// <summary>
    /// API key sent as <c>X-AllStak-Key</c>. Required.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

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
    /// If <c>true</c>, automatically capture unhandled exceptions from ASP.NET Core routes.
    /// </summary>
    public bool CaptureUnhandledExceptions { get; set; } = true;

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
