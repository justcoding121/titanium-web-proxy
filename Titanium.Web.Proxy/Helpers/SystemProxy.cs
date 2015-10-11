using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Titanium.Web.Proxy.Helpers
{
    internal static class NativeMethods
    {
        [DllImport("wininet.dll")]
        internal static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer,
            int dwBufferLength);
    }

    public static class SystemProxyHelper
    {
        public const int InternetOptionSettingsChanged = 39;
        public const int InternetOptionRefresh = 37;
        private static object _prevProxyServer;
        private static object _prevProxyEnable;

        public static void EnableProxyHttp(string hostname, int port)
        {
            var reg = Registry.CurrentUser.OpenSubKey(
                "Software\\Microsoft\\Windows\\CurrentVersion\\Internet Settings", true);
            if (reg != null)
            {
                _prevProxyEnable = reg.GetValue("ProxyEnable");
                _prevProxyServer = reg.GetValue("ProxyServer");
                reg.SetValue("ProxyEnable", 1);
                reg.SetValue("ProxyServer", "http=" + hostname + ":" + port + ";");
            }
            Refresh();
        }

        public static void EnableProxyHttps(string hostname, int port)
        {
            var reg = Registry.CurrentUser.OpenSubKey(
                "Software\\Microsoft\\Windows\\CurrentVersion\\Internet Settings", true);
            if (reg != null)
            {
                reg.SetValue("ProxyEnable", 1);
                reg.SetValue("ProxyServer", "http=" + hostname + ":" + port + ";https=" + hostname + ":" + port);
            }
            Refresh();
        }

        public static void DisableAllProxy()
        {
            var reg = Registry.CurrentUser.OpenSubKey(
                "Software\\Microsoft\\Windows\\CurrentVersion\\Internet Settings", true);
            if (reg != null)
            {
                reg.SetValue("ProxyEnable", _prevProxyEnable);
                if (_prevProxyServer != null)
                    reg.SetValue("ProxyServer", _prevProxyServer);
            }
            Refresh();
        }

        private static void Refresh()
        {
            NativeMethods.InternetSetOption(IntPtr.Zero, InternetOptionSettingsChanged, IntPtr.Zero,0);
            NativeMethods.InternetSetOption(IntPtr.Zero, InternetOptionRefresh, IntPtr.Zero, 0);
        }
    }
}