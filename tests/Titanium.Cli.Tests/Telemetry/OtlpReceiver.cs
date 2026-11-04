using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace Titanium.Cli.Tests.Telemetry;

/// <summary>One decoded span from an OTLP export request.</summary>
internal sealed class ReceivedSpan
{
    public string TraceId { get; init; } = "";
    public string SpanId { get; init; } = "";
    public string ParentSpanId { get; init; } = "";
    public string TraceState { get; init; } = "";
    public string Name { get; init; } = "";
    public int Kind { get; init; }
    public int StatusCode { get; init; }
    public string Service { get; init; } = "";
    public Dictionary<string, object> Attributes { get; init; } = [];
    public Dictionary<string, object> ResourceAttributes { get; init; } = [];
    public List<ReceivedEvent> Events { get; init; } = [];

    public bool IsRoot => ParentSpanId.Length == 0;

    public string Str(string key) => Attributes.TryGetValue(key, out var v) ? (string)v : "";

    public long Int(string key) => Attributes.TryGetValue(key, out var v) ? (long)v : -1;
}

internal sealed class ReceivedEvent
{
    public string Name { get; init; } = "";
    public Dictionary<string, object> Attributes { get; init; } = [];
}

/// <summary>
///     An in-process OTLP/HTTP collector. It answers every export with an empty success, decodes the protobuf body
///     by hand (the SDK keeps its generated types internal), and can be told to stall or fail so exporter-down and
///     queue-overflow behavior is observable.
/// </summary>
internal sealed class OtlpReceiver : IDisposable
{
    private readonly HttpListener listener = new();
    private readonly ConcurrentQueue<ReceivedSpan> spans = new();
    private readonly ConcurrentQueue<string> metricNames = new();
    private readonly CancellationTokenSource stop = new();
    private readonly TaskCompletionSource releaseStall = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int traceRequests;
    private int metricRequests;
    private volatile bool stalled;
    private volatile int failStatus;

    public OtlpReceiver()
    {
        Port = FreePort();
        listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        listener.Start();
        _ = Task.Run(AcceptAsync);
    }

    public int Port { get; }

    /// <summary>Base URL; the HTTP exporter appends <c>/v1/traces</c> and <c>/v1/metrics</c>.</summary>
    public string Endpoint => $"http://127.0.0.1:{Port}";

    public IReadOnlyList<ReceivedSpan> Spans => spans.ToArray();

    public IReadOnlyCollection<string> MetricNames => metricNames.ToArray();

    public int TraceRequests => Volatile.Read(ref traceRequests);

    public int MetricRequests => Volatile.Read(ref metricRequests);

    /// <summary>While set, requests are held open until <see cref="Release" /> (the exporter then times out).</summary>
    public void Stall() => stalled = true;

    public void Release()
    {
        stalled = false;
        releaseStall.TrySetResult();
    }

    /// <summary>Answers every export with this HTTP status instead of 200 (0 restores success).</summary>
    public void FailWith(int status) => failStatus = status;

    public async Task<IReadOnlyList<ReceivedSpan>> WaitForSpansAsync(
        Func<IReadOnlyList<ReceivedSpan>, bool> done, int timeoutMs = 10_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var snapshot = Spans;
            if (done(snapshot))
            {
                return snapshot;
            }

            await Task.Delay(25);
        }

        return Spans;
    }

    public async Task<bool> WaitForMetricAsync(string name, int timeoutMs = 10_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (MetricNames.Contains(name))
            {
                return true;
            }

            await Task.Delay(25);
        }

        return MetricNames.Contains(name);
    }

    public void Dispose()
    {
        stop.Cancel();
        releaseStall.TrySetResult();
        try
        {
            listener.Stop();
            listener.Close();
        }
        catch (ObjectDisposedException)
        {
            // Already closed.
        }
    }

    private async Task AcceptAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch (Exception) when (stop.IsCancellationRequested || !listener.IsListening)
            {
                return;
            }

            _ = Task.Run(() => HandleAsync(context));
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        try
        {
            using var body = new MemoryStream();
            await context.Request.InputStream.CopyToAsync(body);
            var path = context.Request.Url!.AbsolutePath;
            if (path.EndsWith("/v1/traces", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref traceRequests);
            }
            else if (path.EndsWith("/v1/metrics", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref metricRequests);
            }

            if (stalled)
            {
                await Task.WhenAny(releaseStall.Task, Task.Delay(Timeout.Infinite, stop.Token));
            }

            if (failStatus != 0)
            {
                context.Response.StatusCode = failStatus;
                context.Response.Close();
                return;
            }

            if (path.EndsWith("/v1/traces", StringComparison.Ordinal))
            {
                foreach (var span in OtlpDecoder.Spans(body.ToArray()))
                {
                    spans.Enqueue(span);
                }
            }
            else if (path.EndsWith("/v1/metrics", StringComparison.Ordinal))
            {
                foreach (var name in OtlpDecoder.MetricNames(body.ToArray()))
                {
                    metricNames.Enqueue(name);
                }
            }

            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/x-protobuf";
            context.Response.ContentLength64 = 0;
            context.Response.Close();
        }
        catch (Exception)
        {
            try { context.Response.Abort(); } catch (Exception) { /* receiver is shutting down */ }
        }
    }

    private static int FreePort()
    {
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}

/// <summary>Reads the parts of the OTLP protobuf messages the tests assert on, straight off the wire format.</summary>
internal static class OtlpDecoder
{
    public static IEnumerable<ReceivedSpan> Spans(byte[] request)
    {
        foreach (var resourceSpans in Fields(request, 1))
        {
            var resourceAttributes = new Dictionary<string, object>();
            foreach (var resource in Fields(resourceSpans, 1))
            {
                foreach (var attribute in Fields(resource, 1))
                {
                    AddAttribute(resourceAttributes, attribute);
                }
            }

            var service = resourceAttributes.TryGetValue("service.name", out var s) ? (string)s : "";
            foreach (var scopeSpans in Fields(resourceSpans, 2))
            {
                foreach (var span in Fields(scopeSpans, 2))
                {
                    yield return DecodeSpan(span, service, resourceAttributes);
                }
            }
        }
    }

    public static IEnumerable<string> MetricNames(byte[] request)
    {
        foreach (var resourceMetrics in Fields(request, 1))
        {
            foreach (var scopeMetrics in Fields(resourceMetrics, 2))
            {
                foreach (var metric in Fields(scopeMetrics, 2))
                {
                    foreach (var name in Fields(metric, 1))
                    {
                        yield return Encoding.UTF8.GetString(name);
                    }
                }
            }
        }
    }

    private static ReceivedSpan DecodeSpan(byte[] span, string service, Dictionary<string, object> resource)
    {
        var attributes = new Dictionary<string, object>();
        foreach (var attribute in Fields(span, 9))
        {
            AddAttribute(attributes, attribute);
        }

        var events = new List<ReceivedEvent>();
        foreach (var raw in Fields(span, 11))
        {
            var eventAttributes = new Dictionary<string, object>();
            foreach (var attribute in Fields(raw, 3))
            {
                AddAttribute(eventAttributes, attribute);
            }

            events.Add(new ReceivedEvent { Name = Text(raw, 2), Attributes = eventAttributes });
        }

        var statusCode = 0;
        foreach (var status in Fields(span, 15))
        {
            statusCode = (int)Varint(status, 3);
        }

        return new ReceivedSpan
        {
            TraceId = Convert.ToHexString(First(span, 1)).ToLowerInvariant(),
            SpanId = Convert.ToHexString(First(span, 2)).ToLowerInvariant(),
            ParentSpanId = Convert.ToHexString(First(span, 4)).ToLowerInvariant(),
            TraceState = Text(span, 3),
            Name = Text(span, 5),
            Kind = (int)Varint(span, 6),
            StatusCode = statusCode,
            Service = service,
            Attributes = attributes,
            ResourceAttributes = resource,
            Events = events,
        };
    }

    private static void AddAttribute(Dictionary<string, object> into, byte[] keyValue)
    {
        var key = Text(keyValue, 1);
        foreach (var value in Fields(keyValue, 2))
        {
            foreach (var (field, wire, payload, number) in Walk(value))
            {
                _ = wire;
                switch (field)
                {
                    case 1:
                        into[key] = Encoding.UTF8.GetString(payload);
                        break;
                    case 2:
                        into[key] = number != 0;
                        break;
                    case 3:
                        into[key] = (long)number;
                        break;
                    case 4:
                        into[key] = BitConverter.UInt64BitsToDouble(number);
                        break;
                }
            }
        }
    }

    private static byte[] First(byte[] message, int field) => Fields(message, field).FirstOrDefault() ?? [];

    private static string Text(byte[] message, int field) => Encoding.UTF8.GetString(First(message, field));

    private static ulong Varint(byte[] message, int field)
    {
        foreach (var (f, _, _, number) in Walk(message))
        {
            if (f == field)
            {
                return number;
            }
        }

        return 0;
    }

    private static IEnumerable<byte[]> Fields(byte[] message, int field)
    {
        foreach (var (f, wire, payload, _) in Walk(message))
        {
            if (f == field && wire == 2)
            {
                yield return payload;
            }
        }
    }

    private static IEnumerable<(int Field, int Wire, byte[] Payload, ulong Number)> Walk(byte[] data)
    {
        var position = 0;
        while (position < data.Length)
        {
            var key = ReadVarint(data, ref position);
            var field = (int)(key >> 3);
            var wire = (int)(key & 7);
            switch (wire)
            {
                case 0:
                    yield return (field, wire, [], ReadVarint(data, ref position));
                    break;
                case 1:
                    yield return (field, wire, [], BitConverter.ToUInt64(data, position));
                    position += 8;
                    break;
                case 2:
                    var length = (int)ReadVarint(data, ref position);
                    yield return (field, wire, data.AsSpan(position, length).ToArray(), 0);
                    position += length;
                    break;
                case 5:
                    yield return (field, wire, [], BitConverter.ToUInt32(data, position));
                    position += 4;
                    break;
                default:
                    throw new InvalidDataException($"Unsupported protobuf wire type {wire}.");
            }
        }
    }

    private static ulong ReadVarint(byte[] data, ref int position)
    {
        ulong result = 0;
        var shift = 0;
        while (true)
        {
            var b = data[position++];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return result;
            }

            shift += 7;
        }
    }
}
