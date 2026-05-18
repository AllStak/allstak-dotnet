using System.Net;
using System.Text.Json;
using AllStak.Transport;
using Microsoft.Extensions.Logging.Abstractions;

namespace AllStak.Tests;

/// <summary>
/// Tests for <see cref="HttpTransport"/>: retry on 5xx, no retry on 4xx,
/// 401 disables SDK, timeout behavior. Uses a mock <see cref="HttpMessageHandler"/>.
/// </summary>
public sealed class HttpTransportTests
{
    private static AllStakOptions MakeOptions(int maxRetries = 3) => new()
    {
        ApiKey = "ask_test_transport",
        Host = "https://fake.allstak.test",
        MaxRetries = maxRetries,
        ConnectTimeoutMs = 1_000,
        ReadTimeoutMs = 1_000,
    };

    /// <summary>
    /// A testable HttpTransport that injects a custom <see cref="HttpMessageHandler"/>.
    /// We use reflection to swap the internal HttpClient since the constructor is internal.
    /// </summary>
    private static HttpTransport CreateWithHandler(HttpMessageHandler handler, int maxRetries = 3)
    {
        var options = MakeOptions(maxRetries);
        var transport = new HttpTransport(options, NullLogger.Instance);

        // Replace the internal _http field with one backed by our handler.
        var httpField = typeof(HttpTransport).GetField("_http",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMilliseconds(options.ConnectTimeoutMs + options.ReadTimeoutMs),
        };
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-AllStak-Key", options.ApiKey);
        httpField.SetValue(transport, client);

        return transport;
    }

    // ── Helpers ───────────────────────────────────────────────────────

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new();
        public int CallCount { get; private set; }

        public void Enqueue(HttpStatusCode status, string body = "")
        {
            _responses.Enqueue(new HttpResponseMessage(status)
            {
                Content = new StringContent(body),
            });
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            CallCount++;
            if (_responses.Count == 0)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            return Task.FromResult(_responses.Dequeue());
        }
    }

    private sealed class TimeoutHandler : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            CallCount++;
            // Simulate a long delay — the CancellationToken should fire.
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    // ── Tests ────────────────────────────────────────────────────────

    [Fact]
    public async Task Success_ReturnsStatusAndBody()
    {
        var handler = new FakeHandler();
        handler.Enqueue(HttpStatusCode.OK, """{"ok":true}""");
        var transport = CreateWithHandler(handler);

        var (status, body) = await transport.PostAsync("/test", new { x = 1 });

        Assert.Equal(200, status);
        Assert.Contains("ok", body);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Retries_On5xx()
    {
        var handler = new FakeHandler();
        handler.Enqueue(HttpStatusCode.InternalServerError, "err");
        handler.Enqueue(HttpStatusCode.BadGateway, "err");
        handler.Enqueue(HttpStatusCode.OK, "ok");
        var transport = CreateWithHandler(handler, maxRetries: 3);

        var (status, _) = await transport.PostAsync("/test", new { });

        Assert.Equal(200, status);
        Assert.Equal(3, handler.CallCount);
    }

    [Fact]
    public async Task NoRetry_On400()
    {
        var handler = new FakeHandler();
        handler.Enqueue(HttpStatusCode.BadRequest, "bad");
        var transport = CreateWithHandler(handler, maxRetries: 3);

        var (status, _) = await transport.PostAsync("/test", new { });

        Assert.Equal(400, status);
        Assert.Equal(1, handler.CallCount); // No retry
    }

    [Fact]
    public async Task NoRetry_On404()
    {
        var handler = new FakeHandler();
        handler.Enqueue(HttpStatusCode.NotFound, "not found");
        var transport = CreateWithHandler(handler, maxRetries: 3);

        var (status, _) = await transport.PostAsync("/test", new { });

        Assert.Equal(404, status);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task NoRetry_On422()
    {
        var handler = new FakeHandler();
        handler.Enqueue(HttpStatusCode.UnprocessableEntity, "bad entity");
        var transport = CreateWithHandler(handler, maxRetries: 3);

        var (status, _) = await transport.PostAsync("/test", new { });

        Assert.Equal(422, status);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Auth401_DisablesSDK()
    {
        var handler = new FakeHandler();
        handler.Enqueue(HttpStatusCode.Unauthorized, "invalid key");
        var transport = CreateWithHandler(handler, maxRetries: 3);

        Assert.False(transport.IsDisabled);

        await Assert.ThrowsAsync<AllStakAuthException>(
            () => transport.PostAsync("/test", new { }));

        Assert.True(transport.IsDisabled);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task PostAsync_WhenDisabled_ThrowsAuthException()
    {
        var handler = new FakeHandler();
        handler.Enqueue(HttpStatusCode.Unauthorized, "bad key");
        var transport = CreateWithHandler(handler, maxRetries: 1);

        // First call — triggers disable.
        await Assert.ThrowsAsync<AllStakAuthException>(
            () => transport.PostAsync("/first", new { }));

        // Subsequent call — immediately rejects.
        var ex = await Assert.ThrowsAsync<AllStakAuthException>(
            () => transport.PostAsync("/second", new { }));

        Assert.Contains("disabled", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, handler.CallCount); // Second call never hit the wire
    }

    [Fact]
    public async Task AllRetries_Exhausted_ThrowsTransportException()
    {
        var handler = new FakeHandler();
        handler.Enqueue(HttpStatusCode.InternalServerError);
        handler.Enqueue(HttpStatusCode.InternalServerError);
        var transport = CreateWithHandler(handler, maxRetries: 2);

        await Assert.ThrowsAsync<AllStakTransportException>(
            () => transport.PostAsync("/test", new { }));

        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task Timeout_TriggersRetry()
    {
        var handler = new TimeoutHandler();
        // With maxRetries=1 and a short timeout, the transport should fail
        // on TaskCanceledException and throw AllStakTransportException.
        var options = MakeOptions(maxRetries: 1);
        var transport = new HttpTransport(options, NullLogger.Instance);

        // Replace HttpClient with one that has a very short timeout.
        var httpField = typeof(HttpTransport).GetField("_http",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(50) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-AllStak-Key", options.ApiKey);
        httpField.SetValue(transport, client);

        await Assert.ThrowsAsync<AllStakTransportException>(
            () => transport.PostAsync("/test", new { }));

        Assert.Equal(1, handler.CallCount);
    }
}
