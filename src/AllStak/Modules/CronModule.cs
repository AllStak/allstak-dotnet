using AllStak.Models;
using AllStak.Transport;
using Microsoft.Extensions.Logging;

namespace AllStak.Modules;

/// <summary>
/// Cron / background-job monitoring. Sends a heartbeat per job execution.
/// Auto-creates the monitor on first ping (backend behavior).
/// </summary>
public sealed class CronModule
{
    private const string Path = "/ingest/v1/heartbeat";
    private readonly HttpTransport _transport;
    private readonly ILogger _logger;

    internal CronModule(HttpTransport transport, ILogger logger)
    {
        _transport = transport;
        _logger = logger;
    }

    /// <summary>
    /// Wrap a job body in a using-block; success/failure heartbeat is sent on dispose.
    /// Exceptions inside the block are re-thrown — the SDK never swallows user exceptions.
    ///
    /// <code>
    /// using (AllStakClient.Instance.Cron.Job("daily-report"))
    /// {
    ///     await GenerateReport();
    /// }
    /// </code>
    /// </summary>
    public JobHandle Job(string slug) => new(this, slug);

    /// <summary>Send a single heartbeat ping directly.</summary>
    public async Task<bool> PingAsync(string slug, string status, long durationMs, string? message = null, CancellationToken ct = default)
    {
        if (_transport.IsDisabled) return false;
        try
        {
            var payload = new HeartbeatPayload { Slug = slug, Status = status, DurationMs = durationMs, Message = message };
            var (code, _) = await _transport.PostAsync(Path, payload, ct).ConfigureAwait(false);
            return code == 202;
        }
        catch (AllStakAuthException)
        {
            _logger.LogDebug("[AllStak] cron ping skipped — SDK disabled");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[AllStak] cron ping failed silently");
            return false;
        }
    }
}

/// <summary>Dispose to send success/failure heartbeat.</summary>
public sealed class JobHandle : IDisposable
{
    private readonly CronModule _cron;
    private readonly string _slug;
    private readonly long _startMs;
    private bool _finished;
    private Exception? _pendingException;

    internal JobHandle(CronModule cron, string slug)
    {
        _cron = cron;
        _slug = slug;
        _startMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    /// <summary>Mark the job as failed with a message. Pending heartbeat will be "failed".</summary>
    public void MarkFailed(string message)
    {
        _pendingException = new Exception(message);
    }

    public void Dispose()
    {
        if (_finished) return;
        _finished = true;
        var duration = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _startMs;
        var status = _pendingException != null ? "failed" : "success";
        var message = _pendingException?.Message;
        // Fire-and-forget — never block user code on dispose
        _ = _cron.PingAsync(_slug, status, duration, message);
    }
}
