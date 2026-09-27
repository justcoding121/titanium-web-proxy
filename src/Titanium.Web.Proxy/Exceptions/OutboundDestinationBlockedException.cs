namespace Titanium.Web.Proxy.Exceptions;

/// <summary>
///     Thrown when an outbound destination policy hook rejects a resolved destination address:
///     <see cref="Proxy.ProxyServer.BlockPrivateNetworkDestinations" /> (private, link-local,
///     loopback, and other non-globally-routable ranges) or
///     <see cref="Proxy.ProxyServer.BlockLoopbackDestinations" /> (loopback only).
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
    ///     Loopback-only reject for <see cref="Proxy.ProxyServer.BlockLoopbackDestinations" />.
    ///     Distinct from the primary constructor so the message names the loopback flag.
    /// </summary>
    internal OutboundDestinationBlockedException(string hostname, string blockedAddress,
        LoopbackDestinationBlockTag _)
        : base($"Connection to '{hostname}' ({blockedAddress}) was blocked because " +
               $"{nameof(Proxy.ProxyServer.BlockLoopbackDestinations)} is enabled and the resolved " +
               "address is a loopback destination.")
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

    /// <summary>
    ///     Distinguishes the loopback-only constructor overload from the private-network constructor.
    /// </summary>
    internal readonly struct LoopbackDestinationBlockTag
    {
        public static LoopbackDestinationBlockTag Instance => default;
    }
}
