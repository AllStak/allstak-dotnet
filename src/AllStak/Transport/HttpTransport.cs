using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
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

    /// <summary>Upper bound on an honored <c>Retry-After</c> delay (5 minutes).</summary>
    internal static readonly TimeSpan MaxRetryAfter = TimeSpan.FromMinutes(5);

    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _apiKey;
    private readonly Func<string>? _apiKeyProvider;
    private readonly Action<TransportErrorContext>? _onTransportError;
    private readonly int _maxRetries;
    private readonly ILogger _logger;
    private readonly Random _jitter = new();
    private volatile bool _disabled;

    public bool IsDisabled => _disabled;

    public HttpTransport(AllStakOptions options, ILogger logger)
    {
        _baseUrl = options.Host.TrimEnd('/');
        _apiKey = options.ApiKey;
        _apiKeyProvider = options.ApiKeyProvider;   // P0-H — dynamic rotation
        _onTransportError = options.OnTransportError; // P0-I — observable failure
        _maxRetries = Math.Clamp(options.MaxRetries, 1, 5);
        _logger = logger;
        _http = new HttpClient
        {
            Timeout = TimeSpan.FromMilliseconds(options.ConnectTimeoutMs + options.ReadTimeoutMs),
        };
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
        if (_disabled)
            throw new AllStakAuthException("SDK disabled due to invalid API key");

        var url = $"{_baseUrl}{path}";

        // P0-C — sanitize the wire payload BEFORE serializing it onto the network.
        // Serialize → parse → recursive scrub → reserialize. The chokepoint here
        // protects every telemetry type (errors, logs, http, db, traces) with
        // one wire-in.
        var rawJson = JsonSerializer.Serialize(payload);
        string scrubbedJson;
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            scrubbedJson = Sanitizer.SanitizeJson(doc.RootElement);
        }
        catch (Exception sanErr)
        {
            _logger.LogWarning(sanErr, "[AllStak] sanitizer failed; dropping payload (path={Path})", path);
            return (0, string.Empty);
        }

        Exception? lastExc = null;
        int lastStatus = 0;

        for (int attempt = 1; attempt <= _maxRetries; attempt++)
        {
            // Server-directed delay (429/503 Retry-After). Zero when absent/invalid.
            TimeSpan retryAfterDelay = TimeSpan.Zero;
            try
            {
                using var content = new StringContent(scrubbedJson, Encoding.UTF8, "application/json");
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
                    _logger.LogWarning("[AllStak] SDK disabled: invalid API key (401). No further events will be sent.");
                    throw new AllStakAuthException("Invalid API key");
                }

                if (NonRetryableStatuses.Contains(lastStatus))
                    return (lastStatus, body);

                if (lastStatus < 400)
                    return (lastStatus, body);

                // 429 (rate limited) / 503 (unavailable) may carry a Retry-After
                // header telling us exactly how long to wait. Honor it when present.
                if (lastStatus == 429 || lastStatus == 503)
                    retryAfterDelay = ParseRetryAfter(resp.Headers.RetryAfter, DateTimeOffset.UtcNow);

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

        // P0-I — final failure is now visible.
        // 1) Log at Warning (not Debug) so it shows up in default prod logging.
        _logger.LogWarning(
            lastExc,
            "[AllStak] all {Attempts} attempts failed for POST {Path}. Last status={Status}. Event lost.",
            _maxRetries, path, lastStatus);
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
