using AllStak;

namespace AllStak.Tests;

/// <summary>
/// Tests for <see cref="AllStakClient"/> lifecycle: initialization,
/// singleton behavior, reset, and fail-open on bad config.
/// </summary>
[Collection("Singleton")]
public sealed class AllStakClientTests : IDisposable
{
    public AllStakClientTests()
    {
        // Ensure a clean singleton state before every test.
        AllStakClient.Reset();
    }

    public void Dispose()
    {
        AllStakClient.Reset();
    }

    [Fact]
    public void Initialize_CreatesClient()
    {
        var client = AllStakClient.Initialize(o =>
        {
            o.ApiKey = "ask_test_1234";
            o.Environment = "test";
        });

        Assert.NotNull(client);
        Assert.True(AllStakClient.IsInitialized);
        Assert.Same(client, AllStakClient.Instance);
    }

    [Fact]
    public void Initialize_CalledTwice_ReturnsSameInstance()
    {
        var first = AllStakClient.Initialize(o => o.ApiKey = "ask_test_first");
        var second = AllStakClient.Initialize(o => o.ApiKey = "ask_test_second");

        Assert.Same(first, second);
    }

    [Fact]
    public void Instance_BeforeInit_Throws()
    {
        Assert.False(AllStakClient.IsInitialized);
        Assert.Throws<InvalidOperationException>(() => AllStakClient.Instance);
    }

    [Fact]
    public void Reset_ThenInit_CreatesNewInstance()
    {
        var first = AllStakClient.Initialize(o => o.ApiKey = "ask_test_a");
        AllStakClient.Reset();

        Assert.False(AllStakClient.IsInitialized);

        var second = AllStakClient.Initialize(o => o.ApiKey = "ask_test_b");
        Assert.NotSame(first, second);
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var client = AllStakClient.Initialize(o => o.ApiKey = "ask_test_dispose");

        // Calling Dispose multiple times must not throw.
        client.Dispose();
        client.Dispose();
        client.Dispose();
    }

    [Fact]
    public void Initialize_WithMissingApiKey_Throws()
    {
        // ApiKey is required — empty string should fail.
        Assert.Throws<ArgumentException>(() =>
            AllStakClient.Initialize(o => { /* ApiKey left empty */ }));
    }

    [Fact]
    public void Modules_AreExposed()
    {
        var client = AllStakClient.Initialize(o =>
        {
            o.ApiKey = "ask_test_modules";
        });

        Assert.NotNull(client.Errors);
        Assert.NotNull(client.Logs);
        Assert.NotNull(client.Http);
        Assert.NotNull(client.Tracing);
        Assert.NotNull(client.Database);
        Assert.NotNull(client.Cron);
    }

    [Fact]
    public async Task CaptureExceptionAsync_WhenNotInitialized_ReturnsNull()
    {
        // Static convenience should not throw when SDK isn't initialized.
        var result = await AllStakClient.CaptureExceptionAsync(new Exception("test"));
        Assert.Null(result);
    }

    [Fact]
    public void SetUser_WhenNotInitialized_DoesNotThrow()
    {
        // Fail-open: no-op when SDK hasn't been initialized.
        AllStakClient.SetUser(id: "u1", email: "a@b.com");
        AllStakClient.ClearUser();
    }

    [Fact]
    public void EnableAutoSessionTracking_False_WiresNoSession()
    {
        var client = AllStakClient.Initialize(o =>
        {
            o.ApiKey = "ask_test_no_session";
            o.EnableAutoSessionTracking = false;
        });

        // Opt-out: the error module must have no session tracker attached, so no
        // session id is stamped and no /sessions/start is ever posted.
        Assert.Null(client.Errors.Session);
    }

    [Fact]
    public void DefaultRuntime_UnderTestHost_SkipsSessionTracking()
    {
        // EnableAutoSessionTracking defaults to true, but the SDK auto-skips
        // session tracking under a unit-test host (testhost / vstest / xunit)
        // exactly as the runtime-release registration does — so no session is
        // wired when running inside the test runner.
        var client = AllStakClient.Initialize(o => o.ApiKey = "ask_test_default_session");

        Assert.Null(client.Errors.Session);
    }
}
