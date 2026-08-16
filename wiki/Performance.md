# Performance

Titanium targets **low-overhead MITM proxying**: connection pooling, HTTP/2 multiplexing, and buffer reuse. Numbers below are **Release** measurements with [RpsLoadProbe](https://github.com/justcoding121/titanium-web-proxy/tree/develop/tools/RpsLoadProbe) (and BenchmarkDotNet / Basic example where noted). They are orientation only — absolute RPS varies by hardware, OS, and background load.

For pooling knobs and certificate first-visit tuning, see [Performance and pooling](Home#performance-and-pooling).

## Measurement environment

### Windows (developer laptop)

Unless a subsection names another host, saturation RPS tables used:

| | |
|---|---|
| OS | Windows 11 (10.0.26200) |
| CPU | 11th Gen Intel Core i7-1185G7 @ 3.00 GHz (8 logical processors) |
| RAM | 31.8 GiB |
| Runtime | .NET 10.0.10 |
| nginx | nginx/Windows **1.31.3** |
| Harness | RpsLoadProbe Release; arms run **sequentially** |

### Linux (GitHub-hosted `ubuntu-latest`)

Fair TLS-terminate numbers in [Linux saturation](#fair-tls-terminate-compare--linux-github-hosted-ubuntu-latest) were measured on a stock Actions runner (not a container job):

| | |
|---|---|
| OS | Ubuntu 24.04.4 LTS |
| CPU | AMD EPYC 7763 (4 logical processors on the VM) |
| RAM | 15.6 GiB |
| Runtime | .NET 10.0.11 |
| nginx | nginx/1.24.0 (Ubuntu) |
| Harness | RpsLoadProbe Release; `compare-terminate` (HTTP/3 arms skipped — no QuicListener / msquic on this image) |

## At a glance

| What | Result |
|---|---|
| HTTPS TTFB vs direct (median, 14 hosts) | Cold **≈ parity** (−1 ms); warm **−25 ms** (proxy faster) |
| HTTP/1 loopback GET (no body intercept) | **~186 µs**, **~17.5 KB** allocated / request |
| Cleartext reverse HTTP/1 peak | **~16.0k RPS** (TWP) vs **~15.5k RPS** (nginx/Windows) |
| TLS-terminate reverse HTTP/1 peak (Windows) | **~21.8k RPS** (TWP) vs **~14.4k RPS** (nginx/Windows) |
| TLS-terminate H2→H1 cleartext peak (Windows) | **~7.4k RPS** @ c=64, **0% err** (TWP) · nginx **~14.9k** @ c=32 |
| TLS-terminate H3→H1 cleartext peak (Windows) | **~2.4k RPS** @ c=32, **0% err** (TWP) |
| Cross-version bridges under load (Windows) | All H1↔H2↔H3 directions **0% err** — see [Bridge matrix](#bridge-matrix-compare-bridges--windows) |
| TLS-terminate reverse HTTP/1 peak (Linux GHA) | **~18–37k RPS** (TWP) vs **~28–61k** (nginx); **TWP÷nginx ≈ 0.61** stable — see [Linux vs Windows](#linux-vs-windows-h1-tls) |
| TLS-terminate H2→H1 cleartext peak (Linux GHA) | **~10–24k RPS** (TWP) · nginx H2 **~19–42k** peak |
| Explicit HTTPS MITM peak | **~13.6k RPS** |
| Basic example footprint (Release, after load) | **~74 MB** working set · **~24–29 MB** private bytes |

### Why isn’t HTTP/3 > HTTP/2 > HTTP/1 in raw RPS?

For **tiny JSON responses** (~64 B) on loopback, that ordering is **not** expected:

1. **Topology dominates protocol.** Always compare arms that share the same crypto hop count and upstream protocol.
2. **HTTP/2/3 shine at multiplexing**, not at maximizing single-origin tiny-GET RPS.
3. **Fair terminate topology** (client TLS → cleartext origin) is what nginx uses for H2. TWP matches that with `ForwardCleartext` + the H2→H1 bridge (and H1 TLS terminate).

### H2→H1 cleartext bridge

Under multiplexed load an earlier bridge path used `RespondStreaming` (HEADERS without `END_STREAM` + DATA). .NET `HttpClient` reported **`Received an HTTP/2 pseudo-header as a trailing header`** and error rates climbed with concurrency. The stream was also missing `IsExternalBridge`, racing `Http2Helper` against the synthetic emitter.

The shipped path marks `IsExternalBridge`, buffers the origin body, and emits via the buffered synthetic path. Keep-alive pooling remains enabled with residual-buffer and lease guards.

## Saturation RPS

Reproduce locally (Release):

```powershell
pwsh tools/RpsLoadProbe/run-rps.ps1 -Mode compare-terminate
```

### Fair TLS-terminate compare (`compare-terminate`) — Windows

Local Release (warmup 1s / measure 4s; concurrency 8, 32, 64). Host: [Windows (developer laptop)](#windows-developer-laptop). Cleartext-origin arms use **process-split** origin/proxy.

| Arm | Topology | Sustainable | Peak | Notes |
|---|---|---:|---:|---|
| TWP H1 TLS | Client TLS → cleartext H1 | **21,803** @ 64 | **21,803** | 0% err |
| nginx H1 TLS | ssl → cleartext H1 | **13,826** @ 64 | **14,424** @ 32 | 0% err |
| TWP H2→H1 | Client h2 TLS → H2→H1 bridge → cleartext H1 | **7,373** @ 64 | **7,373** | **0% err** |
| nginx H2 | Client h2 TLS → cleartext H1 | **5,898** @ 64 | **14,920** @ 32 | 0% err |
| TWP H3→H1 | Client h3 → cleartext H1 | **2,327** @ 64 | **2,423** @ 32 | **0% err** |

### Bridge matrix (`compare-bridges`) — Windows

Local Release `compare-bridges` (warmup 1s / measure 3s; concurrency 8, 32). All arms **0% error**.

| Arm | Client → origin | Peak @ c=32 |
|---|---|---:|
| H2→H1 cleartext | H2 TLS → H1 cleartext | **9,104** |
| H1→H2 | H1 TLS → H2 TLS | **8,843** |
| H1→H3 | H1 TLS → H3 QUIC | **13,499** |
| H2→H3 | H2 TLS → H3 QUIC | **5,055** |
| H3→H1 cleartext | H3 → H1 cleartext | **3,593** |
| H3→H2 | H3 → H2 TLS | **1,842** |
| H2↔H2 MITM | H2 TLS → H2 TLS | **4,330** |
| H3↔H3 MITM | H3 → H3 | **8,477** |

### Linux vs Windows (H1 TLS)

On the Windows laptop, TWP H1 TLS **leads** nginx/Windows (~21.8k vs ~14.4k). On Linux GHA, **nginx leads**. Absolute GHA RPS swings ~2× between VMs, but the **TWP÷nginx ratio stays ≈ 0.61** across runs (including after process-split, ThreadPool floors, and Server GC on the probe). The ranking flip is **nginx/Windows being slow** vs native Linux nginx (epoll), not a Linux-only TWP correctness bug. The residual ~40% is managed SslStream + proxy pipeline vs nginx C — not closed by harness knobs.

### Fair TLS-terminate compare — Linux (GitHub-hosted `ubuntu-latest`)

Latest CSV: `rps-ramp-20260816-082447.csv` (Actions artifact `rps-csv` from run [31936352891](https://github.com/justcoding121/titanium-web-proxy/actions/runs/31936352891) @ `cbed2a40`; warmup 2s / measure 8s; concurrency 8, 16, 32, 64). Host: [Linux (GitHub-hosted)](#linux-github-hosted-ubuntu-latest). HTTP/3 arms skipped (no QuicListener / msquic on this image). A quieter VM earlier the same day hit ~37k / ~61k at the same ~0.61 ratio ([31936116039](https://github.com/justcoding121/titanium-web-proxy/actions/runs/31936116039)).

| Arm | Topology | Sustainable | Peak | Notes |
|---|---|---:|---:|---|
| TWP H1 TLS | Client TLS → cleartext H1 | **20,734** @ 64 | **20,734** | 0% err · ≈61% of nginx |
| nginx H1 TLS | ssl → cleartext H1 | **34,104** @ 64 | **34,104** | 0% err |
| TWP H2→H1 | Client h2 TLS → H2→H1 bridge → cleartext H1 | **13,886** @ 64 | **13,886** | **0% err** |
| nginx H2 | Client h2 TLS → cleartext H1 | **16,235** @ 64 | **22,267** @ 32 | ~0% err |

On the GHA VM (4 vCPU), nginx still leads peak RPS; TWP stays zero-error.

### Protocol / topology matrix

| Client | Upstream | TWP | nginx/Windows | Mode |
|---|---|---|---|---|
| H1 cleartext | H1 cleartext | yes | yes | `compare` |
| H1 TLS | H1 cleartext | `ForwardCleartext` | ssl `proxy_pass http://` | `compare-terminate` |
| H2 TLS | H1 cleartext | H2→H1 bridge + `ForwardCleartext` | ssl+http2 → cleartext | `compare-terminate` |
| H2 TLS | H2 TLS (MITM) | native h2↔h2 | n/a (nginx terminates) | `compare-tls` / `reverse-http2` |
| H2 TLS | H2 cleartext (h2c) | **not supported** (no h2c) | uncommon | — |
| H3 QUIC | H3 QUIC | MITM | **no QUIC on nginx/Windows** | `reverse-http3` |
| H1 TLS | H2 TLS | H1→H2 bridge | — | `reverse-http11-to-http2` |
| H1 TLS | H3 QUIC | H1→H3 bridge | — | `reverse-http1-to-http3` |
| H2 TLS | H3 QUIC | H2→H3 bridge | — | `reverse-http2-to-http3` |
| H3 QUIC | H2 TLS | H3→H2 (`Http2OriginConnection` pool) | — | `reverse-http3-to-http2` |
| H3 QUIC | H1 cleartext | `ForwardCleartext` + Http11 | — | `reverse-http3-cleartext` |
| All of above (matrix) | | | | `compare-bridges` |

## Raising limits on large hosts

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

```powershell
dotnet run -c Release --project benchmarks/Titanium.Web.Proxy.Benchmarks -- --filter '*Throughput*'
```

| Benchmark | Setup | Mean | Allocated / op |
|---|---|---:|---:|
| HTTP/1 GET through proxy | Passthrough | **186 µs** | **17.5 KB** |
| HTTP/2 multiplexed GETs | 10 concurrent streams | **3.0 ms** / batch | **~14 KB** / request |

## Process footprint (Basic example)

| Metric | Approx. value |
|---|---:|
| Working set | **~74 MB** |
| Private bytes | **~24–29 MB** |
