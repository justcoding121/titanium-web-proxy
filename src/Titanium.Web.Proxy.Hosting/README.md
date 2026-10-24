# Titanium.Web.Proxy.Hosting

Registers a `ProxyServer` with `IServiceCollection` for applications that host the proxy themselves.

```csharp
services.AddTitaniumReverseProxy(proxy =>
{
    proxy.ReverseProxy = new ReverseProxyOptions
    {
        Routes = routes,
        ClusterManager = clusters,
    };
});
```

The registration does not listen until the application calls `Start`.
