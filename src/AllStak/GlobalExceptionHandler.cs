using AllStak.Modules;
using Microsoft.Extensions.Logging;

namespace AllStak;

/// <summary>
/// Process-wide capture of crashes that occur outside any ASP.NET request:
/// background threads, workers, fire-and-forget tasks, console apps. Subscribes
/// to <see cref="AppDomain.UnhandledException"/> and
/// <see cref="System.Threading.Tasks.TaskScheduler.UnobservedTaskException"/>.
///
/// <para>Subscription is idempotent and guarded so a double-init does not attach
/// two handlers; <see cref="Unsubscribe"/> detaches on shutdown / dispose.</para>
///
/// <para>On <see cref="AppDomain.UnhandledException"/> the process is usually
/// terminating, so we capture, perform a best-effort <i>synchronous</i> flush
/// with a short timeout, and then let normal termination proceed — we never
/// swallow the exception.</para>
/// </summary>
internal sealed class GlobalExceptionHandler
{
    private readonly ErrorModule _errors;
    private readonly Func<Task> _flushAll;
    private readonly AllStakOptions _options;
    private readonly ILogger _logger;

    private UnhandledExceptionEventHandler? _appDomainHandler;
    private EventHandler<UnobservedTaskExceptionEventArgs>? _taskHandler;
    private readonly object _lock = new();
    private bool _subscribed;

    /// <summary>Test/diagnostic seam: whether handlers are currently attached.</summary>
    internal bool IsSubscribed { get { lock (_lock) { return _subscribed; } } }

    internal GlobalExceptionHandler(ErrorModule errors, Func<Task> flushAll, AllStakOptions options, ILogger logger)
    {
        _errors = errors;
        _flushAll = flushAll;
        _options = options;
        _logger = logger;
    }

    /// <summary>Idempotently attach the configured global handlers.</summary>
    internal void Subscribe()
    {
        lock (_lock)
        {
            if (_subscribed) return;

            if (_options.CaptureUnhandledExceptions)
            {
                _appDomainHandler = OnAppDomainUnhandledException;
                AppDomain.CurrentDomain.UnhandledException += _appDomainHandler;
            }

            if (_options.CaptureUnobservedTaskExceptions)
            {
                _taskHandler = OnUnobservedTaskException;
                System.Threading.Tasks.TaskScheduler.UnobservedTaskException += _taskHandler;
            }

            _subscribed = true;
        }
    }

    /// <summary>Idempotently detach any handlers attached by <see cref="Subscribe"/>.</summary>
    internal void Unsubscribe()
    {
        lock (_lock)
        {
            if (!_subscribed) return;

            if (_appDomainHandler != null)
            {
                AppDomain.CurrentDomain.UnhandledException -= _appDomainHandler;
                _appDomainHandler = null;
            }
            if (_taskHandler != null)
            {
                System.Threading.Tasks.TaskScheduler.UnobservedTaskException -= _taskHandler;
                _taskHandler = null;
            }

            _subscribed = false;
        }
    }

    private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        // The runtime hands us object; it is virtually always an Exception, but
        // guard for the rare non-CLS-compliant throw.
        if (e.ExceptionObject is not Exception ex)
        {
            _logger.LogWarning("[AllStak] AppDomain.UnhandledException with non-Exception object; skipping capture");
            return;
        }

        try
        {
            // The process is terminating — capture synchronously and flush with a
            // short timeout so we don't hang shutdown. We do NOT swallow the
            // exception: normal termination proceeds after this handler returns.
            _ = _errors.CaptureExceptionAsync(ex, metadata: UnhandledMetadata("AppDomain.UnhandledException"));
            BestEffortFlush();
        }
        catch (Exception inner)
        {
            _logger.LogDebug(inner, "[AllStak] global unhandled-exception capture failed");
        }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        try
        {
            _ = _errors.CaptureExceptionAsync(e.Exception, metadata: UnhandledMetadata("TaskScheduler.UnobservedTaskException"));

            // Default: do NOT mark observed, to preserve the runtime's existing
            // escalation behavior. Opt-in via config.
            if (_options.MarkUnobservedTaskExceptionsObserved)
                e.SetObserved();
        }
        catch (Exception inner)
        {
            _logger.LogDebug(inner, "[AllStak] unobserved-task-exception capture failed");
        }
    }

    /// <summary>
    /// Mechanism marker for events captured by a global handler — consistent with
    /// the SDK convention of carrying <c>mechanism</c> / <c>handled</c> in metadata.
    /// These events are unhandled (<c>handled=false</c>).
    /// </summary>
    private static Dictionary<string, object> UnhandledMetadata(string mechanism) => new()
    {
        ["mechanism"] = mechanism,
        ["handled"] = false,
    };

    private void BestEffortFlush()
    {
        try
        {
            var timeout = TimeSpan.FromMilliseconds(Math.Max(0, _options.ShutdownFlushTimeoutMs));
            // Synchronous wait with a hard cap — the process may be tearing down.
            _flushAll().Wait(timeout);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[AllStak] best-effort flush on unhandled exception failed");
        }
    }
}
