using System;
using System.Runtime.InteropServices;
using Titanium.Web.Proxy.Examples.Basic.Helpers;

namespace Titanium.Web.Proxy.Examples.Basic
{
    public class Program
    {
        private static readonly ProxyTestController controller = new ProxyTestController();

        public static void Main(string[] args)
        {
            if(RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                 // fix console hang due to QuickEdit mode
                ConsoleHelper.DisableQuickEditMode();
            }

            // Start proxy controller
            controller.StartProxy();

            Console.WriteLine("Hit any key to exit..");
            Console.WriteLine();
            Console.Read();

            controller.Stop();
        }
    }
}
