using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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

        private readonly ProxyServer proxyServer;

        private readonly Dictionary<HttpWebClient, SessionListItem> sessionDictionary =
            new Dictionary<HttpWebClient, SessionListItem>();

        private int lastSessionNumber;
        private SessionListItem selectedSession;

        public MainWindow()
        {
            proxyServer = new ProxyServer();
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

            proxyServer.ForwardToUpstreamGateway = true;

            ////if you need Load or Create Certificate now. ////// "true" if you need Enable===> Trust the RootCertificate used by this proxy server
            //proxyServer.CertificateManager.EnsureRootCertificate(true);

            ////or load directly certificate(As Administrator if need this)
            ////and At the same time chose path and password
            ////if password is incorrect and (overwriteRootCert=true)(RootCertificate=null) ====> replace an existing .pfx file
            ////note : load now (if existed)
            //proxyServer.CertificateManager.LoadRootCertificate(@"C:\NameFolder\rootCert.pfx", "PfxPassword");

            var explicitEndPoint = new ExplicitProxyEndPoint(IPAddress.Any, 8000, true);

            proxyServer.AddEndPoint(explicitEndPoint);
            //proxyServer.UpStreamHttpProxy = new ExternalProxy
            //{
            //    HostName = "158.69.115.45",
            //    Port = 3128,
            //    UserName = "Titanium",
            //    Password = "Titanium",
            //};

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
            proxyServer.Start();

            proxyServer.SetAsSystemProxy(explicitEndPoint, ProxyProtocolType.AllHttp);

            InitializeComponent();
        }

        public ObservableCollection<SessionListItem> Sessions { get; } = new ObservableCollection<SessionListItem>();

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

        private async Task ProxyServer_BeforeTunnelConnectRequest(object sender, TunnelConnectSessionEventArgs e)
        {
            string hostname = e.HttpClient.Request.RequestUri.Host;
            if (hostname.EndsWith("webex.com"))
            {
                e.DecryptSsl = false;
            }

            await Dispatcher.InvokeAsync(() => { AddSession(e); });
        }

        private async Task ProxyServer_BeforeTunnelConnectResponse(object sender, TunnelConnectSessionEventArgs e)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                if (sessionDictionary.TryGetValue(e.HttpClient, out var item))
                {
                    item.Update();
                }
            });
        }

        private async Task ProxyServer_BeforeRequest(object sender, SessionEventArgs e)
        {
            SessionListItem item = null;
            await Dispatcher.InvokeAsync(() => { item = AddSession(e); });

            if (e.HttpClient.Request.HasBody)
            {
                e.HttpClient.Request.KeepBody = true;
                await e.GetRequestBody();
            }
        }

        private async Task ProxyServer_BeforeResponse(object sender, SessionEventArgs e)
        {
            SessionListItem item = null;
            await Dispatcher.InvokeAsync(() =>
            {
                if (sessionDictionary.TryGetValue(e.HttpClient, out item))
                {
                    item.Update();
                }
            });

            if (item != null)
            {
                if (e.HttpClient.Response.HasBody)
                {
                    e.HttpClient.Response.KeepBody = true;
                    await e.GetResponseBody();

                    await Dispatcher.InvokeAsync(() => { item.Update(); });
                }
            }
        }

        private async Task ProxyServer_AfterResponse(object sender, SessionEventArgs e)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                if (sessionDictionary.TryGetValue(e.HttpClient, out var item))
                {
                    item.Exception = e.Exception;
                }
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
            bool isTunnelConnect = e is TunnelConnectSessionEventArgs;
            var item = new SessionListItem
            {
                Number = lastSessionNumber,
                HttpClient = e.HttpClient,
                IsTunnelConnect = isTunnelConnect
            };

            if (isTunnelConnect || e.HttpClient.Request.UpgradeToWebSocket)
            {
                e.DataReceived += (sender, args) =>
                {
                    var session = (SessionEventArgs)sender;
                    if (sessionDictionary.TryGetValue(session.HttpClient, out var li))
                    {
                        li.ReceivedDataCount += args.Count;
                    }
                };

                e.DataSent += (sender, args) =>
                {
                    var session = (SessionEventArgs)sender;
                    if (sessionDictionary.TryGetValue(session.HttpClient, out var li))
                    {
                        li.SentDataCount += args.Count;
                    }
                };
            }

            item.Update();
            return item;
        }

        private void ListViewSessions_OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Delete)
            {
                var selectedItems = ((ListView)sender).SelectedItems;
                foreach (var item in selectedItems.Cast<SessionListItem>().ToArray())
                {
                    Sessions.Remove(item);
                    sessionDictionary.Remove(item.HttpClient);
                }
            }
        }

        private void SelectedSessionChanged()
        {
            if (SelectedSession == null)
            {
                return;
            }

            const int truncateLimit = 1024;

            var session = SelectedSession.HttpClient;
            var request = session.Request;
            var data = (request.IsBodyRead ? request.Body : null) ?? new byte[0];
            bool truncated = data.Length > truncateLimit;
            if (truncated)
            {
                data = data.Take(truncateLimit).ToArray();
            }

            //string hexStr = string.Join(" ", data.Select(x => x.ToString("X2")));
            var sb = new StringBuilder();
            sb.Append(request.HeaderText);
            sb.Append(request.Encoding.GetString(data));
            sb.Append(truncated ? Environment.NewLine + $"Data is truncated after {truncateLimit} bytes" : null);
            sb.Append((request as ConnectRequest)?.ClientHelloInfo);
            TextBoxRequest.Text = sb.ToString();

            var response = session.Response;
            data = (response.IsBodyRead ? response.Body : null) ?? new byte[0];
            truncated = data.Length > truncateLimit;
            if (truncated)
            {
                data = data.Take(truncateLimit).ToArray();
            }

            //hexStr = string.Join(" ", data.Select(x => x.ToString("X2")));
            sb = new StringBuilder();
            sb.Append(response.HeaderText);
            sb.Append(response.Encoding.GetString(data));
            sb.Append(truncated ? Environment.NewLine + $"Data is truncated after {truncateLimit} bytes" : null);
            sb.Append((response as ConnectResponse)?.ServerHelloInfo);
            if (SelectedSession.Exception != null)
            {
                sb.Append(Environment.NewLine);
                sb.Append(SelectedSession.Exception);
            }

            TextBoxResponse.Text = sb.ToString();
        }
    }
}
