using System;
using System.Buffers;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.Routing;

/// <summary>Host and path used for route matching. Never throws.</summary>
internal readonly struct RouteTarget
{
    public RouteTarget(string? host, string path)
    {
        Host = host;
        Path = path;
    }

    public string? Host { get; }
    public string Path { get; }

    public static RouteTarget Get(Request request, string? fallbackHost)
    {
        var raw = request.RequestUriString8.GetString();
        if (raw.Length > 0 && raw[0] == '/')
        {
            return new RouteTarget(HostOnly(request.Host) ?? fallbackHost, NormalizeMatchPath(SlicePath(raw)));
        }

        if (UriExtensions.GetScheme(request.RequestUriString8).Length > 0)
        {
            return ParseAbsolute(raw, fallbackHost);
        }

        return new RouteTarget(HostOnly(request.Host) ?? fallbackHost, "/");
    }

    internal static string NormalizeMatchPath(string path)
    {
        if (path.Length == 0)
        {
            return "/";
        }

        if (!NeedsNormalize(path))
        {
            return path[0] == '/' ? path : "/" + path;
        }

        var rented = ArrayPool<char>.Shared.Rent(path.Length);
        try
        {
            var decoded = DecodeUnreserved(path, rented);
            return RemoveDotSegments(decoded);
        }
        finally
        {
            ArrayPool<char>.Shared.Return(rented);
        }
    }

    private static RouteTarget ParseAbsolute(string raw, string? fallbackHost)
    {
        var scheme = raw.IndexOf("://", StringComparison.Ordinal);
        if (scheme < 0)
        {
            return new RouteTarget(HostOnly(requestHost: null) ?? fallbackHost, "/");
        }

        var authStart = scheme + 3;
        var authEnd = authStart;
        while (authEnd < raw.Length && raw[authEnd] != '/' && raw[authEnd] != '?' && raw[authEnd] != '#')
        {
            authEnd++;
        }

        var authority = raw[authStart..authEnd];
        var at = authority.LastIndexOf('@');
        if (at >= 0)
        {
            authority = authority[(at + 1)..];
        }

        var path = authEnd < raw.Length && raw[authEnd] == '/' ? SlicePath(raw[authEnd..]) : "/";
        var host = HostOnly(authority);
        if (string.IsNullOrEmpty(host))
        {
            host = fallbackHost;
        }

        return new RouteTarget(host, NormalizeMatchPath(path));
    }

    private static string SlicePath(string target)
    {
        var end = target.Length;
        for (var i = 0; i < target.Length; i++)
        {
            if (target[i] is '?' or '#')
            {
                end = i;
                break;
            }
        }

        return end == 0 ? "/" : target[..end];
    }

    internal static string? HostOnly(string? requestHost)
    {
        if (string.IsNullOrEmpty(requestHost))
        {
            return requestHost;
        }

        if (requestHost[0] == '[')
        {
            var end = requestHost.IndexOf(']');
            if (end > 1)
            {
                return requestHost[1..end];
            }

            return requestHost;
        }

        var colon = requestHost.LastIndexOf(':');
        if (colon > 0 && int.TryParse(requestHost.AsSpan(colon + 1), out _))
        {
            return requestHost[..colon];
        }

        return requestHost;
    }

    private static bool NeedsNormalize(string path)
    {
        if (path.Contains('%', StringComparison.Ordinal))
        {
            return true;
        }

        for (var i = 0; i < path.Length - 1; i++)
        {
            if (path[i] == '/' && (path[i + 1] == '/' || path[i + 1] == '.'))
            {
                return true;
            }
        }

        return false;
    }

    private static ReadOnlySpan<char> DecodeUnreserved(string path, char[] buffer)
    {
        var n = 0;
        for (var i = 0; i < path.Length; i++)
        {
            var ch = path[i];
            if (ch == '%' && i + 2 < path.Length && TryHex(path[i + 1], out var hi) && TryHex(path[i + 2], out var lo))
            {
                var decoded = (char)((hi << 4) | lo);
                if (IsUnreserved(decoded))
                {
                    buffer[n++] = decoded;
                    i += 2;
                    continue;
                }
            }

            buffer[n++] = ch;
        }

        return buffer.AsSpan(0, n);
    }

    private static string RemoveDotSegments(ReadOnlySpan<char> path)
    {
        var rented = ArrayPool<char>.Shared.Rent(path.Length + 1);
        try
        {
            var w = 0;
            rented[w++] = '/';
            var i = 0;
            if (path.Length > 0 && path[0] == '/')
            {
                i = 1;
            }

            while (i <= path.Length)
            {
                var start = i;
                while (i < path.Length && path[i] != '/')
                {
                    i++;
                }

                var segment = path[start..i];
                var atEnd = i >= path.Length;
                if (segment.Length == 0 || segment.SequenceEqual("."))
                {
                    // skip
                }
                else if (segment.SequenceEqual(".."))
                {
                    if (w > 1)
                    {
                        w--;
                        while (w > 1 && rented[w - 1] != '/')
                        {
                            w--;
                        }
                    }
                }
                else
                {
                    if (w > 1)
                    {
                        rented[w++] = '/';
                    }

                    segment.CopyTo(rented.AsSpan(w));
                    w += segment.Length;
                }

                if (atEnd)
                {
                    break;
                }

                i++;
            }

            if (w == 0)
            {
                return "/";
            }

            return new string(rented, 0, w);
        }
        finally
        {
            ArrayPool<char>.Shared.Return(rented);
        }
    }

    private static bool IsUnreserved(char c) =>
        c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '.' or '_' or '~';

    private static bool TryHex(char c, out int value)
    {
        if (c is >= '0' and <= '9')
        {
            value = c - '0';
            return true;
        }

        if (c is >= 'a' and <= 'f')
        {
            value = c - 'a' + 10;
            return true;
        }

        if (c is >= 'A' and <= 'F')
        {
            value = c - 'A' + 10;
            return true;
        }

        value = 0;
        return false;
    }
}
