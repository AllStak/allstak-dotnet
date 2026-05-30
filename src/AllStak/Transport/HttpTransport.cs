using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace AllStak.Transport;

internal sealed class AllStakAuthException : Exception
{
    public AllStakAuthException(string message) : base(message) { }
}

internal sealed class AllStakTransportException : Exception
{
    public AllStakTransportException(string message) : base(message) { }
}

/// <summary>
/// HTTP transport with retry/backoff + 401-disable.
/// </summary>
internal sealed class HttpTransport
{
    private static readonly TimeSpan[] BackoffDelays =
    {
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(8),
    };

    private static readonly HashSet<int> NonRetryableStatuses = new() { 400, 401, 403, 404, 422 };

    /// <summary>
    /// Ingest paths that are <b>best-effort live-only</b> and must NOT be spooled to
    /// the offline cache. A replayed stale session would skew durations / crash-free
    /// math, and release registration is a one-shot startup control call — neither is
    /// buffered telemetry. Only error / log / span / http / db telemetry is persisted.
    /// </summary>
    private static readonly HashSet<string> NonPersistablePaths = new(StringComparer.Ordinal)
    {
        "/ingest/v1/sessions/start",
        "/ingest/v1/sessions/end",
        "/ingest/v1/releases",
    };

    /// <summary>Upper bound on an honored <c>Retry-After</c> delay (5 minutes).</summary>
    internal static readonly TimeSpan MaxRetryAfter = TimeSpan.FromMinutes(5);
    private const int CompressionThresholdBytes = 1024;

    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _apiKey;
    private readonly Func<string>? _apiKeyProvider;
    private readonly Action<TransportErrorContext>? _onTransportError;
    private readonly int _maxRetries;
    private readonly ILogger _logger;
    private readonly Random _jitter = new();
    private readonly FileSystemCache? _cache;
    private readonly bool _sendDefaultPii;
    private readonly string[]? _extraDenylist;
    private volatile bool _disabled;
    private long _eventsCaptured;
    private long _eventsSent;
    private long _eventsFailed;
    private long _eventsDropped;
    private long _eventsPersisted;
    private long _eventsReplayed;
    private long _retryAttempts;
    private long _rateLimitedCount;
    private long _compressedPayloads;
    private long _uncompressedPayloads;
    private long _compressionBytesSaved;

    public bool IsDisabled => _disabled;
    internal TransportDiagnostics Diagnostics => new()
    {
        EventsCaptured = Interlocked.Read(ref _eventsCaptured),
        EventsSent = Interlocked.Read(ref _eventsSent),
        EventsFailed = Interlocked.Read(ref _eventsFailed),
        EventsDropped = Interlocked.Read(ref _eventsDropped),
        EventsPersisted = Interlocked.Read(ref _eventsPersisted),
        EventsReplayed = Interlocked.Read(ref _eventsReplayed),
        RetryAttempts = Interlocked.Read(ref _retryAttempts),
        RateLimitedCount = Interlocked.Read(ref _rateLimitedCount),
        CompressedPayloads = Interlocked.Read(ref _compressedPayloads),
        UncompressedPayloads = Interlocked.Read(ref _uncompressedPayloads),
        CompressionBytesSaved = Interlocked.Read(ref _compressionBytesSaved),
        QueueSize = _cache?.Count() ?? 0,
        Disabled = _disabled,
    };

    /// <summary>The offline spool, or <c>null</c> when offline caching is disabled.</summary>
    internal FileSystemCache? Cache => _cache;

    public HttpTransport(AllStakOptions options, ILogger logger)
    {
        _baseUrl = options.Host.TrimEnd('/');
        _apiKey = options.ApiKey;
        _apiKeyProvider = options.ApiKeyProvider;   // P0-H — dynamic rotation
        _onTransportError = options.OnTransportError; // P0-I — observable failure
        _maxRetries = Math.Clamp(options.MaxRetries, 1, 5);
        _logger = logger;
        // Data-scrubbing config snapshotted once for the wire chokepoint.
        _sendDefaultPii = options.SendDefaultPii;
        _extraDenylist = options.ExtraDenylist is { Count: > 0 } extra
            ? System.Linq.Enumerable.ToArray(extra)
            : null;
        _http = new HttpClient
        {
            Timeout = TimeSpan.FromMilliseconds(options.ConnectTimeoutMs + options.ReadTimeoutMs),
        };
        // Offline/persistent queue: scrubbed envelopes that cannot be
        // delivered are spooled to disk and replayed on the next init. Fully fail-open
        // — a non-writable dir simply yields IsAvailable=false and we no-op.
        if (options.EnableOfflineCache)
        {
            var dir = string.IsNullOrWhiteSpace(options.CacheDirectoryPath)
                ? FileSystemCache.DefaultDirectory()
                : options.CacheDirectoryPath!;
            _cache = new FileSystemCache(
                dir, logger,
                maxEnvelopes: options.OfflineCacheMaxEnvelopes,
                maxBytes: options.OfflineCacheMaxBytes,
                maxAge: TimeSpan.FromHours(Math.Max(0, options.OfflineCacheMaxAgeHours)));
        }
        // Note: no DefaultRequestHeaders.X-AllStak-Key — set per-request via
        // ResolveApiKey() so ApiKeyProvider can rotate without restart.
    }

    /// <summary>Resolve the current API key — provider wins over static. (P0-H)</summary>
    private string ResolveApiKey()
    {
        if (_apiKeyProvider != null)
        {
            try { return _apiKeyProvider() ?? _apiKey; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[AllStak] ApiKeyProvider threw — falling back to static ApiKey");
                return _apiKey;
            }
        }
        return _apiKey;
    }

    public async Task<(int status, string body)> PostAsync<T>(string path, T payload, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _eventsCaptured);
        if (_disabled)
        {
            Interlocked.Increment(ref _eventsDropped);
            throw new AllStakAuthException("SDK disabled due to invalid API key");
        }

        // P0-C — sanitize the wire payload BEFORE serializing it onto the network.
        // Serialize → parse → recursive scrub → reserialize. The chokepoint here
        // protects every telemetry type (errors, logs, http, db, traces) with
        // one wire-in. Both key-name redaction and value-pattern PII scrubbing
        // (CC/SSN always; email/IP gated on SendDefaultPii) run here. Critically,
        // the offline cache only ever sees this already-scrubbed body, so
        // unredacted secrets never touch disk.
        var rawJson = JsonSerializer.Serialize(payload);
        string scrubbedJson;
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            scrubbedJson = Sanitizer.SanitizeJson(doc.RootElement, _extraDenylist, _sendDefaultPii);
        }
        catch (Exception sanErr)
        {
            _logger.LogWarning(sanErr, "[AllStak] sanitizer failed; dropping payload (path={Path})", path);
            Interlocked.Increment(ref _eventsFailed);
            Interlocked.Increment(ref _eventsDropped);
            return (0, string.Empty);
        }

        // persistOnFailure: spool the scrubbed body when delivery ultimately fails.
        return await SendScrubbedAsync(path, scrubbedJson, persistOnFailure: true, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Re-send an already-scrubbed envelope read back from the offline cache. Goes
    /// through the same retry/backoff/circuit-breaker as a live send, but does NOT
    /// re-persist on failure (it is already on disk; the drainer leaves it in place).
    /// </summary>
    internal Task<(int status, string body)> SendCachedAsync(string path, string scrubbedBody, CancellationToken ct = default)
        => SendScrubbedAsync(path, scrubbedBody, persistOnFailure: false, ct);

    private async Task<(int status, string body)> SendScrubbedAsync(
        string path, string scrubbedJson, bool persistOnFailure, CancellationToken ct)
    {
        if (_disabled)
            throw new AllStakAuthException("SDK disabled due to invalid API key");

        var url = $"{_baseUrl}{path}";
        var preparedBody = PrepareRequestBody(scrubbedJson);
        if (preparedBody.Compressed)
        {
            Interlocked.Increment(ref _compressedPayloads);
            Interlocked.Add(ref _compressionBytesSaved, preparedBody.BytesSaved);
        }
        else
        {
            Interlocked.Increment(ref _uncompressedPayloads);
        }

        Exception? lastExc = null;
        int lastStatus = 0;

        for (int attempt = 1; attempt <= _maxRetries; attempt++)
        {
            // Server-directed delay (429/503 Retry-After). Zero when absent/invalid.
            TimeSpan retryAfterDelay = TimeSpan.Zero;
            try
            {
                using var content = new ByteArrayContent(preparedBody.Body);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
                {
                    CharSet = Encoding.UTF8.WebName,
                };
                if (preparedBody.Compressed)
                    content.Headers.ContentEncoding.Add("gzip");
                using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
                // P0-H — per-request API key for rotation without restart.
                req.Headers.TryAddWithoutValidation("X-AllStak-Key", ResolveApiKey());
                using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
                lastStatus = (int)resp.StatusCode;
                var body = string.Empty;
                try { body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false); } catch { }

                if (lastStatus == 401)
                {
                    _disabled = true;
                    Interlocked.Increment(ref _eventsFailed);
                    Interlocked.Increment(ref _eventsDropped);
                    _logger.LogWarning("[AllStak] SDK disabled: invalid API key (401). No further events will be sent.");
                    throw new AllStakAuthException("Invalid API key");
                }

                if (NonRetryableStatuses.Contains(lastStatus))
                {
                    Interlocked.Increment(ref _eventsFailed);
                    Interlocked.Increment(ref _eventsDropped);
                    return (lastStatus, body);
                }

                if (lastStatus < 400)
                {
                    Interlocked.Increment(ref _eventsSent);
                    return (lastStatus, body);
                }

                // 429 (rate limited) / 503 (unavailable) may carry a Retry-After
                // header telling us exactly how long to wait. Honor it when present.
                if (lastStatus == 429 || lastStatus == 503)
                {
                    if (lastStatus == 429) Interlocked.Increment(ref _rateLimitedCount);
                    retryAfterDelay = ParseRetryAfter(resp.Headers.RetryAfter, DateTimeOffset.UtcNow);
                }

                // 5xx (and 429) — retry
            }
            catch (AllStakAuthException)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException)
            {
                lastExc = ex;
                _logger.LogDebug(ex, "[AllStak] transport error on attempt {Attempt}", attempt);
            }
            catch (Exception ex)
            {
                lastExc = ex;
                _logger.LogDebug(ex, "[AllStak] unexpected transport error on attempt {Attempt}", attempt);
            }

            if (attempt < _maxRetries)
            {
                Interlocked.Increment(ref _retryAttempts);
                TimeSpan delay;
                if (retryAfterDelay > TimeSpan.Zero)
                {
                    // Server told us when to come back — respect it (already clamped).
                    delay = retryAfterDelay;
                }
                else
                {
                    var backoff = BackoffDelays[Math.Min(attempt - 1, BackoffDelays.Length - 1)];
                    var jitter = TimeSpan.FromMilliseconds(_jitter.Next(0, 500));
                    delay = backoff + jitter;
                }
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }

        // Offline/persistent queue: instead of losing this telemetry, spool the
        // already-scrubbed body to disk so the next init can replay it. Only for
        // live sends (not drained ones) and only for persistable telemetry paths
        // (sessions/releases are best-effort live-only). Fully fail-open.
        bool persisted = false;
        if (persistOnFailure && _cache != null && _cache.IsAvailable && !NonPersistablePaths.Contains(path))
        {
            persisted = _cache.Persist(path, scrubbedJson);
        }
        Interlocked.Increment(ref _eventsFailed);
        if (persisted) Interlocked.Increment(ref _eventsPersisted);
        else Interlocked.Increment(ref _eventsDropped);

        // P0-I — final failure is now visible.
        // 1) Log at Warning (not Debug) so it shows up in default prod logging.
        _logger.LogWarning(
            lastExc,
            "[AllStak] all {Attempts} attempts failed for POST {Path}. Last status={Status}. {Disposition}",
            _maxRetries, path, lastStatus, persisted ? "Spooled to offline cache." : "Event lost.");
        // 2) Surface to the host app's metrics pipeline if they registered a handler.
        if (_onTransportError != null)
        {
            try
            {
                _onTransportError(new TransportErrorContext(path, lastStatus, lastExc, _maxRetries));
            }
            catch (Exception cbErr)
            {
                _logger.LogWarning(cbErr, "[AllStak] OnTransportError callback threw");
            }
        }
        throw new AllStakTransportException(
            $"All {_maxRetries} attempts failed for POST {path}. Last status={lastStatus}, last error={lastExc?.Message}");
    }

    private static PreparedRequestBody PrepareRequestBody(string scrubbedJson)
    {
        var raw = Encoding.UTF8.GetBytes(scrubbedJson);
        if (raw.Length < CompressionThresholdBytes)
            return new PreparedRequestBody(raw, false, 0);

        try
        {
            using var output = new MemoryStream();
            using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
            {
                gzip.Write(raw, 0, raw.Length);
            }
            var compressed = output.ToArray();
            if (compressed.Length >= raw.Length)
                return new PreparedRequestBody(raw, false, 0);
            return new PreparedRequestBody(compressed, true, raw.Length - compressed.Length);
        }
        catch
        {
            return new PreparedRequestBody(raw, false, 0);
        }
    }

    private readonly record struct PreparedRequestBody(byte[] Body, bool Compressed, int BytesSaved);

    /// <summary>
    /// Replay every envelope spooled to the offline cache by a previous process /
    /// outage, oldest first, through the live transport (same retry/backoff/circuit
    /// breaker). An entry is removed only once it is <b>accepted</b> (2xx) or is
    /// <b>permanently undeliverable</b> (a 4xx other than 429); transient failures
    /// (network, 5xx, 429, exhausted retries) leave it on disk for the next attempt.
    /// Fully fail-open and safe to fire-and-forget on init — it never throws.
    /// </summary>
    public async Task DrainCacheAsync(CancellationToken ct = default)
    {
        var cache = _cache;
        if (cache is null || !cache.IsAvailable) return;

        IReadOnlyList<CachedEnvelope> envelopes;
        try { envelopes = cache.Load(); }
        catch (Exception ex) { _logger.LogDebug(ex, "[AllStak] offline cache load failed"); return; }
        if (envelopes.Count == 0) return;

        _logger.LogDebug("[AllStak] draining {Count} offline envelope(s)", envelopes.Count);

        foreach (var env in envelopes)
        {
            if (_disabled) return; // 401 disabled mid-drain — stop; leave the rest spooled.
            if (ct.IsCancellationRequested) return;
            try
            {
                var (status, _) = await SendCachedAsync(env.Path, env.Body, ct).ConfigureAwait(false);
                // Accepted (2xx) or permanently rejected (4xx, but NOT 429) → remove.
                bool accepted = status is >= 200 and < 300;
                bool permanentReject = status is >= 400 and < 500 && status != 429;
                if (accepted || permanentReject)
                {
                    if (accepted) Interlocked.Increment(ref _eventsReplayed);
                    cache.Remove(env.File);
                }
                // else: 5xx / 429 / 0 — leave on disk for the next drain.
            }
            catch (AllStakAuthException)
            {
                // SDK disabled (invalid key). Keep everything spooled; stop draining.
                return;
            }
            catch (Exception ex)
            {
                // Network / exhausted retries — leave this envelope on disk, try next.
                _logger.LogDebug(ex, "[AllStak] offline replay failed (path={Path}); kept on disk", env.Path);
            }
        }
    }

    /// <summary>
    /// Convert an HTTP <c>Retry-After</c> header into a wait delay. The header can be
    /// either a delta in seconds (<c>Retry-After: 2</c>) or an absolute HTTP date
    /// (<c>Retry-After: Wed, 21 Oct 2015 07:28:00 GMT</c>); .NET exposes both via
    /// <see cref="RetryConditionHeaderValue.Delta"/> and <see cref="RetryConditionHeaderValue.Date"/>.
    /// Returns <see cref="TimeSpan.Zero"/> when the header is absent or invalid (caller
    /// falls back to jittered backoff). The result is clamped to
    /// <see cref="MaxRetryAfter"/> (5 minutes) and never negative.
    /// </summary>
    /// <param name="retryAfter">The parsed header value, or <c>null</c> if absent.</param>
    /// <param name="now">Reference "now" used to resolve a date-form header into a delta.</param>
    internal static TimeSpan ParseRetryAfter(RetryConditionHeaderValue? retryAfter, DateTimeOffset now)
    {
        if (retryAfter is null)
            return TimeSpan.Zero;

        TimeSpan delay;
        if (retryAfter.Delta is TimeSpan delta)
        {
            delay = delta;
        }
        else if (retryAfter.Date is DateTimeOffset date)
        {
            delay = date - now;
        }
        else
        {
            return TimeSpan.Zero;
        }

        if (delay <= TimeSpan.Zero)
            return TimeSpan.Zero;

        return delay > MaxRetryAfter ? MaxRetryAfter : delay;
    }
}
