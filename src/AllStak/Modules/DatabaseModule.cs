using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AllStak.Models;
using AllStak.Transport;
using Microsoft.Extensions.Logging;

namespace AllStak.Modules;

/// <summary>Database query telemetry, buffered and batched (100/batch).</summary>
public sealed class DatabaseModule : IDisposable
{
    private const string Path = "/ingest/v1/db";
    private const int BatchSize = 100;

    private readonly HttpTransport _transport;
    private readonly AllStakOptions _options;
    private readonly ILogger _logger;
    private readonly FlushBuffer<DbQueryPayload> _buffer;

    internal DatabaseModule(HttpTransport transport, AllStakOptions options, ILogger logger)
    {
        _transport = transport;
        _options = options;
        _logger = logger;
        _buffer = new FlushBuffer<DbQueryPayload>(
            "database", options.BufferSize, options.FlushIntervalMs, FlushBatchAsync, logger);
    }

    public void Record(
        string rawSql,
        long durationMs,
        string status = "success",
        string? errorMessage = null,
        string? databaseName = null,
        string? databaseType = null,
        int rowsAffected = -1,
        string? traceId = null,
        string? spanId = null)
    {
        if (_transport.IsDisabled) return;
        var normalized = NormalizeQuery(rawSql);
        _buffer.Push(new DbQueryPayload
        {
            NormalizedQuery = normalized,
            QueryHash = HashQuery(normalized),
            QueryType = DetectQueryType(normalized),
            DurationMs = Math.Max(0, durationMs),
            TimestampMillis = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Status = status,
            ErrorMessage = errorMessage?.Length > 500 ? errorMessage[..500] : errorMessage,
            DatabaseName = databaseName,
            DatabaseType = databaseType,
            Service = _options.ServiceName,
            Environment = _options.Environment,
            Release = _options.Release,
            TraceId = traceId,
            SpanId = spanId,
            RowsAffected = rowsAffected,
        });
    }

    public Task FlushAsync() => _buffer.FlushAsync();
    public void Dispose() => _buffer.Dispose();

    internal int BufferCount => _buffer.Count;
    internal long DroppedCount => _buffer.DroppedCount;

    private async Task FlushBatchAsync(IReadOnlyList<DbQueryPayload> items)
    {
        for (int i = 0; i < items.Count; i += BatchSize)
        {
            var chunk = items.Skip(i).Take(BatchSize).ToList();
            var batch = new DbQueryBatch { Queries = chunk };
            try
            {
                await _transport.PostAsync(Path, batch).ConfigureAwait(false);
            }
            catch (AllStakAuthException) { return; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[AllStak] db flush error (discarding)");
            }
        }
    }

    public static string NormalizeQuery(string sql)
    {
        if (string.IsNullOrEmpty(sql)) return "";
        sql = Regex.Replace(sql, @"'[^']*'", "?");
        sql = Regex.Replace(sql, @"\b\d+(\.\d+)?\b", "?");
        sql = Regex.Replace(sql, @"\s+", " ").Trim();
        return sql;
    }

    public static string HashQuery(string normalized)
    {
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }

    public static string DetectQueryType(string sql)
    {
        var first = sql.TrimStart().Split(' ', 2)[0].ToUpperInvariant();
        return first is "SELECT" or "INSERT" or "UPDATE" or "DELETE" ? first : "OTHER";
    }
}
