Titanium
========
A light weight http(s) proxy server written in C#

![Build Status](https://ci.appveyor.com/api/projects/status/rvlxv8xgj0m7lkr4?svg=true)

Kindly report only issues/bugs here . For programming help or questions use [StackOverflow](http://stackoverflow.com/questions/tagged/titanium-web-proxy) with the tag Titanium-Web-Proxy.

![alt tag](https://raw.githubusercontent.com/titanium007/Titanium/master/Titanium.Web.Proxy.Test/Capture.PNG)

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
* Titanium_Proxy_Test_Root.cer


Setup HTTP proxy:

```csharp
	// listen to client request & server response events
	ProxyServer.BeforeRequest += OnRequest;
	ProxyServer.BeforeResponse += OnResponse;

	var explicitEndPoint = new ExplicitProxyEndPoint(IPAddress.Any, 8000, true){
		//Exclude Https addresses you don't want to proxy/cannot be proxied
		//for example exclude dropbox client which use certificate pinning
		ExcludedHttpsHostNameRegex = new List<string>() { "dropbox.com" }
	};

	//Add an explicit endpoint where the client is aware of the proxy
	//So client would send request in a proxy friendly manner
	ProxyServer.AddEndPoint(explicitEndPoint);
	ProxyServer.Start();
	
	//Only explicit proxies can be set as a system proxy!
	ProxyServer.SetAsSystemHttpProxy(explicitEndPoint);
	ProxyServer.SetAsSystemHttpsProxy(explicitEndPoint);
	
	foreach (var endPoint in ProxyServer.ProxyEndPoints)
		Console.WriteLine("Listening on '{0}' endpoint at Ip {1} and port: {2} ", 
			endPoint.GetType().Name, endPoint.IpAddress, endPoint.Port);

	//wait here (You can use something else as a wait function, I am using this as a demo)
	Console.Read();
	
	//Unsubscribe & Quit
	ProxyServer.BeforeRequest -= OnRequest;
    ProxyServer.BeforeResponse -= OnResponse;
	ProxyServer.Stop();
	
```
Sample request and response event handlers

```csharp
		
		//Test On Request, intercept requests
        public void OnRequest(object sender, SessionEventArgs e)
        {
            Console.WriteLine(e.ProxySession.Request.RequestUrl);

            //read request headers
            var requestHeaders = e.ProxySession.Request.RequestHeaders;

            if ((e.RequestMethod.ToUpper() == "POST" || e.RequestMethod.ToUpper() == "PUT"))
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

            if (e.ProxySession.Request.RequestUrl.Contains("google.com"))
            {
                e.Ok("<!DOCTYPE html><html><body><h1>Website Blocked</h1><p>Blocked by titanium web proxy.</p></body></html>");
            }
        }
	
	 public void OnResponse(object sender, SessionEventArgs e)
	{
            ////read response headers
            var responseHeaders = e.ProxySession.Response.ResponseHeaders;


            if (e.ResponseStatusCode == "200")
            {
                if (e.ResponseContentType.Trim().ToLower().Contains("text/html"))
                {
                    //Get/Set response body bytes
                    byte[] responseBodyBytes = e.GetResponseBody();
                    e.SetResponseBody(responseBodyBytes);

                    //Get response body as string
                    string responseBody = e.GetResponseBodyAsString();

                    //Modify e.ServerResponse
                    Regex rex = new Regex("</body>", RegexOptions.RightToLeft | RegexOptions.IgnoreCase | RegexOptions.Multiline);
                    string modified = rex.Replace(responseBody, "<script type =\"text/javascript\">alert('Response was modified by this script!');</script></body>", 1);

                    //Set modifed response Html Body
                    e.SetResponseBodyString(modified);
                }
            }
	}
```
Future roadmap
============
* Add callbacks for client/server certificate validation/selection
* Support mutual authentication
* Support Server Name Indication (SNI) for transparent endpoints
* Support HTTP 2.0 

