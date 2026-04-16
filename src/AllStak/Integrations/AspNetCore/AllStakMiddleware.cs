using System.Diagnostics;
using AllStak.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace AllStak.Integrations.AspNetCore;

/// <summary>
/// ASP.NET Core middleware that:
/// 1. Starts a fresh trace per request (or adopts <c>traceparent</c> / <c>X-AllStak-Trace-Id</c> if present)
/// 2. Captures inbound HTTP request telemetry (method, path, host, status, duration, sizes)
/// 3. Auto-captures unhandled exceptions with full request context, user, stack, and trace link
/// 4. Rethrows so the framework's own exception handler runs.
/// </summary>
public sealed class AllStakMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<AllStakMiddleware> _logger;

    public AllStakMiddleware(RequestDelegate next, ILogger<AllStakMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!AllStakClient.IsInitialized)
        {
            await _next(context);
            return;
        }

        var client = AllStakClient.Instance;
        var options = client.Options;
        var sw = Stopwatch.StartNew();
        var startedAt = DateTime.UtcNow;

        // Trace ID — adopt incoming or mint fresh
        string incomingTrace = context.Request.Headers["X-AllStak-Trace-Id"].ToString();
        if (string.IsNullOrEmpty(incomingTrace))
            incomingTrace = context.Request.Headers["traceparent"].ToString();

        if (!string.IsNullOrEmpty(incomingTrace))
            client.Tracing.SetTraceId(incomingTrace);
        else
            client.Tracing.ResetTrace();

        var traceId = client.Tracing.GetTraceId();
        context.Response.Headers["X-AllStak-Trace-Id"] = traceId;

        Exception? captured = null;
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            captured = ex;
            throw;
        }
        finally
        {
            sw.Stop();

            // Request telemetry
            if (options.CaptureHttpRequests)
            {
                try
                {
                    var userId = ExtractUserId(context);
                    long reqSize = context.Request.ContentLength ?? 0;
                    long respSize = context.Response.ContentLength ?? 0;

                    client.Http.Record(
                        direction: "inbound",
                        method: context.Request.Method,
                        host: context.Request.Host.HasValue ? context.Request.Host.Value : "localhost",
                        path: context.Request.Path.HasValue ? context.Request.Path.Value! : "/",
                        statusCode: context.Response.StatusCode,
                        durationMs: sw.ElapsedMilliseconds,
                        requestSize: reqSize,
                        responseSize: respSize,
                        traceId: traceId,
                        userId: userId);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[AllStak] middleware request capture failed");
                }
            }

            // Exception capture
            if (captured != null && options.CaptureUnhandledExceptions)
            {
                try
                {
                    UserContext? userCtx = options.CaptureUserContext ? BuildUserContext(context) : null;

                    var reqCtx = new RequestContext
                    {
                        Method = context.Request.Method,
                        Path = context.Request.Path.HasValue ? context.Request.Path.Value : "/",
                        Host = context.Request.Host.HasValue ? context.Request.Host.Value : "localhost",
                        StatusCode = context.Response.StatusCode == 200 ? 500 : context.Response.StatusCode,
                        UserAgent = context.Request.Headers["User-Agent"].ToString(),
                    };

                    var meta = new Dictionary<string, object>
                    {
                        ["http.method"] = context.Request.Method,
                        ["http.path"] = context.Request.Path.HasValue ? context.Request.Path.Value! : "/",
                        ["http.host"] = context.Request.Host.HasValue ? context.Request.Host.Value : "localhost",
                        ["http.status"] = reqCtx.StatusCode!,
                        ["traceId"] = traceId,
                    };

                    _ = client.Errors.CaptureExceptionAsync(
                        captured,
                        user: userCtx,
                        request: reqCtx,
                        traceId: traceId,
                        metadata: meta);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[AllStak] middleware exception capture failed");
                }
            }
        }
    }

    private static string? ExtractUserId(HttpContext ctx)
    {
        var user = ctx.User;
        if (user?.Identity?.IsAuthenticated != true) return null;
        return user.FindFirst("sub")?.Value
               ?? user.FindFirst("id")?.Value
               ?? user.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
               ?? user.Identity?.Name;
    }

    private static UserContext? BuildUserContext(HttpContext ctx)
    {
        var user = ctx.User;
        if (user?.Identity?.IsAuthenticated != true) return null;
        var id = ExtractUserId(ctx);
        var email = user.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value
                    ?? user.FindFirst("email")?.Value;
        var ip = ctx.Connection?.RemoteIpAddress?.ToString();
        return new UserContext { Id = id, Email = email, Ip = ip };
    }
}
