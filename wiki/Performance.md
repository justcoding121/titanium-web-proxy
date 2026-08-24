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

Calibration for the shared 4 vCPU loopback shape: how close client + origin are to saturated before ranking reverse peers. Tiny keep-alive GET. Median of **3** repeats @ `f9769503` — [32737672381](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32737672381). Warmup 2s / measure 8s; concurrency 8, 16, 32, 64. Block A **% of origin-HttpClient** uses median **peak** RPS. Blocks B/C use peer÷YARP / ÷nginx on median peak (not % of H1 origin). **RPS cells** embed median RSS / CPU for the **proxy child** plus its **full descendant tree** (serve-proxy → nginx master → workers); origin-direct samples the **origin** child. Product matrices below use matched `dotnet-httpclient` only (not bombardier). **H3→H1** after inbound H3 `Http2PendingWork` (unroot completed stream tasks): Win Block C TWP RSS **112** MiB vs YARP **141** (~**0.79×**); Linux **140** vs **182** (~**0.77×**). RPS still leads YARP (**1.12×** / **1.07×**).

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
| origin-direct | dotnet-httpclient | **75,458**<br><sub>(53 MiB / 42.1% CPU)</sub> | **75,697**<br><sub>(53 MiB / 42.1% CPU)</sub> | **100%** |
| origin-direct-bombardier | bombardier | **57,354**<br><sub>(54 MiB / 30.4% CPU)</sub> | **57,354**<br><sub>(54 MiB / 30.4% CPU)</sub> | **75.8%** |
| bare-reverse-http1 | dotnet-httpclient | **37,781**<br><sub>(55 MiB / 44.5% CPU)</sub> | **37,781**<br><sub>(55 MiB / 44.5% CPU)</sub> | **49.9%** |
| nginx-reverse-http1 | dotnet-httpclient | **27,698**<br><sub>(120 MiB / 24.8% CPU)</sub> | **28,702**<br><sub>(120 MiB / 24.8% CPU)</sub> | **37.9%** |
| yarp-reverse-http1 | dotnet-httpclient | **33,984**<br><sub>(87 MiB / 47.8% CPU)</sub> | **33,984**<br><sub>(87 MiB / 47.8% CPU)</sub> | **44.9%** |
| twp-reverse-http1 | dotnet-httpclient | 🥇 **37,305**<br><sub>(73 MiB / 48.1% CPU)</sub> | **37,305**<br><sub>(73 MiB / 48.1% CPU)</sub> | **49.3%** |

**Linux** (`ubuntu-latest`)

| Arm | Generator | Sustain | Peak | % of origin-HttpClient |
|---|---|---:|---:|---:|
| origin-direct | dotnet-httpclient | **80,190**<br><sub>(78 MiB / 43.7% CPU)</sub> | **80,190**<br><sub>(78 MiB / 43.7% CPU)</sub> | **100%** |
| origin-direct-bombardier | bombardier | **47,967**<br><sub>(79 MiB / 37.4% CPU)</sub> | **47,967**<br><sub>(79 MiB / 37.4% CPU)</sub> | **59.8%** |
| bare-reverse-http1 | dotnet-httpclient | **35,466**<br><sub>(66 MiB / 45.8% CPU)</sub> | **35,466**<br><sub>(66 MiB / 45.8% CPU)</sub> | **44.2%** |
| nginx-reverse-http1 | dotnet-httpclient | 🥇 **43,639**<br><sub>(71 MiB / 40.6% CPU)</sub> | **43,639**<br><sub>(71 MiB / 40.6% CPU)</sub> | **54.4%** |
| yarp-reverse-http1 | dotnet-httpclient | **31,718**<br><sub>(116 MiB / 48.6% CPU)</sub> | **31,718**<br><sub>(116 MiB / 48.6% CPU)</sub> | **39.6%** |
| twp-reverse-http1 | dotnet-httpclient | **36,725**<br><sub>(85 MiB / 49.7% CPU)</sub> | **36,725**<br><sub>(85 MiB / 49.7% CPU)</sub> | **45.8%** |

Reverse peers are about **38–54%** of the origin-direct HttpClient peak on this runner class. Prefer the **%** column over absolute RPS across runs. Bare and origin-direct are controls (not medal peers).

#### Block B — H2 TLS→H1

Peer ratios (÷YARP / ÷nginx) on median peak; **RPS cells** embed `(MiB / CPU%)`.

**Windows** (`windows-latest`)

| Arm | Generator | Sustain | Peak | ÷YARP | ÷nginx |
|---|---|---:|---:|---:|---:|
| nginx-reverse-http2 | dotnet-httpclient | **22,708**<br><sub>(137 MiB / 23.6% CPU)</sub> | **22,708**<br><sub>(137 MiB / 23.6% CPU)</sub> | **0.53×** | **1.00×** |
| yarp-reverse-http2 | dotnet-httpclient | **43,028**<br><sub>(92 MiB / 50.3% CPU)</sub> | **43,028**<br><sub>(92 MiB / 50.3% CPU)</sub> | **1.00×** | **1.89×** |
| twp-reverse-http2-cleartext | dotnet-httpclient | 🥇 **45,107**<br><sub>(101 MiB / 50.7% CPU)</sub> | **45,107**<br><sub>(101 MiB / 50.7% CPU)</sub> | **1.05×** | **1.99×** |

**Linux** (`ubuntu-latest`)

| Arm | Generator | Sustain | Peak | ÷YARP | ÷nginx |
|---|---|---:|---:|---:|---:|
| nginx-reverse-http2 | dotnet-httpclient | **16,510**<br><sub>(95 MiB / 22.7% CPU)</sub> | **22,691**<br><sub>(95 MiB / 22.7% CPU)</sub> | **0.67×** | **1.00×** |
| yarp-reverse-http2 | dotnet-httpclient | **34,054**<br><sub>(120 MiB / 48.3% CPU)</sub> | **34,054**<br><sub>(120 MiB / 48.3% CPU)</sub> | **1.00×** | **1.50×** |
| twp-reverse-http2-cleartext | dotnet-httpclient | 🥇 **38,619**<br><sub>(118 MiB / 51.8% CPU)</sub> | **38,619**<br><sub>(118 MiB / 51.8% CPU)</sub> | **1.13×** | **1.70×** |

#### Block C — H3→H1

Same layout as Block B. Requires QuicListener; nginx only with `http_v3_module` (Windows nginx has no QUIC).

**Windows** (`windows-latest`)

| Arm | Generator | Sustain | Peak | ÷YARP | ÷nginx |
|---|---|---:|---:|---:|---:|
| nginx-reverse-http3-cleartext | dotnet-httpclient | *Not possible* (no QUIC) | *Not possible* | — | — |
| yarp-reverse-http3-cleartext | dotnet-httpclient | **20,754**<br><sub>(141 MiB / 48.1% CPU)</sub> | **20,754**<br><sub>(141 MiB / 48.1% CPU)</sub> | **1.00×** | — |
| twp-reverse-http3-cleartext | dotnet-httpclient | 🥇 **23,323**<br><sub>(112 MiB / 41.7% CPU)</sub> | **23,323**<br><sub>(112 MiB / 41.7% CPU)</sub> | **1.12×** | — |

**Linux** (`ubuntu-latest`)

| Arm | Generator | Sustain | Peak | ÷YARP | ÷nginx |
|---|---|---:|---:|---:|---:|
| nginx-reverse-http3-cleartext | dotnet-httpclient | **0**<br><sub>(104 MiB / 21.9% CPU)</sub> | **18,938**<br><sub>(104 MiB / 21.9% CPU)</sub> | **0.87×** | **1.00×** |
| yarp-reverse-http3-cleartext | dotnet-httpclient | **21,690**<br><sub>(182 MiB / 48.8% CPU)</sub> | **21,690**<br><sub>(182 MiB / 48.8% CPU)</sub> | **1.00×** | **1.15×** |
| twp-reverse-http3-cleartext | dotnet-httpclient | 🥇 **23,118**<br><sub>(140 MiB / 51.4% CPU)</sub> | **23,118**<br><sub>(140 MiB / 51.4% CPU)</sub> | **1.07×** | **1.22×** |

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

Median of **3 repeats** on `windows-latest` (4 vCPU / 16 GiB). Saturation @ `f9769503` — [32737672381](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32737672381); bridges [32737668291](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32737668291). Three-process harness, parent-seeded loopback CA. Warmup 2s / measure 8s; concurrency 8, 16, 32, 64. Prefer TWP÷peer ratios over absolute RPS. **RPS cells** include median RSS / CPU at the peak-RPS step as `<br><sub>(MiB / CPU%)</sub>` for TWP, nginx, and YARP (proxy child + descendant tree). Bridge / H3→H1 Memory after inbound H3 **`Http2PendingWork`** + prior **`ClientSyntheticStreams`**. H1 plain / terminate / MITM rows below are still the prior paste (no `compare-same` / `compare-mitm` this pass). Laptop High-perf / cool-paired numbers stay on the [local lab](Performance-Local-Lab).

**Load generators:** Reverse inbound H3 arms use **`dotnet-httpclient`** (`http_version=3.0`, `RequestVersionExact`). nginx/Windows is same-OS only (no QUIC).

| Mode | Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---|---|---:|---:|---:|---:|---:|---:|
| Reverse | HTTP/1 · plain | HTTP/1 · plain | 🥇 **25,525**<br><sub>(76 MiB / 52.9% CPU)</sub> | **25,525**<br><sub>(76 MiB / 52.9% CPU)</sub> | **13,325**<br><sub>(120 MiB / 24.8% CPU)</sub> | **13,325**<br><sub>(120 MiB / 24.8% CPU)</sub> | **21,341**<br><sub>(88 MiB / 49.6% CPU)</sub> | **21,341**<br><sub>(88 MiB / 49.6% CPU)</sub> |
| Reverse | HTTP/1 · plain | HTTP/1 · TLS | 🥇 **21,069**<br><sub>(84 MiB / 52% CPU)</sub> | **21,069**<br><sub>(84 MiB / 52% CPU)</sub> | *Not possible* | *Not possible* | **19,498**<br><sub>(100 MiB / 52.2% CPU)</sub> | **19,498**<br><sub>(100 MiB / 52.2% CPU)</sub> |
| Reverse | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **20,286**<br><sub>(82 MiB / 50.5% CPU)</sub> | **20,286**<br><sub>(82 MiB / 50.5% CPU)</sub> | **8,980**<br><sub>(137 MiB / 24.9% CPU)</sub> | **8,980**<br><sub>(137 MiB / 24.9% CPU)</sub> | **17,956**<br><sub>(103 MiB / 50.6% CPU)</sub> | **17,956**<br><sub>(103 MiB / 50.6% CPU)</sub> |
| Reverse | HTTP/1 · TLS | HTTP/2 · TLS | 🥇 **41,099**<br><sub>(121 MiB / 48.2% CPU)</sub> | **41,099**<br><sub>(121 MiB / 48.2% CPU)</sub> | *Not possible* | *Not possible* | **40,363**<br><sub>(108 MiB / 48% CPU)</sub> | **40,363**<br><sub>(108 MiB / 48% CPU)</sub> |
| Reverse | HTTP/1 · TLS | HTTP/3 · QUIC | **24,579**<br><sub>(113 MiB / 50.9% CPU)</sub> | **24,579**<br><sub>(113 MiB / 50.9% CPU)</sub> | *Not possible* (no QUIC) | *Not possible* (no QUIC) | 🥇 **24,610**<br><sub>(127 MiB / 51.2% CPU)</sub> | **24,610**<br><sub>(127 MiB / 51.2% CPU)</sub> |
| Reverse | HTTP/2 · plain | HTTP/1 · plain | 🥇 **54,965**<br><sub>(92 MiB / 51.3% CPU)</sub> | **54,965**<br><sub>(92 MiB / 51.3% CPU)</sub> | *Not possible* | *Not possible* | **51,379**<br><sub>(84 MiB / 49.3% CPU)</sub> | **51,379**<br><sub>(84 MiB / 49.3% CPU)</sub> |
| Reverse | HTTP/2 · plain | HTTP/2 · plain | 🥇 **123,474**<br><sub>(72 MiB / 34.8% CPU)</sub> | **123,474**<br><sub>(72 MiB / 34.8% CPU)</sub> | *Not possible* | *Not possible* | **90,820**<br><sub>(95 MiB / 50.2% CPU)</sub> | **90,820**<br><sub>(95 MiB / 50.2% CPU)</sub> |
| Reverse | HTTP/2 · plain | HTTP/2 · TLS | 🥇 **73,711**<br><sub>(81 MiB / 35.1% CPU)</sub> | **73,711**<br><sub>(81 MiB / 35.1% CPU)</sub> | *Not possible* | *Not possible* | **55,888**<br><sub>(97 MiB / 46.5% CPU)</sub> | **55,888**<br><sub>(97 MiB / 46.5% CPU)</sub> |
| Reverse | HTTP/2 · plain | HTTP/3 · QUIC | 🥇 **43,005**<br><sub>(142 MiB / 52.9% CPU)</sub> | **43,005**<br><sub>(142 MiB / 52.9% CPU)</sub> | *Not possible* (no QUIC) | *Not possible* (no QUIC) | **41,493**<br><sub>(130 MiB / 50.6% CPU)</sub> | **41,493**<br><sub>(130 MiB / 50.6% CPU)</sub> |
| Reverse | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **52,700**<br><sub>(100 MiB / 52.7% CPU)</sub> | **52,700**<br><sub>(100 MiB / 52.7% CPU)</sub> | **22,708**<br><sub>(137 MiB / 23.6% CPU)</sub> | **22,708**<br><sub>(137 MiB / 23.6% CPU)</sub> | **46,315**<br><sub>(91 MiB / 50.9% CPU)</sub> | **46,315**<br><sub>(91 MiB / 50.9% CPU)</sub> |
| Reverse | HTTP/2 · TLS | HTTP/2 · plain | 🥇 **113,697**<br><sub>(91 MiB / 36.8% CPU)</sub> | **113,697**<br><sub>(91 MiB / 36.8% CPU)</sub> | *Not possible* | *Not possible* | **78,136**<br><sub>(101 MiB / 52.1% CPU)</sub> | **78,136**<br><sub>(101 MiB / 52.1% CPU)</sub> |
| Reverse | HTTP/2 · TLS | HTTP/3 · QUIC | 🥇 **42,415**<br><sub>(142 MiB / 54.5% CPU)</sub> | **42,415**<br><sub>(142 MiB / 54.5% CPU)</sub> | *Not possible* (no QUIC) | *Not possible* (no QUIC) | **37,948**<br><sub>(128 MiB / 52% CPU)</sub> | **37,948**<br><sub>(128 MiB / 52% CPU)</sub> |
| Reverse | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **25,956**<br><sub>(114 MiB / 45.6% CPU)</sub> | **25,956**<br><sub>(114 MiB / 45.6% CPU)</sub> | *Not possible* (no QUIC) | *Not possible* (no QUIC) | **25,323**<br><sub>(166 MiB / 49.3% CPU)</sub> | **25,323**<br><sub>(166 MiB / 49.3% CPU)</sub> |
| Reverse | HTTP/3 · QUIC | HTTP/2 · TLS | 🥇 **35,712**<br><sub>(128 MiB / 47.8% CPU)</sub> | **35,712**<br><sub>(128 MiB / 47.8% CPU)</sub> | *Not possible* (no QUIC) | *Not possible* (no QUIC) | **32,975**<br><sub>(180 MiB / 48.3% CPU)</sub> | **32,975**<br><sub>(180 MiB / 48.3% CPU)</sub> |
| Reverse | HTTP/3 · QUIC | HTTP/3 · QUIC | 🥇 **14,981**<br><sub>(173 MiB / 47.7% CPU)</sub> | **14,981**<br><sub>(173 MiB / 47.7% CPU)</sub> | *Not possible* (no QUIC) | *Not possible* (no QUIC) | **11,620**<br><sub>(165 MiB / 51.7% CPU)</sub> | **11,620**<br><sub>(165 MiB / 51.7% CPU)</sub> |
| MITM | HTTP/1 · plain | HTTP/1 · plain | 🥇 **33,785**<br><sub>(78 MiB / 51.9% CPU)</sub> | **33,785**<br><sub>(78 MiB / 51.9% CPU)</sub> | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/1 · plain | HTTP/1 · TLS | 🥇 **30,763**<br><sub>(97 MiB / 51.2% CPU)</sub> | **30,763**<br><sub>(97 MiB / 51.2% CPU)</sub> | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **30,785**<br><sub>(89 MiB / 49.6% CPU)</sub> | **30,785**<br><sub>(89 MiB / 49.6% CPU)</sub> | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/1 · TLS | HTTP/2 · TLS | 🥇 **37,519**<br><sub>(114 MiB / 49.5% CPU)</sub> | **37,519**<br><sub>(114 MiB / 49.5% CPU)</sub> | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/1 · TLS | HTTP/3 · QUIC | 🥇 **20,160**<br><sub>(114 MiB / 52% CPU)</sub> | **20,160**<br><sub>(114 MiB / 52% CPU)</sub> | *Not possible* (no QUIC) | *Not possible* (no QUIC) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 · plain | HTTP/1 · plain | 🥇 **45,273**<br><sub>(190 MiB / 50.7% CPU)</sub> | **45,273**<br><sub>(190 MiB / 50.7% CPU)</sub> | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 · plain | HTTP/2 · plain | 🥇 **91,446**<br><sub>(73 MiB / 35.5% CPU)</sub> | **91,446**<br><sub>(73 MiB / 35.5% CPU)</sub> | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 · plain | HTTP/2 · TLS | 🥇 **82,967**<br><sub>(87 MiB / 37.1% CPU)</sub> | **82,967**<br><sub>(87 MiB / 37.1% CPU)</sub> | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 · plain | HTTP/3 · QUIC | 🥇 **33,186**<br><sub>(191 MiB / 54% CPU)</sub> | **33,186**<br><sub>(191 MiB / 54% CPU)</sub> | *Not possible* (no QUIC) | *Not possible* (no QUIC) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **43,911**<br><sub>(182 MiB / 54.6% CPU)</sub> | **43,911**<br><sub>(182 MiB / 54.6% CPU)</sub> | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 · TLS | HTTP/2 · plain | 🥇 **86,099**<br><sub>(93 MiB / 39% CPU)</sub> | **86,099**<br><sub>(93 MiB / 39% CPU)</sub> | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 · TLS | HTTP/3 · QUIC | 🥇 **32,660**<br><sub>(184 MiB / 53.4% CPU)</sub> | **32,660**<br><sub>(184 MiB / 53.4% CPU)</sub> | *Not possible* (no QUIC) | *Not possible* (no QUIC) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **22,442**<br><sub>(241 MiB / 42.4% CPU)</sub> | **22,442**<br><sub>(241 MiB / 42.4% CPU)</sub> | *Not possible* (no QUIC) | *Not possible* (no QUIC) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/3 · QUIC | HTTP/2 · TLS | 🥇 **27,656**<br><sub>(218 MiB / 47.9% CPU)</sub> | **27,656**<br><sub>(218 MiB / 47.9% CPU)</sub> | *Not possible* (no QUIC) | *Not possible* (no QUIC) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/3 · QUIC | HTTP/3 · QUIC | 🥇 **16,808**<br><sub>(215 MiB / 47.2% CPU)</sub> | **16,808**<br><sub>(215 MiB / 47.2% CPU)</sub> | *Not possible* (no QUIC) | *Not possible* (no QUIC) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/1 · plain (CONNECT) | HTTP/1 · TLS | 🥇 **27,351**<br><sub>(105 MiB / 50.6% CPU)</sub> | **27,351**<br><sub>(105 MiB / 50.6% CPU)</sub> | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/1 · TLS | HTTP/1 · TLS | 🥇 **28,338**<br><sub>(83 MiB / 48.4% CPU)</sub> | **28,338**<br><sub>(83 MiB / 48.4% CPU)</sub> | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **76,456**<br><sub>(89 MiB / 37.1% CPU)</sub> | **76,456**<br><sub>(89 MiB / 37.1% CPU)</sub> | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 · TLS | HTTP/1 · TLS | 🥇 **39,157**<br><sub>(167 MiB / 53.3% CPU)</sub> | **39,157**<br><sub>(167 MiB / 53.3% CPU)</sub> | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **20,318**<br><sub>(243 MiB / 41.8% CPU)</sub> | **20,318**<br><sub>(243 MiB / 41.8% CPU)</sub> | *Not possible* (no QUIC) | *Not possible* (no QUIC) | *Not possible* (no MITM) | *Not possible* (no MITM) |

TWP÷YARP bridges @ `f9769503`: H3→H1 ≈ **1.02×** RPS / **0.69×** Memory (114 / 166 MiB); H3→H2 ≈ **1.08×** / **0.71×**; H2 TLS→H1 ≈ **1.14×** / **1.10×** Memory; H1→H2 ≈ **1.02×** / **1.12×** Memory. H1→H3 RPS ~tie (**1.00×**) with Memory win (**0.89×**). Prefer ratios over absolute RPS on GHA VMs. H1 plain / terminate / MITM rows are prior paste (no `compare-same` / `compare-mitm` this pass). nginx/YARP cannot MITM.

## Linux — Titanium vs nginx vs YARP

Median of **3 repeats** on `ubuntu-latest` (4 vCPU / 16 GiB). Saturation @ `f9769503` ([32737672381](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32737672381)); bridges [32737668291](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32737668291). Warmup 2s / measure 8s; concurrency 8, 16, 32, 64. **Linux nginx is the authoritative nginx baseline.** The RPS workflow installs nginx.org mainline (`http_v3_module`) and `libmsquic` (`QuicListener.IsSupported=true` on `ubuntu-latest`). Prefer ratios over absolute RPS. **RPS cells** include peer `(MiB / CPU%)` as on Windows. Bridge / H3→H1 after inbound H3 **`Http2PendingWork`**. H1 plain / terminate / MITM rows prior paste (no `compare-same` / `compare-mitm` this pass).

TWP÷YARP bridges: H3→H1 ≈ **1.07×** RPS / **0.80×** Memory (146 / 183 MiB); H3→H2 ≈ **1.06×** / **0.75×**; H2 TLS→H1 ≈ **1.10×** / **0.98×**; H1→H2 ≈ **1.03×** / **1.08×** Memory. H1 plain ÷nginx from prior `compare-same` ≈ **0.81**.

| Mode | Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---|---|---:|---:|---:|---:|---:|---:|
| Reverse | HTTP/1 · plain | HTTP/1 · plain | **46,284**<br><sub>(82 MiB / 51.1% CPU)</sub> | **46,284**<br><sub>(82 MiB / 51.1% CPU)</sub> | 🥇 **55,862**<br><sub>(72 MiB / 41.2% CPU)</sub> | **55,862**<br><sub>(72 MiB / 41.2% CPU)</sub> | **41,203**<br><sub>(114 MiB / 49.4% CPU)</sub> | **41,203**<br><sub>(114 MiB / 49.4% CPU)</sub> |
| Reverse | HTTP/1 · plain | HTTP/1 · TLS | 🥇 **37,194**<br><sub>(103 MiB / 48.6% CPU)</sub> | **37,194**<br><sub>(103 MiB / 48.6% CPU)</sub> | *Not possible* | *Not possible* | **33,500**<br><sub>(140 MiB / 49.2% CPU)</sub> | **33,500**<br><sub>(140 MiB / 49.2% CPU)</sub> |
| Reverse | HTTP/1 · TLS | HTTP/1 · plain | **36,518**<br><sub>(107 MiB / 49.1% CPU)</sub> | **36,518**<br><sub>(107 MiB / 49.1% CPU)</sub> | 🥇 **43,011**<br><sub>(99 MiB / 41.6% CPU)</sub> | **43,011**<br><sub>(99 MiB / 41.6% CPU)</sub> | **32,495**<br><sub>(135 MiB / 50% CPU)</sub> | **32,495**<br><sub>(135 MiB / 50% CPU)</sub> |
| Reverse | HTTP/1 · TLS | HTTP/2 · TLS | 🥇 **28,343**<br><sub>(156 MiB / 49.8% CPU)</sub> | **28,343**<br><sub>(156 MiB / 49.8% CPU)</sub> | *Not possible* | *Not possible* | **27,456**<br><sub>(144 MiB / 46.9% CPU)</sub> | **27,456**<br><sub>(144 MiB / 46.9% CPU)</sub> |
| Reverse | HTTP/1 · TLS | HTTP/3 · QUIC | 🥇 **21,375**<br><sub>(142 MiB / 52.4% CPU)</sub> | **21,375**<br><sub>(142 MiB / 52.4% CPU)</sub> | *Not possible* (no H3 origin) | *Not possible* (no H3 origin) | **19,843**<br><sub>(161 MiB / 49.6% CPU)</sub> | **19,843**<br><sub>(161 MiB / 49.6% CPU)</sub> |
| Reverse | HTTP/2 · plain | HTTP/1 · plain | 🥇 **41,502**<br><sub>(113 MiB / 52.8% CPU)</sub> | **41,502**<br><sub>(113 MiB / 52.8% CPU)</sub> | *Not possible* | *Not possible* | **39,900**<br><sub>(115 MiB / 48.3% CPU)</sub> | **39,900**<br><sub>(115 MiB / 48.3% CPU)</sub> |
| Reverse | HTTP/2 · plain | HTTP/2 · plain | 🥇 **74,010**<br><sub>(101 MiB / 41.2% CPU)</sub> | **74,010**<br><sub>(101 MiB / 41.2% CPU)</sub> | *Not possible* | *Not possible* | **55,978**<br><sub>(129 MiB / 46.7% CPU)</sub> | **55,978**<br><sub>(129 MiB / 46.7% CPU)</sub> |
| Reverse | HTTP/2 · plain | HTTP/2 · TLS | 🥇 **51,070**<br><sub>(110 MiB / 39.8% CPU)</sub> | **51,070**<br><sub>(110 MiB / 39.8% CPU)</sub> | *Not possible* | *Not possible* | **38,998**<br><sub>(129 MiB / 45.9% CPU)</sub> | **38,998**<br><sub>(129 MiB / 45.9% CPU)</sub> |
| Reverse | HTTP/2 · plain | HTTP/3 · QUIC | 🥇 **29,622**<br><sub>(148 MiB / 50.5% CPU)</sub> | **29,622**<br><sub>(148 MiB / 50.5% CPU)</sub> | *Not possible* (no H3 origin) | *Not possible* (no H3 origin) | **28,768**<br><sub>(151 MiB / 45.9% CPU)</sub> | **28,768**<br><sub>(151 MiB / 45.9% CPU)</sub> |
| Reverse | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **38,014**<br><sub>(118 MiB / 52.2% CPU)</sub> | **38,014**<br><sub>(118 MiB / 52.2% CPU)</sub> | **16,510**<br><sub>(95 MiB / 22.7% CPU)</sub> | **22,691**<br><sub>(95 MiB / 22.7% CPU)</sub> | **34,499**<br><sub>(120 MiB / 48.7% CPU)</sub> | **34,499**<br><sub>(120 MiB / 48.7% CPU)</sub> |
| Reverse | HTTP/2 · TLS | HTTP/2 · plain | 🥇 **68,135**<br><sub>(109 MiB / 40.8% CPU)</sub> | **68,135**<br><sub>(109 MiB / 40.8% CPU)</sub> | *Not possible* | *Not possible* | **44,788**<br><sub>(124 MiB / 46.5% CPU)</sub> | **44,788**<br><sub>(124 MiB / 46.5% CPU)</sub> |
| Reverse | HTTP/2 · TLS | HTTP/3 · QUIC | 🥇 **28,067**<br><sub>(157 MiB / 49.2% CPU)</sub> | **28,067**<br><sub>(157 MiB / 49.2% CPU)</sub> | *Not possible* (no H3 origin) | *Not possible* (no H3 origin) | **25,084**<br><sub>(155 MiB / 46.2% CPU)</sub> | **25,084**<br><sub>(155 MiB / 46.2% CPU)</sub> |
| Reverse | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **23,227**<br><sub>(146 MiB / 51.8% CPU)</sub> | **23,227**<br><sub>(146 MiB / 51.8% CPU)</sub> | **0**<br><sub>(103 MiB / 21.9% CPU)</sub> | **18,730**<br><sub>(103 MiB / 21.9% CPU)</sub> | **21,638**<br><sub>(183 MiB / 49.2% CPU)</sub> | **21,638**<br><sub>(183 MiB / 49.2% CPU)</sub> |
| Reverse | HTTP/3 · QUIC | HTTP/2 · TLS | 🥇 **25,529**<br><sub>(144 MiB / 53.5% CPU)</sub> | **25,529**<br><sub>(144 MiB / 53.5% CPU)</sub> | *Not possible* (no H3→H2) | *Not possible* (no H3→H2) | **24,112**<br><sub>(193 MiB / 47% CPU)</sub> | **24,112**<br><sub>(193 MiB / 47% CPU)</sub> |
| Reverse | HTTP/3 · QUIC | HTTP/3 · QUIC | 🥇 **19,341**<br><sub>(214 MiB / 47.4% CPU)</sub> | **19,341**<br><sub>(214 MiB / 47.4% CPU)</sub> | *Not possible* (no H3 origin) | *Not possible* (no H3 origin) | **15,760**<br><sub>(198 MiB / 47.5% CPU)</sub> | **15,760**<br><sub>(198 MiB / 47.5% CPU)</sub> |
| MITM | HTTP/1 · plain | HTTP/1 · plain | 🥇 **56,421**<br><sub>(85 MiB / 51.3% CPU)</sub> | **56,421**<br><sub>(85 MiB / 51.3% CPU)</sub> | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/1 · plain | HTTP/1 · TLS | 🥇 **46,215**<br><sub>(106 MiB / 47.1% CPU)</sub> | **46,215**<br><sub>(106 MiB / 47.1% CPU)</sub> | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **46,892**<br><sub>(107 MiB / 48.4% CPU)</sub> | **46,892**<br><sub>(107 MiB / 48.4% CPU)</sub> | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/1 · TLS | HTTP/2 · TLS | 🥇 **43,132**<br><sub>(151 MiB / 49.3% CPU)</sub> | **43,132**<br><sub>(151 MiB / 49.3% CPU)</sub> | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/1 · TLS | HTTP/3 · QUIC | 🥇 **28,615**<br><sub>(153 MiB / 52% CPU)</sub> | **28,615**<br><sub>(153 MiB / 52% CPU)</sub> | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 · plain | HTTP/1 · plain | 🥇 **59,946**<br><sub>(231 MiB / 50.5% CPU)</sub> | **59,946**<br><sub>(231 MiB / 50.5% CPU)</sub> | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 · plain | HTTP/2 · plain | 🥇 **105,099**<br><sub>(107 MiB / 39.5% CPU)</sub> | **105,099**<br><sub>(107 MiB / 39.5% CPU)</sub> | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 · plain | HTTP/2 · TLS | 🥇 **81,941**<br><sub>(111 MiB / 38% CPU)</sub> | **81,941**<br><sub>(111 MiB / 38% CPU)</sub> | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 · plain | HTTP/3 · QUIC | 🥇 **34,410**<br><sub>(228 MiB / 49% CPU)</sub> | **34,410**<br><sub>(228 MiB / 49% CPU)</sub> | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **55,620**<br><sub>(228 MiB / 49.5% CPU)</sub> | **55,620**<br><sub>(228 MiB / 49.5% CPU)</sub> | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 · TLS | HTTP/2 · plain | 🥇 **96,923**<br><sub>(114 MiB / 38% CPU)</sub> | **96,923**<br><sub>(114 MiB / 38% CPU)</sub> | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 · TLS | HTTP/3 · QUIC | 🥇 **32,204**<br><sub>(233 MiB / 49.3% CPU)</sub> | **32,204**<br><sub>(233 MiB / 49.3% CPU)</sub> | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **31,313**<br><sub>(319 MiB / 48.1% CPU)</sub> | **31,313**<br><sub>(319 MiB / 48.1% CPU)</sub> | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/3 · QUIC | HTTP/2 · TLS | 🥇 **30,142**<br><sub>(283 MiB / 48.4% CPU)</sub> | **30,142**<br><sub>(283 MiB / 48.4% CPU)</sub> | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/3 · QUIC | HTTP/3 · QUIC | 🥇 **23,327**<br><sub>(286 MiB / 45.9% CPU)</sub> | **23,327**<br><sub>(286 MiB / 45.9% CPU)</sub> | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/1 · plain (CONNECT) | HTTP/1 · TLS | 🥇 **37,282**<br><sub>(131 MiB / 47.8% CPU)</sub> | **37,282**<br><sub>(131 MiB / 47.8% CPU)</sub> | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/1 · TLS | HTTP/1 · TLS | 🥇 **39,263**<br><sub>(109 MiB / 46.1% CPU)</sub> | **39,263**<br><sub>(109 MiB / 46.1% CPU)</sub> | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **76,804**<br><sub>(119 MiB / 36.8% CPU)</sub> | **76,804**<br><sub>(119 MiB / 36.8% CPU)</sub> | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 · TLS | HTTP/1 · TLS | 🥇 **43,868**<br><sub>(220 MiB / 46.8% CPU)</sub> | **43,868**<br><sub>(220 MiB / 46.8% CPU)</sub> | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **26,330**<br><sub>(294 MiB / 45.7% CPU)</sub> | **26,330**<br><sub>(294 MiB / 45.7% CPU)</sub> | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |

On this GHA shape, TWP H1 plain ÷ nginx H1 plain ≈ **0.83** (46,284 / 55,862). H1 TLS terminate ≈ **0.85** (36,518 / 43,011). TWP÷YARP H1 plain ≈ **1.12×** (46,284 / 41,203). Bridges @ `f9769503`: H3→H1 ≈ **1.07×** RPS / **0.80×** Memory; H3→H2 ≈ **1.06×** / **0.75×**; H2 TLS→H1 ≈ **1.10×** / **0.98×**; H1→H2 ≈ **1.03×** / **1.08×** Memory. Absolute RPS swings by VM; prefer the **ratio** and **median across repeats**. MITM publishes the same **15** Client×Origin pairs as Reverse (inspectable/decrypt), then dual-crypto extras (CONNECT, TLS↔TLS). nginx/YARP cannot MITM.

**nginx HTTP/3:** inbound QUIC terminate → cleartext H1 (`nginx-reverse-http3-cleartext`) @ `f9769503` bridges: sustain **0** (p99/error SLO miss) / peak **18,730**. TWP/YARP H3→H1 on this row are from the same bridges pass. nginx still cannot speak HTTP/3 to an origin (no H3 upstream in this conf).

**YARP HTTP/3 (this matrix):** TWP leads H3→H1 ≈ **1.07×** (23,227 / 21,638), H3→H2 ≈ **1.06×** (25,529 / 24,112). H1→H2 ≈ **1.03×** (28,343 / 27,456). H1→H3 ≈ **1.08×** (21,375 / 19,843). h2c→H3 ≈ **1.03×**.

**Windows vs Linux:** both CI envs are **4 vCPU / 16 GiB**, but do **not** compare absolute RPS across OS. Linux nginx leads H1 plain/TLS terminate (TWP second, ahead of YARP). Windows bridges @ `f9769503` H3→H1 leads YARP on RPS (**1.02×**) and Memory (**0.69×**). Cool laptop notes remain on [Performance Local Lab](Performance-Local-Lab).


### Tiny JSON reverse is nginx’s best case on Linux

The tables above use **~64 B keep-alive GET** on loopback. On Linux H1 reverse, nginx leads; YARP sits near TWP. Heavier bodies, POSTs, TLS handshake cost, and lossy/HOL workloads (below) change the picture. MITM rows remain TWP-only. nginx HTTP/3 is inbound-terminate only (see note above).

### Why isn’t HTTP/3 > HTTP/2 > HTTP/1 in raw RPS?

For **tiny JSON responses** (~64 B) on loopback, that ordering is **not** expected: topology (TLS hop count, terminate vs MITM) dominates; HTTP/2 and HTTP/3 help multiplexing, not single-origin tiny-GET RPS. See the **lossy** tables below for a workload where protocol design matters.

## Heavier reverse workloads

Separate from the tiny-GET matrix. Same measurement environments. Modes: `compare-bodies`, `compare-post`, `compare-lossy`, `compare-tls-cost`, `compare-arch` in [RpsLoadProbe](https://github.com/justcoding121/titanium-web-proxy/tree/develop/tools/RpsLoadProbe). **PUT with the same body is the same proxy work as POST; DELETE with no body matches GET** — only POST is published. Bodies/POST/lossy stay **half-duplex**. `compare-arch` is the slow-consumer / early-response / duplex set. Laptop numbers are on [Performance Local Lab](Performance-Local-Lab#architecture-sensitive); CI medians go in the tables below.

Lossy link = **userspace** shim (not kernel `netem`): TCP gets per-buffer delay + occasional whole-connection stalls (honest HOL for multiplexed H2); UDP gets per-datagram delay + drops (QUIC). `compare-lossy` publishes H1/H2/H3; H3 is where the protocol design is supposed to matter.

### Windows — heavier reverse GET (64 KiB / 256 KiB)

Median of **3** repeats on `windows-latest` @ `1f2d0eee`. Source: Actions [32688081527](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32688081527) (`compare-bodies`). Warmup 2s / measure 8s. **RPS cells** include `(MiB / CPU%)` footprints.

| Body | Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---|---|---:|---:|---:|---:|---:|---:|
| 64 KiB | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **8,600**<br><sub>(98 MiB / 50.1% CPU)</sub> | **8,600**<br><sub>(98 MiB / 50.1% CPU)</sub> | **906**<br><sub>(137 MiB / 24.9% CPU)</sub> | **906**<br><sub>(137 MiB / 24.9% CPU)</sub> | **6,903**<br><sub>(113 MiB / 45.6% CPU)</sub> | **6,903**<br><sub>(113 MiB / 45.6% CPU)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **8,011**<br><sub>(186 MiB / 45.6% CPU)</sub> | **8,011**<br><sub>(186 MiB / 45.6% CPU)</sub> | **779**<br><sub>(138 MiB / 25% CPU)</sub> | **779**<br><sub>(138 MiB / 25% CPU)</sub> | **5,789**<br><sub>(136 MiB / 49.5% CPU)</sub> | **5,789**<br><sub>(136 MiB / 49.5% CPU)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **4,127**<br><sub>(128 MiB / 40.9% CPU)</sub> | **4,127**<br><sub>(128 MiB / 40.9% CPU)</sub> | *Not possible* (no QUIC) | *Not possible* (no QUIC) | **3,709**<br><sub>(189 MiB / 50.8% CPU)</sub> | **3,709**<br><sub>(189 MiB / 50.8% CPU)</sub> |
| 256 KiB | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **2,824**<br><sub>(102 MiB / 48.7% CPU)</sub> | **2,824**<br><sub>(102 MiB / 48.7% CPU)</sub> | **249**<br><sub>(137 MiB / 24.9% CPU)</sub> | **249**<br><sub>(137 MiB / 24.9% CPU)</sub> | **1,884**<br><sub>(140 MiB / 48.5% CPU)</sub> | **1,884**<br><sub>(140 MiB / 48.5% CPU)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **2,504**<br><sub>(154 MiB / 40.4% CPU)</sub> | **2,504**<br><sub>(154 MiB / 40.4% CPU)</sub> | **170**<br><sub>(139 MiB / 24.9% CPU)</sub> | **170**<br><sub>(139 MiB / 24.9% CPU)</sub> | **1,958**<br><sub>(101 MiB / 42.7% CPU)</sub> | **1,958**<br><sub>(101 MiB / 42.7% CPU)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **1,108**<br><sub>(129 MiB / 41.5% CPU)</sub> | **1,108**<br><sub>(129 MiB / 41.5% CPU)</sub> | *Not possible* (no QUIC) | *Not possible* (no QUIC) | **1,048**<br><sub>(176 MiB / 45.3% CPU)</sub> | **1,048**<br><sub>(176 MiB / 45.3% CPU)</sub> |

nginx/Windows collapses on large reverse bodies in this harness; treat as same-OS only. H1 TLS **64 KiB** ≈ **1.25×** YARP; **256 KiB** ≈ **1.50×**. H2→H1 64 KiB ≈ **1.38×**; H3→H1 64 KiB ≈ **1.11×**.

### Linux — heavier reverse GET (64 KiB / 256 KiB)

Median of **3** repeats @ `1f2d0eee`. Source: Actions [32688081527](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32688081527) (`compare-bodies`). Warmup 2s / measure 8s.

| Body | Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---|---|---:|---:|---:|---:|---:|---:|
| 64 KiB | HTTP/1 · TLS | HTTP/1 · plain | **12,030**<br><sub>(185 MiB / 42.3% CPU)</sub> | **12,030**<br><sub>(185 MiB / 42.3% CPU)</sub> | 🥇 **13,402**<br><sub>(98 MiB / 37.3% CPU)</sub> | **13,402**<br><sub>(98 MiB / 37.3% CPU)</sub> | **9,932**<br><sub>(169 MiB / 47.9% CPU)</sub> | **9,932**<br><sub>(169 MiB / 47.9% CPU)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **8,850**<br><sub>(247 MiB / 39.5% CPU)</sub> | **8,850**<br><sub>(247 MiB / 39.5% CPU)</sub> | **6,886**<br><sub>(105 MiB / 24.3% CPU)</sub> | **6,886**<br><sub>(105 MiB / 24.3% CPU)</sub> | **7,294**<br><sub>(164 MiB / 47.7% CPU)</sub> | **7,294**<br><sub>(164 MiB / 47.7% CPU)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **7,504**<br><sub>(230 MiB / 42.9% CPU)</sub> | **7,504**<br><sub>(230 MiB / 42.9% CPU)</sub> | **2,417**<br><sub>(116 MiB / 17.5% CPU)</sub> | **2,417**<br><sub>(116 MiB / 17.5% CPU)</sub> | **5,191**<br><sub>(242 MiB / 52.6% CPU)</sub> | **5,191**<br><sub>(242 MiB / 52.6% CPU)</sub> |
| 256 KiB | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **3,868**<br><sub>(124 MiB / 31.6% CPU)</sub> | **3,868**<br><sub>(124 MiB / 31.6% CPU)</sub> | **3,850**<br><sub>(99 MiB / 29% CPU)</sub> | **3,850**<br><sub>(99 MiB / 29% CPU)</sub> | **2,912**<br><sub>(175 MiB / 43.5% CPU)</sub> | **2,912**<br><sub>(175 MiB / 43.5% CPU)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **2,249**<br><sub>(207 MiB / 31.8% CPU)</sub> | **2,249**<br><sub>(207 MiB / 31.8% CPU)</sub> | **1,804**<br><sub>(111 MiB / 23.3% CPU)</sub> | **1,804**<br><sub>(111 MiB / 23.3% CPU)</sub> | **1,919**<br><sub>(167 MiB / 44.9% CPU)</sub> | **1,919**<br><sub>(167 MiB / 44.9% CPU)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **1,864**<br><sub>(177 MiB / 41.9% CPU)</sub> | **1,864**<br><sub>(177 MiB / 41.9% CPU)</sub> | **642**<br><sub>(102 MiB / 18.2% CPU)</sub> | **642**<br><sub>(102 MiB / 18.2% CPU)</sub> | **1,533**<br><sub>(191 MiB / 49.8% CPU)</sub> | **1,533**<br><sub>(191 MiB / 49.8% CPU)</sub> |

On this GHA pass TWP÷YARP H1 TLS ≈ **1.21×** (64 KiB) / **1.33×** (256 KiB); H2→H1 ≈ **1.21×** / **1.17×**; H3→H1 ≈ **1.45×** / **1.22×**. TWP÷nginx H1 TLS ≈ **0.90** / **1.00**. Absolute RPS swings by VM; prefer ratios.

### Windows — POST 64 KiB request + 64 KiB response

Median of **3** repeats on `windows-latest` @ `d3566486`. Source: Actions [32711461804](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32711461804) (`compare-post`). H1 keep-alive POST hang fixed (session-lite + reusable guard).

| Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---|---:|---:|---:|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | 🥇 **6,720**<br><sub>(92 MiB / 44% CPU)</sub> | **6,720**<br><sub>(92 MiB / 44% CPU)</sub> | **353**<br><sub>(137 MiB / 24.6% CPU)</sub> | **353**<br><sub>(137 MiB / 24.6% CPU)</sub> | **4,767**<br><sub>(113 MiB / 56.2% CPU)</sub> | **4,767**<br><sub>(113 MiB / 56.2% CPU)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **4,576**<br><sub>(183 MiB / 48.3% CPU)</sub> | **4,576**<br><sub>(183 MiB / 48.3% CPU)</sub> | **337**<br><sub>(137 MiB / 24.6% CPU)</sub> | **337**<br><sub>(137 MiB / 24.6% CPU)</sub> | **4,018**<br><sub>(117 MiB / 52.4% CPU)</sub> | **4,018**<br><sub>(117 MiB / 52.4% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **2,346**<br><sub>(158 MiB / 41.3% CPU)</sub> | **2,346**<br><sub>(158 MiB / 41.3% CPU)</sub> | *Not possible* | *Not possible* | **2,133**<br><sub>(156 MiB / 47.4% CPU)</sub> | **2,133**<br><sub>(156 MiB / 47.4% CPU)</sub> |

TWP leads H1 POST (~**1.41×** YARP), H2 POST (~**1.14×** YARP), and H3 POST (~**1.10×** YARP).

### Linux — POST 64 KiB request + 64 KiB response

Median of **3** repeats @ `d3566486`. Source: Actions [32711461804](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32711461804) (`compare-post`).

| Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---|---:|---:|---:|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | 🥇 **4,674**<br><sub>(125 MiB / 44.9% CPU)</sub> | **4,674**<br><sub>(125 MiB / 44.9% CPU)</sub> | **4,029**<br><sub>(96 MiB / 48.9% CPU)</sub> | **4,029**<br><sub>(96 MiB / 48.9% CPU)</sub> | **3,104**<br><sub>(178 MiB / 55.2% CPU)</sub> | **3,104**<br><sub>(178 MiB / 55.2% CPU)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **3,102**<br><sub>(230 MiB / 49.1% CPU)</sub> | **3,102**<br><sub>(230 MiB / 49.1% CPU)</sub> | **1,963**<br><sub>(99 MiB / 22.3% CPU)</sub> | **1,963**<br><sub>(99 MiB / 22.3% CPU)</sub> | **2,489**<br><sub>(148 MiB / 47.9% CPU)</sub> | **2,489**<br><sub>(148 MiB / 47.9% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **2,903**<br><sub>(227 MiB / 43.5% CPU)</sub> | **2,903**<br><sub>(227 MiB / 43.5% CPU)</sub> | **750**<br><sub>(106 MiB / 24.1% CPU)</sub> | **750**<br><sub>(106 MiB / 24.1% CPU)</sub> | **2,555**<br><sub>(253 MiB / 50% CPU)</sub> | **2,555**<br><sub>(253 MiB / 50% CPU)</sub> |

Linux nginx H1/H2/H3 POST completed (nginx.org mainline). TWP÷YARP H1 ≈ **1.51×**; H2 ≈ **1.25×**; H3 ≈ **1.14×**. TWP÷nginx H3 ≈ **3.87×**.

### Windows — lossy / high-RTT (H2 HOL / H3 loss)

Userspace **5 ms** one-way delay + **1%** TCP connection stall (H1/H2) or UDP datagram drop (H3); **64 KiB** GET. H1/H2: median of **3** repeats on `windows-latest` @ `1f2d0eee` — [32688085411](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32688085411) (`compare-lossy`). **H3:** GHA Windows userspace UDP shim collapses (sustain **0**); published H3 row is the laptop `quic-http3` remasure under the same delay/loss workload ([Performance Local Lab](Performance-Local-Lab#lossy--high-rtt-h2-hol--h3-packet-loss), `windows-20260822-lossy-h3-quic/`).

| Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---|---:|---:|---:|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | **663**<br><sub>(107 MiB / 5.4% CPU)</sub> | **663**<br><sub>(107 MiB / 5.4% CPU)</sub> | **645**<br><sub>(137 MiB / 18.3% CPU)</sub> | **645**<br><sub>(137 MiB / 18.3% CPU)</sub> | 🥇 **663**<br><sub>(122 MiB / 5.1% CPU)</sub> | **663**<br><sub>(122 MiB / 5.1% CPU)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **86**<br><sub>(126 MiB / 1.9% CPU)</sub> | **86**<br><sub>(126 MiB / 1.9% CPU)</sub> | **18**<br><sub>(138 MiB / 1.1% CPU)</sub> | **18**<br><sub>(138 MiB / 1.1% CPU)</sub> | **18**<br><sub>(98 MiB / 1.7% CPU)</sub> | **18**<br><sub>(98 MiB / 1.7% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **1,572** | **1,572** | *Not possible* (no QUIC) | *Not possible* | **0** | **50** |

TWP H2 HOL leads (~**4.8×** YARP). H3 is the protocol-shape win vs H2 HOL on the same lossy session (~**112×** H2); YARP H3 did not hold the p99 SLO under this userspace UDP shim (sustain **0**, peak **50**).

### Linux — lossy / high-RTT (H2 HOL / H3 loss)

Median of **3** repeats @ `1f2d0eee`. Source: [32688085411](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32688085411) (`compare-lossy`; lossy H3 uses `quic-http3`).

| Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---|---:|---:|---:|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | 🥇 **1,204**<br><sub>(140 MiB / 13.1% CPU)</sub> | **1,204**<br><sub>(140 MiB / 13.1% CPU)</sub> | **1,209**<br><sub>(96 MiB / 6.8% CPU)</sub> | **1,209**<br><sub>(96 MiB / 6.8% CPU)</sub> | **1,199**<br><sub>(152 MiB / 16.3% CPU)</sub> | **1,199**<br><sub>(152 MiB / 16.3% CPU)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **314**<br><sub>(192 MiB / 6.6% CPU)</sub> | **314**<br><sub>(192 MiB / 6.6% CPU)</sub> | **40**<br><sub>(99 MiB / 0.4% CPU)</sub> | **40**<br><sub>(99 MiB / 0.4% CPU)</sub> | **44**<br><sub>(119 MiB / 1.6% CPU)</sub> | **44**<br><sub>(119 MiB / 1.6% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | **328**<br><sub>(147 MiB / 12.2% CPU)</sub> | **328**<br><sub>(147 MiB / 12.2% CPU)</sub> | **86**<br><sub>(107 MiB / 2.2% CPU)</sub> | **86**<br><sub>(107 MiB / 2.2% CPU)</sub> | 🥇 **332**<br><sub>(185 MiB / 20.7% CPU)</sub> | **332**<br><sub>(185 MiB / 20.7% CPU)</sub> |

TWP H2 HOL ≫ YARP (~**7.1×**). H3 TWP÷YARP ≈ **0.99×**.

### Architecture-sensitive

`compare-arch` isolates slow app readers, origin-early response, H2 duplex, and WebSocket echo. See [TWP vs YARP IO model](Performance-Profiling#twp-vs-yarp-io-model). Laptop 1-rep numbers are on [Performance Local Lab](Performance-Local-Lab#architecture-sensitive).

Median of **3** repeats on matched 4 vCPU / 16 GiB runners @ `d3566486` ([32711464527](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32711464527)) (`compare-arch`). Slow consumer = 256 KiB GET, 16 KiB read + 8 ms sleep. Early response = 64 KiB POST, origin writes after 8 KiB. Duplex H2 = overlapping 64 KiB POST on H2 TLS↔H2 TLS. WebSocket = echo round-trips/sec. H1 early-response unblocked by session-lite keep-alive POST fix.

`compare-lossy` (slow **network**) is already published above; it is not a slow **app** reader.

#### Windows

| Scenario | Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---|---|---:|---:|---:|---:|---:|---:|
| Slow consumer (256 KiB GET, throttled client read) | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **248**<br><sub>(93 MiB / 4.4% CPU)</sub> | **248**<br><sub>(93 MiB / 4.4% CPU)</sub> | **194**<br><sub>(141 MiB / 24.5% CPU)</sub> | **194**<br><sub>(141 MiB / 24.5% CPU)</sub> | **244**<br><sub>(107 MiB / 5% CPU)</sub> | **244**<br><sub>(107 MiB / 5% CPU)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · TLS | HTTP/1 · plain | **248**<br><sub>(118 MiB / 5% CPU)</sub> | **248**<br><sub>(118 MiB / 5% CPU)</sub> | **169**<br><sub>(141 MiB / 24.8% CPU)</sub> | **169**<br><sub>(141 MiB / 24.8% CPU)</sub> | 🥇 **256**<br><sub>(109 MiB / 6.9% CPU)</sub> | **256**<br><sub>(109 MiB / 6.9% CPU)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **268**<br><sub>(98 MiB / 19.1% CPU)</sub> | **268**<br><sub>(98 MiB / 19.1% CPU)</sub> | *Not possible* (no QUIC) | *Not possible* | **259**<br><sub>(161 MiB / 22.4% CPU)</sub> | **259**<br><sub>(161 MiB / 22.4% CPU)</sub> |
| Early response (origin writes after first request chunk) | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **5,034**<br><sub>(85 MiB / 38% CPU)</sub> | **5,034**<br><sub>(85 MiB / 38% CPU)</sub> | **349**<br><sub>(136 MiB / 24.6% CPU)</sub> | **349**<br><sub>(136 MiB / 24.6% CPU)</sub> | **3,441**<br><sub>(114 MiB / 41.6% CPU)</sub> | **3,441**<br><sub>(114 MiB / 41.6% CPU)</sub> |
| Early response (origin writes after first request chunk) | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **4,285**<br><sub>(143 MiB / 43.3% CPU)</sub> | **4,285**<br><sub>(143 MiB / 43.3% CPU)</sub> | **300**<br><sub>(137 MiB / 24.5% CPU)</sub> | **300**<br><sub>(137 MiB / 24.5% CPU)</sub> | **3,016**<br><sub>(109 MiB / 39.9% CPU)</sub> | **3,016**<br><sub>(109 MiB / 39.9% CPU)</sub> |
| Early response (origin writes after first request chunk) | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **2,263**<br><sub>(136 MiB / 43.5% CPU)</sub> | **2,263**<br><sub>(136 MiB / 43.5% CPU)</sub> | *Not possible* (no QUIC) | *Not possible* | **1,985**<br><sub>(155 MiB / 53.6% CPU)</sub> | **1,985**<br><sub>(155 MiB / 53.6% CPU)</sub> |
| Duplex (both directions live) | HTTP/2 · TLS | HTTP/2 · TLS | **885**<br><sub>(113 MiB / 13.1% CPU)</sub> | **885**<br><sub>(113 MiB / 13.1% CPU)</sub> | *Not possible* | *Not possible* | 🥇 **2,293**<br><sub>(119 MiB / 38.5% CPU)</sub> | **2,293**<br><sub>(119 MiB / 38.5% CPU)</sub> |
| Duplex (WebSocket / extended CONNECT) | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **31,149**<br><sub>(93 MiB / 44.8% CPU)</sub> | **31,149**<br><sub>(93 MiB / 44.8% CPU)</sub> | **18,388**<br><sub>(137 MiB / 24.7% CPU)</sub> | **18,388**<br><sub>(137 MiB / 24.7% CPU)</sub> | **28,676**<br><sub>(89 MiB / 44.6% CPU)</sub> | **28,676**<br><sub>(89 MiB / 44.6% CPU)</sub> |

#### Linux

| Scenario | Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---|---|---:|---:|---:|---:|---:|---:|
| Slow consumer (256 KiB GET, throttled client read) | HTTP/1 · TLS | HTTP/1 · plain | **464**<br><sub>(116 MiB / 9.7% CPU)</sub> | **464**<br><sub>(116 MiB / 9.7% CPU)</sub> | 🥇 **473**<br><sub>(98 MiB / 6.6% CPU)</sub> | **473**<br><sub>(98 MiB / 6.6% CPU)</sub> | **419**<br><sub>(147 MiB / 15.3% CPU)</sub> | **419**<br><sub>(147 MiB / 15.3% CPU)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · TLS | HTTP/1 · plain | **468**<br><sub>(140 MiB / 19.1% CPU)</sub> | **468**<br><sub>(140 MiB / 19.1% CPU)</sub> | 🥇 **477**<br><sub>(108 MiB / 13.7% CPU)</sub> | **477**<br><sub>(108 MiB / 13.7% CPU)</sub> | **472**<br><sub>(144 MiB / 25% CPU)</sub> | **472**<br><sub>(144 MiB / 25% CPU)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **472**<br><sub>(133 MiB / 36.3% CPU)</sub> | **472**<br><sub>(133 MiB / 36.3% CPU)</sub> | **410**<br><sub>(122 MiB / 24.4% CPU)</sub> | **410**<br><sub>(122 MiB / 24.4% CPU)</sub> | **469**<br><sub>(197 MiB / 41% CPU)</sub> | **469**<br><sub>(197 MiB / 41% CPU)</sub> |
| Early response (origin writes after first request chunk) | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **4,637**<br><sub>(132 MiB / 47.7% CPU)</sub> | **4,637**<br><sub>(132 MiB / 47.7% CPU)</sub> | **3,998**<br><sub>(95 MiB / 50.6% CPU)</sub> | **3,998**<br><sub>(95 MiB / 50.6% CPU)</sub> | **3,088**<br><sub>(173 MiB / 56.8% CPU)</sub> | **3,088**<br><sub>(173 MiB / 56.8% CPU)</sub> |
| Early response (origin writes after first request chunk) | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **3,132**<br><sub>(230 MiB / 46.4% CPU)</sub> | **3,132**<br><sub>(230 MiB / 46.4% CPU)</sub> | **1,925**<br><sub>(99 MiB / 22.4% CPU)</sub> | **1,925**<br><sub>(99 MiB / 22.4% CPU)</sub> | **2,273**<br><sub>(132 MiB / 48.3% CPU)</sub> | **2,273**<br><sub>(132 MiB / 48.3% CPU)</sub> |
| Early response (origin writes after first request chunk) | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **2,875**<br><sub>(220 MiB / 45.9% CPU)</sub> | **2,875**<br><sub>(220 MiB / 45.9% CPU)</sub> | **708**<br><sub>(107 MiB / 24% CPU)</sub> | **708**<br><sub>(107 MiB / 24% CPU)</sub> | **2,042**<br><sub>(248 MiB / 48.7% CPU)</sub> | **2,042**<br><sub>(248 MiB / 48.7% CPU)</sub> |
| Duplex (both directions live) | HTTP/2 · TLS | HTTP/2 · TLS | **378**<br><sub>(136 MiB / 9.2% CPU)</sub> | **378**<br><sub>(136 MiB / 9.2% CPU)</sub> | *Not possible* | *Not possible* | 🥇 **1,706**<br><sub>(149 MiB / 45.4% CPU)</sub> | **1,706**<br><sub>(149 MiB / 45.4% CPU)</sub> |
| Duplex (WebSocket / extended CONNECT) | HTTP/1 · TLS | HTTP/1 · plain | **31,574**<br><sub>(122 MiB / 43.7% CPU)</sub> | **31,574**<br><sub>(122 MiB / 43.7% CPU)</sub> | 🥇 **32,771**<br><sub>(96 MiB / 35.6% CPU)</sub> | **32,771**<br><sub>(96 MiB / 35.6% CPU)</sub> | **26,797**<br><sub>(122 MiB / 44.5% CPU)</sub> | **26,797**<br><sub>(122 MiB / 44.5% CPU)</sub> |

Slow consumer is sleep-bound; H1/H2/H3 sit in the same band. Early-response H1/H2/H3: TWP leads (H1 early ≈ **1.46×** / **1.50×** YARP Win/Linux after keep-alive POST fix). **Duplex H2**: YARP leads by design — Win ≈ **0.39×** (885 / 2,293), Linux ≈ **0.22×** (378 / 1,706); irreducible concurrent-copier cell (see [IO model](Performance-Profiling#twp-vs-yarp-io-model)). WebSocket: TWP leads Windows ≈ **1.09×** YARP; Linux nginx leads.

### TLS termination cost (H1 TLS → cleartext origin)

Isolates keep-alive tiny GET vs **new connection per request** (handshake-dominated) vs keep-alive **256 KiB**. Product comparison uses RPS and end-to-end latency; TWP can also capture `ClientTlsTiming` when `TWP_RPS_CAPTURE_TLS=1` (child process) — nginx/YARP have no equivalent hook.

#### Windows

Median of **3** repeats on `windows-latest` @ `1f2d0eee`. Source: Actions [32688087733](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32688087733) (`compare-tls-cost`). Absolute RPS on GHA swings hard; prefer **TWP÷YARP**.

| Workload | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---:|---:|---:|---:|---:|---:|
| Keep-alive · tiny GET | 🥇 **25,426**<br><sub>(81 MiB / 50.7% CPU)</sub> | **25,426**<br><sub>(81 MiB / 50.7% CPU)</sub> | **12,864**<br><sub>(137 MiB / 24.8% CPU)</sub> | **12,864**<br><sub>(137 MiB / 24.8% CPU)</sub> | **23,364**<br><sub>(107 MiB / 50.4% CPU)</sub> | **23,364**<br><sub>(107 MiB / 50.4% CPU)</sub> |
| New-connection · tiny GET | 🥇 **769**<br><sub>(86 MiB / 10% CPU)</sub> | **769**<br><sub>(86 MiB / 10% CPU)</sub> | **256**<br><sub>(136 MiB / 24.6% CPU)</sub> | **256**<br><sub>(136 MiB / 24.6% CPU)</sub> | **753**<br><sub>(114 MiB / 9.9% CPU)</sub> | **753**<br><sub>(114 MiB / 9.9% CPU)</sub> |
| Keep-alive · 256 KiB GET | 🥇 **2,886**<br><sub>(116 MiB / 44.5% CPU)</sub> | **2,886**<br><sub>(116 MiB / 44.5% CPU)</sub> | **259**<br><sub>(136 MiB / 25% CPU)</sub> | **259**<br><sub>(136 MiB / 25% CPU)</sub> | **2,512**<br><sub>(137 MiB / 45.6% CPU)</sub> | **2,512**<br><sub>(137 MiB / 45.6% CPU)</sub> |

#### Linux

Median of **3** repeats @ `1f2d0eee`. Source: Actions [32688087733](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32688087733) (`compare-tls-cost`).

| Workload | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---:|---:|---:|---:|---:|---:|
| Keep-alive · tiny GET | **51,331**<br><sub>(96 MiB / 49% CPU)</sub> | **51,331**<br><sub>(96 MiB / 49% CPU)</sub> | 🥇 **66,322**<br><sub>(99 MiB / 39.8% CPU)</sub> | **66,322**<br><sub>(99 MiB / 39.8% CPU)</sub> | **45,016**<br><sub>(136 MiB / 49.7% CPU)</sub> | **45,016**<br><sub>(136 MiB / 49.7% CPU)</sub> |
| New-connection · tiny GET | **1,666**<br><sub>(125 MiB / 39% CPU)</sub> | **1,666**<br><sub>(125 MiB / 39% CPU)</sub> | 🥇 **1,729**<br><sub>(101 MiB / 34.6% CPU)</sub> | **1,729**<br><sub>(101 MiB / 34.6% CPU)</sub> | **1,652**<br><sub>(152 MiB / 38.4% CPU)</sub> | **1,652**<br><sub>(152 MiB / 38.4% CPU)</sub> |
| Keep-alive · 256 KiB GET | **5,208**<br><sub>(122 MiB / 29.8% CPU)</sub> | **5,208**<br><sub>(122 MiB / 29.8% CPU)</sub> | 🥇 **5,272**<br><sub>(100 MiB / 27% CPU)</sub> | **5,272**<br><sub>(100 MiB / 27% CPU)</sub> | **4,130**<br><sub>(175 MiB / 42.4% CPU)</sub> | **4,130**<br><sub>(175 MiB / 42.4% CPU)</sub> |

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
