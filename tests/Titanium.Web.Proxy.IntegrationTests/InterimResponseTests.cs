using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Integration tests for interim (1xx) response handling in
///     <c>ResponseHandler.HandleHttpSessionResponse</c>. Previously only 100 Continue was recognized;
///     any other 1xx (e.g. 103 Early Hints) was mistakenly treated as the final response - forwarded to the
///     client as-is, with the proxy then trying to read the *next* HTTP message on the connection as if it
///     were a brand new request/response pair. These tests assert the corrected behavior: every interim
///     response is relayed (100 is still discarded, matching the documented "client can simply discard this
///     interim response" behavior) and the proxy keeps reading interim responses on the connection until
///     the true final response arrives. 101 Switching Protocols is verified separately, since it is
///     deliberately excluded from the loop (it *is* the final message of the exchange).
///     <para>
///         Uses raw <see cref="TcpClient" />/<see cref="Setup.TestServer.HandleTcpRequest" /> on both sides
///         because .NET's <c>HttpClient</c> does not reliably surface arbitrary 1xx responses (other than
///         100/101, which it has dedicated handling for) to application code.
///     </para>
/// </summary>
[DoNotParallelize]
[TestClass]
public class InterimResponseTests
{
    private static TestServer sharedServer;

    [ClassInitialize]
    public static void ClassSetup(TestContext _)
    {
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);
    }

    [ClassCleanup]
    public static void ClassCleanup()
    {
        sharedServer?.Dispose();
    }

    private static readonly Encoding MsgEncoding = HttpHelper.GetEncodingFromContentType(null);

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task Interim_103_EarlyHints_Response_Is_Relayed_Before_Final_Response()
    {
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();

        server.HandleTcpRequest(async context =>
        {
            await DrainRequestHeaders(context);

            var raw = MsgEncoding.GetBytes(
                "HTTP/1.1 103 Early Hints\r\n" +
                "Link: </style.css>; rel=preload\r\n" +
                "\r\n" +
                "HTTP/1.1 200 OK\r\n" +
                "Content-Length: 5\r\n" +
                "\r\n" +
                "hello");

            await context.Transport.Output.WriteAsync(raw);
            context.Transport.Output.Complete();
        });

        var proxy = testSuite.GetReverseProxy();
        proxy.BeforeRequest += (sender, e) =>
        {
            e.HttpClient.Request.Url = server.ListeningTcpUrl;
            return Task.CompletedTask;
        };

        var responseText = await SendRawRequestAndReadResponse(proxy,
            "GET / HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n", TimeSpan.FromSeconds(8));

        var firstStatusIndex = responseText.IndexOf("HTTP/1.1 103", StringComparison.Ordinal);
        var finalStatusIndex = responseText.IndexOf("HTTP/1.1 200", StringComparison.Ordinal);

        Assert.IsTrue(firstStatusIndex >= 0,
            "The 103 Early Hints interim response should have been relayed to the client.");
        Assert.IsTrue(finalStatusIndex > firstStatusIndex,
            "The final 200 response should follow the interim response.");
        Assert.IsTrue(responseText.Contains("Link: </style.css>; rel=preload", StringComparison.Ordinal),
            "The interim response's own headers should have been relayed too.");
        Assert.IsTrue(responseText.EndsWith("hello", StringComparison.Ordinal),
            "The final response's body should still be relayed correctly.");
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task Interim_Multiple_1xx_Responses_Are_All_Relayed_In_Order_Before_Final_Response()
    {
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();

        server.HandleTcpRequest(async context =>
        {
            await DrainRequestHeaders(context);

            var raw = MsgEncoding.GetBytes(
                "HTTP/1.1 103 Early Hints\r\nX-Seq: 1\r\n\r\n" +
                "HTTP/1.1 103 Early Hints\r\nX-Seq: 2\r\n\r\n" +
                "HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\nok");

            await context.Transport.Output.WriteAsync(raw);
            context.Transport.Output.Complete();
        });

        var proxy = testSuite.GetReverseProxy();
        proxy.BeforeRequest += (sender, e) =>
        {
            e.HttpClient.Request.Url = server.ListeningTcpUrl;
            return Task.CompletedTask;
        };

        var responseText = await SendRawRequestAndReadResponse(proxy,
            "GET / HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n", TimeSpan.FromSeconds(8));

        var seq1Index = responseText.IndexOf("X-Seq: 1", StringComparison.Ordinal);
        var seq2Index = responseText.IndexOf("X-Seq: 2", StringComparison.Ordinal);
        var finalIndex = responseText.IndexOf("HTTP/1.1 200", StringComparison.Ordinal);

        Assert.IsTrue(seq1Index >= 0 && seq2Index > seq1Index && finalIndex > seq2Index,
            $"Expected both interim responses relayed in order before the final response. Got:\n{responseText}");
        Assert.IsTrue(responseText.EndsWith("ok", StringComparison.Ordinal));
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task Interim_100Continue_From_Server_Is_Discarded_Not_Relayed_To_Client()
    {
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();

        server.HandleTcpRequest(async context =>
        {
            await DrainRequestHeaders(context);

            var raw = MsgEncoding.GetBytes(
                "HTTP/1.1 100 Continue\r\n\r\n" +
                "HTTP/1.1 200 OK\r\nContent-Length: 5\r\n\r\nhello");

            await context.Transport.Output.WriteAsync(raw);
            context.Transport.Output.Complete();
        });

        var proxy = testSuite.GetReverseProxy();
        proxy.BeforeRequest += (sender, e) =>
        {
            e.HttpClient.Request.Url = server.ListeningTcpUrl;
            return Task.CompletedTask;
        };

        var responseText = await SendRawRequestAndReadResponse(proxy,
            "GET / HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n", TimeSpan.FromSeconds(8));

        Assert.IsFalse(responseText.Contains("100 Continue", StringComparison.Ordinal),
            "100 Continue must still be discarded rather than relayed to the client (per spec, it is safe to discard).");
        Assert.IsTrue(responseText.StartsWith("HTTP/1.1 200", StringComparison.Ordinal));
        Assert.IsTrue(responseText.EndsWith("hello", StringComparison.Ordinal));
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task SwitchingProtocols_101_Response_Is_Relayed_Exactly_Once_Not_Looped_As_Interim()
    {
        // Regression guard for the loop's exclusion of 101: 100-199 broadly includes 101, so a naive
        // interim-response loop would incorrectly try to read yet another response after it (hanging, since
        // the "connection" is about to become a raw tunnel and nothing else will arrive framed as HTTP).
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();

        server.HandleTcpRequest(async context =>
        {
            await DrainRequestHeaders(context);

            var raw = MsgEncoding.GetBytes(
                "HTTP/1.1 101 Switching Protocols\r\n" +
                "Upgrade: custom-protocol\r\n" +
                "Connection: Upgrade\r\n" +
                "\r\n");

            await context.Transport.Output.WriteAsync(raw);
            context.Transport.Output.Complete();
        });

        var proxy = testSuite.GetReverseProxy();
        proxy.BeforeRequest += (sender, e) =>
        {
            e.HttpClient.Request.Url = server.ListeningTcpUrl;
            return Task.CompletedTask;
        };

        var responseText = await SendRawRequestAndReadResponse(proxy,
            "GET / HTTP/1.1\r\nHost: localhost\r\nUpgrade: custom-protocol\r\nConnection: Upgrade\r\n\r\n",
            TimeSpan.FromSeconds(5));

        Assert.IsTrue(responseText.StartsWith("HTTP/1.1 101", StringComparison.Ordinal));
        Assert.AreEqual(1, CountOccurrences(responseText, "HTTP/1.1 101"),
            "The 101 response must be relayed exactly once, never looped as if it were a discardable interim response.");
        Assert.AreEqual(1, CountOccurrences(responseText, "HTTP/1.1"),
            "Nothing else should have been read/relayed as a second message after the 101.");
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) != -1)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    private static async Task DrainRequestHeaders(Microsoft.AspNetCore.Connections.ConnectionContext context)
    {
        var requestText = string.Empty;
        while (!requestText.Contains("\r\n\r\n", StringComparison.Ordinal))
        {
            var result = await context.Transport.Input.ReadAsync();
            foreach (var seg in result.Buffer) requestText += MsgEncoding.GetString(seg.Span);
            context.Transport.Input.AdvanceTo(result.Buffer.End);
        }
    }

    /// <summary>
    ///     Sends a hand-built raw HTTP request directly to the proxy and accumulates whatever bytes come
    ///     back within <paramref name="readTimeout" />. A bounded timeout (rather than reading to end-of-stream)
    ///     is used deliberately so a test can't hang forever if a scenario leaves the connection open (e.g.
    ///     after switching protocols) instead of closing it.
    /// </summary>
    private static async Task<string> SendRawRequestAndReadResponse(ProxyServer proxy, string rawRequest,
        TimeSpan readTimeout)
    {
        using var tcpClient = new TcpClient();
        await tcpClient.ConnectAsync(IPAddress.Loopback, proxy.ProxyEndPoints[0].Port);
        var stream = tcpClient.GetStream();

        await stream.WriteAsync(MsgEncoding.GetBytes(rawRequest));

        using var ms = new MemoryStream();
        var buffer = new byte[8192];
        using var cts = new CancellationTokenSource(readTimeout);
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer, 0, buffer.Length, cts.Token);
                if (read == 0) break;
                ms.Write(buffer, 0, read);
            }
        }
        catch (OperationCanceledException)
        {
            // Timed out waiting for more data - the connection was likely kept open (e.g. pooled, or
            // switched to a raw tunnel after a 101). Return whatever was received so far.
        }

        return MsgEncoding.GetString(ms.ToArray());
    }
}
