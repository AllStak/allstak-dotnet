using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AllStak
{
    /// <summary>
    /// AllStak .NET SDK sanitizer.
    /// Recursively scrubs sensitive data across the event surface
    /// (user, extras, metadata, breadcrumbs.data, contexts, request, response).
    /// Conforms to the canonical AllStak SDK denylist
    /// (docs/standards/sdk-platform-standards.md).
    ///
    /// Two layers of redaction compose here:
    /// 1. KEY-NAME redaction (always on): case-insensitive substring match on
    ///    keys (password / token / cookie / …) → value replaced with the
    ///    sentinel "[REDACTED]".
    /// 2. VALUE-PATTERN redaction (Sentry data-scrubbing parity): high-risk
    ///    financial/identity patterns that leak into free-text string VALUES are
    ///    matched and replaced even when the key looks innocent.
    ///    - ALWAYS scrubbed (never legitimately wanted in telemetry):
    ///        • Credit-card numbers (13-19 digits, separators allowed) that pass
    ///          the Luhn checksum — Luhn-failing runs are preserved so order ids
    ///          / timestamps are not corrupted.
    ///        • US SSN in dashed <c>ddd-dd-dddd</c> form (bare 9-digit numbers are
    ///          NOT matched, to avoid nuking arbitrary numeric ids).
    ///    - Scrubbed UNLESS <c>sendDefaultPii</c> is true (default false = Sentry
    ///      parity):
    ///        • Email addresses.
    ///        • IPv4 addresses (octets validated 0-255).
    ///
    /// Semantics:
    /// - Case-insensitive substring match on keys.
    /// - Value replacement with the sentinel string "[REDACTED]" (key preserved).
    /// - Recursion into IDictionary and IEnumerable; primitives pass through.
    /// - Cycle protection via reference-equality HashSet using RuntimeHelpers.GetHashCode.
    /// - Pure: returns a sanitized copy; never mutates caller-owned structures.
    /// - Fail-open: a value-scrubber error never throws — the unscrubbed-but-
    ///   key-redacted value is returned instead so an event is never dropped.
    /// </summary>
    public static class Sanitizer
    {
        public const string Redacted = "[REDACTED]";

        /// <summary>
        /// Strings longer than this are not scanned by the value-pattern scrubbers
        /// (key-name redaction still applies). Bounds the regex cost on the wire path
        /// — multi-megabyte blobs are not credit-card carriers and scanning them
        /// would risk pathological backtracking / latency.
        /// </summary>
        private const int MaxValueScanLength = 32 * 1024;

        public static readonly IReadOnlyList<string> DefaultDenylist = new[]
        {
            "authorization",
            "proxy-authorization",
            "cookie",
            "set-cookie",
            "password",
            "passwd",
            "pwd",
            "api_key",
            "apikey",
            "x-api-key",
            "x-allstak-key",
            "x-auth-token",
            "x-access-token",
            "token",
            "bearer",
            "jwt",
            "session",
            "secret",
            "credit_card",
            "card_number",
            "cvv",
            "ssn",
            "csrf",
        };

        /// <summary>
        /// Exact keys that are non-sensitive correlation identifiers and must never
        /// be redacted, even though they contain a denylist substring. The
        /// release-health session id is an opaque random correlation id (not a
        /// credential); redacting it would break server-side session correlation
        /// and crash-free tracking. Genuine session secrets (e.g.
        /// <c>session_token</c>, <c>session_cookie</c>) are NOT allowlisted and
        /// stay redacted via their <c>token</c> / <c>cookie</c> substrings.
        /// Matched case-insensitively on the exact (full) key.
        /// </summary>
        private static readonly HashSet<string> AllowlistExactKeys =
            new(StringComparer.OrdinalIgnoreCase)
            {
                "sessionid",
                "session_id",
            };

        /// <summary>
        /// Scalar string fields whose VALUE must NOT be value-pattern scrubbed.
        /// These are structural / identity fields where a CC/SSN/email/IP regex
        /// hit would be a false positive that corrupts legitimate data:
        /// release/version/sdk identity, span/operation names, and URLs/paths
        /// (which have their own URL redactor and routinely contain numbers).
        /// Stack-frame fields and the explicit <c>user</c> object are exempt via
        /// the subtree set below. Matched case-insensitively on the exact key.
        /// (Key-name redaction still applies to these keys as normal.)
        /// </summary>
        private static readonly HashSet<string> ValueScrubExemptKeys =
            new(StringComparer.OrdinalIgnoreCase)
            {
                // release / version / sdk identity
                "release", "version", "dist", "platform", "environment",
                "sdkname", "sdk.name", "sdkversion", "sdk.version",
                "commit.sha", "commit.branch", "commitsha", "branch",
                // correlation ids (opaque, never PII)
                "sessionid", "session_id", "traceid", "spanid", "parentspanid",
                "requestid", "errorid", "errorfingerprint", "debugid",
                // span / operation names
                "operation", "service",
                // URLs / paths — own redactor; numeric-heavy, high false-positive risk
                "url", "uri", "path", "host", "absolute_uri", "abspath",
                // stack-frame scalar fields (also covered by subtree exemption)
                "filename", "function",
            };

        /// <summary>
        /// Keys whose entire nested subtree (object or array) is exempt from
        /// value-pattern scrubbing. The explicit <c>user</c> object is intentional
        /// identification set via <c>SetUser</c> (id/email/ip ship as before —
        /// <c>sendDefaultPii</c> does NOT strip explicitly-set user data, matching
        /// Sentry). Structured stack frames (<c>frames</c>) and raw stack-trace
        /// lines (<c>stackTrace</c>) carry filenames / namespaces, not free-text
        /// PII, and must not be corrupted. Key-name redaction still recurses into
        /// these subtrees.
        /// </summary>
        private static readonly HashSet<string> ValueScrubExemptSubtreeKeys =
            new(StringComparer.OrdinalIgnoreCase)
            {
                "user",
                "frames",
                "stacktrace",
                "stack_trace",
            };

        // ── Compiled value-pattern regexes (compiled once; reused on the wire path) ──

        /// <summary>
        /// Credit-card CANDIDATE: a run of 13-19 digits where spaces or single
        /// hyphens may separate groups. Word boundaries keep it from matching the
        /// middle of a longer digit blob. A match is only redacted after it passes
        /// the Luhn checksum, so Luhn-failing runs (order ids, timestamps) survive.
        /// </summary>
        private static readonly Regex CreditCardCandidate = new(
            @"\b(?:\d[ -]?){12,18}\d\b",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>US SSN in dashed form only — bare 9-digit numbers are NOT matched.</summary>
        private static readonly Regex Ssn = new(
            @"\b\d{3}-\d{2}-\d{4}\b",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>Standard email address.</summary>
        private static readonly Regex Email = new(
            @"\b[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}\b",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>IPv4 with each octet validated to 0-255.</summary>
        private static readonly Regex IPv4 = new(
            @"\b(?:(?:25[0-5]|2[0-4]\d|1?\d?\d)\.){3}(?:25[0-5]|2[0-4]\d|1?\d?\d)\b",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>
        /// IPv6 (best-effort): full 8-group form and common <c>::</c>-compressed
        /// forms. Conservative — only matches text that is unambiguously an IPv6
        /// literal (requires <c>::</c> or all eight hex groups), so it will not eat
        /// ordinary colon-separated text.
        /// </summary>
        private static readonly Regex IPv6 = new(
            @"\b(?:[A-Fa-f0-9]{1,4}:){7}[A-Fa-f0-9]{1,4}\b" +
            @"|(?<![\w:])(?:[A-Fa-f0-9]{1,4}:){1,7}:(?:[A-Fa-f0-9]{1,4}:){0,6}[A-Fa-f0-9]{0,4}(?![\w:])",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>Sanitize a dictionary payload. Returns a sanitized copy.</summary>
        public static IDictionary<string, object?> Sanitize(
            IDictionary<string, object?> payload,
            IEnumerable<string>? extraDenylist = null,
            bool sendDefaultPii = false)
        {
            var ctx = new Context(BuildDenylist(extraDenylist), sendDefaultPii);
            return (IDictionary<string, object?>)Walk(payload, ctx, valueScrub: true)!;
        }

        /// <summary>Sanitize an arbitrary object (dictionary or list root).</summary>
        public static object? SanitizeObject(
            object? payload,
            IEnumerable<string>? extraDenylist = null,
            bool sendDefaultPii = false)
        {
            var ctx = new Context(BuildDenylist(extraDenylist), sendDefaultPii);
            return Walk(payload, ctx, valueScrub: true);
        }

        /// <summary>Sanitize a JsonElement. Returns a new JSON string with sensitive values replaced.</summary>
        public static string SanitizeJson(
            JsonElement element,
            IEnumerable<string>? extraDenylist = null,
            bool sendDefaultPii = false)
        {
            var ctx = new Context(BuildDenylist(extraDenylist), sendDefaultPii);
            var native = JsonToNative(element);
            var scrubbed = Walk(native, ctx, valueScrub: true);
            return JsonSerializer.Serialize(scrubbed);
        }

        /// <summary>
        /// Per-call sanitizer state: the resolved key denylist, the PII gate, and
        /// the reference-cycle guard. Bundled so the recursive walker keeps a tidy
        /// signature.
        /// </summary>
        private sealed class Context
        {
            internal readonly List<string> Denylist;
            internal readonly bool SendDefaultPii;
            internal readonly HashSet<int> Seen = new();

            internal Context(List<string> denylist, bool sendDefaultPii)
            {
                Denylist = denylist;
                SendDefaultPii = sendDefaultPii;
            }
        }

        private static List<string> BuildDenylist(IEnumerable<string>? extra)
        {
            var list = new List<string>(DefaultDenylist.Count + 8);
            foreach (var t in DefaultDenylist) list.Add(t);
            if (extra != null)
            {
                foreach (var t in extra)
                {
                    if (string.IsNullOrEmpty(t)) continue;
                    var lower = t.ToLowerInvariant();
                    if (!list.Contains(lower)) list.Add(lower);
                }
            }
            return list;
        }

        private static bool IsSensitive(string key, List<string> denylist)
        {
            // Exact-key allowlist wins over the substring denylist so opaque
            // correlation ids (e.g. the release-health sessionId) survive.
            if (AllowlistExactKeys.Contains(key)) return false;
            var k = key.ToLowerInvariant();
            foreach (var term in denylist)
            {
                if (k.Contains(term)) return true;
            }
            return false;
        }

        /// <summary>
        /// Recursively copy <paramref name="value"/>, redacting sensitive keys and
        /// (when <paramref name="valueScrub"/> is true) value-pattern PII in strings.
        /// <paramref name="valueScrub"/> is turned off for entire subtrees that are
        /// exempt (e.g. the explicit user object, stack frames).
        /// </summary>
        private static object? Walk(object? value, Context ctx, bool valueScrub)
        {
            if (value is null) return null;
            if (value is string s) return valueScrub ? ScrubValue(s, ctx) : s;
            if (value.GetType().IsPrimitive || value is decimal) return value;

            if (value is IDictionary<string, object?> dictTyped)
            {
                var hash = RuntimeHelpers.GetHashCode(dictTyped);
                if (!ctx.Seen.Add(hash)) return Redacted;
                var outDict = new Dictionary<string, object?>(dictTyped.Count);
                foreach (var kvp in dictTyped)
                    outDict[kvp.Key] = WalkChild(kvp.Key, kvp.Value, ctx, valueScrub);
                return outDict;
            }

            if (value is IDictionary dict)
            {
                var hash = RuntimeHelpers.GetHashCode(dict);
                if (!ctx.Seen.Add(hash)) return Redacted;
                var outDict = new Dictionary<string, object?>();
                foreach (DictionaryEntry kvp in dict)
                {
                    var key = kvp.Key?.ToString() ?? "";
                    outDict[key] = WalkChild(key, kvp.Value, ctx, valueScrub);
                }
                return outDict;
            }

            if (value is IEnumerable enumerable)
            {
                var hash = RuntimeHelpers.GetHashCode(enumerable);
                if (!ctx.Seen.Add(hash)) return Redacted;
                var outList = new List<object?>();
                foreach (var item in enumerable)
                    outList.Add(Walk(item, ctx, valueScrub));
                return outList;
            }

            return value;
        }

        /// <summary>
        /// Process one dictionary entry: key-name redaction first (wins, always on),
        /// then recurse — disabling value scrubbing for this key's subtree when the
        /// key is value-scrub exempt, and disabling it for this scalar string when
        /// the key is an exempt scalar field.
        /// </summary>
        private static object? WalkChild(string key, object? value, Context ctx, bool valueScrub)
        {
            if (IsSensitive(key, ctx.Denylist)) return Redacted;

            // Subtree exemption: an entire nested object/array (the explicit user
            // object, stack frames) ships without value-pattern scrubbing.
            var childValueScrub = valueScrub && !ValueScrubExemptSubtreeKeys.Contains(key);

            // Scalar-field exemption: this string value is structural (release,
            // url, operation, …) and must pass through untouched.
            if (childValueScrub && value is string && ValueScrubExemptKeys.Contains(key))
                childValueScrub = false;

            return Walk(value, ctx, childValueScrub);
        }

        /// <summary>
        /// Apply the value-pattern scrubbers to a single string. Fail-open: any
        /// error (or an over-long string) returns the input unchanged. Ordering:
        /// credit-card (Luhn-gated) and SSN are ALWAYS scrubbed; email + IP are
        /// scrubbed only when sendDefaultPii is false.
        /// </summary>
        private static string ScrubValue(string input, Context ctx)
        {
            if (string.IsNullOrEmpty(input)) return input;
            // Skip very large strings — not realistic PII carriers, and scanning
            // them on the wire path is a latency / backtracking risk.
            if (input.Length > MaxValueScanLength) return input;
            // Cheap fast-path: no digits and no '@' means nothing here can match.
            if (!ContainsScannableChar(input)) return input;

            try
            {
                var result = input;
                // (A) ALWAYS — high-risk financial/identity data.
                result = ScrubCreditCards(result);
                result = Ssn.Replace(result, Redacted);
                // (B) UNLESS the caller opted into PII.
                if (!ctx.SendDefaultPii)
                {
                    result = Email.Replace(result, Redacted);
                    result = IPv4.Replace(result, Redacted);
                    result = IPv6.Replace(result, Redacted);
                }
                return result;
            }
            catch
            {
                // Fail-open: never drop/break an event over a scrubber error.
                return input;
            }
        }

        private static bool ContainsScannableChar(string s)
        {
            foreach (var c in s)
            {
                if (char.IsDigit(c) || c == '@') return true;
                // IPv6 hex literals can be letters-only between colons.
                if (c == ':') return true;
            }
            return false;
        }

        /// <summary>
        /// Replace credit-card candidate runs that pass the Luhn checksum. Runs that
        /// fail Luhn (order ids, timestamps, arbitrary digit blobs) are preserved.
        /// </summary>
        private static string ScrubCreditCards(string input)
        {
            return CreditCardCandidate.Replace(input, m =>
                PassesLuhn(m.Value) ? Redacted : m.Value);
        }

        /// <summary>
        /// Luhn (mod-10) checksum over the digits of <paramref name="candidate"/>,
        /// ignoring spaces / hyphens. Returns false unless the digit count is a
        /// plausible card length (13-19) so short Luhn-coincidental numbers are not
        /// flagged.
        /// </summary>
        internal static bool PassesLuhn(string candidate)
        {
            int sum = 0;
            int count = 0;
            bool alternate = false;
            // Walk right-to-left, doubling every second digit.
            for (int i = candidate.Length - 1; i >= 0; i--)
            {
                var c = candidate[i];
                if (c == ' ' || c == '-') continue;
                if (c < '0' || c > '9') return false;
                int d = c - '0';
                count++;
                if (alternate)
                {
                    d *= 2;
                    if (d > 9) d -= 9;
                }
                sum += d;
                alternate = !alternate;
            }
            if (count < 13 || count > 19) return false;
            return sum % 10 == 0;
        }

        private static object? JsonToNative(JsonElement el)
        {
            switch (el.ValueKind)
            {
                case JsonValueKind.Object:
                    var d = new Dictionary<string, object?>();
                    foreach (var p in el.EnumerateObject()) d[p.Name] = JsonToNative(p.Value);
                    return d;
                case JsonValueKind.Array:
                    var list = new List<object?>();
                    foreach (var i in el.EnumerateArray()) list.Add(JsonToNative(i));
                    return list;
                case JsonValueKind.String: return el.GetString();
                case JsonValueKind.Number:
                    if (el.TryGetInt64(out var l)) return l;
                    if (el.TryGetDouble(out var dbl)) return dbl;
                    return el.GetRawText();
                case JsonValueKind.True: return true;
                case JsonValueKind.False: return false;
                case JsonValueKind.Null:
                case JsonValueKind.Undefined: return null;
                default: return el.GetRawText();
            }
        }
    }
}
