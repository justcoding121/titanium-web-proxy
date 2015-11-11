using Autofac;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Helpers;

namespace Titanium.Web.Proxy
{
    /// <summary>
    ///     Proxy Server Main class
    /// </summary>
    public partial class ProxyServer
    {
        private static readonly int BUFFER_SIZE = 8192;
        
        private static readonly string[] ColonSpaceSplit = { ": " };
        private static readonly char[] SpaceSplit = { ' ' };

        private static readonly Regex CookieSplitRegEx = new Regex(@",(?! )");

        private static readonly byte[] ChunkTrail = Encoding.ASCII.GetBytes(Environment.NewLine);

        private static readonly byte[] ChunkEnd =
            Encoding.ASCII.GetBytes(0.ToString("x2") + Environment.NewLine + Environment.NewLine);

        private static TcpListener _listener;

        public static List<string> ExcludedHttpsHostNameRegex = new List<string>();
        private static IContainer _container;
        private static ILifetimeScope _scope;

        static ProxyServer()
        {
            CertManager = new CertificateManager("Titanium",
                "Titanium Root Certificate Authority");

            ListeningIpAddress = IPAddress.Any;
            ListeningPort = 0;

            SetupAutofac();

            Initialize();
        }

        private static void SetupAutofac()
        {
            var builder = new ContainerBuilder();
            builder.RegisterModule(new AutofacSetup());
            _container = builder.Build();

            _scope = _container.BeginLifetimeScope();
        }

        private static CertificateManager CertManager { get; set; }

        public static string RootCertificateName { get; set; }
        public static bool EnableSsl { get; set; }
        public static bool SetAsSystemProxy { get; set; }

        public static int ListeningPort { get; set; }
        public static IPAddress ListeningIpAddress { get; set; }

        public static event EventHandler<SessionEventArgs> BeforeRequest;
        public static event EventHandler<SessionEventArgs> BeforeResponse;

        public static void Initialize()
        {
            ServicePointManager.Expect100Continue = false;
            WebRequest.DefaultWebProxy = null;
            ServicePointManager.DefaultConnectionLimit = 10;
            ServicePointManager.DnsRefreshTimeout = 3 * 60 * 1000; //3 minutes
            ServicePointManager.MaxServicePointIdleTime = 3 * 60 * 1000;

            //HttpWebRequest certificate validation callback
            ServicePointManager.ServerCertificateValidationCallback =
                delegate(object s, X509Certificate certificate, X509Chain chain, SslPolicyErrors sslPolicyErrors)
                {
                    if (sslPolicyErrors == SslPolicyErrors.None) return true;
                    return false;
                };

            //Fix a bug in .NET 4.0
            NetFrameworkHelper.UrlPeriodFix();
            //useUnsafeHeaderParsing 
            NetFrameworkHelper.ToggleAllowUnsafeHeaderParsing(true);
        }


        public static bool Start()
        {
            _listener = new TcpListener(ListeningIpAddress, ListeningPort);
            _listener.Start();

            ListeningPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
            // accept clients asynchronously
            _listener.BeginAcceptTcpClient(OnAcceptConnection, _listener);

            var certTrusted = false;

            if (EnableSsl)
                certTrusted = CertManager.CreateTrustedRootCertificate();

            if (SetAsSystemProxy)
            {
                SystemProxyHelper.EnableProxyHttp(
                    Equals(ListeningIpAddress, IPAddress.Any) ? "127.0.0.1" : ListeningIpAddress.ToString(), ListeningPort);
                FireFoxHelper.AddFirefox();


                if (EnableSsl)
                {
                    RootCertificateName = RootCertificateName ?? "Titanium_Proxy_Test_Root";

                    //If certificate was trusted by the machine
                    if (certTrusted)
                    {
                        SystemProxyHelper.EnableProxyHttps(
                            Equals(ListeningIpAddress, IPAddress.Any) ? "127.0.0.1" : ListeningIpAddress.ToString(),
                            ListeningPort);
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
                _listener.BeginAcceptTcpClient(OnAcceptConnection, _listener);

                var client = _listener.EndAcceptTcpClient(asyn);
                Task.Factory.StartNew(() => HandleClient(client));
            }
            catch
            {
                // ignored
            }
        }


        public static void Stop()
        {
            if (SetAsSystemProxy)
            {
                SystemProxyHelper.DisableAllProxy();
                FireFoxHelper.RemoveFirefox();
            }

            _listener.Stop();
            CertManager.Dispose();
        }
    }
}