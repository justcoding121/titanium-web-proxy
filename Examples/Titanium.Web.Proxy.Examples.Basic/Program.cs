using System;
using System.Runtime.InteropServices;

namespace Titanium.Web.Proxy.Examples.Basic
{
    public class Program
    {
        private static readonly ProxyTestController Controller = new ProxyTestController();

        public static void Main(string[] args)
        {
            //Start proxy controller
            Controller.StartProxy();

            Console.WriteLine("Hit any key to exit..");
            Console.WriteLine();
            Console.ReadLine();

            Controller.Stop();
        }
    }
}