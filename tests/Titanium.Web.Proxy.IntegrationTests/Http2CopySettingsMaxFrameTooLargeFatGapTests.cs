using System;
using System.Buffers.Binary;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat <c>Http2Helper.Copy</c> suite: SETTINGS_MAX_FRAME_SIZE above 2^24-1,
///     and a valid ENABLE_PUSH=0 that must not GOAWAY.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http2CopySettingsMaxFrameTooLargeFatGapTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _) =>
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    private static async Task IdleOriginAsync(Http2RawOriginServer origin)
    {
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            try { while (true) await conn.ReadFrameAsync(); } catch { }
        });
        await Task.CompletedTask;
    }

    private static async Task<(Http2FrameType Type, int Error)> ReadGoAwayAsync(
        Http2RawClient raw, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var frame = await raw.Connection.ReadFrameAsync()
                .WaitAsync(deadline - DateTime.UtcNow);
            if (frame.Type == Http2FrameType.GoAway)
            {
                return (frame.Type,
                    BinaryPrimitives.ReadInt32BigEndian(frame.Payload.AsSpan(4, 4)));
            }
        }

        Assert.Fail("Timed out waiting for GOAWAY.");
        return default;
    }

    private static byte[] SettingsEntry(ushort id, uint value)
    {
        var payload = new byte[6];
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(0, 2), id);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(2, 4), value);
        return payload;
    }

    /// <summary>
    ///     Bug class: SETTINGS_MAX_FRAME_SIZE=2^24 is accepted.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Settings_MaxFrameSizeTooLarge_GoAwayProtocolError()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        await IdleOriginAsync(origin);
        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        await raw.Connection.WriteFrameAsync(
            Http2FrameType.Settings, 0, 0, SettingsEntry(0x5, 16777216u));

        var (type, code) = await ReadGoAwayAsync(raw, TimeSpan.FromSeconds(10));
        Assert.AreEqual(Http2FrameType.GoAway, type);
        Assert.AreEqual((int)Http2ErrorCode.ProtocolError, code);
    }

    /// <summary>
    ///     Bug class: SETTINGS_ENABLE_PUSH=0 (legal) incorrectly GOAWAYs.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Settings_EnablePushZero_AllowsSubsequentGet()
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
        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        await raw.Connection.WriteFrameAsync(
            Http2FrameType.Settings, 0, 0, SettingsEntry(0x2, 0));

        var block = raw.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "https"),
             (":authority", $"{uri.Host}:{uri.Port}"), (":path", "/push0")],
            []);
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: true);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var frame = await raw.Connection.ReadFrameAsync()
                .WaitAsync(deadline - DateTime.UtcNow);
            if (frame.Type == Http2FrameType.GoAway)
                Assert.Fail("Valid ENABLE_PUSH=0 must not GOAWAY.");
            if (frame.Type == Http2FrameType.Headers && frame.StreamId == 1)
                return;
        }

        Assert.Fail("Timed out waiting for response HEADERS after ENABLE_PUSH=0.");
    }

    /// <summary>
    ///     Bug class: SETTINGS_MAX_FRAME_SIZE at the RFC maximum is rejected.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Settings_MaxFrameSizeMaxLegal_AllowsSubsequentGet()
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
        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        await raw.Connection.WriteFrameAsync(
            Http2FrameType.Settings, 0, 0, SettingsEntry(0x5, 16777215u));

        var block = raw.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "https"),
             (":authority", $"{uri.Host}:{uri.Port}"), (":path", "/maxframe")],
            []);
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: true);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var frame = await raw.Connection.ReadFrameAsync()
                .WaitAsync(deadline - DateTime.UtcNow);
            if (frame.Type == Http2FrameType.GoAway)
                Assert.Fail("Legal MAX_FRAME_SIZE=2^24-1 must not GOAWAY.");
            if (frame.Type == Http2FrameType.Headers && frame.StreamId == 1)
                return;
        }

        Assert.Fail("Timed out waiting for response HEADERS after legal MAX_FRAME_SIZE.");
    }

    /// <summary>
    ///     Bug class: SETTINGS_ENABLE_CONNECT_PROTOCOL=1 is rejected for the client leg.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Settings_EnableConnectProtocolOne_AllowsSubsequentGet()
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
        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        await raw.Connection.WriteFrameAsync(
            Http2FrameType.Settings, 0, 0, SettingsEntry(0x8, 1));

        var block = raw.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "https"),
             (":authority", $"{uri.Host}:{uri.Port}"), (":path", "/connect1")],
            []);
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: true);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var frame = await raw.Connection.ReadFrameAsync()
                .WaitAsync(deadline - DateTime.UtcNow);
            if (frame.Type == Http2FrameType.GoAway)
                Assert.Fail("Valid ENABLE_CONNECT_PROTOCOL=1 must not GOAWAY.");
            if (frame.Type == Http2FrameType.Headers && frame.StreamId == 1)
                return;
        }

        Assert.Fail("Timed out waiting for response HEADERS after ENABLE_CONNECT_PROTOCOL=1.");
    }
}
