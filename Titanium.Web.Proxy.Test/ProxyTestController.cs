using System;
using System.Net;
using System.Text.RegularExpressions;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.Test
{
    public class ProxyTestController
    {
        public int ListeningPort { get; set; }
        public bool EnableSsl { get; set; }
        public bool SetAsSystemProxy { get; set; }

        public void StartProxy()
        {
            ProxyServer.BeforeRequest += OnRequest;
            ProxyServer.BeforeResponse += OnResponse;


            //Exclude Https addresses you don't want to proxy
            //Usefull for clients that use certificate pinning
            //for example dropbox.com
            // ProxyServer.ExcludedHttpsHostNameRegex.Add(".dropbox.com");
            var explicitEndPoint = new ExplicitProxyEndPoint { EnableSsl = true, IpAddress = IPAddress.Any, Port = 8000 };
            var transparentEndPoint = new TransparentProxyEndPoint { EnableSsl = true, IpAddress = IPAddress.Loopback, Port = 443 };
            ProxyServer.AddEndPoint(explicitEndPoint);
            ProxyServer.AddEndPoint(transparentEndPoint);
            ProxyServer.Start();
            ProxyServer.SetAsSystemProxy(explicitEndPoint);


           // Console.WriteLine("Proxy listening on local machine port: {0} ", ProxyServer.ListeningPort);
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