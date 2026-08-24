# Performance

Titanium targets **low-overhead MITM proxying**: connection pooling, HTTP/2 multiplexing, and buffer reuse. Numbers below are **Release** measurements with [RpsLoadProbe](https://github.com/justcoding121/titanium-web-proxy/tree/develop/tools/RpsLoadProbe) (and BenchmarkDotNet / Basic example where noted). Publishable tables cite **GitHub Actions** medians on matched **4 vCPU / 16 GiB** runners. Absolute RPS still varies by OS kernel, TLS, and MsQuic packaging — compare **within a table**, not across Windows vs Linux.

Control arms: **nginx** (native C reverse-proxy ceiling; Linux is authoritative) and **YARP** (`Yarp.ReverseProxy`, managed .NET reverse proxy). Neither can MITM (no CONNECT / forged certs). FiddlerCore is not compared (commercial debugger license; not a throughput peer).

For pooling knobs and certificate first-visit tuning, see [Performance and pooling](Home#performance-and-pooling). For the local cool A/B lab, laptop tables, and profiling notes, see [Performance Profiling](Performance-Profiling).

## Contents

- [Measurement environment](#measurement-environment)
    - [Windows (GitHub-hosted `windows-latest`)](#windows-github-hosted-windows-latest)
    - [Linux (GitHub-hosted `ubuntu-latest`)](#linux-github-hosted-ubuntu-latest)
    - [Saturation control](#saturation-control)
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

Both OS use the standard public-repo GitHub-hosted runner class (**4 vCPU / 16 GiB / 14 GB SSD**). Same harness knobs (`workflow_dispatch` [RPS saturation](https://github.com/justcoding121/titanium-web-proxy/actions/workflows/rps-saturation.yml): warmup 2s / measure 8s; concurrency 8, 16, 32, 64; median of 3 repeats). Every `--ramp` arm is **three OS processes** (parent load generator + origin child + proxy child), except **origin-direct** arms (load gen + origin only). Prefer **TWP÷YARP** / **TWP÷nginx** ratios over absolute RPS.

Laptop High-perf / cool-paired Windows numbers live on [Performance Local Lab](Performance-Local-Lab). Do not mix those absolutes into the tables below.

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

### Saturation control

Calibration for the shared 4 vCPU loopback shape: how close client + origin are to saturated before ranking reverse peers. Tiny keep-alive GET. Median of **3** repeats @ `70b5ca33` — [32756394056](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32756394056). Warmup 2s / measure 8s; concurrency 8, 16, 32, 64. Block A **% of origin-HttpClient** uses median **peak** RPS. Blocks B/C use peer÷YARP / ÷nginx on median peak (not % of H1 origin). **RPS cells** embed median RSS / CPU for the **proxy child** plus its **full descendant tree** (serve-proxy → nginx master → workers); origin-direct samples the **origin** child. Product matrices below use matched `dotnet-httpclient` only (not bombardier). **H3→H1** (saturation Block C): Win TWP RSS **102** MiB vs YARP **157** (~**0.65×**); Linux **139** vs **181** (~**0.77×**). RPS vs YARP (**1.02×** / **1.1×**).



```powershell
pwsh tools/RpsLoadProbe/run-rps.ps1 -Mode compare-saturation
```

#### Block A — H1 plain

| Arm | Generator | Notes |
|---|---|---|
| `origin-direct` | `dotnet-httpclient` | No proxy — origin ceiling under the same client used for publishable ratios |
| `origin-direct-bombardier` | `bombardier` | External client check (CI installs bombardier) |
| `bare-reverse-http1` | `dotnet-httpclient` | Thin C# H1 reverse (`BareHttp1ReverseProxy`) — .NET runtime / loopback ceiling for a three-process reverse hop; **not** a product peer |
| `nginx-reverse-http1` / `yarp-reverse-http1` / `twp-reverse-http1` | `dotnet-httpclient` | Product peers (medals among these three only) |

**Windows** (`windows-latest`)

| Arm | Generator | Sustain | Peak | % of origin-HttpClient |
|---|---|---:|---:|---:|
| origin-direct | dotnet-httpclient | **51,427**<br><sub>(53 MiB / 40.8% CPU)</sub> | **51,427**<br><sub>(53 MiB / 40.8% CPU)</sub> | **100.0%** |
| origin-direct-bombardier | bombardier | **39,120**<br><sub>(54 MiB / 23.6% CPU)</sub> | **39,120**<br><sub>(54 MiB / 23.6% CPU)</sub> | **76.1%** |
| bare-reverse-http1 | dotnet-httpclient | **25,740**<br><sub>(53 MiB / 47.4% CPU)</sub> | **25,740**<br><sub>(53 MiB / 47.4% CPU)</sub> | **50.1%** |
| nginx-reverse-http1 | dotnet-httpclient | **13,544**<br><sub>(120 MiB / 24.8% CPU)</sub> | **13,691**<br><sub>(120 MiB / 24.8% CPU)</sub> | **26.6%** |
| yarp-reverse-http1 | dotnet-httpclient | **21,480**<br><sub>(90 MiB / 51.2% CPU)</sub> | **21,480**<br><sub>(90 MiB / 51.2% CPU)</sub> | **41.8%** |
| twp-reverse-http1 | dotnet-httpclient | 🥇 **25,491**<br><sub>(72 MiB / 50.4% CPU)</sub> | **25,491**<br><sub>(72 MiB / 50.4% CPU)</sub> | **49.6%** |

**Linux** (`ubuntu-latest`)

| Arm | Generator | Sustain | Peak | % of origin-HttpClient |
|---|---|---:|---:|---:|
| origin-direct | dotnet-httpclient | **72,584**<br><sub>(79 MiB / 42.9% CPU)</sub> | **72,584**<br><sub>(79 MiB / 42.9% CPU)</sub> | **100.0%** |
| origin-direct-bombardier | bombardier | **42,071**<br><sub>(78 MiB / 34.8% CPU)</sub> | **42,071**<br><sub>(78 MiB / 34.8% CPU)</sub> | **58.0%** |
| bare-reverse-http1 | dotnet-httpclient | **32,744**<br><sub>(66 MiB / 44.7% CPU)</sub> | **32,744**<br><sub>(66 MiB / 44.7% CPU)</sub> | **45.1%** |
| nginx-reverse-http1 | dotnet-httpclient | 🥇 **38,331**<br><sub>(71 MiB / 40.5% CPU)</sub> | **38,331**<br><sub>(71 MiB / 40.5% CPU)</sub> | **52.8%** |
| yarp-reverse-http1 | dotnet-httpclient | **27,852**<br><sub>(118 MiB / 49.8% CPU)</sub> | **27,852**<br><sub>(118 MiB / 49.8% CPU)</sub> | **38.4%** |
| twp-reverse-http1 | dotnet-httpclient | **32,213**<br><sub>(84 MiB / 50.3% CPU)</sub> | **32,213**<br><sub>(84 MiB / 50.3% CPU)</sub> | **44.4%** |

Reverse peers are about **50–50%** of the origin-direct HttpClient peak on this runner class (Win TWP **49.6%**, Lin TWP **44.4%**). Prefer the **%** column over absolute RPS across runs. Bare and origin-direct are controls (not medal peers).

#### Block B — H2 TLS→H1

Peer ratios (÷YARP / ÷nginx) on median peak; **RPS cells** embed `(MiB / CPU%)`.

**Windows** (`windows-latest`)

| Arm | Generator | Sustain | Peak | ÷YARP | ÷nginx |
|---|---|---:|---:|---:|---:|
| nginx-reverse-http2 | dotnet-httpclient | **10,682**<br><sub>(137 MiB / 23.9% CPU)</sub> | **11,106**<br><sub>(137 MiB / 23.9% CPU)</sub> | **0.38×** | **1×** |
| yarp-reverse-http2 | dotnet-httpclient | **29,267**<br><sub>(92 MiB / 53.5% CPU)</sub> | **29,267**<br><sub>(92 MiB / 53.5% CPU)</sub> | **1×** | **2.64×** |
| twp-reverse-http2-cleartext | dotnet-httpclient | 🥇 **34,965**<br><sub>(98 MiB / 51.9% CPU)</sub> | **34,965**<br><sub>(98 MiB / 51.9% CPU)</sub> | **1.19×** | **3.15×** |

**Linux** (`ubuntu-latest`)

| Arm | Generator | Sustain | Peak | ÷YARP | ÷nginx |
|---|---|---:|---:|---:|---:|
| nginx-reverse-http2 | dotnet-httpclient | **13,183**<br><sub>(95 MiB / 22.8% CPU)</sub> | **18,064**<br><sub>(95 MiB / 22.8% CPU)</sub> | **0.62×** | **1×** |
| yarp-reverse-http2 | dotnet-httpclient | **28,976**<br><sub>(122 MiB / 49.9% CPU)</sub> | **28,976**<br><sub>(122 MiB / 49.9% CPU)</sub> | **1×** | **1.6×** |
| twp-reverse-http2-cleartext | dotnet-httpclient | 🥇 **34,486**<br><sub>(117 MiB / 52% CPU)</sub> | **34,486**<br><sub>(117 MiB / 52% CPU)</sub> | **1.19×** | **1.91×** |

#### Block C — H3→H1

Same layout as Block B. Requires QuicListener; nginx only with `http_v3_module` (Windows nginx has no QUIC).

**Windows** (`windows-latest`)

| Arm | Generator | Sustain | Peak | ÷YARP | ÷nginx |
|---|---|---:|---:|---:|---:|
| nginx-reverse-http3-cleartext | dotnet-httpclient | *Not possible* (no QUIC) | *Not possible* | — | — |
| yarp-reverse-http3-cleartext | dotnet-httpclient | **14,650**<br><sub>(157 MiB / 48.8% CPU)</sub> | **14,650**<br><sub>(157 MiB / 48.8% CPU)</sub> | **1×** | **—** |
| twp-reverse-http3-cleartext | dotnet-httpclient | 🥇 **15,015**<br><sub>(102 MiB / 43.9% CPU)</sub> | **15,015**<br><sub>(102 MiB / 43.9% CPU)</sub> | **1.02×** | **—** |

**Linux** (`ubuntu-latest`)

| Arm | Generator | Sustain | Peak | ÷YARP | ÷nginx |
|---|---|---:|---:|---:|---:|
| nginx-reverse-http3-cleartext | dotnet-httpclient | **0**<br><sub>(102 MiB / 22.6% CPU)</sub> | **14,877**<br><sub>(102 MiB / 22.6% CPU)</sub> | **0.8×** | **1×** |
| yarp-reverse-http3-cleartext | dotnet-httpclient | **18,579**<br><sub>(181 MiB / 50.3% CPU)</sub> | **18,579**<br><sub>(181 MiB / 50.3% CPU)</sub> | **1×** | **1.25×** |
| twp-reverse-http3-cleartext | dotnet-httpclient | 🥇 **20,362**<br><sub>(139 MiB / 49.6% CPU)</sub> | **20,362**<br><sub>(139 MiB / 49.6% CPU)</sub> | **1.1×** | **1.37×** |

**How to read the tables**

- **Mode**: **Reverse** = transparent fixed-forward (may TLS-terminate to a cleartext origin, or re-encrypt to a configured HTTPS/QUIC origin). **MITM** = both legs are visible in the clear inside TWP — either by decrypting client TLS/QUIC (forged cert / CONNECT) **or** by accepting an already-cleartext client (explicit HTTP proxy / inspectable transparent reverse) while still speaking plain or TLS to the origin. nginx and YARP cannot do MITM. **HTTP/3 has no cleartext client** (QUIC always encrypted).
- **Sustainable** = last concurrency that still met error/latency SLOs. **Peak** = highest RPS in that ramp.
- 🥇 = best among **TWP / nginx / YARP** only on that row or saturation block (never vs bare / origin-direct / bombardier). Product matrices and saturation **Sustain**: highest sustainable RPS. Saturation **RPS cells** embed `(MiB / CPU%)`; 🥇 for Memory/CPU is omitted (footprint is informational). Omitted when only TWP can run the path (no fair multi-product comparison).
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
pwsh tools/RpsLoadProbe/run-rps.ps1 -Mode compare-saturation
```

## Windows — Titanium vs nginx vs YARP

Client / origin: HTTP version and whether TLS is used (`plain` = cleartext, `TLS` = encrypted, `QUIC` = HTTP/3).

Median of **3 repeats** on `windows-latest` (4 vCPU / 16 GiB). Saturation @ `70b5ca33` — [32756394056](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32756394056); same [32756396780](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32756396780); terminate [32756400033](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32756400033); bridges [32756402848](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32756402848); MITM [32756409108](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32756409108). **H3→H1** same-job @ `70b5ca33` — [32756406040](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32756406040) (`compare-http3-cleartext`). Three-process harness, parent-seeded loopback CA. Warmup 2s / measure 8s; concurrency 8, 16, 32, 64. Prefer TWP÷peer ratios over absolute RPS. **RPS cells** include median RSS / CPU at the peak-RPS step as `<br><sub>(MiB / CPU%)</sub>` for TWP, nginx, and YARP (proxy child + descendant tree). Laptop High-perf / cool-paired numbers stay on the [local lab](Performance-Local-Lab).

**Load generators:** Reverse inbound H3 arms use **`dotnet-httpclient`** (`http_version=3.0`, `RequestVersionExact`). nginx/Windows is same-OS only (no QUIC).

| Mode | Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---|---|---:|---:|---:|---:|---:|---:|
| Reverse | HTTP/1 · plain | HTTP/1 · plain | 🥇 **31,858**<br><sub>(71 MiB / 47.7% CPU)</sub> | **31,858**<br><sub>(71 MiB / 47.7% CPU)</sub> | **19,073**<br><sub>(120 MiB / 24.9% CPU)</sub> | **19,390**<br><sub>(120 MiB / 24.9% CPU)</sub> | **26,449**<br><sub>(86 MiB / 50.8% CPU)</sub> | **26,449**<br><sub>(86 MiB / 50.8% CPU)</sub> |
| Reverse | HTTP/1 · plain | HTTP/1 · TLS | 🥇 **26,571**<br><sub>(84 MiB / 51.5% CPU)</sub> | **26,571**<br><sub>(84 MiB / 51.5% CPU)</sub> | *Not possible* | *Not possible* | **23,785**<br><sub>(94 MiB / 50.4% CPU)</sub> | **23,785**<br><sub>(94 MiB / 50.4% CPU)</sub> |
| Reverse | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **20,650**<br><sub>(83 MiB / 50% CPU)</sub> | **20,650**<br><sub>(83 MiB / 50% CPU)</sub> | **8,515**<br><sub>(136 MiB / 24.8% CPU)</sub> | **8,879**<br><sub>(136 MiB / 24.8% CPU)</sub> | **18,241**<br><sub>(101 MiB / 47.5% CPU)</sub> | **18,241**<br><sub>(101 MiB / 47.5% CPU)</sub> |
| Reverse | HTTP/1 · TLS | HTTP/2 · TLS | 🥇 **70,007**<br><sub>(114 MiB / 49.7% CPU)</sub> | **70,007**<br><sub>(114 MiB / 49.7% CPU)</sub> | *Not possible* | *Not possible* | **68,366**<br><sub>(108 MiB / 49.7% CPU)</sub> | **68,366**<br><sub>(108 MiB / 49.7% CPU)</sub> |
| Reverse | HTTP/1 · TLS | HTTP/3 · QUIC | **43,774**<br><sub>(133 MiB / 49.5% CPU)</sub> | **43,774**<br><sub>(133 MiB / 49.5% CPU)</sub> | *Not possible* (no H3 origin) | *Not possible* (no H3 origin) | 🥇 **44,061**<br><sub>(143 MiB / 51.1% CPU)</sub> | **44,061**<br><sub>(143 MiB / 51.1% CPU)</sub> |
| Reverse | HTTP/2 · plain | HTTP/1 · plain | 🥇 **88,369**<br><sub>(96 MiB / 47.6% CPU)</sub> | **88,369**<br><sub>(96 MiB / 47.6% CPU)</sub> | *Not possible* | *Not possible* | **85,941**<br><sub>(86 MiB / 48.6% CPU)</sub> | **85,941**<br><sub>(86 MiB / 48.6% CPU)</sub> |
| Reverse | HTTP/2 · plain | HTTP/2 · plain | 🥇 **93,940**<br><sub>(72 MiB / 36.2% CPU)</sub> | **93,940**<br><sub>(72 MiB / 36.2% CPU)</sub> | *Not possible* | *Not possible* | **67,808**<br><sub>(97 MiB / 50.3% CPU)</sub> | **67,808**<br><sub>(97 MiB / 50.3% CPU)</sub> |
| Reverse | HTTP/2 · plain | HTTP/2 · TLS | 🥇 **78,048**<br><sub>(81 MiB / 37% CPU)</sub> | **78,048**<br><sub>(81 MiB / 37% CPU)</sub> | *Not possible* | *Not possible* | **58,860**<br><sub>(98 MiB / 46.9% CPU)</sub> | **58,860**<br><sub>(98 MiB / 46.9% CPU)</sub> |
| Reverse | HTTP/2 · plain | HTTP/3 · QUIC | **67,476**<br><sub>(162 MiB / 50.5% CPU)</sub> | **67,476**<br><sub>(162 MiB / 50.5% CPU)</sub> | *Not possible* (no H3 origin) | *Not possible* (no H3 origin) | 🥇 **68,150**<br><sub>(153 MiB / 50% CPU)</sub> | **68,150**<br><sub>(153 MiB / 50% CPU)</sub> |
| Reverse | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **35,550**<br><sub>(101 MiB / 51.9% CPU)</sub> | **35,550**<br><sub>(101 MiB / 51.9% CPU)</sub> | **5,312**<br><sub>(137 MiB / 24.2% CPU)</sub> | **11,195**<br><sub>(137 MiB / 24.2% CPU)</sub> | **29,226**<br><sub>(91 MiB / 53.3% CPU)</sub> | **29,226**<br><sub>(91 MiB / 53.3% CPU)</sub> |
| Reverse | HTTP/2 · TLS | HTTP/2 · plain | 🥇 **175,995**<br><sub>(91 MiB / 38.4% CPU)</sub> | **175,995**<br><sub>(91 MiB / 38.4% CPU)</sub> | *Not possible* | *Not possible* | **119,743**<br><sub>(98 MiB / 50.2% CPU)</sub> | **119,743**<br><sub>(98 MiB / 50.2% CPU)</sub> |
| Reverse | HTTP/2 · TLS | HTTP/3 · QUIC | 🥇 **67,452**<br><sub>(167 MiB / 51.6% CPU)</sub> | **67,452**<br><sub>(167 MiB / 51.6% CPU)</sub> | *Not possible* (no H3 origin) | *Not possible* (no H3 origin) | **64,735**<br><sub>(152 MiB / 52.8% CPU)</sub> | **64,735**<br><sub>(152 MiB / 52.8% CPU)</sub> |
| Reverse | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **24,543**<br><sub>(112 MiB / 41.2% CPU)</sub> | **24,543**<br><sub>(112 MiB / 41.2% CPU)</sub> | *Not possible* (no QUIC) | *Not possible* (no QUIC) | **20,362**<br><sub>(124 MiB / 48.2% CPU)</sub> | **21,714**<br><sub>(124 MiB / 48.2% CPU)</sub> |
| Reverse | HTTP/3 · QUIC | HTTP/2 · TLS | 🥇 **61,242**<br><sub>(178 MiB / 46.7% CPU)</sub> | **61,242**<br><sub>(178 MiB / 46.7% CPU)</sub> | *Not possible* (no H3→H2) | *Not possible* (no H3→H2) | **61,038**<br><sub>(181 MiB / 46.4% CPU)</sub> | **61,038**<br><sub>(181 MiB / 46.4% CPU)</sub> |
| Reverse | HTTP/3 · QUIC | HTTP/3 · QUIC | 🥇 **16,295**<br><sub>(122 MiB / 41.8% CPU)</sub> | **18,745**<br><sub>(122 MiB / 41.8% CPU)</sub> | *Not possible* (no H3 origin) | *Not possible* (no H3 origin) | **13,910**<br><sub>(162 MiB / 48.7% CPU)</sub> | **15,582**<br><sub>(162 MiB / 48.7% CPU)</sub> |
| MITM | HTTP/1 · plain | HTTP/1 · plain | 🥇 **30,490**<br><sub>(76 MiB / 49.6% CPU)</sub> | **30,490**<br><sub>(76 MiB / 49.6% CPU)</sub> | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/1 · plain | HTTP/1 · TLS | 🥇 **26,767**<br><sub>(83 MiB / 50.7% CPU)</sub> | **26,767**<br><sub>(83 MiB / 50.7% CPU)</sub> | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **26,130**<br><sub>(84 MiB / 47.8% CPU)</sub> | **26,130**<br><sub>(84 MiB / 47.8% CPU)</sub> | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/1 · TLS | HTTP/2 · TLS | 🥇 **31,194**<br><sub>(109 MiB / 45.5% CPU)</sub> | **31,194**<br><sub>(109 MiB / 45.5% CPU)</sub> | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/1 · TLS | HTTP/3 · QUIC | 🥇 **18,728**<br><sub>(105 MiB / 49.7% CPU)</sub> | **18,728**<br><sub>(105 MiB / 49.7% CPU)</sub> | *Not possible* (no QUIC) | *Not possible* (no QUIC) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 · plain | HTTP/1 · plain | 🥇 **42,149**<br><sub>(92 MiB / 51.7% CPU)</sub> | **42,149**<br><sub>(92 MiB / 51.7% CPU)</sub> | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 · plain | HTTP/2 · plain | 🥇 **93,541**<br><sub>(77 MiB / 35.8% CPU)</sub> | **93,541**<br><sub>(77 MiB / 35.8% CPU)</sub> | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 · plain | HTTP/2 · TLS | 🥇 **79,762**<br><sub>(83 MiB / 35.1% CPU)</sub> | **79,762**<br><sub>(83 MiB / 35.1% CPU)</sub> | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 · plain | HTTP/3 · QUIC | 🥇 **32,920**<br><sub>(128 MiB / 52.4% CPU)</sub> | **32,920**<br><sub>(128 MiB / 52.4% CPU)</sub> | *Not possible* (no QUIC) | *Not possible* (no QUIC) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **40,783**<br><sub>(98 MiB / 50.8% CPU)</sub> | **40,783**<br><sub>(98 MiB / 50.8% CPU)</sub> | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 · TLS | HTTP/2 · plain | 🥇 **85,081**<br><sub>(95 MiB / 34.6% CPU)</sub> | **85,081**<br><sub>(95 MiB / 34.6% CPU)</sub> | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 · TLS | HTTP/3 · QUIC | 🥇 **32,255**<br><sub>(136 MiB / 54% CPU)</sub> | **32,255**<br><sub>(136 MiB / 54% CPU)</sub> | *Not possible* (no QUIC) | *Not possible* (no QUIC) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **19,762**<br><sub>(106 MiB / 44.4% CPU)</sub> | **19,762**<br><sub>(106 MiB / 44.4% CPU)</sub> | *Not possible* (no QUIC) | *Not possible* (no QUIC) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/3 · QUIC | HTTP/2 · TLS | 🥇 **26,881**<br><sub>(121 MiB / 46.4% CPU)</sub> | **26,881**<br><sub>(121 MiB / 46.4% CPU)</sub> | *Not possible* (no QUIC) | *Not possible* (no QUIC) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/3 · QUIC | HTTP/3 · QUIC | 🥇 **16,497**<br><sub>(125 MiB / 46.8% CPU)</sub> | **16,497**<br><sub>(125 MiB / 46.8% CPU)</sub> | *Not possible* (no QUIC) | *Not possible* (no QUIC) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/1 · plain (CONNECT) | HTTP/1 · TLS | 🥇 **22,426**<br><sub>(97 MiB / 47.7% CPU)</sub> | **22,426**<br><sub>(97 MiB / 47.7% CPU)</sub> | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/1 · TLS | HTTP/1 · TLS | 🥇 **24,078**<br><sub>(81 MiB / 48.5% CPU)</sub> | **24,078**<br><sub>(81 MiB / 48.5% CPU)</sub> | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **74,234**<br><sub>(91 MiB / 33.8% CPU)</sub> | **74,234**<br><sub>(91 MiB / 33.8% CPU)</sub> | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 · TLS | HTTP/1 · TLS | 🥇 **35,551**<br><sub>(97 MiB / 51.9% CPU)</sub> | **35,551**<br><sub>(97 MiB / 51.9% CPU)</sub> | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **17,208**<br><sub>(112 MiB / 44.3% CPU)</sub> | **17,208**<br><sub>(112 MiB / 44.3% CPU)</sub> | *Not possible* (no QUIC) | *Not possible* (no QUIC) | *Not possible* (no MITM) | *Not possible* (no MITM) |

TWP÷YARP: H3→H1 @ `70b5ca33` ≈ **1.13×** RPS / **0.9×** Memory (112 / 124 MiB) — [32756406040](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32756406040); other bridges @ `70b5ca33`: H3→H2 ≈ **1×** / **0.98×**; H2 TLS→H1 ≈ **1.22×** / **1.11×** Memory; H1→H2 ≈ **1.02×** / **1.05×** Memory. H1→H3 ≈ **0.99×** / **0.93×** Memory. Prefer ratios over absolute RPS on GHA VMs. nginx/YARP cannot MITM.

## Linux — Titanium vs nginx vs YARP

Median of **3 repeats** on `ubuntu-latest` (4 vCPU / 16 GiB). Saturation @ `70b5ca33` ([32756394056](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32756394056)); same [32756396780](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32756396780); terminate [32756400033](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32756400033); bridges [32756402848](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32756402848); MITM [32756409108](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32756409108). **H3→H1** same-job @ `70b5ca33` — [32756406040](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32756406040) (`compare-http3-cleartext`, TWP+YARP+nginx). Warmup 2s / measure 8s; concurrency 8, 16, 32, 64. **Linux nginx is the authoritative nginx baseline.** The RPS workflow installs nginx.org mainline (`http_v3_module`) and `libmsquic` (`QuicListener.IsSupported=true` on `ubuntu-latest`). Prefer ratios over absolute RPS. **RPS cells** include peer `(MiB / CPU%)` as on Windows.

TWP÷YARP: H3→H1 @ `70b5ca33` ≈ **1.09×** RPS / **0.81×** Memory (150 / 184 MiB); other bridges @ `70b5ca33`: H3→H2 ≈ **1.08×** / **0.78×**; H2 TLS→H1 ≈ **1.13×** / **1×**; H1→H2 ≈ **1.03×** / **1.03×** Memory. H1 plain ÷nginx from `compare-same` ≈ **0.86**.

| Mode | Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---|---|---:|---:|---:|---:|---:|---:|
| Reverse | HTTP/1 · plain | HTTP/1 · plain | **47,744**<br><sub>(83 MiB / 50.1% CPU)</sub> | **47,744**<br><sub>(83 MiB / 50.1% CPU)</sub> | 🥇 **55,628**<br><sub>(72 MiB / 40.3% CPU)</sub> | **55,628**<br><sub>(72 MiB / 40.3% CPU)</sub> | **40,735**<br><sub>(116 MiB / 49% CPU)</sub> | **40,735**<br><sub>(116 MiB / 49% CPU)</sub> |
| Reverse | HTTP/1 · plain | HTTP/1 · TLS | 🥇 **37,032**<br><sub>(102 MiB / 49% CPU)</sub> | **37,032**<br><sub>(102 MiB / 49% CPU)</sub> | *Not possible* | *Not possible* | **33,147**<br><sub>(128 MiB / 49% CPU)</sub> | **33,147**<br><sub>(128 MiB / 49% CPU)</sub> |
| Reverse | HTTP/1 · TLS | HTTP/1 · plain | **28,542**<br><sub>(100 MiB / 49.3% CPU)</sub> | **28,542**<br><sub>(100 MiB / 49.3% CPU)</sub> | 🥇 **33,902**<br><sub>(96 MiB / 41.3% CPU)</sub> | **33,902**<br><sub>(96 MiB / 41.3% CPU)</sub> | **24,999**<br><sub>(130 MiB / 49.6% CPU)</sub> | **24,999**<br><sub>(130 MiB / 49.6% CPU)</sub> |
| Reverse | HTTP/1 · TLS | HTTP/2 · TLS | 🥇 **35,999**<br><sub>(147 MiB / 50% CPU)</sub> | **35,999**<br><sub>(147 MiB / 50% CPU)</sub> | *Not possible* | *Not possible* | **35,078**<br><sub>(142 MiB / 47.5% CPU)</sub> | **35,078**<br><sub>(142 MiB / 47.5% CPU)</sub> |
| Reverse | HTTP/1 · TLS | HTTP/3 · QUIC | 🥇 **26,854**<br><sub>(145 MiB / 52.7% CPU)</sub> | **26,854**<br><sub>(145 MiB / 52.7% CPU)</sub> | *Not possible* (no H3 origin) | *Not possible* (no H3 origin) | **24,646**<br><sub>(169 MiB / 49% CPU)</sub> | **24,646**<br><sub>(169 MiB / 49% CPU)</sub> |
| Reverse | HTTP/2 · plain | HTTP/1 · plain | 🥇 **51,046**<br><sub>(113 MiB / 52.3% CPU)</sub> | **51,046**<br><sub>(113 MiB / 52.3% CPU)</sub> | *Not possible* | *Not possible* | **49,198**<br><sub>(114 MiB / 47.4% CPU)</sub> | **49,198**<br><sub>(114 MiB / 47.4% CPU)</sub> |
| Reverse | HTTP/2 · plain | HTTP/2 · plain | 🥇 **91,802**<br><sub>(103 MiB / 41.8% CPU)</sub> | **91,802**<br><sub>(103 MiB / 41.8% CPU)</sub> | *Not possible* | *Not possible* | **70,354**<br><sub>(120 MiB / 46.2% CPU)</sub> | **70,354**<br><sub>(120 MiB / 46.2% CPU)</sub> |
| Reverse | HTTP/2 · plain | HTTP/2 · TLS | 🥇 **73,245**<br><sub>(114 MiB / 39.7% CPU)</sub> | **73,245**<br><sub>(114 MiB / 39.7% CPU)</sub> | *Not possible* | *Not possible* | **57,992**<br><sub>(133 MiB / 45.2% CPU)</sub> | **57,992**<br><sub>(133 MiB / 45.2% CPU)</sub> |
| Reverse | HTTP/2 · plain | HTTP/3 · QUIC | 🥇 **36,700**<br><sub>(156 MiB / 50.5% CPU)</sub> | **36,700**<br><sub>(156 MiB / 50.5% CPU)</sub> | *Not possible* (no H3 origin) | *Not possible* (no H3 origin) | **35,962**<br><sub>(156 MiB / 45.6% CPU)</sub> | **35,962**<br><sub>(156 MiB / 45.6% CPU)</sub> |
| Reverse | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **38,414**<br><sub>(120 MiB / 51.2% CPU)</sub> | **38,414**<br><sub>(120 MiB / 51.2% CPU)</sub> | **16,262**<br><sub>(95 MiB / 22.9% CPU)</sub> | **22,526**<br><sub>(95 MiB / 22.9% CPU)</sub> | **34,096**<br><sub>(120 MiB / 48.5% CPU)</sub> | **34,096**<br><sub>(120 MiB / 48.5% CPU)</sub> |
| Reverse | HTTP/2 · TLS | HTTP/2 · plain | 🥇 **83,763**<br><sub>(111 MiB / 41% CPU)</sub> | **83,763**<br><sub>(111 MiB / 41% CPU)</sub> | *Not possible* | *Not possible* | **57,056**<br><sub>(126 MiB / 45.6% CPU)</sub> | **57,056**<br><sub>(126 MiB / 45.6% CPU)</sub> |
| Reverse | HTTP/2 · TLS | HTTP/3 · QUIC | 🥇 **34,104**<br><sub>(164 MiB / 49.8% CPU)</sub> | **34,104**<br><sub>(164 MiB / 49.8% CPU)</sub> | *Not possible* (no H3 origin) | *Not possible* (no H3 origin) | **31,551**<br><sub>(158 MiB / 44.6% CPU)</sub> | **31,551**<br><sub>(158 MiB / 44.6% CPU)</sub> |
| Reverse | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **23,666**<br><sub>(150 MiB / 51.8% CPU)</sub> | **23,666**<br><sub>(150 MiB / 51.8% CPU)</sub> | **0**<br><sub>(103 MiB / 22% CPU)</sub> | **19,342**<br><sub>(103 MiB / 22% CPU)</sub> | **21,727**<br><sub>(184 MiB / 48.8% CPU)</sub> | **21,727**<br><sub>(184 MiB / 48.8% CPU)</sub> |
| Reverse | HTTP/3 · QUIC | HTTP/2 · TLS | 🥇 **32,854**<br><sub>(156 MiB / 52.6% CPU)</sub> | **32,854**<br><sub>(156 MiB / 52.6% CPU)</sub> | *Not possible* (no H3→H2) | *Not possible* (no H3→H2) | **30,417**<br><sub>(200 MiB / 47.4% CPU)</sub> | **30,417**<br><sub>(200 MiB / 47.4% CPU)</sub> |
| Reverse | HTTP/3 · QUIC | HTTP/3 · QUIC | 🥇 **25,774**<br><sub>(172 MiB / 46.7% CPU)</sub> | **25,774**<br><sub>(172 MiB / 46.7% CPU)</sub> | *Not possible* (no H3 origin) | *Not possible* (no H3 origin) | **22,022**<br><sub>(212 MiB / 47% CPU)</sub> | **22,022**<br><sub>(212 MiB / 47% CPU)</sub> |
| MITM | HTTP/1 · plain | HTTP/1 · plain | 🥇 **43,214**<br><sub>(86 MiB / 50.5% CPU)</sub> | **43,214**<br><sub>(86 MiB / 50.5% CPU)</sub> | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/1 · plain | HTTP/1 · TLS | 🥇 **35,436**<br><sub>(104 MiB / 48% CPU)</sub> | **35,436**<br><sub>(104 MiB / 48% CPU)</sub> | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **34,650**<br><sub>(109 MiB / 48.5% CPU)</sub> | **34,650**<br><sub>(109 MiB / 48.5% CPU)</sub> | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/1 · TLS | HTTP/2 · TLS | 🥇 **31,381**<br><sub>(148 MiB / 46.8% CPU)</sub> | **31,381**<br><sub>(148 MiB / 46.8% CPU)</sub> | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/1 · TLS | HTTP/3 · QUIC | 🥇 **22,021**<br><sub>(145 MiB / 51% CPU)</sub> | **22,021**<br><sub>(145 MiB / 51% CPU)</sub> | *Not possible* (no QUIC) | *Not possible* (no QUIC) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 · plain | HTTP/1 · plain | 🥇 **46,899**<br><sub>(116 MiB / 50% CPU)</sub> | **46,899**<br><sub>(116 MiB / 50% CPU)</sub> | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 · plain | HTTP/2 · plain | 🥇 **76,596**<br><sub>(106 MiB / 37.1% CPU)</sub> | **76,596**<br><sub>(106 MiB / 37.1% CPU)</sub> | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 · plain | HTTP/2 · TLS | 🥇 **58,638**<br><sub>(111 MiB / 35.6% CPU)</sub> | **58,638**<br><sub>(111 MiB / 35.6% CPU)</sub> | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 · plain | HTTP/3 · QUIC | 🥇 **27,305**<br><sub>(152 MiB / 47.8% CPU)</sub> | **27,305**<br><sub>(152 MiB / 47.8% CPU)</sub> | *Not possible* (no QUIC) | *Not possible* (no QUIC) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **43,445**<br><sub>(120 MiB / 49.3% CPU)</sub> | **43,445**<br><sub>(120 MiB / 49.3% CPU)</sub> | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 · TLS | HTTP/2 · plain | 🥇 **69,301**<br><sub>(112 MiB / 36.4% CPU)</sub> | **69,301**<br><sub>(112 MiB / 36.4% CPU)</sub> | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 · TLS | HTTP/3 · QUIC | 🥇 **26,163**<br><sub>(153 MiB / 47.6% CPU)</sub> | **26,163**<br><sub>(153 MiB / 47.6% CPU)</sub> | *Not possible* (no QUIC) | *Not possible* (no QUIC) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **24,653**<br><sub>(143 MiB / 46.3% CPU)</sub> | **24,653**<br><sub>(143 MiB / 46.3% CPU)</sub> | *Not possible* (no QUIC) | *Not possible* (no QUIC) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/3 · QUIC | HTTP/2 · TLS | 🥇 **24,660**<br><sub>(146 MiB / 48.1% CPU)</sub> | **24,660**<br><sub>(146 MiB / 48.1% CPU)</sub> | *Not possible* (no QUIC) | *Not possible* (no QUIC) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/3 · QUIC | HTTP/3 · QUIC | 🥇 **18,959**<br><sub>(157 MiB / 44.7% CPU)</sub> | **18,959**<br><sub>(157 MiB / 44.7% CPU)</sub> | *Not possible* (no QUIC) | *Not possible* (no QUIC) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/1 · plain (CONNECT) | HTTP/1 · TLS | 🥇 **26,867**<br><sub>(129 MiB / 48.1% CPU)</sub> | **26,867**<br><sub>(129 MiB / 48.1% CPU)</sub> | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/1 · TLS | HTTP/1 · TLS | 🥇 **28,383**<br><sub>(110 MiB / 46% CPU)</sub> | **28,383**<br><sub>(110 MiB / 46% CPU)</sub> | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **56,281**<br><sub>(116 MiB / 34.3% CPU)</sub> | **56,281**<br><sub>(116 MiB / 34.3% CPU)</sub> | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 · TLS | HTTP/1 · TLS | 🥇 **33,549**<br><sub>(124 MiB / 46.1% CPU)</sub> | **33,549**<br><sub>(124 MiB / 46.1% CPU)</sub> | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **20,261**<br><sub>(152 MiB / 44.5% CPU)</sub> | **20,261**<br><sub>(152 MiB / 44.5% CPU)</sub> | *Not possible* (no QUIC) | *Not possible* (no QUIC) | *Not possible* (no MITM) | *Not possible* (no MITM) |

On this GHA shape, TWP H1 plain ÷ nginx H1 plain ≈ **0.86** (47,744 / 55,628). H1 TLS terminate ≈ **0.84** (28,542 / 33,902). TWP÷YARP H1 plain ≈ **1.17×** (47,744 / 40,735). H3→H1 @ `70b5ca33` ≈ **1.09×** RPS / **0.81×** Memory; other bridges @ `70b5ca33`: H3→H2 ≈ **1.08×** / **0.78×**; H2 TLS→H1 ≈ **1.13×** / **1×**; H1→H2 ≈ **1.03×** / **1.03×** Memory. Absolute RPS swings by VM; prefer the **ratio** and **median across repeats**. MITM publishes the same **15** Client×Origin pairs as Reverse (inspectable/decrypt), then dual-crypto extras (CONNECT, TLS↔TLS). nginx/YARP cannot MITM.

**nginx HTTP/3:** inbound QUIC terminate → cleartext H1 (`nginx-reverse-http3-cleartext`) @ `70b5ca33` `compare-http3-cleartext`: sustain **0** (p99/error SLO miss when 0) / peak **19,342**. TWP/YARP H3→H1 on this row are from the same pass. nginx still cannot speak HTTP/3 to an origin (no H3 upstream in this conf).

**YARP HTTP/3 (this matrix):** TWP leads H3→H1 ≈ **1.09×** (23,666 / 21,727), H3→H2 ≈ **1.08×** (32,854 / 30,417). H1→H2 ≈ **1.03×** (35,999 / 35,078). H1→H3 ≈ **1.09×** (26,854 / 24,646).

**Windows vs Linux:** both CI envs are **4 vCPU / 16 GiB**, but do **not** compare absolute RPS across OS. Linux nginx leads H1 plain/TLS terminate (TWP second, ahead of YARP). Windows H3→H1 @ `70b5ca33` ≈ **1.13×** RPS / **0.9×** Memory. Cool laptop notes remain on [Performance Local Lab](Performance-Local-Lab).


### Tiny JSON reverse is nginx’s best case on Linux

The tables above use **~64 B keep-alive GET** on loopback. On Linux H1 reverse, nginx leads; YARP sits near TWP. Heavier bodies, POSTs, TLS handshake cost, and lossy/HOL workloads (below) change the picture. MITM rows remain TWP-only. nginx HTTP/3 is inbound-terminate only (see note above).

### Why isn’t HTTP/3 > HTTP/2 > HTTP/1 in raw RPS?

For **tiny JSON responses** (~64 B) on loopback, that ordering is **not** expected: topology (TLS hop count, terminate vs MITM) dominates; HTTP/2 and HTTP/3 help multiplexing, not single-origin tiny-GET RPS. See the **lossy** tables below for a workload where protocol design matters.

## Heavier reverse workloads

Separate from the tiny-GET matrix. Same measurement environments. Modes: `compare-bodies`, `compare-post`, `compare-lossy`, `compare-tls-cost`, `compare-arch` in [RpsLoadProbe](https://github.com/justcoding121/titanium-web-proxy/tree/develop/tools/RpsLoadProbe). **PUT with the same body is the same proxy work as POST; DELETE with no body matches GET** — only POST is published. Bodies/POST/lossy stay **half-duplex**. `compare-arch` is the slow-consumer / early-response / duplex set. Laptop numbers are on [Performance Local Lab](Performance-Local-Lab#architecture-sensitive); CI medians go in the tables below.

Lossy link = **userspace** shim (not kernel `netem`): TCP gets per-buffer delay + occasional whole-connection stalls (honest HOL for multiplexed H2); UDP gets per-datagram delay + drops (QUIC). `compare-lossy` publishes H1/H2/H3; H3 is where the protocol design is supposed to matter.

### Windows — heavier reverse GET (64 KiB / 256 KiB)

Median of **3** repeats on `windows-latest` @ `70b5ca33`. Source: Actions [32756412397](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32756412397) (`compare-bodies`). Warmup 2s / measure 8s. **RPS cells** include `(MiB / CPU%)` footprints.

| Body | Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---|---|---:|---:|---:|---:|---:|---:|
| 64 KiB | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **9,203**<br><sub>(103 MiB / 45.8% CPU)</sub> | **9,654**<br><sub>(103 MiB / 45.8% CPU)</sub> | **888**<br><sub>(136 MiB / 24.7% CPU)</sub> | **955**<br><sub>(136 MiB / 24.7% CPU)</sub> | **8,189**<br><sub>(106 MiB / 47% CPU)</sub> | **8,695**<br><sub>(106 MiB / 47% CPU)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **8,757**<br><sub>(157 MiB / 44.4% CPU)</sub> | **8,792**<br><sub>(157 MiB / 44.4% CPU)</sub> | **803**<br><sub>(137 MiB / 24.9% CPU)</sub> | **815**<br><sub>(137 MiB / 24.9% CPU)</sub> | **6,978**<br><sub>(111 MiB / 49.4% CPU)</sub> | **7,170**<br><sub>(111 MiB / 49.4% CPU)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **4,125**<br><sub>(108 MiB / 41% CPU)</sub> | **4,299**<br><sub>(108 MiB / 41% CPU)</sub> | *Not possible* (no QUIC) | *Not possible* (no QUIC) | **3,900**<br><sub>(178 MiB / 49.7% CPU)</sub> | **3,900**<br><sub>(178 MiB / 49.7% CPU)</sub> |
| 256 KiB | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **2,891**<br><sub>(99 MiB / 45.8% CPU)</sub> | **3,080**<br><sub>(99 MiB / 45.8% CPU)</sub> | **238**<br><sub>(137 MiB / 24.8% CPU)</sub> | **258**<br><sub>(137 MiB / 24.8% CPU)</sub> | **2,677**<br><sub>(98 MiB / 48.7% CPU)</sub> | **2,740**<br><sub>(98 MiB / 48.7% CPU)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **2,662**<br><sub>(116 MiB / 39.7% CPU)</sub> | **2,674**<br><sub>(116 MiB / 39.7% CPU)</sub> | **177**<br><sub>(139 MiB / 24.6% CPU)</sub> | **177**<br><sub>(139 MiB / 24.6% CPU)</sub> | **1,868**<br><sub>(100 MiB / 44.7% CPU)</sub> | **2,044**<br><sub>(100 MiB / 44.7% CPU)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **1,143**<br><sub>(86 MiB / 41% CPU)</sub> | **1,191**<br><sub>(86 MiB / 41% CPU)</sub> | *Not possible* (no QUIC) | *Not possible* (no QUIC) | **1,099**<br><sub>(171 MiB / 45% CPU)</sub> | **1,099**<br><sub>(171 MiB / 45% CPU)</sub> |

nginx/Windows collapses on large reverse bodies in this harness; treat as same-OS only. H1 TLS **64 KiB** ≈ **1.11×** YARP; **256 KiB** ≈ **1.12×**. H2→H1 64 KiB ≈ **1.23×**; H3→H1 64 KiB ≈ **1.1×**.

### Linux — heavier reverse GET (64 KiB / 256 KiB)

Median of **3** repeats @ `70b5ca33`. Source: Actions [32756412397](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32756412397) (`compare-bodies`). Warmup 2s / measure 8s.

| Body | Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---|---|---:|---:|---:|---:|---:|---:|
| 64 KiB | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **8,106**<br><sub>(166 MiB / 45.1% CPU)</sub> | **8,106**<br><sub>(166 MiB / 45.1% CPU)</sub> | **8,022**<br><sub>(97 MiB / 42.2% CPU)</sub> | **8,022**<br><sub>(97 MiB / 42.2% CPU)</sub> | **6,656**<br><sub>(159 MiB / 48.4% CPU)</sub> | **6,656**<br><sub>(159 MiB / 48.4% CPU)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **5,934**<br><sub>(218 MiB / 39.6% CPU)</sub> | **5,934**<br><sub>(218 MiB / 39.6% CPU)</sub> | **3,537**<br><sub>(102 MiB / 24.8% CPU)</sub> | **3,537**<br><sub>(102 MiB / 24.8% CPU)</sub> | **4,841**<br><sub>(162 MiB / 47.7% CPU)</sub> | **4,841**<br><sub>(162 MiB / 47.7% CPU)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **5,670**<br><sub>(174 MiB / 44% CPU)</sub> | **5,670**<br><sub>(174 MiB / 44% CPU)</sub> | **1,682**<br><sub>(114 MiB / 22.1% CPU)</sub> | **1,719**<br><sub>(114 MiB / 22.1% CPU)</sub> | **4,400**<br><sub>(222 MiB / 52% CPU)</sub> | **4,400**<br><sub>(222 MiB / 52% CPU)</sub> |
| 256 KiB | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **2,771**<br><sub>(122 MiB / 37.7% CPU)</sub> | **2,771**<br><sub>(122 MiB / 37.7% CPU)</sub> | **2,689**<br><sub>(96 MiB / 37.1% CPU)</sub> | **2,689**<br><sub>(96 MiB / 37.1% CPU)</sub> | **2,190**<br><sub>(168 MiB / 45.8% CPU)</sub> | **2,190**<br><sub>(168 MiB / 45.8% CPU)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **1,567**<br><sub>(214 MiB / 33.4% CPU)</sub> | **1,567**<br><sub>(214 MiB / 33.4% CPU)</sub> | **961**<br><sub>(101 MiB / 23.8% CPU)</sub> | **967**<br><sub>(101 MiB / 23.8% CPU)</sub> | **1,319**<br><sub>(144 MiB / 42.2% CPU)</sub> | **1,331**<br><sub>(144 MiB / 42.2% CPU)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **1,489**<br><sub>(146 MiB / 43.8% CPU)</sub> | **1,489**<br><sub>(146 MiB / 43.8% CPU)</sub> | **415**<br><sub>(104 MiB / 23.7% CPU)</sub> | **415**<br><sub>(104 MiB / 23.7% CPU)</sub> | **1,322**<br><sub>(215 MiB / 47.7% CPU)</sub> | **1,322**<br><sub>(215 MiB / 47.7% CPU)</sub> |

On this GHA pass TWP÷YARP H1 TLS ≈ **1.22×** (64 KiB) / **1.27×** (256 KiB); H2→H1 ≈ **1.23×** / **1.18×**; H3→H1 ≈ **1.29×** / **1.13×**. TWP÷nginx H1 TLS ≈ **1.01** / **1.03**. Absolute RPS swings by VM; prefer ratios.

### Windows — POST 64 KiB request + 64 KiB response

Median of **3** repeats on `windows-latest` @ `70b5ca33`. Source: Actions [32756415082](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32756415082) (`compare-post`).

| Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---|---:|---:|---:|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | 🥇 **5,945**<br><sub>(78 MiB / 46.2% CPU)</sub> | **6,034**<br><sub>(78 MiB / 46.2% CPU)</sub> | **302**<br><sub>(136 MiB / 24.6% CPU)</sub> | **369**<br><sub>(136 MiB / 24.6% CPU)</sub> | **3,050**<br><sub>(116 MiB / 42.5% CPU)</sub> | **3,243**<br><sub>(116 MiB / 42.5% CPU)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **4,047**<br><sub>(175 MiB / 48.6% CPU)</sub> | **4,047**<br><sub>(175 MiB / 48.6% CPU)</sub> | **326**<br><sub>(137 MiB / 24.6% CPU)</sub> | **342**<br><sub>(137 MiB / 24.6% CPU)</sub> | **2,769**<br><sub>(119 MiB / 38.9% CPU)</sub> | **2,895**<br><sub>(119 MiB / 38.9% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **2,062**<br><sub>(112 MiB / 42.8% CPU)</sub> | **2,150**<br><sub>(112 MiB / 42.8% CPU)</sub> | *Not possible* (no QUIC) | *Not possible* (no QUIC) | **1,928**<br><sub>(162 MiB / 48.6% CPU)</sub> | **2,035**<br><sub>(162 MiB / 48.6% CPU)</sub> |

TWP leads H1 POST (~**1.86×** YARP), H2 POST (~**1.4×** YARP), and H3 POST (~**1.06×** YARP).

### Linux — POST 64 KiB request + 64 KiB response

Median of **3** repeats @ `70b5ca33`. Source: Actions [32756415082](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32756415082) (`compare-post`).

| Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---|---:|---:|---:|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | 🥇 **4,876**<br><sub>(128 MiB / 45.5% CPU)</sub> | **4,876**<br><sub>(128 MiB / 45.5% CPU)</sub> | **4,131**<br><sub>(96 MiB / 48.7% CPU)</sub> | **4,131**<br><sub>(96 MiB / 48.7% CPU)</sub> | **3,260**<br><sub>(172 MiB / 54.9% CPU)</sub> | **3,260**<br><sub>(172 MiB / 54.9% CPU)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **3,101**<br><sub>(216 MiB / 47.5% CPU)</sub> | **3,101**<br><sub>(216 MiB / 47.5% CPU)</sub> | **2,033**<br><sub>(102 MiB / 22.5% CPU)</sub> | **2,063**<br><sub>(102 MiB / 22.5% CPU)</sub> | **2,591**<br><sub>(171 MiB / 48% CPU)</sub> | **2,591**<br><sub>(171 MiB / 48% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **2,965**<br><sub>(218 MiB / 44.4% CPU)</sub> | **2,965**<br><sub>(218 MiB / 44.4% CPU)</sub> | **771**<br><sub>(107 MiB / 23.9% CPU)</sub> | **771**<br><sub>(107 MiB / 23.9% CPU)</sub> | **2,648**<br><sub>(257 MiB / 49.7% CPU)</sub> | **2,648**<br><sub>(257 MiB / 49.7% CPU)</sub> |

Linux nginx H1/H2/H3 POST completed (nginx.org mainline). TWP÷YARP H1 ≈ **1.5×**; H2 ≈ **1.2×**; H3 ≈ **1.12×**. TWP÷nginx H3 ≈ **3.84×**.

### Windows — lossy / high-RTT (H2 HOL / H3 loss)

Userspace **5 ms** one-way delay + **1%** TCP connection stall (H1/H2) or UDP datagram drop (H3); **64 KiB** GET. H1/H2: median of **3** repeats on `windows-latest` @ `70b5ca33` — [32756417744](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32756417744) (`compare-lossy`). **H3:** GHA Windows userspace UDP shim collapses (sustain **0**); published H3 row is the laptop `quic-http3` remasure under the same delay/loss workload ([Performance Local Lab](Performance-Local-Lab#lossy--high-rtt-h2-hol--h3-packet-loss), `windows-20260822-lossy-h3-quic/`).

| Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---|---:|---:|---:|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | **658**<br><sub>(104 MiB / 4.8% CPU)</sub> | **663**<br><sub>(104 MiB / 4.8% CPU)</sub> | **636**<br><sub>(137 MiB / 18% CPU)</sub> | **636**<br><sub>(137 MiB / 18% CPU)</sub> | 🥇 **662**<br><sub>(120 MiB / 4.8% CPU)</sub> | **662**<br><sub>(120 MiB / 4.8% CPU)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **60**<br><sub>(122 MiB / 1.9% CPU)</sub> | **85**<br><sub>(122 MiB / 1.9% CPU)</sub> | **16**<br><sub>(141 MiB / 1% CPU)</sub> | **16**<br><sub>(141 MiB / 1% CPU)</sub> | **17**<br><sub>(85 MiB / 6.4% CPU)</sub> | **18**<br><sub>(85 MiB / 6.4% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **1,572** | **1,572** | *Not possible* (no QUIC) | *Not possible* | **0** | **50** |

TWP H2 HOL leads (~**4.74×** YARP). H3 is the protocol-shape win vs H2 HOL on the same lossy session; Win H3 GHA remains 0 (laptop remasure kept above).

### Linux — lossy / high-RTT (H2 HOL / H3 loss)

Median of **3** repeats @ `70b5ca33`. Source: [32756417744](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32756417744) (`compare-lossy`; lossy H3 uses `quic-http3`).

| Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---|---:|---:|---:|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | **1,210**<br><sub>(140 MiB / 9.7% CPU)</sub> | **1,210**<br><sub>(140 MiB / 9.7% CPU)</sub> | 🥇 **1,214**<br><sub>(98 MiB / 5% CPU)</sub> | **1,214**<br><sub>(98 MiB / 5% CPU)</sub> | **1,206**<br><sub>(146 MiB / 13.2% CPU)</sub> | **1,206**<br><sub>(146 MiB / 13.2% CPU)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **316**<br><sub>(182 MiB / 5.4% CPU)</sub> | **316**<br><sub>(182 MiB / 5.4% CPU)</sub> | **40**<br><sub>(97 MiB / 0.2% CPU)</sub> | **40**<br><sub>(97 MiB / 0.2% CPU)</sub> | **40**<br><sub>(117 MiB / 1.2% CPU)</sub> | **40**<br><sub>(117 MiB / 1.2% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | **316**<br><sub>(146 MiB / 9.7% CPU)</sub> | **316**<br><sub>(146 MiB / 9.7% CPU)</sub> | **96**<br><sub>(110 MiB / 1.6% CPU)</sub> | **96**<br><sub>(110 MiB / 1.6% CPU)</sub> | 🥇 **349**<br><sub>(182 MiB / 16.5% CPU)</sub> | **349**<br><sub>(182 MiB / 16.5% CPU)</sub> |

TWP H2 HOL ≫ YARP (~**7.9×**). H3 TWP÷YARP ≈ **0.9×**.

### Architecture-sensitive

`compare-arch` isolates slow app readers, origin-early response, H2 duplex, and WebSocket echo. See [TWP vs YARP IO model](Performance-Profiling#twp-vs-yarp-io-model). Laptop 1-rep numbers are on [Performance Local Lab](Performance-Local-Lab#architecture-sensitive).

Median of **3** repeats on matched 4 vCPU / 16 GiB runners @ `70b5ca33` ([32756423650](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32756423650)) (`compare-arch`). Slow consumer = 256 KiB GET, 16 KiB read + 8 ms sleep. Early response = 64 KiB POST, origin writes after 8 KiB. Duplex H2 = overlapping 64 KiB POST on H2 TLS↔H2 TLS. WebSocket = echo round-trips/sec.

`compare-lossy` (slow **network**) is already published above; it is not a slow **app** reader.

#### Windows

| Scenario | Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---|---|---:|---:|---:|---:|---:|---:|
| Slow consumer (256 KiB GET, throttled client read) | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **248**<br><sub>(90 MiB / 5.7% CPU)</sub> | **248**<br><sub>(90 MiB / 5.7% CPU)</sub> | **214**<br><sub>(142 MiB / 24.6% CPU)</sub> | **214**<br><sub>(142 MiB / 24.6% CPU)</sub> | **248**<br><sub>(107 MiB / 4.8% CPU)</sub> | **248**<br><sub>(107 MiB / 4.8% CPU)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **256**<br><sub>(115 MiB / 6% CPU)</sub> | **256**<br><sub>(115 MiB / 6% CPU)</sub> | **184**<br><sub>(140 MiB / 24.6% CPU)</sub> | **184**<br><sub>(140 MiB / 24.6% CPU)</sub> | **256**<br><sub>(110 MiB / 7.4% CPU)</sub> | **256**<br><sub>(110 MiB / 7.4% CPU)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **265**<br><sub>(92 MiB / 19.6% CPU)</sub> | **265**<br><sub>(92 MiB / 19.6% CPU)</sub> | *Not possible* (no QUIC) | *Not possible* (no QUIC) | **264**<br><sub>(166 MiB / 20.9% CPU)</sub> | **264**<br><sub>(166 MiB / 20.9% CPU)</sub> |
| Early response (origin writes after first request chunk) | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **5,710**<br><sub>(80 MiB / 46% CPU)</sub> | **5,863**<br><sub>(80 MiB / 46% CPU)</sub> | **331**<br><sub>(136 MiB / 24.7% CPU)</sub> | **368**<br><sub>(136 MiB / 24.7% CPU)</sub> | **4,206**<br><sub>(117 MiB / 56% CPU)</sub> | **4,300**<br><sub>(117 MiB / 56% CPU)</sub> |
| Early response (origin writes after first request chunk) | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **3,926**<br><sub>(176 MiB / 48.8% CPU)</sub> | **3,926**<br><sub>(176 MiB / 48.8% CPU)</sub> | **0**<br><sub>(137 MiB / 24.5% CPU)</sub> | **343**<br><sub>(137 MiB / 24.5% CPU)</sub> | **3,137**<br><sub>(116 MiB / 52.9% CPU)</sub> | **3,288**<br><sub>(116 MiB / 52.9% CPU)</sub> |
| Early response (origin writes after first request chunk) | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **2,043**<br><sub>(114 MiB / 43.5% CPU)</sub> | **2,122**<br><sub>(114 MiB / 43.5% CPU)</sub> | *Not possible* (no QUIC) | *Not possible* (no QUIC) | **1,822**<br><sub>(153 MiB / 54.8% CPU)</sub> | **1,872**<br><sub>(153 MiB / 54.8% CPU)</sub> |
| Duplex (both directions live) | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **82**<br><sub>(111 MiB / 10.4% CPU)</sub> | **636**<br><sub>(111 MiB / 10.4% CPU)</sub> | *Not possible* | *Not possible* | **24**<br><sub>(115 MiB / 42.4% CPU)</sub> | **2,375**<br><sub>(115 MiB / 42.4% CPU)</sub> |
| Duplex (WebSocket / extended CONNECT) | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **24,609**<br><sub>(94 MiB / 43.9% CPU)</sub> | **24,609**<br><sub>(94 MiB / 43.9% CPU)</sub> | **12,648**<br><sub>(137 MiB / 24.8% CPU)</sub> | **12,725**<br><sub>(137 MiB / 24.8% CPU)</sub> | **22,845**<br><sub>(87 MiB / 43.8% CPU)</sub> | **22,845**<br><sub>(87 MiB / 43.8% CPU)</sub> |

#### Linux

| Scenario | Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---|---|---:|---:|---:|---:|---:|---:|
| Slow consumer (256 KiB GET, throttled client read) | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **471**<br><sub>(115 MiB / 9.7% CPU)</sub> | **471**<br><sub>(115 MiB / 9.7% CPU)</sub> | **468**<br><sub>(97 MiB / 6.1% CPU)</sub> | **468**<br><sub>(97 MiB / 6.1% CPU)</sub> | **422**<br><sub>(145 MiB / 14.5% CPU)</sub> | **422**<br><sub>(145 MiB / 14.5% CPU)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · TLS | HTTP/1 · plain | **474**<br><sub>(158 MiB / 19.4% CPU)</sub> | **474**<br><sub>(158 MiB / 19.4% CPU)</sub> | 🥇 **477**<br><sub>(108 MiB / 13% CPU)</sub> | **477**<br><sub>(108 MiB / 13% CPU)</sub> | **474**<br><sub>(145 MiB / 24.4% CPU)</sub> | **474**<br><sub>(145 MiB / 24.4% CPU)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **471**<br><sub>(127 MiB / 33.7% CPU)</sub> | **471**<br><sub>(127 MiB / 33.7% CPU)</sub> | **120**<br><sub>(127 MiB / 23.6% CPU)</sub> | **417**<br><sub>(127 MiB / 23.6% CPU)</sub> | **470**<br><sub>(189 MiB / 39.7% CPU)</sub> | **470**<br><sub>(189 MiB / 39.7% CPU)</sub> |
| Early response (origin writes after first request chunk) | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **4,680**<br><sub>(138 MiB / 47.5% CPU)</sub> | **4,680**<br><sub>(138 MiB / 47.5% CPU)</sub> | **4,048**<br><sub>(95 MiB / 50.6% CPU)</sub> | **4,048**<br><sub>(95 MiB / 50.6% CPU)</sub> | **3,176**<br><sub>(174 MiB / 56.4% CPU)</sub> | **3,176**<br><sub>(174 MiB / 56.4% CPU)</sub> |
| Early response (origin writes after first request chunk) | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **3,302**<br><sub>(234 MiB / 48.7% CPU)</sub> | **3,302**<br><sub>(234 MiB / 48.7% CPU)</sub> | **0**<br><sub>(98 MiB / 22.2% CPU)</sub> | **2,016**<br><sub>(98 MiB / 22.2% CPU)</sub> | **2,237**<br><sub>(144 MiB / 47.8% CPU)</sub> | **2,353**<br><sub>(144 MiB / 47.8% CPU)</sub> |
| Early response (origin writes after first request chunk) | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **2,830**<br><sub>(193 MiB / 45% CPU)</sub> | **2,830**<br><sub>(193 MiB / 45% CPU)</sub> | **0**<br><sub>(108 MiB / 24% CPU)</sub> | **724**<br><sub>(108 MiB / 24% CPU)</sub> | **2,086**<br><sub>(249 MiB / 48.6% CPU)</sub> | **2,086**<br><sub>(249 MiB / 48.6% CPU)</sub> |
| Duplex (both directions live) | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **379**<br><sub>(142 MiB / 8.2% CPU)</sub> | **379**<br><sub>(142 MiB / 8.2% CPU)</sub> | *Not possible* | *Not possible* | **20**<br><sub>(142 MiB / 44.7% CPU)</sub> | **1,718**<br><sub>(142 MiB / 44.7% CPU)</sub> |
| Duplex (WebSocket / extended CONNECT) | HTTP/1 · TLS | HTTP/1 · plain | **29,302**<br><sub>(122 MiB / 43.7% CPU)</sub> | **29,302**<br><sub>(122 MiB / 43.7% CPU)</sub> | 🥇 **33,756**<br><sub>(96 MiB / 36% CPU)</sub> | **33,756**<br><sub>(96 MiB / 36% CPU)</sub> | **27,357**<br><sub>(121 MiB / 43.9% CPU)</sub> | **27,357**<br><sub>(121 MiB / 43.9% CPU)</sub> |

Slow consumer is sleep-bound; H1/H2/H3 sit in the same band. Early-response H1/H2/H3: TWP leads (H1 early ≈ **1.36×** / **1.47×** YARP Win/Linux). **Duplex H2**: YARP leads by design — Win ≈ **0.27×** (636 / 2,375), Linux ≈ **0.22×** (379 / 1,718); irreducible concurrent-copier cell (see [IO model](Performance-Profiling#twp-vs-yarp-io-model)). WebSocket: TWP÷YARP Windows ≈ **1.08×**; Linux nginx leads.

### TLS termination cost (H1 TLS → cleartext origin)

Isolates keep-alive tiny GET vs **new connection per request** (handshake-dominated) vs keep-alive **256 KiB**. Product comparison uses RPS and end-to-end latency; TWP can also capture `ClientTlsTiming` when `TWP_RPS_CAPTURE_TLS=1` (child process) — nginx/YARP have no equivalent hook.

#### Windows

Median of **3** repeats on `windows-latest` @ `70b5ca33`. Source: Actions [32756420636](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32756420636) (`compare-tls-cost`). Absolute RPS on GHA swings hard; prefer **TWP÷YARP**.

| Workload | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---:|---:|---:|---:|---:|---:|
| Keep-alive · tiny GET | 🥇 **33,778**<br><sub>(89 MiB / 47.1% CPU)</sub> | **33,778**<br><sub>(89 MiB / 47.1% CPU)</sub> | **18,813**<br><sub>(137 MiB / 24.9% CPU)</sub> | **19,612**<br><sub>(137 MiB / 24.9% CPU)</sub> | **30,209**<br><sub>(100 MiB / 49% CPU)</sub> | **30,209**<br><sub>(100 MiB / 49% CPU)</sub> |
| New-connection · tiny GET | 🥇 **940**<br><sub>(70 MiB / 9.1% CPU)</sub> | **940**<br><sub>(70 MiB / 9.1% CPU)</sub> | **318**<br><sub>(136 MiB / 24.3% CPU)</sub> | **326**<br><sub>(136 MiB / 24.3% CPU)</sub> | **907**<br><sub>(97 MiB / 10.7% CPU)</sub> | **910**<br><sub>(97 MiB / 10.7% CPU)</sub> |
| Keep-alive · 256 KiB GET | 🥇 **3,664**<br><sub>(102 MiB / 39.8% CPU)</sub> | **3,762**<br><sub>(102 MiB / 39.8% CPU)</sub> | **288**<br><sub>(136 MiB / 24.7% CPU)</sub> | **320**<br><sub>(136 MiB / 24.7% CPU)</sub> | **3,363**<br><sub>(116 MiB / 40.9% CPU)</sub> | **3,428**<br><sub>(116 MiB / 40.9% CPU)</sub> |

#### Linux

Median of **3** repeats @ `70b5ca33`. Source: Actions [32756420636](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32756420636) (`compare-tls-cost`).

| Workload | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---:|---:|---:|---:|---:|---:|
| Keep-alive · tiny GET | **23,141**<br><sub>(97 MiB / 49.6% CPU)</sub> | **23,141**<br><sub>(97 MiB / 49.6% CPU)</sub> | 🥇 **26,968**<br><sub>(97 MiB / 41.6% CPU)</sub> | **26,968**<br><sub>(97 MiB / 41.6% CPU)</sub> | **20,448**<br><sub>(131 MiB / 50.3% CPU)</sub> | **20,448**<br><sub>(131 MiB / 50.3% CPU)</sub> |
| New-connection · tiny GET | **958**<br><sub>(105 MiB / 47.2% CPU)</sub> | **972**<br><sub>(105 MiB / 47.2% CPU)</sub> | 🥇 **987**<br><sub>(97 MiB / 44.1% CPU)</sub> | **987**<br><sub>(97 MiB / 44.1% CPU)</sub> | **954**<br><sub>(147 MiB / 46% CPU)</sub> | **954**<br><sub>(147 MiB / 46% CPU)</sub> |
| Keep-alive · 256 KiB GET | 🥇 **2,708**<br><sub>(124 MiB / 36.6% CPU)</sub> | **2,708**<br><sub>(124 MiB / 36.6% CPU)</sub> | **2,517**<br><sub>(96 MiB / 36% CPU)</sub> | **2,543**<br><sub>(96 MiB / 36% CPU)</sub> | **2,110**<br><sub>(167 MiB / 45.6% CPU)</sub> | **2,110**<br><sub>(167 MiB / 45.6% CPU)</sub> |

All three workloads are **>1.00×** YARP on both OS. nginx leads Linux keep-alive tiny and Linux new-connection; TWP is second, YARP third.

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
