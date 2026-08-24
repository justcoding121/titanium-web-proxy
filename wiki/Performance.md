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

### Saturation control

Calibration for the shared 4 vCPU loopback shape: how close client + origin are to saturated before ranking reverse peers. Tiny keep-alive GET. Median of **3** repeats @ `a3b9af1e` — [32672941240](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32672941240). Warmup 2s / measure 8s; concurrency 8, 16, 32, 64. Block A **% of origin-HttpClient** uses median **peak** RPS. Blocks B/C use peer÷YARP / ÷nginx on median peak (not % of H1 origin). **Memory (RSS)** / CPU sample the **proxy child** plus its **full descendant tree** (serve-proxy → nginx master → workers); origin-direct samples the **origin** child. Product matrices below use matched `dotnet-httpclient` only (not bombardier).

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

| Arm | Generator | Sustain | Peak | % of origin-HttpClient | Memory (RSS) | CPU avg % |
|---|---|---:|---:|---:|---:|---:|
| origin-direct | dotnet-httpclient | **127,144** | **128,202** | **100%** | **53 MiB** | **47.6** |
| origin-direct-bombardier | bombardier | **94,587** | **95,521** | **74.5%** | **53 MiB** | **27.0** |
| bare-reverse-http1 | dotnet-httpclient | **61,356** | **61,356** | **47.9%** | **51 MiB** | **47.1** |
| nginx-reverse-http1 | dotnet-httpclient | **35,389** | **36,369** | **28.4%** | **120 MiB** | 🥇 **24.3** |
| yarp-reverse-http1 | dotnet-httpclient | **57,823** | **57,823** | **45.1%** | **84 MiB** | **49.6** |
| twp-reverse-http1 | dotnet-httpclient | 🥇 **60,077** | **60,077** | **46.9%** | 🥇 **66 MiB** | **48.9** |

**Linux** (`ubuntu-latest`)

| Arm | Generator | Sustain | Peak | % of origin-HttpClient | Memory (RSS) | CPU avg % |
|---|---|---:|---:|---:|---:|---:|
| origin-direct | dotnet-httpclient | **105,791** | **105,791** | **100%** | **78 MiB** | **42.1** |
| origin-direct-bombardier | bombardier | **57,999** | **57,999** | **54.8%** | **78 MiB** | **31.2** |
| bare-reverse-http1 | dotnet-httpclient | **47,955** | **47,955** | **45.3%** | **66 MiB** | **43.5** |
| nginx-reverse-http1 | dotnet-httpclient | 🥇 **60,266** | **60,266** | **57.0%** | 🥇 **72 MiB** | 🥇 **39.4** |
| yarp-reverse-http1 | dotnet-httpclient | **41,795** | **41,795** | **39.5%** | **114 MiB** | **49.4** |
| twp-reverse-http1 | dotnet-httpclient | **47,148** | **47,148** | **44.6%** | **87 MiB** | **49.1** |

Reverse peers are about **28–57%** of the origin-direct HttpClient peak on this runner class. Prefer the **%** column over absolute RPS across runs. Bare and origin-direct are controls (not medal peers).

#### Block B — H2 TLS→H1

Peer ratios (÷YARP / ÷nginx) on median peak + Memory (RSS) / CPU among TWP / nginx / YARP.

**Windows** (`windows-latest`)

| Arm | Generator | Sustain | Peak | ÷YARP | ÷nginx | Memory (RSS) | CPU avg % |
|---|---|---:|---:|---:|---:|---:|---:|
| nginx-reverse-http2 | dotnet-httpclient | **28,507** | **28,507** | **0.38×** | **1.00×** | **137 MiB** | 🥇 **23.1** |
| yarp-reverse-http2 | dotnet-httpclient | **74,813** | **74,813** | **1.00×** | **2.62×** | 🥇 **95 MiB** | **48.3** |
| twp-reverse-http2-cleartext | dotnet-httpclient | 🥇 **76,697** | **76,697** | **1.03×** | **2.69×** | **848 MiB** | **47.1** |

**Linux** (`ubuntu-latest`)

| Arm | Generator | Sustain | Peak | ÷YARP | ÷nginx | Memory (RSS) | CPU avg % |
|---|---|---:|---:|---:|---:|---:|---:|
| nginx-reverse-http2 | dotnet-httpclient | **23,281** | **30,402** | **0.78×** | **1.00×** | 🥇 **96 MiB** | 🥇 **21.6** |
| yarp-reverse-http2 | dotnet-httpclient | **39,023** | **39,023** | **1.00×** | **1.28×** | **121 MiB** | **47.8** |
| twp-reverse-http2-cleartext | dotnet-httpclient | 🥇 **43,916** | **43,916** | **1.13×** | **1.44×** | **626 MiB** | **49.9** |

#### Block C — H3→H1

Same layout as Block B. Requires QuicListener; nginx only with `http_v3_module` (Windows nginx has no QUIC).

**Windows** (`windows-latest`)

| Arm | Generator | Sustain | Peak | ÷YARP | ÷nginx | Memory (RSS) | CPU avg % |
|---|---|---:|---:|---:|---:|---:|---:|
| nginx-reverse-http3-cleartext | dotnet-httpclient | *Not possible* (no QUIC) | *Not possible* | — | — | — | — |
| yarp-reverse-http3-cleartext | dotnet-httpclient | **44,105** | **44,105** | **1.00×** | — | 🥇 **155 MiB** | **50.3** |
| twp-reverse-http3-cleartext | dotnet-httpclient | 🥇 **44,716** | **44,716** | **1.01×** | — | **271 MiB** | 🥇 **46.4** |

**Linux** (`ubuntu-latest`)

| Arm | Generator | Sustain | Peak | ÷YARP | ÷nginx | Memory (RSS) | CPU avg % |
|---|---|---:|---:|---:|---:|---:|---:|
| nginx-reverse-http3-cleartext | dotnet-httpclient | **0** | **27,519** | **1.19×** | **1.00×** | **106 MiB** | **20.3** |
| yarp-reverse-http3-cleartext | dotnet-httpclient | **23,196** | **23,196** | **1.00×** | **0.84×** | 🥇 **184 MiB** | **48.8** |
| twp-reverse-http3-cleartext | dotnet-httpclient | 🥇 **24,580** | **24,580** | **1.06×** | **0.89×** | **241 MiB** | 🥇 **47.5** |

**How to read the tables**

- **Mode**: **Reverse** = transparent fixed-forward (may TLS-terminate to a cleartext origin, or re-encrypt to a configured HTTPS/QUIC origin). **MITM** = both legs are visible in the clear inside TWP — either by decrypting client TLS/QUIC (forged cert / CONNECT) **or** by accepting an already-cleartext client (explicit HTTP proxy / inspectable transparent reverse) while still speaking plain or TLS to the origin. nginx and YARP cannot do MITM. **HTTP/3 has no cleartext client** (QUIC always encrypted).
- **Sustainable** = last concurrency that still met error/latency SLOs. **Peak** = highest RPS in that ramp.
- 🥇 = best among **TWP / nginx / YARP** only on that row or saturation block (never vs bare / origin-direct / bombardier). Product matrices and saturation **Sustain**: highest sustainable RPS. Saturation **Memory (RSS)** / **CPU avg %**: lowest value among those three that have **sustain > 0** (sustain **0** / *Not possible* peers are excluded). Omitted when only TWP can run the path (no fair multi-product comparison).
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

Median of **3 repeats** on `windows-latest` (4 vCPU / 16 GiB). Same/MITM @ `bf01825b` ([32659721937](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32659721937), [32659725022](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32659725022)); bridges @ `97559056` ([32668659736](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32668659736)) — three-process harness, parent-seeded loopback CA. Warmup 2s / measure 8s; concurrency 8, 16, 32, 64. Prefer TWP÷peer ratios over absolute RPS. Laptop High-perf / cool-paired numbers stay on the [local lab](Performance-Profiling#local-windows-lab-developer-laptop).

**Load generators:** Reverse inbound H3 arms use **`dotnet-httpclient`** (`http_version=3.0`, `RequestVersionExact`). nginx/Windows is same-OS only (no QUIC).

| Mode | Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---|---|---:|---:|---:|---:|---:|---:|
| Reverse | HTTP/1 · plain | HTTP/1 · plain | 🥇 **22,723** | **22,723** | **13,956** | **13,956** | **21,958** | **21,958** |
| Reverse | HTTP/1 · plain | HTTP/1 · TLS | 🥇 **19,889** | **19,889** | *Not possible* | *Not possible* | **19,875** | **19,875** |
| Reverse | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **20,009** | **20,009** | **8,978** | **9,195** | **18,702** | **18,702** |
| Reverse | HTTP/1 · TLS | HTTP/2 · TLS | 🥇 **24,801** | **24,801** | *Not possible* | *Not possible* | **24,434** | **24,434** |
| Reverse | HTTP/1 · TLS | HTTP/3 · QUIC | 🥇 **15,512** | **15,512** | *Not possible* (no QUIC) | *Not possible* (no QUIC) | **15,334** | **15,334** |
| Reverse | HTTP/2 · plain | HTTP/1 · plain | 🥇 **36,164** | **36,164** | *Not possible* | *Not possible* | **31,973** | **31,973** |
| Reverse | HTTP/2 · plain | HTTP/2 · plain | 🥇 **88,895** | **88,895** | *Not possible* | *Not possible* | **65,961** | **65,961** |
| Reverse | HTTP/2 · plain | HTTP/2 · TLS | 🥇 **77,703** | **77,703** | *Not possible* | *Not possible* | **56,634** | **56,634** |
| Reverse | HTTP/2 · plain | HTTP/3 · QUIC | 🥇 **30,689** | **30,689** | *Not possible* (no QUIC) | *Not possible* (no QUIC) | **28,536** | **28,536** |
| Reverse | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **34,918** | **34,918** | **11,464** | **11,474** | **29,618** | **29,618** |
| Reverse | HTTP/2 · TLS | HTTP/2 · plain | 🥇 **83,333** | **83,333** | *Not possible* | *Not possible* | **56,026** | **56,026** |
| Reverse | HTTP/2 · TLS | HTTP/3 · QUIC | 🥇 **29,610** | **29,610** | *Not possible* (no QUIC) | *Not possible* (no QUIC) | **26,004** | **26,004** |
| Reverse | HTTP/3 · QUIC | HTTP/1 · plain | **14,357** | **14,357** | *Not possible* (no QUIC) | *Not possible* (no QUIC) | 🥇 **15,127** | **15,127** |
| Reverse | HTTP/3 · QUIC | HTTP/2 · TLS | 🥇 **24,401** | **24,401** | *Not possible* (no QUIC) | *Not possible* (no QUIC) | **22,087** | **22,087** |
| Reverse | HTTP/3 · QUIC | HTTP/3 · QUIC | 🥇 **21,206** | **21,206** | *Not possible* (no QUIC) | *Not possible* (no QUIC) | **16,184** | **16,184** |
| MITM | HTTP/1 · plain | HTTP/1 · plain | **21,505** | **21,505** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/1 · plain | HTTP/1 · TLS | **19,463** | **19,463** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/1 · TLS | HTTP/1 · plain | **19,386** | **19,386** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/1 · TLS | HTTP/2 · TLS | **24,901** | **24,901** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/1 · TLS | HTTP/3 · QUIC | **15,503** | **15,503** | *Not possible* (no QUIC) | *Not possible* (no QUIC) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 · plain | HTTP/1 · plain | **31,062** | **31,062** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 · plain | HTTP/2 · plain | **87,152** | **87,152** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 · plain | HTTP/2 · TLS | **76,028** | **76,028** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 · plain | HTTP/3 · QUIC | **30,653** | **30,653** | *Not possible* (no QUIC) | *Not possible* (no QUIC) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 · TLS | HTTP/1 · plain | **30,187** | **30,187** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 · TLS | HTTP/2 · plain | **81,841** | **81,841** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 · TLS | HTTP/3 · QUIC | **29,435** | **29,435** | *Not possible* (no QUIC) | *Not possible* (no QUIC) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/3 · QUIC | HTTP/1 · plain | **12,835** | **12,835** | *Not possible* (no QUIC) | *Not possible* (no QUIC) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/3 · QUIC | HTTP/2 · TLS | **24,383** | **24,383** | *Not possible* (no QUIC) | *Not possible* (no QUIC) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/3 · QUIC | HTTP/3 · QUIC | **21,011** | **21,011** | *Not possible* (no QUIC) | *Not possible* (no QUIC) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/1 · plain (CONNECT) | HTTP/1 · TLS | **16,815** | **16,815** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/1 · TLS | HTTP/1 · TLS | **17,785** | **17,785** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 · TLS | HTTP/2 · TLS | **72,361** | **72,361** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 · TLS | HTTP/1 · TLS | **27,020** | **27,020** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/3 · QUIC | HTTP/1 · TLS | **11,684** | **11,684** | *Not possible* (no QUIC) | *Not possible* (no QUIC) | *Not possible* (no MITM) | *Not possible* (no MITM) |

TWP÷YARP H1 plain ≈ **1.03×** (22,723 / 21,958); H1 TLS terminate ≈ **1.07×** (20,009 / 18,702). Bridges @ `97559056`: h2c→H1 ≈ **1.13×**, H3→H2 ≈ **1.10×**, H1→H3 ≈ **1.01×**, h2c→H3 ≈ **1.08×**, H2 TLS→H3 ≈ **1.14×**; open Win gap vs YARP (gate **≥1.00×**): H3→H1 ≈ **0.95×** (14,357 / 15,127). Prefer ratios over absolute RPS on GHA VMs. MITM publishes the same **15** Client×Origin pairs as Reverse (inspectable/decrypt), then dual-crypto extras (CONNECT, TLS↔TLS). nginx/YARP cannot MITM.

## Linux — Titanium vs nginx vs YARP

Median of **3 repeats** on `ubuntu-latest` (4 vCPU / 16 GiB). Same/MITM @ `bf01825b` ([32659721937](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32659721937), [32659725022](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32659725022)); bridges @ `97559056` ([32668659736](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32668659736)). Warmup 2s / measure 8s; concurrency 8, 16, 32, 64. **Linux nginx is the authoritative nginx baseline.** The RPS workflow installs nginx.org mainline (`http_v3_module`) and `libmsquic` (`QuicListener.IsSupported=true` on `ubuntu-latest`). Prefer ratios over absolute RPS.

TWP÷nginx H1 plain reverse ≈ **0.85** (31,835 / 37,241); TWP÷YARP H1 plain ≈ **1.12×** (31,835 / 28,397).

| Mode | Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---|---|---:|---:|---:|---:|---:|---:|
| Reverse | HTTP/1 · plain | HTTP/1 · plain | **31,835** | **31,835** | 🥇 **37,241** | **37,241** | **28,397** | **28,397** |
| Reverse | HTTP/1 · plain | HTTP/1 · TLS | 🥇 **23,709** | **23,709** | *Not possible* | *Not possible* | **22,090** | **22,090** |
| Reverse | HTTP/1 · TLS | HTTP/1 · plain | **23,478** | **23,478** | 🥇 **28,181** | **28,181** | **20,392** | **20,392** |
| Reverse | HTTP/1 · TLS | HTTP/2 · TLS | 🥇 **22,588** | **22,588** | *Not possible* | *Not possible* | **22,317** | **22,317** |
| Reverse | HTTP/1 · TLS | HTTP/3 · QUIC | 🥇 **17,605** | **17,605** | *Not possible* (no H3 origin) | *Not possible* (no H3 origin) | **16,765** | **16,765** |
| Reverse | HTTP/2 · plain | HTTP/1 · plain | 🥇 **38,056** | **38,056** | *Not possible* | *Not possible* | **34,478** | **34,478** |
| Reverse | HTTP/2 · plain | HTTP/2 · plain | 🥇 **66,402** | **66,402** | *Not possible* | *Not possible* | **48,631** | **48,631** |
| Reverse | HTTP/2 · plain | HTTP/2 · TLS | 🥇 **51,321** | **51,321** | *Not possible* | *Not possible* | **39,522** | **39,522** |
| Reverse | HTTP/2 · plain | HTTP/3 · QUIC | 🥇 **27,182** | **27,182** | *Not possible* (no H3 origin) | *Not possible* (no H3 origin) | **26,465** | **26,465** |
| Reverse | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **35,183** | **35,183** | **13,291** | **18,780** | **29,560** | **29,560** |
| Reverse | HTTP/2 · TLS | HTTP/2 · plain | 🥇 **59,676** | **59,676** | *Not possible* | *Not possible* | **39,640** | **39,640** |
| Reverse | HTTP/2 · TLS | HTTP/3 · QUIC | 🥇 **25,712** | **25,712** | *Not possible* (no H3 origin) | *Not possible* (no H3 origin) | **22,964** | **22,964** |
| Reverse | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **21,226** | **21,226** | **0** | **14,867** | **18,908** | **18,908** |
| Reverse | HTTP/3 · QUIC | HTTP/2 · TLS | 🥇 **24,004** | **24,004** | *Not possible* (no H3→H2) | *Not possible* (no H3→H2) | **21,658** | **21,658** |
| Reverse | HTTP/3 · QUIC | HTTP/3 · QUIC | 🥇 **19,361** | **19,361** | *Not possible* (no H3 origin) | *Not possible* (no H3 origin) | **15,825** | **15,825** |
| MITM | HTTP/1 · plain | HTTP/1 · plain | **31,659** | **31,659** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/1 · plain | HTTP/1 · TLS | **24,249** | **24,249** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/1 · TLS | HTTP/1 · plain | **23,444** | **23,444** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/1 · TLS | HTTP/2 · TLS | **24,519** | **24,519** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/1 · TLS | HTTP/3 · QUIC | **17,773** | **17,773** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 · plain | HTTP/1 · plain | **38,330** | **38,330** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 · plain | HTTP/2 · plain | **66,340** | **66,340** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 · plain | HTTP/2 · TLS | **51,899** | **51,899** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 · plain | HTTP/3 · QUIC | **27,767** | **27,767** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 · TLS | HTTP/1 · plain | **35,342** | **35,342** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 · TLS | HTTP/2 · plain | **60,463** | **60,463** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 · TLS | HTTP/3 · QUIC | **25,733** | **25,733** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/3 · QUIC | HTTP/1 · plain | **20,509** | **20,509** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/3 · QUIC | HTTP/2 · TLS | **24,229** | **24,229** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/3 · QUIC | HTTP/3 · QUIC | **19,674** | **19,674** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/1 · plain (CONNECT) | HTTP/1 · TLS | **18,316** | **18,316** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/1 · TLS | HTTP/1 · TLS | **19,547** | **19,547** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 · TLS | HTTP/2 · TLS | **48,957** | **48,957** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 · TLS | HTTP/1 · TLS | **27,168** | **27,168** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/3 · QUIC | HTTP/1 · TLS | **15,903** | **15,903** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |

On this GHA shape, TWP H1 plain ÷ nginx H1 plain ≈ **0.85** (31,835 / 37,241). H1 TLS terminate ≈ **0.83** (23,478 / 28,181). TWP÷YARP H1 plain ≈ **1.12×**; H1 TLS terminate ≈ **1.15×**. Bridges @ `97559056`: all published TWP÷YARP **≥1.00×** (h2c→H1 ≈ **1.10×**, H3→H1 ≈ **1.12×**, H3→H2 ≈ **1.11×**). Absolute RPS swings by VM; prefer the **ratio** and **median across repeats**. MITM publishes the same **15** Client×Origin pairs as Reverse (inspectable/decrypt), then dual-crypto extras (CONNECT, TLS↔TLS). nginx/YARP cannot MITM.

**nginx HTTP/3:** inbound QUIC terminate → cleartext H1 (`nginx-reverse-http3-cleartext`) @ `97559056` bridges: sustain **0** (p99/error SLO miss) / peak **14,867**. TWP/YARP H3→H1 on this row are from the same bridges pass. nginx still cannot speak HTTP/3 to an origin (no H3 upstream in this conf).

**YARP HTTP/3 (this matrix):** TWP leads H3→H1 ≈ **1.12×** (21,226 / 18,908), H3→H2 ≈ **1.11×** (24,004 / 21,658). H1→H2 ≈ **1.01×** (22,588 / 22,317). H1→H3 ≈ **1.05×** (17,605 / 16,765). h2c→H3 ≈ **1.03×**.

**Windows vs Linux:** both CI envs are **4 vCPU / 16 GiB**, but do **not** compare absolute RPS across OS. Linux nginx leads H1 plain/TLS terminate (TWP second, ahead of YARP). Windows bridges @ `97559056` closed h2c→H1 / H3→H2; **H3→H1 remains the open Win YARP-led cell** (≈ **0.95×**). Cool laptop notes remain on [Performance Profiling](Performance-Profiling#local-windows-lab-developer-laptop).


### Tiny JSON reverse is nginx’s best case on Linux

The tables above use **~64 B keep-alive GET** on loopback. On Linux H1 reverse, nginx leads; YARP sits near TWP. Heavier bodies, POSTs, TLS handshake cost, and lossy/HOL workloads (below) change the picture. MITM rows remain TWP-only. nginx HTTP/3 is inbound-terminate only (see note above).

### Why isn’t HTTP/3 > HTTP/2 > HTTP/1 in raw RPS?

For **tiny JSON responses** (~64 B) on loopback, that ordering is **not** expected: topology (TLS hop count, terminate vs MITM) dominates; HTTP/2 and HTTP/3 help multiplexing, not single-origin tiny-GET RPS. See the **lossy** tables below for a workload where protocol design matters.

## Heavier reverse workloads

Separate from the tiny-GET matrix. Same measurement environments. Modes: `compare-bodies`, `compare-post`, `compare-lossy`, `compare-tls-cost`, `compare-arch` in [RpsLoadProbe](https://github.com/justcoding121/titanium-web-proxy/tree/develop/tools/RpsLoadProbe). **PUT with the same body is the same proxy work as POST; DELETE with no body matches GET** — only POST is published. Bodies/POST/lossy stay **half-duplex**. `compare-arch` is the slow-consumer / early-response / duplex set. Laptop numbers are on [Performance-Profiling](Performance-Profiling#architecture-sensitive); CI medians go in the tables below.

Lossy link = **userspace** shim (not kernel `netem`): TCP gets per-buffer delay + occasional whole-connection stalls (honest HOL for multiplexed H2); UDP gets per-datagram delay + drops (QUIC). `compare-lossy` publishes H1/H2/H3; H3 is where the protocol design is supposed to matter.

### Windows — heavier reverse GET (64 KiB / 256 KiB)

Median of **3** repeats on `windows-latest` @ `8ac422ee`. Source: Actions [32631121563](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32631121563) (`compare-bodies`). Warmup 2s / measure 8s.

| Body | Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---|---|---:|---:|---:|---:|---:|---:|
| 64 KiB | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **11,026** | **12,257** | **1,129** | **1,184** | **9,985** | **11,229** |
| 64 KiB | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **10,112** | **10,346** | **1,006** | **1,031** | **8,630** | **9,265** |
| 64 KiB | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **5,361** | **6,006** | *Not possible* (no QUIC) | *Not possible* (no QUIC) | **4,913** | **4,913** |
| 256 KiB | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **3,323** | **3,687** | **274** | **309** | **2,244** | **2,244** |
| 256 KiB | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **2,414** | **2,438** | **222** | **222** | **2,302** | **2,642** |
| 256 KiB | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **1,495** | **1,569** | *Not possible* (no QUIC) | *Not possible* (no QUIC) | **1,371** | **1,410** |

nginx/Windows collapses on large reverse bodies in this harness; treat as same-OS only. H1 TLS **64 KiB** ≈ **1.10×** YARP; **256 KiB** ≈ **1.48×**. H2→H1 64 KiB ≈ **1.17×**. H3→H1 64 KiB ≈ **1.09×**; 256 KiB ≈ **1.09×**.

### Linux — heavier reverse GET (64 KiB / 256 KiB)

Median of **3** repeats @ `8ac422ee`. Source: Actions [32631121563](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32631121563) (`compare-bodies`). Warmup 2s / measure 8s.

| Body | Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---|---|---:|---:|---:|---:|---:|---:|
| 64 KiB | HTTP/1 · TLS | HTTP/1 · plain | **8,457** | **8,457** | 🥇 **8,789** | **8,800** | **6,885** | **7,036** |
| 64 KiB | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **5,953** | **5,953** | **4,004** | **4,004** | **5,251** | **5,251** |
| 64 KiB | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **5,776** | **5,776** | **1,737** | **1,832** | **4,462** | **4,462** |
| 256 KiB | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **2,821** | **2,821** | **2,726** | **2,726** | **2,186** | **2,186** |
| 256 KiB | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **1,543** | **1,543** | **1,010** | **1,014** | **1,407** | **1,407** |
| 256 KiB | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **1,546** | **1,546** | **440** | **458** | **1,299** | **1,325** |

On this GHA pass TWP÷YARP H1 TLS ≈ **1.23×** (64 KiB) / **1.29×** (256 KiB); H2→H1 ≈ **1.13×** / **1.10×**; H3→H1 ≈ **1.29×** / **1.19×**. TWP÷nginx H1 TLS ≈ **0.96** / **1.03**. Absolute RPS swings by VM; prefer ratios.

### Windows — POST 64 KiB request + 64 KiB response

Median of **3** repeats on `windows-latest` @ `21396a4d`. Source: Actions [32608567396](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32608567396) (`compare-post`).

| Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---|---:|---:|---:|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | 🥇 **7,594** | **7,819** | **425** | **433** | **5,921** | **5,927** |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **6,006** | **6,006** | **423** | **444** | **4,880** | **4,880** |
| HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **2,772** | **2,816** | *Not possible* | *Not possible* | **2,769** | **2,861** |

TWP leads H1 POST (~**1.28×** YARP), H2 POST (~**1.23×** YARP), and H3 POST (~**1.00×** YARP).

### Linux — POST 64 KiB request + 64 KiB response

Median of **3** repeats @ `21396a4d`. Source: Actions [32608567396](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32608567396) (`compare-post`).

| Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---|---:|---:|---:|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | 🥇 **4,415** | **4,415** | **4,277** | **4,292** | **3,406** | **3,406** |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **3,350** | **3,350** | **2,071** | **2,147** | **2,776** | **2,776** |
| HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **2,919** | **2,919** | **799** | **802** | **2,771** | **2,771** |

Linux nginx H1/H2/H3 POST completed (nginx.org mainline). TWP÷YARP H3 POST ≈ **1.05×**; TWP÷nginx H3 ≈ **3.65×**.

### Windows — lossy / high-RTT (H2 HOL / H3 loss)

Userspace **5 ms** one-way delay + **1%** TCP connection stall (H1/H2) or UDP datagram drop (H3); **64 KiB** GET. Median of **3** repeats on `windows-latest` @ `ab9c0631`. Source: [32666017510](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32666017510) (`compare-lossy`). H3 on GHA Windows collapses through the userspace UDP shim (sustain ≈ **0**); use the [laptop lab](Performance-Profiling#lossy--high-rtt-h2-hol--h3-packet-loss) for the Windows H3 signal.

| Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---|---:|---:|---:|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | 🥇 **662** | **662** | **635** | **635** | 🥇 **662** | **662** |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **60** | **87** | **18** | **18** | **17** | **17** |
| HTTP/3 · QUIC | HTTP/1 · plain | *Not measured* (GHA UDP-shim) | *Not measured* | *Not possible* (no QUIC) | *Not possible* | *Not measured* (GHA UDP-shim) | *Not measured* |

H1: TWP÷YARP ≈ **1.00×** (662 / 662). H2: TWP÷YARP ≈ **3.53×** (60 / 17) with lossy-only **MaxConcurrentStreamsPerConnection=8**. Laptop Windows (same shim, `quic-http3`): TWP H3 ≈ **1,572** sustain vs H2 ≈ **14** (~**112×**). Absolute RPS is low because the shim delays every buffer/datagram — the point is the **protocol shape**.

### Linux — lossy / high-RTT (H2 HOL / H3 loss)

Median of **3** repeats @ `ab9c0631`. Source: [32666017510](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32666017510) (`compare-lossy`; lossy H3 uses `quic-http3`).

| Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---|---:|---:|---:|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | **1,195** | **1,195** | 🥇 **1,207** | **1,207** | **1,192** | **1,192** |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **319** | **319** | **40** | **40** | **44** | **44** |
| HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **321** | **321** | **90** | **90** | **316** | **316** |

Same H1 story (nginx 1st **1,207**, TWP 2nd ahead of YARP ≈ **1.00×**). **H2:** TWP leads ≈ **7.3×** YARP (319 / 44). **H3:** TWP leads ≈ **1.02×** YARP (321 / 316) after size-gated HEADERS+DATA coalesce for bodies ≥16 KiB (`ab9c0631`); nginx H3 terminate stays far below — TWP 1st among peers that complete.

### Architecture-sensitive

`compare-arch` isolates slow app readers, origin-early response, H2 duplex, and WebSocket echo. See [TWP vs YARP IO model](Performance-Profiling#twp-vs-yarp-io-model). Laptop 1-rep numbers are on [Performance-Profiling](Performance-Profiling#architecture-sensitive).

Median of **3** repeats on matched 4 vCPU / 16 GiB runners. Slow/early/duplex rows @ `21396a4d` ([32608568872](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32608568872)); WebSocket remasure @ `ab9c0631` ([32666019333](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32666019333)) (`compare-arch`). Slow consumer = 256 KiB GET, 16 KiB read + 8 ms sleep. Early response = 64 KiB POST, origin writes after 8 KiB. Duplex H2 = overlapping 64 KiB POST on H2 TLS↔H2 TLS. WebSocket = echo round-trips/sec.

`compare-lossy` (slow **network**) is already published above; it is not a slow **app** reader.

#### Windows

| Scenario | Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---|---|---:|---:|---:|---:|---:|---:|
| Slow consumer (256 KiB GET, throttled client read) | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **256** | **256** | **243** | **243** | 🥇 **256** | **256** |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **256** | **256** | **248** | **248** | 🥇 **256** | **256** |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **272** | **272** | *Not possible* (no QUIC) | *Not possible* | 🥇 **272** | **272** |
| Early response (origin writes after first request chunk) | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **11,243** | **12,400** | **584** | **637** | **6,868** | **9,421** |
| Early response (origin writes after first request chunk) | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **8,770** | **9,163** | **0** | **622** | **6,867** | **6,867** |
| Early response (origin writes after first request chunk) | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **4,041** | **4,256** | *Not possible* (no QUIC) | *Not possible* | **3,656** | **3,955** |
| Duplex (both directions live) | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **35** | **1,102** | *Not possible* | *Not possible* | **0** | **2,235** |
| Duplex (WebSocket / extended CONNECT) | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **23,951** | **23,951** | **13,082** | **13,082** | **21,032** | **21,032** |

#### Linux

| Scenario | Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---|---|---:|---:|---:|---:|---:|---:|
| Slow consumer (256 KiB GET, throttled client read) | HTTP/1 · TLS | HTTP/1 · plain | **447** | **447** | 🥇 **472** | **472** | **419** | **419** |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · TLS | HTTP/1 · plain | **472** | **472** | 🥇 **479** | **479** | **472** | **472** |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **473** | **473** | **120** | **427** | **472** | **472** |
| Early response (origin writes after first request chunk) | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **4,334** | **4,334** | **4,305** | **4,305** | **3,336** | **3,336** |
| Early response (origin writes after first request chunk) | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **3,695** | **3,695** | **0** | **2,128** | **2,402** | **2,555** |
| Early response (origin writes after first request chunk) | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **2,861** | **2,861** | **0** | **784** | **2,227** | **2,227** |
| Duplex (both directions live) | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **16** | **210** | *Not possible* | *Not possible* | **13** | **1,884** |
| Duplex (WebSocket / extended CONNECT) | HTTP/1 · TLS | HTTP/1 · plain | **32,850** | **32,850** | 🥇 **34,594** | **34,594** | **28,450** | **28,450** |

Slow consumer is sleep-bound; H1/H2/H3 sit in the same band once bodies stream. Early-response H3: TWP leads on both OS. Duplex H2: TWP holds a higher sustain than YARP on this pass (YARP peaks higher). WebSocket @ `ab9c0631` arch: TWP leads on Windows ≈ **1.14×** YARP (23,951 / 21,032); Linux nginx leads.

### TLS termination cost (H1 TLS → cleartext origin)

Isolates keep-alive tiny GET vs **new connection per request** (handshake-dominated) vs keep-alive **256 KiB**. Product comparison uses RPS and end-to-end latency; TWP can also capture `ClientTlsTiming` when `TWP_RPS_CAPTURE_TLS=1` (child process) — nginx/YARP have no equivalent hook.

#### Windows

Median of **3** repeats on `windows-latest` @ `13059143`. Source: Actions [32625349927](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32625349927) (`compare-tls-cost`). Absolute RPS on GHA swings hard; prefer **TWP÷YARP**.

| Workload | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---:|---:|---:|---:|---:|---:|
| Keep-alive · tiny GET | 🥇 **23,320** | **23,320** | **11,801** | **12,719** | **21,827** | **21,827** |
| New-connection · tiny GET | 🥇 **708** | **708** | **235** | **238** | **701** | **701** |
| Keep-alive · 256 KiB GET | 🥇 **2,840** | **2,891** | **220** | **236** | **2,582** | **2,594** |

#### Linux

Median of **3** repeats @ `13059143`. Source: Actions [32625349927](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32625349927) (`compare-tls-cost`).

| Workload | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---:|---:|---:|---:|---:|---:|
| Keep-alive · tiny GET | **24,241** | **24,241** | 🥇 **29,229** | **29,229** | **21,058** | **21,058** |
| New-connection · tiny GET | **999** | **999** | 🥇 **1,023** | **1,024** | **986** | **986** |
| Keep-alive · 256 KiB GET | 🥇 **2,776** | **2,776** | **2,685** | **2,685** | **2,194** | **2,194** |

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
