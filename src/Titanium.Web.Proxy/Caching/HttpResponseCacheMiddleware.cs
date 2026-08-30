using System;
using System.Collections.Generic;
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
        if (context.Session is not SessionEventArgs session)
        {
            await next(context, cancellationToken).ConfigureAwait(false);
            return;
        }

        var request = session.HttpClient.Request;
        var method = request.Method ?? "GET";
        var isCacheableMethod =
            method.Equals("GET", StringComparison.OrdinalIgnoreCase) ||
            method.Equals("HEAD", StringComparison.OrdinalIgnoreCase);

        if (isCacheableMethod)
        {
            var key = BuildCacheKey(session);
            if (_cache.TryGet(key, out var cached) && cached is not null)
            {
                var headers = new List<HttpHeader>(cached.Headers.Count + 1);
                foreach (var h in cached.Headers)
                {
                    headers.Add(new HttpHeader(h.Key, h.Value));
                }

                headers.Add(new HttpHeader("X-Cache", "HIT"));
                session.GenericResponse(cached.Body, (System.Net.HttpStatusCode)cached.StatusCode, headers);
                context.IsHandled = true;
                return;
            }
        }

        await next(context, cancellationToken).ConfigureAwait(false);

        if (!isCacheableMethod || context.IsHandled)
        {
            return;
        }

        // Cache synthetic / already-buffered 200 responses produced during BeforeRequest.
        // Upstream responses are cached when body is available (e.g. after GetResponseBody).
        TryCacheCurrentResponse(session);
    }

    /// <summary>
    /// Call from <see cref="ProxyServer.AfterResponse"/> when caching upstream 200 bodies.
    /// </summary>
    public void TryCacheCurrentResponse(SessionEventArgs session)
    {
        ArgumentNullException.ThrowIfNull(session);

        var method = session.HttpClient.Request.Method ?? "";
        if (!method.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
            !method.Equals("HEAD", StringComparison.OrdinalIgnoreCase))
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

        var headers = new List<KeyValuePair<string, string>>();
        foreach (var header in response.Headers)
        {
            headers.Add(new KeyValuePair<string, string>(header.Name, header.Value));
        }

        _cache.Set(
            BuildCacheKey(session),
            new CachedHttpResponse
            {
                StatusCode = 200,
                Body = body,
                Headers = headers,
                ExpiresUtc = DateTimeOffset.UtcNow + _defaultTtl,
            },
            _defaultTtl);
    }

    private static string BuildCacheKey(SessionEventArgs session)
    {
        var request = session.HttpClient.Request;
        var host = request.Host ?? request.RequestUri?.Host ?? "";
        var path = request.RequestUri?.PathAndQuery;
        if (string.IsNullOrEmpty(path))
        {
            path = request.RequestUriString8.GetString();
            if (string.IsNullOrEmpty(path))
            {
                path = "/";
            }
        }

        return $"{request.Method}:{host}{path}";
    }
}
