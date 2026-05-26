using System.Diagnostics;

namespace AllStak;

/// <summary>
/// CI-free automatic release detection (step 3 of release resolution).
///
/// <para>The runtime mechanism is a guarded <c>git describe --tags --always
/// --dirty</c> shell-out, run at most once and cached. It is seamable: the
/// parse logic (<see cref="Parse"/>) takes a git-runner delegate so tests
/// inject canned output or a throwing runner without needing a real repo.</para>
///
/// <para><b>Production note:</b> a shipped assembly has no <c>.git</c>
/// directory, so the runtime git shell-out only resolves a release when the
/// app runs from a source checkout. The robust production mechanism is a
/// <i>build-time embed</i>: SourceLink / <c>AssemblyInformationalVersion</c>
/// baking the commit SHA into the assembly at build time (the SDK version
/// constant already comes from the assembly via
/// <see cref="AllStakOptions.SdkVersion"/>). That is the recommended path for
/// published packages; this runtime detector is the requested no-CI fallback
/// for local/source runs, with the SDK-version constant as the final guarantee
/// that release is never empty.</para>
/// </summary>
internal static class ReleaseDetector
{
    /// <summary>
    /// Runs a git command with the given args and returns trimmed stdout, or
    /// <c>null</c> on any failure. Implementations should not throw for expected
    /// failures; <see cref="Parse"/> treats both <c>null</c> and a thrown
    /// exception as "no release".
    /// </summary>
    internal delegate string? GitRunner(string[] args);

    /// <summary>
    /// Default runner: shells out to <c>git</c> with a short timeout. Returns
    /// <c>null</c> on non-zero exit, timeout, or when git is unavailable.
    /// </summary>
    internal static readonly GitRunner DefaultGitRunner = static args =>
    {
        try
        {
            var psi = new ProcessStartInfo("git")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var proc = Process.Start(psi);
            if (proc is null) return null;

            var stdout = proc.StandardOutput.ReadToEnd();
            if (!proc.WaitForExit(2_000))
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
                return null;
            }
            if (proc.ExitCode != 0) return null;
            var trimmed = stdout.Trim();
            return string.IsNullOrEmpty(trimmed) ? null : trimmed;
        }
        catch
        {
            return null;
        }
    };

    /// <summary>
    /// Pure, seamable parse: derives the release from <c>git describe</c>. Any
    /// exception or null/blank output yields <c>null</c> so the caller falls
    /// through to the version fallback. Tests pass a fake runner.
    /// </summary>
    internal static string? Parse(GitRunner? runner)
    {
        if (runner is null) return null;
        try
        {
            var output = runner(new[] { "describe", "--tags", "--always", "--dirty" });
            if (string.IsNullOrWhiteSpace(output)) return null;
            return output.Trim();
        }
        catch
        {
            // Fail-open: detection is best-effort and must never break init.
            return null;
        }
    }

    private static string? _cached;
    private static bool _cachedComputed;
    private static readonly object _gate = new();

    /// <summary>
    /// Cached convenience over <see cref="Parse"/> using the default git runner.
    /// The shell-out runs at most once per process. Unit tests exercise
    /// <see cref="Parse"/> directly with a fake runner, so the parse logic needs
    /// no real repo.
    /// </summary>
    internal static string? DetectCached()
    {
        if (_cachedComputed) return _cached;
        lock (_gate)
        {
            if (!_cachedComputed)
            {
                _cached = Parse(DefaultGitRunner);
                _cachedComputed = true;
            }
        }
        return _cached;
    }
}
