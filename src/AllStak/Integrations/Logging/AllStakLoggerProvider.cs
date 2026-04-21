using Microsoft.Extensions.Logging;

namespace AllStak.Integrations.Logging;

/// <summary>
/// Microsoft.Extensions.Logging provider that ships every <c>ILogger</c> call
/// at <c>Information</c> or above to AllStak's logs ingest. Plug it into the
/// host's logging pipeline to get auto-capture without sprinkling
/// <c>AllStakClient.Instance.Log.Info(...)</c> through your code.
///
/// Usage (Program.cs):
/// <code>
/// builder.Logging.AddProvider(new AllStakLoggerProvider());
/// </code>
///
/// Or via the helper extension <c>builder.Logging.AddAllStak();</c>.
/// </summary>
public sealed class AllStakLoggerProvider : ILoggerProvider
{
    private readonly LogLevel _minLevel;

    public AllStakLoggerProvider(LogLevel minLevel = LogLevel.Information)
    {
        _minLevel = minLevel;
    }

    public ILogger CreateLogger(string categoryName) => new AllStakLogger(categoryName, _minLevel);

    public void Dispose() { /* AllStakClient handles its own lifetime */ }
}

internal sealed class AllStakLogger : ILogger
{
    private readonly string _category;
    private readonly LogLevel _minLevel;

    public AllStakLogger(string category, LogLevel minLevel)
    {
        _category = category;
        _minLevel = minLevel;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel level) => level >= _minLevel && level != LogLevel.None;

    public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception,
                            Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(level)) return;
        if (!AllStakClient.IsInitialized) return;

        var client = AllStakClient.Instance;
        var message = formatter(state, exception);
        var allstakLevel = level switch
        {
            LogLevel.Critical => "fatal",
            LogLevel.Error => "error",
            LogLevel.Warning => "warn",
            LogLevel.Information => "info",
            LogLevel.Debug => "debug",
            LogLevel.Trace => "debug",
            _ => "info",
        };

        var meta = new Dictionary<string, object> { ["category"] = _category };
        if (exception is not null) meta["exception"] = exception.GetType().FullName ?? exception.GetType().Name;

        try
        {
            client.Logs.Log(allstakLevel, message, metadata: meta);
            // ILogger.LogError(...) and LogCritical with exception → also hit error stream
            if (exception is not null && (level == LogLevel.Error || level == LogLevel.Critical))
            {
                _ = AllStakClient.CaptureExceptionAsync(exception, metadata: meta);
            }
        }
        catch
        {
            /* never throw from the logger */
        }
    }
}

public static class AllStakLoggerExtensions
{
    /// <summary>Convenience: <c>builder.Logging.AddAllStak();</c></summary>
    public static ILoggingBuilder AddAllStak(this ILoggingBuilder builder, LogLevel minLevel = LogLevel.Information)
    {
        builder.AddProvider(new AllStakLoggerProvider(minLevel));
        return builder;
    }
}
