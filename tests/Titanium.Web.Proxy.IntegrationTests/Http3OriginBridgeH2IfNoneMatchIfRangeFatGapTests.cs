#pragma warning disable CA1416
#pragma warning disable TWP001

using System;
using System.Linq;
using System.Net;
using System.Net.Quic;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat <c>Http3OriginBridge</c> H3→H2 fast path: conditional request headers survive the bridge.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http3OriginBridgeH2IfNoneMatchIfRangeFatGapTests
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
    public async Task H3ToH2_IfNoneMatch_ReachesOrigin()
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
                seen = headers.FirstOrDefault(h => h.Name == "if-none-match").Value;
                var resp = conn.EncodeHeaders([(":status", "304")], []);
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
                "/inm",
                extraRequestHeaders: [("if-none-match", "W/\"wave22\"")]);
            Assert.AreEqual(304, response.StatusCode, response.TextBody);
            Assert.AreEqual("W/\"wave22\"", seen);
        }
        finally
        {
            proxy.Stop();
            proxy.Dispose();
        }
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task H3ToH2_IfRange_ReachesOrigin()
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
                seen = headers.FirstOrDefault(h => h.Name == "if-range").Value;
                var resp = conn.EncodeHeaders([(":status", "206"), ("content-range", "bytes 0-0/2")], []);
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
                "/ifr",
                extraRequestHeaders: [("if-range", "\"etag-wave22\"")]);
            Assert.AreEqual(206, response.StatusCode, response.TextBody);
            Assert.AreEqual("\"etag-wave22\"", seen);
        }
        finally
        {
            proxy.Stop();
            proxy.Dispose();
        }
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task H3ToH2_IfUnmodifiedSince_ReachesOrigin()
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
                seen = headers.FirstOrDefault(h => h.Name == "if-unmodified-since").Value;
                var resp = conn.EncodeHeaders([(":status", "412")], []);
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
                "/iums",
                extraRequestHeaders: [("if-unmodified-since", "Mon, 09 Nov 2026 00:00:00 GMT")]);
            Assert.AreEqual(412, response.StatusCode, response.TextBody);
            Assert.AreEqual("Mon, 09 Nov 2026 00:00:00 GMT", seen);
        }
        finally
        {
            proxy.Stop();
            proxy.Dispose();
        }
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task H3ToH2_ContentMd5_OnPost_ReachesOrigin()
    {
        RequireQuic();

        string? seen = null;
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            while (true)
            {
                var (streamId, headers, body) = await conn.ReadRequestAsync();
                if (headers.FirstOrDefault(h => h.Name == ":method").Value is not "POST")
                    continue;
                seen = headers.FirstOrDefault(h => h.Name == "content-md5").Value;
                var payload = Encoding.UTF8.GetBytes($"n={body.Length}");
                var resp = conn.EncodeHeaders(
                    [(":status", "200"), ("content-length", payload.Length.ToString())], []);
                await conn.WriteHeaderBlockAsync(streamId, resp, endStream: false);
                await conn.WriteFrameAsync(Http2FrameType.Data, streamId, Http2FrameFlag.EndStream, payload);
            }
        });

        var (proxy, quicEp, uri) = await StartH2BridgeAsync(origin);
        try
        {
            await using var client = await QuicHttp3Client.ConnectAsync(
                new IPEndPoint(IPAddress.Loopback, quicEp.Port), "localhost");
            var body = Encoding.UTF8.GetBytes("h3-md5");
            var response = await client.SendAsync(
                "POST",
                $"{uri.Host}:{uri.Port}",
                "/md5",
                body,
                extraRequestHeaders: [("content-md5", "QrkBlrSHxUBpCXpo/pirbw==")]);
            Assert.AreEqual(200, response.StatusCode, response.TextBody);
            Assert.AreEqual("QrkBlrSHxUBpCXpo/pirbw==", seen);
            Assert.AreEqual("n=7", response.TextBody);
        }
        finally
        {
            proxy.Stop();
            proxy.Dispose();
        }
    }

    /// <summary>
    ///     <c>PrepareH2OriginRequestHeaders</c> strips the TE hop-by-hop header on the interception path.
    /// </summary>
    [TestMethod]
    [Timeout(60_000)]
    public async Task H3ToH2_Interception_PrepareH2_StripsTeHeader()
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
                "/te",
                extraRequestHeaders: [("te", "trailers"), ("x-te-probe", "1")]);
            Assert.AreEqual(204, response.StatusCode, response.TextBody);
            Assert.IsFalse(names.Exists(n => string.Equals(n, "te", StringComparison.OrdinalIgnoreCase)));
            Assert.IsTrue(names.Exists(n => string.Equals(n, "x-te-probe", StringComparison.OrdinalIgnoreCase)));
        }
        finally
        {
            proxy.Stop();
            proxy.Dispose();
        }
    }
}
