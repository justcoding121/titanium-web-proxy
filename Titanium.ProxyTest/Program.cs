using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Titanium.HTTPProxyServer.Test
{
    public class Program
    {
        public static void Main(string[] args)
        {
            //On Console exit reset system proxy
            handler = new ConsoleEventDelegate(ConsoleEventCallback);
            SetConsoleCtrlHandler(handler, true);

            //Start proxy controller
            var controller = new ProxyTestController();
            controller.Visited += PageVisited;

            controller.StartProxy();
     
            Console.Write("Do you want to monitor HTTPS? (Y/N):");

            if(Console.ReadLine().Trim().ToLower()=="y" )
            {
                InstallCertificate(System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location));
                SystemProxyUtility.EnableProxyHTTPS("localhost", controller.ListeningPort);
            }
            Console.WriteLine("Hit any key to exit..");
            Console.WriteLine(); 
            Console.Read();

            //Reset System Proxy on exit
            SystemProxyUtility.DisableAllProxy();
            FireFoxUtility.RemoveFirefox();
            controller.Stop();
        }
        private static void InstallCertificate(string cerDirectory)
        {
            X509Certificate2 certificate = new X509Certificate2(Path.Combine(cerDirectory , "Titanium Proxy Test Root Certificate.cer"));
            X509Store store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);

            store.Open(OpenFlags.ReadWrite);
            store.Add(certificate);
            store.Close();

            X509Store store1 = new X509Store(StoreName.My, StoreLocation.CurrentUser);

            store1.Open(OpenFlags.ReadWrite);
            store1.Add(certificate);
            store1.Close();
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
                    SystemProxyUtility.DisableAllProxy();
                    FireFoxUtility.RemoveFirefox();
                  
                }
                catch { }
            }
            return false;
        }
        // Keeps it from getting garbage collected
        static ConsoleEventDelegate handler;  
        // Pinvoke
        private delegate bool ConsoleEventDelegate(int eventType);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetConsoleCtrlHandler(ConsoleEventDelegate callback, bool add);
       


    }
  
}
