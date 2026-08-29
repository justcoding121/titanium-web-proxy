using Titanium.Web.Proxy;
using Titanium.Web.Proxy.Models;

namespace Titanium.Inspector.Services;

/// <summary>Seam for system-proxy writes so tests can assert without mutating the machine.</summary>
public interface ISystemProxyController
{
    void SetAsSystemProxy(ProxyServer proxy, ExplicitProxyEndPoint endPoint);
    void RestoreOriginalProxySettings(ProxyServer proxy);
}

/// <summary>Production controller that configures the OS system proxy via <see cref="ProxyServer"/>.</summary>
public sealed class ProxyServerSystemProxyController : ISystemProxyController
{
    public void SetAsSystemProxy(ProxyServer proxy, ExplicitProxyEndPoint endPoint)
    {
        var settings = MitmBypass.CreateSystemProxySettings();
        proxy.SetAsSystemProxy(endPoint, ProxyProtocolType.AllHttp, settings);
    }

    public void RestoreOriginalProxySettings(ProxyServer proxy) =>
        proxy.RestoreOriginalProxySettings();
}

/// <summary>Backward-compatible alias for <see cref="ProxyServerSystemProxyController"/>.</summary>
public sealed class WinInetSystemProxyController : ISystemProxyController
{
    private readonly ProxyServerSystemProxyController _inner = new();

    public void SetAsSystemProxy(ProxyServer proxy, ExplicitProxyEndPoint endPoint) =>
        _inner.SetAsSystemProxy(proxy, endPoint);

    public void RestoreOriginalProxySettings(ProxyServer proxy) =>
        _inner.RestoreOriginalProxySettings(proxy);
}

/// <summary>Test double that records calls without touching OS proxy settings.</summary>
public sealed class RecordingSystemProxyController : ISystemProxyController
{
    public int SetCount { get; private set; }
    public int RestoreCount { get; private set; }
    public bool LastEnabled { get; private set; }

    public void SetAsSystemProxy(ProxyServer proxy, ExplicitProxyEndPoint endPoint)
    {
        SetCount++;
        LastEnabled = true;
    }

    public void RestoreOriginalProxySettings(ProxyServer proxy)
    {
        RestoreCount++;
        LastEnabled = false;
    }
}