using System;
using System.Threading;
using Titanium.Web.Proxy.Options;

namespace Titanium.Web.Proxy.Logging;

/// <summary>
///     Stable id for one limit-driven failure. The rate limiter indexes this; do not renumber.
/// </summary>
internal enum LimitId
{
    BufferedBody = 0,
    HeaderCount = 1,
    HeaderAggregate = 2,
    HeaderLine = 3,
    Http3FramePayload = 4,
    DeferredOutboundBytes = 5,
    DecodedHeaderList = 6,
    TrailerHeaderCount = 7,
    TrailerHeaderBlock = 8,
    Http2CompressedHeaderBlock = 9,
    InterimResponses = 10,
    AuthChallengeRounds = 11,
    UpstreamProxyAuthAttempts = 12,
    WinAuthToken = 13,
    Http2WindowUpdate = 14,
    UpgradeHandshakeHeaders = 15,
    AdmissionGlobal = 16,
    AdmissionEndpoint = 17,
    TimeoutConnect = 18,
    TimeoutResponseHeader = 19,
    TimeoutIdleRead = 20,
    TimeoutIdleWrite = 21,
    TimeoutRequest = 22,
    TimeoutClientHeader = 23,
    WebSocketFrame = 24,
    Count = 25
}

/// <summary>
///     One row of the operator-facing limit reference: property, CLI key, default, and the hint
///     printed when the limit rejects a request. Docs and the config applier are checked against this.
/// </summary>
internal readonly struct LimitEntry
{
    public LimitEntry(LimitId id, string property, string cliKey, string what, string familyKey,
        string hint, PolicyFamily? family)
    {
        Id = id;
        Property = property;
        CliKey = cliKey;
        What = what;
        FamilyKey = familyKey;
        Hint = hint;
        Family = family;
    }

    public LimitId Id { get; }
    public string Property { get; }
    public string CliKey { get; }
    public string What { get; }
    public string FamilyKey { get; }
    public string Hint { get; }
    public PolicyFamily? Family { get; }
}

/// <summary>Single source of truth for limit names, CLI keys, and log hints.</summary>
internal static class LimitCatalog
{
    private const string HeaderLimitsKey = "headerLimits";
    private const string AdmissionControlKey = "admissionControl";

    private static readonly LimitEntry[] Entries = Build();

    internal static LimitEntry Get(LimitId id) => Entries[(int)id];

    internal static ReadOnlySpan<LimitEntry> All => Entries;

    internal static string Reason(LimitId id) => id.ToString();

    private static LimitEntry[] Build()
    {
        var all = new LimitEntry[(int)LimitId.Count];
        void Add(LimitEntry entry) => all[(int)entry.Id] = entry;

        Add(Row(LimitId.BufferedBody, "MaxBufferedBodyBytes", "server.limits.maxBufferedBodyBytes",
            "buffered body", "bodyBudget", PolicyFamily.BodyBudget));
        Add(Row(LimitId.HeaderCount, "MaxHeaderCount", "server.limits.maxHeaderCount",
            "header count", HeaderLimitsKey, PolicyFamily.HeaderLimits));
        Add(Row(LimitId.HeaderAggregate, "MaxHeaderAggregateBytes", "server.limits.maxHeaderAggregateBytes",
            "header block size", HeaderLimitsKey, PolicyFamily.HeaderLimits));
        Add(Row(LimitId.HeaderLine, "MaxHeaderLineBytes", "server.limits.maxHeaderLineBytes",
            "header line", HeaderLimitsKey, PolicyFamily.HeaderLimits));
        Add(Row(LimitId.Http3FramePayload, "MaxHttp3FramePayloadBytes", "server.limits.maxHttp3FramePayloadBytes",
            "HTTP/3 frame payload", "bodyBudget", PolicyFamily.BodyBudget));
        Add(Row(LimitId.DeferredOutboundBytes, "MaxDeferredOutboundBytesPerStream",
            "server.limits.maxDeferredOutboundBytesPerStream",
            "HTTP/2 deferred DATA", "bodyBudget", PolicyFamily.BodyBudget));
        Add(Row(LimitId.DecodedHeaderList, "MaxDecodedHeaderListBytes", "server.limits.maxDecodedHeaderListBytes",
            "decoded header list", HeaderLimitsKey, PolicyFamily.HeaderLimits));
        Add(Row(LimitId.TrailerHeaderCount, "MaxTrailerHeaderCount", "server.limits.maxTrailerHeaderCount",
            "trailer header count", HeaderLimitsKey, PolicyFamily.HeaderLimits));
        Add(Row(LimitId.TrailerHeaderBlock, "MaxTrailerHeaderBlockBytes", "server.limits.maxTrailerHeaderBlockBytes",
            "trailer header block", HeaderLimitsKey, PolicyFamily.HeaderLimits));
        Add(Row(LimitId.Http2CompressedHeaderBlock, "MaxHttp2CompressedHeaderBlockBytes",
            "server.limits.maxHttp2CompressedHeaderBlockBytes",
            "compressed header block", HeaderLimitsKey, PolicyFamily.HeaderLimits));
        Add(Row(LimitId.InterimResponses, "MaxInterimResponses", "server.limits.maxInterimResponses",
            "interim responses", HeaderLimitsKey, PolicyFamily.HeaderLimits));
        Add(Row(LimitId.AuthChallengeRounds, "MaxAuthChallengeRounds", "server.limits.maxAuthChallengeRounds",
            "authentication challenge rounds", AdmissionControlKey, PolicyFamily.AdmissionControl));
        Add(Row(LimitId.UpstreamProxyAuthAttempts, "MaxUpstreamProxyAuthenticationAttempts",
            "server.limits.maxUpstreamProxyAuthenticationAttempts",
            "upstream proxy authentication attempts", AdmissionControlKey, PolicyFamily.AdmissionControl));
        Add(Row(LimitId.WinAuthToken, "MaxWinAuthTokenBytes", "server.limits.maxWinAuthTokenBytes",
            "Windows authentication token", AdmissionControlKey, PolicyFamily.AdmissionControl));
        Add(Row(LimitId.Http2WindowUpdate, "Http2WindowUpdateTimeoutSeconds",
            "server.limits.http2WindowUpdateTimeoutSeconds",
            "HTTP/2 flow-control wait", "http2AbuseBudget", PolicyFamily.Http2AbuseBudget));
        Add(Row(LimitId.UpgradeHandshakeHeaders, "MaxHeaderCount", "server.limits.maxHeaderCount",
            "upgrade handshake headers", HeaderLimitsKey, PolicyFamily.HeaderLimits));
        Add(Row(LimitId.AdmissionGlobal, "MaxConcurrentClientConnections",
            "server.pooling.maxConcurrentClientConnections",
            "client connections", AdmissionControlKey, PolicyFamily.AdmissionControl));
        Add(Row(LimitId.AdmissionEndpoint, "MaxConcurrentClients", "server.limits.maxConcurrentClients",
            "endpoint client connections", AdmissionControlKey, PolicyFamily.AdmissionControl));
        Add(Timeout(LimitId.TimeoutConnect, "ConnectTimeOutSeconds", "server.timeouts.connectTimeOutSeconds",
            "connect"));
        Add(Timeout(LimitId.TimeoutResponseHeader, "ResponseHeaderTimeoutSeconds",
            "server.timeouts.responseHeaderTimeoutSeconds", "response header"));
        Add(Timeout(LimitId.TimeoutIdleRead, "IdleReadTimeoutSeconds", "server.timeouts.idleReadTimeoutSeconds",
            "idle read"));
        Add(Timeout(LimitId.TimeoutIdleWrite, "IdleWriteTimeoutSeconds", "server.timeouts.idleWriteTimeoutSeconds",
            "idle write"));
        Add(Timeout(LimitId.TimeoutRequest, "RequestTimeoutSeconds", "server.timeouts.requestTimeoutSeconds",
            "request"));
        Add(Timeout(LimitId.TimeoutClientHeader, "ClientHeaderTimeoutSeconds",
            "server.timeouts.clientHeaderTimeoutSeconds", "client header"));
        Add(Row(LimitId.WebSocketFrame, "MaxWebSocketFramePayloadBytes",
            "server.limits.maxWebSocketFramePayloadBytes",
            "WebSocket frame", "webSocketFrameBudget", PolicyFamily.WebSocketFrameBudget));
        return all;
    }

    private static LimitEntry Row(LimitId id, string property, string cliKey, string what, string familyKey,
        PolicyFamily family) =>
        new(id, property, cliKey, what, familyKey,
            $"Raise {cliKey} or set server.policyModes.{familyKey} to observe or disabled.", family);

    private static LimitEntry Timeout(LimitId id, string property, string cliKey, string what) =>
        new(id, property, cliKey, what + " timeout", "timeouts",
            $"Raise {cliKey}.", null);
}

/// <summary>
///     Per-limit log throttle. First event is logged immediately; later events within 10 seconds
///     increment a suppressed count and are dropped. No locks. A suppressed call allocates nothing.
/// </summary>
internal static class LimitLogThrottle
{
    internal const long WindowTicks = 10L * TimeSpan.TicksPerSecond;

    private static readonly long[] LastTicks = new long[(int)LimitId.Count];
    private static readonly int[] Suppressed = new int[(int)LimitId.Count];

    /// <summary>
    ///     Returns the number of events suppressed since the previous logged line (0 on the first),
    ///     or -1 when this call must not log.
    /// </summary>
    internal static int TryAcquire(LimitId id)
    {
        var index = (int)id;
        var now = DateTime.UtcNow.Ticks;
        var last = Volatile.Read(ref LastTicks[index]);
        if (last != 0 && now - last < WindowTicks)
        {
            Interlocked.Increment(ref Suppressed[index]);
            return -1;
        }

        if (Interlocked.CompareExchange(ref LastTicks[index], now, last) != last)
        {
            Interlocked.Increment(ref Suppressed[index]);
            return -1;
        }

        return Interlocked.Exchange(ref Suppressed[index], 0);
    }

    internal static void Reset()
    {
        Array.Clear(LastTicks);
        Array.Clear(Suppressed);
    }
}
