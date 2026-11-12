#pragma warning disable CA1416
#pragma warning disable TWP001

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Quic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Clusters;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Routing;
using Titanium.Web.Proxy.Transforms;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat <c>Http3OriginBridge</c> + <c>ReverseProxySessionDispatch</c> suite: gateway
///     status retry on a transparent QUIC listener when the upstream cluster speaks HTTP/1.1.
/// </summary>
[DoNotParallelize]
[TestClass]
public class ReverseProxyDispatchH3ToH1GatewayRetryFatGapTests
{
    private static void RequireQuic()
    {
        if (!QuicListener.IsSupported || !QuicConnection.IsSupported)
            Assert.Inconclusive("MsQuic / System.Net.Quic is not supported on this platform.");
    }

    /// <summary>
    ///     Bug class: interception-off terminate-lite must still honor route status retry.
    /// </summary>
    [TestMethod]
    [Timeout(60_000)]
    public async Task H3ToH1_ReverseProxy_StatusRetry_ReachesSuccessBody()
    {
        RequireQuic();

        var hits = 0;
        await using var origin = new ProtocolMatrixTests.RecordingOrigin((_, _) =>
            Interlocked.Increment(ref hits) < 3 ? (503, "busy") : (200, "ok-retry"));
        await using var started = await StartRetryBridgeProxyAsync(
            origin.Port,
            new RetryConfig { Attempts = 2 },
            destinations: 3,
            enableHttpInterception: false); // terminate-lite; RetryConfig still forces per-request dispatch

        await using var client = await QuicHttp3Client.ConnectAsync(
            new IPEndPoint(IPAddress.Loopback, started.Quic.Port), "localhost");
        var response = await client.SendAsync("GET", $"127.0.0.1:{origin.Port}", "/retry-off");
        Assert.AreEqual(200, response.StatusCode, response.TextBody);
        Assert.AreEqual("ok-retry", response.TextBody);
        Assert.AreEqual(3, hits);
    }

    private static async Task<Started> StartRetryBridgeProxyAsync(
        int originPort, RetryConfig retry, int destinations, bool enableHttpInterception)
    {
        var manager = new ClusterManager();
        var dests = new List<DestinationConfig>();
        for (var i = 0; i < destinations; i++)
        {
            dests.Add(new DestinationConfig
            {
                Id = "c1-d" + i,
                Address = "127.0.0.1",
                Port = originPort,
            });
        }

        await manager.ApplyAsync([new ClusterConfig { Id = "c1", Destinations = dests }]);
        var routes = new[]
        {
            new RouteConfig
            {
                Id = "r",
                ClusterId = "c1",
                Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix },
                Retry = retry,
            },
        };

        var proxy = new ProxyServer(false, false, false)
        {
            EnableHttp3 = true,
            EnableHttpInterception = enableHttpInterception,
            EnableHttpsSvcbDnsDiscovery = false,
        };
        proxy.CertificateManager.RootCertificateName = TestCertificateAuthority.RootCertificateName;
        proxy.CertificateManager.RootCertificate = TestCertificateAuthority.RootCertificate;
        proxy.CertificateManager.SaveFakeCertificates = false;
        proxy.ServerCertificateValidationCallback += (_, args) =>
        {
            args.IsValid = TestCertificateAuthority.Validate(args.Certificate, args.SslPolicyErrors);
            return Task.CompletedTask;
        };
        proxy.ReverseProxy = new ReverseProxyOptions
        {
            Routes = routes,
            ClusterManager = manager,
            RouteMatcher = new RouteMatcher(),
            LoadBalancer = new LoadBalancer(),
            TransformEngine = new TransformEngine(),
            Clusters =
            [
                new ClusterConfig { Id = "c1", Destinations = dests },
            ],
        };

        var quic = new TransparentQuicProxyEndPoint(IPAddress.Loopback, 0)
        {
            GenericCertificate = TestCertificateAuthority.ServerCertificate,
            ForwardHost = "127.0.0.1",
            ForwardPort = originPort,
            ForwardCleartext = true,
        };
        quic.BeforeQuicAuthenticate += (_, e) =>
        {
            e.UpstreamHttpProtocol = UpstreamHttpProtocol.Http11;
            e.AllowHttpProtocolTranslation = true;
            return Task.CompletedTask;
        };

        proxy.AddEndPoint(quic);
        proxy.Start();
        return new Started(proxy, quic);
    }

    private sealed class Started(ProxyServer server, TransparentQuicProxyEndPoint quic) : IAsyncDisposable
    {
        public TransparentQuicProxyEndPoint Quic { get; } = quic;

        public async ValueTask DisposeAsync()
        {
            if (server.ProxyRunning)
                await server.StopAsync();
            server.Dispose();
        }
    }
}
