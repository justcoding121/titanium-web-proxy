using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Routing;

namespace Titanium.Web.Proxy.Abstractions.Configuration;

/// <summary>Immutable routes and clusters published together.</summary>
public sealed class ProxyConfigSnapshot
{
    public long Generation { get; init; }
    public IReadOnlyList<RouteConfig> Routes { get; init; } = [];
    public IReadOnlyList<ClusterConfig> Clusters { get; init; } = [];
}

/// <summary>Supplies the current reverse-proxy snapshot and notifies subscribers.</summary>
public interface IProxyConfigProvider
{
    ProxyConfigSnapshot Current { get; }

    IDisposable Subscribe(Action<ProxyConfigSnapshot> onChange);
}

/// <summary>In-memory provider for tests and hosts that push snapshots themselves.</summary>
public sealed class InMemoryProxyConfigProvider : IProxyConfigProvider
{
    private readonly object _gate = new();
    private readonly List<Action<ProxyConfigSnapshot>> _subscribers = [];
    private ProxyConfigSnapshot _current = new();

    public ProxyConfigSnapshot Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public void Update(IReadOnlyList<RouteConfig> routes, IReadOnlyList<ClusterConfig> clusters)
    {
        Action<ProxyConfigSnapshot>[] subscribers;
        ProxyConfigSnapshot next;
        lock (_gate)
        {
            next = new ProxyConfigSnapshot
            {
                Generation = _current.Generation + 1,
                Routes = routes,
                Clusters = clusters,
            };
            _current = next;
            subscribers = _subscribers.ToArray();
        }

        foreach (var subscriber in subscribers)
        {
            subscriber(next);
        }
    }

    public IDisposable Subscribe(Action<ProxyConfigSnapshot> onChange)
    {
        ArgumentNullException.ThrowIfNull(onChange);
        lock (_gate)
        {
            _subscribers.Add(onChange);
        }

        return new Subscription(() =>
        {
            lock (_gate)
            {
                _subscribers.Remove(onChange);
            }
        });
    }

    private sealed class Subscription(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
