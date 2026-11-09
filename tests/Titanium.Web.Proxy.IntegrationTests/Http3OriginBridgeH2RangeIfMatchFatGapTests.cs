#pragma warning disable CA1416
#pragma warning disable TWP001

using System;
using System.Linq;
using System.Net;
using System.Net.Quic;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat <c>Http3OriginBridge</c> H3→H2 fast path: conditional and range request headers survive the bridge.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http3OriginBridgeH2RangeIfMatchFatGapTests
{
    private static void RequireQuic()
    {
        if (!QuicListener.IsSupported || !QuicConnection.IsSupported)
            Assert.Inconclusive("MsQuic / System.Net.Quic is not supported on this platform.");
    }

    private static async Task<(ProxyServer proxy, TransparentQuicProxyEndPoint ep, Uri originUri)> StartH2BridgeAsync(
        Http2RawOriginServer origin, bool interception = false)
    {
        var uri = new Uri(origin.Url);
        var quicEp = new TransparentQuicProxyEndPoint(IPAddress.Loopback, 0)
        {
            ForwardHost = uri.Host,
            ForwardPort = uri.Port,
        };
        quicEp.BeforeQuicAuthenticate += (_, args) =>
        {
            args.UpstreamHttpProtocol = UpstreamHttpProtocol.Http2;
            args.AllowHttpProtocolTranslation = true;
            return Task.CompletedTask;
        };

        var proxy = new ProxyServer(false, false, false)
        {
            EnableHttp3 = true,
            EnableHttpInterception = interception,
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
        proxy.AddEndPoint(quicEp);
        proxy.Start();
        await Task.CompletedTask;
        return (proxy, quicEp, uri);
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task H3ToH2_Range_ReachesOrigin()
    {
        RequireQuic();

        string? seen = null;
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            while (true)
            {
                var (streamId, headers, _) = await conn.ReadRequestAsync();
                seen = headers.FirstOrDefault(h => h.Name == "range").Value;
                var resp = conn.EncodeHeaders([(":status", "206"), ("content-range", "bytes 0-4/10")], []);
                await conn.WriteHeaderBlockAsync(streamId, resp, endStream: true);
            }
        });

        var (proxy, quicEp, uri) = await StartH2BridgeAsync(origin);
        try
        {
            await using var client = await QuicHttp3Client.ConnectAsync(
                new IPEndPoint(IPAddress.Loopback, quicEp.Port), "localhost");
            var response = await client.SendAsync(
                "GET",
                $"{uri.Host}:{uri.Port}",
                "/rng",
                extraRequestHeaders: [("range", "bytes=0-4")]);
            Assert.AreEqual(206, response.StatusCode, response.TextBody);
            Assert.AreEqual("bytes=0-4", seen);
        }
        finally
        {
            proxy.Stop();
            proxy.Dispose();
        }
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task H3ToH2_IfMatch_ReachesOrigin()
    {
        RequireQuic();

        string? seen = null;
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            while (true)
            {
                var (streamId, headers, _) = await conn.ReadRequestAsync();
                seen = headers.FirstOrDefault(h => h.Name == "if-match").Value;
                var resp = conn.EncodeHeaders([(":status", "204")], []);
                await conn.WriteHeaderBlockAsync(streamId, resp, endStream: true);
            }
        });

        var (proxy, quicEp, uri) = await StartH2BridgeAsync(origin);
        try
        {
            await using var client = await QuicHttp3Client.ConnectAsync(
                new IPEndPoint(IPAddress.Loopback, quicEp.Port), "localhost");
            var response = await client.SendAsync(
                "GET",
                $"{uri.Host}:{uri.Port}",
                "/ifm",
                extraRequestHeaders: [("if-match", "\"gap-etag\"")]);
            Assert.AreEqual(204, response.StatusCode, response.TextBody);
            Assert.AreEqual("\"gap-etag\"", seen);
        }
        finally
        {
            proxy.Stop();
            proxy.Dispose();
        }
    }

    /// <summary>
    ///     <c>PrepareH2OriginRequestHeaders</c> also strips Proxy-Connection, Keep-Alive, Upgrade, and Transfer-Encoding.
    /// </summary>
    [TestMethod]
    [Timeout(60_000)]
    public async Task H3ToH2_Interception_PrepareH2OriginRequestHeaders_StripsRemainingHopByHop()
    {
        RequireQuic();

        var names = new System.Collections.Generic.List<string>();
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            while (true)
            {
                var (streamId, headers, _) = await conn.ReadRequestAsync();
                names.AddRange(headers.Where(h => !h.Name.StartsWith(':')).Select(h => h.Name));
                var resp = conn.EncodeHeaders([(":status", "204")], []);
                await conn.WriteHeaderBlockAsync(streamId, resp, endStream: true);
            }
        });

        var (proxy, quicEp, uri) = await StartH2BridgeAsync(origin, interception: true);
        try
        {
            await using var client = await QuicHttp3Client.ConnectAsync(
                new IPEndPoint(IPAddress.Loopback, quicEp.Port), "localhost");
            var response = await client.SendAsync(
                "GET",
                $"{uri.Host}:{uri.Port}",
                "/hop",
                extraRequestHeaders:
                [
                    ("Proxy-Connection", "keep-alive"),
                    ("Keep-Alive", "timeout=9"),
                    ("Upgrade", "websocket"),
                    ("Transfer-Encoding", "chunked"),
                    ("X-Probe", "1"),
                ]);
            Assert.AreEqual(204, response.StatusCode, response.TextBody);
            Assert.IsFalse(names.Exists(n => string.Equals(n, "proxy-connection", StringComparison.OrdinalIgnoreCase)));
            Assert.IsFalse(names.Exists(n => string.Equals(n, "keep-alive", StringComparison.OrdinalIgnoreCase)));
            Assert.IsFalse(names.Exists(n => string.Equals(n, "upgrade", StringComparison.OrdinalIgnoreCase)));
            Assert.IsFalse(names.Exists(n => string.Equals(n, "transfer-encoding", StringComparison.OrdinalIgnoreCase)));
            Assert.IsTrue(names.Exists(n => string.Equals(n, "x-probe", StringComparison.OrdinalIgnoreCase)));
        }
        finally
        {
            proxy.Stop();
            proxy.Dispose();
        }
    }
}
