using System.Diagnostics;

namespace Titanium.Inspector.Services;

public enum ThrottleDirection
{
    Upload,
    Download,
}

/// <summary>
///     Shared upload and download clocks for one throttle profile.
///     Bandwidth is reserved under a lock; the wait happens outside it.
///     Cancelling the epoch (profile change, stop, dispose) releases waiters without throwing.
///     A caller token that is cancelled still throws, so a client abort can stop a paced write.
/// </summary>
internal sealed class LinkShaper : IDisposable
{
    public static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(10);

    private readonly TimeProvider _time;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _epoch = new();
    private long _uploadReadyAt;
    private long _downloadReadyAt;
    private int _disposed;

    public LinkShaper(
        NetworkThrottleProfile profile,
        TimeProvider? time = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        Profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _time = time ?? TimeProvider.System;
        _delay = delay ?? ((span, token) => Task.Delay(span, _time, token));
    }

    public NetworkThrottleProfile Profile { get; }

    public bool IsEnabled => Profile.IsEnabled && Volatile.Read(ref _disposed) == 0;

    /// <summary>One round-trip. Does not move the bandwidth clocks.</summary>
    public TimeSpan LatencyDelay =>
        Profile.LatencyMs > 0 ? TimeSpan.FromMilliseconds(Profile.LatencyMs) : TimeSpan.Zero;

    /// <summary>
    ///     Reserves <paramref name="byteCount"/> on the shared clock.
    ///     Idle time does not bank credit. Zero bytes do not move the clock.
    /// </summary>
    public TimeSpan Reserve(ThrottleDirection direction, long byteCount)
    {
        if (byteCount <= 0 || Profile.BytesPerSecond <= 0 || Volatile.Read(ref _disposed) != 0)
        {
            return TimeSpan.Zero;
        }

        var frequency = _time.TimestampFrequency;
        if (frequency <= 0)
        {
            return TimeSpan.Zero;
        }

        long cost;
        try
        {
            checked
            {
                cost = (byteCount * frequency + Profile.BytesPerSecond - 1) / Profile.BytesPerSecond;
            }
        }
        catch (OverflowException)
        {
            cost = frequency * (long)MaxWait.TotalSeconds;
        }

        if (cost < 0)
        {
            cost = 0;
        }

        lock (_gate)
        {
            if (_disposed != 0)
            {
                return TimeSpan.Zero;
            }

            var now = _time.GetTimestamp();
            ref var clock = ref Clock(direction);
            var start = now > clock ? now : clock;
            var wake = start + cost;
            if (wake < start)
            {
                wake = long.MaxValue;
            }

            clock = wake;
            var delay = _time.GetElapsedTime(now, wake);
            return delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
        }
    }

    public async Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken = default)
    {
        if (delay <= TimeSpan.Zero || Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        if (delay > MaxWait)
        {
            Debug.WriteLine("LinkShaper wait capped at 10 minutes.");
            delay = MaxWait;
        }

        CancellationToken epoch;
        try
        {
            epoch = _epoch.Token;
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        if (epoch.IsCancellationRequested)
        {
            return;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(epoch, cancellationToken);
        try
        {
            await _delay(delay, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
        }
        catch (ObjectDisposedException ex)
        {
            Debug.WriteLine($"LinkShaper wait ended because the shaper was disposed: {ex.Message}");
        }
    }

    /// <summary>Unblocks current waits. Later waits on this instance also return immediately.</summary>
    public void ReleaseWaiters()
    {
        try
        {
            if (!_epoch.IsCancellationRequested)
            {
                _epoch.Cancel();
            }
        }
        catch (ObjectDisposedException ex)
        {
            Debug.WriteLine($"LinkShaper release ignored a disposed epoch: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        ReleaseWaiters();
        _epoch.Dispose();
    }

    private ref long Clock(ThrottleDirection direction) =>
        ref direction == ThrottleDirection.Upload ? ref _uploadReadyAt : ref _downloadReadyAt;
}
