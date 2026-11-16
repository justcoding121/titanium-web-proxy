using System;
using System.Collections.Generic;
using System.Security.Cryptography.X509Certificates;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.Certificates;

/// <summary>
///     Exact host, then the longest wildcard. Among matching bindings prefer one that is still
///     valid; if none is valid, fall back to the best match so behavior never gets worse than
///     returning an expired certificate when that is all that is configured.
/// </summary>
public static class SniCertificateSelector
{
    public static X509Certificate2? Select(string? serverName, IReadOnlyList<SniCertificateBinding> bindings) =>
        Select(serverName, bindings, DateTime.UtcNow);

    /// <summary>Test seam with an injected clock.</summary>
    public static X509Certificate2? Select(
        string? serverName,
        IReadOnlyList<SniCertificateBinding> bindings,
        DateTime utcNow)
    {
        if (string.IsNullOrEmpty(serverName) || bindings.Count == 0)
        {
            return null;
        }

        SniCertificateBinding? exactValid = null;
        SniCertificateBinding? exactAny = null;
        SniCertificateBinding? wildcardValid = null;
        SniCertificateBinding? wildcardAny = null;
        var wildcardValidLength = -1;
        var wildcardAnyLength = -1;

        for (var i = 0; i < bindings.Count; i++)
        {
            var binding = bindings[i];
            var hosts = binding.Hosts;
            for (var h = 0; h < hosts.Count; h++)
            {
                var host = hosts[h];
                if (IsExactHost(host, serverName))
                {
                    exactAny ??= binding;
                    if (binding.IsValidAt(utcNow))
                    {
                        exactValid ??= binding;
                    }

                    continue;
                }

                ConsiderWildcard(host, serverName, binding, utcNow,
                    ref wildcardValid, ref wildcardValidLength,
                    ref wildcardAny, ref wildcardAnyLength);
            }
        }

        return exactValid?.Certificate
               ?? exactAny?.Certificate
               ?? wildcardValid?.Certificate
               ?? wildcardAny?.Certificate;
    }

    private static bool IsExactHost(string host, string serverName) =>
        host.Length > 0 && host[0] != '*' && string.Equals(host, serverName, StringComparison.OrdinalIgnoreCase);

    private static void ConsiderWildcard(
        string host,
        string serverName,
        SniCertificateBinding binding,
        DateTime utcNow,
        ref SniCertificateBinding? wildcardValid,
        ref int wildcardValidLength,
        ref SniCertificateBinding? wildcardAny,
        ref int wildcardAnyLength)
    {
        if (host.Length == 0 || host[0] != '*')
        {
            return;
        }

        var suffix = host[1..];
        if (!serverName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            || serverName.Length <= suffix.Length)
        {
            return;
        }

        if (suffix.Length > wildcardAnyLength)
        {
            wildcardAny = binding;
            wildcardAnyLength = suffix.Length;
        }

        if (binding.IsValidAt(utcNow) && suffix.Length > wildcardValidLength)
        {
            wildcardValid = binding;
            wildcardValidLength = suffix.Length;
        }
    }
}
