using Titanium.Web.Proxy.Abstractions.Routing;

namespace Titanium.Cli.Geo;

/// <summary>Builds and hot-swaps <see cref="MmdbGeoIpResolver"/> instances from <c>server.geoIpDatabasePath</c>.</summary>
internal sealed class GeoIpResolverHost : IDisposable
{
    private static readonly TimeSpan DisposeGrace = TimeSpan.FromSeconds(30);

    private IGeoIpResolver? _current;
    private string? _path;
    private long _writeTicksUtc;
    private readonly List<(Timer Timer, IDisposable Resolver)> _pendingDisposals = [];

    public IGeoIpResolver? Resolver => Volatile.Read(ref _current);

    /// <param name="startup">When true, a missing or corrupt database fails the call. Reload keeps the previous resolver.</param>
    public void Apply(string? databasePath, bool startup, Action<string>? warn = null)
    {
        databasePath = string.IsNullOrWhiteSpace(databasePath) ? null : databasePath.Trim();
        if (databasePath is null)
        {
            var previous = Swap(null);
            _path = null;
            _writeTicksUtc = 0;
            if (previous is IDisposable disposable)
            {
                ScheduleDispose(disposable);
            }

            return;
        }

        if (!TryReadWriteTicks(databasePath, startup, warn, out var writeTicks))
        {
            return;
        }

        if (string.Equals(_path, databasePath, StringComparison.Ordinal) &&
            writeTicks == _writeTicksUtc &&
            _current is not null)
        {
            return;
        }

        try
        {
            var next = GeoIpAssemblyLoader.CreateMmdbResolver(databasePath);
            var previous = Swap(next);
            _path = databasePath;
            _writeTicksUtc = writeTicks;
            if (previous is IDisposable disposable)
            {
                ScheduleDispose(disposable);
            }
        }
        catch (Exception ex) when (ex is not FileNotFoundException)
        {
            FailOrWarn(startup, warn, databasePath, ex, corrupt: true);
        }
    }

    private static bool TryReadWriteTicks(
        string databasePath,
        bool startup,
        Action<string>? warn,
        out long writeTicks)
    {
        writeTicks = 0;
        if (!File.Exists(databasePath))
        {
            FailOrWarn(
                startup,
                warn,
                databasePath,
                new FileNotFoundException($"GeoIP database not found: {databasePath}", databasePath),
                corrupt: false);
            return false;
        }

        try
        {
            writeTicks = File.GetLastWriteTimeUtc(databasePath).Ticks;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (startup)
            {
                throw new InvalidOperationException($"GeoIP database is not readable: {databasePath}", ex);
            }

            warn?.Invoke($"GeoIP reload skipped: cannot read '{databasePath}' ({ex.Message}). Keeping the previous resolver.");
            return false;
        }
    }

    private static void FailOrWarn(
        bool startup,
        Action<string>? warn,
        string databasePath,
        Exception ex,
        bool corrupt)
    {
        if (startup)
        {
            if (ex is FileNotFoundException missing)
            {
                throw missing;
            }

            var detail = ex;
            while (detail.InnerException is not null)
            {
                detail = detail.InnerException;
            }

            throw new InvalidOperationException(
                $"GeoIP database is corrupt or unreadable: {databasePath} ({detail.GetType().Name}: {detail.Message})",
                ex);
        }

        warn?.Invoke(
            corrupt
                ? $"GeoIP reload failed for '{databasePath}' ({ex.Message}). Keeping the previous resolver."
                : $"GeoIP reload skipped: database not found at '{databasePath}'. Keeping the previous resolver.");
    }

    private IGeoIpResolver? Swap(IGeoIpResolver? next) =>
        Interlocked.Exchange(ref _current, next);

    private void ScheduleDispose(IDisposable? previous)
    {
        if (previous is null)
        {
            return;
        }

        var timer = new Timer(
            static state =>
            {
                var pair = ((IDisposable Resolver, GeoIpResolverHost Host))state!;
                try
                {
                    pair.Resolver.Dispose();
                }
                catch (ObjectDisposedException)
                {
                    // already gone
                }

                lock (pair.Host._pendingDisposals)
                {
                    pair.Host._pendingDisposals.RemoveAll(p => ReferenceEquals(p.Resolver, pair.Resolver));
                }
            },
            (previous, this),
            DisposeGrace,
            Timeout.InfiniteTimeSpan);
        lock (_pendingDisposals)
        {
            _pendingDisposals.Add((timer, previous));
        }
    }

    public void Dispose()
    {
        var current = Swap(null);
        if (current is IDisposable disposable)
        {
            disposable.Dispose();
        }

        lock (_pendingDisposals)
        {
            foreach (var (timer, resolver) in _pendingDisposals)
            {
                timer.Dispose();
                try
                {
                    resolver.Dispose();
                }
                catch (ObjectDisposedException)
                {
                    // already gone
                }
            }

            _pendingDisposals.Clear();
        }
    }
}
