namespace Titanium.Web.Proxy.Abstractions.Configuration;

/// <summary>Checks a snapshot before it is published. A non-empty result rejects the reload.</summary>
public interface IProxyConfigValidator
{
    IReadOnlyList<string> Validate(ProxyConfigSnapshot snapshot);
}
