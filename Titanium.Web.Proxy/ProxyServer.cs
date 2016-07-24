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
using System.Security.Authentication;

namespace Titanium.Web.Proxy
{
    /// <summary>
    ///     Proxy Server Main class
    /// </summary>
    public partial class ProxyServer : IDisposable
    {
        /// <summary>
        /// Does the root certificate used by this proxy is trusted by the machine?
        /// </summary>
        private bool certTrusted { get; set; }

        /// <summary>
        /// Is the proxy currently running
        /// </summary>
        private bool proxyRunning { get; set; }

        /// <summary>
        /// Manages certificates used by this proxy
        /// </summary>
        private CertificateManager certificateCacheManager { get; set; }

        /// <summary>
        /// A object that creates tcp connection to server
        /// </summary>
        private TcpConnectionFactory tcpConnectionFactory { get; set; }

        /// <summary>
        /// Manage system proxy settings
        /// </summary>
        private SystemProxyManager systemProxySettingsManager { get; set; }

        private FireFoxProxySettingsManager firefoxProxySettingsManager { get; set; }

        /// <summary>
        /// Buffer size used throughout this proxy
        /// </summary>
        public int BUFFER_SIZE { get; set; } = 8192;

        /// <summary>
        /// Name of the root certificate issuer
        /// </summary>
        public string RootCertificateIssuerName { get; set; }

        /// <summary>
        /// Name of the root certificate
        /// </summary>
        public string RootCertificateName { get; set; }

        /// <summary>
        /// Does this proxy uses the HTTP protocol 100 continue behaviour strictly?
        /// Broken 100 contunue implementations on server/client may cause problems if enabled
        /// </summary>
        public bool Enable100ContinueBehaviour { get; set; }

        /// <summary>
        /// Minutes certificates should be kept in cache when not used
        /// </summary>
        public int CertificateCacheTimeOutMinutes { get; set; }

        /// <summary>
        /// Seconds client/server connection are to be kept alive when waiting for read/write to complete
        /// </summary>
        public int ConnectionTimeOutSeconds { get; set; }

        /// <summary>
        /// Intercept request to server
        /// </summary>
        public event Func<object, SessionEventArgs, Task> BeforeRequest;

        /// <summary>
        /// Intercept response from server
        /// </summary>
        public event Func<object, SessionEventArgs, Task> BeforeResponse;

        /// <summary>
        /// External proxy for Http
        /// </summary>
        public ExternalProxy ExternalHttpProxy { get; set; }

        /// <summary>
        /// External proxy for Https
        /// </summary>
        public ExternalProxy ExternalHttpsProxy { get; set; }

        /// <summary>
        /// Verifies the remote Secure Sockets Layer (SSL) certificate used for authentication
        /// </summary>
        public event Func<object, CertificateValidationEventArgs, Task> ServerCertificateValidationCallback;

        /// <summary>
        /// Callback tooverride client certificate during SSL mutual authentication
        /// </summary>
        public event Func<object, CertificateSelectionEventArgs, Task> ClientCertificateSelectionCallback;

        /// <summary>
        /// A list of IpAddress & port this proxy is listening to
        /// </summary>
        public List<ProxyEndPoint> ProxyEndPoints { get; set; }

        /// <summary>
        /// List of supported Ssl versions
        /// </summary>
        public SslProtocols SupportedSslProtocols { get; set; } = SslProtocols.Tls | SslProtocols.Tls11 | SslProtocols.Tls12 | SslProtocols.Ssl3;

        /// <summary>
        /// Constructor
        /// </summary>
        public ProxyServer()
        {
            //default values
            ConnectionTimeOutSeconds = 120;
            CertificateCacheTimeOutMinutes = 60;

            ProxyEndPoints = new List<ProxyEndPoint>();
            tcpConnectionFactory = new TcpConnectionFactory();
            systemProxySettingsManager = new SystemProxyManager();
            firefoxProxySettingsManager = new FireFoxProxySettingsManager();

            RootCertificateName = RootCertificateName ?? "Titanium Root Certificate Authority";
            RootCertificateIssuerName = RootCertificateIssuerName ?? "Titanium";

            certificateCacheManager = new CertificateManager(RootCertificateIssuerName,
                RootCertificateName);
        }

        /// <summary>
        /// Add a proxy end point
        /// </summary>
        /// <param name="endPoint"></param>
        public void AddEndPoint(ProxyEndPoint endPoint)
        {
            if (ProxyEndPoints.Any(x => x.IpAddress == endPoint.IpAddress && x.Port == endPoint.Port))
            {
                throw new Exception("Cannot add another endpoint to same port & ip address");
            }

            ProxyEndPoints.Add(endPoint);

            if (proxyRunning)
            {
                Listen(endPoint);
            }
        }

        /// <summary>
        /// Remove a proxy end point
        /// Will throw error if the end point does'nt exist 
        /// </summary>
        /// <param name="endPoint"></param>
        public void RemoveEndPoint(ProxyEndPoint endPoint)
        {
            if (ProxyEndPoints.Contains(endPoint) == false)
            {
                throw new Exception("Cannot remove endPoints not added to proxy");
            }

            ProxyEndPoints.Remove(endPoint);

            if (proxyRunning)
            {
                QuitListen(endPoint);
            }
        }

        /// <summary>
        /// Set the given explicit end point as the default proxy server for current machine
        /// </summary>
        /// <param name="endPoint"></param>
        public void SetAsSystemHttpProxy(ExplicitProxyEndPoint endPoint)
        {
            ValidateEndPointAsSystemProxy(endPoint);

            //clear any settings previously added
            ProxyEndPoints.OfType<ExplicitProxyEndPoint>().ToList().ForEach(x => x.IsSystemHttpProxy = false);

            systemProxySettingsManager.SetHttpProxy(
                Equals(endPoint.IpAddress, IPAddress.Any) | Equals(endPoint.IpAddress, IPAddress.Loopback) ? "127.0.0.1" : endPoint.IpAddress.ToString(), endPoint.Port);

            endPoint.IsSystemHttpProxy = true;
#if !DEBUG
            firefoxProxySettingsManager.AddFirefox();
#endif
            Console.WriteLine("Set endpoint at Ip {1} and port: {2} as System HTTP Proxy", endPoint.GetType().Name, endPoint.IpAddress, endPoint.Port);

        }


        /// <summary>
        /// Set the given explicit end point as the default proxy server for current machine
        /// </summary>
        /// <param name="endPoint"></param>
        public void SetAsSystemHttpsProxy(ExplicitProxyEndPoint endPoint)
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
                systemProxySettingsManager.SetHttpsProxy(
                   Equals(endPoint.IpAddress, IPAddress.Any) | Equals(endPoint.IpAddress, IPAddress.Loopback) ? "127.0.0.1" : endPoint.IpAddress.ToString(),
                    endPoint.Port);
            }

            endPoint.IsSystemHttpsProxy = true;

#if !DEBUG
            firefoxProxySettingsManager.AddFirefox();
#endif
            Console.WriteLine("Set endpoint at Ip {1} and port: {2} as System HTTPS Proxy", endPoint.GetType().Name, endPoint.IpAddress, endPoint.Port);
        }

        /// <summary>
        /// Remove any HTTP proxy setting of current machien
        /// </summary>
        public void DisableSystemHttpProxy()
        {
            systemProxySettingsManager.RemoveHttpProxy();
        }

        /// <summary>
        /// Remove any HTTPS proxy setting for current machine
        /// </summary>
        public void DisableSystemHttpsProxy()
        {
            systemProxySettingsManager.RemoveHttpsProxy();
        }

        /// <summary>
        /// Clear all proxy settings for current machine
        /// </summary>
        public void DisableAllSystemProxies()
        {
            systemProxySettingsManager.DisableAllProxy();
        }

        /// <summary>
        /// Start this proxy server
        /// </summary>
        public void Start()
        {
            if (proxyRunning)
            {
                throw new Exception("Proxy is already running.");
            }

            certTrusted = certificateCacheManager.CreateTrustedRootCertificate().Result;

            foreach (var endPoint in ProxyEndPoints)
            {
                Listen(endPoint);
            }

            certificateCacheManager.ClearIdleCertificates(CertificateCacheTimeOutMinutes);

            proxyRunning = true;
        }

        /// <summary>
        /// Stop this proxy server
        /// </summary>
        public void Stop()
        {
            if (!proxyRunning)
            {
                throw new Exception("Proxy is not running.");
            }

            var setAsSystemProxy = ProxyEndPoints.OfType<ExplicitProxyEndPoint>().Any(x => x.IsSystemHttpProxy || x.IsSystemHttpsProxy);

            if (setAsSystemProxy)
            {
                systemProxySettingsManager.DisableAllProxy();
#if !DEBUG
                firefoxProxySettingsManager.RemoveFirefox();
#endif
            }

            foreach (var endPoint in ProxyEndPoints)
            {
                QuitListen(endPoint);
            }

            ProxyEndPoints.Clear();

            certificateCacheManager.StopClearIdleCertificates();

            proxyRunning = false;
        }

        /// <summary>
        /// Listen on the given end point on local machine
        /// </summary>
        /// <param name="endPoint"></param>
        private void Listen(ProxyEndPoint endPoint)
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
        private void QuitListen(ProxyEndPoint endPoint)
        {
            endPoint.listener.Stop();
            endPoint.listener.Server.Close();
            endPoint.listener.Server.Dispose();
        }

        /// <summary>
        /// Verifiy if its safe to set this end point as System proxy
        /// </summary>
        /// <param name="endPoint"></param>
        private void ValidateEndPointAsSystemProxy(ExplicitProxyEndPoint endPoint)
        {
            if (ProxyEndPoints.Contains(endPoint) == false)
            {
                throw new Exception("Cannot set endPoints not added to proxy as system proxy");
            }

            if (!proxyRunning)
            {
                throw new Exception("Cannot set system proxy settings before proxy has been started.");
            }
        }

        /// <summary>
        /// When a connection is received from client act
        /// </summary>
        /// <param name="asyn"></param>
        private void OnAcceptConnection(IAsyncResult asyn)
        {
            var endPoint = (ProxyEndPoint)asyn.AsyncState;

            TcpClient tcpClient = null;

            try
            {
                //based on end point type call appropriate request handlers
                tcpClient = endPoint.listener.EndAcceptTcpClient(asyn);
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

            if (tcpClient != null)
            {               
                Task.Run(async () =>
                {
                    try
                    {      
                        if (endPoint.GetType() == typeof(TransparentProxyEndPoint))
                        {
                            await HandleClient(endPoint as TransparentProxyEndPoint, tcpClient);
                        }
                        else
                        {
                            await HandleClient(endPoint as ExplicitProxyEndPoint, tcpClient);
                        }
                     
                    }
                    finally
                    {
                        if (tcpClient != null)
                        {
                            tcpClient.LingerState = new LingerOption(true, 0);
                            tcpClient.Client.Shutdown(SocketShutdown.Both);
                            tcpClient.Client.Close();
                            tcpClient.Client.Dispose();

                            tcpClient.Close();
                        }
                    }
                });
            }

            // Get the listener that handles the client request.
            endPoint.listener.BeginAcceptTcpClient(OnAcceptConnection, endPoint);
        }

        public void Dispose()
        {
            if (proxyRunning)
            {
                Stop();
            }

            certificateCacheManager.Dispose();
        }
    }
}