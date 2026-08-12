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
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Examples.Shared;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Options;

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
        private readonly bool trustRootCertificate;
        private readonly bool trustRootCertificateMachine;

        public MainWindow()
        {
            // false,false: do not auto-trust on Start/SetAsSystemProxy — trust is opt-in via TWP_TRUST_ROOT.
            proxyServer = new ProxyServer(false, false, false);

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

            // Opt-in MITM root trust (same as Basic):
            //   TWP_TRUST_ROOT=1           → Current User Personal + Trusted Root
            //   TWP_TRUST_ROOT_MACHINE=1   → also Local Machine stores (needs elevation)
            trustRootCertificate = Environment.GetEnvironmentVariable("TWP_TRUST_ROOT") is "1" or "true" or "TRUE";
            trustRootCertificateMachine =
                Environment.GetEnvironmentVariable("TWP_TRUST_ROOT_MACHINE") is "1" or "true" or "TRUE";
            if (trustRootCertificate || trustRootCertificateMachine)
            {
                proxyServer.CertificateManager.EnsureRootCertificate();
                proxyServer.CertificateManager.TrustRootCertificate(machineTrusted: trustRootCertificateMachine);
            }

            // Match ProxyProfile.Balanced / library defaults (same as the Basic example).
            // Speed opt-ins for modern browsers: LeafCertificateKeyAlgorithm = EcdsaP256,
            // SaveFakeCertificates = true — see wiki Home.md Performance.
            proxyServer.Profile = ProxyProfile.Balanced;
            proxyServer.ForwardToUpstreamGateway = true;

            var explicitEndPoint = new ExplicitProxyEndPoint(IPAddress.Any, 8000);

            proxyServer.AddEndPoint(explicitEndPoint);

            // HTTP/3 (experimental — suppress TWP001). Example default is on; library Balanced stays off.
            // Set TWP_ENABLE_HTTP3=0 to disable. Requires MsQuic + supported OS; see wiki/HTTP-3.md.
#pragma warning disable TWP001
            var enableHttp3 = Environment.GetEnvironmentVariable("TWP_ENABLE_HTTP3") switch
            {
                "0" or "false" or "FALSE" => false,
                _ => true
            };
            if (enableHttp3 && QuicListener.IsSupported)
            {
                proxyServer.EnableHttp3 = true;
                // Learn H3 from Alt-Svc; set TWP_ENABLE_SVCB_DNS=1 for proactive HTTPS/SVCB discovery.
                proxyServer.EnableHttpsSvcbDnsDiscovery =
                    Environment.GetEnvironmentVariable("TWP_ENABLE_SVCB_DNS") is "1" or "true" or "TRUE";
                var quicEndPoint = new TransparentQuicProxyEndPoint(IPAddress.Any, 443)
                {
                    ForwardHost = "localhost",
                    ForwardPort = 443
                };
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
                Dispatcher.BeginInvoke(() => { ClientConnectionCount = proxyServer.ClientConnectionCount; });
            };
            proxyServer.ServerConnectionCountChanged += delegate
            {
                Dispatcher.BeginInvoke(() => { ServerConnectionCount = proxyServer.ServerConnectionCount; });
            };
            proxyServer.Http3ClientConnectionCountChanged += delegate
            {
                Dispatcher.BeginInvoke(() => { Http3ClientConnectionCount = proxyServer.Http3ClientConnectionCount; });
            };
            proxyServer.Http3ServerConnectionCountChanged += delegate
            {
                Dispatcher.BeginInvoke(() => { Http3ServerConnectionCount = proxyServer.Http3ServerConnectionCount; });
            };
            proxyServer.Start();

            // Screenshot automation (TWP_CAPTURE_PATH) skips system-proxy registration so CI/desktop
            // capture runs do not alter the machine's proxy settings.
            var capturePath = Environment.GetEnvironmentVariable("TWP_CAPTURE_PATH");
            if (string.IsNullOrWhiteSpace(capturePath))
            {
                proxyServer.SetAsSystemProxy(explicitEndPoint, ProxyProtocolType.AllHttp,
                    KnownMitmExclusions.CreateSystemProxySettings(settings =>
                    {
                        // Route localhost/loopback traffic through the proxy for this example.
                        settings.ProxyLoopback = true;
                    }));
            }

            InitializeComponent();

            // Always clear system proxy when the window closes (graceful or App.Shutdown).
            // Without this, browsers keep pointing at a dead :8000 and hang after exit.
            Closed += (_, _) => ShutdownProxy();

            if (!string.IsNullOrWhiteSpace(capturePath))
            {
                Loaded += async (_, _) =>
                {
                    ToggleSystemProxy.IsChecked = false;
                    await CaptureScreenshotAndExitAsync(capturePath);
                };
            }
        }

        /// <summary>
        ///     Renders this window to JPEG via <see cref="RenderTargetBitmap" /> (works when desktop
        ///     bit-blit cannot see the WPF surface) then shuts down. Used for wiki screenshot refresh.
        /// </summary>
        private async Task CaptureScreenshotAndExitAsync(string capturePath)
        {
            WindowState = WindowState.Maximized;
            var delayMs = 12000;
            if (int.TryParse(Environment.GetEnvironmentVariable("TWP_CAPTURE_DELAY_MS"), out var parsed) &&
                parsed > 0)
                delayMs = parsed;

            // Wait for demo traffic (prefer a completed example.org row with a real upstream id).
            for (var i = 0; i < delayMs / 200; i++)
            {
                await Task.Delay(200);
                var demo = Sessions.FirstOrDefault(s =>
                    !s.IsTunnelConnect &&
                    s.ServerConnectionId.HasValue &&
                    (s.Host?.Contains("example.org", StringComparison.OrdinalIgnoreCase) == true ||
                     s.Url?.Contains("example", StringComparison.OrdinalIgnoreCase) == true));
                if (demo != null)
                {
                    SelectedSession = demo;
                    break;
                }
            }

            if (SelectedSession == null)
            {
                var any = Sessions.FirstOrDefault(s =>
                    !s.IsTunnelConnect && s.ServerConnectionId.HasValue);
                if (any != null) SelectedSession = any;
            }

            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
            await Task.Delay(500);

            var dpi = VisualTreeHelper.GetDpi(this);
            var width = Math.Max(1, (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX));
            var height = Math.Max(1, (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY));
            var rtb = new RenderTargetBitmap(width, height, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY,
                PixelFormats.Pbgra32);
            rtb.Render(this);

            var encoder = new JpegBitmapEncoder { QualityLevel = 90 };
            encoder.Frames.Add(BitmapFrame.Create(rtb));
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(capturePath))!);
            await using (var fs = File.Create(capturePath))
                encoder.Save(fs);

            Application.Current.Shutdown();
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
                if (trustRootCertificate || trustRootCertificateMachine)
                    proxyServer.CertificateManager.RemoveTrustedRootCertificate(
                        machineTrusted: trustRootCertificateMachine);
            }
            catch (Exception)
            {
                // ignored
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

        private async Task ProxyServer_BeforeTunnelConnectRequest(object sender, TunnelConnectSessionEventArgs e)
        {
            var hostname = e.HttpClient.Request.RequestUri.Host;
            if (KnownMitmExclusions.ShouldDisableSslDecrypt(hostname))
                e.DecryptSsl = false;

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
            var item = await Dispatcher.InvokeAsync(() =>
            {
                if (!sessionDictionary.TryGetValue(e.HttpClient, out var found))
                    return null;

                found.Update(e);
                // Prefer showing a real request/response in the detail pane once traffic arrives.
                if (SelectedSession == null && found is { IsTunnelConnect: false })
                    SelectedSession = found;
                return found;
            });

            if (item != null && e.HttpClient.Response.HasBody)
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
                HttpClient = e.HttpClient,
                ClientRemoteEndPoint = e.ClientRemoteEndPoint,
                ClientLocalEndPoint = e.ClientLocalEndPoint,
                IsTunnelConnect = isTunnelConnect
            };

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

        private static string TunnelTypeToString(TunnelType tunnelType)
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
                    ProxyProtocolType.AllHttp,
                    KnownMitmExclusions.CreateSystemProxySettings(settings => settings.ProxyLoopback = true));
            else
                proxyServer.RestoreOriginalProxySettings();
        }
    }
}