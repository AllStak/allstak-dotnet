using System.Data.Common;
using System.Diagnostics;
using AllStak.Integrations.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AllStak.Integrations.EntityFrameworkCore;

/// <summary>
/// EF Core DbCommand interceptor that records every query as DB telemetry.
///
/// Attach it inside your <c>AddDbContext</c> registration with the
/// <see cref="AllStakDbContextExtensions.UseAllStak(Microsoft.EntityFrameworkCore.DbContextOptionsBuilder)"/>
/// one-liner:
/// <code>
/// services.AddDbContext&lt;MyDbContext&gt;(opts =&gt; opts.UseSqlite(conn).UseAllStak());
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

/// <summary>
/// DI helpers for wiring the EF Core query interceptor.
/// </summary>
public static class AllStakDbContextExtensions
{
    /// <summary>
    /// Register <see cref="AllStakDbCommandInterceptor"/> as a singleton in DI so a
    /// single shared instance can be resolved and attached to every context.
    ///
    /// <para><b>Important:</b> EF Core does NOT automatically apply an interceptor
    /// just because it is registered in the application service provider — the
    /// developer must attach it per context with <c>UseAllStak()</c> inside the
    /// <c>AddDbContext</c> options callback (see the overloads below). Registering
    /// the singleton lets the <see cref="UseAllStak(DbContextOptionsBuilder, IServiceProvider)"/>
    /// overload reuse the SDK-managed instance.</para>
    ///
    /// <para><c>AddAllStak()</c> calls this for you unless
    /// <see cref="AllStakOptions.InstrumentEntityFrameworkCore"/> is set to
    /// <c>false</c>.</para>
    /// </summary>
    public static IServiceCollection AddAllStakDbContextInterceptor(this IServiceCollection services)
    {
        if (services is null) throw new ArgumentNullException(nameof(services));
        services.TryAddSingleton<AllStakDbCommandInterceptor>();
        return services;
    }

    /// <summary>
    /// Attach the AllStak query interceptor to a <c>DbContext</c> from its
    /// options-builder callback. This is the documented wiring for query telemetry
    /// and must be added inside your <c>AddDbContext</c> registration (or a manually
    /// constructed context's <c>OnConfiguring</c>):
    /// <code>
    /// services.AddDbContext&lt;MyDbContext&gt;(o =&gt; o.UseSqlite(conn).UseAllStak());
    /// </code>
    /// </summary>
    public static DbContextOptionsBuilder UseAllStak(this DbContextOptionsBuilder optionsBuilder)
    {
        if (optionsBuilder is null) throw new ArgumentNullException(nameof(optionsBuilder));
        return optionsBuilder.AddInterceptors(new AllStakDbCommandInterceptor());
    }

    /// <summary>
    /// Attach the AllStak query interceptor using the SDK-managed singleton resolved
    /// from the application service provider. Use this overload from the
    /// <c>(serviceProvider, options)</c> form of <c>AddDbContext</c> so the same
    /// interceptor instance is shared across contexts:
    /// <code>
    /// services.AddDbContext&lt;MyDbContext&gt;((sp, o) =&gt; o.UseSqlite(conn).UseAllStak(sp));
    /// </code>
    /// Falls back to a fresh interceptor instance when one is not registered in DI.
    /// </summary>
    public static DbContextOptionsBuilder UseAllStak(
        this DbContextOptionsBuilder optionsBuilder, IServiceProvider serviceProvider)
    {
        if (optionsBuilder is null) throw new ArgumentNullException(nameof(optionsBuilder));
        if (serviceProvider is null) throw new ArgumentNullException(nameof(serviceProvider));
        var interceptor = serviceProvider.GetService<AllStakDbCommandInterceptor>()
                          ?? new AllStakDbCommandInterceptor();
        return optionsBuilder.AddInterceptors(interceptor);
    }
}
