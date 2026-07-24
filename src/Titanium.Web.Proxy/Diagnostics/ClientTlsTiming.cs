using System;

namespace Titanium.Web.Proxy.Diagnostics;

/// <summary>
///     Captures the timing of the client-facing (browser-to-proxy) TLS handshake performed while decrypting
///     an HTTPS tunnel. Only populated when <see cref="ProxyServer.EnableRequestTimingCapture" /> is
///     enabled; otherwise no instance is ever allocated.
/// </summary>
public sealed class ClientTlsTiming
{
    internal ClientTlsTiming(DateTime startedAt)
    {
        StartedAt = startedAt;
    }

    /// <summary>When the proxy started authenticating itself to the client as a TLS server.</summary>
    public DateTime StartedAt { get; }

    /// <summary>When the TLS handshake with the client completed successfully. Null if it failed.</summary>
    public DateTime? CompletedAt { get; internal set; }

    /// <summary>How long the handshake took. Null until it has completed.</summary>
    public TimeSpan? HandshakeDuration => CompletedAt - StartedAt;

    internal void MarkCompleted()
    {
        CompletedAt = DateTime.UtcNow;
    }
}
