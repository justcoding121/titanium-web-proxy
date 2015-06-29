Titanium
========

A light weight http(s) proxy server written in C#

![alt tag](https://raw.githubusercontent.com/titanium007/Titanium/master/Titanium.Web.Proxy.Test/Capture.PNG)
Features
========

* Supports HTTPS and all features of HTTP 1.1 (except pipelining)
* Supports relaying of WebSockets
* Supports script injection
* Async using HTTPWebRequest class for better performance


Usage
=====

Refer the HTTP Proxy Server library in your project, look up Test project to learn usage.

Add reference to 
* Titanium.Web.Proxy.dll

These files also should be in your application directory
* Ionic.Zip.dll
* makecert.exe
* DO_NOT_TRUST_FiddlerRoot.cer


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
		
	public void OnRequest(object sender, SessionEventArgs e)
	{
		//Modify  e.ProxyRequest
	}
	
	 public void OnResponse(object sender, SessionEventArgs e)
	{
		if (e.ServerResponse.StatusCode == HttpStatusCode.OK)
		{
			if (e.ServerResponse.ContentType.Trim().ToLower().Contains("text/html"))
			{
				//Get response body
				e.GetResponseBody();
				//Modify e.ServerResponse
				e.ResponseString = "<html><head></head><body>Response is modified!</body></html>";
			}
		}
	}
```
Future updates
============
* Expose only APIs that are safe to be consumed by developer
* Replace makecert.exe with other certificate generation APIs (like bouncy)
* Release nuget package
* Support modification of web socket requests
* Support HTTP 2.0 Once spec is ready
