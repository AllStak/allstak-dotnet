using System.Net;
using System.Security.Claims;
using System.Text.Json;
using AllStak;
using AllStak.Integrations.AspNetCore;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace AllStak.Tests;

/// <summary>
/// Tests for <see cref="AllStakMiddleware"/>: trace header propagation,
/// request telemetry capture, exception re-throw after capture.
/// </summary>
[Collection("Singleton")]
public sealed class MiddlewareTests : IDisposable
{
    public MiddlewareTests()
    {
        AllStakClient.Reset();
    }

    public void Dispose() => AllStakClient.Reset();

    // ── Helpers ───────────────────────────────────────────────────────

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public List<string> Bodies { get; } = new();
        public List<string> Paths { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            Paths.Add(request.RequestUri?.AbsolutePath ?? "");
            var body = request.Content != null
                ? await request.Content.ReadAsStringAsync(ct)
                : "";
            Bodies.Add(body);
            return new HttpResponseMessage(HttpStatusCode.Accepted)
            {
                Content = new StringContent("""{"data":{"id":"evt_mw"}}"""),
            };
        }
    }

    private static CapturingHandler InitClientWithCapture()
    {
        AllStakClient.Reset();
        var handler = new CapturingHandler();

        var opts = new AllStakOptions
        {
            ApiKey = "ask_test_middleware",
            Host = "https://fake.allstak.test",
            Environment = "test",
            MaxRetries = 1,
            FlushIntervalMs = 60_000, // prevent timer flush
            CaptureHttpRequests = true,
            CaptureUnhandledExceptions = true,
        };

        AllStakClient.InitializeFromOptions(opts);

        // Swap transport's HttpClient via reflection — this must happen on the
        // transport that ALL modules already reference (they share one instance).
        var client = AllStakClient.Instance;
        var transportField = typeof(AllStakClient).GetField("_transport",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var transport = transportField.GetValue(client)!;
        var httpField = transport.GetType().GetField("_http",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        httpClient.DefaultRequestHeaders.TryAddWithoutValidation("X-AllStak-Key", opts.ApiKey);
        httpField.SetValue(transport, httpClient);

        return handler;
    }

    private static DefaultHttpContext CreateHttpContext(
        string method = "GET",
        string path = "/api/test",
        string? incomingTraceId = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        context.Request.Host = new HostString("localhost", 5000);
        context.Response.StatusCode = 200;

        if (incomingTraceId != null)
            context.Request.Headers["X-AllStak-Trace-Id"] = incomingTraceId;

        return context;
    }

    // ── Tests ────────────────────────────────────────────────────────

    [Fact]
    public async Task TraceId_SetOnResponse_WhenNoneIncoming()
    {
        InitClientWithCapture();
        var middleware = new AllStakMiddleware(
            _ => Task.CompletedTask,
            NullLogger<AllStakMiddleware>.Instance);

        var ctx = CreateHttpContext();
        await middleware.InvokeAsync(ctx);

        // The middleware sets X-AllStak-Trace-Id on the response.
        Assert.True(AllStakClient.IsInitialized, "SDK should be initialized");
        var headerValue = ctx.Response.Headers["X-AllStak-Trace-Id"].ToString();
        Assert.False(string.IsNullOrEmpty(headerValue), "Trace ID header should be set on response");
    }

    [Fact]
    public async Task TraceId_AdoptsIncoming()
    {
        InitClientWithCapture();
        var middleware = new AllStakMiddleware(
            _ => Task.CompletedTask,
            NullLogger<AllStakMiddleware>.Instance);

        var ctx = CreateHttpContext(incomingTraceId: "incoming-trace-abc");
        await middleware.InvokeAsync(ctx);

        Assert.Equal("incoming-trace-abc",
            ctx.Response.Headers["X-AllStak-Trace-Id"].ToString());
    }

    [Fact]
    public async Task Exception_IsReThrownAfterCapture()
    {
        InitClientWithCapture();
        var testException = new InvalidOperationException("middleware boom");
        var middleware = new AllStakMiddleware(
            _ => throw testException,
            NullLogger<AllStakMiddleware>.Instance);

        var ctx = CreateHttpContext();

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => middleware.InvokeAsync(ctx));

        Assert.Same(testException, thrown);
    }

    [Fact]
    public async Task Exception_CapturedAndSentToBackend()
    {
        var handler = InitClientWithCapture();
        var middleware = new AllStakMiddleware(
            _ => throw new ArgumentException("bad arg"),
            NullLogger<AllStakMiddleware>.Instance);

        var ctx = CreateHttpContext(method: "POST", path: "/api/orders");

        await Assert.ThrowsAsync<ArgumentException>(
            () => middleware.InvokeAsync(ctx));

        // The middleware fires CaptureExceptionAsync as fire-and-forget.
        // Give it a moment to complete, then also flush.
        await Task.Delay(500);

        // The error capture should have been sent.
        var errorBodies = handler.Bodies
            .Where(b => b.Contains("exceptionClass"))
            .ToList();

        Assert.NotEmpty(errorBodies);

        using var doc = JsonDocument.Parse(errorBodies[0]);
        var root = doc.RootElement;
        Assert.Equal("ArgumentException", root.GetProperty("exceptionClass").GetString());
    }

    [Fact]
    public async Task Middleware_SkipsWhenNotInitialized()
    {
        AllStakClient.Reset();

        bool nextCalled = false;
        var middleware = new AllStakMiddleware(
            _ => { nextCalled = true; return Task.CompletedTask; },
            NullLogger<AllStakMiddleware>.Instance);

        var ctx = CreateHttpContext();
        await middleware.InvokeAsync(ctx);

        Assert.True(nextCalled);
        // No trace header should be set when SDK is not initialized.
        var headerValue = ctx.Response.Headers["X-AllStak-Trace-Id"].ToString();
        Assert.True(string.IsNullOrEmpty(headerValue));
    }

    [Fact]
    public async Task RequestTelemetry_CapturedOnSuccess()
    {
        var handler = InitClientWithCapture();
        var middleware = new AllStakMiddleware(
            _ => Task.CompletedTask,
            NullLogger<AllStakMiddleware>.Instance);

        var ctx = CreateHttpContext(method: "GET", path: "/api/health");
        await middleware.InvokeAsync(ctx);

        // Flush the HTTP buffer so the request telemetry is sent.
        await AllStakClient.Instance.Http.FlushAsync();

        // Look for the HTTP request batch.
        var httpBodies = handler.Bodies
            .Where(b => b.Contains("requests"))
            .ToList();

        Assert.NotEmpty(httpBodies);

        using var doc = JsonDocument.Parse(httpBodies[0]);
        var requests = doc.RootElement.GetProperty("requests");
        Assert.True(requests.GetArrayLength() > 0);

        var first = requests[0];
        Assert.Equal("GET", first.GetProperty("method").GetString());
        Assert.Equal("/api/health", first.GetProperty("path").GetString());
        Assert.Equal("inbound", first.GetProperty("direction").GetString());
    }
}
