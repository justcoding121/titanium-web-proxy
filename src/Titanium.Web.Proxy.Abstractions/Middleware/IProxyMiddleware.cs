namespace Titanium.Web.Proxy.Abstractions.Middleware;

/// <summary>Middleware next delegate.</summary>
public delegate ValueTask ProxyMiddlewareDelegate(ProxyMiddlewareContext context, CancellationToken cancellationToken);

/// <summary>Context for <see cref="IProxyMiddleware"/> (runs inside BeforeRequest).</summary>
public sealed class ProxyMiddlewareContext
{
    public required object Session { get; init; }
    public bool IsHandled { get; set; }
    public Dictionary<string, object?> Items { get; } = new();
}

/// <summary>
/// Optional middleware. An empty middleware list must allocate nothing on the hot path.
/// Chain terminus is the existing BeforeRequest handler list.
/// </summary>
public interface IProxyMiddleware
{
    ValueTask InvokeAsync(ProxyMiddlewareContext context, ProxyMiddlewareDelegate next, CancellationToken cancellationToken);
}
