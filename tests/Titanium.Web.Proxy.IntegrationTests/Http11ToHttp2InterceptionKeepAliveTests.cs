using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Regression: H1→H2 intercept deliver must not write the response body twice after
///     <c>WriteResponseAsync</c> coalesces tiny Content-Length bodies (sets <c>IsBodySent</c>).
///     A second body write desyncs HTTP/1 keep-alive and was the GHA MITM H1→H2 sustain-0 bug.
/// </summary>
[TestClass]
[DoNotParallelize]
public class Http11ToHttp2InterceptionKeepAliveTests
{
    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task H1_To_H2c_WithInterception_KeepAlive_TwoGets_BothSucceed()
    {
        using var rawServer = Http2RawOriginServer.CreateCleartext();
        rawServer.HandleConnection(async connection =>
        {
            await connection.SendInitialSettingsAsync();
            for (var i = 0; i < 8; i++)
            {
                var (streamId, _, _) = await connection.ReadRequestAsync();
                var headers = connection.EncodeHeaders([(":status", "200")], Array.Empty<(string, string)>());
                await connection.WriteHeaderBlockAsync(streamId, headers, endStream: false);
                await connection.WriteFrameAsync(Http2FrameType.Data, streamId, Http2FrameFlag.EndStream,
                    "keepalive"u8.ToArray());
            }
        });

        var proxy = new ProxyServer(false, false, false) { EnableHttp2 = true };
        proxy.AddEndPoint(new TransparentProxyEndPoint(IPAddress.Loopback, 0, decryptSsl: false));
        proxy.Start();
        try
        {
            var beforeRequestHits = 0;
            var beforeResponseHits = 0;
            proxy.BeforeRequest += (_, _) =>
            {
                Interlocked.Increment(ref beforeRequestHits);
                return Task.CompletedTask;
            };
            proxy.BeforeResponse += (_, _) =>
            {
                Interlocked.Increment(ref beforeResponseHits);
                return Task.CompletedTask;
            };

            var endpoint = proxy.ProxyEndPoints.OfType<TransparentProxyEndPoint>().First();
            endpoint.ForwardHost = "127.0.0.1";
            endpoint.ForwardPort = rawServer.Port;
            endpoint.ForwardCleartext = true;
            endpoint.BeforeHttpAuthenticate += (_, e) =>
            {
                e.UpstreamHttpProtocol = UpstreamHttpProtocol.Http2;
                e.AllowHttpProtocolTranslation = true;
                return Task.CompletedTask;
            };

            Assert.IsTrue(proxy.NeedsHttpInterception());

            using var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, endpoint.Port);
            await using var stream = tcp.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, detectEncodingFromByteOrderMarks: false,
                bufferSize: 4096, leaveOpen: true);

            async Task<(int Status, string Body)> ExchangeAsync()
            {
                var request = "GET / HTTP/1.1\r\nHost: localhost\r\nConnection: keep-alive\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(request));

                var statusLine = await reader.ReadLineAsync();
                Assert.IsNotNull(statusLine);
                Assert.IsTrue(statusLine!.StartsWith("HTTP/1.1 ", StringComparison.Ordinal), statusLine);

                var status = int.Parse(statusLine.AsSpan("HTTP/1.1 ".Length, 3));
                var contentLength = -1;
                string? line;
                while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync()))
                {
                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                        contentLength = int.Parse(line.AsSpan("Content-Length:".Length).Trim());
                }

                Assert.IsTrue(contentLength >= 0, "Expected Content-Length from H1→H2 bridge.");
                var buf = new char[contentLength];
                var read = 0;
                while (read < contentLength)
                {
                    var n = await reader.ReadAsync(buf.AsMemory(read, contentLength - read));
                    Assert.IsTrue(n > 0, "Unexpected EOF reading body.");
                    read += n;
                }

                return (status, new string(buf));
            }

            var (status1, body1) = await ExchangeAsync();
            Assert.AreEqual(200, status1);
            Assert.AreEqual("keepalive", body1);

            var (status2, body2) = await ExchangeAsync();
            Assert.AreEqual(200, status2);
            Assert.AreEqual("keepalive", body2);

            Assert.AreEqual(2, beforeRequestHits, "Both requests must hit the session path.");
            Assert.AreEqual(2, beforeResponseHits);
        }
        finally
        {
            proxy.Stop();
            proxy.Dispose();
        }
    }
}
