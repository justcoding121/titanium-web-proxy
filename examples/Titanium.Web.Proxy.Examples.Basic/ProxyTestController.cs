using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Examples.Basic.Helpers;
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.StreamExtended.Network;

namespace Titanium.Web.Proxy.Examples.Basic
{
    public class ProxyTestController : IDisposable
    {
        private readonly ProxyServer proxyServer;
        private ExplicitProxyEndPoint explicitEndPoint;

        private CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();
        private CancellationToken CancellationToken => cancellationTokenSource.Token;
        private ConcurrentQueue<Tuple<ConsoleColor?, string>> consoleMessageQueue
            = new ConcurrentQueue<Tuple<ConsoleColor?, string>>();

        public ProxyTestController()
        {
            Task.Run(() => ListenToConsole());

            proxyServer = new ProxyServer();

            //proxyServer.EnableHttp2 = true;

            // generate root certificate without storing it in file system
            //proxyServer.CertificateManager.CreateRootCertificate(false);

            //proxyServer.CertificateManager.TrustRootCertificate();
            //proxyServer.CertificateManager.TrustRootCertificateAsAdmin();

            proxyServer.ExceptionFunc = async exception =>
            {
                if (exception is ProxyHttpException phex)
                {
                    WriteToConsole(exception.Message + ": " + phex.InnerException?.Message, ConsoleColor.Red);
                }
                else
                {
                    WriteToConsole(exception.Message, ConsoleColor.Red);
                }
            };

            proxyServer.TcpTimeWaitSeconds = 10;
            proxyServer.ConnectionTimeOutSeconds = 15;
            proxyServer.ReuseSocket = false;
            proxyServer.EnableConnectionPool = false;
            proxyServer.ForwardToUpstreamGateway = true;
            proxyServer.CertificateManager.SaveFakeCertificates = true;
            //proxyServer.ProxyBasicAuthenticateFunc = async (args, userName, password) =>
            //{
            //    return true;
            //};

            // this is just to show the functionality, provided implementations use junk value
            //proxyServer.GetCustomUpStreamProxyFunc = onGetCustomUpStreamProxyFunc;
            //proxyServer.CustomUpStreamProxyFailureFunc = onCustomUpStreamProxyFailureFunc;

            // optionally set the Certificate Engine
            // Under Mono or Non-Windows runtimes only BouncyCastle will be supported
            //proxyServer.CertificateManager.CertificateEngine = Network.CertificateEngine.BouncyCastle;

            // optionally set the Root Certificate
            //proxyServer.CertificateManager.RootCertificate = new X509Certificate2("myCert.pfx", string.Empty, X509KeyStorageFlags.Exportable);
        }

        public void StartProxy()
        {
            proxyServer.BeforeRequest += OnRequest;
            proxyServer.BeforeResponse += OnResponse;
            proxyServer.AfterResponse += OnAfterResponse;

            proxyServer.ServerCertificateValidationCallback += OnCertificateValidation;
            proxyServer.ClientCertificateSelectionCallback += OnCertificateSelection;

            //proxyServer.EnableWinAuth = true;

            explicitEndPoint = new ExplicitProxyEndPoint(IPAddress.Any, 8000);

            // Fired when a CONNECT request is received
            explicitEndPoint.BeforeTunnelConnectRequest += OnBeforeTunnelConnectRequest;
            explicitEndPoint.BeforeTunnelConnectResponse += OnBeforeTunnelConnectResponse;

            // An explicit endpoint is where the client knows about the existence of a proxy
            // So client sends request in a proxy friendly manner
            proxyServer.AddEndPoint(explicitEndPoint);
            proxyServer.Start();

            // Transparent endpoint is useful for reverse proxy (client is not aware of the existence of proxy)
            // A transparent endpoint usually requires a network router port forwarding HTTP(S) packets
            // or by DNS to send data to this endPoint.
            //var transparentEndPoint = new TransparentProxyEndPoint(IPAddress.Any, 443, true)
            //{
            //    // Generic Certificate hostname to use
            //    // When SNI is disabled by client
            //    GenericCertificateName = "localhost"
            //};

            //proxyServer.AddEndPoint(transparentEndPoint);
            //proxyServer.UpStreamHttpProxy = new ExternalProxy("localhost", 8888);
            //proxyServer.UpStreamHttpsProxy = new ExternalProxy("localhost", 8888);

            // SOCKS proxy
            //proxyServer.UpStreamHttpProxy = new ExternalProxy("127.0.0.1", 1080)
            //    { ProxyType = ExternalProxyType.Socks5, UserName = "User1", Password = "Pass" };
            //proxyServer.UpStreamHttpsProxy = new ExternalProxy("127.0.0.1", 1080)
            //    { ProxyType = ExternalProxyType.Socks5, UserName = "User1", Password = "Pass" };


            //var socksEndPoint = new SocksProxyEndPoint(IPAddress.Any, 1080, true)
            //{
            //    // Generic Certificate hostname to use
            //    // When SNI is disabled by client
            //    GenericCertificateName = "google.com"
            //};

            //proxyServer.AddEndPoint(socksEndPoint);

            foreach (var endPoint in proxyServer.ProxyEndPoints)
            {
                Console.WriteLine("Listening on '{0}' endpoint at Ip {1} and port: {2} ", endPoint.GetType().Name,
                    endPoint.IpAddress, endPoint.Port);
            }

            // Only explicit proxies can be set as system proxy!
            //proxyServer.SetAsSystemHttpProxy(explicitEndPoint);
            //proxyServer.SetAsSystemHttpsProxy(explicitEndPoint);
            if (RunTime.IsWindows)
            {
                proxyServer.SetAsSystemProxy(explicitEndPoint, ProxyProtocolType.AllHttp);
            }
        }

        public void Stop()
        {
            explicitEndPoint.BeforeTunnelConnectRequest -= OnBeforeTunnelConnectRequest;
            explicitEndPoint.BeforeTunnelConnectResponse -= OnBeforeTunnelConnectResponse;

            proxyServer.BeforeRequest -= OnRequest;
            proxyServer.BeforeResponse -= OnResponse;
            proxyServer.ServerCertificateValidationCallback -= OnCertificateValidation;
            proxyServer.ClientCertificateSelectionCallback -= OnCertificateSelection;

            proxyServer.Stop();

            // remove the generated certificates
            //proxyServer.CertificateManager.RemoveTrustedRootCertificates();
        }

        private async Task<IExternalProxy> OnGetCustomUpStreamProxyFunc(SessionEventArgsBase arg)
        {
            arg.GetState().PipelineInfo.AppendLine(nameof(OnGetCustomUpStreamProxyFunc));

            // this is just to show the functionality, provided values are junk
            return new ExternalProxy
            {
                BypassLocalhost = false,
                HostName = "127.0.0.9",
                Port = 9090,
                Password = "fake",
                UserName = "fake",
                UseDefaultCredentials = false
            };
        }

        private async Task<IExternalProxy> OnCustomUpStreamProxyFailureFunc(SessionEventArgsBase arg)
        {
            arg.GetState().PipelineInfo.AppendLine(nameof(OnCustomUpStreamProxyFailureFunc));

            // this is just to show the functionality, provided values are junk
            return new ExternalProxy
            {
                BypassLocalhost = false,
                HostName = "127.0.0.10",
                Port = 9191,
                Password = "fake2",
                UserName = "fake2",
                UseDefaultCredentials = false
            };
        }

        private async Task OnBeforeTunnelConnectRequest(object sender, TunnelConnectSessionEventArgs e)
        {
            string hostname = e.HttpClient.Request.RequestUri.Host;
            e.GetState().PipelineInfo.AppendLine(nameof(OnBeforeTunnelConnectRequest) + ":" + hostname);
            WriteToConsole("Tunnel to: " + hostname);

            var clientLocalIp = e.ClientLocalEndPoint.Address;
            if (!clientLocalIp.Equals(IPAddress.Loopback) && !clientLocalIp.Equals(IPAddress.IPv6Loopback))
            {
                e.HttpClient.UpStreamEndPoint = new IPEndPoint(clientLocalIp, 0);
            }

            if (hostname.Contains("dropbox.com"))
            {
                // Exclude Https addresses you don't want to proxy
                // Useful for clients that use certificate pinning
                // for example dropbox.com
                e.DecryptSsl = false;
            }
        }

        private void WebSocket_DataSent(object sender, DataEventArgs e)
        {
            var args = (SessionEventArgs)sender;
            WebSocketDataSentReceived(args, e, true);
        }

        private void WebSocket_DataReceived(object sender, DataEventArgs e)
        {
            var args = (SessionEventArgs)sender;
            WebSocketDataSentReceived(args, e, false);
        }

        private void WebSocketDataSentReceived(SessionEventArgs args, DataEventArgs e, bool sent)
        {
            var color = sent ? ConsoleColor.Green : ConsoleColor.Blue;

            foreach (var frame in args.WebSocketDecoder.Decode(e.Buffer, e.Offset, e.Count))
            {
                if (frame.OpCode == WebsocketOpCode.Binary)
                {
                    var data = frame.Data.ToArray();
                    string str = string.Join(",", data.ToArray().Select(x => x.ToString("X2")));
                    WriteToConsole(str, color);
                }

                if (frame.OpCode == WebsocketOpCode.Text)
                {
                    WriteToConsole(frame.GetText(), color);
                }
            }
        }

        private Task OnBeforeTunnelConnectResponse(object sender, TunnelConnectSessionEventArgs e)
        {
            e.GetState().PipelineInfo.AppendLine(nameof(OnBeforeTunnelConnectResponse) + ":" + e.HttpClient.Request.RequestUri);

            return Task.CompletedTask;
        }

        // intercept & cancel redirect or update requests
        private async Task OnRequest(object sender, SessionEventArgs e)
        {
            e.GetState().PipelineInfo.AppendLine(nameof(OnRequest) + ":" + e.HttpClient.Request.RequestUri);

            var clientLocalIp = e.ClientLocalEndPoint.Address;
            if (!clientLocalIp.Equals(IPAddress.Loopback) && !clientLocalIp.Equals(IPAddress.IPv6Loopback))
            {
                e.HttpClient.UpStreamEndPoint = new IPEndPoint(clientLocalIp, 0);
            }

            if (e.HttpClient.Request.Url.Contains("yahoo.com"))
            {
                e.CustomUpStreamProxy = new ExternalProxy("localhost", 8888);
            }

            WriteToConsole("Active Client Connections:" + ((ProxyServer)sender).ClientConnectionCount);
            WriteToConsole(e.HttpClient.Request.Url);

            // store it in the UserData property
            // It can be a simple integer, Guid, or any type
            //e.UserData = new CustomUserData()
            //{
            //    RequestHeaders = e.HttpClient.Request.Headers,
            //    RequestBody = e.HttpClient.Request.HasBody ? e.HttpClient.Request.Body:null,
            //    RequestBodyString = e.HttpClient.Request.HasBody? e.HttpClient.Request.BodyString:null
            //};

            ////This sample shows how to get the multipart form data headers
            //if (e.HttpClient.Request.Host == "mail.yahoo.com" && e.HttpClient.Request.IsMultipartFormData)
            //{
            //    e.MultipartRequestPartSent += MultipartRequestPartSent;
            //}

            // To cancel a request with a custom HTML content
            // Filter URL
            //if (e.HttpClient.Request.RequestUri.AbsoluteUri.Contains("yahoo.com"))
            //{ 
            //    e.Ok("<!DOCTYPE html>" +
            //          "<html><body><h1>" +
            //          "Website Blocked" +
            //          "</h1>" +
            //          "<p>Blocked by titanium web proxy.</p>" +
            //          "</body>" +
            //          "</html>");
            //} 

            ////Redirect example
            //if (e.HttpClient.Request.RequestUri.AbsoluteUri.Contains("wikipedia.org"))
            //{ 
            //   e.Redirect("https://www.paypal.com");
            //} 
        }

        // Modify response
        private async Task MultipartRequestPartSent(object sender, MultipartRequestPartSentEventArgs e)
        {
            e.GetState().PipelineInfo.AppendLine(nameof(MultipartRequestPartSent));

            var session = (SessionEventArgs)sender;
            WriteToConsole("Multipart form data headers:");
            foreach (var header in e.Headers)
            {
                WriteToConsole(header.ToString());
            }
        }

        private async Task OnResponse(object sender, SessionEventArgs e)
        {
            e.GetState().PipelineInfo.AppendLine(nameof(OnResponse));

            if (e.HttpClient.ConnectRequest?.TunnelType == TunnelType.Websocket)
            {
                e.DataSent += WebSocket_DataSent;
                e.DataReceived += WebSocket_DataReceived;
            }

            WriteToConsole("Active Server Connections:" + ((ProxyServer)sender).ServerConnectionCount);

            string ext = System.IO.Path.GetExtension(e.HttpClient.Request.RequestUri.AbsolutePath);

            // access user data set in request to do something with it
            //var userData = e.HttpClient.UserData as CustomUserData;

            //if (ext == ".gif" || ext == ".png" || ext == ".jpg")
            //{ 
            //    byte[] btBody = Encoding.UTF8.GetBytes("<!DOCTYPE html>" +
            //                                           "<html><body><h1>" +
            //                                           "Image is blocked" +
            //                                           "</h1>" +
            //                                           "<p>Blocked by Titanium</p>" +
            //                                           "</body>" +
            //                                           "</html>");

            //    var response = new OkResponse(btBody);
            //    response.HttpVersion = e.HttpClient.Request.HttpVersion;

            //    e.Respond(response);
            //    e.TerminateServerConnection();
            //} 

            //// print out process id of current session
            ////WriteToConsole($"PID: {e.HttpClient.ProcessId.Value}");

            ////if (!e.ProxySession.Request.Host.Equals("medeczane.sgk.gov.tr")) return;
            //if (e.HttpClient.Request.Method == "GET" || e.HttpClient.Request.Method == "POST")
            //{
            //    if (e.HttpClient.Response.StatusCode == (int)HttpStatusCode.OK)
            //    {
            //        if (e.HttpClient.Response.ContentType != null && e.HttpClient.Response.ContentType.Trim().ToLower().Contains("text/html"))
            //        {
            //            var bodyBytes = await e.GetResponseBody();
            //            e.SetResponseBody(bodyBytes);

            //            string body = await e.GetResponseBodyAsString();
            //            e.SetResponseBodyString(body);
            //        }
            //    }
            //}
        }

        private async Task OnAfterResponse(object sender, SessionEventArgs e)
        {
            WriteToConsole($"Pipelineinfo: {e.GetState().PipelineInfo}", ConsoleColor.Yellow);
        }

        /// <summary>
        ///     Allows overriding default certificate validation logic
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        public Task OnCertificateValidation(object sender, CertificateValidationEventArgs e)
        {
            e.GetState().PipelineInfo.AppendLine(nameof(OnCertificateValidation));

            // set IsValid to true/false based on Certificate Errors
            if (e.SslPolicyErrors == SslPolicyErrors.None)
            {
                e.IsValid = true;
            }

            return Task.CompletedTask;
        }

        /// <summary>
        ///     Allows overriding default client certificate selection logic during mutual authentication
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        public Task OnCertificateSelection(object sender, CertificateSelectionEventArgs e)
        {
            e.GetState().PipelineInfo.AppendLine(nameof(OnCertificateSelection));

            // set e.clientCertificate to override

            return Task.CompletedTask;
        }

        private void WriteToConsole(string message, ConsoleColor? consoleColor = null)
        {
            consoleMessageQueue.Enqueue(new Tuple<ConsoleColor?, string>(consoleColor, message));
        }

        private async Task ListenToConsole()
        {
            while (!CancellationToken.IsCancellationRequested)
            {
                while (consoleMessageQueue.TryDequeue(out var item))
                {
                    var consoleColor = item.Item1;
                    var message = item.Item2;

                    if (consoleColor.HasValue)
                    {
                        ConsoleColor existing = Console.ForegroundColor;
                        Console.ForegroundColor = consoleColor.Value;
                        Console.WriteLine(message);
                        Console.ForegroundColor = existing;
                    }
                    else
                    {
                        Console.WriteLine(message);
                    }
                }

                //reduce CPU usage
                await Task.Delay(50);
            }
        }

        public void Dispose()
        {
            cancellationTokenSource.Dispose();
            proxyServer.Dispose();
        }

        ///// <summary>
        ///// User data object as defined by user.
        ///// User data can be set to each SessionEventArgs.HttpClient.UserData property
        ///// </summary>
        //public class CustomUserData
        //{
        //    public HeaderCollection RequestHeaders { get; set; }
        //    public byte[] RequestBody { get; set; }
        //    public string RequestBodyString { get; set; }
        //}
    }
}
