using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Failure and framing behavior of the H1 terminate-lite path against a hand-written origin: a pooled origin
///     socket that went stale, bodiless responses on a kept-alive socket, close-delimited bodies, and what the
///     origin actually receives. Each test names the bug class it guards.
/// </summary>
[TestClass]
[DoNotParallelize]
public class H1TerminateLiteResilienceTests
{
    private static readonly TimeSpan Step = TimeSpan.FromSeconds(15);

    private static (ProxyServer Proxy, TransparentProxyEndPoint EndPoint) StartProxy(int originPort)
    {
        var proxy = new ProxyServer(false, false, false) { EnableHttp2 = false };
        proxy.CertificateManager.RootCertificateName = TestCertificateAuthority.RootCertificateName;
        proxy.CertificateManager.RootCertificate = TestCertificateAuthority.RootCertificate;
        proxy.ServerCertificateValidationCallback += (_, args) =>
        {
            args.IsValid = TestCertificateAuthority.Validate(args.Certificate, args.SslPolicyErrors);
            return Task.CompletedTask;
        };

        var endPoint = new TransparentProxyEndPoint(IPAddress.Loopback, 0, decryptSsl: true)
        {
            GenericCertificate = TestCertificateAuthority.ServerCertificate,
            ForwardHost = "127.0.0.1",
            ForwardPort = originPort,
            ForwardCleartext = true,
        };
        proxy.AddEndPoint(endPoint);
        proxy.Start();
        return (proxy, endPoint);
    }

    private static HttpClient MakeClient() => new(new HttpClientHandler
    {
        UseProxy = false,
        ServerCertificateCustomValidationCallback = (_, cert, _, _) => cert is not null,
    })
    {
        Timeout = Step,
    };

    /// <summary>Raw plain-TCP origin; the handler gets each accepted connection's stream and its index.</summary>
    private sealed class RawOrigin : IAsyncDisposable
    {
        private readonly TcpListener listener;
        private readonly Func<Stream, int, Task> handler;
        private readonly CancellationTokenSource stopping = new();
        private int connections;

        private RawOrigin(TcpListener listener, Func<Stream, int, Task> handler)
        {
            this.listener = listener;
            this.handler = handler;
            _ = AcceptLoopAsync(stopping.Token);
        }

        public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

        public int Connections => Volatile.Read(ref connections);

        public static RawOrigin Start(Func<Stream, int, Task> handler)
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            return new RawOrigin(l, handler);
        }

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(ct);
                }
                catch
                {
                    return;
                }

                var index = Interlocked.Increment(ref connections);
                _ = Task.Run(async () =>
                {
                    using (client)
                    {
                        try
                        {
                            await handler(client.GetStream(), index);
                        }
                        catch
                        {
                            // Per-connection failure is the test's business, not the accept loop's.
                        }
                    }
                });
            }
        }

        public async ValueTask DisposeAsync()
        {
            await stopping.CancelAsync();
            listener.Stop();
            stopping.Dispose();
        }
    }

    /// <summary>Reads one request head (up to the blank line). Null when the peer closed first.</summary>
    private static async Task<string?> ReadRequestHeadAsync(Stream s)
    {
        var sb = new StringBuilder();
        var one = new byte[1];
        while (true)
        {
            if (await s.ReadAsync(one) == 0)
                return sb.Length == 0 ? null : sb.ToString();

            sb.Append((char)one[0]);
            if (sb.Length >= 4 && sb[^4] == '\r' && sb[^3] == '\n' && sb[^2] == '\r' && sb[^1] == '\n')
                return sb.ToString();
        }
    }

    private static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);

    [TestMethod]
    [Timeout(60_000)]
    public async Task StalePooledOriginSocket_IsRedialedAndTheRequestStillSucceeds()
    {
        // Bug class: the origin closes an idle keep-alive socket; the next bodiless GET is written to the dead
        // pooled socket and the client gets a 502/reset instead of a transparent replay on a fresh connection.
        await using var origin = RawOrigin.Start(async (s, index) =>
        {
            await ReadRequestHeadAsync(s);
            var body = "response-" + index;
            await s.WriteAsync(Ascii(
                $"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: keep-alive\r\n\r\n{body}"));
            // Returning disposes the TcpClient: the proxy's pooled socket is now closed by the origin.
        });

        var (proxy, ep) = StartProxy(origin.Port);
        try
        {
            using var client = MakeClient();
            var first = await client.GetAsync($"https://127.0.0.1:{ep.Port}/one");
            Assert.AreEqual(HttpStatusCode.OK, first.StatusCode);
            Assert.AreEqual("response-1", await first.Content.ReadAsStringAsync());

            await Task.Delay(500); // let the origin's FIN reach the proxy's pooled socket

            var second = await client.GetAsync($"https://127.0.0.1:{ep.Port}/two");
            Assert.AreEqual(HttpStatusCode.OK, second.StatusCode, "stale pooled socket must not surface to the client");
            Assert.AreEqual("response-2", await second.Content.ReadAsStringAsync());
            Assert.AreEqual(2, origin.Connections, "second request must have used a freshly dialed origin socket");
        }
        finally
        {
            proxy.Stop();
            proxy.Dispose();
        }
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task HeadThenGet_OnOneKeepAliveSocket_DoesNotDesyncFraming()
    {
        // Bug class: a HEAD response advertises Content-Length but has no body; if the lite path waits for those
        // bytes it swallows the next response (or hangs) on the shared keep-alive client connection.
        await using var origin = RawOrigin.Start(async (s, _) =>
        {
            while (await ReadRequestHeadAsync(s) is { } head)
            {
                if (head.StartsWith("HEAD ", StringComparison.Ordinal))
                {
                    await s.WriteAsync(Ascii("HTTP/1.1 200 OK\r\nContent-Length: 5\r\nConnection: keep-alive\r\n\r\n"));
                }
                else
                {
                    await s.WriteAsync(Ascii(
                        "HTTP/1.1 200 OK\r\nContent-Length: 5\r\nConnection: keep-alive\r\n\r\nhello"));
                }
            }
        });

        var (proxy, ep) = StartProxy(origin.Port);
        try
        {
            using var client = MakeClient();
            using var head = await client.SendAsync(
                new HttpRequestMessage(HttpMethod.Head, $"https://127.0.0.1:{ep.Port}/h"));
            Assert.AreEqual(HttpStatusCode.OK, head.StatusCode);
            Assert.AreEqual(5, head.Content.Headers.ContentLength);

            var get = await client.GetAsync($"https://127.0.0.1:{ep.Port}/g");
            Assert.AreEqual(HttpStatusCode.OK, get.StatusCode);
            Assert.AreEqual("hello", await get.Content.ReadAsStringAsync());
        }
        finally
        {
            proxy.Stop();
            proxy.Dispose();
        }
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task CloseDelimitedBody_IsRelayedUntilTheOriginCloses()
    {
        // Bug class: a response with neither Content-Length nor chunking (body ends at origin close) is relayed as
        // empty, or the client waits forever because the proxy never ends the message.
        var payload = new string('x', 5000);
        await using var origin = RawOrigin.Start(async (s, _) =>
        {
            await ReadRequestHeadAsync(s);
            await s.WriteAsync(Ascii("HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nConnection: close\r\n\r\n" + payload));
        });

        var (proxy, ep) = StartProxy(origin.Port);
        try
        {
            using var client = MakeClient();
            var response = await client.GetAsync($"https://127.0.0.1:{ep.Port}/until-close");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual(payload, await response.Content.ReadAsStringAsync());
        }
        finally
        {
            proxy.Stop();
            proxy.Dispose();
        }
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task TruncatedKnownLengthBody_IsNeverPresentedAsComplete_AndTheNextRequestStillWorks()
    {
        // Bug class: the origin dies before sending the declared Content-Length; the proxy rewrites the length to
        // what arrived (a truncated body looks complete) or reuses the half-read socket for the next request.
        await using var origin = RawOrigin.Start(async (s, index) =>
        {
            await ReadRequestHeadAsync(s);
            if (index == 1)
            {
                await s.WriteAsync(Ascii("HTTP/1.1 200 OK\r\nContent-Length: 1000\r\nConnection: keep-alive\r\n\r\nshort"));
                return; // closes after 5 of 1000 bytes
            }

            await s.WriteAsync(Ascii("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"));
        });

        var (proxy, ep) = StartProxy(origin.Port);
        try
        {
            using var client = MakeClient();
            var completed = false;
            try
            {
                var response = await client.GetAsync($"https://127.0.0.1:{ep.Port}/trunc");
                completed = (await response.Content.ReadAsByteArrayAsync()).Length == 1000;
            }
            catch (HttpRequestException)
            {
                // Expected: the declared length was never satisfied.
            }
            catch (IOException)
            {
            }

            Assert.IsFalse(completed, "a 5-byte body must not be presented as the declared 1000 bytes");

            var next = await client.GetAsync($"https://127.0.0.1:{ep.Port}/next");
            Assert.AreEqual(HttpStatusCode.OK, next.StatusCode);
            Assert.AreEqual("ok", await next.Content.ReadAsStringAsync());
        }
        finally
        {
            proxy.Stop();
            proxy.Dispose();
        }
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task ForwardedRequest_KeepsTargetAndDropsClientConnectionClose()
    {
        // Bug class: the client's hop-by-hop "Connection: close" is forwarded, forcing the origin to close (no
        // origin pooling), or the path/query is rewritten on its way to the fixed forward host.
        var heads = new ConcurrentQueue<string>();
        await using var origin = RawOrigin.Start(async (s, _) =>
        {
            if (await ReadRequestHeadAsync(s) is { } head)
                heads.Enqueue(head);
            await s.WriteAsync(Ascii("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: keep-alive\r\n\r\nok"));
        });

        var (proxy, ep) = StartProxy(origin.Port);
        try
        {
            using var client = MakeClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://127.0.0.1:{ep.Port}/a/b?x=1&y=two");
            request.Headers.ConnectionClose = true;
            request.Headers.Add("X-Trace", "abc");
            var response = await client.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

            Assert.IsTrue(heads.TryDequeue(out var seen));
            StringAssert.StartsWith(seen, "GET /a/b?x=1&y=two HTTP/1.1\r\n");
            StringAssert.Contains(seen, "X-Trace: abc");
            Assert.IsFalse(seen.Contains("Connection: close", StringComparison.OrdinalIgnoreCase),
                "client hop-by-hop Connection: close must not reach the origin: " + seen);
        }
        finally
        {
            proxy.Stop();
            proxy.Dispose();
        }
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task OriginDown_ReturnsAnErrorInsteadOfHanging()
    {
        // Bug class: a refused origin dial on the lite path leaves the client connection open with no response.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var closedPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var (proxy, ep) = StartProxy(closedPort);
        try
        {
            using var client = MakeClient();
            try
            {
                var response = await client.GetAsync($"https://127.0.0.1:{ep.Port}/down");
                Assert.IsTrue((int)response.StatusCode >= 500, "origin failure must surface as a 5xx, got " + response.StatusCode);
            }
            catch (HttpRequestException)
            {
                // Closing the client connection is also a prompt, explicit failure.
            }
        }
        finally
        {
            proxy.Stop();
            proxy.Dispose();
        }
    }
}
