using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Net.Security;

namespace Titanium.Web.Proxy
{
    /// <summary>
    ///     Proxy Server Main class
    /// </summary>
    public partial class ProxyServer
    {

        static ProxyServer()
        {
            ProxyEndPoints = new List<ProxyEndPoint>();

            //default values
            ConnectionCacheTimeOutMinutes = 3;
            CertificateCacheTimeOutMinutes = 60;
        }

        /// <summary>
        /// Manages certificates used by this proxy
        /// </summary>
        private static CertificateManager CertManager { get; set; }
   
        /// <summary>
        /// Does the root certificate used by this proxy is trusted by the machine?
        /// </summary>
        private static bool certTrusted { get; set; }

        /// <summary>
        /// Is the proxy currently running
        /// </summary>
        private static bool proxyRunning { get; set; }

        /// <summary>
        /// Name of the root certificate issuer
        /// </summary>
        public static string RootCertificateIssuerName { get; set; }

        /// <summary>
        /// Name of the root certificate
        /// </summary>
        public static string RootCertificateName { get; set; }

        /// <summary>
        /// Does this proxy uses the HTTP protocol 100 continue behaviour strictly?
        /// Broken 100 contunue implementations on server/client may cause problems if enabled
        /// </summary>
        public static bool Enable100ContinueBehaviour { get; set; }
       
        /// <summary>
        /// Minutes TCP connection cache to servers to be kept alive when in idle state
        /// </summary>
        public static int ConnectionCacheTimeOutMinutes { get; set; }
       
        /// <summary>
        /// Minutes certificates should be kept in cache when not used
        /// </summary>
        public static int CertificateCacheTimeOutMinutes { get; set; }

        /// <summary>
        /// Intercept request to server
        /// </summary>
        public static event Func<object, SessionEventArgs, Task> BeforeRequest;

        /// <summary>
        /// Intercept response from server
        /// </summary>
        public static event Func<object, SessionEventArgs, Task> BeforeResponse;

        /// <summary>
        /// External proxy for Http
        /// </summary>
        public static ExternalProxy UpStreamHttpProxy { get; set; }

        /// <summary>
        /// External proxy for Http
        /// </summary>
        public static ExternalProxy UpStreamHttpsProxy { get; set; }

        /// <summary>
        /// Verifies the remote Secure Sockets Layer (SSL) certificate used for authentication
        /// </summary>
        public static event Func<object, CertificateValidationEventArgs, Task> ServerCertificateValidationCallback;
       
        /// <summary>
        /// Callback tooverride client certificate during SSL mutual authentication
        /// </summary>
        public static event Func<object, CertificateSelectionEventArgs, Task> ClientCertificateSelectionCallback;

        /// <summary>
        /// A list of IpAddress & port this proxy is listening to
        /// </summary>
        public static List<ProxyEndPoint> ProxyEndPoints { get; set; }

        /// <summary>
        /// Initialize the proxy
        /// </summary>
        public static void Initialize()
        {
            TcpConnectionManager.ClearIdleConnections();
            CertManager.ClearIdleCertificates();
        }

        /// <summary>
        /// Quit the proxy
        /// </summary>
        public static void Quit()
        {
            TcpConnectionManager.StopClearIdleConnections();
            CertManager.StopClearIdleCertificates();
        }

        /// <summary>
        /// Add a proxy end point
        /// </summary>
        /// <param name="endPoint"></param>
        public static void AddEndPoint(ProxyEndPoint endPoint)
        {
            ProxyEndPoints.Add(endPoint);

            if (proxyRunning)
                Listen(endPoint);
        }

        /// <summary>
        /// Remove a proxy end point
        /// Will throw error if the end point does'nt exist 
        /// </summary>
        /// <param name="endPoint"></param>
        public static void RemoveEndPoint(ProxyEndPoint endPoint)
        {

            if (ProxyEndPoints.Contains(endPoint) == false)
                throw new Exception("Cannot remove endPoints not added to proxy");

            ProxyEndPoints.Remove(endPoint);

            if (proxyRunning)
                QuitListen(endPoint);
        }

        /// <summary>
        /// Set the given explicit end point as the default proxy server for current machine
        /// </summary>
        /// <param name="endPoint"></param>
        public static void SetAsSystemHttpProxy(ExplicitProxyEndPoint endPoint)
        {
            ValidateEndPointAsSystemProxy(endPoint);

            //clear any settings previously added
            ProxyEndPoints.OfType<ExplicitProxyEndPoint>().ToList().ForEach(x => x.IsSystemHttpProxy = false);

            SystemProxyHelper.SetHttpProxy(
                Equals(endPoint.IpAddress, IPAddress.Any) | Equals(endPoint.IpAddress, IPAddress.Loopback) ? "127.0.0.1" : endPoint.IpAddress.ToString(), endPoint.Port);

            endPoint.IsSystemHttpProxy = true;
#if !DEBUG
            FireFoxHelper.AddFirefox();
#endif
            Console.WriteLine("Set endpoint at Ip {1} and port: {2} as System HTTPS Proxy", endPoint.GetType().Name, endPoint.IpAddress, endPoint.Port);

        }

        /// <summary>
        /// Remove any HTTP proxy setting of current machien
        /// </summary>
        public static void DisableSystemHttpProxy()
        {
            SystemProxyHelper.RemoveHttpProxy();
        }

        /// <summary>
        /// Set the given explicit end point as the default proxy server for current machine
        /// </summary>
        /// <param name="endPoint"></param>
        public static void SetAsSystemHttpsProxy(ExplicitProxyEndPoint endPoint)
        {
            ValidateEndPointAsSystemProxy(endPoint);

            if (!endPoint.EnableSsl)
            {
                throw new Exception("Endpoint do not support Https connections");
            }

            //clear any settings previously added
            ProxyEndPoints.OfType<ExplicitProxyEndPoint>().ToList().ForEach(x => x.IsSystemHttpsProxy = false);


            //If certificate was trusted by the machine
            if (certTrusted)
            {
                SystemProxyHelper.SetHttpsProxy(
                   Equals(endPoint.IpAddress, IPAddress.Any) | Equals(endPoint.IpAddress, IPAddress.Loopback) ? "127.0.0.1" : endPoint.IpAddress.ToString(),
                    endPoint.Port);
            }

            endPoint.IsSystemHttpsProxy = true;

#if !DEBUG
            FireFoxHelper.AddFirefox();
#endif
            Console.WriteLine("Set endpoint at Ip {1} and port: {2} as System HTTPS Proxy", endPoint.GetType().Name, endPoint.IpAddress, endPoint.Port);
        }

        /// <summary>
        /// Remove any HTTPS proxy setting for current machine
        /// </summary>
        public static void DisableSystemHttpsProxy()
        {
            SystemProxyHelper.RemoveHttpsProxy();
        }

        /// <summary>
        /// Clear all proxy settings for current machine
        /// </summary>
        public static void DisableAllSystemProxies()
        {
            SystemProxyHelper.DisableAllProxy();
        }

        /// <summary>
        /// Start this proxy server
        /// </summary>
        public static void Start()
        {
            if (proxyRunning)
                throw new Exception("Proxy is already running.");

            RootCertificateName = RootCertificateName ?? "Titanium Root Certificate Authority";
            RootCertificateIssuerName = RootCertificateIssuerName ?? "Titanium";

            CertManager = new CertificateManager(RootCertificateIssuerName,
                RootCertificateName);
            
            certTrusted = CertManager.CreateTrustedRootCertificate().Result;

            foreach (var endPoint in ProxyEndPoints)
            {
                Listen(endPoint);
            }

            Initialize();

            proxyRunning = true;
        }

        /// <summary>
        /// Stop this proxy server
        /// </summary>
        public static void Stop()
        {
            if (!proxyRunning)
                throw new Exception("Proxy is not running.");

            var setAsSystemProxy = ProxyEndPoints.OfType<ExplicitProxyEndPoint>().Any(x => x.IsSystemHttpProxy || x.IsSystemHttpsProxy);

            if (setAsSystemProxy)
            {
                SystemProxyHelper.DisableAllProxy();
#if !DEBUG
                FireFoxHelper.RemoveFirefox();
#endif
            }

            foreach (var endPoint in ProxyEndPoints)
            {
                endPoint.listener.Stop();
            }

            CertManager.Dispose();

            Quit();

            proxyRunning = false;
        }

        /// <summary>
        /// Listen on the given end point on local machine
        /// </summary>
        /// <param name="endPoint"></param>
        private static void Listen(ProxyEndPoint endPoint)
        {
            endPoint.listener = new TcpListener(endPoint.IpAddress, endPoint.Port);
            endPoint.listener.Start();

            endPoint.Port = ((IPEndPoint)endPoint.listener.LocalEndpoint).Port;
            // accept clients asynchronously
            endPoint.listener.BeginAcceptTcpClient(OnAcceptConnection, endPoint);
        }

        /// <summary>
        /// Quit listening on the given end point
        /// </summary>
        /// <param name="endPoint"></param>
        private static void QuitListen(ProxyEndPoint endPoint)
        {
            endPoint.listener.Stop();
        }

        /// <summary>
        /// Verifiy if its safe to set this end point as System proxy
        /// </summary>
        /// <param name="endPoint"></param>
        private static void ValidateEndPointAsSystemProxy(ExplicitProxyEndPoint endPoint)
        {
            if (ProxyEndPoints.Contains(endPoint) == false)
                throw new Exception("Cannot set endPoints not added to proxy as system proxy");

            if (!proxyRunning)
                throw new Exception("Cannot set system proxy settings before proxy has been started.");
        }

        /// <summary>
        /// When a connection is received from client act
        /// </summary>
        /// <param name="asyn"></param>
        private static void OnAcceptConnection(IAsyncResult asyn)
        {
            var endPoint = (ProxyEndPoint)asyn.AsyncState;

            try
            {
                //based on end point type call appropriate request handlers
                var client = endPoint.listener.EndAcceptTcpClient(asyn);
                if (endPoint.GetType() == typeof(TransparentProxyEndPoint))
                    HandleClient(endPoint as TransparentProxyEndPoint, client);
                else
                    HandleClient(endPoint as ExplicitProxyEndPoint, client);

                // Get the listener that handles the client request.
                endPoint.listener.BeginAcceptTcpClient(OnAcceptConnection, endPoint);
            }
            catch (ObjectDisposedException)
            {
                // The listener was Stop()'d, disposing the underlying socket and
                // triggering the completion of the callback. We're already exiting,
                // so just return.
                return;
            }
            catch
            {
                //Other errors are discarded to keep proxy running
            }
           
        }
      
    }
}