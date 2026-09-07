using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Clusters;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Routing;
using Titanium.Web.Proxy.Transforms;

namespace Titanium.Web.Proxy.IntegrationTests;

[TestClass]
public class TransformEngineIntegrationTests
{
    [TestMethod]
    public async Task PathPrefix_And_QueryValueSet_ReachOrigin()
    {
        using var origin = new TcpListener(IPAddress.Loopback, 0);
        origin.Start();
        var originPort = ((IPEndPoint)origin.LocalEndpoint).Port;
        var pathTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Task.Run(async () =>
        {
            using var client = await origin.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            var buf = new byte[4096];
            var n = await stream.ReadAsync(buf);
            var req = Encoding.ASCII.GetString(buf, 0, n);
            pathTcs.TrySetResult(req.Split('\n')[0]);
            var body = Encoding.UTF8.GetBytes("ok");
            var resp = "HTTP/1.1 200 OK\r\nContent-Length: " + body.Length +
                       "\r\nContent-Type: text/plain\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(resp));
            await stream.WriteAsync(body);
        });

        var clusters = new List<ClusterConfig>
        {
            new()
            {
                Id = "c1",
                Destinations =
                [
                    new DestinationConfig { Id = "d1", Address = "127.0.0.1", Port = originPort },
                ],
            },
        };
        var clusterManager = new ClusterManager();
        await clusterManager.ApplyAsync(clusters);

        using var proxy = new ProxyServer(userTrustRootCertificate: false);
        proxy.ReverseProxy = new ReverseProxyOptions
        {
            TransformEngine = new TransformEngine(),
            RouteMatcher = new RouteMatcher(),
            LoadBalancer = new LoadBalancer(),
            ClusterManager = clusterManager,
            Routes =
            [
                new RouteConfig
                {
                    Id = "r1",
                    Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix },
                    ClusterId = "c1",
                    Transforms =
                    [
                        new TransformConfig
                        {
                            Kind = "PathPrefix",
                            Parameters = new Dictionary<string, string> { ["prefix"] = "/gw" },
                        },
                        new TransformConfig
                        {
                            Kind = "QueryValueSet",
                            Parameters = new Dictionary<string, string> { ["name"] = "env", ["value"] = "lab" },
                        },
                    ],
                },
            ],
            Clusters = clusters,
        };

        var ep = new ExplicitProxyEndPoint(IPAddress.Loopback, 0, decryptSsl: false);
        proxy.AddEndPoint(ep);
        proxy.Start();

        using var handler = new HttpClientHandler
        {
            Proxy = new WebProxy($"http://127.0.0.1:{ep.Port}"),
            UseProxy = true,
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        var response = await http.GetAsync("http://transform.test/api");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var line = await pathTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        StringAssert.Contains(line, "/gw/api");
        StringAssert.Contains(line, "env=lab");
        await proxy.StopAsync();
        origin.Stop();
    }
}
