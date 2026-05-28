using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace AllStak
{
    /// <summary>
    /// AllStak .NET SDK sanitizer.
    /// Recursively scrubs sensitive keys across the event surface
    /// (user, extras, metadata, breadcrumbs.data, contexts, request, response).
    /// Conforms to the canonical AllStak SDK denylist
    /// (docs/standards/sdk-platform-standards.md).
    ///
    /// Semantics:
    /// - Case-insensitive substring match on keys.
    /// - Value replacement with the sentinel string "[REDACTED]" (key preserved).
    /// - Recursion into IDictionary and IEnumerable; primitives pass through.
    /// - Cycle protection via reference-equality HashSet using RuntimeHelpers.GetHashCode.
    /// - Pure: returns a sanitized copy; never mutates caller-owned structures.
    /// </summary>
    public static class Sanitizer
    {
        public const string Redacted = "[REDACTED]";

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

        /// <summary>Sanitize a dictionary payload. Returns a sanitized copy.</summary>
        public static IDictionary<string, object?> Sanitize(
            IDictionary<string, object?> payload,
            IEnumerable<string>? extraDenylist = null)
        {
            var denylist = BuildDenylist(extraDenylist);
            var seen = new HashSet<int>();
            return (IDictionary<string, object?>)Walk(payload, denylist, seen)!;
        }

        /// <summary>Sanitize an arbitrary object (dictionary or list root).</summary>
        public static object? SanitizeObject(object? payload, IEnumerable<string>? extraDenylist = null)
        {
            var denylist = BuildDenylist(extraDenylist);
            var seen = new HashSet<int>();
            return Walk(payload, denylist, seen);
        }

        /// <summary>Sanitize a JsonElement. Returns a new JSON string with sensitive values replaced.</summary>
        public static string SanitizeJson(JsonElement element, IEnumerable<string>? extraDenylist = null)
        {
            var denylist = BuildDenylist(extraDenylist);
            var seen = new HashSet<int>();
            var native = JsonToNative(element);
            var scrubbed = Walk(native, denylist, seen);
            return JsonSerializer.Serialize(scrubbed);
        }

        private static List<string> BuildDenylist(IEnumerable<string>? extra)
        {
            var list = new List<string>(DefaultDenylist.Count + 8);
            foreach (var t in DefaultDenylist) list.Add(t);
            if (extra != null)
            {
                foreach (var t in extra)
                {
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

        private static object? Walk(object? value, List<string> denylist, HashSet<int> seen)
        {
            if (value is null) return null;
            if (value is string || value.GetType().IsPrimitive || value is decimal) return value;

            if (value is IDictionary<string, object?> dictTyped)
            {
                var hash = RuntimeHelpers.GetHashCode(dictTyped);
                if (!seen.Add(hash)) return Redacted;
                var outDict = new Dictionary<string, object?>(dictTyped.Count);
                foreach (var kvp in dictTyped)
                {
                    outDict[kvp.Key] = IsSensitive(kvp.Key, denylist)
                        ? Redacted
                        : Walk(kvp.Value, denylist, seen);
                }
                return outDict;
            }

            if (value is IDictionary dict)
            {
                var hash = RuntimeHelpers.GetHashCode(dict);
                if (!seen.Add(hash)) return Redacted;
                var outDict = new Dictionary<string, object?>();
                foreach (DictionaryEntry kvp in dict)
                {
                    var key = kvp.Key?.ToString() ?? "";
                    outDict[key] = IsSensitive(key, denylist)
                        ? Redacted
                        : Walk(kvp.Value, denylist, seen);
                }
                return outDict;
            }

            if (value is IEnumerable enumerable)
            {
                var hash = RuntimeHelpers.GetHashCode(enumerable);
                if (!seen.Add(hash)) return Redacted;
                var outList = new List<object?>();
                foreach (var item in enumerable)
                {
                    outList.Add(Walk(item, denylist, seen));
                }
                return outList;
            }

            return value;
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
