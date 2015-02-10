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


namespace Titanium.HTTPProxyServer
{
    /// <summary>
    /// Proxy Server Main class
    /// </summary>
    public partial class ProxyServer
    {

        private static readonly int BUFFER_SIZE = 8192;
        private static readonly char[] semiSplit = new char[] { ';' };
        private static readonly char[] equalSplit = new char[] { '=' };
        private static readonly String[] colonSpaceSplit = new string[] { ": " };
        private static readonly char[] spaceSplit = new char[] { ' ' };
        private static readonly char[] commaSplit = new char[] { ',' };
        private static readonly Regex cookieSplitRegEx = new Regex(@",(?! )");

        private static object _outputLockObj = new object();
        private static List<string> _pinnedCertificateClients = new List<string>();
        private static Dictionary<string, X509Certificate2> certificateCache = new Dictionary<string, X509Certificate2>();
        private static X509Store _store = new X509Store(StoreName.My, StoreLocation.CurrentUser);

        private static TcpListener _listener;
        private static Thread _listenerThread;

        public static event EventHandler<SessionEventArgs> BeforeRequest;
        public static event EventHandler<SessionEventArgs> BeforeResponse;



        public IPAddress ListeningIPInterface { get; set; }


        public static Int32 ListeningPort
        {
            get
            {
                return ((IPEndPoint)_listener.LocalEndpoint).Port;
            }
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

            URLPeriodFix();

        }


        public static bool Start()
        {
            _listener = new TcpListener(IPAddress.Any, 0);
            _listener.Start();
            _listenerThread = new Thread(new ParameterizedThreadStart(Listen));
            _listenerThread.Start(_listener);
            _listenerThread.IsBackground = true;

            return true;
        }


        public static void Stop()
        {
            _listener.Stop();
            _listenerThread.Abort();
            _listenerThread.Join();
        }
        // Thread signal. 
        public static ManualResetEvent tcpClientConnected =
            new ManualResetEvent(false);
        private static void Listen(Object obj)
        {
            TcpListener listener = (TcpListener)obj;
     
            try
            {
                while (true)
                {
                    // Set the event to nonsignaled state.
                    tcpClientConnected.Reset();

                    listener.BeginAcceptTcpClient(new AsyncCallback(DoAcceptTcpClientCallback), listener);
                    // Wait until a connection is made and processed before  
                    // continuing.
                    tcpClientConnected.WaitOne();
                }
            }
            catch (ThreadAbortException ex) { Debug.WriteLine(ex.Message); }
            catch (SocketException ex)
            {
                Debug.WriteLine(ex.Message);
            }


        }
        public static void DoAcceptTcpClientCallback(IAsyncResult ar)
        {
            // Get the listener that handles the client request.
            TcpListener listener = (TcpListener)ar.AsyncState;

            // End the operation and display the received data on  
            // the console.
            TcpClient client = listener.EndAcceptTcpClient(ar);

            while (!ThreadPool.UnsafeQueueUserWorkItem(new WaitCallback(ProcessClient), client)) ;

            // Signal the calling thread to continue.
            tcpClientConnected.Set();
        }
        private static int pending = 0;
        private static void ProcessClient(Object param)
        {
          
            try
            {
                TcpClient client = param as TcpClient;
                DoHttpProcessing(client);
                client.Close();
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.Message);
            }

        }


    }
}
