using System;
using System.Collections.Generic;

namespace Titanium.Web.Proxy.Resilience;

/// <summary>Process-wide overload level derived from sampled pressure readings.</summary>
public enum OverloadLevel
{
    Normal = 0,
    Elevated = 1,
    Critical = 2,
}

/// <summary>One sampled signal for overload protection.</summary>
internal interface IPressureSignal
{
    string Name { get; }

    /// <summary>Current reading, or null when the signal is disabled on this platform.</summary>
    double? Read();
}

/// <summary>Thresholds for one pressure signal with hysteresis and dwell.</summary>
public sealed class OverloadSignalThresholds
{
    /// <summary>Zero disables this signal.</summary>
    public double ElevatedAt { get; init; }

    public double CriticalAt { get; init; }

    /// <summary>Minimum time at a level before a downgrade is allowed.</summary>
    public TimeSpan MinDwell { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Fraction below the entry threshold required to exit (default 10%).</summary>
    public double HysteresisFraction { get; init; } = 0.10;
}

/// <summary>Configuration for <see cref="OverloadMonitor"/>.</summary>
public sealed class OverloadMonitorConfig
{
    public OverloadSignalThresholds HeapPercent { get; init; } = new() { ElevatedAt = 85, CriticalAt = 95 };

    public OverloadSignalThresholds ThreadPoolQueue { get; init; } = new() { ElevatedAt = 1000, CriticalAt = 10000 };

    public OverloadSignalThresholds ActiveConnections { get; init; } = new();
}

/// <summary>Pure state machine: max over signals with hysteresis and dwell.</summary>
public sealed class OverloadMonitor
{
    private readonly OverloadMonitorConfig _config;
    private OverloadLevel _level = OverloadLevel.Normal;
    private DateTimeOffset _levelSince = DateTimeOffset.MinValue;

    public OverloadMonitor(OverloadMonitorConfig config) => _config = config;

    public OverloadLevel Level => _level;

    /// <summary>Advances state from fresh readings.</summary>
    public OverloadLevel Next(IReadOnlyDictionary<string, double?> readings, DateTimeOffset now)
    {
        var desired = OverloadLevel.Normal;
        foreach (var (name, value) in readings)
        {
            if (value is not { } reading)
            {
                continue;
            }

            var thresholds = ThresholdsFor(name);
            if (thresholds is null || thresholds.ElevatedAt <= 0)
            {
                continue;
            }

            desired = Max(desired, LevelForReading(reading, thresholds, name));
        }

        if (desired > _level)
        {
            _level = desired;
            _levelSince = now;
            return _level;
        }

        if (desired < _level &&
            now - _levelSince >= MinDwellFor(_level) &&
            CanExitTo(desired, readings, now))
        {
            _level = desired;
            _levelSince = now;
        }

        return _level;
    }

    private OverloadLevel LevelForReading(
        double reading,
        OverloadSignalThresholds thresholds,
        string name)
    {
        _ = name;
        if (reading >= thresholds.CriticalAt)
        {
            return OverloadLevel.Critical;
        }

        if (reading >= thresholds.ElevatedAt)
        {
            return OverloadLevel.Elevated;
        }

        return OverloadLevel.Normal;
    }

    private bool CanExitTo(OverloadLevel target, IReadOnlyDictionary<string, double?> readings, DateTimeOffset now)
    {
        foreach (var (name, value) in readings)
        {
            if (value is not { } reading)
            {
                continue;
            }

            var thresholds = ThresholdsFor(name);
            if (thresholds is null || thresholds.ElevatedAt <= 0)
            {
                continue;
            }

            var exitElevated = thresholds.ElevatedAt * (1 - thresholds.HysteresisFraction);
            var exitCritical = thresholds.CriticalAt * (1 - thresholds.HysteresisFraction);
            var held = _level switch
            {
                OverloadLevel.Critical when reading >= exitCritical => OverloadLevel.Critical,
                OverloadLevel.Elevated when reading >= exitElevated => OverloadLevel.Elevated,
                OverloadLevel.Elevated when reading >= thresholds.CriticalAt => OverloadLevel.Critical,
                _ => OverloadLevel.Normal,
            };

            if (Max(target, held) > target)
            {
                return false;
            }
        }

        _ = now;
        return true;
    }

    private TimeSpan MinDwellFor(OverloadLevel level) => level switch
    {
        OverloadLevel.Critical => _config.HeapPercent.MinDwell,
        OverloadLevel.Elevated => _config.HeapPercent.MinDwell,
        _ => TimeSpan.Zero,
    };

    private OverloadSignalThresholds? ThresholdsFor(string name) => name switch
    {
        "heapPercent" => _config.HeapPercent,
        "threadPoolQueue" => _config.ThreadPoolQueue,
        "activeConnections" => _config.ActiveConnections,
        _ => null,
    };

    private static OverloadLevel Max(OverloadLevel a, OverloadLevel b) => (OverloadLevel)Math.Max((int)a, (int)b);
}
