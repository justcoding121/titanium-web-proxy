#pragma warning disable CA1416
#pragma warning disable TWP001
using System.Linq;
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

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Gateway status retry. H1 is covered here; H2 and H3 clients use the same origins.
/// </summary>
[TestClass]
public class StatusRetryTests
{
    [TestMethod]
    [Timeout(30_000)]
    public async Task StatusRetry_ResetsFailureStreak_AfterRetried200()
    {
        var hits = 0;
        await using var origin = new ProtocolMatrixTests.RecordingOrigin((_, _) =>
            Interlocked.Increment(ref hits) == 1 ? (503, "busy") : (200, "recovered"));
        var routes = new[]
        {
            new RouteConfig
            {
                Id = "r",
                ClusterId = "c1",
                Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix },
                Retry = new RetryConfig { Attempts = 1 },
            },
        };
        await using var started = await ProtocolMatrixTests.StartAsync(
            origin.Port, routes, ProtocolMatrixTests.ClientKind.H1Cleartext, destinations: 2);
        using var response = await ProtocolMatrixTests.SendAsync(
            started, ProtocolMatrixTests.ClientKind.H1Cleartext, "/retry");
        var body = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, body);
        Assert.AreEqual(2, hits);

        var manager = started.Server.ReverseProxy!.ClusterManager!;
        Assert.AreEqual(DestinationState.Healthy, manager.GetDestinationState("c1-d0"));
        Assert.AreEqual(DestinationState.Healthy, manager.GetDestinationState("c1-d1"));
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task H2Client_StatusRetry_FailsOverToSecondDestination()
    {
        var hits = 0;
        await using var origin = new ProtocolMatrixTests.RecordingOrigin((_, _) =>
            Interlocked.Increment(ref hits) == 1 ? (503, "busy") : (200, "recovered"));
        var before = 0;
        var after = 0;
        await using var started = await StartRetryProxy(origin.Port, attempts: 1);
        started.Server.BeforeResponse += (_, _) =>
        {
            Interlocked.Increment(ref before);
            return Task.CompletedTask;
        };
        started.Server.AfterResponse += (_, _) =>
        {
            Interlocked.Increment(ref after);
            return Task.CompletedTask;
        };

        using var response = await ProtocolMatrixTests.SendAsync(
            started, ProtocolMatrixTests.ClientKind.H2Tls, "/retry");
        var body = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, body);
        Assert.AreEqual("recovered", body);
        Assert.AreEqual(2, hits);
        Assert.AreEqual(1, before);
        Assert.AreEqual(1, after);
        var manager = started.Server.ReverseProxy!.ClusterManager!;
        Assert.AreEqual(DestinationState.Healthy, manager.GetDestinationState("c1-d0"));
        Assert.AreEqual(DestinationState.Healthy, manager.GetDestinationState("c1-d1"));
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task H2Client_StatusRetry_BothDestinationsFail_ClientSeesOne503()
    {
        var hits = 0;
        await using var origin = new ProtocolMatrixTests.RecordingOrigin((_, _) =>
        {
            Interlocked.Increment(ref hits);
            return (503, "busy");
        });
        await using var started = await StartRetryProxy(origin.Port, attempts: 1);
        using var response = await ProtocolMatrixTests.SendAsync(
            started, ProtocolMatrixTests.ClientKind.H2Tls, "/retry");
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.AreEqual(2, hits);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task H2Client_StatusRetry_NotWhenAttemptsZero()
    {
        var hits = 0;
        await using var origin = new ProtocolMatrixTests.RecordingOrigin((_, _) =>
        {
            Interlocked.Increment(ref hits);
            return (503, "busy");
        });
        await using var started = await StartRetryProxy(origin.Port, attempts: 0);
        using var response = await ProtocolMatrixTests.SendAsync(
            started, ProtocolMatrixTests.ClientKind.H2Tls, "/retry");
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.AreEqual(1, hits);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task H2Client_StatusRetry_NotWhenNoAlternate()
    {
        var hits = 0;
        await using var origin = new ProtocolMatrixTests.RecordingOrigin((_, _) =>
        {
            Interlocked.Increment(ref hits);
            return (503, "busy");
        });
        await using var started = await StartRetryProxy(origin.Port, attempts: 1, destinations: 1);
        using var response = await ProtocolMatrixTests.SendAsync(
            started, ProtocolMatrixTests.ClientKind.H2Tls, "/retry");
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.AreEqual(1, hits);
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task H3Client_StatusRetry_FailsOverToSecondDestination()
    {
        if (!QuicListener.IsSupported || !QuicConnection.IsSupported)
            Assert.Inconclusive("MsQuic / System.Net.Quic is not supported on this platform.");

        var hits = 0;
        await using var origin = new ProtocolMatrixTests.RecordingOrigin((_, _) =>
            Interlocked.Increment(ref hits) == 1 ? (503, "busy") : (200, "recovered"));
        var manager = new ClusterManager();
        await manager.ApplyAsync(
        [
            new ClusterConfig
            {
                Id = "c1",
                Destinations =
                [
                    new DestinationConfig { Id = "c1-d0", Address = "127.0.0.1", Port = origin.Port },
                    new DestinationConfig { Id = "c1-d1", Address = "127.0.0.1", Port = origin.Port },
                ],
            },
        ]);
        using var proxy = new ProxyServer(false, false, false)
        {
            EnableHttp3 = true,
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
            Routes =
            [
                new RouteConfig
                {
                    Id = "r",
                    ClusterId = "c1",
                    Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix },
                    Retry = new RetryConfig { Attempts = 1 },
                },
            ],
            ClusterManager = manager,
            RouteMatcher = new RouteMatcher(),
            LoadBalancer = new LoadBalancer(),
        };
        var quic = new TransparentQuicProxyEndPoint(IPAddress.Loopback, 0)
        {
            GenericCertificate = TestCertificateAuthority.ServerCertificate,
            ForwardHost = "127.0.0.1",
            ForwardPort = origin.Port,
            ForwardCleartext = true,
        };
        quic.BeforeQuicAuthenticate += (_, args) =>
        {
            args.UpstreamHttpProtocol = UpstreamHttpProtocol.Http11;
            args.AllowHttpProtocolTranslation = true;
            return Task.CompletedTask;
        };
        proxy.AddEndPoint(quic);
        proxy.Start();
        try
        {
            await using var client = await QuicHttp3Client.ConnectAsync(
                new IPEndPoint(IPAddress.Loopback, quic.Port), "localhost");
            var response = await client.SendAsync("GET", $"127.0.0.1:{origin.Port}", "/retry");
            Assert.AreEqual(200, response.StatusCode, response.TextBody);
            Assert.AreEqual("recovered", response.TextBody);
            Assert.AreEqual(2, hits);
            Assert.AreEqual(DestinationState.Healthy, manager.GetDestinationState("c1-d0"));
            Assert.AreEqual(DestinationState.Healthy, manager.GetDestinationState("c1-d1"));
        }
        finally
        {
            proxy.Stop();
        }
    }

    private static Task<ProtocolMatrixTests.Started> StartRetryProxy(int originPort, int attempts, int destinations = 2)
    {
        var routes = new[]
        {
            new RouteConfig
            {
                Id = "r",
                ClusterId = "c1",
                Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix },
                Retry = attempts > 0 ? new RetryConfig { Attempts = attempts } : null,
            },
        };
        return ProtocolMatrixTests.StartAsync(
            originPort, routes, ProtocolMatrixTests.ClientKind.H2Tls, destinations: destinations);
    }
}
