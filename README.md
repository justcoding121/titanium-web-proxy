Titanium
========
A light weight HTTP(S) proxy server written in C#

![Build Status](https://ci.appveyor.com/api/projects/status/rvlxv8xgj0m7lkr4?svg=true)

Kindly report only issues/bugs here . For programming help or questions use [StackOverflow](http://stackoverflow.com/questions/tagged/titanium-web-proxy) with the tag Titanium-Web-Proxy.

![alt tag](https://raw.githubusercontent.com/justcoding121/Titanium-Web-Proxy/develop/Examples/Titanium.Web.Proxy.Examples.Basic/Capture.PNG)

Features
========

* Supports HTTP(S) and most features of HTTP 1.1 
* Support redirect/block/update requests
* Supports updating response
* Safely relays Web Socket requests over HTTP
* Support mutual SSL authentication
* Fully asynchronous proxy
* Supports proxy authentication & automatic proxy detection

Usage
=====

Refer the HTTP Proxy Server library in your project, look up Test project to learn usage. ([Wiki & Contribution guidelines](https://github.com/justcoding121/Titanium-Web-Proxy/wiki))

Install by nuget:

For beta releases on [beta branch](https://github.com/justcoding121/Titanium-Web-Proxy/tree/beta)

    Install-Package Titanium.Web.Proxy -Pre

For stable releases on [stable branch](https://github.com/justcoding121/Titanium-Web-Proxy/tree/stable)

    Install-Package Titanium.Web.Proxy

Setup HTTP proxy:

```csharp
var proxyServer = new ProxyServer();

//locally trust root certificate used by this proxy 
proxyServer.TrustRootCertificate = true;

//optionally set the Certificate Engine
//Under Mono only BouncyCastle will be supported
//proxyServer.CertificateEngine = Network.CertificateEngine.BouncyCastle;

proxyServer.BeforeRequest += OnRequest;
proxyServer.BeforeResponse += OnResponse;
proxyServer.ServerCertificateValidationCallback += OnCertificateValidation;
proxyServer.ClientCertificateSelectionCallback += OnCertificateSelection;


var explicitEndPoint = new ExplicitProxyEndPoint(IPAddress.Any, 8000, true)
{
//Exclude HTTPS addresses you don't want to proxy
//Useful for clients that use certificate pinning
//for example dropbox.com
// ExcludedHttpsHostNameRegex = new List<string>() { "google.com", "dropbox.com" }

//Use self-issued generic certificate on all HTTPS requests
//Optimizes performance by not creating a certificate for each HTTPS-enabled domain
//Useful when certificate trust is not required by proxy clients
// GenericCertificate = new X509Certificate2(Path.Combine(System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location), "genericcert.pfx"), "password")
};

//An explicit endpoint is where the client knows about the existence of a proxy
//So client sends request in a proxy friendly manner
proxyServer.AddEndPoint(explicitEndPoint);
proxyServer.Start();

//Warning! Transparent endpoint is not tested end to end 
//Transparent endpoint is useful for reverse proxy (client is not aware of the existence of proxy)
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

//proxyServer.UpStreamHttpProxy = new ExternalProxy() { HostName = "localhost", Port = 8888 };
//proxyServer.UpStreamHttpsProxy = new ExternalProxy() { HostName = "localhost", Port = 8888 };

foreach (var endPoint in proxyServer.ProxyEndPoints)
Console.WriteLine("Listening on '{0}' endpoint at Ip {1} and port: {2} ",
    endPoint.GetType().Name, endPoint.IpAddress, endPoint.Port);

//Only explicit proxies can be set as system proxy!
proxyServer.SetAsSystemHttpProxy(explicitEndPoint);
proxyServer.SetAsSystemHttpsProxy(explicitEndPoint);

//wait here (You can use something else as a wait function, I am using this as a demo)
Console.Read();

//Unsubscribe & Quit
proxyServer.BeforeRequest -= OnRequest;
proxyServer.BeforeResponse -= OnResponse;
proxyServer.ServerCertificateValidationCallback -= OnCertificateValidation;
proxyServer.ClientCertificateSelectionCallback -= OnCertificateSelection;

proxyServer.Stop();
	
```
Sample request and response event handlers

```csharp		

//To access requestBody from OnResponse handler
private Dictionary<Guid, string> requestBodyHistory = new Dictionary<Guid, string>();

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
	
	//store request Body/request headers etc with request Id as key
	//so that you can find it from response handler using request Id
  	requestBodyHistory[e.Id] = bodyString;
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
}

//Modify response
public async Task OnResponse(object sender, SessionEventArgs e)
{
    //read response headers
    var responseHeaders = e.WebSession.Response.ResponseHeaders;

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
    
    //access request body/request headers etc by looking up using requestId
    if(requestBodyHistory.ContainsKey(e.Id))
    {
	var requestBody = requestBodyHistory[e.Id];
    }
}

/// Allows overriding default certificate validation logic
public Task OnCertificateValidation(object sender, CertificateValidationEventArgs e)
{
    //set IsValid to true/false based on Certificate Errors
    if (e.SslPolicyErrors == System.Net.Security.SslPolicyErrors.None)
	e.IsValid = true;

    return Task.FromResult(0);
}

/// Allows overriding default client certificate selection logic during mutual authentication
public Task OnCertificateSelection(object sender, CertificateSelectionEventArgs e)
{
    //set e.clientCertificate to override
    return Task.FromResult(0);
}
```
Future road map (Pull requests are welcome!)
============
* Implement Kerberos/NTLM authentication over HTTP protocols for windows domain
* Support Server Name Indication (SNI) for transparent endpoints
* Support HTTP 2.0 
* Support SOCKS protocol

