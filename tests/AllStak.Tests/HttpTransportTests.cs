using System.Net;
using System.Net.Http.Headers;
using System.IO.Compression;
using System.Text;
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
        public HttpRequestMessage? LastRequest { get; private set; }
        public byte[] LastBody { get; private set; } = Array.Empty<byte>();
        public IReadOnlyCollection<string> LastContentEncoding { get; private set; } = Array.Empty<string>();

        public void Enqueue(HttpStatusCode status, string body = "")
        {
            _responses.Enqueue(new HttpResponseMessage(status)
            {
                Content = new StringContent(body),
            });
        }

        public void EnqueueWithRetryAfter(HttpStatusCode status, TimeSpan retryAfter, string body = "")
        {
            var msg = new HttpResponseMessage(status)
            {
                Content = new StringContent(body),
            };
            msg.Headers.RetryAfter = new RetryConditionHeaderValue(retryAfter);
            _responses.Enqueue(msg);
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            CallCount++;
            LastRequest = request;
            LastContentEncoding = request.Content?.Headers.ContentEncoding.ToArray() ?? Array.Empty<string>();
            LastBody = request.Content is null
                ? Array.Empty<byte>()
                : await request.Content.ReadAsByteArrayAsync(ct);
            if (_responses.Count == 0)
                return new HttpResponseMessage(HttpStatusCode.OK);
            return _responses.Dequeue();
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
    public async Task TinyPayload_IsSentUncompressed_AndCounted()
    {
        var handler = new FakeHandler();
        handler.Enqueue(HttpStatusCode.Accepted, """{"ok":true}""");
        var transport = CreateWithHandler(handler);

        var (status, _) = await transport.PostAsync("/test", new { message = "hi" });

        Assert.Equal(202, status);
        Assert.Empty(handler.LastContentEncoding);
        Assert.Contains("\"message\":\"hi\"", Encoding.UTF8.GetString(handler.LastBody));
        Assert.Equal(1, transport.Diagnostics.UncompressedPayloads);
        Assert.Equal(0, transport.Diagnostics.CompressedPayloads);
        Assert.Equal(0, transport.Diagnostics.CompressionBytesSaved);
    }

    [Fact]
    public async Task LargePayload_IsGzippedWhenSmaller_AndCounted()
    {
        var handler = new FakeHandler();
        handler.Enqueue(HttpStatusCode.Accepted, """{"ok":true}""");
        var transport = CreateWithHandler(handler);
        var message = new string('x', 8_000);

        var (status, _) = await transport.PostAsync("/test", new { message });

        Assert.Equal(202, status);
        Assert.Contains("gzip", handler.LastContentEncoding);
        Assert.Contains(message, Gunzip(handler.LastBody));
        Assert.Equal(1, transport.Diagnostics.CompressedPayloads);
        Assert.Equal(0, transport.Diagnostics.UncompressedPayloads);
        Assert.True(transport.Diagnostics.CompressionBytesSaved > 0);
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

    // ── Retry-After parsing ──────────────────────────────────────────

    [Fact]
    public void ParseRetryAfter_Null_ReturnsZero()
    {
        Assert.Equal(TimeSpan.Zero,
            HttpTransport.ParseRetryAfter(null, DateTimeOffset.UtcNow));
    }

    private static string Gunzip(byte[] bytes)
    {
        using var input = new MemoryStream(bytes);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return Encoding.UTF8.GetString(output.ToArray());
    }

    [Fact]
    public void ParseRetryAfter_DeltaSeconds_ReturnsDelta()
    {
        var header = new RetryConditionHeaderValue(TimeSpan.FromSeconds(2));
        Assert.Equal(TimeSpan.FromSeconds(2),
            HttpTransport.ParseRetryAfter(header, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void ParseRetryAfter_HttpDate_ReturnsDeltaFromNow()
    {
        var now = new DateTimeOffset(2026, 5, 26, 12, 0, 0, TimeSpan.Zero);
        var when = now.AddSeconds(30);
        var header = new RetryConditionHeaderValue(when);

        // HTTP-date has 1s resolution, so allow a small tolerance.
        var result = HttpTransport.ParseRetryAfter(header, now);
        Assert.InRange(result, TimeSpan.FromSeconds(29), TimeSpan.FromSeconds(31));
    }

    [Fact]
    public void ParseRetryAfter_PastDate_ReturnsZero()
    {
        var now = new DateTimeOffset(2026, 5, 26, 12, 0, 0, TimeSpan.Zero);
        var header = new RetryConditionHeaderValue(now.AddSeconds(-30));
        Assert.Equal(TimeSpan.Zero, HttpTransport.ParseRetryAfter(header, now));
    }

    [Fact]
    public void ParseRetryAfter_OverFiveMinutes_ClampedToFiveMinutes()
    {
        var header = new RetryConditionHeaderValue(TimeSpan.FromSeconds(600)); // 10 min
        Assert.Equal(TimeSpan.FromMinutes(5),
            HttpTransport.ParseRetryAfter(header, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void ParseRetryAfter_DateOverFiveMinutes_ClampedToFiveMinutes()
    {
        var now = new DateTimeOffset(2026, 5, 26, 12, 0, 0, TimeSpan.Zero);
        var header = new RetryConditionHeaderValue(now.AddMinutes(10));
        Assert.Equal(TimeSpan.FromMinutes(5),
            HttpTransport.ParseRetryAfter(header, now));
    }

    [Fact]
    public async Task Retries_On429_WithRetryAfter()
    {
        // A 429 carrying Retry-After: 0 (so the test does not actually sleep)
        // must be treated as retryable, then succeed.
        var handler = new FakeHandler();
        handler.EnqueueWithRetryAfter(HttpStatusCode.TooManyRequests, TimeSpan.Zero, "rate limited");
        handler.Enqueue(HttpStatusCode.OK, "ok");
        var transport = CreateWithHandler(handler, maxRetries: 3);

        var (status, _) = await transport.PostAsync("/test", new { });

        Assert.Equal(200, status);
        Assert.Equal(2, handler.CallCount);
    }
}
