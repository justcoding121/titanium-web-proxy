using Titanium.Web.Proxy;
using Titanium.Web.Proxy.Models;

namespace Titanium.Inspector.Services;

/// <summary>Seam for system-proxy writes so tests can assert without mutating the machine.</summary>
public interface ISystemProxyController
{
    SystemProxyChangeResult SetAsSystemProxy(ProxyServer proxy, ExplicitProxyEndPoint endPoint, InspectorSettings settings);
    SystemProxyChangeResult RestoreOriginalProxySettings(ProxyServer proxy);
}

/// <summary>Production controller that configures the OS system proxy via <see cref="ProxyServer"/>.</summary>
public sealed class ProxyServerSystemProxyController : ISystemProxyController
{
    public SystemProxyChangeResult SetAsSystemProxy(ProxyServer proxy, ExplicitProxyEndPoint endPoint, InspectorSettings settings)
    {
        var proxySettings = MitmBypass.CreateSystemProxySettings(settings);
        return proxy.TrySetAsSystemProxy(endPoint, ProxyProtocolType.AllHttp, proxySettings);
    }

    public SystemProxyChangeResult RestoreOriginalProxySettings(ProxyServer proxy) =>
        proxy.TryRestoreOriginalProxySettings();
}

/// <summary>Backward-compatible alias for <see cref="ProxyServerSystemProxyController"/>.</summary>
public sealed class WinInetSystemProxyController : ISystemProxyController
{
    private readonly ProxyServerSystemProxyController _inner = new();

    public SystemProxyChangeResult SetAsSystemProxy(ProxyServer proxy, ExplicitProxyEndPoint endPoint, InspectorSettings settings) =>
        _inner.SetAsSystemProxy(proxy, endPoint, settings);

    public SystemProxyChangeResult RestoreOriginalProxySettings(ProxyServer proxy) =>
        _inner.RestoreOriginalProxySettings(proxy);
}

/// <summary>Test double that records calls without touching OS proxy settings.</summary>
public sealed class RecordingSystemProxyController : ISystemProxyController
{
    public int SetCount { get; private set; }
    public int RestoreCount { get; private set; }
    public bool LastEnabled { get; private set; }
    public InspectorSettings? LastSettings { get; private set; }

    public bool FailSet { get; set; }
    public bool FailRestore { get; set; }
    public bool ThrowOnSet { get; set; }
    public bool ThrowOnRestore { get; set; }

    public SystemProxyChangeResult SetAsSystemProxy(ProxyServer proxy, ExplicitProxyEndPoint endPoint, InspectorSettings settings)
    {
        SetCount++;
        LastEnabled = true;
        LastSettings = settings;
        if (ThrowOnSet)
            throw new InvalidOperationException("recorded set throw");
        if (FailSet)
            return SystemProxyChangeResult.Fail("recorded set failure");
        return SystemProxyChangeResult.Ok("System proxy recorded");
    }

    public SystemProxyChangeResult RestoreOriginalProxySettings(ProxyServer proxy)
    {
        RestoreCount++;
        LastEnabled = false;
        if (ThrowOnRestore)
            throw new InvalidOperationException("recorded restore throw");
        if (FailRestore)
            return SystemProxyChangeResult.Fail("recorded restore failure");
        return SystemProxyChangeResult.Ok("System proxy restore recorded");
    }
}
