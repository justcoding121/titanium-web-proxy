using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32;

// Helper classes for setting system proxy settings
namespace Titanium.Web.Proxy.Helpers
{
    [Flags]
    public enum ProxyProtocolType
    {
        /// <summary>
        /// The none
        /// </summary>
        None = 0,

        /// <summary>
        /// HTTP
        /// </summary>
        Http = 1,

        /// <summary>
        /// HTTPS
        /// </summary>
        Https = 2,

        /// <summary>
        /// Both HTTP and HTTPS
        /// </summary>
        AllHttp = Http | Https,
    }

    internal partial class NativeMethods
    {
        [DllImport("wininet.dll")]
        internal static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer,
            int dwBufferLength);

        [DllImport("kernel32.dll")]
        internal static extern IntPtr GetConsoleWindow();

        // Keeps it from getting garbage collected
        internal static ConsoleEventDelegate Handler;

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool SetConsoleCtrlHandler(ConsoleEventDelegate callback, bool add);

        // Pinvoke
        internal delegate bool ConsoleEventDelegate(int eventType);
    }

    internal class HttpSystemProxyValue
    {
        internal string HostName { get; set; }

        internal int Port { get; set; }

        internal ProxyProtocolType ProtocolType { get; set; }

        public override string ToString()
        {
            string protocol;
            switch (ProtocolType)
            {
                case ProxyProtocolType.Http:
                    protocol = "http";
                    break;
                case ProxyProtocolType.Https:
                    protocol = "https";
                    break;
                default:
                    throw new Exception("Unsupported protocol type");
            }

            return $"{protocol}={HostName}:{Port}";
        }
    }

    /// <summary>
    /// Manage system proxy settings
    /// </summary>
    internal class SystemProxyManager
    {
        private const string regKeyInternetSettings = "Software\\Microsoft\\Windows\\CurrentVersion\\Internet Settings";
        private const string regAutoConfigUrl = "AutoConfigURL";
        private const string regProxyEnable = "ProxyEnable";
        private const string regProxyServer = "ProxyServer";
        private const string regProxyOverride = "ProxyOverride";

        internal const int InternetOptionSettingsChanged = 39;
        internal const int InternetOptionRefresh = 37;

        private ProxyInfo originalValues;

        public SystemProxyManager()
        {
            AppDomain.CurrentDomain.ProcessExit += (o, args) => RestoreOriginalSettings();
            if (Environment.UserInteractive && NativeMethods.GetConsoleWindow() != IntPtr.Zero)
            {
                NativeMethods.Handler = eventType =>
                {
                    if (eventType != 2)
                    {
                        return false;
                    }

                    RestoreOriginalSettings();
                    return false;
                };

                //On Console exit make sure we also exit the proxy
                NativeMethods.SetConsoleCtrlHandler(NativeMethods.Handler, true);
            }
        }

        /// <summary>
        /// Set the HTTP and/or HTTPS proxy server for current machine
        /// </summary>
        /// <param name="hostname"></param>
        /// <param name="port"></param>
        /// <param name="protocolType"></param>
        internal void SetProxy(string hostname, int port, ProxyProtocolType protocolType)
        {
            var reg = Registry.CurrentUser.OpenSubKey(regKeyInternetSettings, true);

            if (reg != null)
            {
                SaveOriginalProxyConfiguration(reg);
                PrepareRegistry(reg);

                var exisitingContent = reg.GetValue(regProxyServer) as string;
                var existingSystemProxyValues = ProxyInfo.GetSystemProxyValues(exisitingContent);
                existingSystemProxyValues.RemoveAll(x => (protocolType & x.ProtocolType) != 0);
                if ((protocolType & ProxyProtocolType.Http) != 0)
                {
                    existingSystemProxyValues.Add(new HttpSystemProxyValue
                    {
                        HostName = hostname,
                        ProtocolType = ProxyProtocolType.Http,
                        Port = port
                    });
                }

                if ((protocolType & ProxyProtocolType.Https) != 0)
                {
                    existingSystemProxyValues.Add(new HttpSystemProxyValue
                    {
                        HostName = hostname,
                        ProtocolType = ProxyProtocolType.Https,
                        Port = port
                    });
                }

                reg.DeleteValue(regAutoConfigUrl, false);
                reg.SetValue(regProxyEnable, 1);
                reg.SetValue(regProxyServer, string.Join(";", existingSystemProxyValues.Select(x => x.ToString()).ToArray()));

                Refresh();
            }
        }

        /// <summary>
        /// Remove the HTTP and/or HTTPS proxy setting from current machine
        /// </summary>
        internal void RemoveProxy(ProxyProtocolType protocolType, bool saveOriginalConfig = true)
        {
            var reg = Registry.CurrentUser.OpenSubKey(regKeyInternetSettings, true);
            if (reg != null)
            {
                if (saveOriginalConfig)
                {
                    SaveOriginalProxyConfiguration(reg);
                }

                if (reg.GetValue(regProxyServer) != null)
                {
                    var exisitingContent = reg.GetValue(regProxyServer) as string;

                    var existingSystemProxyValues = ProxyInfo.GetSystemProxyValues(exisitingContent);
                    existingSystemProxyValues.RemoveAll(x => (protocolType & x.ProtocolType) != 0);

                    if (existingSystemProxyValues.Count != 0)
                    {
                        reg.SetValue(regProxyEnable, 1);
                        reg.SetValue(regProxyServer, string.Join(";", existingSystemProxyValues.Select(x => x.ToString()).ToArray()));
                    }
                    else
                    {
                        reg.SetValue(regProxyEnable, 0);
                        reg.SetValue(regProxyServer, string.Empty);
                    }
                }

                Refresh();
            }
        }

        /// <summary>
        /// Removes all types of proxy settings (both http and https)
        /// </summary>
        internal void DisableAllProxy()
        {
            var reg = Registry.CurrentUser.OpenSubKey(regKeyInternetSettings, true);

            if (reg != null)
            {
                SaveOriginalProxyConfiguration(reg);

                reg.SetValue(regProxyEnable, 0);
                reg.SetValue(regProxyServer, string.Empty);

                Refresh();
            }
        }

        internal void SetAutoProxyUrl(string url)
        {
            var reg = Registry.CurrentUser.OpenSubKey(regKeyInternetSettings, true);

            if (reg != null)
            {
                SaveOriginalProxyConfiguration(reg);
                reg.SetValue(regAutoConfigUrl, url);
                Refresh();
            }
        }

        internal void SetProxyOverride(string proxyOverride)
        {
            var reg = Registry.CurrentUser.OpenSubKey(regKeyInternetSettings, true);

            if (reg != null)
            {
                SaveOriginalProxyConfiguration(reg);
                reg.SetValue(regProxyOverride, proxyOverride);
                Refresh();
            }
        }

        internal void RestoreOriginalSettings()
        {
            if (originalValues == null)
            {
                return;
            }

            var reg = Registry.CurrentUser.OpenSubKey(regKeyInternetSettings, true);

            if (reg != null)
            {
                var ov = originalValues;
                if (ov.AutoConfigUrl != null)
                {
                    reg.SetValue(regAutoConfigUrl, ov.AutoConfigUrl);
                }
                else
                {
                    reg.DeleteValue(regAutoConfigUrl, false);
                }

                if (ov.ProxyEnable.HasValue)
                {
                    reg.SetValue(regProxyEnable, ov.ProxyEnable.Value);
                }
                else
                {
                    reg.DeleteValue(regProxyEnable, false);
                }

                if (ov.ProxyServer != null)
                {
                    reg.SetValue(regProxyServer, ov.ProxyServer);
                }
                else
                {
                    reg.DeleteValue(regProxyServer, false);
                }

                if (ov.ProxyOverride != null)
                {
                    reg.SetValue(regProxyOverride, ov.ProxyOverride);
                }
                else
                {
                    reg.DeleteValue(regProxyOverride, false);
                }

                originalValues = null;
                Refresh();
            }
        }

        internal ProxyInfo GetProxyInfoFromRegistry()
        {
            var reg = Registry.CurrentUser.OpenSubKey(regKeyInternetSettings, true);

            if (reg != null)
            {
                return GetProxyInfoFromRegistry(reg);
            }

            return null;
        }

        private ProxyInfo GetProxyInfoFromRegistry(RegistryKey reg)
        {
            var pi = new ProxyInfo(
                null,
                reg.GetValue(regAutoConfigUrl) as string,
                reg.GetValue(regProxyEnable) as int?,
                reg.GetValue(regProxyServer) as string,
                reg.GetValue(regProxyOverride) as string);

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
        /// Prepares the proxy server registry (create empty values if they don't exist) 
        /// </summary>
        /// <param name="reg"></param>
        private static void PrepareRegistry(RegistryKey reg)
        {
            if (reg.GetValue(regProxyEnable) == null)
            {
                reg.SetValue(regProxyEnable, 0);
            }

            if (reg.GetValue(regProxyServer) == null || reg.GetValue(regProxyEnable) as int? == 0)
            {
                reg.SetValue(regProxyServer, string.Empty);
            }
        }

        /// <summary>
        /// Refresh the settings so that the system know about a change in proxy setting
        /// </summary>
        private void Refresh()
        {
            NativeMethods.InternetSetOption(IntPtr.Zero, InternetOptionSettingsChanged, IntPtr.Zero, 0);
            NativeMethods.InternetSetOption(IntPtr.Zero, InternetOptionRefresh, IntPtr.Zero, 0);
        }
    }
}
