using System.Net;
using Envoy.Config.Cluster.V3;
using Envoy.Config.Core.V3;
using Envoy.Config.Endpoint.V3;
using Envoy.Service.Discovery.V3;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.AspNetCore;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Titanium.Xds.Tests;

internal sealed class FakeAdsServer : IAsyncDisposable
{
    private readonly WebApplication _app;

    public FakeAdsServer()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddGrpc();
        builder.WebHost.UseKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, 0, listen => listen.Protocols = HttpProtocols.Http2);
        });
        _app = builder.Build();
        _app.MapGrpcService<FakeAdsService>();
    }

    public string Address { get; private set; } = string.Empty;

    public async Task StartAsync()
    {
        await _app.StartAsync();
        Address = _app.Urls.First();
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

internal sealed class FakeAdsService : AggregatedDiscoveryService.AggregatedDiscoveryServiceBase
{
    public override async Task StreamAggregatedResources(
        IAsyncStreamReader<DiscoveryRequest> requestStream,
        IServerStreamWriter<DiscoveryResponse> responseStream,
        ServerCallContext context)
    {
        await foreach (var request in requestStream.ReadAllAsync(context.CancellationToken))
        {
            if (request.TypeUrl != XdsTypeUrls.Cluster)
            {
                continue;
            }

            if (!string.IsNullOrEmpty(request.ErrorDetail))
            {
                break;
            }

            var cluster = new Cluster { Name = "c1", Type = ClusterDiscoveryType.Static };

            if (request.VersionInfo == string.Empty)
            {
                await responseStream.WriteAsync(new DiscoveryResponse
                {
                    TypeUrl = XdsTypeUrls.Cluster,
                    VersionInfo = "v1",
                    Nonce = "n1",
                    Resources = { Any.Pack(cluster) },
                }).ConfigureAwait(false);
                continue;
            }

            if (request.ResponseNonce == "n1")
            {
                var bad = new Cluster { Name = "c2", Type = ClusterDiscoveryType.LogicalDns };
                await responseStream.WriteAsync(new DiscoveryResponse
                {
                    TypeUrl = XdsTypeUrls.Cluster,
                    VersionInfo = "v2-bad",
                    Nonce = "n2",
                    Resources = { Any.Pack(bad) },
                }).ConfigureAwait(false);
            }
        }
    }
}

internal static class XdsTypeUrls
{
    internal const string Cluster = "type.googleapis.com/envoy.config.cluster.v3.Cluster";
}
