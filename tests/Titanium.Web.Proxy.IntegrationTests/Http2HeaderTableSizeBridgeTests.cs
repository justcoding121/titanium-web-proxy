using System;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Regression test for a real-world HTTP/2 bug (browser: <c>ERR_HTTP2_PROTOCOL_ERROR</c>; proxy log:
///     "Failed to decode HTTP/2 headers: invalid max dynamic table size" followed by cascading
///     "HTTP/2 stream error" resets) seen browsing sites like https://www.google.com/ through the proxy with
///     TLS decryption on. <see cref="Http2Helper.CopyHttp2FrameAsync" />'s <c>ProcessCompleteHeaderBlockAsync</c>
///     sized the decoder for headers arriving from one peer using *that peer's own* self-declared
///     SETTINGS_HEADER_TABLE_SIZE, instead of the value forwarded to it (transparently, by this same relay)
///     from the *other* peer - which is what that peer's real encoder is actually bound by (RFC 7540 §6.5.2).
///     A local Kestrel <c>TestServer</c> and .NET's own HTTP/2 client both only ever use the RFC default
///     (4096) for this setting, so the divergence - and the bug - never surfaced against those; a real
///     browser (which typically advertises a larger value, e.g. Chrome's 65536) does trigger it against any
///     real origin that grows its dynamic table in response. Reproduced deterministically here with
///     <see cref="Http2RawClient" />/<see cref="Http2RawOriginServer" /> declaring exactly that divergence.
/// </summary>
[TestClass]
public class Http2HeaderTableSizeBridgeTests
{
    private const int ClientDeclaredHeaderTableSize = 65536;

    private static X509Certificate2 CreateOriginCertificate()
    {
        return TestCertificateAuthority.ServerCertificate;
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task Http2_Origin_Response_Growing_Dynamic_Table_To_ClientDeclared_Size_Decodes_Correctly()
    {
        using var rawServer = new Http2RawOriginServer(CreateOriginCertificate());
        rawServer.HandleConnection(async connection =>
        {
            // A real origin server never declares anything unusual about its own receive budget - it just
            // uses however much of the table the peer (here: the proxy, relaying the raw client's declared
            // ClientDeclaredHeaderTableSize) told it it may use.
            await connection.SendInitialSettingsAsync();
            var (streamId, _, _) = await connection.ReadRequestAsync();

            // Grow the encoder's dynamic table to match what the client side declared (forwarded to this
            // server, transparently, by the proxy) and index a repeated header so the Dynamic Table Size
            // Update instruction is actually exercised, not just emitted and ignored.
            var headers = connection.EncodeHeadersWithTableSizeUpdate(ClientDeclaredHeaderTableSize,
                new[] { (":status", "200") },
                new[] { ("x-large-indexed-header", new string('a', 100)) });
            await connection.WriteHeaderBlockAsync(streamId, headers, true);
        });

        using var testSuite = new TestSuite();
        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;
        // These tests exercise MITM HPACK table sizing; keep interception on so compressed
        // passthrough (which forces SETTINGS_HEADER_TABLE_SIZE=0) is not used.
        proxy.EnableHttpInterception = true;

        var uri = new Uri(rawServer.Url);
        using var rawClient = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port,
            ClientDeclaredHeaderTableSize);

        var requestHeaders = rawClient.Connection.EncodeHeaders(
            new[]
            {
                (":method", "GET"), (":scheme", "https"), (":authority", $"{uri.Host}:{uri.Port}"),
                (":path", "/")
            },
            Array.Empty<(string, string)>());
        await rawClient.Connection.WriteHeaderBlockAsync(1, requestHeaders, true);

        var (_, responseHeaders, endStream) = await rawClient.Connection.ReadHeaderBlockAsync();
        Assert.AreEqual("200", responseHeaders.Single(h => h.Name == ":status").Value);
        Assert.AreEqual(new string('a', 100), responseHeaders.Single(h => h.Name == "x-large-indexed-header").Value,
            "The response header was not decoded correctly after the origin grew its dynamic table to the " +
            "client-declared size - possible HPACK decoder-sizing regression.");
        Assert.IsTrue(endStream);
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task Http2_Client_Request_Growing_Dynamic_Table_To_ServerDeclared_Size_Decodes_Correctly()
    {
        // Mirror of the above for the opposite direction: the raw client here plays the role of the origin
        // server declaring a larger SETTINGS_HEADER_TABLE_SIZE, which the proxy must forward to the real
        // origin; the raw origin server then grows its own dynamic table (used to encode headers *sent to
        // the proxy*, i.e. request-direction traffic is not applicable here - so instead this exercises the
        // client growing its own encoder to what the *origin* declared, forwarded to the client).
        const int serverDeclaredHeaderTableSize = 65536;

        using var rawServer = new Http2RawOriginServer(CreateOriginCertificate());
        System.Collections.Generic.List<(string Name, string Value)>? receivedRequestHeaders = null;
        var requestReceived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        rawServer.HandleConnection(async connection =>
        {
            await connection.SendInitialSettingsAsync(serverDeclaredHeaderTableSize);
            var (_, headers, _) = await connection.ReadHeaderBlockAsync();
            receivedRequestHeaders = headers;
            requestReceived.TrySetResult(true);

            var responseHeaders = connection.EncodeHeaders(new[] { (":status", "200") },
                Array.Empty<(string, string)>());
            await connection.WriteHeaderBlockAsync(1, responseHeaders, true);
        });

        using var testSuite = new TestSuite();
        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;
        // These tests exercise MITM HPACK table sizing; keep interception on so compressed
        // passthrough (which forces SETTINGS_HEADER_TABLE_SIZE=0) is not used.
        proxy.EnableHttpInterception = true;

        var uri = new Uri(rawServer.Url);
        using var rawClient = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        // Wait for the origin's initial SETTINGS (declaring serverDeclaredHeaderTableSize) to be relayed
        // through the proxy before growing our own encoder to that size - a real client only does so once
        // it has actually observed the peer's advertised budget, which also incidentally guarantees the
        // proxy's own ServerSettings state has already been updated from that same frame before our
        // request arrives at the proxy's other relay task.
        var settingsFrame = await rawClient.Connection.ReadFrameAsync();
        Assert.AreEqual(Titanium.Web.Proxy.Http2.Http2FrameType.Settings, settingsFrame.Type);

        var requestHeaders = rawClient.Connection.EncodeHeadersWithTableSizeUpdate(serverDeclaredHeaderTableSize,
            new[]
            {
                (":method", "GET"), (":scheme", "https"), (":authority", $"{uri.Host}:{uri.Port}"),
                (":path", "/")
            },
            new[] { ("x-large-indexed-header", new string('b', 100)) });
        await rawClient.Connection.WriteHeaderBlockAsync(1, requestHeaders, true);

        await Task.WhenAny(requestReceived.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.IsTrue(requestReceived.Task.IsCompleted, "The origin server never received the request.");
        Assert.IsNotNull(receivedRequestHeaders);
        Assert.AreEqual(new string('b', 100),
            receivedRequestHeaders.Single(h => h.Name == "x-large-indexed-header").Value,
            "The request header was not decoded correctly after the client grew its dynamic table to the " +
            "server-declared size - possible HPACK decoder-sizing regression.");

        var (_, responseHeaders, _) = await rawClient.Connection.ReadHeaderBlockAsync();
        Assert.AreEqual("200", responseHeaders.Single(h => h.Name == ":status").Value);
    }
}
