using System;
using Xunit;

namespace AllStak.Tests;

/// <summary>
/// Tests for automatic, CI-free release detection: the pure parse seam in
/// <see cref="ReleaseDetector"/> and the resolution precedence in
/// <see cref="AllStakOptions.ApplyReleaseAutodetect"/>
/// (explicit &gt; env &gt; detected &gt; version, plus the opt-out).
///
/// To stay independent of the host environment's CI vars, env-sensitive cases
/// clear the relevant variables for the duration of the test.
/// </summary>
public class ReleaseDetectionTests
{
    private sealed class EnvScope : IDisposable
    {
        private static readonly string[] Keys =
        {
            "ALLSTAK_RELEASE", "VERCEL_GIT_COMMIT_SHA", "RAILWAY_GIT_COMMIT_SHA", "RENDER_GIT_COMMIT",
        };
        private readonly (string key, string? val)[] _saved;

        public EnvScope(string? releaseEnv = null)
        {
            _saved = Array.ConvertAll(Keys, k => (k, Environment.GetEnvironmentVariable(k)));
            foreach (var k in Keys) Environment.SetEnvironmentVariable(k, null);
            if (releaseEnv != null) Environment.SetEnvironmentVariable("ALLSTAK_RELEASE", releaseEnv);
        }

        public void Dispose()
        {
            foreach (var (key, val) in _saved) Environment.SetEnvironmentVariable(key, val);
        }
    }

    // --- ReleaseDetector.Parse: pure seam ---

    [Fact]
    public void Parse_RunnerReturnsDescribeOutput_Parsed()
    {
        string[]? seen = null;
        ReleaseDetector.GitRunner runner = args => { seen = args; return "v1.2.3-4-gabc1234-dirty\n"; };
        var result = ReleaseDetector.Parse(runner);
        Assert.Equal("v1.2.3-4-gabc1234-dirty", result);
        Assert.Equal(new[] { "describe", "--tags", "--always", "--dirty" }, seen);
    }

    [Fact]
    public void Parse_RunnerReturnsNull_FallsThrough()
    {
        ReleaseDetector.GitRunner runner = _ => null;
        Assert.Null(ReleaseDetector.Parse(runner));
    }

    [Fact]
    public void Parse_RunnerReturnsBlank_FallsThrough()
    {
        ReleaseDetector.GitRunner runner = _ => "   \n";
        Assert.Null(ReleaseDetector.Parse(runner));
    }

    [Fact]
    public void Parse_RunnerThrows_Swallowed()
    {
        ReleaseDetector.GitRunner runner = _ => throw new InvalidOperationException("git not found");
        Assert.Null(ReleaseDetector.Parse(runner));
    }

    [Fact]
    public void Parse_NullRunner_ReturnsNull()
    {
        Assert.Null(ReleaseDetector.Parse(null));
    }

    // --- Resolution precedence via ApplyReleaseAutodetect ---

    [Fact]
    public void ExplicitRelease_AlwaysWins()
    {
        using var _ = new EnvScope(releaseEnv: "from-env");
        var opts = new AllStakOptions { Release = "explicit-v1", DetectReleaseFn = () => "detected-sha" };
        opts.ApplyReleaseAutodetect();
        Assert.Equal("explicit-v1", opts.Release);
    }

    [Fact]
    public void EnvVar_WinsOverDetection()
    {
        using var _ = new EnvScope(releaseEnv: "from-env");
        var opts = new AllStakOptions { DetectReleaseFn = () => "detected-sha" };
        opts.ApplyReleaseAutodetect();
        Assert.Equal("from-env", opts.Release);
    }

    [Fact]
    public void Detection_UsedWhenNoExplicitOrEnv()
    {
        using var _ = new EnvScope();
        var opts = new AllStakOptions { DetectReleaseFn = () => "abc1234-dirty" };
        opts.ApplyReleaseAutodetect();
        Assert.Equal("abc1234-dirty", opts.Release);
    }

    [Fact]
    public void FallsBackToSdkVersion_WhenDetectionEmpty()
    {
        using var _ = new EnvScope();
        var opts = new AllStakOptions { DetectReleaseFn = () => null };
        opts.ApplyReleaseAutodetect();
        Assert.Equal(AllStakOptions.SdkVersion, opts.Release);
    }

    [Fact]
    public void OptOut_DisablesDetectionAndFallback()
    {
        using var _ = new EnvScope();
        var opts = new AllStakOptions { AutoDetectRelease = false, DetectReleaseFn = () => "detected-sha" };
        opts.ApplyReleaseAutodetect();
        Assert.Null(opts.Release);
    }
}
