using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.Linq;

namespace Titanium.Web.Proxy.Helpers
{
    internal static class NativeMethods
    {
        [DllImport("wininet.dll")]
        internal static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer,
            int dwBufferLength);
    }

   internal class HttpSystemProxyValue
    {
        internal string HostName { get; set; }
        internal int Port { get; set; }
        internal bool IsHttps { get; set; }

        public override string ToString()
        {
            if (!IsHttps)
                return "http=" + HostName + ":" + Port;
            else
                return "https=" + HostName + ":" + Port;
        }
    }

    internal static class SystemProxyHelper
    {
        internal const int InternetOptionSettingsChanged = 39;
        internal const int InternetOptionRefresh = 37;

        internal static void SetHttpProxy(string hostname, int port)
        {
            var reg = Registry.CurrentUser.OpenSubKey(
                "Software\\Microsoft\\Windows\\CurrentVersion\\Internet Settings", true);
            if (reg != null)
            {
                prepareRegistry(reg);

                var exisitingContent = reg.GetValue("ProxyServer") as string;
                var existingSystemProxyValues = GetSystemProxyValues(exisitingContent);
                existingSystemProxyValues.RemoveAll(x => !x.IsHttps);
                existingSystemProxyValues.Add(new HttpSystemProxyValue()
                {
                    HostName = hostname,
                    IsHttps = false,
                    Port = port
                });

                reg.SetValue("ProxyEnable", 1);
                reg.SetValue("ProxyServer", String.Join(";", existingSystemProxyValues.Select(x => x.ToString()).ToArray()));
            }

            Refresh();
        }

     
        internal static void RemoveHttpProxy()
        {
            var reg = Registry.CurrentUser.OpenSubKey(
                    "Software\\Microsoft\\Windows\\CurrentVersion\\Internet Settings", true);
            if (reg != null)
            {
                if (reg.GetValue("ProxyServer")!=null)
                {
                    var exisitingContent = reg.GetValue("ProxyServer") as string;

                    var existingSystemProxyValues = GetSystemProxyValues(exisitingContent);
                    existingSystemProxyValues.RemoveAll(x => !x.IsHttps);

                    if (!(existingSystemProxyValues.Count() == 0))
                    {
                        reg.SetValue("ProxyEnable", 1);
                        reg.SetValue("ProxyServer", String.Join(";", existingSystemProxyValues.Select(x => x.ToString()).ToArray()));
                    }
                    else
                    {
                        reg.SetValue("ProxyEnable", 0);
                        reg.SetValue("ProxyServer", string.Empty);
                    }
                }
            }

            Refresh();
        }

        internal static void SetHttpsProxy(string hostname, int port)
        {
            var reg = Registry.CurrentUser.OpenSubKey(
                "Software\\Microsoft\\Windows\\CurrentVersion\\Internet Settings", true);
            
            if (reg != null)
            {
                prepareRegistry(reg);

                var exisitingContent = reg.GetValue("ProxyServer") as string;

                var existingSystemProxyValues = GetSystemProxyValues(exisitingContent);
                existingSystemProxyValues.RemoveAll(x => x.IsHttps);
                existingSystemProxyValues.Add(new HttpSystemProxyValue()
                {
                    HostName = hostname,
                    IsHttps = true,
                    Port = port
                });

                reg.SetValue("ProxyEnable", 1);
                reg.SetValue("ProxyServer", String.Join(";", existingSystemProxyValues.Select(x => x.ToString()).ToArray()));
            }

            Refresh();
        }

        internal static void RemoveHttpsProxy()
        {
            var reg = Registry.CurrentUser.OpenSubKey(
                    "Software\\Microsoft\\Windows\\CurrentVersion\\Internet Settings", true);
            if (reg != null)
            {
                if (reg.GetValue("ProxyServer") != null)
                {
                    var exisitingContent = reg.GetValue("ProxyServer") as string;

                    var existingSystemProxyValues = GetSystemProxyValues(exisitingContent);
                    existingSystemProxyValues.RemoveAll(x => x.IsHttps);

                    if (!(existingSystemProxyValues.Count() == 0))
                    {
                        reg.SetValue("ProxyEnable", 1);
                        reg.SetValue("ProxyServer", String.Join(";", existingSystemProxyValues.Select(x => x.ToString()).ToArray()));
                    }
                    else
                    {
                        reg.SetValue("ProxyEnable", 0);
                        reg.SetValue("ProxyServer", string.Empty);
                    }
                  
                }
            }

            Refresh();
        }

        internal static void DisableAllProxy()
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
        private static List<HttpSystemProxyValue> GetSystemProxyValues(string prevServerValue)
        {
            var result = new List<HttpSystemProxyValue>();

            if (string.IsNullOrWhiteSpace(prevServerValue))
                return result;

            var proxyValues = prevServerValue.Split(';');

            if (proxyValues.Length > 0)
            {
                foreach (var value in proxyValues)
                {
                    var parsedValue = parseProxyValue(value);
                    if (parsedValue != null)
                        result.Add(parsedValue);
                }
            }
            else
            {
                var parsedValue = parseProxyValue(prevServerValue);
                if (parsedValue != null)
                    result.Add(parsedValue);
            }

            return result;
        }

        private static HttpSystemProxyValue parseProxyValue(string value)
        {
            var tmp = Regex.Replace(value, @"\s+", " ").Trim().ToLower();
            if (tmp.StartsWith("http="))
            {
                var endPoint = tmp.Substring(5);
                return new HttpSystemProxyValue()
                {
                    HostName = endPoint.Split(':')[0],
                    Port = int.Parse(endPoint.Split(':')[1]),
                    IsHttps = false
                };
            }
            else if (tmp.StartsWith("https="))
            {
                var endPoint = tmp.Substring(5);
                return new HttpSystemProxyValue()
                {
                    HostName = endPoint.Split(':')[0],
                    Port = int.Parse(endPoint.Split(':')[1]),
                    IsHttps = true
                };
            }
            return null;

        }

        private static void prepareRegistry(RegistryKey reg)
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
       

        private static void Refresh()
        {
            NativeMethods.InternetSetOption(IntPtr.Zero, InternetOptionSettingsChanged, IntPtr.Zero, 0);
            NativeMethods.InternetSetOption(IntPtr.Zero, InternetOptionRefresh, IntPtr.Zero, 0);
        }
    }
}