using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Titanium.Web.Proxy.Routing;

/// <summary>
/// Origin lists: exact values, <c>*</c>, one-label wildcard subdomains, and <c>regex:</c> entries.
/// Compiled once per distinct list string. The request origin is never a cache key.
/// </summary>
internal static class CorsOriginMatcher
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly ConcurrentDictionary<string, CompiledList> Cache = new(StringComparer.Ordinal);

    internal static int CacheCount => Cache.Count;

    internal static void ResetCacheForTests() => Cache.Clear();

    /// <summary>
    /// <c>*</c> or a missing list allows every origin. A list echoes the request origin on a match.
    /// An unlisted origin gets nothing.
    /// </summary>
    internal static string? Match(string? configured, string origin)
    {
        if (string.IsNullOrWhiteSpace(configured) || string.Equals(configured.Trim(), "*", StringComparison.Ordinal))
        {
            return "*";
        }

        if (string.IsNullOrEmpty(origin))
        {
            return null;
        }

        return Cache.GetOrAdd(configured, static value => CompiledList.Parse(value)).MatchOrigin(origin);
    }

    /// <summary>True when <paramref name="entry"/> is <c>https://*.example.com</c> with an optional port.</summary>
    internal static bool IsWildcard(string entry) => TryParseWildcard(entry, out _, out _, out _);

    private static bool TryParseWildcard(string entry, out string scheme, out string suffix, out int? port)
    {
        scheme = "";
        suffix = "";
        port = null;
        var schemeEnd = entry.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd <= 0)
        {
            return false;
        }

        scheme = entry[..schemeEnd];
        if (scheme is not ("http" or "https"))
        {
            return false;
        }

        var rest = entry[(schemeEnd + 3)..];
        if (!rest.StartsWith("*.", StringComparison.Ordinal) || rest.Contains('/') || rest.Contains('?') || rest.Contains('#'))
        {
            return false;
        }

        var host = rest[2..];
        var colon = host.LastIndexOf(':');
        if (colon >= 0)
        {
            if (!int.TryParse(host[(colon + 1)..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsed) || parsed is < 1 or > 65535)
            {
                return false;
            }

            port = parsed;
            host = host[..colon];
        }

        if (host.StartsWith("*.", StringComparison.Ordinal) || host.Contains('*') || !HasTwoLabels(host))
        {
            return false;
        }

        suffix = host;
        return true;
    }

    private static bool HasTwoLabels(string host)
    {
        var dot = host.IndexOf('.');
        return dot > 0 && dot < host.Length - 1 && !host.EndsWith('.');
    }

    private sealed class CompiledList
    {
        private readonly string[] exact;
        private readonly Wildcard[] wildcards;
        private readonly Regex[] regexes;

        private CompiledList(string[] exact, Wildcard[] wildcards, Regex[] regexes)
        {
            this.exact = exact;
            this.wildcards = wildcards;
            this.regexes = regexes;
        }

        internal static CompiledList Parse(string configured)
        {
            var parts = configured.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var exact = new List<string>();
            var wildcards = new List<Wildcard>();
            var regexes = new List<Regex>();
            foreach (var part in parts)
            {
                if (part.StartsWith("regex:", StringComparison.Ordinal))
                {
                    if (Compile(part["regex:".Length..]) is { } regex)
                    {
                        regexes.Add(regex);
                    }

                    continue;
                }

                if (TryParseWildcard(part, out var scheme, out var suffix, out var port))
                {
                    wildcards.Add(new Wildcard(scheme, suffix, port));
                    continue;
                }

                exact.Add(part);
            }

            return new CompiledList(exact.ToArray(), wildcards.ToArray(), regexes.ToArray());
        }

        internal string? MatchOrigin(string origin)
        {
            if (MatchesExact(origin))
            {
                return origin;
            }

            if (IsClosed(origin))
            {
                return null;
            }

            if (MatchesWildcard(origin))
            {
                return origin;
            }

            return FirstRegexMatch(origin);
        }

        private bool MatchesExact(string origin)
        {
            for (var i = 0; i < exact.Length; i++)
            {
                if (string.Equals(exact[i], origin, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private bool MatchesWildcard(string origin)
        {
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.HostNameType is not UriHostNameType.Dns)
            {
                return false;
            }

            var explicitPort = OriginHasExplicitPort(origin);
            for (var i = 0; i < wildcards.Length; i++)
            {
                if (wildcards[i].Matches(uri, explicitPort))
                {
                    return true;
                }
            }

            return false;
        }

        private string? FirstRegexMatch(string origin)
        {
            for (var i = 0; i < regexes.Length; i++)
            {
                try
                {
                    if (regexes[i].IsMatch(origin))
                    {
                        return origin;
                    }
                }
                catch (RegexMatchTimeoutException)
                {
                    return null;
                }
            }

            return null;
        }

        private static bool IsClosed(string origin) =>
            origin.Length > 256 || string.Equals(origin, "null", StringComparison.Ordinal) || HasControl(origin);

        private static bool HasControl(string origin) => origin.Any(char.IsControl);

        private static bool OriginHasExplicitPort(string origin)
        {
            var scheme = origin.IndexOf("://", StringComparison.Ordinal);
            if (scheme < 0)
            {
                return false;
            }

            var start = scheme + 3;
            var slash = origin.IndexOf('/', start);
            var authority = slash < 0 ? origin[start..] : origin[start..slash];
            return authority.Contains(':');
        }

        private static Regex? Compile(string pattern)
        {
            try
            {
                _ = new Regex(pattern, RegexOptions.NonBacktracking | RegexOptions.CultureInvariant, RegexTimeout);
                return new Regex(
                    "^(?:" + pattern + ")$",
                    RegexOptions.NonBacktracking | RegexOptions.CultureInvariant,
                    RegexTimeout);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                return null;
            }
        }
    }

    private readonly record struct Wildcard(string Scheme, string Suffix, int? Port)
    {
        internal bool Matches(Uri uri, bool explicitPort)
        {
            if (!string.Equals(uri.Scheme, Scheme, StringComparison.Ordinal))
            {
                return false;
            }

            if (Port is int required)
            {
                if (!explicitPort || uri.Port != required)
                {
                    return false;
                }
            }
            else if (explicitPort)
            {
                return false;
            }

            var host = uri.Host;
            if (host.Length <= Suffix.Length + 1 || host[host.Length - Suffix.Length - 1] != '.')
            {
                return false;
            }

            if (!host.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var label = host[..(host.Length - Suffix.Length - 1)];
            return label.Length > 0 && !label.Contains('.') &&
                label.All(static ch => char.IsAsciiLetterOrDigit(ch) || ch == '-');
        }
    }
}
