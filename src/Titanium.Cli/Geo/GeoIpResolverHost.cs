using Titanium.Web.Proxy.Abstractions.Routing;

namespace Titanium.Cli.Geo;

/// <summary>Builds and hot-swaps <see cref="MmdbGeoIpResolver"/> instances from <c>server.geoIpDatabasePath</c>.</summary>
internal sealed class GeoIpResolverHost : IDisposable
{
    private static readonly TimeSpan DisposeGrace = TimeSpan.FromSeconds(30);

    private IGeoIpResolver? _current;
    private string? _path;
    private long _writeTicksUtc;
    private readonly List<Timer> _pendingDisposals = [];

    public IGeoIpResolver? Resolver => Volatile.Read(ref _current);

    /// <param name="startup">When true, a missing or corrupt database fails the call. Reload keeps the previous resolver.</param>
    public void Apply(string? databasePath, bool startup, Action<string>? warn = null)
    {
        databasePath = string.IsNullOrWhiteSpace(databasePath) ? null : databasePath.Trim();
        if (databasePath is null)
        {
            Swap(null);
            _path = null;
            _writeTicksUtc = 0;
            return;
        }

        if (!File.Exists(databasePath))
        {
            if (startup)
            {
                throw new FileNotFoundException($"GeoIP database not found: {databasePath}", databasePath);
            }

            warn?.Invoke($"GeoIP reload skipped: database not found at '{databasePath}'. Keeping the previous resolver.");
            return;
        }

        long writeTicks;
        try
        {
            writeTicks = File.GetLastWriteTimeUtc(databasePath).Ticks;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (startup)
            {
                throw new InvalidOperationException($"GeoIP database is not readable: {databasePath}", ex);
            }

            warn?.Invoke($"GeoIP reload skipped: cannot read '{databasePath}' ({ex.Message}). Keeping the previous resolver.");
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
            if (startup)
            {
                throw new InvalidOperationException($"GeoIP database is corrupt or unreadable: {databasePath}", ex);
            }

            warn?.Invoke($"GeoIP reload failed for '{databasePath}' ({ex.Message}). Keeping the previous resolver.");
        }
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
                    // timer is one-shot; list holds roots until host disposal.
                }
            },
            (previous, this),
            DisposeGrace,
            Timeout.InfiniteTimeSpan);
        lock (_pendingDisposals)
        {
            _pendingDisposals.Add(timer);
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
            foreach (var timer in _pendingDisposals)
            {
                timer.Dispose();
            }

            _pendingDisposals.Clear();
        }
    }
}
