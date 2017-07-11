using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.Examples.Wpf
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly ProxyServer proxyServer;

        private int lastSessionNumber;

        public ObservableCollection<SessionListItem> Sessions { get; } =  new ObservableCollection<SessionListItem>();

        public SessionListItem SelectedSession
        {
            get { return selectedSession; }
            set
            {
                if (value != selectedSession)
                {
                    selectedSession = value;
                    SelectedSessionChanged();
                }
            }
        }

        public static readonly DependencyProperty ClientConnectionCountProperty = DependencyProperty.Register(
            nameof(ClientConnectionCount), typeof(int), typeof(MainWindow), new PropertyMetadata(default(int)));

        public int ClientConnectionCount
        {
            get { return (int)GetValue(ClientConnectionCountProperty); }
            set { SetValue(ClientConnectionCountProperty, value); }
        }

        public static readonly DependencyProperty ServerConnectionCountProperty = DependencyProperty.Register(
            nameof(ServerConnectionCount), typeof(int), typeof(MainWindow), new PropertyMetadata(default(int)));

        public int ServerConnectionCount
        {
            get { return (int)GetValue(ServerConnectionCountProperty); }
            set { SetValue(ServerConnectionCountProperty, value); }
        }

        private readonly Dictionary<SessionEventArgs, SessionListItem> sessionDictionary = new Dictionary<SessionEventArgs, SessionListItem>();
        private SessionListItem selectedSession;

        public MainWindow()
        {
            proxyServer = new ProxyServer();
            proxyServer.TrustRootCertificate = true;
            proxyServer.ForwardToUpstreamGateway = true;

            var explicitEndPoint = new ExplicitProxyEndPoint(IPAddress.Any, 8000, true)
            {
                //IncludedHttpsHostNameRegex = new string[0],
            };

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
            proxyServer.TunnelConnectRequest += ProxyServer_TunnelConnectRequest;
            proxyServer.TunnelConnectResponse += ProxyServer_TunnelConnectResponse;
            proxyServer.ClientConnectionCountChanged += delegate { Dispatcher.Invoke(() => { ClientConnectionCount = proxyServer.ClientConnectionCount; }); };
            proxyServer.ServerConnectionCountChanged += delegate { Dispatcher.Invoke(() => { ServerConnectionCount = proxyServer.ServerConnectionCount; }); };
            proxyServer.Start();

            proxyServer.SetAsSystemProxy(explicitEndPoint, ProxyProtocolType.AllHttp);

            InitializeComponent();
        }

        private async Task ProxyServer_TunnelConnectRequest(object sender, TunnelConnectSessionEventArgs e)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                AddSession(e);
            });
        }

        private async Task ProxyServer_TunnelConnectResponse(object sender, SessionEventArgs e)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                SessionListItem item;
                if (sessionDictionary.TryGetValue(e, out item))
                {
                    item.Response = e.WebSession.Response;
                    item.Update();
                }
            });
        }

        private async Task ProxyServer_BeforeRequest(object sender, SessionEventArgs e)
        {
            SessionListItem item = null;
            await Dispatcher.InvokeAsync(() =>
            {
                item = AddSession(e);
            });

            if (e.WebSession.Request.HasBody)
            {
                item.RequestBody = await e.GetRequestBody();
            }
        }

        private async Task ProxyServer_BeforeResponse(object sender, SessionEventArgs e)
        {
            SessionListItem item = null;
            await Dispatcher.InvokeAsync(() =>
            {
                SessionListItem item2;
                if (sessionDictionary.TryGetValue(e, out item2))
                {
                    item2.Response = e.WebSession.Response;
                    item2.Update();
                    item = item2;
                }
            });

            if (item != null)
            {
                if (e.WebSession.Response.HasBody)
                {
                    item.ResponseBody = await e.GetResponseBody();
                }
            }
        }

        private SessionListItem AddSession(SessionEventArgs e)
        {
            var item = CreateSessionListItem(e);
            Sessions.Add(item);
            sessionDictionary.Add(e, item);
            return item;
        }

        private SessionListItem CreateSessionListItem(SessionEventArgs e)
        {
            lastSessionNumber++;
            var item = new SessionListItem
            {
                Number = lastSessionNumber,
                SessionArgs = e,
                // save the headers because TWP will set it to null in Dispose
                RequestHeaders = e.WebSession.Request.RequestHeaders,
                ResponseHeaders = e.WebSession.Response.ResponseHeaders,
                Request = e.WebSession.Request,
                Response = e.WebSession.Response,
            };

            if (e is TunnelConnectSessionEventArgs || e.WebSession.Request.UpgradeToWebSocket)
            {
                e.DataReceived += (sender, args) =>
                {
                    var session = (SessionEventArgs)sender;
                    SessionListItem li;
                    if (sessionDictionary.TryGetValue(session, out li))
                    {
                        li.ReceivedDataCount += args.Count;
                    }
                };

                e.DataSent += (sender, args) =>
                {
                    var session = (SessionEventArgs)sender;
                    SessionListItem li;
                    if (sessionDictionary.TryGetValue(session, out li))
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
                    sessionDictionary.Remove(item.SessionArgs);
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

            var session = SelectedSession;
            var data = session.RequestBody ?? new byte[0];
            bool truncated = data.Length > truncateLimit;
            if (truncated)
            {
                data = data.Take(truncateLimit).ToArray();
            }

            //restore the headers
            typeof(Request).GetProperty(nameof(Request.RequestHeaders)).SetValue(session.Request, session.RequestHeaders);
            typeof(Response).GetProperty(nameof(Response.ResponseHeaders)).SetValue(session.Response, session.ResponseHeaders);

            //string hexStr = string.Join(" ", data.Select(x => x.ToString("X2")));
            TextBoxRequest.Text = session.Request.HeaderText + session.Request.Encoding.GetString(data) +
                                  (truncated ? Environment.NewLine + $"Data is truncated after {truncateLimit} bytes" : null) +
                                  (session.Request as ConnectRequest)?.ClientHelloInfo;

            data = session.ResponseBody ?? new byte[0];
            truncated = data.Length > truncateLimit;
            if (truncated)
            {
                data = data.Take(truncateLimit).ToArray();
            }

            //hexStr = string.Join(" ", data.Select(x => x.ToString("X2")));
            TextBoxResponse.Text = session.Response.HeaderText + session.Response.Encoding.GetString(data) +
                                   (truncated ? Environment.NewLine + $"Data is truncated after {truncateLimit} bytes" : null) +
                                   (session.Response as ConnectResponse)?.ServerHelloInfo;
        }
    }
}
