using System.Net;

namespace Titanium.Web.Proxy.Abstractions.Routing;

/// <summary>
/// Maps a client IP address to a two-letter ISO 3166-1 country code (upper-case).
/// Implementations return <see langword="false"/> when the country is unknown.
/// </summary>
public interface IGeoIpResolver
{
    /// <summary>Resolves <paramref name="address"/> to a country ISO code when known.</summary>
    bool TryGetCountry(IPAddress address, out string countryIsoCode);
}
