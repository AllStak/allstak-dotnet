using System.Net;
using System.Text.Json;
using AllStak;
using AllStak.Integrations.AspNetCore;
using AllStak.Integrations.EntityFrameworkCore;
using AllStak.Transport;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AllStak.Tests;

/// <summary>
/// End-to-end proof that the EF Core query interceptor actually records DB
/// telemetry for the path the SDK documents — i.e. the developer wires
/// <c>o.UseSqlite(conn).UseAllStak(sp)</c> inside <c>AddDbContext</c>. The test
/// executes a real SQLite query and asserts a <c>/ingest/v1/db</c> payload is
/// produced (not just DI registration).
/// </summary>
[Collection("Singleton")]
public sealed class EfCoreInterceptorE2ETests : IDisposable
{
    public EfCoreInterceptorE2ETests() => AllStakClient.Reset();

    public void Dispose() => AllStakClient.Reset();

    /// <summary>Captures the path + JSON body of every request and returns 202.</summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public List<(string Path, string Body)> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var body = await TestHttpContent.ReadDecodedStringAsync(request, ct);
            Requests.Add((request.RequestUri!.AbsolutePath, body));
            return new HttpResponseMessage(HttpStatusCode.Accepted)
            {
                Content = new StringContent("""{"data":{"id":"evt_e2e"}}"""),
            };
        }
    }

    /// <summary>
    /// Swap the live HttpClient inside the initialized client's transport with one
    /// backed by <paramref name="handler"/>, so we observe the wire without network.
    /// </summary>
    private static void HijackTransport(AllStakClient client, HttpMessageHandler handler)
    {
        var transportField = typeof(AllStakClient).GetField("_transport",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var transport = transportField.GetValue(client)!;

        var httpField = typeof(HttpTransport).GetField("_http",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        http.DefaultRequestHeaders.TryAddWithoutValidation("X-AllStak-Key", "ask_test_ef_e2e");
        httpField.SetValue(transport, http);
    }

    private sealed class WidgetContext : DbContext
    {
        public WidgetContext(DbContextOptions<WidgetContext> options) : base(options) { }
        public DbSet<Widget> Widgets => Set<Widget>();
    }

    private sealed class Widget
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
    }

    [Fact]
    public async Task DocumentedAddDbContextWiring_RecordsDbTelemetry_OnRealQuery()
    {
        // A single shared in-memory SQLite connection kept open for the test's life.
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        var services = new ServiceCollection();
        services.AddAllStak(o =>
        {
            o.ApiKey = "ask_test_ef_e2e";
            o.Host = "https://fake.allstak.test";
            o.Environment = "test";
            o.EnableAutoSessionTracking = false;
            o.EnableOfflineCache = false;
            o.FlushIntervalMs = 50;
        });

        // The DOCUMENTED wiring: attach the interceptor via UseAllStak(sp) inside
        // AddDbContext. This is the path the README/comments now describe.
        services.AddDbContext<WidgetContext>((sp, options) =>
            options.UseSqlite(connection).UseAllStak(sp));

        using var provider = services.BuildServiceProvider();

        // Force the SDK singleton to initialize, then hijack its transport so we
        // can observe what would be posted to /ingest/v1/db.
        var client = provider.GetRequiredService<AllStakClient>();
        var handler = new CapturingHandler();
        HijackTransport(client, handler);

        // Create schema + run a real SELECT through the documented context.
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WidgetContext>();
            db.Database.EnsureCreated();
            db.Widgets.Add(new Widget { Name = "gizmo" });
            db.SaveChanges();
            _ = db.Widgets.Where(w => w.Name == "gizmo").ToList();
        }

        // Flush the DB buffer so the batched telemetry is posted.
        await client.Database.FlushAsync();

        // The interceptor must have produced at least one /ingest/v1/db payload
        // carrying a normalized query — proving the feature is live, not inert.
        var dbRequests = handler.Requests.Where(r => r.Path == "/ingest/v1/db").ToList();
        Assert.NotEmpty(dbRequests);

        var sawQuery = dbRequests.Any(r =>
        {
            using var doc = JsonDocument.Parse(r.Body);
            return doc.RootElement.TryGetProperty("queries", out var queries)
                   && queries.ValueKind == JsonValueKind.Array
                   && queries.GetArrayLength() > 0
                   && queries.EnumerateArray().Any(q =>
                          q.TryGetProperty("normalizedQuery", out var nq)
                          && !string.IsNullOrWhiteSpace(nq.GetString()));
        });
        Assert.True(sawQuery, "Expected a /ingest/v1/db payload with a normalized query record.");

        connection.Dispose();
    }
}
