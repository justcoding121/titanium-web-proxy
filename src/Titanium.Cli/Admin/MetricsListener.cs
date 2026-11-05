using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading;

namespace Titanium.Cli.Admin;

/// <summary>
///     Aggregates the core <c>Titanium.Web.Proxy</c> meter in memory for Prometheus exposition.
///     <para>
///         Nothing here runs until <see cref="Start" /> is called, so a process with no admin or metrics
///         endpoint pays nothing: the core instruments stay disabled and every recording call is one
///         <c>Enabled</c> check. Once started, only the proxy, connection, pool, and certificate-cache
///         families are enabled (never every instrument), and only a fixed allow-list of tag keys is kept.
///     </para>
///     <para>
///         The measurement path allocates nothing for a series that already exists: tags are folded into a
///         fixed-shape <see cref="SeriesKey" /> struct, the series cell is found in a
///         <see cref="ConcurrentDictionary{TKey,TValue}" />, and the value is added with
///         <see cref="Interlocked" />. Allocation happens only when a series is first seen.
///     </para>
/// </summary>
internal sealed class MetricsListener : IDisposable
{
    public const string DefaultMeterName = "Titanium.Web.Proxy";

    /// <summary>Distinct <c>route</c> and <c>cluster</c> values kept before further values fold into <see cref="OverflowValue" />.</summary>
    public const int MaxDistinctRoutesAndClusters = 200;

    public const string OverflowValue = "other";

    /// <summary>Per-instrument ceiling on series; measurements beyond it fold into one all-<see cref="OverflowValue" /> series.</summary>
    public const int MaxSeriesPerInstrument = 4096;

    /// <summary>Instrument name prefixes that are enabled. The proxy, connection, pool, retry (under <c>twp.proxy.</c>), and cache families.</summary>
    public static readonly string[] SelectedPrefixes =
    [
        "twp.proxy.",
        "twp.connections.",
        "twp.pool.",
        "twp.certificates.",
        "twp.cache.",
    ];

    /// <summary>The only tag keys that become labels. Any other tag is dropped, so client IP, path, and Host can never leak in.</summary>
    public static readonly string[] LabelNames =
    [
        "route", "cluster", "status_class", "protocol", "error", "direction", "reason", "outcome", "result",
    ];

    private readonly string _meterName;
    private readonly LabelCap _routes = new(MaxDistinctRoutesAndClusters);
    private readonly LabelCap _clusters = new(MaxDistinctRoutesAndClusters);
    private readonly object _gate = new();
    private MeterListener? _listener;
    private InstrumentState[] _instruments = [];
    private bool _disposed;

    public MetricsListener(string meterName = DefaultMeterName)
    {
        _meterName = meterName;
    }

    public bool IsStarted => Volatile.Read(ref _listener) is not null;

    /// <summary>Enabled instruments in publish order. Safe to read while measurements arrive.</summary>
    public InstrumentState[] Instruments => Volatile.Read(ref _instruments);

    public static bool IsSelected(string instrumentName) =>
        SelectedPrefixes.Any(prefix => instrumentName.StartsWith(prefix, StringComparison.Ordinal));

    public void Start()
    {
        lock (_gate)
        {
            if (_listener is not null || _disposed)
            {
                return;
            }

            var listener = new MeterListener
            {
                InstrumentPublished = OnInstrumentPublished,
            };
            listener.SetMeasurementEventCallback<long>(OnLong);
            listener.SetMeasurementEventCallback<int>(OnInt);
            listener.SetMeasurementEventCallback<double>(OnDouble);
            listener.SetMeasurementEventCallback<float>(OnFloat);
            _listener = listener;
            listener.Start();
        }
    }

    /// <summary>Polls observable instruments so their latest value is aggregated. Call once per scrape.</summary>
    public void RecordObservableInstruments() => Volatile.Read(ref _listener)?.RecordObservableInstruments();

    public void Dispose()
    {
        MeterListener? listener;
        lock (_gate)
        {
            _disposed = true;
            listener = _listener;
            _listener = null;
        }

        listener?.Dispose();
    }

    private void OnInstrumentPublished(Instrument instrument, MeterListener listener)
    {
        if (!string.Equals(instrument.Meter.Name, _meterName, StringComparison.Ordinal) || !IsSelected(instrument.Name))
        {
            return;
        }

        if (!InstrumentState.TryCreate(instrument, this, out var state))
        {
            return;
        }

        lock (_gate)
        {
            var current = _instruments;
            var next = new InstrumentState[current.Length + 1];
            Array.Copy(current, next, current.Length);
            next[^1] = state;
            Volatile.Write(ref _instruments, next);
        }

        listener.EnableMeasurementEvents(instrument, state);
    }

    private static void OnLong(Instrument instrument, long value, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state) =>
        ((InstrumentState)state!).RecordLong(value, tags);

    private static void OnInt(Instrument instrument, int value, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state) =>
        ((InstrumentState)state!).RecordLong(value, tags);

    private static void OnDouble(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state) =>
        ((InstrumentState)state!).RecordDouble(value, tags);

    private static void OnFloat(Instrument instrument, float value, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state) =>
        ((InstrumentState)state!).RecordDouble(value, tags);

    internal static int SlotOf(string key) => key switch
    {
        "route" => 0,
        "cluster" => 1,
        "status_class" => 2,
        "protocol" => 3,
        "error" => 4,
        "direction" => 5,
        "reason" => 6,
        "outcome" => 7,
        _ => -1,
    };

    /// <summary>Exposition type an instrument maps to.</summary>
    internal enum MetricType
    {
        Counter,
        Gauge,
        Histogram,
    }

    /// <summary>How a measurement combines with the existing cell.</summary>
    internal enum AggregationMode
    {
        Sum,
        Last,
        Buckets,
    }

    /// <summary>Fixed-shape label set. One slot per allow-listed tag key; null means the tag was absent.</summary>
    internal struct SeriesKey : IEquatable<SeriesKey>
    {
        public string? Route;
        public string? Cluster;
        public string? StatusClass;
        public string? Protocol;
        public string? Error;
        public string? Direction;
        public string? Reason;
        public string? Outcome;

        public void Set(int slot, string value)
        {
            switch (slot)
            {
                case 0: Route = value; break;
                case 1: Cluster = value; break;
                case 2: StatusClass = value; break;
                case 3: Protocol = value; break;
                case 4: Error = value; break;
                case 5: Direction = value; break;
                case 6: Reason = value; break;
                case 7: Outcome = value; break;
            }
        }

        public readonly string? Get(int slot) => slot switch
        {
            0 => Route,
            1 => Cluster,
            2 => StatusClass,
            3 => Protocol,
            4 => Error,
            5 => Direction,
            6 => Reason,
            7 => Outcome,
            _ => null,
        };

        public readonly bool Equals(SeriesKey other) =>
            string.Equals(Route, other.Route, StringComparison.Ordinal) &&
            string.Equals(Cluster, other.Cluster, StringComparison.Ordinal) &&
            string.Equals(StatusClass, other.StatusClass, StringComparison.Ordinal) &&
            string.Equals(Protocol, other.Protocol, StringComparison.Ordinal) &&
            string.Equals(Error, other.Error, StringComparison.Ordinal) &&
            string.Equals(Direction, other.Direction, StringComparison.Ordinal) &&
            string.Equals(Reason, other.Reason, StringComparison.Ordinal) &&
            string.Equals(Outcome, other.Outcome, StringComparison.Ordinal);

        public readonly override bool Equals(object? obj) => obj is SeriesKey other && Equals(other);

        public readonly override int GetHashCode() =>
            HashCode.Combine(Route, Cluster, StatusClass, Protocol, Error, Direction, Reason, Outcome);

        public readonly SeriesKey AllOverflow()
        {
            var key = default(SeriesKey);
            for (var slot = 0; slot < LabelNames.Length; slot++)
            {
                if (Get(slot) is not null)
                {
                    key.Set(slot, OverflowValue);
                }
            }

            return key;
        }
    }

    /// <summary>Holds one series. A scalar series uses <see cref="Long" /> or <see cref="DoubleBits" />; a histogram uses <see cref="Buckets" />.</summary>
    internal sealed class Cell
    {
        public long Long;
        public long DoubleBits;
        public long[]? Buckets;
    }

    /// <summary>Caps the distinct values of one tag key. Values past the cap fold into <see cref="OverflowValue" />.</summary>
    private sealed class LabelCap
    {
        private readonly ConcurrentDictionary<string, byte> _seen = new(StringComparer.Ordinal);
        private readonly int _max;
        private int _count;

        public LabelCap(int max) => _max = max;

        public string Admit(string value)
        {
            if (_seen.ContainsKey(value) || string.Equals(value, OverflowValue, StringComparison.Ordinal))
            {
                return value;
            }

            if (Interlocked.Increment(ref _count) > _max)
            {
                Interlocked.Decrement(ref _count);
                return OverflowValue;
            }

            if (!_seen.TryAdd(value, 0))
            {
                Interlocked.Decrement(ref _count);
            }

            return value;
        }
    }

    /// <summary>One enabled instrument and its series.</summary>
    internal sealed class InstrumentState
    {
        private readonly MetricsListener _owner;
        private int _seriesCount;

        private InstrumentState(MetricsListener owner, Instrument instrument, MetricType type, AggregationMode mode, bool integral)
        {
            _owner = owner;
            Name = instrument.Name;
            Unit = instrument.Unit;
            Description = instrument.Description;
            Type = type;
            Mode = mode;
            Integral = integral;
            Bounds = HistogramBounds.For(instrument.Unit);
        }

        public string Name { get; }

        public string? Unit { get; }

        public string? Description { get; }

        public MetricType Type { get; }

        public AggregationMode Mode { get; }

        /// <summary>True when the instrument measures integers, so values print without a fraction.</summary>
        public bool Integral { get; }

        /// <summary>Upper bounds of the finite histogram buckets. The last bucket is <c>+Inf</c>.</summary>
        public double[] Bounds { get; }

        public ConcurrentDictionary<SeriesKey, Cell> Cells { get; } = new();

        public static bool TryCreate(Instrument instrument, MetricsListener owner, out InstrumentState state)
        {
            state = null!;
            var clr = instrument.GetType();
            if (!clr.IsGenericType)
            {
                return false;
            }

            var definition = clr.GetGenericTypeDefinition();
            var valueType = clr.GetGenericArguments()[0];
            var integral = valueType == typeof(long) || valueType == typeof(int);
            if (!integral && valueType != typeof(double) && valueType != typeof(float))
            {
                return false;
            }

            MetricType type;
            AggregationMode mode;
            if (definition == typeof(Counter<>))
            {
                (type, mode) = (MetricType.Counter, AggregationMode.Sum);
            }
            else if (definition == typeof(ObservableCounter<>))
            {
                (type, mode) = (MetricType.Counter, AggregationMode.Last);
            }
            else if (definition == typeof(UpDownCounter<>))
            {
                (type, mode) = (MetricType.Gauge, AggregationMode.Sum);
            }
            else if (definition == typeof(Histogram<>))
            {
                (type, mode) = (MetricType.Histogram, AggregationMode.Buckets);
            }
            else if (definition == typeof(ObservableGauge<>) || definition == typeof(ObservableUpDownCounter<>) ||
                     definition == typeof(Gauge<>))
            {
                (type, mode) = (MetricType.Gauge, AggregationMode.Last);
            }
            else
            {
                return false;
            }

            state = new InstrumentState(owner, instrument, type, mode, integral);
            return true;
        }

        public void RecordLong(long value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var cell = Resolve(BuildKey(tags));
            switch (Mode)
            {
                case AggregationMode.Sum:
                    Interlocked.Add(ref cell.Long, value);
                    break;
                case AggregationMode.Last:
                    Volatile.Write(ref cell.Long, value);
                    break;
                default:
                    Observe(cell, value);
                    break;
            }
        }

        public void RecordDouble(double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var cell = Resolve(BuildKey(tags));
            switch (Mode)
            {
                case AggregationMode.Sum:
                    AddDouble(ref cell.DoubleBits, value);
                    break;
                case AggregationMode.Last:
                    Volatile.Write(ref cell.DoubleBits, BitConverter.DoubleToInt64Bits(value));
                    break;
                default:
                    Observe(cell, value);
                    break;
            }
        }

        public static double ReadDouble(Cell cell) => BitConverter.Int64BitsToDouble(Volatile.Read(ref cell.DoubleBits));

        private static void AddDouble(ref long bits, double value)
        {
            long seen;
            do
            {
                seen = Volatile.Read(ref bits);
            }
            while (Interlocked.CompareExchange(ref bits, BitConverter.DoubleToInt64Bits(BitConverter.Int64BitsToDouble(seen) + value), seen) != seen);
        }

        private void Observe(Cell cell, double value)
        {
            var bounds = Bounds;
            var index = 0;
            while (index < bounds.Length && value > bounds[index])
            {
                index++;
            }

            Interlocked.Increment(ref cell.Buckets![index]);
            AddDouble(ref cell.DoubleBits, value);
        }

        private static SeriesKey BuildKey(ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var key = default(SeriesKey);
            foreach (var tag in tags)
            {
                var slot = SlotOf(tag.Key);
                if (slot >= 0 && tag.Value is string value)
                {
                    key.Set(slot, value);
                }
            }

            return key;
        }

        private Cell Resolve(SeriesKey key)
        {
            return Cells.TryGetValue(key, out var cell) ? cell : ResolveSlow(key);
        }

        private Cell ResolveSlow(SeriesKey key)
        {
            if (key.Route is { } route)
            {
                key.Route = _owner._routes.Admit(route);
            }

            if (key.Cluster is { } cluster)
            {
                key.Cluster = _owner._clusters.Admit(cluster);
            }

            if (Cells.TryGetValue(key, out var existing))
            {
                return existing;
            }

            if (Volatile.Read(ref _seriesCount) >= MaxSeriesPerInstrument)
            {
                key = key.AllOverflow();
            }

            return Cells.GetOrAdd(key, static (_, self) => self.NewCell(), this);
        }

        private Cell NewCell()
        {
            Interlocked.Increment(ref _seriesCount);
            return new Cell { Buckets = Mode == AggregationMode.Buckets ? new long[Bounds.Length + 1] : null };
        }
    }

    internal static class HistogramBounds
    {
        /// <summary>Request duration in seconds: 1 ms to 30 s, 14 finite buckets plus <c>+Inf</c>.</summary>
        public static readonly double[] DurationSeconds =
        [
            0.001, 0.0025, 0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10, 30,
        ];

        /// <summary>Fallback for a histogram in any other unit.</summary>
        public static readonly double[] Generic =
        [
            1, 2, 5, 10, 25, 50, 100, 250, 500, 1_000, 2_500, 5_000, 10_000, 50_000,
        ];

        public static double[] For(string? unit) => unit == "s" ? DurationSeconds : Generic;
    }
}
