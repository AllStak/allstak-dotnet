using System.Diagnostics;

namespace AllStak.Integrations.HttpClient;

/// <summary>
/// Delegating handler that captures outbound HttpClient calls — method, host, path,
/// status, duration, request/response size — and records them as <c>outbound</c>
/// telemetry in AllStak.
///
/// Usage:
/// <code>
/// builder.Services.AddHttpClient("default")
///     .AddHttpMessageHandler&lt;AllStakHttpHandler&gt;();
/// builder.Services.AddTransient&lt;AllStakHttpHandler&gt;();
/// </code>
/// </summary>
public sealed class AllStakHttpHandler : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (!AllStakClient.IsInitialized)
            return await base.SendAsync(request, cancellationToken);

        var client = AllStakClient.Instance;
        var sw = Stopwatch.StartNew();
        string host = request.RequestUri?.Host ?? "";
        string path = request.RequestUri?.AbsolutePath ?? "/";
        int status = 0;
        long responseSize = 0;
        long requestSize = 0;
        string? errorFp = null;
        var traceId = client.Tracing.GetTraceId();
        var spanId = client.Tracing.CurrentSpanId;
        var requestId = Guid.NewGuid().ToString("N");

        global::AllStak.TraceHeaders.Apply(request.Headers, traceId, requestId, spanId);

        try
        {
            requestSize = request.Content?.Headers?.ContentLength ?? 0;
            var resp = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            sw.Stop();
            status = (int)resp.StatusCode;
            responseSize = resp.Content?.Headers?.ContentLength ?? 0;

            client.Http.Record(
                direction: "outbound",
                method: request.Method.Method,
                host: host,
                path: path,
                statusCode: status,
                durationMs: sw.ElapsedMilliseconds,
                requestSize: requestSize,
                responseSize: responseSize,
                traceId: traceId,
                requestId: requestId,
                spanId: spanId,
                errorFingerprint: errorFp);
            return resp;
        }
        catch (Exception ex)
        {
            sw.Stop();
            errorFp = ex.GetType().Name;
            try
            {
                client.Http.Record(
                    direction: "outbound",
                    method: request.Method.Method,
                    host: host,
                    path: path,
                    statusCode: status,
                    durationMs: sw.ElapsedMilliseconds,
                    requestSize: requestSize,
                    responseSize: responseSize,
                    traceId: traceId,
                    requestId: requestId,
                    spanId: spanId,
                    errorFingerprint: errorFp);
            }
            catch { }
            throw;
        }
    }
}
