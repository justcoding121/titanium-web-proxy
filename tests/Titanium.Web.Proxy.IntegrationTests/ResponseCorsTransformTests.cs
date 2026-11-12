using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Clusters;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Routing;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     The <c>ResponseCors</c> transform on a real request: the proxy reads the request <c>Origin</c> and adds the
///     allow header to the actual response, which Ingress <c>cors-allow-origin</c> relies on.
/// </summary>
[TestClass]
[DoNotParallelize]
public class ResponseCorsTransformTests
{
    [TestMethod]
    [Timeout(60 * 1000)]
    public async Task ListedOrigin_IsEchoed_AndAnUnlistedOneGetsNoAllowHeader()
    {
        var (origin, originPort) = BindLoopbackHttpListener();
        using var cts = new CancellationTokenSource();
        var accept = Task.Run(() => ServeAsync(origin, cts.Token), cts.Token);

        using var proxy = new ProxyServer(false, false, false)
        {
            EnableHttp2 = false,
            // Route transforms run on the session path (the CLI turns this on whenever a route has transforms).
            EnableHttpInterception = true,
        };
        var endPoint = new TransparentProxyEndPoint(IPAddress.Loopback, 0, decryptSsl: false)
        {
            GenericCertificateName = "localhost",
            ForwardHost = "127.0.0.1",
            ForwardPort = originPort,
            ForwardCleartext = true,
        };
        proxy.AddEndPoint(endPoint);
        await AttachRouteAsync(proxy, originPort, "https://app.example.com,https://b.example.com");
        proxy.Start();

        try
        {
            using var handler = new HttpClientHandler { UseProxy = false };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };

            using var listed = await GetAsync(client, endPoint.Port, "https://b.example.com");
            Assert.AreEqual(HttpStatusCode.OK, listed.StatusCode);
            Assert.AreEqual("https://b.example.com", string.Join(",", listed.Headers.GetValues("Access-Control-Allow-Origin")));
            StringAssert.Contains(string.Join(",", listed.Headers.GetValues("Vary")), "Origin");
            StringAssert.Contains(string.Join(",", listed.Headers.GetValues("Vary")), "Accept-Encoding");

            using var unlisted = await GetAsync(client, endPoint.Port, "https://evil.example.com");
            Assert.AreEqual(HttpStatusCode.OK, unlisted.StatusCode);
            Assert.IsFalse(unlisted.Headers.Contains("Access-Control-Allow-Origin"));
            StringAssert.Contains(string.Join(",", unlisted.Headers.GetValues("Vary")), "Origin");

            using var noOrigin = await GetAsync(client, endPoint.Port, null);
            Assert.IsFalse(noOrigin.Headers.Contains("Access-Control-Allow-Origin"));
        }
        finally
        {
            cts.Cancel();
            proxy.Stop();
            try { origin.Stop(); origin.Close(); } catch { /* ignore */ }
            try { await accept.WaitAsync(TimeSpan.FromSeconds(2)); } catch { /* ignore */ }
        }
    }

    private static Task<HttpResponseMessage> GetAsync(HttpClient client, int port, string? requestOrigin)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/data");
        if (requestOrigin is not null)
        {
            request.Headers.TryAddWithoutValidation("Origin", requestOrigin);
        }

        return client.SendAsync(request);
    }

    private static async Task AttachRouteAsync(ProxyServer proxy, int originPort, string origins)
    {
        var manager = new ClusterManager();
        await manager.ApplyAsync(
        [
            new ClusterConfig
            {
                Id = "c",
                Destinations =
                [
                    new DestinationConfig
                    {
                        Id = "d",
                        Address = "127.0.0.1",
                        Port = originPort,
                        UseDestinationHost = true,
                    },
                ],
            },
        ]);
        proxy.ReverseProxy = new ReverseProxyOptions
        {
            Routes =
            [
                new RouteConfig
                {
                    Id = "r",
                    ClusterId = "c",
                    Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix },
                    Transforms =
                    [
                        new TransformConfig
                        {
                            Kind = TransformKinds.ResponseCors,
                            Parameters = new System.Collections.Generic.Dictionary<string, string> { ["origins"] = origins },
                        },
                    ],
                },
            ],
            ClusterManager = manager,
            RouteMatcher = new RouteMatcher(),
            LoadBalancer = new LoadBalancer(),
        };
    }

    private static (HttpListener Listener, int Port) BindLoopbackHttpListener()
    {
        Exception? last = null;
        for (var i = 0; i < 8; i++)
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                listener.Start();
                return (listener, port);
            }
            catch (Exception ex) when (ex is HttpListenerException or SocketException)
            {
                last = ex;
                try { listener.Close(); } catch { /* ignore */ }
            }
        }

        throw new InvalidOperationException("Failed to bind HttpListener.", last);
    }

    /// <summary>Answers 200 with its own <c>Vary</c>, so the test can see the proxy join rather than replace it.</summary>
    private static async Task ServeAsync(HttpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync().WaitAsync(ct).ConfigureAwait(false); }
            catch { return; }

            try
            {
                var body = Encoding.UTF8.GetBytes("ok");
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "text/plain";
                ctx.Response.Headers["Vary"] = "Accept-Encoding";
                ctx.Response.OutputStream.Write(body);
                ctx.Response.Close();
            }
            catch
            {
                try { ctx.Response.Abort(); } catch { /* ignore */ }
            }
        }
    }
}
