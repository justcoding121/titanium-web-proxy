using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Helpers.WinHttp;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network;
using Titanium.Web.Proxy.Network.Tcp;
using Titanium.Web.Proxy.Network.WinAuth.Security;

namespace Titanium.Web.Proxy
{
    /// <summary>
    ///     Proxy Server Main class
    /// </summary>
    public partial class ProxyServer : IDisposable
    {
        internal static readonly string UriSchemeHttp = Uri.UriSchemeHttp;
        internal static readonly string UriSchemeHttps = Uri.UriSchemeHttps;

        /// <summary>
        /// Backing field for corresponding public property
        /// </summary>
        private int clientConnectionCount;

        /// <summary>
        /// Backing field for corresponding public property
        /// </summary>
        private int serverConnectionCount;

        /// <summary>
        /// A object that creates tcp connection to server
        /// </summary>
        private TcpConnectionFactory tcpConnectionFactory { get; }

        /// <summary>
        /// Manaage upstream proxy detection
        /// </summary>
        private WinHttpWebProxyFinder systemProxyResolver;

        /// <summary>
        /// Manage system proxy settings
        /// </summary>
        private SystemProxyManager systemProxySettingsManager { get; }

        /// <summary>
        /// An default exception log func
        /// </summary>
        private readonly Lazy<ExceptionHandler> defaultExceptionFunc = new Lazy<ExceptionHandler>(() => (e => { }));

        /// <summary>
        /// backing exception func for exposed public property
        /// </summary>
        private ExceptionHandler exceptionFunc;

        /// <summary>
        /// Is the proxy currently running
        /// </summary>
        public bool ProxyRunning { get; private set; }

        /// <summary>
        /// Gets or sets a value indicating whether requests will be chained to upstream gateway.
        /// </summary>
        public bool ForwardToUpstreamGateway { get; set; }

        /// <summary>
        /// Enable disable Windows Authentication (NTLM/Kerberos)
        /// Note: NTLM/Kerberos will always send local credentials of current user
        /// who is running the proxy process. This is because a man
        /// in middle attack is not currently supported
        /// (which would require windows delegation enabled for this server process)
        /// </summary>
        public bool EnableWinAuth { get; set; }

        /// <summary>
        /// Should we check for certificare revocation during SSL authentication to servers
        /// Note: If enabled can reduce performance (Default disabled)
        /// </summary>
        public bool CheckCertificateRevocation { get; set; }

        /// <summary>
        /// Does this proxy uses the HTTP protocol 100 continue behaviour strictly?
        /// Broken 100 contunue implementations on server/client may cause problems if enabled
        /// </summary>
        public bool Enable100ContinueBehaviour { get; set; }

        /// <summary>
        /// Buffer size used throughout this proxy
        /// </summary>
        public int BufferSize { get; set; } = 8192;

        /// <summary>
        /// Seconds client/server connection are to be kept alive when waiting for read/write to complete
        /// </summary>
        public int ConnectionTimeOutSeconds { get; set; }

        /// <summary>
        /// Total number of active client connections
        /// </summary>
        public int ClientConnectionCount => clientConnectionCount;

        /// <summary>
        /// Total number of active server connections
        /// </summary>
        public int ServerConnectionCount => serverConnectionCount;

        /// <summary>
        /// Realm used during Proxy Basic Authentication 
        /// </summary>
        public string ProxyRealm { get; set; } = "TitaniumProxy";


        /// <summary>
        /// List of supported Ssl versions
        /// </summary>
        public SslProtocols SupportedSslProtocols { get; set; } =
#if NET45
            SslProtocols.Ssl3 |
#endif
            SslProtocols.Tls | SslProtocols.Tls11 | SslProtocols.Tls12;


        /// <summary>
        /// Manages certificates used by this proxy
        /// </summary>
        public CertificateManager CertificateManager { get; }

        /// <summary>
        /// External proxy for Http
        /// </summary>
        public ExternalProxy UpStreamHttpProxy { get; set; }

        /// <summary>
        /// External proxy for Http
        /// </summary>
        public ExternalProxy UpStreamHttpsProxy { get; set; }

        /// <summary>
        /// Local adapter/NIC endpoint (where proxy makes request via)
        /// default via any IP addresses of this machine
        /// </summary>
        public IPEndPoint UpStreamEndPoint { get; set; } = new IPEndPoint(IPAddress.Any, 0);

        /// <summary>
        /// A list of IpAddress and port this proxy is listening to
        /// </summary>
        public List<ProxyEndPoint> ProxyEndPoints { get; set; }

        /// <summary>
        /// Occurs when client connection count changed.
        /// </summary>
        public event EventHandler ClientConnectionCountChanged;

        /// <summary>
        /// Occurs when server connection count changed.
        /// </summary>
        public event EventHandler ServerConnectionCountChanged;

        /// <summary>
        /// Verifies the remote Secure Sockets Layer (SSL) certificate used for authentication
        /// </summary>
        public event AsyncEventHandler<CertificateValidationEventArgs> ServerCertificateValidationCallback;

        /// <summary>
        /// Callback tooverride client certificate during SSL mutual authentication
        /// </summary>
        public event AsyncEventHandler<CertificateSelectionEventArgs> ClientCertificateSelectionCallback;

        /// <summary>
        /// A callback to provide authentication credentials for up stream proxy this proxy is using for HTTP(S) requests
        /// return the ExternalProxy object with valid credentials
        /// </summary>
        public Func<SessionEventArgs, Task<ExternalProxy>> GetCustomUpStreamProxyFunc { get; set; }

        /// <summary>
        /// Intercept request to server
        /// </summary>
        public event AsyncEventHandler<SessionEventArgs> BeforeRequest;

        /// <summary>
        /// Intercept response from server
        /// </summary>
        public event AsyncEventHandler<SessionEventArgs> BeforeResponse;

        /// <summary>
        /// Intercept after response from server
        /// </summary>
        public event AsyncEventHandler<SessionEventArgs> AfterResponse;

        /// <summary>
        /// Callback for error events in proxy
        /// </summary>
        public ExceptionHandler ExceptionFunc
        {
            get => exceptionFunc ?? defaultExceptionFunc.Value;
            set => exceptionFunc = value;
        }

        /// <summary>
        /// A callback to authenticate clients 
        /// Parameters are username, password provided by client
        /// return true for successful authentication
        /// </summary>
        public Func<string, string, Task<bool>> AuthenticateUserFunc { get; set; }

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="userTrustRootCertificate"></param>
        /// <param name="machineTrustRootCertificate">Note:setting machineTrustRootCertificate to true will force userTrustRootCertificate to true</param>
        /// <param name="trustRootCertificateAsAdmin"></param>
        public ProxyServer(bool userTrustRootCertificate = true, bool machineTrustRootCertificate = false, bool trustRootCertificateAsAdmin = false) : this(null, null, userTrustRootCertificate, machineTrustRootCertificate, trustRootCertificateAsAdmin)
        {
        }

        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="rootCertificateName">Name of root certificate.</param>
        /// <param name="rootCertificateIssuerName">Name of root certificate issuer.</param>
        /// <param name="userTrustRootCertificate"></param>
        /// <param name="machineTrustRootCertificate">Note:setting machineTrustRootCertificate to true will force userTrustRootCertificate to true</param>
        /// <param name="trustRootCertificateAsAdmin"></param>
        public ProxyServer(string rootCertificateName, string rootCertificateIssuerName, bool userTrustRootCertificate = true, bool machineTrustRootCertificate = false, bool trustRootCertificateAsAdmin = false)
        {
            //default values
            ConnectionTimeOutSeconds = 30;

            ProxyEndPoints = new List<ProxyEndPoint>();
            tcpConnectionFactory = new TcpConnectionFactory();
            if (!RunTime.IsRunningOnMono && RunTime.IsWindows)
            {
                systemProxySettingsManager = new SystemProxyManager();
            }

            CertificateManager = new CertificateManager(rootCertificateName, rootCertificateIssuerName, userTrustRootCertificate, machineTrustRootCertificate, trustRootCertificateAsAdmin, ExceptionFunc);
        }

        /// <summary>
        /// Add a proxy end point
        /// </summary>
        /// <param name="endPoint"></param>
        public void AddEndPoint(ProxyEndPoint endPoint)
        {
            if (ProxyEndPoints.Any(x => x.IpAddress.Equals(endPoint.IpAddress) && endPoint.Port != 0 && x.Port == endPoint.Port))
            {
                throw new Exception("Cannot add another endpoint to same port & ip address");
            }

            ProxyEndPoints.Add(endPoint);

            if (ProxyRunning)
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

            if (ProxyRunning)
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
            SetAsSystemProxy(endPoint, ProxyProtocolType.Http);
        }

        /// <summary>
        /// Set the given explicit end point as the default proxy server for current machine
        /// </summary>
        /// <param name="endPoint"></param>
        public void SetAsSystemHttpsProxy(ExplicitProxyEndPoint endPoint)
        {
            SetAsSystemProxy(endPoint, ProxyProtocolType.Https);
        }

        /// <summary>
        /// Set the given explicit end point as the default proxy server for current machine
        /// </summary>
        /// <param name="endPoint"></param>
        /// <param name="protocolType"></param>
        public void SetAsSystemProxy(ExplicitProxyEndPoint endPoint, ProxyProtocolType protocolType)
        {
            if (RunTime.IsRunningOnMono)
            {
                throw new Exception("Mono Runtime do not support system proxy settings.");
            }

            ValidateEndPointAsSystemProxy(endPoint);

            bool isHttp = (protocolType & ProxyProtocolType.Http) > 0;
            bool isHttps = (protocolType & ProxyProtocolType.Https) > 0;

            if (isHttps)
            {
                if (!endPoint.EnableSsl)
                {
                    throw new Exception("Endpoint do not support Https connections");
                }

                CertificateManager.EnsureRootCertificate();

                //If certificate was trusted by the machine
                if (!CertificateManager.CertValidated)
                {
                    protocolType = protocolType & ~ProxyProtocolType.Https;
                    isHttps = false;
                }
            }

            //clear any settings previously added
            if (isHttp)
            {
                ProxyEndPoints.OfType<ExplicitProxyEndPoint>().ToList().ForEach(x => x.IsSystemHttpProxy = false);
            }

            if (isHttps)
            {
                ProxyEndPoints.OfType<ExplicitProxyEndPoint>().ToList().ForEach(x => x.IsSystemHttpsProxy = false);
            }

            systemProxySettingsManager.SetProxy(
                Equals(endPoint.IpAddress, IPAddress.Any) |
                Equals(endPoint.IpAddress, IPAddress.Loopback)
                    ? "127.0.0.1"
                    : endPoint.IpAddress.ToString(),
                endPoint.Port,
                protocolType);

            if (isHttp)
            {
                endPoint.IsSystemHttpProxy = true;
            }

            if (isHttps)
            {
                endPoint.IsSystemHttpsProxy = true;
            }

            string proxyType = null;
            switch (protocolType)
            {
                case ProxyProtocolType.Http:
                    proxyType = "HTTP";
                    break;
                case ProxyProtocolType.Https:
                    proxyType = "HTTPS";
                    break;
                case ProxyProtocolType.AllHttp:
                    proxyType = "HTTP and HTTPS";
                    break;
            }

            if (protocolType != ProxyProtocolType.None)
            {
                Console.WriteLine("Set endpoint at Ip {0} and port: {1} as System {2} Proxy", endPoint.IpAddress, endPoint.Port, proxyType);
            }
        }

        /// <summary>
        /// Remove any HTTP proxy setting of current machien
        /// </summary>
        public void DisableSystemHttpProxy()
        {
            DisableSystemProxy(ProxyProtocolType.Http);
        }

        /// <summary>
        /// Remove any HTTPS proxy setting for current machine
        /// </summary>
        public void DisableSystemHttpsProxy()
        {
            DisableSystemProxy(ProxyProtocolType.Https);
        }

        /// <summary>
        /// Remove the specified proxy settings for current machine
        /// </summary>
        public void DisableSystemProxy(ProxyProtocolType protocolType)
        {
            if (RunTime.IsRunningOnMono)
            {
                throw new Exception("Mono Runtime do not support system proxy settings.");
            }

            systemProxySettingsManager.RemoveProxy(protocolType);
        }

        /// <summary>
        /// Clear all proxy settings for current machine
        /// </summary>
        public void DisableAllSystemProxies()
        {
            if (RunTime.IsRunningOnMono)
            {
                throw new Exception("Mono Runtime do not support system proxy settings.");
            }

            systemProxySettingsManager.DisableAllProxy();
        }

        /// <summary>
        /// Start this proxy server
        /// </summary>
        public void Start()
        {
            if (ProxyRunning)
            {
                throw new Exception("Proxy is already running.");
            }

            if (ProxyEndPoints.OfType<ExplicitProxyEndPoint>().Any(x => x.GenericCertificate == null))
            {
                CertificateManager.EnsureRootCertificate();
            }

            //clear any system proxy settings which is pointing to our own endpoint (causing a cycle)
            //due to non gracious proxy shutdown before or something else
            if (systemProxySettingsManager != null && RunTime.IsWindows)
            {
                var proxyInfo = systemProxySettingsManager.GetProxyInfoFromRegistry();
                if (proxyInfo.Proxies != null)
                {
                    var protocolToRemove = ProxyProtocolType.None;
                    foreach (var proxy in proxyInfo.Proxies.Values)
                    {
                        if ((proxy.HostName == "127.0.0.1"
                            || proxy.HostName.Equals("localhost", StringComparison.OrdinalIgnoreCase))
                            && ProxyEndPoints.Any(x => x.Port == proxy.Port))
                        {
                            protocolToRemove |= proxy.ProtocolType;
                        }
                    }

                    if (protocolToRemove != ProxyProtocolType.None)
                    {
                        //do not restore to any of listening address when we quit
                        systemProxySettingsManager.RemoveProxy(protocolToRemove, false);
                    }
                }
            }

            if (ForwardToUpstreamGateway && GetCustomUpStreamProxyFunc == null && systemProxySettingsManager != null)
            {
                // Use WinHttp to handle PAC/WAPD scripts.
                systemProxyResolver = new WinHttpWebProxyFinder();
                systemProxyResolver.LoadFromIE();

                GetCustomUpStreamProxyFunc = GetSystemUpStreamProxy;
            }

            ProxyRunning = true;

            foreach (var endPoint in ProxyEndPoints)
            {
                Listen(endPoint);
            }

            CertificateManager.ClearIdleCertificates();

            if (RunTime.IsWindows && !RunTime.IsRunningOnMono)
            {
                //clear orphaned windows auth states every 2 minutes
                WinAuthEndPoint.ClearIdleStates(2);
            }
        }

        /// <summary>
        /// Stop this proxy server
        /// </summary>
        public void Stop()
        {
            if (!ProxyRunning)
            {
                throw new Exception("Proxy is not running.");
            }

            if (!RunTime.IsRunningOnMono && RunTime.IsWindows)
            {
                bool setAsSystemProxy = ProxyEndPoints.OfType<ExplicitProxyEndPoint>().Any(x => x.IsSystemHttpProxy || x.IsSystemHttpsProxy);

                if (setAsSystemProxy)
                {
                    systemProxySettingsManager.RestoreOriginalSettings();
                }
            }

            foreach (var endPoint in ProxyEndPoints)
            {
                QuitListen(endPoint);
            }

            ProxyEndPoints.Clear();

            CertificateManager?.StopClearIdleCertificates();

            ProxyRunning = false;
        }

        /// <summary>
        /// Dispose Proxy.
        /// </summary>
        public void Dispose()
        {
            if (ProxyRunning)
            {
                Stop();
            }

            CertificateManager?.Dispose();
        }

        /// <summary>
        /// Listen on the given end point on local machine
        /// </summary>
        /// <param name="endPoint"></param>
        private void Listen(ProxyEndPoint endPoint)
        {
            endPoint.Listener = new TcpListener(endPoint.IpAddress, endPoint.Port);
            try
            {
                endPoint.Listener.Start();

                endPoint.Port = ((IPEndPoint)endPoint.Listener.LocalEndpoint).Port;

                // accept clients asynchronously
                endPoint.Listener.BeginAcceptTcpClient(OnAcceptConnection, endPoint);
            }
            catch (SocketException ex)
            {
                var pex = new Exception($"Endpoint {endPoint} failed to start. Check inner exception and exception data for details.", ex);
                pex.Data.Add("ipAddress", endPoint.IpAddress);
                pex.Data.Add("port", endPoint.Port);
                throw pex;
            }
        }

        /// <summary>
        /// Verifiy if its safe to set this end point as System proxy
        /// </summary>
        /// <param name="endPoint"></param>
        private void ValidateEndPointAsSystemProxy(ExplicitProxyEndPoint endPoint)
        {
            if (endPoint == null)
                throw new ArgumentNullException(nameof(endPoint));
            if (ProxyEndPoints.Contains(endPoint) == false)
            {
                throw new Exception("Cannot set endPoints not added to proxy as system proxy");
            }

            if (!ProxyRunning)
            {
                throw new Exception("Cannot set system proxy settings before proxy has been started.");
            }
        }

        /// <summary>
        /// Gets the system up stream proxy.
        /// </summary>
        /// <param name="sessionEventArgs">The <see cref="SessionEventArgs"/> instance containing the event data.</param>
        /// <returns><see cref="ExternalProxy"/> instance containing valid proxy configuration from PAC/WAPD scripts if any exists.</returns>
        private Task<ExternalProxy> GetSystemUpStreamProxy(SessionEventArgs sessionEventArgs)
        {
            var proxy = systemProxyResolver.GetProxy(sessionEventArgs.WebSession.Request.RequestUri);
            return Task.FromResult(proxy);
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
                tcpClient = endPoint.Listener.EndAcceptTcpClient(asyn);
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
                    await HandleClient(tcpClient, endPoint);
                });
            }

            // Get the listener that handles the client request.
            endPoint.Listener.BeginAcceptTcpClient(OnAcceptConnection, endPoint);
        }

        private async Task HandleClient(TcpClient tcpClient, ProxyEndPoint endPoint)
        {
            UpdateClientConnectionCount(true);

            tcpClient.ReceiveTimeout = ConnectionTimeOutSeconds * 1000;
            tcpClient.SendTimeout = ConnectionTimeOutSeconds * 1000;

            try
            {
                if (endPoint is TransparentProxyEndPoint tep)
                {
                    await HandleClient(tep, tcpClient);
                }
                else
                {
                    await HandleClient((ExplicitProxyEndPoint)endPoint, tcpClient);
                }
            }
            finally
            {
                UpdateClientConnectionCount(false);
                tcpClient.CloseSocket();
            }
        }

        /// <summary>
        /// Quit listening on the given end point
        /// </summary>
        /// <param name="endPoint"></param>
        private void QuitListen(ProxyEndPoint endPoint)
        {
            endPoint.Listener.Stop();
            endPoint.Listener.Server.Dispose();
        }


        internal void UpdateClientConnectionCount(bool increment)
        {
            if (increment)
            {
                Interlocked.Increment(ref clientConnectionCount);
            }
            else
            {
                Interlocked.Decrement(ref clientConnectionCount);
            }

            ClientConnectionCountChanged?.Invoke(this, EventArgs.Empty);
        }

        internal void UpdateServerConnectionCount(bool increment)
        {
            if (increment)
            {
                Interlocked.Increment(ref serverConnectionCount);
            }
            else
            {
                Interlocked.Decrement(ref serverConnectionCount);
            }

            ServerConnectionCountChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
