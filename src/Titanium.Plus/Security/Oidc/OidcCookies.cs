using System.Linq;
using System.Text;
using Titanium.Web.Proxy.Http;

namespace Titanium.Plus.Security.Oidc;

/// <summary>Reads and removes the policy's cookies on the client request, and builds the ones it sets.</summary>
internal static class OidcCookies
{
    private const string CookieHeader = "Cookie";

    /// <summary>Every value of the cookie called <paramref name="name"/>, in the order the client sent them.</summary>
    public static List<string> Values(Request request, string name)
    {
        var found = new List<string>(1);
        var lines = request.Headers.GetHeaders(CookieHeader);
        if (lines is null)
        {
            return found;
        }

        foreach (var line in lines)
        {
            found.AddRange(
                Pairs(line.Value)
                    .Where(pair => pair.Name.Equals(name, StringComparison.Ordinal))
                    .Select(pair => pair.Value.ToString()));
        }

        return found;
    }

    /// <summary>
    /// Removes the session cookie and the login-flow cookies from the request, so the origin never receives them.
    /// Every other cookie is kept as the client wrote it.
    /// </summary>
    public static void RemoveOwn(Request request, string sessionName, string flowPrefix)
    {
        var lines = request.Headers.GetHeaders(CookieHeader);
        if (lines is null || lines.Count == 0)
        {
            return;
        }

        var kept = new List<string>();
        var removed = false;
        foreach (var line in lines)
        {
            foreach (var pair in Pairs(line.Value))
            {
                if (pair.Name.Equals(sessionName, StringComparison.Ordinal) ||
                    pair.Name.StartsWith(flowPrefix, StringComparison.Ordinal))
                {
                    removed = true;
                    continue;
                }

                kept.Add(pair.Raw.ToString());
            }
        }

        if (!removed)
        {
            return;
        }

        request.Headers.RemoveHeader(CookieHeader);
        if (kept.Count > 0)
        {
            request.Headers.AddHeader(CookieHeader, string.Join("; ", kept));
        }
    }

    /// <summary>A <c>Set-Cookie</c> value: host-only, HttpOnly, Secure, SameSite=Lax.</summary>
    public static string Set(string name, string value, TimeSpan lifetime) =>
        $"{name}={value}; Path=/; Max-Age={((long)Math.Max(1, lifetime.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture)}; HttpOnly; Secure; SameSite=Lax";

    /// <summary>A <c>Set-Cookie</c> value that makes the browser drop the cookie.</summary>
    public static string Clear(string name) =>
        $"{name}=; Path=/; Max-Age=0; Expires=Thu, 01 Jan 1970 00:00:00 GMT; HttpOnly; Secure; SameSite=Lax";

    /// <summary>True when the pair would fit in a browser's cookie jar.</summary>
    public static bool Fits(string name, string value) =>
        Encoding.UTF8.GetByteCount(name) + 1 + Encoding.UTF8.GetByteCount(value) <= OidcSettings.MaxCookieBytes;

    private static IEnumerable<(string Name, ReadOnlyMemory<char> Value, ReadOnlyMemory<char> Raw)> Pairs(string header)
    {
        var memory = header.AsMemory();
        var start = 0;
        while (start < memory.Length)
        {
            var end = memory.Span[start..].IndexOf(';');
            var length = end < 0 ? memory.Length - start : end;
            var segment = memory.Slice(start, length).Trim();
            start += length + 1;
            if (segment.IsEmpty)
            {
                continue;
            }

            var eq = segment.Span.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            yield return (segment[..eq].Trim().ToString(), segment[(eq + 1)..].Trim(), segment);
        }
    }
}
