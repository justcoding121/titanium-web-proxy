Titanium
========

A light weight http(s) proxy server written in C#

![alt tag](https://raw.githubusercontent.com/titanium007/Titanium/master/Titanium.Web.Proxy.Test/Capture.PNG)
Features
========

* Supports HTTPS and all features of HTTP 1.1 
* Supports relaying of WebSockets
* Supports script injection
* Async using HTTPWebRequest class for better performance

Usage
=====

Refer the HTTP Proxy Server library in your project, look up Test project to learn usage.

Install by nuget:

    Install-Package Titanium.Web.Proxy -Pre

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
		
		//Test On Request, intecept requests
        //Read browser URL send back to proxy by the injection script in OnResponse event
        public void OnRequest(object sender, SessionEventArgs e)
        {

           
          Console.WriteLine(e.RequestURL);

            //modify request headers
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
		 
             //modify response headers
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
* Replace makecert.exe with other certificate generation APIs (like bouncy)
* Support modification of web socket requests
* Support HTTP 2.0 Once spec is ready
