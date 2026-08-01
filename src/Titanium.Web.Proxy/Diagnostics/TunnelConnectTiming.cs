using System;

namespace Titanium.Web.Proxy.Diagnostics;

/// <summary>
///     Captures CONNECT-phase milestones that dominate cold HTTPS page-load latency: certificate
///     readiness, origin capability discovery (SVCB / HTTP/2 probe), and browser TLS completion.
///     Only allocated when <see cref="ProxyServer.EnableRequestTimingCapture" /> is enabled.
/// </summary>
public sealed class TunnelConnectTiming
{
    internal TunnelConnectTiming(DateTime startedAt)
    {
        StartedAt = startedAt;
    }

    /// <summary>When CONNECT decryption / capability negotiation began.</summary>
    public DateTime StartedAt { get; }

    /// <summary>When the fake leaf certificate became available (cache hit or generation complete).</summary>
    public DateTime? CertificateReadyAt { get; private set; }

    /// <summary>When origin HTTP/3 capability resolution started (cache lookup and/or SVCB queue).</summary>
    public DateTime? OriginCapabilityStartedAt { get; private set; }

    /// <summary>When origin HTTP/3 capability resolution returned a decision for this CONNECT.</summary>
    public DateTime? OriginCapabilityCompletedAt { get; private set; }

    /// <summary>
    ///     How the HTTP/3 capability decision was reached for this CONNECT
    ///     (<c>cache</c>, <c>forced</c>, <c>none</c>, or <c>background</c> when SVCB was queued).
    /// </summary>
    public string? OriginCapabilitySource { get; private set; }

    /// <summary>When the HTTP/2 origin capability probe (or cache lookup) started.</summary>
    public DateTime? Http2ProbeStartedAt { get; private set; }

    /// <summary>When the HTTP/2 origin capability probe (or cache lookup) completed.</summary>
    public DateTime? Http2ProbeCompletedAt { get; private set; }

    /// <summary>
    ///     Whether the HTTP/2 capability result came from cache (<see langword="true" />), a live
    ///     probe (<see langword="false" />), or was skipped (<see langword="null" />).
    /// </summary>
    public bool? Http2CapabilityCacheHit { get; private set; }

    /// <summary>When the browser-facing TLS handshake started.</summary>
    public DateTime? BrowserTlsStartedAt { get; private set; }

    /// <summary>When the browser-facing TLS handshake completed successfully.</summary>
    public DateTime? BrowserTlsCompletedAt { get; private set; }

    /// <summary>Wall time from CONNECT capability work start to browser TLS completion.</summary>
    public TimeSpan? TotalDuration => BrowserTlsCompletedAt - StartedAt;

    /// <summary>How long certificate generation/load took, once both marks are present.</summary>
    public TimeSpan? CertificateDuration =>
        CertificateReadyAt.HasValue ? CertificateReadyAt - StartedAt : null;

    /// <summary>How long origin capability resolution took for this CONNECT.</summary>
    public TimeSpan? OriginCapabilityDuration =>
        OriginCapabilityCompletedAt - OriginCapabilityStartedAt;

    /// <summary>How long the HTTP/2 capability probe or cache lookup took.</summary>
    public TimeSpan? Http2ProbeDuration => Http2ProbeCompletedAt - Http2ProbeStartedAt;

    /// <summary>How long the browser TLS handshake took.</summary>
    public TimeSpan? BrowserTlsDuration => BrowserTlsCompletedAt - BrowserTlsStartedAt;

    internal void MarkCertificateReady() => CertificateReadyAt = DateTime.UtcNow;

    internal void MarkOriginCapabilityStarted(string source)
    {
        OriginCapabilityStartedAt = DateTime.UtcNow;
        OriginCapabilitySource = source;
    }

    internal void MarkOriginCapabilityCompleted(string source)
    {
        OriginCapabilityCompletedAt = DateTime.UtcNow;
        OriginCapabilitySource = source;
    }

    internal void MarkHttp2ProbeStarted(bool cacheHit)
    {
        Http2ProbeStartedAt = DateTime.UtcNow;
        Http2CapabilityCacheHit = cacheHit;
    }

    internal void MarkHttp2ProbeCompleted() => Http2ProbeCompletedAt = DateTime.UtcNow;

    internal void MarkBrowserTlsStarted() => BrowserTlsStartedAt = DateTime.UtcNow;

    internal void MarkBrowserTlsCompleted() => BrowserTlsCompletedAt = DateTime.UtcNow;
}
