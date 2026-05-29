using System.Collections.Generic;
using System.Text.Json;
using Xunit;

namespace AllStak.Tests;

public class SanitizerTests
{
    private const string R = AllStak.Sanitizer.Redacted;

    [Fact]
    public void Redacts_TopLevel_SensitiveKey()
    {
        var input = new Dictionary<string, object?> { ["Authorization"] = "Bearer abc" };
        var output = AllStak.Sanitizer.Sanitize(input);
        Assert.Equal(R, output["Authorization"]);
    }

    [Fact]
    public void CaseInsensitive_KeyMatch()
    {
        var input = new Dictionary<string, object?>
        {
            ["X-Api-Key"] = "k",
            ["PASSWORD"] = "p",
            ["safe"] = "v"
        };
        var output = AllStak.Sanitizer.Sanitize(input);
        Assert.Equal(R, output["X-Api-Key"]);
        Assert.Equal(R, output["PASSWORD"]);
        Assert.Equal("v", output["safe"]);
    }

    [Fact]
    public void DoesNotRedact_SessionCorrelationId()
    {
        // The release-health sessionId is an opaque correlation id, not a secret.
        // It must survive sanitization so the backend can correlate sessions and
        // mark them errored/crashed — even though it contains the "session" term.
        var input = new Dictionary<string, object?>
        {
            ["sessionId"] = "7222575d-0bec-470e-b8be-ddc5c212ad6c",
            ["session_id"] = "abc-123",
        };
        var output = AllStak.Sanitizer.Sanitize(input);
        Assert.Equal("7222575d-0bec-470e-b8be-ddc5c212ad6c", output["sessionId"]);
        Assert.Equal("abc-123", output["session_id"]);
    }

    [Fact]
    public void StillRedacts_SessionSecrets()
    {
        // Genuine session secrets are NOT allowlisted: they keep matching the
        // token / cookie / secret denylist substrings.
        var input = new Dictionary<string, object?>
        {
            ["session_token"] = "t",
            ["session_cookie"] = "c",
            ["session_secret"] = "s",
        };
        var output = AllStak.Sanitizer.Sanitize(input);
        Assert.Equal(R, output["session_token"]);
        Assert.Equal(R, output["session_cookie"]);
        Assert.Equal(R, output["session_secret"]);
    }

    [Fact]
    public void Recurses_Into_NestedDict()
    {
        var input = new Dictionary<string, object?>
        {
            ["user"] = new Dictionary<string, object?>
            {
                ["email"] = "a@b",
                ["password"] = "p"
            }
        };
        var output = AllStak.Sanitizer.Sanitize(input);
        var user = (Dictionary<string, object?>)output["user"]!;
        Assert.Equal("a@b", user["email"]);
        Assert.Equal(R, user["password"]);
    }

    [Fact]
    public void Recurses_Into_List()
    {
        var input = new Dictionary<string, object?>
        {
            ["items"] = new List<object?>
            {
                new Dictionary<string, object?> { ["token"] = "t" },
                new Dictionary<string, object?> { ["safe"] = "v" }
            }
        };
        var output = AllStak.Sanitizer.Sanitize(input);
        var items = (List<object?>)output["items"]!;
        Assert.Equal(R, ((Dictionary<string, object?>)items[0]!)["token"]);
        Assert.Equal("v", ((Dictionary<string, object?>)items[1]!)["safe"]);
    }

    [Fact]
    public void DoesNot_Mutate_Caller()
    {
        var input = new Dictionary<string, object?> { ["Authorization"] = "v" };
        AllStak.Sanitizer.Sanitize(input);
        Assert.Equal("v", input["Authorization"]);
    }

    [Fact]
    public void Cycle_Protection()
    {
        var d = new Dictionary<string, object?> { ["a"] = 1 };
        d["self"] = d;
        var output = AllStak.Sanitizer.Sanitize(d);
        Assert.Equal(1, output["a"]);
        Assert.Equal(R, output["self"]);
    }

    [Fact]
    public void Covers_CanonicalDenylist()
    {
        var input = new Dictionary<string, object?>();
        foreach (var term in AllStak.Sanitizer.DefaultDenylist)
        {
            input[term] = "leaky";
        }
        var output = AllStak.Sanitizer.Sanitize(input);
        foreach (var term in AllStak.Sanitizer.DefaultDenylist)
        {
            Assert.Equal(R, output[term]);
        }
    }

    [Fact]
    public void Canary_ShouldNotLeak_Dotnet()
    {
        var input = new Dictionary<string, object?>
        {
            ["metadata"] = new Dictionary<string, object?> { ["api_key"] = "should_not_leak_dotnet" },
            ["user"] = new Dictionary<string, object?> { ["password"] = "should_not_leak_dotnet" }
        };
        var output = AllStak.Sanitizer.Sanitize(input);
        var serialized = JsonSerializer.Serialize(output);
        Assert.DoesNotContain("should_not_leak_dotnet", serialized);
    }

    [Fact]
    public void Extension_Denylist_Adds()
    {
        var input = new Dictionary<string, object?> { ["custom_pii"] = "v" };
        var output = AllStak.Sanitizer.Sanitize(input, new[] { "custom_pii" });
        Assert.Equal(R, output["custom_pii"]);
    }

    [Fact]
    public void Sanitizes_JsonElement()
    {
        var json = """{"Authorization":"Bearer x","safe":"v"}""";
        using var doc = JsonDocument.Parse(json);
        var scrubbed = AllStak.Sanitizer.SanitizeJson(doc.RootElement);
        Assert.Contains("[REDACTED]", scrubbed);
        Assert.DoesNotContain("Bearer x", scrubbed);
        Assert.Contains("\"safe\":\"v\"", scrubbed);
    }

    [Fact]
    public void Primitive_Passthrough()
    {
        Assert.Equal(42, AllStak.Sanitizer.SanitizeObject(42));
        Assert.Equal("x", AllStak.Sanitizer.SanitizeObject("x"));
        Assert.Null(AllStak.Sanitizer.SanitizeObject(null));
    }

    // ── Value-pattern PII scrubbing (Sentry data-scrubbing parity) ──────────

    // A real Visa test number that passes the Luhn checksum.
    private const string ValidCard = "4111111111111111";
    // Same length, last digit bumped so Luhn FAILS — must be preserved.
    private const string LuhnInvalidRun = "4111111111111112";

    [Fact]
    public void CreditCard_RedactedOnly_WhenLuhnValid()
    {
        var input = new Dictionary<string, object?>
        {
            ["message"] = $"charge to card {ValidCard} declined",
        };
        var output = AllStak.Sanitizer.Sanitize(input);
        var msg = (string)output["message"]!;
        Assert.DoesNotContain(ValidCard, msg);
        Assert.Contains(R, msg);
        // Surrounding free text is preserved — conservative replacement.
        Assert.Contains("charge to card", msg);
        Assert.Contains("declined", msg);
    }

    [Fact]
    public void CreditCard_WithSeparators_LuhnValid_Redacted()
    {
        var input = new Dictionary<string, object?>
        {
            ["note"] = "4111 1111 1111 1111",
        };
        var output = AllStak.Sanitizer.Sanitize(input);
        Assert.Equal(R, output["note"]);
    }

    [Fact]
    public void CreditCard_LuhnInvalidRun_Preserved()
    {
        // A 16-digit run that fails Luhn (e.g. an order id) must NOT be nuked.
        var input = new Dictionary<string, object?>
        {
            ["orderId"] = LuhnInvalidRun,
            ["timestamp"] = "20260529120000123456", // 20-digit blob, not a card length
        };
        var output = AllStak.Sanitizer.Sanitize(input);
        Assert.Equal(LuhnInvalidRun, output["orderId"]);
        Assert.Equal("20260529120000123456", output["timestamp"]);
    }

    [Fact]
    public void Ssn_WithHyphens_Redacted()
    {
        var input = new Dictionary<string, object?>
        {
            ["message"] = "applicant ssn 123-45-6789 on file",
        };
        var output = AllStak.Sanitizer.Sanitize(input);
        var msg = (string)output["message"]!;
        Assert.DoesNotContain("123-45-6789", msg);
        Assert.Contains(R, msg);
    }

    [Fact]
    public void Ssn_BareNineDigits_Preserved()
    {
        // No hyphens → not treated as an SSN (could be any numeric id).
        var input = new Dictionary<string, object?> { ["note"] = "ref 123456789 done" };
        var output = AllStak.Sanitizer.Sanitize(input);
        Assert.Equal("ref 123456789 done", output["note"]);
    }

    [Fact]
    public void Email_Redacted_WhenSendDefaultPiiFalse()
    {
        var input = new Dictionary<string, object?>
        {
            ["message"] = "login failed for alice@example.com",
        };
        var output = AllStak.Sanitizer.Sanitize(input, sendDefaultPii: false);
        var msg = (string)output["message"]!;
        Assert.DoesNotContain("alice@example.com", msg);
        Assert.Contains(R, msg);
    }

    [Fact]
    public void IPv4_Redacted_WhenSendDefaultPiiFalse()
    {
        var input = new Dictionary<string, object?>
        {
            ["message"] = "request from 192.168.1.42 blocked",
        };
        var output = AllStak.Sanitizer.Sanitize(input, sendDefaultPii: false);
        var msg = (string)output["message"]!;
        Assert.DoesNotContain("192.168.1.42", msg);
        Assert.Contains(R, msg);
    }

    [Fact]
    public void IPv4_InvalidOctet_Preserved()
    {
        // 999 is not a valid octet → not an IP; version-looking numbers survive.
        var input = new Dictionary<string, object?> { ["note"] = "build 1.2.999.0" };
        var output = AllStak.Sanitizer.Sanitize(input, sendDefaultPii: false);
        Assert.Equal("build 1.2.999.0", output["note"]);
    }

    [Fact]
    public void Email_And_IPv4_Preserved_WhenSendDefaultPiiTrue()
    {
        var input = new Dictionary<string, object?>
        {
            ["message"] = "login failed for alice@example.com from 192.168.1.42",
        };
        var output = AllStak.Sanitizer.Sanitize(input, sendDefaultPii: true);
        var msg = (string)output["message"]!;
        // Opted into PII → email + IP pass through unchanged.
        Assert.Contains("alice@example.com", msg);
        Assert.Contains("192.168.1.42", msg);
        Assert.DoesNotContain(R, msg);
    }

    [Fact]
    public void CreditCard_And_Ssn_AlwaysScrubbed_EvenWhenSendDefaultPiiTrue()
    {
        // (A) layer is ALWAYS on regardless of the PII flag.
        var input = new Dictionary<string, object?>
        {
            ["message"] = $"card {ValidCard} ssn 123-45-6789",
        };
        var output = AllStak.Sanitizer.Sanitize(input, sendDefaultPii: true);
        var msg = (string)output["message"]!;
        Assert.DoesNotContain(ValidCard, msg);
        Assert.DoesNotContain("123-45-6789", msg);
    }

    [Fact]
    public void ExplicitUser_Email_NotScrubbed()
    {
        // The explicit user object is intentional identification; sendDefaultPii
        // (false here) must NOT strip user.email / user.ip — matching Sentry.
        var input = new Dictionary<string, object?>
        {
            ["user"] = new Dictionary<string, object?>
            {
                ["id"] = "u-1",
                ["email"] = "alice@example.com",
                ["ip"] = "192.168.1.42",
            },
        };
        var output = AllStak.Sanitizer.Sanitize(input, sendDefaultPii: false);
        var user = (Dictionary<string, object?>)output["user"]!;
        Assert.Equal("alice@example.com", user["email"]);
        Assert.Equal("192.168.1.42", user["ip"]);
        Assert.Equal("u-1", user["id"]);
    }

    [Fact]
    public void StackFrame_Paths_NotCorrupted()
    {
        // Frame filenames / functions and raw stackTrace lines must pass through
        // even if they contain numbers that could look like IPs to a naive scanner.
        var input = new Dictionary<string, object?>
        {
            ["frames"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["filename"] = "/app/src/Service.cs",
                    ["function"] = "MyApp.Service.Handle",
                    ["absPath"] = "/var/www/10.0.0.1/Service.cs",
                },
            },
            ["stackTrace"] = new List<object?>
            {
                "at MyApp.Service.Handle() in /var/www/192.168.0.1/Service.cs:line 42",
            },
        };
        var output = AllStak.Sanitizer.Sanitize(input, sendDefaultPii: false);
        var frame = (Dictionary<string, object?>)((List<object?>)output["frames"]!)[0]!;
        Assert.Equal("/app/src/Service.cs", frame["filename"]);
        Assert.Equal("MyApp.Service.Handle", frame["function"]);
        Assert.Equal("/var/www/10.0.0.1/Service.cs", frame["absPath"]);
        var line = (string)((List<object?>)output["stackTrace"]!)[0]!;
        Assert.Contains("192.168.0.1", line);
    }

    [Fact]
    public void ReleaseAndUrl_Fields_NotValueScrubbed()
    {
        // Structural identity / URL fields must survive value scrubbing even when
        // they carry IP-looking or number-heavy values.
        var input = new Dictionary<string, object?>
        {
            ["release"] = "1.2.3.4",
            ["url"] = "https://10.0.0.1/api/v1/users",
            ["path"] = "/users/192.168.0.1",
            ["operation"] = "http.client GET 10.0.0.5",
        };
        var output = AllStak.Sanitizer.Sanitize(input, sendDefaultPii: false);
        Assert.Equal("1.2.3.4", output["release"]);
        Assert.Equal("https://10.0.0.1/api/v1/users", output["url"]);
        Assert.Equal("/users/192.168.0.1", output["path"]);
        Assert.Equal("http.client GET 10.0.0.5", output["operation"]);
    }

    [Fact]
    public void ValueScrubbing_AppliesTo_MetadataAndBreadcrumbs()
    {
        var input = new Dictionary<string, object?>
        {
            ["metadata"] = new Dictionary<string, object?>
            {
                ["note"] = "user alice@example.com paid with 4111111111111111",
            },
            ["breadcrumbs"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["message"] = "called 192.168.1.1",
                    ["data"] = new Dictionary<string, object?> { ["detail"] = "ssn 123-45-6789" },
                },
            },
        };
        var output = AllStak.Sanitizer.Sanitize(input, sendDefaultPii: false);
        var meta = (Dictionary<string, object?>)output["metadata"]!;
        var note = (string)meta["note"]!;
        Assert.DoesNotContain("alice@example.com", note);
        Assert.DoesNotContain("4111111111111111", note);

        var crumb = (Dictionary<string, object?>)((List<object?>)output["breadcrumbs"]!)[0]!;
        Assert.DoesNotContain("192.168.1.1", (string)crumb["message"]!);
        var data = (Dictionary<string, object?>)crumb["data"]!;
        Assert.DoesNotContain("123-45-6789", (string)data["detail"]!);
    }

    [Fact]
    public void KeyBasedRedaction_StillWins_OverValueScrubbing()
    {
        // A denylisted key is fully redacted regardless of its (PII) value.
        var input = new Dictionary<string, object?>
        {
            ["password"] = "alice@example.com",
            ["api_key"] = "192.168.1.1",
        };
        var output = AllStak.Sanitizer.Sanitize(input, sendDefaultPii: true);
        Assert.Equal(R, output["password"]);
        Assert.Equal(R, output["api_key"]);
    }

    [Fact]
    public void ExtraDenylist_Plumbed_AsValueScrubVariant()
    {
        var input = new Dictionary<string, object?> { ["customer_pan"] = "secret" };
        var output = AllStak.Sanitizer.Sanitize(input, extraDenylist: new[] { "customer_pan" }, sendDefaultPii: true);
        Assert.Equal(R, output["customer_pan"]);
    }

    [Fact]
    public void FailOpen_OnPathologicalInput()
    {
        // A huge string (over the scan cap) is returned unchanged rather than
        // risking pathological regex cost — and certainly never throws/drops.
        var huge = new string('a', 200_000) + " 4111111111111111";
        var input = new Dictionary<string, object?> { ["message"] = huge };
        var output = AllStak.Sanitizer.Sanitize(input, sendDefaultPii: false);
        // Over-cap: scrubbing is skipped, value preserved verbatim (fail-open).
        Assert.Equal(huge, output["message"]);
    }

    [Fact]
    public void Luhn_Helper_KnownVectors()
    {
        Assert.True(AllStak.Sanitizer.PassesLuhn("4111111111111111"));   // Visa test
        Assert.True(AllStak.Sanitizer.PassesLuhn("4111 1111 1111 1111")); // with spaces
        Assert.False(AllStak.Sanitizer.PassesLuhn("4111111111111112"));  // bad checksum
        Assert.False(AllStak.Sanitizer.PassesLuhn("123456789"));         // too short for a card
    }

    [Theory]
    // Conservative: none of these legitimate values may be over-redacted.
    [InlineData("trace 7222575d-0bec-470e-b8be-ddc5c212ad6c done")] // UUID (hex/colons-free)
    [InlineData("at 2026-05-29T12:00:00.123456Z")]                  // ISO timestamp
    [InlineData("order #1234567890 shipped")]                       // 10-digit id (Luhn-fail / not SSN)
    [InlineData("phone (555) 867-5309")]                            // phone number
    [InlineData("port 8080 latency 123ms")]                         // plain numbers
    public void Conservative_DoesNotOverRedact(string text)
    {
        var input = new Dictionary<string, object?> { ["message"] = text };
        var output = AllStak.Sanitizer.Sanitize(input, sendDefaultPii: false);
        Assert.Equal(text, output["message"]);
    }

    [Fact]
    public void ValueScrubbing_FlowsThrough_SanitizeJson()
    {
        var json = """{"message":"pay 4111111111111111 from alice@example.com"}""";
        using var doc = JsonDocument.Parse(json);
        var scrubbed = AllStak.Sanitizer.SanitizeJson(doc.RootElement, null, sendDefaultPii: false);
        Assert.DoesNotContain("4111111111111111", scrubbed);
        Assert.DoesNotContain("alice@example.com", scrubbed);
    }
}
