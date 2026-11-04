using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Plus.Observability;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Clusters;

namespace Titanium.Plus.Tests;

/// <summary>The exporter feeds the host's core-meter exposition through <see cref="PrometheusMetricsExporter.AppendTo" />.</summary>
[TestClass]
public class PrometheusAppenderTests
{
    private static ClusterConfig Cluster(string destinationId) => new()
    {
        Id = "c1",
        Destinations = [new DestinationConfig { Id = destinationId, Address = "127.0.0.1", Port = 80 }],
    };

    [TestMethod]
    public async Task AppendTo_AddsDestinationSeriesAfterExistingText()
    {
        var manager = new ClusterManager();
        await manager.ApplyAsync([Cluster("d1")]);
        var sb = new StringBuilder("twp_proxy_requests_total 1\n");

        new PrometheusMetricsExporter(manager, null).AppendTo(sb);

        var text = sb.ToString();
        Assert.IsTrue(text.StartsWith("twp_proxy_requests_total 1\n", StringComparison.Ordinal));
        StringAssert.Contains(text, "titanium_destination_state{id=\"d1\"} 0");
    }

    [TestMethod]
    public async Task Render_AndAppendTo_ProduceTheSameSeries()
    {
        var manager = new ClusterManager();
        await manager.ApplyAsync([Cluster("d1")]);
        var exporter = new PrometheusMetricsExporter(manager, null);
        var sb = new StringBuilder();

        exporter.AppendTo(sb);
        var rendered = exporter.Render();

        // The scrape counter differs by one between calls; every other line is identical.
        var appended = sb.ToString().Split('\n').Where(line => !line.StartsWith("titanium_plus_scrapes_total ", StringComparison.Ordinal));
        var direct = rendered.Split('\n').Where(line => !line.StartsWith("titanium_plus_scrapes_total ", StringComparison.Ordinal));
        CollectionAssert.AreEqual(appended.ToList(), direct.ToList());
    }

    [TestMethod]
    public async Task Scrape_WhileClustersReload_NeverThrowsAndSeesAWholeSnapshot()
    {
        var manager = new ClusterManager();
        await manager.ApplyAsync([Cluster("d0")]);
        var exporter = new PrometheusMetricsExporter(manager, null);
        using var stop = new CancellationTokenSource();

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scraper = Task.Run(() =>
        {
            var scrapes = 0;
            while (!stop.IsCancellationRequested)
            {
                var sb = new StringBuilder();
                exporter.AppendTo(sb);
                var states = sb.ToString().Split('\n').Count(line => line.StartsWith("titanium_destination_state{", StringComparison.Ordinal));
                Assert.AreEqual(1, states, "each snapshot has exactly one destination");
                scrapes++;
                if (scrapes == 1)
                {
                    started.TrySetResult();
                }
            }

            return scrapes;
        });

        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        for (var i = 1; i <= 200; i++)
        {
            await manager.ApplyAsync([Cluster("d" + i)]);
        }

        await stop.CancelAsync();
        Assert.IsTrue(await scraper > 0);
    }
}
