using System;
using System.Buffers.Binary;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat <c>Http2Helper.Copy</c> suite: origin GOAWAY after partial DATA must not wedge the client stream.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http2CopyOriginGoAwayMidDataFatGapTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _) =>
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    /// <summary>
    ///     Bug class: origin GOAWAY mid-body leaves the MITM client hung without RST or GOAWAY.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Origin_GoAwayAfterPartialData_ClientGetsError()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            var (streamId, _, _) = await conn.ReadRequestAsync();
            var resp = conn.EncodeHeaders(
                [(":status", "200")],
                [("content-type", "text/plain"), ("content-length", "8")]);
            await conn.WriteHeaderBlockAsync(streamId, resp, endStream: false);
            await conn.WriteFrameAsync(Http2FrameType.Data, streamId, 0, Encoding.ASCII.GetBytes("pa"));

            var goAway = new byte[8];
            BinaryPrimitives.WriteInt32BigEndian(goAway.AsSpan(0, 4), streamId);
            BinaryPrimitives.WriteInt32BigEndian(goAway.AsSpan(4, 4), (int)Http2ErrorCode.NoError);
            await conn.WriteFrameAsync(Http2FrameType.GoAway, 0, 0, goAway);
            try { while (true) await conn.ReadFrameAsync(); } catch { }
        });

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;

        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);
        var block = raw.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "https"), (":authority", $"{uri.Host}:{uri.Port}"), (":path", "/mid")],
            []);
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: true);

        var sawError = false;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            var frame = await raw.Connection.ReadFrameAsync().WaitAsync(deadline - DateTime.UtcNow);
            if (frame.Type is Http2FrameType.GoAway or Http2FrameType.RstStream)
            {
                sawError = true;
                break;
            }

            if (frame.Type is Http2FrameType.Headers or Http2FrameType.Continuation)
            {
                var status = raw.Connection.DecodeHeaders(frame.Payload)
                    .FirstOrDefault(h => h.Name == ":status").Value;
                if (status is not null and not "200")
                {
                    sawError = true;
                    break;
                }
            }
        }

        Assert.IsTrue(sawError, "Partial DATA then origin GOAWAY must surface as error to the client.");
    }
}
