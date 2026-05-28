using System.Net;
using System.Text.Json;
using AllStak;
using AllStak.Session;
using AllStak.Transport;
using Microsoft.Extensions.Logging.Abstractions;

namespace AllStak.Tests;

/// <summary>
/// Tests for <see cref="SessionTracker"/> release-health lifecycle: start payload
/// shape, end payload shape + status transitions (ok -> errored -> crashed), the
/// SDK-version release fallback, idempotency, disabled-transport behavior, and the
/// <see cref="AllStakOptions.EnableAutoSessionTracking"/> opt-out.
/// </summary>
public sealed class SessionTrackerTests
{
    /// <summary>Captures the JSON body of every request keyed by path; replies 202.</summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly object _lock = new();
        public List<(string Path, string Body)> Calls { get; } = new();

        public IReadOnlyList<string> BodiesFor(string path)
        {
            lock (_lock) return Calls.Where(c => c.Path == path).Select(c => c.Body).ToList();
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content != null ? await request.Content.ReadAsStringAsync(ct) : "";
            lock (_lock) Calls.Add((request.RequestUri!.AbsolutePath, body));
            return new HttpResponseMessage(HttpStatusCode.Accepted)
            {
                Content = new StringContent("""{"success":true,"data":{"id":"x","sessionId":"y"}}"""),
            };
        }
    }

    private static AllStakOptions MakeOptions(Action<AllStakOptions>? configure = null)
    {
        var options = new AllStakOptions
        {
            ApiKey = "ask_test_session",
            Host = "https://fake.allstak.test",
            Environment = "test",
            Release = "v0.0.1-test",
            MaxRetries = 1,
            ShutdownFlushTimeoutMs = 2_000,
        };
        configure?.Invoke(options);
        return options;
    }

    private static (HttpTransport transport, CapturingHandler handler) MakeTransport(AllStakOptions options)
    {
        var handler = new CapturingHandler();
        var transport = new HttpTransport(options, NullLogger.Instance);
        var httpField = typeof(HttpTransport).GetField("_http",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-AllStak-Key", options.ApiKey);
        httpField.SetValue(transport, client);
        return (transport, handler);
    }

    /// <summary>Spin until at least one body is captured for the path, or time out.</summary>
    private static async Task<string> WaitForBodyAsync(CapturingHandler handler, string path)
    {
        for (int i = 0; i < 100; i++)
        {
            var bodies = handler.BodiesFor(path);
            if (bodies.Count > 0) return bodies[0];
            await Task.Delay(20);
        }
        throw new Xunit.Sdk.XunitException($"No request captured for {path} within timeout");
    }

    // ── start payload shape ─────────────────────────────────────────────

    [Fact]
    public async Task Start_PostsSessionStart_WithConfiguredAttributes()
    {
        var options = MakeOptions();
        var (transport, handler) = MakeTransport(options);
        var tracker = new SessionTracker(options, transport, NullLogger.Instance);

        var session = tracker.Start();

        var body = await WaitForBodyAsync(handler, "/ingest/v1/sessions/start");
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        Assert.Equal(session.Id, root.GetProperty("sessionId").GetString());
        Assert.Equal("v0.0.1-test", root.GetProperty("release").GetString());
        Assert.Equal("test", root.GetProperty("environment").GetString());
        Assert.Equal("allstak-dotnet", root.GetProperty("sdkName").GetString());
        Assert.Equal("dotnet", root.GetProperty("platform").GetString());
        Assert.False(string.IsNullOrEmpty(root.GetProperty("sdkVersion").GetString()));
    }

    [Fact]
    public async Task Start_AttachesUserId_WhenProvided()
    {
        var options = MakeOptions();
        var (transport, handler) = MakeTransport(options);
        var tracker = new SessionTracker(options, transport, NullLogger.Instance);

        tracker.Start("user-42");

        var body = await WaitForBodyAsync(handler, "/ingest/v1/sessions/start");
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("user-42", doc.RootElement.GetProperty("userId").GetString());
    }

    [Fact]
    public void Start_IsIdempotent()
    {
        var options = MakeOptions();
        var (transport, _) = MakeTransport(options);
        var tracker = new SessionTracker(options, transport, NullLogger.Instance);

        var first = tracker.Start();
        var second = tracker.Start();
        Assert.Equal(first.Id, second.Id);
    }

    [Fact]
    public async Task NoRelease_StillSends_FallingBackToSdkVersion()
    {
        // Release-health sessions are NEVER sampled: when no release is resolved
        // the start envelope falls back to the SDK version so the session is still
        // attributable rather than dropped.
        var options = MakeOptions(o => o.Release = null);
        var (transport, handler) = MakeTransport(options);
        var tracker = new SessionTracker(options, transport, NullLogger.Instance);

        tracker.Start();

        var body = await WaitForBodyAsync(handler, "/ingest/v1/sessions/start");
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(AllStakOptions.SdkVersion, doc.RootElement.GetProperty("release").GetString());
    }

    // ── end payload shape + status transitions ──────────────────────────

    [Fact]
    public async Task End_PostsOk_WhenNoErrors()
    {
        var options = MakeOptions();
        var (transport, handler) = MakeTransport(options);
        var tracker = new SessionTracker(options, transport, NullLogger.Instance);

        var session = tracker.Start();
        tracker.End();

        var body = await WaitForBodyAsync(handler, "/ingest/v1/sessions/end");
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        Assert.Equal("ok", root.GetProperty("status").GetString());
        Assert.Equal(session.Id, root.GetProperty("sessionId").GetString());
        Assert.True(root.GetProperty("durationMs").GetInt64() >= 0);
    }

    [Fact]
    public async Task RecordError_ThenEnd_PostsErrored()
    {
        var options = MakeOptions();
        var (transport, handler) = MakeTransport(options);
        var tracker = new SessionTracker(options, transport, NullLogger.Instance);

        tracker.Start();
        tracker.RecordError();
        tracker.RecordError();
        tracker.End();

        var body = await WaitForBodyAsync(handler, "/ingest/v1/sessions/end");
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("errored", doc.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task OkToErroredToCrashed_TransitionsAreMonotonic()
    {
        var options = MakeOptions();
        var (transport, handler) = MakeTransport(options);
        var tracker = new SessionTracker(options, transport, NullLogger.Instance);

        var session = tracker.Start();
        Assert.Equal(SessionStatus.Ok, session.Status);

        tracker.RecordError();
        Assert.Equal(SessionStatus.Errored, session.Status);

        tracker.RecordCrash();
        Assert.Equal(SessionStatus.Crashed, session.Status);

        // A later handled error must not downgrade a crashed session.
        tracker.RecordError();
        Assert.Equal(SessionStatus.Crashed, session.Status);

        tracker.End();

        var body = await WaitForBodyAsync(handler, "/ingest/v1/sessions/end");
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("crashed", doc.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task End_IsIdempotent_SecondCallNoOp()
    {
        var options = MakeOptions();
        var (transport, handler) = MakeTransport(options);
        var tracker = new SessionTracker(options, transport, NullLogger.Instance);

        tracker.Start();
        tracker.End();
        await WaitForBodyAsync(handler, "/ingest/v1/sessions/end");
        tracker.End();

        // Give any erroneous second POST a chance to land before asserting.
        await Task.Delay(50);
        Assert.Single(handler.BodiesFor("/ingest/v1/sessions/end"));
    }

    [Fact]
    public async Task ExplicitStatus_OverridesAccumulatedStatus()
    {
        var options = MakeOptions();
        var (transport, handler) = MakeTransport(options);
        var tracker = new SessionTracker(options, transport, NullLogger.Instance);

        tracker.Start();
        tracker.RecordError();
        tracker.End(SessionStatus.Abnormal);

        var body = await WaitForBodyAsync(handler, "/ingest/v1/sessions/end");
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("abnormal", doc.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public void CurrentSessionId_StableAcrossReads_ThenNullAfterEnd()
    {
        var options = MakeOptions();
        var (transport, _) = MakeTransport(options);
        var tracker = new SessionTracker(options, transport, NullLogger.Instance);

        var session = tracker.Start();
        Assert.Equal(session.Id, tracker.CurrentSessionId);
        Assert.Equal(session.Id, tracker.CurrentSessionId);

        tracker.End();
        Assert.Null(tracker.CurrentSessionId);
    }

    // ── disabled transport keeps in-memory tracking, skips network ──────

    [Fact]
    public async Task DisabledTransport_SkipsNetwork_ButInMemoryStillWorks()
    {
        var options = MakeOptions();
        var (transport, handler) = MakeTransport(options);
        // Disable the transport via reflection (mirrors a 401-induced disable).
        typeof(HttpTransport).GetField("_disabled",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(transport, true);

        var tracker = new SessionTracker(options, transport, NullLogger.Instance);
        var session = tracker.Start();
        tracker.RecordError();
        tracker.End();

        Assert.Equal(SessionStatus.Errored, session.Status);
        Assert.Equal(1, session.ErrorCount);
        await Task.Delay(50);
        Assert.Empty(handler.Calls);
    }
}
