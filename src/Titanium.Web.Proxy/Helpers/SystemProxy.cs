using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Microsoft.Win32;
using Titanium.Web.Proxy.Models;

// Helper classes for setting system proxy settings
namespace Titanium.Web.Proxy.Helpers
{
    internal class HttpSystemProxyValue
    {
        internal string HostName { get; }

        internal int Port { get; }

        internal ProxyProtocolType ProtocolType { get; }

        public HttpSystemProxyValue(string hostName, int port, ProxyProtocolType protocolType)
        {
            HostName = hostName;
            Port = port;
            ProtocolType = protocolType;
        }

        public override string ToString()
        {
            string protocol;
            switch (ProtocolType)
            {
                case ProxyProtocolType.Http:
                    protocol = ProxyServer.UriSchemeHttp;
                    break;
                case ProxyProtocolType.Https:
                    protocol = ProxyServer.UriSchemeHttps;
                    break;
                default:
                    throw new Exception("Unsupported protocol type");
            }

            return $"{protocol}={HostName}:{Port}";
        }
    }

    /// <summary>
    ///     Manage system proxy settings
    /// </summary>
    [SuppressMessage("StyleCop.CSharp.MaintainabilityRules", "SA1402:FileMayOnlyContainASingleType", Justification = "Reviewed.")]
    internal class SystemProxyManager
    {
        private const string RegKeyInternetSettings = "Software\\Microsoft\\Windows\\CurrentVersion\\Internet Settings";
        private const string RegAutoConfigUrl = "AutoConfigURL";
        private const string RegProxyEnable = "ProxyEnable";
        private const string RegProxyServer = "ProxyServer";
        private const string RegProxyOverride = "ProxyOverride";

        internal const int InternetOptionSettingsChanged = 39;
        internal const int InternetOptionRefresh = 37;

        private ProxyInfo? originalValues;

        public SystemProxyManager()
        {
            AppDomain.CurrentDomain.ProcessExit += (o, args) => RestoreOriginalSettings();
            if (Environment.UserInteractive && NativeMethods.GetConsoleWindow() != IntPtr.Zero)
            {
                var handler = new NativeMethods.ConsoleEventDelegate(eventType =>
                {
                    if (eventType != 2)
                    {
                        return false;
                    }

                    RestoreOriginalSettings();
                    return false;
                });
                NativeMethods.Handler = handler;

                // On Console exit make sure we also exit the proxy
                NativeMethods.SetConsoleCtrlHandler(handler, true);
            }
        }

        /// <summary>
        ///     Set the HTTP and/or HTTPS proxy server for current machine
        /// </summary>
        /// <param name="hostname"></param>
        /// <param name="port"></param>
        /// <param name="protocolType"></param>
        internal void SetProxy(string hostname, int port, ProxyProtocolType protocolType)
        {
            using (var reg = OpenInternetSettingsKey())
            {
                if (reg == null)
                {
                    return;
                }

                SaveOriginalProxyConfiguration(reg);
                PrepareRegistry(reg);

                string? existingContent = reg.GetValue(RegProxyServer) as string;
                var existingSystemProxyValues = ProxyInfo.GetSystemProxyValues(existingContent);
                existingSystemProxyValues.RemoveAll(x => (protocolType & x.ProtocolType) != 0);
                if ((protocolType & ProxyProtocolType.Http) != 0)
                {
                    existingSystemProxyValues.Add(new HttpSystemProxyValue(hostname, port, ProxyProtocolType.Http));
                }

                if ((protocolType & ProxyProtocolType.Https) != 0)
                {
                    existingSystemProxyValues.Add(new HttpSystemProxyValue(hostname, port, ProxyProtocolType.Https));
                }

                reg.DeleteValue(RegAutoConfigUrl, false);
                reg.SetValue(RegProxyEnable, 1);
                reg.SetValue(RegProxyServer,
                    string.Join(";", existingSystemProxyValues.Select(x => x.ToString()).ToArray()));

                Refresh();
            }
        }

        /// <summary>
        ///     Remove the HTTP and/or HTTPS proxy setting from current machine
        /// </summary>
        internal void RemoveProxy(ProxyProtocolType protocolType, bool saveOriginalConfig = true)
        {
            using (var reg = OpenInternetSettingsKey())
            {
                if (reg == null)
                {
                    return;
                }

                if (saveOriginalConfig)
                {
                    SaveOriginalProxyConfiguration(reg);
                }

                if (reg.GetValue(RegProxyServer) != null)
                {
                    string? existingContent = reg.GetValue(RegProxyServer) as string;

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
        internal void DisableAllProxy()
        {
            using (var reg = OpenInternetSettingsKey())
            {
                if (reg == null)
                {
                    return;
                }

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
                if (reg == null)
                {
                    return;
                }

                SaveOriginalProxyConfiguration(reg);
                reg.SetValue(RegAutoConfigUrl, url);
                Refresh();
            }
        }

        internal void SetProxyOverride(string proxyOverride)
        {
            using (var reg = OpenInternetSettingsKey())
            {
                if (reg == null)
                {
                    return;
                }

                SaveOriginalProxyConfiguration(reg);
                reg.SetValue(RegProxyOverride, proxyOverride);
                Refresh();
            }
        }

        internal void RestoreOriginalSettings()
        {
            if (originalValues == null)
            {
                return;
            }

            using (var reg = Registry.CurrentUser.OpenSubKey(RegKeyInternetSettings, true))
            {
                if (reg == null)
                {
                    return;
                }

                var ov = originalValues;
                if (ov.AutoConfigUrl != null)
                {
                    reg.SetValue(RegAutoConfigUrl, ov.AutoConfigUrl);
                }
                else
                {
                    reg.DeleteValue(RegAutoConfigUrl, false);
                }

                if (ov.ProxyEnable.HasValue)
                {
                    reg.SetValue(RegProxyEnable, ov.ProxyEnable.Value);
                }
                else
                {
                    reg.DeleteValue(RegProxyEnable, false);
                }

                if (ov.ProxyServer != null)
                {
                    reg.SetValue(RegProxyServer, ov.ProxyServer);
                }
                else
                {
                    reg.DeleteValue(RegProxyServer, false);
                }

                if (ov.ProxyOverride != null)
                {
                    reg.SetValue(RegProxyOverride, ov.ProxyOverride);
                }
                else
                {
                    reg.DeleteValue(RegProxyOverride, false);
                }

                // This should not be needed, but sometimes the values are not stored into the registry
                // at system shutdown without flushing.
                reg.Flush();

                originalValues = null;

                const int smShuttingdown = 0x2000;
                Version windows7Version = new Version(6, 1);
                if (Environment.OSVersion.Version > windows7Version ||
                    NativeMethods.GetSystemMetrics(smShuttingdown) == 0)
                {
                    // Do not call refresh() in Windows 7 or earlier at system shutdown.
                    // SetInternetOption in the refresh method re-enables ProxyEnable registry value
                    // in Windows 7 or earlier at system shutdown.
                    Refresh();
                }
            }
        }

        internal ProxyInfo? GetProxyInfoFromRegistry()
        {
            using (var reg = OpenInternetSettingsKey())
            {
                if (reg == null)
                {
                    return null;
                }

                return GetProxyInfoFromRegistry(reg);
            }
        }

        private ProxyInfo GetProxyInfoFromRegistry(RegistryKey reg)
        {
            var pi = new ProxyInfo(null,
                reg.GetValue(RegAutoConfigUrl) as string,
                reg.GetValue(RegProxyEnable) as int?,
                reg.GetValue(RegProxyServer) as string,
                reg.GetValue(RegProxyOverride) as string);

            return pi;
        }

        private void SaveOriginalProxyConfiguration(RegistryKey reg)
        {
            if (originalValues != null)
            {
                return;
            }

            originalValues = GetProxyInfoFromRegistry(reg);
        }

        /// <summary>
        ///     Prepares the proxy server registry (create empty values if they don't exist)
        /// </summary>
        /// <param name="reg"></param>
        private static void PrepareRegistry(RegistryKey reg)
        {
            if (reg.GetValue(RegProxyEnable) == null)
            {
                reg.SetValue(RegProxyEnable, 0);
            }

            if (reg.GetValue(RegProxyServer) == null || reg.GetValue(RegProxyEnable) as int? == 0)
            {
                reg.SetValue(RegProxyServer, string.Empty);
            }
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
}
