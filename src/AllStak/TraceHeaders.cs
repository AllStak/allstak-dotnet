using Microsoft.AspNetCore.Http;
using System.Net.Http.Headers;

namespace AllStak;

internal sealed record TraceHeaders(string TraceId, string ParentSpanId, string RequestId)
{
    public static TraceHeaders From(HttpRequest request)
    {
        var traceparent = request.Headers["traceparent"].ToString();
        var traceId = TraceIdFromTraceparent(traceparent)
                      ?? FirstValidTraceHeader(request, "X-AllStak-Trace-Id", "X-Trace-Id")
                      ?? RandomTraceId();
        var parentSpanId = ParentSpanIdFromTraceparent(traceparent)
                           ?? FirstValidSpanHeader(request, "X-AllStak-Span-Id", "X-Span-Id")
                           ?? "";
        var requestId = FirstHeader(request, "X-Request-Id", "X-AllStak-Request-Id")
                        ?? RandomTraceId();
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
        if (parts.Length != 4 || parts[0] != "00" || parts[3].Length != 2) return null;
        var traceId = parts[1].ToLowerInvariant();
        return IsValidTraceId(traceId) && IsHex(parts[3]) ? traceId : null;
    }

    private static string? ParentSpanIdFromTraceparent(string? value)
    {
        var parts = (value ?? "").Split('-');
        if (parts.Length != 4 || parts[0] != "00" || parts[3].Length != 2) return null;
        var spanId = parts[2].ToLowerInvariant();
        return IsValidSpanId(spanId) && IsHex(parts[3]) ? spanId : null;
    }

    private static string? FirstValidTraceHeader(HttpRequest request, params string[] names)
    {
        foreach (var name in names)
        {
            var value = request.Headers[name].ToString().Trim().ToLowerInvariant();
            if (IsValidTraceId(value)) return value;
        }
        return null;
    }

    private static string? FirstValidSpanHeader(HttpRequest request, params string[] names)
    {
        foreach (var name in names)
        {
            var value = request.Headers[name].ToString().Trim().ToLowerInvariant();
            if (IsValidSpanId(value)) return value;
        }
        return null;
    }

    internal static string RandomTraceId() => Guid.NewGuid().ToString("N").ToLowerInvariant();

    internal static string RandomSpanId() => Guid.NewGuid().ToString("N")[..16].ToLowerInvariant();

    internal static string NormalizeTraceId(string? traceId)
    {
        var hex = new string((traceId ?? "").Where(Uri.IsHexDigit).Select(char.ToLowerInvariant).ToArray());
        var candidate = hex.Length >= 32
            ? hex[..32]
            : hex.Length > 0
                ? hex.PadRight(32, '0')
                : "";
        return IsValidTraceId(candidate) ? candidate : RandomTraceId();
    }

    internal static string? TryNormalizeTraceId(string? traceId)
    {
        var candidate = (traceId ?? "").Replace("-", "").Trim().ToLowerInvariant();
        return IsValidTraceId(candidate) ? candidate : null;
    }

    internal static string NormalizeSpanId(string? spanId)
    {
        var hex = new string((spanId ?? "").Where(Uri.IsHexDigit).Select(char.ToLowerInvariant).ToArray());
        var candidate = hex.Length >= 16
            ? hex[..16]
            : hex.Length > 0
                ? hex.PadRight(16, '0')
                : "";
        return IsValidSpanId(candidate) ? candidate : RandomSpanId();
    }

    internal static string? TryNormalizeSpanId(string? spanId)
    {
        var candidate = (spanId ?? "").Replace("-", "").Trim().ToLowerInvariant();
        return IsValidSpanId(candidate) ? candidate : null;
    }

    private static bool IsValidTraceId(string? traceId) =>
        !string.IsNullOrWhiteSpace(traceId)
        && traceId.Length == 32
        && IsHex(traceId)
        && traceId.Any(c => c != '0');

    private static bool IsValidSpanId(string? spanId) =>
        !string.IsNullOrWhiteSpace(spanId)
        && spanId.Length == 16
        && IsHex(spanId)
        && spanId.Any(c => c != '0');

    private static bool IsHex(string value) => value.All(Uri.IsHexDigit);

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
        var wireTraceId = NormalizeTraceId(traceId);
        var wireSpanId = string.IsNullOrWhiteSpace(spanId) ? null : NormalizeSpanId(spanId);
        headers["X-AllStak-Trace-Id"] = wireTraceId;
        if (!string.IsNullOrWhiteSpace(requestId)) headers["X-AllStak-Request-Id"] = requestId;
        if (!string.IsNullOrWhiteSpace(wireSpanId))
        {
            headers["X-AllStak-Span-Id"] = wireSpanId;
            headers["traceparent"] = $"00-{wireTraceId}-{wireSpanId}-{TraceFlags(sampled)}";
        }
        headers["baggage"] = MergeBaggage(headers["baggage"].ToString(), wireTraceId, requestId, wireSpanId);
        headers["AllStak-Baggage"] = Baggage(wireTraceId, requestId, wireSpanId);
    }

    public static void Apply(HttpRequestHeaders headers, string traceId, string? requestId, string? spanId, bool sampled = true)
    {
        var wireTraceId = NormalizeTraceId(traceId);
        var wireSpanId = string.IsNullOrWhiteSpace(spanId) ? null : NormalizeSpanId(spanId);
        Set(headers, "X-AllStak-Trace-Id", wireTraceId);
        if (!string.IsNullOrWhiteSpace(requestId)) Set(headers, "X-AllStak-Request-Id", requestId);
        if (!string.IsNullOrWhiteSpace(wireSpanId))
        {
            Set(headers, "X-AllStak-Span-Id", wireSpanId);
            Set(headers, "traceparent", $"00-{wireTraceId}-{wireSpanId}-{TraceFlags(sampled)}");
        }
        var existing = headers.TryGetValues("baggage", out var values) ? string.Join(",", values) : null;
        Set(headers, "baggage", MergeBaggage(existing, wireTraceId, requestId, wireSpanId));
        Set(headers, "AllStak-Baggage", Baggage(wireTraceId, requestId, wireSpanId));
    }

    private static void Set(HttpRequestHeaders headers, string name, string value)
    {
        headers.Remove(name);
        headers.TryAddWithoutValidation(name, value);
    }
}
