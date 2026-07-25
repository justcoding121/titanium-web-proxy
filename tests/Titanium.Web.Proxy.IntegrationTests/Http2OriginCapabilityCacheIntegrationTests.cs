using System;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Verifies that <c>ExplicitClientHandler</c>'s "does the real origin support HTTP/2" probe (needed
///     because Titanium cannot switch a decrypted connection's protocol after the fact, so it must know the
///     origin's ALPN capability before deciding what to offer the client) is only performed once per host,
///     not once per CONNECT tunnel. Real browsers open many short-lived tunnels to the very same host
///     (connection racing/sharding), so without a per-host cache every one of those tunnels would pay for
///     its own redundant probe TLS handshake to the origin.
/// </summary>
[TestClass]
public class Http2OriginCapabilityCacheIntegrationTests
{
    private static X509Certificate2 CreateOriginCertificate()
    {
        return TestCertificateAuthority.ServerCertificate;
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task Http2_Repeated_Tunnels_To_Same_Host_Reuse_The_Cached_Http2_Capability_Probe_Result()
    {
        using var rawServer = new Http2RawOriginServer(CreateOriginCertificate());
        rawServer.HandleConnection(async connection =>
        {
            await connection.SendInitialSettingsAsync();
            var (streamId, _, _) = await connection.ReadRequestAsync();

            var headers = connection.EncodeHeaders(new[] { (":status", "200") }, Array.Empty<(string, string)>());
            await connection.WriteHeaderBlockAsync(streamId, headers, true);
        });

        using var testSuite = new TestSuite();
        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;

        // Isolate this test to the HTTP/2-capability probe path: connection prefetch is a separate,
        // unrelated optimization that would also open extra real connections to the origin regardless of
        // ALPN, which would otherwise make the exact connection-count assertion below misleading.
        proxy.EnableTcpServerConnectionPrefetch = false;

        var uri = new Uri(rawServer.Url);

        async Task<int> SendOneRequestOverANewTunnelAsync()
        {
            using var rawClient =
                await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

            var requestHeaders = rawClient.Connection.EncodeHeaders(
                new[]
                {
                    (":method", "GET"), (":scheme", "https"), (":authority", $"{uri.Host}:{uri.Port}"),
                    (":path", "/")
                },
                Array.Empty<(string, string)>());
            await rawClient.Connection.WriteHeaderBlockAsync(1, requestHeaders, true);

            var (_, responseHeaders, _) = await rawClient.Connection.ReadHeaderBlockAsync();
            return int.Parse(responseHeaders.Single(h => h.Name == ":status").Value);
        }

        var firstStatus = await SendOneRequestOverANewTunnelAsync();
        Assert.AreEqual(200, firstStatus, "The first tunnel's real request did not complete successfully.");

        var secondStatus = await SendOneRequestOverANewTunnelAsync();
        Assert.AreEqual(200, secondStatus, "The second tunnel's real request did not complete successfully.");

        // The first tunnel's cold-cache discovery connection is retained and adopted directly as its
        // session connection (one connection total), and the second tunnel's cache hit needs no discovery
        // connection at all (prefetch is disabled here), so it opens its own single fresh session
        // connection - two real origin connections across both tunnels. Without the cache this would be 3
        // (the first tunnel's single adopted discovery connection, plus a probe *and* a real connection
        // for the second tunnel).
        Assert.AreEqual(2, rawServer.AcceptedConnectionCount,
            "Expected exactly one HTTP/2-capability probe/session connection for the first tunnel and one " +
            "session connection for the second; the second tunnel should have reused the first tunnel's " +
            "cached capability result instead of probing the origin again.");
    }
}
