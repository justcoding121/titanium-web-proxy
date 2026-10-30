using System.Linq;

namespace Titanium.Plus.Security.Oidc;

/// <summary>
/// Decides where a browser may be sent after sign-in or sign-out. A target is either a path on this host or an
/// absolute URL whose origin is on the allow list. Anything else is dropped, never repaired.
/// </summary>
internal static class OidcRedirect
{
    private const int MaxTargetLength = 2048;

    /// <summary>Returns <paramref name="candidate"/> unchanged when it is a safe target, otherwise null.</summary>
    public static string? Resolve(string? candidate, IReadOnlyCollection<string> allowedOrigins)
    {
        if (string.IsNullOrEmpty(candidate) || candidate.Length > MaxTargetLength || HasUnsafeCharacter(candidate))
        {
            return null;
        }

        if (candidate[0] == '/')
        {
            // "//host" and "/\host" are network-path references: the browser leaves this site.
            return candidate.Length > 1 && candidate[1] is '/' or '\\' ? null : candidate;
        }

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            !string.IsNullOrEmpty(uri.UserInfo) || string.IsNullOrEmpty(uri.Host))
        {
            return null;
        }

        var origin = OriginOf(uri);
        return allowedOrigins.Any(allowed => string.Equals(allowed, origin, StringComparison.OrdinalIgnoreCase))
            ? candidate
            : null;
    }

    /// <summary>The scheme, host, and non-default port of an absolute http(s) URL. Null for anything else.</summary>
    public static string? OriginOf(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) && !string.IsNullOrEmpty(uri.Host)
            ? OriginOf(uri)
            : null;

    private static string OriginOf(Uri uri) =>
        uri.IsDefaultPort
            ? $"{uri.Scheme}://{uri.IdnHost}"
            : $"{uri.Scheme}://{uri.IdnHost}:{uri.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

    /// <summary>
    /// The path and query of a request target as a same-site path. An absolute-form target loses its authority and
    /// a run of leading slashes collapses, so the result can never name another host. Null when nothing safe is left.
    /// </summary>
    public static string? SamePathAndQuery(string? target)
    {
        if (string.IsNullOrEmpty(target))
        {
            return null;
        }

        if ((target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
             target.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) &&
            Uri.TryCreate(target, UriKind.Absolute, out var absolute))
        {
            target = absolute.PathAndQuery;
        }

        var start = 0;
        while (start + 1 < target.Length && target[start] == '/' && target[start + 1] == '/')
        {
            start++;
        }

        var trimmed = start == 0 ? target : target[start..];
        return trimmed.Length > 0 && trimmed[0] == '/' && Resolve(trimmed, []) is not null ? trimmed : null;
    }

    private static bool HasUnsafeCharacter(string value) =>
        value.Any(c => c <= ' ' || c == '\u007f' || c == '\\');
}
