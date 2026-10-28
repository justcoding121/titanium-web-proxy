using System.Collections.Generic;
using BenchmarkDotNet.Attributes;
using Titanium.Web.Proxy.Abstractions;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Clusters;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Routing;

namespace Titanium.Web.Proxy.Benchmarks;

/// <summary>Dispatch microbenchmarks. Reference only; RPS comparison runs on the pull request.</summary>
[MemoryDiagnoser]
public class ReverseProxyDispatchBenchmarks
{
    private ReverseProxyOptions _options = null!;
    private Request _request = null!;
    private RouteMatcher _matcher = null!;
    private List<RouteConfig> _routes = null!;

    [GlobalSetup]
    public void Setup()
    {
        var manager = new ClusterManager();
        var cluster = new ClusterConfig
        {
            Id = "c",
            Destinations = [new DestinationConfig { Id = "d", Address = "127.0.0.1", Port = 80 }],
        };
        manager.ApplyAsync([cluster]).AsTask().GetAwaiter().GetResult();
        _routes =
        [
            new RouteConfig
            {
                Id = "r",
                ClusterId = "c",
                Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix },
            },
        ];
        _options = new ReverseProxyOptions
        {
            Routes = _routes,
            ClusterManager = manager,
            RouteMatcher = new RouteMatcher(),
            LoadBalancer = new LoadBalancer(),
        };
        _matcher = new RouteMatcher();
        _request = new Request
        {
            Method = "GET",
            RequestUriString8 = (ByteString)"/health",
            Host = "app",
        };
    }

    [Benchmark]
    public int Resolve() =>
        (int)DestinationResolver.Resolve(_options, _request, null, 80, out _, out _);

    [Benchmark]
    public RouteConfig? Match() =>
        _matcher.Match(new RouteMatchContext("app", "/health", "GET", null, null), _routes);
}
