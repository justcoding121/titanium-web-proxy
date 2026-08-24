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

Calibration for the shared 4 vCPU loopback shape: how close client + origin are to saturated before ranking reverse peers. Tiny keep-alive GET. Median of **3** repeats @ `0ff3673c` — [32685356597](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32685356597). Warmup 2s / measure 8s; concurrency 8, 16, 32, 64. Block A **% of origin-HttpClient** uses median **peak** RPS. Blocks B/C use peer÷YARP / ÷nginx on median peak (not % of H1 origin). **Memory (RSS)** / CPU sample the **proxy child** plus its **full descendant tree** (serve-proxy → nginx master → workers); origin-direct samples the **origin** child. Product matrices below use matched `dotnet-httpclient` only (not bombardier). **H2→H1 Memory** after `Http2PendingWork` + lite wire: Win **~144 MiB** (was ~848 MiB), Linux **~227 MiB** (was ~626 MiB), RPS still leads YARP.

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
| origin-direct | dotnet-httpclient | **61,873** | **61,983** | **100%** | **53 MiB** | **42.8** |
| origin-direct-bombardier | bombardier | **46,950** | **47,181** | **76.1%** | **54 MiB** | **25.6** |
| bare-reverse-http1 | dotnet-httpclient | **30,801** | **30,801** | **49.7%** | **54 MiB** | **45.0** |
| nginx-reverse-http1 | dotnet-httpclient | **19,151** | **19,451** | **31.4%** | **120 MiB** | 🥇 **24.8** |
| yarp-reverse-http1 | dotnet-httpclient | **26,913** | **26,913** | **43.4%** | **84 MiB** | **48.8** |
| twp-reverse-http1 | dotnet-httpclient | 🥇 **31,869** | **31,869** | **51.4%** | 🥇 **73 MiB** | **45.0** |

**Linux** (`ubuntu-latest`)

| Arm | Generator | Sustain | Peak | % of origin-HttpClient | Memory (RSS) | CPU avg % |
|---|---|---:|---:|---:|---:|---:|
| origin-direct | dotnet-httpclient | **97,911** | **97,912** | **100%** | **79 MiB** | **43.5** |
| origin-direct-bombardier | bombardier | **61,344** | **61,541** | **62.9%** | **79 MiB** | **37.8** |
| bare-reverse-http1 | dotnet-httpclient | **46,344** | **46,344** | **47.3%** | **69 MiB** | **46.2** |
| nginx-reverse-http1 | dotnet-httpclient | 🥇 **55,817** | **55,817** | **57.0%** | 🥇 **72 MiB** | 🥇 **40.8** |
| yarp-reverse-http1 | dotnet-httpclient | **41,821** | **41,821** | **42.7%** | **117 MiB** | **49.3** |
| twp-reverse-http1 | dotnet-httpclient | **48,510** | **48,510** | **49.5%** | **87 MiB** | **50.3** |

Reverse peers are about **28–57%** of the origin-direct HttpClient peak on this runner class. Prefer the **%** column over absolute RPS across runs. Bare and origin-direct are controls (not medal peers).

#### Block B — H2 TLS→H1

Peer ratios (÷YARP / ÷nginx) on median peak + Memory (RSS) / CPU among TWP / nginx / YARP.

**Windows** (`windows-latest`)

| Arm | Generator | Sustain | Peak | ÷YARP | ÷nginx | Memory (RSS) | CPU avg % |
|---|---|---:|---:|---:|---:|---:|---:|
| nginx-reverse-http2 | dotnet-httpclient | **15,025** | **15,025** | **0.43×** | **1.00×** | **137 MiB** | 🥇 **23.6** |
| yarp-reverse-http2 | dotnet-httpclient | **35,176** | **35,176** | **1.00×** | **2.34×** | 🥇 **90 MiB** | **49.0** |
| twp-reverse-http2-cleartext | dotnet-httpclient | 🥇 **37,842** | **37,842** | **1.08×** | **2.52×** | **144 MiB** | **49.8** |

**Linux** (`ubuntu-latest`)

| Arm | Generator | Sustain | Peak | ÷YARP | ÷nginx | Memory (RSS) | CPU avg % |
|---|---|---:|---:|---:|---:|---:|---:|
| nginx-reverse-http2 | dotnet-httpclient | **21,348** | **29,587** | **0.66×** | **1.00×** | 🥇 **96 MiB** | 🥇 **22.8** |
| yarp-reverse-http2 | dotnet-httpclient | **44,626** | **44,626** | **1.00×** | **1.51×** | **121 MiB** | **48.5** |
| twp-reverse-http2-cleartext | dotnet-httpclient | 🥇 **49,714** | **49,714** | **1.11×** | **1.68×** | **227 MiB** | **51.9** |

#### Block C — H3→H1

Same layout as Block B. Requires QuicListener; nginx only with `http_v3_module` (Windows nginx has no QUIC).

**Windows** (`windows-latest`)

| Arm | Generator | Sustain | Peak | ÷YARP | ÷nginx | Memory (RSS) | CPU avg % |
|---|---|---:|---:|---:|---:|---:|---:|
| nginx-reverse-http3-cleartext | dotnet-httpclient | *Not possible* (no QUIC) | *Not possible* | — | — | — | — |
| yarp-reverse-http3-cleartext | dotnet-httpclient | **18,857** | **18,857** | **1.00×** | — | 🥇 **160 MiB** | **48.1** |
| twp-reverse-http3-cleartext | dotnet-httpclient | 🥇 **19,758** | **19,758** | **1.05×** | — | **232 MiB** | 🥇 **42.9** |

**Linux** (`ubuntu-latest`)

| Arm | Generator | Sustain | Peak | ÷YARP | ÷nginx | Memory (RSS) | CPU avg % |
|---|---|---:|---:|---:|---:|---:|---:|
| nginx-reverse-http3-cleartext | dotnet-httpclient | **0** | **25,054** | **0.90×** | **1.00×** | **107 MiB** | **21.7** |
| yarp-reverse-http3-cleartext | dotnet-httpclient | **27,950** | **27,950** | **1.00×** | **1.12×** | 🥇 **192 MiB** | **49.5** |
| twp-reverse-http3-cleartext | dotnet-httpclient | 🥇 **31,466** | **31,466** | **1.13×** | **1.26×** | **301 MiB** | **52.1** |

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

Median of **3 repeats** on `windows-latest` (4 vCPU / 16 GiB). Same/MITM/bridges @ `1f2d0eee` — same [32688076110](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32688076110), [32688077908](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32688077908), bridges [32685354747](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32685354747) @ `0ff3673c` (product tip; wiki paste @ `1f2d0eee`). Three-process harness, parent-seeded loopback CA. Warmup 2s / measure 8s; concurrency 8, 16, 32, 64. Prefer TWP÷peer ratios over absolute RPS. **TWP Memory (RSS) / CPU** are median at the peak-RPS step (proxy child + descendant tree); peers stay RPS-only here (see [Saturation](#saturation-control) for peer Memory). Laptop High-perf / cool-paired numbers stay on the [local lab](Performance-Profiling#local-windows-lab-developer-laptop).

**Load generators:** Reverse inbound H3 arms use **`dotnet-httpclient`** (`http_version=3.0`, `RequestVersionExact`). nginx/Windows is same-OS only (no QUIC).

| Mode | Client | Origin | TWP sustain | TWP peak | TWP Memory | TWP CPU % | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---|---|---:|---:|---:|---:|---:|---:|---:|---:|
| Reverse | HTTP/1 · plain | HTTP/1 · plain | 🥇 **25,525** | **25,525** | **73 MiB** | **50** | **13,325** | **13,325** | **21,341** | **21,341** |
| Reverse | HTTP/1 · plain | HTTP/1 · TLS | 🥇 **21,069** | **21,069** | **84 MiB** | **52** | *Not possible* | *Not possible* | **19,498** | **19,498** |
| Reverse | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **20,286** | **20,286** | **82 MiB** | **49.4** | **8,980** | **8,980** | **17,956** | **17,956** |
| Reverse | HTTP/1 · TLS | HTTP/2 · TLS | 🥇 **42,165** | **42,165** | **112 MiB** | **47.6** | *Not possible* | *Not possible* | **41,148** | **41,148** |
| Reverse | HTTP/1 · TLS | HTTP/3 · QUIC | 🥇 **24,823** | **24,823** | **116 MiB** | **50.7** | *Not possible* (no QUIC) | *Not possible* (no QUIC) | **24,684** | **24,684** |
| Reverse | HTTP/2 · plain | HTTP/1 · plain | 🥇 **56,585** | **56,585** | **195 MiB** | **51.4** | *Not possible* | *Not possible* | **52,676** | **52,676** |
| Reverse | HTTP/2 · plain | HTTP/2 · plain | 🥇 **122,084** | **122,084** | **71 MiB** | **34.9** | *Not possible* | *Not possible* | **89,134** | **89,134** |
| Reverse | HTTP/2 · plain | HTTP/2 · TLS | 🥇 **74,430** | **74,430** | **80 MiB** | **35** | *Not possible* | *Not possible* | **55,671** | **55,671** |
| Reverse | HTTP/2 · plain | HTTP/3 · QUIC | 🥇 **42,251** | **42,251** | **174 MiB** | **51.2** | *Not possible* (no QUIC) | *Not possible* (no QUIC) | **41,584** | **41,584** |
| Reverse | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **54,493** | **54,493** | **192 MiB** | **50.6** | **11,057** | **11,057** | **47,759** | **47,759** |
| Reverse | HTTP/2 · TLS | HTTP/2 · plain | 🥇 **112,459** | **112,459** | **89 MiB** | **35.2** | *Not possible* | *Not possible* | **77,976** | **77,976** |
| Reverse | HTTP/2 · TLS | HTTP/3 · QUIC | 🥇 **40,976** | **40,976** | **198 MiB** | **53.6** | *Not possible* (no QUIC) | *Not possible* (no QUIC) | **37,267** | **37,267** |
| Reverse | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **27,046** | **27,046** | **252 MiB** | **42.9** | *Not possible* (no QUIC) | *Not possible* (no QUIC) | **24,085** | **24,085** |
| Reverse | HTTP/3 · QUIC | HTTP/2 · TLS | 🥇 **35,922** | **35,922** | **228 MiB** | **45.5** | *Not possible* (no QUIC) | *Not possible* (no QUIC) | **32,152** | **32,152** |
| Reverse | HTTP/3 · QUIC | HTTP/3 · QUIC | 🥇 **14,958** | **14,958** | **175 MiB** | **48.8** | *Not possible* (no QUIC) | *Not possible* (no QUIC) | **11,653** | **11,653** |
| MITM | HTTP/1 · plain | HTTP/1 · plain | 🥇 **33,785** | **33,785** | **77 MiB** | **48.5** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/1 · plain | HTTP/1 · TLS | 🥇 **30,763** | **30,763** | **87 MiB** | **49.9** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **30,785** | **30,785** | **85 MiB** | **45.8** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/1 · TLS | HTTP/2 · TLS | 🥇 **37,519** | **37,519** | **113 MiB** | **49.4** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/1 · TLS | HTTP/3 · QUIC | 🥇 **20,160** | **20,160** | **111 MiB** | **49.8** | *Not possible* (no QUIC) | *Not possible* (no QUIC) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 · plain | HTTP/1 · plain | 🥇 **45,273** | **45,273** | **185 MiB** | **50.3** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 · plain | HTTP/2 · plain | 🥇 **91,446** | **91,446** | **72 MiB** | **35.5** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 · plain | HTTP/2 · TLS | 🥇 **82,967** | **82,967** | **82 MiB** | **36** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 · plain | HTTP/3 · QUIC | 🥇 **33,186** | **33,186** | **177 MiB** | **49.5** | *Not possible* (no QUIC) | *Not possible* (no QUIC) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **43,911** | **43,911** | **172 MiB** | **49** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 · TLS | HTTP/2 · plain | 🥇 **86,099** | **86,099** | **93 MiB** | **38.7** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 · TLS | HTTP/3 · QUIC | 🥇 **32,660** | **32,660** | **176 MiB** | **50.7** | *Not possible* (no QUIC) | *Not possible* (no QUIC) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **22,442** | **22,442** | **234 MiB** | **41.1** | *Not possible* (no QUIC) | *Not possible* (no QUIC) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/3 · QUIC | HTTP/2 · TLS | 🥇 **27,656** | **27,656** | **216 MiB** | **47.6** | *Not possible* (no QUIC) | *Not possible* (no QUIC) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/3 · QUIC | HTTP/3 · QUIC | 🥇 **16,808** | **16,808** | **171 MiB** | **44.1** | *Not possible* (no QUIC) | *Not possible* (no QUIC) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/1 · plain (CONNECT) | HTTP/1 · TLS | 🥇 **27,351** | **27,351** | **100 MiB** | **50.3** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/1 · TLS | HTTP/1 · TLS | 🥇 **28,338** | **28,338** | **82 MiB** | **46.5** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **76,456** | **76,456** | **88 MiB** | **33.3** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 · TLS | HTTP/1 · TLS | 🥇 **39,157** | **39,157** | **156 MiB** | **49.9** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **20,318** | **20,318** | **239 MiB** | **41** | *Not possible* (no QUIC) | *Not possible* (no QUIC) | *Not possible* (no MITM) | *Not possible* (no MITM) |

TWP÷YARP H1 plain ≈ **1.20×** (25,525 / 21,341); H1 TLS terminate ≈ **1.13×**. Bridges: **all published TWP÷YARP ≥1.00×** — H3→H1 ≈ **1.12×** (27,046 / 24,085; closed prior **0.993×** gap). Prefer ratios over absolute RPS on GHA VMs. MITM publishes the same **15** Client×Origin pairs as Reverse (inspectable/decrypt), then dual-crypto extras (CONNECT, TLS↔TLS). nginx/YARP cannot MITM.

## Linux — Titanium vs nginx vs YARP

Median of **3 repeats** on `ubuntu-latest` (4 vCPU / 16 GiB). Same/MITM @ `1f2d0eee` ([32688076110](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32688076110), [32688077908](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32688077908)); bridges @ `0ff3673c` ([32685354747](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32685354747)). Warmup 2s / measure 8s; concurrency 8, 16, 32, 64. **Linux nginx is the authoritative nginx baseline.** The RPS workflow installs nginx.org mainline (`http_v3_module`) and `libmsquic` (`QuicListener.IsSupported=true` on `ubuntu-latest`). Prefer ratios over absolute RPS. **TWP Memory / CPU** as on Windows; peers Memory only on [Saturation](#saturation-control).

TWP÷nginx H1 plain reverse ≈ **0.83** (46,284 / 55,862); TWP÷YARP H1 plain ≈ **1.12×**.

| Mode | Client | Origin | TWP sustain | TWP peak | TWP Memory | TWP CPU % | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---|---|---:|---:|---:|---:|---:|---:|---:|---:|
| Reverse | HTTP/1 · plain | HTTP/1 · plain | **46,284** | **46,284** | **81 MiB** | **50.3** | 🥇 **55,862** | **55,862** | **41,203** | **41,203** |
| Reverse | HTTP/1 · plain | HTTP/1 · TLS | 🥇 **37,194** | **37,194** | **102 MiB** | **48.3** | *Not possible* | *Not possible* | **33,500** | **33,500** |
| Reverse | HTTP/1 · TLS | HTTP/1 · plain | **36,518** | **36,518** | **104 MiB** | **49.1** | 🥇 **43,011** | **43,011** | **32,495** | **32,495** |
| Reverse | HTTP/1 · TLS | HTTP/2 · TLS | 🥇 **48,038** | **48,038** | **150 MiB** | **48** | *Not possible* | *Not possible* | **46,712** | **46,712** |
| Reverse | HTTP/1 · TLS | HTTP/3 · QUIC | 🥇 **30,886** | **30,886** | **158 MiB** | **50.8** | *Not possible* (no H3 origin) | *Not possible* (no H3 origin) | **29,371** | **29,371** |
| Reverse | HTTP/2 · plain | HTTP/1 · plain | **66,809** | **66,809** | **216 MiB** | **49.4** | *Not possible* | *Not possible* | 🥇 **67,822** | **67,822** |
| Reverse | HTTP/2 · plain | HTTP/2 · plain | 🥇 **123,947** | **123,947** | **103 MiB** | **39.6** | *Not possible* | *Not possible* | **90,109** | **90,109** |
| Reverse | HTTP/2 · plain | HTTP/2 · TLS | 🥇 **74,148** | **74,148** | **107 MiB** | **39.6** | *Not possible* | *Not possible* | **59,392** | **59,392** |
| Reverse | HTTP/2 · plain | HTTP/3 · QUIC | 🥇 **38,554** | **38,554** | **229 MiB** | **49.6** | *Not possible* (no H3 origin) | *Not possible* (no H3 origin) | **37,816** | **37,816** |
| Reverse | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **62,181** | **62,181** | **225 MiB** | **48** | **29,622** | **29,622** | **58,331** | **58,331** |
| Reverse | HTTP/2 · TLS | HTTP/2 · plain | 🥇 **113,846** | **113,846** | **110 MiB** | **38.6** | *Not possible* | *Not possible* | **71,600** | **71,600** |
| Reverse | HTTP/2 · TLS | HTTP/3 · QUIC | 🥇 **35,985** | **35,985** | **232 MiB** | **49** | *Not possible* (no H3 origin) | *Not possible* (no H3 origin) | **33,568** | **33,568** |
| Reverse | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **34,129** | **34,129** | **301 MiB** | **46** | **0** | **40,727** | **31,205** | **31,205** |
| Reverse | HTTP/3 · QUIC | HTTP/2 · TLS | 🥇 **33,159** | **33,159** | **276 MiB** | **47** | *Not possible* (no H3→H2) | *Not possible* (no H3→H2) | **32,811** | **32,811** |
| Reverse | HTTP/3 · QUIC | HTTP/3 · QUIC | 🥇 **26,711** | **26,711** | **278 MiB** | **47.9** | *Not possible* (no H3 origin) | *Not possible* (no H3 origin) | **21,999** | **21,999** |
| MITM | HTTP/1 · plain | HTTP/1 · plain | 🥇 **56,421** | **56,421** | **83 MiB** | **50.1** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/1 · plain | HTTP/1 · TLS | 🥇 **46,215** | **46,215** | **104 MiB** | **46.6** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **46,892** | **46,892** | **107 MiB** | **47.8** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/1 · TLS | HTTP/2 · TLS | 🥇 **43,132** | **43,132** | **151 MiB** | **48.8** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/1 · TLS | HTTP/3 · QUIC | 🥇 **28,615** | **28,615** | **152 MiB** | **51.4** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 · plain | HTTP/1 · plain | 🥇 **59,946** | **59,946** | **212 MiB** | **49.9** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 · plain | HTTP/2 · plain | 🥇 **105,099** | **105,099** | **104 MiB** | **38.5** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 · plain | HTTP/2 · TLS | 🥇 **81,941** | **81,941** | **111 MiB** | **37.1** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 · plain | HTTP/3 · QUIC | 🥇 **34,410** | **34,410** | **225 MiB** | **48.7** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **55,620** | **55,620** | **226 MiB** | **49.3** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 · TLS | HTTP/2 · plain | 🥇 **96,923** | **96,923** | **113 MiB** | **37.8** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 · TLS | HTTP/3 · QUIC | 🥇 **32,204** | **32,204** | **230 MiB** | **47.5** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **31,313** | **31,313** | **295 MiB** | **46.9** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/3 · QUIC | HTTP/2 · TLS | 🥇 **30,142** | **30,142** | **279 MiB** | **47.6** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/3 · QUIC | HTTP/3 · QUIC | 🥇 **23,327** | **23,327** | **277 MiB** | **45.3** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/1 · plain (CONNECT) | HTTP/1 · TLS | 🥇 **37,282** | **37,282** | **128 MiB** | **46.9** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/1 · TLS | HTTP/1 · TLS | 🥇 **39,263** | **39,263** | **108 MiB** | **45.8** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **76,804** | **76,804** | **113 MiB** | **36.5** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 · TLS | HTTP/1 · TLS | 🥇 **43,868** | **43,868** | **209 MiB** | **45.9** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **26,330** | **26,330** | **290 MiB** | **45** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |

On this GHA shape, TWP H1 plain ÷ nginx H1 plain ≈ **0.83** (46,284 / 55,862). H1 TLS terminate ≈ **0.85** (36,518 / 43,011). TWP÷YARP H1 plain ≈ **1.12×** (46,284 / 41,203). Bridges @ `0ff3673c`: H3→H1 ≈ **1.09×** (34,129 / 31,205), H3→H2 ≈ **1.01×**, H2 TLS→H1 ≈ **1.07×**, H1→H3 ≈ **1.05×**; h2c→H1 ≈ **0.985×** (66,809 / 67,822 — within ~1.5% of YARP). Absolute RPS swings by VM; prefer the **ratio** and **median across repeats**. MITM publishes the same **15** Client×Origin pairs as Reverse (inspectable/decrypt), then dual-crypto extras (CONNECT, TLS↔TLS). nginx/YARP cannot MITM.

**nginx HTTP/3:** inbound QUIC terminate → cleartext H1 (`nginx-reverse-http3-cleartext`) @ `0ff3673c` bridges: sustain **0** (p99/error SLO miss) / peak **40,727**. TWP/YARP H3→H1 on this row are from the same bridges pass. nginx still cannot speak HTTP/3 to an origin (no H3 upstream in this conf).

**YARP HTTP/3 (this matrix):** TWP leads H3→H1 ≈ **1.09×** (34,129 / 31,205), H3→H2 ≈ **1.01×** (33,159 / 32,811). H1→H2 ≈ **1.03×** (48,038 / 46,712). H1→H3 ≈ **1.05×** (30,886 / 29,371). h2c→H3 ≈ **1.02×**.

**Windows vs Linux:** both CI envs are **4 vCPU / 16 GiB**, but do **not** compare absolute RPS across OS. Linux nginx leads H1 plain/TLS terminate (TWP second, ahead of YARP). Windows bridges @ `0ff3673c` closed the last YARP-led tiny-GET cell (H3→H1 ≈ **1.12×**). Cool laptop notes remain on [Performance Profiling](Performance-Profiling#local-windows-lab-developer-laptop).


### Tiny JSON reverse is nginx’s best case on Linux

The tables above use **~64 B keep-alive GET** on loopback. On Linux H1 reverse, nginx leads; YARP sits near TWP. Heavier bodies, POSTs, TLS handshake cost, and lossy/HOL workloads (below) change the picture. MITM rows remain TWP-only. nginx HTTP/3 is inbound-terminate only (see note above).

### Why isn’t HTTP/3 > HTTP/2 > HTTP/1 in raw RPS?

For **tiny JSON responses** (~64 B) on loopback, that ordering is **not** expected: topology (TLS hop count, terminate vs MITM) dominates; HTTP/2 and HTTP/3 help multiplexing, not single-origin tiny-GET RPS. See the **lossy** tables below for a workload where protocol design matters.

## Heavier reverse workloads

Separate from the tiny-GET matrix. Same measurement environments. Modes: `compare-bodies`, `compare-post`, `compare-lossy`, `compare-tls-cost`, `compare-arch` in [RpsLoadProbe](https://github.com/justcoding121/titanium-web-proxy/tree/develop/tools/RpsLoadProbe). **PUT with the same body is the same proxy work as POST; DELETE with no body matches GET** — only POST is published. Bodies/POST/lossy stay **half-duplex**. `compare-arch` is the slow-consumer / early-response / duplex set. Laptop numbers are on [Performance-Profiling](Performance-Profiling#architecture-sensitive); CI medians go in the tables below.

Lossy link = **userspace** shim (not kernel `netem`): TCP gets per-buffer delay + occasional whole-connection stalls (honest HOL for multiplexed H2); UDP gets per-datagram delay + drops (QUIC). `compare-lossy` publishes H1/H2/H3; H3 is where the protocol design is supposed to matter.

### Windows — heavier reverse GET (64 KiB / 256 KiB)

Median of **3** repeats on `windows-latest` @ `1f2d0eee`. Source: Actions [32688081527](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32688081527) (`compare-bodies`). Warmup 2s / measure 8s. **TWP Memory** at peak-RPS step noted in narrative.

| Body | Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---|---|---:|---:|---:|---:|---:|---:|
| 64 KiB | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **8,600** | **8,600** | **906** | **906** | **6,903** | **6,903** |
| 64 KiB | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **8,011** | **8,011** | **779** | **779** | **5,789** | **5,789** |
| 64 KiB | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **4,127** | **4,127** | *Not possible* (no QUIC) | *Not possible* (no QUIC) | **3,709** | **3,709** |
| 256 KiB | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **2,824** | **2,824** | **249** | **249** | **1,884** | **1,884** |
| 256 KiB | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **2,504** | **2,504** | **170** | **170** | **1,958** | **1,958** |
| 256 KiB | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **1,108** | **1,108** | *Not possible* (no QUIC) | *Not possible* (no QUIC) | **1,048** | **1,048** |

nginx/Windows collapses on large reverse bodies in this harness; treat as same-OS only. H1 TLS **64 KiB** ≈ **1.25×** YARP (TWP Memory ~97 MiB); **256 KiB** ≈ **1.50×**. H2→H1 64 KiB ≈ **1.38×**; H3→H1 64 KiB ≈ **1.11×**.

### Linux — heavier reverse GET (64 KiB / 256 KiB)

Median of **3** repeats @ `1f2d0eee`. Source: Actions [32688081527](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32688081527) (`compare-bodies`). Warmup 2s / measure 8s.

| Body | Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---|---|---:|---:|---:|---:|---:|---:|
| 64 KiB | HTTP/1 · TLS | HTTP/1 · plain | **12,030** | **12,030** | 🥇 **13,402** | **13,402** | **9,932** | **9,932** |
| 64 KiB | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **8,850** | **8,850** | **6,886** | **6,886** | **7,294** | **7,294** |
| 64 KiB | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **7,504** | **7,504** | **2,417** | **2,417** | **5,191** | **5,191** |
| 256 KiB | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **3,868** | **3,868** | **3,850** | **3,850** | **2,912** | **2,912** |
| 256 KiB | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **2,249** | **2,249** | **1,804** | **1,804** | **1,919** | **1,919** |
| 256 KiB | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **1,864** | **1,864** | **642** | **642** | **1,533** | **1,533** |

On this GHA pass TWP÷YARP H1 TLS ≈ **1.21×** (64 KiB) / **1.33×** (256 KiB); H2→H1 ≈ **1.21×** / **1.17×**; H3→H1 ≈ **1.45×** / **1.22×**. TWP÷nginx H1 TLS ≈ **0.90** / **1.00**. Absolute RPS swings by VM; prefer ratios.

### Windows — POST 64 KiB request + 64 KiB response

Median of **3** repeats on `windows-latest` @ `21396a4d`. Source: Actions [32608567396](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32608567396) (`compare-post`). **Tip remasure @ `1f2d0eee` blocked:** three-process harness since `bf01825b` hangs TWP H1 keep-alive POST (RPS ≈ concurrency; YARP/nginx POST OK). H2/H3 POST tip medians were healthy (~3.8k / ~2.3k Win) but H1 POST needs a product fix before republishing.
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

Userspace **5 ms** one-way delay + **1%** TCP connection stall (H1/H2) or UDP datagram drop (H3); **64 KiB** GET. Median of **3** repeats on `windows-latest` @ `1f2d0eee`. Source: [32688085411](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32688085411) (`compare-lossy`). H3 on GHA Windows collapses through the userspace UDP shim (sustain ≈ **0**); use the [laptop lab](Performance-Profiling#lossy--high-rtt-h2-hol--h3-packet-loss) for the Windows H3 signal.

| Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---|---:|---:|---:|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | **663** | **663** | **645** | **645** | 🥇 **663** | **663** |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **86** | **86** | **18** | **18** | **18** | **18** |
| HTTP/3 · QUIC | HTTP/1 · plain | *Not measured* (GHA UDP-shim) | *Not measured* | *Not possible* (no QUIC) | *Not possible* | *Not measured* (GHA UDP-shim) | *Not measured* |

TWP H2 HOL leads (~**4.8×** YARP). H1 ≈ parity with YARP.

### Linux — lossy / high-RTT (H2 HOL / H3 loss)

Median of **3** repeats @ `1f2d0eee`. Source: [32688085411](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32688085411) (`compare-lossy`; lossy H3 uses `quic-http3`).

| Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---|---:|---:|---:|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | 🥇 **1,204** | **1,204** | **1,209** | **1,209** | **1,199** | **1,199** |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **314** | **314** | **40** | **40** | **44** | **44** |
| HTTP/3 · QUIC | HTTP/1 · plain | **328** | **328** | **86** | **86** | 🥇 **332** | **332** |

TWP H2 HOL ≫ YARP (~**7.1×**). H3 ≈ parity with YARP (~**0.99×**).

### Architecture-sensitive

`compare-arch` isolates slow app readers, origin-early response, H2 duplex, and WebSocket echo. See [TWP vs YARP IO model](Performance-Profiling#twp-vs-yarp-io-model). Laptop 1-rep numbers are on [Performance-Profiling](Performance-Profiling#architecture-sensitive).

Median of **3** repeats on matched 4 vCPU / 16 GiB runners @ `1f2d0eee` ([32688089789](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32688089789)) (`compare-arch`). Slow consumer = 256 KiB GET, 16 KiB read + 8 ms sleep. Early response = 64 KiB POST, origin writes after 8 KiB. Duplex H2 = overlapping 64 KiB POST on H2 TLS↔H2 TLS. WebSocket = echo round-trips/sec. **H1 early-response tip hang** (RPS≈8) is the same three-process POST body issue as compare-post; H2/H3 early and slow/duplex/WS cells below are tip medians.

`compare-lossy` (slow **network**) is already published above; it is not a slow **app** reader.

#### Windows

| Scenario | Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---|---|---:|---:|---:|---:|---:|---:|
| Slow consumer (256 KiB GET, throttled client read) | HTTP/1 · TLS | HTTP/1 · plain | **248** | **248** | **218** | **218** | **241** | **241** |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · TLS | HTTP/1 · plain | **248** | **248** | **198** | **198** | 🥇 **256** | **256** |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **263** | **263** | *Not possible* (no QUIC) | *Not possible* | **257** | **257** |
| Early response (origin writes after first request chunk) | HTTP/1 · TLS | HTTP/1 · plain | *blocked* (POST hang) | *blocked* | **413** | **413** | 🥇 **5,383** | **5,383** |
| Early response (origin writes after first request chunk) | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **5,786** | **5,786** | **401** | **401** | **3,452** | **3,452** |
| Early response (origin writes after first request chunk) | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **2,683** | **2,683** | *Not possible* (no QUIC) | *Not possible* | **2,261** | **2,261** |
| Duplex (both directions live) | HTTP/2 · TLS | HTTP/2 · TLS | **1,670** | **1,670** | *Not possible* | *Not possible* | 🥇 **2,666** | **2,666** |
| Duplex (WebSocket / extended CONNECT) | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **35,743** | **35,743** | **24,549** | **24,549** | **33,006** | **33,006** |

#### Linux

| Scenario | Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---|---|---:|---:|---:|---:|---:|---:|
| Slow consumer (256 KiB GET, throttled client read) | HTTP/1 · TLS | HTTP/1 · plain | **467** | **467** | 🥇 **472** | **472** | **420** | **420** |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · TLS | HTTP/1 · plain | **476** | **476** | 🥇 **478** | **478** | **470** | **470** |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **474** | **474** | **427** | **427** | **471** | **471** |
| Early response (origin writes after first request chunk) | HTTP/1 · TLS | HTTP/1 · plain | *blocked* (POST hang) | *blocked* | 🥇 **4,375** | **4,375** | **3,413** | **3,413** |
| Early response (origin writes after first request chunk) | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **3,448** | **3,448** | **2,147** | **2,147** | **2,528** | **2,528** |
| Early response (origin writes after first request chunk) | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **3,135** | **3,135** | **789** | **789** | **2,234** | **2,234** |
| Duplex (both directions live) | HTTP/2 · TLS | HTTP/2 · TLS | **579** | **579** | *Not possible* | *Not possible* | 🥇 **1,862** | **1,862** |
| Duplex (WebSocket / extended CONNECT) | HTTP/1 · TLS | HTTP/1 · plain | **34,995** | **34,995** | 🥇 **39,340** | **39,340** | **31,648** | **31,648** |

Slow consumer is sleep-bound; H1/H2/H3 sit in the same band once bodies stream. Early-response H2/H3: TWP leads. H1 early blocked by keep-alive POST hang under three-process. WebSocket: TWP leads Windows ≈ **1.08×** YARP (35,743 / 33,006); Linux nginx leads.

### TLS termination cost (H1 TLS → cleartext origin)

Isolates keep-alive tiny GET vs **new connection per request** (handshake-dominated) vs keep-alive **256 KiB**. Product comparison uses RPS and end-to-end latency; TWP can also capture `ClientTlsTiming` when `TWP_RPS_CAPTURE_TLS=1` (child process) — nginx/YARP have no equivalent hook.

#### Windows

Median of **3** repeats on `windows-latest` @ `1f2d0eee`. Source: Actions [32688087733](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32688087733) (`compare-tls-cost`). Absolute RPS on GHA swings hard; prefer **TWP÷YARP**.

| Workload | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---:|---:|---:|---:|---:|---:|
| Keep-alive · tiny GET | 🥇 **25,426** | **25,426** | **12,864** | **12,864** | **23,364** | **23,364** |
| New-connection · tiny GET | 🥇 **769** | **769** | **256** | **256** | **753** | **753** |
| Keep-alive · 256 KiB GET | 🥇 **2,886** | **2,886** | **259** | **259** | **2,512** | **2,512** |

#### Linux

Median of **3** repeats @ `1f2d0eee`. Source: Actions [32688087733](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32688087733) (`compare-tls-cost`).

| Workload | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---:|---:|---:|---:|---:|---:|
| Keep-alive · tiny GET | **51,331** | **51,331** | 🥇 **66,322** | **66,322** | **45,016** | **45,016** |
| New-connection · tiny GET | **1,666** | **1,666** | 🥇 **1,729** | **1,729** | **1,652** | **1,652** |
| Keep-alive · 256 KiB GET | **5,208** | **5,208** | 🥇 **5,272** | **5,272** | **4,130** | **4,130** |

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
