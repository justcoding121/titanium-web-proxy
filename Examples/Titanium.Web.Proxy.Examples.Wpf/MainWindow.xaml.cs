using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Helpers;
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

        public ObservableCollection<SessionListItem> Sessions { get; } = new ObservableCollection<SessionListItem>();

        public SessionListItem SelectedSession { get; set; }

        private readonly Dictionary<SessionEventArgs, SessionListItem> sessionDictionary = new Dictionary<SessionEventArgs, SessionListItem>();

        public MainWindow()
        {
            proxyServer = new ProxyServer();
            proxyServer.TrustRootCertificate = true;
            proxyServer.ForwardToUpstreamGateway = true;

            var explicitEndPoint = new ExplicitProxyEndPoint(IPAddress.Any, 8000, true);
            proxyServer.AddEndPoint(explicitEndPoint);
            proxyServer.BeforeRequest += ProxyServer_BeforeRequest;
            proxyServer.BeforeResponse += ProxyServer_BeforeResponse;
            proxyServer.TunnelConnectRequest += ProxyServer_TunnelConnectRequest;
            proxyServer.TunnelConnectResponse += ProxyServer_TunnelConnectResponse;
            proxyServer.Start();

            proxyServer.SetAsSystemProxy(explicitEndPoint, ProxyProtocolType.AllHttp);

            InitializeComponent();
        }

        private async Task ProxyServer_TunnelConnectRequest(object sender, TunnelConnectSessionEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                AddSession(e);
            });
        }

        private async Task ProxyServer_TunnelConnectResponse(object sender, SessionEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                SessionListItem item;
                if (sessionDictionary.TryGetValue(e, out item))
                {
                    item.Update();
                }
            });
        }

        private async Task ProxyServer_BeforeRequest(object sender, SessionEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                AddSession(e);
            });
        }

        private async Task ProxyServer_BeforeResponse(object sender, SessionEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                SessionListItem item;
                if (sessionDictionary.TryGetValue(e, out item))
                {
                    item.Update();
                }
            });
        }

        private void AddSession(SessionEventArgs e)
        {
            var item = CreateSessionListItem(e);
            Sessions.Add(item);
            sessionDictionary.Add(e, item);
        }

        private SessionListItem CreateSessionListItem(SessionEventArgs e)
        {
            lastSessionNumber++;
            var item = new SessionListItem
            {
                Number = lastSessionNumber,
                SessionArgs = e,
            };

            if (e is TunnelConnectSessionEventArgs)
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

        private void Session_DataReceived(object sender, EventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                var session = (SessionEventArgs)sender;
                SessionListItem item;
                if (sessionDictionary.TryGetValue(session, out item))
                {
                    item.Update();
                }
            });
        }

        private void ListViewSessions_OnKeyDown(object sender, KeyEventArgs e)
        {
            var selectedItems = ((ListView)sender).SelectedItems;
            foreach (var item in selectedItems.Cast<SessionListItem>().ToArray())
            {
                Sessions.Remove(item);
                sessionDictionary.Remove(item.SessionArgs);
            }
        }
    }
}
