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

Calibration for the shared 4 vCPU loopback shape: how close client + origin are to saturated before ranking reverse peers. Tiny keep-alive GET. Median of **3** repeats @ `9d7c2966` — [32866709227](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32866709227). Warmup 2s / measure 8s; concurrency 8, 16, 32, 64. Block A **% of origin-HttpClient** uses median **peak** RPS. Blocks B/C use peer÷YARP / ÷nginx on median peak (not % of H1 origin). **RPS cells** embed median RSS / CPU for the **proxy child** plus its **full descendant tree** (serve-proxy → nginx master → workers); origin-direct samples the **origin** child. Product matrices below use matched `dotnet-httpclient` only (not bombardier). **H3→H1** (saturation Block C): Win TWP RSS **103** MiB vs YARP **119** (~**0.87×**); Linux **142** vs **181** (~**0.78×**). RPS vs YARP (**0.99×** / **1.1×**).



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
| origin-direct | dotnet-httpclient | **50,717**<br><sub>(53 MiB / 41.6% CPU)</sub> | **50,717**<br><sub>(53 MiB / 41.6% CPU)</sub> | **100.0%** |
| origin-direct-bombardier | bombardier | **38,335**<br><sub>(55 MiB / 22.7% CPU)</sub> | **38,335**<br><sub>(55 MiB / 22.7% CPU)</sub> | **75.6%** |
| bare-reverse-http1 | dotnet-httpclient | **25,467**<br><sub>(55 MiB / 46.5% CPU)</sub> | **25,467**<br><sub>(55 MiB / 46.5% CPU)</sub> | **50.2%** |
| nginx-reverse-http1 | dotnet-httpclient | **13,259**<br><sub>(121 MiB / 25.0% CPU)</sub> | **13,422**<br><sub>(121 MiB / 25.0% CPU)</sub> | **26.5%** |
| yarp-reverse-http1 | dotnet-httpclient | **21,331**<br><sub>(89 MiB / 49.9% CPU)</sub> | **21,331**<br><sub>(89 MiB / 49.9% CPU)</sub> | **42.1%** |
| twp-reverse-http1 | dotnet-httpclient | 🥇 **25,358**<br><sub>(75 MiB / 49.2% CPU)</sub> | **25,358**<br><sub>(75 MiB / 49.2% CPU)</sub> | **50.0%** |

**Linux** (`ubuntu-latest`)

| Arm | Generator | Sustain | Peak | % of origin-HttpClient |
|---|---|---:|---:|---:|
| origin-direct | dotnet-httpclient | **68,992**<br><sub>(78 MiB / 41.8% CPU)</sub> | **68,992**<br><sub>(78 MiB / 41.8% CPU)</sub> | **100.0%** |
| origin-direct-bombardier | bombardier | **41,996**<br><sub>(78 MiB / 34.4% CPU)</sub> | **41,996**<br><sub>(78 MiB / 34.4% CPU)</sub> | **60.9%** |
| bare-reverse-http1 | dotnet-httpclient | **32,077**<br><sub>(60 MiB / 45.0% CPU)</sub> | **32,077**<br><sub>(60 MiB / 45.0% CPU)</sub> | **46.5%** |
| nginx-reverse-http1 | dotnet-httpclient | 🥇 **38,039**<br><sub>(72 MiB / 40.9% CPU)</sub> | **38,039**<br><sub>(72 MiB / 40.9% CPU)</sub> | **55.1%** |
| yarp-reverse-http1 | dotnet-httpclient | **26,736**<br><sub>(113 MiB / 49.2% CPU)</sub> | **26,736**<br><sub>(113 MiB / 49.2% CPU)</sub> | **38.8%** |
| twp-reverse-http1 | dotnet-httpclient | **31,431**<br><sub>(84 MiB / 50.0% CPU)</sub> | **31,431**<br><sub>(84 MiB / 50.0% CPU)</sub> | **45.6%** |

Reverse peers are about **50–46%** of the origin-direct HttpClient peak on this runner class (Win TWP **50.0%**, Lin TWP **45.6%**). Prefer the **%** column over absolute RPS across runs. Bare and origin-direct are controls (not medal peers).

#### Block B — H2 TLS→H1

Peer ratios (÷YARP / ÷nginx) on median peak; **RPS cells** embed `(MiB / CPU%)`.

**Windows** (`windows-latest`)

| Arm | Generator | Sustain | Peak | ÷YARP | ÷nginx |
|---|---|---:|---:|---:|---:|
| nginx-reverse-http2 | dotnet-httpclient | **8,264**<br><sub>(248 MiB / 24.8% CPU)</sub> | **8,411**<br><sub>(248 MiB / 24.8% CPU)</sub> | **0.29×** | **1×** |
| yarp-reverse-http2 | dotnet-httpclient | **29,096**<br><sub>(95 MiB / 53.0% CPU)</sub> | **29,096**<br><sub>(95 MiB / 53.0% CPU)</sub> | **1×** | **3.46×** |
| twp-reverse-http2-cleartext | dotnet-httpclient | 🥇 **35,180**<br><sub>(96 MiB / 52.0% CPU)</sub> | **35,180**<br><sub>(96 MiB / 52.0% CPU)</sub> | **1.21×** | **4.18×** |

**Linux** (`ubuntu-latest`)

| Arm | Generator | Sustain | Peak | ÷YARP | ÷nginx |
|---|---|---:|---:|---:|---:|
| nginx-reverse-http2 | dotnet-httpclient | **14,490**<br><sub>(97 MiB / 19.4% CPU)</sub> | **14,490**<br><sub>(97 MiB / 19.4% CPU)</sub> | **0.51×** | **1×** |
| yarp-reverse-http2 | dotnet-httpclient | **28,448**<br><sub>(120 MiB / 49.0% CPU)</sub> | **28,448**<br><sub>(120 MiB / 49.0% CPU)</sub> | **1×** | **1.96×** |
| twp-reverse-http2-cleartext | dotnet-httpclient | 🥇 **33,751**<br><sub>(120 MiB / 51.7% CPU)</sub> | **33,751**<br><sub>(120 MiB / 51.7% CPU)</sub> | **1.19×** | **2.33×** |

#### Block C — H3→H1

Same layout as Block B. Requires QuicListener; nginx only with `http_v3_module` (Windows nginx has no QUIC).

**Windows** (`windows-latest`)

| Arm | Generator | Sustain | Peak | ÷YARP | ÷nginx |
|---|---|---:|---:|---:|---:|
| nginx-reverse-http3-cleartext | dotnet-httpclient | *Not possible* (no QUIC) | *Not possible* | — | — |
| yarp-reverse-http3-cleartext | dotnet-httpclient | **14,549**<br><sub>(119 MiB / 50.8% CPU)</sub> | **15,166**<br><sub>(119 MiB / 50.8% CPU)</sub> | **1×** | **—** |
| twp-reverse-http3-cleartext | dotnet-httpclient | 🥇 **15,028**<br><sub>(103 MiB / 46.5% CPU)</sub> | **15,028**<br><sub>(103 MiB / 46.5% CPU)</sub> | **0.99×** | **—** |

**Linux** (`ubuntu-latest`)

| Arm | Generator | Sustain | Peak | ÷YARP | ÷nginx |
|---|---|---:|---:|---:|---:|
| nginx-reverse-http3-cleartext | dotnet-httpclient | **0**<br><sub>(103 MiB / 22.4% CPU)</sub> | **15,186**<br><sub>(103 MiB / 22.4% CPU)</sub> | **0.85×** | **1×** |
| yarp-reverse-http3-cleartext | dotnet-httpclient | **17,856**<br><sub>(181 MiB / 49.5% CPU)</sub> | **17,856**<br><sub>(181 MiB / 49.5% CPU)</sub> | **1×** | **1.18×** |
| twp-reverse-http3-cleartext | dotnet-httpclient | 🥇 **20,378**<br><sub>(142 MiB / 50.4% CPU)</sub> | **20,378**<br><sub>(142 MiB / 50.4% CPU)</sub> | **1.14×** | **1.34×** |

**How to read the tables**

- **Reverse** = bare transparent fixed-forward (no TWP plugins / interception). nginx knobs match TWP/YARP streaming (`keepalive 256`, `proxy_buffering off`). **MITM** = TWP-only table on the same Client×Origin wires: **Lite** = no-op handlers (unchanged-lite finish reuses reverse compressed relay); **Full** = mutating handlers that append up to four unique headers per direction (RPS harness adds one; product uses `MitmCompressedRelayHelper` — no probe name in library code). Remove/replace/non-unique header growth and body mutation still force full decode/re-encode. nginx/YARP cannot MITM. **HTTP/3 has no cleartext client** (QUIC always encrypted).
- **Sustainable** = last concurrency that still met error/latency SLOs. **Peak** = highest RPS in that ramp.
- 🥇 = best among **TWP / nginx / YARP** on Reverse rows (or saturation blocks): highest RPS; on an RPS tie, lower Memory (RSS) then lower CPU%. MITM is TWP-only. **Lite÷Reverse** / **Full÷Reverse** = TWP MITM lite or full sustain ÷ TWP Reverse sustain on the same Client×Origin from the same `compare-product` job.
- *Not possible* = product cannot do that path. *Not measured* = path exists but no published number yet for that OS.
- Product refresh: `compare-product` @ `c71937a2` — [32960766249](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32960766249). Heavier/saturation/tls: [32866709227](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32866709227), [32871900682](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32871900682), [32866714851](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32866714851), [32866717729](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32866717729), [32866720742](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32866720742), [32866723562](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32866723562).

```powershell
pwsh tools/RpsLoadProbe/run-rps.ps1 -Mode compare-product
pwsh tools/RpsLoadProbe/run-rps.ps1 -Mode compare-bodies
pwsh tools/RpsLoadProbe/run-rps.ps1 -Mode compare-post
pwsh tools/RpsLoadProbe/run-rps.ps1 -Mode compare-lossy
pwsh tools/RpsLoadProbe/run-rps.ps1 -Mode compare-tls-cost
pwsh tools/RpsLoadProbe/run-rps.ps1 -Mode compare-arch
pwsh tools/RpsLoadProbe/run-rps.ps1 -Mode compare-saturation
```

## Windows — Titanium vs nginx vs YARP

Client / origin: HTTP version and whether TLS is used (`plain` = cleartext, `TLS` = encrypted, `QUIC` = HTTP/3).

### Reverse

Median of **3 repeats** on `windows-latest` (4 vCPU / 16 GiB). Bare reverse 5×5 @ `c71937a2` — `compare-product` [32960766249](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32960766249). Warmup 2s / measure 8s; concurrency 8, 16, 32, 64. Prefer TWP÷peer ratios over absolute RPS. **RPS cells** include median RSS / CPU at the peak-RPS step as `<br><sub>(MiB / CPU%)</sub>`. nginx terminate peers use `keepalive 256` + streaming buffers. Laptop High-perf / cool-paired numbers stay on the [local lab](Performance-Local-Lab).

**Load generators:** Reverse inbound H3 arms use **`dotnet-httpclient`** (`http_version=3.0`, `RequestVersionExact`). nginx/Windows is same-OS only (no QUIC).

| Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---|---:|---:|---:|---:|---:|---:|
| HTTP/1 · plain | HTTP/1 · plain | 🥇 **41025**<br><sub>(72 MiB / 48.1% CPU)</sub> | 🥇 **41025**<br><sub>(72 MiB / 48.1% CPU)</sub> | **25024**<br><sub>(121 MiB / 24.8% CPU)</sub> | **25024**<br><sub>(121 MiB / 24.8% CPU)</sub> | **34609**<br><sub>(86 MiB / 48.4% CPU)</sub> | **34609**<br><sub>(86 MiB / 48.4% CPU)</sub> |
| HTTP/1 · plain | HTTP/1 · TLS | 🥇 **34712**<br><sub>(81 MiB / 50.5% CPU)</sub> | 🥇 **34712**<br><sub>(81 MiB / 50.5% CPU)</sub> | *Not possible* | *Not possible* | **30528**<br><sub>(99 MiB / 50.8% CPU)</sub> | **30528**<br><sub>(99 MiB / 50.8% CPU)</sub> |
| HTTP/1 · plain | HTTP/2 · plain | 🥇 **55322**<br><sub>(101 MiB / 47.8% CPU)</sub> | 🥇 **55322**<br><sub>(101 MiB / 47.8% CPU)</sub> | *Not possible* | *Not possible* | **52232**<br><sub>(92 MiB / 51.1% CPU)</sub> | **52232**<br><sub>(92 MiB / 51.1% CPU)</sub> |
| HTTP/1 · plain | HTTP/2 · TLS | 🥇 **49471**<br><sub>(107 MiB / 48.5% CPU)</sub> | 🥇 **49471**<br><sub>(107 MiB / 48.5% CPU)</sub> | *Not possible* | *Not possible* | **46990**<br><sub>(100 MiB / 49.7% CPU)</sub> | **46990**<br><sub>(100 MiB / 49.7% CPU)</sub> |
| HTTP/1 · plain | HTTP/3 · QUIC | 🥇 **27928**<br><sub>(112 MiB / 50.8% CPU)</sub> | 🥇 **27928**<br><sub>(112 MiB / 50.8% CPU)</sub> | *Not possible* (no QUIC) | *Not possible* (no QUIC) | **27476**<br><sub>(124 MiB / 51.6% CPU)</sub> | **27476**<br><sub>(124 MiB / 51.6% CPU)</sub> |
| HTTP/1 · TLS | HTTP/1 · plain | 🥇 **34140**<br><sub>(83 MiB / 43.9% CPU)</sub> | 🥇 **34140**<br><sub>(83 MiB / 43.9% CPU)</sub> | **16331**<br><sub>(137 MiB / 24.8% CPU)</sub> | **16519**<br><sub>(137 MiB / 24.8% CPU)</sub> | **29749**<br><sub>(101 MiB / 49.0% CPU)</sub> | **29749**<br><sub>(101 MiB / 49.0% CPU)</sub> |
| HTTP/1 · TLS | HTTP/1 · TLS | 🥇 **31041**<br><sub>(83 MiB / 46.6% CPU)</sub> | 🥇 **31041**<br><sub>(83 MiB / 46.6% CPU)</sub> | *Not possible* | *Not possible* | **27018**<br><sub>(101 MiB / 50.0% CPU)</sub> | **27018**<br><sub>(101 MiB / 50.0% CPU)</sub> |
| HTTP/1 · TLS | HTTP/2 · plain | 🥇 **43769**<br><sub>(110 MiB / 47.5% CPU)</sub> | 🥇 **43769**<br><sub>(110 MiB / 47.5% CPU)</sub> | *Not possible* | *Not possible* | **43070**<br><sub>(109 MiB / 49.2% CPU)</sub> | **43070**<br><sub>(109 MiB / 49.2% CPU)</sub> |
| HTTP/1 · TLS | HTTP/2 · TLS | 🥇 **40108**<br><sub>(114 MiB / 47.9% CPU)</sub> | 🥇 **40108**<br><sub>(114 MiB / 47.9% CPU)</sub> | *Not possible* | *Not possible* | **39005**<br><sub>(106 MiB / 48.0% CPU)</sub> | **39005**<br><sub>(106 MiB / 48.0% CPU)</sub> |
| HTTP/1 · TLS | HTTP/3 · QUIC | 🥇 **24269**<br><sub>(113 MiB / 49.6% CPU)</sub> | 🥇 **24269**<br><sub>(113 MiB / 49.6% CPU)</sub> | *Not possible* (no QUIC) | *Not possible* (no QUIC) | **23450**<br><sub>(128 MiB / 50.8% CPU)</sub> | **23450**<br><sub>(128 MiB / 50.8% CPU)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | 🥇 **54074**<br><sub>(90 MiB / 51.9% CPU)</sub> | 🥇 **54074**<br><sub>(90 MiB / 51.9% CPU)</sub> | *Not possible* | *Not possible* | **49807**<br><sub>(85 MiB / 51.8% CPU)</sub> | **49807**<br><sub>(85 MiB / 51.8% CPU)</sub> |
| HTTP/2 · plain | HTTP/1 · TLS | 🥇 **46754**<br><sub>(101 MiB / 52.0% CPU)</sub> | 🥇 **46754**<br><sub>(101 MiB / 52.0% CPU)</sub> | *Not possible* | *Not possible* | **41956**<br><sub>(91 MiB / 50.6% CPU)</sub> | **41956**<br><sub>(91 MiB / 50.6% CPU)</sub> |
| HTTP/2 · plain | HTTP/2 · plain | 🥇 **119261**<br><sub>(72 MiB / 35.1% CPU)</sub> | 🥇 **119261**<br><sub>(72 MiB / 35.1% CPU)</sub> | *Not possible* | *Not possible* | **89148**<br><sub>(97 MiB / 52.7% CPU)</sub> | **89148**<br><sub>(97 MiB / 52.7% CPU)</sub> |
| HTTP/2 · plain | HTTP/2 · TLS | 🥇 **102054**<br><sub>(82 MiB / 37.2% CPU)</sub> | 🥇 **102054**<br><sub>(82 MiB / 37.2% CPU)</sub> | *Not possible* | *Not possible* | **77568**<br><sub>(104 MiB / 47.5% CPU)</sub> | **77568**<br><sub>(104 MiB / 47.5% CPU)</sub> |
| HTTP/2 · plain | HTTP/3 · QUIC | 🥇 **42180**<br><sub>(135 MiB / 53.8% CPU)</sub> | 🥇 **42180**<br><sub>(135 MiB / 53.8% CPU)</sub> | *Not possible* (no QUIC) | *Not possible* (no QUIC) | **40400**<br><sub>(131 MiB / 51.8% CPU)</sub> | **40400**<br><sub>(131 MiB / 51.8% CPU)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **51831**<br><sub>(100 MiB / 52.7% CPU)</sub> | 🥇 **51831**<br><sub>(100 MiB / 52.7% CPU)</sub> | **14574**<br><sub>(137 MiB / 24.2% CPU)</sub> | **14876**<br><sub>(137 MiB / 24.2% CPU)</sub> | **45383**<br><sub>(93 MiB / 49.8% CPU)</sub> | **45383**<br><sub>(93 MiB / 49.8% CPU)</sub> |
| HTTP/2 · TLS | HTTP/1 · TLS | 🥇 **45126**<br><sub>(102 MiB / 51.7% CPU)</sub> | 🥇 **45126**<br><sub>(102 MiB / 51.7% CPU)</sub> | *Not possible* | *Not possible* | **39564**<br><sub>(95 MiB / 51.6% CPU)</sub> | **39564**<br><sub>(95 MiB / 51.6% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | 🥇 **110147**<br><sub>(90 MiB / 36.2% CPU)</sub> | 🥇 **110147**<br><sub>(90 MiB / 36.2% CPU)</sub> | *Not possible* | *Not possible* | **75865**<br><sub>(101 MiB / 52.3% CPU)</sub> | **75865**<br><sub>(101 MiB / 52.3% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **97618**<br><sub>(87 MiB / 37.5% CPU)</sub> | 🥇 **97618**<br><sub>(87 MiB / 37.5% CPU)</sub> | *Not possible* | *Not possible* | **69005**<br><sub>(101 MiB / 49.6% CPU)</sub> | **69005**<br><sub>(101 MiB / 49.6% CPU)</sub> |
| HTTP/2 · TLS | HTTP/3 · QUIC | 🥇 **40752**<br><sub>(141 MiB / 52.8% CPU)</sub> | 🥇 **40752**<br><sub>(141 MiB / 52.8% CPU)</sub> | *Not possible* (no QUIC) | *Not possible* (no QUIC) | **36652**<br><sub>(132 MiB / 53.1% CPU)</sub> | **36652**<br><sub>(132 MiB / 53.1% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **24291**<br><sub>(112 MiB / 44.5% CPU)</sub> | 🥇 **24291**<br><sub>(112 MiB / 44.5% CPU)</sub> | *Not possible* (no QUIC) | *Not possible* (no QUIC) | **22638**<br><sub>(121 MiB / 48.1% CPU)</sub> | **23401**<br><sub>(121 MiB / 48.1% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **22674**<br><sub>(119 MiB / 44.6% CPU)</sub> | 🥇 **22674**<br><sub>(119 MiB / 44.6% CPU)</sub> | *Not possible* | *Not possible* | **20885**<br><sub>(119 MiB / 49.9% CPU)</sub> | **21058**<br><sub>(119 MiB / 49.9% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/2 · plain | 🥇 **37142**<br><sub>(129 MiB / 49.4% CPU)</sub> | 🥇 **37142**<br><sub>(129 MiB / 49.4% CPU)</sub> | *Not possible* (no H3→H2) | *Not possible* (no H3→H2) | **34757**<br><sub>(155 MiB / 50.2% CPU)</sub> | **34757**<br><sub>(155 MiB / 50.2% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | 🥇 **33873**<br><sub>(130 MiB / 47.9% CPU)</sub> | 🥇 **33873**<br><sub>(130 MiB / 47.9% CPU)</sub> | *Not possible* (no H3→H2) | *Not possible* (no H3→H2) | **33573**<br><sub>(163 MiB / 48.2% CPU)</sub> | **33573**<br><sub>(163 MiB / 48.2% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/3 · QUIC | **20878**<br><sub>(129 MiB / 46.4% CPU)</sub> | **20878**<br><sub>(129 MiB / 46.4% CPU)</sub> | *Not possible* (no QUIC) | *Not possible* (no QUIC) | 🥇 **24536**<br><sub>(173 MiB / 50.8% CPU)</sub> | 🥇 **24536**<br><sub>(173 MiB / 50.8% CPU)</sub> |

### MITM (TWP only)

Same Client×Origin wires with interception on (`compare-product` [32960766249](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32960766249)). **Lite** = no-op handlers (unchanged-lite finish). **Full** = append-only header mutation (harness: one probe header each way; product: generic append-only relay via `MitmCompressedRelayHelper`). nginx/YARP cannot MITM. **Lite÷Reverse** / **Full÷Reverse** vs bare reverse (same job).

**Post-fix note (2026-08-27):** v1 generalized append-only relay (`MitmCompressedRelayHelper`) landed on `mitm/relay-publish`. Pre-fix H2→H2 Full÷Reverse was **0.13–0.16×** ([32960766249](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32960766249)); first GHA pass medians were **0.767–0.794×** on H2 plain→H2 plain (h2c→h2c). Fix-forward: zero-copy HPACK append suffix on relay path. Laptop spot @ c=64: h2c→h2c Full÷Reverse **1.003×**, H2 TLS→h2c **0.990×**. GHA `compare-product` ×3 remeasure in flight ([33041442034](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33041442034) + 2 siblings). Published table below is still pre-fix CI until those runs validate ≥0.80 on all arms.

| Client | Origin | Lite sustain | Full sustain | Lite÷Reverse | Full÷Reverse |
|---|---|---:|---:|---:|---:|
| HTTP/1 · plain | HTTP/1 · plain | 🥇 **40598**<br><sub>(76 MiB / 48.1% CPU)</sub> | **35091**<br><sub>(79 MiB / 52.6% CPU)</sub> | **0.99×** | **0.86×** |
| HTTP/1 · plain | HTTP/1 · TLS | 🥇 **34487**<br><sub>(88 MiB / 49.2% CPU)</sub> | **30702**<br><sub>(91 MiB / 51.3% CPU)</sub> | **0.99×** | **0.88×** |
| HTTP/1 · plain | HTTP/2 · plain | 🥇 **53742**<br><sub>(120 MiB / 50.9% CPU)</sub> | **52732**<br><sub>(123 MiB / 50.6% CPU)</sub> | **0.97×** | **0.95×** |
| HTTP/1 · plain | HTTP/2 · TLS | 🥇 **47896**<br><sub>(115 MiB / 51.6% CPU)</sub> | **48004**<br><sub>(117 MiB / 49.7% CPU)</sub> | **0.97×** | **0.97×** |
| HTTP/1 · plain | HTTP/3 · QUIC | 🥇 **27577**<br><sub>(109 MiB / 52.6% CPU)</sub> | **27356**<br><sub>(112 MiB / 51.4% CPU)</sub> | **0.99×** | **0.98×** |
| HTTP/1 · TLS | HTTP/1 · plain | 🥇 **33374**<br><sub>(87 MiB / 49.9% CPU)</sub> | **29388**<br><sub>(87 MiB / 48.4% CPU)</sub> | **0.98×** | **0.86×** |
| HTTP/1 · TLS | HTTP/1 · TLS | 🥇 **30543**<br><sub>(91 MiB / 46.3% CPU)</sub> | **27441**<br><sub>(90 MiB / 52.5% CPU)</sub> | **0.98×** | **0.88×** |
| HTTP/1 · TLS | HTTP/2 · plain | 🥇 **41828**<br><sub>(126 MiB / 50.7% CPU)</sub> | **41796**<br><sub>(136 MiB / 48.7% CPU)</sub> | **0.96×** | **0.95×** |
| HTTP/1 · TLS | HTTP/2 · TLS | 🥇 **39553**<br><sub>(120 MiB / 49.9% CPU)</sub> | **38882**<br><sub>(125 MiB / 51.0% CPU)</sub> | **0.99×** | **0.97×** |
| HTTP/1 · TLS | HTTP/3 · QUIC | 🥇 **24173**<br><sub>(109 MiB / 52.1% CPU)</sub> | **23744**<br><sub>(111 MiB / 51.9% CPU)</sub> | **1.00×** | **0.98×** |
| HTTP/2 · plain | HTTP/1 · plain | 🥇 **52800**<br><sub>(97 MiB / 52.0% CPU)</sub> | **51344**<br><sub>(94 MiB / 53.0% CPU)</sub> | **0.98×** | **0.95×** |
| HTTP/2 · plain | HTTP/1 · TLS | 🥇 **44671**<br><sub>(107 MiB / 54.1% CPU)</sub> | **44315**<br><sub>(103 MiB / 54.3% CPU)</sub> | **0.96×** | **0.95×** |
| HTTP/2 · plain | HTTP/2 · plain | 🥇 **101312**<br><sub>(71 MiB / 49.5% CPU)</sub> | **18755**<br><sub>(70 MiB / 51.0% CPU)</sub> | **0.85×** | **0.16×** |
| HTTP/2 · plain | HTTP/2 · TLS | 🥇 **88479**<br><sub>(89 MiB / 48.2% CPU)</sub> | **13680**<br><sub>(79 MiB / 48.5% CPU)</sub> | **0.87×** | **0.13×** |
| HTTP/2 · plain | HTTP/3 · QUIC | 🥇 **41372**<br><sub>(132 MiB / 55.0% CPU)</sub> | **40516**<br><sub>(129 MiB / 53.9% CPU)</sub> | **0.98×** | **0.96×** |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **49960**<br><sub>(107 MiB / 50.6% CPU)</sub> | **49168**<br><sub>(108 MiB / 52.2% CPU)</sub> | **0.96×** | **0.95×** |
| HTTP/2 · TLS | HTTP/1 · TLS | 🥇 **42231**<br><sub>(102 MiB / 52.5% CPU)</sub> | **43365**<br><sub>(99 MiB / 53.1% CPU)</sub> | **0.94×** | **0.96×** |
| HTTP/2 · TLS | HTTP/2 · plain | 🥇 **96587**<br><sub>(98 MiB / 46.7% CPU)</sub> | **19466**<br><sub>(87 MiB / 53.9% CPU)</sub> | **0.88×** | **0.18×** |
| HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **85710**<br><sub>(96 MiB / 43.5% CPU)</sub> | **14442**<br><sub>(91 MiB / 49.4% CPU)</sub> | **0.88×** | **0.15×** |
| HTTP/2 · TLS | HTTP/3 · QUIC | 🥇 **39904**<br><sub>(138 MiB / 53.5% CPU)</sub> | **39272**<br><sub>(139 MiB / 54.9% CPU)</sub> | **0.98×** | **0.96×** |
| HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **23440**<br><sub>(111 MiB / 45.6% CPU)</sub> | **16283**<br><sub>(111 MiB / 49.0% CPU)</sub> | **0.96×** | **0.67×** |
| HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **20759**<br><sub>(120 MiB / 46.9% CPU)</sub> | **13849**<br><sub>(99 MiB / 49.8% CPU)</sub> | **0.92×** | **0.61×** |
| HTTP/3 · QUIC | HTTP/2 · plain | 🥇 **33821**<br><sub>(138 MiB / 49.2% CPU)</sub> | **32620**<br><sub>(140 MiB / 49.6% CPU)</sub> | **0.91×** | **0.88×** |
| HTTP/3 · QUIC | HTTP/2 · TLS | 🥇 **31759**<br><sub>(141 MiB / 50.2% CPU)</sub> | **31318**<br><sub>(145 MiB / 51.1% CPU)</sub> | **0.94×** | **0.92×** |
| HTTP/3 · QUIC | HTTP/3 · QUIC | 🥇 **19614**<br><sub>(126 MiB / 48.9% CPU)</sub> | **18634**<br><sub>(111 MiB / 45.7% CPU)</sub> | **0.94×** | **0.89×** |
| HTTP/1 · plain (CONNECT) | HTTP/1 · TLS | 🥇 **25522**<br><sub>(106 MiB / 53.6% CPU)</sub> | **24557**<br><sub>(103 MiB / 55.8% CPU)</sub> | *n/a* | *n/a* |

## Linux — Titanium vs nginx vs YARP

### Reverse

Median of **3 repeats** on `ubuntu-latest` (4 vCPU / 16 GiB). Bare reverse 5×5 @ `c71937a2` — `compare-product` [32960766249](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32960766249). Warmup 2s / measure 8s; concurrency 8, 16, 32, 64. **Linux nginx is the authoritative nginx baseline.** nginx terminate peers use `keepalive 256` + streaming buffers. The RPS workflow installs nginx.org mainline (`http_v3_module`) and `libmsquic`. Prefer ratios over absolute RPS.

| Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---|---:|---:|---:|---:|---:|---:|
| HTTP/1 · plain | HTTP/1 · plain | **32442**<br><sub>(82 MiB / 50.0% CPU)</sub> | **32442**<br><sub>(82 MiB / 50.0% CPU)</sub> | 🥇 **38910**<br><sub>(72 MiB / 40.5% CPU)</sub> | 🥇 **38910**<br><sub>(72 MiB / 40.5% CPU)</sub> | **27468**<br><sub>(115 MiB / 49.9% CPU)</sub> | **27468**<br><sub>(115 MiB / 49.9% CPU)</sub> |
| HTTP/1 · plain | HTTP/1 · TLS | 🥇 **24088**<br><sub>(99 MiB / 50.0% CPU)</sub> | 🥇 **24088**<br><sub>(99 MiB / 50.0% CPU)</sub> | *Not possible* | *Not possible* | **21483**<br><sub>(124 MiB / 50.4% CPU)</sub> | **21483**<br><sub>(124 MiB / 50.4% CPU)</sub> |
| HTTP/1 · plain | HTTP/2 · plain | 🥇 **38601**<br><sub>(129 MiB / 51.5% CPU)</sub> | 🥇 **38601**<br><sub>(129 MiB / 51.5% CPU)</sub> | *Not possible* | *Not possible* | **34502**<br><sub>(125 MiB / 49.5% CPU)</sub> | **34502**<br><sub>(125 MiB / 49.5% CPU)</sub> |
| HTTP/1 · plain | HTTP/2 · TLS | 🥇 **31103**<br><sub>(131 MiB / 49.8% CPU)</sub> | 🥇 **31103**<br><sub>(131 MiB / 49.8% CPU)</sub> | *Not possible* | *Not possible* | **29700**<br><sub>(132 MiB / 48.1% CPU)</sub> | **29700**<br><sub>(132 MiB / 48.1% CPU)</sub> |
| HTTP/1 · plain | HTTP/3 · QUIC | 🥇 **22339**<br><sub>(131 MiB / 51.9% CPU)</sub> | 🥇 **22339**<br><sub>(131 MiB / 51.9% CPU)</sub> | *Not possible* (no QUIC) | *Not possible* (no QUIC) | **21210**<br><sub>(149 MiB / 49.3% CPU)</sub> | **21210**<br><sub>(149 MiB / 49.3% CPU)</sub> |
| HTTP/1 · TLS | HTTP/1 · plain | **23293**<br><sub>(105 MiB / 49.3% CPU)</sub> | **23293**<br><sub>(105 MiB / 49.3% CPU)</sub> | 🥇 **26873**<br><sub>(97 MiB / 41.5% CPU)</sub> | 🥇 **26873**<br><sub>(97 MiB / 41.5% CPU)</sub> | **19966**<br><sub>(136 MiB / 50.1% CPU)</sub> | **19966**<br><sub>(136 MiB / 50.1% CPU)</sub> |
| HTTP/1 · TLS | HTTP/1 · TLS | 🥇 **19135**<br><sub>(104 MiB / 48.3% CPU)</sub> | 🥇 **19135**<br><sub>(104 MiB / 48.3% CPU)</sub> | *Not possible* | *Not possible* | **17065**<br><sub>(138 MiB / 49.6% CPU)</sub> | **17065**<br><sub>(138 MiB / 49.6% CPU)</sub> |
| HTTP/1 · TLS | HTTP/2 · plain | 🥇 **26310**<br><sub>(142 MiB / 49.8% CPU)</sub> | 🥇 **26310**<br><sub>(142 MiB / 49.8% CPU)</sub> | *Not possible* | *Not possible* | **24764**<br><sub>(144 MiB / 49.1% CPU)</sub> | **24764**<br><sub>(144 MiB / 49.1% CPU)</sub> |
| HTTP/1 · TLS | HTTP/2 · TLS | 🥇 **23637**<br><sub>(146 MiB / 48.0% CPU)</sub> | 🥇 **23637**<br><sub>(146 MiB / 48.0% CPU)</sub> | *Not possible* | *Not possible* | **22212**<br><sub>(145 MiB / 47.2% CPU)</sub> | **22212**<br><sub>(145 MiB / 47.2% CPU)</sub> |
| HTTP/1 · TLS | HTTP/3 · QUIC | 🥇 **17278**<br><sub>(134 MiB / 51.3% CPU)</sub> | 🥇 **17278**<br><sub>(134 MiB / 51.3% CPU)</sub> | *Not possible* (no QUIC) | *Not possible* (no QUIC) | **16348**<br><sub>(157 MiB / 49.5% CPU)</sub> | **16348**<br><sub>(157 MiB / 49.5% CPU)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | 🥇 **37017**<br><sub>(111 MiB / 53.0% CPU)</sub> | 🥇 **37017**<br><sub>(111 MiB / 53.0% CPU)</sub> | *Not possible* | *Not possible* | **34334**<br><sub>(118 MiB / 49.9% CPU)</sub> | **34334**<br><sub>(118 MiB / 49.9% CPU)</sub> |
| HTTP/2 · plain | HTTP/1 · TLS | 🥇 **28470**<br><sub>(126 MiB / 51.8% CPU)</sub> | 🥇 **28470**<br><sub>(126 MiB / 51.8% CPU)</sub> | *Not possible* | *Not possible* | **25800**<br><sub>(124 MiB / 50.2% CPU)</sub> | **25800**<br><sub>(124 MiB / 50.2% CPU)</sub> |
| HTTP/2 · plain | HTTP/2 · plain | 🥇 **64814**<br><sub>(100 MiB / 39.7% CPU)</sub> | 🥇 **64814**<br><sub>(100 MiB / 39.7% CPU)</sub> | *Not possible* | *Not possible* | **48617**<br><sub>(121 MiB / 46.6% CPU)</sub> | **48617**<br><sub>(121 MiB / 46.6% CPU)</sub> |
| HTTP/2 · plain | HTTP/2 · TLS | 🥇 **51516**<br><sub>(109 MiB / 39.4% CPU)</sub> | 🥇 **51516**<br><sub>(109 MiB / 39.4% CPU)</sub> | *Not possible* | *Not possible* | **39280**<br><sub>(124 MiB / 45.8% CPU)</sub> | **39280**<br><sub>(124 MiB / 45.8% CPU)</sub> |
| HTTP/2 · plain | HTTP/3 · QUIC | 🥇 **26375**<br><sub>(140 MiB / 50.0% CPU)</sub> | 🥇 **26375**<br><sub>(140 MiB / 50.0% CPU)</sub> | *Not possible* (no QUIC) | *Not possible* (no QUIC) | **25387**<br><sub>(154 MiB / 46.5% CPU)</sub> | **25387**<br><sub>(154 MiB / 46.5% CPU)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **34630**<br><sub>(118 MiB / 52.0% CPU)</sub> | 🥇 **34630**<br><sub>(118 MiB / 52.0% CPU)</sub> | **14998**<br><sub>(97 MiB / 19.4% CPU)</sub> | **14998**<br><sub>(97 MiB / 19.4% CPU)</sub> | **29299**<br><sub>(121 MiB / 49.6% CPU)</sub> | **29299**<br><sub>(121 MiB / 49.6% CPU)</sub> |
| HTTP/2 · TLS | HTTP/1 · TLS | 🥇 **26593**<br><sub>(115 MiB / 50.4% CPU)</sub> | 🥇 **26593**<br><sub>(115 MiB / 50.4% CPU)</sub> | *Not possible* | *Not possible* | **23102**<br><sub>(128 MiB / 49.8% CPU)</sub> | **23102**<br><sub>(128 MiB / 49.8% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | 🥇 **57728**<br><sub>(112 MiB / 39.3% CPU)</sub> | 🥇 **57728**<br><sub>(112 MiB / 39.3% CPU)</sub> | *Not possible* | *Not possible* | **39242**<br><sub>(127 MiB / 46.9% CPU)</sub> | **39242**<br><sub>(127 MiB / 46.9% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **48512**<br><sub>(115 MiB / 38.9% CPU)</sub> | 🥇 **48512**<br><sub>(115 MiB / 38.9% CPU)</sub> | *Not possible* | *Not possible* | **33817**<br><sub>(128 MiB / 45.9% CPU)</sub> | **33817**<br><sub>(128 MiB / 45.9% CPU)</sub> |
| HTTP/2 · TLS | HTTP/3 · QUIC | 🥇 **25113**<br><sub>(146 MiB / 49.1% CPU)</sub> | 🥇 **25113**<br><sub>(146 MiB / 49.1% CPU)</sub> | *Not possible* (no QUIC) | *Not possible* (no QUIC) | **22435**<br><sub>(156 MiB / 46.5% CPU)</sub> | **22435**<br><sub>(156 MiB / 46.5% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **20531**<br><sub>(143 MiB / 49.8% CPU)</sub> | 🥇 **20531**<br><sub>(143 MiB / 49.8% CPU)</sub> | **12370**<br><sub>(102 MiB / 22.6% CPU)</sub> | **15142**<br><sub>(102 MiB / 22.6% CPU)</sub> | **18328**<br><sub>(179 MiB / 50.1% CPU)</sub> | **18328**<br><sub>(179 MiB / 50.1% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **15566**<br><sub>(147 MiB / 47.3% CPU)</sub> | 🥇 **15566**<br><sub>(147 MiB / 47.3% CPU)</sub> | *Not possible* | *Not possible* | **14627**<br><sub>(194 MiB / 50.6% CPU)</sub> | **14627**<br><sub>(194 MiB / 50.6% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/2 · plain | 🥇 **26473**<br><sub>(139 MiB / 53.1% CPU)</sub> | 🥇 **26473**<br><sub>(139 MiB / 53.1% CPU)</sub> | *Not possible* (no H3→H2) | *Not possible* (no H3→H2) | **23598**<br><sub>(195 MiB / 48.3% CPU)</sub> | **23598**<br><sub>(195 MiB / 48.3% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | 🥇 **23194**<br><sub>(147 MiB / 51.4% CPU)</sub> | 🥇 **23194**<br><sub>(147 MiB / 51.4% CPU)</sub> | *Not possible* (no H3→H2) | *Not possible* (no H3→H2) | **21588**<br><sub>(190 MiB / 47.7% CPU)</sub> | **21588**<br><sub>(190 MiB / 47.7% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/3 · QUIC | 🥇 **18908**<br><sub>(149 MiB / 46.9% CPU)</sub> | 🥇 **18908**<br><sub>(149 MiB / 46.9% CPU)</sub> | *Not possible* (no QUIC) | *Not possible* (no QUIC) | **15495**<br><sub>(192 MiB / 48.1% CPU)</sub> | **15495**<br><sub>(192 MiB / 48.1% CPU)</sub> |

### MITM (TWP only)

Same Client×Origin wires with interception on (`compare-product` [32960766249](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32960766249)). **Lite** = no-op handlers (unchanged-lite finish). **Full** = append-only header mutation (harness: one probe header each way; product: generic append-only relay via `MitmCompressedRelayHelper`). nginx/YARP cannot MITM. **Lite÷Reverse** / **Full÷Reverse** vs bare reverse (same job).

**Post-fix note (2026-08-27):** v1 generalized append-only relay (`MitmCompressedRelayHelper`) landed on `mitm/relay-publish`. Pre-fix H2→H2 Full÷Reverse was **0.13–0.16×** ([32960766249](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32960766249)); first GHA pass medians were **0.767–0.794×** on H2 plain→H2 plain (h2c→h2c). Fix-forward: zero-copy HPACK append suffix on relay path. Laptop spot @ c=64: h2c→h2c Full÷Reverse **1.003×**, H2 TLS→h2c **0.990×**. GHA `compare-product` ×3 remeasure in flight ([33041442034](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33041442034) + 2 siblings). Published table below is still pre-fix CI until those runs validate ≥0.80 on all arms.

| Client | Origin | Lite sustain | Full sustain | Lite÷Reverse | Full÷Reverse |
|---|---|---:|---:|---:|---:|
| HTTP/1 · plain | HTTP/1 · plain | 🥇 **31341**<br><sub>(84 MiB / 50.5% CPU)</sub> | **25750**<br><sub>(95 MiB / 50.7% CPU)</sub> | **0.97×** | **0.79×** |
| HTTP/1 · plain | HTTP/1 · TLS | 🥇 **24143**<br><sub>(107 MiB / 51.3% CPU)</sub> | **19549**<br><sub>(114 MiB / 51.0% CPU)</sub> | **1.00×** | **0.81×** |
| HTTP/1 · plain | HTTP/2 · plain | 🥇 **38706**<br><sub>(135 MiB / 53.0% CPU)</sub> | **36217**<br><sub>(140 MiB / 52.6% CPU)</sub> | **1.00×** | **0.94×** |
| HTTP/1 · plain | HTTP/2 · TLS | 🥇 **30144**<br><sub>(147 MiB / 51.5% CPU)</sub> | **29575**<br><sub>(141 MiB / 51.1% CPU)</sub> | **0.97×** | **0.95×** |
| HTTP/1 · plain | HTTP/3 · QUIC | 🥇 **21802**<br><sub>(134 MiB / 53.1% CPU)</sub> | **22335**<br><sub>(132 MiB / 52.9% CPU)</sub> | **0.98×** | **1.00×** |
| HTTP/1 · TLS | HTTP/1 · plain | 🥇 **22828**<br><sub>(110 MiB / 50.5% CPU)</sub> | **18135**<br><sub>(111 MiB / 50.8% CPU)</sub> | **0.98×** | **0.78×** |
| HTTP/1 · TLS | HTTP/1 · TLS | 🥇 **19298**<br><sub>(109 MiB / 49.2% CPU)</sub> | **15947**<br><sub>(120 MiB / 49.6% CPU)</sub> | **1.01×** | **0.83×** |
| HTTP/1 · TLS | HTTP/2 · plain | 🥇 **26264**<br><sub>(152 MiB / 50.6% CPU)</sub> | **25256**<br><sub>(165 MiB / 51.2% CPU)</sub> | **1.00×** | **0.96×** |
| HTTP/1 · TLS | HTTP/2 · TLS | 🥇 **22284**<br><sub>(157 MiB / 49.5% CPU)</sub> | **22285**<br><sub>(153 MiB / 49.6% CPU)</sub> | **0.94×** | **0.94×** |
| HTTP/1 · TLS | HTTP/3 · QUIC | 🥇 **17158**<br><sub>(154 MiB / 51.9% CPU)</sub> | **16953**<br><sub>(149 MiB / 51.7% CPU)</sub> | **0.99×** | **0.98×** |
| HTTP/2 · plain | HTTP/1 · plain | 🥇 **35954**<br><sub>(111 MiB / 53.8% CPU)</sub> | **35149**<br><sub>(114 MiB / 53.9% CPU)</sub> | **0.97×** | **0.95×** |
| HTTP/2 · plain | HTTP/1 · TLS | 🥇 **27724**<br><sub>(122 MiB / 52.6% CPU)</sub> | **26872**<br><sub>(123 MiB / 52.4% CPU)</sub> | **0.97×** | **0.94×** |
| HTTP/2 · plain | HTTP/2 · plain | 🥇 **57129**<br><sub>(101 MiB / 48.2% CPU)</sub> | **14442**<br><sub>(96 MiB / 51.5% CPU)</sub> | **0.88×** | **0.22×** |
| HTTP/2 · plain | HTTP/2 · TLS | 🥇 **45119**<br><sub>(99 MiB / 45.6% CPU)</sub> | **9618**<br><sub>(98 MiB / 49.4% CPU)</sub> | **0.88×** | **0.19×** |
| HTTP/2 · plain | HTTP/3 · QUIC | 🥇 **26558**<br><sub>(136 MiB / 50.2% CPU)</sub> | **26568**<br><sub>(140 MiB / 51.2% CPU)</sub> | **1.01×** | **1.01×** |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **32900**<br><sub>(120 MiB / 53.4% CPU)</sub> | **33051**<br><sub>(121 MiB / 53.2% CPU)</sub> | **0.95×** | **0.95×** |
| HTTP/2 · TLS | HTTP/1 · TLS | 🥇 **25456**<br><sub>(116 MiB / 51.0% CPU)</sub> | **25225**<br><sub>(123 MiB / 50.7% CPU)</sub> | **0.96×** | **0.95×** |
| HTTP/2 · TLS | HTTP/2 · plain | 🥇 **50915**<br><sub>(110 MiB / 46.8% CPU)</sub> | **13907**<br><sub>(108 MiB / 49.9% CPU)</sub> | **0.88×** | **0.24×** |
| HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **42013**<br><sub>(109 MiB / 44.5% CPU)</sub> | **9756**<br><sub>(108 MiB / 48.7% CPU)</sub> | **0.87×** | **0.20×** |
| HTTP/2 · TLS | HTTP/3 · QUIC | 🥇 **24834**<br><sub>(139 MiB / 49.7% CPU)</sub> | **24670**<br><sub>(152 MiB / 50.8% CPU)</sub> | **0.99×** | **0.98×** |
| HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **19193**<br><sub>(138 MiB / 50.6% CPU)</sub> | **13232**<br><sub>(136 MiB / 48.4% CPU)</sub> | **0.93×** | **0.64×** |
| HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **15251**<br><sub>(158 MiB / 48.7% CPU)</sub> | **9627**<br><sub>(153 MiB / 47.0% CPU)</sub> | **0.98×** | **0.62×** |
| HTTP/3 · QUIC | HTTP/2 · plain | 🥇 **24530**<br><sub>(152 MiB / 55.6% CPU)</sub> | **24462**<br><sub>(158 MiB / 55.7% CPU)</sub> | **0.93×** | **0.92×** |
| HTTP/3 · QUIC | HTTP/2 · TLS | 🥇 **21297**<br><sub>(157 MiB / 53.4% CPU)</sub> | **21013**<br><sub>(154 MiB / 54.0% CPU)</sub> | **0.92×** | **0.91×** |
| HTTP/3 · QUIC | HTTP/3 · QUIC | 🥇 **16680**<br><sub>(147 MiB / 48.6% CPU)</sub> | **16729**<br><sub>(154 MiB / 48.0% CPU)</sub> | **0.88×** | **0.88×** |
| HTTP/1 · plain (CONNECT) | HTTP/1 · TLS | 🥇 **14398**<br><sub>(118 MiB / 52.1% CPU)</sub> | **14522**<br><sub>(135 MiB / 51.5% CPU)</sub> | *n/a* | *n/a* |

### Tiny JSON reverse is nginx’s best case on Linux

The tables above use **~64 B keep-alive GET** on loopback. On Linux H1 reverse, nginx leads; YARP sits near TWP. Heavier bodies, POSTs, TLS handshake cost, and lossy/HOL workloads (below) change the picture. MITM rows remain TWP-only. nginx HTTP/3 is inbound-terminate only (see note above).

### Why isn’t HTTP/3 > HTTP/2 > HTTP/1 in raw RPS?

For **tiny JSON responses** (~64 B) on loopback, that ordering is **not** expected: topology (TLS hop count, terminate vs MITM) dominates; HTTP/2 and HTTP/3 help multiplexing, not single-origin tiny-GET RPS. See the **lossy** tables below for a workload where protocol design matters.

## Heavier reverse workloads

Separate from the tiny-GET matrix. Same measurement environments. Modes: `compare-bodies`, `compare-post`, `compare-lossy`, `compare-tls-cost`, `compare-arch` in [RpsLoadProbe](https://github.com/justcoding121/titanium-web-proxy/tree/develop/tools/RpsLoadProbe). **PUT with the same body is the same proxy work as POST; DELETE with no body matches GET** — only POST is published. Bodies/POST/lossy stay **half-duplex**. `compare-arch` is the slow-consumer / early-response / duplex set. Laptop numbers are on [Performance Local Lab](Performance-Local-Lab#architecture-sensitive); CI medians go in the tables below.

Lossy link = **userspace** shim (not kernel `netem`): TCP gets per-buffer delay + occasional whole-connection stalls (honest HOL for multiplexed H2); UDP gets per-datagram delay + drops (QUIC). `compare-lossy` publishes H1/H2/H3; H3 is where the protocol design is supposed to matter.

### Windows — heavier reverse GET (64 KiB / 256 KiB)

Median of **3** repeats on `windows-latest` @ `9d7c2966`. Source: Actions [32871900682](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32871900682) (`compare-bodies`). Warmup 2s / measure 8s. **RPS cells** include `(MiB / CPU%)` footprints.

| Body | Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---|---|---:|---:|---:|---:|---:|---:|
| 64 KiB | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **10,297**<br><sub>(99 MiB / 45.5% CPU)</sub> | **10,564**<br><sub>(99 MiB / 45.5% CPU)</sub> | **717**<br><sub>(137 MiB / 24.6% CPU)</sub> | **739**<br><sub>(137 MiB / 24.6% CPU)</sub> | **9,283**<br><sub>(110 MiB / 44.7% CPU)</sub> | **9,413**<br><sub>(110 MiB / 44.7% CPU)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **9,135**<br><sub>(168 MiB / 46.2% CPU)</sub> | **9,135**<br><sub>(168 MiB / 46.2% CPU)</sub> | **626**<br><sub>(137 MiB / 24.8% CPU)</sub> | **637**<br><sub>(137 MiB / 24.8% CPU)</sub> | **7,533**<br><sub>(108 MiB / 49.7% CPU)</sub> | **7,745**<br><sub>(108 MiB / 49.7% CPU)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **4,728**<br><sub>(108 MiB / 39.8% CPU)</sub> | **4,900**<br><sub>(108 MiB / 39.8% CPU)</sub> | *Not possible* (no QUIC) | *Not possible* (no QUIC) | **3,995**<br><sub>(177 MiB / 49.0% CPU)</sub> | **4,035**<br><sub>(177 MiB / 49.0% CPU)</sub> |
| 256 KiB | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **3,056**<br><sub>(88 MiB / 42.6% CPU)</sub> | **3,271**<br><sub>(88 MiB / 42.6% CPU)</sub> | **191**<br><sub>(136 MiB / 24.8% CPU)</sub> | **194**<br><sub>(136 MiB / 24.8% CPU)</sub> | **2,488**<br><sub>(115 MiB / 45.7% CPU)</sub> | **2,839**<br><sub>(115 MiB / 45.7% CPU)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **2,568**<br><sub>(115 MiB / 38.9% CPU)</sub> | **2,639**<br><sub>(115 MiB / 38.9% CPU)</sub> | **163**<br><sub>(137 MiB / 24.9% CPU)</sub> | **163**<br><sub>(137 MiB / 24.9% CPU)</sub> | **2,003**<br><sub>(132 MiB / 41.6% CPU)</sub> | **2,003**<br><sub>(132 MiB / 41.6% CPU)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **1,210**<br><sub>(88 MiB / 39.3% CPU)</sub> | **1,254**<br><sub>(88 MiB / 39.3% CPU)</sub> | *Not possible* (no QUIC) | *Not possible* (no QUIC) | **1,100**<br><sub>(125 MiB / 43.9% CPU)</sub> | **1,136**<br><sub>(125 MiB / 43.9% CPU)</sub> |

nginx/Windows collapses on large reverse bodies in this harness; treat as same-OS only. H1 TLS **64 KiB** ≈ **1.11×** YARP; **256 KiB** ≈ **1.23×**. H2→H1 64 KiB ≈ **1.21×**; H3→H1 64 KiB ≈ **1.18×**.

### Linux — heavier reverse GET (64 KiB / 256 KiB)

Median of **3** repeats @ `9d7c2966`. Source: Actions [32871900682](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32871900682) (`compare-bodies`). Warmup 2s / measure 8s.

| Body | Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---|---|---:|---:|---:|---:|---:|---:|
| 64 KiB | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **7,902**<br><sub>(170 MiB / 44.4% CPU)</sub> | **7,902**<br><sub>(170 MiB / 44.4% CPU)</sub> | **5,518**<br><sub>(96 MiB / 52.5% CPU)</sub> | **5,518**<br><sub>(96 MiB / 52.5% CPU)</sub> | **6,420**<br><sub>(158 MiB / 48.8% CPU)</sub> | **6,420**<br><sub>(158 MiB / 48.8% CPU)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **5,831**<br><sub>(218 MiB / 38.9% CPU)</sub> | **5,831**<br><sub>(218 MiB / 38.9% CPU)</sub> | **1,790**<br><sub>(95 MiB / 23.6% CPU)</sub> | **1,925**<br><sub>(95 MiB / 23.6% CPU)</sub> | **4,739**<br><sub>(161 MiB / 45.9% CPU)</sub> | **4,739**<br><sub>(161 MiB / 45.9% CPU)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **5,384**<br><sub>(177 MiB / 44.5% CPU)</sub> | **5,384**<br><sub>(177 MiB / 44.5% CPU)</sub> | **1,614**<br><sub>(113 MiB / 22.0% CPU)</sub> | **1,718**<br><sub>(113 MiB / 22.0% CPU)</sub> | **4,236**<br><sub>(219 MiB / 51.1% CPU)</sub> | **4,236**<br><sub>(219 MiB / 51.1% CPU)</sub> |
| 256 KiB | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **2,742**<br><sub>(121 MiB / 37.2% CPU)</sub> | **2,742**<br><sub>(121 MiB / 37.2% CPU)</sub> | **1,732**<br><sub>(95 MiB / 53.3% CPU)</sub> | **1,732**<br><sub>(95 MiB / 53.3% CPU)</sub> | **2,142**<br><sub>(171 MiB / 45.9% CPU)</sub> | **2,142**<br><sub>(171 MiB / 45.9% CPU)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **1,535**<br><sub>(206 MiB / 32.7% CPU)</sub> | **1,535**<br><sub>(206 MiB / 32.7% CPU)</sub> | **538**<br><sub>(96 MiB / 18.6% CPU)</sub> | **538**<br><sub>(96 MiB / 18.6% CPU)</sub> | **1,319**<br><sub>(164 MiB / 42.7% CPU)</sub> | **1,330**<br><sub>(164 MiB / 42.7% CPU)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **1,432**<br><sub>(148 MiB / 43.1% CPU)</sub> | **1,432**<br><sub>(148 MiB / 43.1% CPU)</sub> | **448**<br><sub>(107 MiB / 23.3% CPU)</sub> | **448**<br><sub>(107 MiB / 23.3% CPU)</sub> | **1,270**<br><sub>(218 MiB / 47.4% CPU)</sub> | **1,270**<br><sub>(218 MiB / 47.4% CPU)</sub> |

On this GHA pass TWP÷YARP H1 TLS ≈ **1.23×** (64 KiB) / **1.28×** (256 KiB); H2→H1 ≈ **1.23×** / **1.16×**; H3→H1 ≈ **1.27×** / **1.13×**. TWP÷nginx H1 TLS ≈ **1.43** / **1.58**. Absolute RPS swings by VM; prefer ratios.

### Windows — POST 64 KiB request + 64 KiB response

Median of **3** repeats on `windows-latest` @ `9d7c2966`. Source: Actions [32866714851](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32866714851) (`compare-post`).

| Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---|---:|---:|---:|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | 🥇 **8,612**<br><sub>(84 MiB / 42.0% CPU)</sub> | **9,133**<br><sub>(84 MiB / 42.0% CPU)</sub> | **497**<br><sub>(138 MiB / 24.8% CPU)</sub> | **538**<br><sub>(138 MiB / 24.8% CPU)</sub> | **5,837**<br><sub>(96 MiB / 56.0% CPU)</sub> | **6,283**<br><sub>(96 MiB / 56.0% CPU)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **6,241**<br><sub>(196 MiB / 49.7% CPU)</sub> | **6,242**<br><sub>(196 MiB / 49.7% CPU)</sub> | **486**<br><sub>(139 MiB / 24.9% CPU)</sub> | **493**<br><sub>(139 MiB / 24.9% CPU)</sub> | **5,106**<br><sub>(118 MiB / 50.9% CPU)</sub> | **5,147**<br><sub>(118 MiB / 50.9% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **2,691**<br><sub>(118 MiB / 43.7% CPU)</sub> | **2,937**<br><sub>(118 MiB / 43.7% CPU)</sub> | *Not possible* (no QUIC) | *Not possible* (no QUIC) | **2,549**<br><sub>(132 MiB / 49.5% CPU)</sub> | **2,747**<br><sub>(132 MiB / 49.5% CPU)</sub> |

TWP leads H1 POST (~**1.5×** YARP), H2 POST (~**1.2×** YARP), and H3 POST (~**1.1×** YARP).

### Linux — POST 64 KiB request + 64 KiB response

Median of **3** repeats @ `9d7c2966`. Source: Actions [32866714851](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32866714851) (`compare-post`).

| Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---|---:|---:|---:|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | 🥇 **6,746**<br><sub>(127 MiB / 41.8% CPU)</sub> | **6,746**<br><sub>(127 MiB / 41.8% CPU)</sub> | **5,443**<br><sub>(98 MiB / 44.8% CPU)</sub> | **5,443**<br><sub>(98 MiB / 44.8% CPU)</sub> | **4,410**<br><sub>(176 MiB / 53.8% CPU)</sub> | **4,410**<br><sub>(176 MiB / 53.8% CPU)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **4,720**<br><sub>(220 MiB / 47.0% CPU)</sub> | **4,720**<br><sub>(220 MiB / 47.0% CPU)</sub> | **2,362**<br><sub>(106 MiB / 21.8% CPU)</sub> | **2,475**<br><sub>(106 MiB / 21.8% CPU)</sub> | **3,616**<br><sub>(144 MiB / 48.3% CPU)</sub> | **3,626**<br><sub>(144 MiB / 48.3% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **3,306**<br><sub>(223 MiB / 43.7% CPU)</sub> | **3,306**<br><sub>(223 MiB / 43.7% CPU)</sub> | **835**<br><sub>(111 MiB / 24.6% CPU)</sub> | **835**<br><sub>(111 MiB / 24.6% CPU)</sub> | **2,959**<br><sub>(260 MiB / 51.5% CPU)</sub> | **2,959**<br><sub>(260 MiB / 51.5% CPU)</sub> |

Linux nginx H1/H2/H3 POST completed (nginx.org mainline). TWP÷YARP H1 ≈ **1.5×**; H2 ≈ **1.3×**; H3 ≈ **1.1×**. TWP÷nginx H3 ≈ **4×**.

### Windows — lossy / high-RTT (H2 HOL / H3 loss)

Userspace **5 ms** one-way delay + **1%** TCP connection stall (H1/H2) or UDP datagram drop (H3); **64 KiB** GET. H1/H2: median of **3** repeats on `windows-latest` @ `9d7c2966` — [32866717729](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32866717729) (`compare-lossy`). **H3:** GHA Windows userspace UDP shim collapses (sustain **0**); published H3 row is the laptop `quic-http3` remasure under the same delay/loss workload ([Performance Local Lab](Performance-Local-Lab#lossy--high-rtt-h2-hol--h3-packet-loss), `windows-20260822-lossy-h3-quic/).

| Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---|---:|---:|---:|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | 🥇 **663**<br><sub>(98 MiB / 4.8% CPU)</sub> | **663**<br><sub>(98 MiB / 4.8% CPU)</sub> | **634**<br><sub>(137 MiB / 19.6% CPU)</sub> | **634**<br><sub>(137 MiB / 19.6% CPU)</sub> | **662**<br><sub>(113 MiB / 5.7% CPU)</sub> | **662**<br><sub>(113 MiB / 5.7% CPU)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **59**<br><sub>(122 MiB / 1.7% CPU)</sub> | **88**<br><sub>(122 MiB / 1.7% CPU)</sub> | **18**<br><sub>(137 MiB / 0.5% CPU)</sub> | **18**<br><sub>(137 MiB / 0.5% CPU)</sub> | **18**<br><sub>(84 MiB / 1.1% CPU)</sub> | **18**<br><sub>(84 MiB / 1.1% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **1,572** | **1,572** | *Not possible* (no QUIC) | *Not possible* | **0** | **50** |

TWP H2 HOL leads (~**3.31×** YARP). H3 is the protocol-shape win vs H2 HOL on the same lossy session; Win H3 GHA remains 0 (laptop remasure kept above).

### Linux — lossy / high-RTT (H2 HOL / H3 loss)

Median of **3** repeats @ `9d7c2966`. Source: [32866717729](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32866717729) (`compare-lossy`; lossy H3 uses `quic-http3`).

| Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---|---:|---:|---:|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | **1,201**<br><sub>(142 MiB / 12.9% CPU)</sub> | **1,201**<br><sub>(142 MiB / 12.9% CPU)</sub> | 🥇 **1,206**<br><sub>(97 MiB / 11.8% CPU)</sub> | **1,206**<br><sub>(97 MiB / 11.8% CPU)</sub> | **1,197**<br><sub>(144 MiB / 16.6% CPU)</sub> | **1,197**<br><sub>(144 MiB / 16.6% CPU)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **309**<br><sub>(191 MiB / 6.5% CPU)</sub> | **309**<br><sub>(191 MiB / 6.5% CPU)</sub> | **40**<br><sub>(95 MiB / 0.3% CPU)</sub> | **42**<br><sub>(95 MiB / 0.3% CPU)</sub> | **40**<br><sub>(118 MiB / 1.4% CPU)</sub> | **44**<br><sub>(118 MiB / 1.4% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **332**<br><sub>(149 MiB / 13.3% CPU)</sub> | **332**<br><sub>(149 MiB / 13.3% CPU)</sub> | **88**<br><sub>(105 MiB / 2.7% CPU)</sub> | **88**<br><sub>(105 MiB / 2.7% CPU)</sub> | **330**<br><sub>(176 MiB / 17.9% CPU)</sub> | **330**<br><sub>(176 MiB / 17.9% CPU)</sub> |

TWP H2 HOL ≫ YARP (~**7.7×**). H3 TWP÷YARP ≈ **1×**.

### Architecture-sensitive

`compare-arch` isolates slow app readers, origin-early response, H2 duplex, and WebSocket echo. See [TWP vs YARP IO model](Performance-Profiling#twp-vs-yarp-io-model). Laptop 1-rep numbers are on [Performance Local Lab](Performance-Local-Lab#architecture-sensitive).

Median of **3** repeats on matched 4 vCPU / 16 GiB runners @ `9d7c2966` ([32866720742](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32866720742)) (`compare-arch`). Slow consumer = 256 KiB GET, 16 KiB read + 8 ms sleep. Early response = 64 KiB POST, origin writes after 8 KiB. Duplex H2 = overlapping 64 KiB POST on H2 TLS↔H2 TLS. WebSocket = echo round-trips/sec.

`compare-lossy` (slow **network**) is already published above; it is not a slow **app** reader.

#### Windows

| Scenario | Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---|---|---:|---:|---:|---:|---:|---:|
| Slow consumer (256 KiB GET, throttled client read) | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **248**<br><sub>(91 MiB / 3.8% CPU)</sub> | **248**<br><sub>(91 MiB / 3.8% CPU)</sub> | **209**<br><sub>(140 MiB / 24.7% CPU)</sub> | **209**<br><sub>(140 MiB / 24.7% CPU)</sub> | **248**<br><sub>(110 MiB / 4.9% CPU)</sub> | **248**<br><sub>(110 MiB / 4.9% CPU)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · TLS | HTTP/1 · plain | **248**<br><sub>(125 MiB / 3.8% CPU)</sub> | **248**<br><sub>(125 MiB / 3.8% CPU)</sub> | **166**<br><sub>(137 MiB / 24.6% CPU)</sub> | **166**<br><sub>(137 MiB / 24.6% CPU)</sub> | 🥇 **249**<br><sub>(112 MiB / 7.4% CPU)</sub> | **249**<br><sub>(112 MiB / 7.4% CPU)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **264**<br><sub>(99 MiB / 17.1% CPU)</sub> | **264**<br><sub>(99 MiB / 17.1% CPU)</sub> | *Not possible* (no QUIC) | *Not possible* (no QUIC) | **264**<br><sub>(160 MiB / 21.3% CPU)</sub> | **264**<br><sub>(160 MiB / 21.3% CPU)</sub> |
| Early response (origin writes after first request chunk) | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **6,507**<br><sub>(84 MiB / 43.0% CPU)</sub> | **6,881**<br><sub>(84 MiB / 43.0% CPU)</sub> | **347**<br><sub>(137 MiB / 24.9% CPU)</sub> | **419**<br><sub>(137 MiB / 24.9% CPU)</sub> | **3,256**<br><sub>(117 MiB / 39.5% CPU)</sub> | **3,606**<br><sub>(117 MiB / 39.5% CPU)</sub> |
| Early response (origin writes after first request chunk) | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **5,011**<br><sub>(153 MiB / 48.6% CPU)</sub> | **5,011**<br><sub>(153 MiB / 48.6% CPU)</sub> | **0**<br><sub>(139 MiB / 24.8% CPU)</sub> | **383**<br><sub>(139 MiB / 24.8% CPU)</sub> | **2,967**<br><sub>(97 MiB / 39.6% CPU)</sub> | **3,184**<br><sub>(97 MiB / 39.6% CPU)</sub> |
| Early response (origin writes after first request chunk) | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **2,153**<br><sub>(125 MiB / 42.8% CPU)</sub> | **2,256**<br><sub>(125 MiB / 42.8% CPU)</sub> | *Not possible* (no QUIC) | *Not possible* (no QUIC) | **1,874**<br><sub>(154 MiB / 53.3% CPU)</sub> | **2,034**<br><sub>(154 MiB / 53.3% CPU)</sub> |
| Duplex (both directions live) | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **784**<br><sub>(124 MiB / 13.2% CPU)</sub> | **1,270**<br><sub>(124 MiB / 13.2% CPU)</sub> | *Not possible* | *Not possible* | **18**<br><sub>(120 MiB / 30.7% CPU)</sub> | **2,135**<br><sub>(120 MiB / 30.7% CPU)</sub> |
| Duplex (WebSocket / extended CONNECT) | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **32,785**<br><sub>(97 MiB / 40.8% CPU)</sub> | **32,785**<br><sub>(97 MiB / 40.8% CPU)</sub> | **19,252**<br><sub>(138 MiB / 24.9% CPU)</sub> | **19,803**<br><sub>(138 MiB / 24.9% CPU)</sub> | **30,820**<br><sub>(85 MiB / 43.6% CPU)</sub> | **30,820**<br><sub>(85 MiB / 43.6% CPU)</sub> |

#### Linux

| Scenario | Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---|---|---:|---:|---:|---:|---:|---:|
| Slow consumer (256 KiB GET, throttled client read) | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **468**<br><sub>(115 MiB / 9.5% CPU)</sub> | **468**<br><sub>(115 MiB / 9.5% CPU)</sub> | **416**<br><sub>(96 MiB / 9.6% CPU)</sub> | **416**<br><sub>(96 MiB / 9.6% CPU)</sub> | **418**<br><sub>(138 MiB / 13.6% CPU)</sub> | **418**<br><sub>(138 MiB / 13.6% CPU)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · TLS | HTTP/1 · plain | **473**<br><sub>(131 MiB / 17.8% CPU)</sub> | **473**<br><sub>(131 MiB / 17.8% CPU)</sub> | **462**<br><sub>(96 MiB / 17.3% CPU)</sub> | **462**<br><sub>(96 MiB / 17.3% CPU)</sub> | 🥇 **474**<br><sub>(146 MiB / 23.5% CPU)</sub> | **474**<br><sub>(146 MiB / 23.5% CPU)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **474**<br><sub>(125 MiB / 34.3% CPU)</sub> | **474**<br><sub>(125 MiB / 34.3% CPU)</sub> | **119**<br><sub>(100 MiB / 11.4% CPU)</sub> | **238**<br><sub>(100 MiB / 11.4% CPU)</sub> | **472**<br><sub>(193 MiB / 40.0% CPU)</sub> | **472**<br><sub>(193 MiB / 40.0% CPU)</sub> |
| Early response (origin writes after first request chunk) | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **5,039**<br><sub>(149 MiB / 45.3% CPU)</sub> | **5,039**<br><sub>(149 MiB / 45.3% CPU)</sub> | **3,904**<br><sub>(98 MiB / 49.4% CPU)</sub> | **3,904**<br><sub>(98 MiB / 49.4% CPU)</sub> | **3,425**<br><sub>(175 MiB / 55.3% CPU)</sub> | **3,425**<br><sub>(175 MiB / 55.3% CPU)</sub> |
| Early response (origin writes after first request chunk) | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **3,661**<br><sub>(233 MiB / 44.1% CPU)</sub> | **3,661**<br><sub>(233 MiB / 44.1% CPU)</sub> | **0**<br><sub>(103 MiB / 24.6% CPU)</sub> | **1,803**<br><sub>(103 MiB / 24.6% CPU)</sub> | **2,370**<br><sub>(148 MiB / 47.6% CPU)</sub> | **2,570**<br><sub>(148 MiB / 47.6% CPU)</sub> |
| Early response (origin writes after first request chunk) | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **3,018**<br><sub>(193 MiB / 43.5% CPU)</sub> | **3,018**<br><sub>(193 MiB / 43.5% CPU)</sub> | **0**<br><sub>(114 MiB / 24.7% CPU)</sub> | **545**<br><sub>(114 MiB / 24.7% CPU)</sub> | **2,232**<br><sub>(262 MiB / 46.7% CPU)</sub> | **2,232**<br><sub>(262 MiB / 46.7% CPU)</sub> |
| Duplex (both directions live) | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **185**<br><sub>(124 MiB / 9.0% CPU)</sub> | **282**<br><sub>(124 MiB / 9.0% CPU)</sub> | *Not possible* | *Not possible* | **138**<br><sub>(139 MiB / 44.6% CPU)</sub> | **1,882**<br><sub>(139 MiB / 44.6% CPU)</sub> |
| Duplex (WebSocket / extended CONNECT) | HTTP/1 · TLS | HTTP/1 · plain | **35,088**<br><sub>(121 MiB / 44.5% CPU)</sub> | **35,088**<br><sub>(121 MiB / 44.5% CPU)</sub> | 🥇 **39,303**<br><sub>(97 MiB / 37.1% CPU)</sub> | **39,303**<br><sub>(97 MiB / 37.1% CPU)</sub> | **32,013**<br><sub>(122 MiB / 44.9% CPU)</sub> | **32,013**<br><sub>(122 MiB / 44.9% CPU)</sub> |

Slow consumer is sleep-bound; H1/H2/H3 sit in the same band. Early-response H1/H2/H3: TWP leads (H1 early ≈ **2.00×** / **1.47×** YARP Win/Linux). **Duplex H2**: YARP leads by design — Win ≈ **0.59×** (1,270 / 2,135), Linux ≈ **0.15×** (282 / 1,882); irreducible concurrent-copier cell (see [IO model](Performance-Profiling#twp-vs-yarp-io-model)). WebSocket: TWP÷YARP Windows ≈ **1.06×**; Linux nginx leads.

### TLS termination cost (H1 TLS → cleartext origin)

Isolates keep-alive tiny GET vs **new connection per request** (handshake-dominated) vs keep-alive **256 KiB**. Product comparison uses RPS and end-to-end latency; TWP can also capture `ClientTlsTiming` when `TWP_RPS_CAPTURE_TLS=1` (child process) — nginx/YARP have no equivalent hook.

#### Windows

Median of **3** repeats on `windows-latest` @ `9d7c2966`. Source: Actions [32866723562](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32866723562) (`compare-tls-cost`). Absolute RPS on GHA swings hard; prefer **TWP÷YARP**.

| Workload | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---:|---:|---:|---:|---:|---:|
| Keep-alive · tiny GET | 🥇 **20,709**<br><sub>(82 MiB / 48.3% CPU)</sub> | **20,709**<br><sub>(82 MiB / 48.3% CPU)</sub> | **9,096**<br><sub>(137 MiB / 24.8% CPU)</sub> | **9,158**<br><sub>(137 MiB / 24.8% CPU)</sub> | **17,934**<br><sub>(103 MiB / 48.1% CPU)</sub> | **17,934**<br><sub>(103 MiB / 48.1% CPU)</sub> |
| New-connection · tiny GET | 🥇 **739**<br><sub>(65 MiB / 10.4% CPU)</sub> | **745**<br><sub>(65 MiB / 10.4% CPU)</sub> | **252**<br><sub>(136 MiB / 24.2% CPU)</sub> | **256**<br><sub>(136 MiB / 24.2% CPU)</sub> | **591**<br><sub>(93 MiB / 10.1% CPU)</sub> | **607**<br><sub>(93 MiB / 10.1% CPU)</sub> |
| Keep-alive · 256 KiB GET | 🥇 **2,698**<br><sub>(87 MiB / 46.2% CPU)</sub> | **2,971**<br><sub>(87 MiB / 46.2% CPU)</sub> | **172**<br><sub>(137 MiB / 24.7% CPU)</sub> | **174**<br><sub>(137 MiB / 24.7% CPU)</sub> | **2,569**<br><sub>(134 MiB / 44.0% CPU)</sub> | **2,569**<br><sub>(134 MiB / 44.0% CPU)</sub> |

#### Linux

Median of **3** repeats @ `9d7c2966`. Source: Actions [32866723562](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32866723562) (`compare-tls-cost`).

| Workload | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---:|---:|---:|---:|---:|---:|
| Keep-alive · tiny GET | **23,687**<br><sub>(104 MiB / 49.1% CPU)</sub> | **23,687**<br><sub>(104 MiB / 49.1% CPU)</sub> | 🥇 **25,897**<br><sub>(95 MiB / 41.3% CPU)</sub> | **26,177**<br><sub>(95 MiB / 41.3% CPU)</sub> | **20,716**<br><sub>(133 MiB / 50.5% CPU)</sub> | **20,716**<br><sub>(133 MiB / 50.5% CPU)</sub> |
| New-connection · tiny GET | **962**<br><sub>(124 MiB / 47.5% CPU)</sub> | **973**<br><sub>(124 MiB / 47.5% CPU)</sub> | 🥇 **1,005**<br><sub>(96 MiB / 44.2% CPU)</sub> | **1,008**<br><sub>(96 MiB / 44.2% CPU)</sub> | **941**<br><sub>(148 MiB / 45.8% CPU)</sub> | **941**<br><sub>(148 MiB / 45.8% CPU)</sub> |
| Keep-alive · 256 KiB GET | 🥇 **2,690**<br><sub>(126 MiB / 36.5% CPU)</sub> | **2,690**<br><sub>(126 MiB / 36.5% CPU)</sub> | **1,749**<br><sub>(96 MiB / 53.6% CPU)</sub> | **1,749**<br><sub>(96 MiB / 53.6% CPU)</sub> | **2,146**<br><sub>(172 MiB / 45.9% CPU)</sub> | **2,146**<br><sub>(172 MiB / 45.9% CPU)</sub> |

All three workloads are **>1.00×** YARP on both OS. nginx leads Linux keep-alive tiny and Linux new-connection; TWP is second, YARP third.

## Other measurements

| What | Result |
|---|---|
| HTTPS TTFB vs direct (median, 14 hosts) | Cold **≈ parity** (−1 ms); warm **−25 ms** (proxy faster) |
| HTTP/1 loopback GET (no body intercept) | **~128 µs**, **~9.9 KB** allocated / request |
| Basic example footprint (Release, after load) | **~74 MB** working set · **~24–29 MB** private bytes |

```powershell
dotnet run -c Release --project benchmarks/Titanium.Web.Proxy.Benchmarks -- --filter '*Throughput*'
```

Local laptop BDN @ `9d7c2966` (Release):

| Benchmark | Setup | Mean | Allocated / op |
|---|---|---:|---:|
| HTTP/1 GET through proxy | Passthrough | **128 µs** | **9.9 KB** |
| HTTP/2 multiplexed GETs | 10 concurrent streams | **253 µs** / batch | **~4.4 KB** / request |

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
