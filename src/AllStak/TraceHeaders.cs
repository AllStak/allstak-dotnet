using Microsoft.AspNetCore.Http;
using System.Net.Http.Headers;

namespace AllStak;

internal sealed record TraceHeaders(string TraceId, string ParentSpanId, string RequestId)
{
    public static TraceHeaders From(HttpRequest request)
    {
        var traceparent = request.Headers["traceparent"].ToString();
        var traceId = TraceIdFromTraceparent(traceparent)
                      ?? FirstHeader(request, "X-AllStak-Trace-Id", "X-Trace-Id")
                      ?? Guid.NewGuid().ToString("N");
        var parentSpanId = ParentSpanIdFromTraceparent(traceparent)
                           ?? FirstHeader(request, "X-AllStak-Span-Id", "X-Span-Id")
                           ?? "";
        var requestId = FirstHeader(request, "X-Request-Id", "X-AllStak-Request-Id")
                        ?? Guid.NewGuid().ToString("N");
        return new TraceHeaders(traceId, parentSpanId, requestId);
    }

    private static string? FirstHeader(HttpRequest request, params string[] names)
    {
        foreach (var name in names)
        {
            var value = request.Headers[name].ToString();
            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
        }
        return null;
    }

    private static string? TraceIdFromTraceparent(string? value)
    {
        var parts = (value ?? "").Split('-');
        return parts.Length >= 2 && parts[1].Length == 32 ? parts[1] : null;
    }

    private static string? ParentSpanIdFromTraceparent(string? value)
    {
        var parts = (value ?? "").Split('-');
        return parts.Length >= 3 && parts[2].Length == 16 ? parts[2] : null;
    }

    public static string Baggage(string traceId, string? requestId, string? spanId)
    {
        var parts = new List<string> { $"allstak-trace_id={traceId}" };
        if (!string.IsNullOrWhiteSpace(requestId)) parts.Add($"allstak-request_id={requestId}");
        if (!string.IsNullOrWhiteSpace(spanId)) parts.Add($"allstak-span_id={spanId}");
        return string.Join(",", parts);
    }

    public static string MergeBaggage(string? existing, string traceId, string? requestId, string? spanId)
    {
        var parts = (existing ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(part => !part.StartsWith("allstak-", StringComparison.OrdinalIgnoreCase))
            .ToList();
        parts.AddRange(Baggage(traceId, requestId, spanId).Split(','));
        return string.Join(",", parts);
    }

    /// <summary>W3C traceparent trace-flags: <c>01</c> = sampled, <c>00</c> = not sampled.</summary>
    private static string TraceFlags(bool sampled) => sampled ? "01" : "00";

    public static void Apply(IHeaderDictionary headers, string traceId, string? requestId, string? spanId, bool sampled = true)
    {
        headers["X-AllStak-Trace-Id"] = traceId;
        if (!string.IsNullOrWhiteSpace(requestId)) headers["X-AllStak-Request-Id"] = requestId;
        if (!string.IsNullOrWhiteSpace(spanId))
        {
            headers["X-AllStak-Span-Id"] = spanId;
            headers["traceparent"] = $"00-{traceId}-{spanId[..Math.Min(16, spanId.Length)]}-{TraceFlags(sampled)}";
        }
        headers["baggage"] = MergeBaggage(headers["baggage"].ToString(), traceId, requestId, spanId);
        headers["AllStak-Baggage"] = Baggage(traceId, requestId, spanId);
    }

    public static void Apply(HttpRequestHeaders headers, string traceId, string? requestId, string? spanId, bool sampled = true)
    {
        Set(headers, "X-AllStak-Trace-Id", traceId);
        if (!string.IsNullOrWhiteSpace(requestId)) Set(headers, "X-AllStak-Request-Id", requestId);
        if (!string.IsNullOrWhiteSpace(spanId))
        {
            Set(headers, "X-AllStak-Span-Id", spanId);
            Set(headers, "traceparent", $"00-{traceId}-{spanId[..Math.Min(16, spanId.Length)]}-{TraceFlags(sampled)}");
        }
        var existing = headers.TryGetValues("baggage", out var values) ? string.Join(",", values) : null;
        Set(headers, "baggage", MergeBaggage(existing, traceId, requestId, spanId));
        Set(headers, "AllStak-Baggage", Baggage(traceId, requestId, spanId));
    }

    private static void Set(HttpRequestHeaders headers, string name, string value)
    {
        headers.Remove(name);
        headers.TryAddWithoutValidation(name, value);
    }
}
