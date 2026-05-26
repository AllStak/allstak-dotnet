# AllStak for .NET

AllStak SDK for ASP.NET Core and .NET services. Captures exceptions, logs, inbound and outbound HTTP requests, spans, and Entity Framework telemetry.

## Install

> **Not yet on NuGet.** `dotnet add package AllStak` is reserved
> but does not resolve a published artifact yet. Until first
> publish lands (tracked in
> [`docs/devops/sdk-python-dotnet-first-publish.md`](https://github.com/AllStak/allstak/blob/dev/docs/devops/sdk-python-dotnet-first-publish.md)
> in the platform monorepo), build and reference the project
> directly from source:
>
> ```bash
> git clone https://github.com/AllStak/allstak-dotnet.git
> dotnet add reference path/to/allstak-dotnet/src/AllStak/AllStak.csproj
> ```
>
> Once `0.1.x` ships on NuGet, the canonical install becomes:
>
> ```bash
> dotnet add package AllStak
> ```

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

## License

MIT
