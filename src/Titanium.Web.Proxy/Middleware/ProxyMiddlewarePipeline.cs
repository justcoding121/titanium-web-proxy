using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Abstractions.Middleware;

namespace Titanium.Web.Proxy.Middleware;

/// <summary>
/// Builds a middleware chain once per config reload. Empty list → invoke terminus with zero allocation.
/// </summary>
public static class ProxyMiddlewarePipeline
{
    public static ProxyMiddlewareDelegate Build(IReadOnlyList<IProxyMiddleware>? middleware, ProxyMiddlewareDelegate terminus)
    {
        if (middleware is null || middleware.Count == 0)
        {
            return terminus;
        }

        ProxyMiddlewareDelegate pipeline = terminus;
        for (var i = middleware.Count - 1; i >= 0; i--)
        {
            var current = middleware[i];
            var next = pipeline;
            pipeline = (ctx, ct) => current.InvokeAsync(ctx, next, ct);
        }

        return pipeline;
    }

    public static ValueTask InvokeEmptyAsync(ProxyMiddlewareContext context, ProxyMiddlewareDelegate terminus, CancellationToken cancellationToken)
        => terminus(context, cancellationToken);
}
