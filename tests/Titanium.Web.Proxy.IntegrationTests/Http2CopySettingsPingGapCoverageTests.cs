using System;
using System.Buffers.Binary;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Remaining <c>Http2Helper.Copy</c> SETTINGS / PING / stream-id-0 enforcement gaps:
///     invalid ENABLE_PUSH, undersized MAX_FRAME_SIZE, bad PING length, and connection-only
///     frames on a non-zero stream.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http2CopySettingsPingGapCoverageTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _)
    {
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);
    }

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    private static async Task<Http2RawFrame.Frame> ReadSignificantFrameAsync(
        Http2RawClient raw, TimeSpan timeout)
    {
        Http2RawFrame.Frame frame;
        do
        {
            frame = await raw.Connection.ReadFrameAsync().WaitAsync(timeout);
        } while (frame.Type is Http2FrameType.Settings or Http2FrameType.WindowUpdate);

        return frame;
    }

    private static byte[] SettingsEntry(Http2SettingsId id, uint value)
    {
        var payload = new byte[6];
        payload[0] = (byte)(((int)id >> 8) & 0xff);
        payload[1] = (byte)((int)id & 0xff);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(2), value);
        return payload;
    }

    private static Func<Http2RawFrame.Connection, Task> NoOpOriginHandler() => async conn =>
    {
        await conn.SendInitialSettingsAsync();
        try { while (true) await conn.ReadFrameAsync(); }
        catch { /* torn down */ }
    };

    /// <summary>
    ///     RFC 9113 §6.5.2: SETTINGS_ENABLE_PUSH MUST be 0 or 1.
    ///     Bug class: ENABLE_PUSH=2 is accepted and push state becomes undefined.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Client_Settings_EnablePushGreaterThanOne_TriggersGoAwayProtocolError()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(NoOpOriginHandler());

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;

        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);
        await raw.Connection.WriteFrameAsync(Http2FrameType.Settings, 0, (Http2FrameFlag)0,
            SettingsEntry(Http2SettingsId.EnablePush, 2u));

        var frame = await ReadSignificantFrameAsync(raw, TimeSpan.FromSeconds(10));
        Assert.AreEqual(Http2FrameType.GoAway, frame.Type,
            $"ENABLE_PUSH=2 must GOAWAY; got {frame.Type}.");
        Assert.AreEqual((int)Http2ErrorCode.ProtocolError,
            BinaryPrimitives.ReadInt32BigEndian(frame.Payload.AsSpan(4, 4)));
    }

    /// <summary>
    ///     RFC 7540 §6.5.2: SETTINGS_MAX_FRAME_SIZE below 2^14 is a PROTOCOL_ERROR.
    ///     Bug class: undersized MAX_FRAME_SIZE is clamped instead of rejected.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Client_Settings_MaxFrameSizeBelowMinimum_TriggersGoAwayProtocolError()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(NoOpOriginHandler());

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;

        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);
        await raw.Connection.WriteFrameAsync(Http2FrameType.Settings, 0, (Http2FrameFlag)0,
            SettingsEntry(Http2SettingsId.MaxFrameSize, 16383u));

        var frame = await ReadSignificantFrameAsync(raw, TimeSpan.FromSeconds(10));
        Assert.AreEqual(Http2FrameType.GoAway, frame.Type,
            $"MAX_FRAME_SIZE=16383 must GOAWAY; got {frame.Type}.");
        Assert.AreEqual((int)Http2ErrorCode.ProtocolError,
            BinaryPrimitives.ReadInt32BigEndian(frame.Payload.AsSpan(4, 4)));
    }

    /// <summary>
    ///     RFC 7540 §6.7: PING payload length must be exactly 8.
    ///     Bug class: truncated PING is ignored / hung instead of FRAME_SIZE_ERROR.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Client_Ping_InvalidLength_TriggersGoAwayFrameSizeError()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(NoOpOriginHandler());

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;

        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);
        await raw.Connection.WriteFrameAsync(Http2FrameType.Ping, 0, (Http2FrameFlag)0, new byte[4]);

        var frame = await ReadSignificantFrameAsync(raw, TimeSpan.FromSeconds(10));
        Assert.AreEqual(Http2FrameType.GoAway, frame.Type,
            $"PING length 4 must GOAWAY; got {frame.Type}.");
        Assert.AreEqual((int)Http2ErrorCode.FrameSizeError,
            BinaryPrimitives.ReadInt32BigEndian(frame.Payload.AsSpan(4, 4)));
    }

    /// <summary>
    ///     RFC 9113 §6.7: PING must use stream id 0.
    ///     Bug class: PING on a stream is treated as a stream frame and corrupts flow state.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Client_Ping_OnNonZeroStream_TriggersGoAwayProtocolError()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(NoOpOriginHandler());

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;

        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);
        await raw.Connection.WriteFrameAsync(Http2FrameType.Ping, 1, (Http2FrameFlag)0, new byte[8]);

        var frame = await ReadSignificantFrameAsync(raw, TimeSpan.FromSeconds(10));
        Assert.AreEqual(Http2FrameType.GoAway, frame.Type,
            $"PING on stream 1 must GOAWAY; got {frame.Type}.");
        Assert.AreEqual((int)Http2ErrorCode.ProtocolError,
            BinaryPrimitives.ReadInt32BigEndian(frame.Payload.AsSpan(4, 4)));
    }

    /// <summary>
    ///     RFC 7540 §5.1.1: RST_STREAM is stream-specific; stream id 0 is PROTOCOL_ERROR.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Client_RstStream_OnStreamZero_TriggersGoAwayProtocolError()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(NoOpOriginHandler());

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;

        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);
        var rst = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(rst, (int)Http2ErrorCode.Cancel);
        await raw.Connection.WriteFrameAsync(Http2FrameType.RstStream, 0, (Http2FrameFlag)0, rst);

        var frame = await ReadSignificantFrameAsync(raw, TimeSpan.FromSeconds(10));
        Assert.AreEqual(Http2FrameType.GoAway, frame.Type,
            $"RST_STREAM on stream 0 must GOAWAY; got {frame.Type}.");
        Assert.AreEqual((int)Http2ErrorCode.ProtocolError,
            BinaryPrimitives.ReadInt32BigEndian(frame.Payload.AsSpan(4, 4)));
    }

    /// <summary>
    ///     RFC 7540 §6.5.2: SETTINGS_INITIAL_WINDOW_SIZE above 2^31-1 is FLOW_CONTROL_ERROR.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Client_Settings_InitialWindowSizeTooLarge_TriggersGoAwayFlowControlError()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(NoOpOriginHandler());

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;

        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);
        await raw.Connection.WriteFrameAsync(Http2FrameType.Settings, 0, (Http2FrameFlag)0,
            SettingsEntry(Http2SettingsId.InitialWindowSize, 0x80000000u));

        var frame = await ReadSignificantFrameAsync(raw, TimeSpan.FromSeconds(10));
        Assert.AreEqual(Http2FrameType.GoAway, frame.Type,
            $"INITIAL_WINDOW_SIZE overflow must GOAWAY; got {frame.Type}.");
        Assert.AreEqual((int)Http2ErrorCode.FlowControlError,
            BinaryPrimitives.ReadInt32BigEndian(frame.Payload.AsSpan(4, 4)));
    }
}
