using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using Titanium.Web.Proxy.Abstractions.Network;
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
    ///     Cancels the Kestrel-style <c>AcceptAsync</c> loop started for <see cref="Listener" />.
    /// </summary>
    internal CancellationTokenSource? AcceptLoopCts { get; set; }

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

    /// <summary>SNI certificate list. Null keeps <see cref="GenericCertificate"/>.</summary>
    public IReadOnlyList<SniCertificateBinding>? SniCertificates { get; set; }

    /// <summary>Inbound client-certificate mode. <see cref="ClientCertificateMode.None"/> changes nothing.</summary>
    public ClientCertificateMode ClientCertificateMode { get; set; }

    /// <summary>
    ///     Trust anchors for an inbound client certificate. Null uses the platform trust store,
    ///     which is the historical behavior. A reload assigns a new collection; it never mutates one
    ///     that a handshake is already reading.
    /// </summary>
    public X509Certificate2Collection? ClientCertificateTrustedRoots { get; set; }

    /// <summary>When true, the accept loop requires a PROXY protocol preamble.</summary>
    public bool AcceptProxyProtocol { get; set; }

    private CidrPrefix[]? _trustedProxySources;
    private IReadOnlyList<string>? _trustedProxySourceText;

    /// <summary>
    /// CIDR prefixes allowed to send a PROXY protocol preamble. Null or empty keeps today's behavior:
    /// any peer may send the preamble. A non-empty list skips the preamble for everyone else.
    /// </summary>
    public IReadOnlyList<string>? TrustedProxyProtocolSources
    {
        get => _trustedProxySourceText;
        set
        {
            if (value is null || value.Count == 0)
            {
                _trustedProxySourceText = value;
                _trustedProxySources = null;
                return;
            }

            var parsed = new CidrPrefix[value.Count];
            var text = new string[value.Count];
            for (var i = 0; i < value.Count; i++)
            {
                if (!CidrPrefix.TryParse(value[i], out var prefix) || prefix is null)
                {
                    throw new FormatException($"Invalid PROXY protocol CIDR '{value[i]}'.");
                }

                parsed[i] = prefix;
                text[i] = value[i];
            }

            _trustedProxySources = parsed;
            _trustedProxySourceText = text;
        }
    }

    /// <summary>True when the peer may send a PROXY protocol preamble.</summary>
    internal bool IsTrustedProxyProtocolPeer(IPAddress? address)
    {
        if (_trustedProxySources is null)
        {
            return true;
        }

        if (address is null)
        {
            return false;
        }

        return _trustedProxySources.Any(prefix => prefix.Contains(address));
    }

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

    /// <summary>Single-slot forward-host equivalence cache for this listener.</summary>
    internal Routing.ForwardHostEquivalenceCache? ForwardHostEquivalence;

    /// <summary>
    /// Endpoint-level override for <see cref="ProxyServer.EnableHttpInterception"/>.
    /// <see langword="null"/> inherits the server-level value.
    /// </summary>
    public bool? EnableHttpInterception { get; set; }

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