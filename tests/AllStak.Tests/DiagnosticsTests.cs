using System.Net;
using AllStak.Transport;
using Microsoft.Extensions.Logging.Abstractions;

namespace AllStak.Tests;

[Collection("Singleton")]
public sealed class DiagnosticsTests : IDisposable
{
    public DiagnosticsTests()
    {
        AllStakClient.Reset();
    }

    public void Dispose()
    {
        AllStakClient.Reset();
    }

    [Fact]
    public void GetDiagnostics_BeforeInit_ReturnsDisabledSnapshot()
    {
        var diagnostics = AllStakClient.GetDiagnostics();

        Assert.True(diagnostics.Disabled);
        Assert.Equal(0, diagnostics.EventsCaptured);
        Assert.Equal(0, diagnostics.QueueSize);
    }

    [Fact]
    public void Diagnostics_AggregatesBufferBreadcrumbAndTraceState()
    {
        var client = AllStakClient.Initialize(o =>
        {
            o.ApiKey = "ask_test_diagnostics";
            o.Host = "http://127.0.0.1:1";
            o.EnableOfflineCache = false;
            o.EnableAutoSessionTracking = false;
            o.MaxRetries = 1;
            o.ConnectTimeoutMs = 25;
            o.ReadTimeoutMs = 25;
            o.FlushIntervalMs = 600_000;
            o.BufferSize = 100;
        });

        client.Errors.AddBreadcrumb("custom", "ready");
        client.Logs.Info("buffered log");
        using var span = client.Tracing.StartSpan("diagnostics.test");

        var diagnostics = client.Diagnostics;

        Assert.False(diagnostics.Disabled);
        Assert.Equal(1, diagnostics.BreadcrumbCount);
        Assert.Equal(1, diagnostics.ActiveTraceCount);
        Assert.Equal(1, diagnostics.ActiveSpanCount);
        Assert.True(diagnostics.QueueSize >= 1);
        Assert.True(diagnostics.EventsCaptured >= 1);
    }

    [Fact]
    public async Task TransportDiagnostics_CountsSuccessRateLimitFailureAndPersistence()
    {
        var dir = Path.Combine(Path.GetTempPath(), "allstak-dotnet-diagnostics-" + Guid.NewGuid().ToString("N"));
        try
        {
            var handler = new SequencedHandler();
            handler.Enqueue(HttpStatusCode.TooManyRequests);
            handler.Enqueue(HttpStatusCode.Accepted, """{"ok":true}""");

            var options = new AllStakOptions
            {
                ApiKey = "ask_test_diagnostics_transport",
                Host = "https://fake.allstak.test",
                EnableOfflineCache = true,
                CacheDirectoryPath = dir,
                MaxRetries = 1,
                ConnectTimeoutMs = 50,
                ReadTimeoutMs = 50,
            };
            var transport = CreateWithHandler(handler, options);

            await Assert.ThrowsAsync<AllStakTransportException>(
                () => transport.PostAsync("/ingest/v1/errors", new { message = "persist me" }));

            var (status, _) = await transport.PostAsync("/ingest/v1/errors", new { message = "ok" });
            Assert.Equal(202, status);

            var diagnostics = transport.Diagnostics;
            Assert.Equal(2, diagnostics.EventsCaptured);
            Assert.Equal(1, diagnostics.EventsSent);
            Assert.Equal(1, diagnostics.EventsFailed);
            Assert.Equal(1, diagnostics.EventsPersisted);
            Assert.Equal(0, diagnostics.EventsDropped);
            Assert.Equal(1, diagnostics.RateLimitedCount);
            Assert.Equal(1, diagnostics.QueueSize);
        }
        finally
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    private static HttpTransport CreateWithHandler(HttpMessageHandler handler, AllStakOptions options)
    {
        var transport = new HttpTransport(options, NullLogger.Instance);
        var httpField = typeof(HttpTransport).GetField("_http",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        httpField.SetValue(transport, new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMilliseconds(options.ConnectTimeoutMs + options.ReadTimeoutMs),
        });
        return transport;
    }

    private sealed class SequencedHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpResponseMessage>> _responses = new();

        public void Enqueue(HttpStatusCode status, string body = "")
            => _responses.Enqueue(() => new HttpResponseMessage(status) { Content = new StringContent(body) });

        public void EnqueueThrow()
            => _responses.Enqueue(() => throw new HttpRequestException("network down"));

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (_responses.Count == 0)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted));
            return Task.FromResult(_responses.Dequeue()());
        }
    }
}
