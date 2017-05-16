using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Security;
using System.Threading.Tasks;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.Examples.Basic
{
    public class ProxyTestController
    {
        private readonly ProxyServer proxyServer;

        //share requestBody outside handlers
        private readonly Dictionary<Guid, string> requestBodyHistory = new Dictionary<Guid, string>();

        public ProxyTestController()
        {
            proxyServer = new ProxyServer();

            //generate root certificate without storing it in file system
            //proxyServer.CertificateEngine = Network.CertificateEngine.BouncyCastle;
            //proxyServer.CertificateManager.CreateTrustedRootCertificate(false);
            //proxyServer.CertificateManager.TrustRootCertificate();

            proxyServer.ExceptionFunc = exception => Console.WriteLine(exception.Message);
            proxyServer.TrustRootCertificate = true;

            //optionally set the Certificate Engine
            //Under Mono only BouncyCastle will be supported
            //proxyServer.CertificateEngine = Network.CertificateEngine.BouncyCastle;

            //optionally set the Root Certificate
            //proxyServer.RootCertificate = new X509Certificate2("myCert.pfx", string.Empty, X509KeyStorageFlags.Exportable);
        }

        public void StartProxy()
        {
            proxyServer.BeforeRequest += OnRequest;
            proxyServer.BeforeResponse += OnResponse;
            proxyServer.ServerCertificateValidationCallback += OnCertificateValidation;
            proxyServer.ClientCertificateSelectionCallback += OnCertificateSelection;

            var explicitEndPoint = new ExplicitProxyEndPoint(IPAddress.Any, 8000, true)
            {
                //Exclude Https addresses you don't want to proxy
                //Useful for clients that use certificate pinning
                //for example google.com and dropbox.com
                ExcludedHttpsHostNameRegex = new List<string> { "dropbox.com" }

                //Include Https addresses you want to proxy (others will be excluded)
                //for example github.com
                //IncludedHttpsHostNameRegex = new List<string> { "github.com" }

                //You can set only one of the ExcludedHttpsHostNameRegex and IncludedHttpsHostNameRegex properties, otherwise ArgumentException will be thrown

                //Use self-issued generic certificate on all https requests
                //Optimizes performance by not creating a certificate for each https-enabled domain
                //Useful when certificate trust is not required by proxy clients
                //GenericCertificate = new X509Certificate2(Path.Combine(System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location), "genericcert.pfx"), "password")
            };

            //An explicit endpoint is where the client knows about the existence of a proxy
            //So client sends request in a proxy friendly manner
            proxyServer.AddEndPoint(explicitEndPoint);
            proxyServer.Start();


            //Transparent endpoint is useful for reverse proxying (client is not aware of the existence of proxy)
            //A transparent endpoint usually requires a network router port forwarding HTTP(S) packets to this endpoint
            //Currently do not support Server Name Indication (It is not currently supported by SslStream class)
            //That means that the transparent endpoint will always provide the same Generic Certificate to all HTTPS requests
            //In this example only google.com will work for HTTPS requests
            //Other sites will receive a certificate mismatch warning on browser
            var transparentEndPoint = new TransparentProxyEndPoint(IPAddress.Any, 8001, true)
            {
                GenericCertificateName = "google.com"
            };
            proxyServer.AddEndPoint(transparentEndPoint);

            //ProxyServer.UpStreamHttpProxy = new ExternalProxy() { HostName = "localhost", Port = 8888 };
            //ProxyServer.UpStreamHttpsProxy = new ExternalProxy() { HostName = "localhost", Port = 8888 };

            foreach (var endPoint in proxyServer.ProxyEndPoints)
                Console.WriteLine("Listening on '{0}' endpoint at Ip {1} and port: {2} ",
                    endPoint.GetType().Name, endPoint.IpAddress, endPoint.Port);

            //Only explicit proxies can be set as system proxy!
            proxyServer.SetAsSystemHttpProxy(explicitEndPoint);
            proxyServer.SetAsSystemHttpsProxy(explicitEndPoint);
        }

        public void Stop()
        {
            proxyServer.BeforeRequest -= OnRequest;
            proxyServer.BeforeResponse -= OnResponse;
            proxyServer.ServerCertificateValidationCallback -= OnCertificateValidation;
            proxyServer.ClientCertificateSelectionCallback -= OnCertificateSelection;

            proxyServer.Stop();

            //remove the generated certificates
            //proxyServer.CertificateManager.RemoveTrustedRootCertificates();
        }

        //intecept & cancel redirect or update requests
        public async Task OnRequest(object sender, SessionEventArgs e)
        {
            Console.WriteLine("Active Client Connections:" + ((ProxyServer)sender).ClientConnectionCount);
            Console.WriteLine(e.WebSession.Request.Url);

            //read request headers
            var requestHeaders = e.WebSession.Request.RequestHeaders;

            var method = e.WebSession.Request.Method.ToUpper();
            if (method == "POST" || method == "PUT" || method == "PATCH")
            {
                //Get/Set request body bytes
                byte[] bodyBytes = await e.GetRequestBody();
                await e.SetRequestBody(bodyBytes);

                //Get/Set request body as string
                string bodyString = await e.GetRequestBodyAsString();
                await e.SetRequestBodyString(bodyString);

                requestBodyHistory[e.Id] = bodyString;
            }

            ////To cancel a request with a custom HTML content
            ////Filter URL
            //if (e.WebSession.Request.RequestUri.AbsoluteUri.Contains("google.com"))
            //{
            //    await e.Ok("<!DOCTYPE html>" +
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
            //    await e.Redirect("https://www.paypal.com");
            //}
        }

        //Modify response
        public async Task OnResponse(object sender, SessionEventArgs e)
        {
            Console.WriteLine("Active Server Connections:" + ((ProxyServer)sender).ServerConnectionCount);

            if (requestBodyHistory.ContainsKey(e.Id))
            {
                //access request body by looking up the shared dictionary using requestId
                var requestBody = requestBodyHistory[e.Id];
            }

            //read response headers
            var responseHeaders = e.WebSession.Response.ResponseHeaders;

            // print out process id of current session
            //Console.WriteLine($"PID: {e.WebSession.ProcessId.Value}");

            //if (!e.ProxySession.Request.Host.Equals("medeczane.sgk.gov.tr")) return;
            if (e.WebSession.Request.Method == "GET" || e.WebSession.Request.Method == "POST")
            {
                if (e.WebSession.Response.ResponseStatusCode == "200")
                {
                    if (e.WebSession.Response.ContentType != null && e.WebSession.Response.ContentType.Trim().ToLower().Contains("text/html"))
                    {
                        byte[] bodyBytes = await e.GetResponseBody();
                        await e.SetResponseBody(bodyBytes);

                        string body = await e.GetResponseBodyAsString();
                        await e.SetResponseBodyString(body);
                    }
                }
            }
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
    }
}
