# Performance

Titanium targets **low-overhead MITM proxying**: connection pooling, HTTP/2 multiplexing, and buffer reuse. Numbers below are **Release** measurements with [RpsLoadProbe](https://github.com/justcoding121/titanium-web-proxy/tree/develop/tools/RpsLoadProbe) (and BenchmarkDotNet / Basic example where noted). Absolute RPS varies by hardware, OS, and background load — compare **within a table**, not across Windows vs Linux.

For pooling knobs and certificate first-visit tuning, see [Performance and pooling](Home#performance-and-pooling).

## Measurement environment

### Windows (developer laptop)

| | |
|---|---|
| OS | Windows 11 (10.0.26200) |
| CPU | 11th Gen Intel Core i7-1185G7 @ 3.00 GHz (8 logical processors) |
| RAM | 31.8 GiB |
| Runtime | .NET 10.0.10 |
| nginx | nginx/Windows **1.31.3** |
| Harness | RpsLoadProbe Release; arms run **sequentially** |

### Linux (GitHub-hosted `ubuntu-latest`)

| | |
|---|---|
| OS | Ubuntu 24.04.4 LTS |
| CPU | AMD EPYC 7763 (4 logical processors on the VM) |
| RAM | 15.6 GiB |
| Runtime | .NET 10.0.11 |
| nginx | nginx/1.24.0 (Ubuntu) |
| Harness | RpsLoadProbe Release; median of 3 repeats where noted |

**How to read the tables:** each row is one client → origin path. **Sustainable** = last concurrency that still met error/latency SLOs. **Peak** = highest RPS observed in that ramp. **Winner** = higher **sustainable** RPS on that OS (peak is informational). *Not possible* means that product cannot do that path. *Not measured* means the path exists but we have not published a number for that OS yet. Where nginx cannot compete, Winner is **TWP**.

```powershell
pwsh tools/RpsLoadProbe/run-rps.ps1 -Mode compare-same
pwsh tools/RpsLoadProbe/run-rps.ps1 -Mode compare-terminate
pwsh tools/RpsLoadProbe/run-rps.ps1 -Mode compare-bridges
```

## Windows — Titanium vs nginx

Client / origin columns: HTTP version and whether TLS is used (`plain` = cleartext, `TLS` = encrypted, `QUIC` = HTTP/3).

| Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | Winner |
|---|---|---:|---:|---:|---:|---|
| HTTP/1 · plain | HTTP/1 · plain | **29,376** | **29,376** | **24,587** | **24,587** | **TWP** |
| HTTP/1 · TLS | HTTP/1 · plain | **19,951** | **29,511** | **12,072** | **13,501** | **TWP** |
| HTTP/1 · TLS | HTTP/1 · TLS | **22,540** | **22,540** | *Not possible* (no MITM) | *Not possible* | **TWP** |
| HTTP/2 · TLS | HTTP/1 · plain | **9,875** | **10,441** | **5,841** | **13,465** | **TWP** |
| HTTP/2 · TLS | HTTP/2 · TLS | **6,168** | **6,168** | *Not possible* (no MITM) | *Not possible* | **TWP** |
| HTTP/2 · TLS | HTTP/2 · plain | **6,889** | **6,889** | *Not possible* | *Not possible* | **TWP** |
| HTTP/2 · plain | HTTP/1 · plain | **10,757** | **11,088** | *Not possible* | *Not possible* | **TWP** |
| HTTP/2 · plain | HTTP/2 · plain | **6,344** | **6,344** | *Not possible* | *Not possible* | **TWP** |
| HTTP/2 · plain | HTTP/2 · TLS | **6,036** | **6,036** | *Not possible* | *Not possible* | **TWP** |
| HTTP/2 · plain | HTTP/3 · QUIC | **7,587** | **7,949** | *Not possible* (no QUIC) | *Not possible* | **TWP** |
| HTTP/3 · QUIC | HTTP/1 · plain | **2,246** | **3,541** | *Not possible* (no QUIC) | *Not possible* | **TWP** |
| HTTP/3 · QUIC | HTTP/2 · TLS | **1,842** | **1,842** | *Not possible* (no QUIC) | *Not possible* | **TWP** |
| HTTP/3 · QUIC | HTTP/3 · QUIC | **7,335** | **7,335** | *Not possible* (no QUIC) | *Not possible* | **TWP** |
| HTTP/1 · TLS | HTTP/2 · TLS | **8,843** | **8,843** | *Not possible* | *Not possible* | **TWP** |
| HTTP/1 · TLS | HTTP/3 · QUIC | **13,499** | **13,499** | *Not possible* (no QUIC) | *Not possible* | **TWP** |
| HTTP/2 · TLS | HTTP/3 · QUIC | **5,055** | **5,055** | *Not possible* (no QUIC) | *Not possible* | **TWP** |

Windows sources: `compare-same` / `compare-terminate` / `compare-bridges` / `reverse-http2-to-h2c` / `reverse-h2c*` (warmup 1s; measure 3–4s; concurrency up to 256). All published TWP arms **0% error**.

nginx/Windows is a limited port. Use it for **same-OS** comparison only — not as the industry nginx baseline.

**H2 TLS → H1 plain on Windows:** TWP wins **sustain** (Winner column). nginx can still post a higher **short-burst peak** at low concurrency before collapsing; closing that peak gap needs a thinner reverse path than the full MITM H2→H1 session bridge.

## Linux — Titanium vs nginx

Median of **3 repeats** from Actions runs [31944380342](https://github.com/justcoding121/titanium-web-proxy/actions/runs/31944380342) (`compare-same`), [31944381620](https://github.com/justcoding121/titanium-web-proxy/actions/runs/31944381620) (`compare-terminate`), [31944382877](https://github.com/justcoding121/titanium-web-proxy/actions/runs/31944382877) (`compare-bridges`); warmup 2s / measure 8s; concurrency 8, 16, 32, 64. **Linux nginx is the authoritative nginx baseline.** The RPS workflow installs `libmsquic` (`QuicListener.IsSupported=true` on `ubuntu-latest`).

**Why nginx still leads on H1 plain (~0.65×):** the harness is fair (split processes, same Kestrel origin). Absolute RPS swings hard by GHA VM; prefer the **ratio**. TWP reverse cleartext still runs the full MITM session pipeline per keep-alive GET (`SessionEventArgs`, dual header rebuild, framing validation, async deadlines) — nginx `proxy_pass` is a thin C reverse path. Mitigations shipped: sticky `ForwardHost` upstream (no per-GET pool Get/Release), `Poll(0)` half-close checks, skip h2c peek / H3 resolve when those features are off, and skip `NetworkStream` header `FlushAsync`. Closing the rest needs a dedicated thin reverse byte-path (not the session API).

| Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | Winner |
|---|---|---:|---:|---:|---:|---|
| HTTP/1 · plain | HTTP/1 · plain | **25,782** | **25,782** | **39,351** | **39,351** | **nginx** |
| HTTP/1 · TLS | HTTP/1 · plain | **18,678** | **18,678** | **28,398** | **28,398** | **nginx** |
| HTTP/1 · TLS | HTTP/1 · TLS | **15,828** | **15,828** | *Not possible* (no MITM) | *Not possible* | **TWP** |
| HTTP/2 · TLS | HTTP/1 · plain | **11,233** | **11,233** | **13,545** | **19,079** | **nginx** |
| HTTP/2 · TLS | HTTP/2 · TLS | **5,283** | **5,289** | *Not possible* (no MITM) | *Not possible* | **TWP** |
| HTTP/2 · TLS | HTTP/2 · plain | **12,744** | **12,897** | *Not possible* | *Not possible* | **TWP** |
| HTTP/2 · plain | HTTP/1 · plain | **32,381** | **32,381** | *Not possible* | *Not possible* | **TWP** |
| HTTP/2 · plain | HTTP/2 · plain | **8,005** | **8,005** | *Not possible* | *Not possible* | **TWP** |
| HTTP/2 · plain | HTTP/2 · TLS | **5,832** | **5,832** | *Not possible* | *Not possible* | **TWP** |
| HTTP/2 · plain | HTTP/3 · QUIC | **19,796** | **19,796** | *Not possible* (no QUIC) | *Not possible* | **TWP** |
| HTTP/3 · QUIC | HTTP/1 · plain | **7,225** | **7,225** | *Not possible* (no QUIC) | *Not possible* | **TWP** |
| HTTP/3 · QUIC | HTTP/2 · TLS | **3,050** | **10,322** | *Not possible* (no QUIC) | *Not possible* | **TWP** |
| HTTP/3 · QUIC | HTTP/3 · QUIC | **9,598** | **9,598** | *Not possible* (no QUIC) | *Not possible* | **TWP** |
| HTTP/1 · TLS | HTTP/2 · TLS | **19,244** | **19,244** | *Not possible* | *Not possible* | **TWP** |
| HTTP/1 · TLS | HTTP/3 · QUIC | **19,352** | **19,352** | *Not possible* (no QUIC) | *Not possible* | **TWP** |
| HTTP/2 · TLS | HTTP/3 · QUIC | **17,758** | **17,758** | *Not possible* (no QUIC) | *Not possible* | **TWP** |

On this GHA shape, TWP H1 plain ÷ nginx H1 plain ≈ **0.66**. Absolute RPS swings by VM; prefer the **ratio** and **median across repeats**.

### Why isn’t HTTP/3 > HTTP/2 > HTTP/1 in raw RPS?

For **tiny JSON responses** (~64 B) on loopback, that ordering is **not** expected: topology (TLS hop count, terminate vs MITM) dominates; HTTP/2 and HTTP/3 help multiplexing, not single-origin tiny-GET RPS.

## Other measurements

| What | Result |
|---|---|
| HTTPS TTFB vs direct (median, 14 hosts) | Cold **≈ parity** (−1 ms); warm **−25 ms** (proxy faster) |
| HTTP/1 loopback GET (no body intercept) | **~186 µs**, **~17.5 KB** allocated / request |
| Basic example footprint (Release, after load) | **~74 MB** working set · **~24–29 MB** private bytes |

```powershell
dotnet run -c Release --project benchmarks/Titanium.Web.Proxy.Benchmarks -- --filter '*Throughput*'
```

| Benchmark | Setup | Mean | Allocated / op |
|---|---|---:|---:|
| HTTP/1 GET through proxy | Passthrough | **186 µs** | **17.5 KB** |
| HTTP/2 multiplexed GETs | 10 concurrent streams | **3.0 ms** / batch | **~14 KB** / request |

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
    MaxCachedConnections = 256,
    GenericCertificateName = "example.com"
};
ep.BeforeSslAuthenticate += (_, a) =>
{
    a.UpstreamHttpProtocol = UpstreamHttpProtocol.Http11;
    a.AllowHttpProtocolTranslation = true; // HTTP/2 client → HTTP/1 origin
    return Task.CompletedTask;
};
```
