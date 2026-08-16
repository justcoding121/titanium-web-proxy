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

Median of **3 repeats** from Actions runs [31940116289](https://github.com/justcoding121/titanium-web-proxy/actions/runs/31940116289) (`compare-same`), [31940118740](https://github.com/justcoding121/titanium-web-proxy/actions/runs/31940118740) (`compare-terminate`), [31940117559](https://github.com/justcoding121/titanium-web-proxy/actions/runs/31940117559) (`compare-bridges`); warmup 2s / measure 8s; concurrency 8, 16, 32, 64. **Linux nginx is the authoritative nginx baseline.** The RPS workflow installs `libmsquic` so HTTP/3 arms can run when the package is available for the runner distro.

**H1 plain gap (why nginx led ~0.63×):** the harness is fair (split processes, same Kestrel origin). TWP was paying per keep-alive GET for shared-pool Get/Release + `IsGoodConnection` and header-rebuild churn. Transparent reverse with fixed `ForwardHost` now keeps the origin socket sticky on the client connection (nginx-like upstream keepalive); `HeaderBuilder` status-line encoding and response write paths were also tightened. Re-measure `compare-same` after that change before treating the H1 plain row as final.

| Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | Winner |
|---|---|---:|---:|---:|---:|---|
| HTTP/1 · plain | HTTP/1 · plain | **35,393** | **35,393** | **56,358** | **56,358** | **nginx** |
| HTTP/1 · TLS | HTTP/1 · plain | **27,353** | **27,353** | **43,973** | **43,973** | **nginx** |
| HTTP/1 · TLS | HTTP/1 · TLS | **24,183** | **24,183** | *Not possible* (no MITM) | *Not possible* | **TWP** |
| HTTP/2 · TLS | HTTP/1 · plain | **11,167** | **11,813** | **13,326** | **18,428** | **nginx** |
| HTTP/2 · TLS | HTTP/2 · TLS | **8,593** | **8,593** | *Not possible* (no MITM) | *Not possible* | **TWP** |
| HTTP/2 · TLS | HTTP/2 · plain | **12,363** | **12,363** | *Not possible* | *Not possible* | **TWP** |
| HTTP/2 · plain | HTTP/1 · plain | **29,930** | **29,930** | *Not possible* | *Not possible* | **TWP** |
| HTTP/2 · plain | HTTP/2 · plain | **11,409** | **11,523** | *Not possible* | *Not possible* | **TWP** |
| HTTP/2 · plain | HTTP/2 · TLS | **9,524** | **9,524** | *Not possible* | *Not possible* | **TWP** |
| HTTP/2 · plain | HTTP/3 · QUIC | *Not measured* (no msquic on runner) | *Not measured* | *Not possible* (no QUIC) | *Not possible* | — |
| HTTP/3 · QUIC | HTTP/1 · plain | *Not measured* (no msquic on runner) | *Not measured* | *Not possible* (no QUIC) | *Not possible* | — |
| HTTP/3 · QUIC | HTTP/2 · TLS | *Not measured* (no msquic on runner) | *Not measured* | *Not possible* (no QUIC) | *Not possible* | — |
| HTTP/3 · QUIC | HTTP/3 · QUIC | *Not measured* (no msquic on runner) | *Not measured* | *Not possible* (no QUIC) | *Not possible* | — |
| HTTP/1 · TLS | HTTP/2 · TLS | **18,363** | **18,363** | *Not possible* | *Not possible* | **TWP** |
| HTTP/1 · TLS | HTTP/3 · QUIC | *Not measured* (no msquic on runner) | *Not measured* | *Not possible* (no QUIC) | *Not possible* | — |
| HTTP/2 · TLS | HTTP/3 · QUIC | *Not measured* (no msquic on runner) | *Not measured* | *Not possible* (no QUIC) | *Not possible* | — |

On this GHA shape, TWP H1 TLS ÷ nginx H1 TLS ≈ **0.62**. Absolute RPS swings by VM; prefer the **ratio** and **median across repeats**.

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
