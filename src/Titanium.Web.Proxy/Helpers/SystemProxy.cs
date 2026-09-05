using System.Collections.Generic;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Titanium.Web.Proxy.Models;

// Helper classes for setting system proxy settings
namespace Titanium.Web.Proxy.Helpers;

internal class HttpSystemProxyValue
{
    private readonly string protocol;

    public HttpSystemProxyValue(string hostName, int port, ProxyProtocolType protocolType)
    {
        HostName = hostName;
        Port = port;
        ProtocolType = protocolType;
        protocol = protocolType switch
        {
            ProxyProtocolType.Http => ProxyServer.UriSchemeHttp,
            ProxyProtocolType.Https => ProxyServer.UriSchemeHttps,
            _ => throw new ArgumentOutOfRangeException(nameof(protocolType), protocolType,
                "Only HTTP and HTTPS proxy values are supported.")
        };
    }

    internal string HostName { get; }

    internal int Port { get; }

    internal ProxyProtocolType ProtocolType { get; }

    public override string ToString()
    {
        return $"{protocol}={HostName}:{Port}";
    }
}

/// <summary>
///     Manage system proxy settings
/// </summary>
[SuppressMessage("StyleCop.CSharp.MaintainabilityRules", "SA1402:FileMayOnlyContainASingleType",
    Justification = "Reviewed.")]
[SupportedOSPlatform("windows")]
internal class SystemProxyManager : ISystemProxyBackend
{
    private const string RegKeyInternetSettings = "Software\\Microsoft\\Windows\\CurrentVersion\\Internet Settings";
    private const string RegAutoConfigUrl = "AutoConfigURL";
    private const string RegProxyEnable = "ProxyEnable";
    private const string RegProxyServer = "ProxyServer";
    private const string RegProxyOverride = "ProxyOverride";

    internal const int InternetOptionSettingsChanged = 39;
    internal const int InternetOptionRefresh = 37;

    private ProxyInfo? originalValues;

    private readonly EventHandler processExitHandler;
    private readonly UnhandledExceptionEventHandler unhandledExceptionHandler;
    private readonly NativeMethods.ConsoleEventDelegate? consoleEventHandler;
    private bool disposed;

    public SystemProxyManager()
    {
        // Best-effort restore when the process is going away. Hard kills (e.g. End Task /
        // taskkill /F) cannot run managed code; Start(changeSystemProxySettings: true) clears
        // stale local-proxy entries on the next run.
        processExitHandler = (_, _) => RestoreOriginalSettings();
        unhandledExceptionHandler = (_, _) => RestoreOriginalSettings();
        AppDomain.CurrentDomain.ProcessExit += processExitHandler;
        AppDomain.CurrentDomain.UnhandledException += unhandledExceptionHandler;

        if (Environment.UserInteractive && NativeMethods.GetConsoleWindow() != IntPtr.Zero)
        {
            consoleEventHandler = eventType =>
            {
                // CTRL_C_EVENT=0, CTRL_BREAK_EVENT=1, CTRL_CLOSE_EVENT=2,
                // CTRL_LOGOFF_EVENT=5, CTRL_SHUTDOWN_EVENT=6
                if (eventType is 0 or 1 or 2 or 5 or 6)
                    RestoreOriginalSettings();

                return false;
            };
            // On console control events, restore system proxy before the process exits.
            NativeMethods.SetConsoleCtrlHandler(consoleEventHandler, true);
        }
    }

    /// <summary>
    ///     Unsubscribes the AppDomain and console-control handlers registered by the constructor.
    ///     Without this, every <see cref="ProxyServer" /> created and disposed over an
    ///     application's lifetime (e.g. in tests, or short-lived proxy instances) leaks a retained
    ///     reference through <see cref="AppDomain.ProcessExit" />/<see cref="AppDomain.UnhandledException" />,
    ///     and a disposed instance's console handler could still fire and call into a torn-down object.
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposed) return;

        if (disposing)
        {
            AppDomain.CurrentDomain.ProcessExit -= processExitHandler;
            AppDomain.CurrentDomain.UnhandledException -= unhandledExceptionHandler;

            if (consoleEventHandler != null)
            {
                NativeMethods.SetConsoleCtrlHandler(consoleEventHandler, false);
            }
        }

        disposed = true;
    }

    /// <summary>
    ///     Set the HTTP and/or HTTPS proxy server for current machine
    /// </summary>
    /// <param name="hostname"></param>
    /// <param name="port"></param>
    /// <param name="protocolType"></param>
    public void SetProxy(string hostname, int port, ProxyProtocolType protocolType)
    {
        SetProxy(hostname, port, protocolType, null);
    }

    /// <summary>
    ///     Set the HTTP and/or HTTPS proxy server for current machine.
    /// </summary>
    /// <param name="hostname"></param>
    /// <param name="port"></param>
    /// <param name="protocolType"></param>
    /// <param name="proxyOverride">
    ///     The proxy bypass list to set, or <see langword="null"/> to preserve the current list.
    /// </param>
    public void SetProxy(string hostname, int port, ProxyProtocolType protocolType, string? proxyOverride)
    {
        using (var reg = OpenInternetSettingsKey())
        {
            if (reg == null) return;

            SaveOriginalProxyConfiguration(reg);
            PrepareRegistry(reg);

            var existingContent = reg.GetValue(RegProxyServer) as string;
            var existingSystemProxyValues = ProxyInfo.GetSystemProxyValues(existingContent);
            existingSystemProxyValues.RemoveAll(x => (protocolType & x.ProtocolType) != 0);
            if ((protocolType & ProxyProtocolType.Http) != 0)
                existingSystemProxyValues.Add(new HttpSystemProxyValue(hostname, port, ProxyProtocolType.Http));

            if ((protocolType & ProxyProtocolType.Https) != 0)
                existingSystemProxyValues.Add(new HttpSystemProxyValue(hostname, port, ProxyProtocolType.Https));

            reg.DeleteValue(RegAutoConfigUrl, false);
            reg.SetValue(RegProxyEnable, 1);
            reg.SetValue(RegProxyServer,
                string.Join(";", existingSystemProxyValues.Select(x => x.ToString()).ToArray()));
            if (proxyOverride != null) reg.SetValue(RegProxyOverride, proxyOverride);

            Refresh();
        }
    }

    /// <summary>
    ///     Remove the HTTP and/or HTTPS proxy setting from current machine
    /// </summary>
    public void RemoveProxy(ProxyProtocolType protocolType, bool saveOriginalConfig = true)
    {
        using (var reg = OpenInternetSettingsKey())
        {
            if (reg == null) return;

            if (saveOriginalConfig) SaveOriginalProxyConfiguration(reg);

            if (reg.GetValue(RegProxyServer) != null)
            {
                var existingContent = reg.GetValue(RegProxyServer) as string;

                var existingSystemProxyValues = ProxyInfo.GetSystemProxyValues(existingContent);
                existingSystemProxyValues.RemoveAll(x => (protocolType & x.ProtocolType) != 0);

                if (existingSystemProxyValues.Count != 0)
                {
                    reg.SetValue(RegProxyEnable, 1);
                    reg.SetValue(RegProxyServer,
                        string.Join(";", existingSystemProxyValues.Select(x => x.ToString()).ToArray()));
                }
                else
                {
                    reg.SetValue(RegProxyEnable, 0);
                    reg.SetValue(RegProxyServer, string.Empty);
                }
            }

            Refresh();
        }
    }

    /// <summary>
    ///     Removes all types of proxy settings (both http and https)
    /// </summary>
    public void DisableAllProxy()
    {
        using (var reg = OpenInternetSettingsKey())
        {
            if (reg == null) return;

            SaveOriginalProxyConfiguration(reg);

            reg.SetValue(RegProxyEnable, 0);
            reg.SetValue(RegProxyServer, string.Empty);

            Refresh();
        }
    }

    internal void SetAutoProxyUrl(string url)
    {
        using (var reg = OpenInternetSettingsKey())
        {
            if (reg == null) return;

            SaveOriginalProxyConfiguration(reg);
            reg.SetValue(RegAutoConfigUrl, url);
            Refresh();
        }
    }

    internal void SetProxyOverride(string proxyOverride)
    {
        using (var reg = OpenInternetSettingsKey())
        {
            if (reg == null) return;

            SaveOriginalProxyConfiguration(reg);
            reg.SetValue(RegProxyOverride, proxyOverride);
            Refresh();
        }
    }


    public string? GetCurrentProxyOverride() => GetProxyInfoFromRegistry()?.ProxyOverride;

    public ProxyProtocolType GetStaleLocalProxyProtocols(IReadOnlyCollection<int> ownedPorts)
    {
        var stale = ProxyProtocolType.None;
        var proxyInfo = GetProxyInfoFromRegistry();
        if (proxyInfo?.Proxies == null) return stale;

        foreach (var proxy in proxyInfo.Proxies.Values)
        {
            if (!NetworkHelper.IsLocalIpAddress(proxy.HostName) || !ownedPorts.Contains(proxy.Port))
                continue;
            stale |= proxy.ProtocolType;
        }

        return stale;
    }
    public void RestoreOriginalSettings()
    {
        var ov = originalValues;
        if (ov == null) return;

        try
        {
        using (var reg = Registry.CurrentUser.OpenSubKey(RegKeyInternetSettings, true))
        {
            if (reg == null) return;

            if (ov.AutoConfigUrl != null)
                reg.SetValue(RegAutoConfigUrl, ov.AutoConfigUrl);
            else
                reg.DeleteValue(RegAutoConfigUrl, false);

            if (ov.ProxyEnable.HasValue)
                reg.SetValue(RegProxyEnable, ov.ProxyEnable.Value);
            else
                reg.DeleteValue(RegProxyEnable, false);

            if (ov.ProxyServer != null)
                reg.SetValue(RegProxyServer, ov.ProxyServer);
            else
                reg.DeleteValue(RegProxyServer, false);

            if (ov.ProxyOverride != null)
                reg.SetValue(RegProxyOverride, ov.ProxyOverride);
            else
                reg.DeleteValue(RegProxyOverride, false);

            // This should not be needed, but sometimes the values are not stored into the registry
            // at system shutdown without flushing.
            reg.Flush();

            originalValues = null;

            const int smShuttingdown = 0x2000;
            var windows7Version = new Version(6, 1);
            if (Environment.OSVersion.Version > windows7Version ||
                NativeMethods.GetSystemMetrics(smShuttingdown) == 0)
                // Do not call refresh() in Windows 7 or earlier at system shutdown.
                // SetInternetOption in the refresh method re-enables ProxyEnable registry value
                // in Windows 7 or earlier at system shutdown.
                Refresh();
        }
        }
        catch
        {
            // process-exit restore must not throw
        }
    }

    internal static ProxyInfo? GetProxyInfoFromRegistry()
    {
        using (var reg = OpenInternetSettingsKey())
        {
            if (reg == null) return null;

            return GetProxyInfoFromRegistry(reg);
        }
    }

    private static ProxyInfo GetProxyInfoFromRegistry(RegistryKey reg)
    {
        var proxyEnableValue = reg.GetValue(RegProxyEnable);
        var pi = new ProxyInfo(null,
            reg.GetValue(RegAutoConfigUrl) as string,
            proxyEnableValue is int proxyEnable ? proxyEnable : null,
            reg.GetValue(RegProxyServer) as string,
            reg.GetValue(RegProxyOverride) as string);

        return pi;
    }

    private void SaveOriginalProxyConfiguration(RegistryKey reg)
    {
        if (originalValues != null) return;

        originalValues = GetProxyInfoFromRegistry(reg);
    }

    /// <summary>
    ///     Prepares the proxy server registry (create empty values if they don't exist)
    /// </summary>
    /// <param name="reg"></param>
    private static void PrepareRegistry(RegistryKey reg)
    {
        if (reg.GetValue(RegProxyEnable) == null) reg.SetValue(RegProxyEnable, 0);

        if (reg.GetValue(RegProxyServer) == null ||
            reg.GetValue(RegProxyEnable) is int proxyEnable && proxyEnable == 0)
            reg.SetValue(RegProxyServer, string.Empty);
    }

    /// <summary>
    ///     Refresh the settings so that the system know about a change in proxy setting
    /// </summary>
    private static void Refresh()
    {
        NativeMethods.InternetSetOption(IntPtr.Zero, InternetOptionSettingsChanged, IntPtr.Zero, 0);
        NativeMethods.InternetSetOption(IntPtr.Zero, InternetOptionRefresh, IntPtr.Zero, 0);
    }

    /// <summary>
    ///     Opens the registry key with the internet settings
    /// </summary>
    private static RegistryKey? OpenInternetSettingsKey()
    {
        return Registry.CurrentUser?.OpenSubKey(RegKeyInternetSettings, true);
    }
}