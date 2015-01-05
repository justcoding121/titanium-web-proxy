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
using Titanium.HTTPProxyServer;


namespace HTTPProxyServer
{

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

        private TcpListener _listener;
        private Thread _listenerThread;

        public event EventHandler<SessionEventArgs> BeforeRequest;
        public event EventHandler<SessionEventArgs> BeforeResponse;



        public IPAddress ListeningIPInterface { get; set; }


        public Int32 ListeningPort
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


        public bool Start()
        {
            _listener = new TcpListener(IPAddress.Any, 0);
            _listener.Start();
            _listenerThread = new Thread(new ParameterizedThreadStart(Listen));
            _listenerThread.Start(_listener);
            _listenerThread.IsBackground = true;

      

            return true;
        }


        public void Stop()
        {
            _listener.Stop();
            _listenerThread.Abort();
            _listenerThread.Join();
        }

        private void Listen(Object obj)
        {
            TcpListener listener = (TcpListener)obj;
            CredentialManager.Cache = new Dictionary<string, System.Security.Principal.WindowsPrincipal>();
            try
            {
                while (true)
                {


                    TcpClient client = listener.AcceptTcpClient();
                    while (!ThreadPool.UnsafeQueueUserWorkItem(new WaitCallback(ProcessClient), client)) ;
                }
            }
            catch (ThreadAbortException ex) { Debug.WriteLine(ex.Message); }
            catch (SocketException ex)
            {
                Debug.WriteLine(ex.Message);
            }


        }

        private void ProcessClient(Object obj)
        {
            try
            {
                TcpClient client = (TcpClient)obj;
                DoHttpProcessing(client);
                client.Close();
            }
            catch { }
        }


    }
}
