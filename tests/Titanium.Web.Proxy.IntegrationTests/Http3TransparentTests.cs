#pragma warning disable CA1416
#pragma warning disable TWP001

using System;
using System.Net;
using System.Net.Quic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     End-to-end HTTP/3 transparent proxy tests (client QUIC → proxy → H3 origin).
///     Gated on <see cref="QuicListener.IsSupported" /> so machines without MsQuic skip cleanly.
/// </summary>
[TestClass]
public class Http3TransparentTests
{
    public TestContext TestContext { get; set; }

    private static void RequireQuic()
    {
        if (!QuicListener.IsSupported || !QuicConnection.IsSupported)
            Assert.Inconclusive("MsQuic / System.Net.Quic is not supported on this platform.");
    }

    private static ProxyServer CreateHttp3Proxy(TransparentQuicProxyEndPoint quicEndPoint)
    {
        var proxy = new ProxyServer(false, false, false)
        {
            EnableHttp3 = true,
            EnableHttpsSvcbDnsDiscovery = false
        };
        proxy.CertificateManager.RootCertificateName = TestCertificateAuthority.RootCertificateName;
        proxy.CertificateManager.RootCertificate = TestCertificateAuthority.RootCertificate;
        proxy.CertificateManager.SaveFakeCertificates = false;
        proxy.ServerCertificateValidationCallback += (_, args) =>
        {
            args.IsValid = TestCertificateAuthority.Validate(args.Certificate, args.SslPolicyErrors);
            return Task.CompletedTask;
        };

        quicEndPoint.BeforeQuicAuthenticate += async (_, args) =>
        {
            // Force H3→H3 so the request hits QuicConnectionPool + Http3OriginBridge.
            args.UpstreamHttpProtocol = UpstreamHttpProtocol.Http3;
            await Task.CompletedTask;
        };

        proxy.AddEndPoint(quicEndPoint);
        proxy.Start();
        return proxy;
    }

    [TestMethod]
    public async Task Get_RoundTrip_ReturnsOriginBody()
    {
        RequireQuic();

        await using var origin = new QuicHttp3OriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleRequest(req =>
        {
            Assert.AreEqual("GET", req.Method);
            Assert.AreEqual("/hello", req.Path);
            return Task.FromResult(new QuicHttp3Response(200, "h3-ok"));
        });

        var quicEp = new TransparentQuicProxyEndPoint(IPAddress.Loopback, 0)
        {
            ForwardHost = "localhost",
            ForwardPort = origin.Port
        };

        using var proxy = CreateHttp3Proxy(quicEp);
        await using var client = await QuicHttp3Client.ConnectAsync(
            new IPEndPoint(IPAddress.Loopback, quicEp.Port), "localhost");

        var response = await client.SendAsync("GET", $"localhost:{origin.Port}", "/hello");

        Assert.AreEqual(200, response.StatusCode, $"body={response.TextBody}; originAccepts={origin.AcceptedConnectionCount}");
        Assert.AreEqual("h3-ok", response.TextBody);
        Assert.IsTrue(origin.AcceptedConnectionCount >= 1);
    }

    [TestMethod]
    public async Task Post_BodyIntegrity_RoundTrips()
    {
        RequireQuic();

        var payload = Encoding.UTF8.GetBytes("post-body-12345");
        byte[]? seen = null;

        await using var origin = new QuicHttp3OriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleRequest(req =>
        {
            seen = req.Body;
            return Task.FromResult(new QuicHttp3Response(200, $"len={req.Body.Length}"));
        });

        var quicEp = new TransparentQuicProxyEndPoint(IPAddress.Loopback, 0)
        {
            ForwardHost = "localhost",
            ForwardPort = origin.Port
        };

        using var proxy = CreateHttp3Proxy(quicEp);
        await using var client = await QuicHttp3Client.ConnectAsync(
            new IPEndPoint(IPAddress.Loopback, quicEp.Port), "localhost");

        var response = await client.SendAsync("POST", $"localhost:{origin.Port}", "/echo", payload);

        Assert.AreEqual(200, response.StatusCode);
        Assert.AreEqual($"len={payload.Length}", response.TextBody);
        CollectionAssert.AreEqual(payload, seen);
    }

    [TestMethod]
    public async Task BeforeRequest_SyntheticOk_DoesNotHitOrigin()
    {
        RequireQuic();

        await using var origin = new QuicHttp3OriginServer(TestCertificateAuthority.ServerCertificate);
        var originHits = 0;
        origin.HandleRequest(_ =>
        {
            Interlocked.Increment(ref originHits);
            return Task.FromResult(new QuicHttp3Response(200, "should-not-run"));
        });

        var quicEp = new TransparentQuicProxyEndPoint(IPAddress.Loopback, 0)
        {
            ForwardHost = "localhost",
            ForwardPort = origin.Port
        };

        using var proxy = CreateHttp3Proxy(quicEp);
        proxy.BeforeRequest += (_, args) =>
        {
            args.Ok("synthetic");
            return Task.CompletedTask;
        };

        await using var client = await QuicHttp3Client.ConnectAsync(
            new IPEndPoint(IPAddress.Loopback, quicEp.Port), "localhost");

        var response = await client.SendAsync("GET", $"localhost:{origin.Port}", "/synth");

        Assert.AreEqual(200, response.StatusCode);
        Assert.AreEqual("synthetic", response.TextBody);
        Assert.AreEqual(0, originHits);
    }

    [TestMethod]
    public async Task ConcurrentStreams_ShareOneClientConnection()
    {
        RequireQuic();

        await using var origin = new QuicHttp3OriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleRequest(req => Task.FromResult(new QuicHttp3Response(200, req.Path)));

        var quicEp = new TransparentQuicProxyEndPoint(IPAddress.Loopback, 0)
        {
            ForwardHost = "localhost",
            ForwardPort = origin.Port
        };

        using var proxy = CreateHttp3Proxy(quicEp);
        await using var client = await QuicHttp3Client.ConnectAsync(
            new IPEndPoint(IPAddress.Loopback, quicEp.Port), "localhost");

        var authority = $"localhost:{origin.Port}";
        var tasks = new[]
        {
            client.SendAsync("GET", authority, "/a"),
            client.SendAsync("GET", authority, "/b"),
            client.SendAsync("GET", authority, "/c")
        };
        var results = await Task.WhenAll(tasks);

        Assert.AreEqual("/a", results[0].TextBody);
        Assert.AreEqual("/b", results[1].TextBody);
        Assert.AreEqual("/c", results[2].TextBody);
    }

    [TestMethod]
    public async Task BeforeQuicAuthenticate_Reject_PreventsHandshakeCompletion()
    {
        RequireQuic();

        await using var origin = new QuicHttp3OriginServer(TestCertificateAuthority.ServerCertificate);
        var quicEp = new TransparentQuicProxyEndPoint(IPAddress.Loopback, 0)
        {
            ForwardHost = "localhost",
            ForwardPort = origin.Port
        };
        quicEp.BeforeQuicAuthenticate += (_, args) =>
        {
            args.Reject();
            return Task.CompletedTask;
        };

        using var proxy = new ProxyServer(false, false, false) { EnableHttp3 = true };
        proxy.CertificateManager.RootCertificateName = TestCertificateAuthority.RootCertificateName;
        proxy.CertificateManager.RootCertificate = TestCertificateAuthority.RootCertificate;
        proxy.CertificateManager.SaveFakeCertificates = false;
        proxy.AddEndPoint(quicEp);
        proxy.Start();

        // Reject cancels the handshake; MsQuic surfaces this as AuthenticationException or QuicException.
        try
        {
            await using var client = await QuicHttp3Client.ConnectAsync(
                new IPEndPoint(IPAddress.Loopback, quicEp.Port), "localhost");
            Assert.Fail("Expected handshake to fail after Reject().");
        }
        catch (Exception ex) when (ex is QuicException or System.Security.Authentication.AuthenticationException)
        {
            // expected
        }
    }

    [TestMethod]
    public async Task StopAsync_DrainsInFlightHttp3Session()
    {
        RequireQuic();

        var releaseOrigin = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var origin = new QuicHttp3OriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleRequest(async _ =>
        {
            await releaseOrigin.Task.WaitAsync(TimeSpan.FromSeconds(10));
            return new QuicHttp3Response(200, "drained");
        });

        var quicEp = new TransparentQuicProxyEndPoint(IPAddress.Loopback, 0)
        {
            ForwardHost = "localhost",
            ForwardPort = origin.Port
        };

        var proxy = CreateHttp3Proxy(quicEp);
        try
        {
            await using var client = await QuicHttp3Client.ConnectAsync(
                new IPEndPoint(IPAddress.Loopback, quicEp.Port), "localhost");

            var responseTask = client.SendAsync("GET", $"localhost:{origin.Port}", "/slow");
            // Give the request a moment to enter the proxy accept/path.
            await Task.Delay(200);
            Assert.IsTrue(proxy.Http3ClientConnectionCount >= 1);

            releaseOrigin.TrySetResult();
            var response = await responseTask.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual("drained", response.TextBody);

            await proxy.StopAsync();
            Assert.AreEqual(0, proxy.Http3ClientConnectionCount);
        }
        finally
        {
            releaseOrigin.TrySetResult();
            proxy.Dispose();
        }
    }
}

#pragma warning restore TWP001
#pragma warning restore CA1416
