using System;
using System.Net;
using System.Threading.Tasks;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.Examples.Basic
{
    public class ProxyTestController
    {
        private ProxyServer proxyServer;

        public ProxyTestController()
        {
            proxyServer = new ProxyServer();
            proxyServer.TrustRootCertificate = true;
        }

        public void StartProxy()
        {
            proxyServer.BeforeRequest += OnRequest;
            proxyServer.BeforeResponse += OnResponse;
            proxyServer.ServerCertificateValidationCallback += OnCertificateValidation;
            proxyServer.ClientCertificateSelectionCallback += OnCertificateSelection;

            //Exclude Https addresses you don't want to proxy
            //Usefull for clients that use certificate pinning
            //for example dropbox.com
            var explicitEndPoint = new ExplicitProxyEndPoint(IPAddress.Any, 8000, true)
            {
                // ExcludedHttpsHostNameRegex = new List<string>() { "google.com", "dropbox.com" }
            };

            //An explicit endpoint is where the client knows about the existance of a proxy
            //So client sends request in a proxy friendly manner
            proxyServer.AddEndPoint(explicitEndPoint);
            proxyServer.Start();


            //Transparent endpoint is usefull for reverse proxying (client is not aware of the existance of proxy)
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
        }

        //intecept & cancel, redirect or update requests
        public async Task OnRequest(object sender, SessionEventArgs e)
        {
            Console.WriteLine(e.WebSession.Request.Url);

            ////read request headers
            var requestHeaders = e.WebSession.Request.RequestHeaders;

            var method = e.WebSession.Request.Method.ToUpper();
            if ((method == "POST" || method == "PUT" || method == "PATCH"))
            {
                //Get/Set request body bytes
                byte[] bodyBytes = await e.GetRequestBody();
                await e.SetRequestBody(bodyBytes);

                //Get/Set request body as string
                string bodyString = await e.GetRequestBodyAsString();
                await e.SetRequestBodyString(bodyString);

            }

            //To cancel a request with a custom HTML content
            //Filter URL
            if (e.WebSession.Request.RequestUri.AbsoluteUri.Contains("google.com"))
            {
                await e.Ok("<!DOCTYPE html>" +
                      "<html><body><h1>" +
                      "Website Blocked" +
                      "</h1>" +
                      "<p>Blocked by titanium web proxy.</p>" +
                      "</body>" +
                      "</html>");
            }
            //Redirect example
            if (e.WebSession.Request.RequestUri.AbsoluteUri.Contains("wikipedia.org"))
            {
                await e.Redirect("https://www.paypal.com");
            }

            // set CustomData to provide state set here to the
            // response handler
            e.CustomData = "Custom data test";
        }

        //Modify response
        public async Task OnResponse(object sender, SessionEventArgs e)
        {
            //read response headers
            var responseHeaders = e.WebSession.Response.ResponseHeaders;

            // print out process id of current session
            Console.WriteLine($"PID: {e.WebSession.ProcessId.Value}");

            //if (!e.ProxySession.Request.Host.Equals("medeczane.sgk.gov.tr")) return;
            if (e.WebSession.Request.Method == "GET" || e.WebSession.Request.Method == "POST")
            {
                if (e.WebSession.Response.ResponseStatusCode == "200")
                {
                    if (e.WebSession.Response.ContentType!=null && e.WebSession.Response.ContentType.Trim().ToLower().Contains("text/html"))
                    {
                        byte[] bodyBytes = await e.GetResponseBody();
                        await e.SetResponseBody(bodyBytes);

                        string body = await e.GetResponseBodyAsString();
                        await e.SetResponseBodyString(body);
                    }
                }
            }

            // retrieve the custom state set in the request handler
            string data = (string)e.CustomData;
        }

        /// <summary>
        /// Allows overriding default certificate validation logic
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        public Task OnCertificateValidation(object sender, CertificateValidationEventArgs e)
        {
            //set IsValid to true/false based on Certificate Errors
            if (e.SslPolicyErrors == System.Net.Security.SslPolicyErrors.None)
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