using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Titanium.Web.Proxy.Network.Quic;

/// <summary>
///     Resolves the original (pre-NAT) destination of an inbound QUIC connection.
///     <para>
///         When the proxy intercepts traffic via firewall/NAT redirection, the destination seen by the OS
///         socket is the proxy's own UDP endpoint, not the origin server. This interface bridges the
///         managed interception contract: the operator's firewall rules or packet-filter driver supply
///         original-destination data, and this adapter exposes it to the proxy.
///     </para>
///     <para>
///         SNI from the QUIC ClientHello (<see cref="BeforeQuicAuthenticateEventArgs.SniHostName" />) and
///         the <c>:authority</c> pseudo-header are validation inputs and context hints, but are not a
///         reliable replacement for the actual original destination obtained from the OS routing table or
///         packet-filter metadata, especially for pinned certificates, IP-literal URLs, or non-standard ports.
///     </para>
/// </summary>
public interface IOriginalDestinationResolver
{
    /// <summary>
    ///     Resolves the original destination host name and port for the given inbound QUIC connection.
    /// </summary>
    /// <param name="localEndPoint">The local UDP endpoint the proxy is listening on.</param>
    /// <param name="remoteEndPoint">The remote UDP endpoint of the connecting QUIC client.</param>
    /// <param name="sniHostName">
    ///     The SNI hostname from the QUIC ClientHello, or <see langword="null" /> if not present.
    ///     May be used as a hint but should not be trusted as the authoritative destination.
    /// </param>
    /// <param name="cancellationToken">Cancellation token for the lookup.</param>
    /// <returns>
    ///     The resolved (<c>hostName</c>, <c>port</c>) tuple, where <c>hostName</c> is the original
    ///     destination host name or IP address string and <c>port</c> is the original destination port.
    ///     Returns <see langword="null" /> if resolution fails and the caller should fall back to
    ///     <see cref="TransparentQuicProxyEndPoint.ForwardHost" /> / <see cref="TransparentQuicProxyEndPoint.ForwardPort" />.
    /// </returns>
    ValueTask<(string hostName, int port)?> ResolveAsync(
        IPEndPoint localEndPoint,
        IPEndPoint remoteEndPoint,
        string? sniHostName,
        CancellationToken cancellationToken);
}
