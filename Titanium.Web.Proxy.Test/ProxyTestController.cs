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
            var explicitEndPoint = new ExplicitProxyEndPoint(IPAddress.Loopback, 8000, true){
                ExcludedHostNameRegex = new List<string>() { "dropbox.com" }
            };

            var transparentEndPoint = new TransparentProxyEndPoint(IPAddress.Loopback, 8001, true)
            { 
            };

            ProxyServer.AddEndPoint(explicitEndPoint);
            ProxyServer.AddEndPoint(transparentEndPoint);
            ProxyServer.Start();

            foreach (var endPoint in ProxyServer.ProxyEndPoints)
                Console.WriteLine("Listening on '{0}' endpoint at Ip {1} and port: {2} ", endPoint.GetType().Name, endPoint.IpAddress, endPoint.Port);

            ProxyServer.SetAsSystemProxy(explicitEndPoint);

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