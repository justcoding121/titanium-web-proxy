using System;
using System.Collections.Generic;
using System.Net;
using Titanium.Web.Proxy.Helpers;

namespace Titanium.Web.Proxy.Masque;

internal readonly record struct MasquePin(IPAddress? Address, string? Rejection);

/// <summary>Pins the first resolved address that the listener's port and address rules allow.</summary>
internal static class MasqueTargetPolicy
{
    public static MasquePin Select(string host, int port, IReadOnlyList<int> allowedPorts, bool blockPrivate,
        Func<string, IPAddress[]> resolve)
    {
        if (!Contains(allowedPorts, port))
        {
            return new MasquePin(null, "port");
        }

        IPAddress[] addresses;
        if (IPAddress.TryParse(host, out var literal))
        {
            addresses = [literal];
        }
        else
        {
            addresses = resolve(host);
        }

        foreach (var address in addresses)
        {
            if (blockPrivate && PrivateNetworkGuard.IsBlocked(address))
            {
                continue;
            }

            return new MasquePin(address, null);
        }

        return new MasquePin(null, addresses.Length == 0 ? "dns" : "blocked");
    }

    private static bool Contains(IReadOnlyList<int> ports, int port)
    {
        for (var i = 0; i < ports.Count; i++)
        {
            if (ports[i] == port)
            {
                return true;
            }
        }

        return false;
    }
}
