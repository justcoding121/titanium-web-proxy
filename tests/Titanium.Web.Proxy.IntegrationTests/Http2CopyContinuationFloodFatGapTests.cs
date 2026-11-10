using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Options;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat <c>Http2Helper.Copy</c> suite: CONTINUATION flood frame-count bound on an open header block.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http2CopyContinuationFloodFatGapTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _) =>
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    /// <summary>
    ///     Bug class: endless zero-length CONTINUATION frames wedge the copy leg instead of GOAWAY.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Client_ContinuationFlood_ExceedsFrameBudget_GoAwayEnhanceYourCalm()
    {
        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.ResourceLimits = ProxyResourceLimits.Create(
            maxHeaderLineBytes: 64 * 1024,
            maxHeaderCount: 256,
            maxHeaderAggregateBytes: 256 * 1024,
            maxEncodedBodyBytes: null,
            maxDecodedBodyBytes: null,
            maxDecompressionRatio: 200,
            maxConcurrentClients: null,
            maxConcurrentStreamsPerConnection: 100,
            maxPeerInitiatedIncompleteStreamResets: 100,
            maxOpenHeaderBlockFrames: 5,
            maxOpenHeaderBlockDuration: TimeSpan.FromSeconds(10),
            connectionPoolingEnabled: true,
            maxCachedConnectionsPerHost: 4,
            maxCertificateCacheEntries: null);

        var serverUri = new Uri(sharedServer.ListeningHttpsUrl);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, serverUri.Host, serverUri.Port);

        var goAwayTask = ReadUntilGoAwayAsync(raw.Connection, TimeSpan.FromSeconds(12));
        await raw.Connection.WriteFrameAsync(Http2FrameType.Headers, 1, 0, Array.Empty<byte>());
        for (var i = 0; i < 12; i++)
        {
            try
            {
                await raw.Connection.WriteFrameAsync(Http2FrameType.Continuation, 1, 0, Array.Empty<byte>());
            }
            catch (IOException) { break; }
        }

        Assert.AreEqual(Http2ErrorCode.EnhanceYourCalm, await goAwayTask);
    }

    private static async Task<Http2ErrorCode> ReadUntilGoAwayAsync(Http2RawFrame.Connection connection, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        while (true)
        {
            Http2RawFrame.Frame frame;
            try
            {
                frame = await connection.ReadFrameAsync().WaitAsync(cts.Token);
            }
            catch (Exception ex) when (cts.IsCancellationRequested)
            {
                Assert.Fail("Timed out waiting for GOAWAY: " + ex.Message);
                return default;
            }

            if (frame.Type != Http2FrameType.GoAway)
                continue;
            var ec = (frame.Payload[4] << 24) | (frame.Payload[5] << 16) |
                     (frame.Payload[6] << 8) | frame.Payload[7];
            return (Http2ErrorCode)ec;
        }
    }
}
