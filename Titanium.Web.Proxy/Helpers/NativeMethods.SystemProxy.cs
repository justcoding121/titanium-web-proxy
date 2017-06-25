using System;
using System.Runtime.InteropServices;

namespace Titanium.Web.Proxy.Helpers
{
    internal partial class NativeMethods
    {
        [DllImport("wininet.dll")]
        internal static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);

        [DllImport("kernel32.dll")]
        internal static extern IntPtr GetConsoleWindow();

        // Keeps it from getting garbage collected
        internal static ConsoleEventDelegate Handler;

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool SetConsoleCtrlHandler(ConsoleEventDelegate callback, bool add);

        // Pinvoke
        internal delegate bool ConsoleEventDelegate(int eventType);
    }
}