using System.Net;
using System.Net.Http.Json;
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

    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _apiKey;
    private readonly int _maxRetries;
    private readonly ILogger _logger;
    private readonly Random _jitter = new();
    private volatile bool _disabled;

    public bool IsDisabled => _disabled;

    public HttpTransport(AllStakOptions options, ILogger logger)
    {
        _baseUrl = options.Host.TrimEnd('/');
        _apiKey = options.ApiKey;
        _maxRetries = Math.Clamp(options.MaxRetries, 1, 5);
        _logger = logger;
        _http = new HttpClient
        {
            Timeout = TimeSpan.FromMilliseconds(options.ConnectTimeoutMs + options.ReadTimeoutMs),
        };
        _http.DefaultRequestHeaders.TryAddWithoutValidation("X-AllStak-Key", _apiKey);
    }

    public async Task<(int status, string body)> PostAsync<T>(string path, T payload, CancellationToken ct = default)
    {
        if (_disabled)
            throw new AllStakAuthException("SDK disabled due to invalid API key");

        var url = $"{_baseUrl}{path}";
        Exception? lastExc = null;
        int lastStatus = 0;

        for (int attempt = 1; attempt <= _maxRetries; attempt++)
        {
            try
            {
                using var resp = await _http.PostAsJsonAsync(url, payload, ct).ConfigureAwait(false);
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

                // 5xx — retry
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
                var backoff = BackoffDelays[Math.Min(attempt - 1, BackoffDelays.Length - 1)];
                var jitter = TimeSpan.FromMilliseconds(_jitter.Next(0, 500));
                await Task.Delay(backoff + jitter, ct).ConfigureAwait(false);
            }
        }

        throw new AllStakTransportException(
            $"All {_maxRetries} attempts failed for POST {path}. Last status={lastStatus}, last error={lastExc?.Message}");
    }
}
