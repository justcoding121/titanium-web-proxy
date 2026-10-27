namespace Titanium.Web.Proxy.Abstractions.Configuration;

/// <summary>Rewrites a snapshot before it is published. Throw to keep the previous snapshot.</summary>
public interface IProxyConfigFilter
{
    ValueTask<ProxyConfigSnapshot> ApplyAsync(ProxyConfigSnapshot snapshot, CancellationToken cancellationToken);
}
