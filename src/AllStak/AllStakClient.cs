using AllStak.Models;
using AllStak.Modules;
using AllStak.Session;
using AllStak.Transport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AllStak;

/// <summary>
/// The AllStak .NET SDK client. Create once at application startup
/// via <see cref="AllStakClient.Initialize"/>, or inject it via DI
/// with <c>AddAllStak</c>.
/// </summary>
public sealed class AllStakClient : IDisposable
{
    private static AllStakClient? _instance;
    private static readonly object _initLock = new();

    /// <summary>Process-wide singleton. Throws if not initialized.</summary>
    public static AllStakClient Instance =>
        _instance ?? throw new InvalidOperationException(
            "AllStak SDK is not initialized. Call AllStakClient.Initialize(...) or AddAllStak(...) first.");

    /// <summary>True if <see cref="Initialize"/> has been called.</summary>
    public static bool IsInitialized => _instance != null;

    internal readonly AllStakOptions Options;
    private readonly HttpTransport _transport;
    private readonly ILogger _logger;
    private readonly GlobalExceptionHandler _globalHandler;
    private readonly SessionTracker? _session;

    /// <summary>Error capture module.</summary>
    public ErrorModule Errors { get; }
    /// <summary>Structured logs module.</summary>
    public LogModule Logs { get; }
    /// <summary>HTTP monitoring module (inbound + outbound).</summary>
    public HttpMonitorModule Http { get; }
    /// <summary>Distributed tracing module.</summary>
    public TracingModule Tracing { get; }
    /// <summary>Database query telemetry module.</summary>
    public DatabaseModule Database { get; }
    /// <summary>Cron / background job monitoring module.</summary>
    public CronModule Cron { get; }

    private AllStakClient(AllStakOptions options, ILogger? logger)
    {
        Options = options ?? throw new ArgumentNullException(nameof(options));
        if (string.IsNullOrWhiteSpace(options.ApiKey))
            throw new ArgumentException("AllStakOptions.ApiKey is required", nameof(options));
        options.ApplyReleaseAutodetect();

        _logger = logger ?? NullLogger.Instance;
        _transport = new HttpTransport(options, _logger);
        RegisterRuntimeRelease();

        Errors = new ErrorModule(_transport, options, _logger);
        Logs = new LogModule(_transport, options, _logger);
        Http = new HttpMonitorModule(_transport, options, _logger);
        Tracing = new TracingModule(_transport, options, _logger);
        Database = new DatabaseModule(_transport, options, _logger);
        Cron = new CronModule(_transport, options, _logger);

        // Release-health session: one session per process / app-launch. Started
        // here (after release is resolved) so the start envelope carries the
        // resolved release; ended on graceful shutdown. Never sampled, fail-open,
        // and skipped under a unit-test host or when opted out. Wired into the
        // error module so its session id stamps every event and handled/unhandled
        // captures advance the session status.
        if (options.EnableAutoSessionTracking && !IsLikelyTestHost())
        {
            _session = new SessionTracker(options, _transport, _logger);
            Errors.Session = _session;
            try { _session.Start(Errors.CurrentUserId); }
            catch (Exception ex) { _logger.LogDebug(ex, "[AllStak] session start failed"); }
        }

        // Offline/persistent queue replay: asynchronously re-send any telemetry
        // spooled to disk by a previous process / outage. Fire-and-forget so init
        // never blocks on the network; fully fail-open; skipped under a unit-test
        // host so test runs don't replay another run's spool.
        if (options.EnableOfflineCache && !IsLikelyTestHost())
            DrainOfflineCache();

        // Process-wide capture for crashes outside ASP.NET requests (background
        // workers, fire-and-forget tasks). Idempotent + opt-out via options.
        _globalHandler = new GlobalExceptionHandler(Errors, FlushAllAsync, options, _logger);
        if (options.CaptureUnhandledExceptions || options.CaptureUnobservedTaskExceptions)
            _globalHandler.Subscribe();

        AppDomain.CurrentDomain.ProcessExit += (_, _) => Shutdown();
    }

    private void RegisterRuntimeRelease()
    {
        if (!Options.AutoRegisterRelease ||
            string.IsNullOrWhiteSpace(Options.Release) ||
            IsLikelyTestHost())
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await _transport.PostAsync("/ingest/v1/releases", new
                {
                    version = Options.Release,
                    environment = Options.Environment ?? "production",
                    commitSha = Options.CommitSha,
                    branch = Options.Branch,
                    author = $"{AllStakOptions.SdkName}/{AllStakOptions.SdkVersion}",
                    message = "Registered automatically by AllStak .NET SDK at runtime",
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[AllStak] runtime release registration failed");
            }
        });
    }

    /// <summary>
    /// Fire-and-forget replay of the offline event spool through the live transport.
    /// Never throws; logs at debug on failure. Separated out so the constructor stays
    /// readable and so it can run after a short delay if the transport is warming up.
    /// </summary>
    private void DrainOfflineCache()
    {
        _ = Task.Run(async () =>
        {
            try { await _transport.DrainCacheAsync().ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogDebug(ex, "[AllStak] offline cache drain failed"); }
        });
    }

    private static bool IsLikelyTestHost()
    {
        var name = AppDomain.CurrentDomain.FriendlyName;
        return name.Contains("testhost", StringComparison.OrdinalIgnoreCase)
            || name.Contains("vstest", StringComparison.OrdinalIgnoreCase)
            || name.Contains("xunit", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Initialize the singleton. Subsequent calls return the existing instance.</summary>
    public static AllStakClient Initialize(Action<AllStakOptions> configure, ILogger? logger = null)
    {
        lock (_initLock)
        {
            if (_instance != null) return _instance;
            var opts = new AllStakOptions();
            configure(opts);
            _instance = new AllStakClient(opts, logger);
            return _instance;
        }
    }

    /// <summary>Used by DI registration. Internal.</summary>
    internal static AllStakClient InitializeFromOptions(AllStakOptions options, ILogger? logger = null)
    {
        lock (_initLock)
        {
            if (_instance != null) return _instance;
            _instance = new AllStakClient(options, logger);
            return _instance;
        }
    }

    /// <summary>Reset the singleton. Used by tests.</summary>
    public static void Reset()
    {
        lock (_initLock)
        {
            _instance?.Dispose();
            _instance = null;
        }
    }

    /// <summary>Convenience: capture an exception on the singleton.</summary>
    public static Task<string?> CaptureExceptionAsync(
        Exception exc,
        string level = "error",
        Dictionary<string, object>? metadata = null) =>
        IsInitialized ? Instance.Errors.CaptureExceptionAsync(exc, level: level, metadata: metadata) : Task.FromResult<string?>(null);

    /// <summary>Set current user context on the singleton.</summary>
    public static void SetUser(string? id = null, string? email = null, string? ip = null)
    {
        if (IsInitialized) Instance.Errors.SetUser(id, email, ip);
    }

    /// <summary>Clear user context on the singleton.</summary>
    public static void ClearUser()
    {
        if (IsInitialized) Instance.Errors.ClearUser();
    }

    /// <summary>Flush all buffered telemetry across modules.</summary>
    public async Task FlushAllAsync()
    {
        try { await Logs.FlushAsync(); } catch { }
        try { await Http.FlushAsync(); } catch { }
        try { await Tracing.FlushAsync(); } catch { }
        try { await Database.FlushAsync(); } catch { }
    }

    /// <summary>Best-effort shutdown — flushes all buffers and disposes resources.</summary>
    public void Shutdown()
    {
        try { _globalHandler.Unsubscribe(); } catch { }
        try { FlushAllAsync().GetAwaiter().GetResult(); } catch { }
        // End the release-health session (POST /sessions/end). Idempotent and
        // bounded; never throws or blocks shutdown indefinitely.
        try { _session?.End(); } catch { }
        Logs.Dispose();
        Http.Dispose();
        Tracing.Dispose();
        Database.Dispose();
    }

    public void Dispose() => Shutdown();
}
