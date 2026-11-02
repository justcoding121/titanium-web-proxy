using Envoy.Service.Discovery.V3;
using Google.Protobuf.WellKnownTypes;
using Grpc.Net.Client;

namespace Titanium.Xds.Ads;

public enum AdsConnectionState
{
    Connecting,
    Synced,
    Degraded,
}

public sealed class AdsAckDecision
{
    public required bool Ack { get; init; }

    public string? ErrorDetail { get; init; }
}

public sealed class AdsClientOptions
{
    public required string ServerAddress { get; init; }

    public required Node Node { get; init; }

    public TimeSpan SettleWindow { get; init; } = TimeSpan.FromMilliseconds(500);

    public int MaxResourcesPerType { get; init; } = 10_000;

    public int MaxMessageBytes { get; init; } = 16 * 1024 * 1024;

    public IReadOnlyList<string> ResourceTypeOrder { get; init; } = XdsTypeUrls.ApplyOrder;
}

public sealed class AdsClient : IAsyncDisposable
{
    private readonly AdsClientOptions _options;
    private readonly AdsResourceStore _store = new();
    private readonly SemaphoreSlim _applyGate = new(1, 1);
    private GrpcChannel? _channel;
    private AggregatedDiscoveryService.AggregatedDiscoveryServiceClient? _client;
    private CancellationTokenSource? _runCts;
    private Task? _runTask;

    public AdsClient(AdsClientOptions options) => _options = options;

    public AdsConnectionState State { get; private set; } = AdsConnectionState.Connecting;

    public event Func<AdsResourceStore, Task<AdsAckDecision>>? ValidateSnapshot;

    public event Func<AdsResourceStore, Task>? SnapshotReady;

    public AdsResourceStore Store => _store;

    public void Start(CancellationToken cancellationToken)
    {
        _runCts?.Dispose();
        _runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _runTask = RunAsync(_runCts.Token);
    }

    public async Task StopAsync()
    {
        if (_runCts is not null)
        {
            await _runCts.CancelAsync().ConfigureAwait(false);
        }

        if (_runTask is not null)
        {
            try
            {
                await _runTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected when StopAsync cancels the run loop.
            }
        }

        _channel?.Dispose();
        _channel = null;
        _client = null;
        _runCts?.Dispose();
        _runCts = null;
        _runTask = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var attempt = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                State = AdsConnectionState.Connecting;
                _channel = GrpcChannel.ForAddress(_options.ServerAddress);
                _client = new AggregatedDiscoveryService.AggregatedDiscoveryServiceClient(_channel);
                await StreamOnceAsync(cancellationToken).ConfigureAwait(false);
                attempt = 0;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                State = AdsConnectionState.Degraded;
                attempt++;
                var delay = TimeSpan.FromMilliseconds(Math.Min(30_000, 1000 * Math.Pow(2, attempt)));
                delay += TimeSpan.FromMilliseconds(Random.Shared.Next(0, 250));
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task StreamOnceAsync(CancellationToken cancellationToken)
    {
        if (_client is null)
        {
            return;
        }

        using var call = _client.StreamAggregatedResources(cancellationToken: cancellationToken);
        var versions = _options.ResourceTypeOrder.ToDictionary(t => t, _ => string.Empty, StringComparer.Ordinal);
        var nonces = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var typeUrl in _options.ResourceTypeOrder)
        {
            await call.RequestStream.WriteAsync(
                new DiscoveryRequest
                {
                    TypeUrl = typeUrl,
                    VersionInfo = versions[typeUrl],
                    Node = _options.Node,
                },
                cancellationToken).ConfigureAwait(false);
        }

        while (await call.ResponseStream.MoveNext(cancellationToken).ConfigureAwait(false))
        {
            var response = call.ResponseStream.Current;
            if (response.Resources.Count > _options.MaxResourcesPerType)
            {
                await NackAsync(call, response, versions, _options.Node, "too many resources", cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            _store.Replace(response.TypeUrl, response.Resources);
            var decision = ValidateSnapshot is null
                ? new AdsAckDecision { Ack = true }
                : await ValidateSnapshot(_store).ConfigureAwait(false);

            if (!decision.Ack)
            {
                await NackAsync(
                        call,
                        response,
                        versions,
                        _options.Node,
                        decision.ErrorDetail ?? "validation failed",
                        cancellationToken)
                    .ConfigureAwait(false);
                _store.ClearType(response.TypeUrl);
                continue;
            }

            versions[response.TypeUrl] = response.VersionInfo;
            nonces[response.TypeUrl] = response.Nonce;
            await call.RequestStream.WriteAsync(
                new DiscoveryRequest
                {
                    TypeUrl = response.TypeUrl,
                    VersionInfo = response.VersionInfo,
                    ResponseNonce = response.Nonce,
                    Node = _options.Node,
                },
                cancellationToken).ConfigureAwait(false);

            if (_options.ResourceTypeOrder.All(t => !string.IsNullOrEmpty(versions[t])))
            {
                await Task.Delay(_options.SettleWindow, cancellationToken).ConfigureAwait(false);
                await _applyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    if (SnapshotReady is not null)
                    {
                        await SnapshotReady(_store).ConfigureAwait(false);
                    }

                    State = AdsConnectionState.Synced;
                }
                finally
                {
                    _applyGate.Release();
                }
            }
        }
    }

    private static async Task NackAsync(
        Grpc.Core.AsyncDuplexStreamingCall<DiscoveryRequest, DiscoveryResponse> call,
        DiscoveryResponse response,
        Dictionary<string, string> versions,
        Node node,
        string detail,
        CancellationToken cancellationToken)
    {
        await call.RequestStream.WriteAsync(
            new DiscoveryRequest
            {
                TypeUrl = response.TypeUrl,
                VersionInfo = versions.GetValueOrDefault(response.TypeUrl) ?? string.Empty,
                ResponseNonce = response.Nonce,
                ErrorDetail = detail,
                Node = node,
            },
            cancellationToken).ConfigureAwait(false);
    }
}
