# AllStak

**Error tracking for .NET Core and ASP.NET. One-line `AddAllStak()` in `Program.cs`.**

[![CI](https://github.com/AllStak/allstak-dotnet/actions/workflows/ci.yml/badge.svg)](https://github.com/AllStak/allstak-dotnet/actions)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)

Official AllStak SDK for .NET — captures exceptions, structured logs, HTTP requests, Entity Framework queries, and distributed traces for ASP.NET Core and .NET services.

## Dashboard

View captured events live at [app.allstak.sa](https://app.allstak.sa).

![AllStak dashboard](https://app.allstak.sa/images/dashboard-preview.png)

## Features

- `AppDomain.UnhandledException` capture
- `Microsoft.Extensions.Logging` provider for structured logs
- ASP.NET Core middleware for inbound request telemetry
- `HttpClient` `DelegatingHandler` for outbound HTTP
- Entity Framework Core `DbCommandInterceptor` for query capture
- Distributed tracing with `AllStak.Tracing.StartSpan`
- Cron heartbeats and singleton or DI-based registration
- Targets `net8.0`, `net9.0`

## What You Get

Once integrated, every event flows to your AllStak dashboard:

- **Errors** — stack traces, breadcrumbs, release + environment tags
- **Logs** — structured logs bridged from `ILogger` with search and filters
- **HTTP** — inbound and outbound request timing, status codes, failed calls
- **Database** — Entity Framework Core query capture
- **Traces** — distributed spans with context propagation
- **Alerts** — email and webhook notifications on regressions

## Installation

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

## Quick Start

> Create a project at [app.allstak.sa](https://app.allstak.sa) to get your API key.

```csharp
using AllStak;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddAllStak(opts =>
{
    opts.ApiKey = Environment.GetEnvironmentVariable("ALLSTAK_API_KEY")!;
    opts.Environment = "production";
    opts.Release = "myapp@1.0.0";
    opts.ServiceName = "myapp-api";
});

var app = builder.Build();
app.UseAllStak();

AllStakClient.Instance.Errors.Capture(new Exception("test: hello from allstak-dotnet"));

app.Run();
```

Run the app — the test error appears in your dashboard within seconds.

## Get Your API Key

1. Sign up at [app.allstak.sa](https://app.allstak.sa)
2. Create a project
3. Copy your API key from **Project Settings → API Keys**
4. Export it as `ALLSTAK_API_KEY` or pass it to `AddAllStak(opts => opts.ApiKey = ...)`

## Configuration

| Option | Type | Required | Default | Description |
|---|---|---|---|---|
| `ApiKey` | `string` | yes | — | Project API key (`ask_live_…`) |
| `Host` | `string` | no | `https://api.allstak.sa` | Ingest host override |
| `Environment` | `string?` | no | — | Deployment env |
| `Release` | `string?` | no | — | Version / build tag |
| `ServiceName` | `string` | no | `dotnet-service` | Logical service identifier |
| `FlushIntervalMs` | `int` | no | `2000` | Background flush cadence |
| `BufferSize` | `int` | no | `500` | Max items per buffer |
| `Debug` | `bool` | no | `false` | Verbose SDK logging |

## Example Usage

Capture an exception with context:

```csharp
AllStakClient.Instance.Errors.Capture(ex, new Dictionary<string, object?>
{
    ["orderId"] = "ORD-42",
});
```

Send a structured log:

```csharp
AllStakClient.Instance.Logs.Info("Order processed", new { orderId = "ORD-123" });
```

Set user and tag:

```csharp
AllStakClient.Instance.Errors.SetUser(new UserContext { Id = "u_42", Email = "alice@example.com" });
AllStakClient.Instance.Errors.SetTag("region", "eu-west-1");
```

## Production Endpoint

Production endpoint: `https://api.allstak.sa`. Override via `Host` for self-hosted deployments:

```csharp
builder.Services.AddAllStak(opts =>
{
    opts.ApiKey = "...";
    opts.Host = "https://allstak.mycorp.com";
});
```

## Links

- Documentation: https://docs.allstak.sa
- Dashboard: https://app.allstak.sa
- Source: https://github.com/AllStak/allstak-dotnet

## License

MIT © AllStak
