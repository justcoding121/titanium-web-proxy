using System;
using System.Buffers.Binary;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat <c>Http2Helper.Copy.Headers</c> suite: origin response Upgrade / Keep-Alive
///     forbidden fields, and request Host alongside :authority.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http2CopyHeadersOriginUpgradeFatGapTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _) =>
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    private static async Task<(Http2FrameType Type, int Error)> ReadRstOrGoAwayAsync(
        Http2RawClient raw, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var frame = await raw.Connection.ReadFrameAsync()
                .WaitAsync(deadline - DateTime.UtcNow);
            if (frame.Type == Http2FrameType.RstStream)
                return (frame.Type, BinaryPrimitives.ReadInt32BigEndian(frame.Payload.AsSpan(0, 4)));
            if (frame.Type == Http2FrameType.GoAway)
                return (frame.Type, BinaryPrimitives.ReadInt32BigEndian(frame.Payload.AsSpan(4, 4)));
        }

        Assert.Fail("Timed out waiting for RST/GOAWAY.");
        return default;
    }

    private static async Task AssertOriginForbiddenResponseAsync(string forbiddenName, string forbiddenValue)
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            var (streamId, _, _) = await conn.ReadRequestAsync();
            var resp = conn.EncodeHeaders(
                [(":status", "200"), (forbiddenName, forbiddenValue), ("content-length", "0")],
                []);
            await conn.WriteHeaderBlockAsync(streamId, resp, endStream: true);
        });

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableHttpInterception = true;

        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);
        var block = raw.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "https"),
             (":authority", $"{uri.Host}:{uri.Port}"), (":path", "/forbid")],
            []);
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: true);

        var (type, _) = await ReadRstOrGoAwayAsync(raw, TimeSpan.FromSeconds(12));
        Assert.IsTrue(type is Http2FrameType.RstStream or Http2FrameType.GoAway,
            $"Origin '{forbiddenName}' response header must terminate exchange; got {type}");
    }

    [TestMethod]
    [Timeout(30_000)]
    public Task Origin_ResponseUpgradeHeader_SoftResetsStream() =>
        AssertOriginForbiddenResponseAsync("upgrade", "websocket");

    [TestMethod]
    [Timeout(30_000)]
    public Task Origin_ResponseKeepAliveHeader_SoftResetsStream() =>
        AssertOriginForbiddenResponseAsync("keep-alive", "timeout=5");

    /// <summary>
    ///     Bug class: Host alongside :authority desyncs or rejects a valid GET.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Request_HostAlongsideAuthority_Succeeds()
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
        var block = raw.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "https"),
             (":authority", $"{uri.Host}:{uri.Port}"), (":path", "/host")],
            [("host", $"{uri.Host}:{uri.Port}")]);
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: true);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(12);
        while (DateTime.UtcNow < deadline)
        {
            var frame = await raw.Connection.ReadFrameAsync()
                .WaitAsync(deadline - DateTime.UtcNow);
            if (frame.Type is Http2FrameType.Headers or Http2FrameType.Continuation)
            {
                foreach (var h in raw.Connection.DecodeHeaders(frame.Payload))
                {
                    if (h.Name == ":status")
                    {
                        Assert.AreEqual("204", h.Value);
                        return;
                    }
                }
            }

            if (frame.Type is Http2FrameType.RstStream or Http2FrameType.GoAway)
                Assert.Fail($"Host alongside :authority must not fail; got {frame.Type}");
        }

        Assert.Fail("Timed out waiting for 204.");
    }
}
