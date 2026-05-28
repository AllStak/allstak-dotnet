using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace AllStak.Transport;

/// <summary>
/// A single persisted envelope read back off disk: the ingest <paramref name="Path"/>
/// it must be re-POSTed to, the already-PII-scrubbed JSON body, and the on-disk
/// <paramref name="File"/> backing it (so the drainer can delete it once the
/// envelope is accepted or permanently rejected).
/// </summary>
internal readonly record struct CachedEnvelope(string Path, string Body, string File);

/// <summary>
/// Sentry-style on-disk spool for telemetry that could not be delivered live
/// (network outage, retries exhausted, or process shutting down with events still
/// buffered). One file per envelope. Bodies written here are <b>already PII
/// scrubbed</b> — the transport sanitizes on the wire-in chokepoint and only the
/// scrubbed JSON is ever handed to this store, so secrets never touch disk.
///
/// <para>The store is bounded three ways and drops the OLDEST entries first:
/// by file count, by total bytes, and by max age. It is fully fail-open: every
/// filesystem operation is wrapped so a read-only FS, a sandboxed/serverless host,
/// or a transient IO error degrades silently to the SDK's existing in-memory
/// behavior — it never throws and never blocks init or capture.</para>
/// </summary>
internal sealed class FileSystemCache
{
    // Sane server defaults (a few MB, ~48h). The total byte cap dominates for
    // large payloads; the count cap dominates for many small ones.
    internal const int DefaultMaxEnvelopes = 100;
    internal const long DefaultMaxBytes = 5L * 1024 * 1024; // 5 MB
    internal static readonly TimeSpan DefaultMaxAge = TimeSpan.FromHours(48);

    private const string FileExtension = ".allstak-envelope.json";

    private readonly string _dir;
    private readonly int _maxEnvelopes;
    private readonly long _maxBytes;
    private readonly TimeSpan _maxAge;
    private readonly ILogger _logger;
    private readonly object _lock = new();

    /// <summary>True only when the spool directory exists and is writable.</summary>
    public bool IsAvailable { get; }

    /// <summary>The resolved spool directory (for diagnostics / tests).</summary>
    public string Directory => _dir;

    public FileSystemCache(
        string directory,
        ILogger logger,
        int maxEnvelopes = DefaultMaxEnvelopes,
        long maxBytes = DefaultMaxBytes,
        TimeSpan? maxAge = null)
    {
        _dir = directory;
        _logger = logger;
        _maxEnvelopes = Math.Max(1, maxEnvelopes);
        _maxBytes = Math.Max(1, maxBytes);
        _maxAge = maxAge ?? DefaultMaxAge;
        IsAvailable = TryEnsureWritable(directory, logger);
    }

    /// <summary>
    /// Default spool directory: a per-app subfolder under the OS local-app-data /
    /// temp dir. Never throws — returns a best-effort path the caller can pass to
    /// the constructor (which will then verify writability).
    /// </summary>
    public static string DefaultDirectory()
    {
        string baseDir;
        try
        {
            baseDir = System.Environment.GetFolderPath(
                System.Environment.SpecialFolder.LocalApplicationData,
                System.Environment.SpecialFolderOption.None);
            if (string.IsNullOrEmpty(baseDir))
                baseDir = Path.GetTempPath();
        }
        catch
        {
            try { baseDir = Path.GetTempPath(); }
            catch { baseDir = "."; }
        }
        return Path.Combine(baseDir, "AllStak", "offline-cache");
    }

    private static bool TryEnsureWritable(string dir, ILogger logger)
    {
        try
        {
            System.IO.Directory.CreateDirectory(dir);
            // Probe write access — read-only FS / sandboxed hosts fail here.
            var probe = Path.Combine(dir, ".allstak-write-probe");
            File.WriteAllText(probe, "1");
            File.Delete(probe);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "[AllStak] offline cache dir not writable ({Dir}); degrading to in-memory", dir);
            return false;
        }
    }

    /// <summary>
    /// Persist one already-scrubbed envelope. No-op (returns false) when the store
    /// is unavailable. Never throws. Enforces the bounds (drop-oldest) after write.
    /// </summary>
    public bool Persist(string path, string scrubbedBody)
    {
        if (!IsAvailable) return false;
        try
        {
            var record = new EnvelopeRecord { Path = path, Body = scrubbedBody, StoredAtUnixMs = NowMs() };
            var json = JsonSerializer.Serialize(record);
            // Sortable, collision-resistant name: <unix-ms>-<rand><ext>. Lexical
            // order == chronological order, which the drop-oldest logic relies on.
            var name = $"{record.StoredAtUnixMs:D013}-{RandomToken()}{FileExtension}";
            var full = Path.Combine(_dir, name);
            lock (_lock)
            {
                // Atomic-ish: write to temp then move into place so a crash mid-write
                // never leaves a half-parsed envelope the drainer would choke on.
                var tmp = full + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, full, overwrite: true);
                Enforce();
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[AllStak] failed to persist offline envelope (path={Path})", path);
            return false;
        }
    }

    /// <summary>
    /// Load all currently-spooled envelopes, oldest first. Never throws; returns an
    /// empty list when unavailable or on error. Unreadable / corrupt files are
    /// skipped (and deleted) rather than aborting the drain.
    /// </summary>
    public IReadOnlyList<CachedEnvelope> Load()
    {
        if (!IsAvailable) return Array.Empty<CachedEnvelope>();
        var result = new List<CachedEnvelope>();
        try
        {
            lock (_lock)
            {
                Enforce(); // expire stale entries before replay
                foreach (var file in EnumerateOldestFirst())
                {
                    try
                    {
                        var json = File.ReadAllText(file);
                        var rec = JsonSerializer.Deserialize<EnvelopeRecord>(json);
                        if (rec is null || string.IsNullOrEmpty(rec.Path) || rec.Body is null)
                        {
                            TryDelete(file);
                            continue;
                        }
                        result.Add(new CachedEnvelope(rec.Path, rec.Body, file));
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "[AllStak] dropping unreadable offline envelope {File}", file);
                        TryDelete(file);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[AllStak] failed to load offline cache");
        }
        return result;
    }

    /// <summary>Remove a single spooled envelope by its backing file. Never throws.</summary>
    public void Remove(string file)
    {
        if (!IsAvailable) return;
        lock (_lock) TryDelete(file);
    }

    /// <summary>Current spooled envelope count (best-effort; 0 when unavailable).</summary>
    public int Count()
    {
        if (!IsAvailable) return 0;
        try { lock (_lock) return EnumerateOldestFirst().Count(); }
        catch { return 0; }
    }

    // ── bounds enforcement (drop OLDEST) ─────────────────────────────────

    private void Enforce()
    {
        try
        {
            var files = EnumerateOldestFirst()
                .Select(f => new FileInfo(f))
                .Where(fi => fi.Exists)
                .ToList();

            var now = DateTimeOffset.UtcNow;

            // 1) Max age — drop anything older than the cutoff.
            if (_maxAge > TimeSpan.Zero)
            {
                foreach (var fi in files.ToList())
                {
                    if (now - new DateTimeOffset(fi.LastWriteTimeUtc, TimeSpan.Zero) > _maxAge)
                    {
                        TryDelete(fi.FullName);
                        files.Remove(fi);
                    }
                }
            }

            // 2) Count cap — drop oldest until within the envelope limit.
            while (files.Count > _maxEnvelopes)
            {
                TryDelete(files[0].FullName);
                files.RemoveAt(0);
            }

            // 3) Byte cap — drop oldest until total bytes fit.
            long total = files.Sum(fi => fi.Length);
            while (total > _maxBytes && files.Count > 0)
            {
                total -= files[0].Length;
                TryDelete(files[0].FullName);
                files.RemoveAt(0);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[AllStak] offline cache enforcement failed");
        }
    }

    private IEnumerable<string> EnumerateOldestFirst()
    {
        // File names are zero-padded unix-ms prefixed, so ordinal sort == oldest-first.
        return System.IO.Directory
            .EnumerateFiles(_dir, "*" + FileExtension)
            .OrderBy(Path.GetFileName, StringComparer.Ordinal);
    }

    private void TryDelete(string file)
    {
        try { if (File.Exists(file)) File.Delete(file); }
        catch (Exception ex) { _logger.LogDebug(ex, "[AllStak] could not delete offline envelope {File}", file); }
    }

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static string RandomToken()
    {
        Span<byte> b = stackalloc byte[6];
        RandomNumberGenerator.Fill(b);
        return Convert.ToHexString(b).ToLowerInvariant();
    }

    /// <summary>On-disk shape of a spooled envelope.</summary>
    private sealed class EnvelopeRecord
    {
        public string Path { get; set; } = string.Empty;
        public string Body { get; set; } = string.Empty;
        public long StoredAtUnixMs { get; set; }
    }
}
