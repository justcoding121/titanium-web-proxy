# Titanium Web Proxy

A lightweight, asynchronous HTTP(S) proxy server for .NET. This wiki documents the major features and the most common APIs. For the full type reference, see the [API documentation](https://justcoding121.github.io/titanium-web-proxy/docs/api/Titanium.Web.Proxy.ProxyServer.html).

## Contents

- [Getting started](#getting-started)
- [Endpoints](#endpoints)
- [Decrypting HTTPS](#decrypting-https)
- [Intercepting requests and responses](#intercepting-requests-and-responses)
- [Modifying bodies](#modifying-bodies)
- [Custom and redirected responses](#custom-and-redirected-responses)
- [Streaming bodies](#streaming-bodies)
- [HTTP/2](#http2)
- [Tunnel (CONNECT) interception](#tunnel-connect-interception)
- [Upstream proxies](#upstream-proxies)
- [Authentication](#authentication)
- [Performance and pooling](#performance-and-pooling)
- [Supported frameworks](#supported-frameworks)
- [Protocol feature support](Protocol-Support)

## Getting started

Install from [NuGet](https://www.nuget.org/packages/Titanium.Web.Proxy):

```shell
dotnet add package Titanium.Web.Proxy
```

Start an explicit proxy that logs every requested URL:

```csharp
using System;
using System.Net;
using System.Threading.Tasks;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Models;

using var proxyServer = new ProxyServer();

proxyServer.BeforeRequest += OnRequest;

var endPoint = new ExplicitProxyEndPoint(IPAddress.Loopback, 8000, decryptSsl: true);
proxyServer.AddEndPoint(endPoint);

// Create and trust the root certificate used to decrypt HTTPS traffic.
proxyServer.CertificateManager.EnsureRootCertificate(
    userTrustRootCertificate: true,
    machineTrustRootCertificate: false);

proxyServer.Start();
Console.WriteLine("Proxy listening on 127.0.0.1:8000. Press Enter to stop.");
Console.ReadLine();
proxyServer.Stop();

static Task OnRequest(object sender, SessionEventArgs e)
{
    Console.WriteLine(e.HttpClient.Request.Url);
    return Task.CompletedTask;
}
```

Configure your client to use `127.0.0.1:8000` as its HTTP and HTTPS proxy.

## Endpoints

Add one or more endpoints before calling `Start()`:

- **`ExplicitProxyEndPoint`** — the client is configured to use the proxy (standard `HTTP_PROXY` / system proxy setup). Supports `CONNECT` tunneling.
- **`TransparentProxyEndPoint`** — traffic is redirected to the proxy without the client knowing (e.g. via routing/NAT). Set `GenericCertificateName` for the server name to present.
- **`SocksProxyEndPoint`** — SOCKS4/SOCKS5 endpoint.

```csharp
proxyServer.AddEndPoint(new ExplicitProxyEndPoint(IPAddress.Loopback, 8000));
proxyServer.AddEndPoint(new TransparentProxyEndPoint(IPAddress.Loopback, 8001, decryptSsl: true)
{
    GenericCertificateName = "example.com"
});
proxyServer.AddEndPoint(new SocksProxyEndPoint(IPAddress.Loopback, 1080));
```

## Decrypting HTTPS

To inspect HTTPS traffic the proxy generates per-host certificates signed by its own root certificate, which the client must trust.

```csharp
// Generate (if needed) and trust the root certificate for the current user.
proxyServer.CertificateManager.EnsureRootCertificate(
    userTrustRootCertificate: true,
    machineTrustRootCertificate: false);
```

Useful `CertificateManager` members:

- `RootCertificate` / `RootCertificateName` / `PfxFilePath` — the CA used for signing.
- `CreateRootCertificate(...)`, `TrustRootCertificate(...)`, `RemoveTrustedRootCertificate(...)`.
- `SaveFakeCertificates` — cache generated leaf certificates on disk.
- `CertificateEngine` — `BouncyCastle` or the built-in engine.

Only decrypt endpoints where you need to see content; leave `decryptSsl: false` to pass HTTPS through as an opaque tunnel.

## Intercepting requests and responses

Subscribe to the proxy lifecycle events. All handlers are `async`.

```csharp
proxyServer.BeforeRequest  += OnRequest;   // before the request is sent upstream
proxyServer.BeforeResponse += OnResponse;  // after response headers are received
proxyServer.AfterResponse  += OnAfterResponse;
```

`SessionEventArgs` exposes `HttpClient.Request` and `HttpClient.Response`, headers, the URL, client/process info, and per-session `UserData`.

```csharp
Task OnRequest(object sender, SessionEventArgs e)
{
    var request = e.HttpClient.Request;
    request.Headers.AddHeader("X-Proxy", "titanium");
    return Task.CompletedTask;
}
```

## Modifying bodies

Read and replace the whole body (buffers it in memory):

```csharp
async Task OnResponse(object sender, SessionEventArgs e)
{
    if (e.HttpClient.Response.ContentType?.Contains("text/html") == true)
    {
        var body = await e.GetResponseBodyAsString();
        e.SetResponseBodyString(body.Replace("http://", "https://"));
    }
}
```

For large or unbounded bodies, prefer the streaming APIs below instead of `GetResponseBody()`.

## Custom and redirected responses

Answer the client directly, without contacting the server:

```csharp
proxyServer.BeforeRequest += (sender, e) =>
{
    if (e.HttpClient.Request.Url.Contains("blocked.example"))
        e.Ok("<html><body>Blocked</body></html>");

    return Task.CompletedTask;
};
```

- `e.Ok(html)` / `e.Ok(bytes)` — send a `200` response.
- `e.Respond(response)` — send an arbitrary `Response`.
- `e.Redirect(url)` — send a redirect.
- `e.TerminateServerConnection()` — close the upstream connection instead of reusing it.

When you supply a response after the server was already contacted, the original server body is drained so the connection can be reused; see [Draining bodies](Streaming-Bodies#draining-bodies).

## Streaming bodies

Inspect or modify bodies chunk-by-chunk, or generate a response body from scratch, **without buffering it in memory** — ideal for large downloads or endless streams (e.g. server-sent events).

```csharp
proxyServer.OnResponseBodyWrite += (sender, e) =>
{
    e.BodyBytes = Transform(e.BodyBytes); // modify each chunk as it streams
    return Task.CompletedTask;
};
```

See the dedicated **[Streaming Bodies](Streaming-Bodies)** page for `OnRequestBodyWrite`/`OnResponseBodyWrite`, `RespondStreaming`, draining, and the HTTP/1.x vs HTTP/2 details.

## HTTP/2

Enable HTTP/2 support (frames are relayed for decrypted h2 connections):

```csharp
proxyServer.EnableHttp2 = true;
```

The body-streaming and synthetic-streaming APIs work over HTTP/2 as well as HTTP/1.x — see [Streaming Bodies](Streaming-Bodies).

## Tunnel (CONNECT) interception

On an `ExplicitProxyEndPoint`, decide per-`CONNECT` whether to decrypt:

```csharp
explicitEndPoint.BeforeTunnelConnectRequest += (sender, e) =>
{
    var host = e.HttpClient.Request.RequestUri.Host;
    if (host.EndsWith("bank.example"))
        e.DecryptSsl = false; // pass through without decrypting

    return Task.CompletedTask;
};

explicitEndPoint.BeforeTunnelConnectResponse += (sender, e) => Task.CompletedTask;
```

## Upstream proxies

Chain through another proxy, globally or per request:

```csharp
proxyServer.UpStreamHttpProxy  = new ExternalProxy("upstream.example", 8888);
proxyServer.UpStreamHttpsProxy = new ExternalProxy("upstream.example", 8888);

// Or resolve the upstream proxy dynamically:
proxyServer.GetCustomUpStreamProxyFunc = async args =>
{
    return new ExternalProxy("upstream.example", 8888);
};

// Detect and reuse the system's configured proxy:
proxyServer.ForwardToUpstreamGateway = true;
```

`ExternalProxy` supports HTTP, HTTPS, and SOCKS4/5, with optional credentials.

## Authentication

- **Proxy authentication (Basic)**:

  ```csharp
  proxyServer.ProxyBasicAuthenticateFunc = async (args, userName, password) =>
      userName == "user" && password == "secret";
  ```

- **Windows authentication (Kerberos/NTLM)** to upstream servers:

  ```csharp
  proxyServer.EnableWinAuth = true;
  ```

- **Mutual TLS**: provide the client certificate via `ClientCertificateSelectionCallback`, and validate server certificates with `ServerCertificateValidationCallback`.

## Performance and pooling

- `EnableConnectionPool` — reuse idle upstream TCP connections (enabled by default). Only connections that are safe to reuse under HTTP (persistent, body fully received, not authenticated to a specific identity) are pooled; set to `false` to force a fresh connection per client.
- `ConnectionTimeOutSeconds`, `TcpTimeWaitSeconds`, `ReuseSocket` — tune connection lifetime.
- `BufferPool` / `BufferSize` — reuse I/O buffers.
- `CertificateManager.SaveFakeCertificates` — cache generated certificates.

## Supported frameworks

- .NET 10

Versions prior to 4.0 also supported .NET Framework 4.6.2 and .NET 8; starting with 4.0, the package targets
.NET 10 only.

## Protocol feature support

Wondering whether a specific HTTP/1.x or HTTP/2 feature (trailers, interim 1xx responses, HPACK, server
push, ...) is supported? See the **[Protocol Feature Support](Protocol-Support)** page for a full
Yes/No/Partial breakdown.
