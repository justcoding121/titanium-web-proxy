namespace Titanium.Cli.Tests.Support;

/// <summary>A <see cref="TimeProvider" /> whose timers fire only when the test calls <see cref="Advance" />.</summary>
internal sealed class FakeTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<FakeTimer> _timers = [];
    private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public int ActiveTimers
    {
        get
        {
            lock (_gate)
            {
                return _timers.Count;
            }
        }
    }

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new FakeTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    public void Advance(TimeSpan by)
    {
        var target = GetUtcNow() + by;
        while (true)
        {
            FakeTimer? next;
            lock (_gate)
            {
                next = _timers
                    .Where(t => t.Due is not null && t.Due <= target)
                    .OrderBy(t => t.Due)
                    .FirstOrDefault();
                if (next is null)
                {
                    _now = target;
                    return;
                }

                _now = next.Due!.Value;
            }

            next.Fire();
        }
    }

    private void Register(FakeTimer timer)
    {
        lock (_gate)
        {
            if (!_timers.Contains(timer))
            {
                _timers.Add(timer);
            }
        }
    }

    private void Unregister(FakeTimer timer)
    {
        lock (_gate)
        {
            _timers.Remove(timer);
        }
    }

    private sealed class FakeTimer : ITimer
    {
        private readonly FakeTimeProvider _owner;
        private readonly TimerCallback _callback;
        private readonly object? _state;
        private TimeSpan _period = Timeout.InfiniteTimeSpan;

        public FakeTimer(FakeTimeProvider owner, TimerCallback callback, object? state)
        {
            _owner = owner;
            _callback = callback;
            _state = state;
        }

        public DateTimeOffset? Due { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            _period = period;
            if (dueTime == Timeout.InfiniteTimeSpan)
            {
                Due = null;
                _owner.Unregister(this);
                return true;
            }

            Due = _owner._now + dueTime;
            _owner.Register(this);
            return true;
        }

        public void Fire()
        {
            if (_period == Timeout.InfiniteTimeSpan || _period == TimeSpan.Zero)
            {
                Due = null;
                _owner.Unregister(this);
            }
            else
            {
                Due = _owner._now + _period;
            }

            _callback(_state);
        }

        public void Dispose() => _owner.Unregister(this);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
