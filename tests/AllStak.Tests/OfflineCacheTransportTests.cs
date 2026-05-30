using System.Net;
using AllStak.Transport;
using Microsoft.Extensions.Logging.Abstractions;

namespace AllStak.Tests;

/// <summary>
/// Integration tests for the offline/persistent event queue wired into
/// <see cref="HttpTransport"/>: persist-on-send-failure, drain-and-resend on the
/// next init, scrub-before-persist (no secret hits disk), session calls are NOT
/// persisted, opt-out disables it, and graceful no-op when the store is unavailable.
/// </summary>
public sealed class OfflineCacheTransportTests : IDisposable
{
    private readonly string _dir;

    public OfflineCacheTransportTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "allstak-xport-cache-" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort */ }
    }

    private AllStakOptions MakeOptions(bool offline = true, int maxRetries = 2) => new()
    {
        ApiKey = "ask_test_offline",
        Host = "https://fake.allstak.test",
        MaxRetries = maxRetries,
        ConnectTimeoutMs = 500,
        ReadTimeoutMs = 500,
        EnableOfflineCache = offline,
        CacheDirectoryPath = _dir,
    };

    /// <summary>Build an HttpTransport with a mock handler swapped in via reflection.</summary>
    private HttpTransport CreateWithHandler(HttpMessageHandler handler, AllStakOptions options)
    {
        var transport = new HttpTransport(options, NullLogger.Instance);
        var httpField = typeof(HttpTransport).GetField("_http",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMilliseconds(options.ConnectTimeoutMs + options.ReadTimeoutMs),
        };
        httpField.SetValue(transport, client);
        return transport;
    }

    private static FileSystemCache CacheOf(HttpTransport t)
    {
        var prop = typeof(HttpTransport).GetProperty("Cache",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        return (FileSystemCache)prop.GetValue(t)!;
    }

    // ── Helpers ───────────────────────────────────────────────────────

    private sealed class SequencedHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpResponseMessage>> _responses = new();
        public int CallCount { get; private set; }
        public List<string> SeenBodies { get; } = new();

        public void Enqueue(HttpStatusCode status, string body = "")
            => _responses.Enqueue(() => new HttpResponseMessage(status) { Content = new StringContent(body) });

        public void EnqueueThrow()
            => _responses.Enqueue(() => throw new HttpRequestException("network down"));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            CallCount++;
            if (request.Content != null)
                SeenBodies.Add(await TestHttpContent.ReadDecodedStringAsync(request, ct));
            if (_responses.Count == 0)
                return new HttpResponseMessage(HttpStatusCode.OK);
            return _responses.Dequeue()();
        }
    }

    // ── persist-on-send-failure ───────────────────────────────────────

    [Fact]
    public async Task NetworkFailure_PersistsScrubbedEnvelope()
    {
        var handler = new SequencedHandler();
        handler.EnqueueThrow();
        handler.EnqueueThrow();
        var transport = CreateWithHandler(handler, MakeOptions(maxRetries: 2));

        await Assert.ThrowsAsync<AllStakTransportException>(
            () => transport.PostAsync("/ingest/v1/errors", new { message = "boom" }));

        var spooled = CacheOf(transport).Load();
        Assert.Single(spooled);
        Assert.Equal("/ingest/v1/errors", spooled[0].Path);
        Assert.Contains("boom", spooled[0].Body);
    }

    [Fact]
    public async Task ServerError5xxExhausted_Persists()
    {
        var handler = new SequencedHandler();
        handler.Enqueue(HttpStatusCode.InternalServerError);
        handler.Enqueue(HttpStatusCode.InternalServerError);
        var transport = CreateWithHandler(handler, MakeOptions(maxRetries: 2));

        await Assert.ThrowsAsync<AllStakTransportException>(
            () => transport.PostAsync("/ingest/v1/logs", new { msg = "hi" }));

        Assert.Equal(1, CacheOf(transport).Count());
    }

    [Fact]
    public async Task PermanentReject4xx_DoesNotPersist()
    {
        var handler = new SequencedHandler();
        handler.Enqueue(HttpStatusCode.BadRequest, "bad");
        var transport = CreateWithHandler(handler, MakeOptions(maxRetries: 2));

        var (status, _) = await transport.PostAsync("/ingest/v1/errors", new { message = "boom" });

        Assert.Equal(400, status);
        Assert.Equal(0, CacheOf(transport).Count()); // 4xx is permanent — never spooled
    }

    // ── scrub-before-persist ──────────────────────────────────────────

    [Fact]
    public async Task PersistedBody_IsScrubbed_NoSecretHitsDisk()
    {
        var handler = new SequencedHandler();
        handler.EnqueueThrow();
        handler.EnqueueThrow();
        var transport = CreateWithHandler(handler, MakeOptions(maxRetries: 2));

        await Assert.ThrowsAsync<AllStakTransportException>(
            () => transport.PostAsync("/ingest/v1/errors", new
            {
                message = "login failed",
                metadata = new { password = "hunter2", authorization = "Bearer super-secret-token" },
            }));

        // Read the raw bytes off disk and assert no secret survives.
        var files = Directory.GetFiles(_dir, "*.json");
        Assert.Single(files);
        var onDisk = File.ReadAllText(files[0]);
        Assert.DoesNotContain("hunter2", onDisk);
        Assert.DoesNotContain("super-secret-token", onDisk);
        Assert.Contains("[REDACTED]", onDisk);
    }

    // ── session calls are NOT persisted ───────────────────────────────

    [Theory]
    [InlineData("/ingest/v1/sessions/start")]
    [InlineData("/ingest/v1/sessions/end")]
    [InlineData("/ingest/v1/releases")]
    public async Task LiveOnlyPaths_AreNeverPersisted(string path)
    {
        var handler = new SequencedHandler();
        handler.EnqueueThrow();
        handler.EnqueueThrow();
        var transport = CreateWithHandler(handler, MakeOptions(maxRetries: 2));

        await Assert.ThrowsAsync<AllStakTransportException>(
            () => transport.PostAsync(path, new { sessionId = "s1" }));

        Assert.Equal(0, CacheOf(transport).Count());
    }

    // ── opt-out disables it ────────────────────────────────────────────

    [Fact]
    public void OptOut_NoCacheCreated()
    {
        var handler = new SequencedHandler();
        var transport = CreateWithHandler(handler, MakeOptions(offline: false));
        Assert.Null(CacheOf(transport));
    }

    [Fact]
    public async Task OptOut_FailureDropsEventNoSpool()
    {
        var handler = new SequencedHandler();
        handler.EnqueueThrow();
        handler.EnqueueThrow();
        var transport = CreateWithHandler(handler, MakeOptions(offline: false, maxRetries: 2));

        await Assert.ThrowsAsync<AllStakTransportException>(
            () => transport.PostAsync("/ingest/v1/errors", new { message = "boom" }));

        Assert.False(Directory.Exists(_dir)); // opt-out never creates the spool dir
    }

    // ── graceful no-op when store unavailable ─────────────────────────

    [Fact]
    public async Task UnwritableStore_DegradesToInMemory_NoThrow()
    {
        // Point the cache dir under an existing *file* so it can't be created.
        var file = Path.Combine(_dir, "blocker");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(file, "x");
        var opts = MakeOptions();
        opts.CacheDirectoryPath = Path.Combine(file, "nested", "cache");

        var handler = new SequencedHandler();
        handler.EnqueueThrow();
        handler.EnqueueThrow();
        var transport = CreateWithHandler(handler, opts);

        Assert.False(CacheOf(transport).IsAvailable);
        // Capture path still works (throws transport exc as before) and persists nothing.
        await Assert.ThrowsAsync<AllStakTransportException>(
            () => transport.PostAsync("/ingest/v1/errors", new { message = "boom" }));
        await transport.DrainCacheAsync(); // no throw
    }

    // ── drain-and-resend on init ───────────────────────────────────────

    [Fact]
    public async Task Drain_ResendsPersistedEnvelopes_AndRemovesOnAccept()
    {
        // First transport: fail twice → spool one envelope.
        var failHandler = new SequencedHandler();
        failHandler.EnqueueThrow();
        failHandler.EnqueueThrow();
        var t1 = CreateWithHandler(failHandler, MakeOptions(maxRetries: 2));
        await Assert.ThrowsAsync<AllStakTransportException>(
            () => t1.PostAsync("/ingest/v1/errors", new { message = "survive-restart" }));
        Assert.Equal(1, CacheOf(t1).Count());

        // Second transport (simulated next init): same dir, server now accepts (202).
        var okHandler = new SequencedHandler();
        okHandler.Enqueue(HttpStatusCode.Accepted, """{"data":{"id":"x"}}""");
        var t2 = CreateWithHandler(okHandler, MakeOptions(maxRetries: 2));

        await t2.DrainCacheAsync();

        Assert.Equal(1, okHandler.CallCount);
        Assert.Contains("survive-restart", okHandler.SeenBodies[0]);
        Assert.Equal(0, CacheOf(t2).Count()); // removed after 2xx accept
    }

    [Fact]
    public async Task Drain_TransientFailure_KeepsEnvelopeOnDisk()
    {
        // Spool one.
        var failHandler = new SequencedHandler();
        failHandler.EnqueueThrow();
        failHandler.EnqueueThrow();
        var t1 = CreateWithHandler(failHandler, MakeOptions(maxRetries: 2));
        await Assert.ThrowsAsync<AllStakTransportException>(
            () => t1.PostAsync("/ingest/v1/errors", new { message = "still-down" }));
        Assert.Equal(1, CacheOf(t1).Count());

        // Drain while server is still down → envelope stays on disk for next time.
        var stillDown = new SequencedHandler();
        stillDown.EnqueueThrow();
        stillDown.EnqueueThrow();
        var t2 = CreateWithHandler(stillDown, MakeOptions(maxRetries: 2));

        await t2.DrainCacheAsync();

        Assert.Equal(1, CacheOf(t2).Count()); // not removed — transient failure
    }

    [Fact]
    public async Task Drain_PermanentReject_RemovesEnvelope()
    {
        var failHandler = new SequencedHandler();
        failHandler.EnqueueThrow();
        failHandler.EnqueueThrow();
        var t1 = CreateWithHandler(failHandler, MakeOptions(maxRetries: 2));
        await Assert.ThrowsAsync<AllStakTransportException>(
            () => t1.PostAsync("/ingest/v1/errors", new { message = "malformed" }));
        Assert.Equal(1, CacheOf(t1).Count());

        // Server now permanently rejects (422) → drop it, don't retry forever.
        var rejectHandler = new SequencedHandler();
        rejectHandler.Enqueue(HttpStatusCode.UnprocessableEntity, "nope");
        var t2 = CreateWithHandler(rejectHandler, MakeOptions(maxRetries: 2));

        await t2.DrainCacheAsync();

        Assert.Equal(0, CacheOf(t2).Count());
    }
}
