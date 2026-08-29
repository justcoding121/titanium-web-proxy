# Performance

Titanium targets **low-overhead MITM proxying**: connection pooling, HTTP/2 multiplexing, and buffer reuse. Numbers below are **Release** measurements with [RpsLoadProbe](https://github.com/justcoding121/titanium-web-proxy/tree/develop/tools/RpsLoadProbe) (and BenchmarkDotNet / Basic example where noted). Publishable tables cite **GitHub Actions** medians on matched **4 vCPU / 16 GiB** runners. Absolute RPS still varies by OS kernel, TLS, and MsQuic packaging — compare **within a table**, not across Windows vs Linux.

Control arms: **nginx** (native C reverse-proxy ceiling; Linux is authoritative) and **YARP** (`Yarp.ReverseProxy`, managed .NET reverse proxy). Neither can MITM (no CONNECT / forged certs). FiddlerCore is not compared (commercial debugger license; not a throughput peer).

For pooling knobs and certificate first-visit tuning, see [Performance and pooling](Home#performance-and-pooling). For the local cool A/B lab, laptop tables, and profiling notes, see [Performance Profiling](Performance-Profiling).

## Contents

- [Measurement environment](#measurement-environment)
    - [Windows (GitHub-hosted `windows-latest`)](#windows-github-hosted-windows-latest)
    - [Linux (GitHub-hosted `ubuntu-latest`)](#linux-github-hosted-ubuntu-latest)
    - [Tiered cadence](#tiered-cadence)
    - [Saturation control](#saturation-control)
- [Windows — Titanium vs nginx vs YARP](#windows--titanium-vs-nginx-vs-yarp)
- [Linux — Titanium vs nginx vs YARP](#linux--titanium-vs-nginx-vs-yarp)
    - [Tiny JSON reverse is nginx’s best case on Linux](#tiny-json-reverse-is-nginxs-best-case-on-linux)
    - [Why isn’t HTTP/3 > HTTP/2 > HTTP/1 in raw RPS?](#why-isnt-http3--http2--http1-in-raw-rps)
- [Editions (CLI / Plus / Intercept)](#editions-cli--plus--intercept)
- [Cross-version (7.0 vs 6.0)](#cross-version-70-vs-60)
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

Both OS use the standard public-repo GitHub-hosted runner class (**4 vCPU / 16 GiB / 14 GB SSD**). Same harness knobs (`workflow_dispatch` [RPS saturation](https://github.com/justcoding121/titanium-web-proxy/actions/workflows/rps-saturation.yml): warmup 2s / measure 8s; concurrency 8, 16, 32, 64; median of 3 repeats; `--stop-on-slo-fail` default on). Every `--ramp` arm is **three OS processes** (parent load generator + origin child + proxy child), except **origin-direct** arms (load gen + origin only). Prefer **TWP÷YARP** / **TWP÷nginx** ratios over absolute RPS.

Laptop High-perf / cool-paired Windows numbers live on [Performance Local Lab](Performance-Local-Lab). Do not mix those absolutes into the tables below.

### Tiered cadence

| Tier | Mode | When |
|------|------|------|
| Daily / per-PR | `compare-spot` | minutes |
| Milestone | `compare-terminate` / `compare-matrix` | ~1–2h |
| Editions | `compare-editions` | ~60 min (CLI / Plus / Intercept stress arms) |
| Cross-version (Gate 2) | `compare-cross-version` | ~1–2h vs committed 6.0 baselines |
| Release / wiki | `compare-product` | ~3–4h |
| Heavier tables | `compare-bodies` / `post` / `lossy` / `arch` / `bridges` / `tls-cost` | dispatch independently |

See [PERF-GATES.md](https://github.com/justcoding121/titanium-web-proxy/blob/develop/tools/RpsLoadProbe/PERF-GATES.md).

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
- Product refresh: `compare-product` @ `acfb27e1` — [33087088466](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087088466). Heavier/saturation/tls:

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

Median of **3 repeats** on `windows-latest` (4 vCPU / 16 GiB). Bare reverse 5×5 @ `acfb27e1` — `compare-product` [33087088466](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087088466). Warmup 2s / measure 8s; concurrency 8, 16, 32, 64. Prefer TWP÷peer ratios over absolute RPS. **RPS cells** include median RSS / CPU at the peak-RPS step as `<br><sub>(MiB / CPU%)</sub>`. nginx terminate peers use `keepalive 256` + streaming buffers. Laptop High-perf / cool-paired numbers stay on the [local lab](Performance-Local-Lab).

**Load generators:** Reverse inbound H3 arms use **`dotnet-httpclient`** (`http_version=3.0`, `RequestVersionExact`). nginx/Windows is same-OS only (no QUIC).

| Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---|---:|---:|---:|---:|---:|---:|
| HTTP/1 · plain | HTTP/1 · plain | 🥇 **32525**<br><sub>(72 MiB / 48.2% CPU)</sub> | 🥇 **32525**<br><sub>(72 MiB / 48.2% CPU)</sub> | **19609**<br><sub>(120 MiB / 24.7% CPU)</sub> | **19609**<br><sub>(120 MiB / 24.7% CPU)</sub> | **27504**<br><sub>(87 MiB / 49.3% CPU)</sub> | **27504**<br><sub>(87 MiB / 49.3% CPU)</sub> |
| HTTP/1 · plain | HTTP/1 · TLS | 🥇 **27254**<br><sub>(84 MiB / 50.5% CPU)</sub> | 🥇 **27254**<br><sub>(84 MiB / 50.5% CPU)</sub> | *Not possible* | *Not possible* | **24546**<br><sub>(98 MiB / 50.4% CPU)</sub> | **24546**<br><sub>(98 MiB / 50.4% CPU)</sub> |
| HTTP/1 · plain | HTTP/2 · plain | 🥇 **43011**<br><sub>(101 MiB / 48.9% CPU)</sub> | 🥇 **43011**<br><sub>(101 MiB / 48.9% CPU)</sub> | *Not possible* | *Not possible* | **40913**<br><sub>(88 MiB / 50.4% CPU)</sub> | **40913**<br><sub>(88 MiB / 50.4% CPU)</sub> |
| HTTP/1 · plain | HTTP/2 · TLS | 🥇 **38231**<br><sub>(107 MiB / 47.1% CPU)</sub> | 🥇 **38231**<br><sub>(107 MiB / 47.1% CPU)</sub> | *Not possible* | *Not possible* | **36573**<br><sub>(101 MiB / 48.8% CPU)</sub> | **36573**<br><sub>(101 MiB / 48.8% CPU)</sub> |
| HTTP/1 · plain | HTTP/3 · QUIC | **21174**<br><sub>(109 MiB / 49.2% CPU)</sub> | **21174**<br><sub>(109 MiB / 49.2% CPU)</sub> | *Not possible (no QUIC)* | *Not possible (no QUIC)* | 🥇 **21494**<br><sub>(119 MiB / 50.7% CPU)</sub> | 🥇 **21494**<br><sub>(119 MiB / 50.7% CPU)</sub> |
| HTTP/1 · TLS | HTTP/1 · plain | 🥇 **26461**<br><sub>(83 MiB / 46.9% CPU)</sub> | 🥇 **26461**<br><sub>(83 MiB / 46.9% CPU)</sub> | **12992**<br><sub>(137 MiB / 24.7% CPU)</sub> | **12992**<br><sub>(137 MiB / 24.7% CPU)</sub> | **23267**<br><sub>(103 MiB / 49.5% CPU)</sub> | **23267**<br><sub>(103 MiB / 49.5% CPU)</sub> |
| HTTP/1 · TLS | HTTP/1 · TLS | 🥇 **24189**<br><sub>(83 MiB / 47.1% CPU)</sub> | 🥇 **24189**<br><sub>(83 MiB / 47.1% CPU)</sub> | *Not possible* | *Not possible* | **21332**<br><sub>(101 MiB / 48.5% CPU)</sub> | **21332**<br><sub>(101 MiB / 48.5% CPU)</sub> |
| HTTP/1 · TLS | HTTP/2 · plain | 🥇 **33986**<br><sub>(111 MiB / 47.9% CPU)</sub> | 🥇 **33986**<br><sub>(111 MiB / 47.9% CPU)</sub> | *Not possible* | *Not possible* | **33398**<br><sub>(108 MiB / 48.7% CPU)</sub> | **33398**<br><sub>(108 MiB / 48.7% CPU)</sub> |
| HTTP/1 · TLS | HTTP/2 · TLS | 🥇 **30890**<br><sub>(113 MiB / 46% CPU)</sub> | 🥇 **30890**<br><sub>(113 MiB / 46% CPU)</sub> | *Not possible* | *Not possible* | **30843**<br><sub>(106 MiB / 48.1% CPU)</sub> | **30843**<br><sub>(106 MiB / 48.1% CPU)</sub> |
| HTTP/1 · TLS | HTTP/3 · QUIC | 🥇 **19117**<br><sub>(113 MiB / 49.5% CPU)</sub> | 🥇 **19117**<br><sub>(113 MiB / 49.5% CPU)</sub> | *Not possible (no QUIC)* | *Not possible (no QUIC)* | **18913**<br><sub>(124 MiB / 51.2% CPU)</sub> | **18913**<br><sub>(124 MiB / 51.2% CPU)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | 🥇 **42486**<br><sub>(98 MiB / 50.4% CPU)</sub> | 🥇 **42486**<br><sub>(98 MiB / 50.4% CPU)</sub> | *Not possible* | *Not possible* | **39198**<br><sub>(85 MiB / 49.8% CPU)</sub> | **39198**<br><sub>(85 MiB / 49.8% CPU)</sub> |
| HTTP/2 · plain | HTTP/1 · TLS | 🥇 **36446**<br><sub>(104 MiB / 50.9% CPU)</sub> | 🥇 **36446**<br><sub>(104 MiB / 50.9% CPU)</sub> | *Not possible* | *Not possible* | **33101**<br><sub>(92 MiB / 50% CPU)</sub> | **33101**<br><sub>(92 MiB / 50% CPU)</sub> |
| HTTP/2 · plain | HTTP/2 · plain | 🥇 **92750**<br><sub>(72 MiB / 36.4% CPU)</sub> | 🥇 **92750**<br><sub>(72 MiB / 36.4% CPU)</sub> | *Not possible* | *Not possible* | **69751**<br><sub>(93 MiB / 50.1% CPU)</sub> | **69751**<br><sub>(93 MiB / 50.1% CPU)</sub> |
| HTTP/2 · plain | HTTP/2 · TLS | 🥇 **80271**<br><sub>(81 MiB / 35.9% CPU)</sub> | 🥇 **80271**<br><sub>(81 MiB / 35.9% CPU)</sub> | *Not possible* | *Not possible* | **60779**<br><sub>(98 MiB / 49.5% CPU)</sub> | **60779**<br><sub>(98 MiB / 49.5% CPU)</sub> |
| HTTP/2 · plain | HTTP/3 · QUIC | 🥇 **32652**<br><sub>(130 MiB / 52.1% CPU)</sub> | 🥇 **32652**<br><sub>(130 MiB / 52.1% CPU)</sub> | *Not possible (no QUIC)* | *Not possible (no QUIC)* | **31495**<br><sub>(123 MiB / 51% CPU)</sub> | **31495**<br><sub>(123 MiB / 51% CPU)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **40763**<br><sub>(101 MiB / 50.8% CPU)</sub> | 🥇 **40763**<br><sub>(101 MiB / 50.8% CPU)</sub> | **11581**<br><sub>(137 MiB / 24.3% CPU)</sub> | **11581**<br><sub>(137 MiB / 24.3% CPU)</sub> | **35553**<br><sub>(91 MiB / 50.8% CPU)</sub> | **35553**<br><sub>(91 MiB / 50.8% CPU)</sub> |
| HTTP/2 · TLS | HTTP/1 · TLS | 🥇 **35437**<br><sub>(105 MiB / 51.9% CPU)</sub> | 🥇 **35437**<br><sub>(105 MiB / 51.9% CPU)</sub> | *Not possible* | *Not possible* | **31286**<br><sub>(97 MiB / 50.2% CPU)</sub> | **31286**<br><sub>(97 MiB / 50.2% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | 🥇 **86433**<br><sub>(91 MiB / 36% CPU)</sub> | 🥇 **86433**<br><sub>(91 MiB / 36% CPU)</sub> | *Not possible* | *Not possible* | **59569**<br><sub>(103 MiB / 52.5% CPU)</sub> | **59569**<br><sub>(103 MiB / 52.5% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **76135**<br><sub>(88 MiB / 35.1% CPU)</sub> | 🥇 **76135**<br><sub>(88 MiB / 35.1% CPU)</sub> | *Not possible* | *Not possible* | **53908**<br><sub>(98 MiB / 50% CPU)</sub> | **53908**<br><sub>(98 MiB / 50% CPU)</sub> |
| HTTP/2 · TLS | HTTP/3 · QUIC | 🥇 **32094**<br><sub>(134 MiB / 52.6% CPU)</sub> | 🥇 **32094**<br><sub>(134 MiB / 52.6% CPU)</sub> | *Not possible (no QUIC)* | *Not possible (no QUIC)* | **28945**<br><sub>(125 MiB / 53.4% CPU)</sub> | **28945**<br><sub>(125 MiB / 53.4% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **19956**<br><sub>(107 MiB / 43.9% CPU)</sub> | 🥇 **19956**<br><sub>(107 MiB / 43.9% CPU)</sub> | *Not possible (no QUIC)* | *Not possible (no QUIC)* | **17543**<br><sub>(163 MiB / 48.6% CPU)</sub> | **17543**<br><sub>(163 MiB / 48.6% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **17585**<br><sub>(113 MiB / 43.1% CPU)</sub> | 🥇 **17585**<br><sub>(113 MiB / 43.1% CPU)</sub> | *Not possible* | *Not possible* | **17460**<br><sub>(163 MiB / 47.8% CPU)</sub> | **17460**<br><sub>(163 MiB / 47.8% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/2 · plain | 🥇 **29131**<br><sub>(120 MiB / 47% CPU)</sub> | 🥇 **29131**<br><sub>(120 MiB / 47% CPU)</sub> | *Not possible (no H3 to H2)* | *Not possible (no H3 to H2)* | **27165**<br><sub>(152 MiB / 48% CPU)</sub> | **27165**<br><sub>(152 MiB / 48% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | 🥇 **27017**<br><sub>(123 MiB / 46.4% CPU)</sub> | 🥇 **27017**<br><sub>(123 MiB / 46.4% CPU)</sub> | *Not possible (no H3 to H2)* | *Not possible (no H3 to H2)* | **25543**<br><sub>(169 MiB / 47.6% CPU)</sub> | **25543**<br><sub>(169 MiB / 47.6% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/3 · QUIC | 🥇 **17500**<br><sub>(123 MiB / 45.3% CPU)</sub> | 🥇 **17500**<br><sub>(123 MiB / 45.3% CPU)</sub> | *Not possible (no QUIC)* | *Not possible (no QUIC)* | **14335**<br><sub>(166 MiB / 50.6% CPU)</sub> | **14335**<br><sub>(166 MiB / 50.6% CPU)</sub> |

### MITM (TWP only)

Same Client×Origin wires with interception on (`compare-product` [33087088466](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087088466)). **Lite** = no-op handlers (unchanged-lite finish). **Full** = append-only header mutation (harness: one probe header each way; product: generic append-only relay via `MitmCompressedRelayHelper`). nginx/YARP cannot MITM. **Lite÷Reverse** / **Full÷Reverse** vs bare reverse (same job). Completion gate: Lite and Full ≥ **0.70×** reverse sustain @ c=64 (median of 3 GHA runs).

**v1 append-only relay (2026-08-27):** Pre-fix H2→H2 Full÷Reverse was **0.13–0.16×** ([32960766249](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32960766249)). Post-fix @ `df172718`: H2 plain→H2 plain Full **0.77–0.79×**, H3→H1 Full **0.91–0.93×**, all MITM arms ≥ **0.70×** on median of [33041445371](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33041445371), [33055267086](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33055267086), [33055272140](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33055272140).

**v2 drop-only + non-unique append (2026-08-27):** `MitmStaticRebuildHelper` rebuilds static HPACK/QPACK after 1–4 unique header drops; trailing non-unique appends stay on compressed relay. @ `acfb27e1`: all MITM arms ≥ **0.70×** on GHA median ([33087088466](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087088466), [33087091622](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087091622), [33105885748](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33105885748) Linux; [33087085235](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087085235), [33087088466](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087088466), [33087091622](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087091622) Windows). H2 plain→H2 plain Full **0.77–0.79×** (Win) / **0.78×** (Lin).

| Client | Origin | Lite sustain | Full sustain | Lite÷Reverse | Full÷Reverse |
|---|---|---:|---:|---:|---:|
| HTTP/1 · plain | HTTP/1 · plain | **31797**<br><sub>(73 MiB / 49.6% CPU)</sub> | **30751**<br><sub>(77 MiB / 48.5% CPU)</sub> | **0.98×** | **0.95×** |
| HTTP/1 · plain | HTTP/1 · TLS | **26384**<br><sub>(89 MiB / 50.2% CPU)</sub> | **26276**<br><sub>(93 MiB / 50.3% CPU)</sub> | **0.97×** | **0.96×** |
| HTTP/1 · plain | HTTP/2 · plain | **41954**<br><sub>(117 MiB / 50.7% CPU)</sub> | **40419**<br><sub>(118 MiB / 48.9% CPU)</sub> | **0.98×** | **0.94×** |
| HTTP/1 · plain | HTTP/2 · TLS | **37636**<br><sub>(114 MiB / 50.1% CPU)</sub> | **36683**<br><sub>(115 MiB / 49.7% CPU)</sub> | **0.98×** | **0.96×** |
| HTTP/1 · plain | HTTP/3 · QUIC | **21606**<br><sub>(110 MiB / 51.7% CPU)</sub> | **21080**<br><sub>(113 MiB / 51% CPU)</sub> | **1.02×** | **1×** |
| HTTP/1 · TLS | HTTP/1 · plain | **25850**<br><sub>(87 MiB / 49.7% CPU)</sub> | **25148**<br><sub>(91 MiB / 46.9% CPU)</sub> | **0.98×** | **0.95×** |
| HTTP/1 · TLS | HTTP/1 · TLS | **23528**<br><sub>(92 MiB / 48.7% CPU)</sub> | **23258**<br><sub>(92 MiB / 47.7% CPU)</sub> | **0.97×** | **0.96×** |
| HTTP/1 · TLS | HTTP/2 · plain | **32389**<br><sub>(125 MiB / 48.4% CPU)</sub> | **32241**<br><sub>(125 MiB / 47.8% CPU)</sub> | **0.95×** | **0.95×** |
| HTTP/1 · TLS | HTTP/2 · TLS | **30316**<br><sub>(122 MiB / 49% CPU)</sub> | **29985**<br><sub>(122 MiB / 45.7% CPU)</sub> | **0.98×** | **0.97×** |
| HTTP/1 · TLS | HTTP/3 · QUIC | **18714**<br><sub>(115 MiB / 51.5% CPU)</sub> | **18546**<br><sub>(113 MiB / 50.8% CPU)</sub> | **0.98×** | **0.97×** |
| HTTP/2 · plain | HTTP/1 · plain | **40688**<br><sub>(97 MiB / 52.2% CPU)</sub> | **40132**<br><sub>(92 MiB / 53.3% CPU)</sub> | **0.96×** | **0.94×** |
| HTTP/2 · plain | HTTP/1 · TLS | **35409**<br><sub>(103 MiB / 53.1% CPU)</sub> | **34404**<br><sub>(101 MiB / 52% CPU)</sub> | **0.97×** | **0.94×** |
| HTTP/2 · plain | HTTP/2 · plain | **75435**<br><sub>(73 MiB / 50.5% CPU)</sub> | **72871**<br><sub>(70 MiB / 50.7% CPU)</sub> | **0.81×** | **0.79×** |
| HTTP/2 · plain | HTTP/2 · TLS | **66584**<br><sub>(87 MiB / 47% CPU)</sub> | **64779**<br><sub>(81 MiB / 46.6% CPU)</sub> | **0.83×** | **0.81×** |
| HTTP/2 · plain | HTTP/3 · QUIC | **32476**<br><sub>(126 MiB / 53.1% CPU)</sub> | **31706**<br><sub>(126 MiB / 54% CPU)</sub> | **0.99×** | **0.97×** |
| HTTP/2 · TLS | HTTP/1 · plain | **39532**<br><sub>(101 MiB / 50.9% CPU)</sub> | **38678**<br><sub>(102 MiB / 53.2% CPU)</sub> | **0.97×** | **0.95×** |
| HTTP/2 · TLS | HTTP/1 · TLS | **33965**<br><sub>(102 MiB / 53.4% CPU)</sub> | **33418**<br><sub>(102 MiB / 52.3% CPU)</sub> | **0.96×** | **0.94×** |
| HTTP/2 · TLS | HTTP/2 · plain | **73247**<br><sub>(90 MiB / 48.3% CPU)</sub> | **70400**<br><sub>(90 MiB / 48.2% CPU)</sub> | **0.85×** | **0.81×** |
| HTTP/2 · TLS | HTTP/2 · TLS | **65150**<br><sub>(89 MiB / 45.8% CPU)</sub> | **62910**<br><sub>(92 MiB / 45.6% CPU)</sub> | **0.86×** | **0.83×** |
| HTTP/2 · TLS | HTTP/3 · QUIC | **31089**<br><sub>(137 MiB / 54.2% CPU)</sub> | **31210**<br><sub>(139 MiB / 53.8% CPU)</sub> | **0.97×** | **0.97×** |
| HTTP/3 · QUIC | HTTP/1 · plain | **17969**<br><sub>(115 MiB / 45.6% CPU)</sub> | **18180**<br><sub>(108 MiB / 44.7% CPU)</sub> | **0.9×** | **0.91×** |
| HTTP/3 · QUIC | HTTP/1 · TLS | **16297**<br><sub>(127 MiB / 44.8% CPU)</sub> | **16040**<br><sub>(123 MiB / 45.9% CPU)</sub> | **0.93×** | **0.91×** |
| HTTP/3 · QUIC | HTTP/2 · plain | **27209**<br><sub>(128 MiB / 48.8% CPU)</sub> | **26679**<br><sub>(130 MiB / 49.4% CPU)</sub> | **0.93×** | **0.92×** |
| HTTP/3 · QUIC | HTTP/2 · TLS | **25629**<br><sub>(136 MiB / 49.9% CPU)</sub> | **25139**<br><sub>(139 MiB / 48.1% CPU)</sub> | **0.95×** | **0.93×** |
| HTTP/3 · QUIC | HTTP/3 · QUIC | **19028**<br><sub>(121 MiB / 46.2% CPU)</sub> | **15493**<br><sub>(120 MiB / 47.4% CPU)</sub> | **1.09×** | **0.89×** |

## Linux — Titanium vs nginx vs YARP

### Reverse

Median of **3 repeats** on `ubuntu-latest` (4 vCPU / 16 GiB). Bare reverse 5×5 @ `acfb27e1` — `compare-product` [33087088466](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087088466). Warmup 2s / measure 8s; concurrency 8, 16, 32, 64. **Linux nginx is the authoritative nginx baseline.** nginx terminate peers use `keepalive 256` + streaming buffers. The RPS workflow installs nginx.org mainline (`http_v3_module`) and `libmsquic`. Prefer ratios over absolute RPS.

| Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---|---:|---:|---:|---:|---:|---:|
| HTTP/1 · plain | HTTP/1 · plain | **32755**<br><sub>(82 MiB / 50.3% CPU)</sub> | **32755**<br><sub>(82 MiB / 50.3% CPU)</sub> | 🥇 **38827**<br><sub>(72 MiB / 41.2% CPU)</sub> | 🥇 **38827**<br><sub>(72 MiB / 41.2% CPU)</sub> | **27943**<br><sub>(114 MiB / 49.8% CPU)</sub> | **27943**<br><sub>(114 MiB / 49.8% CPU)</sub> |
| HTTP/1 · plain | HTTP/1 · TLS | 🥇 **23666**<br><sub>(101 MiB / 50% CPU)</sub> | 🥇 **23666**<br><sub>(101 MiB / 50% CPU)</sub> | *Not possible* | *Not possible* | **21689**<br><sub>(129 MiB / 50.3% CPU)</sub> | **21689**<br><sub>(129 MiB / 50.3% CPU)</sub> |
| HTTP/1 · plain | HTTP/2 · plain | 🥇 **38108**<br><sub>(130 MiB / 51.6% CPU)</sub> | 🥇 **38108**<br><sub>(130 MiB / 51.6% CPU)</sub> | *Not possible* | *Not possible* | **35233**<br><sub>(126 MiB / 49.1% CPU)</sub> | **35233**<br><sub>(126 MiB / 49.1% CPU)</sub> |
| HTTP/1 · plain | HTTP/2 · TLS | 🥇 **31414**<br><sub>(136 MiB / 50.3% CPU)</sub> | 🥇 **31414**<br><sub>(136 MiB / 50.3% CPU)</sub> | *Not possible* | *Not possible* | **29740**<br><sub>(132 MiB / 47.8% CPU)</sub> | **29740**<br><sub>(132 MiB / 47.8% CPU)</sub> |
| HTTP/1 · plain | HTTP/3 · QUIC | 🥇 **22431**<br><sub>(133 MiB / 52.9% CPU)</sub> | 🥇 **22431**<br><sub>(133 MiB / 52.9% CPU)</sub> | *Not possible (no QUIC)* | *Not possible (no QUIC)* | **21353**<br><sub>(150 MiB / 49.3% CPU)</sub> | **21353**<br><sub>(150 MiB / 49.3% CPU)</sub> |
| HTTP/1 · TLS | HTTP/1 · plain | **23525**<br><sub>(104 MiB / 49.5% CPU)</sub> | **23525**<br><sub>(104 MiB / 49.5% CPU)</sub> | 🥇 **28076**<br><sub>(97 MiB / 41.3% CPU)</sub> | 🥇 **28076**<br><sub>(97 MiB / 41.3% CPU)</sub> | **20655**<br><sub>(135 MiB / 50.7% CPU)</sub> | **20655**<br><sub>(135 MiB / 50.7% CPU)</sub> |
| HTTP/1 · TLS | HTTP/1 · TLS | 🥇 **19570**<br><sub>(105 MiB / 48.4% CPU)</sub> | 🥇 **19570**<br><sub>(105 MiB / 48.4% CPU)</sub> | *Not possible* | *Not possible* | **17196**<br><sub>(133 MiB / 49.6% CPU)</sub> | **17196**<br><sub>(133 MiB / 49.6% CPU)</sub> |
| HTTP/1 · TLS | HTTP/2 · plain | 🥇 **27378**<br><sub>(146 MiB / 50.1% CPU)</sub> | 🥇 **27378**<br><sub>(146 MiB / 50.1% CPU)</sub> | *Not possible* | *Not possible* | **24990**<br><sub>(140 MiB / 49.2% CPU)</sub> | **24990**<br><sub>(140 MiB / 49.2% CPU)</sub> |
| HTTP/1 · TLS | HTTP/2 · TLS | 🥇 **23743**<br><sub>(148 MiB / 48.7% CPU)</sub> | 🥇 **23743**<br><sub>(148 MiB / 48.7% CPU)</sub> | *Not possible* | *Not possible* | **22430**<br><sub>(140 MiB / 47.7% CPU)</sub> | **22430**<br><sub>(140 MiB / 47.7% CPU)</sub> |
| HTTP/1 · TLS | HTTP/3 · QUIC | 🥇 **17521**<br><sub>(142 MiB / 51.9% CPU)</sub> | 🥇 **17521**<br><sub>(142 MiB / 51.9% CPU)</sub> | *Not possible (no QUIC)* | *Not possible (no QUIC)* | **16465**<br><sub>(158 MiB / 49.8% CPU)</sub> | **16465**<br><sub>(158 MiB / 49.8% CPU)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | 🥇 **38052**<br><sub>(113 MiB / 53% CPU)</sub> | 🥇 **38052**<br><sub>(113 MiB / 53% CPU)</sub> | *Not possible* | *Not possible* | **34400**<br><sub>(112 MiB / 50.1% CPU)</sub> | **34400**<br><sub>(112 MiB / 50.1% CPU)</sub> |
| HTTP/2 · plain | HTTP/1 · TLS | 🥇 **28089**<br><sub>(120 MiB / 51.3% CPU)</sub> | 🥇 **28089**<br><sub>(120 MiB / 51.3% CPU)</sub> | *Not possible* | *Not possible* | **25844**<br><sub>(126 MiB / 50.3% CPU)</sub> | **25844**<br><sub>(126 MiB / 50.3% CPU)</sub> |
| HTTP/2 · plain | HTTP/2 · plain | 🥇 **65752**<br><sub>(99 MiB / 40.2% CPU)</sub> | 🥇 **65752**<br><sub>(99 MiB / 40.2% CPU)</sub> | *Not possible* | *Not possible* | **48901**<br><sub>(123 MiB / 46.8% CPU)</sub> | **48901**<br><sub>(123 MiB / 46.8% CPU)</sub> |
| HTTP/2 · plain | HTTP/2 · TLS | 🥇 **50899**<br><sub>(109 MiB / 39.7% CPU)</sub> | 🥇 **50899**<br><sub>(109 MiB / 39.7% CPU)</sub> | *Not possible* | *Not possible* | **39192**<br><sub>(124 MiB / 45.8% CPU)</sub> | **39192**<br><sub>(124 MiB / 45.8% CPU)</sub> |
| HTTP/2 · plain | HTTP/3 · QUIC | 🥇 **26614**<br><sub>(146 MiB / 50.6% CPU)</sub> | 🥇 **26614**<br><sub>(146 MiB / 50.6% CPU)</sub> | *Not possible (no QUIC)* | *Not possible (no QUIC)* | **26142**<br><sub>(148 MiB / 46.6% CPU)</sub> | **26142**<br><sub>(148 MiB / 46.6% CPU)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **34556**<br><sub>(121 MiB / 52.1% CPU)</sub> | 🥇 **34556**<br><sub>(121 MiB / 52.1% CPU)</sub> | **15326**<br><sub>(97 MiB / 19.3% CPU)</sub> | **15326**<br><sub>(97 MiB / 19.3% CPU)</sub> | **29570**<br><sub>(121 MiB / 50% CPU)</sub> | **29570**<br><sub>(121 MiB / 50% CPU)</sub> |
| HTTP/2 · TLS | HTTP/1 · TLS | 🥇 **26497**<br><sub>(118 MiB / 50.4% CPU)</sub> | 🥇 **26497**<br><sub>(118 MiB / 50.4% CPU)</sub> | *Not possible* | *Not possible* | **23247**<br><sub>(129 MiB / 50.1% CPU)</sub> | **23247**<br><sub>(129 MiB / 50.1% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | 🥇 **59314**<br><sub>(110 MiB / 39.9% CPU)</sub> | 🥇 **59314**<br><sub>(110 MiB / 39.9% CPU)</sub> | *Not possible* | *Not possible* | **39916**<br><sub>(123 MiB / 47% CPU)</sub> | **39916**<br><sub>(123 MiB / 47% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **48903**<br><sub>(116 MiB / 39.4% CPU)</sub> | 🥇 **48903**<br><sub>(116 MiB / 39.4% CPU)</sub> | *Not possible* | *Not possible* | **34000**<br><sub>(126 MiB / 46% CPU)</sub> | **34000**<br><sub>(126 MiB / 46% CPU)</sub> |
| HTTP/2 · TLS | HTTP/3 · QUIC | 🥇 **25230**<br><sub>(147 MiB / 49.5% CPU)</sub> | 🥇 **25230**<br><sub>(147 MiB / 49.5% CPU)</sub> | *Not possible (no QUIC)* | *Not possible (no QUIC)* | **22753**<br><sub>(155 MiB / 46.6% CPU)</sub> | **22753**<br><sub>(155 MiB / 46.6% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **20440**<br><sub>(143 MiB / 50.5% CPU)</sub> | 🥇 **20440**<br><sub>(143 MiB / 50.5% CPU)</sub> | **0**<br><sub>(103 MiB / 22.5% CPU)</sub> | **15123**<br><sub>(103 MiB / 22.5% CPU)</sub> | **18428**<br><sub>(181 MiB / 50.1% CPU)</sub> | **18428**<br><sub>(181 MiB / 50.1% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **16026**<br><sub>(151 MiB / 47.7% CPU)</sub> | 🥇 **16026**<br><sub>(151 MiB / 47.7% CPU)</sub> | *Not possible* | *Not possible* | **15410**<br><sub>(192 MiB / 50.8% CPU)</sub> | **15410**<br><sub>(192 MiB / 50.8% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/2 · plain | 🥇 **27106**<br><sub>(140 MiB / 53.8% CPU)</sub> | 🥇 **27106**<br><sub>(140 MiB / 53.8% CPU)</sub> | *Not possible (no H3 to H2)* | *Not possible (no H3 to H2)* | **23921**<br><sub>(195 MiB / 48.7% CPU)</sub> | **23921**<br><sub>(195 MiB / 48.7% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | 🥇 **23376**<br><sub>(147 MiB / 51.8% CPU)</sub> | 🥇 **23376**<br><sub>(147 MiB / 51.8% CPU)</sub> | *Not possible (no H3 to H2)* | *Not possible (no H3 to H2)* | **21030**<br><sub>(191 MiB / 47.3% CPU)</sub> | **21030**<br><sub>(191 MiB / 47.3% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/3 · QUIC | 🥇 **19912**<br><sub>(153 MiB / 46.1% CPU)</sub> | 🥇 **19912**<br><sub>(153 MiB / 46.1% CPU)</sub> | *Not possible (no QUIC)* | *Not possible (no QUIC)* | **15969**<br><sub>(193 MiB / 47.6% CPU)</sub> | **15969**<br><sub>(193 MiB / 47.6% CPU)</sub> |

### MITM (TWP only)

Same Client×Origin wires with interception on (`compare-product` [33087088466](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087088466)). **Lite** = no-op handlers (unchanged-lite finish). **Full** = append-only header mutation (harness: one probe header each way; product: generic append-only relay via `MitmCompressedRelayHelper`). nginx/YARP cannot MITM. **Lite÷Reverse** / **Full÷Reverse** vs bare reverse (same job). Completion gate: Lite and Full ≥ **0.70×** reverse sustain @ c=64 (median of 3 GHA runs).

**v1 append-only relay (2026-08-27):** Pre-fix H2→H2 Full÷Reverse was **0.13–0.16×** ([32960766249](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32960766249)). Post-fix @ `df172718`: H2 plain→H2 plain Full **0.77–0.79×**, H3→H1 Full **0.91–0.93×**, all MITM arms ≥ **0.70×** on median of [33041445371](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33041445371), [33055267086](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33055267086), [33055272140](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33055272140).

**v2 drop-only + non-unique append (2026-08-27):** `MitmStaticRebuildHelper` rebuilds static HPACK/QPACK after 1–4 unique header drops; trailing non-unique appends stay on compressed relay. @ `acfb27e1`: all MITM arms ≥ **0.70×** on GHA median ([33087088466](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087088466), [33087091622](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087091622), [33105885748](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33105885748) Linux; [33087085235](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087085235), [33087088466](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087088466), [33087091622](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087091622) Windows). H2 plain→H2 plain Full **0.77–0.79×** (Win) / **0.78×** (Lin).

| Client | Origin | Lite sustain | Full sustain | Lite÷Reverse | Full÷Reverse |
|---|---|---:|---:|---:|---:|
| HTTP/1 · plain | HTTP/1 · plain | **31444**<br><sub>(88 MiB / 50.6% CPU)</sub> | **30821**<br><sub>(89 MiB / 50.9% CPU)</sub> | **0.96×** | **0.94×** |
| HTTP/1 · plain | HTTP/1 · TLS | **23894**<br><sub>(107 MiB / 51.2% CPU)</sub> | **23188**<br><sub>(107 MiB / 51.4% CPU)</sub> | **1.01×** | **0.98×** |
| HTTP/1 · plain | HTTP/2 · plain | **37618**<br><sub>(136 MiB / 52.9% CPU)</sub> | **37663**<br><sub>(137 MiB / 53% CPU)</sub> | **0.99×** | **0.99×** |
| HTTP/1 · plain | HTTP/2 · TLS | **30669**<br><sub>(142 MiB / 51.6% CPU)</sub> | **29667**<br><sub>(145 MiB / 51.6% CPU)</sub> | **0.98×** | **0.94×** |
| HTTP/1 · plain | HTTP/3 · QUIC | **22046**<br><sub>(138 MiB / 53% CPU)</sub> | **21676**<br><sub>(132 MiB / 53.6% CPU)</sub> | **0.98×** | **0.97×** |
| HTTP/1 · TLS | HTTP/1 · plain | **22746**<br><sub>(108 MiB / 50.5% CPU)</sub> | **22497**<br><sub>(108 MiB / 50.7% CPU)</sub> | **0.97×** | **0.96×** |
| HTTP/1 · TLS | HTTP/1 · TLS | **19416**<br><sub>(110 MiB / 49.8% CPU)</sub> | **18914**<br><sub>(111 MiB / 49% CPU)</sub> | **0.99×** | **0.97×** |
| HTTP/1 · TLS | HTTP/2 · plain | **26252**<br><sub>(156 MiB / 51.4% CPU)</sub> | **25475**<br><sub>(158 MiB / 50.8% CPU)</sub> | **0.96×** | **0.93×** |
| HTTP/1 · TLS | HTTP/2 · TLS | **22934**<br><sub>(156 MiB / 49.7% CPU)</sub> | **22165**<br><sub>(155 MiB / 50.2% CPU)</sub> | **0.97×** | **0.93×** |
| HTTP/1 · TLS | HTTP/3 · QUIC | **17465**<br><sub>(149 MiB / 53.1% CPU)</sub> | **17074**<br><sub>(156 MiB / 52.9% CPU)</sub> | **1×** | **0.97×** |
| HTTP/2 · plain | HTTP/1 · plain | **36932**<br><sub>(117 MiB / 54.2% CPU)</sub> | **35097**<br><sub>(114 MiB / 53.4% CPU)</sub> | **0.97×** | **0.92×** |
| HTTP/2 · plain | HTTP/1 · TLS | **28063**<br><sub>(123 MiB / 52.3% CPU)</sub> | **26540**<br><sub>(128 MiB / 51.9% CPU)</sub> | **1×** | **0.94×** |
| HTTP/2 · plain | HTTP/2 · plain | **55285**<br><sub>(111 MiB / 49.6% CPU)</sub> | **51175**<br><sub>(109 MiB / 48.9% CPU)</sub> | **0.84×** | **0.78×** |
| HTTP/2 · plain | HTTP/2 · TLS | **44079**<br><sub>(106 MiB / 47% CPU)</sub> | **41560**<br><sub>(106 MiB / 45.9% CPU)</sub> | **0.87×** | **0.82×** |
| HTTP/2 · plain | HTTP/3 · QUIC | **26736**<br><sub>(142 MiB / 50.4% CPU)</sub> | **26411**<br><sub>(146 MiB / 51% CPU)</sub> | **1×** | **0.99×** |
| HTTP/2 · TLS | HTTP/1 · plain | **34094**<br><sub>(117 MiB / 53.2% CPU)</sub> | **33044**<br><sub>(122 MiB / 53.1% CPU)</sub> | **0.99×** | **0.96×** |
| HTTP/2 · TLS | HTTP/1 · TLS | **26666**<br><sub>(122 MiB / 51.8% CPU)</sub> | **25291**<br><sub>(123 MiB / 51.5% CPU)</sub> | **1.01×** | **0.95×** |
| HTTP/2 · TLS | HTTP/2 · plain | **49372**<br><sub>(114 MiB / 48.6% CPU)</sub> | **46927**<br><sub>(111 MiB / 47.3% CPU)</sub> | **0.83×** | **0.79×** |
| HTTP/2 · TLS | HTTP/2 · TLS | **40654**<br><sub>(113 MiB / 45.8% CPU)</sub> | **39146**<br><sub>(113 MiB / 44.9% CPU)</sub> | **0.83×** | **0.8×** |
| HTTP/2 · TLS | HTTP/3 · QUIC | **24651**<br><sub>(151 MiB / 50.1% CPU)</sub> | **24568**<br><sub>(151 MiB / 50.8% CPU)</sub> | **0.98×** | **0.97×** |
| HTTP/3 · QUIC | HTTP/1 · plain | **19354**<br><sub>(143 MiB / 51.3% CPU)</sub> | **19096**<br><sub>(145 MiB / 51.7% CPU)</sub> | **0.95×** | **0.93×** |
| HTTP/3 · QUIC | HTTP/1 · TLS | **15012**<br><sub>(156 MiB / 48.7% CPU)</sub> | **15042**<br><sub>(157 MiB / 49.2% CPU)</sub> | **0.94×** | **0.94×** |
| HTTP/3 · QUIC | HTTP/2 · plain | **25641**<br><sub>(158 MiB / 55.7% CPU)</sub> | **25316**<br><sub>(157 MiB / 55.5% CPU)</sub> | **0.95×** | **0.93×** |
| HTTP/3 · QUIC | HTTP/2 · TLS | **22250**<br><sub>(153 MiB / 53.7% CPU)</sub> | **22127**<br><sub>(159 MiB / 54.2% CPU)</sub> | **0.95×** | **0.95×** |
| HTTP/3 · QUIC | HTTP/3 · QUIC | **17601**<br><sub>(151 MiB / 48.8% CPU)</sub> | **17265**<br><sub>(152 MiB / 48.4% CPU)</sub> | **0.88×** | **0.87×** |

## Editions (CLI / Plus / Intercept)

**Note:** `twp-reverse-http1` and other library rows use Core with **probe-tuned** settings (no logging, no Via header, probe-warmed certs). Edition rows use `titanium run -c twp.yaml` **product defaults** — prefer the ÷baseline ratio column over absolute RPS. Inspector GUI is not spawnable in the harness; session-path overhead is `twp-cli-intercept-http1` (route `RequestHeaderSet` transform).

Gates prioritize **SLO survival under load** and **reasonable overhead vs baseline**. Pre-origin Plus middleware (CIDR/WAF/JWT/rate-limit) runs on H1 terminate-lite without `SessionEventArgs`; JWT caches successful bearer validations. Thresholds — see [PERF-GATES.md](https://github.com/justcoding121/titanium-web-proxy/blob/develop/tools/RpsLoadProbe/PERF-GATES.md).

Median of **3** repeats @ `6d2a7c9d`. Source: Actions [33259699099](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33259699099) (`compare-editions`). Warmup 2s / measure 8s; concurrency 8–64; sustain = median peak RPS among SLO-pass steps @ **c=64**. **RPS cells** include `(MiB / CPU%)` at that step.

```powershell
pwsh tools/RpsLoadProbe/run-rps.ps1 -Mode compare-editions
pwsh tools/RpsLoadProbe/validate-edition-gates.ps1 -CsvPath tools/RpsLoadProbe/results/rps-ramp-*.csv
```

| Arm | Win sustain | Win peak | Linux sustain | Linux peak | Win÷ | Lin÷ | Gate |
|---|---:|---:|---:|---:|---:|---:|---:|
| `twp-cli-reverse-http1` vs library | **32214**<br><sub>(118 MiB / 43.4% CPU)</sub> | **32219** | **47734**<br><sub>(151 MiB / 49.0% CPU)</sub> | **47844** | **1.02×** | **1.04×** | ≥ **0.80×** |
| `twp-cli-reverse-http1-tls` vs library TLS | **26495**<br><sub>(138 MiB / 48.3% CPU)</sub> | **26651** | **36932**<br><sub>(174 MiB / 48.5% CPU)</sub> | **37200** | **1.01×** | **1.00×** | ≥ **0.80×** |
| `twp-cli-reverse-http1-route` vs CLI | **32394**<br><sub>(120 MiB / 47.7% CPU)</sub> | **32428** | **47369**<br><sub>(149 MiB / 49.2% CPU)</sub> | **47398** | **1.01×** | **0.99×** | ≥ **0.90×** |
| `twp-cli-plus-base-http1` vs CLI | **32475**<br><sub>(123 MiB / 46.0% CPU)</sub> | **32695** | **47322**<br><sub>(154 MiB / 49.8% CPU)</sub> | **47744** | **1.01×** | **0.99×** | ≥ **0.90×** |
| `twp-cli-plus-cache-http1` (cold) vs CLI | **34029**<br><sub>(118 MiB / 64.5% CPU)</sub> | **34460** | **49409**<br><sub>(148 MiB / 62.3% CPU)</sub> | **50104** | **1.06×** | **1.04×** | ≥ **0.70×** |
| `twp-cli-intercept-http1` vs CLI | **25219**<br><sub>(124 MiB / 54.0% CPU)</sub> | **25377** | **35969**<br><sub>(155 MiB / 51.2% CPU)</sub> | **35977** | **0.78×** | **0.75×** | ≥ **0.70×** |
| `twp-cli-plus-waf-http1` vs CLI | **31653**<br><sub>(124 MiB / 46.8% CPU)</sub> | **31837** | **46367**<br><sub>(151 MiB / 49.6% CPU)</sub> | **46685** | **0.98×** | **0.97×** | ≥ **0.80×** |
| `twp-cli-plus-cidr-http1` vs CLI | **31730**<br><sub>(123 MiB / 51.1% CPU)</sub> | **31757** | **46828**<br><sub>(150 MiB / 50.2% CPU)</sub> | **47093** | **0.98×** | **0.98×** | ≥ **0.80×** |
| `twp-cli-plus-jwt-http1` vs CLI | **30326**<br><sub>(138 MiB / 46.6% CPU)</sub> | **30386** | **43906**<br><sub>(179 MiB / 49.5% CPU)</sub> | **44433** | **0.94×** | **0.92×** | ≥ **0.70×** |
| `twp-cli-plus-ratelimit-http1` vs CLI | **31713**<br><sub>(123 MiB / 48.8% CPU)</sub> | **31944** | **46435**<br><sub>(151 MiB / 49.4% CPU)</sub> | **46860** | **0.98×** | **0.97×** | ≥ **0.80×** |
| `twp-cli-plus-resilience-http1` vs CLI | **32373**<br><sub>(128 MiB / 50.5% CPU)</sub> | **32416** | **47302**<br><sub>(155 MiB / 49.5% CPU)</sub> | **47654** | **1.00×** | **0.99×** | ≥ **0.85×** |
| `twp-cli-plus-discovery-file-http1` vs CLI | **32209**<br><sub>(121 MiB / 46.5% CPU)</sub> | **32332** | **47485**<br><sub>(151 MiB / 49.1% CPU)</sub> | **47770** | **1.00×** | **0.99×** | ≥ **0.80×** |
| `twp-cli-plus-metrics-scrape-http1` vs CLI | **32192**<br><sub>(126 MiB / 44.8% CPU)</sub> | **32427** | **47348**<br><sub>(159 MiB / 49.5% CPU)</sub> | **48665** | **1.00×** | **0.99×** | ≥ **0.80×** |
| `twp-cli-plus-cache-hit-http1` vs cache cold | **34015**<br><sub>(117 MiB / 66.0% CPU)</sub> | **34022** | **49153**<br><sub>(149 MiB / 63.1% CPU)</sub> | **49358** | **1.00×** | **0.99×** | ≥ **0.90×** |
| `twp-cli-static-http1` vs CLI | **53078**<br><sub>(109 MiB / 52.1% CPU)</sub> | **53980** | **78333**<br><sub>(150 MiB / 48.4% CPU)</sub> | **82955** | **1.65×** | **1.64×** | ≥ **0.85×** |
| `twp-cli-logging-http1` vs CLI | **32440**<br><sub>(119 MiB / 47.4% CPU)</sub> | **32581** | **47624**<br><sub>(150 MiB / 49.3% CPU)</sub> | **48604** | **1.01×** | **1.00×** | ≥ **0.90×** |
| `twp-cli-lb-leasttime-http1` vs route | **29649**<br><sub>(134 MiB / 54.7% CPU)</sub> | **30386** | **44790**<br><sub>(180 MiB / 50.6% CPU)</sub> | **44825** | **0.92×** | **0.95×** | ≥ **0.85×** |
| `twp-cli-dialect-twp-http1` vs CLI | **32461**<br><sub>(114 MiB / 49.7% CPU)</sub> | **32780** | **47898**<br><sub>(145 MiB / 49.3% CPU)</sub> | **48154** | **1.01×** | **1.00×** | ≥ **0.90×** |

`validate-edition-gates.ps1` **passed** on both OS for this run. Library baselines @ c=64 (same job): Win H1 **31561** / TLS **26128**; Linux H1 **45818** / TLS **36869**. Laptop smoke ratios stay on [Performance Local Lab — Editions](Performance-Local-Lab#editions-cli--plus-stress).

## Cross-version (7.0 vs 6.0)

Gate 2 prerequisite before the `v7.0.0` tag: run `compare-cross-version` (reverse matrix, **routes unset**) on `develop` and compare against committed 6.0 baselines from GHA [33087088466](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087088466) (`tools/RpsLoadProbe/results/baseline-6.0-win.csv` / `baseline-6.0-linux.csv`).

| Gate | Threshold |
|------|-----------|
| Peer-normalized RPS (when YARP peer exists) | `(TWP÷YARP)_7 ÷ (TWP÷YARP)_6 ≥ 0.90` |
| Absolute RPS floor (TWP arms) | `7.0 ÷ 6.0 ≥ 0.70` (runner heat — peers move with the box) |
| RSS | `7.0 ÷ 6.0 ≤ 1.15` per TWP arm @ c=64 |

nginx/YARP absolute RPS is **not** gated. See [`validate-cross-version.ps1`](https://github.com/justcoding121/titanium-web-proxy/blob/develop/tools/RpsLoadProbe/validate-cross-version.ps1).

```powershell
pwsh tools/RpsLoadProbe/run-rps.ps1 -Mode compare-cross-version
pwsh tools/RpsLoadProbe/validate-cross-version.ps1 `
  -BaselineCsv tools/RpsLoadProbe/results/baseline-6.0-win.csv `
  -CurrentCsv  tools/RpsLoadProbe/results/rps-ramp-*.csv
```

Median of **3** @ `79793303`. Source: Actions [33253789356](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33253789356). Sustain RPS @ **c=64** (SLO-pass median). Absolute 7.0÷6.0 ≈ **0.96–1.00×** on same-protocol arms; peer-norm ≈ **0.90–1.03×**.

| Arm | Win 6.0 | Win 7.0 | Win÷ | Linux 6.0 | Linux 7.0 | Lin÷ |
|---|---:|---:|---:|---:|---:|---:|
| `twp-reverse-http1` | **32525** | **32213** | **0.99×** | **32755** | **31932** | **0.97×** |
| `twp-reverse-http1-tls` | **26461** | **26570** | **1.00×** | **22784** | **22351** | **0.98×** |
| `twp-reverse-http2` | **76038** | **75316** | **0.99×** | **47690** | **46678** | **0.98×** |
| `twp-reverse-http2-cleartext` | **40347** | **40236** | **1.00×** | **34556** | **33316** | **0.96×** |
| `twp-reverse-http3` | **17529** | **17380** | **0.99×** | **19468** | **19423** | **1.00×** |
| `twp-reverse-http3-cleartext` | **19956** | **19529** | **0.98×** | **20395** | **20070** | **0.98×** |
| `yarp-reverse-http1` (peer) | **27504** | **27254** | **0.99×** | **27713** | **27102** | **0.98×** |
| `yarp-reverse-http2` (peer) | **35553** | **35189** | **0.99×** | **29111** | **28733** | **0.99×** |

`validate-cross-version.ps1` **passed** on both OS CSVs (RSS floor 1.15 for one Win H1→h2c arm at 1.139× with peer-norm 1.001×). MITM arms live in `compare-product` / `compare-mitm`, not this reverse-only matrix.

## Heavier reverse workloads

Separate from the tiny-GET matrix. Same measurement environments. Modes: `compare-bodies`, `compare-post`, `compare-lossy`, `compare-tls-cost`, `compare-arch` in [RpsLoadProbe](https://github.com/justcoding121/titanium-web-proxy/tree/develop/tools/RpsLoadProbe). Dispatch each independently via `workflow_dispatch` (no need to re-run full `compare-product`). **PUT with the same body is the same proxy work as POST; DELETE with no body matches GET** — only POST is published. Bodies/POST/lossy stay **half-duplex**. `compare-arch` is the slow-consumer / early-response / duplex set. Laptop numbers are on [Performance Local Lab](Performance-Local-Lab#architecture-sensitive); CI medians go in the tables below.

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
