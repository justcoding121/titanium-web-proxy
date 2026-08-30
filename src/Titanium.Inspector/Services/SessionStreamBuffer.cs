using System.Threading.Channels;

namespace Titanium.Inspector.Services;

/// <summary>Bounded channel (10k) for session snapshots before UI batching.</summary>
public sealed class SessionStreamBuffer
{
    private readonly Channel<SessionSnapshot> _channel;
    private readonly SessionRegistry _registry;
    private long _nextId;

    public SessionStreamBuffer(SessionRegistry registry, int capacity = 10_000)
    {
        _registry = registry;
        _channel = Channel.CreateBounded<SessionSnapshot>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
        _ = Task.Run(ReadLoopAsync);
    }

    public event Action<SessionSnapshot>? SessionAdded;

    public void Publish(SessionSnapshot snapshot)
    {
        _channel.Writer.TryWrite(snapshot);
    }

    public SessionSnapshot CreatePlaceholder(string method, string url) =>
        new()
        {
            Id = Interlocked.Increment(ref _nextId),
            Method = method,
            Url = url,
        };

    private async Task ReadLoopAsync()
    {
        await foreach (var snapshot in _channel.Reader.ReadAllAsync())
        {
            _registry.Add(snapshot);
            SessionAdded?.Invoke(snapshot);
        }
    }
}
