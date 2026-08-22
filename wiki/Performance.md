# Performance

Titanium targets **low-overhead MITM proxying**: connection pooling, HTTP/2 multiplexing, and buffer reuse. Numbers below are **Release** measurements with [RpsLoadProbe](https://github.com/justcoding121/titanium-web-proxy/tree/develop/tools/RpsLoadProbe) (and BenchmarkDotNet / Basic example where noted). Publishable tables cite **GitHub Actions** medians on matched **4 vCPU / 16 GiB** runners. Absolute RPS still varies by OS kernel, TLS, and MsQuic packaging — compare **within a table**, not across Windows vs Linux.

Control arms: **nginx** (native C reverse-proxy ceiling; Linux is authoritative) and **YARP** (`Yarp.ReverseProxy`, managed .NET reverse proxy). Neither can MITM (no CONNECT / forged certs). FiddlerCore is not compared (commercial debugger license; not a throughput peer).

For pooling knobs and certificate first-visit tuning, see [Performance and pooling](Home#performance-and-pooling). For the local cool A/B lab, laptop tables, and the techniques used to find each hotspot, see [Performance Profiling](Performance-Profiling).

## Contents

- [Measurement environment](#measurement-environment)
    - [Windows (GitHub-hosted `windows-latest`)](#windows-github-hosted-windows-latest)
    - [Linux (GitHub-hosted `ubuntu-latest`)](#linux-github-hosted-ubuntu-latest)
- [Windows — Titanium vs nginx vs YARP](#windows--titanium-vs-nginx-vs-yarp)
- [Linux — Titanium vs nginx vs YARP](#linux--titanium-vs-nginx-vs-yarp)
    - [Tiny JSON reverse is nginx’s best case on Linux](#tiny-json-reverse-is-nginxs-best-case-on-linux)
    - [Why isn’t HTTP/3 > HTTP/2 > HTTP/1 in raw RPS?](#why-isnt-http3--http2--http1-in-raw-rps)
- [Heavier reverse workloads](#heavier-reverse-workloads)
    - [Windows — heavier reverse GET (64 KiB / 256 KiB)](#windows--heavier-reverse-get-64-kib--256-kib)
    - [Linux — heavier reverse GET (64 KiB / 256 KiB)](#linux--heavier-reverse-get-64-kib--256-kib)
    - [Windows — POST 64 KiB request + 64 KiB response](#windows--post-64-kib-request--64-kib-response)
    - [Linux — POST 64 KiB request + 64 KiB response](#linux--post-64-kib-request--64-kib-response)
    - [Windows — lossy / high-RTT (H2 HOL / H3 loss)](#windows--lossy--high-rtt-h2-hol--h3-loss)
    - [Linux — lossy / high-RTT (H2 HOL / H3 loss)](#linux--lossy--high-rtt-h2-hol--h3-loss)
    - [Architecture-sensitive](#architecture-sensitive)
    - [TLS termination cost (H1 TLS → cleartext origin)](#tls-termination-cost-h1-tls--cleartext-origin)
- [Other measurements](#other-measurements)
- [Raising limits on large hosts](#raising-limits-on-large-hosts)

## Measurement environment

Both OS use the standard public-repo GitHub-hosted runner class (**4 vCPU / 16 GiB / 14 GB SSD**). Same harness knobs (`workflow_dispatch` [RPS saturation](https://github.com/justcoding121/titanium-web-proxy/actions/workflows/rps-saturation.yml): warmup 2s / measure 8s; concurrency 8, 16, 32, 64; median of 3 repeats). Prefer **TWP÷YARP** / **TWP÷nginx** ratios over absolute RPS.

Laptop High-perf / cool-paired Windows numbers live on [Performance Profiling — Local Windows lab](Performance-Profiling#local-windows-lab-developer-laptop). Do not mix those absolutes into the tables below.

### Windows (GitHub-hosted `windows-latest`)

| | |
|---|---|
| OS | Windows Server (GitHub-hosted `windows-latest`) |
| CPU | **4** logical processors |
| RAM | **16** GiB |
| Runtime | .NET 10.0.x |
| nginx | nginx/Windows **1.31.3** (same-OS only; no QUIC) |
| YARP | Yarp.ReverseProxy **2.3.0** |
| Harness | RpsLoadProbe Release; median of 3 repeats |

### Linux (GitHub-hosted `ubuntu-latest`)

| | |
|---|---|
| OS | Ubuntu 24.04.x LTS |
| CPU | **4** logical processors (AMD EPYC; runners this pass were 7763 / 9V74) |
| RAM | **16** GiB |
| Runtime | .NET 10.0.11 |
| nginx | nginx/**1.31.4** (nginx.org mainline, `--with-http_v3_module`) |
| YARP | Yarp.ReverseProxy **2.3.0** |
| Harness | RpsLoadProbe Release; median of 3 repeats where noted |

**How to read the tables**

- **Mode**: **Reverse** = transparent fixed-forward (may TLS-terminate to a cleartext origin, or re-encrypt to a configured HTTPS/QUIC origin). **MITM** = both legs are visible in the clear inside TWP — either by decrypting client TLS/QUIC (forged cert / CONNECT) **or** by accepting an already-cleartext client (explicit HTTP proxy / inspectable transparent reverse) while still speaking plain or TLS to the origin. nginx and YARP cannot do MITM. **HTTP/3 has no cleartext client** (QUIC always encrypted).
- **Sustainable** = last concurrency that still met error/latency SLOs. **Peak** = highest RPS in that ramp.
- 🟢 = highest **sustainable** RPS among products that have a number on that row. Omitted when only TWP can run the path (no fair multi-product comparison).
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
pwsh tools/RpsLoadProbe/run-rps.ps1 -Mode compare-arch
```

## Windows — Titanium vs nginx vs YARP

Client / origin: HTTP version and whether TLS is used (`plain` = cleartext, `TLS` = encrypted, `QUIC` = HTTP/3).

Median of **3 repeats** on `windows-latest` (4 vCPU / 16 GiB) @ `02dcbdbf`. Sources: [32570355206](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32570355206) (`compare-same`), [32570356532](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32570356532) (`compare-bridges`), [32570357689](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32570357689) (`compare-mitm`). Warmup 2s / measure 8s; concurrency 8, 16, 32, 64. Laptop High-perf / cool-paired numbers stay on the [local lab](Performance-Profiling#local-windows-lab-developer-laptop).

**Load generators:** Reverse inbound H3 arms use **`dotnet-httpclient`** (`http_version=3.0`, `RequestVersionExact`) after dual-listen reverse H3. nginx/Windows is same-OS only (no QUIC).

| Mode | Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---|---|---:|---:|---:|---:|---:|---:|
| Reverse | HTTP/1 · plain | HTTP/1 · plain | 🟢 **32,170** | **32,170** | **24,617** | **25,428** | **31,360** | **31,360** |
| Reverse | HTTP/1 · plain | HTTP/1 · TLS | 🟢 **30,070** | **30,070** | *Not possible* | *Not possible* | **29,242** | **29,242** |
| Reverse | HTTP/1 · TLS | HTTP/1 · plain | 🟢 **28,032** | **28,032** | **16,654** | **17,439** | **27,463** | **27,463** |
| Reverse | HTTP/1 · TLS | HTTP/2 · TLS | 🟢 **24,968** | **24,968** | *Not possible* | *Not possible* | **24,171** | **24,171** |
| Reverse | HTTP/1 · TLS | HTTP/3 · QUIC | **13,586** | **13,586** | *Not possible* (no QUIC) | *Not possible* (no QUIC) | 🟢 **14,795** | **14,795** |
| Reverse | HTTP/2 · plain | HTTP/1 · plain | **33,008** | **33,008** | *Not possible* | *Not possible* | 🟢 **34,758** | **34,758** |
| Reverse | HTTP/2 · plain | HTTP/2 · plain | 🟢 **94,991** | **94,991** | *Not possible* | *Not possible* | **72,308** | **72,308** |
| Reverse | HTTP/2 · plain | HTTP/2 · TLS | 🟢 **84,741** | **84,741** | *Not possible* | *Not possible* | **66,051** | **66,051** |
| Reverse | HTTP/2 · plain | HTTP/3 · QUIC | 🟢 **32,670** | **32,670** | *Not possible* (no QUIC) | *Not possible* (no QUIC) | **30,650** | **30,650** |
| Reverse | HTTP/2 · TLS | HTTP/1 · plain | 🟢 **32,280** | **32,280** | **20,312** | **20,312** | **31,298** | **31,298** |
| Reverse | HTTP/2 · TLS | HTTP/2 · plain | 🟢 **85,816** | **85,816** | *Not possible* | *Not possible* | **56,741** | **56,741** |
| Reverse | HTTP/2 · TLS | HTTP/3 · QUIC | 🟢 **32,009** | **32,009** | *Not possible* (no QUIC) | *Not possible* (no QUIC) | **27,823** | **27,823** |
| Reverse | HTTP/3 · QUIC | HTTP/1 · plain | **13,881** | **13,881** | *Not possible* (no QUIC) | *Not possible* (no QUIC) | 🟢 **15,313** | **15,702** |
| Reverse | HTTP/3 · QUIC | HTTP/2 · TLS | 🟢 **24,047** | **24,047** | *Not possible* (no QUIC) | *Not possible* (no QUIC) | **20,996** | **20,996** |
| Reverse | HTTP/3 · QUIC | HTTP/3 · QUIC | 🟢 **23,543** | **23,543** | *Not possible* (no QUIC) | *Not possible* (no QUIC) | **17,316** | **17,316** |
| MITM | HTTP/1 · plain | HTTP/1 · plain | **24,783** | **24,783** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/1 · plain | HTTP/1 · TLS | **19,549** | **19,549** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/1 · TLS | HTTP/1 · TLS | **21,780** | **21,780** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 · plain | HTTP/2 · plain | **92,049** | **92,049** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 · plain | HTTP/2 · TLS | **78,316** | **78,316** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 · TLS | HTTP/1 · TLS | **28,145** | **28,145** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 · TLS | HTTP/2 · TLS | **79,925** | **79,925** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/3 · QUIC | HTTP/1 · TLS | **14,748** | **14,748** | *Not possible* (no QUIC) | *Not possible* (no QUIC) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/3 · QUIC | HTTP/2 · TLS | **26,334** | **26,334** | *Not possible* (no QUIC) | *Not possible* (no QUIC) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/3 · QUIC | HTTP/3 · QUIC | **22,536** | **22,536** | *Not possible* (no QUIC) | *Not possible* (no QUIC) | *Not possible* (no MITM) | *Not possible* (no MITM) |

TWP÷YARP H1 plain ≈ **1.03×** (32,170 / 31,360); H1 TLS terminate ≈ **1.02×** (28,032 / 27,463). H3→H1 ≈ **0.91×**, H3→H2 ≈ **1.15×**, H3→H3 ≈ **1.36×**. Prefer ratios over absolute RPS on GHA VMs.

## Linux — Titanium vs nginx vs YARP

Median of **3 repeats** on `ubuntu-latest` (4 vCPU / 16 GiB) @ `02dcbdbf` (same matrix as Windows): [32570355206](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32570355206) (`compare-same`), [32570356532](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32570356532) (`compare-bridges`), [32570357689](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32570357689) (`compare-mitm`). Warmup 2s / measure 8s; concurrency 8, 16, 32, 64. **Linux nginx is the authoritative nginx baseline.** The RPS workflow installs nginx.org mainline (`http_v3_module`) and `libmsquic` (`QuicListener.IsSupported=true` on `ubuntu-latest`).

TWP÷nginx H1 plain reverse ≈ **0.69** (30,709 / 44,687); TWP÷YARP H1 plain ≈ **0.96** (30,709 / 32,033). Prefer ratios over absolute RPS on GHA VMs.

| Mode | Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---|---|---:|---:|---:|---:|---:|---:|
| Reverse | HTTP/1 · plain | HTTP/1 · plain | **30,709** | **30,709** | 🟢 **44,687** | **44,687** | **32,033** | **32,033** |
| Reverse | HTTP/1 · plain | HTTP/1 · TLS | **28,140** | **28,140** | *Not possible* | *Not possible* | 🟢 **28,293** | **28,293** |
| Reverse | HTTP/1 · TLS | HTTP/1 · plain | **23,822** | **23,822** | 🟢 **31,910** | **31,910** | **25,352** | **25,352** |
| Reverse | HTTP/1 · TLS | HTTP/2 · TLS | 🟢 **25,603** | **25,603** | *Not possible* | *Not possible* | **23,757** | **23,757** |
| Reverse | HTTP/1 · TLS | HTTP/3 · QUIC | **17,633** | **17,633** | *Not possible* (no H3 origin) | *Not possible* (no H3 origin) | 🟢 **17,891** | **17,891** |
| Reverse | HTTP/2 · plain | HTTP/1 · plain | 🟢 **38,982** | **38,982** | *Not possible* | *Not possible* | **35,889** | **35,889** |
| Reverse | HTTP/2 · plain | HTTP/2 · plain | 🟢 **74,287** | **74,287** | *Not possible* | *Not possible* | **55,026** | **55,026** |
| Reverse | HTTP/2 · plain | HTTP/2 · TLS | 🟢 **60,696** | **60,696** | *Not possible* | *Not possible* | **46,989** | **46,989** |
| Reverse | HTTP/2 · plain | HTTP/3 · QUIC | 🟢 **29,797** | **29,797** | *Not possible* (no H3 origin) | *Not possible* (no H3 origin) | **28,061** | **28,061** |
| Reverse | HTTP/2 · TLS | HTTP/1 · plain | 🟢 **35,960** | **35,960** | **16,364** | **22,840** | **30,206** | **30,206** |
| Reverse | HTTP/2 · TLS | HTTP/2 · plain | 🟢 **62,192** | **62,192** | *Not possible* | *Not possible* | **40,803** | **40,803** |
| Reverse | HTTP/2 · TLS | HTTP/3 · QUIC | 🟢 **28,636** | **28,636** | *Not possible* (no H3 origin) | *Not possible* (no H3 origin) | **24,268** | **24,268** |
| Reverse | HTTP/3 · QUIC | HTTP/1 · plain | 🟢 **19,551** | **19,551** | **0** | **14,848** | **19,332** | **19,332** |
| Reverse | HTTP/3 · QUIC | HTTP/2 · TLS | 🟢 **27,350** | **27,350** | *Not possible* (no H3→H2) | *Not possible* (no H3→H2) | **22,019** | **22,019** |
| Reverse | HTTP/3 · QUIC | HTTP/3 · QUIC | 🟢 **21,703** | **21,703** | *Not possible* (no H3 origin) | *Not possible* (no H3 origin) | **18,574** | **18,574** |
| MITM | HTTP/1 · plain | HTTP/1 · plain | **49,778** | **49,778** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/1 · plain | HTTP/1 · TLS | **34,872** | **34,872** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/1 · TLS | HTTP/1 · TLS | **37,036** | **37,036** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 · plain | HTTP/2 · plain | **103,611** | **103,611** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 · plain | HTTP/2 · TLS | **82,244** | **82,244** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 · TLS | HTTP/1 · TLS | **44,272** | **44,272** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 · TLS | HTTP/2 · TLS | **77,141** | **77,141** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/3 · QUIC | HTTP/1 · TLS | **26,820** | **26,820** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/3 · QUIC | HTTP/2 · TLS | **31,954** | **31,954** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/3 · QUIC | HTTP/3 · QUIC | **25,659** | **25,659** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |

On this GHA shape, TWP H1 plain ÷ nginx H1 plain ≈ **0.69** (30,709 / 44,687). H1 TLS terminate ≈ **0.75** (23,822 / 31,910). TWP÷YARP H1 plain ≈ **0.96**; H1 TLS terminate ≈ **0.94**. Absolute RPS swings by VM; prefer the **ratio** and **median across repeats**.

**nginx HTTP/3:** inbound QUIC terminate → cleartext H1 (`nginx-reverse-http3-cleartext`) is in `compare-bridges` as of [32577474009](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32577474009) @ `629fb878`. Sustain **0** (did not hold the SLO); peak **14,848**. TWP/YARP H3→H1 cells on this row stay from the `02dcbdbf` matrix above. nginx still cannot speak HTTP/3 to an origin (no H3 upstream in this conf).

**YARP HTTP/3 (this matrix):** TWP leads H3→H1 ≈ **1.01×** (19,551 / 19,332), H3→H2 ≈ **1.24×** (27,350 / 22,019), H3→H3 ≈ **1.17×** (21,703 / 18,574). H1→H2 ≈ **1.08×** (25,603 / 23,757). Near-ties: H1→H3 ≈ **0.99×**, h2c→H3 ≈ **1.06×**.

**Windows vs Linux:** both CI envs are **4 vCPU / 16 GiB**, but do **not** compare absolute RPS across OS. Linux still has nginx leading H1 plain/TLS terminate; vs YARP, H3 bridges and same-protocol H2 are TWP-led, with H1 plain still ~**0.96×** YARP. Windows reverse tiny-GET is at parity or better vs YARP on most arms (see Windows section). Cool laptop notes remain on [Performance Profiling](Performance-Profiling#local-windows-lab-developer-laptop).


### Tiny JSON reverse is nginx’s best case on Linux

The tables above use **~64 B keep-alive GET** on loopback. On Linux H1 reverse, nginx leads; YARP sits near TWP. Heavier bodies, POSTs, TLS handshake cost, and lossy/HOL workloads (below) change the picture. MITM rows remain TWP-only. nginx HTTP/3 is inbound-terminate only (see note above).

### Why isn’t HTTP/3 > HTTP/2 > HTTP/1 in raw RPS?

For **tiny JSON responses** (~64 B) on loopback, that ordering is **not** expected: topology (TLS hop count, terminate vs MITM) dominates; HTTP/2 and HTTP/3 help multiplexing, not single-origin tiny-GET RPS. See the **lossy** tables below for a workload where protocol design matters.

## Heavier reverse workloads

Separate from the tiny-GET matrix. Same measurement environments. Modes: `compare-bodies`, `compare-post`, `compare-lossy`, `compare-tls-cost`, `compare-arch` in [RpsLoadProbe](https://github.com/justcoding121/titanium-web-proxy/tree/develop/tools/RpsLoadProbe). **PUT with the same body is the same proxy work as POST; DELETE with no body matches GET** — only POST is published. Bodies/POST/lossy stay **half-duplex**. `compare-arch` is the slow-consumer / early-response / duplex set. Laptop numbers are on [Performance-Profiling](Performance-Profiling#architecture-sensitive); CI medians go in the tables below.

Lossy link = **userspace** shim (not kernel `netem`): TCP gets per-buffer delay + occasional whole-connection stalls (honest HOL for multiplexed H2); UDP gets per-datagram delay + drops (QUIC). `compare-lossy` publishes H1/H2/H3; H3 is where the protocol design is supposed to matter.

### Windows — heavier reverse GET (64 KiB / 256 KiB)

Median of **3** repeats on `windows-latest`. Source: Actions [32570358864](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32570358864) (`compare-bodies`). Warmup 2s / measure 8s.

| Body | Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---|---|---:|---:|---:|---:|---:|---:|
| 64 KiB | HTTP/1 · TLS | HTTP/1 · plain | 🟢 **8,835** | **9,089** | **867** | **917** | **8,452** | **8,900** |
| 64 KiB | HTTP/2 · TLS | HTTP/1 · plain | 🟢 **8,921** | **8,921** | **754** | **784** | **6,977** | **6,977** |
| 64 KiB | HTTP/3 · QUIC | HTTP/1 · plain | **2,232** | **2,291** | *Not possible* (no QUIC) | *Not possible* (no QUIC) | 🟢 **3,974** | **3,974** |
| 256 KiB | HTTP/1 · TLS | HTTP/1 · plain | **2,457** | **2,489** | **224** | **239** | 🟢 **2,710** | **2,829** |
| 256 KiB | HTTP/2 · TLS | HTTP/1 · plain | 🟢 **2,699** | **2,717** | **175** | **176** | **1,934** | **2,031** |
| 256 KiB | HTTP/3 · QUIC | HTTP/1 · plain | 🟢 **2,147** | **2,273** | *Not possible* (no QUIC) | *Not possible* (no QUIC) | **1,104** | **1,141** |

nginx/Windows collapses on large reverse bodies in this harness; treat as same-OS only.

### Linux — heavier reverse GET (64 KiB / 256 KiB)

Median of **3** repeats. Source: Actions [32570358864](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32570358864) (`compare-bodies` on `02dcbdbf`). Warmup 2s / measure 8s.

| Body | Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---|---|---:|---:|---:|---:|---:|---:|
| 64 KiB | HTTP/1 · TLS | HTTP/1 · plain | **11,710** | **11,710** | 🟢 **13,536** | **14,727** | **10,893** | **10,893** |
| 64 KiB | HTTP/2 · TLS | HTTP/1 · plain | 🟢 **10,027** | **10,027** | **7,519** | **7,582** | **7,892** | **7,892** |
| 64 KiB | HTTP/3 · QUIC | HTTP/1 · plain | **4,583** | **5,654** | **2,415** | **2,670** | 🟢 **5,588** | **5,588** |
| 256 KiB | HTTP/1 · TLS | HTTP/1 · plain | **3,655** | **3,655** | 🟢 **4,311** | **4,311** | **3,315** | **3,315** |
| 256 KiB | HTTP/2 · TLS | HTTP/1 · plain | 🟢 **2,424** | **2,424** | **1,942** | **1,942** | **2,110** | **2,115** |
| 256 KiB | HTTP/3 · QUIC | HTTP/1 · plain | 🟢 **4,483** | **4,857** | **614** | **723** | **1,670** | **1,680** |

On this GHA pass TWP÷YARP H1 TLS ≈ **1.07×** (64 KiB) / **1.10×** (256 KiB); H2→H1 ≈ **1.27×** / **1.15×**; H3→H1 ≈ **0.82×** / **2.68×** (YARP soft on 256 KiB H3 — treat absolute cautiously). TWP÷nginx H1 TLS ≈ **0.87** / **0.85**. Absolute RPS swings by VM; prefer ratios.

### Windows — POST 64 KiB request + 64 KiB response

Median of **3** repeats on `windows-latest`. Source: Actions [32570360081](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32570360081) (`compare-post`).

| Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---|---:|---:|---:|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | 🟢 **5,268** | **5,286** | **334** | **369** | **4,151** | **4,337** |
| HTTP/2 · TLS | HTTP/1 · plain | 🟢 **4,037** | **4,037** | **310** | **343** | **3,493** | **3,507** |
| HTTP/3 · QUIC | HTTP/1 · plain | **0** | **972** | *Not possible* | *Not possible* | 🟢 **2,020** | **2,020** |

TWP wins H1 POST (~**1.27×** YARP) and H2 POST (~**1.16×** YARP). H3 POST did not hold the SLO (sustain **0**).

### Linux — POST 64 KiB request + 64 KiB response

Median of **3** repeats. Source: Actions [32570360081](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32570360081) (`compare-post`).

| Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---|---:|---:|---:|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | 🟢 **4,161** | **4,161** | **4,082** | **4,082** | **3,218** | **3,218** |
| HTTP/2 · TLS | HTTP/1 · plain | 🟢 **3,255** | **3,255** | **1,952** | **2,000** | **2,531** | **2,541** |
| HTTP/3 · QUIC | HTTP/1 · plain | **0** | **1,989** | **764** | **765** | 🟢 **2,650** | **2,650** |

Linux nginx H1/H2/H3 POST completed this pass (nginx.org mainline). TWP H3 POST peaked at **1,989** but did not hold the error/latency SLO (sustain **0**).

### Windows — lossy / high-RTT (H2 HOL / H3 loss)

Userspace **5 ms** one-way delay + **1%** TCP connection stall (H1/H2) or UDP datagram drop (H3); **64 KiB** GET. Median of **3** repeats on `windows-latest`. Source: Actions pending (`compare-lossy` remasure with H3).

| Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---|---:|---:|---:|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | **567** | **567** | **638** | **638** | 🟢 **663** | **663** |
| HTTP/2 · TLS | HTTP/1 · plain | **16** | **18** | **16** | **18** | 🟢 **18** | **18** |
| HTTP/3 · QUIC | HTTP/1 · plain | *Not measured* | *Not measured* | *Not possible* (no QUIC) | *Not possible* | *Not measured* | *Not measured* |

H1 stays usable; H2 collapses under connection stalls (HOL). H3 cells await the remasure that routes HttpClient through the UDP shim. Laptop preview: TWP H3 ≈ **1,308** sustain vs H2 ≈ **15** (`windows-20260822-lossy-h3/`). Absolute RPS is low because the shim delays every buffer/datagram — the point is the **protocol shape**.

### Linux — lossy / high-RTT (H2 HOL / H3 loss)

Median of **3** repeats. Source: Actions pending (`compare-lossy` remasure with H3).

| Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---|---:|---:|---:|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | **1,105** | **1,105** | 🟢 **1,207** | **1,207** | **1,192** | **1,192** |
| HTTP/2 · TLS | HTTP/1 · plain | 🟢 **40** | **45** | 🟢 **40** | **44** | 🟢 **40** | **44** |
| HTTP/3 · QUIC | HTTP/1 · plain | *Not measured* | *Not measured* | *Not measured* | *Not measured* | *Not measured* | *Not measured* |

Same H1/H2 story as Windows. H3 (and Linux nginx H3 terminate under loss) filled after CI.

### Architecture-sensitive

`compare-arch` isolates slow app readers, origin-early response, H2 duplex, and WebSocket echo. See [TWP vs YARP IO model](Performance-Profiling#twp-vs-yarp-io-model). Laptop 1-rep numbers are on [Performance-Profiling](Performance-Profiling#architecture-sensitive).

Median of **3** repeats on matched 4 vCPU / 16 GiB runners @ `629fb878`. Source: [32577472805](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32577472805) (`compare-arch`). Slow consumer = 256 KiB GET, 16 KiB read + 8 ms sleep. Early response = 64 KiB POST, origin writes after 8 KiB. Duplex H2 = overlapping 64 KiB POST on H2 TLS↔H2 TLS. WebSocket = echo round-trips/sec.

`compare-lossy` (slow **network**) is already published above; it is not a slow **app** reader.

#### Windows

| Scenario | Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---|---|---:|---:|---:|---:|---:|---:|
| Slow consumer (256 KiB GET, throttled client read) | HTTP/1 · TLS | HTTP/1 · plain | 🟢 **248** | **248** | **230** | **230** | 🟢 **248** | **248** |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · TLS | HTTP/1 · plain | **249** | **249** | **213** | **213** | 🟢 **256** | **256** |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/3 · QUIC | HTTP/1 · plain | **0** | **0** | *Not possible* (no QUIC) | *Not possible* | 🟢 **236** | **236** |
| Early response (origin writes after first request chunk) | HTTP/1 · TLS | HTTP/1 · plain | 🟢 **7,099** | **7,531** | **374** | **443** | **5,222** | **5,812** |
| Early response (origin writes after first request chunk) | HTTP/2 · TLS | HTTP/1 · plain | 🟢 **6,501** | **6,501** | **0** | **427** | **3,775** | **4,463** |
| Early response (origin writes after first request chunk) | HTTP/3 · QUIC | HTTP/1 · plain | **1,372** | **1,372** | *Not possible* (no QUIC) | *Not possible* | 🟢 **1,808** | **1,808** |
| Duplex (both directions live) | HTTP/2 · TLS | HTTP/2 · TLS | 🟢 **1,299** | **1,299** | *Not possible* | *Not possible* | **28** | **3,069** |
| Duplex (WebSocket / extended CONNECT) | HTTP/1 · TLS | HTTP/1 · plain | 🟢 **41,615** | **41,615** | **24,184** | **25,549** | **40,191** | **40,191** |

#### Linux

| Scenario | Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---|---|---:|---:|---:|---:|---:|---:|
| Slow consumer (256 KiB GET, throttled client read) | HTTP/1 · TLS | HTTP/1 · plain | **453** | **453** | 🟢 **472** | **472** | **407** | **407** |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · TLS | HTTP/1 · plain | 🟢 **477** | **477** | **476** | **476** | **474** | **474** |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/3 · QUIC | HTTP/1 · plain | **0** | **0** | **286** | **286** | 🟢 **432** | **432** |
| Early response (origin writes after first request chunk) | HTTP/1 · TLS | HTTP/1 · plain | 🟢 **4,089** | **4,089** | **3,942** | **4,039** | **3,145** | **3,145** |
| Early response (origin writes after first request chunk) | HTTP/2 · TLS | HTTP/1 · plain | 🟢 **3,215** | **3,215** | **0** | **1,996** | **2,231** | **2,338** |
| Early response (origin writes after first request chunk) | HTTP/3 · QUIC | HTTP/1 · plain | 🟢 **2,157** | **2,157** | **0** | **728** | **2,116** | **2,116** |
| Duplex (both directions live) | HTTP/2 · TLS | HTTP/2 · TLS | 🟢 **40** | **602** | *Not possible* | *Not possible* | **24** | **1,745** |
| Duplex (WebSocket / extended CONNECT) | HTTP/1 · TLS | HTTP/1 · plain | **28,700** | **28,700** | 🟢 **32,650** | **32,650** | **27,717** | **27,717** |

Slow consumer is sleep-bound; H1/H2 sit in the same band. TWP H3 slow-consumer sustain **0** (HttpClient sees `Content-Length` but no body bytes). Early-response H1/H2: TWP leads on both OS. Duplex H2: TWP holds a higher sustain than YARP on this pass (YARP peaks higher). WebSocket: TWP leads on Windows; Linux nginx leads. nginx HTTP/2 origin is *Not possible* on the duplex H2↔H2 row.

### TLS termination cost (H1 TLS → cleartext origin)

Isolates keep-alive tiny GET vs **new connection per request** (handshake-dominated) vs keep-alive **256 KiB**. Product comparison uses RPS and end-to-end latency; TWP can also capture `ClientTlsTiming` when `TWP_RPS_CAPTURE_TLS=1` (child process) — nginx/YARP have no equivalent hook.

#### Windows

Median of **3** repeats on `windows-latest`. Source: Actions [32570362731](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32570362731) (`compare-tls-cost`).

| Workload | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---:|---:|---:|---:|---:|---:|
| Keep-alive · tiny GET | 🟢 **26,617** | **26,617** | **15,888** | **16,727** | **26,206** | **26,206** |
| New-connection · tiny GET | **655** | **658** | **274** | **281** | 🟢 **778** | **778** |
| Keep-alive · 256 KiB GET | **2,364** | **2,516** | **245** | **269** | 🟢 **2,410** | **2,562** |

#### Linux

Median of **3** repeats. Source: Actions [32570362731](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32570362731) (`compare-tls-cost`).

| Workload | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---:|---:|---:|---:|---:|---:|
| Keep-alive · tiny GET | **21,000** | **21,000** | 🟢 **28,816** | **28,816** | **21,345** | **21,345** |
| New-connection · tiny GET | 🟢 **1,170** | **1,182** | **1,030** | **1,030** | **988** | **988** |
| Keep-alive · 256 KiB GET | **2,188** | **2,188** | 🟢 **2,732** | **2,732** | **2,200** | **2,200** |

**Verdict (Linux, authoritative nginx):** On keep-alive tiny terminate, TWP is ~**0.73×** nginx and ~**0.98×** YARP. On **new-connection** terminate, TWP is **ahead** of both (~1.14× nginx, ~1.18× YARP). With **256 KiB** bodies, nginx still leads sustain when all three are healthy.

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
