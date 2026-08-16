# Performance

Titanium targets **low-overhead MITM proxying**: connection pooling, HTTP/2 multiplexing, and buffer reuse. Figures below were measured on one Windows 11 / .NET 10 machine with **RpsLoadProbe** and the Basic example in Release. Treat them as orientation, not a guarantee—re-run on your hardware.

For pooling knobs and certificate first-visit tuning, see [Performance and pooling](Home#performance-and-pooling).

## At a glance

| What | Result |
|---|---|
| HTTPS TTFB vs direct (median, 14 hosts) | Cold **≈ parity** (−1 ms); warm **−25 ms** (proxy faster) |
| HTTP/1 loopback GET (no body intercept) | **~186 µs**, **~17.5 KB** allocated / request |
| Cleartext reverse HTTP/1 peak | **~16.0k RPS** (TWP) vs **~15.5k RPS** (nginx/Windows) |
| TLS-terminate reverse HTTP/1 peak | **~24.7k RPS** (TWP) vs **~13.0k RPS** (nginx/Windows) |
| TLS-terminate H2→H1 cleartext peak | **~7.6k RPS** @ c=64, **0% err** (TWP) · nginx **~14.2k** @ c=32 (fails SLO at c=64) |
| Reverse HTTP/3 (MITM to Quic origin) | see prior compare-tls tables |
| Explicit HTTPS MITM peak | **~13.6k RPS** |
| Basic example footprint (Release, after load) | **~74 MB** working set · **~24–29 MB** private bytes |

### Why isn’t HTTP/3 > HTTP/2 > HTTP/1 in raw RPS?

For **tiny JSON responses** (~64 B) on loopback, that ordering is **not** expected:

1. **Topology dominates protocol.** Always compare arms that share the same crypto hop count and upstream protocol.
2. **HTTP/2/3 shine at multiplexing**, not at maximizing single-origin tiny-GET RPS.
3. **Fair terminate topology** (client TLS → cleartext origin) is what nginx uses for H2. TWP matches that with `ForwardCleartext` + the H2→H1 bridge (and H1 TLS terminate).

### H2→H1 cleartext bridge (fixed)

Under multiplexed load the bridge used `RespondStreaming` (HEADERS without `END_STREAM` + DATA). .NET `HttpClient` reported **`Received an HTTP/2 pseudo-header as a trailing header`** and error rates climbed with concurrency. The bridge also omitted `IsExternalBridge`, racing `Http2Helper` against the synthetic emitter.

**Fix:** mark the stream `IsExternalBridge`, buffer the origin body, and emit via the buffered synthetic path. Keep-alive pooling remains enabled with residual-buffer and lease guards.

## Saturation RPS (this Windows machine)

**Machine:** Windows 11 (10.0.26200), 11th Gen Intel Core i7-1185G7 @ 3.00 GHz (8 logical), 31.8 GiB RAM, .NET 10.0.10, nginx/Windows **1.31.3**.

```powershell
pwsh tools/RpsLoadProbe/run-rps.ps1 -Mode compare-terminate
```

### Fair TLS-terminate compare (`compare-terminate`)

CSV: `tools/RpsLoadProbe/results/rps-ramp-20260816-045803.csv` (warmup 2s / measure 8s, c=8,32,64).

| Arm | Topology | Sustainable | Peak | Notes |
|---|---|---:|---:|---|
| TWP H1 TLS | Client TLS → cleartext H1 | **24,689** @ 64 | **24,689** | 0% err |
| nginx H1 TLS | ssl → cleartext H1 | **12,693** @ 64 | **13,010** | 0% err |
| TWP H2→H1 | Client h2 TLS → H2→H1 bridge → cleartext H1 | **7,554** @ 64 | **7,554** | **0% err** (stable at c=64) |
| nginx H2 | Client h2 TLS → cleartext H1 | **14,175** @ 32 | **14,175** | fails SLO at c=64 |
| TWP H3→H1 | Client h3 → cleartext H1 | — | ~1.8k | **errors** (stream abort 258) — follow-up |

nginx H2 still leads peak RPS on this machine; TWP H2→H1 is the first zero-error fair topology and stays within SLO at c=64 where nginx does not.

### Protocol / topology matrix (what we measure)

| Client | Upstream | TWP | nginx/Windows | Mode |
|---|---|---|---|---|
| H1 cleartext | H1 cleartext | yes | yes | `compare` |
| H1 TLS | H1 cleartext | `ForwardCleartext` | ssl `proxy_pass http://` | `compare-terminate` |
| H2 TLS | H1 cleartext | H2→H1 bridge + `ForwardCleartext` | ssl+http2 → cleartext | `compare-terminate` |
| H2 TLS | H2 TLS (MITM) | native h2↔h2 | n/a (nginx terminates) | `compare-tls` / `reverse-http2` |
| H2 TLS | H2 cleartext (h2c) | **not supported** (no h2c) | uncommon | — |
| H3 QUIC | H3 QUIC | MITM | **no QUIC on nginx/Windows** | `reverse-http3` |
| H3 QUIC | H2 cleartext/TLS | bridge paths exist; h2c N/A | — | — |
| H3 QUIC | H1 cleartext | `ForwardCleartext` + Http11 | — | `reverse-http3-cleartext` (WIP) |

## Raising limits on big machines

There is **no artificial upper clamp** on server defaults. Per-endpoint overrides:

| Knob | Scope | Default | Override |
|---|---|---|---|
| `ProxyServer.MaxCachedConnections` | process, per upstream host | 128 | any ≥ 1 |
| `ProxyEndPoint.MaxCachedConnections` | endpoint → pool depth for that EP’s sessions | null (use server) | e.g. `256` on reverse EP |
| `ProxyEndPoint.MaxConcurrentClients` | endpoint admission | null (off) | any ≥ 1 |
| `ResourceLimits.MaxConcurrentStreamsPerConnection` | H2 streams | 256 | `ProxyResourceLimits.Create(...)` |
| `TransparentQuicProxyEndPoint.MaxInboundBidirectionalStreams` | H3 | 100 (probe uses 256) | property on EP |
| `ForwardCleartext` | transparent TLS terminate | false | `true` + decrypt |

```csharp
proxy.MaxCachedConnections = 512;
proxy.ResourceLimits = ProxyResourceLimits.Create(
    /* … */,
    maxConcurrentStreamsPerConnection: 1000,
    maxCachedConnectionsPerHost: 512,
    /* … */);

var ep = new TransparentProxyEndPoint(IPAddress.Any, 443, decryptSsl: true)
{
    ForwardHost = "127.0.0.1",
    ForwardPort = 8080,
    ForwardCleartext = true,
    MaxCachedConnections = 256, // deeper pool for this reverse EP only
    GenericCertificateName = "example.com"
};
ep.BeforeSslAuthenticate += (_, a) =>
{
    a.UpstreamHttpProtocol = UpstreamHttpProtocol.Http11;
    a.AllowHttpProtocolTranslation = true; // H2 client → H1 origin bridge
    return Task.CompletedTask;
};
```

## HTTPS latency / loopback microbenchmarks

Unchanged from prior wiki revision — see curl median Δ and BenchmarkDotNet tables in git history if needed; re-run:

```powershell
dotnet run -c Release --project benchmarks/Titanium.Web.Proxy.Benchmarks -- --filter '*Throughput*'
```

## Process footprint (Basic example)

| Metric | Approx. value |
|---|---:|
| Working set | **~74 MB** |
| Private bytes | **~24–29 MB** |
