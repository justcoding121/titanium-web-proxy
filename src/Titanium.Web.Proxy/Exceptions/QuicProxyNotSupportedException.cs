using System;

namespace Titanium.Web.Proxy.Exceptions;

/// <summary>
///     Thrown by <see cref="Network.Quic.QuicConnectionFactory" /> when an upstream proxy is configured
///     but <c>System.Net.Quic</c> does not expose a hook for CONNECT tunnelling or SOCKS5 UDP ASSOCIATE.
///     The caller should catch this and fall back to a TCP-based bridge so that proxy rules are honoured
///     on the TCP leg rather than being silently bypassed.
/// </summary>
internal sealed class QuicProxyNotSupportedException : Exception // NOSONAR S3871 -- internal transport fallback signal is not supported public API.
{
    internal QuicProxyNotSupportedException(string proxyDescription)
        : base($"QUIC cannot route via proxy '{proxyDescription}': System.Net.Quic does not support " +
               "CONNECT tunnelling or SOCKS5 UDP ASSOCIATE. Fall back to TCP.")
    {
    }
}
