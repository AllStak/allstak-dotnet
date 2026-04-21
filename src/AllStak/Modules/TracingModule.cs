using System.Diagnostics;
using AllStak.Models;
using AllStak.Transport;
using Microsoft.Extensions.Logging;

namespace AllStak.Modules;

/// <summary>Distributed tracing spans, buffered and batched.</summary>
public sealed class TracingModule : IDisposable
{
    private const string Path = "/ingest/v1/spans";
    private readonly HttpTransport _transport;
    private readonly AllStakOptions _options;
    private readonly ILogger _logger;
    private readonly FlushBuffer<SpanPayload> _buffer;
    private readonly AsyncLocal<string?> _currentTraceId = new();
    private readonly AsyncLocal<Stack<string>?> _spanStack = new();

    internal TracingModule(HttpTransport transport, AllStakOptions options, ILogger logger)
    {
        _transport = transport;
        _options = options;
        _logger = logger;
        _buffer = new FlushBuffer<SpanPayload>(
            "tracing", options.BufferSize, options.FlushIntervalMs, FlushBatchAsync, logger);
    }

    public string GetTraceId()
    {
        if (_currentTraceId.Value == null)
            _currentTraceId.Value = Guid.NewGuid().ToString("N");
        return _currentTraceId.Value;
    }

    public void SetTraceId(string traceId) => _currentTraceId.Value = traceId;
    public void ResetTrace()
    {
        _currentTraceId.Value = null;
        _spanStack.Value = null;
    }

    public string? CurrentSpanId => _spanStack.Value?.Count > 0 ? _spanStack.Value.Peek() : null;

    /// <summary>Begin a span. Dispose the returned Span to finish it.</summary>
    public Span StartSpan(string operation, string description = "", IDictionary<string, string>? tags = null)
    {
        var traceId = GetTraceId();
        var spanId = Guid.NewGuid().ToString("N");
        var parentSpanId = CurrentSpanId ?? "";

        _spanStack.Value ??= new Stack<string>();
        _spanStack.Value.Push(spanId);

        return new Span(
            traceId, spanId, parentSpanId, operation, description,
            _options.ServiceName, _options.Environment ?? "", _options.Release ?? "",
            tags != null ? new Dictionary<string, string>(tags) : new Dictionary<string, string>(),
            NowMs(), OnSpanFinished);
    }

    private void OnSpanFinished(Span span)
    {
        if (_spanStack.Value is { Count: > 0 })
        {
            try { _spanStack.Value.Pop(); } catch { }
        }
        _buffer.Push(span.ToPayload());
    }

    public Task FlushAsync() => _buffer.FlushAsync();
    public void Dispose() => _buffer.Dispose();

    private async Task FlushBatchAsync(IReadOnlyList<SpanPayload> items)
    {
        var batch = new SpanBatch { Spans = new List<SpanPayload>(items) };
        try
        {
            await _transport.PostAsync(Path, batch).ConfigureAwait(false);
        }
        catch (AllStakAuthException) { }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[AllStak] span flush error (discarding)");
        }
    }

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}

/// <summary>A single span. Dispose to finish.</summary>
public sealed class Span : IDisposable
{
    private readonly Action<Span> _onFinish;
    internal string TraceId { get; }
    internal string SpanId { get; }
    internal string ParentSpanId { get; }
    internal string Operation { get; }
    internal string Description { get; private set; }
    internal string Service { get; }
    internal string Environment { get; }
    internal string Release { get; }
    internal Dictionary<string, string> Tags { get; }
    internal long StartTimeMs { get; }
    internal long? EndTimeMs { get; private set; }
    internal string StatusValue { get; private set; } = "ok";
    private bool _finished;

    internal Span(string traceId, string spanId, string parentSpanId, string operation,
        string description, string service, string environment, string release,
        Dictionary<string, string> tags, long startTimeMs, Action<Span> onFinish)
    {
        TraceId = traceId;
        SpanId = spanId;
        ParentSpanId = parentSpanId;
        Operation = operation;
        Description = description;
        Service = service;
        Environment = environment;
        Release = release;
        Tags = tags;
        StartTimeMs = startTimeMs;
        _onFinish = onFinish;
    }

    public Span SetTag(string key, string value) { Tags[key] = value; return this; }
    public Span SetDescription(string description) { Description = description; return this; }
    public Span SetStatus(string status) { StatusValue = status; return this; }

    public void Finish(string status = "ok")
    {
        if (_finished) return;
        _finished = true;
        StatusValue = status is "ok" or "error" or "timeout" ? status : "ok";
        EndTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _onFinish(this);
    }

    public void Dispose() => Finish(_finished ? StatusValue : StatusValue);

    internal SpanPayload ToPayload()
    {
        var end = EndTimeMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return new SpanPayload
        {
            TraceId = TraceId,
            SpanId = SpanId,
            ParentSpanId = ParentSpanId,
            Operation = Operation,
            Description = Description,
            Status = StatusValue,
            DurationMs = end - StartTimeMs,
            StartTimeMillis = StartTimeMs,
            EndTimeMillis = end,
            Service = Service,
            Environment = Environment,
            Release = Release,
            Tags = Tags,
        };
    }
}
