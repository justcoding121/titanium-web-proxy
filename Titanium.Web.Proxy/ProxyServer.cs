using StreamExtended.Network;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Helpers;
#if NET45
using Titanium.Web.Proxy.Helpers.WinHttp;
#endif
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network;
using Titanium.Web.Proxy.Network.Tcp;
#if NET45
using Titanium.Web.Proxy.Network.WinAuth.Security;
#endif

namespace Titanium.Web.Proxy
{
    /// <summary>
    ///     Proxy Server Main class
    /// </summary>
    public partial class ProxyServer : IDisposable
    {
#if NET45
        internal static readonly string UriSchemeHttp = Uri.UriSchemeHttp;
        internal static readonly string UriSchemeHttps = Uri.UriSchemeHttps;
#else
        internal const string UriSchemeHttp = "http";
        internal const string UriSchemeHttps = "https";
#endif

        /// <summary>
        /// Is the proxy currently running
        /// </summary>
        private bool proxyRunning { get; set; }

        /// <summary>
        /// An default exception log func
        /// </summary>
        private readonly Lazy<Action<Exception>> defaultExceptionFunc = new Lazy<Action<Exception>>(() => (e => { }));

        /// <summary>
        /// backing exception func for exposed public property
        /// </summary>
        private Action<Exception> exceptionFunc;

        /// <summary>
        /// Backing field for corresponding public property
        /// </summary>
        private bool trustRootCertificate;

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

#if NET45
        private WinHttpWebProxyFinder systemProxyResolver;

        /// <summary>
        /// Manage system proxy settings
        /// </summary>
        private SystemProxyManager systemProxySettingsManager { get; }

        /// <summary>
        /// Set firefox to use default system proxy
        /// </summary>
        private readonly FireFoxProxySettingsManager firefoxProxySettingsManager = new FireFoxProxySettingsManager();
#endif

        /// <summary>
        /// Buffer size used throughout this proxy
        /// </summary>
        public int BufferSize { get; set; } = 8192;

        /// <summary>
        /// Manages certificates used by this proxy
        /// </summary>
        public CertificateManager CertificateManager { get; }

        /// <summary>
        /// The root certificate
        /// </summary>
        public X509Certificate2 RootCertificate
        {
            get { return CertificateManager.RootCertificate; }
            set { CertificateManager.RootCertificate = value; }
        }

        /// <summary>
        /// Name of the root certificate issuer 
        /// (This is valid only when RootCertificate property is not set)
        /// </summary>
        public string RootCertificateIssuerName
        {
            get { return CertificateManager.Issuer; }
            set { CertificateManager.Issuer = value; }
        }

        /// <summary>
        /// Name of the root certificate
        /// (This is valid only when RootCertificate property is not set)
        /// If no certificate is provided then a default Root Certificate will be created and used
        /// The provided root certificate will be stored in proxy exe directory with the private key 
        /// Root certificate file will be named as "rootCert.pfx"
        /// </summary>
        public string RootCertificateName
        {
            get { return CertificateManager.RootCertificateName; }
            set { CertificateManager.RootCertificateName = value; }
        }

        /// <summary>
        /// Trust the RootCertificate used by this proxy server
        /// Note that this do not make the client trust the certificate!
        /// This would import the root certificate to the certificate store of machine that runs this proxy server
        /// </summary>
        public bool TrustRootCertificate
        {
            get { return trustRootCertificate; }
            set
            {
                trustRootCertificate = value;
                if (value)
                {
                    EnsureRootCertificate();
                }
            }
        }

        /// <summary>
        /// Select Certificate Engine 
        /// Optionally set to BouncyCastle
        /// Mono only support BouncyCastle and it is the default
        /// </summary>
        public CertificateEngine CertificateEngine
        {
            get { return CertificateManager.Engine; }
            set { CertificateManager.Engine = value; }
        }

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
        /// Intercept tunnel connect reques
        /// </summary>
        public event Func<object, TunnelConnectSessionEventArgs, Task> TunnelConnectRequest;

        /// <summary>
        /// Intercept tunnel connect response
        /// </summary>
        public event Func<object, TunnelConnectSessionEventArgs, Task> TunnelConnectResponse;

        /// <summary>
        /// Occurs when client connection count changed.
        /// </summary>
        public event EventHandler ClientConnectionCountChanged;

        /// <summary>
        /// Occurs when server connection count changed.
        /// </summary>
        public event EventHandler ServerConnectionCountChanged;

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
        /// Is the proxy currently running
        /// </summary>
        public bool ProxyRunning => proxyRunning;

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
        /// Verifies the remote Secure Sockets Layer (SSL) certificate used for authentication
        /// </summary>
        public event Func<object, CertificateValidationEventArgs, Task> ServerCertificateValidationCallback;

        /// <summary>
        /// Callback tooverride client certificate during SSL mutual authentication
        /// </summary>
        public event Func<object, CertificateSelectionEventArgs, Task> ClientCertificateSelectionCallback;

        /// <summary>
        /// Callback for error events in proxy
        /// </summary>
        public Action<Exception> ExceptionFunc
        {
            get { return exceptionFunc ?? defaultExceptionFunc.Value; }
            set { exceptionFunc = value; }
        }

        /// <summary>
        /// A callback to authenticate clients 
        /// Parameters are username, password provided by client
        /// return true for successful authentication
        /// </summary>
        public Func<string, string, Task<bool>> AuthenticateUserFunc { get; set; }

        /// <summary>
        /// Realm used during Proxy Basic Authentication 
        /// </summary>
        public string ProxyRealm { get; set; } = "TitaniumProxy";

        /// <summary>
        /// A callback to provide authentication credentials for up stream proxy this proxy is using for HTTP requests
        /// return the ExternalProxy object with valid credentials
        /// </summary>
        public Func<SessionEventArgs, Task<ExternalProxy>> GetCustomUpStreamHttpProxyFunc { get; set; }

        /// <summary>
        /// A callback to provide authentication credentials for up stream proxy this proxy is using for HTTPS requests
        /// return the ExternalProxy object with valid credentials
        /// </summary>
        public Func<SessionEventArgs, Task<ExternalProxy>> GetCustomUpStreamHttpsProxyFunc { get; set; }

        /// <summary>
        /// A list of IpAddress and port this proxy is listening to
        /// </summary>
        public List<ProxyEndPoint> ProxyEndPoints { get; set; }

        /// <summary>
        /// List of supported Ssl versions
        /// </summary>
#if NET45
        public SslProtocols SupportedSslProtocols { get; set; } = SslProtocols.Tls | SslProtocols.Tls11 | SslProtocols.Tls12 | SslProtocols.Ssl3;
#else
        public SslProtocols SupportedSslProtocols { get; set; } = SslProtocols.Tls | SslProtocols.Tls11 | SslProtocols.Tls12;
#endif

        /// <summary>
        /// Total number of active client connections
        /// </summary>
        public int ClientConnectionCount => clientConnectionCount;

        /// <summary>
        /// Total number of active server connections
        /// </summary>
        public int ServerConnectionCount => serverConnectionCount;

        /// <summary>
        /// Constructor
        /// </summary>
        public ProxyServer() : this(null, null)
        {
        }

        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="rootCertificateName">Name of root certificate.</param>
        /// <param name="rootCertificateIssuerName">Name of root certificate issuer.</param>
        public ProxyServer(string rootCertificateName, string rootCertificateIssuerName)
        {
            //default values
            ConnectionTimeOutSeconds = 30;
            CertificateCacheTimeOutMinutes = 60;

            ProxyEndPoints = new List<ProxyEndPoint>();
            tcpConnectionFactory = new TcpConnectionFactory();
#if NET45
            if (!RunTime.IsRunningOnMono)
            {
                systemProxySettingsManager = new SystemProxyManager();
            }
#endif

            CertificateManager = new CertificateManager(ExceptionFunc);
            if (rootCertificateName != null)
            {
                RootCertificateName = rootCertificateName;
            }

            if (rootCertificateIssuerName != null)
            {
                RootCertificateIssuerName = rootCertificateIssuerName;
            }
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

#if NET45
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

                EnsureRootCertificate();

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
                endPoint.IsSystemHttpsProxy = true;
            }

            if (isHttps)
            {
                endPoint.IsSystemHttpsProxy = true;
            }

            firefoxProxySettingsManager.UseSystemProxy();

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
#endif

        /// <summary>
        /// Start this proxy server
        /// </summary>
        public void Start()
        {
            if (proxyRunning)
            {
                throw new Exception("Proxy is already running.");
            }

#if NET45
            //clear any system proxy settings which is pointing to our own endpoint (causing a cycle)
            //due to non gracious proxy shutdown before or something else
            if (systemProxySettingsManager != null)
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

            if (ForwardToUpstreamGateway
                && GetCustomUpStreamHttpProxyFunc == null && GetCustomUpStreamHttpsProxyFunc == null
                && systemProxySettingsManager != null)
            {
                // Use WinHttp to handle PAC/WAPD scripts.
                systemProxyResolver = new WinHttpWebProxyFinder();
                systemProxyResolver.LoadFromIE();

                GetCustomUpStreamHttpProxyFunc = GetSystemUpStreamProxy;
                GetCustomUpStreamHttpsProxyFunc = GetSystemUpStreamProxy;
            }
#endif

            foreach (var endPoint in ProxyEndPoints)
            {
                Listen(endPoint);
            }

            CertificateManager.ClearIdleCertificates(CertificateCacheTimeOutMinutes);

#if NET45
            if (!RunTime.IsRunningOnMono)
            {
                //clear orphaned windows auth states every 2 minutes
                WinAuthEndPoint.ClearIdleStates(2);
            }
#endif

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

#if NET45
            if (!RunTime.IsRunningOnMono)
            {
                bool setAsSystemProxy = ProxyEndPoints.OfType<ExplicitProxyEndPoint>().Any(x => x.IsSystemHttpProxy || x.IsSystemHttpsProxy);

                if (setAsSystemProxy)
                {
                    systemProxySettingsManager.RestoreOriginalSettings();
                }
            }
#endif

            foreach (var endPoint in ProxyEndPoints)
            {
                QuitListen(endPoint);
            }

            ProxyEndPoints.Clear();

            CertificateManager?.StopClearIdleCertificates();

            proxyRunning = false;
        }

        /// <summary>
        ///  Handle dispose of a client/server session
        /// </summary>
        /// <param name="clientStream"></param>
        /// <param name="clientStreamReader"></param>
        /// <param name="clientStreamWriter"></param>
        /// <param name="serverConnection"></param>
        private void Dispose(CustomBufferedStream clientStream, CustomBinaryReader clientStreamReader, HttpResponseWriter clientStreamWriter, TcpConnection serverConnection)
        {
            clientStream?.Dispose();

            clientStreamReader?.Dispose();
            clientStreamWriter?.Dispose();

            if (serverConnection != null)
            {
                serverConnection.Dispose();
                serverConnection = null;
                UpdateServerConnectionCount(false);
            }
        }

        /// <summary>
        /// Dispose Proxy.
        /// </summary>
        public void Dispose()
        {
            if (proxyRunning)
            {
                Stop();
            }

            CertificateManager?.Dispose();
        }

#if NET45
        /// <summary>
        /// Listen on the given end point on local machine
        /// </summary>
        /// <param name="endPoint"></param>
        private void Listen(ProxyEndPoint endPoint)
        {
            endPoint.Listener = new TcpListener(endPoint.IpAddress, endPoint.Port);
            endPoint.Listener.Start();

            endPoint.Port = ((IPEndPoint)endPoint.Listener.LocalEndpoint).Port;
            // accept clients asynchronously
            endPoint.Listener.BeginAcceptTcpClient(OnAcceptConnection, endPoint);
        }
#else
        private async void Listen(ProxyEndPoint endPoint)
        {
            endPoint.Listener = new TcpListener(endPoint.IpAddress, endPoint.Port);
            endPoint.Listener.Start();

            endPoint.Port = ((IPEndPoint)endPoint.Listener.LocalEndpoint).Port;

            while (true)
            {
                TcpClient tcpClient = await endPoint.Listener.AcceptTcpClientAsync();
                if (tcpClient != null)
                    Task.Run(async () => HandleClient(tcpClient, endPoint));
            }

        }
#endif

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

            if (!proxyRunning)
            {
                throw new Exception("Cannot set system proxy settings before proxy has been started.");
            }
        }

#if NET45
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
#endif

        private void EnsureRootCertificate()
        {
            if (!CertificateManager.CertValidated)
            {
                CertificateManager.CreateTrustedRootCertificate();

                if (TrustRootCertificate)
                {
                    CertificateManager.TrustRootCertificate();
                }
            }
        }

#if NET45
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
#endif

        private async Task HandleClient(TcpClient tcpClient, ProxyEndPoint endPoint)
        {
            UpdateClientConnectionCount(true);

            tcpClient.ReceiveTimeout = ConnectionTimeOutSeconds * 1000;
            tcpClient.SendTimeout = ConnectionTimeOutSeconds * 1000;

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
                UpdateClientConnectionCount(false);

                try
                {
                    if (tcpClient != null)
                    {
                        //This line is important!
                        //contributors please don't remove it without discussion
                        //It helps to avoid eventual deterioration of performance due to TCP port exhaustion
                        //due to default TCP CLOSE_WAIT timeout for 4 minutes
                        tcpClient.LingerState = new LingerOption(true, 0);
                        tcpClient.Dispose();
                    }
                }
                catch
                {
                }
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
