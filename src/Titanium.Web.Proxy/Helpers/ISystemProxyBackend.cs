using System;
using System.Collections.Generic;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.Helpers;

/// <summary>
///     Platform-specific system HTTP(S) proxy configuration (WinINET, macOS networksetup, Linux desktop/env).
/// </summary>
internal interface ISystemProxyBackend : IDisposable
{
    void SetProxy(string hostname, int port, ProxyProtocolType protocolType, string? proxyOverride);

    void RemoveProxy(ProxyProtocolType protocolType, bool saveOriginalConfig = true);

    void DisableAllProxy();

    void RestoreOriginalSettings();

    /// <summary>Current proxy bypass list in WinINET-style semicolon form, or null if unknown.</summary>
    string? GetCurrentProxyOverride();

    /// <summary>
    ///     Protocols whose current system proxy points at a local host and a port in
    ///     <paramref name="ownedPorts"/> (used to clear stale settings after a crash).
    /// </summary>
    ProxyProtocolType GetStaleLocalProxyProtocols(IReadOnlyCollection<int> ownedPorts);
}
