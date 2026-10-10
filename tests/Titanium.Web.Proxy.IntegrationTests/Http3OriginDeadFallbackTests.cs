#pragma warning disable CA1416
#pragma warning disable TWP001

using System;
using System.Diagnostics;
using System.Net;
using System.Net.Quic;
using System.Net.Sockets;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Regression-2026-10-07 ledger item 8: <c>TCP fallback after H3 failure also failed for x.bidswitch.net</c>.
///     When the cached H3 capability is stale (nothing answers on UDP) and the TCP origin is dead as well, the
///     bridge must answer with a bounded 502 - never hang - and the stale H3 capability must be evicted so later
///     requests stop trying QUIC first.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http3OriginDeadFallbackTests
{
    [TestMethod]
    [TestCategory("Regression-2026-10-07")]
    [Timeout(90 * 1000)]
    public async Task H3AndTcpBothDead_Returns502_Bounded_AndEvictsStaleH3Capability()
    {
        if (!QuicListener.IsSupported || !QuicConnection.IsSupported)
            Assert.Inconclusive("MsQuic / System.Net.Quic is not supported on this platform.");

        // A port with nothing listening on TCP (refused) and nothing on UDP.
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var deadPort = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        using var testSuite = new TestSuite();
        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableHttp3 = true;
        proxy.EnableHttpsSvcbDnsDiscovery = false;

        var hostAndPort = $"localhost:{deadPort}";
        proxy.Http3OriginCapabilityCache.Set(hostAndPort, int.MinValue, TimeSpan.FromMinutes(5), targetName: null);
        proxy.Http3WarmOrigins.Mark("localhost", deadPort);
        Assert.IsTrue(proxy.Http3OriginCapabilityCache.TryGet(hostAndPort, out _), "precondition: H3 capability cached");

        proxy.BeforeRequest += (_, args) =>
        {
            args.UpstreamHttpProtocol = UpstreamHttpProtocol.Auto;
            return Task.CompletedTask;
        };

        using var client = TestHelper.GetHttp2Client(proxy);
        client.Timeout = TimeSpan.FromSeconds(75);

        var sw = Stopwatch.StartNew();
        using var response = await client.GetAsync($"https://localhost:{deadPort}/dead");
        sw.Stop();

        Assert.AreEqual(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.IsTrue(sw.Elapsed < TimeSpan.FromSeconds(60), $"must be bounded, took {sw.Elapsed}");
        Assert.IsFalse(proxy.Http3OriginCapabilityCache.TryGet(hostAndPort, out _),
            "the stale H3 capability must be evicted after the QUIC attempt failed");
    }
}
