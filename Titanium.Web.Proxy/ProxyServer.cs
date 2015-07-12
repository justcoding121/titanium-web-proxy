using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Diagnostics;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Helpers;


namespace Titanium.Web.Proxy
{
    /// <summary>
    /// Proxy Server Main class
    /// </summary>
    public partial class ProxyServer
    {

        private static readonly int BUFFER_SIZE = 8192;
        private static readonly char[] semiSplit = new char[] { ';' };

        private static readonly String[] colonSpaceSplit = new string[] { ": " };
        private static readonly char[] spaceSplit = new char[] { ' ' };

        private static readonly Regex cookieSplitRegEx = new Regex(@",(?! )");

        private static object certificateAccessLock = new object();
        private static List<string> pinnedCertificateClients = new List<string>();



        private static TcpListener listener;
        private static Thread listenerThread;

        public static event EventHandler<SessionEventArgs> BeforeRequest;
        public static event EventHandler<SessionEventArgs> BeforeResponse;



        public static IPAddress ListeningIPInterface { get; set; }

        public static string RootCertificateName { get; set; }
        public static bool EnableSSL { get; set; }
        public static bool SetAsSystemProxy { get; set; }

        public static Int32 ListeningPort
        {
            get
            {
                return ((IPEndPoint)listener.LocalEndpoint).Port;
            }
        }

        public static CertificateManager CertManager { get; set; }

        static ProxyServer()
        {
            CertManager = new CertificateManager("Titanium",
                "Titanium Root Certificate Authority");
        }
        public ProxyServer()
        {


            System.Net.ServicePointManager.Expect100Continue = false;
            System.Net.WebRequest.DefaultWebProxy = null;
            System.Net.ServicePointManager.DefaultConnectionLimit = 10;
            ServicePointManager.DnsRefreshTimeout = 3 * 60 * 1000;//3 minutes
            ServicePointManager.MaxServicePointIdleTime = 3 * 60 * 1000;

            //HttpWebRequest certificate validation callback
            ServicePointManager.ServerCertificateValidationCallback = delegate(object s, X509Certificate certificate, X509Chain chain, SslPolicyErrors sslPolicyErrors)
            {
                if (sslPolicyErrors == SslPolicyErrors.None) return true;
                else
                    return false;
            };

            //Fix a bug in .NET 4.0
            NetFrameworkHelper.URLPeriodFix();

        }

        private static bool ShouldListen { get; set; }
        public static bool Start()
        {
           
            listener = new TcpListener(IPAddress.Any, 0);
            listener.Start();
            listenerThread = new Thread(new ParameterizedThreadStart(Listen));
            listenerThread.IsBackground = true;
            ShouldListen = true;
            listenerThread.Start(listener);

            if (SetAsSystemProxy)
            {
                SystemProxyHelper.EnableProxyHTTP("localhost", ListeningPort);
                FireFoxHelper.AddFirefox();

             
                if (EnableSSL)
                {
                    RootCertificateName = RootCertificateName == null ? "Titanium_Proxy_Test_Root" : RootCertificateName;

                    bool certTrusted = CertManager.CreateTrustedRootCertificate();
                    //If certificate was trusted by the machine
                    if (certTrusted)
                    {
                        SystemProxyHelper.EnableProxyHTTPS("localhost", ListeningPort);
                    }

                 
                }
            }

            return true;
        }


        public static void Stop()
        {
            if (SetAsSystemProxy)
            {
                SystemProxyHelper.DisableAllProxy();
                FireFoxHelper.RemoveFirefox();
            }

            ShouldListen = false;
            listener.Stop();
            listenerThread.Interrupt();

           
        }

        private static void Listen(Object obj)
        {
            TcpListener listener = (TcpListener)obj;

            try
            {
                while (ShouldListen)
                {

                    var client = listener.AcceptTcpClient();
                    Task.Factory.StartNew(() => HandleClient(client));

                }
            }
            catch (ThreadInterruptedException) { }
            catch (SocketException) { }


        }
      



    }
}