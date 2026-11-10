using System;
using System.Buffers.Binary;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat <c>Http2Helper.Copy.Headers</c> suite: <c>:protocol</c> on non-CONNECT,
///     duplicate <c>:scheme</c> / uppercase field names, and empty <c>:path</c>.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http2CopyHeadersProtocolOnGetFatGapTests
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

    /// <summary>
    ///     Bug class: <c>:protocol</c> on a GET is accepted and treated as extended CONNECT.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task ProtocolPseudo_OnGet_IsRejected()
    {
        var contacted = false;
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            try
            {
                _ = await conn.ReadRequestAsync();
                contacted = true;
            }
            catch { }
        });

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableHttpInterception = true;
        proxy.EnableRfc8441 = true;

        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);
        var block = raw.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "https"),
             (":authority", $"{uri.Host}:{uri.Port}"), (":path", "/"),
             (":protocol", "websocket")],
            []);
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: true);

        var (type, code) = await ReadRstOrGoAwayAsync(raw, TimeSpan.FromSeconds(10));
        Assert.IsTrue(type is Http2FrameType.RstStream or Http2FrameType.GoAway, $"got {type}");
        if (type == Http2FrameType.RstStream)
            Assert.AreEqual((int)Http2ErrorCode.ProtocolError, code);
        Assert.IsFalse(contacted);
    }

    /// <summary>
    ///     Bug class: uppercase field names are normalized instead of PROTOCOL_ERROR.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task UppercaseFieldName_IsRejected()
    {
        var contacted = false;
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            try
            {
                _ = await conn.ReadRequestAsync();
                contacted = true;
            }
            catch { }
        });

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableHttpInterception = true;

        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);
        var block = raw.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "https"),
             (":authority", $"{uri.Host}:{uri.Port}"), (":path", "/")],
            [("X-Upper", "1")]);
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: true);

        var (type, _) = await ReadRstOrGoAwayAsync(raw, TimeSpan.FromSeconds(10));
        Assert.IsTrue(type is Http2FrameType.RstStream or Http2FrameType.GoAway, $"got {type}");
        Assert.IsFalse(contacted);
    }

    /// <summary>
    ///     Bug class: empty <c>:path</c> is accepted and forwarded.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task EmptyPath_IsRejected()
    {
        var contacted = false;
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            try
            {
                _ = await conn.ReadRequestAsync();
                contacted = true;
            }
            catch { }
        });

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableHttpInterception = true;

        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);
        var block = raw.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "https"),
             (":authority", $"{uri.Host}:{uri.Port}"), (":path", "")],
            []);
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: true);

        var (type, _) = await ReadRstOrGoAwayAsync(raw, TimeSpan.FromSeconds(10));
        Assert.IsTrue(type is Http2FrameType.RstStream or Http2FrameType.GoAway, $"got {type}");
        Assert.IsFalse(contacted);
    }
}
