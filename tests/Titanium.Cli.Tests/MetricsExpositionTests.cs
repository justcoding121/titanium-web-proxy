using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Cli.Admin;
using Titanium.Cli.Config;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.Caching;
using Titanium.Web.Proxy.Clusters;

namespace Titanium.Cli.Tests;

/// <summary>
///     A private <see cref="Meter" /> that mirrors the production instrument names, units, and tags, so each test
///     gets isolated state without touching the process-wide <c>Titanium.Web.Proxy</c> meter.
/// </summary>
internal sealed class MirrorMeter : IDisposable
{
    public MirrorMeter()
    {
        Name = "Titanium.Web.Proxy.Test." + Guid.NewGuid().ToString("N");
        Meter = new Meter(Name);
        Requests = Meter.CreateCounter<long>("twp.proxy.requests", "{request}", "Completed proxy requests.");
        Duration = Meter.CreateHistogram<double>("twp.proxy.request.duration", "s", "Proxy request duration.");
        Active = Meter.CreateUpDownCounter<long>("twp.proxy.requests.active", "{request}", "Requests in flight.");
        Failed = Meter.CreateCounter<long>("twp.proxy.requests.failed", "{request}", "Failed proxy requests.");
        Bytes = Meter.CreateCounter<long>("twp.proxy.bytes", "By", "Bytes observed on a proxied request.");
        Pool = Meter.CreateCounter<long>("twp.pool.outcomes", "{connection}", "Upstream connection pool outcomes.");
        NotSelected = Meter.CreateHistogram<long>("twp.body.buffered_bytes", "By", "Not part of the selected families.");
        Cached = Meter.CreateObservableGauge("twp.certificates.cached", () => CachedCertificates, "{certificate}", "Cached leaf certificates.");
    }

    public string Name { get; }
    public Meter Meter { get; }
    public Counter<long> Requests { get; }
    public Histogram<double> Duration { get; }
    public UpDownCounter<long> Active { get; }
    public Counter<long> Failed { get; }
    public Counter<long> Bytes { get; }
    public Counter<long> Pool { get; }
    public Histogram<long> NotSelected { get; }
    public ObservableGauge<long> Cached { get; }
    public long CachedCertificates { get; set; }

    public void Request(string route, string cluster, string statusClass, string protocol, double seconds = 0)
    {
        Requests.Add(1,
            new KeyValuePair<string, object?>("route", route),
            new KeyValuePair<string, object?>("cluster", cluster),
            new KeyValuePair<string, object?>("status_class", statusClass),
            new KeyValuePair<string, object?>("protocol", protocol));
        if (seconds > 0)
        {
            Duration.Record(seconds,
                new KeyValuePair<string, object?>("route", route),
                new KeyValuePair<string, object?>("status_class", statusClass));
        }
    }

    public void Dispose() => Meter.Dispose();
}

[TestClass]
public class MetricsExpositionTests
{
    private string _tempDir = null!;

    [TestInitialize]
    public void Init()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "twp-cli-metrics-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            Directory.Delete(_tempDir, true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    [TestMethod]
    public void Render_MatchesGoldenFile()
    {
        using var meter = new MirrorMeter();
        using var exposition = new MetricsExposition(meter.Name);
        exposition.Start();

        for (var i = 0; i < 3; i++)
        {
            meter.Request("api", "c1", "2xx", "http/1.1");
        }

        meter.Request("api", "c1", "5xx", "h2");
        meter.Request("web", "c2", "2xx", "h2");
        meter.Request("web", "c2", "2xx", "h2");
        foreach (var seconds in new[] { 0.0004, 0.004, 0.3, 0.3 })
        {
            meter.Duration.Record(seconds,
                new KeyValuePair<string, object?>("route", "api"),
                new KeyValuePair<string, object?>("status_class", "2xx"));
        }

        meter.Duration.Record(31,
            new KeyValuePair<string, object?>("route", "web"),
            new KeyValuePair<string, object?>("status_class", "2xx"));
        meter.Active.Add(2);
        meter.Active.Add(-1);
        meter.Failed.Add(2, new KeyValuePair<string, object?>("error", "Timeout"));
        meter.Bytes.Add(100, new KeyValuePair<string, object?>("direction", "in"));
        meter.Bytes.Add(50, new KeyValuePair<string, object?>("direction", "in"));
        meter.Bytes.Add(300, new KeyValuePair<string, object?>("direction", "out"));
        meter.Pool.Add(5, new KeyValuePair<string, object?>("outcome", "reuse"));
        meter.Pool.Add(1, new KeyValuePair<string, object?>("outcome", "retry"));
        meter.CachedCertificates = 7;

        // Tags outside the allow-list must never become labels, and unselected families never appear.
        meter.Requests.Add(1,
            new KeyValuePair<string, object?>("route", "api"),
            new KeyValuePair<string, object?>("cluster", "c1"),
            new KeyValuePair<string, object?>("status_class", "2xx"),
            new KeyValuePair<string, object?>("protocol", "http/1.1"),
            new KeyValuePair<string, object?>("client_ip", "10.0.0.1"),
            new KeyValuePair<string, object?>("path", "/secret"),
            new KeyValuePair<string, object?>("host", "example.test"));
        meter.NotSelected.Record(10);

        exposition.AddAppender(sb => sb
            .Append("# TYPE titanium_destination_state gauge\n")
            .Append("titanium_destination_state{id=\"d1\"} 0\n"));

        var actual = exposition.Render().Replace("\r\n", "\n");
        AssertGolden("prometheus-core.txt", actual);
        Lacks(actual, "10.0.0.1");
        Lacks(actual, "/secret");
        Lacks(actual, "example.test");
        Lacks(actual, "twp_body");
    }

    [TestMethod]
    public void Histogram_UsesCumulativeBucketsWithFixedBoundaries()
    {
        using var meter = new MirrorMeter();
        using var exposition = new MetricsExposition(meter.Name);
        exposition.Start();

        // Boundaries are inclusive upper bounds (le): 0.001 lands in the first bucket, 30 in the last finite one.
        foreach (var seconds in new[] { 0.0005, 0.001, 0.0011, 0.2, 30, 30.5, 120 })
        {
            meter.Duration.Record(seconds, new KeyValuePair<string, object?>("route", "r"));
        }

        var text = exposition.Render();
        var expected = new (string Le, int Count)[]
        {
            ("0.001", 2), ("0.0025", 3), ("0.005", 3), ("0.01", 3), ("0.025", 3), ("0.05", 3), ("0.1", 3),
            ("0.25", 4), ("0.5", 4), ("1", 4), ("2.5", 4), ("5", 4), ("10", 4), ("30", 5), ("+Inf", 7),
        };
        Assert.AreEqual(15, expected.Length);
        foreach (var (le, count) in expected)
        {
            Assert.AreEqual(
                count.ToString(CultureInfo.InvariantCulture),
                SampleValue(text, $"twp_proxy_request_duration_seconds_bucket{{route=\"r\",le=\"{le}\"}}"),
                $"le={le}");
        }

        Assert.AreEqual("7", SampleValue(text, "twp_proxy_request_duration_seconds_count{route=\"r\"}"));
        var sum = double.Parse(SampleValue(text, "twp_proxy_request_duration_seconds_sum{route=\"r\"}"), CultureInfo.InvariantCulture);
        Assert.AreEqual(0.0005 + 0.001 + 0.0011 + 0.2 + 30 + 30.5 + 120, sum, 1e-9);
        Has(text, "# TYPE twp_proxy_request_duration_seconds histogram");
    }

    [TestMethod]
    public void Names_MapDotsAndUnitsAndCounterSuffix()
    {
        using var meter = new MirrorMeter();
        using var exposition = new MetricsExposition(meter.Name);
        exposition.Start();
        meter.Request("r", "c", "2xx", "h2", 0.01);
        meter.Active.Add(1);
        meter.Failed.Add(1, new KeyValuePair<string, object?>("error", "Timeout"));
        meter.Bytes.Add(5, new KeyValuePair<string, object?>("direction", "in"));
        meter.CachedCertificates = 3;

        var text = exposition.Render();
        Has(text, "# TYPE twp_proxy_requests_total counter");
        Has(text, "# TYPE twp_proxy_requests_active gauge");
        Has(text, "# TYPE twp_proxy_requests_failed_total counter");
        Has(text, "# TYPE twp_proxy_bytes_total counter");
        Has(text, "# TYPE twp_certificates_cached gauge");
        Has(text, "# TYPE twp_proxy_request_duration_seconds histogram");
        Assert.AreEqual("3", SampleValue(text, "twp_certificates_cached"));
        Assert.AreEqual("1", SampleValue(text, "twp_proxy_requests_active"));
    }

    [TestMethod]
    public void CardinalityGuard_FoldsRoutesAndClustersPastTwoHundredIntoOther()
    {
        using var meter = new MirrorMeter();
        using var exposition = new MetricsExposition(meter.Name);
        exposition.Start();

        for (var i = 0; i < 250; i++)
        {
            meter.Request("route-" + i, "cluster-" + i, "2xx", "h2");
        }

        // Admitted values keep counting under their own label after the cap is hit.
        meter.Request("route-0", "cluster-0", "2xx", "h2");
        meter.Request("route-249", "cluster-249", "2xx", "h2");

        var text = exposition.Render();
        var series = text.Split('\n').Where(line => line.StartsWith("twp_proxy_requests_total{", StringComparison.Ordinal)).ToList();
        var routes = series.Select(line => LabelValue(line, "route")).ToList();
        var clusters = series.Select(line => LabelValue(line, "cluster")).ToList();

        Assert.AreEqual(200, routes.Count(r => r != "other"));
        Assert.AreEqual(200, clusters.Count(c => c != "other"));
        Assert.AreEqual(1, series.Count(line => line.Contains("route=\"other\"", StringComparison.Ordinal)));
        Assert.AreEqual("2", SampleValue(text, "twp_proxy_requests_total{route=\"route-0\",cluster=\"cluster-0\",status_class=\"2xx\",protocol=\"h2\"}"));
        // 50 rejected routes plus the extra hit on route-249, which was itself folded.
        Assert.AreEqual("51", SampleValue(text, "twp_proxy_requests_total{route=\"other\",cluster=\"other\",status_class=\"2xx\",protocol=\"h2\"}"));
        Assert.AreEqual(252, series.Sum(line => int.Parse(line[(line.LastIndexOf(' ') + 1)..], CultureInfo.InvariantCulture)));
    }

    [TestMethod]
    public void Scrape_WithNoTraffic_IsValidAndStartsLazily()
    {
        using var meter = new MirrorMeter();
        using var exposition = new MetricsExposition(meter.Name);
        Assert.IsFalse(exposition.IsListening);
        Assert.IsFalse(meter.Requests.Enabled, "no listener means the instrument stays disabled");

        var text = exposition.Render();

        Assert.IsTrue(exposition.IsListening);
        Has(text, "# TYPE twp_proxy_requests_total counter");
        Lacks(text, "twp_proxy_requests_total{");
        Lacks(text, "twp_body");
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            Assert.IsTrue(
                line.StartsWith('#') || line.Split(' ').Length == 2,
                $"unexpected exposition line: {line}");
        }

        Assert.AreEqual(text, exposition.Render(), "empty scrapes are stable");
    }

    [TestMethod]
    public void Listener_EnablesOnlySelectedFamilies()
    {
        using var meter = new MirrorMeter();
        using var exposition = new MetricsExposition(meter.Name);
        exposition.Start();

        Assert.IsTrue(meter.Requests.Enabled);
        Assert.IsTrue(meter.Duration.Enabled);
        Assert.IsTrue(meter.Pool.Enabled);
        Assert.IsTrue(meter.Cached.Enabled);
        Assert.IsFalse(meter.NotSelected.Enabled, "twp.body.* is outside the selected families");
        Assert.IsTrue(MetricsListener.IsSelected("twp.connections.active"));
        Assert.IsFalse(MetricsListener.IsSelected("twp.http2.capability_lookups"));
    }

    [TestMethod]
    public void Listener_IgnoresAnotherMeterWithTheSameInstrumentNames()
    {
        using var mine = new MirrorMeter();
        using var other = new MirrorMeter();
        using var exposition = new MetricsExposition(mine.Name);
        exposition.Start();

        other.Request("other-route", "c", "2xx", "h2");

        Assert.IsFalse(other.Requests.Enabled);
        Lacks(exposition.Render(), "other-route");
    }

    [TestMethod]
    public void Scrape_DuringRecordingAndAppenderChanges_NeverFailsAndCountsNeverGoBackwards()
    {
        using var meter = new MirrorMeter();
        using var exposition = new MetricsExposition(meter.Name);
        using var stop = new CancellationTokenSource();

        var writers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                meter.Request("r", "c", "2xx", "h2", 0.01);
            }
        })).ToArray();

        long previous = 0;
        for (var i = 0; i < 200; i++)
        {
            if (i % 20 == 0)
            {
                exposition.AddAppender(sb => sb.Append("# swapped\n"));
            }

            var text = exposition.Render();
            if (TrySampleValue(text, "twp_proxy_requests_total{route=\"r\",cluster=\"c\",status_class=\"2xx\",protocol=\"h2\"}", out var raw))
            {
                var value = long.Parse(raw, CultureInfo.InvariantCulture);
                Assert.IsTrue(value >= previous, $"count went backwards: {previous} -> {value}");
                previous = value;
            }
        }

        stop.Cancel();
        Task.WaitAll(writers);
        Assert.IsTrue(previous > 0);
    }

    [TestMethod]
    public async Task Scrape_DuringConfigReload_StaysConsistent()
    {
        var path = Path.Combine(_tempDir, "reload.json");
        WriteConfig(path, "r-a", 19001);
        using var proxy = new ProxyServer(userTrustRootCertificate: false);
        var clusterManager = new ClusterManager();
        var routes = new List<Titanium.Web.Proxy.Abstractions.Routing.RouteConfig>();
        using var meter = new MirrorMeter();
        using var exposition = new MetricsExposition(meter.Name);
        exposition.AddAppender(sb => sb.Append("# destinations ").Append(clusterManager.Snapshot.Clusters.Count).Append('\n'));
        exposition.Start();
        using var stop = new CancellationTokenSource();

        var recorder = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                meter.Request("r-a", "c1", "2xx", "h2", 0.002);
            }
        });
        var scraper = Task.Run(() =>
        {
            var scrapes = 0;
            while (!stop.IsCancellationRequested)
            {
                var text = exposition.Render();
                Has(text, "# TYPE twp_proxy_requests_total counter");
                scrapes++;
            }

            return scrapes;
        });

        for (var i = 0; i < 20; i++)
        {
            WriteConfig(path, i % 2 == 0 ? "r-b" : "r-a", 19000 + i);
            await RunCommand.ReloadConfigAsync(
                path,
                proxy,
                clusterManager,
                routes,
                middleware: [],
                loadBalancer: new LoadBalancer(),
                responseCache: new MemoryHttpResponseCache(),
                plusOptions: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                getGrpc: static () => null,
                setGrpc: static _ => { },
                refreshReverseProxy: static () => { });
        }

        stop.Cancel();
        await recorder;
        Assert.IsTrue(await scraper > 0);
        Has(exposition.Render(), "# destinations 1");
    }

    [TestMethod]
    public async Task HttpEndpoint_ServesMetricsAndRejectsOtherPaths()
    {
        using var meter = new MirrorMeter();
        using var exposition = new MetricsExposition(meter.Name);
        using var endpoint = new MetricsHttpEndpoint(exposition, IPAddress.Loopback, 0);
        endpoint.Start();
        meter.Request("api", "c1", "2xx", "h2");

        var ok = await RawGetAsync(endpoint.Port, "GET /metrics HTTP/1.1");
        StringAssert.StartsWith(ok, "HTTP/1.1 200 OK");
        Has(ok, "Content-Type: text/plain; version=0.0.4");
        Has(ok, "twp_proxy_requests_total{route=\"api\"");

        StringAssert.StartsWith(await RawGetAsync(endpoint.Port, "GET /other HTTP/1.1"), "HTTP/1.1 404");
        StringAssert.StartsWith(await RawGetAsync(endpoint.Port, "POST /metrics HTTP/1.1"), "HTTP/1.1 405");
        var head = await RawGetAsync(endpoint.Port, "HEAD /metrics HTTP/1.1");
        StringAssert.StartsWith(head, "HTTP/1.1 200 OK");
        Lacks(head, "twp_proxy_requests_total");
    }

    [TestMethod]
    public void AppenderFailure_DoesNotTakeCoreSeriesDown()
    {
        using var meter = new MirrorMeter();
        using var exposition = new MetricsExposition(meter.Name);
        meter.CachedCertificates = 1;
        exposition.AddAppender(_ => throw new InvalidOperationException("boom"));
        exposition.AddAppender(sb => sb.Append("after 1\n"));

        var text = exposition.Render();

        Has(text, "twp_certificates_cached 1");
        Has(text, "# appender failed: InvalidOperationException");
        Has(text, "after 1");
    }

    private static void Has(string text, string needle) =>
        Assert.IsTrue(text.Contains(needle, StringComparison.Ordinal), $"expected to find: {needle}");

    private static void Lacks(string text, string needle) =>
        Assert.IsFalse(text.Contains(needle, StringComparison.Ordinal), $"unexpected: {needle}");

    private static void WriteConfig(string path, string routeId, int port) => File.WriteAllText(path, $$"""
        {
          "schemaVersion": "7.0",
          "listeners": [ { "host": "127.0.0.1", "port": 1, "decryptSsl": false } ],
          "routes": [
            { "id": "{{routeId}}", "clusterId": "c1", "order": 1, "match": { "path": "/", "pathKind": "Prefix" } }
          ],
          "clusters": [
            { "id": "c1", "algorithm": "RoundRobin", "destinations": [ { "id": "d1", "address": "127.0.0.1", "port": {{port}} } ] }
          ]
        }
        """);

    private static async Task<string> RawGetAsync(int port, string requestLine)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"{requestLine}\r\nHost: localhost\r\n\r\n"));
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    private static bool TrySampleValue(string text, string series, out string value)
    {
        foreach (var line in text.Split('\n'))
        {
            if (line.StartsWith(series + " ", StringComparison.Ordinal))
            {
                value = line[(series.Length + 1)..];
                return true;
            }
        }

        value = string.Empty;
        return false;
    }

    private static string SampleValue(string text, string series)
    {
        Assert.IsTrue(TrySampleValue(text, series, out var value), $"missing series {series}");
        return value;
    }

    private static string LabelValue(string line, string label)
    {
        var start = line.IndexOf(label + "=\"", StringComparison.Ordinal) + label.Length + 2;
        return line[start..line.IndexOf('"', start)];
    }

    internal static void AssertGolden(string fileName, string actual)
    {
        var sourcePath = FindSourceGolden(fileName);
        if (string.Equals(Environment.GetEnvironmentVariable("UPDATE_TWP_METRICS_GOLDEN"), "1", StringComparison.Ordinal))
        {
            File.WriteAllText(sourcePath, actual);
        }

        var expected = File.ReadAllText(sourcePath).Replace("\r\n", "\n");
        Assert.AreEqual(expected, actual, $"Golden file {fileName} differs. Re-run with UPDATE_TWP_METRICS_GOLDEN=1 to refresh it.");
    }

    private static string FindSourceGolden(string fileName)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "tests", "Titanium.Cli.Tests", "Golden", fileName);
            if (Directory.Exists(Path.GetDirectoryName(candidate)))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("Could not locate tests/Titanium.Cli.Tests/Golden from " + AppContext.BaseDirectory);
    }
}
