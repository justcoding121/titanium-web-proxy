using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Quic;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.Examples.Wpf
{
    /// <summary>
    ///     Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        public static readonly DependencyProperty ClientConnectionCountProperty = DependencyProperty.Register(
            nameof(ClientConnectionCount), typeof(int), typeof(MainWindow), new PropertyMetadata(default(int)));

        public static readonly DependencyProperty ServerConnectionCountProperty = DependencyProperty.Register(
            nameof(ServerConnectionCount), typeof(int), typeof(MainWindow), new PropertyMetadata(default(int)));

        public static readonly DependencyProperty Http3ClientConnectionCountProperty = DependencyProperty.Register(
            nameof(Http3ClientConnectionCount), typeof(int), typeof(MainWindow), new PropertyMetadata(default(int)));

        public static readonly DependencyProperty Http3ServerConnectionCountProperty = DependencyProperty.Register(
            nameof(Http3ServerConnectionCount), typeof(int), typeof(MainWindow), new PropertyMetadata(default(int)));

        private readonly ProxyServer proxyServer;

        private readonly Dictionary<HttpWebClient, SessionListItem> sessionDictionary =
            new Dictionary<HttpWebClient, SessionListItem>();

        private int lastSessionNumber;
        private SessionListItem selectedSession;
        private bool proxyShutdownDone;

        public MainWindow()
        {
            proxyServer = new ProxyServer();

            // Session traffic is shown in the UI. Library diagnostics go to a rolling file (not the
            // console — WinExe usually has none). Debug builds capture full protocol diagnostics.
            proxyServer.Logging.EnableConsole = false;
            proxyServer.Logging.EnableFile = true;
            proxyServer.Logging.FilePath = Path.Combine(AppContext.BaseDirectory, "logs", "wpf-proxy.log");
#if DEBUG
            proxyServer.Logging.MinimumLevel = LogLevel.Trace;
#else
            proxyServer.Logging.MinimumLevel = LogLevel.Warning;
#endif

            var certificateDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Titanium.Web.Proxy");
            Directory.CreateDirectory(certificateDirectory);
            proxyServer.CertificateManager.PfxFilePath = Path.Combine(certificateDirectory, "rootCert.pfx");

            //proxyServer.EnableHttp2 = false;

            //proxyServer.CertificateManager.CertificateEngine = CertificateEngine.DefaultWindows;

            ////Set a password for the .pfx file
            //proxyServer.CertificateManager.PfxPassword = "PfxPassword";

            ////Set Name(path) of the Root certificate file
            //proxyServer.CertificateManager.PfxFilePath = @"C:\NameFolder\rootCert.pfx";

            ////do you want Replace an existing Root certificate file(.pfx) if password is incorrect(RootCertificate=null)?  yes====>true
            //proxyServer.CertificateManager.OverwritePfxFile = true;

            ////save all fake certificates in folder "crts"(will be created in proxy dll directory)
            ////if create new Root certificate file(.pfx) ====> delete folder "crts"
            //proxyServer.CertificateManager.SaveFakeCertificates = true;

            // Match Basic example interactive-proxy knobs: pool reuse + library default
            // ConnectionTimeOutSeconds (60). A short idle lifetime forces full TCP/TLS
            // reconnects after normal think time.
            proxyServer.TcpTimeWaitSeconds = 10;
            proxyServer.EnableConnectionPool = true;
            proxyServer.ForwardToUpstreamGateway = true;

            //increase the ThreadPool (for server prod)
            //proxyServer.ThreadPoolWorkerThread = Environment.ProcessorCount * 6;

            ////if you need Load or Create Certificate now. ////// "true" if you need Enable===> Trust the RootCertificate used by this proxy server
            //proxyServer.CertificateManager.EnsureRootCertificate(true);

            ////or load directly certificate(As Administrator if need this)
            ////and At the same time chose path and password
            ////if password is incorrect and (overwriteRootCert=true)(RootCertificate=null) ====> replace an existing .pfx file
            ////note : load now (if existed)
            //proxyServer.CertificateManager.LoadRootCertificate(@"C:\NameFolder\rootCert.pfx", "PfxPassword");

            var explicitEndPoint = new ExplicitProxyEndPoint(IPAddress.Any, 8000);

            proxyServer.AddEndPoint(explicitEndPoint);
            //proxyServer.UpStreamHttpProxy = new ExternalProxy
            //{
            //    HostName = "158.69.115.45",
            //    Port = 3128,
            //    UserName = "Titanium",
            //    Password = "Titanium",
            //};

            //var socksEndPoint = new SocksProxyEndPoint(IPAddress.Any, 1080, true)
            //{
            //    // Generic Certificate hostname to use
            //    // When SNI is disabled by client
            //    //GenericCertificateName = "google.com"
            //};

            //proxyServer.AddEndPoint(socksEndPoint);

            // HTTP/3 transparent QUIC endpoint (experimental — suppress TWP001 to opt in).
            // Requires MsQuic and a supported OS (Windows 11 / Server 2022+, or libmsquic on Linux/macOS).
            // UDP traffic must be redirected here; see wiki/HTTP-3.md.
#pragma warning disable TWP001
            if (QuicListener.IsSupported)
            {
                proxyServer.EnableHttp3 = true;
                // Learn H3 from Alt-Svc on the first response; proactive SVCB DNS is off for
                // interactive browsing (same default as the Basic example).
                proxyServer.EnableHttpsSvcbDnsDiscovery = false;
                var quicEndPoint = new TransparentQuicProxyEndPoint(IPAddress.Any, 443)
                {
                    // Replace with IOriginalDestinationResolver for real NAT-transparent interception.
                    ForwardHost = "localhost",
                    ForwardPort = 443
                };
                quicEndPoint.BeforeQuicAuthenticate += ProxyServer_BeforeQuicAuthenticate;
                proxyServer.AddEndPoint(quicEndPoint);
            }
#pragma warning restore TWP001

            proxyServer.BeforeRequest += ProxyServer_BeforeRequest;
            proxyServer.BeforeResponse += ProxyServer_BeforeResponse;
            proxyServer.AfterResponse += ProxyServer_AfterResponse;
            explicitEndPoint.BeforeTunnelConnectRequest += ProxyServer_BeforeTunnelConnectRequest;
            explicitEndPoint.BeforeTunnelConnectResponse += ProxyServer_BeforeTunnelConnectResponse;
            proxyServer.ClientConnectionCountChanged += delegate
            {
                Dispatcher.Invoke(() => { ClientConnectionCount = proxyServer.ClientConnectionCount; });
            };
            proxyServer.ServerConnectionCountChanged += delegate
            {
                Dispatcher.Invoke(() => { ServerConnectionCount = proxyServer.ServerConnectionCount; });
            };
            proxyServer.Http3ClientConnectionCountChanged += delegate
            {
                Dispatcher.Invoke(() => { Http3ClientConnectionCount = proxyServer.Http3ClientConnectionCount; });
            };
            proxyServer.Http3ServerConnectionCountChanged += delegate
            {
                Dispatcher.Invoke(() => { Http3ServerConnectionCount = proxyServer.Http3ServerConnectionCount; });
            };
            proxyServer.Start();

            proxyServer.SetAsSystemProxy(explicitEndPoint, ProxyProtocolType.AllHttp, new SystemProxySettings
            {
                // Route localhost/loopback traffic through the proxy for this example.
                ProxyLoopback = true
            });

            InitializeComponent();

            // Always clear system proxy when the window closes (graceful or App.Shutdown).
            // Without this, browsers keep pointing at a dead :8000 and hang after exit.
            Closed += (_, _) => ShutdownProxy();
        }

        /// <summary>
        ///     Stops the proxy and restores Windows system proxy settings. Safe to call more than once.
        /// </summary>
        internal void EnsureProxyShutdown() => ShutdownProxy();

        private void ShutdownProxy()
        {
            if (proxyShutdownDone) return;
            proxyShutdownDone = true;

            try
            {
                if (proxyServer.ProxyRunning)
                    proxyServer.Stop();
                else
                    proxyServer.RestoreOriginalProxySettings();
            }
            catch (Exception)
            {
                // Best-effort: still try Dispose below.
                try
                {
                    proxyServer.RestoreOriginalProxySettings();
                }
                catch (Exception)
                {
                    // ignored
                }
            }

            try
            {
                proxyServer.Dispose();
            }
            catch (Exception)
            {
                // ignored
            }
        }

        public ObservableCollectionEx<SessionListItem> Sessions { get; } =
            new ObservableCollectionEx<SessionListItem>();

        public SessionListItem SelectedSession
        {
            get => selectedSession;
            set
            {
                if (value != selectedSession)
                {
                    selectedSession = value;
                    SelectedSessionChanged();
                }
            }
        }

        public int ClientConnectionCount
        {
            get => (int)GetValue(ClientConnectionCountProperty);
            set => SetValue(ClientConnectionCountProperty, value);
        }

        public int ServerConnectionCount
        {
            get => (int)GetValue(ServerConnectionCountProperty);
            set => SetValue(ServerConnectionCountProperty, value);
        }

        public int Http3ClientConnectionCount
        {
            get => (int)GetValue(Http3ClientConnectionCountProperty);
            set => SetValue(Http3ClientConnectionCountProperty, value);
        }

        public int Http3ServerConnectionCount
        {
            get => (int)GetValue(Http3ServerConnectionCountProperty);
            set => SetValue(Http3ServerConnectionCountProperty, value);
        }

#pragma warning disable TWP001
        private Task ProxyServer_BeforeQuicAuthenticate(object sender, BeforeQuicAuthenticateEventArgs e)
        {
            return Task.CompletedTask;
        }
#pragma warning restore TWP001

        private async Task ProxyServer_BeforeTunnelConnectRequest(object sender, TunnelConnectSessionEventArgs e)
        {
            var hostname = e.HttpClient.Request.RequestUri.Host;
            if (hostname.EndsWith("webex.com")) e.DecryptSsl = false;

            await Dispatcher.InvokeAsync(() => { AddSession(e); });
        }

        private async Task ProxyServer_BeforeTunnelConnectResponse(object sender, TunnelConnectSessionEventArgs e)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                if (sessionDictionary.TryGetValue(e.HttpClient, out var item)) item.Update(e);
            });
        }

        private async Task ProxyServer_BeforeRequest(object sender, SessionEventArgs e)
        {
            //if (e.HttpClient.Request.HttpVersion.Major != 2) return;

            SessionListItem item = null;
            await Dispatcher.InvokeAsync(() => { item = AddSession(e); });

            if (e.HttpClient.Request.HasBody)
            {
                e.HttpClient.Request.KeepBody = true;
                await e.GetRequestBody();

                if (item == SelectedSession) await Dispatcher.InvokeAsync(SelectedSessionChanged);
            }
        }

        private async Task ProxyServer_BeforeResponse(object sender, SessionEventArgs e)
        {
            SessionListItem item = null;
            await Dispatcher.InvokeAsync(() =>
            {
                if (sessionDictionary.TryGetValue(e.HttpClient, out item)) item.Update(e);
            });

            //e.HttpClient.Response.Headers.AddHeader("X-Titanium-Header", "HTTP/2 works");

            //e.SetResponseBody(Encoding.ASCII.GetBytes("TITANIUMMMM!!!!"));

            if (item != null)
                if (e.HttpClient.Response.HasBody)
                {
                    e.HttpClient.Response.KeepBody = true;
                    await e.GetResponseBody();

                    await Dispatcher.InvokeAsync(() => { item.Update(e); });
                    if (item == SelectedSession) await Dispatcher.InvokeAsync(SelectedSessionChanged);
                }
        }

        private async Task ProxyServer_AfterResponse(object sender, SessionEventArgs e)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                if (sessionDictionary.TryGetValue(e.HttpClient, out var item)) item.Exception = e.Exception;
            });
        }

        private SessionListItem AddSession(SessionEventArgsBase e)
        {
            var item = CreateSessionListItem(e);
            Sessions.Add(item);
            sessionDictionary.Add(e.HttpClient, item);
            return item;
        }

        private SessionListItem CreateSessionListItem(SessionEventArgsBase e)
        {
            lastSessionNumber++;
            var isTunnelConnect = e is TunnelConnectSessionEventArgs;
            var item = new SessionListItem
            {
                Number = lastSessionNumber,
                ClientConnectionId = e.ClientConnectionId,
                ServerConnectionId = e.ServerConnectionId,
                HttpClient = e.HttpClient,
                ClientRemoteEndPoint = e.ClientRemoteEndPoint,
                ClientLocalEndPoint = e.ClientLocalEndPoint,
                IsTunnelConnect = isTunnelConnect
            };

            //if (isTunnelConnect || e.HttpClient.Request.UpgradeToWebSocket)
            e.DataReceived += (sender, args) =>
            {
                var session = (SessionEventArgsBase)sender;
                if (sessionDictionary.TryGetValue(session.HttpClient, out var li))
                {
                    var connectRequest = session.HttpClient.ConnectRequest;
                    var tunnelType = connectRequest?.TunnelType ?? TunnelType.Unknown;
                    if (tunnelType != TunnelType.Unknown) li.Protocol = TunnelTypeToString(tunnelType);

                    li.ReceivedDataCount += args.Count;
                }
            };

            e.DataSent += (sender, args) =>
            {
                var session = (SessionEventArgsBase)sender;
                if (sessionDictionary.TryGetValue(session.HttpClient, out var li))
                {
                    var connectRequest = session.HttpClient.ConnectRequest;
                    var tunnelType = connectRequest?.TunnelType ?? TunnelType.Unknown;
                    if (tunnelType != TunnelType.Unknown) li.Protocol = TunnelTypeToString(tunnelType);

                    li.SentDataCount += args.Count;
                }
            };

            item.Update(e);
            return item;
        }

        private string TunnelTypeToString(TunnelType tunnelType)
        {
            switch (tunnelType)
            {
                case TunnelType.Https:
                    return "https";
                case TunnelType.Websocket:
                    return "websocket";
                case TunnelType.Http2:
                    return "http2";
            }

            return null;
        }

        private void ListViewSessions_OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Delete)
            {
                var isSelected = false;
                var selectedItems = ((ListView)sender).SelectedItems;
                Sessions.SuppressNotification = true;
                foreach (var item in selectedItems.Cast<SessionListItem>().ToArray())
                {
                    if (item == SelectedSession) isSelected = true;

                    Sessions.Remove(item);
                    sessionDictionary.Remove(item.HttpClient);
                }

                Sessions.SuppressNotification = false;

                if (isSelected) SelectedSession = null;
            }
        }

        private void SelectedSessionChanged()
        {
            if (SelectedSession == null)
            {
                TextBoxRequest.Text = null;
                TextBoxResponse.Text = string.Empty;
                ImageResponse.Source = null;
                return;
            }

            const int truncateLimit = 1024;

            var session = SelectedSession.HttpClient;
            var request = session.Request;
            var fullData = (request.IsBodyRead ? request.Body : null) ?? Array.Empty<byte>();
            var data = fullData;
            var truncated = data.Length > truncateLimit;
            if (truncated) data = data.Take(truncateLimit).ToArray();

            //string hexStr = string.Join(" ", data.Select(x => x.ToString("X2")));
            var sb = new StringBuilder();
            sb.AppendLine("URI: " + request.RequestUri);
            sb.Append(request.HeaderText);
            sb.Append(request.Encoding.GetString(data));
            if (truncated)
            {
                sb.AppendLine();
                sb.Append($"Data is truncated after {truncateLimit} bytes");
            }

            sb.Append((request as ConnectRequest)?.ClientHelloInfo);
            TextBoxRequest.Text = sb.ToString();

            var response = session.Response;
            fullData = (response.IsBodyRead ? response.Body : null) ?? Array.Empty<byte>();
            data = fullData;
            truncated = data.Length > truncateLimit;
            if (truncated) data = data.Take(truncateLimit).ToArray();

            //hexStr = string.Join(" ", data.Select(x => x.ToString("X2")));
            sb = new StringBuilder();
            sb.Append(response.HeaderText);
            sb.Append(response.Encoding.GetString(data));
            if (truncated)
            {
                sb.AppendLine();
                sb.Append($"Data is truncated after {truncateLimit} bytes");
            }

            sb.Append((response as ConnectResponse)?.ServerHelloInfo);
            if (SelectedSession.Exception != null)
            {
                sb.Append(Environment.NewLine);
                sb.Append(SelectedSession.Exception);
            }

            TextBoxResponse.Text = sb.ToString();

            try
            {
                if (fullData.Length > 0)
                    using (var stream = new MemoryStream(fullData))
                    {
                        ImageResponse.Source =
                            BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                    }
            }
            catch
            {
                ImageResponse.Source = null;
            }
        }

        private void ButtonProxyOnOff_OnClick(object sender, RoutedEventArgs e)
        {
            var button = (ToggleButton)sender;
            if (button.IsChecked == true)
                proxyServer.SetAsSystemProxy((ExplicitProxyEndPoint)proxyServer.ProxyEndPoints[0],
                    ProxyProtocolType.AllHttp);
            else
                proxyServer.RestoreOriginalProxySettings();
        }
    }
}