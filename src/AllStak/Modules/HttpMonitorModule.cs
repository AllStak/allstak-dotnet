using AllStak.Models;
using AllStak.Transport;
using Microsoft.Extensions.Logging;

namespace AllStak.Modules;

/// <summary>Inbound and outbound HTTP request telemetry. Batches up to 100 per POST.</summary>
public sealed class HttpMonitorModule : IDisposable
{
    private const string Path = "/ingest/v1/http-requests";
    private const int MaxBatch = 100;
    private readonly HttpTransport _transport;
    private readonly AllStakOptions _options;
    private readonly ILogger _logger;
    private readonly FlushBuffer<HttpRequestItem> _buffer;

    internal HttpMonitorModule(HttpTransport transport, AllStakOptions options, ILogger logger)
    {
        _transport = transport;
        _options = options;
        _logger = logger;
        _buffer = new FlushBuffer<HttpRequestItem>(
            "http", options.BufferSize, options.FlushIntervalMs, FlushBatchAsync, logger);
    }

    public void Record(
        string direction,
        string method,
        string host,
        string path,
        int statusCode,
        long durationMs,
        long requestSize = 0,
        long responseSize = 0,
        string? traceId = null,
        string? requestId = null,
        string? userId = null,
        string? errorFingerprint = null,
        string? spanId = null,
        string? parentSpanId = null)
    {
        if (_transport.IsDisabled) return;
        _buffer.Push(new HttpRequestItem
        {
            Direction = direction,
            Method = method.ToUpperInvariant(),
            Host = host,
            Path = StripQuery(path),
            StatusCode = statusCode,
            DurationMs = Math.Max(0, durationMs),
            RequestSize = requestSize,
            ResponseSize = responseSize,
            TraceId = traceId ?? Guid.NewGuid().ToString("N"),
            RequestId = requestId,
            Timestamp = DateTime.UtcNow.ToString("o"),
            UserId = userId,
            ErrorFingerprint = errorFingerprint,
            SpanId = spanId,
            ParentSpanId = parentSpanId,
            Environment = _options.Environment,
            Release = _options.Release,
            // Release-tracking metadata. We allocate a fresh dict per item
            // because items live in a buffer; sharing a single map across
            // pushes would race during flush.
            Metadata = BuildReleaseTagsDict(),
        });
    }

    /// <summary>
    /// Snapshot the release-tracking tags from <see cref="AllStakOptions.ReleaseTags"/>
    /// into a fresh dictionary, or null if no tags are set.
    /// </summary>
    private Dictionary<string, object>? BuildReleaseTagsDict()
    {
        var tags = _options.ReleaseTags();
        if (tags.Count == 0) return null;
        var d = new Dictionary<string, object>(tags.Count);
        foreach (var kv in tags) d[kv.Key] = kv.Value;
        return d;
    }

    public Task FlushAsync() => _buffer.FlushAsync();
    public void Dispose() => _buffer.Dispose();

    private async Task FlushBatchAsync(IReadOnlyList<HttpRequestItem> items)
    {
        for (int i = 0; i < items.Count; i += MaxBatch)
        {
            var chunk = items.Skip(i).Take(MaxBatch).ToList();
            var batch = new HttpRequestBatch { Requests = chunk };
            try
            {
                await _transport.PostAsync(Path, batch).ConfigureAwait(false);
            }
            catch (AllStakAuthException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[AllStak] http batch flush error (discarding)");
            }
        }
    }

    private static string StripQuery(string path)
    {
        var q = path.IndexOf('?');
        return q < 0 ? path : path.Substring(0, q);
    }
}
