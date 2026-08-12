# Titanium Web Proxy

A lightweight, asynchronous HTTP(S) proxy server for .NET. This wiki documents the major features and the most common APIs. For the full type reference, see the [API documentation](https://justcoding121.github.io/titanium-web-proxy/docs/api/Titanium.Web.Proxy.ProxyServer.html).

## Contents

- [Getting started](#getting-started)
- [Screenshots](#screenshots)
- [Endpoints](#endpoints)
- [Decrypting HTTPS](#decrypting-https)
- [Intercepting requests and responses](#intercepting-requests-and-responses)
- [Modifying bodies](#modifying-bodies)
- [Custom and redirected responses](#custom-and-redirected-responses)
- [Streaming bodies](#streaming-bodies)
- [HTTP/2](#http2)
- [HTTP/3](#http3)
- [Tunnel (CONNECT) interception](#tunnel-connect-interception)
- [Upstream proxies](#upstream-proxies)
- [Authentication](#authentication)
- [Performance and pooling](#performance-and-pooling)
- [Logging and diagnostics](#logging-and-diagnostics)
- [Request timing](#request-timing)
- [Supported frameworks](#supported-frameworks)
- [Breaking changes: unified logging and timing](#breaking-changes-unified-logging-and-timing)
- [Migrating from 4.x to 5.0](Migration-4.x-to-5.0)
- [Security considerations](Security-Considerations)
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

Example apps live under [`examples/`](https://github.com/justcoding121/titanium-web-proxy/tree/develop/examples) in the main repository ([Basic](https://github.com/justcoding121/titanium-web-proxy/tree/develop/examples/Titanium.Web.Proxy.Examples.Basic), [WPF](https://github.com/justcoding121/titanium-web-proxy/tree/develop/examples/Titanium.Web.Proxy.Examples.Wpf), [Windows service](https://github.com/justcoding121/titanium-web-proxy/tree/develop/examples/Titanium.Web.Proxy.Examples.WindowsService)).

## Screenshots

**Basic console example** — compact per-request traffic tape:

<img src="images/basic-screenshot.jpg" alt="Basic console proxy screenshot" width="900" />

**WPF example** — session list with request/response inspection:

<img src="images/wpf-screenshot.jpg" alt="WPF proxy application screenshot" width="900" />

## Endpoints

Add one or more endpoints before calling `Start()`:

- **`ExplicitProxyEndPoint`** — the client is configured to use the proxy (standard `HTTP_PROXY` / system proxy setup). Supports `CONNECT` tunneling.
- **`TransparentProxyEndPoint`** — traffic is redirected to the proxy without the client knowing (e.g. via routing/NAT). Set `GenericCertificateName` for the server name to present.
- **`SocksProxyEndPoint`** — SOCKS4/SOCKS5 endpoint. See [SOCKS endpoint](#socks-endpoint) for protocol handling details.

```csharp
proxyServer.AddEndPoint(new ExplicitProxyEndPoint(IPAddress.Loopback, 8000));
proxyServer.AddEndPoint(new TransparentProxyEndPoint(IPAddress.Loopback, 8001, decryptSsl: true)
{
    GenericCertificateName = "example.com"
});
proxyServer.AddEndPoint(new SocksProxyEndPoint(IPAddress.Loopback, 1080));
```

## SOCKS endpoint

`SocksProxyEndPoint` accepts SOCKS4 and SOCKS5 connections. The client is unaware it is communicating with a proxy; traffic is typically redirected here via a local application configuration or system-level routing.

**Protocol routing after the SOCKS handshake:**

| Traffic | `decryptSsl: true` (default) | `decryptSsl: false` |
|---|---|---|
| HTTPS (TLS ClientHello detected) | MITM-decrypted; HTTP(S) interception pipeline runs (`BeforeRequest`/`BeforeResponse`/`AfterResponse`) | Opaque TCP relay to the SOCKS destination — no inspection |
| Plain HTTP | HTTP interception pipeline runs | HTTP interception pipeline runs |
| Non-HTTP, non-TLS (e.g. SMTP, custom TCP protocol) | Opaque TCP relay to the SOCKS destination | Opaque TCP relay to the SOCKS destination |

Non-HTTP plain traffic is detected by peeking the first bytes. When the opening bytes do not match any known HTTP method, the connection is relayed transparently to the target host and port negotiated during the SOCKS handshake — no HTTP parsing is attempted and no proxy events fire.

To opt individual HTTPS connections out of decryption at runtime, subscribe to `BeforeSslAuthenticate` on the endpoint:

```csharp
var socksEndPoint = new SocksProxyEndPoint(IPAddress.Loopback, 1080, decryptSsl: true);
socksEndPoint.BeforeSslAuthenticate += (sender, e) =>
{
    if (e.SniHostName.EndsWith(".internal", StringComparison.OrdinalIgnoreCase))
        e.DecryptSsl = false; // relay opaquely without decrypting
    return Task.CompletedTask;
};
proxyServer.AddEndPoint(socksEndPoint);
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
- `CertificateEngine` — `BouncyCastle` (default; distinct key per host), `BouncyCastleFast` (faster;
  **one shared key for all leaves**), or `DefaultWindows` (Windows only; also shared key).
- `LeafCertificateKeyAlgorithm` — key algorithm for generated leaf certificates, `Rsa2048` (default)
  or `EcdsaP256`. See [first-visit latency](#first-visit-latency-and-the-leaf-key-algorithm).
- `LeafRsaKeyPairBufferSize` — how many RSA-2048 leaf private keys to keep pre-generated (default `8`;
  `0` disables; max `256`). Process-wide; unused when leaves are ECDSA P-256.

Only decrypt endpoints where you need to see content; leave `decryptSsl: false` to pass HTTPS through as an opaque tunnel.

### Certificate cache memory bounds

Every distinct MITM'd host gets its own generated leaf certificate, kept in an in-memory cache
(each entry holds a full `X509Certificate2` plus private key) so repeat connections to the same
host don't re-run certificate generation. Two independent, nullable knobs on
`ProxyResourceLimits` bound this:

- `MaxCertificateCacheEntries` — caps the in-memory cache. `ProxyResourceLimits.Default` (and the
  `Balanced`/`LegacyCompatible` profiles) set this to `1024` (roughly 10 MB), comfortably above a
  typical single-session browsing workload. `null` disables the bound.
- `MaxCertificateDiskCacheEntries` — caps the on-disk cache used when `SaveFakeCertificates` is
  `true`, independently of the in-memory bound. Disk is far cheaper than a live certificate handle,
  so `Default`/`Balanced`/`LegacyCompatible` leave this `null` (unbounded) so a warm disk cache
  survives process restarts. The `PublicFacing` profile bounds it at `50,000`, since untrusted
  clients can enumerate hostnames and an unbounded disk cache would otherwise be a disk-exhaustion
  vector.

Both entries in a cached certificate that age out (idle longer than `CertificateCacheTimeOutMinutes`,
default 60) or get evicted to stay within the bound are disposed a sweep interval later, not
immediately — this gives any TLS handshake that grabbed a reference just before eviction time to
finish, while still reclaiming native key handles well before the next full GC.

Use `ProxyResourceLimits.Default.WithCertificateCacheBounds(...)` to change either bound without
having to reconstruct every other limit:

```csharp
proxyServer.ResourceLimits = ProxyResourceLimits.Default.WithCertificateCacheBounds(
    maxCertificateCacheEntries: 4096,
    maxCertificateDiskCacheEntries: 50_000);
```

The `twp.certificates.cached` observable gauge (see [Logging and diagnostics](#logging-and-diagnostics))
reports live in-memory cache occupancy, so you can confirm the bound is holding instead of
inferring it indirectly from process working set.

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

HTTP/2 support is on by default (negotiated via TLS ALPN only — no cleartext h2c upgrade). To opt out and
force HTTP/1.1 only:

```csharp
proxyServer.EnableHttp2 = false;
```

Header/body modification in `BeforeRequest`/`BeforeResponse`, chunked trailers, interim (1xx) responses, and
the synthetic-response APIs (`Ok`/`Respond`/`Redirect`/`GenericResponse`/`RespondStreaming`) all work over
HTTP/2 the same as over HTTP/1.x — see [Streaming Bodies](Streaming-Bodies). WebSocket over HTTP/2
(RFC 8441), including HTTP/1.1 Upgrade → h2 origin on the translation bridge, is opt-in via
`EnableRfc8441`. Not supported: HTTP/2 server push and cleartext h2c upgrade. See
[Protocol Feature Support](Protocol-Support) for the full breakdown.

## HTTP/3

HTTP/3 support is available as an opt-in feature.  See the **[HTTP/3](HTTP-3)** page for the full
setup guide.  Quick start:

```csharp
proxy.EnableHttp3 = true;
var quicEndPoint = new TransparentQuicProxyEndPoint(IPAddress.Any, 443);
proxy.AddEndPoint(quicEndPoint);
proxy.Start();
```

All existing `BeforeRequest`/`BeforeResponse`/`AfterResponse` event handlers work unchanged for HTTP/3
streams. The proxy auto-discovers HTTP/3 capability via `Alt-Svc` (and optional background HTTPS/SVCB
DNS) and uses HTTP/3 on **new** Auto-mode connections once that origin is warm — a cache hit alone
only starts background QUIC warm-up. An already-open H2↔H2 MITM session keeps using H2 for its
streams rather than upgrading them mid-connection.

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

Shipped defaults follow `ProxyProfile.Balanced`: networking knobs that are safe for every client are
on; certificate and experimental-protocol choices that trade compatibility or persistence stay
opt-in. Examples match those library defaults.

| Knob | Balanced default | Speed opt-in | Notes |
|---|---|---|---|
| `EnableConnectionPool` | `true` | — | Live pool switch; prefer this over unused `ProxyResourceLimits.ConnectionPoolingEnabled`. |
| `MaxCachedConnections` | `4` | raise for high fan-out per origin | Same: live knob is on `ProxyServer`, not `ResourceLimits.MaxCachedConnectionsPerHost`. |
| `EnableTcpServerConnectionPrefetch` | `true` | — | Overlaps origin connect with client work. |
| `NoDelay` | `true` | — | Disables Nagle. |
| `EnableTcpKeepAlive` | `true` | — | NAT-friendly for long tunnels. |
| `TcpTimeWaitSeconds` | `0` | — | Abortive close; avoids TIME_WAIT churn. |
| `EnableHttp2` | `true` | — | Used when peers negotiate ALPN. |
| `CheckCertificateRevocation` | off | — | Revocation checks add latency. |
| `BufferPool` / `BufferSize` | `DefaultBufferPool` / 8 KiB | larger buffers | Higher memory per concurrent stream. |
| `LeafCertificateKeyAlgorithm` | `Rsa2048` | `EcdsaP256` | Biggest first-visit win for modern browsers; see below. |
| `LeafRsaKeyPairBufferSize` | `8` | raise under RSA stampede | Unused when leaves are ECDSA. |
| `SaveFakeCertificates` | `false` | `true` | Disk cache across restarts; privacy/disk tradeoff. |
| `EnableHttp3` / QPACK dynamic / RFC 8441 | off | opt-in | Experimental / feature flags. |
| `CertificateEngine` | `BouncyCastle` | `BouncyCastleFast` | Fast shares one leaf key across hosts — weaker isolation. |

- `EnableConnectionPool` — reuse idle upstream TCP connections (enabled by default). Only connections that are safe to reuse under HTTP (persistent, body fully received, not authenticated to a specific identity) are pooled; set to `false` to force a fresh connection per client.
- `ConnectionTimeOutSeconds`, `TcpTimeWaitSeconds`, `ReuseSocket` — tune connection lifetime.
- `BufferPool` / `BufferSize` — reuse I/O buffers.
- `CertificateManager.SaveFakeCertificates` — cache generated certificates.

### First-visit latency and the leaf key algorithm

Once a host's certificate is cached, the proxy costs little: measured against Chrome loading
google.com, wikipedia.org, news.google.com, youtube.com and jw.org, going through it adds roughly
30 ms to main-document TTFB, which is about what the extra TLS leg and hop should cost.

The first visit to a host is different, because a certificate has to be produced before the browser
handshake can finish. An RSA-2048 key pair costs a few hundred milliseconds of CPU, and a page
pulling resources from a few dozen not-yet-seen hosts needs one per host — all at once, all
CPU-bound, so they inflate each other well past their uncontended cost. Two things bound that:

- Leaf RSA key pairs come from a small buffer that a background task keeps topped up (default size
  `8` via `LeafRsaKeyPairBufferSize`; `0` disables; max `256`), so a key generated while the proxy
  was idle is handed over immediately and only a burst longer than the buffer waits on generation
  at all.
- Setting `CertificateManager.LeafCertificateKeyAlgorithm` to `CertificateKeyAlgorithm.EcdsaP256`
  issues P-256 leaves instead, which cost a fraction of an RSA key pair to generate while still
  giving every host its own key. On a cold certificate cache this takes first-visit TTFB from
  several times the direct baseline down to roughly parity with it. Only clients that accept ECDSA
  server certificates can be intercepted afterwards — universal among current browsers, but not in
  much older TLS stacks, which is why `Rsa2048` remains the default. The root certificate stays RSA
  either way, so an already-installed and trusted root keeps working.

Honoured by the BouncyCastle engines; the Windows engine always issues RSA.

## Logging and diagnostics

Every exception the proxy catches — even one handled internally and never surfaced to your code — is
reported through `ProxyServer.Logging`, a `Microsoft.Extensions.Logging`-based abstraction. This replaced
the old `ExceptionFunc` callback; see
[Breaking changes: unified logging and timing](#breaking-changes-unified-logging-and-timing) below if you
are migrating.

```csharp
// Master switch: false gives zero logging overhead (no timestamps read, no strings formatted).
proxyServer.Logging.Enabled = true;

// Only entries at or above this level are actually written to a sink. Every caught exception is still
// classified and reported to the gateway regardless - this only controls how much reaches a sink.
// Defaults to LogLevel.Error so out-of-the-box behavior stays quiet.
proxyServer.Logging.MinimumLevel = LogLevel.Information;

// Built-in sinks, both asynchronous and best-effort so they never block proxy traffic:
proxyServer.Logging.EnableConsole = true;          // default on
proxyServer.Logging.EnableConsoleColors = true;    // default on; colors each line by level
proxyServer.Logging.EnableFile = true;             // default off
proxyServer.Logging.FilePath = "logs/titanium-proxy.log"; // default path; size-based rolling file
proxyServer.Logging.MaxFileSizeBytes = 10 * 1024 * 1024;
proxyServer.Logging.MaxRolledFiles = 5;

// Changes to the Logging options above only take effect once you (re)apply them - Start() does this
// automatically, but call it yourself to change configuration while already running:
proxyServer.ApplyLoggingConfiguration();
```

To bridge into an existing logging pipeline (Serilog, NLog, an ASP.NET Core host's `ILoggerFactory`, etc.)
instead of the built-in Console/File sinks, set `LoggerFactory` — this disables the built-in sinks entirely
and hands every log record to your factory verbatim:

```csharp
proxyServer.Logging.LoggerFactory = hostLoggerFactory;
proxyServer.ApplyLoggingConfiguration();
```

Exceptions the proxy considers expected/benign under normal operation (client disconnects, cancelled
operations, expected socket resets, retries, and similar) are logged at `Debug`/`Trace` so they never
contribute to `Error`-level noise in the default configuration, while genuinely unexpected failures are
always logged at `Error` or `Critical`.

The built-in console sink colors each line by level (dim `Trace`/`Debug`, default `Information`, yellow
`Warning`, red `Error`, bold red `Critical`) so failures stand out while scrolling through busy output.
Colors are automatically suppressed for a stream that is redirected (e.g. `proxy.exe > out.log`) or when
the [`NO_COLOR`](https://no-color.org/) environment variable is set, regardless of
`EnableConsoleColors` — so redirected output and log files never end up with raw escape codes. The
rolling-file sink is always plain text.

## Request timing

Set `EnableRequestTimingCapture` to populate structured timing objects for every session; when left
`false` (the default) no timing object is ever allocated, so there is no cost at all when the feature is
unused.

```csharp
proxyServer.EnableRequestTimingCapture = true;

proxyServer.AfterResponse += (sender, e) =>
{
    var timing = e.Timing; // HttpRequestTiming, or null if capture is disabled
    if (timing != null)
    {
        Console.WriteLine($"Time to first byte: {timing.TimeToFirstByte}");
        Console.WriteLine($"Total duration: {timing.TotalDuration}");
        Console.WriteLine($"Upstream connection reused: {timing.UpstreamConnectionReused}");
    }

    return Task.CompletedTask;
};
```

- **`SessionEventArgsBase.Timing`** (`HttpRequestTiming`) — per-request milestones: when the client's
  request headers were read, when an upstream connection became ready, when the request was sent, when
  response headers arrived, and when the session completed — plus derived durations
  (`ConnectionWaitDuration`, `TimeToFirstByte`, `ResponseDeliveryDuration`, `TotalDuration`) and retry
  bookkeeping (`AttemptCount`, `UpstreamConnectionReused`).
- **`SessionEventArgsBase.UpstreamConnectionTiming`** (`UpstreamConnectionTiming`) — timing of the
  underlying upstream TCP/TLS connection itself (DNS resolution, TCP handshake, optional upstream-proxy
  CONNECT tunnel, TLS handshake). Shared by every session that reuses the same pooled connection.
- **`TunnelConnectSessionEventArgs.ClientTlsTiming`** (`ClientTlsTiming`) — duration of the client-facing
  (browser-to-proxy) TLS handshake performed while decrypting an HTTPS `CONNECT` tunnel on an explicit
  endpoint.
- **`TunnelConnectSessionEventArgs.ConnectTiming`** (`TunnelConnectTiming`) — CONNECT-phase milestones
  (certificate readiness, HTTP/3 capability source, HTTP/2 probe, browser TLS). Allocated only when
  `EnableRequestTimingCapture` is true and the tunnel is being decrypted.

## Supported frameworks

- .NET 10

Versions prior to 4.0 also supported .NET Framework 4.6.2 and .NET 8; starting with 4.0, the package targets
.NET 10 only.

## Breaking changes: unified logging and timing

- `ProxyServer.ExceptionFunc` and the `ExceptionHandler` delegate were removed. Use
  [`ProxyServer.Logging`](#logging-and-diagnostics) instead — every exception the old callback would have
  received is now reported through the logging gateway, classified by severity rather than delivered
  uniformly to a single callback.
- `SessionEventArgsBase.TimeLine` (the free-form `Dictionary<string, DateTime>` of named milestones) was
  removed. Use [`Timing`/`UpstreamConnectionTiming`/`ClientTlsTiming`](#request-timing) instead, which are
  strongly typed and only allocated when `EnableRequestTimingCapture` is set.
- `ClientConnectionId` / `ServerConnectionId` / `HttpRequestTiming.UpstreamConnectionId` changed from
  `Guid` to process-wide monotonic `long` counters (unbound server id is `0`, not `Guid.Empty`). See
  [Connection IDs are monotonic `long` counters, not `Guid`](Migration-4.x-to-5.0#connection-ids-are-monotonic-long-counters-not-guid)
  in the 5.0 migration guide.

## Migrating from 4.x to 5.0

5.0 bundles a large security- and correctness-hardening pass — TLS defaults, certificate storage
location, HTTP/1 framing strictness, body-size budgets, WebSocket/HTTP-2/HTTP-3 abuse limits, and a
few credential/redaction fixes all changed observable behavior in some way. See the dedicated
**[Migration guide: 4.x → 5.0](Migration-4.x-to-5.0)** page for the full list, each with its rationale
and remedy.

## Protocol feature support

Wondering whether a specific HTTP/1.x, HTTP/2, or HTTP/3 feature (trailers, interim 1xx responses, HPACK,
QPACK, Alt-Svc, server push, ...) is supported? See the **[Protocol Feature Support](Protocol-Support)** page
for a full
Yes/No/Partial breakdown.
