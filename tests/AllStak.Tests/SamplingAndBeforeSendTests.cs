using System.Net;
using System.Text.Json;
using AllStak;
using AllStak.Modules;
using AllStak.Transport;
using Microsoft.Extensions.Logging.Abstractions;

namespace AllStak.Tests;

/// <summary>
/// Tests for <see cref="AllStakOptions.BeforeSend"/>,
/// <see cref="AllStakOptions.SampleRate"/>, and
/// <see cref="AllStakOptions.TracesSampleRate"/>.
/// </summary>
[Collection("Singleton")]
public sealed class SamplingAndBeforeSendTests : IDisposable
{
    public void Dispose() => AllStakClient.Reset();

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public List<string> Bodies { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content != null ? await request.Content.ReadAsStringAsync(ct) : "";
            Bodies.Add(body);
            return new HttpResponseMessage(HttpStatusCode.Accepted)
            {
                Content = new StringContent("""{"data":{"id":"evt_x"}}"""),
            };
        }
    }

    private static (ErrorModule module, CapturingHandler handler, AllStakOptions options) CreateErrors(
        Action<AllStakOptions>? configure = null)
    {
        var options = new AllStakOptions
        {
            ApiKey = "ask_sample",
            Host = "https://fake.allstak.test",
            Environment = "test",
            MaxRetries = 1,
        };
        configure?.Invoke(options);

        var handler = new CapturingHandler();
        var transport = new HttpTransport(options, NullLogger.Instance);
        var httpField = typeof(HttpTransport).GetField("_http",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        httpField.SetValue(transport, new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) });

        return (new ErrorModule(transport, options, NullLogger.Instance), handler, options);
    }

    // ── BeforeSend ────────────────────────────────────────────────────

    [Fact]
    public async Task BeforeSend_MutatesEvent()
    {
        var (module, handler, _) = CreateErrors(o =>
        {
            o.BeforeSend = evt =>
            {
                evt.Message = "scrubbed-by-beforesend";
                evt.Level = "warning";
                evt.Metadata ??= new Dictionary<string, object>();
                evt.Metadata["injected"] = "yes";
                return evt;
            };
        });

        await module.CaptureExceptionAsync(new Exception("original"));

        Assert.Single(handler.Bodies);
        using var doc = JsonDocument.Parse(handler.Bodies[0]);
        Assert.Equal("scrubbed-by-beforesend", doc.RootElement.GetProperty("message").GetString());
        Assert.Equal("warning", doc.RootElement.GetProperty("level").GetString());
        Assert.Equal("yes", doc.RootElement.GetProperty("metadata").GetProperty("injected").GetString());
    }

    [Fact]
    public async Task BeforeSend_ReturningNull_DropsEvent()
    {
        var (module, handler, _) = CreateErrors(o => o.BeforeSend = _ => null);

        var result = await module.CaptureExceptionAsync(new Exception("dropped"));

        Assert.Null(result);
        Assert.Empty(handler.Bodies);
    }

    [Fact]
    public async Task BeforeSend_ReturningNull_DropsMessageEvents()
    {
        var (module, handler, _) = CreateErrors(o => o.BeforeSend = _ => null);

        var result = await module.CaptureErrorAsync("MyError", "boom");

        Assert.Null(result);
        Assert.Empty(handler.Bodies);
    }

    [Fact]
    public async Task BeforeSend_Throws_FailsOpen_SendsOriginal()
    {
        var (module, handler, _) = CreateErrors(o =>
            o.BeforeSend = _ => throw new InvalidOperationException("callback bug"));

        await module.CaptureExceptionAsync(new Exception("survives"));

        // Fail-open: the original event is still sent unchanged.
        Assert.Single(handler.Bodies);
        using var doc = JsonDocument.Parse(handler.Bodies[0]);
        Assert.Equal("survives", doc.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public async Task BeforeSend_ReceivesSanitizedEvent()
    {
        AllStakEvent? seen = null;
        var (module, handler, _) = CreateErrors(o =>
        {
            o.BeforeSend = evt =>
            {
                seen = evt;
                return evt;
            };
        });

        await module.CaptureExceptionAsync(
            new Exception("card 4111111111111111"),
            metadata: new Dictionary<string, object>
            {
                ["Authorization"] = "Bearer abc",
                ["nested"] = new Dictionary<string, object> { ["apiKey"] = "key-123" },
            });

        Assert.Single(handler.Bodies);
        Assert.NotNull(seen);
        Assert.Equal("card [REDACTED]", seen!.Message);
        Assert.Equal("[REDACTED]", seen.Metadata!["Authorization"]);
        var nested = Assert.IsType<Dictionary<string, object>>(seen.Metadata["nested"]);
        Assert.Equal("[REDACTED]", nested["apiKey"]);
    }

    [Fact]
    public async Task BeforeSend_CannotReintroduceSecretsOnWire()
    {
        var (module, handler, _) = CreateErrors(o =>
        {
            o.BeforeSend = evt =>
            {
                evt.Message = "card 4111111111111111";
                evt.Metadata ??= new Dictionary<string, object>();
                evt.Metadata["Authorization"] = "Bearer abc";
                evt.Metadata["nested"] = new Dictionary<string, object> { ["token"] = "secret-token" };
                return evt;
            };
        });

        await module.CaptureExceptionAsync(new Exception("original"));

        Assert.Single(handler.Bodies);
        using var doc = JsonDocument.Parse(handler.Bodies[0]);
        Assert.Equal("card [REDACTED]", doc.RootElement.GetProperty("message").GetString());
        var metadata = doc.RootElement.GetProperty("metadata");
        Assert.Equal("[REDACTED]", metadata.GetProperty("Authorization").GetString());
        Assert.Equal("[REDACTED]", metadata.GetProperty("nested").GetProperty("token").GetString());
    }

    // ── SampleRate ────────────────────────────────────────────────────

    [Fact]
    public async Task SampleRate_Zero_DropsEverything()
    {
        var (module, handler, _) = CreateErrors(o => o.SampleRate = 0.0);

        var result = await module.CaptureExceptionAsync(new Exception("sampled out"));

        Assert.Null(result);
        Assert.Empty(handler.Bodies);
    }

    [Fact]
    public async Task SampleRate_One_KeepsEverything()
    {
        var (module, handler, _) = CreateErrors(o => o.SampleRate = 1.0);

        await module.CaptureExceptionAsync(new Exception("kept"));

        Assert.Single(handler.Bodies);
    }

    [Fact]
    public async Task SampleRate_Partial_UsesInjectedRng()
    {
        // rate = 0.5: draw 0.4 < 0.5 keeps; draw 0.6 >= 0.5 drops.
        var (module, handler, options) = CreateErrors(o => o.SampleRate = 0.5);

        options.SampleRng = () => 0.4; // keep
        await module.CaptureExceptionAsync(new Exception("keep"));
        Assert.Single(handler.Bodies);

        options.SampleRng = () => 0.6; // drop
        await module.CaptureExceptionAsync(new Exception("drop"));
        Assert.Single(handler.Bodies); // still one
    }

    [Fact]
    public async Task SampleRate_DropsBeforeBeforeSend()
    {
        var beforeSendCalled = false;
        var (module, handler, _) = CreateErrors(o =>
        {
            o.SampleRate = 0.0;
            o.BeforeSend = e => { beforeSendCalled = true; return e; };
        });

        await module.CaptureExceptionAsync(new Exception("x"));

        Assert.Empty(handler.Bodies);
        Assert.False(beforeSendCalled); // sampling short-circuits BeforeSend
    }

    // ── TracesSampleRate → traceparent flag ───────────────────────────

    private static TracingModule CreateTracing(Action<AllStakOptions>? configure = null)
    {
        var options = new AllStakOptions { ApiKey = "ask_trace", Host = "https://fake.allstak.test" };
        configure?.Invoke(options);
        var transport = new HttpTransport(options, NullLogger.Instance);
        return new TracingModule(transport, options, NullLogger.Instance);
    }

    [Fact]
    public void TracesSampleRate_Null_KeepsLegacySampledBehavior()
    {
        var tracing = CreateTracing(o => o.TracesSampleRate = null);
        Assert.True(tracing.IsCurrentTraceSampled);
    }

    [Fact]
    public void TracesSampleRate_One_Sampled_Traceparent01()
    {
        var tracing = CreateTracing(o => o.TracesSampleRate = 1.0);
        Assert.True(tracing.IsCurrentTraceSampled);

        var headers = new Dictionary<string, string>();
        var traceparent = BuildTraceparent("a".PadLeft(32, '0'), "b".PadLeft(16, '0'), tracing.IsCurrentTraceSampled);
        Assert.EndsWith("-01", traceparent);
    }

    [Fact]
    public void TracesSampleRate_Zero_NotSampled_Traceparent00()
    {
        var tracing = CreateTracing(o => o.TracesSampleRate = 0.0);
        Assert.False(tracing.IsCurrentTraceSampled);

        var traceparent = BuildTraceparent("a".PadLeft(32, '0'), "b".PadLeft(16, '0'), tracing.IsCurrentTraceSampled);
        Assert.EndsWith("-00", traceparent);
    }

    [Fact]
    public void TracesSampleRate_Partial_UsesInjectedRng()
    {
        var tracingKeep = CreateTracing(o => { o.TracesSampleRate = 0.5; o.SampleRng = () => 0.1; });
        Assert.True(tracingKeep.IsCurrentTraceSampled);

        var tracingDrop = CreateTracing(o => { o.TracesSampleRate = 0.5; o.SampleRng = () => 0.9; });
        Assert.False(tracingDrop.IsCurrentTraceSampled);
    }

    [Fact]
    public void TracesSampleRate_DecisionIsStablePerTrace()
    {
        int calls = 0;
        var tracing = CreateTracing(o => { o.TracesSampleRate = 0.5; o.SampleRng = () => { calls++; return 0.1; }; });

        Assert.True(tracing.IsCurrentTraceSampled);
        Assert.True(tracing.IsCurrentTraceSampled);
        Assert.True(tracing.IsCurrentTraceSampled);
        Assert.Equal(1, calls); // decided once, cached
    }

    [Fact]
    public void TracesSampleRate_NotSampled_SpanNotBuffered()
    {
        var tracing = CreateTracing(o => o.TracesSampleRate = 0.0);

        var span = tracing.StartSpan("op", "desc");
        span.Finish("ok");

        var buffer = typeof(TracingModule).GetField("_buffer",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(tracing)!;
        var count = (int)buffer.GetType().GetProperty("Count")!.GetValue(buffer)!;
        Assert.Equal(0, count);
    }

    [Fact]
    public void TracesSampleRate_Sampled_SpanBuffered()
    {
        var tracing = CreateTracing(o => o.TracesSampleRate = 1.0);

        var span = tracing.StartSpan("op", "desc");
        span.Finish("ok");

        var buffer = typeof(TracingModule).GetField("_buffer",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(tracing)!;
        var count = (int)buffer.GetType().GetProperty("Count")!.GetValue(buffer)!;
        Assert.Equal(1, count);
    }

    [Fact]
    public void TraceHeadersApply_ReflectsSampledFlag_RealPath()
    {
        // Drive the actual TraceHeaders.Apply that the HttpClient handler / middleware
        // use, asserting the real wire traceparent reflects the sampling decision.
        var traceId = new string('a', 32);
        var spanId = new string('b', 16);

        var sampledReq = new HttpRequestMessage();
        global::AllStak.TraceHeaders.Apply(sampledReq.Headers, traceId, "req-1", spanId, sampled: true);
        Assert.EndsWith("-01", sampledReq.Headers.GetValues("traceparent").Single());

        var notSampledReq = new HttpRequestMessage();
        global::AllStak.TraceHeaders.Apply(notSampledReq.Headers, traceId, "req-2", spanId, sampled: false);
        Assert.EndsWith("-00", notSampledReq.Headers.GetValues("traceparent").Single());
    }

    // Mirror TraceHeaders' traceparent format for assertion purposes.
    private static string BuildTraceparent(string traceId, string spanId, bool sampled) =>
        $"00-{traceId}-{spanId[..Math.Min(16, spanId.Length)]}-{(sampled ? "01" : "00")}";
}
