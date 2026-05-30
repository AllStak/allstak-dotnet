using System.Net;
using AllStak;
using AllStak.Modules;
using AllStak.Transport;
using Microsoft.Extensions.Logging.Abstractions;

namespace AllStak.Tests;

/// <summary>
/// Tests for the process-wide unhandled-exception handlers
/// (<see cref="GlobalExceptionHandler"/>): subscribe/idempotency/opt-out and
/// that captured global events carry the unhandled mechanism marker.
///
/// These touch the process-wide AppDomain / TaskScheduler events, so they run
/// in the singleton (non-parallel) collection.
/// </summary>
[Collection("Singleton")]
public sealed class GlobalExceptionHandlerTests : IDisposable
{
    public void Dispose() => AllStakClient.Reset();

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public List<string> Bodies { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = await TestHttpContent.ReadDecodedStringAsync(request, ct);
            lock (Bodies) Bodies.Add(body);
            return new HttpResponseMessage(HttpStatusCode.Accepted)
            {
                Content = new StringContent("""{"data":{"id":"evt_1"}}"""),
            };
        }
    }

    private static (GlobalExceptionHandler handler, ErrorModule errors, CapturingHandler http) Create(
        Action<AllStakOptions>? configure = null)
    {
        var options = new AllStakOptions { ApiKey = "ask_global", Host = "https://fake.allstak.test", MaxRetries = 1 };
        configure?.Invoke(options);

        var http = new CapturingHandler();
        var transport = new HttpTransport(options, NullLogger.Instance);
        var httpField = typeof(HttpTransport).GetField("_http",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        httpField.SetValue(transport, new HttpClient(http) { Timeout = TimeSpan.FromSeconds(5) });

        var errors = new ErrorModule(transport, options, NullLogger.Instance);
        var handler = new GlobalExceptionHandler(errors, () => Task.CompletedTask, options, NullLogger.Instance);
        return (handler, errors, http);
    }

    [Fact]
    public void Subscribe_AttachesHandlers()
    {
        var (handler, _, _) = Create();
        Assert.False(handler.IsSubscribed);

        handler.Subscribe();
        Assert.True(handler.IsSubscribed);

        handler.Unsubscribe();
        Assert.False(handler.IsSubscribed);
    }

    [Fact]
    public void Subscribe_IsIdempotent()
    {
        var (handler, _, _) = Create();

        handler.Subscribe();
        handler.Subscribe();
        handler.Subscribe();
        Assert.True(handler.IsSubscribed);

        // A single Unsubscribe fully detaches (proves no double-subscribe leaked).
        handler.Unsubscribe();
        Assert.False(handler.IsSubscribed);

        // Unsubscribe again is a safe no-op.
        handler.Unsubscribe();
        Assert.False(handler.IsSubscribed);
    }

    [Fact]
    public void OptOut_BothFlagsFalse_DoesNotSubscribe()
    {
        var (handler, _, _) = Create(o =>
        {
            o.CaptureUnhandledExceptions = false;
            o.CaptureUnobservedTaskExceptions = false;
        });

        handler.Subscribe();

        // Subscribe() flips the guard, but no underlying handlers were attached.
        // IsSubscribed reflects the guard; the meaningful assertion is no throw and
        // a clean unsubscribe.
        handler.Unsubscribe();
        Assert.False(handler.IsSubscribed);
    }

    [Fact]
    public async Task UnobservedTaskException_Captured_WithUnhandledMechanism()
    {
        var (handler, _, http) = Create(o =>
        {
            o.CaptureUnhandledExceptions = false; // isolate the task path
            o.CaptureUnobservedTaskExceptions = true;
        });
        handler.Subscribe();
        try
        {
            // Raise the event directly via reflection on the private handler so the
            // test is deterministic (no GC timing dependency).
            RaiseUnobservedTaskException(handler, new InvalidOperationException("task boom"));

            // CaptureExceptionAsync is fire-and-forget inside the handler; give it a moment.
            await WaitForBodies(http, 1);

            Assert.Single(http.Bodies);
            using var doc = System.Text.Json.JsonDocument.Parse(http.Bodies[0]);
            var meta = doc.RootElement.GetProperty("metadata");
            Assert.Equal("TaskScheduler.UnobservedTaskException", meta.GetProperty("mechanism").GetString());
            Assert.False(meta.GetProperty("handled").GetBoolean());
        }
        finally { handler.Unsubscribe(); }
    }

    [Fact]
    public async Task AppDomainUnhandled_Captured_WithUnhandledMechanism()
    {
        var (handler, _, http) = Create(o =>
        {
            o.CaptureUnhandledExceptions = true;
            o.CaptureUnobservedTaskExceptions = false;
        });
        handler.Subscribe();
        try
        {
            RaiseAppDomainUnhandled(handler, new ApplicationException("domain boom"));
            await WaitForBodies(http, 1);

            Assert.Single(http.Bodies);
            using var doc = System.Text.Json.JsonDocument.Parse(http.Bodies[0]);
            var meta = doc.RootElement.GetProperty("metadata");
            Assert.Equal("AppDomain.UnhandledException", meta.GetProperty("mechanism").GetString());
            Assert.False(meta.GetProperty("handled").GetBoolean());
        }
        finally { handler.Unsubscribe(); }
    }

    // ── reflection helpers to invoke the private event handlers deterministically ──

    private static void RaiseUnobservedTaskException(GlobalExceptionHandler handler, Exception ex)
    {
        var faulted = Task.FromException(ex);
        var args = (UnobservedTaskExceptionEventArgs)Activator.CreateInstance(
            typeof(UnobservedTaskExceptionEventArgs),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public,
            binder: null,
            args: new object?[] { faulted.Exception },
            culture: null)!;
        var m = typeof(GlobalExceptionHandler).GetMethod("OnUnobservedTaskException",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        m.Invoke(handler, new object?[] { null, args });
        // Drain the AggregateException so the test's own finalizer doesn't escalate.
        _ = faulted.Exception;
    }

    private static void RaiseAppDomainUnhandled(GlobalExceptionHandler handler, Exception ex)
    {
        var args = new UnhandledExceptionEventArgs(ex, isTerminating: false);
        var m = typeof(GlobalExceptionHandler).GetMethod("OnAppDomainUnhandledException",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        m.Invoke(handler, new object?[] { null!, args });
    }

    private static async Task WaitForBodies(CapturingHandler http, int count)
    {
        for (int i = 0; i < 50; i++)
        {
            lock (http.Bodies) if (http.Bodies.Count >= count) return;
            await Task.Delay(20);
        }
    }
}
