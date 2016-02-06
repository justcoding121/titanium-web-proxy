using System;
using System.Collections.Generic;
using System.Net;
using System.Text.RegularExpressions;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.Test
{
    public class ProxyTestController
    {


        public void StartProxy()
        {
            ProxyServer.BeforeRequest += OnRequest;
            ProxyServer.BeforeResponse += OnResponse;

            //Exclude Https addresses you don't want to proxy
            //Usefull for clients that use certificate pinning
            //for example dropbox.com
            var explicitEndPoint = new ExplicitProxyEndPoint(IPAddress.Any, 8000, true){
                ExcludedHttpsHostNameRegex = new List<string>() { "dropbox.com" }
            };

            //An explicit endpoint is where the client knows about the existance of a proxy
            //So client sends request in a proxy friendly manner
            ProxyServer.AddEndPoint(explicitEndPoint);
            ProxyServer.Start();

          
            //Transparent endpoint is usefull for reverse proxying (client is not aware of the existance of proxy)
            //A transparent endpoint usually requires a network router port forwarding HTTP(S) packets to this endpoint
            //Currently do not support Server Name Indication (It is not currently supported by SslStream class)
            //That means that the transparent endpoint will always provide the same Generic Certificate to all HTTPS requests
            //In this example only google.com will work for HTTPS requests
            //Other sites will receive a certificate mismatch warning on browser
            //Please read about it before asking questions!
            var transparentEndPoint = new TransparentProxyEndPoint(IPAddress.Any, 8001, true) { 
                GenericCertificateName = "google.com"
            };         
            ProxyServer.AddEndPoint(transparentEndPoint);
          

            foreach (var endPoint in ProxyServer.ProxyEndPoints)
                Console.WriteLine("Listening on '{0}' endpoint at Ip {1} and port: {2} ", 
                    endPoint.GetType().Name, endPoint.IpAddress, endPoint.Port);

            //You can also add/remove end points after proxy has been started
            ProxyServer.RemoveEndPoint(transparentEndPoint);

            //Only explicit proxies can be set as system proxy!
            ProxyServer.SetAsSystemHttpProxy(explicitEndPoint);
            ProxyServer.SetAsSystemHttpsProxy(explicitEndPoint);
        }

        public void Stop()
        {
            ProxyServer.BeforeRequest -= OnRequest;
            ProxyServer.BeforeResponse -= OnResponse;

            ProxyServer.Stop();
        }

        //Test On Request, intecept requests
        //Read browser URL send back to proxy by the injection script in OnResponse event
        public void OnRequest(object sender, SessionEventArgs e)
        {
            Console.WriteLine(e.ProxySession.Request.Url);

            ////read request headers
            //var requestHeaders = e.ProxySession.Request.RequestHeaders;

            //if ((e.RequestMethod.ToUpper() == "POST" || e.RequestMethod.ToUpper() == "PUT"))
            //{
            //    //Get/Set request body bytes
            //    byte[] bodyBytes = e.GetRequestBody();
            //    e.SetRequestBody(bodyBytes);

            //    //Get/Set request body as string
            //    string bodyString = e.GetRequestBodyAsString();
            //    e.SetRequestBodyString(bodyString);

            //}

            ////To cancel a request with a custom HTML content
            ////Filter URL

            //if (e.ProxySession.Request.RequestUrl.Contains("google.com"))
            //{
            //    e.Ok("<!DOCTYPE html><html><body><h1>Website Blocked</h1><p>Blocked by titanium web proxy.</p></body></html>");
            //}
        }

        //Test script injection
        //Insert script to read the Browser URL and send it back to proxy
        public void OnResponse(object sender, SessionEventArgs e)
        {
            
            ////read response headers
           // var responseHeaders = e.ProxySession.Response.ResponseHeaders;
          
            //if (!e.ProxySession.Request.Hostname.Equals("medeczane.sgk.gov.tr")) return;
            //if (e.RequestMethod == "GET" || e.RequestMethod == "POST")
            //{
            //    if (e.ProxySession.Response.ResponseStatusCode == "200")
            //    {
            //        if (e.ProxySession.Response.ContentType.Trim().ToLower().Contains("text/html"))
            //        {
            //            string body = e.GetResponseBodyAsString(); 
            //        }
            //    }
            //}
        }
    }
}