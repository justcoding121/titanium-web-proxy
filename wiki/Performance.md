# Performance

Titanium targets **low-overhead MITM proxying**: connection pooling, HTTP/2 multiplexing, and buffer reuse. Numbers below are **Release** measurements with [RpsLoadProbe](https://github.com/justcoding121/titanium-web-proxy/tree/develop/tools/RpsLoadProbe) (and BenchmarkDotNet / Basic example where noted). Publishable tables cite **GitHub Actions** medians on matched **4-core-class** runners (`ubuntu-latest` / `windows-latest`: **4 vCPU / 16 GiB**; `macos-15-intel`: **4-core / 14 GB**). Do not publish from `macos-latest` (3-core / 7 GB). Absolute RPS still varies by OS kernel, TLS, and MsQuic packaging — compare **within a table**, not across Windows vs Linux vs macOS.

Control arms: **nginx** (native C reverse-proxy ceiling; Linux is authoritative) and **YARP** (`Yarp.ReverseProxy`, managed .NET reverse proxy). Neither can MITM (no CONNECT / forged certs). FiddlerCore is not compared (commercial debugger license; not a throughput peer).

For pooling knobs and certificate first-visit tuning, see [Performance and pooling](Home#performance-and-pooling). For the local cool A/B lab, laptop tables, and profiling notes, see [Performance Profiling](Performance-Profiling).

## Contents

- [Measurement environment](#measurement-environment)
    - [Windows (GitHub-hosted `windows-latest`)](#windows-github-hosted-windows-latest)
    - [Linux (GitHub-hosted `ubuntu-latest`)](#linux-github-hosted-ubuntu-latest)
    - [macOS (GitHub-hosted `macos-15-intel`)](#macos-github-hosted-macos-15-intel)
    - [Tiered cadence](#tiered-cadence)
    - [Saturation control](#saturation-control)
- [Windows — Titanium vs nginx vs YARP](#windows--titanium-vs-nginx-vs-yarp)
- [Linux — Titanium vs nginx vs YARP](#linux--titanium-vs-nginx-vs-yarp)
    - [Tiny JSON reverse is nginx’s best case on Linux](#tiny-json-reverse-is-nginxs-best-case-on-linux)
    - [Why isn’t HTTP/3 > HTTP/2 > HTTP/1 in raw RPS?](#why-isnt-http3--http2--http1-in-raw-rps)
- [macOS — Titanium vs nginx vs YARP](#macos--titanium-vs-nginx-vs-yarp)
    - [Reverse](#reverse-2)
    - [MITM (TWP only)](#mitm-twp-only-2)
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

All three OS use the **4-core-class** public-repo GitHub-hosted runners: **Windows / Linux** at **4 vCPU / 16 GiB / 14 GB SSD**, **macOS** at **`macos-15-intel` 4-core / 14 GB** (not `macos-latest`). Same harness knobs (`workflow_dispatch` [RPS saturation](https://github.com/justcoding121/titanium-web-proxy/actions/workflows/rps-saturation.yml): warmup 2s / measure 8s; concurrency 8, 16, 32, 64; median of 3 repeats; `--stop-on-slo-fail` default on). Every `--ramp` arm is **three OS processes** (parent load generator + origin child + proxy child), except **origin-direct** arms (load gen + origin only). Prefer **TWP÷YARP** / **TWP÷nginx** ratios over absolute RPS.

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

### macOS (GitHub-hosted `macos-15-intel`)

| | |
|---|---|
| OS | macOS 15 (GitHub-hosted `macos-15-intel`, Intel x86_64) |
| CPU | **4** logical processors |
| RAM | **14** GB |
| Runtime | .NET 10.0.x |
| nginx | Homebrew nginx with `--with-http_v3_module` (workflow fails if missing) |
| MsQuic | Homebrew `libmsquic` + `openssl@3` on `DYLD_LIBRARY_PATH` / `DYLD_FALLBACK_LIBRARY_PATH` (`QuicListener.IsSupported`) |
| YARP | Yarp.ReverseProxy **2.3.0** |
| Harness | RpsLoadProbe Release; median of 3 repeats where noted |

Do **not** use `macos-latest` (Apple Silicon, 3-core / 7 GB) for publishable saturation numbers.

### Saturation control

Calibration for the shared 4 vCPU loopback shape: how close client + origin are to saturated before ranking reverse peers. Tiny keep-alive GET. Median of **3** repeats @ `024bd68d` — [34126812894](https://github.com/justcoding121/titanium-web-proxy/actions/runs/34126812894). Warmup 2s / measure 8s; concurrency 8, 16, 32, 64. Block A **% of origin-HttpClient** uses median **peak** RPS. Blocks B/C use peer÷YARP / ÷nginx on median peak (not % of H1 origin). **RPS cells** embed median RSS / CPU for the **proxy child** plus its **full descendant tree** (serve-proxy → nginx master → workers); origin-direct samples the **origin** child. Product matrices below use matched `dotnet-httpclient` only (not bombardier).



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
| origin-direct | dotnet-httpclient | **50,248**<br><sub>(54 MiB / 40.0% CPU)</sub> | **50,248**<br><sub>(54 MiB / 40.0% CPU)</sub> | **100.0%** |
| origin-direct-bombardier | bombardier | **39,040**<br><sub>(55 MiB / 24.0% CPU)</sub> | **39,040**<br><sub>(55 MiB / 24.0% CPU)</sub> | **77.7%** |
| bare-reverse-http1 | dotnet-httpclient | **25,031**<br><sub>(58 MiB / 44.0% CPU)</sub> | **25,031**<br><sub>(58 MiB / 44.0% CPU)</sub> | **49.8%** |
| nginx-reverse-http1 | dotnet-httpclient | **13,418**<br><sub>(123 MiB / 24.8% CPU)</sub> | **13,418**<br><sub>(123 MiB / 24.8% CPU)</sub> | **26.7%** |
| yarp-reverse-http1 | dotnet-httpclient | **21,010**<br><sub>(89 MiB / 50.2% CPU)</sub> | **21,010**<br><sub>(89 MiB / 50.2% CPU)</sub> | **41.8%** |
| twp-reverse-http1 | dotnet-httpclient | 🥇 **24,500**<br><sub>(75 MiB / 47.9% CPU)</sub> | **24,500**<br><sub>(75 MiB / 47.9% CPU)</sub> | **48.8%** |

**Linux** (`ubuntu-latest`)

| Arm | Generator | Sustain | Peak | % of origin-HttpClient |
|---|---|---:|---:|---:|
| origin-direct | dotnet-httpclient | **71,493**<br><sub>(79 MiB / 42.6% CPU)</sub> | **71,493**<br><sub>(79 MiB / 42.6% CPU)</sub> | **100.0%** |
| origin-direct-bombardier | bombardier | **42,014**<br><sub>(79 MiB / 34.9% CPU)</sub> | **42,014**<br><sub>(79 MiB / 34.9% CPU)</sub> | **58.8%** |
| bare-reverse-http1 | dotnet-httpclient | **32,717**<br><sub>(66 MiB / 45.0% CPU)</sub> | **32,717**<br><sub>(66 MiB / 45.0% CPU)</sub> | **45.8%** |
| nginx-reverse-http1 | dotnet-httpclient | 🥇 **39,137**<br><sub>(73 MiB / 40.8% CPU)</sub> | **39,137**<br><sub>(73 MiB / 40.8% CPU)</sub> | **54.7%** |
| yarp-reverse-http1 | dotnet-httpclient | **27,676**<br><sub>(114 MiB / 49.8% CPU)</sub> | **27,676**<br><sub>(114 MiB / 49.8% CPU)</sub> | **38.7%** |
| twp-reverse-http1 | dotnet-httpclient | **32,666**<br><sub>(85 MiB / 50.5% CPU)</sub> | **32,666**<br><sub>(85 MiB / 50.5% CPU)</sub> | **45.7%** |

Reverse peers are about **50–46%** of the origin-direct HttpClient peak on this runner class (Win TWP **50.0%**, Lin TWP **45.6%**). Prefer the **%** column over absolute RPS across runs. Bare and origin-direct are controls (not medal peers).

#### Block B — H2 TLS→H1

Peer ratios (÷YARP / ÷nginx) on median peak; **RPS cells** embed `(MiB / CPU%)`.

**Windows** (`windows-latest`)

| Arm | Generator | Sustain | Peak | ÷YARP | ÷nginx |
|---|---|---:|---:|---:|---:|
| nginx-reverse-http2 | dotnet-httpclient | **7,862**<br><sub>(139 MiB / 24.7% CPU)</sub> | **7,862**<br><sub>(139 MiB / 24.7% CPU)</sub> | **0.27×** | **1.00×** |
| yarp-reverse-http2 | dotnet-httpclient | **28,862**<br><sub>(94 MiB / 53.2% CPU)</sub> | **28,862**<br><sub>(94 MiB / 53.2% CPU)</sub> | **1.00×** | **3.67×** |
| twp-reverse-http2-cleartext | dotnet-httpclient | 🥇 **33,703**<br><sub>(103 MiB / 54.3% CPU)</sub> | **33,703**<br><sub>(103 MiB / 54.3% CPU)</sub> | **1.17×** | **4.29×** |

**Linux** (`ubuntu-latest`)

| Arm | Generator | Sustain | Peak | ÷YARP | ÷nginx |
|---|---|---:|---:|---:|---:|
| nginx-reverse-http2 | dotnet-httpclient | **14,871**<br><sub>(98 MiB / 19.3% CPU)</sub> | **14,871**<br><sub>(98 MiB / 19.3% CPU)</sub> | **0.51×** | **1.00×** |
| yarp-reverse-http2 | dotnet-httpclient | **29,038**<br><sub>(122 MiB / 50.0% CPU)</sub> | **29,038**<br><sub>(122 MiB / 50.0% CPU)</sub> | **1.00×** | **1.95×** |
| twp-reverse-http2-cleartext | dotnet-httpclient | 🥇 **34,081**<br><sub>(123 MiB / 52.5% CPU)</sub> | **34,081**<br><sub>(123 MiB / 52.5% CPU)</sub> | **1.17×** | **2.29×** |

#### Block C — H3→H1

Same layout as Block B. Requires QuicListener; nginx only with `http_v3_module` (Windows nginx has no QUIC).

**Windows** (`windows-latest`)

| Arm | Generator | Sustain | Peak | ÷YARP | ÷nginx |
|---|---|---:|---:|---:|---:|
| nginx-reverse-http3-cleartext | dotnet-httpclient | *Not possible (no QUIC)* | *Not possible (no QUIC)* | — | — |
| yarp-reverse-http3-cleartext | dotnet-httpclient | 🥇 **14,836**<br><sub>(158 MiB / 52.2% CPU)</sub> | **14,836**<br><sub>(158 MiB / 52.2% CPU)</sub> | **1.00×** | — |
| twp-reverse-http3-cleartext | dotnet-httpclient | **14,310**<br><sub>(104 MiB / 44.9% CPU)</sub> | **14,310**<br><sub>(104 MiB / 44.9% CPU)</sub> | **0.96×** | — |

**Linux** (`ubuntu-latest`)

| Arm | Generator | Sustain | Peak | ÷YARP | ÷nginx |
|---|---|---:|---:|---:|---:|
| nginx-reverse-http3-cleartext | dotnet-httpclient | **0**<br><sub>(104 MiB / 22.6% CPU)</sub> | **15,166**<br><sub>(104 MiB / 22.6% CPU)</sub> | **0.81×** | **1.00×** |
| yarp-reverse-http3-cleartext | dotnet-httpclient | **18,684**<br><sub>(181 MiB / 50.3% CPU)</sub> | **18,684**<br><sub>(181 MiB / 50.3% CPU)</sub> | **1.00×** | **1.23×** |
| twp-reverse-http3-cleartext | dotnet-httpclient | 🥇 **20,638**<br><sub>(143 MiB / 50.7% CPU)</sub> | **20,638**<br><sub>(143 MiB / 50.7% CPU)</sub> | **1.10×** | **1.36×** |

**How to read the tables**

- **Reverse** = bare transparent fixed-forward (no TWP plugins / interception). nginx knobs match TWP/YARP streaming (`keepalive 256`, `proxy_buffering off`). **MITM** = TWP-only table on the same Client×Origin wires: **Lite** = no-op handlers (unchanged-lite finish reuses reverse compressed relay); **Full** = mutating handlers that append up to four unique headers per direction (RPS harness adds one; product uses `MitmCompressedRelayHelper` — no probe name in library code). Remove/replace/non-unique header growth and body mutation still force full decode/re-encode. nginx/YARP cannot MITM. **HTTP/3 has no cleartext client** (QUIC always encrypted).
- **Sustainable** = last concurrency that still met error/latency SLOs. **Peak** = highest RPS in that ramp.
- 🥇 = best among **TWP / nginx / YARP** on Reverse rows (or saturation blocks): highest RPS; on an RPS tie, lower Memory (RSS) then lower CPU%. MITM is TWP-only. **Lite÷Reverse** / **Full÷Reverse** = TWP MITM lite or full sustain ÷ TWP Reverse sustain on the same Client×Origin from the same `compare-product` job.
- *Not possible* = product cannot do that path. *Not measured* = path exists but no published number yet for that OS.
- Product refresh: `compare-product` @ `024bd68d` — [34126809918](https://github.com/justcoding121/titanium-web-proxy/actions/runs/34126809918). Heavier/saturation/tls:

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

Median of **3 repeats** on `windows-latest` (4 vCPU / 16 GiB). Bare reverse 5×5 @ `024bd68d` — `compare-product` [34126809918](https://github.com/justcoding121/titanium-web-proxy/actions/runs/34126809918). Warmup 2s / measure 8s; concurrency 8, 16, 32, 64. Prefer TWP÷peer ratios over absolute RPS. **RPS cells** include median RSS / CPU at the peak-RPS step as `<br><sub>(MiB / CPU%)</sub>`. nginx terminate peers use `keepalive 256` + streaming buffers. Laptop High-perf / cool-paired numbers stay on the [local lab](Performance-Local-Lab).

**Load generators:** Reverse inbound H3 arms use **`dotnet-httpclient`** (`http_version=3.0`, `RequestVersionExact`). nginx/Windows is same-OS only (no QUIC).

| Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---|---:|---:|---:|---:|---:|---:|
| HTTP/1 · plain | HTTP/1 · plain | 🥇 **43148**<br><sub>(72 MiB / 48.4% CPU)</sub> | 🥇 **43148**<br><sub>(72 MiB / 48.4% CPU)</sub> | **27494**<br><sub>(123 MiB / 24.8% CPU)</sub> | **27494**<br><sub>(123 MiB / 24.8% CPU)</sub> | **37946**<br><sub>(88 MiB / 50.5% CPU)</sub> | **37946**<br><sub>(88 MiB / 50.5% CPU)</sub> |
| HTTP/1 · plain | HTTP/1 · TLS | 🥇 **36643**<br><sub>(90 MiB / 50.4% CPU)</sub> | 🥇 **36643**<br><sub>(90 MiB / 50.4% CPU)</sub> | **16661**<br><sub>(133 MiB / 24.7% CPU)</sub> | **16661**<br><sub>(133 MiB / 24.7% CPU)</sub> | **33622**<br><sub>(94 MiB / 48.9% CPU)</sub> | **33622**<br><sub>(94 MiB / 48.9% CPU)</sub> |
| HTTP/1 · plain | HTTP/2 · plain | 🥇 **62491**<br><sub>(116 MiB / 48.5% CPU)</sub> | 🥇 **62491**<br><sub>(116 MiB / 48.5% CPU)</sub> | *Not possible* | *Not possible* | **55620**<br><sub>(93 MiB / 52.7% CPU)</sub> | **55620**<br><sub>(93 MiB / 52.7% CPU)</sub> |
| HTTP/1 · plain | HTTP/2 · TLS | 🥇 **54245**<br><sub>(128 MiB / 44.3% CPU)</sub> | 🥇 **54245**<br><sub>(128 MiB / 44.3% CPU)</sub> | *Not possible* | *Not possible* | **49838**<br><sub>(96 MiB / 49.2% CPU)</sub> | **49838**<br><sub>(96 MiB / 49.2% CPU)</sub> |
| HTTP/1 · plain | HTTP/3 · QUIC | **29032**<br><sub>(116 MiB / 48.6% CPU)</sub> | **29032**<br><sub>(116 MiB / 48.6% CPU)</sub> | *Not possible (no QUIC)* | *Not possible (no QUIC)* | 🥇 **29598**<br><sub>(124 MiB / 50.9% CPU)</sub> | 🥇 **29598**<br><sub>(124 MiB / 50.9% CPU)</sub> |
| HTTP/1 · TLS | HTTP/1 · plain | 🥇 **36898**<br><sub>(88 MiB / 47% CPU)</sub> | 🥇 **36898**<br><sub>(88 MiB / 47% CPU)</sub> | **17311**<br><sub>(140 MiB / 24.9% CPU)</sub> | **17311**<br><sub>(140 MiB / 24.9% CPU)</sub> | **32283**<br><sub>(102 MiB / 49.6% CPU)</sub> | **32283**<br><sub>(102 MiB / 49.6% CPU)</sub> |
| HTTP/1 · TLS | HTTP/1 · TLS | 🥇 **32801**<br><sub>(85 MiB / 48.7% CPU)</sub> | 🥇 **32801**<br><sub>(85 MiB / 48.7% CPU)</sub> | **13080**<br><sub>(142 MiB / 24.9% CPU)</sub> | **13080**<br><sub>(142 MiB / 24.9% CPU)</sub> | **29170**<br><sub>(105 MiB / 50.9% CPU)</sub> | **29170**<br><sub>(105 MiB / 50.9% CPU)</sub> |
| HTTP/1 · TLS | HTTP/2 · plain | 🥇 **49296**<br><sub>(127 MiB / 45.9% CPU)</sub> | 🥇 **49296**<br><sub>(127 MiB / 45.9% CPU)</sub> | *Not possible* | *Not possible* | **45621**<br><sub>(107 MiB / 50.2% CPU)</sub> | **45621**<br><sub>(107 MiB / 50.2% CPU)</sub> |
| HTTP/1 · TLS | HTTP/2 · TLS | 🥇 **43714**<br><sub>(134 MiB / 45.7% CPU)</sub> | 🥇 **43714**<br><sub>(134 MiB / 45.7% CPU)</sub> | *Not possible* | *Not possible* | **42064**<br><sub>(113 MiB / 49.1% CPU)</sub> | **42064**<br><sub>(113 MiB / 49.1% CPU)</sub> |
| HTTP/1 · TLS | HTTP/3 · QUIC | **25085**<br><sub>(120 MiB / 50.4% CPU)</sub> | **25085**<br><sub>(120 MiB / 50.4% CPU)</sub> | *Not possible (no QUIC)* | *Not possible (no QUIC)* | 🥇 **25200**<br><sub>(138 MiB / 52% CPU)</sub> | 🥇 **25200**<br><sub>(138 MiB / 52% CPU)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | 🥇 **55581**<br><sub>(97 MiB / 52.1% CPU)</sub> | 🥇 **55581**<br><sub>(97 MiB / 52.1% CPU)</sub> | *Not possible* | *Not possible* | **53465**<br><sub>(87 MiB / 50.3% CPU)</sub> | **53465**<br><sub>(87 MiB / 50.3% CPU)</sub> |
| HTTP/2 · plain | HTTP/1 · TLS | 🥇 **46471**<br><sub>(104 MiB / 51% CPU)</sub> | 🥇 **46471**<br><sub>(104 MiB / 51% CPU)</sub> | *Not possible* | *Not possible* | **44832**<br><sub>(97 MiB / 50.6% CPU)</sub> | **44832**<br><sub>(97 MiB / 50.6% CPU)</sub> |
| HTTP/2 · plain | HTTP/2 · plain | 🥇 **150625**<br><sub>(57 MiB / 29.1% CPU)</sub> | 🥇 **150625**<br><sub>(57 MiB / 29.1% CPU)</sub> | *Not possible* | *Not possible* | **88874**<br><sub>(93 MiB / 50.2% CPU)</sub> | **88874**<br><sub>(93 MiB / 50.2% CPU)</sub> |
| HTTP/2 · plain | HTTP/2 · TLS | 🥇 **122710**<br><sub>(62 MiB / 27.4% CPU)</sub> | 🥇 **122710**<br><sub>(62 MiB / 27.4% CPU)</sub> | *Not possible* | *Not possible* | **77808**<br><sub>(101 MiB / 48.3% CPU)</sub> | **77808**<br><sub>(101 MiB / 48.3% CPU)</sub> |
| HTTP/2 · plain | HTTP/3 · QUIC | 🥇 **42885**<br><sub>(141 MiB / 53.2% CPU)</sub> | 🥇 **42885**<br><sub>(141 MiB / 53.2% CPU)</sub> | *Not possible (no QUIC)* | *Not possible (no QUIC)* | **41031**<br><sub>(135 MiB / 51.7% CPU)</sub> | **41031**<br><sub>(135 MiB / 51.7% CPU)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **54084**<br><sub>(108 MiB / 51.7% CPU)</sub> | 🥇 **54084**<br><sub>(108 MiB / 51.7% CPU)</sub> | **15726**<br><sub>(139 MiB / 24.3% CPU)</sub> | **15726**<br><sub>(139 MiB / 24.3% CPU)</sub> | **47838**<br><sub>(97 MiB / 51.2% CPU)</sub> | **47838**<br><sub>(97 MiB / 51.2% CPU)</sub> |
| HTTP/2 · TLS | HTTP/1 · TLS | 🥇 **47790**<br><sub>(103 MiB / 51.4% CPU)</sub> | 🥇 **47790**<br><sub>(103 MiB / 51.4% CPU)</sub> | **11737**<br><sub>(143 MiB / 24.4% CPU)</sub> | **11737**<br><sub>(143 MiB / 24.4% CPU)</sub> | **41198**<br><sub>(95 MiB / 51.8% CPU)</sub> | **41198**<br><sub>(95 MiB / 51.8% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | 🥇 **140904**<br><sub>(74 MiB / 31.1% CPU)</sub> | 🥇 **140904**<br><sub>(74 MiB / 31.1% CPU)</sub> | *Not possible* | *Not possible* | **77150**<br><sub>(101 MiB / 53% CPU)</sub> | **77150**<br><sub>(101 MiB / 53% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **116819**<br><sub>(75 MiB / 30.3% CPU)</sub> | 🥇 **116819**<br><sub>(75 MiB / 30.3% CPU)</sub> | *Not possible* | *Not possible* | **70121**<br><sub>(98 MiB / 48.3% CPU)</sub> | **70121**<br><sub>(98 MiB / 48.3% CPU)</sub> |
| HTTP/2 · TLS | HTTP/3 · QUIC | 🥇 **41432**<br><sub>(140 MiB / 50.9% CPU)</sub> | 🥇 **41432**<br><sub>(140 MiB / 50.9% CPU)</sub> | *Not possible (no QUIC)* | *Not possible (no QUIC)* | **37958**<br><sub>(134 MiB / 51.3% CPU)</sub> | **37958**<br><sub>(134 MiB / 51.3% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **26401**<br><sub>(116 MiB / 44.6% CPU)</sub> | 🥇 **26401**<br><sub>(116 MiB / 44.6% CPU)</sub> | *Not possible (no QUIC)* | *Not possible (no QUIC)* | **25984**<br><sub>(149 MiB / 48.5% CPU)</sub> | **25984**<br><sub>(149 MiB / 48.5% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **23473**<br><sub>(126 MiB / 43.8% CPU)</sub> | 🥇 **23473**<br><sub>(126 MiB / 43.8% CPU)</sub> | *Not possible (no QUIC)* | *Not possible (no QUIC)* | **21408**<br><sub>(155 MiB / 49.8% CPU)</sub> | **21408**<br><sub>(155 MiB / 49.8% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/2 · plain | 🥇 **46358**<br><sub>(147 MiB / 47.8% CPU)</sub> | 🥇 **46358**<br><sub>(147 MiB / 47.8% CPU)</sub> | *Not possible (no H3 to H2)* | *Not possible (no H3 to H2)* | **34679**<br><sub>(164 MiB / 49.7% CPU)</sub> | **34679**<br><sub>(164 MiB / 49.7% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | 🥇 **42019**<br><sub>(161 MiB / 42.9% CPU)</sub> | 🥇 **42019**<br><sub>(161 MiB / 42.9% CPU)</sub> | *Not possible (no H3 to H2)* | *Not possible (no H3 to H2)* | **35763**<br><sub>(163 MiB / 47.9% CPU)</sub> | **35763**<br><sub>(163 MiB / 47.9% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/3 · QUIC | 🥇 **22676**<br><sub>(128 MiB / 45.5% CPU)</sub> | 🥇 **22676**<br><sub>(128 MiB / 45.5% CPU)</sub> | *Not possible (no QUIC)* | *Not possible (no QUIC)* | **18594**<br><sub>(181 MiB / 51.2% CPU)</sub> | **18594**<br><sub>(181 MiB / 51.2% CPU)</sub> |

### MITM (TWP only)

Same Client×Origin wires with interception on (`compare-product` [34126809918](https://github.com/justcoding121/titanium-web-proxy/actions/runs/34126809918)). **Lite** = no-op handlers (unchanged-lite finish). **Full** = append-only header mutation (harness: one probe header each way; product: generic append-only relay via `MitmCompressedRelayHelper`). nginx/YARP cannot MITM. **Lite÷Reverse** / **Full÷Reverse** vs bare reverse (same job). Completion gate: Lite and Full ≥ **0.80×** reverse sustain @ c=64 (median of 3 GHA runs); reverse TWP÷YARP ≥ **0.95×** (no nginx gate).

**v1 append-only relay (2026-08-27):** Pre-fix H2→H2 Full÷Reverse was **0.13–0.16×** ([32960766249](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32960766249)). Post-fix @ `df172718`: H2 plain→H2 plain Full **0.77–0.79×**, H3→H1 Full **0.91–0.93×**, all MITM arms ≥ **0.70×** on median of [33041445371](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33041445371), [33055267086](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33055267086), [33055272140](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33055272140).

**v2 drop-only + non-unique append (2026-08-27):** `MitmStaticRebuildHelper` rebuilds static HPACK/QPACK after 1–4 unique header drops; trailing non-unique appends stay on compressed relay. @ `024bd68d`: all MITM arms ≥ **0.70×** on GHA median ([33087088466](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087088466), [33087091622](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087091622), [33105885748](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33105885748) Linux; [33087085235](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087085235), [33087088466](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087088466), [33087091622](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087091622) Windows). H2 plain→H2 plain Full **0.77–0.79×** (Win) / **0.78×** (Lin).

| Client | Origin | Lite sustain | Full sustain | Lite÷Reverse | Full÷Reverse |
|---|---|---:|---:|---:|---:|
| HTTP/1 · plain | HTTP/1 · plain | **42416**<br><sub>(78 MiB / 49.6% CPU)</sub> | **41642**<br><sub>(82 MiB / 50.7% CPU)</sub> | **0.98×** | **0.97×** |
| HTTP/1 · plain | HTTP/1 · TLS | **35496**<br><sub>(93 MiB / 50.8% CPU)</sub> | **35017**<br><sub>(94 MiB / 49.4% CPU)</sub> | **0.97×** | **0.96×** |
| HTTP/1 · plain | HTTP/2 · plain | **60830**<br><sub>(118 MiB / 50.1% CPU)</sub> | **59596**<br><sub>(121 MiB / 49.7% CPU)</sub> | **0.97×** | **0.95×** |
| HTTP/1 · plain | HTTP/2 · TLS | **53754**<br><sub>(124 MiB / 47% CPU)</sub> | **52767**<br><sub>(135 MiB / 48.7% CPU)</sub> | **0.99×** | **0.97×** |
| HTTP/1 · plain | HTTP/3 · QUIC | **28558**<br><sub>(112 MiB / 51.6% CPU)</sub> | **28107**<br><sub>(114 MiB / 51% CPU)</sub> | **0.98×** | **0.97×** |
| HTTP/1 · TLS | HTTP/1 · plain | **35649**<br><sub>(93 MiB / 49.9% CPU)</sub> | **35247**<br><sub>(94 MiB / 50% CPU)</sub> | **0.97×** | **0.96×** |
| HTTP/1 · TLS | HTTP/1 · TLS | **32815**<br><sub>(94 MiB / 48.2% CPU)</sub> | **31979**<br><sub>(93 MiB / 49.9% CPU)</sub> | **1×** | **0.97×** |
| HTTP/1 · TLS | HTTP/2 · plain | **48342**<br><sub>(118 MiB / 47.9% CPU)</sub> | **47289**<br><sub>(123 MiB / 50.1% CPU)</sub> | **0.98×** | **0.96×** |
| HTTP/1 · TLS | HTTP/2 · TLS | **43891**<br><sub>(134 MiB / 46.9% CPU)</sub> | **43067**<br><sub>(125 MiB / 45.9% CPU)</sub> | **1×** | **0.99×** |
| HTTP/1 · TLS | HTTP/3 · QUIC | **25139**<br><sub>(116 MiB / 51.7% CPU)</sub> | **24602**<br><sub>(116 MiB / 50% CPU)</sub> | **1×** | **0.98×** |
| HTTP/2 · plain | HTTP/1 · plain | **55126**<br><sub>(101 MiB / 51.5% CPU)</sub> | **53456**<br><sub>(97 MiB / 53.2% CPU)</sub> | **0.99×** | **0.96×** |
| HTTP/2 · plain | HTTP/1 · TLS | **47504**<br><sub>(103 MiB / 54.9% CPU)</sub> | **46365**<br><sub>(103 MiB / 53.5% CPU)</sub> | **1.02×** | **1×** |
| HTTP/2 · plain | HTTP/2 · plain | **99759**<br><sub>(70 MiB / 49.8% CPU)</sub> | **93161**<br><sub>(74 MiB / 48.8% CPU)</sub> | **0.66×** | **0.62×** |
| HTTP/2 · plain | HTTP/2 · TLS | **87259**<br><sub>(79 MiB / 45.4% CPU)</sub> | **82403**<br><sub>(77 MiB / 46.7% CPU)</sub> | **0.71×** | **0.67×** |
| HTTP/2 · plain | HTTP/3 · QUIC | **42663**<br><sub>(137 MiB / 52.5% CPU)</sub> | **41785**<br><sub>(134 MiB / 54.6% CPU)</sub> | **0.99×** | **0.97×** |
| HTTP/2 · TLS | HTTP/1 · plain | **53275**<br><sub>(108 MiB / 53.8% CPU)</sub> | **51033**<br><sub>(112 MiB / 53% CPU)</sub> | **0.99×** | **0.94×** |
| HTTP/2 · TLS | HTTP/1 · TLS | **46834**<br><sub>(106 MiB / 54% CPU)</sub> | **44869**<br><sub>(111 MiB / 51.2% CPU)</sub> | **0.98×** | **0.94×** |
| HTTP/2 · TLS | HTTP/2 · plain | **95259**<br><sub>(97 MiB / 49.4% CPU)</sub> | **89191**<br><sub>(89 MiB / 46.6% CPU)</sub> | **0.68×** | **0.63×** |
| HTTP/2 · TLS | HTTP/2 · TLS | **84908**<br><sub>(85 MiB / 44.9% CPU)</sub> | **79447**<br><sub>(95 MiB / 45.9% CPU)</sub> | **0.73×** | **0.68×** |
| HTTP/2 · TLS | HTTP/3 · QUIC | **41573**<br><sub>(144 MiB / 52.7% CPU)</sub> | **40421**<br><sub>(146 MiB / 52.7% CPU)</sub> | **1×** | **0.98×** |
| HTTP/3 · QUIC | HTTP/1 · plain | **24842**<br><sub>(114 MiB / 45.9% CPU)</sub> | **23923**<br><sub>(124 MiB / 46.3% CPU)</sub> | **0.94×** | **0.91×** |
| HTTP/3 · QUIC | HTTP/1 · TLS | **22186**<br><sub>(131 MiB / 45.5% CPU)</sub> | **21112**<br><sub>(117 MiB / 45.2% CPU)</sub> | **0.95×** | **0.9×** |
| HTTP/3 · QUIC | HTTP/2 · plain | **42900**<br><sub>(157 MiB / 51.1% CPU)</sub> | **41039**<br><sub>(146 MiB / 50.2% CPU)</sub> | **0.93×** | **0.89×** |
| HTTP/3 · QUIC | HTTP/2 · TLS | **40022**<br><sub>(158 MiB / 47.9% CPU)</sub> | **39065**<br><sub>(157 MiB / 48.6% CPU)</sub> | **0.95×** | **0.93×** |
| HTTP/3 · QUIC | HTTP/3 · QUIC | **20576**<br><sub>(129 MiB / 48.4% CPU)</sub> | **20055**<br><sub>(129 MiB / 50.6% CPU)</sub> | **0.91×** | **0.88×** |

## Linux — Titanium vs nginx vs YARP

### Reverse

Median of **3 repeats** on `ubuntu-latest` (4 vCPU / 16 GiB). Bare reverse 5×5 @ `024bd68d` — `compare-product` [34126809918](https://github.com/justcoding121/titanium-web-proxy/actions/runs/34126809918). Warmup 2s / measure 8s; concurrency 8, 16, 32, 64. **Linux nginx is the authoritative nginx baseline.** nginx terminate peers use `keepalive 256` + streaming buffers. The RPS workflow installs nginx.org mainline (`http_v3_module`) and `libmsquic`. Prefer ratios over absolute RPS.

| Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---|---:|---:|---:|---:|---:|---:|
| HTTP/1 · plain | HTTP/1 · plain | **45442**<br><sub>(82 MiB / 50% CPU)</sub> | **45442**<br><sub>(82 MiB / 50% CPU)</sub> | 🥇 **54602**<br><sub>(73 MiB / 39.5% CPU)</sub> | 🥇 **54602**<br><sub>(73 MiB / 39.5% CPU)</sub> | **40514**<br><sub>(117 MiB / 49.4% CPU)</sub> | **40514**<br><sub>(117 MiB / 49.4% CPU)</sub> |
| HTTP/1 · plain | HTTP/1 · TLS | **36887**<br><sub>(106 MiB / 49.2% CPU)</sub> | **36887**<br><sub>(106 MiB / 49.2% CPU)</sub> | 🥇 **43002**<br><sub>(90 MiB / 41.1% CPU)</sub> | 🥇 **43002**<br><sub>(90 MiB / 41.1% CPU)</sub> | **32675**<br><sub>(137 MiB / 49% CPU)</sub> | **32675**<br><sub>(137 MiB / 49% CPU)</sub> |
| HTTP/1 · plain | HTTP/2 · plain | 🥇 **58774**<br><sub>(138 MiB / 50.8% CPU)</sub> | 🥇 **58774**<br><sub>(138 MiB / 50.8% CPU)</sub> | *Not possible* | *Not possible* | **52131**<br><sub>(123 MiB / 48.3% CPU)</sub> | **52131**<br><sub>(123 MiB / 48.3% CPU)</sub> |
| HTTP/1 · plain | HTTP/2 · TLS | 🥇 **45682**<br><sub>(155 MiB / 49.4% CPU)</sub> | 🥇 **45682**<br><sub>(155 MiB / 49.4% CPU)</sub> | *Not possible* | *Not possible* | **43753**<br><sub>(127 MiB / 47.3% CPU)</sub> | **43753**<br><sub>(127 MiB / 47.3% CPU)</sub> |
| HTTP/1 · plain | HTTP/3 · QUIC | 🥇 **31312**<br><sub>(144 MiB / 54.6% CPU)</sub> | 🥇 **31312**<br><sub>(144 MiB / 54.6% CPU)</sub> | *Not possible (no QUIC)* | *Not possible (no QUIC)* | **29231**<br><sub>(156 MiB / 48.4% CPU)</sub> | **29231**<br><sub>(156 MiB / 48.4% CPU)</sub> |
| HTTP/1 · TLS | HTTP/1 · plain | **34763**<br><sub>(109 MiB / 49.9% CPU)</sub> | **34763**<br><sub>(109 MiB / 49.9% CPU)</sub> | 🥇 **42628**<br><sub>(100 MiB / 41.1% CPU)</sub> | 🥇 **42628**<br><sub>(100 MiB / 41.1% CPU)</sub> | **31712**<br><sub>(132 MiB / 49.9% CPU)</sub> | **31712**<br><sub>(132 MiB / 49.9% CPU)</sub> |
| HTTP/1 · TLS | HTTP/1 · TLS | **30596**<br><sub>(107 MiB / 47.7% CPU)</sub> | **30596**<br><sub>(107 MiB / 47.7% CPU)</sub> | 🥇 **35876**<br><sub>(104 MiB / 40.9% CPU)</sub> | 🥇 **35876**<br><sub>(104 MiB / 40.9% CPU)</sub> | **27094**<br><sub>(137 MiB / 49.5% CPU)</sub> | **27094**<br><sub>(137 MiB / 49.5% CPU)</sub> |
| HTTP/1 · TLS | HTTP/2 · plain | 🥇 **43784**<br><sub>(145 MiB / 50.1% CPU)</sub> | 🥇 **43784**<br><sub>(145 MiB / 50.1% CPU)</sub> | *Not possible* | *Not possible* | **39595**<br><sub>(140 MiB / 49.2% CPU)</sub> | **39595**<br><sub>(140 MiB / 49.2% CPU)</sub> |
| HTTP/1 · TLS | HTTP/2 · TLS | 🥇 **38412**<br><sub>(155 MiB / 48.6% CPU)</sub> | 🥇 **38412**<br><sub>(155 MiB / 48.6% CPU)</sub> | *Not possible* | *Not possible* | **35014**<br><sub>(142 MiB / 48.2% CPU)</sub> | **35014**<br><sub>(142 MiB / 48.2% CPU)</sub> |
| HTTP/1 · TLS | HTTP/3 · QUIC | 🥇 **27078**<br><sub>(154 MiB / 53.3% CPU)</sub> | 🥇 **27078**<br><sub>(154 MiB / 53.3% CPU)</sub> | *Not possible (no QUIC)* | *Not possible (no QUIC)* | **24650**<br><sub>(167 MiB / 49.1% CPU)</sub> | **24650**<br><sub>(167 MiB / 49.1% CPU)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | 🥇 **50993**<br><sub>(117 MiB / 51.6% CPU)</sub> | 🥇 **50993**<br><sub>(117 MiB / 51.6% CPU)</sub> | *Not possible* | *Not possible* | **49389**<br><sub>(114 MiB / 47.9% CPU)</sub> | **49389**<br><sub>(114 MiB / 47.9% CPU)</sub> |
| HTTP/2 · plain | HTTP/1 · TLS | 🥇 **40178**<br><sub>(129 MiB / 50.5% CPU)</sub> | 🥇 **40178**<br><sub>(129 MiB / 50.5% CPU)</sub> | *Not possible* | *Not possible* | **38512**<br><sub>(124 MiB / 48.8% CPU)</sub> | **38512**<br><sub>(124 MiB / 48.8% CPU)</sub> |
| HTTP/2 · plain | HTTP/2 · plain | 🥇 **112467**<br><sub>(91 MiB / 38.3% CPU)</sub> | 🥇 **112467**<br><sub>(91 MiB / 38.3% CPU)</sub> | *Not possible* | *Not possible* | **68404**<br><sub>(124 MiB / 46.3% CPU)</sub> | **68404**<br><sub>(124 MiB / 46.3% CPU)</sub> |
| HTTP/2 · plain | HTTP/2 · TLS | 🥇 **86598**<br><sub>(91 MiB / 37% CPU)</sub> | 🥇 **86598**<br><sub>(91 MiB / 37% CPU)</sub> | *Not possible* | *Not possible* | **55654**<br><sub>(126 MiB / 44.9% CPU)</sub> | **55654**<br><sub>(126 MiB / 44.9% CPU)</sub> |
| HTTP/2 · plain | HTTP/3 · QUIC | **34438**<br><sub>(156 MiB / 50.5% CPU)</sub> | **34438**<br><sub>(156 MiB / 50.5% CPU)</sub> | *Not possible (no QUIC)* | *Not possible (no QUIC)* | 🥇 **34482**<br><sub>(157 MiB / 45.7% CPU)</sub> | 🥇 **34482**<br><sub>(157 MiB / 45.7% CPU)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **47100**<br><sub>(121 MiB / 51.4% CPU)</sub> | 🥇 **47100**<br><sub>(121 MiB / 51.4% CPU)</sub> | **21439**<br><sub>(99 MiB / 18.8% CPU)</sub> | **21439**<br><sub>(99 MiB / 18.8% CPU)</sub> | **40159**<br><sub>(122 MiB / 48.2% CPU)</sub> | **40159**<br><sub>(122 MiB / 48.2% CPU)</sub> |
| HTTP/2 · TLS | HTTP/1 · TLS | 🥇 **38515**<br><sub>(120 MiB / 49.1% CPU)</sub> | 🥇 **38515**<br><sub>(120 MiB / 49.1% CPU)</sub> | **18594**<br><sub>(109 MiB / 19.3% CPU)</sub> | **18594**<br><sub>(109 MiB / 19.3% CPU)</sub> | **34597**<br><sub>(129 MiB / 48.5% CPU)</sub> | **34597**<br><sub>(129 MiB / 48.5% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | 🥇 **103058**<br><sub>(99 MiB / 38.9% CPU)</sub> | 🥇 **103058**<br><sub>(99 MiB / 38.9% CPU)</sub> | *Not possible* | *Not possible* | **54026**<br><sub>(123 MiB / 46.3% CPU)</sub> | **54026**<br><sub>(123 MiB / 46.3% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **77878**<br><sub>(99 MiB / 35.6% CPU)</sub> | 🥇 **77878**<br><sub>(99 MiB / 35.6% CPU)</sub> | *Not possible* | *Not possible* | **48036**<br><sub>(131 MiB / 45% CPU)</sub> | **48036**<br><sub>(131 MiB / 45% CPU)</sub> |
| HTTP/2 · TLS | HTTP/3 · QUIC | 🥇 **32383**<br><sub>(166 MiB / 49.6% CPU)</sub> | 🥇 **32383**<br><sub>(166 MiB / 49.6% CPU)</sub> | *Not possible (no QUIC)* | *Not possible (no QUIC)* | **30252**<br><sub>(163 MiB / 45.2% CPU)</sub> | **30252**<br><sub>(163 MiB / 45.2% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **27304**<br><sub>(150 MiB / 51.3% CPU)</sub> | 🥇 **27304**<br><sub>(150 MiB / 51.3% CPU)</sub> | **0**<br><sub>(108 MiB / 21.3% CPU)</sub> | **22735**<br><sub>(108 MiB / 21.3% CPU)</sub> | **25272**<br><sub>(191 MiB / 49.3% CPU)</sub> | **25272**<br><sub>(191 MiB / 49.3% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **21505**<br><sub>(165 MiB / 48.8% CPU)</sub> | 🥇 **21505**<br><sub>(165 MiB / 48.8% CPU)</sub> | **0**<br><sub>(119 MiB / 22.4% CPU)</sub> | **17246**<br><sub>(119 MiB / 22.4% CPU)</sub> | **21129**<br><sub>(197 MiB / 49.1% CPU)</sub> | **21129**<br><sub>(197 MiB / 49.1% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/2 · plain | 🥇 **36800**<br><sub>(166 MiB / 54.4% CPU)</sub> | 🥇 **36800**<br><sub>(166 MiB / 54.4% CPU)</sub> | *Not possible (no H3 to H2)* | *Not possible (no H3 to H2)* | **32383**<br><sub>(198 MiB / 47.8% CPU)</sub> | **32383**<br><sub>(198 MiB / 47.8% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | 🥇 **33722**<br><sub>(166 MiB / 51.6% CPU)</sub> | 🥇 **33722**<br><sub>(166 MiB / 51.6% CPU)</sub> | *Not possible (no H3 to H2)* | *Not possible (no H3 to H2)* | **30263**<br><sub>(203 MiB / 46.8% CPU)</sub> | **30263**<br><sub>(203 MiB / 46.8% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/3 · QUIC | 🥇 **25361**<br><sub>(165 MiB / 44.6% CPU)</sub> | 🥇 **25361**<br><sub>(165 MiB / 44.6% CPU)</sub> | *Not possible (no QUIC)* | *Not possible (no QUIC)* | **21111**<br><sub>(209 MiB / 45.8% CPU)</sub> | **21111**<br><sub>(209 MiB / 45.8% CPU)</sub> |

### MITM (TWP only)

Same Client×Origin wires with interception on (`compare-product` [34126809918](https://github.com/justcoding121/titanium-web-proxy/actions/runs/34126809918)). **Lite** = no-op handlers (unchanged-lite finish). **Full** = append-only header mutation (harness: one probe header each way; product: generic append-only relay via `MitmCompressedRelayHelper`). nginx/YARP cannot MITM. **Lite÷Reverse** / **Full÷Reverse** vs bare reverse (same job). Completion gate: Lite and Full ≥ **0.80×** reverse sustain @ c=64 (median of 3 GHA runs); reverse TWP÷YARP ≥ **0.95×** (no nginx gate).

**v1 append-only relay (2026-08-27):** Pre-fix H2→H2 Full÷Reverse was **0.13–0.16×** ([32960766249](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32960766249)). Post-fix @ `df172718`: H2 plain→H2 plain Full **0.77–0.79×**, H3→H1 Full **0.91–0.93×**, all MITM arms ≥ **0.70×** on median of [33041445371](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33041445371), [33055267086](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33055267086), [33055272140](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33055272140).

**v2 drop-only + non-unique append (2026-08-27):** `MitmStaticRebuildHelper` rebuilds static HPACK/QPACK after 1–4 unique header drops; trailing non-unique appends stay on compressed relay. @ `024bd68d`: all MITM arms ≥ **0.70×** on GHA median ([33087088466](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087088466), [33087091622](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087091622), [33105885748](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33105885748) Linux; [33087085235](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087085235), [33087088466](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087088466), [33087091622](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087091622) Windows). H2 plain→H2 plain Full **0.77–0.79×** (Win) / **0.78×** (Lin).

| Client | Origin | Lite sustain | Full sustain | Lite÷Reverse | Full÷Reverse |
|---|---|---:|---:|---:|---:|
| HTTP/1 · plain | HTTP/1 · plain | **44176**<br><sub>(94 MiB / 50.8% CPU)</sub> | **39967**<br><sub>(95 MiB / 50% CPU)</sub> | **0.97×** | **0.88×** |
| HTTP/1 · plain | HTTP/1 · TLS | **33446**<br><sub>(109 MiB / 50.5% CPU)</sub> | **33296**<br><sub>(111 MiB / 49.5% CPU)</sub> | **0.91×** | **0.9×** |
| HTTP/1 · plain | HTTP/2 · plain | **51914**<br><sub>(136 MiB / 53.8% CPU)</sub> | **50316**<br><sub>(126 MiB / 53.7% CPU)</sub> | **0.88×** | **0.86×** |
| HTTP/1 · plain | HTTP/2 · TLS | **45010**<br><sub>(147 MiB / 50.3% CPU)</sub> | **41890**<br><sub>(143 MiB / 50.5% CPU)</sub> | **0.99×** | **0.92×** |
| HTTP/1 · plain | HTTP/3 · QUIC | **29698**<br><sub>(144 MiB / 55.2% CPU)</sub> | **29205**<br><sub>(154 MiB / 53.7% CPU)</sub> | **0.95×** | **0.93×** |
| HTTP/1 · TLS | HTTP/1 · plain | **32973**<br><sub>(115 MiB / 50.4% CPU)</sub> | **30304**<br><sub>(115 MiB / 50.2% CPU)</sub> | **0.95×** | **0.87×** |
| HTTP/1 · TLS | HTTP/1 · TLS | **28337**<br><sub>(113 MiB / 49% CPU)</sub> | **28230**<br><sub>(112 MiB / 48.3% CPU)</sub> | **0.93×** | **0.92×** |
| HTTP/1 · TLS | HTTP/2 · plain | **40580**<br><sub>(144 MiB / 51.3% CPU)</sub> | **38701**<br><sub>(142 MiB / 51.1% CPU)</sub> | **0.93×** | **0.88×** |
| HTTP/1 · TLS | HTTP/2 · TLS | **35623**<br><sub>(165 MiB / 50% CPU)</sub> | **34976**<br><sub>(157 MiB / 49.8% CPU)</sub> | **0.93×** | **0.91×** |
| HTTP/1 · TLS | HTTP/3 · QUIC | **24740**<br><sub>(158 MiB / 53.1% CPU)</sub> | **25654**<br><sub>(162 MiB / 53.3% CPU)</sub> | **0.91×** | **0.95×** |
| HTTP/2 · plain | HTTP/1 · plain | **44972**<br><sub>(117 MiB / 53.5% CPU)</sub> | **45432**<br><sub>(124 MiB / 53.5% CPU)</sub> | **0.88×** | **0.89×** |
| HTTP/2 · plain | HTTP/1 · TLS | **38000**<br><sub>(130 MiB / 51.6% CPU)</sub> | **34677**<br><sub>(126 MiB / 51.2% CPU)</sub> | **0.95×** | **0.86×** |
| HTTP/2 · plain | HTTP/2 · plain | **70311**<br><sub>(114 MiB / 52.2% CPU)</sub> | **62514**<br><sub>(110 MiB / 50.1% CPU)</sub> | **0.63×** | **0.56×** |
| HTTP/2 · plain | HTTP/2 · TLS | **56973**<br><sub>(106 MiB / 47.7% CPU)</sub> | **53478**<br><sub>(106 MiB / 47.1% CPU)</sub> | **0.66×** | **0.62×** |
| HTTP/2 · plain | HTTP/3 · QUIC | **32982**<br><sub>(160 MiB / 50.7% CPU)</sub> | **31395**<br><sub>(150 MiB / 50.6% CPU)</sub> | **0.96×** | **0.91×** |
| HTTP/2 · TLS | HTTP/1 · plain | **42980**<br><sub>(120 MiB / 52.6% CPU)</sub> | **40591**<br><sub>(123 MiB / 52.8% CPU)</sub> | **0.91×** | **0.86×** |
| HTTP/2 · TLS | HTTP/1 · TLS | **37190**<br><sub>(131 MiB / 51.1% CPU)</sub> | **33561**<br><sub>(133 MiB / 50.4% CPU)</sub> | **0.97×** | **0.87×** |
| HTTP/2 · TLS | HTTP/2 · plain | **65213**<br><sub>(122 MiB / 49.8% CPU)</sub> | **59028**<br><sub>(122 MiB / 48.6% CPU)</sub> | **0.63×** | **0.57×** |
| HTTP/2 · TLS | HTTP/2 · TLS | **57395**<br><sub>(114 MiB / 45.9% CPU)</sub> | **52587**<br><sub>(114 MiB / 45.6% CPU)</sub> | **0.74×** | **0.68×** |
| HTTP/2 · TLS | HTTP/3 · QUIC | **32026**<br><sub>(166 MiB / 50.4% CPU)</sub> | **30999**<br><sub>(167 MiB / 51% CPU)</sub> | **0.99×** | **0.96×** |
| HTTP/3 · QUIC | HTTP/1 · plain | **27096**<br><sub>(153 MiB / 52.9% CPU)</sub> | **25506**<br><sub>(160 MiB / 52.9% CPU)</sub> | **0.99×** | **0.93×** |
| HTTP/3 · QUIC | HTTP/1 · TLS | **20462**<br><sub>(165 MiB / 49.3% CPU)</sub> | **22732**<br><sub>(161 MiB / 49.6% CPU)</sub> | **0.95×** | **1.06×** |
| HTTP/3 · QUIC | HTTP/2 · plain | **31264**<br><sub>(168 MiB / 55.8% CPU)</sub> | **34476**<br><sub>(173 MiB / 55.9% CPU)</sub> | **0.85×** | **0.94×** |
| HTTP/3 · QUIC | HTTP/2 · TLS | **27463**<br><sub>(166 MiB / 52.5% CPU)</sub> | **30491**<br><sub>(168 MiB / 52.8% CPU)</sub> | **0.81×** | **0.9×** |
| HTTP/3 · QUIC | HTTP/3 · QUIC | **20207**<br><sub>(161 MiB / 50.1% CPU)</sub> | **23463**<br><sub>(166 MiB / 50.6% CPU)</sub> | **0.8×** | **0.93×** |

## macOS — Titanium vs nginx vs YARP

Numbers are filled by `tools/RpsLoadProbe/apply-wiki-paste.ps1` after `compare-product` on `macos-15-intel` (placeholder headers match the Linux table shape).

### Reverse

Median of **3 repeats** on `macos-15-intel` (4-core / 14 GB). Bare reverse 5×5 @ `024bd68d` — `compare-product` [34126809918](https://github.com/justcoding121/titanium-web-proxy/actions/runs/34126809918). Warmup 2s / measure 8s; concurrency 8, 16, 32, 64. Prefer TWP÷peer ratios over absolute RPS. **RPS cells** include median RSS / CPU at the peak-RPS step as `<br><sub>(MiB / CPU%)</sub>`. The RPS workflow installs Homebrew nginx (`http_v3_module`), Homebrew `libmsquic` (+ `DYLD_*`), and YARP. Do not publish from `macos-latest` (3-core / 7 GB).

| Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---|---:|---:|---:|---:|---:|---:|
| HTTP/1 · plain | HTTP/1 · plain | 🥇 **15024**<br><sub>(84 MiB / 34.6% CPU)</sub> | 🥇 **15024**<br><sub>(84 MiB / 34.6% CPU)</sub> | **7464**<br><sub>(51 MiB / 13.8% CPU)</sub> | **7464**<br><sub>(51 MiB / 13.8% CPU)</sub> | **11453**<br><sub>(105 MiB / 35.2% CPU)</sub> | **11453**<br><sub>(105 MiB / 35.2% CPU)</sub> |
| HTTP/1 · plain | HTTP/1 · TLS | 🥇 **11517**<br><sub>(94 MiB / 34.2% CPU)</sub> | 🥇 **11517**<br><sub>(94 MiB / 34.2% CPU)</sub> | **6366**<br><sub>(73 MiB / 20.3% CPU)</sub> | **6366**<br><sub>(73 MiB / 20.3% CPU)</sub> | **11162**<br><sub>(122 MiB / 38.4% CPU)</sub> | **11162**<br><sub>(122 MiB / 38.4% CPU)</sub> |
| HTTP/1 · plain | HTTP/2 · plain | **17744**<br><sub>(89 MiB / 36.4% CPU)</sub> | **17744**<br><sub>(89 MiB / 36.4% CPU)</sub> | *Not possible* | *Not possible* | 🥇 **22656**<br><sub>(111 MiB / 33.3% CPU)</sub> | 🥇 **22656**<br><sub>(111 MiB / 33.3% CPU)</sub> |
| HTTP/1 · plain | HTTP/2 · TLS | **14182**<br><sub>(105 MiB / 36.8% CPU)</sub> | **14182**<br><sub>(105 MiB / 36.8% CPU)</sub> | *Not possible* | *Not possible* | 🥇 **18762**<br><sub>(112 MiB / 34.9% CPU)</sub> | 🥇 **18762**<br><sub>(112 MiB / 34.9% CPU)</sub> |
| HTTP/1 · plain | HTTP/3 · QUIC | **5226**<br><sub>(91 MiB / 42.5% CPU)</sub> | **5226**<br><sub>(91 MiB / 42.5% CPU)</sub> | *Not possible (no QUIC)* | *Not possible (no QUIC)* | 🥇 **5914**<br><sub>(114 MiB / 35.4% CPU)</sub> | 🥇 **5914**<br><sub>(114 MiB / 35.4% CPU)</sub> |
| HTTP/1 · TLS | HTTP/1 · plain | 🥇 **10608**<br><sub>(95 MiB / 33.2% CPU)</sub> | 🥇 **10608**<br><sub>(95 MiB / 33.2% CPU)</sub> | **6819**<br><sub>(73 MiB / 13.4% CPU)</sub> | **6819**<br><sub>(73 MiB / 13.4% CPU)</sub> | **9148**<br><sub>(118 MiB / 34.2% CPU)</sub> | **9148**<br><sub>(118 MiB / 34.2% CPU)</sub> |
| HTTP/1 · TLS | HTTP/1 · TLS | 🥇 **8936**<br><sub>(98 MiB / 34.5% CPU)</sub> | 🥇 **8936**<br><sub>(98 MiB / 34.5% CPU)</sub> | **6814**<br><sub>(81 MiB / 19.5% CPU)</sub> | **6814**<br><sub>(81 MiB / 19.5% CPU)</sub> | **8755**<br><sub>(130 MiB / 35% CPU)</sub> | **8755**<br><sub>(130 MiB / 35% CPU)</sub> |
| HTTP/1 · TLS | HTTP/2 · plain | **14872**<br><sub>(97 MiB / 35.8% CPU)</sub> | **14872**<br><sub>(97 MiB / 35.8% CPU)</sub> | *Not possible* | *Not possible* | 🥇 **16912**<br><sub>(116 MiB / 32.6% CPU)</sub> | 🥇 **16912**<br><sub>(116 MiB / 32.6% CPU)</sub> |
| HTTP/1 · TLS | HTTP/2 · TLS | **13282**<br><sub>(151 MiB / 33.6% CPU)</sub> | **13282**<br><sub>(151 MiB / 33.6% CPU)</sub> | *Not possible* | *Not possible* | 🥇 **13433**<br><sub>(119 MiB / 31.7% CPU)</sub> | 🥇 **13433**<br><sub>(119 MiB / 31.7% CPU)</sub> |
| HTTP/1 · TLS | HTTP/3 · QUIC | **3511**<br><sub>(98 MiB / 47.2% CPU)</sub> | **3511**<br><sub>(98 MiB / 47.2% CPU)</sub> | *Not possible (no QUIC)* | *Not possible (no QUIC)* | 🥇 **5856**<br><sub>(124 MiB / 34% CPU)</sub> | 🥇 **5856**<br><sub>(124 MiB / 34% CPU)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | 🥇 **20764**<br><sub>(89 MiB / 39.2% CPU)</sub> | 🥇 **20764**<br><sub>(89 MiB / 39.2% CPU)</sub> | *Not possible* | *Not possible* | **17011**<br><sub>(105 MiB / 38.1% CPU)</sub> | **17011**<br><sub>(105 MiB / 38.1% CPU)</sub> |
| HTTP/2 · plain | HTTP/1 · TLS | **13358**<br><sub>(98 MiB / 37.9% CPU)</sub> | **13358**<br><sub>(98 MiB / 37.9% CPU)</sub> | *Not possible* | *Not possible* | 🥇 **14990**<br><sub>(117 MiB / 41.9% CPU)</sub> | 🥇 **14990**<br><sub>(117 MiB / 41.9% CPU)</sub> |
| HTTP/2 · plain | HTTP/2 · plain | 🥇 **44499**<br><sub>(74 MiB / 28.2% CPU)</sub> | 🥇 **44499**<br><sub>(74 MiB / 28.2% CPU)</sub> | *Not possible* | *Not possible* | **24595**<br><sub>(106 MiB / 36.3% CPU)</sub> | **24595**<br><sub>(106 MiB / 36.3% CPU)</sub> |
| HTTP/2 · plain | HTTP/2 · TLS | 🥇 **27865**<br><sub>(78 MiB / 28.9% CPU)</sub> | 🥇 **27865**<br><sub>(78 MiB / 28.9% CPU)</sub> | *Not possible* | *Not possible* | **23088**<br><sub>(113 MiB / 36.2% CPU)</sub> | **23088**<br><sub>(113 MiB / 36.2% CPU)</sub> |
| HTTP/2 · plain | HTTP/3 · QUIC | **6007**<br><sub>(96 MiB / 44.8% CPU)</sub> | **6007**<br><sub>(96 MiB / 44.8% CPU)</sub> | *Not possible (no QUIC)* | *Not possible (no QUIC)* | 🥇 **7284**<br><sub>(116 MiB / 33.9% CPU)</sub> | 🥇 **7284**<br><sub>(116 MiB / 33.9% CPU)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **19696**<br><sub>(93 MiB / 40.1% CPU)</sub> | 🥇 **19696**<br><sub>(93 MiB / 40.1% CPU)</sub> | **11601**<br><sub>(72 MiB / 14.7% CPU)</sub> | **11601**<br><sub>(72 MiB / 14.7% CPU)</sub> | **14579**<br><sub>(112 MiB / 39.1% CPU)</sub> | **14579**<br><sub>(112 MiB / 39.1% CPU)</sub> |
| HTTP/2 · TLS | HTTP/1 · TLS | 🥇 **14310**<br><sub>(98 MiB / 40.2% CPU)</sub> | 🥇 **14310**<br><sub>(98 MiB / 40.2% CPU)</sub> | **9839**<br><sub>(91 MiB / 14.9% CPU)</sub> | **9839**<br><sub>(91 MiB / 14.9% CPU)</sub> | **11766**<br><sub>(122 MiB / 41.5% CPU)</sub> | **11766**<br><sub>(122 MiB / 41.5% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | 🥇 **43713**<br><sub>(82 MiB / 29.7% CPU)</sub> | 🥇 **43713**<br><sub>(82 MiB / 29.7% CPU)</sub> | *Not possible* | *Not possible* | **25843**<br><sub>(113 MiB / 37.6% CPU)</sub> | **25843**<br><sub>(113 MiB / 37.6% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **31290**<br><sub>(85 MiB / 28% CPU)</sub> | 🥇 **31290**<br><sub>(85 MiB / 28% CPU)</sub> | *Not possible* | *Not possible* | **28790**<br><sub>(117 MiB / 37.3% CPU)</sub> | **28790**<br><sub>(117 MiB / 37.3% CPU)</sub> |
| HTTP/2 · TLS | HTTP/3 · QUIC | 🥇 **7116**<br><sub>(109 MiB / 43.4% CPU)</sub> | 🥇 **7116**<br><sub>(109 MiB / 43.4% CPU)</sub> | *Not possible (no QUIC)* | *Not possible (no QUIC)* | **6614**<br><sub>(123 MiB / 36.6% CPU)</sub> | **6614**<br><sub>(123 MiB / 36.6% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | **5422**<br><sub>(94 MiB / 39.6% CPU)</sub> | **5422**<br><sub>(94 MiB / 39.6% CPU)</sub> | **0**<br><sub>(63 MiB / 11.4% CPU)</sub> | **9594**<br><sub>(63 MiB / 11.4% CPU)</sub> | 🥇 **7112**<br><sub>(166 MiB / 35.9% CPU)</sub> | 🥇 **7112**<br><sub>(166 MiB / 35.9% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | **5401**<br><sub>(117 MiB / 40.8% CPU)</sub> | **5401**<br><sub>(117 MiB / 40.8% CPU)</sub> | **0**<br><sub>(66 MiB / 11.9% CPU)</sub> | **6561**<br><sub>(66 MiB / 11.9% CPU)</sub> | 🥇 **5996**<br><sub>(175 MiB / 36.7% CPU)</sub> | 🥇 **5996**<br><sub>(175 MiB / 36.7% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/2 · plain | **7072**<br><sub>(98 MiB / 40.3% CPU)</sub> | **7072**<br><sub>(98 MiB / 40.3% CPU)</sub> | *Not possible (no H3 to H2)* | *Not possible (no H3 to H2)* | 🥇 **8446**<br><sub>(172 MiB / 34% CPU)</sub> | 🥇 **8446**<br><sub>(172 MiB / 34% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | **6213**<br><sub>(106 MiB / 39% CPU)</sub> | **6213**<br><sub>(106 MiB / 39% CPU)</sub> | *Not possible (no H3 to H2)* | *Not possible (no H3 to H2)* | 🥇 **7921**<br><sub>(167 MiB / 34.9% CPU)</sub> | 🥇 **7921**<br><sub>(167 MiB / 34.9% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/3 · QUIC | **4167**<br><sub>(97 MiB / 38.6% CPU)</sub> | **4167**<br><sub>(97 MiB / 38.6% CPU)</sub> | *Not possible (no QUIC)* | *Not possible (no QUIC)* | 🥇 **4703**<br><sub>(188 MiB / 30.7% CPU)</sub> | 🥇 **4703**<br><sub>(188 MiB / 30.7% CPU)</sub> |

### MITM (TWP only)

Same Client×Origin wires with interception on (`compare-product` [34126809918](https://github.com/justcoding121/titanium-web-proxy/actions/runs/34126809918)). **Lite** = no-op handlers (unchanged-lite finish). **Full** = append-only header mutation (harness: one probe header each way; product: generic append-only relay via `MitmCompressedRelayHelper`). nginx/YARP cannot MITM. **Lite÷Reverse** / **Full÷Reverse** vs bare reverse (same job). Completion gate: Lite and Full ≥ **0.80×** reverse sustain @ c=64 (median of 3 GHA runs); reverse TWP÷YARP ≥ **0.95×** (no nginx gate).

**v1 append-only relay (2026-08-27):** Pre-fix H2→H2 Full÷Reverse was **0.13–0.16×** ([32960766249](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32960766249)). Post-fix @ `df172718`: H2 plain→H2 plain Full **0.77–0.79×**, H3→H1 Full **0.91–0.93×**, all MITM arms ≥ **0.70×** on median of [33041445371](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33041445371), [33055267086](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33055267086), [33055272140](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33055272140).

**v2 drop-only + non-unique append (2026-08-27):** `MitmStaticRebuildHelper` rebuilds static HPACK/QPACK after 1–4 unique header drops; trailing non-unique appends stay on compressed relay. @ `024bd68d`: all MITM arms ≥ **0.70×** on GHA median ([33087088466](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087088466), [33087091622](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087091622), [33105885748](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33105885748) Linux; [33087085235](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087085235), [33087088466](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087088466), [33087091622](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087091622) Windows). H2 plain→H2 plain Full **0.77–0.79×** (Win) / **0.78×** (Lin).

| Client | Origin | Lite sustain | Full sustain | Lite÷Reverse | Full÷Reverse |
|---|---|---:|---:|---:|---:|
| HTTP/1 · plain | HTTP/1 · plain | **19900**<br><sub>(85 MiB / 35% CPU)</sub> | **16913**<br><sub>(86 MiB / 35.3% CPU)</sub> | **1.32×** | **1.13×** |
| HTTP/1 · plain | HTTP/1 · TLS | **16485**<br><sub>(95 MiB / 39.2% CPU)</sub> | **15930**<br><sub>(95 MiB / 38.2% CPU)</sub> | **1.43×** | **1.38×** |
| HTTP/1 · plain | HTTP/2 · plain | **21395**<br><sub>(94 MiB / 39.2% CPU)</sub> | **22098**<br><sub>(94 MiB / 38.6% CPU)</sub> | **1.21×** | **1.25×** |
| HTTP/1 · plain | HTTP/2 · TLS | **18305**<br><sub>(112 MiB / 38.2% CPU)</sub> | **17064**<br><sub>(108 MiB / 38.8% CPU)</sub> | **1.29×** | **1.2×** |
| HTTP/1 · plain | HTTP/3 · QUIC | **5206**<br><sub>(92 MiB / 45.1% CPU)</sub> | **4197**<br><sub>(92 MiB / 44.5% CPU)</sub> | **1×** | **0.8×** |
| HTTP/1 · TLS | HTTP/1 · plain | **13388**<br><sub>(97 MiB / 34.1% CPU)</sub> | **9514**<br><sub>(97 MiB / 33.5% CPU)</sub> | **1.26×** | **0.9×** |
| HTTP/1 · TLS | HTTP/1 · TLS | **11780**<br><sub>(99 MiB / 36.4% CPU)</sub> | **8823**<br><sub>(100 MiB / 35.4% CPU)</sub> | **1.32×** | **0.99×** |
| HTTP/1 · TLS | HTTP/2 · plain | **10530**<br><sub>(99 MiB / 36.1% CPU)</sub> | **10040**<br><sub>(103 MiB / 36% CPU)</sub> | **0.71×** | **0.68×** |
| HTTP/1 · TLS | HTTP/2 · TLS | **8486**<br><sub>(155 MiB / 32.1% CPU)</sub> | **7775**<br><sub>(169 MiB / 33.9% CPU)</sub> | **0.64×** | **0.59×** |
| HTTP/1 · TLS | HTTP/3 · QUIC | **3027**<br><sub>(105 MiB / 48.8% CPU)</sub> | **2898**<br><sub>(105 MiB / 48.4% CPU)</sub> | **0.86×** | **0.83×** |
| HTTP/2 · plain | HTTP/1 · plain | **16212**<br><sub>(91 MiB / 39.3% CPU)</sub> | **14686**<br><sub>(91 MiB / 39.6% CPU)</sub> | **0.78×** | **0.71×** |
| HTTP/2 · plain | HTTP/1 · TLS | **13272**<br><sub>(98 MiB / 41.8% CPU)</sub> | **11864**<br><sub>(98 MiB / 40.3% CPU)</sub> | **0.99×** | **0.89×** |
| HTTP/2 · plain | HTTP/2 · plain | **28429**<br><sub>(80 MiB / 35.7% CPU)</sub> | **23104**<br><sub>(78 MiB / 34.1% CPU)</sub> | **0.64×** | **0.52×** |
| HTTP/2 · plain | HTTP/2 · TLS | **23386**<br><sub>(83 MiB / 35.7% CPU)</sub> | **20906**<br><sub>(84 MiB / 35.3% CPU)</sub> | **0.84×** | **0.75×** |
| HTTP/2 · plain | HTTP/3 · QUIC | **4491**<br><sub>(96 MiB / 45.1% CPU)</sub> | **4985**<br><sub>(97 MiB / 45.2% CPU)</sub> | **0.75×** | **0.83×** |
| HTTP/2 · TLS | HTTP/1 · plain | **15975**<br><sub>(94 MiB / 40.1% CPU)</sub> | **15511**<br><sub>(92 MiB / 40.5% CPU)</sub> | **0.81×** | **0.79×** |
| HTTP/2 · TLS | HTTP/1 · TLS | **16070**<br><sub>(97 MiB / 41.1% CPU)</sub> | **12680**<br><sub>(98 MiB / 43.1% CPU)</sub> | **1.12×** | **0.89×** |
| HTTP/2 · TLS | HTTP/2 · plain | **25016**<br><sub>(90 MiB / 33.1% CPU)</sub> | **26369**<br><sub>(87 MiB / 35.7% CPU)</sub> | **0.57×** | **0.6×** |
| HTTP/2 · TLS | HTTP/2 · TLS | **19676**<br><sub>(90 MiB / 31.6% CPU)</sub> | **24635**<br><sub>(88 MiB / 34.8% CPU)</sub> | **0.63×** | **0.79×** |
| HTTP/2 · TLS | HTTP/3 · QUIC | **5379**<br><sub>(106 MiB / 43.2% CPU)</sub> | **6026**<br><sub>(106 MiB / 40.5% CPU)</sub> | **0.76×** | **0.85×** |
| HTTP/3 · QUIC | HTTP/1 · plain | **4430**<br><sub>(103 MiB / 41.2% CPU)</sub> | **4710**<br><sub>(103 MiB / 38.8% CPU)</sub> | **0.82×** | **0.87×** |
| HTTP/3 · QUIC | HTTP/1 · TLS | **4132**<br><sub>(116 MiB / 41.7% CPU)</sub> | **3975**<br><sub>(115 MiB / 41.4% CPU)</sub> | **0.77×** | **0.74×** |
| HTTP/3 · QUIC | HTTP/2 · plain | **5296**<br><sub>(100 MiB / 38.6% CPU)</sub> | **5368**<br><sub>(100 MiB / 40.1% CPU)</sub> | **0.75×** | **0.76×** |
| HTTP/3 · QUIC | HTTP/2 · TLS | **5082**<br><sub>(108 MiB / 37.2% CPU)</sub> | **5304**<br><sub>(109 MiB / 39.7% CPU)</sub> | **0.82×** | **0.85×** |
| HTTP/3 · QUIC | HTTP/3 · QUIC | **3482**<br><sub>(97 MiB / 36.4% CPU)</sub> | **3026**<br><sub>(96 MiB / 37.8% CPU)</sub> | **0.84×** | **0.73×** |

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
| Peer-normalized RPS (when YARP peer exists) | `(TWP÷YARP)_7 ÷ (TWP÷YARP)_6 ≥ 0.90` **or** current `(TWP÷YARP) ≥ 0.90` |
| Absolute RPS floor (TWP arms) | `7.0 ÷ 6.0 ≥ 0.70` (runner heat — peers move with the box) |
| RSS | `7.0 ÷ 6.0 ≤ 1.20` per TWP arm @ c=64 |

nginx/YARP absolute RPS is **not** gated. See [`validate-cross-version.ps1`](https://github.com/justcoding121/titanium-web-proxy/blob/develop/tools/RpsLoadProbe/validate-cross-version.ps1).

```powershell
pwsh tools/RpsLoadProbe/run-rps.ps1 -Mode compare-cross-version
pwsh tools/RpsLoadProbe/validate-cross-version.ps1 `
  -BaselineCsv tools/RpsLoadProbe/results/baseline-6.0-win.csv `
  -CurrentCsv  tools/RpsLoadProbe/results/rps-ramp-*.csv
```

Median of **3** @ `0ef6d4dd`. Source: Actions [33270571908](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33270571908). Sustain RPS @ **c=64** (SLO-pass median). Absolute 7.0÷6.0 ≈ **0.96–1.00×** on same-protocol arms; peer-norm ≈ **0.90–1.03×**.

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

`validate-cross-version.ps1` **passed** on both OS CSVs (RSS floor **1.20**; peer-norm also passes when current TWP÷YARP ≥ **0.90**). MITM arms live in `compare-product` / `compare-mitm`, not this reverse-only matrix.


## Heavier reverse workloads

Separate from the tiny-GET matrix. Same measurement environments. Modes: `compare-bodies`, `compare-post`, `compare-lossy`, `compare-tls-cost`, `compare-arch` in [RpsLoadProbe](https://github.com/justcoding121/titanium-web-proxy/tree/develop/tools/RpsLoadProbe). Dispatch each independently via `workflow_dispatch` (no need to re-run full `compare-product`). **PUT with the same body is the same proxy work as POST; DELETE with no body matches GET** — only POST is published. Bodies/POST/lossy stay **half-duplex**. `compare-arch` is the slow-consumer / early-response / duplex set. Laptop numbers are on [Performance Local Lab](Performance-Local-Lab#architecture-sensitive); CI medians go in the tables below.

Lossy link = **userspace** shim (not kernel `netem`): TCP gets per-buffer delay + occasional whole-connection stalls (honest HOL for multiplexed H2); UDP gets per-datagram delay + drops (QUIC). `compare-lossy` publishes H1/H2/H3; H3 is where the protocol design is supposed to matter.

### Windows — heavier reverse GET (64 KiB / 256 KiB)

Median of **3** repeats on `windows-latest` @ `024bd68d`. Source: Actions [34126816180](https://github.com/justcoding121/titanium-web-proxy/actions/runs/34126816180) (`compare-bodies`). Warmup 2s / measure 8s. **RPS cells** include `(MiB / CPU%)` footprints.

| Body | Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---|---|---:|---:|---:|---:|---:|---:|
| 64 KiB | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **8,810**<br><sub>(113 MiB / 47.9% CPU)</sub> | **8,810**<br><sub>(113 MiB / 47.9% CPU)</sub> | **548**<br><sub>(139 MiB / 24.7% CPU)</sub> | **548**<br><sub>(139 MiB / 24.7% CPU)</sub> | **7,794**<br><sub>(135 MiB / 45.9% CPU)</sub> | **7,794**<br><sub>(135 MiB / 45.9% CPU)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **7,762**<br><sub>(178 MiB / 42.6% CPU)</sub> | **7,762**<br><sub>(178 MiB / 42.6% CPU)</sub> | **555**<br><sub>(140 MiB / 24.8% CPU)</sub> | **555**<br><sub>(140 MiB / 24.8% CPU)</sub> | **6,825**<br><sub>(132 MiB / 49.6% CPU)</sub> | **6,825**<br><sub>(132 MiB / 49.6% CPU)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **4,020**<br><sub>(135 MiB / 38.1% CPU)</sub> | **4,020**<br><sub>(135 MiB / 38.1% CPU)</sub> | *Not possible (no QUIC)* | *Not possible (no QUIC)* | **3,784**<br><sub>(193 MiB / 48.1% CPU)</sub> | **3,784**<br><sub>(193 MiB / 48.1% CPU)</sub> |
| 256 KiB | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **2,842**<br><sub>(130 MiB / 46.4% CPU)</sub> | **2,842**<br><sub>(130 MiB / 46.4% CPU)</sub> | **0**<br><sub>(141 MiB / 24.8% CPU)</sub> | **162**<br><sub>(141 MiB / 24.8% CPU)</sub> | **2,600**<br><sub>(134 MiB / 45.6% CPU)</sub> | **2,600**<br><sub>(134 MiB / 45.6% CPU)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **2,541**<br><sub>(147 MiB / 40.6% CPU)</sub> | **2,541**<br><sub>(147 MiB / 40.6% CPU)</sub> | **0**<br><sub>(140 MiB / 24.8% CPU)</sub> | **148**<br><sub>(140 MiB / 24.8% CPU)</sub> | **1,911**<br><sub>(134 MiB / 43.7% CPU)</sub> | **1,911**<br><sub>(134 MiB / 43.7% CPU)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **1,156**<br><sub>(108 MiB / 39.7% CPU)</sub> | **1,156**<br><sub>(108 MiB / 39.7% CPU)</sub> | *Not possible (no QUIC)* | *Not possible (no QUIC)* | **1,077**<br><sub>(170 MiB / 44.0% CPU)</sub> | **1,077**<br><sub>(170 MiB / 44.0% CPU)</sub> |

nginx/Windows collapses on large reverse bodies in this harness; treat as same-OS only. H1 TLS **64 KiB** ≈ **1.11×** YARP; **256 KiB** ≈ **1.23×**. H2→H1 64 KiB ≈ **1.21×**; H3→H1 64 KiB ≈ **1.18×**.

### Linux — heavier reverse GET (64 KiB / 256 KiB)

Median of **3** repeats @ `024bd68d`. Source: Actions [34126816180](https://github.com/justcoding121/titanium-web-proxy/actions/runs/34126816180) (`compare-bodies`). Warmup 2s / measure 8s.

| Body | Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---|---|---:|---:|---:|---:|---:|---:|
| 64 KiB | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **7,909**<br><sub>(169 MiB / 44.8% CPU)</sub> | **7,909**<br><sub>(169 MiB / 44.8% CPU)</sub> | **5,528**<br><sub>(97 MiB / 51.9% CPU)</sub> | **5,528**<br><sub>(97 MiB / 51.9% CPU)</sub> | **6,447**<br><sub>(167 MiB / 48.4% CPU)</sub> | **6,447**<br><sub>(167 MiB / 48.4% CPU)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **5,792**<br><sub>(235 MiB / 38.6% CPU)</sub> | **5,792**<br><sub>(235 MiB / 38.6% CPU)</sub> | **1,554**<br><sub>(98 MiB / 18.2% CPU)</sub> | **1,554**<br><sub>(98 MiB / 18.2% CPU)</sub> | **4,814**<br><sub>(158 MiB / 46.8% CPU)</sub> | **4,814**<br><sub>(158 MiB / 46.8% CPU)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **5,478**<br><sub>(181 MiB / 44.1% CPU)</sub> | **5,478**<br><sub>(181 MiB / 44.1% CPU)</sub> | **0**<br><sub>(124 MiB / 21.4% CPU)</sub> | **1,696**<br><sub>(124 MiB / 21.4% CPU)</sub> | **4,331**<br><sub>(221 MiB / 52.3% CPU)</sub> | **4,331**<br><sub>(221 MiB / 52.3% CPU)</sub> |
| 256 KiB | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **2,688**<br><sub>(126 MiB / 37.2% CPU)</sub> | **2,688**<br><sub>(126 MiB / 37.2% CPU)</sub> | **1,717**<br><sub>(97 MiB / 54.0% CPU)</sub> | **1,717**<br><sub>(97 MiB / 54.0% CPU)</sub> | **2,139**<br><sub>(168 MiB / 45.7% CPU)</sub> | **2,139**<br><sub>(168 MiB / 45.7% CPU)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **1,533**<br><sub>(183 MiB / 32.6% CPU)</sub> | **1,533**<br><sub>(183 MiB / 32.6% CPU)</sub> | **578**<br><sub>(98 MiB / 23.1% CPU)</sub> | **578**<br><sub>(98 MiB / 23.1% CPU)</sub> | **1,381**<br><sub>(161 MiB / 46.0% CPU)</sub> | **1,381**<br><sub>(161 MiB / 46.0% CPU)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **1,458**<br><sub>(148 MiB / 43.5% CPU)</sub> | **1,458**<br><sub>(148 MiB / 43.5% CPU)</sub> | **0**<br><sub>(119 MiB / 20.5% CPU)</sub> | **125**<br><sub>(119 MiB / 20.5% CPU)</sub> | **1,248**<br><sub>(219 MiB / 48.1% CPU)</sub> | **1,248**<br><sub>(219 MiB / 48.1% CPU)</sub> |

On this GHA pass TWP÷YARP H1 TLS ≈ **1.23×** (64 KiB) / **1.28×** (256 KiB); H2→H1 ≈ **1.23×** / **1.16×**; H3→H1 ≈ **1.27×** / **1.13×**. TWP÷nginx H1 TLS ≈ **1.43** / **1.58**. Absolute RPS swings by VM; prefer ratios.

### Windows — POST 64 KiB request + 64 KiB response

Median of **3** repeats on `windows-latest` @ `024bd68d`. Source: Actions [34126819267](https://github.com/justcoding121/titanium-web-proxy/actions/runs/34126819267) (`compare-post`).

| Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---|---:|---:|---:|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | 🥇 **6,578**<br><sub>(102 MiB / 44.2% CPU)</sub> | **6,578**<br><sub>(102 MiB / 44.2% CPU)</sub> | **381**<br><sub>(139 MiB / 24.9% CPU)</sub> | **381**<br><sub>(139 MiB / 24.9% CPU)</sub> | **4,448**<br><sub>(133 MiB / 56.5% CPU)</sub> | **4,448**<br><sub>(133 MiB / 56.5% CPU)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **4,521**<br><sub>(178 MiB / 48.8% CPU)</sub> | **4,521**<br><sub>(178 MiB / 48.8% CPU)</sub> | **370**<br><sub>(142 MiB / 24.8% CPU)</sub> | **370**<br><sub>(142 MiB / 24.8% CPU)</sub> | **3,851**<br><sub>(134 MiB / 51.9% CPU)</sub> | **3,851**<br><sub>(134 MiB / 51.9% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **2,211**<br><sub>(177 MiB / 41.2% CPU)</sub> | **2,211**<br><sub>(177 MiB / 41.2% CPU)</sub> | *Not possible (no QUIC)* | *Not possible (no QUIC)* | **2,182**<br><sub>(215 MiB / 48.9% CPU)</sub> | **2,182**<br><sub>(215 MiB / 48.9% CPU)</sub> |

TWP leads H1 POST (~**1.5×** YARP), H2 POST (~**1.2×** YARP), and H3 POST (~**1.1×** YARP).

### Linux — POST 64 KiB request + 64 KiB response

Median of **3** repeats @ `024bd68d`. Source: Actions [34126819267](https://github.com/justcoding121/titanium-web-proxy/actions/runs/34126819267) (`compare-post`).

| Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---|---:|---:|---:|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | 🥇 **7,829**<br><sub>(131 MiB / 40.9% CPU)</sub> | **7,829**<br><sub>(131 MiB / 40.9% CPU)</sub> | **5,642**<br><sub>(101 MiB / 39.5% CPU)</sub> | **5,642**<br><sub>(101 MiB / 39.5% CPU)</sub> | **5,323**<br><sub>(168 MiB / 53.5% CPU)</sub> | **5,323**<br><sub>(168 MiB / 53.5% CPU)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **4,898**<br><sub>(233 MiB / 43.8% CPU)</sub> | **4,898**<br><sub>(233 MiB / 43.8% CPU)</sub> | **2,242**<br><sub>(115 MiB / 17.1% CPU)</sub> | **2,242**<br><sub>(115 MiB / 17.1% CPU)</sub> | **3,998**<br><sub>(169 MiB / 49.7% CPU)</sub> | **3,998**<br><sub>(169 MiB / 49.7% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **3,979**<br><sub>(237 MiB / 44.7% CPU)</sub> | **3,979**<br><sub>(237 MiB / 44.7% CPU)</sub> | **985**<br><sub>(114 MiB / 23.9% CPU)</sub> | **985**<br><sub>(114 MiB / 23.9% CPU)</sub> | **3,302**<br><sub>(261 MiB / 50.9% CPU)</sub> | **3,302**<br><sub>(261 MiB / 50.9% CPU)</sub> |

Linux nginx H1/H2/H3 POST completed (nginx.org mainline). TWP÷YARP H1 ≈ **1.5×**; H2 ≈ **1.3×**; H3 ≈ **1.1×**. TWP÷nginx H3 ≈ **4×**.

### Windows — lossy / high-RTT (H2 HOL / H3 loss)

Userspace **5 ms** one-way delay + **1%** TCP connection stall (H1/H2) or UDP datagram drop (H3); **64 KiB** GET. Median of **3** repeats on `windows-latest` @ `024bd68d` — [34126822092](https://github.com/justcoding121/titanium-web-proxy/actions/runs/34126822092) (`compare-lossy`).
| Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---|---:|---:|---:|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | 🥇 **662**<br><sub>(106 MiB / 5.1% CPU)</sub> | **662**<br><sub>(106 MiB / 5.1% CPU)</sub> | **639**<br><sub>(138 MiB / 19.3% CPU)</sub> | **639**<br><sub>(138 MiB / 19.3% CPU)</sub> | **659**<br><sub>(111 MiB / 5.9% CPU)</sub> | **659**<br><sub>(111 MiB / 5.9% CPU)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | **0**<br><sub>(123 MiB / 1.9% CPU)</sub> | **85**<br><sub>(123 MiB / 1.9% CPU)</sub> | **0**<br><sub>(140 MiB / 0.8% CPU)</sub> | **17**<br><sub>(140 MiB / 0.8% CPU)</sub> | **0**<br><sub>(99 MiB / 1.1% CPU)</sub> | **16**<br><sub>(99 MiB / 1.1% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | **0**<br><sub>(66 MiB / 0.0% CPU)</sub> | **0**<br><sub>(66 MiB / 0.0% CPU)</sub> | *Not possible (no QUIC)* | *Not possible (no QUIC)* | **0**<br><sub>(79 MiB / 0.0% CPU)</sub> | **0**<br><sub>(79 MiB / 0.0% CPU)</sub> |

TWP H2 HOL leads (~**3.31×** YARP). H3 is the protocol-shape win vs H2 HOL on the same lossy session; Win H3 GHA remains 0 (laptop remeasure kept above).

### Linux — lossy / high-RTT (H2 HOL / H3 loss)

Median of **3** repeats @ `024bd68d`. Source: [34126822092](https://github.com/justcoding121/titanium-web-proxy/actions/runs/34126822092) (`compare-lossy`; lossy H3 uses `quic-http3`).

| Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---|---:|---:|---:|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | **1,203**<br><sub>(144 MiB / 13.2% CPU)</sub> | **1,203**<br><sub>(144 MiB / 13.2% CPU)</sub> | 🥇 **1,208**<br><sub>(98 MiB / 11.3% CPU)</sub> | **1,208**<br><sub>(98 MiB / 11.3% CPU)</sub> | **1,196**<br><sub>(150 MiB / 16.5% CPU)</sub> | **1,196**<br><sub>(150 MiB / 16.5% CPU)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **313**<br><sub>(170 MiB / 6.9% CPU)</sub> | **313**<br><sub>(170 MiB / 6.9% CPU)</sub> | **40**<br><sub>(97 MiB / 0.3% CPU)</sub> | **40**<br><sub>(97 MiB / 0.3% CPU)</sub> | **40**<br><sub>(127 MiB / 1.4% CPU)</sub> | **40**<br><sub>(127 MiB / 1.4% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | **324**<br><sub>(149 MiB / 12.9% CPU)</sub> | **324**<br><sub>(149 MiB / 12.9% CPU)</sub> | **90**<br><sub>(107 MiB / 2.4% CPU)</sub> | **90**<br><sub>(107 MiB / 2.4% CPU)</sub> | 🥇 **357**<br><sub>(182 MiB / 20.8% CPU)</sub> | **357**<br><sub>(182 MiB / 20.8% CPU)</sub> |

TWP H2 HOL ≫ YARP (~**7.7×**). H3 TWP÷YARP ≈ **1×**.

### Architecture-sensitive

`compare-arch` isolates slow app readers, origin-early response, H2 duplex, and WebSocket echo. See [TWP vs YARP IO model](Performance-Profiling#twp-vs-yarp-io-model). Laptop 1-rep numbers are on [Performance Local Lab](Performance-Local-Lab#architecture-sensitive).

Median of **3** repeats on matched 4 vCPU / 16 GiB runners @ `024bd68d` ([34126829362](https://github.com/justcoding121/titanium-web-proxy/actions/runs/34126829362)) (`compare-arch`). Slow consumer = 256 KiB GET, 16 KiB read + 8 ms sleep. Early response = 64 KiB POST, origin writes after 8 KiB. Duplex H2 = overlapping 64 KiB POST on H2 TLS↔H2 TLS. WebSocket = echo round-trips/sec.

`compare-lossy` (slow **network**) is already published above; it is not a slow **app** reader.

#### Windows

| Scenario | Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---|---|---:|---:|---:|---:|---:|---:|
| Slow consumer (256 KiB GET, throttled client read) | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **248**<br><sub>(98 MiB / 4.0% CPU)</sub> | **248**<br><sub>(98 MiB / 4.0% CPU)</sub> | **208**<br><sub>(141 MiB / 24.8% CPU)</sub> | **208**<br><sub>(141 MiB / 24.8% CPU)</sub> | **248**<br><sub>(108 MiB / 4.0% CPU)</sub> | **248**<br><sub>(108 MiB / 4.0% CPU)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · TLS | HTTP/1 · plain | **248**<br><sub>(115 MiB / 4.1% CPU)</sub> | **248**<br><sub>(115 MiB / 4.1% CPU)</sub> | **171**<br><sub>(139 MiB / 24.8% CPU)</sub> | **171**<br><sub>(139 MiB / 24.8% CPU)</sub> | 🥇 **249**<br><sub>(111 MiB / 6.2% CPU)</sub> | **249**<br><sub>(111 MiB / 6.2% CPU)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **264**<br><sub>(94 MiB / 18.1% CPU)</sub> | **264**<br><sub>(94 MiB / 18.1% CPU)</sub> | *Not possible (no QUIC)* | *Not possible (no QUIC)* | **263**<br><sub>(158 MiB / 20.7% CPU)</sub> | **263**<br><sub>(158 MiB / 20.7% CPU)</sub> |
| Early response (origin writes after first request chunk) | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **6,099**<br><sub>(99 MiB / 43.0% CPU)</sub> | **6,099**<br><sub>(99 MiB / 43.0% CPU)</sub> | **376**<br><sub>(140 MiB / 24.8% CPU)</sub> | **376**<br><sub>(140 MiB / 24.8% CPU)</sub> | **4,216**<br><sub>(136 MiB / 51.4% CPU)</sub> | **4,216**<br><sub>(136 MiB / 51.4% CPU)</sub> |
| Early response (origin writes after first request chunk) | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **4,285**<br><sub>(178 MiB / 35.9% CPU)</sub> | **4,285**<br><sub>(178 MiB / 35.9% CPU)</sub> | **0**<br><sub>(141 MiB / 24.6% CPU)</sub> | **284**<br><sub>(141 MiB / 24.6% CPU)</sub> | **2,821**<br><sub>(135 MiB / 40.1% CPU)</sub> | **2,821**<br><sub>(135 MiB / 40.1% CPU)</sub> |
| Early response (origin writes after first request chunk) | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **2,252**<br><sub>(148 MiB / 41.0% CPU)</sub> | **2,252**<br><sub>(148 MiB / 41.0% CPU)</sub> | *Not possible (no QUIC)* | *Not possible (no QUIC)* | **1,721**<br><sub>(197 MiB / 45.2% CPU)</sub> | **1,721**<br><sub>(197 MiB / 45.2% CPU)</sub> |
| Duplex (both directions live) | HTTP/2 · TLS | HTTP/2 · TLS | **0**<br><sub>(92 MiB / 0.1% CPU)</sub> | **0**<br><sub>(92 MiB / 0.1% CPU)</sub> | *Not possible* | *Not possible* | **0**<br><sub>(114 MiB / 0.1% CPU)</sub> | **0**<br><sub>(114 MiB / 0.1% CPU)</sub> |
| Duplex (WebSocket / extended CONNECT) | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **31,275**<br><sub>(98 MiB / 40.2% CPU)</sub> | **31,275**<br><sub>(98 MiB / 40.2% CPU)</sub> | **18,071**<br><sub>(141 MiB / 24.6% CPU)</sub> | **18,071**<br><sub>(141 MiB / 24.6% CPU)</sub> | **28,913**<br><sub>(88 MiB / 43.6% CPU)</sub> | **28,913**<br><sub>(88 MiB / 43.6% CPU)</sub> |

#### Linux

| Scenario | Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---|---|---:|---:|---:|---:|---:|---:|
| Slow consumer (256 KiB GET, throttled client read) | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **465**<br><sub>(118 MiB / 10.0% CPU)</sub> | **465**<br><sub>(118 MiB / 10.0% CPU)</sub> | **406**<br><sub>(97 MiB / 10.4% CPU)</sub> | **406**<br><sub>(97 MiB / 10.4% CPU)</sub> | **418**<br><sub>(147 MiB / 14.9% CPU)</sub> | **418**<br><sub>(147 MiB / 14.9% CPU)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **472**<br><sub>(141 MiB / 20.2% CPU)</sub> | **472**<br><sub>(141 MiB / 20.2% CPU)</sub> | **259**<br><sub>(97 MiB / 7.7% CPU)</sub> | **259**<br><sub>(97 MiB / 7.7% CPU)</sub> | **467**<br><sub>(146 MiB / 25.7% CPU)</sub> | **467**<br><sub>(146 MiB / 25.7% CPU)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **471**<br><sub>(129 MiB / 35.7% CPU)</sub> | **471**<br><sub>(129 MiB / 35.7% CPU)</sub> | **0**<br><sub>(118 MiB / 21.4% CPU)</sub> | **193**<br><sub>(118 MiB / 21.4% CPU)</sub> | **466**<br><sub>(196 MiB / 40.7% CPU)</sub> | **466**<br><sub>(196 MiB / 40.7% CPU)</sub> |
| Early response (origin writes after first request chunk) | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **4,568**<br><sub>(135 MiB / 47.8% CPU)</sub> | **4,568**<br><sub>(135 MiB / 47.8% CPU)</sub> | **3,545**<br><sub>(98 MiB / 49.3% CPU)</sub> | **3,545**<br><sub>(98 MiB / 49.3% CPU)</sub> | **3,027**<br><sub>(176 MiB / 56.5% CPU)</sub> | **3,027**<br><sub>(176 MiB / 56.5% CPU)</sub> |
| Early response (origin writes after first request chunk) | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **3,317**<br><sub>(220 MiB / 46.1% CPU)</sub> | **3,317**<br><sub>(220 MiB / 46.1% CPU)</sub> | **0**<br><sub>(114 MiB / 23.6% CPU)</sub> | **1,340**<br><sub>(114 MiB / 23.6% CPU)</sub> | **2,151**<br><sub>(169 MiB / 48.0% CPU)</sub> | **2,151**<br><sub>(169 MiB / 48.0% CPU)</sub> |
| Early response (origin writes after first request chunk) | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **2,823**<br><sub>(204 MiB / 45.1% CPU)</sub> | **2,823**<br><sub>(204 MiB / 45.1% CPU)</sub> | **0**<br><sub>(114 MiB / 24.6% CPU)</sub> | **431**<br><sub>(114 MiB / 24.6% CPU)</sub> | **2,073**<br><sub>(243 MiB / 48.3% CPU)</sub> | **2,073**<br><sub>(243 MiB / 48.3% CPU)</sub> |
| Duplex (both directions live) | HTTP/2 · TLS | HTTP/2 · TLS | **0**<br><sub>(101 MiB / 0.1% CPU)</sub> | **0**<br><sub>(101 MiB / 0.1% CPU)</sub> | *Not possible* | *Not possible* | **0**<br><sub>(150 MiB / 0.3% CPU)</sub> | **0**<br><sub>(150 MiB / 0.3% CPU)</sub> |
| Duplex (WebSocket / extended CONNECT) | HTTP/1 · TLS | HTTP/1 · plain | **29,049**<br><sub>(124 MiB / 44.2% CPU)</sub> | **29,049**<br><sub>(124 MiB / 44.2% CPU)</sub> | 🥇 **33,342**<br><sub>(98 MiB / 36.2% CPU)</sub> | **33,342**<br><sub>(98 MiB / 36.2% CPU)</sub> | **26,992**<br><sub>(123 MiB / 44.1% CPU)</sub> | **26,992**<br><sub>(123 MiB / 44.1% CPU)</sub> |

Slow consumer is sleep-bound; H1/H2/H3 sit in the same band. Early-response H1/H2/H3: TWP leads (H1 early ≈ **2.00×** / **1.47×** YARP Win/Linux). **Duplex H2**: YARP leads by design — Win ≈ **0.59×** (1,270 / 2,135), Linux ≈ **0.15×** (282 / 1,882); irreducible concurrent-copier cell (see [IO model](Performance-Profiling#twp-vs-yarp-io-model)). WebSocket: TWP÷YARP Windows ≈ **1.06×**; Linux nginx leads.

### TLS termination cost (H1 TLS → cleartext origin)

Isolates keep-alive tiny GET vs **new connection per request** (handshake-dominated) vs keep-alive **256 KiB**. Product comparison uses RPS and end-to-end latency; TWP can also capture `ClientTlsTiming` when `TWP_RPS_CAPTURE_TLS=1` (child process) — nginx/YARP have no equivalent hook.

#### Windows

Median of **3** repeats on `windows-latest` @ `024bd68d`. Source: Actions [34126826292](https://github.com/justcoding121/titanium-web-proxy/actions/runs/34126826292) (`compare-tls-cost`). Absolute RPS on GHA swings hard; prefer **TWP÷YARP**.

| Workload | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---:|---:|---:|---:|---:|---:|
| Keep-alive · tiny GET | 🥇 **20,329**<br><sub>(84 MiB / 47.8% CPU)</sub> | **20,329**<br><sub>(84 MiB / 47.8% CPU)</sub> | **8,644**<br><sub>(138 MiB / 24.8% CPU)</sub> | **8,644**<br><sub>(138 MiB / 24.8% CPU)</sub> | **17,804**<br><sub>(102 MiB / 50.3% CPU)</sub> | **17,804**<br><sub>(102 MiB / 50.3% CPU)</sub> |
| New-connection · tiny GET | 🥇 **712**<br><sub>(85 MiB / 9.2% CPU)</sub> | **712**<br><sub>(85 MiB / 9.2% CPU)</sub> | **0**<br><sub>(138 MiB / 24.0% CPU)</sub> | **240**<br><sub>(138 MiB / 24.0% CPU)</sub> | **706**<br><sub>(110 MiB / 11.1% CPU)</sub> | **706**<br><sub>(110 MiB / 11.1% CPU)</sub> |
| Keep-alive · 256 KiB GET | 🥇 **2,839**<br><sub>(118 MiB / 44.5% CPU)</sub> | **2,839**<br><sub>(118 MiB / 44.5% CPU)</sub> | **0**<br><sub>(141 MiB / 24.6% CPU)</sub> | **156**<br><sub>(141 MiB / 24.6% CPU)</sub> | **2,609**<br><sub>(137 MiB / 46.5% CPU)</sub> | **2,609**<br><sub>(137 MiB / 46.5% CPU)</sub> |

#### Linux

Median of **3** repeats @ `024bd68d`. Source: Actions [34126826292](https://github.com/justcoding121/titanium-web-proxy/actions/runs/34126826292) (`compare-tls-cost`).

| Workload | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---:|---:|---:|---:|---:|---:|
| Keep-alive · tiny GET | **23,672**<br><sub>(105 MiB / 49.9% CPU)</sub> | **23,672**<br><sub>(105 MiB / 49.9% CPU)</sub> | 🥇 **27,804**<br><sub>(98 MiB / 41.6% CPU)</sub> | **27,804**<br><sub>(98 MiB / 41.6% CPU)</sub> | **20,362**<br><sub>(130 MiB / 50.1% CPU)</sub> | **20,362**<br><sub>(130 MiB / 50.1% CPU)</sub> |
| New-connection · tiny GET | **974**<br><sub>(121 MiB / 47.1% CPU)</sub> | **974**<br><sub>(121 MiB / 47.1% CPU)</sub> | 🥇 **1,007**<br><sub>(98 MiB / 44.3% CPU)</sub> | **1,007**<br><sub>(98 MiB / 44.3% CPU)</sub> | **964**<br><sub>(149 MiB / 46.2% CPU)</sub> | **964**<br><sub>(149 MiB / 46.2% CPU)</sub> |
| Keep-alive · 256 KiB GET | 🥇 **2,722**<br><sub>(126 MiB / 37.0% CPU)</sub> | **2,722**<br><sub>(126 MiB / 37.0% CPU)</sub> | **1,728**<br><sub>(97 MiB / 53.4% CPU)</sub> | **1,728**<br><sub>(97 MiB / 53.4% CPU)</sub> | **2,152**<br><sub>(169 MiB / 45.9% CPU)</sub> | **2,152**<br><sub>(169 MiB / 45.9% CPU)</sub> |

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
