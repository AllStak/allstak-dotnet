# AllStak .NET SDK

Official .NET SDK for [AllStak](https://allstak.dev) — error tracking,
structured logs, HTTP + EF Core monitoring, distributed tracing, and cron
monitoring for ASP.NET Core applications.

```bash
dotnet add package AllStak
```

## 60-second setup

```csharp
using AllStak;
using AllStak.Integrations.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddAllStak(opts =>
{
    opts.ApiKey = "ask_live_...";
    opts.Environment = "production";
    opts.Release = "myapp@1.4.2";
    opts.ServiceName = "myapp-api";
});

var app = builder.Build();
app.UseAllStak();   // request middleware + auto exception capture
app.Run();
```

That's it. Every request, every unhandled exception, and every trace are
captured automatically.

## ASP.NET Core in two lines

```csharp
builder.Services.AddAllStak(o => o.ApiKey = "ask_live_...");
app.UseAllStak();
```

This automatically captures:

- Inbound HTTP request telemetry (method, path, host, status, duration, sizes)
- Unhandled exceptions from routes — with full stack, request context,
  trace ID, and the authenticated user (claims)
- A fresh trace ID per request (or adopts `traceparent` / `X-AllStak-Trace-Id`)

## EF Core

One line in your `AddDbContext` wires an EF Core `DbCommandInterceptor` that
records every query with normalized SQL, timings, row count, status, and
error message:

```csharp
using AllStak.Integrations.EntityFrameworkCore;

builder.Services.AddDbContext<AppDbContext>(opts =>
    opts.UseSqlite("Data Source=app.db")
        .AddInterceptors(new AllStakDbCommandInterceptor()));
```

Works with any EF Core provider (SQL Server, PostgreSQL, SQLite, MySQL, etc.).

## HttpClient (outbound)

Attach the delegating handler once and every outbound call made by a named
`HttpClient` is captured with method, host, path, status, duration, and
error class name on failure:

```csharp
using AllStak.Integrations.HttpClient;

builder.Services.AddTransient<AllStakHttpHandler>();
builder.Services.AddHttpClient("default")
    .AddHttpMessageHandler<AllStakHttpHandler>();
```

Inject `IHttpClientFactory` and use `CreateClient("default")` — the handler
runs automatically.

## What gets captured automatically

| What                                  | How                                                     |
| ------------------------------------- | ------------------------------------------------------- |
| Unhandled exceptions                  | `app.UseAllStak()` middleware                           |
| Inbound HTTP requests                 | `app.UseAllStak()` middleware                           |
| Per-request trace ID                  | `app.UseAllStak()` middleware                           |
| User context (claims)                 | `app.UseAllStak()` middleware                           |
| SQL queries                           | `AllStakDbCommandInterceptor` on your `DbContext`       |
| Outbound HTTP                         | `AllStakHttpHandler` on your named `HttpClient`         |

## Manual capture cheat sheet

```csharp
var client = AllStakClient.Instance;

// Errors
try { DoWork(); }
catch (Exception ex)
{
    await client.Errors.CaptureExceptionAsync(ex, metadata: new()
    {
        ["orderId"] = "ORD-123",
    });
}

// Logs (buffered, flushed in background)
client.Logs.Info("Order placed", new() { ["id"] = "ORD-1" });
client.Logs.Warn("Slow query", new() { ["ms"] = 4500 });
client.Logs.Error("Payment failed", new() { ["gateway"] = "stripe" });
// valid levels: debug | info | warn | error | fatal (NOT "warning")

// Distributed tracing
using (var span = client.Tracing.StartSpan("db.query", "SELECT users"))
{
    span.SetTag("db.type", "postgresql");
    var rows = await db.Users.ToListAsync();
}

// Cron monitoring — slug auto-created on first ping
using (client.Cron.Job("daily-report"))
{
    await GenerateReport();
    // heartbeat sent on dispose (success | failed + message)
}

// User context (overrides ASP.NET claims)
AllStakClient.SetUser(id: "u-1", email: "alice@example.com");
AllStakClient.ClearUser();

// Graceful flush
await client.FlushAllAsync();
```

## Dashboard mapping

| Your code                                             | Dashboard page       |
| ----------------------------------------------------- | -------------------- |
| `Errors.CaptureExceptionAsync` / middleware           | **Errors**, **Incidents** |
| `Logs.Info/Warn/Error`                                | **Logs**             |
| `UseAllStak()` middleware                             | **Requests** (inbound) |
| `AllStakHttpHandler` on HttpClient                    | **Requests** (outbound) |
| `AllStakDbCommandInterceptor`                         | **Database**         |
| `Tracing.StartSpan`                                   | **Traces**           |
| `Cron.Job` / `Cron.PingAsync`                         | **Cron Jobs**        |

## Configuration

| Property               | Default                   | Notes |
| ---------------------- | ------------------------- | ----- |
| `ApiKey`               | _required_                | Your `ask_live_...` key. |
| `Host`                 | `https://api.allstak.sa`   | Override with your AllStak ingest host. |
| `Environment`          | `null`                    | e.g. `"production"` |
| `Release`              | `null`                    | e.g. `"myapp@1.4.2"` |
| `ServiceName`          | `"dotnet-service"`        | Shown on spans and logs. |
| `FlushIntervalMs`      | `2000`                    | Background flush interval. |
| `BufferSize`           | `500`                     | Max buffered items per feature. |
| `Debug`                | `false`                   | Verbose SDK logging. |
| `ConnectTimeoutMs`     | `3000`                    | Transport connect timeout. |
| `ReadTimeoutMs`        | `3000`                    | Transport read timeout. |
| `MaxRetries`           | `5`                       | Retry 5xx with exponential backoff. |
| `CaptureUnhandledExceptions` | `true`              | Auto-capture from middleware. |
| `CaptureHttpRequests`        | `true`              | Auto-capture inbound HTTP. |
| `CaptureUserContext`         | `true`              | Attach user claims to errors. |

Environment variables: `ALLSTAK_API_KEY`, `ALLSTAK_HOST`,
`ALLSTAK_ENVIRONMENT`, `ALLSTAK_RELEASE`.

## Production notes

- **Never crashes your app.** The SDK swallows every internal exception.
  If ingestion fails, your request still completes.
- **Retries.** 5xx and network errors retry with exponential backoff
  (1s → 2s → 4s → 8s, +jitter, max 5 attempts). 4xx are not retried.
- **401 disables the SDK.** An invalid API key disables the SDK for the
  rest of the process — no further events are sent, a warning is logged,
  and your app keeps running.
- **Flush on shutdown.** `AppDomain.ProcessExit` triggers a best-effort flush.
- **Thread-safe.** All public APIs are safe to call from any thread / any
  `AsyncLocal` context.
- **Non-blocking.** Telemetry is buffered and flushed on a background thread.
  Your request pipeline is never blocked by SDK work.

## Troubleshooting

| Symptom                               | Fix                                              |
| ------------------------------------- | ------------------------------------------------ |
| No events in dashboard                | Check `Host` and `ApiKey`. Set `Debug = true`.   |
| 401 warning                           | Invalid API key. Create a new one in Settings → API Keys. |
| Inbound requests missing              | Make sure `app.UseAllStak()` is called before `UseEndpoints`. |
| DB queries missing                    | Call `.AddInterceptors(new AllStakDbCommandInterceptor())` on your `DbContext`. |
| Outbound HTTP missing                 | Add `AllStakHttpHandler` to your named `HttpClient` factory. |
| Cron monitor not appearing            | It is auto-created on first ping; check the slug matches. |
| `warn` vs `warning`                   | SDK normalizes both; backend only accepts `warn`. |

## Full ASP.NET Core minimal API example

```csharp
using AllStak;
using AllStak.Integrations.AspNetCore;
using AllStak.Integrations.EntityFrameworkCore;
using AllStak.Integrations.HttpClient;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddAllStak(o =>
{
    o.ApiKey = Environment.GetEnvironmentVariable("ALLSTAK_API_KEY")!;
    o.Environment = "production";
    o.Release = "taskflow@1.4.2";
    o.ServiceName = "taskflow-api";
});

builder.Services.AddDbContext<AppDbContext>(o =>
    o.UseSqlite("Data Source=app.db")
        .AddInterceptors(new AllStakDbCommandInterceptor()));

builder.Services.AddTransient<AllStakHttpHandler>();
builder.Services.AddHttpClient("default")
    .AddHttpMessageHandler<AllStakHttpHandler>();

var app = builder.Build();
app.UseAllStak();

app.MapPost("/orders/{id:int}/charge", async (int id, AppDbContext db, IHttpClientFactory f) =>
{
    var order = await db.Orders.FindAsync(id);
    if (order == null) return Results.NotFound();

    using var span = AllStakClient.Instance.Tracing.StartSpan("charge", $"order {id}");
    span.SetTag("order.id", id.ToString());

    var http = f.CreateClient("default");
    var resp = await http.PostAsJsonAsync("https://api.stripe.com/v1/charges",
        new { amount = order.Total });

    if (!resp.IsSuccessStatusCode)
        throw new InvalidOperationException($"Stripe {(int)resp.StatusCode}");

    return Results.Ok(new { ok = true });
});

app.Run();
```

## License

MIT
