namespace Titanium.Web.Proxy.Exceptions;

/// <summary>
///     Thrown when <see cref="Proxy.ProxyServer.BlockPrivateNetworkDestinations" /> is enabled and a
///     request's resolved destination address is loopback, private, link-local, or another
///     non-globally-routable address - the outbound destination policy hook described in the
///     hardening plan's "PublicFacing" posture, protecting a proxy that accepts requests from
///     untrusted clients against SSRF into the host's own private network.
/// </summary>
public sealed class OutboundDestinationBlockedException : ProxyException
{
    internal OutboundDestinationBlockedException(string hostname, string blockedAddress)
        : base($"Connection to '{hostname}' ({blockedAddress}) was blocked because " +
               $"{nameof(Proxy.ProxyServer.BlockPrivateNetworkDestinations)} is enabled and the resolved " +
               "address is not a globally routable destination.")
    {
        Hostname = hostname;
        BlockedAddress = blockedAddress;
    }

    /// <summary>
    ///     The hostname that was being connected to when the block occurred.
    /// </summary>
    public string Hostname { get; }

    /// <summary>
    ///     The specific resolved IP address (as a string) that triggered the block.
    /// </summary>
    public string BlockedAddress { get; }
}
