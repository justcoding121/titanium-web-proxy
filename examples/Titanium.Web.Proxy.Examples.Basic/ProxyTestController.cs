using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Security;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.Examples.Basic
{
    public class ProxyTestController
    {
        private readonly SemaphoreSlim @lock = new SemaphoreSlim(1);
        private readonly ProxyServer proxyServer;
        private ExplicitProxyEndPoint explicitEndPoint;

        public ProxyTestController()
        {
            proxyServer = new ProxyServer();
            // generate root certificate without storing it in file system
            //proxyServer.CertificateManager.CreateRootCertificate(false);

            //proxyServer.CertificateManager.TrustRootCertificate();
            //proxyServer.CertificateManager.TrustRootCertificateAsAdmin();

            proxyServer.ExceptionFunc = async exception =>
            {
                if (exception is ProxyHttpException phex)
                {
                    await WriteToConsole(exception.Message + ": " + phex.InnerException?.Message, ConsoleColor.Red);
                }
                else
                {
                    await WriteToConsole(exception.Message, ConsoleColor.Red);
                }
            };
            proxyServer.ForwardToUpstreamGateway = true;
            proxyServer.CertificateManager.SaveFakeCertificates = true;

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
            // A transparent endpoint usually requires a network router port forwarding HTTP(S) packets or DNS
            // to send data to this endPoint
            //var transparentEndPoint = new TransparentProxyEndPoint(IPAddress.Any, 443, true)
            //{ 
            //    // Generic Certificate hostname to use
            //    // When SNI is disabled by client
            //    GenericCertificateName = "google.com"
            //};

            //proxyServer.AddEndPoint(transparentEndPoint);
            //proxyServer.UpStreamHttpProxy = new ExternalProxy() { HostName = "localhost", Port = 8888 };
            //proxyServer.UpStreamHttpsProxy = new ExternalProxy() { HostName = "localhost", Port = 8888 };

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

        private async Task OnBeforeTunnelConnectRequest(object sender, TunnelConnectSessionEventArgs e)
        {
            string hostname = e.WebSession.Request.RequestUri.Host;
            await WriteToConsole("Tunnel to: " + hostname);

            if (hostname.Contains("dropbox.com"))
            {
                // Exclude Https addresses you don't want to proxy
                // Useful for clients that use certificate pinning
                // for example dropbox.com
                e.DecryptSsl = false;
            }
        }

        private Task OnBeforeTunnelConnectResponse(object sender, TunnelConnectSessionEventArgs e)
        {
            return Task.FromResult(false);
        }

        // intecept & cancel redirect or update requests
        private async Task OnRequest(object sender, SessionEventArgs e)
        {
            await WriteToConsole("Active Client Connections:" + ((ProxyServer)sender).ClientConnectionCount);
            await WriteToConsole(e.WebSession.Request.Url);

            // store it in the UserData property
            // It can be a simple integer, Guid, or any type
            //e.UserData = new CustomUserData()
            //{
            //    RequestHeaders = e.WebSession.Request.Headers,
            //    RequestBody = e.WebSession.Request.HasBody ? e.WebSession.Request.Body:null,
            //    RequestBodyString = e.WebSession.Request.HasBody? e.WebSession.Request.BodyString:null
            //};

            ////This sample shows how to get the multipart form data headers
            //if (e.WebSession.Request.Host == "mail.yahoo.com" && e.WebSession.Request.IsMultipartFormData)
            //{
            //    e.MultipartRequestPartSent += MultipartRequestPartSent;
            //}

            // To cancel a request with a custom HTML content
            // Filter URL
            //if (e.WebSession.Request.RequestUri.AbsoluteUri.Contains("yahoo.com"))
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
            //if (e.WebSession.Request.RequestUri.AbsoluteUri.Contains("wikipedia.org"))
            //{ 
            //   e.Redirect("https://www.paypal.com");
            //} 
        }

        // Modify response
        private async Task MultipartRequestPartSent(object sender, MultipartRequestPartSentEventArgs e)
        {
            var session = (SessionEventArgs)sender;
            await WriteToConsole("Multipart form data headers:");
            foreach (var header in e.Headers)
            {
                await WriteToConsole(header.ToString());
            }
        }

        private async Task OnResponse(object sender, SessionEventArgs e)
        {
            await WriteToConsole("Active Server Connections:" + ((ProxyServer)sender).ServerConnectionCount);

            string ext = System.IO.Path.GetExtension(e.WebSession.Request.RequestUri.AbsolutePath);

            //access user data set in request to do something with it
            //var userData = e.WebSession.UserData as CustomUserData;

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
            //    response.HttpVersion = e.WebSession.Request.HttpVersion;

            //    e.Respond(response);
            //    e.TerminateServerConnection();
            //} 

            //// print out process id of current session
            ////WriteToConsole($"PID: {e.WebSession.ProcessId.Value}");

            ////if (!e.ProxySession.Request.Host.Equals("medeczane.sgk.gov.tr")) return;
            //if (e.WebSession.Request.Method == "GET" || e.WebSession.Request.Method == "POST")
            //{ 
            //    if (e.WebSession.Response.StatusCode == (int)HttpStatusCode.OK)
            //    {
            //        if (e.WebSession.Response.ContentType != null && e.WebSession.Response.ContentType.Trim().ToLower().Contains("text/html"))
            //        {
            //            var bodyBytes = await e.GetResponseBody();
            //            await e.SetResponseBody(bodyBytes);

            //            string body = await e.GetResponseBodyAsString();
            //            await e.SetResponseBodyString(body);
            //        }
            //    }
            //} 
        }

        /// <summary>
        ///     Allows overriding default certificate validation logic
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        public Task OnCertificateValidation(object sender, CertificateValidationEventArgs e)
        {
            // set IsValid to true/false based on Certificate Errors
            if (e.SslPolicyErrors == SslPolicyErrors.None)
            {
                e.IsValid = true;
            }

            return Task.FromResult(0);
        }

        /// <summary>
        ///     Allows overriding default client certificate selection logic during mutual authentication
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        public Task OnCertificateSelection(object sender, CertificateSelectionEventArgs e)
        {
            // set e.clientCertificate to override

            return Task.FromResult(0);
        }

        private async Task WriteToConsole(string message)
        {
            await @lock.WaitAsync();

            ConsoleColor color;

            try
            {
                color = Console.ForegroundColor;
            }
            finally
            {
                @lock.Release();
            }

            await WriteToConsole(message, color);
        }

        private async Task WriteToConsole(string message, ConsoleColor color)
        {
            await @lock.WaitAsync();

            ConsoleColor existing = Console.ForegroundColor;

            try
            {
                Console.ForegroundColor = color;
                Console.WriteLine(message);
            }
            finally
            {
                Console.ForegroundColor = existing;
                @lock.Release();
            }

        }

        ///// <summary>
        ///// User data object as defined by user.
        ///// User data can be set to each SessionEventArgs.WebSession.UserData property
        ///// </summary>
        //public class CustomUserData
        //{
        //    public HeaderCollection RequestHeaders { get; set; }
        //    public byte[] RequestBody { get; set; }
        //    public string RequestBodyString { get; set; }
        //}
    }
}
