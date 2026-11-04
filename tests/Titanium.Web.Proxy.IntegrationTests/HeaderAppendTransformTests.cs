using System;
using System.Collections.Generic;
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
///     Gateway API <c>Add</c> semantics for header modifiers: an append extends the values the client sent
///     (request) or the origin returned (response) instead of replacing them.
/// </summary>
[TestClass]
[DoNotParallelize]
public class HeaderAppendTransformTests
{
    [TestMethod]
    [Timeout(60 * 1000)]
    public async Task RequestHeaderAppend_ExtendsTheClientValue_AndResponseHeaderAppend_ExtendsTheOriginValue()
    {
        var (origin, originPort) = BindLoopbackHttpListener();
        using var cts = new CancellationTokenSource();
        var accept = Task.Run(() => ServeEchoAsync(origin, cts.Token), cts.Token);

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
        await AttachRouteAsync(proxy, originPort,
        [
            Transform(TransformKinds.RequestHeaderAppend, ("name", "X-Test"), ("value", "added")),
            Transform(TransformKinds.RequestHeaderAppend, ("name", "X-Fresh"), ("value", "only")),
            Transform(TransformKinds.RequestHeaderSet, ("name", "X-Set"), ("value", "replaced")),
            Transform(TransformKinds.ResponseHeaderAppend, ("name", "X-Resp"), ("value", "edge")),
            Transform(TransformKinds.ResponseHeaderAppend, ("name", "X-Resp-Fresh"), ("value", "edge")),
            Transform(TransformKinds.ResponseHeaderSet, ("name", "X-Resp-Set"), ("value", "edge")),
        ]);
        proxy.Start();

        try
        {
            using var handler = new HttpClientHandler { UseProxy = false };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
            using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{endPoint.Port}/echo");
            request.Headers.TryAddWithoutValidation("X-Test", "client");
            request.Headers.TryAddWithoutValidation("X-Set", "client");

            using var response = await client.SendAsync(request);
            var echoed = await response.Content.ReadAsStringAsync();

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            StringAssert.Contains(echoed, "x-test=client, added;");
            StringAssert.Contains(echoed, "x-fresh=only;");
            StringAssert.Contains(echoed, "x-set=replaced;");

            Assert.AreEqual("origin, edge", string.Join(", ", response.Headers.GetValues("X-Resp")));
            Assert.AreEqual("edge", string.Join(", ", response.Headers.GetValues("X-Resp-Fresh")));
            Assert.AreEqual("edge", string.Join(", ", response.Headers.GetValues("X-Resp-Set")));
        }
        finally
        {
            cts.Cancel();
            proxy.Stop();
            try { origin.Stop(); origin.Close(); } catch { /* ignore */ }
            try { await accept.WaitAsync(TimeSpan.FromSeconds(2)); } catch { /* ignore */ }
        }
    }

    private static TransformConfig Transform(string kind, params (string Key, string Value)[] parameters) => new()
    {
        Kind = kind,
        Parameters = parameters.ToDictionary(p => p.Key, p => p.Value),
    };

    private static async Task AttachRouteAsync(ProxyServer proxy, int originPort, List<TransformConfig> transforms)
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
                    Transforms = transforms,
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

    /// <summary>Echoes the request headers the origin saw and returns an origin value for <c>X-Resp</c>.</summary>
    private static async Task ServeEchoAsync(HttpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync().WaitAsync(ct).ConfigureAwait(false); }
            catch { return; }

            try
            {
                var seen = new StringBuilder();
                foreach (var name in new[] { "X-Test", "X-Fresh", "X-Set" })
                {
                    seen.Append(name.ToLowerInvariant()).Append('=').Append(ctx.Request.Headers[name]).Append(';');
                }

                var body = Encoding.UTF8.GetBytes(seen.ToString());
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "text/plain";
                ctx.Response.Headers["X-Resp"] = "origin";
                ctx.Response.Headers["X-Resp-Set"] = "origin";
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
