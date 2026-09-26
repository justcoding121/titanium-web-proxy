using System.Threading.Channels;

namespace Titanium.Inspector.Services;

/// <summary>Bounded channel for session snapshots with batched UI delivery.</summary>
public sealed class SessionStreamBuffer
{
    private readonly Channel<SessionSnapshot> _channel;
    private long _nextId;
    private readonly int _batchWindowMs;
    private readonly int _batchMax;

    public SessionStreamBuffer(int capacity = 10_000, int batchWindowMs = 50, int batchMax = 64)
    {
        _batchWindowMs = Math.Max(1, batchWindowMs);
        _batchMax = Math.Max(1, batchMax);
        _channel = Channel.CreateBounded<SessionSnapshot>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
        _ = Task.Run(ReadLoopAsync);
    }

    /// <summary>Compatibility ctor — registry retention is owned by the ViewModel / <see cref="SessionStore"/>.</summary>
    public SessionStreamBuffer(SessionRegistry registry, int capacity = 10_000)
        : this(capacity)
    {
        _ = registry;
    }

    /// <summary>Raised for each snapshot (compat). Prefer <see cref="SessionsBatchAdded"/> for UI.</summary>
    public event Action<SessionSnapshot>? SessionAdded;

    /// <summary>Raised with a short batch of new sessions for one UI turn.</summary>
    public event Action<IReadOnlyList<SessionSnapshot>>? SessionsBatchAdded;

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
        var batch = new List<SessionSnapshot>(_batchMax);
        await foreach (var snapshot in _channel.Reader.ReadAllAsync())
        {
            batch.Add(snapshot);
            var deadline = Environment.TickCount64 + _batchWindowMs;
            while (batch.Count < _batchMax && _channel.Reader.TryRead(out var more))
            {
                batch.Add(more);
            }

            // Wait briefly for more arrivals when the channel is momentarily empty.
            while (batch.Count < _batchMax && Environment.TickCount64 < deadline)
            {
                var remaining = (int)(deadline - Environment.TickCount64);
                if (remaining <= 0)
                {
                    break;
                }

                using var delayCts = new CancellationTokenSource(remaining);
                try
                {
                    if (await _channel.Reader.WaitToReadAsync(delayCts.Token).ConfigureAwait(false))
                    {
                        while (batch.Count < _batchMax && _channel.Reader.TryRead(out var more))
                        {
                            batch.Add(more);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            SessionsBatchAdded?.Invoke(batch.ToList());
            if (SessionAdded is { } singleHandler)
            {
                foreach (var s in batch)
                {
                    singleHandler(s);
                }
            }

            batch.Clear();
        }
    }
}
