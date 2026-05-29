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

## HttpClient

Register the AllStak handler with clients that should emit outbound request telemetry:

```csharp
builder.Services.AddHttpClient("payments")
    .AddHttpMessageHandler<AllStakHttpClientHandler>();
```

## Configuration

| Option | Description |
| --- | --- |
| `ApiKey` | Project API key. |
| `Environment` | Deployment environment. |
| `Release` | App version or commit SHA. |
| `ServiceName` | Logical service name. |
| `CaptureRequestBodies` | Capture redacted request bodies. |
| `CaptureResponseBodies` | Capture redacted response bodies. |

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
