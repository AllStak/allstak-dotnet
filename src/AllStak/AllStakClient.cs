using AllStak.Models;
using AllStak.Modules;
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

        _logger = logger ?? NullLogger.Instance;
        _transport = new HttpTransport(options, _logger);

        Errors = new ErrorModule(_transport, options, _logger);
        Logs = new LogModule(_transport, options, _logger);
        Http = new HttpMonitorModule(_transport, options, _logger);
        Tracing = new TracingModule(_transport, options, _logger);
        Database = new DatabaseModule(_transport, options, _logger);
        Cron = new CronModule(_transport, _logger);

        AppDomain.CurrentDomain.ProcessExit += (_, _) => Shutdown();
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
        try { FlushAllAsync().GetAwaiter().GetResult(); } catch { }
        Logs.Dispose();
        Http.Dispose();
        Tracing.Dispose();
        Database.Dispose();
    }

    public void Dispose() => Shutdown();
}
