using System;
using System.Buffers.Binary;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat <c>Http2Helper.Copy</c> suite: unknown frame type ignored then GET succeeds,
///     and SETTINGS with an unknown identifier still allows a subsequent GET.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http2CopyUnknownFrameAndSettingsIdFatGapTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _) =>
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    private static async Task<string?> ReadStatusAsync(Http2RawClient raw, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var frame = await raw.Connection.ReadFrameAsync()
                .WaitAsync(deadline - DateTime.UtcNow);
            if (frame.Type is Http2FrameType.Headers or Http2FrameType.Continuation)
            {
                foreach (var h in raw.Connection.DecodeHeaders(frame.Payload))
                {
                    if (h.Name == ":status")
                        return h.Value;
                }
            }

            if (frame.Type == Http2FrameType.GoAway)
                Assert.Fail("Unexpected GOAWAY.");
        }

        return null;
    }

    /// <summary>
    ///     Bug class: an unknown frame type tears down the connection instead of being ignored.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task UnknownFrameType_ThenGet_Succeeds()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            while (true)
            {
                var (streamId, _, _) = await conn.ReadRequestAsync();
                var resp = conn.EncodeHeaders([(":status", "204")], []);
                await conn.WriteHeaderBlockAsync(streamId, resp, endStream: true);
            }
        });

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableHttpInterception = true;

        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        // Type 0x0f is unused; RFC 9113 §5.1 — ignore unknown types.
        await raw.Connection.WriteFrameAsync((Http2FrameType)0x0f, 0, 0, new byte[] { 1, 2, 3, 4 });

        var block = raw.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "https"),
             (":authority", $"{uri.Host}:{uri.Port}"), (":path", "/after-unknown")],
            []);
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: true);
        Assert.AreEqual("204", await ReadStatusAsync(raw, TimeSpan.FromSeconds(12)));
    }

    /// <summary>
    ///     Bug class: SETTINGS with an unknown identifier GOAWAYs the connection.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Settings_UnknownIdentifier_ThenGet_Succeeds()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            while (true)
            {
                var (streamId, _, _) = await conn.ReadRequestAsync();
                var resp = conn.EncodeHeaders([(":status", "204")], []);
                await conn.WriteHeaderBlockAsync(streamId, resp, endStream: true);
            }
        });

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableHttpInterception = true;

        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        var entry = new byte[6];
        BinaryPrimitives.WriteUInt16BigEndian(entry.AsSpan(0, 2), 0x99); // unknown
        BinaryPrimitives.WriteUInt32BigEndian(entry.AsSpan(2, 4), 1);
        await raw.Connection.WriteFrameAsync(Http2FrameType.Settings, 0, 0, entry);

        var block = raw.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "https"),
             (":authority", $"{uri.Host}:{uri.Port}"), (":path", "/after-settings")],
            []);
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: true);
        Assert.AreEqual("204", await ReadStatusAsync(raw, TimeSpan.FromSeconds(12)));
    }
}
