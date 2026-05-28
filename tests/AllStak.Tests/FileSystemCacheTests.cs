using AllStak.Transport;
using Microsoft.Extensions.Logging.Abstractions;

namespace AllStak.Tests;

/// <summary>
/// Tests for <see cref="FileSystemCache"/>: persist + load round-trip, oldest-first
/// ordering, cap/eviction (drop oldest by count and bytes), max-age expiry, removal,
/// and graceful no-op when the store directory is unavailable.
/// </summary>
public sealed class FileSystemCacheTests : IDisposable
{
    private readonly string _dir;

    public FileSystemCacheTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "allstak-cache-test-" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort */ }
    }

    private FileSystemCache MakeCache(int maxEnvelopes = 100, long maxBytes = 5L * 1024 * 1024, TimeSpan? maxAge = null)
        => new(_dir, NullLogger.Instance, maxEnvelopes, maxBytes, maxAge);

    [Fact]
    public void Persist_ThenLoad_RoundTrips()
    {
        var cache = MakeCache();
        Assert.True(cache.IsAvailable);

        Assert.True(cache.Persist("/ingest/v1/errors", """{"message":"boom"}"""));

        var loaded = cache.Load();
        Assert.Single(loaded);
        Assert.Equal("/ingest/v1/errors", loaded[0].Path);
        Assert.Contains("boom", loaded[0].Body);
    }

    [Fact]
    public void Load_ReturnsOldestFirst()
    {
        var cache = MakeCache();
        cache.Persist("/ingest/v1/logs", """{"n":1}""");
        Thread.Sleep(5);
        cache.Persist("/ingest/v1/logs", """{"n":2}""");
        Thread.Sleep(5);
        cache.Persist("/ingest/v1/logs", """{"n":3}""");

        var loaded = cache.Load();
        Assert.Equal(3, loaded.Count);
        Assert.Contains("1", loaded[0].Body);
        Assert.Contains("2", loaded[1].Body);
        Assert.Contains("3", loaded[2].Body);
    }

    [Fact]
    public void CountCap_DropsOldest()
    {
        var cache = MakeCache(maxEnvelopes: 3);
        for (int i = 1; i <= 6; i++)
        {
            cache.Persist("/ingest/v1/logs", $$"""{"n":{{i}}}""");
            Thread.Sleep(3); // ensure distinct, ordered file-name timestamps
        }

        var loaded = cache.Load();
        Assert.Equal(3, loaded.Count);
        // Oldest (1,2,3) dropped; newest (4,5,6) kept, oldest-first.
        Assert.Contains("4", loaded[0].Body);
        Assert.Contains("5", loaded[1].Body);
        Assert.Contains("6", loaded[2].Body);
    }

    [Fact]
    public void ByteCap_DropsOldest()
    {
        // Each envelope JSON is comfortably over 200 bytes thanks to the body.
        var body = "{\"data\":\"" + new string('x', 400) + "\"}";
        var cache = MakeCache(maxEnvelopes: 1000, maxBytes: 1200); // ~2-3 envelopes fit

        for (int i = 0; i < 6; i++)
        {
            cache.Persist("/ingest/v1/logs", body);
            Thread.Sleep(3);
        }

        var loaded = cache.Load();
        Assert.True(loaded.Count < 6, "byte cap should have evicted oldest envelopes");
        Assert.True(loaded.Count >= 1, "byte cap should keep at least the newest envelope");
    }

    [Fact]
    public void MaxAge_ExpiresStaleEntries()
    {
        var cache = MakeCache(maxAge: TimeSpan.FromMilliseconds(50));
        cache.Persist("/ingest/v1/logs", """{"n":1}""");
        Assert.Equal(1, cache.Count());

        Thread.Sleep(120);
        // Touch is via load/persist enforcement — Load() enforces age first.
        var loaded = cache.Load();
        Assert.Empty(loaded);
    }

    [Fact]
    public void Remove_DeletesSingleEnvelope()
    {
        var cache = MakeCache();
        cache.Persist("/ingest/v1/logs", """{"n":1}""");
        var loaded = cache.Load();
        Assert.Single(loaded);

        cache.Remove(loaded[0].File);
        Assert.Empty(cache.Load());
    }

    [Fact]
    public void UnwritableDirectory_DegradesGracefully()
    {
        // Point at a path under an existing *file* so CreateDirectory fails → no-op.
        var file = Path.Combine(_dir, "not-a-dir");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(file, "x");
        var badPath = Path.Combine(file, "sub", "cache");

        var cache = new FileSystemCache(badPath, NullLogger.Instance);
        Assert.False(cache.IsAvailable);
        // All operations must no-op silently, never throw.
        Assert.False(cache.Persist("/ingest/v1/errors", "{}"));
        Assert.Empty(cache.Load());
        Assert.Equal(0, cache.Count());
        cache.Remove("anything"); // no throw
    }

    [Fact]
    public void DefaultDirectory_NeverThrows()
    {
        var dir = FileSystemCache.DefaultDirectory();
        Assert.False(string.IsNullOrEmpty(dir));
    }
}
