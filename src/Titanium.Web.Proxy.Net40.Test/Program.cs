using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Titanium.Web.Proxy.Helpers;

namespace Titanium.Web.Proxy.Test
{
    public class Program
    {
        static ProxyTestController controller = new ProxyTestController();
        public static void Main(string[] args)
        {
            //On Console exit make sure we also exit the proxy
            NativeMethods.handler = new NativeMethods.ConsoleEventDelegate(ConsoleEventCallback);
            NativeMethods.SetConsoleCtrlHandler(NativeMethods.handler, true);



            Console.Write("Do you want to monitor HTTPS? (Y/N):");

            if (Console.ReadLine().Trim().ToLower() == "y")
            {
                controller.EnableSSL = true;

            }

            Console.Write("Do you want to set this as a System Proxy? (Y/N):");

            if (Console.ReadLine().Trim().ToLower() == "y")
            {
                controller.SetAsSystemProxy = true;

            }

            //Start proxy controller
            controller.StartProxy();

            Console.WriteLine("Hit any key to exit..");
            Console.WriteLine();
            Console.Read();

            controller.Stop();
        }


        static bool ConsoleEventCallback(int eventType)
        {
            if (eventType == 2)
            {
                try
                {
                    controller.Stop();

                }
                catch { }
            }
            return false;
        }

    }
    internal static class NativeMethods
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool SetConsoleCtrlHandler(ConsoleEventDelegate callback, bool add);

        // Pinvoke
        internal delegate bool ConsoleEventDelegate(int eventType);


        // Keeps it from getting garbage collected
        internal static ConsoleEventDelegate handler;
    }

}
