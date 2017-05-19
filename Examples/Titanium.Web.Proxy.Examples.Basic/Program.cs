using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Titanium.Web.Proxy.Examples.Basic.Helpers;

namespace Titanium.Web.Proxy.Examples.Basic
{
    public class Program
    {
        private static readonly ProxyTestController controller = new ProxyTestController();

        public static void Main(string[] args)
        {
            //fix console hang due to QuickEdit mode
            ConsoleHelper.DisableQuickEditMode();

            //On Console exit make sure we also exit the proxy
            NativeMethods.Handler = ConsoleEventCallback;
            NativeMethods.SetConsoleCtrlHandler(NativeMethods.Handler, true);

            //Start proxy controller
            controller.StartProxy();

            Console.WriteLine("Hit any key to exit..");
            Console.WriteLine();
            Console.Read();

            controller.Stop();
        }


        private static bool ConsoleEventCallback(int eventType)
        {
            if (eventType != 2) return false;
            try
            {
                controller.Stop();
            }
            catch
            {
                // ignored
            }
            return false;
        }
    }

    internal static class NativeMethods
    {
        // Keeps it from getting garbage collected
        internal static ConsoleEventDelegate Handler;

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool SetConsoleCtrlHandler(ConsoleEventDelegate callback, bool add);

        // Pinvoke
        internal delegate bool ConsoleEventDelegate(int eventType);

    }

}
