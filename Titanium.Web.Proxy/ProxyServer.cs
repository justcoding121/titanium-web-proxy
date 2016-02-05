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
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network;
using System.Linq;

namespace Titanium.Web.Proxy
{
    /// <summary>
    ///     Proxy Server Main class
    /// </summary>
    public partial class ProxyServer
    {
        private static readonly int BUFFER_SIZE = 8192;
        private static readonly char[] SemiSplit = { ';' };

        private static readonly string[] ColonSpaceSplit = { ": " };
        private static readonly char[] SpaceSplit = { ' ' };

        private static readonly Regex CookieSplitRegEx = new Regex(@",(?! )");

        private static readonly byte[] NewLineBytes = Encoding.ASCII.GetBytes(Environment.NewLine);

        private static readonly byte[] ChunkEnd =
            Encoding.ASCII.GetBytes(0.ToString("x2") + Environment.NewLine + Environment.NewLine);

        private static List<ProxyEndPoint> _proxyEndPoints { get; set; }


        static ProxyServer()
        {
            CertManager = new CertificateManager("Titanium",
                "Titanium Root Certificate Authority");

            _proxyEndPoints = new List<ProxyEndPoint>();

            Initialize();
        }

        private static CertificateManager CertManager { get; set; }
        private static bool EnableSsl { get; set; }
        private static bool certTrusted { get; set; }
        private static bool proxyStarted { get; set; }

        public static string RootCertificateName { get; set; }

        public static event EventHandler<SessionEventArgs> BeforeRequest;
        public static event EventHandler<SessionEventArgs> BeforeResponse;


        public static void Initialize()
        {
            Task.Factory.StartNew(() => TcpConnectionManager.ClearIdleConnections());
        }

        public static void AddEndPoint(ProxyEndPoint endPoint)
        {
            if (proxyStarted)
                throw new Exception("Cannot add end points after proxy started.");

            _proxyEndPoints.Add(endPoint);
        }
  

        public static void SetAsSystemProxy(ExplicitProxyEndPoint endPoint)
        {
            if (_proxyEndPoints.Contains(endPoint) == false)
                throw new Exception("Cannot set endPoints not added to proxy as system proxy");

            if (!proxyStarted)
                throw new Exception("Cannot set system proxy settings before proxy has been started.");

            //clear any settings previously added
            _proxyEndPoints.OfType<ExplicitProxyEndPoint>().ToList().ForEach(x => x.IsSystemProxy = false);

            endPoint.IsSystemProxy = true;

            SystemProxyHelper.EnableProxyHttp(
                Equals(endPoint.IpAddress, IPAddress.Any) ? "127.0.0.1" : endPoint.IpAddress.ToString(), endPoint.Port);

#if !DEBUG
            FireFoxHelper.AddFirefox();
#endif

            if (endPoint.EnableSsl)
            {
                RootCertificateName = RootCertificateName ?? "Titanium_Proxy_Test_Root";

                //If certificate was trusted by the machine
                if (certTrusted)
                {
                    SystemProxyHelper.EnableProxyHttps(
                        Equals(endPoint.IpAddress, IPAddress.Any) ? "127.0.0.1" : endPoint.IpAddress.ToString(),
                        endPoint.Port);
                }
            }

        }

        public static void Start()
        {
            EnableSsl = _proxyEndPoints.Any(x => x.EnableSsl);

            if (EnableSsl)
                certTrusted = CertManager.CreateTrustedRootCertificate();

            foreach (var endPoint in _proxyEndPoints)
            {

                endPoint.listener = new TcpListener(endPoint.IpAddress, endPoint.Port);
                endPoint.listener.Start();

                endPoint.Port = ((IPEndPoint)endPoint.listener.LocalEndpoint).Port;
                // accept clients asynchronously
                endPoint.listener.BeginAcceptTcpClient(OnAcceptConnection, endPoint);
            }

            proxyStarted = true;
        }

        private static void OnAcceptConnection(IAsyncResult asyn)
        {
            var endPoint = (ProxyEndPoint)asyn.AsyncState;

            // Get the listener that handles the client request.
            endPoint.listener.BeginAcceptTcpClient(OnAcceptConnection, endPoint);

            var client = endPoint.listener.EndAcceptTcpClient(asyn);

            try
            {
                if (endPoint.GetType() == typeof(TransparentProxyEndPoint))
                    Task.Factory.StartNew(() => HandleClient(endPoint as TransparentProxyEndPoint, client));
                else
                    Task.Factory.StartNew(() => HandleClient(endPoint as ExplicitProxyEndPoint, client));
            }
            catch
            {
                // ignored
            }
        }


        public static void Stop()
        {
            var SetAsSystemProxy = _proxyEndPoints.OfType<ExplicitProxyEndPoint>().Any(x => x.IsSystemProxy);

            if (SetAsSystemProxy)
            {
                SystemProxyHelper.DisableAllProxy();
#if !DEBUG
                FireFoxHelper.RemoveFirefox();
#endif
            }

            foreach (var endPoint in _proxyEndPoints)
            {
                endPoint.listener.Stop();
            }

            CertManager.Dispose();
        }
    }
}