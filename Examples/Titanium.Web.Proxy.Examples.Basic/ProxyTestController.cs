﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Security;
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
        private readonly object lockObj = new object();

        private readonly ProxyServer proxyServer;
        private ExplicitProxyEndPoint explicitEndPoint;

        //keep track of request headers
        private readonly IDictionary<Guid, HeaderCollection> requestHeaderHistory = new ConcurrentDictionary<Guid, HeaderCollection>();

        //keep track of response headers
        private readonly IDictionary<Guid, HeaderCollection> responseHeaderHistory = new ConcurrentDictionary<Guid, HeaderCollection>();

        //share requestBody outside handlers
        //Using a dictionary is not a good idea since it can cause memory overflow
        //ideally the data should be moved out of memory
        //private readonly IDictionary<Guid, string> requestBodyHistory = new ConcurrentDictionary<Guid, string>();

        public ProxyTestController()
        {
            proxyServer = new ProxyServer();

            //generate root certificate without storing it in file system
            //proxyServer.CertificateManager.CreateRootCertificate(false);

            //proxyServer.CertificateManager.TrustRootCertificate();
            //proxyServer.CertificateManager.TrustRootCertificateAsAdmin();

            proxyServer.ExceptionFunc = exception =>
            {
                lock (lockObj)
                {
                    var color = Console.ForegroundColor;
                    Console.ForegroundColor = ConsoleColor.Red;
                    if (exception is ProxyHttpException phex)
                    {
                        Console.WriteLine(exception.Message + ": " + phex.InnerException?.Message);
                    }
                    else
                    {
                        Console.WriteLine(exception.Message);
                    }

                    Console.ForegroundColor = color;
                }
            };
            proxyServer.ForwardToUpstreamGateway = true;
            proxyServer.CertificateManager.SaveFakeCertificates = true;
            //optionally set the Certificate Engine
            //Under Mono or Non-Windows runtimes only BouncyCastle will be supported
            //proxyServer.CertificateManager.CertificateEngine = Network.CertificateEngine.BouncyCastle;

            //optionally set the Root Certificate
            //proxyServer.CertificateManager.RootCertificate = new X509Certificate2("myCert.pfx", string.Empty, X509KeyStorageFlags.Exportable);
        }

        public void StartProxy()
        {
            proxyServer.BeforeRequest += OnRequest;
            proxyServer.BeforeResponse += OnResponse;

            proxyServer.ServerCertificateValidationCallback += OnCertificateValidation;
            proxyServer.ClientCertificateSelectionCallback += OnCertificateSelection;

            //proxyServer.EnableWinAuth = true;

            explicitEndPoint = new ExplicitProxyEndPoint(IPAddress.Any, 8000, true)
            {
                //You can set only one of the ExcludedHttpsHostNameRegex and IncludedHttpsHostNameRegex properties, otherwise ArgumentException will be thrown

                //Use self-issued generic certificate on all https requests
                //Optimizes performance by not creating a certificate for each https-enabled domain
                //Useful when certificate trust is not required by proxy clients
                //GenericCertificate = new X509Certificate2(Path.Combine(System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location), "genericcert.pfx"), "password")
            };

            //Fired when a CONNECT request is received
            explicitEndPoint.BeforeTunnelConnectRequest += OnBeforeTunnelConnectRequest;
            explicitEndPoint.BeforeTunnelConnectResponse += OnBeforeTunnelConnectResponse;

            //An explicit endpoint is where the client knows about the existence of a proxy
            //So client sends request in a proxy friendly manner
            proxyServer.AddEndPoint(explicitEndPoint);
            proxyServer.Start();

            //Transparent endpoint is useful for reverse proxy (client is not aware of the existence of proxy)
            //A transparent endpoint usually requires a network router port forwarding HTTP(S) packets or DNS
            //to send data to this endPoint
            //var transparentEndPoint = new TransparentProxyEndPoint(IPAddress.Any, 443, true)
            //{
            //    //Generic Certificate hostname to use
            //    //When SNI is disabled by client
            //    GenericCertificateName = "google.com"
            //};

            //proxyServer.AddEndPoint(transparentEndPoint);

            //proxyServer.UpStreamHttpProxy = new ExternalProxy() { HostName = "localhost", Port = 8888 };
            //proxyServer.UpStreamHttpsProxy = new ExternalProxy() { HostName = "localhost", Port = 8888 };

            foreach (var endPoint in proxyServer.ProxyEndPoints)
                Console.WriteLine("Listening on '{0}' endpoint at Ip {1} and port: {2} ", endPoint.GetType().Name, endPoint.IpAddress, endPoint.Port);

#if NETSTANDARD2_0
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
#endif
            {
                //Only explicit proxies can be set as system proxy!
                //proxyServer.SetAsSystemHttpProxy(explicitEndPoint);
                //proxyServer.SetAsSystemHttpsProxy(explicitEndPoint);
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

            //remove the generated certificates
            //proxyServer.CertificateManager.RemoveTrustedRootCertificates();
        }

        private async Task OnBeforeTunnelConnectRequest(object sender, TunnelConnectSessionEventArgs e)
        {
            string hostname = e.WebSession.Request.RequestUri.Host;
            WriteToConsole("Tunnel to: " + hostname);

            if (hostname.Contains("dropbox.com"))
            {
                //Exclude Https addresses you don't want to proxy
                //Useful for clients that use certificate pinning
                //for example dropbox.com
                e.Excluded = true;
            }
        }

        private async Task OnBeforeTunnelConnectResponse(object sender, TunnelConnectSessionEventArgs e)
        {
        }

        //intecept & cancel redirect or update requests
        private async Task OnRequest(object sender, SessionEventArgs e)
        {
            WriteToConsole("Active Client Connections:" + ((ProxyServer)sender).ClientConnectionCount);
            WriteToConsole(e.WebSession.Request.Url);

            //read request headers
            requestHeaderHistory[e.Id] = e.WebSession.Request.Headers;

            ////This sample shows how to get the multipart form data headers
            //if (e.WebSession.Request.Host == "mail.yahoo.com" && e.WebSession.Request.IsMultipartFormData)
            //{
            //    e.MultipartRequestPartSent += MultipartRequestPartSent;
            //}

            //if (e.WebSession.Request.HasBody)
            //{
            //    //Get/Set request body bytes
            //    var bodyBytes = await e.GetRequestBody();
            //    await e.SetRequestBody(bodyBytes);

            //    //Get/Set request body as string
            //    string bodyString = await e.GetRequestBodyAsString();
            //    await e.SetRequestBodyString(bodyString);

            //    //requestBodyHistory[e.Id] = bodyString;
            //}

            //To cancel a request with a custom HTML content
            //Filter URL
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

        //Modify response
        private void MultipartRequestPartSent(object sender, MultipartRequestPartSentEventArgs e)
        {
            var session = (SessionEventArgs)sender;
            WriteToConsole("Multipart form data headers:");
            foreach (var header in e.Headers)
            {
                WriteToConsole(header.ToString());
            }
        }

        private async Task OnResponse(object sender, SessionEventArgs e)
        {
            WriteToConsole("Active Server Connections:" + ((ProxyServer)sender).ServerConnectionCount);

            //if (requestBodyHistory.ContainsKey(e.Id))
            //{
            //    //access request body by looking up the shared dictionary using requestId
            //    var requestBody = requestBodyHistory[e.Id];
            //}

            ////read response headers
            //responseHeaderHistory[e.Id] = e.WebSession.Response.Headers;

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
        /// Allows overriding default certificate validation logic
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        public Task OnCertificateValidation(object sender, CertificateValidationEventArgs e)
        {
            //set IsValid to true/false based on Certificate Errors
            if (e.SslPolicyErrors == SslPolicyErrors.None)
            {
                e.IsValid = true;
            }

            return Task.FromResult(0);
        }

        /// <summary>
        /// Allows overriding default client certificate selection logic during mutual authentication
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        public Task OnCertificateSelection(object sender, CertificateSelectionEventArgs e)
        {
            //set e.clientCertificate to override

            return Task.FromResult(0);
        }

        private void WriteToConsole(string message)
        {
            lock (lockObj)
            {
                Console.WriteLine(message);
            }
        }
    }
}
