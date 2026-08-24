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

Calibration for the shared 4 vCPU loopback shape: how close client + origin are to saturated before ranking reverse peers. Tiny keep-alive GET. Median of **3** repeats @ `0ff3673c` — [32685356597](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32685356597). Warmup 2s / measure 8s; concurrency 8, 16, 32, 64. Block A **% of origin-HttpClient** uses median **peak** RPS. Blocks B/C use peer÷YARP / ÷nginx on median peak (not % of H1 origin). **RPS cells** embed median RSS / CPU for the **proxy child** plus its **full descendant tree** (serve-proxy → nginx master → workers); origin-direct samples the **origin** child. Product matrices below use matched `dotnet-httpclient` only (not bombardier). **H2→H1** after `Http2PendingWork` + lite wire + **`ClientSyntheticStreams` / `TryTakeStream`** (clears the NullOrigin stream-id bag that grew unboundedly on keep-alive multiplex): Win / Linux RSS in Block B cells below are still the **pre-syntheticStreams** paste (was ~848 / ~626 MiB before PendingWork/lite); laptop cool post-fix Memory ÷YARP ≈ **1.1–1.2×** — see [Performance Profiling — Memory](Performance-Profiling#memory-rss--h2h1-vs-h1--h3). RPS still leads YARP. **Pending GHA remasure** to rewrite published MiB cells.

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
| origin-direct | dotnet-httpclient | **61,873**<br><sub>(53 MiB / 44.7% CPU)</sub> | **61,983**<br><sub>(53 MiB / 44.7% CPU)</sub> | **100%** |
| origin-direct-bombardier | bombardier | **46,950**<br><sub>(54 MiB / 27.5% CPU)</sub> | **47,181**<br><sub>(54 MiB / 27.5% CPU)</sub> | **76.1%** |
| bare-reverse-http1 | dotnet-httpclient | **30,801**<br><sub>(55 MiB / 45.9% CPU)</sub> | **30,801**<br><sub>(55 MiB / 45.9% CPU)</sub> | **49.7%** |
| nginx-reverse-http1 | dotnet-httpclient | **19,151**<br><sub>(120 MiB / 24.8% CPU)</sub> | **19,451**<br><sub>(120 MiB / 24.8% CPU)</sub> | **31.4%** |
| yarp-reverse-http1 | dotnet-httpclient | **26,913**<br><sub>(85 MiB / 49.8% CPU)</sub> | **26,913**<br><sub>(85 MiB / 49.8% CPU)</sub> | **43.4%** |
| twp-reverse-http1 | dotnet-httpclient | 🥇 **31,869**<br><sub>(77 MiB / 48.6% CPU)</sub> | **31,869**<br><sub>(77 MiB / 48.6% CPU)</sub> | **51.4%** |

**Linux** (`ubuntu-latest`)

| Arm | Generator | Sustain | Peak | % of origin-HttpClient |
|---|---|---:|---:|---:|
| origin-direct | dotnet-httpclient | **97,911**<br><sub>(79 MiB / 44.1% CPU)</sub> | **97,912**<br><sub>(79 MiB / 44.1% CPU)</sub> | **100%** |
| origin-direct-bombardier | bombardier | **61,344**<br><sub>(79 MiB / 38.2% CPU)</sub> | **61,541**<br><sub>(79 MiB / 38.2% CPU)</sub> | **62.9%** |
| bare-reverse-http1 | dotnet-httpclient | **46,344**<br><sub>(70 MiB / 46.7% CPU)</sub> | **46,344**<br><sub>(70 MiB / 46.7% CPU)</sub> | **47.3%** |
| nginx-reverse-http1 | dotnet-httpclient | 🥇 **55,817**<br><sub>(72 MiB / 41.2% CPU)</sub> | **55,817**<br><sub>(72 MiB / 41.2% CPU)</sub> | **57.0%** |
| yarp-reverse-http1 | dotnet-httpclient | **41,821**<br><sub>(118 MiB / 49.6% CPU)</sub> | **41,821**<br><sub>(118 MiB / 49.6% CPU)</sub> | **42.7%** |
| twp-reverse-http1 | dotnet-httpclient | **48,510**<br><sub>(89 MiB / 50.9% CPU)</sub> | **48,510**<br><sub>(89 MiB / 50.9% CPU)</sub> | **49.5%** |

Reverse peers are about **28–57%** of the origin-direct HttpClient peak on this runner class. Prefer the **%** column over absolute RPS across runs. Bare and origin-direct are controls (not medal peers).

#### Block B — H2 TLS→H1

Peer ratios (÷YARP / ÷nginx) on median peak; **RPS cells** embed `(MiB / CPU%)`.

**Windows** (`windows-latest`)

| Arm | Generator | Sustain | Peak | ÷YARP | ÷nginx |
|---|---|---:|---:|---:|---:|
| nginx-reverse-http2 | dotnet-httpclient | **15,025**<br><sub>(137 MiB / 23.7% CPU)</sub> | **15,025**<br><sub>(137 MiB / 23.7% CPU)</sub> | **0.43×** | **1.00×** |
| yarp-reverse-http2 | dotnet-httpclient | **35,176**<br><sub>(94 MiB / 50.7% CPU)</sub> | **35,176**<br><sub>(94 MiB / 50.7% CPU)</sub> | **1.00×** | **2.34×** |
| twp-reverse-http2-cleartext | dotnet-httpclient | 🥇 **37,842**<br><sub>(149 MiB / 52.9% CPU)</sub> | **37,842**<br><sub>(149 MiB / 52.9% CPU)</sub> | **1.08×** | **2.52×** |

**Linux** (`ubuntu-latest`)

| Arm | Generator | Sustain | Peak | ÷YARP | ÷nginx |
|---|---|---:|---:|---:|---:|
| nginx-reverse-http2 | dotnet-httpclient | **21,348**<br><sub>(97 MiB / 23.4% CPU)</sub> | **29,587**<br><sub>(97 MiB / 23.4% CPU)</sub> | **0.66×** | **1.00×** |
| yarp-reverse-http2 | dotnet-httpclient | **44,626**<br><sub>(122 MiB / 48.5% CPU)</sub> | **44,626**<br><sub>(122 MiB / 48.5% CPU)</sub> | **1.00×** | **1.51×** |
| twp-reverse-http2-cleartext | dotnet-httpclient | 🥇 **49,714**<br><sub>(227 MiB / 52.3% CPU)</sub> | **49,714**<br><sub>(227 MiB / 52.3% CPU)</sub> | **1.11×** | **1.68×** |

#### Block C — H3→H1

Same layout as Block B. Requires QuicListener; nginx only with `http_v3_module` (Windows nginx has no QUIC).

**Windows** (`windows-latest`)

| Arm | Generator | Sustain | Peak | ÷YARP | ÷nginx |
|---|---|---:|---:|---:|---:|
| nginx-reverse-http3-cleartext | dotnet-httpclient | *Not possible* (no QUIC) | *Not possible* | — | — |
| yarp-reverse-http3-cleartext | dotnet-httpclient | **18,857**<br><sub>(164 MiB / 49.4% CPU)</sub> | **18,857**<br><sub>(164 MiB / 49.4% CPU)</sub> | **1.00×** | — |
| twp-reverse-http3-cleartext | dotnet-httpclient | 🥇 **19,758**<br><sub>(233 MiB / 44.5% CPU)</sub> | **19,758**<br><sub>(233 MiB / 44.5% CPU)</sub> | **1.05×** | — |

**Linux** (`ubuntu-latest`)

| Arm | Generator | Sustain | Peak | ÷YARP | ÷nginx |
|---|---|---:|---:|---:|---:|
| nginx-reverse-http3-cleartext | dotnet-httpclient | **0**<br><sub>(108 MiB / 22% CPU)</sub> | **25,054**<br><sub>(108 MiB / 22% CPU)</sub> | **0.90×** | **1.00×** |
| yarp-reverse-http3-cleartext | dotnet-httpclient | **27,950**<br><sub>(193 MiB / 49.8% CPU)</sub> | **27,950**<br><sub>(193 MiB / 49.8% CPU)</sub> | **1.00×** | **1.12×** |
| twp-reverse-http3-cleartext | dotnet-httpclient | 🥇 **31,466**<br><sub>(304 MiB / 52.4% CPU)</sub> | **31,466**<br><sub>(304 MiB / 52.4% CPU)</sub> | **1.13×** | **1.26×** |

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

Median of **3 repeats** on `windows-latest` (4 vCPU / 16 GiB). Same/MITM/bridges @ `1f2d0eee` — same [32688076110](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32688076110), [32688077908](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32688077908), bridges [32685354747](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32685354747) @ `0ff3673c` (product tip; wiki paste @ `1f2d0eee`). Three-process harness, parent-seeded loopback CA. Warmup 2s / measure 8s; concurrency 8, 16, 32, 64. Prefer TWP÷peer ratios over absolute RPS. **RPS cells** include median RSS / CPU at the peak-RPS step as `<br><sub>(MiB / CPU%)</sub>` for TWP, nginx, and YARP (proxy child + descendant tree). H2→H1 / h2c→H1 / H2→H3 published MiB are **pre-`ClientSyntheticStreams`** until the next GHA paste (laptop cool ≈ **1.1–1.2×** YARP Memory — [Profiling](Performance-Profiling#memory-rss--h2h1-vs-h1--h3)). Laptop High-perf / cool-paired numbers stay on the [local lab](Performance-Local-Lab).

**Load generators:** Reverse inbound H3 arms use **`dotnet-httpclient`** (`http_version=3.0`, `RequestVersionExact`). nginx/Windows is same-OS only (no QUIC).

| Mode | Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---|---|---:|---:|---:|---:|---:|---:|
| Reverse | HTTP/1 · plain | HTTP/1 · plain | 🥇 **25,525**<br><sub>(76 MiB / 52.9% CPU)</sub> | **25,525**<br><sub>(76 MiB / 52.9% CPU)</sub> | **13,325**<br><sub>(120 MiB / 24.8% CPU)</sub> | **13,325**<br><sub>(120 MiB / 24.8% CPU)</sub> | **21,341**<br><sub>(88 MiB / 49.6% CPU)</sub> | **21,341**<br><sub>(88 MiB / 49.6% CPU)</sub> |
| Reverse | HTTP/1 · plain | HTTP/1 · TLS | 🥇 **21,069**<br><sub>(84 MiB / 52% CPU)</sub> | **21,069**<br><sub>(84 MiB / 52% CPU)</sub> | *Not possible* | *Not possible* | **19,498**<br><sub>(100 MiB / 52.2% CPU)</sub> | **19,498**<br><sub>(100 MiB / 52.2% CPU)</sub> |
| Reverse | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **20,286**<br><sub>(82 MiB / 50.5% CPU)</sub> | **20,286**<br><sub>(82 MiB / 50.5% CPU)</sub> | **8,980**<br><sub>(137 MiB / 24.9% CPU)</sub> | **8,980**<br><sub>(137 MiB / 24.9% CPU)</sub> | **17,956**<br><sub>(103 MiB / 50.6% CPU)</sub> | **17,956**<br><sub>(103 MiB / 50.6% CPU)</sub> |
| Reverse | HTTP/1 · TLS | HTTP/2 · TLS | 🥇 **42,165**<br><sub>(115 MiB / 50.6% CPU)</sub> | **42,165**<br><sub>(115 MiB / 50.6% CPU)</sub> | *Not possible* | *Not possible* | **41,148**<br><sub>(111 MiB / 50.9% CPU)</sub> | **41,148**<br><sub>(111 MiB / 50.9% CPU)</sub> |
| Reverse | HTTP/1 · TLS | HTTP/3 · QUIC | 🥇 **24,823**<br><sub>(118 MiB / 54% CPU)</sub> | **24,823**<br><sub>(118 MiB / 54% CPU)</sub> | *Not possible* (no QUIC) | *Not possible* (no QUIC) | **24,684**<br><sub>(131 MiB / 52.9% CPU)</sub> | **24,684**<br><sub>(131 MiB / 52.9% CPU)</sub> |
| Reverse | HTTP/2 · plain | HTTP/1 · plain | 🥇 **56,585**<br><sub>(195 MiB / 52.6% CPU)</sub> | **56,585**<br><sub>(195 MiB / 52.6% CPU)</sub> | *Not possible* | *Not possible* | **52,676**<br><sub>(87 MiB / 54% CPU)</sub> | **52,676**<br><sub>(87 MiB / 54% CPU)</sub> |
| Reverse | HTTP/2 · plain | HTTP/2 · plain | 🥇 **122,084**<br><sub>(74 MiB / 35.2% CPU)</sub> | **122,084**<br><sub>(74 MiB / 35.2% CPU)</sub> | *Not possible* | *Not possible* | **89,134**<br><sub>(100 MiB / 52.3% CPU)</sub> | **89,134**<br><sub>(100 MiB / 52.3% CPU)</sub> |
| Reverse | HTTP/2 · plain | HTTP/2 · TLS | 🥇 **74,430**<br><sub>(81 MiB / 38.1% CPU)</sub> | **74,430**<br><sub>(81 MiB / 38.1% CPU)</sub> | *Not possible* | *Not possible* | **55,671**<br><sub>(95 MiB / 47.9% CPU)</sub> | **55,671**<br><sub>(95 MiB / 47.9% CPU)</sub> |
| Reverse | HTTP/2 · plain | HTTP/3 · QUIC | 🥇 **42,251**<br><sub>(185 MiB / 53.1% CPU)</sub> | **42,251**<br><sub>(185 MiB / 53.1% CPU)</sub> | *Not possible* (no QUIC) | *Not possible* (no QUIC) | **41,584**<br><sub>(137 MiB / 53.5% CPU)</sub> | **41,584**<br><sub>(137 MiB / 53.5% CPU)</sub> |
| Reverse | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **54,493**<br><sub>(200 MiB / 52.6% CPU)</sub> | **54,493**<br><sub>(200 MiB / 52.6% CPU)</sub> | **11,057**<br><sub>(137 MiB / 24.4% CPU)</sub> | **11,057**<br><sub>(137 MiB / 24.4% CPU)</sub> | **47,759**<br><sub>(95 MiB / 52.4% CPU)</sub> | **47,759**<br><sub>(95 MiB / 52.4% CPU)</sub> |
| Reverse | HTTP/2 · TLS | HTTP/2 · plain | 🥇 **112,459**<br><sub>(92 MiB / 35.7% CPU)</sub> | **112,459**<br><sub>(92 MiB / 35.7% CPU)</sub> | *Not possible* | *Not possible* | **77,976**<br><sub>(104 MiB / 53.2% CPU)</sub> | **77,976**<br><sub>(104 MiB / 53.2% CPU)</sub> |
| Reverse | HTTP/2 · TLS | HTTP/3 · QUIC | 🥇 **40,976**<br><sub>(201 MiB / 56.3% CPU)</sub> | **40,976**<br><sub>(201 MiB / 56.3% CPU)</sub> | *Not possible* (no QUIC) | *Not possible* (no QUIC) | **37,267**<br><sub>(137 MiB / 53.6% CPU)</sub> | **37,267**<br><sub>(137 MiB / 53.6% CPU)</sub> |
| Reverse | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **27,046**<br><sub>(255 MiB / 46.4% CPU)</sub> | **27,046**<br><sub>(255 MiB / 46.4% CPU)</sub> | *Not possible* (no QUIC) | *Not possible* (no QUIC) | **24,085**<br><sub>(162 MiB / 51% CPU)</sub> | **24,085**<br><sub>(162 MiB / 51% CPU)</sub> |
| Reverse | HTTP/3 · QUIC | HTTP/2 · TLS | 🥇 **35,922**<br><sub>(233 MiB / 46% CPU)</sub> | **35,922**<br><sub>(233 MiB / 46% CPU)</sub> | *Not possible* (no QUIC) | *Not possible* (no QUIC) | **32,152**<br><sub>(181 MiB / 50.9% CPU)</sub> | **32,152**<br><sub>(181 MiB / 50.9% CPU)</sub> |
| Reverse | HTTP/3 · QUIC | HTTP/3 · QUIC | 🥇 **14,958**<br><sub>(190 MiB / 49.7% CPU)</sub> | **14,958**<br><sub>(190 MiB / 49.7% CPU)</sub> | *Not possible* (no QUIC) | *Not possible* (no QUIC) | **11,653**<br><sub>(162 MiB / 52.1% CPU)</sub> | **11,653**<br><sub>(162 MiB / 52.1% CPU)</sub> |
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

TWP÷YARP H1 plain ≈ **1.20×** (25,525 / 21,341); H1 TLS terminate ≈ **1.13×**. Bridges: **all published TWP÷YARP ≥1.00×** — H3→H1 ≈ **1.12×** (27,046 / 24,085; closed prior **0.993×** gap). Prefer ratios over absolute RPS on GHA VMs. **Memory (RSS):** published H2→H1 / h2c→H1 MiB cells are **pre-`ClientSyntheticStreams`**; laptop cool after the keep ≈ **1.1–1.2×** YARP ([Profiling](Performance-Profiling#memory-rss--h2h1-vs-h1--h3)) — rewrite after GHA `compare-saturation` / `compare-bridges` / `compare-same`. MITM publishes the same **15** Client×Origin pairs as Reverse (inspectable/decrypt), then dual-crypto extras (CONNECT, TLS↔TLS). nginx/YARP cannot MITM.

## Linux — Titanium vs nginx vs YARP

Median of **3 repeats** on `ubuntu-latest` (4 vCPU / 16 GiB). Same/MITM @ `1f2d0eee` ([32688076110](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32688076110), [32688077908](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32688077908)); bridges @ `0ff3673c` ([32685354747](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32685354747)). Warmup 2s / measure 8s; concurrency 8, 16, 32, 64. **Linux nginx is the authoritative nginx baseline.** The RPS workflow installs nginx.org mainline (`http_v3_module`) and `libmsquic` (`QuicListener.IsSupported=true` on `ubuntu-latest`). Prefer ratios over absolute RPS. **RPS cells** include peer `(MiB / CPU%)` as on Windows.

TWP÷nginx H1 plain reverse ≈ **0.83** (46,284 / 55,862); TWP÷YARP H1 plain ≈ **1.12×**.

| Mode | Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---|---|---:|---:|---:|---:|---:|---:|
| Reverse | HTTP/1 · plain | HTTP/1 · plain | **46,284**<br><sub>(82 MiB / 51.1% CPU)</sub> | **46,284**<br><sub>(82 MiB / 51.1% CPU)</sub> | 🥇 **55,862**<br><sub>(72 MiB / 41.2% CPU)</sub> | **55,862**<br><sub>(72 MiB / 41.2% CPU)</sub> | **41,203**<br><sub>(114 MiB / 49.4% CPU)</sub> | **41,203**<br><sub>(114 MiB / 49.4% CPU)</sub> |
| Reverse | HTTP/1 · plain | HTTP/1 · TLS | 🥇 **37,194**<br><sub>(103 MiB / 48.6% CPU)</sub> | **37,194**<br><sub>(103 MiB / 48.6% CPU)</sub> | *Not possible* | *Not possible* | **33,500**<br><sub>(140 MiB / 49.2% CPU)</sub> | **33,500**<br><sub>(140 MiB / 49.2% CPU)</sub> |
| Reverse | HTTP/1 · TLS | HTTP/1 · plain | **36,518**<br><sub>(107 MiB / 49.1% CPU)</sub> | **36,518**<br><sub>(107 MiB / 49.1% CPU)</sub> | 🥇 **43,011**<br><sub>(99 MiB / 41.6% CPU)</sub> | **43,011**<br><sub>(99 MiB / 41.6% CPU)</sub> | **32,495**<br><sub>(135 MiB / 50% CPU)</sub> | **32,495**<br><sub>(135 MiB / 50% CPU)</sub> |
| Reverse | HTTP/1 · TLS | HTTP/2 · TLS | 🥇 **48,038**<br><sub>(158 MiB / 48.1% CPU)</sub> | **48,038**<br><sub>(158 MiB / 48.1% CPU)</sub> | *Not possible* | *Not possible* | **46,712**<br><sub>(145 MiB / 47.9% CPU)</sub> | **46,712**<br><sub>(145 MiB / 47.9% CPU)</sub> |
| Reverse | HTTP/1 · TLS | HTTP/3 · QUIC | 🥇 **30,886**<br><sub>(163 MiB / 51.8% CPU)</sub> | **30,886**<br><sub>(163 MiB / 51.8% CPU)</sub> | *Not possible* (no H3 origin) | *Not possible* (no H3 origin) | **29,371**<br><sub>(179 MiB / 50.7% CPU)</sub> | **29,371**<br><sub>(179 MiB / 50.7% CPU)</sub> |
| Reverse | HTTP/2 · plain | HTTP/1 · plain | **66,809**<br><sub>(229 MiB / 49.7% CPU)</sub> | **66,809**<br><sub>(229 MiB / 49.7% CPU)</sub> | *Not possible* | *Not possible* | 🥇 **67,822**<br><sub>(119 MiB / 48.4% CPU)</sub> | **67,822**<br><sub>(119 MiB / 48.4% CPU)</sub> |
| Reverse | HTTP/2 · plain | HTTP/2 · plain | 🥇 **123,947**<br><sub>(104 MiB / 40.6% CPU)</sub> | **123,947**<br><sub>(104 MiB / 40.6% CPU)</sub> | *Not possible* | *Not possible* | **90,109**<br><sub>(130 MiB / 47.4% CPU)</sub> | **90,109**<br><sub>(130 MiB / 47.4% CPU)</sub> |
| Reverse | HTTP/2 · plain | HTTP/2 · TLS | 🥇 **74,148**<br><sub>(108 MiB / 39.6% CPU)</sub> | **74,148**<br><sub>(108 MiB / 39.6% CPU)</sub> | *Not possible* | *Not possible* | **59,392**<br><sub>(138 MiB / 45.8% CPU)</sub> | **59,392**<br><sub>(138 MiB / 45.8% CPU)</sub> |
| Reverse | HTTP/2 · plain | HTTP/3 · QUIC | 🥇 **38,554**<br><sub>(230 MiB / 50% CPU)</sub> | **38,554**<br><sub>(230 MiB / 50% CPU)</sub> | *Not possible* (no H3 origin) | *Not possible* (no H3 origin) | **37,816**<br><sub>(164 MiB / 47.3% CPU)</sub> | **37,816**<br><sub>(164 MiB / 47.3% CPU)</sub> |
| Reverse | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **62,181**<br><sub>(232 MiB / 48.3% CPU)</sub> | **62,181**<br><sub>(232 MiB / 48.3% CPU)</sub> | **29,622**<br><sub>(96 MiB / 22.9% CPU)</sub> | **29,622**<br><sub>(96 MiB / 22.9% CPU)</sub> | **58,331**<br><sub>(121 MiB / 47.3% CPU)</sub> | **58,331**<br><sub>(121 MiB / 47.3% CPU)</sub> |
| Reverse | HTTP/2 · TLS | HTTP/2 · plain | 🥇 **113,846**<br><sub>(114 MiB / 39.4% CPU)</sub> | **113,846**<br><sub>(114 MiB / 39.4% CPU)</sub> | *Not possible* | *Not possible* | **71,600**<br><sub>(133 MiB / 46% CPU)</sub> | **71,600**<br><sub>(133 MiB / 46% CPU)</sub> |
| Reverse | HTTP/2 · TLS | HTTP/3 · QUIC | 🥇 **35,985**<br><sub>(234 MiB / 49.7% CPU)</sub> | **35,985**<br><sub>(234 MiB / 49.7% CPU)</sub> | *Not possible* (no H3 origin) | *Not possible* (no H3 origin) | **33,568**<br><sub>(176 MiB / 47.7% CPU)</sub> | **33,568**<br><sub>(176 MiB / 47.7% CPU)</sub> |
| Reverse | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **34,129**<br><sub>(308 MiB / 46.6% CPU)</sub> | **34,129**<br><sub>(308 MiB / 46.6% CPU)</sub> | **0**<br><sub>(109 MiB / 19.1% CPU)</sub> | **40,727**<br><sub>(109 MiB / 19.1% CPU)</sub> | **31,205**<br><sub>(203 MiB / 48% CPU)</sub> | **31,205**<br><sub>(203 MiB / 48% CPU)</sub> |
| Reverse | HTTP/3 · QUIC | HTTP/2 · TLS | 🥇 **33,159**<br><sub>(276 MiB / 47.1% CPU)</sub> | **33,159**<br><sub>(276 MiB / 47.1% CPU)</sub> | *Not possible* (no H3→H2) | *Not possible* (no H3→H2) | **32,811**<br><sub>(210 MiB / 45.6% CPU)</sub> | **32,811**<br><sub>(210 MiB / 45.6% CPU)</sub> |
| Reverse | HTTP/3 · QUIC | HTTP/3 · QUIC | 🥇 **26,711**<br><sub>(286 MiB / 49.3% CPU)</sub> | **26,711**<br><sub>(286 MiB / 49.3% CPU)</sub> | *Not possible* (no H3 origin) | *Not possible* (no H3 origin) | **21,999**<br><sub>(210 MiB / 47.8% CPU)</sub> | **21,999**<br><sub>(210 MiB / 47.8% CPU)</sub> |
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

On this GHA shape, TWP H1 plain ÷ nginx H1 plain ≈ **0.83** (46,284 / 55,862). H1 TLS terminate ≈ **0.85** (36,518 / 43,011). TWP÷YARP H1 plain ≈ **1.12×** (46,284 / 41,203). Bridges @ `0ff3673c`: H3→H1 ≈ **1.09×** (34,129 / 31,205), H3→H2 ≈ **1.01×**, H2 TLS→H1 ≈ **1.07×**, H1→H3 ≈ **1.05×**; h2c→H1 ≈ **0.985×** (66,809 / 67,822). Absolute RPS swings by VM; prefer the **ratio** and **median across repeats**. MITM publishes the same **15** Client×Origin pairs as Reverse (inspectable/decrypt), then dual-crypto extras (CONNECT, TLS↔TLS). nginx/YARP cannot MITM.

**nginx HTTP/3:** inbound QUIC terminate → cleartext H1 (`nginx-reverse-http3-cleartext`) @ `0ff3673c` bridges: sustain **0** (p99/error SLO miss) / peak **40,727**. TWP/YARP H3→H1 on this row are from the same bridges pass. nginx still cannot speak HTTP/3 to an origin (no H3 upstream in this conf).

**YARP HTTP/3 (this matrix):** TWP leads H3→H1 ≈ **1.09×** (34,129 / 31,205), H3→H2 ≈ **1.01×** (33,159 / 32,811). H1→H2 ≈ **1.03×** (48,038 / 46,712). H1→H3 ≈ **1.05×** (30,886 / 29,371). h2c→H3 ≈ **1.02×**.

**Windows vs Linux:** both CI envs are **4 vCPU / 16 GiB**, but do **not** compare absolute RPS across OS. Linux nginx leads H1 plain/TLS terminate (TWP second, ahead of YARP). Windows bridges @ `0ff3673c` closed the last YARP-led tiny-GET cell (H3→H1 ≈ **1.12×**). Cool laptop notes remain on [Performance Local Lab](Performance-Local-Lab).


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
