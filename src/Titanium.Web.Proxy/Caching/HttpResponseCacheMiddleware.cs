using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Abstractions.Middleware;
using Titanium.Web.Proxy.Abstractions.Plugins;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Http;
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

    public HttpResponseCacheMiddleware(IHttpResponseCache cache, TimeSpan? defaultTtl = null)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _defaultTtl = defaultTtl is { } t && t > TimeSpan.Zero ? t : TimeSpan.FromMinutes(1);
    }

    public async ValueTask InvokeAsync(
        ProxyMiddlewareContext context,
        ProxyMiddlewareDelegate next,
        CancellationToken cancellationToken)
    {
        if (!TryResolveCacheable(context, out var method, out var host, out var path))
        {
            await next(context, cancellationToken).ConfigureAwait(false);
            return;
        }

        var key = BuildCacheKey(method, host, path);
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
    /// </summary>
    internal void TryStore(
        string method,
        string host,
        string path,
        int statusCode,
        IEnumerable<HttpHeader> headers,
        ReadOnlySpan<byte> body)
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

        var copy = new byte[body.Length];
        body.CopyTo(copy);
        _cache.Set(
            BuildCacheKey(method, host, path),
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

        if (!TryRequestParts(session, out var method, out var host, out var path))
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

        TryStore(method, host, path, 200, response.Headers, body);
    }

    internal static string BuildCacheKey(string method, string host, string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            path = "/";
        }

        return $"{method}:{host}{path}";
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
        out string path)
    {
        if (context.Session is SessionEventArgs session)
        {
            return TryRequestParts(session, out method, out host, out path);
        }

        if (context.Request is { } view)
        {
            method = view.Method ?? "GET";
            host = view.Host ?? "";
            path = string.IsNullOrEmpty(view.Path) ? "/" : view.Path;
            return IsCacheableMethod(method);
        }

        method = "";
        host = "";
        path = "";
        return false;
    }

    private static bool TryRequestParts(SessionEventArgs session, out string method, out string host, out string path)
    {
        var request = session.HttpClient.Request;
        method = request.Method ?? "";
        if (!IsCacheableMethod(method))
        {
            host = "";
            path = "";
            return false;
        }

        host = request.Host ?? request.RequestUri?.Host ?? "";
        path = request.RequestUri?.PathAndQuery ?? "";
        if (string.IsNullOrEmpty(path))
        {
            path = request.RequestUriString8.GetString();
            if (string.IsNullOrEmpty(path))
            {
                path = "/";
            }
        }

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
        };

        var staged = new List<KeyValuePair<string, string>>();
        foreach (var header in headers)
        {
            if (header.Name.Equals("Connection", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var part in header.Value
                    .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    .Where(part => !part.Equals("close", StringComparison.OrdinalIgnoreCase) &&
                                   !part.Equals("keep-alive", StringComparison.OrdinalIgnoreCase)))
                {
                    hopByHop.Add(part);
                }

                continue;
            }

            staged.Add(new KeyValuePair<string, string>(header.Name, header.Value));
        }

        var kept = new List<KeyValuePair<string, string>>(staged.Count);
        foreach (var header in staged.Where(header => !hopByHop.Contains(header.Key)))
        {
            kept.Add(header);
        }

        return kept;
    }
}
