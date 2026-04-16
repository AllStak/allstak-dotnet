using System.Data.Common;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace AllStak.Integrations.EntityFrameworkCore;

/// <summary>
/// EF Core DbCommand interceptor that records every query as DB telemetry.
///
/// Register via:
/// <code>
/// services.AddDbContext&lt;MyDbContext&gt;(opts =&gt;
///     opts.UseSqlite(conn).AddInterceptors(new AllStakDbCommandInterceptor()));
/// </code>
/// </summary>
public sealed class AllStakDbCommandInterceptor : DbCommandInterceptor
{
    private readonly AsyncLocal<Stopwatch?> _sw = new();

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        _sw.Value = Stopwatch.StartNew();
        return base.ReaderExecuting(command, eventData, result);
    }

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        _sw.Value = Stopwatch.StartNew();
        return await base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
    {
        RecordSuccess(command, eventData, rows: -1);
        return base.ReaderExecuted(command, eventData, result);
    }

    public override async ValueTask<DbDataReader> ReaderExecutedAsync(
        DbCommand command, CommandExecutedEventData eventData, DbDataReader result,
        CancellationToken cancellationToken = default)
    {
        RecordSuccess(command, eventData, rows: -1);
        return await base.ReaderExecutedAsync(command, eventData, result, cancellationToken);
    }

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        _sw.Value = Stopwatch.StartNew();
        return base.NonQueryExecuting(command, eventData, result);
    }

    public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        _sw.Value = Stopwatch.StartNew();
        return await base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
    {
        RecordSuccess(command, eventData, rows: result);
        return base.NonQueryExecuted(command, eventData, result);
    }

    public override async ValueTask<int> NonQueryExecutedAsync(
        DbCommand command, CommandExecutedEventData eventData, int result,
        CancellationToken cancellationToken = default)
    {
        RecordSuccess(command, eventData, rows: result);
        return await base.NonQueryExecutedAsync(command, eventData, result, cancellationToken);
    }

    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        _sw.Value = Stopwatch.StartNew();
        return base.ScalarExecuting(command, eventData, result);
    }

    public override async ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        _sw.Value = Stopwatch.StartNew();
        return await base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override object? ScalarExecuted(DbCommand command, CommandExecutedEventData eventData, object? result)
    {
        RecordSuccess(command, eventData, rows: -1);
        return base.ScalarExecuted(command, eventData, result);
    }

    public override async ValueTask<object?> ScalarExecutedAsync(
        DbCommand command, CommandExecutedEventData eventData, object? result,
        CancellationToken cancellationToken = default)
    {
        RecordSuccess(command, eventData, rows: -1);
        return await base.ScalarExecutedAsync(command, eventData, result, cancellationToken);
    }

    public override void CommandFailed(DbCommand command, CommandErrorEventData eventData)
    {
        RecordFailure(command, eventData);
        base.CommandFailed(command, eventData);
    }

    public override async Task CommandFailedAsync(DbCommand command, CommandErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        RecordFailure(command, eventData);
        await base.CommandFailedAsync(command, eventData, cancellationToken);
    }

    private void RecordSuccess(DbCommand command, CommandExecutedEventData ev, int rows)
    {
        if (!AllStakClient.IsInitialized) return;
        var elapsed = _sw.Value?.ElapsedMilliseconds ?? (long)ev.Duration.TotalMilliseconds;
        try
        {
            AllStakClient.Instance.Database.Record(
                rawSql: command.CommandText ?? "",
                durationMs: elapsed,
                status: "success",
                databaseName: command.Connection?.Database,
                databaseType: command.Connection?.GetType().Name,
                rowsAffected: rows);
        }
        catch { }
    }

    private void RecordFailure(DbCommand command, CommandErrorEventData ev)
    {
        if (!AllStakClient.IsInitialized) return;
        var elapsed = _sw.Value?.ElapsedMilliseconds ?? (long)ev.Duration.TotalMilliseconds;
        try
        {
            AllStakClient.Instance.Database.Record(
                rawSql: command.CommandText ?? "",
                durationMs: elapsed,
                status: "error",
                errorMessage: ev.Exception?.Message,
                databaseName: command.Connection?.Database,
                databaseType: command.Connection?.GetType().Name);
        }
        catch { }
    }
}
