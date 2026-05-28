# Changelog

All notable changes to the AllStak .NET SDK.
This project follows [Semantic Versioning](https://semver.org/).

## 0.1.2 — 2026-05-18

Improves privacy handling, API key rotation, and transport failure visibility.

### Added — P0-C: Sanitizer wired into wire path
- New `AllStak.Sanitizer` static class with `Sanitize(IDictionary<string, object?>)` and `SanitizeJson(JsonElement)`. 25-term canonical denylist, recursive, cycle-safe via `RuntimeHelpers.GetHashCode`, pure (no caller mutation).
- `HttpTransport.PostAsync<T>` now serialises payload → parses → scrubs → re-serialises before the network send. One chokepoint protects every telemetry type (errors, logs, http, db, traces).
- Fail-open: if the sanitizer throws on a pathological payload, the SDK logs at Warning level and falls through with the raw payload — telemetry is never blocked.

### Added — P0-H: API key rotation
- `AllStakOptions.ApiKeyProvider: Func<string>?` — when set, the transport resolves the API key per-request via the delegate. Host apps can rotate keys from env / vault / KMS without restart.
- `X-AllStak-Key` is now set per-request via `HttpRequestMessage` (was a default-header static-string before).

### Added — P0-I: Observable transport failures
- New `AllStak.TransportErrorContext` (path, last status, last exception, attempt count).
- `AllStakOptions.OnTransportError: Action<TransportErrorContext>?` — invoked when `PostAsync` exhausts retries. Host apps can plug their own metrics pipeline / dead-letter queue.
- Final exhausted-retries failure is now logged at **Warning** level (was Debug — effectively silent in production logging configs).

### Validation
- Verified sensitive values are scrubbed from telemetry payloads before delivery.
- Covered nested metadata, headers, request bodies, and common credential field names.

### Build
- 55/55 xUnit tests pass on `net10.0`. Sanitizer covers 11 of them.
- Package built against `net8.0` + `net9.0`.

## 0.1.0 — 2026-04-11

First public release. Driven end-to-end through a real ASP.NET Core 8 +
EF Core + SQLite + JWT application (TaskFlow API) with full auth, CRUD,
validation, outbound HTTP, cron, and real exceptions — and verified in
the AllStak dashboard against every feature page.

### Added

- **AllStakClient** — thread-safe singleton with per-module accessors.
- **AllStakOptions** — `ApiKey`, `Host`, `Environment`, `Release`,
  `ServiceName`, `FlushIntervalMs`, `BufferSize`, `Debug`, timeout &
  retry knobs, and capture toggles.
- **HttpTransport** — retry/backoff (1s → 2s → 4s → 8s +jitter, max 5),
  401 disable, 4xx no-retry, thread-safe.
- **FlushBuffer<T>** — bounded ring buffer with background timer, 80%
  early-flush, overflow warning, single-flight drain.
- **ErrorModule** — `CaptureExceptionAsync`, `CaptureErrorAsync`,
  breadcrumbs, user context, full `RequestContext` + trace ID
  serialization.
- **LogModule** — buffered structured logs with `debug | info | warn |
  error | fatal` levels (normalizes `"warning"` → `"warn"`).
- **HttpMonitorModule** — inbound + outbound HTTP telemetry, batched
  up to 100 per POST, query string stripping.
- **TracingModule** — span hierarchy with `AsyncLocal` parent tracking,
  `IDisposable` spans, tag/status/description API.
- **DatabaseModule** — normalized SQL, MD5 query hash, query-type
  detection, status + error + row count, batched.
- **CronModule** — `IDisposable` `JobHandle` with automatic heartbeat
  on dispose (success or failed), and direct `PingAsync`.
- **ASP.NET Core integration** — `AddAllStak(...)` DI extension,
  `UseAllStak()` middleware that captures inbound HTTP telemetry,
  unhandled exceptions (with request context, user claims, and trace
  ID), and per-request trace lifecycle.
- **Outbound HTTP integration** — `AllStakHttpHandler` delegating handler
  that plugs into any named `HttpClient` via `IHttpClientFactory`.
- **EF Core integration** — `AllStakDbCommandInterceptor` that captures
  every query (reader / non-query / scalar, sync & async) with real
  timings, row counts, success/failure, and error messages.

### Verified production surface

- Real register/login/logout/JWT flow ✔
- Real CRUD with ownership / forbidden / 404 / state-transition guards ✔
- Real model validation failures ✔
- Real unhandled exceptions with full stack, user, request context, trace ✔
- 50+ logs across 3 services (auth, tasks, cron, webhooks) ✔
- 96 inbound HTTP requests + 2 outbound (success + failure) ✔
- 50+ EF Core queries (SELECT / INSERT / UPDATE / DELETE) grouped ✔
- Distributed tracing with span linking on error detail ✔
- 2 cron monitors (healthy + failed) auto-created ✔

### Breaking changes

None — initial public release.
