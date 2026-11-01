using Envoy.Config.Core.V3;
using Envoy.Service.Discovery.V3;
using Titanium.Web.Proxy.Configuration;
using Titanium.Web.Proxy.Configuration.Models;
using Titanium.Xds.Ads;

namespace Titanium.Xds;

public sealed class XdsControlPlaneHost : IAsyncDisposable
{
    private readonly XdsConfig _config;
    private readonly TwpConfig _bootstrap;
    private readonly string _stateDirectory;
    private readonly Action<TwpConfig> _onConfigReady;
    private readonly AdsClient _client;
    private TwpConfig? _lastGood;

    public XdsControlPlaneHost(XdsConfig config, TwpConfig bootstrap, string stateDirectory, Action<TwpConfig> onConfigReady)
    {
        _config = config;
        _bootstrap = bootstrap;
        _stateDirectory = stateDirectory;
        _onConfigReady = onConfigReady;
        _client = new AdsClient(new AdsClientOptions
        {
            ServerAddress = NormalizeServer(config.Server),
            Node = BuildNode(config),
        });
        _client.ValidateSnapshot += ValidateAsync;
        _client.SnapshotReady += ApplyAsync;
    }

    public AdsConnectionState ConnectionState => _client.State;

    public TwpConfig? LastGoodConfig => _lastGood;

    public void Start(CancellationToken cancellationToken) => _client.Start(cancellationToken);

    public async ValueTask DisposeAsync() => await _client.DisposeAsync();

    private static string NormalizeServer(string? server)
    {
        if (string.IsNullOrWhiteSpace(server))
        {
            throw new InvalidOperationException("xds.server is required");
        }

        return server.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? server : $"http://{server}";
    }

    private static Node BuildNode(XdsConfig config) => new()
    {
        Id = config.Node?.Id ?? Environment.MachineName,
        Cluster = config.Node?.Cluster ?? "titanium",
        Locality = config.Node?.Locality?.Zone is { Length: > 0 } zone
            ? new Locality { Zone = zone }
            : null,
    };

    private Task<AdsAckDecision> ValidateAsync(AdsResourceStore store)
    {
        var built = XdsSnapshotBuilder.Build(store, _stateDirectory, _bootstrap);
        if (!built.IsSuccess)
        {
            var detail = built.Error!.ToString();
            return Task.FromResult(new AdsAckDecision { Ack = false, ErrorDetail = detail });
        }

        var errors = TwpConfigValidator.Validate(built.Config!);
        if (errors.Count > 0)
        {
            return Task.FromResult(new AdsAckDecision { Ack = false, ErrorDetail = string.Join("; ", errors) });
        }

        return Task.FromResult(new AdsAckDecision { Ack = true });
    }

    private Task ApplyAsync(AdsResourceStore store)
    {
        var built = XdsSnapshotBuilder.Build(store, _stateDirectory, _bootstrap);
        if (!built.IsSuccess || built.Config is null)
        {
            return Task.CompletedTask;
        }

        _lastGood = built.Config;
        _onConfigReady(built.Config);
        return Task.CompletedTask;
    }
}
