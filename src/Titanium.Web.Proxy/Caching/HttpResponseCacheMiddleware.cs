using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Abstractions.Middleware;
using Titanium.Web.Proxy.Abstractions.Plugins;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.Caching;

/// <summary>
/// GET/HEAD response cache middleware. Only allocated when placed in the middleware list;
/// otherwise the hot path pays nothing.
/// </summary>
public sealed class HttpResponseCacheMiddleware : IProxyMiddleware
{
    /// <summary>Largest body stored from the H1 terminate-lite coalesce buffer.</summary>
    internal const int MaxCachedBodyBytes = 64 * 1024;

    private readonly IHttpResponseCache _cache;
    private readonly TimeSpan _defaultTtl;
    private readonly bool _allowAuthenticated;

    public HttpResponseCacheMiddleware(IHttpResponseCache cache, TimeSpan? defaultTtl = null, bool allowAuthenticated = false)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _defaultTtl = defaultTtl is { } t && t > TimeSpan.Zero ? t : TimeSpan.FromMinutes(1);
        _allowAuthenticated = allowAuthenticated;
    }

    public async ValueTask InvokeAsync(
        ProxyMiddlewareContext context,
        ProxyMiddlewareDelegate next,
        CancellationToken cancellationToken)
    {
        if (!TryResolveCacheable(context, out var method, out var host, out var path, out var acceptEncoding,
                out var authorization, out var cookie))
        {
            await next(context, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!IsRequestStoreable(authorization, cookie))
        {
            await next(context, cancellationToken).ConfigureAwait(false);
            return;
        }

        var key = BuildCacheKey(method, host, path, acceptEncoding);
        if (_cache.TryGet(key, out var cached) && cached is not null)
        {
            ServeHit(context, cached);
            return;
        }

        await next(context, cancellationToken).ConfigureAwait(false);

        if (context.Session is SessionEventArgs session && !context.IsHandled)
        {
            TryCacheCurrentResponse(session);
        }
    }

    /// <summary>
    /// Store a 200 whose body is already in memory (lite coalesce or a buffered session response).
    /// Hop-by-hop headers and bodies larger than <see cref="MaxCachedBodyBytes"/> are skipped.
    /// Responses with <c>Set-Cookie</c>, <c>Cache-Control: private|no-store|no-cache</c>, or
    /// <c>Vary: *</c>, and requests carrying <c>Authorization</c> or <c>Cookie</c> (unless
    /// <c>allowAuthenticated</c>), are never stored.
    /// </summary>
    internal void TryStore(
        string method,
        string host,
        string path,
        int statusCode,
        IEnumerable<HttpHeader> headers,
        ReadOnlySpan<byte> body,
        string? acceptEncoding = null,
        string? authorization = null,
        string? cookie = null)
    {
        if (statusCode != 200 || body.Length > MaxCachedBodyBytes)
        {
            return;
        }

        if (!method.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
            !method.Equals("HEAD", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!IsRequestStoreable(authorization, cookie) || !IsResponseStoreable(headers))
        {
            return;
        }

        var copy = new byte[body.Length];
        body.CopyTo(copy);
        _cache.Set(
            BuildCacheKey(method, host, path, acceptEncoding),
            new CachedHttpResponse
            {
                StatusCode = 200,
                Body = copy,
                Headers = CopyCacheableHeaders(headers),
                ExpiresUtc = DateTimeOffset.UtcNow + _defaultTtl,
            },
            _defaultTtl);
    }

    /// <summary>
    /// Call from the session response path when a small 200 body is already buffered.
    /// </summary>
    public void TryCacheCurrentResponse(SessionEventArgs session)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (!TryRequestParts(session, out var method, out var host, out var path, out var acceptEncoding,
                out var authorization, out var cookie))
        {
            return;
        }

        var response = session.HttpClient.Response;
        if (response.StatusCode != 200 || !response.IsBodyRead)
        {
            return;
        }

        byte[] body;
        try
        {
            body = response.Body;
        }
        catch
        {
            return;
        }

        TryStore(method, host, path, 200, response.Headers, body, acceptEncoding, authorization, cookie);
    }

    internal static string BuildCacheKey(string method, string host, string path, string? acceptEncoding = null)
    {
        if (string.IsNullOrEmpty(path))
        {
            path = "/";
        }

        // Include Accept-Encoding until full Vary support lands (WI-19b). Query is part of path
        // (PathAndQuery) so personalized query strings never collide with each other.
        return string.Concat(method, ":", host, path, "|ae=", acceptEncoding ?? string.Empty);
    }

    internal bool IsRequestStoreable(string? authorization, string? cookie)
    {
        if (_allowAuthenticated)
        {
            return true;
        }

        return string.IsNullOrEmpty(authorization) && string.IsNullOrEmpty(cookie);
    }

    internal static bool IsResponseStoreable(IEnumerable<HttpHeader> headers)
    {
        foreach (var header in headers)
        {
            if (header.Name.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (header.Name.Equals("Vary", StringComparison.OrdinalIgnoreCase) &&
                header.Value.Equals("*", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (header.Name.Equals("Cache-Control", StringComparison.OrdinalIgnoreCase) &&
                CacheControlForbidsSharedStore(header.Value))
            {
                return false;
            }
        }

        return true;
    }

    private static bool CacheControlForbidsSharedStore(string value)
    {
        foreach (var part in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.Equals("private", StringComparison.OrdinalIgnoreCase) ||
                part.Equals("no-store", StringComparison.OrdinalIgnoreCase) ||
                part.Equals("no-cache", StringComparison.OrdinalIgnoreCase) ||
                part.StartsWith("no-cache=", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static void ServeHit(ProxyMiddlewareContext context, CachedHttpResponse cached)
    {
        var headers = new List<HttpHeader>(cached.Headers.Count + 1);
        foreach (var h in cached.Headers)
        {
            headers.Add(new HttpHeader(h.Key, h.Value));
        }

        headers.Add(new HttpHeader("X-Cache", "HIT"));

        if (context.Session is SessionEventArgs session)
        {
            session.GenericResponse(cached.Body, (HttpStatusCode)cached.StatusCode, headers);
        }

        context.HandledStatusCode = cached.StatusCode;
        context.HandledBodyBytes = cached.Body;
        context.HandledHeaders = new List<KeyValuePair<string, string>>(headers.Count);
        foreach (var h in headers)
        {
            context.HandledHeaders.Add(new KeyValuePair<string, string>(h.Name, h.Value));
        }

        context.IsHandled = true;
    }

    private static bool TryResolveCacheable(
        ProxyMiddlewareContext context,
        out string method,
        out string host,
        out string path,
        out string? acceptEncoding,
        out string? authorization,
        out string? cookie)
    {
        if (context.Session is SessionEventArgs session)
        {
            return TryRequestParts(session, out method, out host, out path, out acceptEncoding, out authorization,
                out cookie);
        }

        if (context.Request is { } view)
        {
            method = view.Method ?? "GET";
            host = view.Host ?? "";
            path = string.IsNullOrEmpty(view.Path) ? "/" : view.Path;
            acceptEncoding = FirstHeader(view, "Accept-Encoding");
            authorization = view.Authorization ?? FirstHeader(view, "Authorization");
            cookie = FirstHeader(view, "Cookie");
            return IsCacheableMethod(method);
        }

        method = "";
        host = "";
        path = "";
        acceptEncoding = null;
        authorization = null;
        cookie = null;
        return false;
    }

    private static string? FirstHeader(MiddlewareRequestView view, string name)
    {
        var values = view.GetHeaderValues?.Invoke(name);
        return values is { Count: > 0 } ? values[0] : null;
    }

    private static bool TryRequestParts(
        SessionEventArgs session,
        out string method,
        out string host,
        out string path,
        out string? acceptEncoding,
        out string? authorization,
        out string? cookie)
    {
        var request = session.HttpClient.Request;
        method = request.Method ?? "";
        acceptEncoding = null;
        authorization = null;
        cookie = null;
        if (!IsCacheableMethod(method))
        {
            host = "";
            path = "";
            return false;
        }

        host = request.Host ?? request.RequestUri?.Host ?? "";
        // PathAndQuery keeps the query string in the cache key so /a?user=1 never aliases /a?user=2.
        path = request.RequestUri?.PathAndQuery ?? "";
        if (string.IsNullOrEmpty(path))
        {
            path = request.RequestUriString8.GetString();
            if (string.IsNullOrEmpty(path))
            {
                path = "/";
            }
        }

        acceptEncoding = request.Headers.GetFirstHeader("Accept-Encoding")?.Value;
        authorization = request.Headers.GetFirstHeader("Authorization")?.Value;
        cookie = request.Headers.GetFirstHeader("Cookie")?.Value;
        return true;
    }

    private static bool IsCacheableMethod(string method) =>
        method.Equals("GET", StringComparison.OrdinalIgnoreCase) ||
        method.Equals("HEAD", StringComparison.OrdinalIgnoreCase);

    private static List<KeyValuePair<string, string>> CopyCacheableHeaders(IEnumerable<HttpHeader> headers)
    {
        var hopByHop = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Connection",
            "Keep-Alive",
            "Proxy-Connection",
            "Transfer-Encoding",
            "TE",
            "Trailer",
            "Upgrade",
            "Set-Cookie",
        };

        var staged = new List<KeyValuePair<string, string>>();
        foreach (var header in headers)
        {
            if (header.Name.Equals("Connection", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var part in header.Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)) // NOSONAR S3267 -- Where would allocate an enumerator on every cache store.
                {
                    if (!part.Equals("close", StringComparison.OrdinalIgnoreCase) &&
                        !part.Equals("keep-alive", StringComparison.OrdinalIgnoreCase))
                    {
                        hopByHop.Add(part);
                    }
                }

                continue;
            }

            staged.Add(new KeyValuePair<string, string>(header.Name, header.Value));
        }

        var kept = new List<KeyValuePair<string, string>>(staged.Count);
        foreach (var header in staged) // NOSONAR S3267 -- A capturing Where allocates a closure on every cache store.
        {
            if (!hopByHop.Contains(header.Key))
            {
                kept.Add(header);
            }
        }

        return kept;
    }
}
