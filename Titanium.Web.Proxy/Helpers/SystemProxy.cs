using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
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
    }

    internal class HttpSystemProxyValue
    {
        internal string HostName { get; set; }
        internal int Port { get; set; }
        internal ProxyProtocolType ProtocolType { get; set; }

        public override string ToString()
        {
            string protocol = null;
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
        internal const int InternetOptionSettingsChanged = 39;
        internal const int InternetOptionRefresh = 37;

        internal void SetHttpProxy(string hostname, int port)
        {
            SetProxy(hostname, port, ProxyProtocolType.Http);
        }

        /// <summary>
        /// Remove the http proxy setting from current machine
        /// </summary>
        internal void RemoveHttpProxy()
        {
            RemoveProxy(ProxyProtocolType.Http);
        }

        /// <summary>
        /// Set the HTTPS proxy server for current machine
        /// </summary>
        /// <param name="hostname"></param>
        /// <param name="port"></param>
        internal void SetHttpsProxy(string hostname, int port)
        {
            SetProxy(hostname, port, ProxyProtocolType.Https);
        }

        /// <summary>
        /// Removes the https proxy setting to nothing
        /// </summary>
        internal void RemoveHttpsProxy()
        {
            RemoveProxy(ProxyProtocolType.Https);
        }

        internal void SetProxy(string hostname, int port, ProxyProtocolType protocolType)
        {
            var reg = Registry.CurrentUser.OpenSubKey(
                "Software\\Microsoft\\Windows\\CurrentVersion\\Internet Settings", true);

            if (reg != null)
            {
                PrepareRegistry(reg);

                var exisitingContent = reg.GetValue("ProxyServer") as string;
                var existingSystemProxyValues = GetSystemProxyValues(exisitingContent);
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

                reg.SetValue("ProxyEnable", 1);
                reg.SetValue("ProxyServer", string.Join(";", existingSystemProxyValues.Select(x => x.ToString()).ToArray()));
            }

            Refresh();
        }

        internal void RemoveProxy(ProxyProtocolType protocolType)
        {
            var reg = Registry.CurrentUser.OpenSubKey(
                "Software\\Microsoft\\Windows\\CurrentVersion\\Internet Settings", true);
            if (reg?.GetValue("ProxyServer") != null)
            {
                var exisitingContent = reg.GetValue("ProxyServer") as string;

                var existingSystemProxyValues = GetSystemProxyValues(exisitingContent);
                existingSystemProxyValues.RemoveAll(x => (protocolType | x.ProtocolType) != 0);

                if (existingSystemProxyValues.Count != 0)
                {
                    reg.SetValue("ProxyEnable", 1);
                    reg.SetValue("ProxyServer", string.Join(";", existingSystemProxyValues.Select(x => x.ToString()).ToArray()));
                }
                else
                {
                    reg.SetValue("ProxyEnable", 0);
                    reg.SetValue("ProxyServer", string.Empty);
                }
            }

            Refresh();
        }

        /// <summary>
        /// Removes all types of proxy settings (both http and https)
        /// </summary>
        internal void DisableAllProxy()
        {
            var reg = Registry.CurrentUser.OpenSubKey(
                "Software\\Microsoft\\Windows\\CurrentVersion\\Internet Settings", true);

            if (reg != null)
            {
                reg.SetValue("ProxyEnable", 0);
                reg.SetValue("ProxyServer", string.Empty);
            }

            Refresh();
        }

        /// <summary>
        /// Get the current system proxy setting values
        /// </summary>
        /// <param name="prevServerValue"></param>
        /// <returns></returns>
        private List<HttpSystemProxyValue> GetSystemProxyValues(string prevServerValue)
        {
            var result = new List<HttpSystemProxyValue>();

            if (string.IsNullOrWhiteSpace(prevServerValue))
                return result;

            var proxyValues = prevServerValue.Split(';');

            if (proxyValues.Length > 0)
            {
                result.AddRange(proxyValues.Select(ParseProxyValue).Where(parsedValue => parsedValue != null));
            }
            else
            {
                var parsedValue = ParseProxyValue(prevServerValue);
                if (parsedValue != null)
                    result.Add(parsedValue);
            }

            return result;
        }

        /// <summary>
        /// Parses the system proxy setting string
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        private HttpSystemProxyValue ParseProxyValue(string value)
        {
            var tmp = Regex.Replace(value, @"\s+", " ").Trim();

            int equalsIndex = tmp.IndexOf("=", StringComparison.InvariantCulture);
            if (equalsIndex >= 0)
            {
                string protocolTypeStr = tmp.Substring(0, equalsIndex);
                ProxyProtocolType? protocolType = null;
                if (protocolTypeStr.Equals("http", StringComparison.InvariantCultureIgnoreCase))
                {
                    protocolType = ProxyProtocolType.Http;
                }
                else if (protocolTypeStr.Equals("https", StringComparison.InvariantCultureIgnoreCase))
                {
                    protocolType = ProxyProtocolType.Https;
                }

                if (protocolType.HasValue)
                {
                    var endPointParts = tmp.Substring(equalsIndex + 1).Split(':');
                    return new HttpSystemProxyValue
                    {
                        HostName = endPointParts[0],
                        Port = int.Parse(endPointParts[1]),
                        ProtocolType = protocolType.Value,
                    };
                }
            }

            return null;
        }

        /// <summary>
        /// Prepares the proxy server registry (create empty values if they don't exist) 
        /// </summary>
        /// <param name="reg"></param>
        private static void PrepareRegistry(RegistryKey reg)
        {
            if (reg.GetValue("ProxyEnable") == null)
            {
                reg.SetValue("ProxyEnable", 0);
            }

            if (reg.GetValue("ProxyServer") == null || reg.GetValue("ProxyEnable") as string == "0")
            {
                reg.SetValue("ProxyServer", string.Empty);
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
