using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Diagnostics;
using Titanium.Web.Proxy.Logging;

namespace Titanium.Web.Proxy.Http3.Dns;

/// <summary>
///     Lifecycle-owned coordinator for Auto-mode HTTPS/SVCB discovery. Request paths never await
///     DNS: they either hit <see cref="Http3OriginCapabilityCache" /> or queue a single coalesced
///     background lookup that warms the cache for subsequent connections.
/// </summary>
internal sealed class Http3SvcbDiscoveryCoordinator : IDisposable
{
    /// <summary>
    ///     How long a miss (definitive negative, transient failure, or resolver error) suppresses
    ///     further <see cref="QueueDiscovery" /> calls for the same host:port. Deliberately shorter
    ///     than <see cref="UdpSvcbDnsResolver" />'s own one-minute definitive-negative TTL: this is
    ///     purely to avoid spawning a background <see cref="Task" /> (and the resolver call it makes,
    ///     which would itself immediately hit that resolver-level cache) for every single CONNECT to
    ///     a host that was just probed, not to duplicate the resolver's own caching.
    /// </summary>
    private static readonly TimeSpan MissSuppressionTtl = TimeSpan.FromSeconds(30);

    private const int MaxSuppressedEntries = 4096;

    private readonly ConcurrentDictionary<string, long> generations = new();
    private readonly ConcurrentDictionary<string, byte> inflight = new();
    private readonly ConcurrentDictionary<string, DateTime> missSuppressedUntil = new();
    private readonly CancellationTokenSource lifetimeCts = new();
    private readonly ProxyServer server;
    private int disposed;
    private int unavailableDnsLogged;

    internal Http3SvcbDiscoveryCoordinator(ProxyServer server)
    {
        this.server = server;
    }

    /// <summary>
    ///     Bumps the generation for <paramref name="hostAndPort" /> so any in-flight SVCB completion
    ///     that started before an <c>Alt-Svc: clear</c> / eviction will not repopulate the cache.
    /// </summary>
    internal void Invalidate(string hostAndPort)
    {
        generations.AddOrUpdate(hostAndPort, 1, static (_, current) => current + 1);
    }

    /// <summary>
    ///     Queues a background SVCB discovery for <paramref name="host" />:<paramref name="port" />
    ///     when discovery is enabled and no lookup is already in flight. Never blocks the caller.
    /// </summary>
    internal void QueueDiscovery(string host, int port)
    {
        if (Volatile.Read(ref disposed) != 0) return;
        if (!server.EnableHttpsSvcbDnsDiscovery) return;

        // Injected resolvers (unit tests, DoH adapters) bypass OS DNS discovery entirely.
        if (!server.HasCustomHttpsSvcbResolver && !IsUsableDnsServer(server.DnsServerEndPoint))
        {
            if (Interlocked.Exchange(ref unavailableDnsLogged, 1) == 0)
                ProxyLog.SvcbDnsUnavailable(server.Logger,
                    "No OS-configured DNS server is available for HTTPS/SVCB discovery; proactive discovery is skipped.");
            return;
        }

        var key = $"{host}:{port}";

        // A recent miss for this exact host:port already told us there is nothing to discover;
        // do not even spawn a background Task for it again until the suppression window elapses.
        if (missSuppressedUntil.TryGetValue(key, out var suppressedUntil) && DateTime.UtcNow < suppressedUntil)
            return;

        if (!inflight.TryAdd(key, 0)) return;

        var generation = generations.GetOrAdd(key, 0);
        ProxyMetrics.SvcbDiscoveryQueued();

        _ = Task.Run(() => RunDiscoveryAsync(key, host, port, generation), CancellationToken.None);
    }

    private async Task RunDiscoveryAsync(string key, string host, int port, long generation)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            lifetimeCts.Token.ThrowIfCancellationRequested();

            var result = await server.HttpsSvcbResolver
                .TryGetH3CapabilityAsync(host, port, lifetimeCts.Token)
                .ConfigureAwait(false);

            sw.Stop();
            ProxyMetrics.SvcbDiscoveryCompleted(sw.Elapsed.TotalMilliseconds,
                result != null ? "hit" : "miss");

            if (Volatile.Read(ref disposed) != 0) return;
            if (generations.GetOrAdd(key, 0) != generation) return;

            if (result != null)
            {
                var altPort = result.AltPort == port ? int.MinValue : result.AltPort;
                server.Http3OriginCapabilityCache.Set(key, altPort, result.Ttl, result.TargetName);
                missSuppressedUntil.TryRemove(key, out _);
            }
            else
            {
                missSuppressedUntil[key] = DateTime.UtcNow + MissSuppressionTtl;
            }
        }
        catch (OperationCanceledException) when (lifetimeCts.IsCancellationRequested)
        {
            // Proxy shutdown — expected.
        }
        catch (Exception ex)
        {
            sw.Stop();
            ProxyMetrics.SvcbDiscoveryCompleted(sw.Elapsed.TotalMilliseconds, "error");
            ProxyDiagnostics.ReportUnexpected(server.Logger,
                $"Background HTTPS/SVCB discovery failed for '{key}'.", ex);
            missSuppressedUntil[key] = DateTime.UtcNow + MissSuppressionTtl;
        }
        finally
        {
            inflight.TryRemove(key, out _);
        }
    }

    /// <summary>
    ///     Removes expired suppression entries and enforces a backstop size cap. Called periodically
    ///     from <see cref="ProxyServer.TrimOriginCapabilityCaches" /> (driven by the connection-pool
    ///     cleanup loop) alongside the other origin-capability caches.
    /// </summary>
    internal void TrimExpired()
    {
        var now = DateTime.UtcNow;
        foreach (var pair in missSuppressedUntil)
            if (pair.Value <= now)
                missSuppressedUntil.TryRemove(pair.Key, out _);

        var excess = missSuppressedUntil.Count - MaxSuppressedEntries;
        if (excess <= 0) return;

        foreach (var pair in missSuppressedUntil.OrderBy(x => x.Value).Take(excess))
            missSuppressedUntil.TryRemove(pair.Key, out _);
    }

    internal static bool IsUsableDnsServer(IPEndPoint? endpoint)
    {
        if (endpoint == null) return false;
        if (endpoint.Port <= 0) return false;
        if (IPAddress.None.Equals(endpoint.Address) || IPAddress.IPv6None.Equals(endpoint.Address))
            return false;
        if (IPAddress.Any.Equals(endpoint.Address) || IPAddress.IPv6Any.Equals(endpoint.Address))
            return false;
        return true;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;

        try
        {
            lifetimeCts.Cancel();
        }
        catch
        {
            // ignore
        }

        lifetimeCts.Dispose();
    }
}
