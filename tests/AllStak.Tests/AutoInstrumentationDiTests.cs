using System.Net;
using AllStak;
using AllStak.Integrations.AspNetCore;
using AllStak.Integrations.EntityFrameworkCore;
using AllStak.Integrations.HttpClient;
using AllStak.Integrations.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AllStak.Tests;

/// <summary>
/// Verifies that <c>AddAllStak()</c> auto-registers the in-box integrations —
/// outbound HttpClient instrumentation, the global EF Core query interceptor, and
/// the logging provider — so they work with no per-call developer code, while each
/// remains individually opt-out via <see cref="AllStakOptions"/>.
/// </summary>
[Collection("Singleton")]
public sealed class AutoInstrumentationDiTests : IDisposable
{
    public AutoInstrumentationDiTests() => AllStakClient.Reset();

    public void Dispose() => AllStakClient.Reset();

    private static IServiceCollection NewServices(Action<AllStakOptions> extra) =>
        new ServiceCollection().AddAllStak(o =>
        {
            o.ApiKey = "ask_test_di";
            o.Environment = "test";
            o.EnableAutoSessionTracking = false;
            o.EnableOfflineCache = false;
            extra(o);
        });

    // ── Outbound HTTP ─────────────────────────────────────────────────

    [Fact]
    public void AddAllStak_RegistersHttpHandler_ByDefault()
    {
        var services = NewServices(_ => { });

        Assert.Contains(services, d => d.ServiceType == typeof(AllStakHttpHandler)
                                       && d.Lifetime == ServiceLifetime.Transient);
    }

    [Fact]
    public void AddAllStak_DoesNotRegisterHttpHandler_WhenOptedOut()
    {
        var services = NewServices(o => o.InstrumentOutboundHttp = false);

        Assert.DoesNotContain(services, d => d.ServiceType == typeof(AllStakHttpHandler));
    }

    [Fact]
    public async Task AddAllStak_AutoInstrumentsFactoryHttpClient_AppliesTraceHeader()
    {
        // A factory-created client should carry the AllStak handler with zero
        // per-client AddHttpMessageHandler wiring, proving distributed-trace
        // propagation is automatic.
        var probe = new HeaderProbeHandler();
        var services = new ServiceCollection();
        services.AddAllStak(o =>
        {
            o.ApiKey = "ask_test_di";
            o.Environment = "test";
            o.EnableAutoSessionTracking = false;
            o.EnableOfflineCache = false;
        });
        // Terminate the pipeline with our probe so no real network call happens.
        services.AddHttpClient("probe")
                .ConfigurePrimaryHttpMessageHandler(() => probe);

        using var provider = services.BuildServiceProvider();
        // Force the SDK singleton to initialize so the handler is active.
        _ = provider.GetRequiredService<AllStakClient>();

        var factory = provider.GetRequiredService<IHttpClientFactory>();
        var http = factory.CreateClient("probe");

        var resp = await http.GetAsync("https://example.test/widgets");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        // The AllStak handler ran in the pipeline → trace header was injected.
        Assert.True(probe.SawTraceHeader,
            "AllStak handler should have applied the trace propagation header on the outbound request");
    }

    private sealed class HeaderProbeHandler : HttpMessageHandler
    {
        public bool SawTraceHeader { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            SawTraceHeader = request.Headers.Contains("X-AllStak-Trace-Id");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    // ── EF Core global interceptor ────────────────────────────────────

    [Fact]
    public void AddAllStak_RegistersEfCoreInterceptorSingleton_ByDefault()
    {
        var services = NewServices(_ => { });

        // The interceptor is registered as a shared singleton so the documented
        // UseAllStak(serviceProvider) wiring can resolve and attach it per context.
        // EF Core does NOT auto-apply it, so it is intentionally NOT registered as
        // a global IInterceptor.
        Assert.Contains(services, d => d.ServiceType == typeof(AllStakDbCommandInterceptor)
                                       && d.Lifetime == ServiceLifetime.Singleton);
    }

    [Fact]
    public void AddAllStak_ResolvesInterceptorSingleton()
    {
        var services = NewServices(_ => { });
        using var provider = services.BuildServiceProvider();

        // The SDK-managed instance UseAllStak(sp) reuses must be resolvable.
        var interceptor = provider.GetService<AllStakDbCommandInterceptor>();
        Assert.NotNull(interceptor);
    }

    [Fact]
    public void AddAllStak_DoesNotRegisterEfCoreInterceptor_WhenOptedOut()
    {
        var services = NewServices(o => o.InstrumentEntityFrameworkCore = false);

        Assert.DoesNotContain(services, d => d.ServiceType == typeof(AllStakDbCommandInterceptor));
    }

    [Fact]
    public void AddAllStakDbContextInterceptor_StandaloneHelper_Registers()
    {
        var services = new ServiceCollection().AddAllStakDbContextInterceptor();
        using var provider = services.BuildServiceProvider();

        var interceptor = provider.GetService<AllStakDbCommandInterceptor>();
        Assert.NotNull(interceptor);
    }

    // ── Logging provider ──────────────────────────────────────────────

    [Fact]
    public void AddAllStak_RegistersLoggerProvider_ByDefault()
    {
        var services = NewServices(_ => { });
        using var provider = services.BuildServiceProvider();

        var loggerProviders = provider.GetServices<ILoggerProvider>().ToList();
        Assert.Contains(loggerProviders, p => p is AllStakLoggerProvider);
    }

    [Fact]
    public void AddAllStak_DoesNotRegisterLoggerProvider_WhenCaptureLogsOff()
    {
        var services = NewServices(o => o.CaptureLogs = false);

        Assert.DoesNotContain(services, d => d.ServiceType == typeof(ILoggerProvider)
                                             && d.ImplementationFactory != null);
        using var provider = services.BuildServiceProvider();
        var loggerProviders = provider.GetServices<ILoggerProvider>().ToList();
        Assert.DoesNotContain(loggerProviders, p => p is AllStakLoggerProvider);
    }

    // ── Client still initializes through DI ───────────────────────────

    [Fact]
    public void AddAllStak_ResolvesClientSingleton()
    {
        var services = NewServices(_ => { });
        using var provider = services.BuildServiceProvider();

        var client = provider.GetRequiredService<AllStakClient>();
        Assert.NotNull(client);
        Assert.True(AllStakClient.IsInitialized);
        Assert.Same(client, provider.GetRequiredService<AllStakClient>());
    }
}
