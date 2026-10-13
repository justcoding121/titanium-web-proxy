using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Titanium.Inspector.Services;

/// <summary>
/// Bounded in-memory log. Request threads only <c>TryWrite</c> an entry. One writer task appends
/// to the ring. The ring lock is never taken on a request thread.
/// </summary>
public sealed class RunLogRing : ILoggerProvider
{
    private readonly Channel<RunLogEntry> _channel;
    private readonly object _ring = new();
    private readonly CancellationTokenSource _stop = new();
    private RunLogEntry[] _lines;
    private int _count;
    private int _next;
    private long _version;

    public RunLogRing(int capacity)
    {
        _lines = new RunLogEntry[Math.Max(1, capacity)];
        _channel = Channel.CreateBounded<RunLogEntry>(new BoundedChannelOptions(256)
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

            _lines = new RunLogEntry[size];
            _count = kept.Length;
            _next = kept.Length % size;
            for (var i = 0; i < kept.Length; i++)
            {
                _lines[i] = kept[i];
            }

            _version++;
        }
    }

    public IReadOnlyList<RunLogEntry> Snapshot()
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
            Array.Clear(_lines, 0, _lines.Length);
            _version++;
        }
    }

    public ILogger CreateLogger(string categoryName) => new RingLogger(this, categoryName);

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

    private void Enqueue(RunLogEntry entry)
    {
        _channel.Writer.TryWrite(entry);
    }

    private async Task WriteLoop()
    {
        try
        {
            while (await _channel.Reader.WaitToReadAsync(_stop.Token).ConfigureAwait(false))
            {
                while (_channel.Reader.TryRead(out var entry))
                {
                    Append(entry);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutdown
        }
    }

    private void Append(RunLogEntry entry)
    {
        lock (_ring)
        {
            _lines[_next] = entry;
            _next = (_next + 1) % _lines.Length;
            if (_count < _lines.Length)
            {
                _count++;
            }

            _version++;
        }
    }

    private RunLogEntry[] SnapshotUnlocked()
    {
        var result = new RunLogEntry[_count];
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
        private readonly string _category;

        public RingLogger(RunLogRing ring, string category)
        {
            _ring = ring;
            _category = category;
        }

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
            var message = RunLogFormat.BuildMessage(text, exception);
            _ring.Enqueue(new RunLogEntry(DateTime.Now, logLevel, _category, message));
        }
    }
}
