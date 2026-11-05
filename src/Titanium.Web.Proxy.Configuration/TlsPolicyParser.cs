using System.Net.Security;
using System.Security.Authentication;

namespace Titanium.Web.Proxy.Configuration;

/// <summary>Parses the listener TLS policy names of <c>server.tls</c>.</summary>
public static class TlsPolicyParser
{
    /// <summary>
    ///     Accepts <c>Tls12</c>, <c>Tls13</c>, <c>1.2</c>, <c>1.3</c>, <c>TLSv1.2</c>, and <c>TLSv1.3</c>
    ///     (case-insensitive). Older versions are not accepted as a minimum.
    /// </summary>
    public static bool TryParseMinVersion(string? text, out SslProtocols minimum)
    {
        minimum = SslProtocols.None;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var name = text.Trim().Replace("TLSv", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("TLS", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace(".", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal);
        switch (name)
        {
            case "12":
                minimum = SslProtocols.Tls12;
                return true;
            case "13":
                minimum = SslProtocols.Tls13;
                return true;
            default:
                return false;
        }
    }

    /// <summary>Parses one <see cref="TlsCipherSuite" /> name. Numeric values are rejected.</summary>
    public static bool TryParseCipherSuite(string? text, out TlsCipherSuite suite)
    {
        suite = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var name = text.Trim();
        return !char.IsDigit(name[0])
               && Enum.TryParse(name, ignoreCase: true, out suite)
               && Enum.IsDefined(suite);
    }

    /// <summary>Parses every name, or returns false when any is unknown.</summary>
    public static bool TryParseCipherSuites(IEnumerable<string> names, out IReadOnlyList<TlsCipherSuite> suites)
    {
        var list = new List<TlsCipherSuite>();
        foreach (var name in names)
        {
            if (!TryParseCipherSuite(name, out var suite))
            {
                suites = [];
                return false;
            }

            if (!list.Contains(suite))
            {
                list.Add(suite);
            }
        }

        suites = list;
        return list.Count > 0;
    }
}
