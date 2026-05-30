using AllStak.Integrations.EntityFrameworkCore;
using AllStak.Integrations.HttpClient;
using AllStak.Integrations.Logging;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace AllStak.Integrations.AspNetCore;

public static class AllStakServiceCollectionExtensions
{
    /// <summary>
    /// Register the AllStak SDK in DI. The client is initialized as a singleton.
    ///
    /// <para>Beyond the client, this also auto-wires the in-box integrations so the
    /// developer gets them with no per-call code (each individually opt-out via
    /// <see cref="AllStakOptions"/>):</para>
    /// <list type="bullet">
    /// <item><b>Outbound HTTP</b> — registers <see cref="AllStakHttpHandler"/> as an
    /// <c>IHttpClientFactory</c> default (<c>ConfigureHttpClientDefaults</c>) so every
    /// named/typed client propagates the trace and records outbound telemetry.
    /// Gated by <see cref="AllStakOptions.InstrumentOutboundHttp"/>.</item>
    /// <item><b>EF Core</b> — registers an <see cref="AllStakDbCommandInterceptor"/>
    /// singleton in DI. EF Core does NOT auto-apply it, so add
    /// <c>o.UseAllStak(serviceProvider)</c> inside your <c>AddDbContext</c> options
    /// callback to record query telemetry. Gated by
    /// <see cref="AllStakOptions.InstrumentEntityFrameworkCore"/>.</item>
    /// <item><b>Logging</b> — registers <see cref="AllStakLoggerProvider"/> so
    /// <c>ILogger</c> calls reach the logs ingest and errors are promoted. Gated by
    /// <see cref="AllStakOptions.CaptureLogs"/>.</item>
    /// </list>
    ///
    /// <para>Call <c>app.UseAllStak()</c> to register the inbound request middleware.</para>
    /// </summary>
    public static IServiceCollection AddAllStak(this IServiceCollection services, Action<AllStakOptions> configure)
    {
        if (services is null) throw new ArgumentNullException(nameof(services));
        if (configure is null) throw new ArgumentNullException(nameof(configure));

        services.Configure(configure);

        // Resolve the configured options once, eagerly, so the integration
        // registrations below can honor the opt-out flags at registration time.
        var resolved = new AllStakOptions();
        configure(resolved);

        services.AddSingleton(sp =>
        {
            var opts = new AllStakOptions();
            configure(opts);
            var loggerFactory = sp.GetService<ILoggerFactory>();
            var logger = loggerFactory?.CreateLogger("allstak.sdk");
            return AllStakClient.InitializeFromOptions(opts, logger);
        });

        // ── Outbound HTTP auto-instrumentation (.NET 8+) ──────────────────
        // ConfigureHttpClientDefaults applies the AllStak delegating handler to
        // every client built through IHttpClientFactory, so distributed trace
        // propagation + outbound telemetry happen with zero per-client wiring.
        // The handler is a no-op until the client singleton initializes and the
        // SDK's own transport does not route through the factory, so there is no
        // self-instrumentation loop.
        if (resolved.InstrumentOutboundHttp)
        {
            services.TryAddTransient<AllStakHttpHandler>();
            services.ConfigureHttpClientDefaults(b => b.AddHttpMessageHandler<AllStakHttpHandler>());
        }

        // ── EF Core query telemetry ───────────────────────────────────────
        // Register the interceptor as a shared singleton so the developer can pull
        // the SDK-managed instance into each DbContext. EF Core does NOT pick up an
        // IInterceptor from the application service provider automatically, so the
        // documented wiring is an explicit per-context one-liner the developer adds
        // in AddDbContext: o.UseSqlite(conn).UseAllStak(serviceProvider). A single
        // DbCommandInterceptor instance is safe to share across contexts.
        if (resolved.InstrumentEntityFrameworkCore)
        {
            services.AddAllStakDbContextInterceptor();
        }

        // ── Logging auto-capture ──────────────────────────────────────────
        // Register the logger provider directly in DI so ILogger output reaches
        // /ingest/v1/logs and LogError/LogCritical-with-exception are promoted to
        // the error stream, without a separate builder.Logging.AddAllStak() call.
        if (resolved.CaptureLogs)
        {
            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<ILoggerProvider>(new AllStakLoggerProvider(resolved.CaptureLogsMinLevel)));
        }

        return services;
    }
}

public static class AllStakApplicationBuilderExtensions
{
    /// <summary>
    /// Register the AllStak request middleware.
    /// Should be registered BEFORE UseRouting / UseEndpoints and AFTER UseExceptionHandler
    /// (so it can observe the final status code).
    /// </summary>
    public static IApplicationBuilder UseAllStak(this IApplicationBuilder app)
    {
        // Force resolve so the singleton initializes
        _ = app.ApplicationServices.GetService(typeof(AllStakClient));
        return app.UseMiddleware<AllStakMiddleware>();
    }
}
