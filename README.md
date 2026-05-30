# AllStak for .NET

AllStak SDK for ASP.NET Core and .NET services. Captures exceptions, logs, inbound and outbound HTTP requests, spans, and Entity Framework telemetry.

## Install

```bash
dotnet add package AllStak
```

## ASP.NET Core setup

```csharp
using AllStak;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddAllStak(options =>
{
    options.ApiKey = Environment.GetEnvironmentVariable("ALLSTAK_API_KEY");
    options.Environment = builder.Environment.EnvironmentName;
    options.Release = Environment.GetEnvironmentVariable("ALLSTAK_RELEASE");
    options.ServiceName = "checkout-api";
});

var app = builder.Build();

app.UseAllStak();

app.MapGet("/health", () => Results.Ok(new { ok = true }));
app.Run();
```

## Manual capture

```csharp
AllStakClient.Instance.Errors.Capture(new InvalidOperationException("checkout failed"));
AllStakClient.Instance.Logs.Info("payment retry", new Dictionary<string, object>
{
    ["orderId"] = "ord_123"
});
```

## Automatic instrumentation

`AddAllStak()` wires the in-box integrations for you. Outbound HTTP and logging
capture are fully automatic; EF Core query telemetry takes one line per `DbContext`
(EF Core does not auto-apply interceptors). Each is default-on and individually
opt-out:

- **Outbound HttpClient** — on .NET 8+ the AllStak delegating handler is registered
  as an `IHttpClientFactory` default (`ConfigureHttpClientDefaults`), so every
  named/typed client propagates the distributed trace and records outbound request
  telemetry automatically. No `AddHttpMessageHandler` call needed.
- **Entity Framework Core** — the query interceptor is registered as a DI singleton.
  EF Core does not apply an interceptor automatically, so add `UseAllStak(serviceProvider)`
  inside your `AddDbContext` options callback to record SQL, duration, rows, and
  errors:

  ```csharp
  builder.Services.AddDbContext<AppDbContext>((sp, options) =>
      options.UseSqlite(connectionString).UseAllStak(sp));
  ```
- **Logging** — the AllStak logger provider is registered in the host logging
  pipeline, so `ILogger` calls flow to the logs ingest and `LogError` /
  `LogCritical` with an exception are promoted to the error stream. No separate
  `builder.Logging.AddAllStak()` call needed.

### Opting out / manual wiring

```csharp
builder.Services.AddAllStak(options =>
{
    options.ApiKey = Environment.GetEnvironmentVariable("ALLSTAK_API_KEY");

    options.InstrumentOutboundHttp = false;        // skip auto HttpClient handler
    options.InstrumentEntityFrameworkCore = false; // skip the EF Core interceptor singleton
    options.CaptureLogs = false;                   // skip the logging provider
});
```

If you opt out but still want a single client or context instrumented:

```csharp
// One named HttpClient only:
builder.Services.AddTransient<AllStakHttpHandler>();
builder.Services.AddHttpClient("payments").AddHttpMessageHandler<AllStakHttpHandler>();

// Instrument a DbContext explicitly (works with or without AddAllStak):
optionsBuilder.UseSqlite(connection).UseAllStak();
```

## Configuration

| Option | Description |
| --- | --- |
| `ApiKey` | Project API key. |
| `Environment` | Deployment environment. |
| `Release` | App version or commit SHA. |
| `ServiceName` | Logical service name. |
| `InstrumentOutboundHttp` | Auto-instrument outbound HttpClient calls (default `true`). |
| `InstrumentEntityFrameworkCore` | Register the EF Core query interceptor singleton in DI; attach per context with `UseAllStak(sp)` (default `true`). |
| `CaptureLogs` | Auto-register the logging provider (default `true`). |
| `CaptureLogsMinLevel` | Minimum log level captured by the provider (default `Information`). |

## Privacy

The SDK redacts common sensitive headers and body fields. Avoid placing secrets in custom properties.

## Troubleshooting

- No events: confirm `ALLSTAK_API_KEY` is set before the app starts.
- Missing request telemetry: confirm `app.UseAllStak()` runs before endpoint mapping that should be captured.
- Short-lived worker: flush before shutdown when possible.

## Contributing and Support

- Report bugs with the GitHub bug report template: https://github.com/AllStak/allstak-dotnet/issues/new/choose
- Open pull requests using the checklist in [CONTRIBUTING.md](CONTRIBUTING.md).
- Report security vulnerabilities privately through [SECURITY.md](SECURITY.md).

## License

MIT
