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
            handler = new ConsoleEventDelegate(ConsoleEventCallback);
            SetConsoleCtrlHandler(handler, true);

          
  
            Console.Write("Do you want to monitor HTTPS? (Y/N):");

            if(Console.ReadLine().Trim().ToLower()=="y" )
            {
                controller.EnableSSL = true;
               
            }

            Console.Write("Do you want to set this as a System Proxy? (Y/N):");

            if (Console.ReadLine().Trim().ToLower() == "y")
            {
                controller.SetAsSystemProxy = true;

            }

            controller.Visited += PageVisited;

            //Start proxy controller
            controller.StartProxy();

            Console.WriteLine("Hit any key to exit..");
            Console.WriteLine(); 
            Console.Read();

            controller.Stop();
        }
       
        private static void PageVisited(VisitedEventArgs e)
        {
            Console.WriteLine(string.Concat("Visited: ", e.URL));
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
        // Keeps it from getting garbage collected
        private static ConsoleEventDelegate handler;  
        // Pinvoke
        private delegate bool ConsoleEventDelegate(int eventType);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetConsoleCtrlHandler(ConsoleEventDelegate callback, bool add);
       


    }
  
}
