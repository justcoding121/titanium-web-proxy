#pragma warning disable CA1416
#pragma warning disable TWP001

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Quic;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat <c>Http3RequestStream</c> suite: RFC 9114 §4.3 pseudo-header ordering and duplication defects.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http3RequestStreamPseudoHeaderFatGapTests
{
    private static void RequireQuic()
    {
        if (!QuicListener.IsSupported || !QuicConnection.IsSupported)
            Assert.Inconclusive("MsQuic / System.Net.Quic is not supported on this platform.");
    }

    private static ProxyServer CreateH3Proxy(TransparentQuicProxyEndPoint ep)
    {
        var proxy = new ProxyServer(false, false, false)
        {
            EnableHttp3 = true,
            EnableHttpInterception = true,
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
        ep.BeforeQuicAuthenticate += async (_, args) =>
        {
            args.UpstreamHttpProtocol = UpstreamHttpProtocol.Http3;
            await Task.CompletedTask;
        };
        proxy.AddEndPoint(ep);
        proxy.Start();
        return proxy;
    }

    /// <summary>
    ///     Bug class: duplicate <c>:method</c> is resolved last-wins and forwarded to the origin.
    /// </summary>
    [TestMethod]
    [Timeout(60_000)]
    public async Task H3_DuplicateMethod_AbortsWithoutOrigin()
    {
        RequireQuic();
        await using var origin = new QuicHttp3OriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleRequest(_ => Task.FromResult(new QuicHttp3Response(200, "origin")));

        var ep = new TransparentQuicProxyEndPoint(IPAddress.Loopback, 0)
        {
            ForwardHost = "localhost",
            ForwardPort = origin.Port,
        };
        using var proxy = CreateH3Proxy(ep);
        try
        {
            await using var client = await QuicHttp3Client.ConnectAsync(
                new IPEndPoint(IPAddress.Loopback, ep.Port), "localhost");
            var status = await client.TrySendFieldSectionAsync(
            [
                (":method", "GET"), (":method", "POST"),
                (":scheme", "https"),
                (":authority", $"localhost:{origin.Port}"),
                (":path", "/dup-method")
            ]);
            Assert.IsNull(status, "Duplicate :method must reset the stream without a normal response.");
            Assert.AreEqual(0, origin.AcceptedConnectionCount);
        }
        finally
        {
            proxy.Stop();
        }
    }

    /// <summary>
    ///     Bug class: a pseudo-header after a regular field is accepted and forwarded.
    /// </summary>
    [TestMethod]
    [Timeout(60_000)]
    public async Task H3_PseudoHeaderAfterRegularField_AbortsWithoutOrigin()
    {
        RequireQuic();
        await using var origin = new QuicHttp3OriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleRequest(_ => Task.FromResult(new QuicHttp3Response(200, "origin")));

        var ep = new TransparentQuicProxyEndPoint(IPAddress.Loopback, 0)
        {
            ForwardHost = "localhost",
            ForwardPort = origin.Port,
        };
        using var proxy = CreateH3Proxy(ep);
        try
        {
            await using var client = await QuicHttp3Client.ConnectAsync(
                new IPEndPoint(IPAddress.Loopback, ep.Port), "localhost");
            var status = await client.TrySendFieldSectionAsync(
            [
                (":method", "GET"),
                (":scheme", "https"),
                (":authority", $"localhost:{origin.Port}"),
                ("accept", "*/*"),
                (":path", "/late-path")
            ]);
            Assert.IsNull(status, "Pseudo-header after a regular field must reset the stream.");
            Assert.AreEqual(0, origin.AcceptedConnectionCount);
        }
        finally
        {
            proxy.Stop();
        }
    }

    /// <summary>
    ///     Bug class: missing mandatory <c>:authority</c> still reaches the origin.
    /// </summary>
    [TestMethod]
    [Timeout(60_000)]
    public async Task H3_MissingAuthority_AbortsWithoutOrigin()
    {
        RequireQuic();
        await using var origin = new QuicHttp3OriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleRequest(_ => Task.FromResult(new QuicHttp3Response(200, "origin")));

        var ep = new TransparentQuicProxyEndPoint(IPAddress.Loopback, 0)
        {
            ForwardHost = "localhost",
            ForwardPort = origin.Port,
        };
        using var proxy = CreateH3Proxy(ep);
        try
        {
            await using var client = await QuicHttp3Client.ConnectAsync(
                new IPEndPoint(IPAddress.Loopback, ep.Port), "localhost");
            var status = await client.TrySendFieldSectionAsync(
            [
                (":method", "GET"),
                (":scheme", "https"),
                (":path", "/no-authority")
            ]);
            Assert.IsNull(status, "Missing :authority must reset the stream.");
            Assert.AreEqual(0, origin.AcceptedConnectionCount);
        }
        finally
        {
            proxy.Stop();
        }
    }
}
#pragma warning restore TWP001
#pragma warning restore CA1416
