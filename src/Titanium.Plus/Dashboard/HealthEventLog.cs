using Titanium.Web.Proxy.Abstractions.Plugins;

namespace Titanium.Plus.Dashboard;

/// <summary>
///     Bounded ring of the newest destination ejections, recoveries, and probe failures. It subscribes to the
///     cluster manager when that manager publishes health events and stays empty otherwise. Appends never block
///     the caller for longer than a short lock.
/// </summary>
public sealed class HealthEventLog : IDisposable
{
    /// <summary>Events kept. Older ones are overwritten.</summary>
    public const int Capacity = 200;

    private const int MaxFieldLength = 256;

    private readonly object _gate = new();
    private readonly DestinationHealthEvent[] _ring = new DestinationHealthEvent[Capacity];
    private readonly IDestinationHealthEvents? _source;
    private int _head;
    private int _count;

    public HealthEventLog(object? clusterManager = null)
    {
        _source = clusterManager as IDestinationHealthEvents;
        if (_source is not null)
        {
            _source.HealthEvent += Add;
        }
    }

    /// <summary>Events currently held (at most <see cref="Capacity"/>).</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _count;
            }
        }
    }

    public void Add(DestinationHealthEvent healthEvent)
    {
        ArgumentNullException.ThrowIfNull(healthEvent);
        var clean = new DestinationHealthEvent
        {
            Time = healthEvent.Time,
            DestinationId = Trim(healthEvent.DestinationId),
            Kind = Trim(healthEvent.Kind),
            Source = Trim(healthEvent.Source),
            Detail = healthEvent.Detail is null ? null : Trim(healthEvent.Detail),
        };
        lock (_gate)
        {
            _ring[(_head + _count) % Capacity] = clean;
            if (_count < Capacity)
            {
                _count++;
            }
            else
            {
                _head = (_head + 1) % Capacity;
            }
        }
    }

    /// <summary>The newest events first, at most <paramref name="max"/>.</summary>
    public IReadOnlyList<DestinationHealthEvent> Recent(int max = Capacity)
    {
        lock (_gate)
        {
            var take = Math.Clamp(max, 0, _count);
            var list = new List<DestinationHealthEvent>(take);
            for (var i = 0; i < take; i++)
            {
                list.Add(_ring[(_head + _count - 1 - i) % Capacity]);
            }

            return list;
        }
    }

    public void Dispose()
    {
        if (_source is not null)
        {
            _source.HealthEvent -= Add;
        }
    }

    private static string Trim(string value) => value.Length > MaxFieldLength ? value[..MaxFieldLength] : value;
}
