using System;
using Titanium.Web.Proxy.Examples.Basic.Helpers;
using Titanium.Web.Proxy.Helpers;

namespace Titanium.Web.Proxy.Examples.Basic
{
    public class Program
    {
        private static readonly ProxyTestController controller = new ProxyTestController();

        public static void Main(string[] args)
        {
            if (RunTime.IsWindows)
                // fix console hang due to QuickEdit mode
                ConsoleHelper.DisableQuickEditMode();

            // Start proxy controller
            controller.StartProxy();

            Console.WriteLine("Hit any key to exit..");
            Console.WriteLine();

            if (Console.IsInputRedirected)
                // Console.Read() returns immediately (EOF) when stdin has no real interactive source
                // (e.g. run under a process launcher with redirected/absent input), which would
                // otherwise tear the proxy down right after starting it - block forever instead so the
                // process behaves like a long-running service until explicitly killed.
                System.Threading.Thread.Sleep(System.Threading.Timeout.Infinite);
            else
                Console.Read();

            controller.Stop();
        }
    }
}