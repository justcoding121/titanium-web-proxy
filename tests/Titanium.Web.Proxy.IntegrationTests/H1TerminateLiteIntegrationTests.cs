using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Integration coverage for the H1 terminate-lite fast path
///     (<c>ForwardH1TerminateLiteAsync</c> / <c>ForwardH1TerminateSessionLiteAsync</c>).
///     Each test names the bug class it guards. The proxy is configured as a transparent
///     reverse-proxy endpoint with <c>ForwardHost</c> set and <c>ForwardCleartext=true</c>
///     so the client-side uses TLS while the origin-side stays plaintext HTTP/1.1.
/// </summary>
[TestClass]
[DoNotParallelize]
public class H1TerminateLiteIntegrationTests
{
    private static readonly TimeSpan Step = TimeSpan.FromSeconds(15);

    // -----------------------------------------------------------------------
    // Rig helpers
    // -----------------------------------------------------------------------

    private static (ProxyServer Proxy, TransparentProxyEndPoint EndPoint) StartProxy(
        int originPort,
        Action<TransparentProxyEndPoint>? configure = null,
        Action<ProxyServer>? configureProxy = null)
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
        configure?.Invoke(endPoint);
        configureProxy?.Invoke(proxy);
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

    // -----------------------------------------------------------------------
    // Minimal raw HTTP/1.1 origin (plain TCP, no TLS)
    // -----------------------------------------------------------------------

    private sealed class Http11Origin : IAsyncDisposable
    {
        private readonly TcpListener listener;
        private readonly Func<Stream, Task> handler;
        private readonly CancellationTokenSource stopping = new();
        private int connections;

        private Http11Origin(TcpListener listener, Func<Stream, Task> handler)
        {
            this.listener = listener;
            this.handler = handler;
            _ = AcceptLoopAsync(stopping.Token);
        }

        public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
        public int Connections => Volatile.Read(ref connections);

        public static Http11Origin Start(Func<Stream, Task> handler)
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            return new Http11Origin(l, handler);
        }

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(ct); }
                catch { return; }

                Interlocked.Increment(ref connections);
                _ = Task.Run(async () =>
                {
                    using (client)
                    {
                        try { await handler(client.GetStream()); }
                        catch { /* ignored */ }
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

    /// <summary>Drain HTTP/1.1 request headers by reading bytes until double CRLF.</summary>
    private static async Task DrainRequestAsync(Stream s)
    {
        var window = new byte[4];
        var pos = 0;
        var buf = new byte[1];
        while (true)
        {
            var n = await s.ReadAsync(buf);
            if (n == 0) return;
            window[pos % 4] = buf[0];
            pos++;
            if (pos >= 4)
            {
                // Check last 4 bytes for \r\n\r\n
                var i = (pos - 4) % 4;
                if (window[i % 4] == '\r' && window[(i + 1) % 4] == '\n' &&
                    window[(i + 2) % 4] == '\r' && window[(i + 3) % 4] == '\n')
                    return;
            }
        }
    }

    private static byte[] OkWithBody(string body, bool keepAlive = true, string? extraHeaders = null)
    {
        var bodyBytes = Encoding.ASCII.GetBytes(body);
        var connection = keepAlive ? "keep-alive" : "close";
        return Encoding.ASCII.GetBytes(
            $"HTTP/1.1 200 OK\r\n" +
            $"Content-Length: {bodyBytes.Length}\r\n" +
            $"Connection: {connection}\r\n" +
            (extraHeaders ?? string.Empty) +
            "\r\n" + body);
    }

    // -----------------------------------------------------------------------
    // Tests
    // -----------------------------------------------------------------------

    [TestMethod]
    [Timeout(30_000)]
    public async Task Get_SmallBody_CoalesceBuffer_ForwardedAndReturned()
    {
        // Bug class: coalesce buffer path skips the response or sends a wrong Content-Length.
        await using var origin = Http11Origin.Start(async s =>
        {
            await DrainRequestAsync(s);
            await s.WriteAsync(OkWithBody("coalesced-response"));
        });

        var (proxy, ep) = StartProxy(origin.Port);
        try
        {
            using var client = MakeClient();
            var r = await client.GetAsync($"https://127.0.0.1:{ep.Port}/coalesce");
            Assert.AreEqual(HttpStatusCode.OK, r.StatusCode);
            Assert.AreEqual("coalesced-response", await r.Content.ReadAsStringAsync());
        }
        finally { proxy.Stop(); proxy.Dispose(); }
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Head_NoBodyExpected_ProxyDoesNotHang()
    {
        // Bug class: HEAD on the lite path attempts to read a body that does not exist and blocks.
        await using var origin = Http11Origin.Start(async s =>
        {
            await DrainRequestAsync(s);
            var body = "response-body";
            var resp = $"HTTP/1.1 200 OK\r\nContent-Length: {Encoding.ASCII.GetByteCount(body)}\r\nConnection: close\r\n\r\n";
            await s.WriteAsync(Encoding.ASCII.GetBytes(resp));
            // Intentionally omit the body bytes — correct for HEAD per RFC.
        });

        var (proxy, ep) = StartProxy(origin.Port);
        try
        {
            using var client = MakeClient();
            var req = new HttpRequestMessage(HttpMethod.Head, $"https://127.0.0.1:{ep.Port}/head");
            var r = await client.SendAsync(req);
            Assert.AreEqual(HttpStatusCode.OK, r.StatusCode);
        }
        finally { proxy.Stop(); proxy.Dispose(); }
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task LargeBody_OverCoalesceLimit_StreamedRelay_AllBytesDelivered()
    {
        // Bug class: large body (>64 KiB coalesce threshold) sent through streaming relay
        // corrupts or truncates the payload.
        const int bodySize = 128 * 1024; // 128 KiB, above the 64 KiB coalesce limit
        var payload = new byte[bodySize];
        new Random(42).NextBytes(payload);

        await using var origin = Http11Origin.Start(async s =>
        {
            await DrainRequestAsync(s);
            var header = $"HTTP/1.1 200 OK\r\nContent-Length: {bodySize}\r\nConnection: close\r\n\r\n";
            await s.WriteAsync(Encoding.ASCII.GetBytes(header));
            await s.WriteAsync(payload);
        });

        var (proxy, ep) = StartProxy(origin.Port);
        try
        {
            using var client = MakeClient();
            var r = await client.GetAsync($"https://127.0.0.1:{ep.Port}/large");
            Assert.AreEqual(HttpStatusCode.OK, r.StatusCode);
            var received = await r.Content.ReadAsByteArrayAsync();
            CollectionAssert.AreEqual(payload, received);
        }
        finally { proxy.Stop(); proxy.Dispose(); }
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ChunkedBody_StreamedRelay_AllChunksDelivered()
    {
        // Bug class: chunked Transfer-Encoding body on the lite path is read twice or dropped.
        await using var origin = Http11Origin.Start(async s =>
        {
            await DrainRequestAsync(s);
            var header = "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n";
            await s.WriteAsync(Encoding.ASCII.GetBytes(header));
            await s.WriteAsync(Encoding.ASCII.GetBytes("6\r\nhello \r\n"));
            await s.WriteAsync(Encoding.ASCII.GetBytes("5\r\nworld\r\n"));
            await s.WriteAsync(Encoding.ASCII.GetBytes("0\r\n\r\n"));
        });

        var (proxy, ep) = StartProxy(origin.Port);
        try
        {
            using var client = MakeClient();
            var r = await client.GetAsync($"https://127.0.0.1:{ep.Port}/chunked");
            Assert.AreEqual(HttpStatusCode.OK, r.StatusCode);
            Assert.AreEqual("hello world", await r.Content.ReadAsStringAsync());
        }
        finally { proxy.Stop(); proxy.Dispose(); }
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task OriginTruncation_ClientDoesNotHang()
    {
        // Bug class: origin closes before declared Content-Length; proxy hangs waiting for
        // bytes that never arrive instead of closing the client connection.
        await using var origin = Http11Origin.Start(async s =>
        {
            await DrainRequestAsync(s);
            // Declare 1000 bytes but only send 5
            var header = "HTTP/1.1 200 OK\r\nContent-Length: 1000\r\nConnection: close\r\n\r\nshort";
            await s.WriteAsync(Encoding.ASCII.GetBytes(header));
            // Origin closes without sending the remaining 995 bytes.
        });

        var (proxy, ep) = StartProxy(origin.Port);
        try
        {
            using var client = MakeClient();
            try
            {
                var r = await client.GetAsync($"https://127.0.0.1:{ep.Port}/truncate",
                    HttpCompletionOption.ResponseHeadersRead);
                var body = await r.Content.ReadAsStringAsync();
                Assert.IsTrue(body.Length <= 1000, "Truncated body must not grow beyond declared length.");
            }
            catch (HttpRequestException)
            {
                // Acceptable: the connection was closed mid-response.
            }
        }
        finally { proxy.Stop(); proxy.Dispose(); }
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ConflictingContentLength_ReturnsBadGateway_NotForwardedToClient()
    {
        // Bug class: origin sends two Content-Length headers with different values (request-smuggling
        // primitive); proxy forwards rather than rejecting with 502 Bad Gateway.
        await using var origin = Http11Origin.Start(async s =>
        {
            await DrainRequestAsync(s);
            // Conflicting Content-Length values — RFC 9112 §6.3 requires rejection.
            var resp = "HTTP/1.1 200 OK\r\n" +
                       "Content-Length: 5\r\n" +
                       "Content-Length: 10\r\n" +
                       "Connection: close\r\n\r\n" +
                       "hello";
            await s.WriteAsync(Encoding.ASCII.GetBytes(resp));
        });

        var (proxy, ep) = StartProxy(origin.Port);
        try
        {
            using var client = MakeClient();
            var r = await client.GetAsync($"https://127.0.0.1:{ep.Port}/conflict-cl");
            Assert.AreEqual(HttpStatusCode.BadGateway, r.StatusCode,
                "Conflicting Content-Length values must be rejected with 502.");
        }
        finally { proxy.Stop(); proxy.Dispose(); }
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task BothTransferEncodingAndContentLength_ContentLengthStripped_ResponseDelivered()
    {
        // Bug class: a response with both Transfer-Encoding and Content-Length is incorrectly rejected
        // instead of being normalized (CL removed, TE takes precedence per RFC 9112 §6.3).
        await using var origin = Http11Origin.Start(async s =>
        {
            await DrainRequestAsync(s);
            var resp = "HTTP/1.1 200 OK\r\n" +
                       "Content-Length: 5\r\n" +
                       "Transfer-Encoding: chunked\r\n" +
                       "Connection: close\r\n\r\n" +
                       "5\r\nhello\r\n0\r\n\r\n";
            await s.WriteAsync(Encoding.ASCII.GetBytes(resp));
        });

        var (proxy, ep) = StartProxy(origin.Port);
        try
        {
            using var client = MakeClient();
            var r = await client.GetAsync($"https://127.0.0.1:{ep.Port}/both-te-cl");
            // RFC 9112: CL is removed, TE (chunked) drives framing — response is valid.
            Assert.AreEqual(HttpStatusCode.OK, r.StatusCode,
                "A response with TE+CL must be normalized (not rejected); CL is stripped.");
        }
        finally { proxy.Stop(); proxy.Dispose(); }
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task EmptyBody_204_ForwardedWithoutHanging()
    {
        // Bug class: empty 204 No Content causes the lite path to wait for body bytes that never arrive.
        await using var origin = Http11Origin.Start(async s =>
        {
            await DrainRequestAsync(s);
            await s.WriteAsync(Encoding.ASCII.GetBytes(
                "HTTP/1.1 204 No Content\r\nConnection: close\r\n\r\n"));
        });

        var (proxy, ep) = StartProxy(origin.Port);
        try
        {
            using var client = MakeClient();
            var r = await client.GetAsync($"https://127.0.0.1:{ep.Port}/empty");
            Assert.AreEqual(HttpStatusCode.NoContent, r.StatusCode);
        }
        finally { proxy.Stop(); proxy.Dispose(); }
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task NotFound_404_ForwardedCorrectly()
    {
        // Bug class: 4xx responses on the lite path are swallowed or replaced with a 502.
        await using var origin = Http11Origin.Start(async s =>
        {
            await DrainRequestAsync(s);
            await s.WriteAsync(Encoding.ASCII.GetBytes(
                "HTTP/1.1 404 Not Found\r\nContent-Length: 9\r\nConnection: close\r\n\r\nnot found"));
        });

        var (proxy, ep) = StartProxy(origin.Port);
        try
        {
            using var client = MakeClient();
            var r = await client.GetAsync($"https://127.0.0.1:{ep.Port}/missing");
            Assert.AreEqual(HttpStatusCode.NotFound, r.StatusCode);
        }
        finally { proxy.Stop(); proxy.Dispose(); }
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task BeforeRequest_NoopSubscriber_SessionLitePathUsed_BodyReturned()
    {
        // Bug class: ForwardH1TerminateSessionLiteAsync is never entered when a noop BeforeRequest
        // is present; the proxy skips it and goes to the full-session path needlessly.
        await using var origin = Http11Origin.Start(async s =>
        {
            await DrainRequestAsync(s);
            await s.WriteAsync(OkWithBody("session-lite-body"));
        });

        var (proxy, ep) = StartProxy(origin.Port);
        var beforeCalled = 0;
        proxy.BeforeRequest += (_, _) => { Interlocked.Increment(ref beforeCalled); return Task.CompletedTask; };
        try
        {
            using var client = MakeClient();
            var r = await client.GetAsync($"https://127.0.0.1:{ep.Port}/sessionlite");
            Assert.AreEqual(HttpStatusCode.OK, r.StatusCode);
            Assert.AreEqual("session-lite-body", await r.Content.ReadAsStringAsync());
            Assert.AreEqual(1, beforeCalled, "BeforeRequest must fire on the session-lite path.");
        }
        finally { proxy.Stop(); proxy.Dispose(); }
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task BeforeResponse_Subscriber_BodyAccessibleThroughEventArgs()
    {
        // Bug class: ForwardH1TerminateSessionLiteAsync coalesces the body but does not assign
        // response.Body before the BeforeResponse hook fires, so GetResponseBodyAsString returns null.
        await using var origin = Http11Origin.Start(async s =>
        {
            await DrainRequestAsync(s);
            await s.WriteAsync(OkWithBody("hook-accessible", keepAlive: false));
        });

        var (proxy, ep) = StartProxy(origin.Port);
        string? seenBody = null;
        proxy.BeforeResponse += async (_, e) =>
        {
            seenBody = await e.GetResponseBodyAsString();
        };
        try
        {
            using var client = MakeClient();
            var r = await client.GetAsync($"https://127.0.0.1:{ep.Port}/hook");
            Assert.AreEqual(HttpStatusCode.OK, r.StatusCode);
            Assert.AreEqual("hook-accessible", seenBody,
                "BeforeResponse must be able to read the response body on the session-lite path.");
        }
        finally { proxy.Stop(); proxy.Dispose(); }
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task InterimResponse_FallsBackToFullSessionPath_FinalResponseDelivered()
    {
        // Bug class: a 103 Early Hints from the origin raises H1TerminateLiteFallbackException;
        // the proxy must catch it and re-process via the full session path, not return an error.
        await using var origin = Http11Origin.Start(async s =>
        {
            await DrainRequestAsync(s);
            // RFC 8297 103 Early Hints then a normal 200
            await s.WriteAsync(Encoding.ASCII.GetBytes(
                "HTTP/1.1 103 Early Hints\r\n" +
                "Link: </style.css>; rel=preload\r\n\r\n"));
            await s.WriteAsync(OkWithBody("after-103", keepAlive: false));
        });

        var (proxy, ep) = StartProxy(origin.Port);
        try
        {
            using var client = MakeClient();
            var r = await client.GetAsync($"https://127.0.0.1:{ep.Port}/interim");
            // The final 200 with body must arrive; the 103 is informational and not the final response.
            Assert.AreEqual(HttpStatusCode.OK, r.StatusCode);
            Assert.AreEqual("after-103", await r.Content.ReadAsStringAsync());
        }
        finally { proxy.Stop(); proxy.Dispose(); }
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ForwardCleartext_False_ProxyUsesHttpsToTlsOrigin()
    {
        // Bug class: ForwardCleartext=false is the default; the proxy must open a TLS connection
        // to the origin when the client request arrives over HTTPS, not plain HTTP.
        // Uses a raw TLS origin (no Kestrel ALPN negotiation complexity).
        using var server = new Helpers.Http11OnlyOriginServer(TestCertificateAuthority.ServerCertificate);

        var proxy = new ProxyServer(false, false, false) { EnableHttp2 = false };
        proxy.CertificateManager.RootCertificateName = TestCertificateAuthority.RootCertificateName;
        proxy.CertificateManager.RootCertificate = TestCertificateAuthority.RootCertificate;
        proxy.ServerCertificateValidationCallback += (_, args) =>
        {
            args.IsValid = TestCertificateAuthority.Validate(args.Certificate, args.SslPolicyErrors);
            return Task.CompletedTask;
        };

        // ForwardCleartext=false (default): proxy opens TLS to the origin.
        // Use "localhost" so the SNI matches the server certificate's CN=localhost.
        var ep2 = new TransparentProxyEndPoint(IPAddress.Loopback, 0, decryptSsl: true)
        {
            GenericCertificate = TestCertificateAuthority.ServerCertificate,
            ForwardHost = "localhost",
            ForwardPort = server.Port,
            ForwardCleartext = false,
        };
        proxy.AddEndPoint(ep2);
        proxy.Start();
        try
        {
            using var client = MakeClient();
            var r = await client.GetAsync($"https://127.0.0.1:{ep2.Port}/tlscheck");
            Assert.AreEqual(HttpStatusCode.OK, r.StatusCode);
            Assert.AreEqual("h1-only-origin-ok", await r.Content.ReadAsStringAsync());
        }
        finally { proxy.Stop(); proxy.Dispose(); }
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task MultipleKeepAliveRequests_AllSucceed()
    {
        // Bug class: keep-alive request loop on the lite path exits after the first response.
        var shared = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);
        try
        {
            var requestCount = 0;
            shared.HandleRequest(context =>
            {
                Interlocked.Increment(ref requestCount);
                return context.Response.WriteAsync($"req-{requestCount}");
            });

            var (proxy, ep) = StartProxy(shared.HttpListeningPort);
            try
            {
                // Use a single HttpClient (which reuses the connection) for all 3 GET requests.
                using var client = MakeClient();
                for (var i = 1; i <= 3; i++)
                {
                    var r = await client.GetAsync($"https://127.0.0.1:{ep.Port}/item{i}");
                    Assert.AreEqual(HttpStatusCode.OK, r.StatusCode,
                        $"Request {i} must succeed on the keep-alive lite path.");
                }
            }
            finally { proxy.Stop(); proxy.Dispose(); }
        }
        finally { shared.Dispose(); }
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ConnectionClose_RequestHeader_LitePathHonorsIt()
    {
        // Bug class: lite path ignores Connection: close from the client, so the proxy keeps the
        // TLS connection open after the response; the client expects it closed.
        await using var origin = Http11Origin.Start(async s =>
        {
            await DrainRequestAsync(s);
            await s.WriteAsync(OkWithBody("bye", keepAlive: false));
        });

        var (proxy, ep) = StartProxy(origin.Port);
        try
        {
            using var handler = new HttpClientHandler
            {
                UseProxy = false,
                ServerCertificateCustomValidationCallback = (_, cert, _, _) => cert is not null,
            };
            using var client = new HttpClient(handler) { Timeout = Step };
            var req = new HttpRequestMessage(HttpMethod.Get, $"https://127.0.0.1:{ep.Port}/close");
            req.Headers.ConnectionClose = true;
            var r = await client.SendAsync(req);
            Assert.AreEqual(HttpStatusCode.OK, r.StatusCode);
            Assert.AreEqual("bye", await r.Content.ReadAsStringAsync());
        }
        finally { proxy.Stop(); proxy.Dispose(); }
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task OriginRedirect_302_ForwardedToClient()
    {
        // Bug class: 3xx on the lite path is not forwarded; proxy returns a synthetic error instead.
        await using var origin = Http11Origin.Start(async s =>
        {
            await DrainRequestAsync(s);
            await s.WriteAsync(Encoding.ASCII.GetBytes(
                "HTTP/1.1 302 Found\r\nLocation: /new\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
        });

        var (proxy, ep) = StartProxy(origin.Port);
        try
        {
            using var handler = new HttpClientHandler
            {
                UseProxy = false,
                AllowAutoRedirect = false,
                ServerCertificateCustomValidationCallback = (_, cert, _, _) => cert is not null,
            };
            using var client = new HttpClient(handler) { Timeout = Step };
            var r = await client.GetAsync($"https://127.0.0.1:{ep.Port}/old");
            Assert.AreEqual(HttpStatusCode.Redirect, r.StatusCode);
            Assert.AreEqual("/new", r.Headers.Location?.ToString());
        }
        finally { proxy.Stop(); proxy.Dispose(); }
    }
}
