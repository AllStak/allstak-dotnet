using AllStak.Models;
using AllStak.Transport;
using Microsoft.Extensions.Logging;

namespace AllStak.Modules;

/// <summary>Buffered log ingestion. Each log is sent as its own POST.</summary>
public sealed class LogModule : IDisposable
{
    private const string Path = "/ingest/v1/logs";
    private static readonly HashSet<string> ValidLevels = new() { "debug", "info", "warn", "error", "fatal" };

    private readonly HttpTransport _transport;
    private readonly AllStakOptions _options;
    private readonly ILogger _logger;
    private readonly FlushBuffer<LogPayload> _buffer;

    internal LogModule(HttpTransport transport, AllStakOptions options, ILogger logger)
    {
        _transport = transport;
        _options = options;
        _logger = logger;
        _buffer = new FlushBuffer<LogPayload>(
            "logs", options.BufferSize, options.FlushIntervalMs, FlushBatchAsync, logger);
    }

    /// <summary>Buffer a log for async delivery.</summary>
    public void Log(string level, string message,
        string? service = null, string? traceId = null, string? spanId = null,
        string? requestId = null, string? userId = null, string? errorId = null,
        Dictionary<string, object>? metadata = null)
    {
        if (_transport.IsDisabled) return;

        // normalize .NET "warning" → AllStak "warn"
        if (string.Equals(level, "warning", StringComparison.OrdinalIgnoreCase))
            level = "warn";

        level = level.ToLowerInvariant();
        if (!ValidLevels.Contains(level)) level = "info";

        // Merge release-tracking tags into metadata so the dashboard can
        // filter logs by sdk.* / platform / commit.* without adding columns.
        var releaseTags = _options.ReleaseTags();
        Dictionary<string, object>? mergedMeta = null;
        if (releaseTags.Count > 0)
        {
            mergedMeta = new Dictionary<string, object>(releaseTags.Count + (metadata?.Count ?? 0));
            foreach (var kv in releaseTags) mergedMeta[kv.Key] = kv.Value;
            if (metadata != null) foreach (var kv in metadata) mergedMeta[kv.Key] = kv.Value;
        }
        else
        {
            mergedMeta = metadata;
        }

        _buffer.Push(new LogPayload
        {
            Level = level,
            Message = message,
            Service = service ?? _options.ServiceName,
            Environment = _options.Environment,
            Release = _options.Release,
            TraceId = traceId,
            SpanId = spanId,
            RequestId = requestId,
            UserId = userId,
            ErrorId = errorId,
            Metadata = mergedMeta,
        });
    }

    public void Debug(string msg, Dictionary<string, object>? meta = null) => Log("debug", msg, metadata: meta);
    public void Info(string msg, Dictionary<string, object>? meta = null) => Log("info", msg, metadata: meta);
    public void Warn(string msg, Dictionary<string, object>? meta = null) => Log("warn", msg, metadata: meta);
    public void Error(string msg, Dictionary<string, object>? meta = null) => Log("error", msg, metadata: meta);
    public void Fatal(string msg, Dictionary<string, object>? meta = null) => Log("fatal", msg, metadata: meta);

    public Task FlushAsync() => _buffer.FlushAsync();

    public void Dispose() => _buffer.Dispose();

    internal int BufferCount => _buffer.Count;
    internal long DroppedCount => _buffer.DroppedCount;

    private async Task FlushBatchAsync(IReadOnlyList<LogPayload> items)
    {
        // Logs endpoint is single-item — send each independently.
        foreach (var item in items)
        {
            try
            {
                await _transport.PostAsync(Path, item).ConfigureAwait(false);
            }
            catch (AllStakAuthException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[AllStak] log flush error (discarding)");
            }
        }
    }
}
