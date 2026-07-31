using System;
using Titanium.Web.Proxy.Examples.Basic.Helpers;
using Titanium.Web.Proxy.Helpers;

namespace Titanium.Web.Proxy.Examples.Basic
{
    public class Program
    {
        private static readonly ProxyTestController controller = new ProxyTestController();
        private static readonly object exitLock = new object();
        private static bool exiting;

        public static void Main(string[] args)
        {
            if (RunTime.IsWindows)
                // fix console hang due to QuickEdit mode
                ConsoleHelper.DisableQuickEditMode();

            // Ctrl+C / Ctrl+Break: cancel the default terminate so finally can clear system proxy.
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                RequestExit();
            };

            try
            {
                controller.StartProxy();

                Console.WriteLine("Traffic tape: one compact line per completed request (errors are one-liners).");
                Console.WriteLine("Hit any key to exit..");
                Console.WriteLine();

                if (Console.IsInputRedirected)
                    // Console.Read() returns immediately (EOF) when stdin has no real interactive source
                    // (e.g. run under a process launcher with redirected/absent input), which would
                    // otherwise tear the proxy down right after starting it - block forever instead so the
                    // process behaves like a long-running service until explicitly killed.
                    // System proxy restore on kill relies on ProcessExit / console-control handlers in the library.
                    System.Threading.Thread.Sleep(System.Threading.Timeout.Infinite);
                else
                    Console.Read();
            }
            finally
            {
                Shutdown();
            }
        }

        private static void RequestExit()
        {
            // Second Ctrl+C: allow the runtime to terminate if shutdown is stuck.
            lock (exitLock)
            {
                if (exiting)
                    Environment.Exit(0);
            }

            // Wake the main thread from Console.Read by closing stdin isn't portable;
            // Environment.Exit runs ProcessExit (library restores proxy) then finally below if we Shutdown first.
            Shutdown();
            Environment.Exit(0);
        }

        private static void Shutdown()
        {
            lock (exitLock)
            {
                if (exiting) return;
                exiting = true;
            }

            try
            {
                controller.Stop();
            }
            catch (Exception)
            {
                // Best-effort: Stop may throw if the proxy never started or already stopped.
            }

            try
            {
                controller.Dispose();
            }
            catch (Exception)
            {
                // Best-effort cleanup.
            }
        }
    }
}
