using System;
using System.Buffers.Binary;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat <c>Http2Helper.Copy</c> compressed-relay arms: malformed HEADERS PRIORITY /
///     PADDING layout on the first request after preface.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http2CopyCompressedRelayHeaderDefectFatGapTests
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

    private static async Task<(Http2FrameType Type, int Error)> ReadGoAwayAsync(Http2RawClient raw, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var frame = await raw.Connection.ReadFrameAsync()
                .WaitAsync(deadline - DateTime.UtcNow);
            if (frame.Type == Http2FrameType.GoAway)
                return (frame.Type, BinaryPrimitives.ReadInt32BigEndian(frame.Payload.AsSpan(4, 4)));
        }

        Assert.Fail("Timed out waiting for GOAWAY.");
        return default;
    }

    private static async Task<Http2RawClient> ConnectCompressedRelayAsync(ProxyServer proxy, Uri uri)
    {
        var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);
        await raw.Connection.ReadFrameAsync();
        await raw.Connection.ReadFrameAsync();
        return raw;
    }

    /// <summary>
    ///     Bug class: PRIORITY without the full 5-byte payload is accepted on compressed relay.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task CompressedRelay_HeadersPriorityTooShort_GoAwayFrameSizeError()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        await IdleOriginAsync(origin);
        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableHttpInterception = false;

        var uri = new Uri(origin.Url);
        using var raw = await ConnectCompressedRelayAsync(proxy, uri);

        await raw.Connection.WriteFrameAsync(
            Http2FrameType.Headers, 1,
            Http2FrameFlag.Priority | Http2FrameFlag.EndHeaders,
            new byte[] { 0x82, 0x86 });

        var (_, code) = await ReadGoAwayAsync(raw, TimeSpan.FromSeconds(10));
        Assert.AreEqual((int)Http2ErrorCode.FrameSizeError, code);
    }

    /// <summary>
    ///     Bug class: PADDED HEADERS where pad length exceeds payload hangs instead of GOAWAY.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task CompressedRelay_HeadersPaddingTooLarge_GoAwayProtocolError()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        await IdleOriginAsync(origin);
        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableHttpInterception = false;

        var uri = new Uri(origin.Url);
        using var raw = await ConnectCompressedRelayAsync(proxy, uri);

        await raw.Connection.WriteFrameAsync(
            Http2FrameType.Headers, 1,
            Http2FrameFlag.Padded | Http2FrameFlag.EndHeaders,
            new byte[] { 8, 0x82, 0x86 });

        var (_, code) = await ReadGoAwayAsync(raw, TimeSpan.FromSeconds(10));
        Assert.AreEqual((int)Http2ErrorCode.ProtocolError, code);
    }
}
