using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Titanium.Web.Proxy.Diagnostics;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Helpers.WinHttp;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.Logging;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network;
using Titanium.Web.Proxy.Network.Quic;
using Titanium.Web.Proxy.Network.Tcp;
using Titanium.Web.Proxy.Network.WinAuth;
using Titanium.Web.Proxy.Abstractions;
using Titanium.Web.Proxy.Options;
using Titanium.Web.Proxy.StreamExtended.BufferPool;

namespace Titanium.Web.Proxy;

/// <inheritdoc />
/// <summary>
///     This class is the backbone of proxy. One can create as many instances as needed.
///     However care should be taken to avoid using the same listening ports across multiple instances.
/// </summary>
public partial class ProxyServer : IDisposable
{
    /// <summary>
    ///     Set the given explicit end point as the default proxy server for current machine.
    /// </summary>
    /// <param name="endPoint">The explicit endpoint.</param>
    public void SetAsSystemHttpProxy(ExplicitProxyEndPoint endPoint)
    {
        SetAsSystemProxy(endPoint, ProxyProtocolType.Http);
    }

    /// <summary>
    ///     Set the given explicit end point as the default HTTP proxy server for current machine.
    /// </summary>
    /// <param name="endPoint">The explicit endpoint.</param>
    /// <param name="settings">The Windows system proxy settings.</param>
    public void SetAsSystemHttpProxy(ExplicitProxyEndPoint endPoint, SystemProxySettings settings)
    {
        SetAsSystemProxy(endPoint, ProxyProtocolType.Http, settings);
    }

    /// <summary>
    ///     Set the given explicit end point as the default proxy server for current machine.
    /// </summary>
    /// <param name="endPoint">The explicit endpoint.</param>
    public void SetAsSystemHttpsProxy(ExplicitProxyEndPoint endPoint)
    {
        SetAsSystemProxy(endPoint, ProxyProtocolType.Https);
    }

    /// <summary>
    ///     Set the given explicit end point as the default HTTPS proxy server for current machine.
    /// </summary>
    /// <param name="endPoint">The explicit endpoint.</param>
    /// <param name="settings">The Windows system proxy settings.</param>
    public void SetAsSystemHttpsProxy(ExplicitProxyEndPoint endPoint, SystemProxySettings settings)
    {
        SetAsSystemProxy(endPoint, ProxyProtocolType.Https, settings);
    }

    /// <summary>
    ///     Set the given explicit end point as the default proxy server for current machine.
    /// </summary>
    /// <param name="endPoint">The explicit endpoint.</param>
    /// <param name="protocolType">The proxy protocol type.</param>
    public void SetAsSystemProxy(ExplicitProxyEndPoint endPoint, ProxyProtocolType protocolType)
    {
        SetAsSystemProxy(endPoint, protocolType, null);
    }

    /// <summary>
    ///     Set the given explicit end point as the default proxy server for current machine.
    /// </summary>
    /// <param name="endPoint">The explicit endpoint.</param>
    /// <param name="protocolType">The proxy protocol type.</param>
    /// <param name="settings">
    ///     The Windows system proxy settings, or <see langword="null"/> to preserve the current bypass list.
    /// </param>
    public void SetAsSystemProxy(ExplicitProxyEndPoint endPoint, ProxyProtocolType protocolType,
        SystemProxySettings? settings)
    {
        var result = TrySetAsSystemProxy(endPoint, protocolType, settings);
        if (!result.Succeeded)
            logger.LogWarning("SetAsSystemProxy failed: {Message}", result.Message);
    }

    /// <summary>
    ///     Enable OS system proxy without throwing. Failures are logged and returned so Inspector/CLI
    ///     can show a status message instead of crashing.
    /// </summary>
    public SystemProxyChangeResult TrySetAsSystemProxy(ExplicitProxyEndPoint endPoint, ProxyProtocolType protocolType,
        SystemProxySettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(endPoint);

        if (SystemProxySettingsManager == null)
            return SystemProxyChangeResult.Fail(SystemProxyNotSupportedMessage);

        try
        {
            ValidateEndPointAsSystemProxy(endPoint);
            settings?.Validate();

            var isHttp = (protocolType & ProxyProtocolType.Http) > 0;
            var isHttps = (protocolType & ProxyProtocolType.Https) > 0;

            if (isHttps)
            {
                CertificateManager.EnsureRootCertificate();

                if (!CertificateManager.CertValidated)
                {
                    protocolType &= ~ProxyProtocolType.Https;
                    isHttps = false;
                }
            }

            if (isHttp)
                ProxyEndPoints.OfType<ExplicitProxyEndPoint>().ToList().ForEach(x => x.IsSystemHttpProxy = false);

            if (isHttps)
                ProxyEndPoints.OfType<ExplicitProxyEndPoint>().ToList().ForEach(x => x.IsSystemHttpsProxy = false);

            string? proxyOverride = null;
            if (settings != null)
            {
                var currentProxyOverride = SystemProxySettingsManager.GetCurrentProxyOverride();
                proxyOverride = settings.BuildProxyOverride(currentProxyOverride);
            }

            SystemProxySettingsManager.SetProxy(
                FormatSystemProxyHostname(endPoint.IpAddress),
                endPoint.Port,
                protocolType,
                proxyOverride);

            if (isHttp) endPoint.IsSystemHttpProxy = true;
            if (isHttps) endPoint.IsSystemHttpsProxy = true;

            string? proxyType = null;
            switch (protocolType)
            {
                case ProxyProtocolType.Http:
                    proxyType = "HTTP";
                    break;
                case ProxyProtocolType.Https:
                    proxyType = "HTTPS";
                    break;
                case ProxyProtocolType.AllHttp:
                    proxyType = "HTTP and HTTPS";
                    break;
            }

            var message = protocolType == ProxyProtocolType.None
                ? "System proxy request completed with no HTTP(S) protocols enabled"
                : $"Set endpoint at Ip {endPoint.IpAddress} and port: {endPoint.Port} as System {proxyType} Proxy";
            if (protocolType != ProxyProtocolType.None)
                ProxyDiagnostics.ReportInformation(logger, message);

            return SystemProxyChangeResult.Ok(message);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "System proxy enable failed");
            return SystemProxyChangeResult.Fail(ex.Message);
        }
    }

    /// <summary>
    ///     Clear HTTP proxy settings of current machine.
    /// </summary>
    public void DisableSystemHttpProxy()
    {
        DisableSystemProxy(ProxyProtocolType.Http);
    }

    /// <summary>
    ///     Clear HTTPS proxy settings of current machine.
    /// </summary>
    public void DisableSystemHttpsProxy()
    {
        DisableSystemProxy(ProxyProtocolType.Https);
    }

    /// <summary>
    ///     Restores the original proxy settings.
    /// </summary>
    public void RestoreOriginalProxySettings()
    {
        var result = TryRestoreOriginalProxySettings();
        if (!result.Succeeded)
            logger.LogWarning("RestoreOriginalProxySettings failed: {Message}", result.Message);
    }

    /// <summary>Restore OS proxy without throwing.</summary>
    public SystemProxyChangeResult TryRestoreOriginalProxySettings()
    {
        if (SystemProxySettingsManager == null)
            return SystemProxyChangeResult.Fail(SystemProxyNotSupportedMessage);

        try
        {
            SystemProxySettingsManager.RestoreOriginalSettings();
            ClearEndpointSystemProxyFlags(ProxyProtocolType.AllHttp);
            return SystemProxyChangeResult.Ok("System proxy restored");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "System proxy restore failed");
            return SystemProxyChangeResult.Fail(ex.Message);
        }
    }

    /// <summary>
    ///     Clear the specified proxy setting for current machine.
    /// </summary>
    public void DisableSystemProxy(ProxyProtocolType protocolType)
    {
        var result = TryDisableSystemProxy(protocolType);
        if (!result.Succeeded)
            logger.LogWarning("DisableSystemProxy failed: {Message}", result.Message);
    }

    /// <summary>Clear OS proxy for the given protocols without throwing.</summary>
    public SystemProxyChangeResult TryDisableSystemProxy(ProxyProtocolType protocolType)
    {
        if (SystemProxySettingsManager == null)
            return SystemProxyChangeResult.Fail(SystemProxyNotSupportedMessage);

        try
        {
            SystemProxySettingsManager.RemoveProxy(protocolType);
            ClearEndpointSystemProxyFlags(protocolType);
            return SystemProxyChangeResult.Ok("System proxy disabled");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "System proxy disable failed");
            return SystemProxyChangeResult.Fail(ex.Message);
        }
    }

    /// <summary>
    ///     Clear all proxy settings for current machine.
    /// </summary>
    public void DisableAllSystemProxies()
    {
        var result = TryDisableAllSystemProxies();
        if (!result.Succeeded)
            logger.LogWarning("DisableAllSystemProxies failed: {Message}", result.Message);
    }

    /// <summary>Clear all OS proxy settings without throwing.</summary>
    public SystemProxyChangeResult TryDisableAllSystemProxies()
    {
        if (SystemProxySettingsManager == null)
            return SystemProxyChangeResult.Fail(SystemProxyNotSupportedMessage);

        try
        {
            SystemProxySettingsManager.DisableAllProxy();
            ClearEndpointSystemProxyFlags(ProxyProtocolType.AllHttp);
            return SystemProxyChangeResult.Ok("All system proxies disabled");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Disable all system proxies failed");
            return SystemProxyChangeResult.Fail(ex.Message);
        }
    }

    /// <summary>
    ///     Clears <see cref="ExplicitProxyEndPoint.IsSystemHttpProxy" />/
    ///     <see cref="ExplicitProxyEndPoint.IsSystemHttpsProxy" /> on every endpoint for the protocol(s)
    ///     named in <paramref name="protocolType" />, so those flags never outlive the registry setting
    ///     they were tracking.
    /// </summary>
    private void ClearEndpointSystemProxyFlags(ProxyProtocolType protocolType)
    {
        var clearHttp = protocolType.HasFlag(ProxyProtocolType.Http);
        var clearHttps = protocolType.HasFlag(ProxyProtocolType.Https);
        if (!clearHttp && !clearHttps) return;

        foreach (var endPoint in ProxyEndPoints.OfType<ExplicitProxyEndPoint>())
        {
            if (clearHttp) endPoint.IsSystemHttpProxy = false;
            if (clearHttps) endPoint.IsSystemHttpsProxy = false;
        }
    }
}
