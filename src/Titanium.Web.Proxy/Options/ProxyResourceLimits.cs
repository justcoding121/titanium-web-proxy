using System;

namespace Titanium.Web.Proxy.Options;

/// <summary>
///     Immutable, validated snapshot of the resource bounds a peer can make the proxy allocate:
///     header shape, body/decompression budgets, concurrency and abuse-rate ceilings, and pool /
///     certificate-cache sizing. Constructed only through <see cref="Create" />, which validates
///     every field up front, so an invalid limit is a construction-time exception rather than a
///     runtime surprise discovered mid-connection.
///     <para>
///         A limit that can legitimately be turned off is typed as nullable with
///         <see langword="null" /> meaning "disabled" - an explicit state - rather than overloading
///         <c>0</c> or a negative number to mean the same thing. Limits that must always be
///         enforced because disabling them would leave the proxy itself exploitable (the
///         concurrent-stream cap, the open-header-block frame bound) are non-nullable and always
///         validated to be strictly positive.
///     </para>
///     <para>
///         This type has no back-reference to <see cref="ProxyServer" /> and no mutable state after
///         construction: it is meant to be handed down to subsystems by value, not looked up through
///         a service locator or an ambient static, so the dependency graph among consumers stays
///         acyclic per the plan's "Constraints on the policy layer" section.
///     </para>
/// </summary>
public sealed class ProxyResourceLimits
{
    private ProxyResourceLimits()
    {
    }

    /// <summary>Maximum length of a single header line (request/status line or one header field), in bytes.</summary>
    public long MaxHeaderLineBytes { get; private init; }

    /// <summary>Maximum number of header fields accepted in one request or response.</summary>
    public int MaxHeaderCount { get; private init; }

    /// <summary>Maximum aggregate size of all header fields in one request or response, in bytes.</summary>
    public long MaxHeaderAggregateBytes { get; private init; }

    /// <summary>
    ///     Maximum cumulative compressed/on-wire body bytes read for a single request or response.
    ///     <see langword="null" /> disables the budget.
    /// </summary>
    public long? MaxEncodedBodyBytes { get; private init; }

    /// <summary>
    ///     Maximum cumulative decompressed body bytes produced for a single request or response.
    ///     <see langword="null" /> disables the budget. Always checked alongside
    ///     <see cref="MaxDecompressionRatio" />, since a ratio alone cannot bound total memory for a
    ///     small compressed input that expands enormously without also capping the output side.
    /// </summary>
    public long? MaxDecodedBodyBytes { get; private init; }

    /// <summary>
    ///     Maximum allowed ratio of decompressed to compressed bytes. <see langword="null" /> disables
    ///     the ratio check (relying on <see cref="MaxDecodedBodyBytes" /> alone).
    /// </summary>
    public double? MaxDecompressionRatio { get; private init; }

    /// <summary>
    ///     Maximum number of concurrently admitted client connections, checked by the admission gate
    ///     at handler entry/exit rather than the delayed <c>ClientConnectionCount</c>. <see langword="null" />
    ///     disables global admission control.
    /// </summary>
    public int? MaxConcurrentClients { get; private init; }

    /// <summary>
    ///     Proxy-owned cap on concurrent HTTP/2 streams per connection. Always enforced: this is the
    ///     single source of truth consolidating what were previously two independent mechanisms, and
    ///     is also the value advertised to the origin in the relayed SETTINGS frame so the advertised
    ///     and enforced values never disagree.
    /// </summary>
    public int MaxConcurrentStreamsPerConnection { get; private init; }

    /// <summary>
    ///     Maximum number of peer-initiated resets of streams that never completed, per connection,
    ///     before the proxy tears the connection down. Proxy-initiated resets (e.g. in response to a
    ///     client cancellation) do not count. <see langword="null" /> disables the reset budget.
    /// </summary>
    public int? MaxPeerInitiatedIncompleteStreamResets { get; private init; }

    /// <summary>
    ///     Maximum number of CONTINUATION frames tolerated for a single open HTTP/2 header block,
    ///     independent of the existing byte cap. Always enforced: zero-length CONTINUATION frames
    ///     never trip a byte-based check, and only one header block may be open per connection
    ///     direction, so an unbounded frame count also head-of-line blocks every other stream.
    /// </summary>
    public int MaxOpenHeaderBlockFrames { get; private init; }

    /// <summary>
    ///     Maximum wall-clock duration an HTTP/2 header block may stay open (from the initial
    ///     HEADERS/PUSH_PROMISE frame sent without END_HEADERS through its terminating
    ///     CONTINUATION), independent of <see cref="MaxOpenHeaderBlockFrames" />. Bounds a slow
    ///     CONTINUATION-trickle variant that stays under the frame-count cap by pacing itself, which
    ///     a frame-count-only bound cannot catch on its own. Always enforced.
    /// </summary>
    public TimeSpan MaxOpenHeaderBlockDuration { get; private init; }

    /// <summary>
    ///     Whether upstream TCP connection pooling is enabled. Disabling pooling is an explicit choice
    ///     represented by this flag, not by giving <see cref="MaxCachedConnectionsPerHost" /> a
    ///     sentinel value that also has to be validated as "not zero, not negative, unless it means
    ///     disabled".
    /// </summary>
    public bool ConnectionPoolingEnabled { get; private init; }

    /// <summary>
    ///     Maximum pooled connections cached per remote host. Only meaningful when
    ///     <see cref="ConnectionPoolingEnabled" /> is <see langword="true" />; always validated as
    ///     strictly positive regardless, so a future caller cannot re-introduce the "0 spins forever
    ///     holding the pool lock" defect by flipping the flag without also fixing this value.
    /// </summary>
    public int MaxCachedConnectionsPerHost { get; private init; }

    /// <summary>
    ///     Maximum number of generated leaf certificates held in the in-memory certificate cache.
    ///     Each entry holds a full <see cref="System.Security.Cryptography.X509Certificates.X509Certificate2" />
    ///     with its private key, so unlike most other limits in this type this one defends against
    ///     unbounded memory growth from ordinary browsing (many distinct MITM'd hosts), not just
    ///     against an adversarial peer. <see langword="null" /> disables the bound and is <em>not</em>
    ///     the shipped default - see <see cref="Default" />.
    /// </summary>
    public int? MaxCertificateCacheEntries { get; private init; }

    /// <summary>
    ///     Maximum number of generated leaf certificate files retained in the on-disk cache
    ///     (<see cref="Certificates.CertificateManager.SaveFakeCertificates" />), pruned independently
    ///     of <see cref="MaxCertificateCacheEntries" />. Disk is far cheaper than the in-memory cache's
    ///     live <see cref="System.Security.Cryptography.X509Certificates.X509Certificate2" /> handles,
    ///     and a warm disk cache avoids repeating expensive certificate generation across process
    ///     restarts, so this bound is deliberately independent and typically much larger (or
    ///     unbounded). <see langword="null" /> disables the bound.
    /// </summary>
    public int? MaxCertificateDiskCacheEntries { get; private init; }

    /// <summary>
    ///     Today's shipped values, carried forward as the <c>Balanced</c> profile's starting point
    ///     per the plan's rollout section: this is not a behavior change for existing traffic. Limits
    ///     newly introduced by the hardening plan (header aggregate bytes, decompression ratio,
    ///     CONTINUATION frame count, reset budget, admission cap) are set high enough that no
    ///     browser-generated traffic should reach them; they are expected to move once the benchmark
    ///     project has real numbers behind them.
    ///     <para>
    ///         <see cref="MaxCertificateCacheEntries" /> is the one deliberate exception to "unchanged
    ///         for existing traffic": measurement showed process memory holding steady at ~100 MB
    ///         above baseline after closing every browser tab and idling for minutes, tracking the
    ///         number of distinct MITM'd hosts rather than any live connection or session count.
    ///         Unbounded in-memory certificate retention is a defect, not a compatibility guarantee,
    ///         so the shipped default now bounds it at 1024 entries (roughly 10 MB, comfortably above
    ///         real single-session browsing) rather than leaving it unbounded like every other
    ///         nullable limit here defaults to.
    ///     </para>
    /// </summary>
    public static ProxyResourceLimits Default { get; } = Create(
        maxHeaderLineBytes: 64 * 1024,
        maxHeaderCount: 256,
        maxHeaderAggregateBytes: 256 * 1024,
        maxEncodedBodyBytes: null,
        maxDecodedBodyBytes: null,
        maxDecompressionRatio: 200,
        maxConcurrentClients: null,
        maxConcurrentStreamsPerConnection: 100,
        maxPeerInitiatedIncompleteStreamResets: 100,
        maxOpenHeaderBlockFrames: 128,
        maxOpenHeaderBlockDuration: TimeSpan.FromSeconds(10),
        connectionPoolingEnabled: true,
        maxCachedConnectionsPerHost: 4,
        maxCertificateCacheEntries: 1024);

    /// <summary>
    ///     Validates and constructs a <see cref="ProxyResourceLimits" /> snapshot. Every non-nullable
    ///     bound must be strictly positive; every nullable bound, if supplied, must also be strictly
    ///     positive (use <see langword="null" /> to disable rather than a sentinel number).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">A supplied value is zero or negative.</exception>
    public static ProxyResourceLimits Create( // NOSONAR S107 -- Public factory retains named parameters for source compatibility and discoverability.
        long maxHeaderLineBytes,
        int maxHeaderCount,
        long maxHeaderAggregateBytes,
        long? maxEncodedBodyBytes,
        long? maxDecodedBodyBytes,
        double? maxDecompressionRatio,
        int? maxConcurrentClients,
        int maxConcurrentStreamsPerConnection,
        int? maxPeerInitiatedIncompleteStreamResets,
        int maxOpenHeaderBlockFrames,
        TimeSpan maxOpenHeaderBlockDuration,
        bool connectionPoolingEnabled,
        int maxCachedConnectionsPerHost,
        int? maxCertificateCacheEntries)
    {
        RequirePositive(maxHeaderLineBytes, nameof(maxHeaderLineBytes));
        RequirePositive(maxHeaderCount, nameof(maxHeaderCount));
        RequirePositive(maxHeaderAggregateBytes, nameof(maxHeaderAggregateBytes));
        RequirePositiveIfPresent(maxEncodedBodyBytes, nameof(maxEncodedBodyBytes));
        RequirePositiveIfPresent(maxDecodedBodyBytes, nameof(maxDecodedBodyBytes));
        RequirePositiveIfPresent(maxDecompressionRatio, nameof(maxDecompressionRatio));
        RequirePositiveIfPresent(maxConcurrentClients, nameof(maxConcurrentClients));
        RequirePositive(maxConcurrentStreamsPerConnection, nameof(maxConcurrentStreamsPerConnection));
        RequirePositiveIfPresent(maxPeerInitiatedIncompleteStreamResets, nameof(maxPeerInitiatedIncompleteStreamResets));
        RequirePositive(maxOpenHeaderBlockFrames, nameof(maxOpenHeaderBlockFrames));
        RequirePositive(maxOpenHeaderBlockDuration, nameof(maxOpenHeaderBlockDuration));
        RequirePositive(maxCachedConnectionsPerHost, nameof(maxCachedConnectionsPerHost));
        RequirePositiveIfPresent(maxCertificateCacheEntries, nameof(maxCertificateCacheEntries));

        return new ProxyResourceLimits
        {
            MaxHeaderLineBytes = maxHeaderLineBytes,
            MaxHeaderCount = maxHeaderCount,
            MaxHeaderAggregateBytes = maxHeaderAggregateBytes,
            MaxEncodedBodyBytes = maxEncodedBodyBytes,
            MaxDecodedBodyBytes = maxDecodedBodyBytes,
            MaxDecompressionRatio = maxDecompressionRatio,
            MaxConcurrentClients = maxConcurrentClients,
            MaxConcurrentStreamsPerConnection = maxConcurrentStreamsPerConnection,
            MaxPeerInitiatedIncompleteStreamResets = maxPeerInitiatedIncompleteStreamResets,
            MaxOpenHeaderBlockFrames = maxOpenHeaderBlockFrames,
            MaxOpenHeaderBlockDuration = maxOpenHeaderBlockDuration,
            ConnectionPoolingEnabled = connectionPoolingEnabled,
            MaxCachedConnectionsPerHost = maxCachedConnectionsPerHost,
            MaxCertificateCacheEntries = maxCertificateCacheEntries
        };
    }

    /// <summary>
    ///     Returns a copy of this instance with <see cref="MaxCertificateCacheEntries" /> and
    ///     <see cref="MaxCertificateDiskCacheEntries" /> replaced, leaving every other limit
    ///     unchanged. Added instead of extending <see cref="Create" /> - which is public API already
    ///     shipped with a fixed parameter list - so that adding the independent disk-cache bound
    ///     could not be a breaking change for existing callers.
    /// </summary>
    /// <param name="maxCertificateCacheEntries">
    ///     See <see cref="MaxCertificateCacheEntries" />. <see langword="null" /> disables the bound.
    /// </param>
    /// <param name="maxCertificateDiskCacheEntries">
    ///     See <see cref="MaxCertificateDiskCacheEntries" />. <see langword="null" /> disables the bound.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">A supplied value is zero or negative.</exception>
    public ProxyResourceLimits WithCertificateCacheBounds(
        int? maxCertificateCacheEntries, int? maxCertificateDiskCacheEntries)
    {
        RequirePositiveIfPresent(maxCertificateCacheEntries, nameof(maxCertificateCacheEntries));
        RequirePositiveIfPresent(maxCertificateDiskCacheEntries, nameof(maxCertificateDiskCacheEntries));

        return new ProxyResourceLimits
        {
            MaxHeaderLineBytes = MaxHeaderLineBytes,
            MaxHeaderCount = MaxHeaderCount,
            MaxHeaderAggregateBytes = MaxHeaderAggregateBytes,
            MaxEncodedBodyBytes = MaxEncodedBodyBytes,
            MaxDecodedBodyBytes = MaxDecodedBodyBytes,
            MaxDecompressionRatio = MaxDecompressionRatio,
            MaxConcurrentClients = MaxConcurrentClients,
            MaxConcurrentStreamsPerConnection = MaxConcurrentStreamsPerConnection,
            MaxPeerInitiatedIncompleteStreamResets = MaxPeerInitiatedIncompleteStreamResets,
            MaxOpenHeaderBlockFrames = MaxOpenHeaderBlockFrames,
            MaxOpenHeaderBlockDuration = MaxOpenHeaderBlockDuration,
            ConnectionPoolingEnabled = ConnectionPoolingEnabled,
            MaxCachedConnectionsPerHost = MaxCachedConnectionsPerHost,
            MaxCertificateCacheEntries = maxCertificateCacheEntries,
            MaxCertificateDiskCacheEntries = maxCertificateDiskCacheEntries
        };
    }

    private static void RequirePositive(long value, string paramName)
    {
        if (value <= 0)
            throw new ArgumentOutOfRangeException(paramName, value,
                "Resource limits must be strictly positive. To disable a bound that supports it, use null rather than 0 or a negative number.");
    }

    private static void RequirePositive(double value, string paramName)
    {
        if (value <= 0)
            throw new ArgumentOutOfRangeException(paramName, value,
                "Resource limits must be strictly positive. To disable a bound that supports it, use null rather than 0 or a negative number.");
    }

    private static void RequirePositive(TimeSpan value, string paramName)
    {
        if (value <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(paramName, value,
                "Resource limits must be strictly positive. To disable a bound that supports it, use null rather than 0 or a negative number.");
    }

    private static void RequirePositiveIfPresent(long? value, string paramName)
    {
        if (value.HasValue) RequirePositive(value.Value, paramName);
    }

    private static void RequirePositiveIfPresent(int? value, string paramName)
    {
        if (value.HasValue) RequirePositive(value.Value, paramName);
    }

    private static void RequirePositiveIfPresent(double? value, string paramName)
    {
        if (value.HasValue) RequirePositive(value.Value, paramName);
    }
}
