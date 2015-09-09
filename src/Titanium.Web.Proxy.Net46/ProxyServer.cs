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
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Helpers;
using System.Text;


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

        private static readonly byte[] chunkTrail = Encoding.ASCII.GetBytes(Environment.NewLine);
        private static readonly byte[] ChunkEnd = Encoding.ASCII.GetBytes(0.ToString("x2") + Environment.NewLine + Environment.NewLine);

        private static object certificateAccessLock = new object();

        private static TcpListener listener;
        private static CertificateManager CertManager { get; set; }

        public static List<string> ExcludedHttpsHostNameRegex = new List<string>();

        public static event EventHandler<SessionEventArgs> BeforeRequest;
        public static event EventHandler<SessionEventArgs> BeforeResponse;

        public static string RootCertificateName { get; set; }
        public static bool EnableSSL { get; set; }
        public static bool SetAsSystemProxy { get; set; }

        public static Int32 ListeningPort { get; set; }
        public static IPAddress ListeningIpAddress { get; set; }

        static ProxyServer()
        {
            CertManager = new CertificateManager("Titanium",
                "Titanium Root Certificate Authority");

            ListeningIpAddress = IPAddress.Any;
            ListeningPort = 0;

            Initialize();
        }

        public static void Initialize()
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


        }



        public static bool Start()
        {
            listener = new TcpListener(ListeningIpAddress, ListeningPort);
            listener.Start();

            ListeningPort = ((IPEndPoint)listener.LocalEndpoint).Port;
            // accept clients asynchronously
            listener.BeginAcceptTcpClient(OnAcceptConnection, listener);

            if (SetAsSystemProxy)
            {
                SystemProxyHelper.EnableProxyHTTP(ListeningIpAddress == IPAddress.Any ? "127.0.0.1" : ListeningIpAddress.ToString(), ListeningPort);
                FireFoxHelper.AddFirefox();


                if (EnableSSL)
                {
                    RootCertificateName = RootCertificateName == null ? "Titanium_Proxy_Test_Root" : RootCertificateName;

                    bool certTrusted = CertManager.CreateTrustedRootCertificate();
                    //If certificate was trusted by the machine
                    if (certTrusted)
                    {
                        SystemProxyHelper.EnableProxyHTTPS(ListeningIpAddress == IPAddress.Any ? "127.0.0.1" : ListeningIpAddress.ToString(), ListeningPort);
                    }
                }
            }

            return true;
        }
        private static void OnAcceptConnection(IAsyncResult asyn)
        {        
            try
            {
                // Get the listener that handles the client request.
                listener.BeginAcceptTcpClient(OnAcceptConnection, listener);

                TcpClient client = listener.EndAcceptTcpClient(asyn);
                Task.Factory.StartNew(() => HandleClient(client));
            }
            catch { }
          
        
        }


        public static void Stop()
        {
            if (SetAsSystemProxy)
            {
                SystemProxyHelper.DisableAllProxy();
                FireFoxHelper.RemoveFirefox();
            }

            listener.Stop();
            CertManager.Dispose();

        }
    }
}