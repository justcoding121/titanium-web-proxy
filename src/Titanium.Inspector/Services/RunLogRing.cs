using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Titanium.Inspector.Services;

/// <summary>
/// Bounded in-memory log. Request threads only <c>TryWrite</c> a line. One writer task appends
/// to the ring. The ring lock is never taken on a request thread.
/// </summary>
public sealed class RunLogRing : ILoggerProvider
{
    private readonly Channel<string> _channel;
    private readonly object _ring = new();
    private readonly CancellationTokenSource _stop = new();
    private string[] _lines;
    private int _count;
    private int _next;
    private long _version;

    public RunLogRing(int capacity)
    {
        _lines = new string[Math.Max(1, capacity)];
        _channel = Channel.CreateBounded<string>(new BoundedChannelOptions(256)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
        _ = Task.Run(WriteLoop, _stop.Token);
    }

    public int Capacity
    {
        get
        {
            lock (_ring)
            {
                return _lines.Length;
            }
        }
    }

    public long Version => Volatile.Read(ref _version);

    public void SetCapacity(int capacity)
    {
        var size = Math.Clamp(capacity, 100, 50_000);
        lock (_ring)
        {
            if (_lines.Length == size)
            {
                return;
            }

            var kept = SnapshotUnlocked();
            if (kept.Length > size)
            {
                kept = kept.Skip(kept.Length - size).ToArray();
            }

            _lines = new string[size];
            _count = kept.Length;
            _next = kept.Length % size;
            for (var i = 0; i < kept.Length; i++)
            {
                _lines[i] = kept[i];
            }

            _version++;
        }
    }

    public IReadOnlyList<string> Snapshot()
    {
        lock (_ring)
        {
            return SnapshotUnlocked();
        }
    }

    public void Clear()
    {
        lock (_ring)
        {
            _count = 0;
            _next = 0;
            _version++;
        }
    }

    public ILogger CreateLogger(string categoryName) => new RingLogger(this);

    public void Dispose()
    {
        try
        {
            _stop.Cancel();
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        _channel.Writer.TryComplete();
        _stop.Dispose();
    }

    private void Enqueue(string line)
    {
        _channel.Writer.TryWrite(line);
    }

    private async Task WriteLoop()
    {
        try
        {
            while (await _channel.Reader.WaitToReadAsync(_stop.Token).ConfigureAwait(false))
            {
                while (_channel.Reader.TryRead(out var line))
                {
                    Append(line);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutdown
        }
    }

    private void Append(string line)
    {
        lock (_ring)
        {
            _lines[_next] = line;
            _next = (_next + 1) % _lines.Length;
            if (_count < _lines.Length)
            {
                _count++;
            }

            _version++;
        }
    }

    private string[] SnapshotUnlocked()
    {
        var result = new string[_count];
        var start = (_next - _count + _lines.Length) % _lines.Length;
        for (var i = 0; i < _count; i++)
        {
            result[i] = _lines[(start + i) % _lines.Length];
        }

        return result;
    }

    private sealed class RingLogger : ILogger
    {
        private readonly RunLogRing _ring;

        public RingLogger(RunLogRing ring) => _ring = ring;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var text = formatter(state, exception);
            _ring.Enqueue(logLevel + " " + text);
        }
    }
}
