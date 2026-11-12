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
///     Fat <c>Http3OriginBridge</c> H3→H2 fast path: request headers survive the bridge.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http3OriginBridgeH2AcceptDatetimeFatGapTests
{
    private static void RequireQuic()
    {
        if (!QuicListener.IsSupported || !QuicConnection.IsSupported)
            Assert.Inconclusive("MsQuic / System.Net.Quic is not supported on this platform.");
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task H3ToH2_AcceptDatetime_ReachesOrigin()
    {
        RequireQuic();

        string? seen = null;
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        var uri = new Uri(origin.Url);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            while (true)
            {
                var (streamId, headers, _) = await conn.ReadRequestAsync();
                seen = headers.FirstOrDefault(h => h.Name == "accept-datetime").Value;
                var resp = conn.EncodeHeaders([(":status", "204")], []);
                await conn.WriteHeaderBlockAsync(streamId, resp, endStream: true);
            }
        });

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

        try
        {
            await using var client = await QuicHttp3Client.ConnectAsync(
                new IPEndPoint(IPAddress.Loopback, quicEp.Port), "localhost");
            var response = await client.SendAsync(
                "GET",
                $"{uri.Host}:{uri.Port}",
                "/adt",
                extraRequestHeaders: [("accept-datetime", "Mon, 09 Nov 2026 16:00:00 GMT")]);
            Assert.AreEqual(204, response.StatusCode, response.TextBody);
            Assert.IsTrue(seen?.Contains("Nov 2026", StringComparison.Ordinal), seen);
        }
        finally
        {
            proxy.Stop();
            proxy.Dispose();
        }
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task H3ToH2_UpgradeInsecureRequests_ReachesOrigin()
    {
        RequireQuic();

        string? seen = null;
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        var uri = new Uri(origin.Url);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            while (true)
            {
                var (streamId, headers, _) = await conn.ReadRequestAsync();
                seen = headers.FirstOrDefault(h => h.Name == "upgrade-insecure-requests").Value;
                var resp = conn.EncodeHeaders([(":status", "204")], []);
                await conn.WriteHeaderBlockAsync(streamId, resp, endStream: true);
            }
        });

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

        try
        {
            await using var client = await QuicHttp3Client.ConnectAsync(
                new IPEndPoint(IPAddress.Loopback, quicEp.Port), "localhost");
            var response = await client.SendAsync(
                "GET",
                $"{uri.Host}:{uri.Port}",
                "/uisr",
                extraRequestHeaders: [("upgrade-insecure-requests", "1")]);
            Assert.AreEqual(204, response.StatusCode, response.TextBody);
            Assert.AreEqual("1", seen);
        }
        finally
        {
            proxy.Stop();
            proxy.Dispose();
        }
    }

    /// <summary>
    ///     Non-fast-path H3→H2 runs <c>PrepareH2OriginRequestHeaders</c>: strip hop-by-hop, normalize names.
    /// </summary>
    [TestMethod]
    [Timeout(60_000)]
    public async Task H3ToH2_Interception_PrepareH2OriginRequestHeaders_StripsHopByHop()
    {
        RequireQuic();

        var names = new System.Collections.Generic.List<string>();
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        var uri = new Uri(origin.Url);
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
        proxy.AddEndPoint(quicEp);
        proxy.Start();

        try
        {
            await using var client = await QuicHttp3Client.ConnectAsync(
                new IPEndPoint(IPAddress.Loopback, quicEp.Port), "localhost");
            var response = await client.SendAsync(
                "GET",
                $"{uri.Host}:{uri.Port}",
                "/prep",
                extraRequestHeaders:
                [
                    ("Accept-Datetime", "Mon, 09 Nov 2026 20:00:00 GMT"),
                    ("Connection", "keep-alive"),
                    ("TE", "trailers"),
                    ("X-Bridge-Probe", "h3-h2"),
                ]);
            Assert.AreEqual(204, response.StatusCode, response.TextBody);
            Assert.IsFalse(names.Exists(n => string.Equals(n, "connection", StringComparison.OrdinalIgnoreCase)));
            Assert.IsTrue(names.Exists(n => string.Equals(n, "te", StringComparison.OrdinalIgnoreCase)));
            Assert.IsTrue(names.Exists(n => string.Equals(n, "accept-datetime", StringComparison.OrdinalIgnoreCase)));
            Assert.IsTrue(names.Exists(n => string.Equals(n, "x-bridge-probe", StringComparison.OrdinalIgnoreCase)));
        }
        finally
        {
            proxy.Stop();
            proxy.Dispose();
        }
    }
}
