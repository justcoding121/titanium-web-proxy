using System;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Titanium.Web.Proxy.Http3.Dns;

/// <summary>
///     Best-effort discovery of the OS-configured plain-UDP DNS server for HTTPS/SVCB queries.
///     This does <b>not</b> honor Windows NRPT, DoH, or VPN split-DNS policy; callers that need
///     system-resolver fidelity must supply their own <see cref="IHttpsSvcbResolver" />.
/// </summary>
internal static class OsConfiguredDnsServer
{
    private static readonly object Gate = new();
    private static IPEndPoint? cached;
    private static bool cachedResolved;
    private static bool networkChangeHooked;

    /// <summary>
    ///     Returns the first usable OS-configured DNS server endpoint, or <see langword="null" /> when
    ///     none can be discovered. Never falls back to a public third-party resolver.
    /// </summary>
    internal static IPEndPoint? TryGetPrimaryDnsServer()
    {
        EnsureNetworkChangeHook();

        lock (Gate)
        {
            if (cachedResolved) return cached;

            cached = Discover();
            cachedResolved = true;
            return cached;
        }
    }

    /// <summary>Clears the cached discovery result (tests and network-change notifications).</summary>
    internal static void InvalidateCache()
    {
        lock (Gate)
        {
            cached = null;
            cachedResolved = false;
        }
    }

    private static void EnsureNetworkChangeHook()
    {
        if (networkChangeHooked) return;

        lock (Gate)
        {
            if (networkChangeHooked) return;

            try
            {
                NetworkChange.NetworkAddressChanged += (_, _) => InvalidateCache();
                NetworkChange.NetworkAvailabilityChanged += (_, _) => InvalidateCache();
            }
            catch
            {
                // Some runtimes/platforms throw when registering; discovery still works without refresh.
            }

            networkChangeHooked = true;
        }
    }

    private static IPEndPoint? Discover()
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces()
                         .Where(n => n.OperationalStatus == OperationalStatus.Up)
                         .Where(n => n.NetworkInterfaceType is not NetworkInterfaceType.Loopback
                             and not NetworkInterfaceType.Tunnel))
            {
                IPAddressCollection addresses;
                try
                {
                    addresses = nic.GetIPProperties().DnsAddresses;
                }
                catch
                {
                    continue;
                }

                foreach (var address in addresses)
                {
                    if (!IsUsableDnsAddress(address)) continue;
                    return new IPEndPoint(address, 53);
                }
            }
        }
        catch
        {
            // Discovery is best-effort; callers disable SVCB when this returns null.
        }

        return null;
    }

    private static bool IsUsableDnsAddress(IPAddress address)
    {
        if (IPAddress.Any.Equals(address) || IPAddress.IPv6Any.Equals(address))
            return false;

        if (address.AddressFamily == AddressFamily.InterNetworkV6 && address.IsIPv6LinkLocal &&
            address.ScopeId == 0)
            return false;

        return true;
    }
}
