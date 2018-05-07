using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using StreamExtended.Network;
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
    /// <inheritdoc />
    /// <summary>
    ///     This class is the backbone of proxy. One can create as many instances as needed.
    ///     However care should be taken to avoid using the same listening ports across multiple instances.
    /// </summary>
    public partial class ProxyServer : IDisposable
    {
        /// <summary>
        ///     HTTP &amp; HTTPS scheme shorthands.
        /// </summary>
        internal static readonly string UriSchemeHttp = Uri.UriSchemeHttp;
        internal static readonly string UriSchemeHttps = Uri.UriSchemeHttps;

        /// <summary>
        ///     A default exception log func.
        /// </summary>
        private readonly ExceptionHandler defaultExceptionFunc = e => { };

        /// <summary>
        ///     Backing field for exposed public property.
        /// </summary>
        private int clientConnectionCount;

        /// <summary>
        ///     Backing field for exposed public property.
        /// </summary>
        private ExceptionHandler exceptionFunc;

        /// <summary>
        ///     Backing field for exposed public property.
        /// </summary>
        private int serverConnectionCount;

        /// <summary>
        ///     Upstream proxy manager.
        /// </summary>
        private WinHttpWebProxyFinder systemProxyResolver;

        /// <inheritdoc />
        /// <summary>
        ///     Initializes a new instance of ProxyServer class with provided parameters.
        /// </summary>
        /// <param name="userTrustRootCertificate">
        ///     Should fake HTTPS certificate be trusted by this machine's user certificate
        ///     store?
        /// </param>
        /// <param name="machineTrustRootCertificate">Should fake HTTPS certificate be trusted by this machine's certificate store?</param>
        /// <param name="trustRootCertificateAsAdmin">
        ///     Should we attempt to trust certificates with elevated permissions by
        ///     prompting for UAC if required?
        /// </param>
        public ProxyServer(bool userTrustRootCertificate = true, bool machineTrustRootCertificate = false,
            bool trustRootCertificateAsAdmin = false) : this(null, null, userTrustRootCertificate,
            machineTrustRootCertificate, trustRootCertificateAsAdmin)
        {
        }

        /// <summary>
        ///     Initializes a new instance of ProxyServer class with provided parameters.
        /// </summary>
        /// <param name="rootCertificateName">Name of the root certificate.</param>
        /// <param name="rootCertificateIssuerName">Name of the root certificate issuer.</param>
        /// <param name="userTrustRootCertificate">
        ///     Should fake HTTPS certificate be trusted by this machine's user certificate
        ///     store?
        /// </param>
        /// <param name="machineTrustRootCertificate">Should fake HTTPS certificate be trusted by this machine's certificate store?</param>
        /// <param name="trustRootCertificateAsAdmin">
        ///     Should we attempt to trust certificates with elevated permissions by
        ///     prompting for UAC if required?
        /// </param>
        public ProxyServer(string rootCertificateName, string rootCertificateIssuerName,
            bool userTrustRootCertificate = true, bool machineTrustRootCertificate = false,
            bool trustRootCertificateAsAdmin = false)
        {
            // default values
            ConnectionTimeOutSeconds = 60;

            ProxyEndPoints = new List<ProxyEndPoint>();
            tcpConnectionFactory = new TcpConnectionFactory();
            if (!RunTime.IsRunningOnMono && RunTime.IsWindows)
            {
                systemProxySettingsManager = new SystemProxyManager();
            }

            CertificateManager = new CertificateManager(rootCertificateName, rootCertificateIssuerName,
                userTrustRootCertificate, machineTrustRootCertificate, trustRootCertificateAsAdmin, ExceptionFunc);
        }

        /// <summary>
        ///     An factory that creates tcp connection to server.
        /// </summary>
        private TcpConnectionFactory tcpConnectionFactory { get; }

        /// <summary>
        ///     Manage system proxy settings.
        /// </summary>
        private SystemProxyManager systemProxySettingsManager { get; }

        /// <summary>
        ///     Is the proxy currently running?
        /// </summary>
        public bool ProxyRunning { get; private set; }

        /// <summary>
        ///     Gets or sets a value indicating whether requests will be chained to upstream gateway.
        /// </summary>
        public bool ForwardToUpstreamGateway { get; set; }

        /// <summary>
        ///     Enable disable Windows Authentication (NTLM/Kerberos).
        ///     Note: NTLM/Kerberos will always send local credentials of current user
        ///     running the proxy process. This is because a man
        ///     in middle attack with Windows domain authentication is not currently supported.
        /// </summary>
        public bool EnableWinAuth { get; set; }

        /// <summary>
        ///     Should we check for certificare revocation during SSL authentication to servers
        ///     Note: If enabled can reduce performance. Defaults to false.
        /// </summary>
        public X509RevocationMode CheckCertificateRevocation { get; set; }

        /// <summary>
        ///     Does this proxy uses the HTTP protocol 100 continue behaviour strictly?
        ///     Broken 100 contunue implementations on server/client may cause problems if enabled.
        ///     Defaults to false.
        /// </summary>
        public bool Enable100ContinueBehaviour { get; set; }

        /// <summary>
        ///     Buffer size used throughout this proxy.
        /// </summary>
        public int BufferSize { get; set; } = 8192;

        /// <summary>
        ///     Seconds client/server connection are to be kept alive when waiting for read/write to complete.
        /// </summary>
        public int ConnectionTimeOutSeconds { get; set; }

        /// <summary>
        ///     Total number of active client connections.
        /// </summary>
        public int ClientConnectionCount => clientConnectionCount;

        /// <summary>
        ///     Total number of active server connections.
        /// </summary>
        public int ServerConnectionCount => serverConnectionCount;

        /// <summary>
        ///     Realm used during Proxy Basic Authentication.
        /// </summary>
        public string ProxyRealm { get; set; } = "TitaniumProxy";
        
        /// <summary>
        ///     List of supported Ssl versions.
        /// </summary>
        public SslProtocols SupportedSslProtocols { get; set; } =
#if NET45
            SslProtocols.Ssl3 |
#endif
            SslProtocols.Tls | SslProtocols.Tls11 | SslProtocols.Tls12;

        /// <summary>
        ///     Manages certificates used by this proxy.
        /// </summary>
        public CertificateManager CertificateManager { get; }

        /// <summary>
        ///     External proxy used for Http requests.
        /// </summary>
        public ExternalProxy UpStreamHttpProxy { get; set; }

        /// <summary>
        ///     External proxy used for Https requests.
        /// </summary>
        public ExternalProxy UpStreamHttpsProxy { get; set; }

        /// <summary>
        ///     Local adapter/NIC endpoint where proxy makes request via.
        ///     Defaults via any IP addresses of this machine.
        /// </summary>
        public IPEndPoint UpStreamEndPoint { get; set; } = new IPEndPoint(IPAddress.Any, 0);

        /// <summary>
        ///     A list of IpAddress and port this proxy is listening to.
        /// </summary>
        public List<ProxyEndPoint> ProxyEndPoints { get; set; }

        /// <summary>
        ///     A callback to provide authentication credentials for up stream proxy this proxy is using for HTTP(S) requests.
        ///     User should return the ExternalProxy object with valid credentials.
        /// </summary>
        public Func<SessionEventArgsBase, Task<ExternalProxy>> GetCustomUpStreamProxyFunc { get; set; }

        /// <summary>
        ///     Callback for error events in this proxy instance.
        /// </summary>
        public ExceptionHandler ExceptionFunc
        {
            get => exceptionFunc ?? defaultExceptionFunc;
            set => exceptionFunc = value;
        }

        /// <summary>
        ///     A callback to authenticate clients.
        ///     Parameters are username and password as provided by client.
        ///     Should return true for successful authentication.
        /// </summary>
        public Func<string, string, Task<bool>> AuthenticateUserFunc { get; set; }

        /// <summary>
        ///     Dispose the Proxy instance.
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
        ///     Event occurs when client connection count changed.
        /// </summary>
        public event EventHandler ClientConnectionCountChanged;

        /// <summary>
        ///     Event occurs when server connection count changed.
        /// </summary>
        public event EventHandler ServerConnectionCountChanged;

        /// <summary>
        ///     Event to override the default verification logic of remote SSL certificate received during authentication.
        /// </summary>
        public event AsyncEventHandler<CertificateValidationEventArgs> ServerCertificateValidationCallback;

        /// <summary>
        ///     Event to override client certificate selection during mutual SSL authentication.
        /// </summary>
        public event AsyncEventHandler<CertificateSelectionEventArgs> ClientCertificateSelectionCallback;

        /// <summary>
        ///     Intercept request event to server.
        /// </summary>
        public event AsyncEventHandler<SessionEventArgs> BeforeRequest;

        /// <summary>
        ///     Intercept response event from server.
        /// </summary>
        public event AsyncEventHandler<SessionEventArgs> BeforeResponse;

        /// <summary>
        ///     Intercept after response event from server.
        /// </summary>
        public event AsyncEventHandler<SessionEventArgs> AfterResponse;

        /// <summary>
        ///     Add a proxy end point.
        /// </summary>
        /// <param name="endPoint">The proxy endpoint.</param>
        public void AddEndPoint(ProxyEndPoint endPoint)
        {
            if (ProxyEndPoints.Any(x =>
                x.IpAddress.Equals(endPoint.IpAddress) && endPoint.Port != 0 && x.Port == endPoint.Port))
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
        ///     Remove a proxy end point.
        ///     Will throw error if the end point does'nt exist.
        /// </summary>
        /// <param name="endPoint">The existing endpoint to remove.</param>
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
        ///     Set the given explicit end point as the default proxy server for current machine.
        /// </summary>
        /// <param name="endPoint">The explicit endpoint.</param>
        public void SetAsSystemHttpProxy(ExplicitProxyEndPoint endPoint)
        {
            SetAsSystemProxy(endPoint, ProxyProtocolType.Http);
        }

        /// <summary>
        ///     Set the given explicit end point as the default proxy server for current machine.
        /// </summary>
        /// <param name="endPoint">The explicit endpoint.</param>
        public void SetAsSystemHttpsProxy(ExplicitProxyEndPoint endPoint)
        {
            SetAsSystemProxy(endPoint, ProxyProtocolType.Https);
        }

        /// <summary>
        ///     Set the given explicit end point as the default proxy server for current machine.
        /// </summary>
        /// <param name="endPoint">The explicit endpoint.</param>
        /// <param name="protocolType">The proxy protocol type.</param>
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
                CertificateManager.EnsureRootCertificate();

                // If certificate was trusted by the machine
                if (!CertificateManager.CertValidated)
                {
                    protocolType = protocolType & ~ProxyProtocolType.Https;
                    isHttps = false;
                }
            }

            // clear any settings previously added
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
                Console.WriteLine("Set endpoint at Ip {0} and port: {1} as System {2} Proxy", endPoint.IpAddress,
                    endPoint.Port, proxyType);
            }
        }

        /// <summary>
        ///     Clear HTTP proxy settings of current machine.
        /// </summary>
        public void DisableSystemHttpProxy()
        {
            DisableSystemProxy(ProxyProtocolType.Http);
        }

        /// <summary>
        ///     Clear HTTPS proxy settings of current machine.
        /// </summary>
        public void DisableSystemHttpsProxy()
        {
            DisableSystemProxy(ProxyProtocolType.Https);
        }

        /// <summary>
        ///     Clear the specified proxy setting for current machine.
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
        ///     Clear all proxy settings for current machine.
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
        ///     Start this proxy server instance.
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

            // clear any system proxy settings which is pointing to our own endpoint (causing a cycle)
            // due to ungracious proxy shutdown before or something else
            if (systemProxySettingsManager != null && RunTime.IsWindows)
            {
                var proxyInfo = systemProxySettingsManager.GetProxyInfoFromRegistry();
                if (proxyInfo.Proxies != null)
                {
                    var protocolToRemove = ProxyProtocolType.None;
                    foreach (var proxy in proxyInfo.Proxies.Values)
                    {
                        if ((proxy.HostName == "127.0.0.1"
                             || proxy.HostName.EqualsIgnoreCase("localhost"))
                            && ProxyEndPoints.Any(x => x.Port == proxy.Port))
                        {
                            protocolToRemove |= proxy.ProtocolType;
                        }
                    }

                    if (protocolToRemove != ProxyProtocolType.None)
                    {
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

            CertificateManager.ClearIdleCertificates();

            foreach (var endPoint in ProxyEndPoints)
            {
                Listen(endPoint);
            }
        }

        /// <summary>
        ///     Stop this proxy server instance.
        /// </summary>
        public void Stop()
        {
            if (!ProxyRunning)
            {
                throw new Exception("Proxy is not running.");
            }

            if (!RunTime.IsRunningOnMono && RunTime.IsWindows)
            {
                bool setAsSystemProxy = ProxyEndPoints.OfType<ExplicitProxyEndPoint>()
                    .Any(x => x.IsSystemHttpProxy || x.IsSystemHttpsProxy);

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
        ///     Listen on given end point of local machine.
        /// </summary>
        /// <param name="endPoint">The end point to listen.</param>
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
                var pex = new Exception(
                    $"Endpoint {endPoint} failed to start. Check inner exception and exception data for details.", ex);
                pex.Data.Add("ipAddress", endPoint.IpAddress);
                pex.Data.Add("port", endPoint.Port);
                throw pex;
            }
        }

        /// <summary>
        ///     Verify if its safe to set this end point as system proxy.
        /// </summary>
        /// <param name="endPoint">The end point to validate.</param>
        private void ValidateEndPointAsSystemProxy(ExplicitProxyEndPoint endPoint)
        {
            if (endPoint == null)
            {
                throw new ArgumentNullException(nameof(endPoint));
            }

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
        ///  Gets the system up stream proxy.
        /// </summary>
        /// <param name="sessionEventArgs">The session.</param>
        /// <returns>The external proxy as task result.</returns>
        private Task<ExternalProxy> GetSystemUpStreamProxy(SessionEventArgsBase sessionEventArgs)
        {
            var proxy = systemProxyResolver.GetProxy(sessionEventArgs.WebSession.Request.RequestUri);
            return Task.FromResult(proxy);
        }

        /// <summary>
        ///     Act when a connection is received from client.
        /// </summary>
        private void OnAcceptConnection(IAsyncResult asyn)
        {
            var endPoint = (ProxyEndPoint)asyn.AsyncState;

            TcpClient tcpClient = null;

            try
            {
                // based on end point type call appropriate request handlers
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
                // Other errors are discarded to keep proxy running
            }

            if (tcpClient != null)
            {
                Task.Run(async () => { await HandleClient(tcpClient, endPoint); });
            }

            // Get the listener that handles the client request.
            endPoint.Listener.BeginAcceptTcpClient(OnAcceptConnection, endPoint);
        }

        /// <summary>
        ///     Handle the client.
        /// </summary>
        /// <param name="tcpClient">The client.</param>
        /// <param name="endPoint">The proxy endpoint.</param>
        /// <returns>The task.</returns>
        private async Task HandleClient(TcpClient tcpClient, ProxyEndPoint endPoint)
        {
            tcpClient.ReceiveTimeout = ConnectionTimeOutSeconds * 1000;
            tcpClient.SendTimeout = ConnectionTimeOutSeconds * 1000;

            using (var clientConnection = new TcpClientConnection(this, tcpClient))
            {
                if (endPoint is TransparentProxyEndPoint tep)
                {
                    await HandleClient(tep, clientConnection);
                }
                else
                {
                    await HandleClient((ExplicitProxyEndPoint)endPoint, clientConnection);
                }
            }
        }

        /// <summary>
        /// Handle exception.
        /// </summary>
        /// <param name="clientStream">The client stream.</param>
        /// <param name="exception">The exception.</param>
        private void OnException(CustomBufferedStream clientStream, Exception exception)
        {
#if DEBUG
            if (clientStream is DebugCustomBufferedStream debugStream)
            {
                debugStream.LogException(exception);
            }
#endif

            ExceptionFunc(exception);
        }

        /// <summary>
        ///     Quit listening on the given end point.
        /// </summary>
        private void QuitListen(ProxyEndPoint endPoint)
        {
            endPoint.Listener.Stop();
            endPoint.Listener.Server.Dispose();
        }

        /// <summary>
        ///     Update client connection count.
        /// </summary>
        /// <param name="increment">Should we increment/decrement?</param>
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

        /// <summary>
        ///     Update server connection count.
        /// </summary>
        /// <param name="increment">Should we increment/decrement?</param>
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
