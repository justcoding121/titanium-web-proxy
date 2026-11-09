using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Options;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat ResponseHandler suite: conflicting / non-digit Content-Length on the origin
///     response must become 502 Bad Gateway (not relayed to the client).
/// </summary>
[DoNotParallelize]
[TestClass]
public class ResponseHandlerAmbiguousResponseClFatGapTests
{
    private sealed class Http11Origin : IAsyncDisposable
    {
        private readonly TcpListener listener;
        private readonly Func<Stream, Task> handler;
        private readonly CancellationTokenSource stopping = new();

        private Http11Origin(TcpListener listener, Func<Stream, Task> handler)
        {
            this.listener = listener;
            this.handler = handler;
            _ = AcceptLoopAsync(stopping.Token);
        }

        public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

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
                var i = (pos - 4) % 4;
                if (window[i % 4] == '\r' && window[(i + 1) % 4] == '\n' &&
                    window[(i + 2) % 4] == '\r' && window[(i + 3) % 4] == '\n')
                    return;
            }
        }
    }

    /// <summary>
    ///     Bug class: conflicting Content-Length values are forwarded to the client.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task ExplicitHttp_ConflictingResponseContentLength_Returns502()
    {
        await using var origin = Http11Origin.Start(async s =>
        {
            await DrainRequestAsync(s);
            var resp =
                "HTTP/1.1 200 OK\r\n" +
                "Content-Length: 4\r\n" +
                "Content-Length: 9\r\n" +
                "Connection: close\r\n\r\n" +
                "conflict!";
            await s.WriteAsync(Encoding.ASCII.GetBytes(resp));
        });

        using var suite = new TestSuite();
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = false;
        proxy.EnableHttpInterception = true;
        proxy.PolicyModes = ProxyPolicyModes.AllEnforce;

        using var handler = new HttpClientHandler
        {
            Proxy = new WebProxy($"http://127.0.0.1:{proxy.ProxyEndPoints[0].Port}"),
            UseProxy = true,
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        using var response = await client.GetAsync($"http://127.0.0.1:{origin.Port}/conflict-cl");
        Assert.AreEqual(HttpStatusCode.BadGateway, response.StatusCode,
            "Conflicting response Content-Length must become 502 via ResponseHandler.");
    }

    /// <summary>
    ///     Bug class: a non-digit Content-Length is accepted and framed incorrectly.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task ExplicitHttp_NonDigitResponseContentLength_Returns502()
    {
        await using var origin = Http11Origin.Start(async s =>
        {
            await DrainRequestAsync(s);
            var resp =
                "HTTP/1.1 200 OK\r\n" +
                "Content-Length: abc\r\n" +
                "Connection: close\r\n\r\n" +
                "nope";
            await s.WriteAsync(Encoding.ASCII.GetBytes(resp));
        });

        using var suite = new TestSuite();
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = false;
        proxy.EnableHttpInterception = true;
        proxy.PolicyModes = ProxyPolicyModes.AllEnforce;

        using var handler = new HttpClientHandler
        {
            Proxy = new WebProxy($"http://127.0.0.1:{proxy.ProxyEndPoints[0].Port}"),
            UseProxy = true,
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        using var response = await client.GetAsync($"http://127.0.0.1:{origin.Port}/bad-cl");
        Assert.AreEqual(HttpStatusCode.BadGateway, response.StatusCode,
            "Non-digit Content-Length must become 502 via ResponseHandler.");
    }
}
