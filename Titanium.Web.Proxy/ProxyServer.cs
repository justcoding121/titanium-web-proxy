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

        private static ManualResetEvent tcpClientConnected = new ManualResetEvent(false);

        private static TcpListener listener;
        private static Thread listenerThread;

        public static event EventHandler<SessionEventArgs> BeforeRequest;
        public static event EventHandler<SessionEventArgs> BeforeResponse;



        public static IPAddress ListeningIPInterface { get; set; }

        public static string RootCertificateName { get; set; }

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
                "Titanium Root Certificate");
        }
        public ProxyServer()
        {


            System.Net.ServicePointManager.Expect100Continue = false;
            System.Net.WebRequest.DefaultWebProxy = null;
            System.Net.ServicePointManager.DefaultConnectionLimit = 10;
            ServicePointManager.DnsRefreshTimeout = 3 * 60 * 1000;//3 minutes
            ServicePointManager.MaxServicePointIdleTime = 3 * 60 * 1000;

            ServicePointManager.ServerCertificateValidationCallback = delegate(object s, X509Certificate certificate, X509Chain chain, SslPolicyErrors sslPolicyErrors)
            {
                if (sslPolicyErrors == SslPolicyErrors.None) return true;
                else
                    return false;
            };

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

                RootCertificateName = RootCertificateName == null ? "DO_NOT_TRUST_FiddlerRoot" : RootCertificateName;

                bool certTrusted = CertManager.CreateTrustedRootCertificate();
                if (!certTrusted)
                {
                    // The user didn't want to install the self-signed certificate to the root store.
                }
                //CertificateHelper.InstallCertificate(Path.Combine(System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location), string.Concat(RootCertificateName, ".cer")));
                
                if (EnableSSL)
                {
                    SystemProxyHelper.EnableProxyHTTPS("localhost", ListeningPort);
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
                    // Set the event to nonsignaled state.
                    tcpClientConnected.Reset();

                    listener.BeginAcceptTcpClient(new AsyncCallback(AcceptTcpClientCallback), listener);
                    // Wait until a connection is made and processed before  
                    // continuing.
                    tcpClientConnected.WaitOne();
                }
            }
            catch (ThreadInterruptedException) { }
            catch (SocketException ex)
            {
                Debug.WriteLine(ex.Message);
            }



        }
        public static void AcceptTcpClientCallback(IAsyncResult ar)
        {
            try
            {
                // Get the listener that handles the client request.
                TcpListener listener = (TcpListener)ar.AsyncState;
                TcpClient client = null;


                // End the operation and display the received data on  
                // the console.
                client = listener.EndAcceptTcpClient(ar);

                Task.Factory.StartNew(() => ProcessClient(client));

                // Signal the calling thread to continue.
                tcpClientConnected.Set();
            }
            catch(ObjectDisposedException) { }
        }

        private static void ProcessClient(Object param)
        {

            try
            {
                TcpClient client = param as TcpClient;
                HandleClientRequest(client);
                client.Close();
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.Message);
            }

        }



        public static bool EnableSSL { get; set; }

        public static bool SetAsSystemProxy { get; set; }
    }
}