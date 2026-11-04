using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Cli.Admin;
using Titanium.Web.Proxy.Abstractions.Diagnostics;
using Titanium.Web.Proxy.Diagnostics;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Drives the real <see cref="ProxyMetrics.RecordRequest" /> through the Prometheus exposition listener
///     (the exposition sources are linked into this project).
/// </summary>
[TestClass]
[DoNotParallelize]
public class ProxyMetricsExpositionTests
{
    private static readonly string NoError = ForwarderError.None.ToString();
    private static readonly string OtherError = ForwarderError.Other.ToString();

    [TestMethod]
    public void RecordRequest_WithListener_StaysWithinAllocationBudget()
    {
#if DEBUG
        Assert.Inconclusive("Allocation ceilings are enforced in Release.");
#else
        using var exposition = new MetricsExposition();
        exposition.Start();
        var sample = Sample("alloc-route", "alloc-cluster", 200, NoError);
        var failing = Sample("alloc-route", "alloc-cluster", 503, OtherError);
        const int n = 1_000_000;

        // Warm up so every series cell, JIT stub, and tag path exists before measuring.
        for (var i = 0; i < 10_000; i++)
        {
            ProxyMetrics.RecordRequest(i % 2 == 0 ? sample : failing);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < n; i++)
        {
            ProxyMetrics.RecordRequest((i & 1) == 0 ? sample : failing);
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        // Fixed budget for 1M calls across six enabled instruments: nothing per call, only incidental runtime noise.
        Assert.IsTrue(allocated <= 4096, $"1M RecordRequest with a listener allocated {allocated} bytes");
#endif
    }

    [TestMethod]
    public void RecordRequest_WithoutAnyListener_DoesNotAllocate()
    {
#if DEBUG
        Assert.Inconclusive("Allocation ceilings are enforced in Release.");
#else
        var sample = Sample("idle-route", "idle-cluster", 200, NoError);
        for (var i = 0; i < 10_000; i++)
        {
            ProxyMetrics.RecordRequest(sample);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1_000_000; i++)
        {
            ProxyMetrics.RecordRequest(sample);
        }

        Assert.IsTrue(GC.GetAllocatedBytesForCurrentThread() - before <= 0);
#endif
    }

    [TestMethod]
    public void EightThreadsOneMillionRecordsEach_AggregateToExactCounts()
    {
        const int threads = 8;
        const int perThread = 1_000_000;
        using var exposition = new MetricsExposition();
        exposition.Start();

        string[] routes = ["conc-route-0", "conc-route-1"];
        var ok = new[] { Sample(routes[0], "conc", 200, NoError), Sample(routes[1], "conc", 200, NoError) };
        var bad = new[] { Sample(routes[0], "conc", 503, OtherError), Sample(routes[1], "conc", 503, OtherError) };
        using var gate = new Barrier(threads);

        var workers = Enumerable.Range(0, threads).Select(t => Task.Factory.StartNew(() =>
        {
            var okSample = ok[t % 2];
            var badSample = bad[t % 2];
            gate.SignalAndWait();
            for (var i = 0; i < perThread; i++)
            {
                ProxyMetrics.RecordRequest((i & 1) == 0 ? okSample : badSample);
            }
        }, TaskCreationOptions.LongRunning)).ToArray();
        Task.WaitAll(workers);

        var text = exposition.Render();
        const long perRouteHalf = (threads / 2) * (perThread / 2); // 4 threads per route, half the calls each status.
        foreach (var route in routes)
        {
            foreach (var (status, protocolClass) in new[] { ("2xx", "2xx"), ("5xx", "5xx") })
            {
                Assert.AreEqual(perRouteHalf, Value(text, $"twp_proxy_requests_total{{route=\"{route}\",cluster=\"conc\",status_class=\"{protocolClass}\",protocol=\"h1\"}}"), $"{route} {status}");
                var labels = $"route=\"{route}\",status_class=\"{status}\"";
                Assert.AreEqual(perRouteHalf, Value(text, $"twp_proxy_request_duration_seconds_count{{{labels}}}"));
                Assert.AreEqual(perRouteHalf, Value(text, $"twp_proxy_request_duration_seconds_bucket{{{labels},le=\"+Inf\"}}"));
                Assert.AreEqual(0, Value(text, $"twp_proxy_request_duration_seconds_bucket{{{labels},le=\"0.25\"}}"));
                Assert.AreEqual(perRouteHalf, Value(text, $"twp_proxy_request_duration_seconds_bucket{{{labels},le=\"0.5\"}}"));
                // 0.5 is exact in binary, so the lock-free sum must be exact too.
                Assert.AreEqual(perRouteHalf / 2.0, Value(text, $"twp_proxy_request_duration_seconds_sum{{{labels}}}"), 0.0);
            }
        }

        const long total = (long)threads * perThread;
        Assert.AreEqual(total * 10, Value(text, "twp_proxy_bytes_total{direction=\"in\"}"));
        Assert.AreEqual(total * 20, Value(text, "twp_proxy_bytes_total{direction=\"out\"}"));
        Assert.AreEqual(total / 2, Value(text, "twp_proxy_requests_failed_total{error=\"Other\"}"));
    }

    private static double Value(string text, string series)
    {
        foreach (var line in text.Split('\n'))
        {
            if (line.StartsWith(series + " ", StringComparison.Ordinal))
            {
                return double.Parse(line[(series.Length + 1)..], CultureInfo.InvariantCulture);
            }
        }

        Assert.Fail($"missing series {series}");
        return 0;
    }

    private static ProxyMetrics.RequestSample Sample(string route, string cluster, int status, string error) => new()
    {
        Route = route,
        Cluster = cluster,
        Status = status,
        Protocol = "h1",
        Error = error,
        DurationSeconds = 0.5,
        BytesIn = 10,
        BytesOut = 20,
    };
}
