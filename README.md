Titanium
========
A light weight http(s) proxy server written in C#

![Build Status](https://ci.appveyor.com/api/projects/status/rvlxv8xgj0m7lkr4?svg=true)

Kindly report only issues/bugs here . For programming help or questions use [StackOverflow](http://stackoverflow.com/questions/tagged/titanium-web-proxy) with the tag Titanium-Web-Proxy.

![alt tag](https://raw.githubusercontent.com/justcoding121/Titanium-Web-Proxy/release/Examples/Titanium.Web.Proxy.Examples.Basic/Capture.PNG)

Features
========

* Supports Http(s) and most features of HTTP 1.1 
* Supports relaying of WebSockets
* Supports script injection

Usage
=====

Refer the HTTP Proxy Server library in your project, look up Test project to learn usage.

Install by nuget:

    Install-Package Titanium.Web.Proxy

After installing nuget package mark following files to be copied to app directory

* makecert.exe


Setup HTTP proxy:

```csharp
ProxyServer.BeforeRequest += OnRequest;
            ProxyServer.BeforeResponse += OnResponse;
            ProxyServer.RemoteCertificateValidationCallback += OnCertificateValidation;

            //Exclude Https addresses you don't want to proxy
            //Usefull for clients that use certificate pinning
            //for example dropbox.com
            var explicitEndPoint = new ExplicitProxyEndPoint(IPAddress.Any, 8000, true)
            {
                // ExcludedHttpsHostNameRegex = new List<string>() { "google.com", "dropbox.com" }
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
            var transparentEndPoint = new TransparentProxyEndPoint(IPAddress.Any, 8001, true)
            {
                GenericCertificateName = "google.com"
            };
            ProxyServer.AddEndPoint(transparentEndPoint);
			
	
            //ProxyServer.UpStreamHttpProxy = new ExternalProxy() { HostName = "localhost", Port = 8888 };
            //ProxyServer.UpStreamHttpsProxy = new ExternalProxy() { HostName = "localhost", Port = 8888 };

            foreach (var endPoint in ProxyServer.ProxyEndPoints)
                Console.WriteLine("Listening on '{0}' endpoint at Ip {1} and port: {2} ",
                    endPoint.GetType().Name, endPoint.IpAddress, endPoint.Port);

            //Only explicit proxies can be set as system proxy!
            ProxyServer.SetAsSystemHttpProxy(explicitEndPoint);
            ProxyServer.SetAsSystemHttpsProxy(explicitEndPoint);

			//wait here (You can use something else as a wait function, I am using this as a demo)
			Console.Read();
	
			//Unsubscribe & Quit
			ProxyServer.BeforeRequest -= OnRequest;
			ProxyServer.BeforeResponse -= OnResponse;
			ProxyServer.Stop();
	
```
Sample request and response event handlers

```csharp
		
        public void OnRequest(object sender, SessionEventArgs e)
        {
            Console.WriteLine(e.WebSession.Request.Url);

            ////read request headers
            var requestHeaders = e.WebSession.Request.RequestHeaders;

            if ((e.WebSession.Request.Method.ToUpper() == "POST" || e.WebSession.Request.Method.ToUpper() == "PUT"))
            {
                //Get/Set request body bytes
                byte[] bodyBytes = e.GetRequestBody();
                e.SetRequestBody(bodyBytes);

                //Get/Set request body as string
                string bodyString = e.GetRequestBodyAsString();
                e.SetRequestBodyString(bodyString);

            }

            //To cancel a request with a custom HTML content
            //Filter URL
            if (e.WebSession.Request.RequestUri.AbsoluteUri.Contains("google.com"))
            {
                e.Ok("<!DOCTYPE html>" +
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
                e.Redirect("https://www.paypal.com");
            }
        }
	 
        public void OnResponse(object sender, SessionEventArgs e)
        {

            //read response headers
            var responseHeaders = e.WebSession.Response.ResponseHeaders;

            //if (!e.ProxySession.Request.Host.Equals("medeczane.sgk.gov.tr")) return;
            if (e.WebSession.Request.Method == "GET" || e.WebSession.Request.Method == "POST")
            {
                if (e.WebSession.Response.ResponseStatusCode == "200")
                {
                    if (e.WebSession.Response.ContentType.Trim().ToLower().Contains("text/html"))
                    {
                        byte[] bodyBytes = e.GetResponseBody();
                        e.SetResponseBody(bodyBytes);

                        string body = e.GetResponseBodyAsString();
                        e.SetResponseBodyString(body);
                    }
                }
            }
        }

        // Allows overriding default certificate validation logic
        public void OnCertificateValidation(object sender, CertificateValidationEventArgs e)
        {
            //set IsValid to true/false based on Certificate Errors
            if (e.SslPolicyErrors == System.Net.Security.SslPolicyErrors.None)
                e.IsValid = true;
            else
                e.Session.Ok("Cannot validate server certificate! Not safe to proceed.");
        }
```
Future roadmap
============
* Support mutual authentication
* Support Server Name Indication (SNI) for transparent endpoints
* Support HTTP 2.0 

