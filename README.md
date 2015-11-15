Titanium
========
A light weight http(s) proxy server written in C#

[![titanium MyGet Build Status](https://www.myget.org/BuildSource/Badge/titanium?identifier=36bd545d-87aa-4c0c-ae98-6de9a078b016)](https://www.myget.org/)

Kindly report only issues/bugs here . For programming help or questions use [StackOverflow](http://stackoverflow.com/questions/tagged/titanium-web-proxy) with the tag Titanium-Web-Proxy.

![alt tag](https://raw.githubusercontent.com/titanium007/Titanium/master/Titanium.Web.Proxy.Test/Capture.PNG)

Features
========

* Supports Http(s) and all features of HTTP 1.1 
* Supports relaying of WebSockets
* Supports script injection
* Async using HttpWebRequest class for better performance

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
	
	ProxyServer.EnableSSL = true;
	ProxyServer.SetAsSystemProxy = true;
	ProxyServer.Start();
	
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
           Console.WriteLine(e.RequestURL);
		   
            //read request headers
            var requestHeaders = e.RequestHeaders;

            if ((e.RequestMethod.ToUpper() == "POST" || e.RequestMethod.ToUpper() == "PUT") && e.RequestContentLength > 0)
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

            if (e.RequestURL.Contains("google.com"))
            {
                e.Ok("<!DOCTYPE html><html><body><h1>Website Blocked</h1><p>Blocked by titanium web proxy.</p></body></html>");
            }
        }
	
	 public void OnResponse(object sender, SessionEventArgs e)
	{
            //read response headers
            var responseHeaders = e.ResponseHeaders;

            if (e.ResponseStatusCode == HttpStatusCode.OK)
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
Future updates
============
* Support mutual authentication
* Support HTTP 2.0 
* Support modification of web socket requests
