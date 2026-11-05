using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Plus.Caching;
using Titanium.Plus.Security;
using Titanium.Web.Proxy.Abstractions.Middleware;
using Titanium.Web.Proxy.Abstractions.Plugins;

namespace Titanium.Plus.Tests;

/// <summary>
/// Cache middleware is inserted at the list length when Plus applies, which is before access control.
/// A hit therefore stays ahead of later Plus middleware, matching the old RunCommand-then-Activate order.
/// </summary>
[TestClass]
public class ResponseCacheRegistrationTests
{
    [TestMethod]
    public void Disabled_CreatesTheCacheForPurge_AndAddsNoMiddleware()
    {
        var middleware = new List<IProxyMiddleware>();
        var context = new PlusActivationContext
        {
            ProxyServer = new object(),
            Middleware = middleware,
            Options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        };

        var cache = ResponseCacheConfiguration.Install(context);

        Assert.AreSame(cache, context.ResponseCache);
        Assert.AreEqual(0, middleware.Count);
        Assert.IsInstanceOfType<MemoryHttpResponseCache>(context.ResponseCache);
    }

    [TestMethod]
    public void Enabled_InsertsAfterPrePlusMiddleware_AndBeforeAccessControl()
    {
        var middleware = new List<IProxyMiddleware> { new MarkerMiddleware() };
        var context = new PlusActivationContext
        {
            ProxyServer = new object(),
            Middleware = middleware,
            Options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["cache.enable"] = "true",
                ["security.allowCidrs"] = "127.0.0.1/32",
            },
        };

        ResponseCacheConfiguration.Install(context);
        AccessSecurity.TryStart(context, context.Options!);

        Assert.AreEqual(3, middleware.Count);
        Assert.IsInstanceOfType<MarkerMiddleware>(middleware[0]);
        Assert.IsInstanceOfType<HttpResponseCacheMiddleware>(middleware[1]);
        Assert.IsInstanceOfType<CidrAccessMiddleware>(middleware[2]);
    }

    private sealed class MarkerMiddleware : IProxyMiddleware
    {
        public ValueTask InvokeAsync(
            ProxyMiddlewareContext context,
            ProxyMiddlewareDelegate next,
            CancellationToken cancellationToken) =>
            next(context, cancellationToken);
    }
}
