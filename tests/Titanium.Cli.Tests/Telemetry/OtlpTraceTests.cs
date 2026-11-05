using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Cli.AccessLog;
using Titanium.Cli.Telemetry;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Configuration.Models;

namespace Titanium.Cli.Tests.Telemetry;

/// <summary>
///     The exporter end to end: a real proxy, a real origin, the real OTLP exporter and an in-process collector
///     that decodes what arrives.
/// </summary>
[TestClass]
public partial class OtlpTraceTests
{
    private const string IncomingTraceId = "0af7651916cd43dd8448eb211c80319c";
    private const string IncomingSpanId = "b7ad6b7169203331";

    private static ReceivedSpan Single(IEnumerable<ReceivedSpan> spans, string name) =>
        spans.Single(s => s.Name == name);

    [TestMethod]
    [Timeout(60_000)]
    public async Task OneRequest_ExportsServerSpanWithClientChild_AndHeadersLeaveWithTheClientSpan()
    {
        using var receiver = new OtlpReceiver();
        using var origin = new TestOrigin();
        using var proxy = await TestProxy.StartAsync("r1", "c1", [origin.Port]);
        using var host = TelemetryTestOptions.Start(proxy.Proxy, TelemetryTestOptions.For(receiver));
        using var client = TelemetryTestOptions.Client();

        using var request = new HttpRequestMessage(HttpMethod.Get, proxy.Url("/api/items?token=SECRET-QUERY&x=1"));
        request.Headers.Add("Cookie", "session=SECRET-COOKIE");
        request.Headers.Add("Authorization", "Bearer SECRET-AUTH");
        using var response = await client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var spans = await receiver.WaitForSpansAsync(s => s.Count >= 2);
        var server = Single(spans, "twp.proxy.request");
        var upstream = Single(spans, "twp.proxy.upstream");

        Assert.AreEqual(2, server.Kind, "SPAN_KIND_SERVER");
        Assert.AreEqual(3, upstream.Kind, "SPAN_KIND_CLIENT");
        Assert.IsTrue(server.IsRoot);
        Assert.AreEqual(server.SpanId, upstream.ParentSpanId);
        Assert.AreEqual(server.TraceId, upstream.TraceId);
        Assert.AreEqual("twp-test", server.Service);

        Assert.AreEqual("GET", server.Str("http.request.method"));
        Assert.AreEqual("/api/items", server.Str("url.path"));
        Assert.AreEqual("http", server.Str("url.scheme"));
        Assert.AreEqual(200, server.Int("http.response.status_code"));
        Assert.AreEqual("127.0.0.1", server.Str("server.address"));
        Assert.AreEqual("r1", server.Str("twp.route"));
        Assert.AreEqual("c1", server.Str("twp.cluster"));
        Assert.AreEqual("1.1", server.Str("network.protocol.version"));
        Assert.AreEqual(0, server.StatusCode, "a 200 leaves the status unset");

        Assert.AreEqual("127.0.0.1", upstream.Str("server.address"));
        Assert.AreEqual(origin.Port, upstream.Int("server.port"));
        Assert.AreEqual("d1", upstream.Str("twp.destination"));
        Assert.AreEqual(200, upstream.Int("http.response.status_code"));

        // The origin joins the trace as a child of the client span, and the trace is sampled.
        var seen = origin.Requests.Single();
        Assert.AreEqual($"00-{server.TraceId}-{upstream.SpanId}-01", seen.Header("traceparent"));

        // Privacy: no query, cookie or credential anywhere in what was exported.
        foreach (var span in spans)
        {
            foreach (var value in span.Attributes.Values.Concat(span.ResourceAttributes.Values).Select(v => v.ToString()))
            {
                StringAssert.DoesNotMatch(value ?? "", SensitiveExportRegex());
            }

            Assert.IsFalse(span.Attributes.Keys.Any(k => k.Contains("cookie", StringComparison.OrdinalIgnoreCase)
                                                         || k.Contains("authorization", StringComparison.OrdinalIgnoreCase)));
        }
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task IncomingTraceContext_IsContinued_AndBaggageAndTraceStatePassThrough()
    {
        using var receiver = new OtlpReceiver();
        using var origin = new TestOrigin();
        using var proxy = await TestProxy.StartAsync("r1", "c1", [origin.Port]);
        using var host = TelemetryTestOptions.Start(proxy.Proxy, TelemetryTestOptions.For(receiver));
        using var client = TelemetryTestOptions.Client();

        using var request = new HttpRequestMessage(HttpMethod.Get, proxy.Url("/x"));
        request.Headers.Add("traceparent", $"00-{IncomingTraceId}-{IncomingSpanId}-01");
        request.Headers.Add("tracestate", "vendor=abc,other=1");
        request.Headers.Add("baggage", "userId=alice,tier=gold");
        using var response = await client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var spans = await receiver.WaitForSpansAsync(s => s.Count >= 2);
        var server = Single(spans, "twp.proxy.request");
        var upstream = Single(spans, "twp.proxy.upstream");

        Assert.AreEqual(IncomingTraceId, server.TraceId);
        Assert.AreEqual(IncomingSpanId, server.ParentSpanId);
        StringAssert.Contains(server.TraceState, "vendor=abc");
        Assert.AreEqual(server.SpanId, upstream.ParentSpanId);

        var seen = origin.Requests.Single();
        Assert.AreEqual($"00-{IncomingTraceId}-{upstream.SpanId}-01", seen.Header("traceparent"));
        Assert.AreEqual("vendor=abc,other=1", seen.Header("tracestate"));
        Assert.AreEqual("userId=alice,tier=gold", seen.Header("baggage"));
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task MalformedTraceParent_StartsANewRoot()
    {
        using var receiver = new OtlpReceiver();
        using var origin = new TestOrigin();
        using var proxy = await TestProxy.StartAsync("r1", "c1", [origin.Port]);
        using var host = TelemetryTestOptions.Start(proxy.Proxy, TelemetryTestOptions.For(receiver));
        using var client = TelemetryTestOptions.Client();

        using var request = new HttpRequestMessage(HttpMethod.Get, proxy.Url("/x"));
        request.Headers.Add("traceparent", "not-a-trace-context");
        using var response = await client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var spans = await receiver.WaitForSpansAsync(s => s.Count >= 2);
        var server = Single(spans, "twp.proxy.request");
        Assert.IsTrue(server.IsRoot);
        Assert.AreEqual(32, server.TraceId.Length);
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task Sampler_RatioZero_ExportsNothing_ButStillPropagatesAnUnsampledContext()
    {
        using var receiver = new OtlpReceiver();
        using var origin = new TestOrigin();
        using var proxy = await TestProxy.StartAsync("r1", "c1", [origin.Port]);
        var host = TelemetryTestOptions.Start(proxy.Proxy, TelemetryTestOptions.For(receiver, c =>
        {
            c.Traces!.Sampler = "ratio";
            c.Traces.Ratio = 0;
        }));
        using var client = TelemetryTestOptions.Client();

        for (var i = 0; i < 5; i++)
        {
            using var response = await client.GetAsync(proxy.Url("/x"));
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        }

        host.Dispose();
        Assert.AreEqual(0, receiver.Spans.Count);
        foreach (var seen in origin.Requests)
        {
            StringAssert.Matches(seen.Header("traceparent"), UnsampledTraceParentRegex());
        }
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task Sampler_Ratio_RecordsRoughlyThatShareOfRoots()
    {
        using var receiver = new OtlpReceiver();
        using var origin = new TestOrigin();
        using var proxy = await TestProxy.StartAsync("r1", "c1", [origin.Port]);
        var host = TelemetryTestOptions.Start(proxy.Proxy, TelemetryTestOptions.For(receiver, c =>
        {
            c.Traces!.Sampler = "ratio";
            c.Traces.Ratio = 0.5;
        }));
        using var client = TelemetryTestOptions.Client();

        const int requests = 200;
        for (var i = 0; i < requests; i++)
        {
            using var response = await client.GetAsync(proxy.Url("/x"));
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        }

        host.Dispose();
        var traces = receiver.Spans.Select(s => s.TraceId).Distinct().Count();
        Assert.IsTrue(traces is > 50 and < 150, $"expected about {requests / 2} sampled traces, saw {traces}");
        // The ratio is decided once per trace: both spans of a sampled trace are exported together.
        Assert.AreEqual(traces * 2, receiver.Spans.Count);
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task Sampler_ParentBased_FollowsTheCallerAndAppliesTheRatioToRoots()
    {
        using var receiver = new OtlpReceiver();
        using var origin = new TestOrigin();
        using var proxy = await TestProxy.StartAsync("r1", "c1", [origin.Port]);
        var host = TelemetryTestOptions.Start(proxy.Proxy, TelemetryTestOptions.For(receiver, c =>
        {
            c.Traces!.Sampler = "parentbased";
            c.Traces.Ratio = 0;
        }));
        using var client = TelemetryTestOptions.Client();

        const string sampledTrace = "11111111111111111111111111111111";
        const string unsampledTrace = "22222222222222222222222222222222";
        foreach (var (trace, flag) in new[] { (sampledTrace, "01"), (unsampledTrace, "00") })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, proxy.Url("/x"));
            request.Headers.Add("traceparent", $"00-{trace}-{IncomingSpanId}-{flag}");
            using var response = await client.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        }

        using (var root = await client.GetAsync(proxy.Url("/x")))
        {
            Assert.AreEqual(HttpStatusCode.OK, root.StatusCode);
        }

        host.Dispose();
        var traces = receiver.Spans.Select(s => s.TraceId).Distinct().ToList();
        CollectionAssert.AreEqual(new[] { sampledTrace }, traces, "only the caller-sampled trace is recorded; the root had ratio 0");
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task Sampler_ParentBased_DefaultRatioRecordsNewRoots()
    {
        using var receiver = new OtlpReceiver();
        using var origin = new TestOrigin();
        using var proxy = await TestProxy.StartAsync("r1", "c1", [origin.Port]);
        var host = TelemetryTestOptions.Start(proxy.Proxy, TelemetryTestOptions.For(receiver, c => c.Traces!.Sampler = null));
        using var client = TelemetryTestOptions.Client();
        using var response = await client.GetAsync(proxy.Url("/x"));
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        host.Dispose();
        Assert.AreEqual(2, receiver.Spans.Count);
    }

    [TestMethod]
    [Timeout(90_000)]
    public async Task TwoProxies_ShareOneTrace_AsAChainOfSpans()
    {
        using var receiver = new OtlpReceiver();
        using var origin = new TestOrigin();
        using var inner = await TestProxy.StartAsync("inner-route", "inner-cluster", [origin.Port],
            configure: p =>
            {
                p.TracingEnabled = true;
                p.PropagateTraceContext = true;
                // Chained proxies need distinct Via names, otherwise loop detection refuses the hop.
                p.ViaHeaderPseudonym = "twp-inner";
            });
        using var edge = await TestProxy.StartAsync("edge-route", "edge-cluster", [inner.Port]);
        using var host = TelemetryTestOptions.Start(edge.Proxy, TelemetryTestOptions.For(receiver));
        using var client = TelemetryTestOptions.Client();

        using var response = await client.GetAsync(edge.Url("/hop"));
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var spans = await receiver.WaitForSpansAsync(s => s.Count >= 4);
        Assert.AreEqual(4, spans.Count);
        Assert.AreEqual(1, spans.Select(s => s.TraceId).Distinct().Count(), "one trace across both proxies");

        var edgeServer = spans.Single(s => s.Name == "twp.proxy.request" && s.Str("twp.route") == "edge-route");
        var edgeUpstream = spans.Single(s => s.Name == "twp.proxy.upstream" && s.Str("twp.route") == "edge-route");
        var innerServer = spans.Single(s => s.Name == "twp.proxy.request" && s.Str("twp.route") == "inner-route");
        var innerUpstream = spans.Single(s => s.Name == "twp.proxy.upstream" && s.Str("twp.route") == "inner-route");

        Assert.IsTrue(edgeServer.IsRoot);
        Assert.AreEqual(edgeServer.SpanId, edgeUpstream.ParentSpanId);
        Assert.AreEqual(edgeUpstream.SpanId, innerServer.ParentSpanId);
        Assert.AreEqual(innerServer.SpanId, innerUpstream.ParentSpanId);
        Assert.AreEqual($"00-{edgeServer.TraceId}-{innerUpstream.SpanId}-01", origin.Requests.Single().Header("traceparent"));
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task ExporterDown_NeverDelaysOrFailsARequest_AndCountsTheLostSpans()
    {
        using var origin = new TestOrigin();
        using var proxy = await TestProxy.StartAsync("r1", "c1", [origin.Port]);
        var closedPort = Ports.Free();
        var options = TelemetryOptions.Resolve(new TelemetryConfig
        {
            Otlp = new OtlpExporterConfig { Endpoint = $"http://127.0.0.1:{closedPort}", Protocol = "http", TimeoutMs = 500 },
            Traces = new TelemetryTracesConfig { Enabled = true, Sampler = "always" },
        }, name => name == "OTEL_BSP_SCHEDULE_DELAY" ? "20" : null) with { ShutdownTimeoutMs = 1500 };
        var host = TelemetryTestOptions.Start(proxy.Proxy, options);
        using var client = TelemetryTestOptions.Client();

        var slowest = TimeSpan.Zero;
        for (var i = 0; i < 20; i++)
        {
            var started = Stopwatch.GetTimestamp();
            using var response = await client.GetAsync(proxy.Url("/x"));
            slowest = TimeSpan.FromTicks(Math.Max(slowest.Ticks, Stopwatch.GetElapsedTime(started).Ticks));
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        }

        Assert.IsTrue(slowest < TimeSpan.FromSeconds(2), $"slowest request took {slowest}");

        var disposing = Stopwatch.StartNew();
        host.Dispose();
        Assert.IsTrue(disposing.Elapsed < TimeSpan.FromSeconds(10), $"shutdown took {disposing.Elapsed}");
        Assert.IsTrue(host.ExportFailed > 0, "spans handed to a dead collector are counted as lost");
    }

    [TestMethod]
    [Timeout(90_000)]
    public async Task QueueOverflow_DropsSpansBoundedly_CountsThem_AndTheProxyKeepsAnswering()
    {
        using var receiver = new OtlpReceiver();
        receiver.Stall();
        using var origin = new TestOrigin();
        using var proxy = await TestProxy.StartAsync("r1", "c1", [origin.Port]);
        var options = TelemetryTestOptions.For(receiver, c => c.Otlp!.TimeoutMs = 300, new Dictionary<string, string>
        {
            ["OTEL_BSP_MAX_QUEUE_SIZE"] = "4",
            ["OTEL_BSP_MAX_EXPORT_BATCH_SIZE"] = "2",
            ["OTEL_BSP_EXPORT_TIMEOUT"] = "300",
        }) with { ShutdownTimeoutMs = 1000 };
        var host = TelemetryTestOptions.Start(proxy.Proxy, options);
        using var client = TelemetryTestOptions.Client();

        var dropped = new Dictionary<string, long>();
        using var meterListener = new MeterListener();
        meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Name == TelemetryInstruments.DroppedName)
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        meterListener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "reason")
                {
                    dropped[(string)tag.Value!] = value;
                }
            }
        });
        meterListener.Start();

        for (var i = 0; i < 60; i++)
        {
            using var response = await client.GetAsync(proxy.Url("/x"));
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, $"request {i}");
        }

        Assert.IsTrue(host.QueueDropped > 0, "a stalled collector must overflow the bounded queue");
        meterListener.RecordObservableInstruments();
        Assert.IsTrue(dropped.GetValueOrDefault("queue_full") > 0, "twp.telemetry.dropped{reason=queue_full} reports the drops");

        receiver.Release();
        host.Dispose();
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task Shutdown_FlushesWhatIsStillQueued()
    {
        using var receiver = new OtlpReceiver();
        using var origin = new TestOrigin();
        using var proxy = await TestProxy.StartAsync("r1", "c1", [origin.Port]);
        var host = TelemetryTestOptions.Start(proxy.Proxy, TelemetryTestOptions.For(
            receiver, env: new Dictionary<string, string> { ["OTEL_BSP_SCHEDULE_DELAY"] = "600000" }));
        using var client = TelemetryTestOptions.Client();

        for (var i = 0; i < 5; i++)
        {
            using var response = await client.GetAsync(proxy.Url("/x"));
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        }

        await Task.Delay(300);
        Assert.AreEqual(0, receiver.Spans.Count, "the schedule delay keeps the batch queued");

        host.Dispose();
        var spans = await receiver.WaitForSpansAsync(s => s.Count >= 10, timeoutMs: 5000);
        Assert.AreEqual(10, spans.Count, "five server spans and five client spans");
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task ExtAuth_GetsItsOwnSpan_AndTheServiceReceivesItsContext()
    {
        using var receiver = new OtlpReceiver();
        using var origin = new TestOrigin();
        using var auth = new TestOrigin();
        var policy = new RoutePolicyConfig
        {
            Name = "auth",
            Kind = "extAuth",
            Url = $"http://127.0.0.1:{auth.Port}/check",
        };
        using var proxy = await TestProxy.StartAsync("r1", "c1", [origin.Port], policies: [policy]);
        using var host = TelemetryTestOptions.Start(proxy.Proxy, TelemetryTestOptions.For(receiver));
        using var client = TelemetryTestOptions.Client();

        using var response = await client.GetAsync(proxy.Url("/secure"));
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var spans = await receiver.WaitForSpansAsync(s => s.Count >= 3);
        var server = Single(spans, "twp.proxy.request");
        var extAuth = Single(spans, "twp.extauth");
        var upstream = Single(spans, "twp.proxy.upstream");

        Assert.AreEqual(3, extAuth.Kind, "SPAN_KIND_CLIENT");
        Assert.AreEqual(server.SpanId, extAuth.ParentSpanId);
        Assert.AreEqual(server.SpanId, upstream.ParentSpanId);
        Assert.AreEqual(1, extAuth.Attributes["twp.extauth.allowed"] is true ? 1 : 0);
        Assert.AreEqual($"00-{server.TraceId}-{extAuth.SpanId}-01", auth.Requests.Single().Header("traceparent"));
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task Retry_IsAnEventOnTheUpstreamSpan()
    {
        using var receiver = new OtlpReceiver();
        using var origin = new TestOrigin(hit => hit == 1 ? (503, "busy") : (200, "ok"));
        using var proxy = await TestProxy.StartAsync(
            "r1", "c1", [origin.Port, origin.Port, origin.Port], retry: new RetryConfig { Attempts = 2 });
        using var host = TelemetryTestOptions.Start(proxy.Proxy, TelemetryTestOptions.For(receiver));
        using var client = TelemetryTestOptions.Client();

        using var response = await client.GetAsync(proxy.Url("/retry"));
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(2, origin.Hits);

        var spans = await receiver.WaitForSpansAsync(s => s.Count >= 2);
        var upstream = Single(spans, "twp.proxy.upstream");
        var retry = upstream.Events.Single(e => e.Name == "twp.retry");
        Assert.AreEqual(1L, retry.Attributes["twp.retry.attempt"]);
        Assert.AreEqual("status", retry.Attributes["twp.retry.reason"]);
        Assert.AreEqual(503L, retry.Attributes["http.response.status_code"]);
        Assert.AreEqual(200, Single(spans, "twp.proxy.request").Int("http.response.status_code"));
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task ServerError_MarksBothSpansAsErrors()
    {
        using var receiver = new OtlpReceiver();
        using var origin = new TestOrigin(_ => (500, "boom"));
        using var proxy = await TestProxy.StartAsync("r1", "c1", [origin.Port]);
        using var host = TelemetryTestOptions.Start(proxy.Proxy, TelemetryTestOptions.For(receiver));
        using var client = TelemetryTestOptions.Client();

        using var response = await client.GetAsync(proxy.Url("/x"));
        Assert.AreEqual(HttpStatusCode.InternalServerError, response.StatusCode);

        var spans = await receiver.WaitForSpansAsync(s => s.Count >= 2);
        foreach (var span in spans)
        {
            Assert.AreEqual(2, span.StatusCode, span.Name + " is STATUS_CODE_ERROR");
            Assert.AreEqual("500", span.Str("error.type"));
        }
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task HeaderAllowList_RecordsListedHeaders_ButNeverCredentials()
    {
        using var receiver = new OtlpReceiver();
        using var origin = new TestOrigin();
        using var proxy = await TestProxy.StartAsync("r1", "c1", [origin.Port]);
        using var host = TelemetryTestOptions.Start(proxy.Proxy, TelemetryTestOptions.For(
            receiver, c => c.Traces!.HeaderAllowList = ["X-Tenant", "Cookie", "Authorization"]));
        using var client = TelemetryTestOptions.Client();

        using var request = new HttpRequestMessage(HttpMethod.Get, proxy.Url("/x"));
        request.Headers.Add("X-Tenant", "acme");
        request.Headers.Add("Cookie", "session=SECRET-COOKIE");
        request.Headers.Add("Authorization", "Bearer SECRET-AUTH");
        using var response = await client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var spans = await receiver.WaitForSpansAsync(s => s.Count >= 2);
        var server = Single(spans, "twp.proxy.request");
        Assert.AreEqual("acme", server.Str("http.request.header.x-tenant"));
        Assert.IsFalse(server.Attributes.ContainsKey("http.request.header.cookie"));
        Assert.IsFalse(server.Attributes.ContainsKey("http.request.header.authorization"));
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task AccessLogLine_CarriesTheTraceIdOfTheServerSpan()
    {
        using var receiver = new OtlpReceiver();
        using var origin = new TestOrigin();
        var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var proxy = await TestProxy.StartAsync("r1", "c1", [origin.Port], configure: p =>
            p.AfterResponse += (_, e) =>
            {
                lines.Enqueue(JsonAccessLogWriter.FormatLine(e));
                return Task.CompletedTask;
            });
        using var host = TelemetryTestOptions.Start(proxy.Proxy, TelemetryTestOptions.For(receiver));
        using var client = TelemetryTestOptions.Client();

        using var response = await client.GetAsync(proxy.Url("/logged"));
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var spans = await receiver.WaitForSpansAsync(s => s.Count >= 2);
        var server = Single(spans, "twp.proxy.request");
        using var line = JsonDocument.Parse(lines.Single());
        Assert.AreEqual(server.TraceId, line.RootElement.GetProperty("traceId").GetString());
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task Metrics_AreExportedOnTheInterval()
    {
        using var receiver = new OtlpReceiver();
        using var origin = new TestOrigin();
        using var proxy = await TestProxy.StartAsync("r1", "c1", [origin.Port]);
        using var host = TelemetryTestOptions.Start(proxy.Proxy, TelemetryTestOptions.For(receiver, c =>
        {
            c.Traces!.Enabled = false;
            c.Metrics = new TelemetryMetricsConfig { Enabled = true, IntervalMs = 200 };
        }));
        using var client = TelemetryTestOptions.Client();

        using var response = await client.GetAsync(proxy.Url("/x"));
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        Assert.IsTrue(await receiver.WaitForMetricAsync("twp.proxy.requests"), "the request counter reaches the collector");
        Assert.AreEqual(0, receiver.TraceRequests, "traces are off");
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task TracingOff_ProducesNoSpansAndLeavesTraceHeadersUntouched()
    {
        using var origin = new TestOrigin();
        using var proxy = await TestProxy.StartAsync("r1", "c1", [origin.Port]);
        using var client = TelemetryTestOptions.Client();

        using var request = new HttpRequestMessage(HttpMethod.Get, proxy.Url("/x"));
        request.Headers.Add("traceparent", $"00-{IncomingTraceId}-{IncomingSpanId}-01");
        using var response = await client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        Assert.AreEqual($"00-{IncomingTraceId}-{IncomingSpanId}-01", origin.Requests.Single().Header("traceparent"));
    }

    [GeneratedRegex("SECRET|token=")]
    private static partial Regex SensitiveExportRegex();

    [GeneratedRegex("^00-[0-9a-f]{32}-[0-9a-f]{16}-00$")]
    private static partial Regex UnsampledTraceParentRegex();
}
