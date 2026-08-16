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

**How to read the tables**

- **Mode**: **Reverse** = transparent fixed-forward (may TLS-terminate to a cleartext origin). **MITM** = proxy decrypts the client crypto **and** speaks TLS/QUIC to the origin (or explicit decrypt proxy), so both legs are visible in the clear inside TWP. nginx cannot do MITM.
- **Sustainable** = last concurrency that still met error/latency SLOs. **Peak** = highest RPS in that ramp.
- **Winner** = higher **sustainable** RPS when **both** TWP and nginx have a number. Left **blank** when nginx cannot run that path (no fair comparison).
- *Not possible* = product cannot do that path. *Not measured* = path exists but no published number yet for that OS.

```powershell
pwsh tools/RpsLoadProbe/run-rps.ps1 -Mode compare-same
pwsh tools/RpsLoadProbe/run-rps.ps1 -Mode compare-terminate
pwsh tools/RpsLoadProbe/run-rps.ps1 -Mode compare-bridges
pwsh tools/RpsLoadProbe/run-rps.ps1 -Mode compare-mitm
pwsh tools/RpsLoadProbe/run-rps.ps1 -Mode compare-bodies
pwsh tools/RpsLoadProbe/run-rps.ps1 -Mode compare-post
pwsh tools/RpsLoadProbe/run-rps.ps1 -Mode compare-lossy
pwsh tools/RpsLoadProbe/run-rps.ps1 -Mode compare-tls-cost
```

## Windows — Titanium vs nginx

Client / origin: HTTP version and whether TLS is used (`plain` = cleartext, `TLS` = encrypted, `QUIC` = HTTP/3).

| Mode | Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | Winner |
|---|---|---|---:|---:|---:|---:|---|
| Reverse | HTTP/1 · plain | HTTP/1 · plain | **29,376** | **29,376** | **24,587** | **24,587** | **TWP** |
| Reverse | HTTP/1 · TLS | HTTP/1 · plain | **19,951** | **29,511** | **12,072** | **13,501** | **TWP** |
| MITM | HTTP/1 · TLS | HTTP/1 · TLS | **29,713** | **30,755** | *Not possible* (no MITM) | *Not possible* | |
| Reverse | HTTP/2 · TLS | HTTP/1 · plain | **9,875** | **10,441** | **5,841** | **13,465** | **TWP** |
| MITM | HTTP/2 · TLS | HTTP/1 · TLS | **9,774** | **10,242** | *Not possible* (no MITM) | *Not possible* | |
| MITM | HTTP/2 · TLS | HTTP/2 · TLS | **6,222** | **6,222** | *Not possible* (no MITM) | *Not possible* | |
| Reverse | HTTP/2 · TLS | HTTP/2 · plain | **6,889** | **6,889** | *Not possible* | *Not possible* | |
| Reverse | HTTP/2 · plain | HTTP/1 · plain | **10,757** | **11,088** | *Not possible* | *Not possible* | |
| Reverse | HTTP/2 · plain | HTTP/2 · plain | **6,344** | **6,344** | *Not possible* | *Not possible* | |
| Reverse | HTTP/2 · plain | HTTP/2 · TLS | **6,036** | **6,036** | *Not possible* | *Not possible* | |
| Reverse | HTTP/2 · plain | HTTP/3 · QUIC | **7,587** | **7,949** | *Not possible* (no QUIC) | *Not possible* | |
| Reverse | HTTP/3 · QUIC | HTTP/1 · plain | **2,246** | **3,541** | *Not possible* (no QUIC) | *Not possible* | |
| MITM | HTTP/3 · QUIC | HTTP/1 · TLS | **10,416** | **10,416** | *Not possible* (no QUIC) | *Not possible* | |
| MITM | HTTP/3 · QUIC | HTTP/2 · TLS | **2,127** | **7,779** | *Not possible* (no QUIC) | *Not possible* | |
| MITM | HTTP/3 · QUIC | HTTP/3 · QUIC | **6,479** | **7,430** | *Not possible* (no QUIC) | *Not possible* | |
| MITM | HTTP/1 · TLS | HTTP/2 · TLS | **16,423** | **16,709** | *Not possible* | *Not possible* | |
| MITM | HTTP/1 · TLS | HTTP/3 · QUIC | **16,524** | **16,524** | *Not possible* (no QUIC) | *Not possible* | |
| MITM | HTTP/2 · TLS | HTTP/3 · QUIC | **7,151** | **7,151** | *Not possible* (no QUIC) | *Not possible* | |

Windows sources: `compare-same` / `compare-terminate` / `compare-bridges` / local `compare-mitm` (`rps-ramp-20260816-123409`, warmup 1s; measure 3s; concurrency 8–64). H3→H1 TLS MITM from `rps-ramp-20260816-132745` after origin TCP pool release. All published TWP arms **0% error**.

nginx/Windows is a limited port. Use it for **same-OS** comparison only — not as the industry nginx baseline.

**H2 TLS → H1 plain on Windows:** TWP wins **sustain** when both products run reverse terminate. nginx can still post a higher **short-burst peak** at low concurrency before collapsing.

## Linux — Titanium vs nginx

Median of **3 repeats** from Actions runs [31954143134](https://github.com/justcoding121/titanium-web-proxy/actions/runs/31954143134) (`compare-same`), [31954144794](https://github.com/justcoding121/titanium-web-proxy/actions/runs/31954144794) (`compare-terminate`), [31955723330](https://github.com/justcoding121/titanium-web-proxy/actions/runs/31955723330) (`compare-mitm`), [31955725199](https://github.com/justcoding121/titanium-web-proxy/actions/runs/31955725199) (`compare-bridges`). Ceiling control: [31954146336](https://github.com/justcoding121/titanium-web-proxy/actions/runs/31954146336) (`compare-ceiling`). Warmup 2s / measure 8s; concurrency 8, 16, 32, 64. All published TWP arms **0% error**. **Linux nginx is the authoritative nginx baseline.** The RPS workflow installs `libmsquic` (`QuicListener.IsSupported=true` on `ubuntu-latest`).

**Why nginx still leads on H1 plain reverse (~0.71×):** fair harness (split processes, same Kestrel origin). Absolute RPS swings by GHA VM; prefer the **ratio**. A bare C# HTTP/1 reverse on the same job is ~**0.81×** nginx (`compare-ceiling`) — most of the gap is managed runtime vs nginx’s thin C `proxy_pass`, not remaining TWP session work.

| Mode | Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | Winner |
|---|---|---|---:|---:|---:|---:|---|
| Reverse | HTTP/1 · plain | HTTP/1 · plain | **27,826** | **27,826** | **39,254** | **39,254** | **nginx** |
| Reverse | HTTP/1 · TLS | HTTP/1 · plain | **29,808** | **29,808** | **44,200** | **44,200** | **nginx** |
| MITM | HTTP/1 · TLS | HTTP/1 · TLS | **25,493** | **25,493** | *Not possible* (no MITM) | *Not possible* | |
| Reverse | HTTP/2 · TLS | HTTP/1 · plain | **18,583** | **18,726** | **21,177** | **29,330** | **nginx** |
| MITM | HTTP/2 · TLS | HTTP/1 · TLS | **17,868** | **17,868** | *Not possible* (no MITM) | *Not possible* | |
| MITM | HTTP/2 · TLS | HTTP/2 · TLS | **8,508** | **8,508** | *Not possible* (no MITM) | *Not possible* | |
| Reverse | HTTP/2 · TLS | HTTP/2 · plain | **7,345** | **7,546** | *Not possible* | *Not possible* | |
| Reverse | HTTP/2 · plain | HTTP/1 · plain | **18,324** | **18,324** | *Not possible* | *Not possible* | |
| Reverse | HTTP/2 · plain | HTTP/2 · plain | **9,108** | **9,108** | *Not possible* | *Not possible* | |
| Reverse | HTTP/2 · plain | HTTP/2 · TLS | **5,846** | **5,846** | *Not possible* | *Not possible* | |
| Reverse | HTTP/2 · plain | HTTP/3 · QUIC | **13,910** | **13,910** | *Not possible* (no QUIC) | *Not possible* | |
| Reverse | HTTP/3 · QUIC | HTTP/1 · plain | **19,981** | **19,981** | *Not possible* (no QUIC) | *Not possible* | |
| MITM | HTTP/3 · QUIC | HTTP/1 · TLS | **18,153** | **18,153** | *Not possible* (no QUIC) | *Not possible* | |
| MITM | HTTP/3 · QUIC | HTTP/2 · TLS | **3,049** | **9,909** | *Not possible* (no QUIC) | *Not possible* | |
| MITM | HTTP/3 · QUIC | HTTP/3 · QUIC | **11,027** | **11,027** | *Not possible* (no QUIC) | *Not possible* | |
| MITM | HTTP/1 · TLS | HTTP/2 · TLS | **16,783** | **16,783** | *Not possible* | *Not possible* | |
| MITM | HTTP/1 · TLS | HTTP/3 · QUIC | **19,233** | **19,233** | *Not possible* (no QUIC) | *Not possible* | |
| MITM | HTTP/2 · TLS | HTTP/3 · QUIC | **13,803** | **13,803** | *Not possible* (no QUIC) | *Not possible* | |

On this GHA shape, TWP H1 plain ÷ nginx H1 plain ≈ **0.71** (27,826 / 39,254). H1 TLS terminate ≈ **0.67** (29,808 / 44,200). Absolute RPS swings by VM; prefer the **ratio** and **median across repeats**.

### Tiny JSON is nginx’s best case (and TWP’s worst)

The tables above use **~64 B keep-alive GET** on loopback. That is a thin reverse `proxy_pass` workload: nginx’s C path wins on Linux, and TWP still pays for a full session pipeline per request. **“Comparable” on reverse only shows up when the work gets heavier** — larger bodies, mutating methods, TLS handshake cost, or delay/loss that exposes HTTP/2 head-of-line blocking. Tiny JSON is the wrong target if the question is whether TWP can keep up with nginx as a reverse proxy under real traffic.

nginx still cannot MITM or speak QUIC in this harness; those paths remain TWP-only.

### Why isn’t HTTP/3 > HTTP/2 > HTTP/1 in raw RPS?

For **tiny JSON responses** (~64 B) on loopback, that ordering is **not** expected: topology (TLS hop count, terminate vs MITM) dominates; HTTP/2 and HTTP/3 help multiplexing, not single-origin tiny-GET RPS. See the **lossy** tables below for a workload where protocol design matters.

## Heavier reverse workloads

Separate from the tiny-GET matrix. Same measurement environments. Modes: `compare-bodies`, `compare-post`, `compare-lossy`, `compare-tls-cost` in [RpsLoadProbe](https://github.com/justcoding121/titanium-web-proxy/tree/develop/tools/RpsLoadProbe). **PUT with the same body is the same proxy work as POST; DELETE with no body matches GET** — only POST is published.

Lossy link = **userspace** shim (not kernel `netem`): TCP gets per-buffer delay + occasional whole-connection stalls (honest HOL for multiplexed H2); UDP datagram drop exists in the harness but H3 lossy arms are not published yet (MsQuic + multi-connection load hangs through the shim).

### Windows — heavier reverse GET (64 KiB / 256 KiB)

Warmup 1s / measure 3s; concurrency 8–64. Source: local `compare-bodies` (`rps-ramp-20260816-160548`).

| Body | Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | Winner |
|---|---|---|---:|---:|---:|---:|---|
| 64 KiB | HTTP/1 · TLS | HTTP/1 · plain | **10,473** | **10,692** | **926** | **1,016** | **TWP** |
| 64 KiB | HTTP/2 · TLS | HTTP/1 · plain | **3,600** | **3,656** | **877** | **941** | **TWP** |
| 64 KiB | HTTP/3 · QUIC | HTTP/1 · plain | **4,567** | **4,567** | *Not possible* (no QUIC) | *Not possible* | |
| 256 KiB | HTTP/1 · TLS | HTTP/1 · plain | **3,246** | **3,266** | **217** | **254** | **TWP** |
| 256 KiB | HTTP/2 · TLS | HTTP/1 · plain | **1,032** | **1,048** | **169** | **205** | **TWP** |
| 256 KiB | HTTP/3 · QUIC | HTTP/1 · plain | **1,048** | **1,145** | *Not possible* (no QUIC) | *Not possible* | |

nginx/Windows collapses on large reverse bodies in this harness; treat as same-OS only.

### Linux — heavier reverse GET (64 KiB / 256 KiB)

Median of **3** repeats. Source: Actions [31958194269](https://github.com/justcoding121/titanium-web-proxy/actions/runs/31958194269) (`compare-bodies`). Warmup 2s / measure 8s.

| Body | Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | Winner |
|---|---|---|---:|---:|---:|---:|---|
| 64 KiB | HTTP/1 · TLS | HTTP/1 · plain | **6,565** | **6,565** | **8,375** | **8,375** | **nginx** |
| 64 KiB | HTTP/2 · TLS | HTTP/1 · plain | **3,342** | **3,408** | **3,498** | **3,499** | **nginx** |
| 64 KiB | HTTP/3 · QUIC | HTTP/1 · plain | **3,631** | **3,631** | *Not possible* (no QUIC) | *Not possible* | |
| 256 KiB | HTTP/1 · TLS | HTTP/1 · plain | **2,154** | **2,154** | **2,728** | **2,728** | **nginx** |
| 256 KiB | HTTP/2 · TLS | HTTP/1 · plain | **1,015** | **1,015** | **0** | **4** | **TWP** |
| 256 KiB | HTTP/3 · QUIC | HTTP/1 · plain | **1,117** | **1,117** | *Not possible* (no QUIC) | *Not possible* | |

On Linux H1 TLS, TWP÷nginx ≈ **0.78** at 64 KiB and ≈ **0.79** at 256 KiB — better than the tiny-GET ≈0.66–0.71 ratio, but nginx still leads sustain when both stay healthy. nginx H2 at 256 KiB failed this harness (~100% errors); TWP H2/H3 completed.

### Windows — POST 64 KiB request + 64 KiB response

Source: local `compare-post` (`rps-ramp-20260816-160844`).

| Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | Winner |
|---|---|---:|---:|---:|---:|---|
| HTTP/1 · TLS | HTTP/1 · plain | **5,070** | **5,070** | **210** | **212** | **TWP** |
| HTTP/2 · TLS | HTTP/1 · plain | **88** | **124** | **294** | **294** | **nginx** |
| HTTP/3 · QUIC | HTTP/1 · plain | **0** | **0** | *Not possible* | *Not possible* | |

H3 POST reverse-terminate hit stream aborts in the probe (not published as a capability claim).

### Linux — POST 64 KiB request + 64 KiB response

Median of **3** repeats. Source: Actions [31958195358](https://github.com/justcoding121/titanium-web-proxy/actions/runs/31958195358) (`compare-post`).

| Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | Winner |
|---|---|---:|---:|---:|---:|---|
| HTTP/1 · TLS | HTTP/1 · plain | **3,942** | **3,942** | **0** | **0** | **TWP** |
| HTTP/2 · TLS | HTTP/1 · plain | **120** | **242** | **0** | **0** | **TWP** |
| HTTP/3 · QUIC | HTTP/1 · plain | **0** | **0** | *Not possible* | *Not possible* | |

Linux nginx returned **100% errors** on 64 KiB POST in this harness (Windows nginx did complete). Prefer TWP H1 POST as a working reverse path; do not read the nginx zero as a fair peak contest until the nginx POST arm is healthy on Ubuntu.

### Windows — lossy / high-RTT (H2 HOL)

Userspace **5 ms** one-way delay + **1%** connection stall; **64 KiB** GET. Source: local `compare-lossy` (`rps-ramp-20260816-161416`).

| Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | Winner |
|---|---|---:|---:|---:|---:|---|
| HTTP/1 · TLS | HTTP/1 · plain | **576** | **576** | **633** | **633** | **nginx** |
| HTTP/2 · TLS | HTTP/1 · plain | **15** | **15** | **11** | **13** | **TWP** |

H1 scales with concurrency; H2 collapses under connection stalls (HOL). Absolute RPS is low because the shim delays every buffer — the point is the **protocol shape**, not competing with the tiny-GET table.

### Linux — lossy / high-RTT (H2 HOL)

Median of **3** repeats. Source: Actions [31958196755](https://github.com/justcoding121/titanium-web-proxy/actions/runs/31958196755) (`compare-lossy`).

| Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | Winner |
|---|---|---:|---:|---:|---:|---|
| HTTP/1 · TLS | HTTP/1 · plain | **1,122** | **1,122** | **1,210** | **1,210** | **nginx** |
| HTTP/2 · TLS | HTTP/1 · plain | **44** | **46** | **44** | **45** | **(tie)** |

Same story as Windows: H1 stays usable; H2 falls to tens of RPS for both products. TWP and nginx are **comparable** here; tiny-GET H1 leadership does not carry over.

### TLS termination cost (H1 TLS → cleartext origin)

Isolates keep-alive tiny GET vs **new connection per request** (handshake-dominated) vs keep-alive **256 KiB**. Product comparison uses RPS and end-to-end latency; TWP can also capture `ClientTlsTiming` when `TWP_RPS_CAPTURE_TLS=1` (child process) — nginx has no equivalent hook.

#### Windows

Source: local `compare-tls-cost` (`rps-ramp-20260816-161528`).

| Workload | TWP sustain | TWP peak | nginx sustain | nginx peak | Winner | TWP÷nginx |
|---|---:|---:|---:|---:|---|---:|
| Keep-alive · tiny GET | **32,224** | **32,224** | **14,228** | **14,994** | **TWP** | **2.26** |
| New-connection · tiny GET | **899** | **899** | **663** | **704** | **TWP** | **1.36** |
| Keep-alive · 256 KiB GET | **2,842** | **2,956** | **202** | **252** | **TWP** | **14.1** |

#### Linux

Median of **3** repeats. Source: Actions [31958198072](https://github.com/justcoding121/titanium-web-proxy/actions/runs/31958198072) (`compare-tls-cost`).

| Workload | TWP sustain | TWP peak | nginx sustain | nginx peak | Winner | TWP÷nginx |
|---|---:|---:|---:|---:|---|---:|
| Keep-alive · tiny GET | **23,040** | **23,040** | **35,619** | **35,619** | **nginx** | **0.65** |
| New-connection · tiny GET | **1,236** | **1,239** | **1,039** | **1,039** | **TWP** | **1.19** |
| Keep-alive · 256 KiB GET | **2,232** | **2,232** | **2,798** | **2,798** | **nginx** | **0.80** |

**Verdict (Linux, authoritative nginx):** On keep-alive tiny terminate, TWP is still ~**0.65×** nginx (same story as the main table). On **new-connection** terminate, TWP is **ahead** (~1.19×) — handshake-dominated work is in the same league and can favor TWP. With **256 KiB** bodies, the ratio improves to ~**0.80×** vs tiny-GET’s ~0.65–0.71×, but nginx still leads sustain when both are healthy.

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
