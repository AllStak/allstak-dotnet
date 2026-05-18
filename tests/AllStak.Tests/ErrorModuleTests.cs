using System.Net;
using System.Text.Json;
using AllStak;
using AllStak.Models;
using AllStak.Modules;
using AllStak.Transport;
using Microsoft.Extensions.Logging.Abstractions;

namespace AllStak.Tests;

/// <summary>
/// Tests for <see cref="ErrorModule"/>: exception capture payloads,
/// user context, breadcrumbs, and fail-open behavior.
/// </summary>
[Collection("Singleton")]
public sealed class ErrorModuleTests : IDisposable
{
    public void Dispose() => AllStakClient.Reset();

    // ── Helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// A handler that captures the JSON body of every request and responds with 202.
    /// </summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public List<string> Bodies { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content != null
                ? await request.Content.ReadAsStringAsync(ct)
                : "";
            Bodies.Add(body);
            return new HttpResponseMessage(HttpStatusCode.Accepted)
            {
                Content = new StringContent("""{"data":{"id":"evt_123"}}"""),
            };
        }
    }

    private static (ErrorModule module, CapturingHandler handler) CreateModule(Action<AllStakOptions>? configure = null)
    {
        var options = new AllStakOptions
        {
            ApiKey = "ask_test_errors",
            Host = "https://fake.allstak.test",
            Environment = "test",
            Release = "1.0.0-test",
            MaxRetries = 1,
        };
        configure?.Invoke(options);

        var handler = new CapturingHandler();
        var transport = new HttpTransport(options, NullLogger.Instance);

        // Swap internal HttpClient.
        var httpField = typeof(HttpTransport).GetField("_http",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-AllStak-Key", options.ApiKey);
        httpField.SetValue(transport, client);

        var module = new ErrorModule(transport, options, NullLogger.Instance);
        return (module, handler);
    }

    // ── Tests ────────────────────────────────────────────────────────

    [Fact]
    public async Task CaptureExceptionAsync_CreatesPayloadWithStackFrames()
    {
        var (module, handler) = CreateModule();

        Exception caught;
        try { throw new InvalidOperationException("test boom"); }
        catch (Exception ex) { caught = ex; }

        var eventId = await module.CaptureExceptionAsync(caught);

        Assert.Equal("evt_123", eventId);
        Assert.Single(handler.Bodies);

        using var doc = JsonDocument.Parse(handler.Bodies[0]);
        var root = doc.RootElement;

        Assert.Equal("InvalidOperationException", root.GetProperty("exceptionClass").GetString());
        Assert.Equal("test boom", root.GetProperty("message").GetString());
        Assert.Equal("error", root.GetProperty("level").GetString());
        Assert.Equal("test", root.GetProperty("environment").GetString());
        Assert.Equal("1.0.0-test", root.GetProperty("release").GetString());

        // Stack trace (string list) should be present.
        Assert.True(root.GetProperty("stackTrace").GetArrayLength() > 0);

        // Structured frames should also be present.
        Assert.True(root.GetProperty("frames").GetArrayLength() > 0);
    }

    [Fact]
    public async Task CaptureExceptionAsync_AttachesUserContext()
    {
        var (module, handler) = CreateModule();

        module.SetUser(id: "u_42", email: "alice@test.com", ip: "10.0.0.1");

        await module.CaptureExceptionAsync(new Exception("with user"));

        using var doc = JsonDocument.Parse(handler.Bodies[0]);
        var user = doc.RootElement.GetProperty("user");

        Assert.Equal("u_42", user.GetProperty("id").GetString());
        Assert.Equal("alice@test.com", user.GetProperty("email").GetString());
        Assert.Equal("10.0.0.1", user.GetProperty("ip").GetString());
    }

    [Fact]
    public async Task CaptureExceptionAsync_ExplicitUserOverridesDefault()
    {
        var (module, handler) = CreateModule();

        module.SetUser(id: "default_user");

        var explicitUser = new UserContext { Id = "explicit_user", Email = "b@test.com" };
        await module.CaptureExceptionAsync(new Exception("override"), user: explicitUser);

        using var doc = JsonDocument.Parse(handler.Bodies[0]);
        Assert.Equal("explicit_user", doc.RootElement.GetProperty("user").GetProperty("id").GetString());
    }

    [Fact]
    public async Task ClearUser_RemovesContext()
    {
        var (module, handler) = CreateModule();

        module.SetUser(id: "u_temp");
        module.ClearUser();

        await module.CaptureExceptionAsync(new Exception("no user"));

        using var doc = JsonDocument.Parse(handler.Bodies[0]);
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("user").ValueKind);
    }

    [Fact]
    public async Task Breadcrumbs_IncludedInPayload()
    {
        var (module, handler) = CreateModule();

        module.AddBreadcrumb("http", "GET /api/users", level: "info");
        module.AddBreadcrumb("ui", "clicked submit button", level: "info",
            data: new Dictionary<string, object?> { ["buttonId"] = "btn-submit" });

        await module.CaptureExceptionAsync(new Exception("with crumbs"));

        using var doc = JsonDocument.Parse(handler.Bodies[0]);
        var crumbs = doc.RootElement.GetProperty("breadcrumbs");

        Assert.Equal(2, crumbs.GetArrayLength());
        Assert.Equal("http", crumbs[0].GetProperty("type").GetString());
        Assert.Equal("GET /api/users", crumbs[0].GetProperty("message").GetString());
        Assert.Equal("ui", crumbs[1].GetProperty("type").GetString());
    }

    [Fact]
    public async Task Breadcrumbs_ClearedAfterCapture()
    {
        var (module, handler) = CreateModule();

        module.AddBreadcrumb("nav", "page load");
        await module.CaptureExceptionAsync(new Exception("first"));

        // Second capture should have no breadcrumbs.
        await module.CaptureExceptionAsync(new Exception("second"));

        using var doc = JsonDocument.Parse(handler.Bodies[1]);
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("breadcrumbs").ValueKind);
    }

    [Fact]
    public async Task Breadcrumbs_MaxFifty()
    {
        var (module, handler) = CreateModule();

        for (int i = 0; i < 55; i++)
            module.AddBreadcrumb("test", $"crumb {i}");

        await module.CaptureExceptionAsync(new Exception("many crumbs"));

        using var doc = JsonDocument.Parse(handler.Bodies[0]);
        var crumbs = doc.RootElement.GetProperty("breadcrumbs");

        // Oldest 5 should have been dropped, leaving 50.
        Assert.Equal(50, crumbs.GetArrayLength());
        // First breadcrumb should be crumb 5 (0-4 were dropped).
        Assert.Equal("crumb 5", crumbs[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task CaptureExceptionAsync_IncludesMetadata()
    {
        var (module, handler) = CreateModule();

        var meta = new Dictionary<string, object>
        {
            ["orderId"] = "ORD-42",
            ["region"] = "eu-west-1",
        };
        await module.CaptureExceptionAsync(new Exception("with meta"), metadata: meta);

        using var doc = JsonDocument.Parse(handler.Bodies[0]);
        var metadata = doc.RootElement.GetProperty("metadata");

        Assert.Equal("ORD-42", metadata.GetProperty("orderId").GetString());
        Assert.Equal("eu-west-1", metadata.GetProperty("region").GetString());
        // Release tags should also be merged in.
        Assert.Equal("allstak-dotnet", metadata.GetProperty("sdk.name").GetString());
    }

    [Fact]
    public async Task CaptureExceptionAsync_IncludesReleaseTrackingTags()
    {
        var (module, handler) = CreateModule(opts =>
        {
            opts.CommitSha = "abc123";
            opts.Branch = "main";
        });

        await module.CaptureExceptionAsync(new Exception("with tags"));

        using var doc = JsonDocument.Parse(handler.Bodies[0]);
        var metadata = doc.RootElement.GetProperty("metadata");

        Assert.Equal("abc123", metadata.GetProperty("commit.sha").GetString());
        Assert.Equal("main", metadata.GetProperty("commit.branch").GetString());
    }

    [Fact]
    public async Task CaptureExceptionAsync_NeverThrows()
    {
        // Even with a transport that always fails, CaptureExceptionAsync swallows.
        var options = new AllStakOptions
        {
            ApiKey = "ask_test_failopen",
            Host = "https://fake.allstak.test",
            MaxRetries = 1,
        };
        var failHandler = new FailHandler();
        var transport = new HttpTransport(options, NullLogger.Instance);
        var httpField = typeof(HttpTransport).GetField("_http",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var client = new HttpClient(failHandler) { Timeout = TimeSpan.FromMilliseconds(100) };
        httpField.SetValue(transport, client);

        var module = new ErrorModule(transport, options, NullLogger.Instance);

        // This must NOT throw.
        var result = await module.CaptureExceptionAsync(new Exception("should not throw"));
        Assert.Null(result);
    }

    [Fact]
    public async Task CaptureExceptionAsync_IncludesTraceId()
    {
        var (module, handler) = CreateModule();

        await module.CaptureExceptionAsync(new Exception("traced"), traceId: "trace-abc-123");

        using var doc = JsonDocument.Parse(handler.Bodies[0]);
        Assert.Equal("trace-abc-123", doc.RootElement.GetProperty("traceId").GetString());
    }

    [Fact]
    public async Task CaptureExceptionAsync_CustomLevel()
    {
        var (module, handler) = CreateModule();

        await module.CaptureExceptionAsync(new Exception("warning"), level: "warning");

        using var doc = JsonDocument.Parse(handler.Bodies[0]);
        Assert.Equal("warning", doc.RootElement.GetProperty("level").GetString());
    }

    // ── Helpers ───────────────────────────────────────────────────────

    private sealed class FailHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            throw new HttpRequestException("connection refused");
        }
    }
}
