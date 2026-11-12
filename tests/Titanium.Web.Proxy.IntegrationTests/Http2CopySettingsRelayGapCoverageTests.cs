using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Remaining Copy.cs SETTINGS rewrite / RFC 8441 edges: client WtMaxSessions zeroing,
///     client EnableConnectProtocol suppression and invalid values, and SETTINGS ACK with payload.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http2CopySettingsRelayGapCoverageTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _) =>
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);

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

    private static byte[] Concat(params byte[][] parts)
    {
        var len = 0;
        foreach (var p in parts) len += p.Length;
        var all = new byte[len];
        var o = 0;
        foreach (var p in parts)
        {
            Buffer.BlockCopy(p, 0, all, o, p.Length);
            o += p.Length;
        }
        return all;
    }

    /// <summary>
    ///     Bug class: client's SETTINGS_WT_MAX_SESSIONS is forwarded to the origin, enabling
    ///     WebTransport toward an origin that never opted in.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Client_Settings_WtMaxSessions_IsZeroedBeforeRelayToOrigin()
    {
        uint? seen = null;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            try
            {
                while (true)
                {
                    var frame = await conn.ReadFrameAsync();
                    if (frame.Type != Http2FrameType.Settings || (frame.Flags & Http2FrameFlag.Ack) != 0)
                        continue;
                    for (var i = 0; i + 6 <= frame.Payload.Length; i += 6)
                    {
                        var id = (frame.Payload[i] << 8) | frame.Payload[i + 1];
                        if (id == (int)Http2SettingsId.WtMaxSessions)
                        {
                            seen = BinaryPrimitives.ReadUInt32BigEndian(frame.Payload.AsSpan(i + 2, 4));
                            done.TrySetResult();
                            return;
                        }
                    }
                }
            }
            catch { done.TrySetResult(); }
        });

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;

        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);
        await raw.Connection.WriteFrameAsync(Http2FrameType.Settings, 0, (Http2FrameFlag)0,
            SettingsEntry(Http2SettingsId.WtMaxSessions, 4u));

        // Nudge the connection so SETTINGS is processed/relayed.
        var block = raw.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "https"),
             (":authority", $"{uri.Host}:{uri.Port}"), (":path", "/wt")],
            []);
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: true);

        await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.AreEqual(0u, seen, "Client WtMaxSessions must be rewritten to 0 toward the origin.");
    }

    /// <summary>
    ///     Bug class: client's ENABLE_CONNECT_PROTOCOL preference is relayed, so the origin believes
    ///     the browser negotiated RFC 8441 with it directly.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Client_Settings_EnableConnectProtocol_IsZeroedBeforeRelayToOrigin()
    {
        uint? seen = null;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            try
            {
                while (true)
                {
                    var frame = await conn.ReadFrameAsync();
                    if (frame.Type != Http2FrameType.Settings || (frame.Flags & Http2FrameFlag.Ack) != 0)
                        continue;
                    for (var i = 0; i + 6 <= frame.Payload.Length; i += 6)
                    {
                        var id = (frame.Payload[i] << 8) | frame.Payload[i + 1];
                        if (id == (int)Http2SettingsId.EnableConnectProtocol)
                        {
                            seen = BinaryPrimitives.ReadUInt32BigEndian(frame.Payload.AsSpan(i + 2, 4));
                            done.TrySetResult();
                            return;
                        }
                    }
                }
            }
            catch { done.TrySetResult(); }
        });

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;

        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);
        await raw.Connection.WriteFrameAsync(Http2FrameType.Settings, 0, (Http2FrameFlag)0,
            SettingsEntry(Http2SettingsId.EnableConnectProtocol, 1u));

        var block = raw.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "https"),
             (":authority", $"{uri.Host}:{uri.Port}"), (":path", "/connect-proto")],
            []);
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: true);

        await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.AreEqual(0u, seen, "Client ENABLE_CONNECT_PROTOCOL must be suppressed toward the origin.");
    }

    /// <summary>
    ///     RFC 8441 §3: ENABLE_CONNECT_PROTOCOL MUST be 0 or 1.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Client_Settings_EnableConnectProtocolInvalid_TriggersGoAwayProtocolError()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            try { while (true) await conn.ReadFrameAsync(); } catch { }
        });

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;

        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);
        await raw.Connection.WriteFrameAsync(Http2FrameType.Settings, 0, (Http2FrameFlag)0,
            SettingsEntry(Http2SettingsId.EnableConnectProtocol, 2u));

        var frame = await ReadSignificantFrameAsync(raw, TimeSpan.FromSeconds(10));
        Assert.AreEqual(Http2FrameType.GoAway, frame.Type);
        Assert.AreEqual((int)Http2ErrorCode.ProtocolError,
            BinaryPrimitives.ReadInt32BigEndian(frame.Payload.AsSpan(4, 4)));
    }

    /// <summary>
    ///     RFC 7540 §6.5: SETTINGS ACK must have an empty payload.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Client_SettingsAck_WithPayload_TriggersGoAwayFrameSizeError()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            try { while (true) await conn.ReadFrameAsync(); } catch { }
        });

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;

        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);
        await raw.Connection.WriteFrameAsync(Http2FrameType.Settings, 0, Http2FrameFlag.Ack,
            SettingsEntry(Http2SettingsId.EnablePush, 0u));

        var frame = await ReadSignificantFrameAsync(raw, TimeSpan.FromSeconds(10));
        Assert.AreEqual(Http2FrameType.GoAway, frame.Type);
        Assert.AreEqual((int)Http2ErrorCode.FrameSizeError,
            BinaryPrimitives.ReadInt32BigEndian(frame.Payload.AsSpan(4, 4)));
    }

    /// <summary>
    ///     Bug class: a SETTINGS frame that omits ENABLE_PUSH still leaves the RFC default (1)
    ///     toward the origin, so PUSH_PROMISE becomes valid.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Client_Settings_WithoutEnablePush_AppendsExplicitZeroTowardOrigin()
    {
        var sawPushDisabled = false;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            try
            {
                while (true)
                {
                    var frame = await conn.ReadFrameAsync();
                    if (frame.Type != Http2FrameType.Settings || (frame.Flags & Http2FrameFlag.Ack) != 0)
                        continue;
                    for (var i = 0; i + 6 <= frame.Payload.Length; i += 6)
                    {
                        var id = (frame.Payload[i] << 8) | frame.Payload[i + 1];
                        var value = BinaryPrimitives.ReadUInt32BigEndian(frame.Payload.AsSpan(i + 2, 4));
                        if (id == (int)Http2SettingsId.EnablePush && value == 0)
                        {
                            sawPushDisabled = true;
                            done.TrySetResult();
                            return;
                        }
                    }
                }
            }
            catch { done.TrySetResult(); }
        });

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;

        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);
        // Header table size alone — no ENABLE_PUSH entry.
        await raw.Connection.WriteFrameAsync(Http2FrameType.Settings, 0, (Http2FrameFlag)0,
            SettingsEntry(Http2SettingsId.HeaderTableSize, 4096u));

        var block = raw.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "https"),
             (":authority", $"{uri.Host}:{uri.Port}"), (":path", "/nopush")],
            []);
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: true);

        await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.IsTrue(sawPushDisabled,
            "Proxy must append ENABLE_PUSH=0 when the client SETTINGS omitted it.");
    }

    /// <summary>
    ///     Combined SETTINGS: ENABLE_PUSH=0 + MaxHeaderListSize rewrite path under interception.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Client_Settings_MaxHeaderListSize_IsAcceptedAndRequestSucceeds()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            var (streamId, _, _) = await conn.ReadRequestAsync();
            var resp = conn.EncodeHeaders([(":status", "204")], []);
            await conn.WriteHeaderBlockAsync(streamId, resp, endStream: true);
        });

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableHttpInterception = true;

        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);
        await raw.Connection.WriteFrameAsync(Http2FrameType.Settings, 0, (Http2FrameFlag)0,
            Concat(
                SettingsEntry(Http2SettingsId.EnablePush, 0u),
                SettingsEntry(Http2SettingsId.MaxHeaderListSize, 16_384u),
                SettingsEntry(Http2SettingsId.HeaderTableSize, 0u)));

        var block = raw.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "https"),
             (":authority", $"{uri.Host}:{uri.Port}"), (":path", "/ok")],
            []);
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: true);

        string? status = null;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline && status is null)
        {
            var frame = await raw.Connection.ReadFrameAsync().WaitAsync(TimeSpan.FromSeconds(5));
            if (frame.Type is Http2FrameType.Headers or Http2FrameType.Continuation)
            {
                var headers = raw.Connection.DecodeHeaders(frame.Payload);
                status = headers.Find(h => h.Name == ":status").Value;
            }
        }

        Assert.AreEqual("204", status);
    }
}
