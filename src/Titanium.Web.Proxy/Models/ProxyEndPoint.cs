using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using Titanium.Web.Proxy.Diagnostics;
using Titanium.Web.Proxy.Options;

namespace Titanium.Web.Proxy.Models;

/// <summary>
///     An abstract endpoint where the proxy listens
/// </summary>
public abstract class ProxyEndPoint
{
    /// <summary>
    ///     Backing field for <see cref="AdmittedClientCount" />, incremented/decremented synchronously
    ///     by <see cref="TryAdmitClient" />/<see cref="ReleaseClient" /> as part of
    ///     <c>ProxyServer</c>'s admission gate. Deliberately separate from any TIME_WAIT-delayed
    ///     connection-close bookkeeping; see <see cref="ProxyServer.MaxConcurrentClientConnections" />.
    /// </summary>
    private int admittedClientCount;

    /// <summary>
    ///     Constructor.
    /// </summary>
    /// <param name="ipAddress"></param>
    /// <param name="port"></param>
    /// <param name="decryptSsl"></param>
    protected ProxyEndPoint(IPAddress ipAddress, int port, bool decryptSsl)
    {
        IpAddress = ipAddress;
        Port = port;
        DecryptSsl = decryptSsl;
    }

    /// <summary>
    ///     underlying TCP Listener object
    /// </summary>
    internal TcpListener? Listener { get; set; }

    /// <summary>
    ///     Ip Address we are listening.
    /// </summary>
    public IPAddress IpAddress { get; }

    /// <summary>
    ///     Port we are listening.
    /// </summary>
    public int Port { get; internal set; }

    /// <summary>
    ///     Enable SSL?
    /// </summary>
    public bool DecryptSsl { get; }

    /// <summary>
    ///     Generic certificate to use for SSL decryption.
    /// </summary>
    public X509Certificate2? GenericCertificate { get; set; }

    /// <summary>
    /// Optional override of <see cref="ProxyServer.MaxCachedConnections"/> for upstream TCP pooling
    /// when this endpoint handles the session. <see langword="null"/> uses the server default.
    /// Useful for reverse-proxy endpoints that want a deeper pool toward a hot origin without
    /// raising the process-wide default for every explicit MITM host.
    /// </summary>
    public int? MaxCachedConnections { get; set; }

    /// <summary>
    ///     Maximum number of client connections admitted on this endpoint at once, layered on top of
    ///     <see cref="ProxyServer.MaxConcurrentClientConnections" />. <see langword="null" /> (the
    ///     default) disables the per-endpoint admission gate, preserving today's unbounded behavior.
    /// </summary>
    public int? MaxConcurrentClients { get; set; }

    /// <summary>
    ///     Number of client connections currently admitted on this endpoint (accepted and past the
    ///     admission gate, not yet finished being handled).
    /// </summary>
    public int AdmittedClientCount => Volatile.Read(ref admittedClientCount);

    /// <summary>
    ///     Attempts to reserve one admission slot on this endpoint, enforcing
    ///     <see cref="MaxConcurrentClients" /> via a lock-free compare-and-swap loop rather than an
    ///     increment-then-rollback, so <see cref="AdmittedClientCount" /> never transiently overshoots
    ///     the configured limit even under contention. Returns <see langword="false" /> (without
    ///     reserving anything) once the limit is reached and <paramref name="mode" /> is
    ///     <see cref="PolicyMode.Enforce" />. Under <see cref="PolicyMode.Observe" /> the breach is
    ///     recorded but the slot is still admitted; under <see cref="PolicyMode.Disabled" /> the limit
    ///     is not consulted at all.
    /// </summary>
    internal bool TryAdmitClient(PolicyMode mode)
    {
        while (true)
        {
            var current = Volatile.Read(ref admittedClientCount);
            if (mode != PolicyMode.Disabled && MaxConcurrentClients is { } limit && current >= limit)
            {
                ProxyMetrics.PolicyBreach(PolicyFamily.AdmissionControl, mode);
                if (mode == PolicyMode.Enforce) return false;
            }

            if (Interlocked.CompareExchange(ref admittedClientCount, current + 1, current) == current) return true;
        }
    }

    /// <summary>
    ///     Releases one admission slot previously reserved by <see cref="TryAdmitClient" />. Must be
    ///     called exactly once per successful reservation.
    /// </summary>
    internal void ReleaseClient()
    {
        Interlocked.Decrement(ref admittedClientCount);
    }
}