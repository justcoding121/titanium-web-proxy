using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Clusters;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Routing;
using Titanium.Web.Proxy.Transforms;
using static Titanium.Web.Proxy.IntegrationTests.ProtocolMatrixTests;

namespace Titanium.Web.Proxy.IntegrationTests;

[TestClass]
[DoNotParallelize]
public class GeoRoutePolicyTests
{
    private sealed class MapGeoIpResolver(Dictionary<IPAddress, string> map) : IGeoIpResolver
    {
        public bool TryGetCountry(IPAddress address, out string countryIsoCode)
        {
            if (map.TryGetValue(address, out var code))
            {
                countryIsoCode = code;
                return true;
            }

            countryIsoCode = string.Empty;
            return false;
        }
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task DenyCountry_Returns403_WithoutContactingOrigin()
    {
        var hits = 0;
        await using var origin = new RecordingOrigin((_, _) =>
        {
            Interlocked.Increment(ref hits);
            return (200, "ok");
        });

        async Task AssertAsync(string country, HttpStatusCode expected, int expectedHits)
        {
            var resolver = new MapGeoIpResolver(new Dictionary<IPAddress, string> { [IPAddress.Loopback] = country });
            await using var started = await StartWithGeoAsync(origin.Port, resolver);
            using var client = CreateClient(started);
            using var response = await client.GetAsync("http://matrix.test/");
            Assert.AreEqual(expected, response.StatusCode);
            Assert.AreEqual(expectedHits, Volatile.Read(ref hits));
        }

        await AssertAsync("XX", HttpStatusCode.Forbidden, 0);
        await AssertAsync("YY", HttpStatusCode.OK, 1);
    }

    private static async Task<Started> StartWithGeoAsync(int originPort, IGeoIpResolver resolver)
    {
        var manager = new ClusterManager();
        await manager.ApplyAsync(
        [
            new ClusterConfig
            {
                Id = "c1",
                Destinations = [new DestinationConfig { Id = "c1-d0", Address = "127.0.0.1", Port = originPort }],
            },
        ]);

        var proxy = new ProxyServer(false, false, false);
        proxy.ReverseProxy = new ReverseProxyOptions
        {
            Routes =
            [
                new RouteConfig
                {
                    Id = "r",
                    ClusterId = "c1",
                    Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix },
                    Policies = ["geo"],
                },
            ],
            Policies = [new RoutePolicyConfig { Name = "geo", Kind = "geo", Deny = "XX" }],
            ClusterManager = manager,
            RouteMatcher = new RouteMatcher(),
            LoadBalancer = new LoadBalancer(),
            TransformEngine = new TransformEngine(),
            GeoIpResolver = resolver,
        };

        var clear = new ExplicitProxyEndPoint(IPAddress.Loopback, 0, decryptSsl: false);
        proxy.AddEndPoint(clear);
        proxy.Start();
        return new Started(proxy, clear, null!);
    }

    private static HttpClient CreateClient(Started proxy)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = true,
            Proxy = new WebProxy($"http://127.0.0.1:{proxy.Clear.Port}"),
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
    }
}
