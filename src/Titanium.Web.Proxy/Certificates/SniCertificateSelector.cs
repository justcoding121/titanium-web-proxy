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
                if (string.IsNullOrEmpty(host))
                {
                    continue;
                }

                if (host[0] != '*')
                {
                    if (string.Equals(host, serverName, StringComparison.OrdinalIgnoreCase))
                    {
                        return bindings[i].Certificate;
                    }

                    continue;
                }

                var suffix = host.Length > 1 && host[1] == '.' ? host[1..] : host[1..];
                if (serverName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) &&
                    serverName.Length > suffix.Length &&
                    suffix.Length > wildcardLength)
                {
                    wildcard = bindings[i].Certificate;
                    wildcardLength = suffix.Length;
                }
            }
        }

        return wildcard;
    }
}
