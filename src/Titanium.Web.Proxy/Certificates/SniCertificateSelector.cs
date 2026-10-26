using System;
using System.Collections.Generic;
using System.Security.Cryptography.X509Certificates;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.Certificates;

/// <summary>Exact host, then the longest wildcard, otherwise null so the caller uses its default.</summary>
public static class SniCertificateSelector
{
    public static X509Certificate2? Select(string? serverName, IReadOnlyList<SniCertificateBinding> bindings)
    {
        if (string.IsNullOrEmpty(serverName) || bindings.Count == 0)
        {
            return null;
        }

        X509Certificate2? wildcard = null;
        var wildcardLength = -1;
        for (var i = 0; i < bindings.Count; i++)
        {
            var hosts = bindings[i].Hosts;
            for (var h = 0; h < hosts.Count; h++)
            {
                var host = hosts[h];
                if (IsExactHost(host, serverName))
                {
                    return bindings[i].Certificate;
                }

                ConsiderWildcard(host, serverName, bindings[i].Certificate, ref wildcard, ref wildcardLength);
            }
        }

        return wildcard;
    }

    private static bool IsExactHost(string host, string serverName) =>
        host.Length > 0 && host[0] != '*' && string.Equals(host, serverName, StringComparison.OrdinalIgnoreCase);

    private static void ConsiderWildcard(
        string host,
        string serverName,
        X509Certificate2 certificate,
        ref X509Certificate2? wildcard,
        ref int wildcardLength)
    {
        if (host.Length == 0 || host[0] != '*')
        {
            return;
        }

        var suffix = host[1..];
        if (serverName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            && serverName.Length > suffix.Length
            && suffix.Length > wildcardLength)
        {
            wildcard = certificate;
            wildcardLength = suffix.Length;
        }
    }
}
