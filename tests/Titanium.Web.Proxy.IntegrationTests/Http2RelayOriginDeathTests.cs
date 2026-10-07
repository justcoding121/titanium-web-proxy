using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Regression-2026-10-07 (ledger item 12): when the h2 origin vanishes while a client stream is in flight
///     on an h2-to-h2 relay, the client must be told (RST_STREAM, GOAWAY or an error response) instead of the
///     whole connection being closed bare, which browsers surface as ERR_HTTP2_PROTOCOL_ERROR.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http2RelayOriginDeathTests
{
    [TestMethod]
    [TestCategory("Regression-2026-10-07")]
    [Timeout(60 * 1000)]
    public async Task OriginClosesWithStreamInFlight_ClientGetsExplicitSignal() =>
        await RunAsync(originWaitsBeforeClose: TimeSpan.Zero);

    [TestMethod]
    [TestCategory("Regression-2026-10-07")]
    [Timeout(60 * 1000)]
    public async Task OriginClosesAfterIdlePause_ClientGetsExplicitSignal() =>
        await RunAsync(originWaitsBeforeClose: TimeSpan.FromMilliseconds(700));

    private static async Task RunAsync(TimeSpan originWaitsBeforeClose)
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async connection =>
        {
            await connection.SendInitialSettingsAsync();
            _ = await connection.ReadRequestAsync();

            // Read the request, never answer, and die.
            if (originWaitsBeforeClose > TimeSpan.Zero)
                await Task.Delay(originWaitsBeforeClose);
        });

        using var testSuite = new TestSuite();
        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;

        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, "localhost", origin.Port);
        var block = raw.Connection.EncodeHeaders(
            new[] { (":method", "GET"), (":scheme", "https"), (":authority", "localhost"), (":path", "/") },
            Array.Empty<(string, string)>());
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: true);

        var signal = await ReadUntilSignalAsync(raw, TimeSpan.FromSeconds(15));
        Assert.AreNotEqual("bare-close", signal,
            "the proxy closed the client connection without RST_STREAM / GOAWAY / an error response");
        Assert.AreNotEqual("timeout", signal, "the client was left hanging after the origin died");
    }

    private static async Task<string> ReadUntilSignalAsync(Http2RawClient raw, TimeSpan budget)
    {
        using var cts = new CancellationTokenSource(budget);
        try
        {
            while (true)
            {
                var frame = await raw.Connection.ReadFrameAsync().WaitAsync(cts.Token);
                switch (frame.Type)
                {
                    case Http2FrameType.RstStream when frame.StreamId == 1:
                        return "rst-stream";
                    case Http2FrameType.GoAway:
                        return "goaway";
                    case Http2FrameType.Headers when frame.StreamId == 1:
                        return "response";
                }
            }
        }
        catch (OperationCanceledException)
        {
            return "timeout";
        }
        catch (Exception)
        {
            return "bare-close";
        }
    }
}
