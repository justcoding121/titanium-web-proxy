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
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network;
using Titanium.Web.Proxy.Network.Tcp;

namespace Titanium.Web.Proxy
{
    /// <summary>
    ///     Proxy Server Main class
    /// </summary>
    public partial class ProxyServer : IDisposable
    {
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
        internal int serverConnectionCount;

        /// <summary>
        /// A object that creates tcp connection to server
        /// </summary>
        private TcpConnectionFactory tcpConnectionFactory { get; }

        /// <summary>
        /// Manage system proxy settings
        /// </summary>
        private SystemProxyManager systemProxySettingsManager { get; }

#if !DEBUG
        /// <summary>
        /// Set firefox to use default system proxy
        /// </summary>
        private FireFoxProxySettingsManager firefoxProxySettingsManager = new FireFoxProxySettingsManager();
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
        /// A list of IpAddress & port this proxy is listening to
        /// </summary>
        public List<ProxyEndPoint> ProxyEndPoints { get; set; }

        /// <summary>
        /// List of supported Ssl versions
        /// </summary>
        public SslProtocols SupportedSslProtocols { get; set; } = SslProtocols.Tls
                                                                  | SslProtocols.Tls11 | SslProtocols.Tls12 | SslProtocols.Ssl3;

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
            systemProxySettingsManager = new SystemProxyManager();
#if !DEBUG
            new FireFoxProxySettingsManager();
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
            if (ProxyEndPoints.Any(x => x.IpAddress.Equals(endPoint.IpAddress)
                                        && endPoint.Port != 0 && x.Port == endPoint.Port))
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
            if (RunTime.IsRunningOnMono())
            {
                throw new Exception("Mono Runtime do not support system proxy settings.");
            }

            ValidateEndPointAsSystemProxy(endPoint);

            //clear any settings previously added
            ProxyEndPoints.OfType<ExplicitProxyEndPoint>().ToList().ForEach(x => x.IsSystemHttpProxy = false);

            systemProxySettingsManager.SetHttpProxy(
                Equals(endPoint.IpAddress, IPAddress.Any) | Equals(endPoint.IpAddress, IPAddress.Loopback) ? "127.0.0.1" : endPoint.IpAddress.ToString(), endPoint.Port);

            endPoint.IsSystemHttpProxy = true;
#if !DEBUG
            firefoxProxySettingsManager.AddFirefox();
#endif
            Console.WriteLine("Set endpoint at Ip {0} and port: {1} as System HTTP Proxy", endPoint.IpAddress, endPoint.Port);
        }


        /// <summary>
        /// Set the given explicit end point as the default proxy server for current machine
        /// </summary>
        /// <param name="endPoint"></param>
        public void SetAsSystemHttpsProxy(ExplicitProxyEndPoint endPoint)
        {
            if (RunTime.IsRunningOnMono())
            {
                throw new Exception("Mono Runtime do not support system proxy settings.");
            }

            ValidateEndPointAsSystemProxy(endPoint);

            if (!endPoint.EnableSsl)
            {
                throw new Exception("Endpoint do not support Https connections");
            }

            //clear any settings previously added
            ProxyEndPoints.OfType<ExplicitProxyEndPoint>()
                .ToList()
                .ForEach(x => x.IsSystemHttpsProxy = false);

            EnsureRootCertificate();

            //If certificate was trusted by the machine
            if (CertificateManager.CertValidated)
            {
                systemProxySettingsManager.SetHttpsProxy(
                    Equals(endPoint.IpAddress, IPAddress.Any) |
                    Equals(endPoint.IpAddress, IPAddress.Loopback) ? "127.0.0.1" : endPoint.IpAddress.ToString(),
                    endPoint.Port);
            }


            endPoint.IsSystemHttpsProxy = true;

#if !DEBUG
            firefoxProxySettingsManager.AddFirefox();
#endif
            Console.WriteLine("Set endpoint at Ip {0} and port: {1} as System HTTPS Proxy", endPoint.IpAddress, endPoint.Port);
        }

        /// <summary>
        /// Remove any HTTP proxy setting of current machien
        /// </summary>
        public void DisableSystemHttpProxy()
        {
            if (RunTime.IsRunningOnMono())
            {
                throw new Exception("Mono Runtime do not support system proxy settings.");
            }

            systemProxySettingsManager.RemoveHttpProxy();
        }

        /// <summary>
        /// Remove any HTTPS proxy setting for current machine
        /// </summary>
        public void DisableSystemHttpsProxy()
        {
            if (RunTime.IsRunningOnMono())
            {
                throw new Exception("Mono Runtime do not support system proxy settings.");
            }

            systemProxySettingsManager.RemoveHttpsProxy();
        }

        /// <summary>
        /// Clear all proxy settings for current machine
        /// </summary>
        public void DisableAllSystemProxies()
        {
            if (RunTime.IsRunningOnMono())
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
            if (proxyRunning)
            {
                throw new Exception("Proxy is already running.");
            }

            if (ForwardToUpstreamGateway && GetCustomUpStreamHttpProxyFunc == null
                && GetCustomUpStreamHttpsProxyFunc == null)
            {
                GetCustomUpStreamHttpProxyFunc = GetSystemUpStreamProxy;
                GetCustomUpStreamHttpsProxyFunc = GetSystemUpStreamProxy;
            }

            foreach (var endPoint in ProxyEndPoints)
            {
                Listen(endPoint);
            }

            CertificateManager.ClearIdleCertificates(CertificateCacheTimeOutMinutes);

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

            CertificateManager?.StopClearIdleCertificates();

            proxyRunning = false;
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

        /// <summary>
        /// Verifiy if its safe to set this end point as System proxy
        /// </summary>
        /// <param name="endPoint"></param>
        private void ValidateEndPointAsSystemProxy(ExplicitProxyEndPoint endPoint)
        {
            if (endPoint == null) throw new ArgumentNullException(nameof(endPoint));
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
        /// Gets the system up stream proxy.
        /// </summary>
        /// <param name="sessionEventArgs">The <see cref="SessionEventArgs"/> instance containing the event data.</param>
        /// <returns><see cref="ExternalProxy"/> instance containing valid proxy configuration from PAC/WAPD scripts if any exists.</returns>
        private Task<ExternalProxy> GetSystemUpStreamProxy(SessionEventArgs sessionEventArgs)
        {
            // Use built-in WebProxy class to handle PAC/WAPD scripts.
            var systemProxyResolver = new WebProxy();

            var systemProxyUri = systemProxyResolver.GetProxy(sessionEventArgs.WebSession.Request.RequestUri);

            // TODO: Apply authorization
            var systemProxy = new ExternalProxy
            {
                HostName = systemProxyUri.Host,
                Port = systemProxyUri.Port
            };

            return Task.FromResult(systemProxy);
        }


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
                    Interlocked.Increment(ref clientConnectionCount);

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
                        Interlocked.Decrement(ref clientConnectionCount);

                        try
                        {
                            if (tcpClient != null)
                            {
                                //This line is important!
                                //contributors please don't remove it without discussion
                                //It helps to avoid eventual deterioration of performance due to TCP port exhaustion
                                //due to default TCP CLOSE_WAIT timeout for 4 minutes
                                tcpClient.LingerState = new LingerOption(true, 0);
                                tcpClient.Close();
                            }
                        }
                        catch
                        {
                        }
                    }
                });
            }

            // Get the listener that handles the client request.
            endPoint.Listener.BeginAcceptTcpClient(OnAcceptConnection, endPoint);
        }

        /// <summary>
        /// Quit listening on the given end point
        /// </summary>
        /// <param name="endPoint"></param>
        private void QuitListen(ProxyEndPoint endPoint)
        {
            endPoint.Listener.Stop();
            endPoint.Listener.Server.Close();
            endPoint.Listener.Server.Dispose();
        }
    }
}
