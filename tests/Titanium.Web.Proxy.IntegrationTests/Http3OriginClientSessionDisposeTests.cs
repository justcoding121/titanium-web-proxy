#pragma warning disable CA1416
#pragma warning disable TWP001

using System;
using System.Diagnostics;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http3;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Regression-2026-10-07: "HTTP/3 origin background drains did not finish cleanly" (21 in the session log).
///     Disposing an origin client session whose peer streams are still open must finish promptly instead of
///     burning the whole 2 s join budget with leaked drain tasks.
/// </summary>
[TestClass]
public class Http3OriginClientSessionDisposeTests
{
    [TestMethod]
    [TestCategory("Regression-2026-10-07")]
    [Timeout(60 * 1000)]
    public Task Dispose_WithOpenPeerStreams_CompletesPromptly() => RunAsync(killPeerFirst: false);

    [TestMethod]
    [TestCategory("Regression-2026-10-07")]
    [Timeout(60 * 1000)]
    public Task Dispose_AfterPeerAbort_CompletesPromptly() => RunAsync(killPeerFirst: true);

    private static async Task RunAsync(bool killPeerFirst)
    {
        if (!QuicListener.IsSupported || !QuicConnection.IsSupported)
            Assert.Inconclusive("MsQuic / System.Net.Quic is not supported on this platform.");

        var origin = new QuicHttp3OriginServer(TestCertificateAuthority.ServerCertificate);
        await using var connection = await QuicConnection.ConnectAsync(new QuicClientConnectionOptions
        {
            RemoteEndPoint = new DnsEndPoint("localhost", origin.Port),
            MaxInboundUnidirectionalStreams = 10,
            MaxInboundBidirectionalStreams = 0,
            DefaultStreamErrorCode = (long)Http3ErrorCode.RequestCancelled,
            DefaultCloseErrorCode = (long)Http3ErrorCode.NoError,
            ClientAuthenticationOptions = new SslClientAuthenticationOptions
            {
                ApplicationProtocols = new List<SslApplicationProtocol> { SslApplicationProtocol.Http3 },
                TargetHost = "localhost",
                RemoteCertificateValidationCallback = (_, _, _, _) => true,
            },
        });

        var proxy = new ProxyServer(false, false, false);
        var session = new Http3OriginClientSession(connection, proxy);
        await session.StartAsync(default);

        // Let the origin's control stream arrive so a drain task is parked in a read.
        // (PeerSettings is only published once the control stream ends, so it cannot be awaited here.)
        var deadline = Stopwatch.StartNew();
        while (origin.AcceptedConnectionCount < 1 && deadline.ElapsedMilliseconds < 5000)
            await Task.Delay(25);
        await Task.Delay(500);
        if (killPeerFirst)
        {
            await origin.DisposeAsync();
            await Task.Delay(300);
        }

        var sw = Stopwatch.StartNew();
        await session.DisposeAsync();
        sw.Stop();

        if (!killPeerFirst) await origin.DisposeAsync();

        Assert.IsTrue(sw.ElapsedMilliseconds < 1000,
            $"session dispose took {sw.ElapsedMilliseconds} ms; drain tasks outlived cancellation");
    }
}
