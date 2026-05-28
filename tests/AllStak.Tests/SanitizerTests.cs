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
}
