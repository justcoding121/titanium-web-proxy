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

        var allowed = SelectAll(port, allowedPorts, blockPrivate, addresses, out var rejection);
        return allowed.Count == 0 ? new MasquePin(null, rejection) : new MasquePin(allowed[0], null);
    }

    /// <summary>Every resolved address the port and private-range rules allow, in order.</summary>
    public static IReadOnlyList<IPAddress> SelectAll(int port, IReadOnlyList<int> allowedPorts, bool blockPrivate,
        IReadOnlyList<IPAddress> addresses, out string? rejection)
    {
        if (!Contains(allowedPorts, port))
        {
            rejection = "port";
            return [];
        }

        var allowed = new List<IPAddress>();
        foreach (var address in addresses)
        {
            if (blockPrivate && PrivateNetworkGuard.IsBlocked(address))
            {
                continue;
            }

            allowed.Add(address);
        }

        if (allowed.Count != 0)
        {
            rejection = null;
        }
        else if (addresses.Count == 0)
        {
            rejection = "dns";
        }
        else
        {
            rejection = "blocked";
        }
        return allowed;
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
