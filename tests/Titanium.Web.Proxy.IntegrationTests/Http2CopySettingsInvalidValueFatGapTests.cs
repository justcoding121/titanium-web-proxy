using System;
using System.Buffers.Binary;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat <c>Http2Helper.Copy</c> suite: SETTINGS values outside the RFC ranges
///     (ENABLE_PUSH, MAX_FRAME_SIZE, INITIAL_WINDOW_SIZE, ENABLE_CONNECT_PROTOCOL).
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http2CopySettingsInvalidValueFatGapTests
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
    ///     Bug class: SETTINGS_ENABLE_PUSH=2 is accepted instead of PROTOCOL_ERROR.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Settings_EnablePushOutOfRange_GoAwayProtocolError()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        await IdleOriginAsync(origin);
        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        await raw.Connection.WriteFrameAsync(
            Http2FrameType.Settings, 0, 0, SettingsEntry(0x2, 2));

        var (type, code) = await ReadGoAwayAsync(raw, TimeSpan.FromSeconds(10));
        Assert.AreEqual(Http2FrameType.GoAway, type);
        Assert.AreEqual((int)Http2ErrorCode.ProtocolError, code);
    }

    /// <summary>
    ///     Bug class: SETTINGS_MAX_FRAME_SIZE below 2^14 is accepted.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Settings_MaxFrameSizeTooSmall_GoAwayProtocolError()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        await IdleOriginAsync(origin);
        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        await raw.Connection.WriteFrameAsync(
            Http2FrameType.Settings, 0, 0, SettingsEntry(0x5, 16383));

        var (type, code) = await ReadGoAwayAsync(raw, TimeSpan.FromSeconds(10));
        Assert.AreEqual(Http2FrameType.GoAway, type);
        Assert.AreEqual((int)Http2ErrorCode.ProtocolError, code);
    }

    /// <summary>
    ///     Bug class: SETTINGS_INITIAL_WINDOW_SIZE above 2^31-1 is accepted.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Settings_InitialWindowSizeOverflow_GoAwayFlowControlError()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        await IdleOriginAsync(origin);
        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        // value > 2^31-1 → FLOW_CONTROL_ERROR
        await raw.Connection.WriteFrameAsync(
            Http2FrameType.Settings, 0, 0, SettingsEntry(0x4, 0x80000000u));

        var (type, code) = await ReadGoAwayAsync(raw, TimeSpan.FromSeconds(10));
        Assert.AreEqual(Http2FrameType.GoAway, type);
        Assert.AreEqual((int)Http2ErrorCode.FlowControlError, code);
    }

    /// <summary>
    ///     Bug class: SETTINGS_ENABLE_CONNECT_PROTOCOL=2 is accepted.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Settings_EnableConnectProtocolOutOfRange_GoAwayProtocolError()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        await IdleOriginAsync(origin);
        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        await raw.Connection.WriteFrameAsync(
            Http2FrameType.Settings, 0, 0, SettingsEntry(0x8, 2));

        var (type, code) = await ReadGoAwayAsync(raw, TimeSpan.FromSeconds(10));
        Assert.AreEqual(Http2FrameType.GoAway, type);
        Assert.AreEqual((int)Http2ErrorCode.ProtocolError, code);
    }
}
