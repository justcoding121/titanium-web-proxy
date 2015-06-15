using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Win32;
using System.Runtime.InteropServices;

namespace Titanium.Web.Proxy.Test.Helpers
{
    public static class SystemProxyHelper
    {
        [DllImport("wininet.dll")]
        public static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);
        public  const int INTERNET_OPTION_SETTINGS_CHANGED = 39;
        public  const int INTERNET_OPTION_REFRESH = 37;
        static bool settingsReturn, refreshReturn;
        static object prevProxyServer;
        static object prevProxyEnable;

        public static void EnableProxyHTTP(string hostname, int port)
        {
            RegistryKey reg = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Internet Settings", true);
            prevProxyEnable = reg.GetValue("ProxyEnable");
            prevProxyServer = reg.GetValue("ProxyServer");
            reg.SetValue("ProxyEnable", 1);
            reg.SetValue("ProxyServer", "http="+hostname+":" + port + ";");
            refresh();
        }
        public static void EnableProxyHTTPS(string hostname, int port)
        {
            RegistryKey reg = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Internet Settings", true);
            reg.SetValue("ProxyEnable", 1);
            reg.SetValue("ProxyServer", "http=" + hostname + ":" + port + ";https=" + hostname + ":" + port);
            refresh();
        }
        public static void DisableAllProxy()
        {
            RegistryKey reg = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Internet Settings", true);
            reg.SetValue("ProxyEnable", prevProxyEnable);
            if (prevProxyServer != null)
                reg.SetValue("ProxyServer", prevProxyServer);
            refresh();
        }
        private static void refresh()
        {
            settingsReturn = InternetSetOption(IntPtr.Zero, INTERNET_OPTION_SETTINGS_CHANGED, IntPtr.Zero, 0);
            refreshReturn = InternetSetOption(IntPtr.Zero, INTERNET_OPTION_REFRESH, IntPtr.Zero, 0);
        }
    }
}
