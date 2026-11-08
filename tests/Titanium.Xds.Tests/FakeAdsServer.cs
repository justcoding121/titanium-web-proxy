using System.Collections.Concurrent;
using System.Net;
using Envoy.Config.Cluster.V3;
using Envoy.Service.Discovery.V3;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Titanium.Xds.Tests;

/// <summary>Queued ADS responses keyed by type URL. Empty queues still get an empty v1 so multi-type clients can settle.</summary>
internal sealed class FakeAdsState
{
    private readonly ConcurrentDictionary<string, ConcurrentQueue<DiscoveryResponse>> _followUps =
        new(StringComparer.Ordinal);

    public ConcurrentDictionary<string, Any[]> Initial { get; } = new(StringComparer.Ordinal);

    public void EnqueueFollowUp(string typeUrl, string version, string nonce, params Any[] resources)
    {
        var response = new DiscoveryResponse
        {
            TypeUrl = typeUrl,
            VersionInfo = version,
            Nonce = nonce,
        };
        response.Resources.AddRange(resources);
        _followUps.GetOrAdd(typeUrl, _ => new ConcurrentQueue<DiscoveryResponse>()).Enqueue(response);
    }

    public DiscoveryResponse InitialResponse(string typeUrl)
    {
        var response = new DiscoveryResponse
        {
            TypeUrl = typeUrl,
            VersionInfo = "v1",
            Nonce = "n1:" + typeUrl,
        };
        if (Initial.TryGetValue(typeUrl, out var resources))
        {
            response.Resources.AddRange(resources);
        }

        return response;
    }

    public bool TryDequeueFollowUp(string typeUrl, out DiscoveryResponse response)
    {
        response = null!;
        return _followUps.TryGetValue(typeUrl, out var queue) && queue.TryDequeue(out response!);
    }
}

internal sealed class FakeAdsServer : IAsyncDisposable
{
    private readonly WebApplication _app;

    public FakeAdsServer(FakeAdsState? state = null)
    {
        State = state ?? DefaultClusterOnlyState();
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton(State);
        builder.Services.AddGrpc();
        builder.WebHost.UseKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, 0, listen => listen.Protocols = HttpProtocols.Http2);
        });
        _app = builder.Build();
        _app.MapGrpcService<FakeAdsService>();
    }

    public FakeAdsState State { get; }

    public string Address { get; private set; } = string.Empty;

    /// <summary>Host:port without a scheme, for cleartext ADS normalization tests.</summary>
    public string BareAddress
    {
        get
        {
            var uri = new Uri(Address);
            return $"{uri.Host}:{uri.Port}";
        }
    }

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

    private static FakeAdsState DefaultClusterOnlyState()
    {
        var state = new FakeAdsState();
        state.Initial[Titanium.Xds.XdsTypeUrls.Cluster] =
        [
            Any.Pack(new Cluster { Name = "c1", Type = ClusterDiscoveryType.Static }),
        ];
        state.EnqueueFollowUp(
            Titanium.Xds.XdsTypeUrls.Cluster,
            "v2-bad",
            "n2",
            Any.Pack(new Cluster { Name = "c2", Type = ClusterDiscoveryType.LogicalDns }));
        return state;
    }
}

internal sealed class FakeAdsService : AggregatedDiscoveryService.AggregatedDiscoveryServiceBase
{
    private readonly FakeAdsState _state;

    public FakeAdsService(FakeAdsState state) => _state = state;

    public override async Task StreamAggregatedResources(
        IAsyncStreamReader<DiscoveryRequest> requestStream,
        IServerStreamWriter<DiscoveryResponse> responseStream,
        ServerCallContext context)
    {
        await foreach (var request in requestStream.ReadAllAsync(context.CancellationToken))
        {
            if (!string.IsNullOrEmpty(request.ErrorDetail))
            {
                continue;
            }

            if (request.VersionInfo == string.Empty)
            {
                await responseStream.WriteAsync(_state.InitialResponse(request.TypeUrl)).ConfigureAwait(false);
                continue;
            }

            if (_state.TryDequeueFollowUp(request.TypeUrl, out var followUp))
            {
                await responseStream.WriteAsync(followUp).ConfigureAwait(false);
            }
        }
    }
}
