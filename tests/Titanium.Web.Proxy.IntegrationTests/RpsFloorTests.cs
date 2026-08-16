using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
/// Loose reverse-proxy RPS smoke floor — not the publishable saturation number.
/// Headline RPS comes from tools/RpsLoadProbe (and optional nginx control arm).
/// </summary>
[TestClass]
[TestCategory("Slow")]
public class RpsFloorTests
{
    private const int Concurrency = 32;
    private const int MeasureSeconds = 5;
    private const double MinRpsFloor = 200;

    [TestMethod]
    [Timeout(2 * 60 * 1000)]
    public async Task Reverse_Proxy_Sustains_Loose_Rps_Floor()
    {
        using var testSuite = new TestSuite();

        var server = testSuite.GetServer();
        server.HandleRequest(context => context.Response.WriteAsync("ok"));

        var proxy = testSuite.GetReverseProxy();
        proxy.BeforeRequest += (_, e) =>
        {
            e.HttpClient.Request.Url = server.ListeningHttpUrl;
            return Task.CompletedTask;
        };

        var target = new Uri($"http://127.0.0.1:{proxy.ProxyEndPoints[0].Port}/");
        using var client = testSuite.GetReverseProxyClient();

        // Brief warmup so the first measured second is not cold-start dominated.
        for (var i = 0; i < 32; i++)
        {
            using var warmup = await client.GetAsync(target);
            Assert.AreEqual(HttpStatusCode.OK, warmup.StatusCode);
        }

        var ok = 0L;
        var errors = 0L;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(MeasureSeconds));
        var sw = Stopwatch.StartNew();

        var workers = new List<Task>(Concurrency);
        for (var i = 0; i < Concurrency; i++)
        {
            workers.Add(Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    try
                    {
                        using var response = await client.GetAsync(target, cts.Token);
                        if (response.IsSuccessStatusCode)
                            Interlocked.Increment(ref ok);
                        else
                            Interlocked.Increment(ref errors);
                    }
                    catch (OperationCanceledException) when (cts.IsCancellationRequested)
                    {
                        break;
                    }
                    catch
                    {
                        Interlocked.Increment(ref errors);
                    }
                }
            }));
        }

        await Task.WhenAll(workers);
        sw.Stop();

        var elapsed = Math.Max(sw.Elapsed.TotalSeconds, 0.001);
        var rps = ok / elapsed;

        Assert.AreEqual(0, errors, $"Expected zero faults; got {errors} errors ({ok} ok).");
        Assert.IsTrue(rps >= MinRpsFloor,
            $"Expected at least {MinRpsFloor} RPS over {MeasureSeconds}s; got {rps:F1} RPS ({ok} ok). " +
            "This is a catastrophic-regression floor, not the published saturation number.");
    }
}
