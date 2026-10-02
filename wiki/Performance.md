Throughput and footprint of **Titanium** as a reverse / edge proxy and as a decrypting (**MITM**) proxy, measured on the same harness against **YARP**, **nginx**, **HAProxy**, and **Envoy** where each OS can run them.

**RPS** is requests per second (gRPC tables use **RPC/s**). Numbers are Release builds on matched GitHub-hosted runners: Windows and Linux at **4 vCPU / 16 GiB**, macOS at **`macos-15` Apple Silicon 3-core / 7 GB**. We compare products within an OS, not OS against OS, so read within one table — absolute RPS is not comparable across operating systems. *Not possible* means that product cannot run that path on that OS; *Not measured* means the path exists but no published number yet.

For pooling knobs and certificate first-visit tuning, see [Performance and pooling](Home#performance-and-pooling). Laptop cool A/B tables (not publishable) live on [Performance Local Lab](Performance-Local-Lab).

## Why this comparison is fair

- Same load generator, same origin process, and the same warmup / measure windows (2s / 8s) with the same concurrency ramp (8, 16, 32, 64).
- Every reverse arm is three OS processes: load generator + origin + proxy. Origin-direct omits the proxy; peers are never in-process with the client.
- Same runner class per table (`windows-latest` / `ubuntu-latest` / `macos-15`). Laptop numbers are never mixed into these tables.
- Peers use equivalent TLS/ALPN and streaming-friendly settings on the same loopback shape. HAProxy and Envoy are Linux/macOS only — *Not possible* on Windows, so their columns are omitted there.
- MITM (HTTPS decryption with forged certificates) is Titanium-only; peers cannot MITM. Those tables show Titanium MITM overhead versus its own reverse path on the same wires.
- **Tiny keep-alive GET** (~56-byte JSON) is the industry RPS shape (same class as wrk / TechEmpower). It is also real for small JSON APIs and health checks.
- **Same-protocol H2↔H2 / H3↔H3** on that shape is Titanium’s **best case**: with interception off, Titanium copies frames instead of decoding and re-encoding headers (peers do a full HTTP decode). Medals there are not the typical reverse-proxy job.
- **Typical reverse** is H1 TLS→H1 or H2→H1 (~1.1× YARP on Win/Linux tiny GET). With **larger bodies**, see [Heavier reverse](#heavier-reverse-workloads) (ratios @ `41f4adee`).

## How to read the tables

- Bold RPS is **sustain** (last concurrency that still met error/latency SLOs). When peak differs, it appears in the same cell as `<sub>(peak N · …)</sub>` with RSS/CPU. When sustain equals peak, peak is omitted.
- 🥇 = best among OS-possible peers on that row (highest sustain RPS; on a tie, lower memory then lower CPU%). On tiny Reverse that is Titanium / nginx / YARP (plus HAProxy / Envoy on Linux/macOS). The same rule applies to heavier reverse, architecture-sensitive, TLS-cost, gRPC, and WebSocket peer tables. Gold on tiny GET is that frame-copy best case, not “Titanium is 1.7× on all reverse.” For larger-body H2→H2, see the [heavier tables](#heavier-reverse-workloads).
- **MITM** tables are Titanium-only (no peer medal). **Lite÷Reverse** / **Full÷Reverse** = Titanium MITM sustain ÷ Titanium reverse sustain on the same Client×Origin pair — the overhead of decrypting and intercepting versus bare reverse, not versus nginx or YARP.
- *Not possible* = cannot do that path. *Not measured* = path exists but no published number yet. When every row would be *Not possible* for a peer, that column is omitted and a note above the table explains why.

## Contents

- [Why this comparison is fair](#why-this-comparison-is-fair)
- [How to read the tables](#how-to-read-the-tables)
- [Measurement environment](#measurement-environment)
    - [Windows (GitHub-hosted `windows-latest`)](#windows-github-hosted-windows-latest)
    - [Linux (GitHub-hosted `ubuntu-latest`)](#linux-github-hosted-ubuntu-latest)
    - [macOS (GitHub-hosted `macos-15`, Apple Silicon)](#macos-github-hosted-macos-15-apple-silicon)
    - [Saturation control](#saturation-control)
- [Windows — Titanium vs nginx vs YARP](#windows--titanium-vs-nginx-vs-yarp)
- [Linux — Titanium vs nginx vs HAProxy vs Envoy vs YARP](#linux--titanium-vs-nginx-vs-haproxy-vs-envoy-vs-yarp)
- [macOS — Titanium vs nginx vs HAProxy vs Envoy vs YARP](#macos--titanium-vs-nginx-vs-haproxy-vs-envoy-vs-yarp)
- [Editions (CLI / Plus / Intercept)](#editions-cli--plus--intercept)
- [Cross-version (7.0 vs 6.0)](#cross-version-70-vs-60)
- [Heavier reverse workloads](#heavier-reverse-workloads)
- [Unary gRPC (H2 TLS)](#unary-grpc-h2-tls)
- [Unary gRPC (H2 TLS → h2c)](#unary-grpc-h2-tls--h2c)
- [WebSocket (H1 TLS → H1 TLS)](#websocket-h1-tls--h1-tls)
- [WebSocket (H2 TLS 8441 → H1)](#websocket-h2-tls-8441--h1)
- [Other measurements](#other-measurements)
- [Raising limits on large hosts](#raising-limits-on-large-hosts)
- [Maintainer notes](#maintainer-notes)

## Measurement environment

All three OS use public-repo GitHub-hosted runners: **Windows / Linux** at **4 vCPU / 16 GiB / 14 GB SSD**, **macOS** at **`macos-15` Apple Silicon (M1) 3-core / 7 GB** (pinned, not `macos-latest`). macOS is Apple Silicon because that is what Mac users run today; the runner sizes differ, which is fine because we compare products within an OS. Same harness knobs: warmup 2s / measure 8s; concurrency 8, 16, 32, 64; median of 3 repeats. Prefer Titanium÷YARP / Titanium÷nginx ratios over absolute RPS.

Laptop High-perf / cool-paired Windows numbers live on [Performance Local Lab](Performance-Local-Lab). Do not mix those absolutes into the tables below.

### Windows (GitHub-hosted `windows-latest`)

|||
|---|---|
| OS | Windows Server (GitHub-hosted `windows-latest`) |
| CPU | **4** logical processors |
| RAM | **16** GiB |
| Runtime | .NET 10.0.x |
| nginx | nginx/Windows **1.31.3** (same-OS only; no QUIC) |
| HAProxy | *Not possible* on Windows (no official port) |
| Envoy | *Not possible* on Windows (upstream discontinued Windows builds) |
| YARP | Yarp.ReverseProxy **2.3.0** |
| Harness | RpsLoadProbe Release; median of 3 repeats |

### Linux (GitHub-hosted `ubuntu-latest`)

|||
|---|---|
| OS | Ubuntu 24.04.x LTS |
| CPU | **4** logical processors (AMD EPYC; runners this pass were 7763 / 9V74) |
| RAM | **16** GiB |
| Runtime | .NET 10.0.11 |
| nginx | nginx/**1.31.4** (nginx.org mainline, `--with-http_v3_module`) |
| HAProxy | HAProxy **3.2.23** built with `USE_QUIC` (GHA; Ubuntu distro 2.8 is not QUIC-capable) |
| Envoy | pinned GitHub release static binary **1.36.7** (HTTP/3 compiled in) |
| YARP | Yarp.ReverseProxy **2.3.0** |
| Harness | RpsLoadProbe Release; median of 3 repeats where noted |

### macOS (GitHub-hosted `macos-15`, Apple Silicon)

|||
|---|---|
| OS | macOS 15 (GitHub-hosted `macos-15`, Apple Silicon arm64) |
| CPU | **3** logical processors (Apple M1) |
| RAM | **7** GB |
| Runtime | .NET 10.0.x |
| nginx | Homebrew nginx with `--with-http_v3_module` (workflow fails if missing) |
| HAProxy | Homebrew `haproxy` with `USE_QUIC` (workflow fails if missing; 3.2.23 osx source fallback) |
| Envoy | Homebrew bottle when present; else pinned darwin-arm64 **1.36.7** (official GitHub assets are Linux-only). HTTP/3 compiled in. |
| MsQuic | Homebrew `libmsquic` + `openssl@3` on `DYLD_LIBRARY_PATH` / `DYLD_FALLBACK_LIBRARY_PATH` (`QuicListener.IsSupported`) |
| YARP | Yarp.ReverseProxy **2.3.0** |
| Harness | RpsLoadProbe Release; median of 3 repeats where noted |

Use the pinned `macos-15` label (not `macos-latest`) so the image does not change between runs.

### Saturation control

Calibration for the shared 4 vCPU loopback shape: how close client + origin are to saturated before ranking reverse peers. Tiny keep-alive GET. Blocks A and B: median of **3** repeats @ `41f4adee` — [36853300134](https://github.com/justcoding121/titanium-web-proxy/actions/runs/36853300134). Block C is a later re-measure (see that heading). Warmup 2s / measure 8s; concurrency 8, 16, 32, 64. Block A **% of origin-HttpClient** uses median **peak** RPS. Blocks B/C use peer÷YARP / ÷nginx on median peak (not % of H1 origin). **RPS cells** embed median RSS / CPU for the **proxy child** plus its **full descendant tree** (serve-proxy → nginx master → workers); origin-direct samples the **origin** child. Product matrices below use matched `dotnet-httpclient` only (not bombardier).


#### Block A — H1 plain

| Arm | Generator | Notes |
|---|---|---|
| `origin-direct` | `dotnet-httpclient` | No proxy — origin ceiling under the same client used for publishable ratios |
| `origin-direct-bombardier` | `bombardier` | External client check (CI installs bombardier) |
| `bare-reverse-http1` | `dotnet-httpclient` | Thin C# H1 reverse (`BareHttp1ReverseProxy`) — .NET runtime / loopback ceiling for a three-process reverse hop; **not** a product peer |
| `nginx-reverse-http1` / `yarp-reverse-http1` / `twp-reverse-http1` | `dotnet-httpclient` | Product peers (medals among these three only) |

**Windows** (`windows-latest`)

| Arm | Generator | RPS | % of origin-HttpClient |
|---|---|---:|---:|
| origin-direct | dotnet-httpclient | **50,668**<br><sub>(55 MiB / 41.5% CPU)</sub> | **100.0%** |
| origin-direct-bombardier | bombardier | **39,281**<br><sub>(56 MiB / 22.5% CPU)</sub> | **77.5%** |
| bare-reverse-http1 | dotnet-httpclient | **25,315**<br><sub>(60 MiB / 47.6% CPU)</sub> | **50.0%** |
| nginx-reverse-http1 | dotnet-httpclient | **13,599**<br><sub>(125 MiB / 24.9% CPU)</sub> | **26.8%** |
| yarp-reverse-http1 | dotnet-httpclient | **21,373**<br><sub>(88 MiB / 49.9% CPU)</sub> | **42.2%** |
| twp-reverse-http1 | dotnet-httpclient | 🥇 **25,546**<br><sub>(73 MiB / 48.4% CPU)</sub> | **50.4%** |

**Linux** (`ubuntu-latest`)

| Arm | Generator | RPS | % of origin-HttpClient |
|---|---|---:|---:|
| origin-direct | dotnet-httpclient | **67,804**<br><sub>(80 MiB / 41.9% CPU)</sub> | **100.0%** |
| origin-direct-bombardier | bombardier | **40,543**<br><sub>(79 MiB / 34.5% CPU)</sub> | **59.8%** |
| bare-reverse-http1 | dotnet-httpclient | **31,377**<br><sub>(69 MiB / 44.2% CPU)</sub> | **46.3%** |
| nginx-reverse-http1 | dotnet-httpclient | 🥇 **38,409**<br><sub>(75 MiB / 41.2% CPU)</sub> | **56.6%** |
| yarp-reverse-http1 | dotnet-httpclient | **27,147**<br><sub>(114 MiB / 50.6% CPU)</sub> | **40.0%** |
| twp-reverse-http1 | dotnet-httpclient | **31,727**<br><sub>(85 MiB / 49.7% CPU)</sub> | **46.8%** |

Reverse peers are about **51–48%** of the origin-direct HttpClient peak on this runner class (Win TWP **51.2%**, Lin TWP **47.9%**). Prefer the **%** column over absolute RPS across runs. Bare and origin-direct are controls (not medal peers).

#### Block B — H2 TLS→H1

Peer ratios (÷YARP / ÷nginx) on median peak; **RPS cells** embed `(MiB / CPU%)`.

**Windows** (`windows-latest`)

| Arm | Generator | RPS | ÷YARP | ÷nginx |
|---|---|---:|---:|---:|
| nginx-reverse-http2 | dotnet-httpclient | **7,915**<br><sub>(142 MiB / 24.7% CPU)</sub> | **0.27×** | **1.00×** |
| yarp-reverse-http2 | dotnet-httpclient | **28,934**<br><sub>(95 MiB / 53.9% CPU)</sub> | **1.00×** | **3.66×** |
| twp-reverse-http2-cleartext | dotnet-httpclient | 🥇 **35,415**<br><sub>(95 MiB / 50.6% CPU)</sub> | **1.22×** | **4.47×** |

**Linux** (`ubuntu-latest`)

| Arm | Generator | RPS | ÷YARP | ÷nginx |
|---|---|---:|---:|---:|
| nginx-reverse-http2 | dotnet-httpclient | **14,622**<br><sub>(100 MiB / 19.3% CPU)</sub> | **0.51×** | **1.00×** |
| yarp-reverse-http2 | dotnet-httpclient | **28,734**<br><sub>(122 MiB / 50.0% CPU)</sub> | **1.00×** | **1.97×** |
| twp-reverse-http2-cleartext | dotnet-httpclient | 🥇 **33,677**<br><sub>(116 MiB / 51.1% CPU)</sub> | **1.17×** | **2.30×** |

#### Block C — H3→H1

Same layout as Block B. Requires QuicListener. nginx needs `http_v3_module` (Windows nginx has no QUIC). HAProxy needs `USE_QUIC` (GHA Linux/macOS require it). Envoy 1.20+ includes HTTP/3.

Re-measured @ `f2061c27` after the per-stream HTTP/3 write scratch — Windows [37005020668](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37005020668), Linux [37005016067](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37005016067). Medals are still nginx / YARP / Titanium only. On the Linux run HAProxy was **22,785** (86 MiB / 27.1% CPU) and Envoy **3,669** (134 MiB / 24.3% CPU); HAProxy is still ahead of Titanium. The TLS-origin twin is not in this table (the 5×5 below stays @ `41f4adee`); that re-measure is in [Performance Profiling](Performance-Profiling#linux-h2h3ws-gap-close--baseline-2026-10-02). macOS was not re-measured.

**Windows** (`windows-latest`)

| Arm | Generator | RPS | ÷YARP | ÷nginx |
|---|---|---:|---:|---:|
| nginx-reverse-http3-cleartext | dotnet-httpclient | *Not possible (no QUIC)* | — | — |
| yarp-reverse-http3-cleartext | dotnet-httpclient | **14,729**<br><sub>(141 MiB / 50.6% CPU)</sub> | **1.00×** | — |
| twp-reverse-http3-cleartext | dotnet-httpclient | 🥇 **15,068**<br><sub>(104 MiB / 45.2% CPU)</sub> | **1.02×** | — |

**Linux** (`ubuntu-latest`)

| Arm | Generator | RPS | ÷YARP | ÷nginx |
|---|---|---:|---:|---:|
| nginx-reverse-http3-cleartext | dotnet-httpclient | **0**<br><sub>(peak 15,485 · 106 MiB / 22.3% CPU)</sub> | **0.84×** | **1.00×** |
| yarp-reverse-http3-cleartext | dotnet-httpclient | **18,511**<br><sub>(183 MiB / 49.5% CPU)</sub> | **1.00×** | **1.20×** |
| twp-reverse-http3-cleartext | dotnet-httpclient | 🥇 **20,833**<br><sub>(142 MiB / 49.8% CPU)</sub> | **1.13×** | **1.35×** |

## Windows — Titanium vs nginx vs HAProxy vs Envoy vs YARP

Client / origin: HTTP version and whether TLS is used (`plain` = cleartext, `TLS` = encrypted, `QUIC` = HTTP/3).

### Reverse

Median of **3 repeats** on `windows-latest` (4 vCPU / 16 GiB). Bare reverse 5×5 @ `41f4adee` — `compare-product` [36853254836](https://github.com/justcoding121/titanium-web-proxy/actions/runs/36853254836). Warmup 2s / measure 8s; concurrency 8, 16, 32, 64. Prefer TWP÷peer ratios over absolute RPS. **RPS cells** include median RSS / CPU at the peak-RPS step as `<br><sub>(MiB / CPU%)</sub>`. nginx terminate peers use `keepalive 256` + streaming buffers. **HAProxy / Envoy are Linux-only peers** (no official Windows port). Laptop High-perf / cool-paired numbers stay on the [local lab](Performance-Local-Lab). Product 5×5 is **~56-byte JSON keep-alive GET**; H2/H3 same-protocol cells are mostly header work with a tiny body (Titanium best case) — see [Why this comparison is fair](#why-this-comparison-is-fair).

**Load generators:** Reverse inbound H3 arms use **`dotnet-httpclient`** (`http_version=3.0`, `RequestVersionExact`). nginx/Windows is same-OS only (no QUIC). HAProxy/Envoy are Linux-only terminate peers.

| Client | Origin | TWP | nginx | YARP |
|---|---|---:|---:|---:|
| HTTP/1 · plain | HTTP/1 · plain | 🥇 **25,880**<br><sub>(73 MiB / 47.6% CPU)</sub> | **14,026**<br><sub>(125 MiB / 24.8% CPU)</sub> | **21,496**<br><sub>(89 MiB / 49.4% CPU)</sub> |
| HTTP/1 · plain | HTTP/1 · TLS | 🥇 **21,172**<br><sub>(88 MiB / 52.1% CPU)</sub> | **8,314**<br><sub>(135 MiB / 24.9% CPU)</sub> | **19,155**<br><sub>(98 MiB / 50.8% CPU)</sub> |
| HTTP/1 · plain | HTTP/2 · plain | 🥇 **48,182**<br><sub>(110 MiB / 47.8% CPU)</sub> | *Not possible (no H2 upstream)* | **42,621**<br><sub>(93 MiB / 49.2% CPU)</sub> |
| HTTP/1 · plain | HTTP/2 · TLS | 🥇 **32,909**<br><sub>(118 MiB / 45.1% CPU)</sub> | *Not possible (no H2 upstream)* | **30,006**<br><sub>(96 MiB / 46.6% CPU)</sub> |
| HTTP/1 · plain | HTTP/3 · QUIC | 🥇 **18,481**<br><sub>(106 MiB / 50.6% CPU)</sub> | *Not possible (no H3 upstream)* | **17,986**<br><sub>(120 MiB / 51.9% CPU)</sub> |
| HTTP/1 · TLS | HTTP/1 · plain | 🥇 **28,020**<br><sub>(87 MiB / 48.7% CPU)</sub> | **13,783**<br><sub>(142 MiB / 24.6% CPU)</sub> | **24,447**<br><sub>(101 MiB / 48.5% CPU)</sub> |
| HTTP/1 · TLS | HTTP/1 · TLS | 🥇 **19,084**<br><sub>(88 MiB / 48.2% CPU)</sub> | **6,979**<br><sub>(143 MiB / 24.6% CPU)</sub> | **17,016**<br><sub>(103 MiB / 46.6% CPU)</sub> |
| HTTP/1 · TLS | HTTP/2 · plain | 🥇 **28,276**<br><sub>(111 MiB / 45.6% CPU)</sub> | *Not possible (no H2 upstream)* | **26,212**<br><sub>(112 MiB / 46.9% CPU)</sub> |
| HTTP/1 · TLS | HTTP/2 · TLS | 🥇 **33,428**<br><sub>(115 MiB / 43.8% CPU)</sub> | *Not possible (no H2 upstream)* | **31,451**<br><sub>(108 MiB / 48% CPU)</sub> |
| HTTP/1 · TLS | HTTP/3 · QUIC | 🥇 **16,002**<br><sub>(109 MiB / 50.8% CPU)</sub> | *Not possible (no H3 upstream)* | **15,531**<br><sub>(126 MiB / 50.4% CPU)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | 🥇 **36,594**<br><sub>(86 MiB / 51.9% CPU)</sub> | **9,135**<br><sub>(127 MiB / 24.7% CPU)</sub> | **32,083**<br><sub>(86 MiB / 53.7% CPU)</sub> |
| HTTP/2 · plain | HTTP/1 · TLS | 🥇 **38,670**<br><sub>(99 MiB / 49.9% CPU)</sub> | **9,822**<br><sub>(137 MiB / 24.8% CPU)</sub> | **33,958**<br><sub>(92 MiB / 49.4% CPU)</sub> |
| HTTP/2 · plain | HTTP/2 · plain | 🥇 **109,500**<br><sub>(59 MiB / 29.9% CPU)</sub> | *Not possible (no H2 upstream)* | **65,061**<br><sub>(94 MiB / 48.2% CPU)</sub> |
| HTTP/2 · plain | HTTP/2 · TLS | 🥇 **86,785**<br><sub>(68 MiB / 28.5% CPU)</sub> | *Not possible (no H2 upstream)* | **51,561**<br><sub>(101 MiB / 44.1% CPU)</sub> |
| HTTP/2 · plain | HTTP/3 · QUIC | 🥇 **34,582**<br><sub>(127 MiB / 53.5% CPU)</sub> | *Not possible (no H3 upstream)* | **32,128**<br><sub>(127 MiB / 50.6% CPU)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **35,805**<br><sub>(96 MiB / 51.7% CPU)</sub> | **8,246**<br><sub>(141 MiB / 24.6% CPU)</sub> | **29,300**<br><sub>(95 MiB / 53.7% CPU)</sub> |
| HTTP/2 · TLS | HTTP/1 · TLS | 🥇 **30,622**<br><sub>(101 MiB / 52.4% CPU)</sub> | **6,318**<br><sub>(145 MiB / 24.7% CPU)</sub> | **25,261**<br><sub>(98 MiB / 52.6% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | 🥇 **95,497**<br><sub>(76 MiB / 28.9% CPU)</sub> | *Not possible (no H2 upstream)* | **58,094**<br><sub>(103 MiB / 50.6% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **83,912**<br><sub>(74 MiB / 29.6% CPU)</sub> | *Not possible (no H2 upstream)* | **49,700**<br><sub>(98 MiB / 49% CPU)</sub> |
| HTTP/2 · TLS | HTTP/3 · QUIC | 🥇 **30,854**<br><sub>(135 MiB / 53.3% CPU)</sub> | *Not possible (no H3 upstream)* | **26,509**<br><sub>(123 MiB / 53.3% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **20,480**<br><sub>(108 MiB / 43% CPU)</sub> | *Not possible (no QUIC)* | **18,402**<br><sub>(146 MiB / 50.5% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **13,870**<br><sub>(110 MiB / 45.2% CPU)</sub> | *Not possible (no QUIC)* | **13,299**<br><sub>(146 MiB / 49.3% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/2 · plain | 🥇 **34,720**<br><sub>(126 MiB / 47.6% CPU)</sub> | *Not possible (no H2 upstream)* | **23,072**<br><sub>(151 MiB / 49.6% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | 🥇 **34,810**<br><sub>(137 MiB / 47% CPU)</sub> | *Not possible (no H2 upstream)* | **21,949**<br><sub>(150 MiB / 47% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/3 · QUIC | 🥇 **16,836**<br><sub>(121 MiB / 40.3% CPU)</sub> | *Not possible (no H3 upstream)* | **12,462**<br><sub>(156 MiB / 47.8% CPU)</sub> |

### MITM (TWP only)

Same Client×Origin wires with interception on (`compare-product` [36853254836](https://github.com/justcoding121/titanium-web-proxy/actions/runs/36853254836)). **Lite** = no-op handlers (unchanged-lite finish). **Full** = append-only header mutation (harness: one probe header each way; product: generic append-only relay via `MitmCompressedRelayHelper`). nginx/HAProxy/Envoy/YARP cannot MITM. **Lite÷Reverse** / **Full÷Reverse** vs bare reverse (**same job / comparison-group shard**). Completion gate: Lite ≥ **0.40×** and Full ≥ **0.40×** reverse sustain @ c=64 (median of 3 GHA runs); reverse TWP÷YARP ≥ **0.60×** (no terminate-peer gate).

**v1 append-only relay (2026-08-27):** Pre-fix H2→H2 Full÷Reverse was **0.13–0.16×** ([32960766249](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32960766249)). Post-fix @ `df172718`: H2 plain→H2 plain Full **0.77–0.79×**, H3→H1 Full **0.91–0.93×**, all MITM arms ≥ **0.70×** on median of [33041445371](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33041445371), [33055267086](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33055267086), [33055272140](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33055272140).

**v2 drop-only + non-unique append (2026-08-27):** `MitmStaticRebuildHelper` rebuilds static HPACK/QPACK after 1–4 unique header drops; trailing non-unique appends stay on compressed relay. @ `41f4adee`: all MITM arms ≥ **0.70×** on GHA median ([33087088466](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087088466), [33087091622](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087091622), [33105885748](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33105885748) Linux; [33087085235](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087085235), [33087088466](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087088466), [33087091622](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087091622) Windows). H2 plain→H2 plain Full **0.77–0.79×** (Win) / **0.78×** (Lin).

| Client | Origin | Lite sustain | Full sustain | Lite÷Reverse | Full÷Reverse |
|---|---|---:|---:|---:|---:|
| HTTP/1 · plain | HTTP/1 · plain | **25,573**<br><sub>(77 MiB / 48.5% CPU)</sub> | **25,028**<br><sub>(78 MiB / 48.6% CPU)</sub> | **0.99×** | **0.97×** |
| HTTP/1 · plain | HTTP/1 · TLS | **21,034**<br><sub>(90 MiB / 51.8% CPU)</sub> | **20,798**<br><sub>(93 MiB / 51.2% CPU)</sub> | **0.99×** | **0.98×** |
| HTTP/1 · plain | HTTP/2 · plain | **40,940**<br><sub>(102 MiB / 47.9% CPU)</sub> | **46,410**<br><sub>(114 MiB / 50.1% CPU)</sub> | **0.85×** | **0.96×** |
| HTTP/1 · plain | HTTP/2 · TLS | **32,079**<br><sub>(119 MiB / 47.2% CPU)</sub> | **31,772**<br><sub>(121 MiB / 44.2% CPU)</sub> | **0.97×** | **0.97×** |
| HTTP/1 · plain | HTTP/3 · QUIC | **18,244**<br><sub>(107 MiB / 52.9% CPU)</sub> | **17,950**<br><sub>(107 MiB / 51.5% CPU)</sub> | **0.99×** | **0.97×** |
| HTTP/1 · TLS | HTTP/1 · plain | **28,020**<br><sub>(93 MiB / 50.2% CPU)</sub> | **27,368**<br><sub>(88 MiB / 46.1% CPU)</sub> | **1×** | **0.98×** |
| HTTP/1 · TLS | HTTP/1 · TLS | **19,116**<br><sub>(91 MiB / 50.8% CPU)</sub> | **18,790**<br><sub>(92 MiB / 46.7% CPU)</sub> | **1×** | **0.98×** |
| HTTP/1 · TLS | HTTP/2 · plain | **28,104**<br><sub>(117 MiB / 45.4% CPU)</sub> | **27,256**<br><sub>(113 MiB / 46.4% CPU)</sub> | **0.99×** | **0.96×** |
| HTTP/1 · TLS | HTTP/2 · TLS | **33,145**<br><sub>(121 MiB / 46.7% CPU)</sub> | **32,502**<br><sub>(118 MiB / 46.2% CPU)</sub> | **0.99×** | **0.97×** |
| HTTP/1 · TLS | HTTP/3 · QUIC | **15,961**<br><sub>(111 MiB / 49.4% CPU)</sub> | **15,795**<br><sub>(116 MiB / 49.6% CPU)</sub> | **1×** | **0.99×** |
| HTTP/2 · plain | HTTP/1 · plain | **35,159**<br><sub>(96 MiB / 55.4% CPU)</sub> | **34,094**<br><sub>(96 MiB / 54.9% CPU)</sub> | **0.96×** | **0.93×** |
| HTTP/2 · plain | HTTP/1 · TLS | **36,635**<br><sub>(105 MiB / 51.8% CPU)</sub> | **36,515**<br><sub>(101 MiB / 49.4% CPU)</sub> | **0.95×** | **0.94×** |
| HTTP/2 · plain | HTTP/2 · plain | **84,445**<br><sub>(71 MiB / 40.3% CPU)</sub> | **80,594**<br><sub>(67 MiB / 41.7% CPU)</sub> | **0.77×** | **0.74×** |
| HTTP/2 · plain | HTTP/2 · TLS | **71,243**<br><sub>(70 MiB / 37.3% CPU)</sub> | **67,741**<br><sub>(75 MiB / 40% CPU)</sub> | **0.82×** | **0.78×** |
| HTTP/2 · plain | HTTP/3 · QUIC | **34,577**<br><sub>(129 MiB / 53.4% CPU)</sub> | **33,498**<br><sub>(136 MiB / 55.7% CPU)</sub> | **1×** | **0.97×** |
| HTTP/2 · TLS | HTTP/1 · plain | **33,615**<br><sub>(98 MiB / 53.5% CPU)</sub> | **33,523**<br><sub>(98 MiB / 54.9% CPU)</sub> | **0.94×** | **0.94×** |
| HTTP/2 · TLS | HTTP/1 · TLS | **28,964**<br><sub>(102 MiB / 53.2% CPU)</sub> | **28,825**<br><sub>(101 MiB / 55.5% CPU)</sub> | **0.95×** | **0.94×** |
| HTTP/2 · TLS | HTTP/2 · plain | **85,236**<br><sub>(82 MiB / 39% CPU)</sub> | **82,696**<br><sub>(87 MiB / 40.9% CPU)</sub> | **0.89×** | **0.87×** |
| HTTP/2 · TLS | HTTP/2 · TLS | **68,466**<br><sub>(77 MiB / 38.5% CPU)</sub> | **65,341**<br><sub>(80 MiB / 40.8% CPU)</sub> | **0.82×** | **0.78×** |
| HTTP/2 · TLS | HTTP/3 · QUIC | **30,486**<br><sub>(124 MiB / 52.6% CPU)</sub> | **29,988**<br><sub>(137 MiB / 54.1% CPU)</sub> | **0.99×** | **0.97×** |
| HTTP/3 · QUIC | HTTP/1 · plain | **20,113**<br><sub>(119 MiB / 45.2% CPU)</sub> | **19,554**<br><sub>(117 MiB / 44.1% CPU)</sub> | **0.98×** | **0.95×** |
| HTTP/3 · QUIC | HTTP/1 · TLS | **13,347**<br><sub>(120 MiB / 46.8% CPU)</sub> | **12,596**<br><sub>(123 MiB / 45.8% CPU)</sub> | **0.96×** | **0.91×** |
| HTTP/3 · QUIC | HTTP/2 · plain | **33,868**<br><sub>(129 MiB / 46.6% CPU)</sub> | **32,694**<br><sub>(131 MiB / 46.4% CPU)</sub> | **0.98×** | **0.94×** |
| HTTP/3 · QUIC | HTTP/2 · TLS | **33,817**<br><sub>(142 MiB / 47.2% CPU)</sub> | **32,979**<br><sub>(138 MiB / 46.2% CPU)</sub> | **0.97×** | **0.95×** |
| HTTP/3 · QUIC | HTTP/3 · QUIC | **16,282**<br><sub>(116 MiB / 48.1% CPU)</sub> | **15,803**<br><sub>(118 MiB / 45.6% CPU)</sub> | **0.97×** | **0.94×** |

## Linux — Titanium vs nginx vs HAProxy vs Envoy vs YARP

### Reverse

Median of **3 repeats** on `ubuntu-latest` (4 vCPU / 16 GiB). Bare reverse 5×5 @ `41f4adee` — `compare-product` [36853254836](https://github.com/justcoding121/titanium-web-proxy/actions/runs/36853254836). Warmup 2s / measure 8s; concurrency 8, 16, 32, 64. **Linux nginx is the authoritative nginx baseline.** HAProxy (3.2 `USE_QUIC`) and Envoy (GitHub release, HTTP/3 compiled in) run on the same loopback shape as nginx/YARP. nginx terminate peers use `keepalive 256` + streaming buffers. The RPS workflow installs nginx.org mainline (`http_v3_module`), a QUIC-enabled HAProxy, Envoy, and `libmsquic`. Prefer ratios over absolute RPS. Product 5×5 is **~56-byte JSON keep-alive GET**; H2/H3 same-protocol cells are mostly header work with a tiny body (Titanium best case) — see [Why this comparison is fair](#why-this-comparison-is-fair).

| Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---:|---:|---:|---:|---:|
| HTTP/1 · plain | HTTP/1 · plain | **35,542**<br><sub>(85 MiB / 50% CPU)</sub> | 🥇 **44,228**<br><sub>(76 MiB / 40.2% CPU)</sub> | **41,021**<br><sub>(66 MiB / 41.7% CPU)</sub> | **25,019**<br><sub>(115 MiB / 58.7% CPU)</sub> | **32,144**<br><sub>(117 MiB / 49.1% CPU)</sub> |
| HTTP/1 · plain | HTTP/1 · TLS | **53,775**<br><sub>(106 MiB / 48.5% CPU)</sub> | 🥇 **63,560**<br><sub>(93 MiB / 41.2% CPU)</sub> | **60,644**<br><sub>(70 MiB / 43% CPU)</sub> | **47,353**<br><sub>(118 MiB / 52.7% CPU)</sub> | **50,568**<br><sub>(131 MiB / 47.6% CPU)</sub> |
| HTTP/1 · plain | HTTP/2 · plain | 🥇 **42,126**<br><sub>(124 MiB / 50.1% CPU)</sub> | *Not possible (no H2 upstream)* | **27,642**<br><sub>(66 MiB / 42.6% CPU)</sub> | **21,554**<br><sub>(116 MiB / 64.2% CPU)</sub> | **35,843**<br><sub>(123 MiB / 49.5% CPU)</sub> |
| HTTP/1 · plain | HTTP/2 · TLS | 🥇 **38,179**<br><sub>(142 MiB / 49.2% CPU)</sub> | *Not possible (no H2 upstream)* | **34,950**<br><sub>(66 MiB / 43.2% CPU)</sub> | **25,265**<br><sub>(117 MiB / 59.6% CPU)</sub> | **35,038**<br><sub>(131 MiB / 47.5% CPU)</sub> |
| HTTP/1 · plain | HTTP/3 · QUIC | 🥇 **50,848**<br><sub>(164 MiB / 54.4% CPU)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **29,583**<br><sub>(121 MiB / 48.8% CPU)</sub> | **43,839**<br><sub>(170 MiB / 47% CPU)</sub> |
| HTTP/1 · TLS | HTTP/1 · plain | **24,280**<br><sub>(107 MiB / 49.9% CPU)</sub> | 🥇 **28,463**<br><sub>(99 MiB / 41.4% CPU)</sub> | **27,842**<br><sub>(82 MiB / 43.4% CPU)</sub> | **17,181**<br><sub>(127 MiB / 58.9% CPU)</sub> | **20,677**<br><sub>(134 MiB / 51.2% CPU)</sub> |
| HTTP/1 · TLS | HTTP/1 · TLS | **24,232**<br><sub>(110 MiB / 48.3% CPU)</sub> | **27,604**<br><sub>(104 MiB / 41.2% CPU)</sub> | 🥇 **27,745**<br><sub>(83 MiB / 42.8% CPU)</sub> | **19,127**<br><sub>(127 MiB / 54.2% CPU)</sub> | **21,175**<br><sub>(142 MiB / 49.3% CPU)</sub> |
| HTTP/1 · TLS | HTTP/2 · plain | 🥇 **69,681**<br><sub>(151 MiB / 49.9% CPU)</sub> | *Not possible (no H2 upstream)* | **47,137**<br><sub>(84 MiB / 42.8% CPU)</sub> | **54,016**<br><sub>(126 MiB / 52.9% CPU)</sub> | **62,753**<br><sub>(142 MiB / 48.7% CPU)</sub> |
| HTTP/1 · TLS | HTTP/2 · TLS | 🥇 **25,328**<br><sub>(154 MiB / 47.6% CPU)</sub> | *Not possible (no H2 upstream)* | **23,716**<br><sub>(83 MiB / 43.3% CPU)</sub> | **17,369**<br><sub>(126 MiB / 58.4% CPU)</sub> | **22,175**<br><sub>(140 MiB / 48.9% CPU)</sub> |
| HTTP/1 · TLS | HTTP/3 · QUIC | 🥇 **21,737**<br><sub>(150 MiB / 53.2% CPU)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **11,174**<br><sub>(130 MiB / 55.1% CPU)</sub> | **19,611**<br><sub>(160 MiB / 49.6% CPU)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | 🥇 **77,078**<br><sub>(120 MiB / 52.3% CPU)</sub> | **29,472**<br><sub>(79 MiB / 18.5% CPU)</sub> | **46,455**<br><sub>(67 MiB / 24.4% CPU)</sub> | **33,118**<br><sub>(118 MiB / 22.9% CPU)</sub> | **70,854**<br><sub>(112 MiB / 47.4% CPU)</sub> |
| HTTP/2 · plain | HTTP/1 · TLS | 🥇 **29,364**<br><sub>(125 MiB / 50.8% CPU)</sub> | **13,245**<br><sub>(101 MiB / 19.5% CPU)</sub> | **18,930**<br><sub>(71 MiB / 24.6% CPU)</sub> | **13,147**<br><sub>(118 MiB / 23.5% CPU)</sub> | **26,309**<br><sub>(128 MiB / 50.9% CPU)</sub> |
| HTTP/2 · plain | HTTP/2 · plain | 🥇 **95,132**<br><sub>(90 MiB / 38% CPU)</sub> | *Not possible (no H2 upstream)* | **29,728**<br><sub>(66 MiB / 24.3% CPU)</sub> | **24,027**<br><sub>(115 MiB / 21.6% CPU)</sub> | **54,791**<br><sub>(132 MiB / 47% CPU)</sub> |
| HTTP/2 · plain | HTTP/2 · TLS | 🥇 **130,965**<br><sub>(88 MiB / 41.3% CPU)</sub> | *Not possible (no H2 upstream)* | **42,285**<br><sub>(68 MiB / 24.5% CPU)</sub> | **41,750**<br><sub>(118 MiB / 20.9% CPU)</sub> | **88,980**<br><sub>(132 MiB / 45% CPU)</sub> |
| HTTP/2 · plain | HTTP/3 · QUIC | 🥇 **28,674**<br><sub>(144 MiB / 50.3% CPU)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **4,339**<br><sub>(122 MiB / 25% CPU)</sub> | **26,529**<br><sub>(162 MiB / 46.4% CPU)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **39,560**<br><sub>(128 MiB / 51.7% CPU)</sub> | **17,620**<br><sub>(100 MiB / 19% CPU)</sub> | **24,964**<br><sub>(81 MiB / 24.3% CPU)</sub> | **17,152**<br><sub>(129 MiB / 22.7% CPU)</sub> | **33,979**<br><sub>(122 MiB / 48.6% CPU)</sub> |
| HTTP/2 · TLS | HTTP/1 · TLS | 🥇 **59,236**<br><sub>(133 MiB / 49.1% CPU)</sub> | **27,000**<br><sub>(112 MiB / 19.1% CPU)</sub> | **38,045**<br><sub>(86 MiB / 24.6% CPU)</sub> | **30,525**<br><sub>(128 MiB / 22.6% CPU)</sub> | **54,530**<br><sub>(126 MiB / 46.9% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | 🥇 **73,087**<br><sub>(95 MiB / 35.9% CPU)</sub> | *Not possible (no H2 upstream)* | **21,291**<br><sub>(82 MiB / 24.4% CPU)</sub> | **17,672**<br><sub>(125 MiB / 22.1% CPU)</sub> | **40,036**<br><sub>(127 MiB / 47.6% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **61,583**<br><sub>(100 MiB / 35.4% CPU)</sub> | *Not possible (no H2 upstream)* | **23,639**<br><sub>(83 MiB / 24.2% CPU)</sub> | **19,934**<br><sub>(127 MiB / 20.6% CPU)</sub> | **38,518**<br><sub>(127 MiB / 45.6% CPU)</sub> |
| HTTP/2 · TLS | HTTP/3 · QUIC | 🥇 **54,162**<br><sub>(190 MiB / 52.7% CPU)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **16,762**<br><sub>(129 MiB / 24.7% CPU)</sub> | **47,112**<br><sub>(176 MiB / 44.5% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | **21,551**<br><sub>(145 MiB / 50% CPU)</sub> | **0**<br><sub>(peak 15,107 · 107 MiB / 22.6% CPU)</sub> | 🥇 **24,083**<br><sub>(86 MiB / 26.1% CPU)</sub> | **3,989**<br><sub>(132 MiB / 24.5% CPU)</sub> | **18,996**<br><sub>(186 MiB / 50.3% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | **19,461**<br><sub>(163 MiB / 48.5% CPU)</sub> | **0**<br><sub>(peak 14,824 · 117 MiB / 22.7% CPU)</sub> | 🥇 **21,322**<br><sub>(90 MiB / 28.9% CPU)</sub> | **4,755**<br><sub>(136 MiB / 24.3% CPU)</sub> | **18,172**<br><sub>(199 MiB / 49.7% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/2 · plain | 🥇 **60,482**<br><sub>(201 MiB / 55.6% CPU)</sub> | *Not possible (no H2 upstream)* | **54,740**<br><sub>(88 MiB / 25.2% CPU)</sub> | **20**<br><sub>(130 MiB / 0.1% CPU)</sub> | **51,309**<br><sub>(226 MiB / 47.2% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | 🥇 **25,276**<br><sub>(155 MiB / 50.1% CPU)</sub> | *Not possible (no H2 upstream)* | **21,371**<br><sub>(86 MiB / 26.5% CPU)</sub> | **6**<br><sub>(129 MiB / 0.1% CPU)</sub> | **21,601**<br><sub>(193 MiB / 48% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/3 · QUIC | 🥇 **22,007**<br><sub>(157 MiB / 47.7% CPU)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **2,856**<br><sub>(130 MiB / 24.9% CPU)</sub> | **17,736**<br><sub>(207 MiB / 46.7% CPU)</sub> |

### MITM (TWP only)

Same Client×Origin wires with interception on (`compare-product` [36853254836](https://github.com/justcoding121/titanium-web-proxy/actions/runs/36853254836)). **Lite** = no-op handlers (unchanged-lite finish). **Full** = append-only header mutation (harness: one probe header each way; product: generic append-only relay via `MitmCompressedRelayHelper`). nginx/HAProxy/Envoy/YARP cannot MITM. **Lite÷Reverse** / **Full÷Reverse** vs bare reverse (**same job / comparison-group shard**). Completion gate: Lite ≥ **0.40×** and Full ≥ **0.40×** reverse sustain @ c=64 (median of 3 GHA runs); reverse TWP÷YARP ≥ **0.60×** (no terminate-peer gate).

**v1 append-only relay (2026-08-27):** Pre-fix H2→H2 Full÷Reverse was **0.13–0.16×** ([32960766249](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32960766249)). Post-fix @ `df172718`: H2 plain→H2 plain Full **0.77–0.79×**, H3→H1 Full **0.91–0.93×**, all MITM arms ≥ **0.70×** on median of [33041445371](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33041445371), [33055267086](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33055267086), [33055272140](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33055272140).

**v2 drop-only + non-unique append (2026-08-27):** `MitmStaticRebuildHelper` rebuilds static HPACK/QPACK after 1–4 unique header drops; trailing non-unique appends stay on compressed relay. @ `41f4adee`: all MITM arms ≥ **0.70×** on GHA median ([33087088466](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087088466), [33087091622](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087091622), [33105885748](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33105885748) Linux; [33087085235](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087085235), [33087088466](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087088466), [33087091622](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087091622) Windows). H2 plain→H2 plain Full **0.77–0.79×** (Win) / **0.78×** (Lin).

| Client | Origin | Lite sustain | Full sustain | Lite÷Reverse | Full÷Reverse |
|---|---|---:|---:|---:|---:|
| HTTP/1 · plain | HTTP/1 · plain | **36,120**<br><sub>(91 MiB / 49.8% CPU)</sub> | **35,316**<br><sub>(89 MiB / 48.6% CPU)</sub> | **1.02×** | **0.99×** |
| HTTP/1 · plain | HTTP/1 · TLS | **54,813**<br><sub>(111 MiB / 48.3% CPU)</sub> | **53,246**<br><sub>(110 MiB / 48.4% CPU)</sub> | **1.02×** | **0.99×** |
| HTTP/1 · plain | HTTP/2 · plain | **40,743**<br><sub>(131 MiB / 53% CPU)</sub> | **39,266**<br><sub>(127 MiB / 53.2% CPU)</sub> | **0.97×** | **0.93×** |
| HTTP/1 · plain | HTTP/2 · TLS | **37,310**<br><sub>(144 MiB / 52.2% CPU)</sub> | **36,668**<br><sub>(152 MiB / 50.5% CPU)</sub> | **0.98×** | **0.96×** |
| HTTP/1 · plain | HTTP/3 · QUIC | **49,696**<br><sub>(166 MiB / 56% CPU)</sub> | **49,175**<br><sub>(164 MiB / 55.2% CPU)</sub> | **0.98×** | **0.97×** |
| HTTP/1 · TLS | HTTP/1 · plain | **23,938**<br><sub>(113 MiB / 49.9% CPU)</sub> | **23,457**<br><sub>(115 MiB / 50% CPU)</sub> | **0.99×** | **0.97×** |
| HTTP/1 · TLS | HTTP/1 · TLS | **24,270**<br><sub>(112 MiB / 48.4% CPU)</sub> | **24,135**<br><sub>(112 MiB / 48% CPU)</sub> | **1×** | **1×** |
| HTTP/1 · TLS | HTTP/2 · plain | **67,032**<br><sub>(166 MiB / 50.1% CPU)</sub> | **65,781**<br><sub>(158 MiB / 51% CPU)</sub> | **0.96×** | **0.94×** |
| HTTP/1 · TLS | HTTP/2 · TLS | **23,602**<br><sub>(163 MiB / 49.4% CPU)</sub> | **23,843**<br><sub>(170 MiB / 48.6% CPU)</sub> | **0.93×** | **0.94×** |
| HTTP/1 · TLS | HTTP/3 · QUIC | **21,552**<br><sub>(151 MiB / 53% CPU)</sub> | **20,790**<br><sub>(156 MiB / 53.9% CPU)</sub> | **0.99×** | **0.96×** |
| HTTP/2 · plain | HTTP/1 · plain | **76,412**<br><sub>(124 MiB / 52.9% CPU)</sub> | **73,763**<br><sub>(120 MiB / 53% CPU)</sub> | **0.99×** | **0.96×** |
| HTTP/2 · plain | HTTP/1 · TLS | **28,157**<br><sub>(124 MiB / 52% CPU)</sub> | **27,650**<br><sub>(129 MiB / 52.1% CPU)</sub> | **0.96×** | **0.94×** |
| HTTP/2 · plain | HTTP/2 · plain | **71,938**<br><sub>(94 MiB / 43.2% CPU)</sub> | **66,304**<br><sub>(95 MiB / 42.9% CPU)</sub> | **0.76×** | **0.7×** |
| HTTP/2 · plain | HTTP/2 · TLS | **111,003**<br><sub>(103 MiB / 44.1% CPU)</sub> | **108,771**<br><sub>(106 MiB / 41.6% CPU)</sub> | **0.85×** | **0.83×** |
| HTTP/2 · plain | HTTP/3 · QUIC | **28,202**<br><sub>(145 MiB / 50.3% CPU)</sub> | **27,788**<br><sub>(144 MiB / 50.5% CPU)</sub> | **0.98×** | **0.97×** |
| HTTP/2 · TLS | HTTP/1 · plain | **37,310**<br><sub>(124 MiB / 52.7% CPU)</sub> | **37,209**<br><sub>(127 MiB / 52.4% CPU)</sub> | **0.94×** | **0.94×** |
| HTTP/2 · TLS | HTTP/1 · TLS | **57,688**<br><sub>(133 MiB / 49.9% CPU)</sub> | **55,972**<br><sub>(134 MiB / 49.4% CPU)</sub> | **0.97×** | **0.94×** |
| HTTP/2 · TLS | HTTP/2 · plain | **56,665**<br><sub>(102 MiB / 39.9% CPU)</sub> | **53,179**<br><sub>(111 MiB / 40.9% CPU)</sub> | **0.78×** | **0.73×** |
| HTTP/2 · TLS | HTTP/2 · TLS | **51,615**<br><sub>(111 MiB / 40.1% CPU)</sub> | **48,871**<br><sub>(113 MiB / 39.6% CPU)</sub> | **0.84×** | **0.79×** |
| HTTP/2 · TLS | HTTP/3 · QUIC | **52,800**<br><sub>(185 MiB / 51.8% CPU)</sub> | **51,956**<br><sub>(189 MiB / 52.1% CPU)</sub> | **0.97×** | **0.96×** |
| HTTP/3 · QUIC | HTTP/1 · plain | **20,377**<br><sub>(143 MiB / 50.5% CPU)</sub> | **20,027**<br><sub>(140 MiB / 50.4% CPU)</sub> | **0.95×** | **0.93×** |
| HTTP/3 · QUIC | HTTP/1 · TLS | **18,610**<br><sub>(168 MiB / 48.9% CPU)</sub> | **18,240**<br><sub>(168 MiB / 48.8% CPU)</sub> | **0.96×** | **0.94×** |
| HTTP/3 · QUIC | HTTP/2 · plain | **58,870**<br><sub>(200 MiB / 57.1% CPU)</sub> | **59,642**<br><sub>(195 MiB / 56.5% CPU)</sub> | **0.97×** | **0.99×** |
| HTTP/3 · QUIC | HTTP/2 · TLS | **25,006**<br><sub>(155 MiB / 50.1% CPU)</sub> | **24,294**<br><sub>(158 MiB / 50.2% CPU)</sub> | **0.99×** | **0.96×** |
| HTTP/3 · QUIC | HTTP/3 · QUIC | **21,690**<br><sub>(163 MiB / 50.4% CPU)</sub> | **20,322**<br><sub>(161 MiB / 50.5% CPU)</sub> | **0.99×** | **0.92×** |

## macOS — Titanium vs nginx vs HAProxy vs Envoy vs YARP

### Reverse

**Pending re-measure** on `macos-15` (Apple Silicon M1, 3-core / 7 GB): cells below read *Not measured* until the Mac shards finish. Bare reverse 5×5 @ `41f4adee`. Warmup 2s / measure 8s; concurrency 8, 16, 32, 64. Prefer TWP÷peer ratios over absolute RPS. **RPS cells** include median RSS / CPU at the peak-RPS step as `<br><sub>(MiB / CPU%)</sub>`. The RPS workflow installs Homebrew nginx (`http_v3_module`), Homebrew HAProxy with `USE_QUIC` (3.2 source fallback), Envoy (Homebrew bottle or pinned darwin-arm64 1.36.7), Homebrew `libmsquic` (+ `DYLD_*`), and YARP. Use the pinned `macos-15` label, not `macos-latest`. Product 5×5 is **~56-byte JSON keep-alive GET**; H2/H3 same-protocol cells are mostly header work with a tiny body (Titanium best case) — see [Why this comparison is fair](#why-this-comparison-is-fair).

| Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---:|---:|---:|---:|---:|
| HTTP/1 · plain | HTTP/1 · plain | *Not measured* | *Not measured* | *Not measured* | *Not measured* | *Not measured* |
| HTTP/1 · plain | HTTP/1 · TLS | *Not measured* | *Not measured* | *Not measured* | *Not measured* | *Not measured* |
| HTTP/1 · plain | HTTP/2 · plain | *Not measured* | *Not possible (no H2 upstream)* | *Not measured* | *Not measured* | *Not measured* |
| HTTP/1 · plain | HTTP/2 · TLS | *Not measured* | *Not possible (no H2 upstream)* | *Not measured* | *Not measured* | *Not measured* |
| HTTP/1 · plain | HTTP/3 · QUIC | *Not measured* | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | *Not measured* | *Not measured* |
| HTTP/1 · TLS | HTTP/1 · plain | *Not measured* | *Not measured* | *Not measured* | *Not measured* | *Not measured* |
| HTTP/1 · TLS | HTTP/1 · TLS | *Not measured* | *Not measured* | *Not measured* | *Not measured* | *Not measured* |
| HTTP/1 · TLS | HTTP/2 · plain | *Not measured* | *Not possible (no H2 upstream)* | *Not measured* | *Not measured* | *Not measured* |
| HTTP/1 · TLS | HTTP/2 · TLS | *Not measured* | *Not possible (no H2 upstream)* | *Not measured* | *Not measured* | *Not measured* |
| HTTP/1 · TLS | HTTP/3 · QUIC | *Not measured* | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | *Not measured* | *Not measured* |
| HTTP/2 · plain | HTTP/1 · plain | *Not measured* | *Not measured* | *Not measured* | *Not measured* | *Not measured* |
| HTTP/2 · plain | HTTP/1 · TLS | *Not measured* | *Not measured* | *Not measured* | *Not measured* | *Not measured* |
| HTTP/2 · plain | HTTP/2 · plain | *Not measured* | *Not possible (no H2 upstream)* | *Not measured* | *Not measured* | *Not measured* |
| HTTP/2 · plain | HTTP/2 · TLS | *Not measured* | *Not possible (no H2 upstream)* | *Not measured* | *Not measured* | *Not measured* |
| HTTP/2 · plain | HTTP/3 · QUIC | *Not measured* | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | *Not measured* | *Not measured* |
| HTTP/2 · TLS | HTTP/1 · plain | *Not measured* | *Not measured* | *Not measured* | *Not measured* | *Not measured* |
| HTTP/2 · TLS | HTTP/1 · TLS | *Not measured* | *Not measured* | *Not measured* | *Not measured* | *Not measured* |
| HTTP/2 · TLS | HTTP/2 · plain | *Not measured* | *Not possible (no H2 upstream)* | *Not measured* | *Not measured* | *Not measured* |
| HTTP/2 · TLS | HTTP/2 · TLS | *Not measured* | *Not possible (no H2 upstream)* | *Not measured* | *Not measured* | *Not measured* |
| HTTP/2 · TLS | HTTP/3 · QUIC | *Not measured* | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | *Not measured* | *Not measured* |
| HTTP/3 · QUIC | HTTP/1 · plain | *Not measured* | *Not measured* | *Not measured* | *Not measured* | *Not measured* |
| HTTP/3 · QUIC | HTTP/1 · TLS | *Not measured* | *Not measured* | *Not measured* | *Not measured* | *Not measured* |
| HTTP/3 · QUIC | HTTP/2 · plain | *Not measured* | *Not possible (no H2 upstream)* | *Not measured* | *Not measured* | *Not measured* |
| HTTP/3 · QUIC | HTTP/2 · TLS | *Not measured* | *Not possible (no H2 upstream)* | *Not measured* | *Not measured* | *Not measured* |
| HTTP/3 · QUIC | HTTP/3 · QUIC | *Not measured* | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | *Not measured* | *Not measured* |

### MITM (TWP only)

Same Client×Origin wires with interception on (`compare-product` [36853254836](https://github.com/justcoding121/titanium-web-proxy/actions/runs/36853254836)). **Lite** = no-op handlers (unchanged-lite finish). **Full** = append-only header mutation (harness: one probe header each way; product: generic append-only relay via `MitmCompressedRelayHelper`). nginx/HAProxy/Envoy/YARP cannot MITM. **Lite÷Reverse** / **Full÷Reverse** vs bare reverse (**same job / comparison-group shard**). Completion gate: Lite ≥ **0.40×** and Full ≥ **0.40×** reverse sustain @ c=64 (median of 3 GHA runs); reverse TWP÷YARP ≥ **0.60×** (no terminate-peer gate).

**v1 append-only relay (2026-08-27):** Pre-fix H2→H2 Full÷Reverse was **0.13–0.16×** ([32960766249](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32960766249)). Post-fix @ `df172718`: H2 plain→H2 plain Full **0.77–0.79×**, H3→H1 Full **0.91–0.93×**, all MITM arms ≥ **0.70×** on median of [33041445371](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33041445371), [33055267086](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33055267086), [33055272140](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33055272140).

**v2 drop-only + non-unique append (2026-08-27):** `MitmStaticRebuildHelper` rebuilds static HPACK/QPACK after 1–4 unique header drops; trailing non-unique appends stay on compressed relay. @ `41f4adee`: all MITM arms ≥ **0.70×** on GHA median ([33087088466](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087088466), [33087091622](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087091622), [33105885748](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33105885748) Linux; [33087085235](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087085235), [33087088466](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087088466), [33087091622](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087091622) Windows). H2 plain→H2 plain Full **0.77–0.79×** (Win) / **0.78×** (Lin).

| Client | Origin | Lite sustain | Full sustain | Lite÷Reverse | Full÷Reverse |
|---|---|---:|---:|---:|---:|

## Editions (CLI / Plus / Intercept)

**Note:** `twp-reverse-http1` and other library rows use Core with **probe-tuned** settings (no logging, no Via header, probe-warmed certs). Edition rows use `titanium run -c twp.yaml` **product defaults** — prefer the ÷baseline ratio column over absolute RPS. Inspector GUI is not spawnable in the harness; session-path overhead is `twp-cli-intercept-http1` (route `RequestHeaderSet` transform). Pre-origin Plus middleware (CIDR/WAF/JWT/rate-limit) runs on H1 terminate-lite without `SessionEventArgs`; JWT caches successful bearer validations. Maintainer gate thresholds live under [Maintainer notes](#maintainer-notes).

Median of **3** repeats @ `41f4adee`. Source: Actions [36853305087](https://github.com/justcoding121/titanium-web-proxy/actions/runs/36853305087). Warmup 2s / measure 8s; concurrency 8–64; sustain = median peak RPS among SLO-pass steps @ **c=64**. **RPS cells** show sustain; `<sub>` holds peak (when higher) plus `(MiB / CPU%)`.

| Arm | Win | Linux | Win÷ | Lin÷ | Gate |
|---|---:|---:|---:|---:|---|
| `twp-cli-reverse-http1` vs library | **66,323**<br><sub>(131 MiB / 45.5% CPU)</sub> | **34,106**<br><sub>(166 MiB / 50.4% CPU)</sub> | **1.03×** | **1.08×** | ≥ **0.80×** |
| `twp-cli-reverse-http1-tls` vs library TLS | **56,976**<br><sub>(145 MiB / 45.7% CPU)</sub> | **24,583**<br><sub>(197 MiB / 50.3% CPU)</sub> | **1.00×** | **1.04×** | ≥ **0.80×** |
| `twp-cli-reverse-http1-route` vs CLI | **66,646**<br><sub>(135 MiB / 47.6% CPU)</sub> | **33,126**<br><sub>(169 MiB / 50.1% CPU)</sub> | **1.00×** | **0.97×** | ≥ **0.90×** |
| `twp-cli-plus-base-http1` vs CLI | **66,656**<br><sub>(137 MiB / 47.2% CPU)</sub> | **34,197**<br><sub>(170 MiB / 50.5% CPU)</sub> | **1.01×** | **1.00×** | ≥ **0.90×** |
| `twp-cli-plus-cache-http1` (cold) vs CLI | **58,304**<br><sub>(140 MiB / 50.0% CPU)</sub> | **28,354**<br><sub>(175 MiB / 54.0% CPU)</sub> | **0.88×** | **0.83×** | ≥ **0.70×** |
| `twp-cli-intercept-http1` vs CLI | **53,188**<br><sub>(138 MiB / 49.4% CPU)</sub> | **24,664**<br><sub>(176 MiB / 52.9% CPU)</sub> | **0.80×** | **0.72×** | ≥ **0.70×** |
| `twp-cli-plus-waf-http1` vs CLI | **63,745**<br><sub>(134 MiB / 45.3% CPU)</sub> | **32,738**<br><sub>(170 MiB / 51.2% CPU)</sub> | **0.96×** | **0.96×** | ≥ **0.80×** |
| `twp-cli-plus-cidr-http1` vs CLI | **64,199**<br><sub>(133 MiB / 47.6% CPU)</sub> | **32,903**<br><sub>(170 MiB / 51.0% CPU)</sub> | **0.97×** | **0.96×** | ≥ **0.80×** |
| `twp-cli-plus-jwt-http1` vs CLI | **61,258**<br><sub>(149 MiB / 45.8% CPU)</sub> | **30,232**<br><sub>(190 MiB / 50.7% CPU)</sub> | **0.92×** | **0.89×** | ≥ **0.70×** |
| `twp-cli-plus-ratelimit-http1` vs CLI | **63,961**<br><sub>(141 MiB / 47.0% CPU)</sub> | **32,384**<br><sub>(170 MiB / 51.2% CPU)</sub> | **0.96×** | **0.95×** | ≥ **0.80×** |
| `twp-cli-plus-resilience-http1` vs CLI | **65,742**<br><sub>(144 MiB / 48.0% CPU)</sub> | **33,765**<br><sub>(174 MiB / 50.6% CPU)</sub> | **0.99×** | **0.99×** | ≥ **0.85×** |
| `twp-cli-plus-discovery-file-http1` vs CLI | **65,441**<br><sub>(139 MiB / 47.0% CPU)</sub> | **34,153**<br><sub>(172 MiB / 50.6% CPU)</sub> | **0.99×** | **1.00×** | ≥ **0.80×** |
| `twp-cli-plus-metrics-scrape-http1` vs CLI | **65,919**<br><sub>(140 MiB / 46.8% CPU)</sub> | **33,848**<br><sub>(175 MiB / 50.3% CPU)</sub> | **0.99×** | **0.99×** | ≥ **0.80×** |
| `twp-cli-plus-cache-hit-http1` vs cache cold | **58,870**<br><sub>(147 MiB / 48.7% CPU)</sub> | **28,214**<br><sub>(174 MiB / 53.4% CPU)</sub> | **1.01×** | **1.00×** | ≥ **0.90×** |
| `twp-cli-static-http1` vs CLI | **114,525**<br><sub>(130 MiB / 49.1% CPU)</sub> | **54,684**<br><sub>(162 MiB / 50.2% CPU)</sub> | **1.73×** | **1.60×** | ≥ **0.85×** |
| `twp-cli-logging-http1` vs CLI | **67,217**<br><sub>(139 MiB / 45.5% CPU)</sub> | **33,844**<br><sub>(163 MiB / 50.8% CPU)</sub> | **1.01×** | **0.99×** | ≥ **0.90×** |
| `twp-cli-lb-leasttime-http1` vs route | **63,630**<br><sub>(144 MiB / 50.9% CPU)</sub> | **31,242**<br><sub>(191 MiB / 52.4% CPU)</sub> | **0.95×** | **0.94×** | ≥ **0.85×** |
| `twp-cli-dialect-twp-http1` vs CLI | **66,493**<br><sub>(134 MiB / 46.4% CPU)</sub> | **33,965**<br><sub>(158 MiB / 50.3% CPU)</sub> | **1.00×** | **1.00×** | ≥ **0.90×** |

`validate-edition-gates.ps1` **passed** on both OS for this run. Library baselines @ c=64 (same job): Win H1 **64191** / TLS **56711**; Linux H1 **31717** / TLS **23716**. Laptop smoke ratios stay on [Performance Local Lab — Editions](Performance-Local-Lab#editions-cli--plus-stress).

## Cross-version (7.0 vs 6.0)

Same reverse matrix measured on Titanium 7.0 versus committed 6.0 baselines ([33087088466](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087088466)). Median of **3** @ `0ef6d4dd`. Source: Actions [33270571908](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33270571908). Sustain RPS @ **c=64**. Absolute 7.0÷6.0 ≈ **0.96–1.00×** on same-protocol arms; peer-normalized ratios ≈ **0.90–1.03×**.

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

Both OS CSVs passed the cross-version check for this run. MITM arms are measured with the product reverse matrix, not this reverse-only comparison.

## Heavier reverse workloads

Same runners and harness as the tiny-GET tables, but with larger bodies, POST, lossy links, TLS cost, and architecture-sensitive paths (slow consumer / early response / duplex). **PUT with the same body is the same proxy work as POST; DELETE with no body matches GET** — only POST is published. Bodies/POST/lossy stay **half-duplex**. Laptop numbers are on [Performance Local Lab](Performance-Local-Lab#architecture-sensitive). How maintainers refresh these tables (modes, shards, paste scripts) is under [Maintainer notes](#maintainer-notes). **Larger-body check:** 64 / 256 KiB H2 TLS→H2 TLS is where body copy dominates headers — ratios vs YARP are in the tables below (@ `41f4adee`), unlike the tiny-GET H2↔H2 medals above.

Lossy link = **userspace** delay/drop shim (not kernel `netem`): TCP gets per-buffer delay + occasional whole-connection stalls (honest head-of-line for multiplexed HTTP/2); UDP is **loss% only** (no per-datagram delay) + drops (QUIC / MsQuic-safe). Lossy tables publish HTTP/1, HTTP/2, and HTTP/3.

### Windows — heavier reverse GET (64 KiB / 256 KiB)

Median of **3** repeats on `windows-latest` @ `41f4adee`. Source: Actions [36853268072](https://github.com/justcoding121/titanium-web-proxy/actions/runs/36853268072) (`compare-bodies`). Warmup 2s / measure 8s. **RPS cells** include `(MiB / CPU%)` footprints.

*Not possible:* **HAProxy** and **Envoy** columns are omitted (no official Windows port).












| Body | Client | Origin | TWP | nginx | YARP |
|---|---|---|---:|---:|---:|
| 64 KiB | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **9,134**<br><sub>(97 MiB / 44.1% CPU)</sub> | **620**<br><sub>(141 MiB / 24.7% CPU)</sub> | **6,985**<br><sub>(134 MiB / 41.5% CPU)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **11,535**<br><sub>(174 MiB / 46.7% CPU)</sub> | **814**<br><sub>(141 MiB / 24.8% CPU)</sub> | **9,117**<br><sub>(134 MiB / 49.1% CPU)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **3,937**<br><sub>(138 MiB / 37.4% CPU)</sub> | *Not possible (no QUIC)* | **3,576**<br><sub>(196 MiB / 48.7% CPU)</sub> |
| 256 KiB | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **2,569**<br><sub>(123 MiB / 43.3% CPU)</sub> | **167**<br><sub>(142 MiB / 24.8% CPU)</sub> | **2,154**<br><sub>(129 MiB / 45.6% CPU)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **3,307**<br><sub>(152 MiB / 39.9% CPU)</sub> | **197**<br><sub>(142 MiB / 24.7% CPU)</sub> | **2,344**<br><sub>(134 MiB / 42.8% CPU)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/1 · plain | **1,076**<br><sub>(109 MiB / 39.1% CPU)</sub> | *Not possible (no QUIC)* | 🥇 **1,086**<br><sub>(165 MiB / 45.0% CPU)</sub> |
| 64 KiB | HTTP/2 · plain | HTTP/1 · plain | 🥇 **13,884**<br><sub>(175 MiB / 39.7% CPU)</sub> | **3,252**<br><sub>(127 MiB / 24.8% CPU)</sub> | **13,150**<br><sub>(111 MiB / 44.4% CPU)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/2 · plain | 🥇 **9,634**<br><sub>(114 MiB / 38.6% CPU)</sub> | *Not possible* | **6,556**<br><sub>(138 MiB / 49.3% CPU)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **10,247**<br><sub>(120 MiB / 38.7% CPU)</sub> | *Not possible* | **7,632**<br><sub>(131 MiB / 48.6% CPU)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/2 · TLS | 🥇 **3,899**<br><sub>(124 MiB / 39.4% CPU)</sub> | *Not possible* | **3,497**<br><sub>(187 MiB / 46.7% CPU)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **5,698**<br><sub>(143 MiB / 42.4% CPU)</sub> | *Not possible (no QUIC)* | **4,442**<br><sub>(202 MiB / 48.0% CPU)</sub> |
| 256 KiB | HTTP/2 · plain | HTTP/1 · plain | 🥇 **4,390**<br><sub>(145 MiB / 30.5% CPU)</sub> | **978**<br><sub>(127 MiB / 24.5% CPU)</sub> | **3,435**<br><sub>(123 MiB / 35.5% CPU)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/2 · plain | 🥇 **2,521**<br><sub>(142 MiB / 36.1% CPU)</sub> | *Not possible* | **1,810**<br><sub>(167 MiB / 46.0% CPU)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **2,262**<br><sub>(139 MiB / 36.4% CPU)</sub> | *Not possible* | **1,776**<br><sub>(148 MiB / 42.8% CPU)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/2 · TLS | **880**<br><sub>(149 MiB / 42.4% CPU)</sub> | *Not possible* | 🥇 **995**<br><sub>(198 MiB / 43.8% CPU)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **1,389**<br><sub>(151 MiB / 39.5% CPU)</sub> | *Not possible (no QUIC)* | **1,205**<br><sub>(203 MiB / 46.2% CPU)</sub> |

nginx/Windows collapses on large reverse bodies in this harness; treat as same-OS only. H1 TLS **64 KiB** ≈ **1.13×** YARP; **256 KiB** ≈ **1.08×**. H2→H1 64 KiB ≈ **1.17×**; H3→H1 64 KiB ≈ **1.06×**.

### Linux — heavier reverse GET (64 KiB / 256 KiB)

Median of **3** repeats @ `41f4adee`. Source: Actions [36853268072](https://github.com/justcoding121/titanium-web-proxy/actions/runs/36853268072) (`compare-bodies`). Warmup 2s / measure 8s.

| Body | Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---|---:|---:|---:|---:|---:|
| 64 KiB | HTTP/1 · TLS | HTTP/1 · plain | **8,426**<br><sub>(123 MiB / 44.4% CPU)</sub> | **5,076**<br><sub>(100 MiB / 49.4% CPU)</sub> | 🥇 **8,950**<br><sub>(84 MiB / 40.0% CPU)</sub> | **7,261**<br><sub>(131 MiB / 46.1% CPU)</sub> | **6,410**<br><sub>(163 MiB / 48.9% CPU)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **13,272**<br><sub>(242 MiB / 39.0% CPU)</sub> | **3,473**<br><sub>(102 MiB / 14.4% CPU)</sub> | **11,621**<br><sub>(86 MiB / 24.2% CPU)</sub> | **10,632**<br><sub>(141 MiB / 23.7% CPU)</sub> | **11,543**<br><sub>(155 MiB / 44.1% CPU)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **5,644**<br><sub>(184 MiB / 44.8% CPU)</sub> | **1,651**<br><sub>(peak 1,696 · 105 MiB / 22.4% CPU)</sub> | **4,139**<br><sub>(89 MiB / 27.6% CPU)</sub> | **0**<br><sub>(138 MiB / 0.2% CPU)</sub> | **4,274**<br><sub>(225 MiB / 51.4% CPU)</sub> |
| 256 KiB | HTTP/1 · TLS | HTTP/1 · plain | **2,724**<br><sub>(126 MiB / 36.8% CPU)</sub> | **1,731**<br><sub>(100 MiB / 53.5% CPU)</sub> | 🥇 **2,959**<br><sub>(83 MiB / 33.5% CPU)</sub> | **2,697**<br><sub>(145 MiB / 33.9% CPU)</sub> | **2,139**<br><sub>(168 MiB / 45.5% CPU)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/1 · plain | **2,976**<br><sub>(223 MiB / 28.9% CPU)</sub> | **1,038**<br><sub>(102 MiB / 14.1% CPU)</sub> | 🥇 **3,705**<br><sub>(83 MiB / 18.9% CPU)</sub> | **3,680**<br><sub>(162 MiB / 19.6% CPU)</sub> | **3,086**<br><sub>(159 MiB / 37.0% CPU)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **1,464**<br><sub>(157 MiB / 43.0% CPU)</sub> | **448**<br><sub>(109 MiB / 23.9% CPU)</sub> | **1,285**<br><sub>(89 MiB / 31.2% CPU)</sub> | **805**<br><sub>(154 MiB / 20.2% CPU)</sub> | **1,263**<br><sub>(217 MiB / 47.9% CPU)</sub> |
| 64 KiB | HTTP/2 · plain | HTTP/1 · plain | 🥇 **19,850**<br><sub>(244 MiB / 41.2% CPU)</sub> | **4,660**<br><sub>(80 MiB / 14.4% CPU)</sub> | **14,246**<br><sub>(69 MiB / 24.2% CPU)</sub> | **16,092**<br><sub>(130 MiB / 23.6% CPU)</sub> | **16,776**<br><sub>(139 MiB / 40.5% CPU)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/2 · plain | 🥇 **5,497**<br><sub>(158 MiB / 34.4% CPU)</sub> | *Not possible* | **2,086**<br><sub>(89 MiB / 24.6% CPU)</sub> | **4,607**<br><sub>(144 MiB / 22.0% CPU)</sub> | **4,582**<br><sub>(182 MiB / 46.7% CPU)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **9,858**<br><sub>(184 MiB / 38.2% CPU)</sub> | *Not possible* | **4,172**<br><sub>(82 MiB / 24.6% CPU)</sub> | **7,131**<br><sub>(146 MiB / 22.6% CPU)</sub> | **8,403**<br><sub>(186 MiB / 43.4% CPU)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/2 · TLS | 🥇 **3,737**<br><sub>(171 MiB / 45.1% CPU)</sub> | *Not possible* | **2,193**<br><sub>(92 MiB / 35.0% CPU)</sub> | **12**<br><sub>(146 MiB / 0.2% CPU)</sub> | **3,051**<br><sub>(237 MiB / 48.6% CPU)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **8,245**<br><sub>(219 MiB / 41.4% CPU)</sub> | **2,792**<br><sub>(109 MiB / 23.1% CPU)</sub> | **7,599**<br><sub>(97 MiB / 34.2% CPU)</sub> | **0**<br><sub>(141 MiB / 0.1% CPU)</sub> | **7,119**<br><sub>(247 MiB / 48.1% CPU)</sub> |
| 256 KiB | HTTP/2 · plain | HTTP/1 · plain | **4,671**<br><sub>(221 MiB / 30.6% CPU)</sub> | **1,537**<br><sub>(80 MiB / 15.7% CPU)</sub> | **4,782**<br><sub>(70 MiB / 18.6% CPU)</sub> | 🥇 **4,868**<br><sub>(157 MiB / 20.2% CPU)</sub> | **4,754**<br><sub>(156 MiB / 31.8% CPU)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/2 · plain | 🥇 **1,766**<br><sub>(176 MiB / 32.2% CPU)</sub> | *Not possible* | **590**<br><sub>(91 MiB / 24.4% CPU)</sub> | **1,578**<br><sub>(171 MiB / 21.5% CPU)</sub> | **1,296**<br><sub>(191 MiB / 42.6% CPU)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **2,642**<br><sub>(169 MiB / 32.6% CPU)</sub> | *Not possible* | **1,184**<br><sub>(88 MiB / 24.3% CPU)</sub> | **1,995**<br><sub>(162 MiB / 22.5% CPU)</sub> | **2,328**<br><sub>(185 MiB / 38.8% CPU)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/2 · TLS | **832**<br><sub>(188 MiB / 51.0% CPU)</sub> | *Not possible* | **629**<br><sub>(94 MiB / 36.7% CPU)</sub> | **651**<br><sub>(159 MiB / 22.3% CPU)</sub> | 🥇 **906**<br><sub>(225 MiB / 46.3% CPU)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **2,299**<br><sub>(192 MiB / 40.3% CPU)</sub> | **719**<br><sub>(123 MiB / 23.7% CPU)</sub> | **1,886**<br><sub>(96 MiB / 29.9% CPU)</sub> | **1,235**<br><sub>(143 MiB / 20.2% CPU)</sub> | **1,924**<br><sub>(238 MiB / 44.8% CPU)</sub> |

On this GHA pass TWP÷YARP H1 TLS ≈ **1.21×** (64 KiB) / **1.26×** (256 KiB); H2→H1 ≈ **1.20×** / **1.15×**; H3→H1 ≈ **1.29×** / **1.16×**. TWP÷nginx H1 TLS ≈ **1.43** / **1.85**. Absolute RPS swings by VM; prefer ratios.

### Windows — POST 64 KiB request + 64 KiB response

Median of **3** repeats on `windows-latest` @ `41f4adee`. Source: Actions [36853276459](https://github.com/justcoding121/titanium-web-proxy/actions/runs/36853276459) (`compare-post`).

*Not possible:* **HAProxy** and **Envoy** columns are omitted (no official Windows port).












| Client | Origin | TWP | nginx | YARP |
|---|---|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | 🥇 **6,621**<br><sub>(96 MiB / 44.6% CPU)</sub> | **382**<br><sub>(144 MiB / 24.7% CPU)</sub> | **4,412**<br><sub>(137 MiB / 56.9% CPU)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **4,572**<br><sub>(178 MiB / 48.4% CPU)</sub> | **375**<br><sub>(144 MiB / 24.8% CPU)</sub> | **3,938**<br><sub>(135 MiB / 51.0% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **2,239**<br><sub>(175 MiB / 40.5% CPU)</sub> | *Not possible (no QUIC)* | **2,223**<br><sub>(218 MiB / 49.3% CPU)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | 🥇 **6,806**<br><sub>(178 MiB / 46.5% CPU)</sub> | **1,946**<br><sub>(130 MiB / 24.8% CPU)</sub> | **6,321**<br><sub>(124 MiB / 51.1% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | 🥇 **5,685**<br><sub>(123 MiB / 38.1% CPU)</sub> | *Not possible* | **3,894**<br><sub>(143 MiB / 48.9% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **4,157**<br><sub>(123 MiB / 38.9% CPU)</sub> | *Not possible* | **3,060**<br><sub>(142 MiB / 47.3% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | 🥇 **2,301**<br><sub>(164 MiB / 43.3% CPU)</sub> | *Not possible* | **2,018**<br><sub>(206 MiB / 48.5% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **2,104**<br><sub>(183 MiB / 41.8% CPU)</sub> | *Not possible (no QUIC)* | **2,002**<br><sub>(215 MiB / 49.3% CPU)</sub> |

TWP leads H1 POST (~**1.5x** YARP on Windows and Linux), H2→H1 POST (~**1.1-1.3x** YARP), and H3 POST (~**1.0-1.1x** YARP). H2 TLS→H2 TLS POST sustain is healthy on this pass (@ `41f4adee`; TWP ~**1.2-1.4x** YARP).

### Linux — POST 64 KiB request + 64 KiB response

Median of **3** repeats @ `41f4adee`. Source: Actions [36853276459](https://github.com/justcoding121/titanium-web-proxy/actions/runs/36853276459) (`compare-post`).

| Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---:|---:|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | **4,742**<br><sub>(134 MiB / 45.2% CPU)</sub> | **3,498**<br><sub>(100 MiB / 48.2% CPU)</sub> | 🥇 **5,044**<br><sub>(83 MiB / 41.4% CPU)</sub> | **4,956**<br><sub>(132 MiB / 42.0% CPU)</sub> | **3,176**<br><sub>(176 MiB / 55.5% CPU)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **3,056**<br><sub>(217 MiB / 47.3% CPU)</sub> | **1,354**<br><sub>(112 MiB / 21.0% CPU)</sub> | **1,818**<br><sub>(82 MiB / 23.9% CPU)</sub> | **0**<br><sub>(peak 2,979 · 143 MiB / 23.9% CPU)</sub> | **2,533**<br><sub>(168 MiB / 48.1% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **2,878**<br><sub>(220 MiB / 44.2% CPU)</sub> | **463**<br><sub>(109 MiB / 24.9% CPU)</sub> | **1,991**<br><sub>(90 MiB / 33.1% CPU)</sub> | **0**<br><sub>(126 MiB / 0.1% CPU)</sub> | **2,593**<br><sub>(243 MiB / 49.2% CPU)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | 🥇 **5,628**<br><sub>(228 MiB / 44.0% CPU)</sub> | **2,396**<br><sub>(96 MiB / 24.1% CPU)</sub> | **2,567**<br><sub>(68 MiB / 23.2% CPU)</sub> | **4,030**<br><sub>(peak 4,604 · 118 MiB / 21.6% CPU)</sub> | **4,340**<br><sub>(160 MiB / 47.3% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | 🥇 **3,268**<br><sub>(159 MiB / 41.6% CPU)</sub> | *Not possible* | **1,340**<br><sub>(86 MiB / 24.0% CPU)</sub> | **2,854**<br><sub>(142 MiB / 23.5% CPU)</sub> | **2,389**<br><sub>(179 MiB / 46.1% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **2,330**<br><sub>(148 MiB / 40.5% CPU)</sub> | *Not possible* | **1,010**<br><sub>(80 MiB / 23.1% CPU)</sub> | **2,062**<br><sub>(146 MiB / 22.9% CPU)</sub> | **1,903**<br><sub>(170 MiB / 46.3% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | 🥇 **2,132**<br><sub>(222 MiB / 43.9% CPU)</sub> | *Not possible* | **1,136**<br><sub>(96 MiB / 31.5% CPU)</sub> | **0**<br><sub>(126 MiB / 0.1% CPU)</sub> | **1,800**<br><sub>(250 MiB / 47.3% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **2,139**<br><sub>(232 MiB / 44.8% CPU)</sub> | **368**<br><sub>(120 MiB / 25.0% CPU)</sub> | **1,669**<br><sub>(96 MiB / 36.1% CPU)</sub> | **0**<br><sub>(125 MiB / 0.1% CPU)</sub> | **1,973**<br><sub>(255 MiB / 48.6% CPU)</sub> |

Linux nginx H1/H2/H3 POST completed (nginx.org mainline). TWP÷YARP H1 ≈ **1.5×**; H2→H1 ≈ **1.2×**; H3 ≈ **1.1×**. TWP÷nginx H3 ≈ **6×**.

### Windows — lossy / high-RTT (H2 HOL / H3 loss)

Userspace **5 ms** one-way delay + **1%** TCP connection stall (H1/H2); UDP is **loss% only** (no per-datagram delay; MsQuic-safe) + **1%** datagram drop (H3); **64 KiB** GET. Median of **3** repeats on `windows-latest` @ `41f4adee` — [36853280515](https://github.com/justcoding121/titanium-web-proxy/actions/runs/36853280515) (`compare-lossy`).

*Not possible:* **HAProxy** and **Envoy** columns are omitted (no official Windows port).

| Client | Origin | TWP | nginx | YARP |
|---|---|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | 🥇 **663**<br><sub>(88 MiB / 3.9% CPU)</sub> | **650**<br><sub>(142 MiB / 17.5% CPU)</sub> | **662**<br><sub>(118 MiB / 5.0% CPU)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **59**<br><sub>(peak 85 · 112 MiB / 1.6% CPU)</sub> | **18**<br><sub>(142 MiB / 0.6% CPU)</sub> | **18**<br><sub>(86 MiB / 0.9% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **1,123**<br><sub>(124 MiB / 16.9% CPU)</sub> | *Not possible (no QUIC)* | **969**<br><sub>(188 MiB / 22.0% CPU)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | 🥇 **61**<br><sub>(peak 87 · 106 MiB / 1.6% CPU)</sub> | **18**<br><sub>(127 MiB / 0.1% CPU)</sub> | **17**<br><sub>(78 MiB / 0.9% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | **16**<br><sub>(79 MiB / 0.9% CPU)</sub> | *Not possible* | 🥇 **18**<br><sub>(85 MiB / 1.4% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | **16**<br><sub>(78 MiB / 0.7% CPU)</sub> | *Not possible* | 🥇 **18**<br><sub>(84 MiB / 1.4% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | **1,042**<br><sub>(121 MiB / 16.1% CPU)</sub> | *Not possible* | 🥇 **1,244**<br><sub>(192 MiB / 29.5% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | **998**<br><sub>(132 MiB / 16.4% CPU)</sub> | *Not possible (no QUIC)* | 🥇 **1,217**<br><sub>(185 MiB / 30.3% CPU)</sub> |

H1 is near parity with YARP. H2 HOL and H3 loss sustain are non-zero on this Windows GHA pass (@ `41f4adee`); see table.

### Linux — lossy / high-RTT (H2 HOL / H3 loss)

Median of **3** repeats @ `41f4adee`. Source: [36853280515](https://github.com/justcoding121/titanium-web-proxy/actions/runs/36853280515) (`compare-lossy`; lossy H3 uses `quic-http3`, UDP drop-only).

| Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---:|---:|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | **1,214**<br><sub>(120 MiB / 10.1% CPU)</sub> | 🥇 **1,220**<br><sub>(102 MiB / 8.0% CPU)</sub> | **1,219**<br><sub>(83 MiB / 4.8% CPU)</sub> | **1,205**<br><sub>(128 MiB / 6.4% CPU)</sub> | **1,209**<br><sub>(148 MiB / 12.9% CPU)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **315**<br><sub>(177 MiB / 5.2% CPU)</sub> | **40**<br><sub>(101 MiB / 0.2% CPU)</sub> | **40**<br><sub>(84 MiB / 0.2% CPU)</sub> | **40**<br><sub>(137 MiB / 0.3% CPU)</sub> | **40**<br><sub>(127 MiB / 1.1% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | **1,516**<br><sub>(165 MiB / 21.2% CPU)</sub> | 🥇 **1,570**<br><sub>(117 MiB / 23.2% CPU)</sub> | **1,338**<br><sub>(87 MiB / 18.7% CPU)</sub> | **1,462**<br><sub>(138 MiB / 24.5% CPU)</sub> | **1,510**<br><sub>(228 MiB / 30.5% CPU)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | 🥇 **329**<br><sub>(185 MiB / 5.3% CPU)</sub> | **40**<br><sub>(78 MiB / 0.2% CPU)</sub> | **41**<br><sub>(69 MiB / 0.1% CPU)</sub> | **40**<br><sub>(127 MiB / 0.3% CPU)</sub> | **40**<br><sub>(123 MiB / 0.9% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | **40**<br><sub>(111 MiB / 0.7% CPU)</sub> | *Not possible* | **40**<br><sub>(85 MiB / 0.3% CPU)</sub> | **40**<br><sub>(135 MiB / 0.3% CPU)</sub> | 🥇 **41**<br><sub>(130 MiB / 1.3% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | **40**<br><sub>(108 MiB / 0.9% CPU)</sub> | *Not possible* | 🥇 **42**<br><sub>(87 MiB / 0.3% CPU)</sub> | **40**<br><sub>(137 MiB / 0.3% CPU)</sub> | **41**<br><sub>(130 MiB / 1.2% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | 🥇 **1,477**<br><sub>(163 MiB / 23.1% CPU)</sub> | *Not possible* | **1,212**<br><sub>(93 MiB / 21.4% CPU)</sub> | **1,379**<br><sub>(140 MiB / 25.8% CPU)</sub> | **1,417**<br><sub>(235 MiB / 31.4% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | **1,351**<br><sub>(169 MiB / 21.9% CPU)</sub> | 🥇 **1,452**<br><sub>(120 MiB / 23.7% CPU)</sub> | **1,289**<br><sub>(92 MiB / 20.8% CPU)</sub> | **1,404**<br><sub>(138 MiB / 25.9% CPU)</sub> | **1,384**<br><sub>(213 MiB / 30.8% CPU)</sub> |

TWP H2 HOL ≫ YARP (~**7.8×**). H3 TWP÷YARP ≈ **1.07×**.

### Architecture-sensitive

These runs isolate slow app readers, origin-early response, HTTP/2 duplex, and WebSocket echo. See [TWP vs YARP IO model](Performance-Profiling#twp-vs-yarp-io-model). Laptop 1-rep numbers are on [Performance Local Lab](Performance-Local-Lab#architecture-sensitive).

Median of **3** repeats on matched 4 vCPU / 16 GiB runners @ `41f4adee` ([36853288521](https://github.com/justcoding121/titanium-web-proxy/actions/runs/36853288521)). Slow consumer = 256 KiB GET, 16 KiB read + 8 ms sleep. Early response = 64 KiB POST, origin writes after 8 KiB. Duplex HTTP/2 = overlapping 64 KiB POST on H2 TLS↔H2 TLS. WebSocket row = H1 Upgrade echo round-trips/sec (not RFC 8441; see WebSocket RFC 8441 table below).

Lossy-link runs (slow **network**) are already published above; they are not a slow **app** reader.

#### Windows

*Not possible:* **HAProxy** and **Envoy** columns are omitted (no official Windows port).

| Scenario | Client | Origin | TWP | nginx | YARP |
|---|---|---|---:|---:|---:|
| Slow consumer (256 KiB GET, throttled client read) | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **248**<br><sub>(91 MiB / 5.3% CPU)</sub> | **220**<br><sub>(144 MiB / 24.7% CPU)</sub> | **241**<br><sub>(111 MiB / 5.3% CPU)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · TLS | HTTP/1 · plain | **256**<br><sub>(118 MiB / 3.6% CPU)</sub> | **230**<br><sub>(141 MiB / 24.4% CPU)</sub> | 🥇 **256**<br><sub>(114 MiB / 5.3% CPU)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **282**<br><sub>(104 MiB / 12.3% CPU)</sub> | *Not possible (no QUIC)* | **276**<br><sub>(173 MiB / 14.6% CPU)</sub> |
| Early response (origin writes after first request chunk) | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **13,378**<br><sub>(98 MiB / 42.5% CPU)</sub> | **723**<br><sub>(142 MiB / 24.9% CPU)</sub> | **7,476**<br><sub>(137 MiB / 50.0% CPU)</sub> |
| Early response (origin writes after first request chunk) | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **3,367**<br><sub>(202 MiB / 52.7% CPU)</sub> | **350**<br><sub>(143 MiB / 24.7% CPU)</sub> | **2,711**<br><sub>(136 MiB / 49.8% CPU)</sub> |
| Early response (origin writes after first request chunk) | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **2,777**<br><sub>(153 MiB / 42.6% CPU)</sub> | *Not possible (no QUIC)* | **2,477**<br><sub>(213 MiB / 52.8% CPU)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · plain | HTTP/1 · plain | **248**<br><sub>(108 MiB / 3.2% CPU)</sub> | **250**<br><sub>(127 MiB / 9.2% CPU)</sub> | 🥇 **256**<br><sub>(105 MiB / 4.1% CPU)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **256**<br><sub>(123 MiB / 3.5% CPU)</sub> | *Not possible* | **256**<br><sub>(142 MiB / 6.1% CPU)</sub> |
| Early response (origin writes after first request chunk) | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **3,606**<br><sub>(118 MiB / 30.4% CPU)</sub> | *Not possible* | **21**<br><sub>(103 MiB / 0.3% CPU)</sub> |
| Duplex (both directions live) | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **3,531**<br><sub>(119 MiB / 29.9% CPU)</sub> | *Not possible* | **15**<br><sub>(109 MiB / 0.3% CPU)</sub> |
| Duplex (WebSocket / H1 Upgrade) | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **69,940**<br><sub>(98 MiB / 44.0% CPU)</sub> | **38,586**<br><sub>(143 MiB / 24.6% CPU)</sub> | **65,385**<br><sub>(90 MiB / 45.1% CPU)</sub> |

#### Linux

| Scenario | Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---|---:|---:|---:|---:|---:|
| Slow consumer (256 KiB GET, throttled client read) | HTTP/1 · TLS | HTTP/1 · plain | **463**<br><sub>(119 MiB / 9.7% CPU)</sub> | **404**<br><sub>(100 MiB / 9.7% CPU)</sub> | **466**<br><sub>(84 MiB / 5.9% CPU)</sub> | 🥇 **472**<br><sub>(136 MiB / 6.4% CPU)</sub> | **417**<br><sub>(146 MiB / 14.6% CPU)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **474**<br><sub>(137 MiB / 19.9% CPU)</sub> | **468**<br><sub>(100 MiB / 23.1% CPU)</sub> | **473**<br><sub>(84 MiB / 9.3% CPU)</sub> | **468**<br><sub>(154 MiB / 7.5% CPU)</sub> | **466**<br><sub>(144 MiB / 23.9% CPU)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **473**<br><sub>(130 MiB / 34.8% CPU)</sub> | **120**<br><sub>(peak 342 · 94 MiB / 5.8% CPU)</sub> | **472**<br><sub>(88 MiB / 10.5% CPU)</sub> | **1**<br><sub>(144 MiB / 0.2% CPU)</sub> | **472**<br><sub>(195 MiB / 37.8% CPU)</sub> |
| Early response (origin writes after first request chunk) | HTTP/1 · TLS | HTTP/1 · plain | **5,001**<br><sub>(140 MiB / 45.5% CPU)</sub> | **0**<br><sub>(peak 3,541 · 102 MiB / 34.1% CPU)</sub> | 🥇 **5,150**<br><sub>(83 MiB / 41.6% CPU)</sub> | **0**<br><sub>(peak 2,904 · 129 MiB / 25.5% CPU)</sub> | **3,344**<br><sub>(177 MiB / 55.4% CPU)</sub> |
| Early response (origin writes after first request chunk) | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **2,430**<br><sub>(218 MiB / 48.5% CPU)</sub> | **0**<br><sub>(peak 1,345 · 112 MiB / 24.1% CPU)</sub> | **1,363**<br><sub>(84 MiB / 12.8% CPU)</sub> | **0**<br><sub>(peak 2,354 · 153 MiB / 21.4% CPU)</sub> | **2,167**<br><sub>(168 MiB / 48.6% CPU)</sub> |
| Early response (origin writes after first request chunk) | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **2,939**<br><sub>(193 MiB / 45.7% CPU)</sub> | **0**<br><sub>(peak 442 · 116 MiB / 24.8% CPU)</sub> | **1,956**<br><sub>(91 MiB / 34.3% CPU)</sub> | **0**<br><sub>(126 MiB / 0.1% CPU)</sub> | **2,141**<br><sub>(253 MiB / 48.4% CPU)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · plain | HTTP/1 · plain | **478**<br><sub>(142 MiB / 16.7% CPU)</sub> | **463**<br><sub>(79 MiB / 11.7% CPU)</sub> | 🥇 **480**<br><sub>(68 MiB / 6.7% CPU)</sub> | **474**<br><sub>(146 MiB / 4.7% CPU)</sub> | **468**<br><sub>(147 MiB / 15.5% CPU)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · TLS | HTTP/2 · TLS | **452**<br><sub>(144 MiB / 21.1% CPU)</sub> | *Not possible* | **461**<br><sub>(88 MiB / 19.4% CPU)</sub> | **460**<br><sub>(152 MiB / 12.4% CPU)</sub> | 🥇 **470**<br><sub>(162 MiB / 29.9% CPU)</sub> |
| Early response (origin writes after first request chunk) | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **1,936**<br><sub>(peak 2,226 · 141 MiB / 35.4% CPU)</sub> | *Not possible* | **1,147**<br><sub>(84 MiB / 23.2% CPU)</sub> | **0**<br><sub>(peak 2,192 · 151 MiB / 21.8% CPU)</sub> | **50**<br><sub>(145 MiB / 1.4% CPU)</sub> |
| Duplex (both directions live) | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **1,934**<br><sub>(141 MiB / 35.7% CPU)</sub> | *Not possible* | **256**<br><sub>(86 MiB / 4.8% CPU)</sub> | **0**<br><sub>(peak 2,083 · 156 MiB / 21.4% CPU)</sub> | **6**<br><sub>(150 MiB / 0.4% CPU)</sub> |
| Duplex (WebSocket / H1 Upgrade) | HTTP/1 · TLS | HTTP/1 · plain | **34,436**<br><sub>(124 MiB / 44.9% CPU)</sub> | **37,247**<br><sub>(100 MiB / 36.4% CPU)</sub> | 🥇 **37,608**<br><sub>(83 MiB / 39.8% CPU)</sub> | **36,405**<br><sub>(127 MiB / 41.2% CPU)</sub> | **31,719**<br><sub>(125 MiB / 44.9% CPU)</sub> |

Slow consumer is sleep-bound; H1/H2/H3 sit in the same band. Early-response H1: TWP leads (~**1.79x** / **1.50x** YARP Win/Linux). **Duplex H2** sustain is non-zero for TWP on this GHA pass (@ `41f4adee`). WebSocket: TWP is ~**1.07x** YARP on Windows; on Linux HAProxy and nginx lead and TWP is ~**1.09x** YARP.

### TLS termination cost (H1 TLS → cleartext origin)

Isolates keep-alive tiny GET vs **new connection per request** (handshake-dominated) vs keep-alive **256 KiB**. Product comparison uses RPS and end-to-end latency; TWP can also capture `ClientTlsTiming` when `TWP_RPS_CAPTURE_TLS=1` (child process) — nginx/YARP have no equivalent hook.

#### Windows

Median of **3** repeats on `windows-latest` @ `41f4adee`. Source: Actions [36853284423](https://github.com/justcoding121/titanium-web-proxy/actions/runs/36853284423). Absolute RPS on GHA swings hard; prefer **TWP÷YARP**.

*Not possible:* **HAProxy** and **Envoy** columns are omitted (no official Windows port).

| Workload | TWP | nginx | YARP |
|---|---:|---:|---:|
| Keep-alive · tiny GET | 🥇 **20,894**<br><sub>(84 MiB / 48.0% CPU)</sub> | **8,845**<br><sub>(142 MiB / 24.6% CPU)</sub> | **17,746**<br><sub>(101 MiB / 47.9% CPU)</sub> |
| New-connection · tiny GET | 🥇 **724**<br><sub>(80 MiB / 1.5% CPU)</sub> | **248**<br><sub>(141 MiB / 1.4% CPU)</sub> | **717**<br><sub>(112 MiB / 0.5% CPU)</sub> |
| Keep-alive · 256 KiB GET | 🥇 **2,843**<br><sub>(117 MiB / 47.5% CPU)</sub> | **170**<br><sub>(142 MiB / 24.6% CPU)</sub> | **2,663**<br><sub>(129 MiB / 47.1% CPU)</sub> |

#### Linux

Median of **3** repeats @ `41f4adee`. Source: Actions [36853284423](https://github.com/justcoding121/titanium-web-proxy/actions/runs/36853284423).

| Workload | TWP | nginx | HAProxy | Envoy | YARP |
|---|---:|---:|---:|---:|---:|
| Keep-alive · tiny GET | **25,285**<br><sub>(107 MiB / 50.0% CPU)</sub> | 🥇 **29,752**<br><sub>(99 MiB / 42.2% CPU)</sub> | **29,386**<br><sub>(84 MiB / 43.6% CPU)</sub> | **18,088**<br><sub>(127 MiB / 58.7% CPU)</sub> | **21,832**<br><sub>(134 MiB / 51.1% CPU)</sub> |
| New-connection · tiny GET | **1,028**<br><sub>(117 MiB)</sub> | **1,080**<br><sub>(101 MiB / 0.7% CPU)</sub> | **1,014**<br><sub>(82 MiB / 0.2% CPU)</sub> | 🥇 **1,155**<br><sub>(128 MiB)</sub> | **1,030**<br><sub>(147 MiB)</sub> |
| Keep-alive · 256 KiB GET | **2,886**<br><sub>(126 MiB / 37.0% CPU)</sub> | **1,817**<br><sub>(99 MiB / 53.2% CPU)</sub> | 🥇 **3,135**<br><sub>(83 MiB / 33.6% CPU)</sub> | **2,879**<br><sub>(145 MiB / 33.9% CPU)</sub> | **2,252**<br><sub>(174 MiB / 45.2% CPU)</sub> |

#### macOS

**Pending re-measure** on `macos-15` (Apple Silicon M1, 3-core / 7 GB): cells below read *Not measured* until the Mac shards finish.

| Workload | TWP | nginx | HAProxy | Envoy | YARP |
|---|---:|---:|---:|---:|---:|
| Keep-alive · tiny GET | *Not measured* | *Not measured* | *Not measured* | *Not measured* | *Not measured* |
| New-connection · tiny GET | *Not measured* | *Not measured* | *Not measured* | *Not measured* | *Not measured* |
| Keep-alive · 256 KiB GET | *Not measured* | *Not measured* | *Not measured* | *Not measured* | *Not measured* |

On Windows, TWP leads YARP on keep-alive tiny (~**1.18x**) and keep-alive 256 KiB (~**1.07x**); new-connection is a tie (~**1.01x**). On Linux, nginx and HAProxy lead keep-alive tiny, Envoy leads new-connection, and HAProxy leads keep-alive 256 KiB; TWP stays ahead of YARP on keep-alive and ties on new-connection (~**1.00x**). macOS Apple Silicon numbers are pending re-measure. New-connection is Darwin SslStream-bound (TWP≈YARP; handshake p99 SLO **500 ms** on macOS only — Win/Linux stay at **200 ms**).

## Unary gRPC (H2 TLS)

Unary Echo **RPC/s** @ c=64 over H2 TLS→H2 TLS for Titanium, YARP, nginx (`grpc_pass`), HAProxy, and Envoy (OS-possible peers only). Not folded into the architecture-sensitive tables. Median of **3** repeats @ `41f4adee` — [36853254836](https://github.com/justcoding121/titanium-web-proxy/actions/runs/36853254836).

| OS | Titanium | YARP | nginx | HAProxy | Envoy |
|---|---:|---:|---:|---:|---:|
| Windows | 🥇 **52,009**<br><sub>(96 MiB / 22.7% CPU)</sub> | **31,057**<br><sub>(126 MiB / 45.5% CPU)</sub> | **0**<br><sub>(143 MiB / 24.9% CPU)</sub> | *Not possible* | *Not possible* |
| Linux | 🥇 **41,339**<br><sub>(117 MiB / 30.4% CPU)</sub> | **24,277**<br><sub>(158 MiB / 41.9% CPU)</sub> | **0**<br><sub>(114 MiB / 24.8% CPU)</sub> | **8,952**<br><sub>(83 MiB / 24.4% CPU)</sub> | **14,753**<br><sub>(127 MiB / 20.7% CPU)</sub> |
| macOS | *Not measured* | *Not measured* | *Not measured* | *Not measured* | *Not measured* |

## Unary gRPC (H2 TLS → h2c)

Unary Echo **RPC/s** @ c=64 over **H2 TLS → h2c** (edge TLS, cleartext H2 origin). Peers: Titanium, YARP, HAProxy, Envoy. Mode: `compare-grpc` (`*-grpc-h2c`). Median of **3** repeats @ `41f4adee` — [36853254836](https://github.com/justcoding121/titanium-web-proxy/actions/runs/36853254836).

*Not possible:* **nginx** column omitted (no H2 upstream).

| OS | Titanium | YARP | HAProxy | Envoy |
|---|---:|---:|---:|---:|
| Windows | 🥇 **61,643**<br><sub>(90 MiB / 23.5% CPU)</sub> | **36,011**<br><sub>(116 MiB / 48.1% CPU)</sub> | *Not possible* | *Not possible* |
| Linux | 🥇 **43,166**<br><sub>(121 MiB / 29.6% CPU)</sub> | **24,252**<br><sub>(161 MiB / 43.4% CPU)</sub> | **7,782**<br><sub>(83 MiB / 24.5% CPU)</sub> | **12,044**<br><sub>(127 MiB / 22.2% CPU)</sub> |
| macOS | *Not measured* | *Not measured* | *Not measured* | *Not measured* |

## WebSocket (H1 TLS → H1 TLS)

WebSocket echo round-trips/sec over **dual-TLS** H1 (`proxy_ssl` style). Mode: `compare-ws-h1tls` (`*-duplex-ws-h1tls`).

| OS | Titanium | YARP | nginx | HAProxy | Envoy |
|---|---:|---:|---:|---:|---:|
| Windows | 🥇 **22,332**<br><sub>(101 MiB / 47.4% CPU)</sub> | **21,366**<br><sub>(93 MiB / 43.1% CPU)</sub> | **9,361**<br><sub>(142 MiB / 24.6% CPU)</sub> | *Not possible* | *Not possible* |
| Linux | **25,321**<br><sub>(141 MiB / 43.7% CPU)</sub> | **22,634**<br><sub>(134 MiB / 44.1% CPU)</sub> | **26,985**<br><sub>(103 MiB / 37.3% CPU)</sub> | **26,853**<br><sub>(83 MiB / 39.7% CPU)</sub> | 🥇 **28,050**<br><sub>(127 MiB / 37.9% CPU)</sub> |
| macOS | *Not measured* | *Not measured* | *Not measured* | *Not measured* | *Not measured* |

## WebSocket (H2 TLS 8441 → H1)

WebSocket echo over **RFC 8441** extended CONNECT (H2 TLS client → H1 plain origin). Mode: `compare-ws-h2` (`*-duplex-ws-h2`).

The probe client sets `NoDelay` and writes each HTTP/2 frame as one TLS record. The previous Linux row (~1.5k at ~9% CPU, tied with HAProxy) was that client with Nagle on and the frame split across two TLS records, which waited out the ~40 ms delayed ACK. Median of **3** repeats @ `f2061c27`. Source: Linux [37004979850](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37004979850), Windows [37004984433](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37004984433), macOS [37004988043](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37004988043). macOS Titanium samples at c=64 were 6.1k / 7.7k / 19.4k; the cell is that median, not a tight result.

*Not possible:* **nginx** column omitted (no RFC 8441 extended CONNECT).

| OS | Titanium | YARP | HAProxy | Envoy |
|---|---:|---:|---:|---:|
| Windows | 🥇 **28,294**<br><sub>(154 MiB / 43.7% CPU)</sub> | **0**<br><sub>(152 MiB / 19.4% CPU)</sub> | *Not possible* | *Not possible* |
| Linux | 🥇 **42,050**<br><sub>(200 MiB / 49.6% CPU)</sub> | **0**<br><sub>(198 MiB / 43.0% CPU)</sub> | **38,112**<br><sub>(83 MiB / 40.3% CPU)</sub> | **0**<br><sub>(124 MiB / 0.3% CPU)</sub> |
| macOS | **7,705**<br><sub>(288 MiB / 15.9% CPU)</sub> | **0**<br><sub>(186 MiB / 9.8% CPU)</sub> | 🥇 **9,015**<br><sub>(101 MiB / 15.4% CPU)</sub> | **0**<br><sub>(116 MiB / 0.4% CPU)</sub> |

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

## Maintainer notes

CI cadence, shards, paste scripts, and gate floors for people refreshing these tables. Consumers can skip this section.

### Tiered cadence

| Tier | Mode | When |
|------|------|------|
| Daily / per-PR | `compare-spot` | minutes |
| Milestone | `compare-terminate` / `compare-matrix` | ~1–2h |
| Editions | `compare-editions` | ~60 min (CLI / Plus / Intercept stress arms) |
| Cross-version (Gate 2) | `compare-cross-version` | ~1–2h vs committed 6.0 baselines |
| Pre-wiki smoke | `compare-product-smoke` (Linux 2 shards, `repeats=1`) | ~30–60 min; required before full product |
| Release / wiki | `compare-product` (**3** comparison-group shards × Win/Linux/mac) | ~2–2½h wall (Free account queues beyond 20 jobs) |
| Unary gRPC | `compare-grpc` | Win/Linux/mac; H2↔H2 + H2→h2c (`arm_shard` 1/2, 2/2) |
| WebSocket dual-TLS | `compare-ws-h1tls` | Win/Linux/mac; H1 TLS→H1 TLS echo |
| WebSocket RFC 8441 | `compare-ws-h2` | Win/Linux/mac; H2 TLS→H1 plain (nginx N/A) |
| Heavier tables | `compare-bodies` (**2** shards) / `post` / `lossy` / `arch` (**3** shards) / `tls-cost` | dispatch independently |

See [PERF-GATES.md](https://github.com/justcoding121/titanium-web-proxy/blob/develop/tools/RpsLoadProbe/PERF-GATES.md).

### Arm shards (comparison groups)

`--arm-shard i/n` (workflow input `arm_shard`) partitions **wiki rows** (Client×Origin + heavier/arch workload suffix), not individual proxy arms. Paste unions shard CSVs (`paste-compare-product-wiki.ps1 -RunIds …`; heavier `RUNS` lists). Table “Source: Actions” may list several run URLs for one table — **do not mix SHAs**. Free GitHub accounts allow **20** concurrent jobs (~34 for a full suite); extras **queue**, they do not fail. Dispatch product (and cross-version) first when wall clock matters.

One wiki row = one GHA job’s Client×Origin cell set: TWP + YARP + nginx + HAProxy + Envoy (when the OS can run them) + TWP Lite + Full stay on the **same VM**. Shards split **rows**, not individual proxies — so **TWP÷YARP** and **Lite÷Reverse** remain same-job ratios. Do not compare **absolute** RPS across shards (different VMs).

### Gate thresholds

- Reverse product signal: **TWP÷YARP ≥ 0.75** (nginx / HAProxy / Envoy are wiki and chart peers only — no CI gate). Edition CLI/Plus ratios floor at **0.50**.
- MITM overhead: **Lite÷Reverse ≥ 0.40** and **Full÷Reverse ≥ 0.40** (median of 3 GHA runs @ c=64). Absolute RPS moves with runner heat; ratios are the claim.
- Do **not** enable the HTTP/2 MITM multi-origin relay pool (`MaxOriginHttp2ConnectionsPerAuthority` > 1 under interception): Tip A/B showed Lite err%~29 and RSS blow-up, which fails the Lite/Full÷Reverse floors. Multi-origin remains gate-off compressed-relay only.
- Editions: see [PERF-GATES.md](https://github.com/justcoding121/titanium-web-proxy/blob/develop/tools/RpsLoadProbe/PERF-GATES.md) and `validate-edition-gates.ps1`.
- Cross-version (Gate 2) before a major tag: run `compare-cross-version` (reverse matrix, routes unset) on `develop` vs committed 6.0 baselines (`baseline-6.0-win.csv` / `baseline-6.0-linux.csv`).

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

### Paste / regenerate

Product refresh and heavier tables:

```powershell
pwsh tools/RpsLoadProbe/run-rps.ps1 -Mode compare-product
pwsh tools/RpsLoadProbe/run-rps.ps1 -Mode compare-editions
pwsh tools/RpsLoadProbe/validate-edition-gates.ps1 -CsvPath tools/RpsLoadProbe/results/rps-ramp-*.csv
pwsh tools/RpsLoadProbe/run-rps.ps1 -Mode compare-bodies
pwsh tools/RpsLoadProbe/run-rps.ps1 -Mode compare-post
pwsh tools/RpsLoadProbe/run-rps.ps1 -Mode compare-lossy
pwsh tools/RpsLoadProbe/run-rps.ps1 -Mode compare-tls-cost
pwsh tools/RpsLoadProbe/run-rps.ps1 -Mode compare-arch
pwsh tools/RpsLoadProbe/run-rps.ps1 -Mode compare-saturation
```

Harness details: [RpsLoadProbe](https://github.com/justcoding121/titanium-web-proxy/tree/develop/tools/RpsLoadProbe). The harness does not disable TWP safety that peers also skip, and it does not retune to pass gates ([PERF-GATES.md](https://github.com/justcoding121/titanium-web-proxy/blob/develop/tools/RpsLoadProbe/PERF-GATES.md)). MITM leaf keys are a shared RSA/ECDSA pair per `CertificateManager` (intentional RPS tradeoff).
