using System;
using System.Buffers.Binary;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat <c>Http2Helper.Copy</c> suite: legal SETTINGS_HEADER_TABLE_SIZE=0,
///     large MAX_CONCURRENT_STREAMS, INITIAL_WINDOW_SIZE=0, and SETTINGS ACK empty.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http2CopySettingsHeaderTableFatGapTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _) =>
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    private static byte[] SettingsEntry(ushort id, uint value)
    {
        var payload = new byte[6];
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(0, 2), id);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(2, 4), value);
        return payload;
    }

    private static async Task AssertGetSucceedsAfterSettingsAsync(
        Http2RawClient raw, Uri uri, byte[] settingsPayload, string path)
    {
        await raw.Connection.WriteFrameAsync(Http2FrameType.Settings, 0, 0, settingsPayload);

        var block = raw.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "https"),
             (":authority", $"{uri.Host}:{uri.Port}"), (":path", path)],
            []);
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: true);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var frame = await raw.Connection.ReadFrameAsync()
                .WaitAsync(deadline - DateTime.UtcNow);
            if (frame.Type == Http2FrameType.GoAway)
                Assert.Fail($"Legal SETTINGS must not GOAWAY for {path}.");
            if (frame.Type == Http2FrameType.Headers && frame.StreamId == 1)
                return;
        }

        Assert.Fail($"Timed out waiting for response HEADERS for {path}.");
    }

    private static void IdleRespondingOrigin(Http2RawOriginServer origin)
    {
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
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Settings_HeaderTableSizeZero_AllowsSubsequentGet()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        IdleRespondingOrigin(origin);
        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);
        await AssertGetSucceedsAfterSettingsAsync(raw, uri, SettingsEntry(0x1, 0), "/hts0");
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Settings_MaxConcurrentStreamsLarge_AllowsSubsequentGet()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        IdleRespondingOrigin(origin);
        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);
        await AssertGetSucceedsAfterSettingsAsync(raw, uri, SettingsEntry(0x3, 1000), "/mcs");
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Settings_InitialWindowSizeZero_AllowsSubsequentGet()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        IdleRespondingOrigin(origin);
        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);
        await AssertGetSucceedsAfterSettingsAsync(raw, uri, SettingsEntry(0x4, 0), "/iws0");
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Settings_EmptyAck_AllowsSubsequentGet()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        IdleRespondingOrigin(origin);
        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        await raw.Connection.WriteFrameAsync(
            Http2FrameType.Settings, 0, Http2FrameFlag.Ack, Array.Empty<byte>());

        var block = raw.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "https"),
             (":authority", $"{uri.Host}:{uri.Port}"), (":path", "/ack")],
            []);
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: true);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var frame = await raw.Connection.ReadFrameAsync()
                .WaitAsync(deadline - DateTime.UtcNow);
            if (frame.Type == Http2FrameType.GoAway)
                Assert.Fail("Empty SETTINGS ACK must not GOAWAY.");
            if (frame.Type == Http2FrameType.Headers && frame.StreamId == 1)
                return;
        }

        Assert.Fail("Timed out waiting for response HEADERS after empty SETTINGS ACK.");
    }
}
