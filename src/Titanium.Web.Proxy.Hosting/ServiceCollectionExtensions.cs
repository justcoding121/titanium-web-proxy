using Microsoft.Extensions.DependencyInjection;
using Titanium.Web.Proxy.Abstractions;

namespace Titanium.Web.Proxy.Hosting;

/// <summary>Registers <see cref="ProxyServer"/> for application hosts.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds a single <see cref="ProxyServer"/>. The caller starts and stops it.
    /// </summary>
    public static IServiceCollection AddTitaniumReverseProxy(this IServiceCollection services, Action<ProxyServer>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton(sp =>
        {
            _ = sp;
            var proxy = new ProxyServer(userTrustRootCertificate: false);
            configure?.Invoke(proxy);
            return proxy;
        });
        return services;
    }
}
