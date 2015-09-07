using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Net;
using System.Text.RegularExpressions;
using System.DirectoryServices.AccountManagement;
using System.DirectoryServices.ActiveDirectory;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.Helpers;



namespace Titanium.Web.Proxy.Test
{
    public partial class ProxyTestController
    {

        public int ListeningPort { get; set; }
        public bool EnableSSL { get; set; }
        public bool SetAsSystemProxy { get; set; }

        public void StartProxy()
        {

            ProxyServer.BeforeRequest += OnRequest;
            ProxyServer.BeforeResponse += OnResponse;

            ProxyServer.EnableSSL = EnableSSL;

            ProxyServer.SetAsSystemProxy = SetAsSystemProxy;

            //Exclude Https addresses you don't want to proxy
            //Usefull for clients that use certificate pinning
            //for example dropbox.com
            ProxyServer.ExcludedHttpsHostNameRegex.Add(".dropbox.com");

            ProxyServer.Start();

            ProxyServer.ListeningPort = ProxyServer.ListeningPort;

            Console.WriteLine(String.Format("Proxy listening on local machine port: {0} ", ProxyServer.ListeningPort));

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

            Console.WriteLine(e.RequestURL);

            ////modify request headers
            //var requestHeaders = e.RequestHeaders;

            //if ((e.RequestMethod.ToUpper() == "POST" || e.RequestMethod.ToUpper() == "PUT") && e.RequestContentLength > 0)
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

            //if (e.RequestURL.Contains("google.com"))
            //{
            //    e.Ok("<!DOCTYPE html><html><body><h1>Website Blocked</h1><p>Blocked by titanium web proxy.</p></body></html>");
            //}

        }

        //Test script injection
        //Insert script to read the Browser URL and send it back to proxy
        public void OnResponse(object sender, SessionEventArgs e)
        {
            ////modify response headers
            //var responseHeaders = e.ResponseHeaders;
       

            //if (e.ResponseStatusCode == HttpStatusCode.OK)
            //{
            //    if (e.ResponseContentType.Trim().ToLower().Contains("text/html"))
            //    {
            //        //Get/Set response body bytes
            //        byte[] responseBodyBytes = e.GetResponseBody();
            //        e.SetResponseBody(responseBodyBytes);

            //        //Get response body as string
            //        string responseBody = e.GetResponseBodyAsString();

            //        //Modify e.ServerResponse
            //        Regex rex = new Regex("</body>", RegexOptions.RightToLeft | RegexOptions.IgnoreCase | RegexOptions.Multiline);
            //        string modified = rex.Replace(responseBody, "<script type =\"text/javascript\">alert('Response was modified by this script!');</script></body>", 1);

            //        //Set modifed response Html Body
            //        e.SetResponseBodyString(modified);
            //    }
            //}

        }

    }

}
