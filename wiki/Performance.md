# Performance

Throughput and footprint of **Titanium** as a reverse / edge proxy and as a decrypting (**MITM**) proxy, measured on the same harness against **YARP**, **nginx**, **HAProxy**, and **Envoy** where each OS can run them.

**RPS** is requests per second (gRPC tables use **RPC/s**). Numbers are Release builds on matched GitHub-hosted runners: Windows and Linux at **4 vCPU / 16 GiB**, macOS at **`macos-15-intel` 4-core / 14 GB**. Read within one table — absolute RPS is not comparable across operating systems. *Not possible* means that product cannot run that path on that OS; *Not measured* means the path exists but no published number yet.

For pooling knobs and certificate first-visit tuning, see [Performance and pooling](Home#performance-and-pooling). Laptop cool A/B tables (not publishable) live on [Performance Local Lab](Performance-Local-Lab).

## Why this comparison is fair

- Same load generator, same origin process, and the same warmup / measure windows (2s / 8s) with the same concurrency ramp (8, 16, 32, 64).
- Every reverse arm is three OS processes: load generator + origin + proxy. Origin-direct omits the proxy; peers are never in-process with the client.
- Same runner class per table (`windows-latest` / `ubuntu-latest` / `macos-15-intel`). Laptop numbers are never mixed into these tables.
- Peers use equivalent TLS/ALPN and streaming-friendly settings on the same loopback shape. HAProxy and Envoy are Linux/macOS only — *Not possible* on Windows, so their columns are omitted there.
- MITM (HTTPS decryption with forged certificates) is Titanium-only; peers cannot MITM. Those tables show Titanium MITM overhead versus its own reverse path on the same wires.
- **Tiny keep-alive GET** (~56-byte JSON) is the industry RPS shape (same class as wrk / TechEmpower). It is also real for small JSON APIs and health checks.
- **Same-protocol H2↔H2 / H3↔H3** on that shape is Titanium’s **best case**: with interception off, Titanium copies frames instead of decoding and re-encoding headers (peers do a full HTTP decode). Medals there are not the typical reverse-proxy job.
- **Typical reverse** is H1 TLS→H1 or H2→H1 (~1.1× YARP on tiny GET). With **larger bodies**, see [Heavier reverse](#heavier-reverse-workloads): at 64 KiB H2 TLS→H2 TLS, Titanium is **behind** YARP (~0.69–0.76×).

## How to read the tables

- Bold RPS is **sustain** (last concurrency that still met error/latency SLOs). When peak differs, it appears in the same cell as `<sub>(peak N · …)</sub>` with RSS/CPU. When sustain equals peak, peak is omitted.
- 🥇 = best among Titanium / nginx / YARP on Reverse rows (highest RPS; on a tie, lower memory then lower CPU%). Gold medals are **per cell on tiny GET** — an H2↔H2 gold is that frame-copy best case, not “Titanium is 1.7× on all reverse.” For larger-body H2→H2, see the [heavier tables](#heavier-reverse-workloads).
- **MITM** tables are Titanium-only. **Lite÷Reverse** / **Full÷Reverse** = Titanium MITM sustain ÷ Titanium reverse sustain on the same Client×Origin pair — the overhead of decrypting and intercepting versus bare reverse, not versus nginx or YARP.
- *Not possible* = cannot do that path. *Not measured* = path exists but no published number yet. When every row would be *Not possible* for a peer, that column is omitted and a note above the table explains why.

## Contents

- [Why this comparison is fair](#why-this-comparison-is-fair)
- [How to read the tables](#how-to-read-the-tables)
- [Measurement environment](#measurement-environment)
    - [Windows (GitHub-hosted `windows-latest`)](#windows-github-hosted-windows-latest)
    - [Linux (GitHub-hosted `ubuntu-latest`)](#linux-github-hosted-ubuntu-latest)
    - [macOS (GitHub-hosted `macos-15-intel`)](#macos-github-hosted-macos-15-intel)
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

All three OS use the **4-core-class** public-repo GitHub-hosted runners: **Windows / Linux** at **4 vCPU / 16 GiB / 14 GB SSD**, **macOS** at **`macos-15-intel` 4-core / 14 GB** (not `macos-latest`). Same harness knobs: warmup 2s / measure 8s; concurrency 8, 16, 32, 64; median of 3 repeats. Prefer Titanium÷YARP / Titanium÷nginx ratios over absolute RPS.

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

### macOS (GitHub-hosted `macos-15-intel`)

|||
|---|---|
| OS | macOS 15 (GitHub-hosted `macos-15-intel`, Intel x86_64) |
| CPU | **4** logical processors |
| RAM | **14** GB |
| Runtime | .NET 10.0.x |
| nginx | Homebrew nginx with `--with-http_v3_module` (workflow fails if missing) |
| HAProxy | Homebrew `haproxy` with `USE_QUIC` (workflow fails if missing; 3.2.23 osx source fallback) |
| Envoy | Homebrew bottle when present; else pinned darwin-amd64 **1.36.7** (official GitHub assets are Linux-only). HTTP/3 compiled in. |
| MsQuic | Homebrew `libmsquic` + `openssl@3` on `DYLD_LIBRARY_PATH` / `DYLD_FALLBACK_LIBRARY_PATH` (`QuicListener.IsSupported`) |
| YARP | Yarp.ReverseProxy **2.3.0** |
| Harness | RpsLoadProbe Release; median of 3 repeats where noted |

Do **not** use `macos-latest` (Apple Silicon, 3-core / 7 GB) for publishable saturation numbers.

### Saturation control

Calibration for the shared 4 vCPU loopback shape: how close client + origin are to saturated before ranking reverse peers. Tiny keep-alive GET. Median of **3** repeats @ `9a2b3a1e` — [34441539402](https://github.com/justcoding121/titanium-web-proxy/actions/runs/34441539402). Warmup 2s / measure 8s; concurrency 8, 16, 32, 64. Block A **% of origin-HttpClient** uses median **peak** RPS. Blocks B/C use peer÷YARP / ÷nginx on median peak (not % of H1 origin). **RPS cells** embed median RSS / CPU for the **proxy child** plus its **full descendant tree** (serve-proxy → nginx master → workers); origin-direct samples the **origin** child. Product matrices below use matched `dotnet-httpclient` only (not bombardier).

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
| origin-direct | dotnet-httpclient | **50,319**<br><sub>(55 MiB / 40.4% CPU)</sub> | **100.0%** |
| origin-direct-bombardier | bombardier | **38,565**<br><sub>(56 MiB / 21.8% CPU)</sub> | **76.6%** |
| bare-reverse-http1 | dotnet-httpclient | **25,016**<br><sub>(58 MiB / 43.8% CPU)</sub> | **49.7%** |
| nginx-reverse-http1 | dotnet-httpclient | **13,405**<br><sub>(125 MiB / 24.8% CPU)</sub> | **26.6%** |
| yarp-reverse-http1 | dotnet-httpclient | **20,802**<br><sub>(87 MiB / 49.0% CPU)</sub> | **41.3%** |
| twp-reverse-http1 | dotnet-httpclient | 🥇 **24,558**<br><sub>(75 MiB / 48.0% CPU)</sub> | **48.8%** |

**Linux** (`ubuntu-latest`)

| Arm | Generator | RPS | % of origin-HttpClient |
|---|---|---:|---:|
| origin-direct | dotnet-httpclient | **102,038**<br><sub>(80 MiB / 44.2% CPU)</sub> | **100.0%** |
| origin-direct-bombardier | bombardier | **61,434**<br><sub>(80 MiB / 37.4% CPU)</sub> | **60.2%** |
| bare-reverse-http1 | dotnet-httpclient | **46,442**<br><sub>(70 MiB / 46.0% CPU)</sub> | **45.5%** |
| nginx-reverse-http1 | dotnet-httpclient | 🥇 **56,585**<br><sub>(76 MiB / 40.3% CPU)</sub> | **55.5%** |
| yarp-reverse-http1 | dotnet-httpclient | **41,835**<br><sub>(116 MiB / 49.3% CPU)</sub> | **41.0%** |
| twp-reverse-http1 | dotnet-httpclient | **47,721**<br><sub>(95 MiB / 50.9% CPU)</sub> | **46.8%** |

Reverse peers are about **50–46%** of the origin-direct HttpClient peak on this runner class (Win TWP **50.0%**, Lin TWP **45.6%**). Prefer the **%** column over absolute RPS across runs. Bare and origin-direct are controls (not medal peers).

#### Block B — H2 TLS→H1

Peer ratios (÷YARP / ÷nginx) on median peak; **RPS cells** embed `(MiB / CPU%)`.

**Windows** (`windows-latest`)

| Arm | Generator | RPS | ÷YARP | ÷nginx |
|---|---|---:|---:|---:|
| nginx-reverse-http2 | dotnet-httpclient | **7,992**<br><sub>(141 MiB / 24.6% CPU)</sub> | **0.28×** | **1.00×** |
| yarp-reverse-http2 | dotnet-httpclient | **28,294**<br><sub>(97 MiB / 54.9% CPU)</sub> | **1.00×** | **3.54×** |
| twp-reverse-http2-cleartext | dotnet-httpclient | 🥇 **33,720**<br><sub>(105 MiB / 52.2% CPU)</sub> | **1.19×** | **4.22×** |

**Linux** (`ubuntu-latest`)

| Arm | Generator | RPS | ÷YARP | ÷nginx |
|---|---|---:|---:|---:|
| nginx-reverse-http2 | dotnet-httpclient | **23,276**<br><sub>(102 MiB / 18.9% CPU)</sub> | **0.53×** | **1.00×** |
| yarp-reverse-http2 | dotnet-httpclient | **44,299**<br><sub>(122 MiB / 48.0% CPU)</sub> | **1.00×** | **1.90×** |
| twp-reverse-http2-cleartext | dotnet-httpclient | 🥇 **49,167**<br><sub>(124 MiB / 52.1% CPU)</sub> | **1.11×** | **2.11×** |

#### Block C — H3→H1

Same layout as Block B. Requires QuicListener. nginx needs `http_v3_module` (Windows nginx has no QUIC). HAProxy needs `USE_QUIC` (GHA Linux/macOS require it). Envoy 1.20+ includes HTTP/3.

**Windows** (`windows-latest`)

| Arm | Generator | RPS | ÷YARP | ÷nginx |
|---|---|---:|---:|---:|
| nginx-reverse-http3-cleartext | dotnet-httpclient | *Not possible (no QUIC)* | — | — |
| yarp-reverse-http3-cleartext | dotnet-httpclient | **14,041**<br><sub>(142 MiB / 51.8% CPU)</sub> | **1.00×** | — |
| twp-reverse-http3-cleartext | dotnet-httpclient | 🥇 **14,348**<br><sub>(104 MiB / 43.8% CPU)</sub> | **1.02×** | — |

**Linux** (`ubuntu-latest`)

| Arm | Generator | RPS | ÷YARP | ÷nginx |
|---|---|---:|---:|---:|
| nginx-reverse-http3-cleartext | dotnet-httpclient | **0**<br><sub>(peak 24,928 · 110 MiB / 21.8% CPU)</sub> | **0.89×** | **1.00×** |
| yarp-reverse-http3-cleartext | dotnet-httpclient | **27,936**<br><sub>(195 MiB / 48.8% CPU)</sub> | **1.00×** | **1.12×** |
| twp-reverse-http3-cleartext | dotnet-httpclient | 🥇 **30,636**<br><sub>(159 MiB / 52.7% CPU)</sub> | **1.10×** | **1.23×** |

## Windows — Titanium vs nginx vs YARP

Client / origin: HTTP version and whether TLS is used (`plain` = cleartext, `TLS` = encrypted, `QUIC` = HTTP/3).

### Reverse

Median of **3 repeats** on `windows-latest` (4 vCPU / 16 GiB). Bare reverse 5×5 @ `9a2b3a1e` — `compare-product` [34441526151](https://github.com/justcoding121/titanium-web-proxy/actions/runs/34441526151). Warmup 2s / measure 8s; concurrency 8, 16, 32, 64. Prefer TWP÷peer ratios over absolute RPS. **RPS cells** show sustain RPS; `<br><sub>` holds peak (when higher) plus median RSS / CPU at the peak-RPS step. nginx terminate peers use `keepalive 256` + streaming buffers. Laptop High-perf / cool-paired numbers stay on the [local lab](Performance-Local-Lab). Product 5×5 is **~56-byte JSON keep-alive GET**; H2/H3 same-protocol cells are mostly header work with a tiny body (Titanium best case) — see [Why this comparison is fair](#why-this-comparison-is-fair).

*Not possible:* **HAProxy** and **Envoy** columns are omitted (no official Windows port).

**Load generators:** Reverse inbound H3 arms use **`dotnet-httpclient`** (`http_version=3.0`, `RequestVersionExact`). nginx/Windows is same-OS only (no QUIC).

| Client | Origin | TWP | nginx | YARP |
|---|---|---:|---:|---:|
| HTTP/1 · plain | HTTP/1 · plain | 🥇 **23,134**<br><sub>(75 MiB / 47.5% CPU)</sub> | **13,916**<br><sub>(125 MiB / 24.9% CPU)</sub> | **21,699**<br><sub>(86 MiB / 48.8% CPU)</sub> |
| HTTP/1 · plain | HTTP/1 · TLS | 🥇 **20,749**<br><sub>(89 MiB / 52.8% CPU)</sub> | **8,366**<br><sub>(135 MiB / 24.6% CPU)</sub> | **18,784**<br><sub>(100 MiB / 49% CPU)</sub> |
| HTTP/1 · plain | HTTP/2 · plain | 🥇 **45,733**<br><sub>(115 MiB / 50.5% CPU)</sub> | *Not possible (no H2 upstream)* | **39,820**<br><sub>(90 MiB / 49.2% CPU)</sub> |
| HTTP/1 · plain | HTTP/2 · TLS | 🥇 **32,162**<br><sub>(120 MiB / 46.7% CPU)</sub> | *Not possible (no H2 upstream)* | **29,670**<br><sub>(98 MiB / 49.7% CPU)</sub> |
| HTTP/1 · plain | HTTP/3 · QUIC | 🥇 **18,315**<br><sub>(106 MiB / 50.6% CPU)</sub> | *Not possible (no H3 upstream)* | **17,823**<br><sub>(117 MiB / 51% CPU)</sub> |
| HTTP/1 · TLS | HTTP/1 · plain | 🥇 **25,720**<br><sub>(90 MiB / 48.4% CPU)</sub> | **12,987**<br><sub>(141 MiB / 24.8% CPU)</sub> | **22,690**<br><sub>(102 MiB / 48.2% CPU)</sub> |
| HTTP/1 · TLS | HTTP/1 · TLS | 🥇 **18,942**<br><sub>(91 MiB / 48.6% CPU)</sub> | **7,218**<br><sub>(143 MiB / 24.8% CPU)</sub> | **17,296**<br><sub>(104 MiB / 49.7% CPU)</sub> |
| HTTP/1 · TLS | HTTP/2 · plain | 🥇 **27,614**<br><sub>(111 MiB / 44.5% CPU)</sub> | *Not possible (no H2 upstream)* | **26,015**<br><sub>(104 MiB / 47.6% CPU)</sub> |
| HTTP/1 · TLS | HTTP/2 · TLS | 🥇 **32,132**<br><sub>(117 MiB / 45.4% CPU)</sub> | *Not possible (no H2 upstream)* | **29,999**<br><sub>(103 MiB / 48.2% CPU)</sub> |
| HTTP/1 · TLS | HTTP/3 · QUIC | 🥇 **16,275**<br><sub>(110 MiB / 51.3% CPU)</sub> | *Not possible (no H3 upstream)* | **15,797**<br><sub>(124 MiB / 51.4% CPU)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | 🥇 **34,933**<br><sub>(89 MiB / 55% CPU)</sub> | **9,362**<br><sub>(127 MiB / 24.4% CPU)</sub> | **31,625**<br><sub>(86 MiB / 54.4% CPU)</sub> |
| HTTP/2 · plain | HTTP/1 · TLS | 🥇 **35,086**<br><sub>(105 MiB / 49% CPU)</sub> | **8,849**<br><sub>(138 MiB / 24.9% CPU)</sub> | **32,740**<br><sub>(92 MiB / 50.1% CPU)</sub> |
| HTTP/2 · plain | HTTP/2 · plain | 🥇 **112,638**<br><sub>(61 MiB / 28.3% CPU)</sub> | *Not possible (no H2 upstream)* | **64,290**<br><sub>(90 MiB / 49.6% CPU)</sub> |
| HTTP/2 · plain | HTTP/2 · TLS | 🥇 **70,105**<br><sub>(62 MiB / 20.1% CPU)</sub> | *Not possible (no H2 upstream)* | **43,971**<br><sub>(99 MiB / 35.9% CPU)</sub> |
| HTTP/2 · plain | HTTP/3 · QUIC | 🥇 **33,088**<br><sub>(128 MiB / 53.1% CPU)</sub> | *Not possible (no H3 upstream)* | **31,406**<br><sub>(130 MiB / 52% CPU)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **34,943**<br><sub>(97 MiB / 52.2% CPU)</sub> | **8,430**<br><sub>(141 MiB / 24.8% CPU)</sub> | **29,441**<br><sub>(96 MiB / 53.8% CPU)</sub> |
| HTTP/2 · TLS | HTTP/1 · TLS | 🥇 **29,487**<br><sub>(97 MiB / 53.6% CPU)</sub> | **6,542**<br><sub>(144 MiB / 24.6% CPU)</sub> | **25,117**<br><sub>(98 MiB / 54.2% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | 🥇 **107,152**<br><sub>(75 MiB / 29.1% CPU)</sub> | *Not possible (no H2 upstream)* | **59,098**<br><sub>(102 MiB / 52.7% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **84,487**<br><sub>(73 MiB / 28.8% CPU)</sub> | *Not possible (no H2 upstream)* | **49,546**<br><sub>(98 MiB / 49% CPU)</sub> |
| HTTP/2 · TLS | HTTP/3 · QUIC | 🥇 **30,034**<br><sub>(127 MiB / 53.4% CPU)</sub> | *Not possible (no H3 upstream)* | **25,937**<br><sub>(123 MiB / 51.5% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **19,348**<br><sub>(107 MiB / 44% CPU)</sub> | *Not possible (no QUIC)* | **18,041**<br><sub>(143 MiB / 50.9% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **13,646**<br><sub>(113 MiB / 46.7% CPU)</sub> | *Not possible (no QUIC)* | **13,437**<br><sub>(145 MiB / 52.4% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/2 · plain | 🥇 **34,128**<br><sub>(136 MiB / 47.6% CPU)</sub> | *Not possible (no H2 upstream)* | **25,450**<br><sub>(166 MiB / 50.1% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | 🥇 **33,850**<br><sub>(138 MiB / 48% CPU)</sub> | *Not possible (no H2 upstream)* | **26,828**<br><sub>(168 MiB / 48.5% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/3 · QUIC | 🥇 **23,150**<br><sub>(121 MiB / 41.8% CPU)</sub> | *Not possible (no H3 upstream)* | **12,064**<br><sub>(167 MiB / 53.1% CPU)</sub> |

### MITM (TWP only)

Same Client×Origin wires with interception on (`compare-product` [34441526151](https://github.com/justcoding121/titanium-web-proxy/actions/runs/34441526151)). **Lite** = no-op handlers (unchanged-lite finish). **Full** = append-only header mutation (harness: one probe header each way; product: generic append-only relay via `MitmCompressedRelayHelper`). nginx/HAProxy/Envoy/YARP cannot MITM. **Lite÷Reverse** / **Full÷Reverse** vs bare reverse (**same job / comparison-group shard**). Completion gate: Lite ≥ **0.50×** and Full ≥ **0.50×** reverse sustain @ c=64 (median of 3 GHA runs); reverse TWP÷YARP ≥ **0.75×** (no terminate-peer gate).

**v1 append-only relay (2026-08-27):** Pre-fix H2→H2 Full÷Reverse was **0.13–0.16×** ([32960766249](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32960766249)). Post-fix @ `df172718`: H2 plain→H2 plain Full **0.77–0.79×**, H3→H1 Full **0.91–0.93×**, all MITM arms ≥ **0.70×** on median of [33041445371](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33041445371), [33055267086](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33055267086), [33055272140](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33055272140).

**v2 drop-only + non-unique append (2026-08-27):** `MitmStaticRebuildHelper` rebuilds static HPACK/QPACK after 1–4 unique header drops; trailing non-unique appends stay on compressed relay. @ `9a2b3a1e`: all MITM arms ≥ **0.70×** on GHA median ([33087088466](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087088466), [33087091622](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087091622), [33105885748](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33105885748) Linux; [33087085235](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087085235), [33087088466](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087088466), [33087091622](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087091622) Windows). H2 plain→H2 plain Full **0.77–0.79×** (Win) / **0.78×** (Lin).

| Client | Origin | Lite sustain | Full sustain | Lite÷Reverse | Full÷Reverse |
|---|---|---:|---:|---:|---:|
| HTTP/1 · plain | HTTP/1 · plain | **25054**<br><sub>(82 MiB / 50.4% CPU)</sub> | **24740**<br><sub>(82 MiB / 51.6% CPU)</sub> | **1.08×** | **1.07×** |
| HTTP/1 · plain | HTTP/1 · TLS | **20351**<br><sub>(95 MiB / 54.2% CPU)</sub> | **19992**<br><sub>(95 MiB / 52% CPU)</sub> | **0.98×** | **0.96×** |
| HTTP/1 · plain | HTTP/2 · plain | **45468**<br><sub>(110 MiB / 51.5% CPU)</sub> | **43600**<br><sub>(120 MiB / 49.4% CPU)</sub> | **0.99×** | **0.95×** |
| HTTP/1 · plain | HTTP/2 · TLS | **32619**<br><sub>(118 MiB / 46.8% CPU)</sub> | **31475**<br><sub>(125 MiB / 46.3% CPU)</sub> | **1.01×** | **0.98×** |
| HTTP/1 · plain | HTTP/3 · QUIC | **17858**<br><sub>(105 MiB / 47.7% CPU)</sub> | **17488**<br><sub>(104 MiB / 52.6% CPU)</sub> | **0.98×** | **0.95×** |
| HTTP/1 · TLS | HTTP/1 · plain | **24388**<br><sub>(98 MiB / 47.9% CPU)</sub> | **24773**<br><sub>(95 MiB / 48.2% CPU)</sub> | **0.95×** | **0.96×** |
| HTTP/1 · TLS | HTTP/1 · TLS | **18766**<br><sub>(94 MiB / 49.4% CPU)</sub> | **18334**<br><sub>(96 MiB / 49.4% CPU)</sub> | **0.99×** | **0.97×** |
| HTTP/1 · TLS | HTTP/2 · plain | **27503**<br><sub>(112 MiB / 47.3% CPU)</sub> | **27245**<br><sub>(112 MiB / 46.1% CPU)</sub> | **1×** | **0.99×** |
| HTTP/1 · TLS | HTTP/2 · TLS | **31677**<br><sub>(118 MiB / 47.9% CPU)</sub> | **30700**<br><sub>(117 MiB / 44.2% CPU)</sub> | **0.99×** | **0.96×** |
| HTTP/1 · TLS | HTTP/3 · QUIC | **15862**<br><sub>(113 MiB / 50.9% CPU)</sub> | **15726**<br><sub>(112 MiB / 50.8% CPU)</sub> | **0.97×** | **0.97×** |
| HTTP/2 · plain | HTTP/1 · plain | **34123**<br><sub>(95 MiB / 55.4% CPU)</sub> | **33304**<br><sub>(96 MiB / 56.8% CPU)</sub> | **0.98×** | **0.95×** |
| HTTP/2 · plain | HTTP/1 · TLS | **34643**<br><sub>(106 MiB / 50.5% CPU)</sub> | **34092**<br><sub>(109 MiB / 52.9% CPU)</sub> | **0.99×** | **0.97×** |
| HTTP/2 · plain | HTTP/2 · plain | **86231**<br><sub>(71 MiB / 41.3% CPU)</sub> | **80246**<br><sub>(73 MiB / 41.9% CPU)</sub> | **0.77×** | **0.71×** |
| HTTP/2 · plain | HTTP/2 · TLS | **72044**<br><sub>(77 MiB / 36.5% CPU)</sub> | **67973**<br><sub>(77 MiB / 39.7% CPU)</sub> | **1.03×** | **0.97×** |
| HTTP/2 · plain | HTTP/3 · QUIC | **33179**<br><sub>(124 MiB / 53.4% CPU)</sub> | **32949**<br><sub>(129 MiB / 53.1% CPU)</sub> | **1×** | **1×** |
| HTTP/2 · TLS | HTTP/1 · plain | **33820**<br><sub>(98 MiB / 55.2% CPU)</sub> | **32652**<br><sub>(107 MiB / 54.9% CPU)</sub> | **0.97×** | **0.93×** |
| HTTP/2 · TLS | HTTP/1 · TLS | **28427**<br><sub>(100 MiB / 56.2% CPU)</sub> | **27770**<br><sub>(105 MiB / 53.2% CPU)</sub> | **0.96×** | **0.94×** |
| HTTP/2 · TLS | HTTP/2 · plain | **84248**<br><sub>(86 MiB / 40.5% CPU)</sub> | **80554**<br><sub>(84 MiB / 39.7% CPU)</sub> | **0.79×** | **0.75×** |
| HTTP/2 · TLS | HTTP/2 · TLS | **68743**<br><sub>(86 MiB / 38.1% CPU)</sub> | **65184**<br><sub>(81 MiB / 39.6% CPU)</sub> | **0.81×** | **0.77×** |
| HTTP/2 · TLS | HTTP/3 · QUIC | **29843**<br><sub>(126 MiB / 54.4% CPU)</sub> | **29276**<br><sub>(133 MiB / 53.5% CPU)</sub> | **0.99×** | **0.97×** |
| HTTP/3 · QUIC | HTTP/1 · plain | **18648**<br><sub>(117 MiB / 44.9% CPU)</sub> | **17458**<br><sub>(114 MiB / 44.7% CPU)</sub> | **0.96×** | **0.9×** |
| HTTP/3 · QUIC | HTTP/1 · TLS | **12964**<br><sub>(123 MiB / 46.8% CPU)</sub> | **12392**<br><sub>(116 MiB / 47.5% CPU)</sub> | **0.95×** | **0.91×** |
| HTTP/3 · QUIC | HTTP/2 · plain | **30904**<br><sub>(132 MiB / 48.6% CPU)</sub> | **29726**<br><sub>(133 MiB / 50.6% CPU)</sub> | **0.91×** | **0.87×** |
| HTTP/3 · QUIC | HTTP/2 · TLS | **31489**<br><sub>(143 MiB / 48.5% CPU)</sub> | **30510**<br><sub>(143 MiB / 48.3% CPU)</sub> | **0.93×** | **0.9×** |
| HTTP/3 · QUIC | HTTP/3 · QUIC | **16061**<br><sub>(120 MiB / 49.2% CPU)</sub> | **15312**<br><sub>(119 MiB / 49.5% CPU)</sub> | **0.69×** | **0.66×** |

## Linux — Titanium vs nginx vs HAProxy vs Envoy vs YARP

### Reverse

Median of **3 repeats** on `ubuntu-latest` (4 vCPU / 16 GiB). Bare reverse 5×5 @ `9a2b3a1e` — `compare-product` [34441526151](https://github.com/justcoding121/titanium-web-proxy/actions/runs/34441526151). Warmup 2s / measure 8s; concurrency 8, 16, 32, 64. **Linux nginx is the authoritative nginx baseline.** HAProxy (3.2 `USE_QUIC`) and Envoy (GitHub release, HTTP/3 compiled in) run on the same loopback shape as nginx/YARP. nginx terminate peers use `keepalive 256` + streaming buffers. The RPS workflow installs nginx.org mainline (`http_v3_module`), a QUIC-enabled HAProxy, Envoy, and `libmsquic`. Prefer ratios over absolute RPS. Product 5×5 is **~56-byte JSON keep-alive GET**; H2/H3 same-protocol cells are mostly header work with a tiny body (Titanium best case) — see [Why this comparison is fair](#why-this-comparison-is-fair).

| Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---:|---:|---:|---:|---:|
| HTTP/1 · plain | HTTP/1 · plain | **36,672**<br><sub>(94 MiB / 50.5% CPU)</sub> | 🥇 **44,112**<br><sub>(76 MiB / 40.4% CPU)</sub> | **41,748**<br><sub>(67 MiB / 41.3% CPU)</sub> | **24,415**<br><sub>(116 MiB / 59.5% CPU)</sub> | **32,393**<br><sub>(115 MiB / 48.8% CPU)</sub> |
| HTTP/1 · plain | HTTP/1 · TLS | **28,506**<br><sub>(107 MiB / 49.3% CPU)</sub> | 🥇 **34,687**<br><sub>(93 MiB / 40.7% CPU)</sub> | **32,839**<br><sub>(71 MiB / 43% CPU)</sub> | **22,383**<br><sub>(117 MiB / 56.4% CPU)</sub> | **25,710**<br><sub>(138 MiB / 49.6% CPU)</sub> |
| HTTP/1 · plain | HTTP/2 · plain | 🥇 **45,673**<br><sub>(129 MiB / 52.7% CPU)</sub> | *Not possible (no H2 upstream)* | **26,830**<br><sub>(68 MiB / 42.8% CPU)</sub> | **27,447**<br><sub>(116 MiB / 61% CPU)</sub> | **40,965**<br><sub>(125 MiB / 49.4% CPU)</sub> |
| HTTP/1 · plain | HTTP/2 · TLS | 🥇 **37,934**<br><sub>(147 MiB / 49.1% CPU)</sub> | *Not possible (no H2 upstream)* | **35,699**<br><sub>(68 MiB / 43% CPU)</sub> | **19,949**<br><sub>(117 MiB / 61.9% CPU)</sub> | **35,520**<br><sub>(132 MiB / 47.3% CPU)</sub> |
| HTTP/1 · plain | HTTP/3 · QUIC | 🥇 **26,151**<br><sub>(141 MiB / 53.8% CPU)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **12,126**<br><sub>(120 MiB / 56.3% CPU)</sub> | **24,086**<br><sub>(155 MiB / 48.3% CPU)</sub> |
| HTTP/1 · TLS | HTTP/1 · plain | **27,742**<br><sub>(111 MiB / 48.9% CPU)</sub> | **33,232**<br><sub>(100 MiB / 41.5% CPU)</sub> | 🥇 **33,283**<br><sub>(85 MiB / 43.6% CPU)</sub> | **21,660**<br><sub>(127 MiB / 56.4% CPU)</sub> | **24,735**<br><sub>(142 MiB / 49.4% CPU)</sub> |
| HTTP/1 · TLS | HTTP/1 · TLS | **23,878**<br><sub>(111 MiB / 48.2% CPU)</sub> | 🥇 **28,042**<br><sub>(103 MiB / 41.6% CPU)</sub> | **27,394**<br><sub>(86 MiB / 43.8% CPU)</sub> | **19,309**<br><sub>(128 MiB / 55% CPU)</sub> | **21,716**<br><sub>(142 MiB / 49.4% CPU)</sub> |
| HTTP/1 · TLS | HTTP/2 · plain | 🥇 **34,010**<br><sub>(140 MiB / 51.4% CPU)</sub> | *Not possible (no H2 upstream)* | **21,760**<br><sub>(83 MiB / 42.9% CPU)</sub> | **18,208**<br><sub>(127 MiB / 58.6% CPU)</sub> | **30,979**<br><sub>(141 MiB / 49.2% CPU)</sub> |
| HTTP/1 · TLS | HTTP/2 · TLS | 🥇 **29,656**<br><sub>(157 MiB / 48.6% CPU)</sub> | *Not possible (no H2 upstream)* | **29,078**<br><sub>(83 MiB / 44% CPU)</sub> | **21,903**<br><sub>(126 MiB / 56.9% CPU)</sub> | **27,210**<br><sub>(146 MiB / 48.3% CPU)</sub> |
| HTTP/1 · TLS | HTTP/3 · QUIC | 🥇 **22,269**<br><sub>(152 MiB / 52.9% CPU)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **11,093**<br><sub>(130 MiB / 55.9% CPU)</sub> | **19,851**<br><sub>(166 MiB / 49.4% CPU)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | 🥇 **41,263**<br><sub>(126 MiB / 53.1% CPU)</sub> | **18,170**<br><sub>(79 MiB / 19.6% CPU)</sub> | **27,652**<br><sub>(69 MiB / 24.4% CPU)</sub> | **17,979**<br><sub>(118 MiB / 23% CPU)</sub> | **39,219**<br><sub>(115 MiB / 48.3% CPU)</sub> |
| HTTP/2 · plain | HTTP/1 · TLS | 🥇 **32,750**<br><sub>(131 MiB / 51% CPU)</sub> | **15,078**<br><sub>(100 MiB / 19.2% CPU)</sub> | **22,285**<br><sub>(71 MiB / 24.5% CPU)</sub> | **16,529**<br><sub>(119 MiB / 23.1% CPU)</sub> | **31,159**<br><sub>(125 MiB / 48.4% CPU)</sub> |
| HTTP/2 · plain | HTTP/2 · plain | 🥇 **97,239**<br><sub>(90 MiB / 38% CPU)</sub> | *Not possible (no H2 upstream)* | **30,261**<br><sub>(69 MiB / 24.3% CPU)</sub> | **24,868**<br><sub>(116 MiB / 21.8% CPU)</sub> | **55,752**<br><sub>(122 MiB / 47.1% CPU)</sub> |
| HTTP/2 · plain | HTTP/2 · TLS | 🥇 **67,702**<br><sub>(93 MiB / 38.2% CPU)</sub> | *Not possible (no H2 upstream)* | **23,815**<br><sub>(68 MiB / 24.4% CPU)</sub> | **16,231**<br><sub>(118 MiB / 21.5% CPU)</sub> | **44,692**<br><sub>(129 MiB / 45.3% CPU)</sub> |
| HTTP/2 · plain | HTTP/3 · QUIC | 🥇 **30,188**<br><sub>(150 MiB / 51.4% CPU)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **5,340**<br><sub>(121 MiB / 24.8% CPU)</sub> | **28,445**<br><sub>(156 MiB / 45.5% CPU)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **38,662**<br><sub>(128 MiB / 52.3% CPU)</sub> | **17,745**<br><sub>(100 MiB / 18.8% CPU)</sub> | **24,393**<br><sub>(81 MiB / 24.3% CPU)</sub> | **17,919**<br><sub>(128 MiB / 22.5% CPU)</sub> | **34,084**<br><sub>(122 MiB / 48.1% CPU)</sub> |
| HTTP/2 · TLS | HTTP/1 · TLS | 🥇 **30,318**<br><sub>(119 MiB / 49.8% CPU)</sub> | **15,083**<br><sub>(110 MiB / 19.6% CPU)</sub> | **20,646**<br><sub>(85 MiB / 24.4% CPU)</sub> | **16,394**<br><sub>(128 MiB / 22.2% CPU)</sub> | **27,788**<br><sub>(131 MiB / 48.5% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | 🥇 **85,544**<br><sub>(102 MiB / 38% CPU)</sub> | *Not possible (no H2 upstream)* | **53,648**<br><sub>(85 MiB / 24.2% CPU)</sub> | **24,277**<br><sub>(126 MiB / 21.5% CPU)</sub> | **44,751**<br><sub>(125 MiB / 46.4% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **63,379**<br><sub>(99 MiB / 36% CPU)</sub> | *Not possible (no H2 upstream)* | **22,388**<br><sub>(84 MiB / 24.4% CPU)</sub> | **21,510**<br><sub>(125 MiB / 20.6% CPU)</sub> | **38,643**<br><sub>(128 MiB / 45.3% CPU)</sub> |
| HTTP/2 · TLS | HTTP/3 · QUIC | 🥇 **28,423**<br><sub>(151 MiB / 50.5% CPU)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **5,476**<br><sub>(129 MiB / 24.6% CPU)</sub> | **25,085**<br><sub>(163 MiB / 45.8% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | **23,715**<br><sub>(149 MiB / 52.2% CPU)</sub> | **0**<br><sub>(peak 19,206 · 107 MiB / 21.6% CPU)</sub> | 🥇 **30,835**<br><sub>(86 MiB / 25.2% CPU)</sub> | **3**<br><sub>(130 MiB / 0.1% CPU)</sub> | **21,546**<br><sub>(186 MiB / 49% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **19,505**<br><sub>(160 MiB / 49.3% CPU)</sub> | **0**<br><sub>(peak 14,850 · 117 MiB / 22.7% CPU)</sub> | **18,067**<br><sub>(94 MiB / 29.8% CPU)</sub> | **4**<br><sub>(peak 3,743 · 131 MiB / 23.1% CPU)</sub> | **18,423**<br><sub>(197 MiB / 49.9% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/2 · plain | 🥇 **31,386**<br><sub>(160 MiB / 56.2% CPU)</sub> | *Not possible (no H2 upstream)* | **29,864**<br><sub>(86 MiB / 25.4% CPU)</sub> | **18**<br><sub>(peak 4,898 · 138 MiB / 24.5% CPU)</sub> | **26,924**<br><sub>(197 MiB / 48% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | 🥇 **27,669**<br><sub>(154 MiB / 52.3% CPU)</sub> | *Not possible (no H2 upstream)* | **32,742**<br><sub>(peak 32,880 · 92 MiB / 25.9% CPU)</sub> | **5**<br><sub>(peak 4,411 · 139 MiB / 25.0% CPU)</sub> | **24,488**<br><sub>(199 MiB / 47.6% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/3 · QUIC | 🥇 **21,858**<br><sub>(164 MiB / 47.9% CPU)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **2,964**<br><sub>(peak 3,679 · 134 MiB / 24.8% CPU)</sub> | **17,840**<br><sub>(209 MiB / 47% CPU)</sub> |

### MITM (TWP only)

Same Client×Origin wires with interception on (`compare-product` [34441526151](https://github.com/justcoding121/titanium-web-proxy/actions/runs/34441526151)). **Lite** = no-op handlers (unchanged-lite finish). **Full** = append-only header mutation (harness: one probe header each way; product: generic append-only relay via `MitmCompressedRelayHelper`). nginx/HAProxy/Envoy/YARP cannot MITM. **Lite÷Reverse** / **Full÷Reverse** vs bare reverse (**same job / comparison-group shard**). Completion gate: Lite ≥ **0.50×** and Full ≥ **0.50×** reverse sustain @ c=64 (median of 3 GHA runs); reverse TWP÷YARP ≥ **0.75×** (no terminate-peer gate).

**v1 append-only relay (2026-08-27):** Pre-fix H2→H2 Full÷Reverse was **0.13–0.16×** ([32960766249](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32960766249)). Post-fix @ `df172718`: H2 plain→H2 plain Full **0.77–0.79×**, H3→H1 Full **0.91–0.93×**, all MITM arms ≥ **0.70×** on median of [33041445371](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33041445371), [33055267086](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33055267086), [33055272140](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33055272140).

**v2 drop-only + non-unique append (2026-08-27):** `MitmStaticRebuildHelper` rebuilds static HPACK/QPACK after 1–4 unique header drops; trailing non-unique appends stay on compressed relay. @ `9a2b3a1e`: all MITM arms ≥ **0.70×** on GHA median ([33087088466](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087088466), [33087091622](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087091622), [33105885748](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33105885748) Linux; [33087085235](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087085235), [33087088466](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087088466), [33087091622](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087091622) Windows). H2 plain→H2 plain Full **0.77–0.79×** (Win) / **0.78×** (Lin).

| Client | Origin | Lite sustain | Full sustain | Lite÷Reverse | Full÷Reverse |
|---|---|---:|---:|---:|---:|
| HTTP/1 · plain | HTTP/1 · plain | **35578**<br><sub>(88 MiB / 50.3% CPU)</sub> | **34914**<br><sub>(97 MiB / 50.4% CPU)</sub> | **0.97×** | **0.95×** |
| HTTP/1 · plain | HTTP/1 · TLS | **28326**<br><sub>(114 MiB / 49.8% CPU)</sub> | **28227**<br><sub>(114 MiB / 49.8% CPU)</sub> | **0.99×** | **0.99×** |
| HTTP/1 · plain | HTTP/2 · plain | **46274**<br><sub>(124 MiB / 54.5% CPU)</sub> | **44157**<br><sub>(135 MiB / 54.7% CPU)</sub> | **1.01×** | **0.97×** |
| HTTP/1 · plain | HTTP/2 · TLS | **37133**<br><sub>(147 MiB / 51% CPU)</sub> | **36654**<br><sub>(146 MiB / 51.6% CPU)</sub> | **0.98×** | **0.97×** |
| HTTP/1 · plain | HTTP/3 · QUIC | **26091**<br><sub>(142 MiB / 54.6% CPU)</sub> | **25394**<br><sub>(145 MiB / 54.4% CPU)</sub> | **1×** | **0.97×** |
| HTTP/1 · TLS | HTTP/1 · plain | **27963**<br><sub>(116 MiB / 50.4% CPU)</sub> | **27273**<br><sub>(118 MiB / 50% CPU)</sub> | **1.01×** | **0.98×** |
| HTTP/1 · TLS | HTTP/1 · TLS | **24331**<br><sub>(118 MiB / 49% CPU)</sub> | **23589**<br><sub>(116 MiB / 48.9% CPU)</sub> | **1.02×** | **0.99×** |
| HTTP/1 · TLS | HTTP/2 · plain | **33674**<br><sub>(144 MiB / 52.4% CPU)</sub> | **32894**<br><sub>(144 MiB / 52% CPU)</sub> | **0.99×** | **0.97×** |
| HTTP/1 · TLS | HTTP/2 · TLS | **29341**<br><sub>(163 MiB / 49.4% CPU)</sub> | **29024**<br><sub>(160 MiB / 50.3% CPU)</sub> | **0.99×** | **0.98×** |
| HTTP/1 · TLS | HTTP/3 · QUIC | **21874**<br><sub>(159 MiB / 53.2% CPU)</sub> | **21237**<br><sub>(162 MiB / 53.3% CPU)</sub> | **0.98×** | **0.95×** |
| HTTP/2 · plain | HTTP/1 · plain | **39834**<br><sub>(120 MiB / 54% CPU)</sub> | **38577**<br><sub>(118 MiB / 53.7% CPU)</sub> | **0.97×** | **0.93×** |
| HTTP/2 · plain | HTTP/1 · TLS | **32663**<br><sub>(135 MiB / 52.2% CPU)</sub> | **31085**<br><sub>(123 MiB / 51.7% CPU)</sub> | **1×** | **0.95×** |
| HTTP/2 · plain | HTTP/2 · plain | **73656**<br><sub>(93 MiB / 43.9% CPU)</sub> | **71161**<br><sub>(97 MiB / 42.4% CPU)</sub> | **0.76×** | **0.73×** |
| HTTP/2 · plain | HTTP/2 · TLS | **55089**<br><sub>(105 MiB / 41.2% CPU)</sub> | **51723**<br><sub>(99 MiB / 40.6% CPU)</sub> | **0.81×** | **0.76×** |
| HTTP/2 · plain | HTTP/3 · QUIC | **30054**<br><sub>(151 MiB / 51.4% CPU)</sub> | **29788**<br><sub>(156 MiB / 51.7% CPU)</sub> | **1×** | **0.99×** |
| HTTP/2 · TLS | HTTP/1 · plain | **36903**<br><sub>(123 MiB / 53.2% CPU)</sub> | **35802**<br><sub>(127 MiB / 53.5% CPU)</sub> | **0.95×** | **0.93×** |
| HTTP/2 · TLS | HTTP/1 · TLS | **30528**<br><sub>(130 MiB / 51.2% CPU)</sub> | **29484**<br><sub>(124 MiB / 50.3% CPU)</sub> | **1.01×** | **0.97×** |
| HTTP/2 · TLS | HTTP/2 · plain | **63418**<br><sub>(114 MiB / 43% CPU)</sub> | **60875**<br><sub>(111 MiB / 42.8% CPU)</sub> | **0.74×** | **0.71×** |
| HTTP/2 · TLS | HTTP/2 · TLS | **52660**<br><sub>(106 MiB / 40.8% CPU)</sub> | **49767**<br><sub>(106 MiB / 39.5% CPU)</sub> | **0.83×** | **0.79×** |
| HTTP/2 · TLS | HTTP/3 · QUIC | **28301**<br><sub>(166 MiB / 51.1% CPU)</sub> | **27450**<br><sub>(154 MiB / 50.7% CPU)</sub> | **1×** | **0.97×** |
| HTTP/3 · QUIC | HTTP/1 · plain | **22951**<br><sub>(150 MiB / 52.3% CPU)</sub> | **22840**<br><sub>(150 MiB / 52.4% CPU)</sub> | **0.97×** | **0.96×** |
| HTTP/3 · QUIC | HTTP/1 · TLS | **18924**<br><sub>(165 MiB / 50.9% CPU)</sub> | **18029**<br><sub>(157 MiB / 49.7% CPU)</sub> | **0.97×** | **0.92×** |
| HTTP/3 · QUIC | HTTP/2 · plain | **30229**<br><sub>(167 MiB / 57.6% CPU)</sub> | **29277**<br><sub>(163 MiB / 56.6% CPU)</sub> | **0.96×** | **0.93×** |
| HTTP/3 · QUIC | HTTP/2 · TLS | **26262**<br><sub>(163 MiB / 53.8% CPU)</sub> | **25788**<br><sub>(168 MiB / 53.4% CPU)</sub> | **0.95×** | **0.93×** |
| HTTP/3 · QUIC | HTTP/3 · QUIC | **20948**<br><sub>(163 MiB / 50.8% CPU)</sub> | **20826**<br><sub>(162 MiB / 50.9% CPU)</sub> | **0.96×** | **0.95×** |

## macOS — Titanium vs nginx vs HAProxy vs Envoy vs YARP

### Reverse

Median of **3 repeats** on `macos-15-intel` (4-core / 14 GB). Bare reverse 5×5 @ `9a2b3a1e` — `compare-product` [34441526151](https://github.com/justcoding121/titanium-web-proxy/actions/runs/34441526151); Envoy H3 inbound remainder @ `a495a9ae` — [34557758404](https://github.com/justcoding121/titanium-web-proxy/actions/runs/34557758404)–[34557765742](https://github.com/justcoding121/titanium-web-proxy/actions/runs/34557765742). Warmup 2s / measure 8s; concurrency 8, 16, 32, 64. Prefer TWP÷peer ratios over absolute RPS. **RPS cells** show sustain RPS; `<br><sub>` holds peak (when higher) plus median RSS / CPU at the peak-RPS step. The RPS workflow installs Homebrew nginx (`http_v3_module`), Homebrew HAProxy with `USE_QUIC` (3.2 source fallback), Envoy (Homebrew bottle or pinned darwin-amd64 1.36.7), Homebrew `libmsquic` (+ `DYLD_*`), and YARP. Do not publish from `macos-latest` (3-core / 7 GB). Product 5×5 is **~56-byte JSON keep-alive GET**; H2/H3 same-protocol cells are mostly header work with a tiny body (Titanium best case) — see [Why this comparison is fair](#why-this-comparison-is-fair).

| Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---:|---:|---:|---:|---:|
| HTTP/1 · plain | HTTP/1 · plain | 🥇 **12,131**<br><sub>(82 MiB / 32.8% CPU)</sub> | **6,938**<br><sub>(52 MiB / 11.6% CPU)</sub> | **10,945**<br><sub>(48 MiB / 23.2% CPU)</sub> | **3,332**<br><sub>(72 MiB / 22.4% CPU)</sub> | **10,805**<br><sub>(105 MiB / 33.6% CPU)</sub> |
| HTTP/1 · plain | HTTP/1 · TLS | 🥇 **11,064**<br><sub>(93 MiB / 36.5% CPU)</sub> | **5,152**<br><sub>(73 MiB / 17.4% CPU)</sub> | **7,159**<br><sub>(53 MiB / 25.9% CPU)</sub> | **0**<br><sub>(peak 2,011 · 75 MiB / 21% CPU)</sub> | **9,146**<br><sub>(129 MiB / 36.8% CPU)</sub> |
| HTTP/1 · plain | HTTP/2 · plain | 🥇 **19,185**<br><sub>(88 MiB / 33.8% CPU)</sub> | *Not possible (no H2 upstream)* | **11,853**<br><sub>(50 MiB / 25.6% CPU)</sub> | **6,000**<br><sub>(73 MiB / 36.6% CPU)</sub> | **18,729**<br><sub>(111 MiB / 34% CPU)</sub> |
| HTTP/1 · plain | HTTP/2 · TLS | **9,927**<br><sub>(129 MiB / 32.9% CPU)</sub> | *Not possible (no H2 upstream)* | **8,770**<br><sub>(51 MiB / 26.5% CPU)</sub> | **5,948**<br><sub>(73 MiB / 34.9% CPU)</sub> | 🥇 **13,674**<br><sub>(116 MiB / 32.9% CPU)</sub> |
| HTTP/1 · plain | HTTP/3 · QUIC | **3,662**<br><sub>(90 MiB / 45.3% CPU)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **3,393**<br><sub>(75 MiB / 38.2% CPU)</sub> | 🥇 **5,774**<br><sub>(116 MiB / 33.4% CPU)</sub> |
| HTTP/1 · TLS | HTTP/1 · plain | 🥇 **14,162**<br><sub>(94 MiB / 31.5% CPU)</sub> | **8,184**<br><sub>(74 MiB / 18.3% CPU)</sub> | **9,938**<br><sub>(64 MiB / 25.6% CPU)</sub> | **4,084**<br><sub>(peak 4,328 · 79 MiB / 37.4% CPU)</sub> | **7,647**<br><sub>(115 MiB / 32.3% CPU)</sub> |
| HTTP/1 · TLS | HTTP/1 · TLS | **6,128**<br><sub>(97 MiB / 31.7% CPU)</sub> | **4,903**<br><sub>(81 MiB / 15.5% CPU)</sub> | 🥇 **9,241**<br><sub>(67 MiB / 28% CPU)</sub> | **3,594**<br><sub>(80 MiB / 38.9% CPU)</sub> | **6,601**<br><sub>(172 MiB / 35% CPU)</sub> |
| HTTP/1 · TLS | HTTP/2 · plain | **8,317**<br><sub>(96 MiB / 32.8% CPU)</sub> | *Not possible (no H2 upstream)* | 🥇 **12,353**<br><sub>(66 MiB / 28% CPU)</sub> | **5,378**<br><sub>(80 MiB / 20.9% CPU)</sub> | **10,735**<br><sub>(122 MiB / 32.2% CPU)</sub> |
| HTTP/1 · TLS | HTTP/2 · TLS | **8,457**<br><sub>(161 MiB / 33.8% CPU)</sub> | *Not possible (no H2 upstream)* | **8,558**<br><sub>(65 MiB / 29.3% CPU)</sub> | **4,938**<br><sub>(peak 5,195 · 79 MiB / 39.8% CPU)</sub> | 🥇 **9,023**<br><sub>(120 MiB / 28.7% CPU)</sub> |
| HTTP/1 · TLS | HTTP/3 · QUIC | **2,677**<br><sub>(112 MiB / 44.9% CPU)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | 🥇 **4,392**<br><sub>(81 MiB / 37.8% CPU)</sub> | **3,589**<br><sub>(152 MiB / 31.7% CPU)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | **12,398**<br><sub>(88 MiB / 35.1% CPU)</sub> | **10,718**<br><sub>(58 MiB / 15.4% CPU)</sub> | **7,841**<br><sub>(54 MiB / 18% CPU)</sub> | **3,391**<br><sub>(74 MiB / 20.7% CPU)</sub> | 🥇 **12,934**<br><sub>(105 MiB / 37.8% CPU)</sub> |
| HTTP/2 · plain | HTTP/1 · TLS | **13,814**<br><sub>(94 MiB / 38.6% CPU)</sub> | **12,186**<br><sub>(85 MiB / 16% CPU)</sub> | **6,688**<br><sub>(56 MiB / 18.9% CPU)</sub> | **5,045**<br><sub>(77 MiB / 21.3% CPU)</sub> | 🥇 **14,337**<br><sub>(121 MiB / 42.3% CPU)</sub> |
| HTTP/2 · plain | HTTP/2 · plain | 🥇 **30,992**<br><sub>(73 MiB / 25.8% CPU)</sub> | *Not possible (no H2 upstream)* | **8,615**<br><sub>(54 MiB / 17.5% CPU)</sub> | **5,442**<br><sub>(72 MiB / 20% CPU)</sub> | **22,898**<br><sub>(109 MiB / 37.5% CPU)</sub> |
| HTTP/2 · plain | HTTP/2 · TLS | 🥇 **24,573**<br><sub>(77 MiB / 27.7% CPU)</sub> | *Not possible (no H2 upstream)* | **11,876**<br><sub>(58 MiB / 19% CPU)</sub> | **6,393**<br><sub>(73 MiB / 20.2% CPU)</sub> | **18,700**<br><sub>(116 MiB / 34.6% CPU)</sub> |
| HTTP/2 · plain | HTTP/3 · QUIC | **4,840**<br><sub>(97 MiB / 49.3% CPU)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **3,760**<br><sub>(74 MiB / 22.4% CPU)</sub> | 🥇 **8,092**<br><sub>(117 MiB / 33.9% CPU)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **13,829**<br><sub>(94 MiB / 39.2% CPU)</sub> | **9,070**<br><sub>(73 MiB / 14.5% CPU)</sub> | **5,237**<br><sub>(66 MiB / 16.1% CPU)</sub> | **3,516**<br><sub>(81 MiB / 20.4% CPU)</sub> | **13,572**<br><sub>(113 MiB / 37.3% CPU)</sub> |
| HTTP/2 · TLS | HTTP/1 · TLS | 🥇 **9,182**<br><sub>(96 MiB / 37.3% CPU)</sub> | **6,812**<br><sub>(90 MiB / 15.3% CPU)</sub> | **4,920**<br><sub>(66 MiB / 18.2% CPU)</sub> | **2,505**<br><sub>(83 MiB / 20.1% CPU)</sub> | **9,080**<br><sub>(124 MiB / 40.6% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | 🥇 **47,325**<br><sub>(81 MiB / 29% CPU)</sub> | *Not possible (no H2 upstream)* | **5,750**<br><sub>(67 MiB / 5.9% CPU)</sub> | **4,704**<br><sub>(79 MiB / 19% CPU)</sub> | **22,095**<br><sub>(115 MiB / 37.6% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **29,038**<br><sub>(83 MiB / 27.1% CPU)</sub> | *Not possible (no H2 upstream)* | **6,022**<br><sub>(67 MiB / 18.3% CPU)</sub> | **4,494**<br><sub>(79 MiB / 18.6% CPU)</sub> | **16,458**<br><sub>(118 MiB / 35% CPU)</sub> |
| HTTP/2 · TLS | HTTP/3 · QUIC | **4,226**<br><sub>(108 MiB / 36% CPU)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **1,772**<br><sub>(81 MiB / 21.6% CPU)</sub> | 🥇 **4,805**<br><sub>(125 MiB / 33.7% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | **5,834**<br><sub>(101 MiB / 39.5% CPU)</sub> | **0**<br><sub>(peak 7,464 · 63 MiB / 11.2% CPU)</sub> | 🥇 **10,727**<br><sub>(66 MiB / 22% CPU)</sub> | **1,595**<br><sub>(85 MiB / 26.4% CPU)</sub> | **5,558**<br><sub>(187 MiB / 35% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | **3,723**<br><sub>(119 MiB / 38.2% CPU)</sub> | **0**<br><sub>(peak 4,316 · 66 MiB / 11.1% CPU)</sub> | 🥇 **5,430**<br><sub>(69 MiB / 21.8% CPU)</sub> | **1,893**<br><sub>(89 MiB / 31.2% CPU)</sub> | **4,786**<br><sub>(197 MiB / 35.5% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/2 · plain | **4,576**<br><sub>(97 MiB / 41.3% CPU)</sub> | *Not possible (no H2 upstream)* | 🥇 **5,954**<br><sub>(66 MiB / 14.4% CPU)</sub> | **1,262**<br><sub>(83 MiB / 25.3% CPU)</sub> | **5,642**<br><sub>(176 MiB / 34.2% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | **5,532**<br><sub>(106 MiB / 41.4% CPU)</sub> | *Not possible (no H2 upstream)* | **6,633**<br><sub>(68 MiB / 21.5% CPU)</sub> | **2,692**<br><sub>(84 MiB / 32.1% CPU)</sub> | 🥇 **7,854**<br><sub>(167 MiB / 32.5% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/3 · QUIC | **3,460**<br><sub>(96 MiB / 35% CPU)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **1,036**<br><sub>(peak 1,116 · 82 MiB / 28.4% CPU)</sub> | 🥇 **4,659**<br><sub>(188 MiB / 34.4% CPU)</sub> |

### MITM (TWP only)

Same Client×Origin wires with interception on (`compare-product` [34441526151](https://github.com/justcoding121/titanium-web-proxy/actions/runs/34441526151)). **Lite** = no-op handlers (unchanged-lite finish). **Full** = append-only header mutation (harness: one probe header each way; product: generic append-only relay via `MitmCompressedRelayHelper`). nginx/HAProxy/Envoy/YARP cannot MITM. **Lite÷Reverse** / **Full÷Reverse** vs bare reverse (**same job / comparison-group shard**). Completion gate: Lite ≥ **0.50×** and Full ≥ **0.50×** reverse sustain @ c=64 (median of 3 GHA runs); reverse TWP÷YARP ≥ **0.75×** (no terminate-peer gate).

**v1 append-only relay (2026-08-27):** Pre-fix H2→H2 Full÷Reverse was **0.13–0.16×** ([32960766249](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32960766249)). Post-fix @ `df172718`: H2 plain→H2 plain Full **0.77–0.79×**, H3→H1 Full **0.91–0.93×**, all MITM arms ≥ **0.70×** on median of [33041445371](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33041445371), [33055267086](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33055267086), [33055272140](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33055272140).

**v2 drop-only + non-unique append (2026-08-27):** `MitmStaticRebuildHelper` rebuilds static HPACK/QPACK after 1–4 unique header drops; trailing non-unique appends stay on compressed relay. @ `9a2b3a1e`: all MITM arms ≥ **0.70×** on GHA median ([33087088466](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087088466), [33087091622](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087091622), [33105885748](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33105885748) Linux; [33087085235](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087085235), [33087088466](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087088466), [33087091622](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087091622) Windows). H2 plain→H2 plain Full **0.77–0.79×** (Win) / **0.78×** (Lin).

| Client | Origin | Lite sustain | Full sustain | Lite÷Reverse | Full÷Reverse |
|---|---|---:|---:|---:|---:|
| HTTP/1 · plain | HTTP/1 · plain | **11715**<br><sub>(84 MiB / 35.6% CPU)</sub> | **11001**<br><sub>(84 MiB / 31.8% CPU)</sub> | **0.97×** | **0.91×** |
| HTTP/1 · plain | HTTP/1 · TLS | **7193**<br><sub>(94 MiB / 31.1% CPU)</sub> | **8678**<br><sub>(94 MiB / 35.3% CPU)</sub> | **0.65×** | **0.78×** |
| HTTP/1 · plain | HTTP/2 · plain | **14866**<br><sub>(89 MiB / 36.3% CPU)</sub> | **13716**<br><sub>(90 MiB / 35.7% CPU)</sub> | **0.77×** | **0.71×** |
| HTTP/1 · plain | HTTP/2 · TLS | **11164**<br><sub>(109 MiB / 33.4% CPU)</sub> | **9186**<br><sub>(108 MiB / 32.6% CPU)</sub> | **1.12×** | **0.93×** |
| HTTP/1 · plain | HTTP/3 · QUIC | **3459**<br><sub>(91 MiB / 39.6% CPU)</sub> | **3710**<br><sub>(90 MiB / 40.3% CPU)</sub> | **0.94×** | **1.01×** |
| HTTP/1 · TLS | HTTP/1 · plain | **8052**<br><sub>(95 MiB / 30.3% CPU)</sub> | **10123**<br><sub>(96 MiB / 33.1% CPU)</sub> | **0.57×** | **0.71×** |
| HTTP/1 · TLS | HTTP/1 · TLS | **6914**<br><sub>(98 MiB / 36% CPU)</sub> | **9190**<br><sub>(98 MiB / 33.3% CPU)</sub> | **1.13×** | **1.5×** |
| HTTP/1 · TLS | HTTP/2 · plain | **8443**<br><sub>(123 MiB / 35.7% CPU)</sub> | **8055**<br><sub>(102 MiB / 33.1% CPU)</sub> | **1.02×** | **0.97×** |
| HTTP/1 · TLS | HTTP/2 · TLS | **8738**<br><sub>(133 MiB / 33.2% CPU)</sub> | **7260**<br><sub>(157 MiB / 32.6% CPU)</sub> | **1.03×** | **0.86×** |
| HTTP/1 · TLS | HTTP/3 · QUIC | **2633**<br><sub>(105 MiB / 42.6% CPU)</sub> | **2842**<br><sub>(102 MiB / 46.3% CPU)</sub> | **0.98×** | **1.06×** |
| HTTP/2 · plain | HTTP/1 · plain | **13742**<br><sub>(88 MiB / 38.4% CPU)</sub> | **13895**<br><sub>(90 MiB / 37.5% CPU)</sub> | **1.11×** | **1.12×** |
| HTTP/2 · plain | HTTP/1 · TLS | **14725**<br><sub>(96 MiB / 40.8% CPU)</sub> | **10701**<br><sub>(96 MiB / 35.5% CPU)</sub> | **1.07×** | **0.77×** |
| HTTP/2 · plain | HTTP/2 · plain | **24126**<br><sub>(76 MiB / 29.9% CPU)</sub> | **24457**<br><sub>(76 MiB / 29.7% CPU)</sub> | **0.78×** | **0.79×** |
| HTTP/2 · plain | HTTP/2 · TLS | **23664**<br><sub>(83 MiB / 31.7% CPU)</sub> | **24893**<br><sub>(81 MiB / 31% CPU)</sub> | **0.96×** | **1.01×** |
| HTTP/2 · plain | HTTP/3 · QUIC | **4822**<br><sub>(96 MiB / 45% CPU)</sub> | **5035**<br><sub>(95 MiB / 45.9% CPU)</sub> | **1×** | **1.04×** |
| HTTP/2 · TLS | HTTP/1 · plain | **13114**<br><sub>(92 MiB / 38.3% CPU)</sub> | **12954**<br><sub>(91 MiB / 38.1% CPU)</sub> | **0.95×** | **0.94×** |
| HTTP/2 · TLS | HTTP/1 · TLS | **9205**<br><sub>(97 MiB / 36% CPU)</sub> | **9788**<br><sub>(97 MiB / 39.1% CPU)</sub> | **1×** | **1.07×** |
| HTTP/2 · TLS | HTTP/2 · plain | **28081**<br><sub>(86 MiB / 32% CPU)</sub> | **24916**<br><sub>(87 MiB / 30.4% CPU)</sub> | **0.59×** | **0.53×** |
| HTTP/2 · TLS | HTTP/2 · TLS | **22874**<br><sub>(87 MiB / 29.6% CPU)</sub> | **29489**<br><sub>(89 MiB / 32% CPU)</sub> | **0.79×** | **1.02×** |
| HTTP/2 · TLS | HTTP/3 · QUIC | **4679**<br><sub>(105 MiB / 37.9% CPU)</sub> | **4453**<br><sub>(106 MiB / 39.3% CPU)</sub> | **1.11×** | **1.05×** |
| HTTP/3 · QUIC | HTTP/1 · plain | **4000**<br><sub>(102 MiB / 35% CPU)</sub> | **4530**<br><sub>(102 MiB / 38.8% CPU)</sub> | **0.69×** | **0.78×** |
| HTTP/3 · QUIC | HTTP/1 · TLS | **3931**<br><sub>(116 MiB / 38.3% CPU)</sub> | **3992**<br><sub>(115 MiB / 40% CPU)</sub> | **1.06×** | **1.07×** |
| HTTP/3 · QUIC | HTTP/2 · plain | **4272**<br><sub>(98 MiB / 39% CPU)</sub> | **4718**<br><sub>(98 MiB / 39.8% CPU)</sub> | **0.93×** | **1.03×** |
| HTTP/3 · QUIC | HTTP/2 · TLS | **5183**<br><sub>(106 MiB / 38.5% CPU)</sub> | **4838**<br><sub>(108 MiB / 38.9% CPU)</sub> | **0.94×** | **0.87×** |
| HTTP/3 · QUIC | HTTP/3 · QUIC | **3211**<br><sub>(94 MiB / 37.3% CPU)</sub> | **3887**<br><sub>(95 MiB / 37.3% CPU)</sub> | **0.93×** | **1.12×** |

## Editions (CLI / Plus / Intercept)

**Note:** `twp-reverse-http1` and other library rows use Core with **probe-tuned** settings (no logging, no Via header, probe-warmed certs). Edition rows use `titanium run -c twp.yaml` **product defaults** — prefer the ÷baseline ratio column over absolute RPS. Inspector GUI is not spawnable in the harness; session-path overhead is `twp-cli-intercept-http1` (route `RequestHeaderSet` transform). Pre-origin Plus middleware (CIDR/WAF/JWT/rate-limit) runs on H1 terminate-lite without `SessionEventArgs`; JWT caches successful bearer validations. Maintainer gate thresholds live under [Maintainer notes](#maintainer-notes).

Median of **3** repeats @ `6d2a7c9d`. Source: Actions [33259699099](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33259699099). Warmup 2s / measure 8s; concurrency 8–64; sustain = median peak RPS among SLO-pass steps @ **c=64**. **RPS cells** show sustain; `<sub>` holds peak (when higher) plus `(MiB / CPU%)`.

| Arm | Win | Linux | Win÷ | Lin÷ | Gate |
|---|---:|---:|---:|---:|---|
| `twp-cli-reverse-http1` vs library | **32,214**<br><sub>(peak 32,219 · 118 MiB / 43.4% CPU)</sub> | **47,734**<br><sub>(peak 47,844 · 151 MiB / 49.0% CPU)</sub> | **1.02×** | **1.04×** | ≥ **0.80×** |
| `twp-cli-reverse-http1-tls` vs library TLS | **26,495**<br><sub>(peak 26,651 · 138 MiB / 48.3% CPU)</sub> | **36,932**<br><sub>(peak 37,200 · 174 MiB / 48.5% CPU)</sub> | **1.01×** | **1.00×** | ≥ **0.80×** |
| `twp-cli-reverse-http1-route` vs CLI | **32,394**<br><sub>(peak 32,428 · 120 MiB / 47.7% CPU)</sub> | **47,369**<br><sub>(peak 47,398 · 149 MiB / 49.2% CPU)</sub> | **1.01×** | **0.99×** | ≥ **0.90×** |
| `twp-cli-plus-base-http1` vs CLI | **32,475**<br><sub>(peak 32,695 · 123 MiB / 46.0% CPU)</sub> | **47,322**<br><sub>(peak 47,744 · 154 MiB / 49.8% CPU)</sub> | **1.01×** | **0.99×** | ≥ **0.90×** |
| `twp-cli-plus-cache-http1` (cold) vs CLI | **34,029**<br><sub>(peak 34,460 · 118 MiB / 64.5% CPU)</sub> | **49,409**<br><sub>(peak 50,104 · 148 MiB / 62.3% CPU)</sub> | **1.06×** | **1.04×** | ≥ **0.70×** |
| `twp-cli-intercept-http1` vs CLI | **25,219**<br><sub>(peak 25,377 · 124 MiB / 54.0% CPU)</sub> | **35,969**<br><sub>(peak 35,977 · 155 MiB / 51.2% CPU)</sub> | **0.78×** | **0.75×** | ≥ **0.70×** |
| `twp-cli-plus-waf-http1` vs CLI | **31,653**<br><sub>(peak 31,837 · 124 MiB / 46.8% CPU)</sub> | **46,367**<br><sub>(peak 46,685 · 151 MiB / 49.6% CPU)</sub> | **0.98×** | **0.97×** | ≥ **0.80×** |
| `twp-cli-plus-cidr-http1` vs CLI | **31,730**<br><sub>(peak 31,757 · 123 MiB / 51.1% CPU)</sub> | **46,828**<br><sub>(peak 47,093 · 150 MiB / 50.2% CPU)</sub> | **0.98×** | **0.98×** | ≥ **0.80×** |
| `twp-cli-plus-jwt-http1` vs CLI | **30,326**<br><sub>(peak 30,386 · 138 MiB / 46.6% CPU)</sub> | **43,906**<br><sub>(peak 44,433 · 179 MiB / 49.5% CPU)</sub> | **0.94×** | **0.92×** | ≥ **0.70×** |
| `twp-cli-plus-ratelimit-http1` vs CLI | **31,713**<br><sub>(peak 31,944 · 123 MiB / 48.8% CPU)</sub> | **46,435**<br><sub>(peak 46,860 · 151 MiB / 49.4% CPU)</sub> | **0.98×** | **0.97×** | ≥ **0.80×** |
| `twp-cli-plus-resilience-http1` vs CLI | **32,373**<br><sub>(peak 32,416 · 128 MiB / 50.5% CPU)</sub> | **47,302**<br><sub>(peak 47,654 · 155 MiB / 49.5% CPU)</sub> | **1.00×** | **0.99×** | ≥ **0.85×** |
| `twp-cli-plus-discovery-file-http1` vs CLI | **32,209**<br><sub>(peak 32,332 · 121 MiB / 46.5% CPU)</sub> | **47,485**<br><sub>(peak 47,770 · 151 MiB / 49.1% CPU)</sub> | **1.00×** | **0.99×** | ≥ **0.80×** |
| `twp-cli-plus-metrics-scrape-http1` vs CLI | **32,192**<br><sub>(peak 32,427 · 126 MiB / 44.8% CPU)</sub> | **47,348**<br><sub>(peak 48,665 · 159 MiB / 49.5% CPU)</sub> | **1.00×** | **0.99×** | ≥ **0.80×** |
| `twp-cli-plus-cache-hit-http1` vs cache cold | **34,015**<br><sub>(peak 34,022 · 117 MiB / 66.0% CPU)</sub> | **49,153**<br><sub>(peak 49,358 · 149 MiB / 63.1% CPU)</sub> | **1.00×** | **0.99×** | ≥ **0.90×** |
| `twp-cli-static-http1` vs CLI | **53,078**<br><sub>(peak 53,980 · 109 MiB / 52.1% CPU)</sub> | **78,333**<br><sub>(peak 82,955 · 150 MiB / 48.4% CPU)</sub> | **1.65×** | **1.64×** | ≥ **0.85×** |
| `twp-cli-logging-http1` vs CLI | **32,440**<br><sub>(peak 32,581 · 119 MiB / 47.4% CPU)</sub> | **47,624**<br><sub>(peak 48,604 · 150 MiB / 49.3% CPU)</sub> | **1.01×** | **1.00×** | ≥ **0.90×** |
| `twp-cli-lb-leasttime-http1` vs route | **29,649**<br><sub>(peak 30,386 · 134 MiB / 54.7% CPU)</sub> | **44,790**<br><sub>(peak 44,825 · 180 MiB / 50.6% CPU)</sub> | **0.92×** | **0.95×** | ≥ **0.85×** |
| `twp-cli-dialect-twp-http1` vs CLI | **32,461**<br><sub>(peak 32,780 · 114 MiB / 49.7% CPU)</sub> | **47,898**<br><sub>(peak 48,154 · 145 MiB / 49.3% CPU)</sub> | **1.01×** | **1.00×** | ≥ **0.90×** |

`validate-edition-gates.ps1` **passed** on both OS for this run. Library baselines @ c=64 (same job): Win H1 **31561** / TLS **26128**; Linux H1 **45818** / TLS **36869**. Laptop smoke ratios stay on [Performance Local Lab — Editions](Performance-Local-Lab#editions-cli--plus-stress).

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

Same runners and harness as the tiny-GET tables, but with larger bodies, POST, lossy links, TLS cost, and architecture-sensitive paths (slow consumer / early response / duplex). **PUT with the same body is the same proxy work as POST; DELETE with no body matches GET** — only POST is published. Bodies/POST/lossy stay **half-duplex**. Laptop numbers are on [Performance Local Lab](Performance-Local-Lab#architecture-sensitive). How maintainers refresh these tables (modes, shards, paste scripts) is under [Maintainer notes](#maintainer-notes). **Larger-body check:** 64 / 256 KiB H2 TLS→H2 TLS is where body copy dominates headers — Titanium is **behind** YARP here (~0.69–0.76× at 64 KiB), unlike the tiny-GET H2↔H2 medals above.

Lossy link = **userspace** delay/drop shim (not kernel `netem`): TCP gets per-buffer delay + occasional whole-connection stalls (honest head-of-line for multiplexed HTTP/2); UDP gets per-datagram delay + drops (QUIC). Lossy tables publish HTTP/1, HTTP/2, and HTTP/3.

### Windows — heavier reverse GET (64 KiB / 256 KiB)

Median of **3** repeats on `windows-latest` @ `9a2b3a1e`. Source: Actions [34441570199](https://github.com/justcoding121/titanium-web-proxy/actions/runs/34441570199) (`compare-bodies`). Warmup 2s / measure 8s. **RPS cells** show sustain; `<sub>` holds peak (when higher) plus `(MiB / CPU%)`.

*Not possible:* **HAProxy** and **Envoy** columns are omitted (no official Windows port).

| Body | Client | Origin | TWP | nginx | YARP |
|---|---|---|---:|---:|---:|
| 64 KiB | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **13,191**<br><sub>(122 MiB / 46.7% CPU)</sub> | **924**<br><sub>(142 MiB / 24.8% CPU)</sub> | **11,473**<br><sub>(133 MiB / 47.5% CPU)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **11,769**<br><sub>(173 MiB / 46.2% CPU)</sub> | **898**<br><sub>(142 MiB / 24.8% CPU)</sub> | **9,560**<br><sub>(135 MiB / 48.1% CPU)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **5,958**<br><sub>(139 MiB / 41.5% CPU)</sub> | *Not possible (no QUIC)* | **5,060**<br><sub>(185 MiB / 51.0% CPU)</sub> |
| 256 KiB | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **3,693**<br><sub>(136 MiB / 42.1% CPU)</sub> | **241**<br><sub>(142 MiB / 24.9% CPU)</sub> | **3,325**<br><sub>(130 MiB / 47.2% CPU)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **3,784**<br><sub>(149 MiB / 36.2% CPU)</sub> | **230**<br><sub>(142 MiB / 24.8% CPU)</sub> | **2,625**<br><sub>(135 MiB / 45.2% CPU)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **1,535**<br><sub>(111 MiB / 40.4% CPU)</sub> | *Not possible (no QUIC)* | **1,385**<br><sub>(169 MiB / 44.6% CPU)</sub> |
| 64 KiB | HTTP/2 · plain | HTTP/1 · plain | 🥇 **14,010**<br><sub>(174 MiB / 40.5% CPU)</sub> | **5,865**<br><sub>(127 MiB / 24.0% CPU)</sub> | **12,443**<br><sub>(111 MiB / 39.6% CPU)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/2 · plain | **6,319**<br><sub>(79 MiB / 38.1% CPU)</sub> | *Not possible* | 🥇 **9,493**<br><sub>(131 MiB / 50.3% CPU)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/2 · TLS | **5,660**<br><sub>(80 MiB / 36.4% CPU)</sub> | *Not possible* | 🥇 **8,201**<br><sub>(141 MiB / 47.6% CPU)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/2 · TLS | **4,657**<br><sub>(129 MiB / 46.1% CPU)</sub> | *Not possible* | 🥇 **5,021**<br><sub>(195 MiB / 47.6% CPU)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **6,149**<br><sub>(148 MiB / 42.2% CPU)</sub> | *Not possible (no QUIC)* | **4,496**<br><sub>(191 MiB / 49.0% CPU)</sub> |
| 256 KiB | HTTP/2 · plain | HTTP/1 · plain | 🥇 **4,457**<br><sub>(144 MiB / 31.8% CPU)</sub> | **1,759**<br><sub>(127 MiB / 23.6% CPU)</sub> | **3,719**<br><sub>(123 MiB / 37.8% CPU)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/2 · plain | **1,976**<br><sub>(84 MiB / 29.9% CPU)</sub> | *Not possible* | 🥇 **2,325**<br><sub>(169 MiB / 46.5% CPU)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/2 · TLS | **1,758**<br><sub>(87 MiB / 29.7% CPU)</sub> | *Not possible* | 🥇 **2,109**<br><sub>(153 MiB / 44.9% CPU)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/2 · TLS | **1,164**<br><sub>(153 MiB / 43.0% CPU)</sub> | *Not possible* | 🥇 **1,319**<br><sub>(186 MiB / 44.8% CPU)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **1,404**<br><sub>(146 MiB / 41.0% CPU)</sub> | *Not possible (no QUIC)* | **1,269**<br><sub>(196 MiB / 44.8% CPU)</sub> |

nginx/Windows collapses on large reverse bodies in this harness; treat as same-OS only. H1 TLS **64 KiB** ≈ **1.11×** YARP; **256 KiB** ≈ **1.23×**. H2→H1 64 KiB ≈ **1.21×**; H3→H1 64 KiB ≈ **1.18×**.

### Linux — heavier reverse GET (64 KiB / 256 KiB)

Median of **3** repeats @ `a495a9ae`. Source: Actions [34557778171](https://github.com/justcoding121/titanium-web-proxy/actions/runs/34557778171) + [34557780393](https://github.com/justcoding121/titanium-web-proxy/actions/runs/34557780393) (`compare-bodies`; H3 HAProxy/Envoy peers). Warmup 2s / measure 8s.

| Body | Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---|---:|---:|---:|---:|---:|
| 64 KiB | HTTP/1 · TLS | HTTP/1 · plain | **15,191**<br><sub>(177 MiB / 41.8% CPU)</sub> | **10,978**<br><sub>(103 MiB / 44.9% CPU)</sub> | 🥇 **17,384**<br><sub>(85 MiB / 36.7% CPU)</sub> | **14,303**<br><sub>(131 MiB / 45.2% CPU)</sub> | **11,995**<br><sub>(154 MiB / 48.6% CPU)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **8,911**<br><sub>(238 MiB / 39.8% CPU)</sub> | **2,775**<br><sub>(103 MiB / 16.7% CPU)</sub> | **8,042**<br><sub>(84 MiB / 24.1% CPU)</sub> | **7,272**<br><sub>(139 MiB / 23.6% CPU)</sub> | **7,672**<br><sub>(162 MiB / 45.6% CPU)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **8,792**<br><sub>(198 MiB / 42.4% CPU)</sub> | **0**<br><sub>(peak 2,666 · 133 MiB / 16.1% CPU)</sub> | **7,200**<br><sub>(90 MiB / 29.5% CPU)</sub> | **7**<br><sub>(137 MiB / 0.1% CPU)</sub> | **6,048**<br><sub>(238 MiB / 52.5% CPU)</sub> |
| 256 KiB | HTTP/1 · TLS | HTTP/1 · plain | **4,783**<br><sub>(129 MiB / 31.1% CPU)</sub> | **3,268**<br><sub>(101 MiB / 42.5% CPU)</sub> | 🥇 **5,304**<br><sub>(85 MiB / 26.4% CPU)</sub> | **4,485**<br><sub>(146 MiB / 31.5% CPU)</sub> | **3,582**<br><sub>(174 MiB / 43.2% CPU)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/1 · plain | **2,275**<br><sub>(221 MiB / 32.4% CPU)</sub> | **861**<br><sub>(102 MiB / 19.2% CPU)</sub> | 🥇 **2,802**<br><sub>(83 MiB / 20.4% CPU)</sub> | **2,700**<br><sub>(167 MiB / 19.8% CPU)</sub> | **2,068**<br><sub>(162 MiB / 41.8% CPU)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **2,104**<br><sub>(165 MiB / 41.6% CPU)</sub> | **0**<br><sub>(peak 549 · 128 MiB / 15.8% CPU)</sub> | **2,046**<br><sub>(90 MiB / 28.9% CPU)</sub> | **20**<br><sub>(161 MiB / 0.5% CPU)</sub> | **1,765**<br><sub>(224 MiB / 47.7% CPU)</sub> |
| 64 KiB | HTTP/2 · plain | HTTP/1 · plain | 🥇 **13,135**<br><sub>(223 MiB / 42.0% CPU)</sub> | **3,378**<br><sub>(80 MiB / 14.6% CPU)</sub> | **10,431**<br><sub>(69 MiB / 24.5% CPU)</sub> | **10,400**<br><sub>(132 MiB / 23.4% CPU)</sub> | **11,945**<br><sub>(145 MiB / 43.6% CPU)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/2 · plain | **6,803**<br><sub>(103 MiB / 38.8% CPU)</sub> | *Not possible* | **5,393**<br><sub>(86 MiB / 24.5% CPU)</sub> | 🥇 **8,349**<br><sub>(141 MiB / 22.1% CPU)</sub> | **8,200**<br><sub>(191 MiB / 47.6% CPU)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/2 · TLS | **4,299**<br><sub>(103 MiB / 38.9% CPU)</sub> | *Not possible* | **2,634**<br><sub>(82 MiB / 24.7% CPU)</sub> | **4,879**<br><sub>(147 MiB / 23.1% CPU)</sub> | 🥇 **5,624**<br><sub>(180 MiB / 44.4% CPU)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/2 · TLS | **4,570**<br><sub>(177 MiB / 54.5% CPU)</sub> | *Not possible* | **4,441**<br><sub>(92 MiB / 32.6% CPU)</sub> | **22**<br><sub>(143 MiB / 0.2% CPU)</sub> | 🥇 **4,826**<br><sub>(237 MiB / 49.5% CPU)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **6,089**<br><sub>(218 MiB / 42.4% CPU)</sub> | **0**<br><sub>(peak 2,056 · 144 MiB / 23.2% CPU)</sub> | **5,152**<br><sub>(96 MiB / 34.0% CPU)</sub> | **13**<br><sub>(139 MiB / 0.2% CPU)</sub> | **4,813**<br><sub>(246 MiB / 49.6% CPU)</sub> |
| 256 KiB | HTTP/2 · plain | HTTP/1 · plain | **3,541**<br><sub>(198 MiB / 34.2% CPU)</sub> | **0**<br><sub>(peak 526 · 80 MiB / 8.7% CPU)</sub> | **3,864**<br><sub>(70 MiB / 20.3% CPU)</sub> | 🥇 **3,876**<br><sub>(159 MiB / 19.9% CPU)</sub> | **3,710**<br><sub>(154 MiB / 33.6% CPU)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/2 · plain | **1,912**<br><sub>(110 MiB / 28.6% CPU)</sub> | *Not possible* | **1,565**<br><sub>(89 MiB / 24.2% CPU)</sub> | 🥇 **3,000**<br><sub>(171 MiB / 21.9% CPU)</sub> | **2,032**<br><sub>(200 MiB / 39.8% CPU)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/2 · TLS | **1,291**<br><sub>(116 MiB / 31.1% CPU)</sub> | *Not possible* | **737**<br><sub>(84 MiB / 24.5% CPU)</sub> | **1,365**<br><sub>(161 MiB / 22.0% CPU)</sub> | 🥇 **1,502**<br><sub>(184 MiB / 40.2% CPU)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/2 · TLS | **1,210**<br><sub>(196 MiB / 54.0% CPU)</sub> | *Not possible* | **1,260**<br><sub>(94 MiB / 31.2% CPU)</sub> | **0**<br><sub>(162 MiB / 0.1% CPU)</sub> | 🥇 **1,424**<br><sub>(231 MiB / 47.5% CPU)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **1,715**<br><sub>(195 MiB / 43.0% CPU)</sub> | **0**<br><sub>(145 MiB / 0.3% CPU)</sub> | **1,336**<br><sub>(96 MiB / 30.6% CPU)</sub> | **0**<br><sub>(152 MiB / 0.1% CPU)</sub> | **1,471**<br><sub>(247 MiB / 47.1% CPU)</sub> |

On this GHA pass TWP÷YARP H1 TLS ≈ **1.23×** (64 KiB) / **1.28×** (256 KiB); H2→H1 ≈ **1.23×** / **1.16×**; H3→H1 ≈ **1.27×** / **1.13×**. TWP÷nginx H1 TLS ≈ **1.43** / **1.58**. Absolute RPS swings by VM; prefer ratios.

### Windows — POST 64 KiB request + 64 KiB response

Median of **3** repeats on `windows-latest` @ `9a2b3a1e`. Source: Actions [34441591377](https://github.com/justcoding121/titanium-web-proxy/actions/runs/34441591377) (`compare-post`).

*Not possible:* **HAProxy** and **Envoy** columns are omitted (no official Windows port).

| Client | Origin | TWP | nginx | YARP |
|---|---|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | 🥇 **5,883**<br><sub>(94 MiB / 46.6% CPU)</sub> | **352**<br><sub>(142 MiB / 24.7% CPU)</sub> | **4,093**<br><sub>(137 MiB / 55.7% CPU)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **3,997**<br><sub>(184 MiB / 49.3% CPU)</sub> | **352**<br><sub>(143 MiB / 24.7% CPU)</sub> | **3,494**<br><sub>(134 MiB / 52.1% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **2,039**<br><sub>(162 MiB / 40.4% CPU)</sub> | *Not possible (no QUIC)* | **1,893**<br><sub>(203 MiB / 48.6% CPU)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | 🥇 **6,237**<br><sub>(182 MiB / 46.1% CPU)</sub> | **1,622**<br><sub>(130 MiB / 24.6% CPU)</sub> | **5,889**<br><sub>(125 MiB / 53.5% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | **8**<br><sub>(83 MiB / 0.2% CPU)</sub> | *Not possible* | 🥇 **3,602**<br><sub>(143 MiB / 49.3% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | **8**<br><sub>(88 MiB / 0.1% CPU)</sub> | *Not possible* | 🥇 **2,837**<br><sub>(142 MiB / 46.8% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | **1,769**<br><sub>(178 MiB / 44.3% CPU)</sub> | *Not possible* | 🥇 **1,857**<br><sub>(194 MiB / 47.4% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **1,935**<br><sub>(175 MiB / 42.5% CPU)</sub> | *Not possible (no QUIC)* | **1,768**<br><sub>(213 MiB / 47.8% CPU)</sub> |

TWP leads H1 POST (~**1.5×** YARP), H2 POST (~**1.2×** YARP), and H3 POST (~**1.1×** YARP).

### Linux — POST 64 KiB request + 64 KiB response

Median of **3** repeats @ `a495a9ae`. Source: Actions [34557782264](https://github.com/justcoding121/titanium-web-proxy/actions/runs/34557782264) (`compare-post`; H3 HAProxy/Envoy peers).

| Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---:|---:|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | **5,003**<br><sub>(137 MiB / 44.0% CPU)</sub> | **4,021**<br><sub>(99 MiB / 48.4% CPU)</sub> | **5,302**<br><sub>(85 MiB / 40.0% CPU)</sub> | 🥇 **5,331**<br><sub>(131 MiB / 40.0% CPU)</sub> | **3,440**<br><sub>(175 MiB / 54.5% CPU)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **3,311**<br><sub>(219 MiB / 46.8% CPU)</sub> | **1,627**<br><sub>(114 MiB / 21.9% CPU)</sub> | **2,082**<br><sub>(84 MiB / 24.0% CPU)</sub> | **0**<br><sub>(peak 3,225 · 145 MiB / 22.6% CPU)</sub> | **2,730**<br><sub>(164 MiB / 48.9% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **3,051**<br><sub>(224 MiB / 43.8% CPU)</sub> | **556**<br><sub>(112 MiB / 24.9% CPU)</sub> | **2,210**<br><sub>(92 MiB / 34.9% CPU)</sub> | **0**<br><sub>(126 MiB / 0.1% CPU)</sub> | **2,672**<br><sub>(245 MiB / 49.2% CPU)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | 🥇 **5,664**<br><sub>(230 MiB / 42.2% CPU)</sub> | **2,442**<br><sub>(97 MiB / 23.0% CPU)</sub> | **2,928**<br><sub>(67 MiB / 23.1% CPU)</sub> | **0**<br><sub>(peak 5,034 · 130 MiB / 23.4% CPU)</sub> | **4,432**<br><sub>(156 MiB / 46.2% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | **8**<br><sub>(112 MiB / 0.2% CPU)</sub> | *Not possible* | **1,566**<br><sub>(88 MiB / 23.5% CPU)</sub> | 🥇 **3,108**<br><sub>(141 MiB / 23.5% CPU)</sub> | **2,548**<br><sub>(186 MiB / 47.7% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | **8**<br><sub>(124 MiB / 0.2% CPU)</sub> | *Not possible* | **1,209**<br><sub>(85 MiB / 24.6% CPU)</sub> | **1,949**<br><sub>(149 MiB / 20.7% CPU)</sub> | 🥇 **2,010**<br><sub>(179 MiB / 46.2% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | 🥇 **1,974**<br><sub>(240 MiB / 49.3% CPU)</sub> | *Not possible* | **1,292**<br><sub>(94 MiB / 32.3% CPU)</sub> | **0**<br><sub>(126 MiB / 0.1% CPU)</sub> | **1,905**<br><sub>(253 MiB / 46.8% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **2,286**<br><sub>(236 MiB / 44.1% CPU)</sub> | **462**<br><sub>(120 MiB / 24.8% CPU)</sub> | **1,727**<br><sub>(95 MiB / 35.8% CPU)</sub> | **0**<br><sub>(126 MiB / 0.1% CPU)</sub> | **2,079**<br><sub>(272 MiB / 47.5% CPU)</sub> |

Linux nginx H1/H2/H3 POST completed (nginx.org mainline). TWP÷YARP H1 ≈ **1.5×**; H2 ≈ **1.3×**; H3 ≈ **1.1×**. TWP÷nginx H3 ≈ **4×**.

### Windows — lossy / high-RTT (H2 HOL / H3 loss)

Userspace **5 ms** one-way delay + **1%** TCP connection stall (H1/H2) or UDP datagram drop (H3); **64 KiB** GET. Median of **3** repeats on `windows-latest` @ `9a2b3a1e` — [34441595456](https://github.com/justcoding121/titanium-web-proxy/actions/runs/34441595456) (`compare-lossy`).

*Not possible:* **HAProxy** and **Envoy** columns are omitted (no official Windows port).

| Client | Origin | TWP | nginx | YARP |
|---|---|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | 🥇 **663**<br><sub>(112 MiB / 3.0% CPU)</sub> | **652**<br><sub>(143 MiB / 16.4% CPU)</sub> | **662**<br><sub>(121 MiB / 4.5% CPU)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | **0**<br><sub>(peak 86 · 120 MiB / 1.4% CPU)</sub> | **0**<br><sub>(peak 17 · 142 MiB / 0.6% CPU)</sub> | **0**<br><sub>(peak 16 · 99 MiB / 0.8% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | **0**<br><sub>(67 MiB / 0.1% CPU)</sub> | *Not possible (no QUIC)* | **0**<br><sub>(80 MiB / 0.0% CPU)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | **0**<br><sub>(peak 89 · 130 MiB / 1.5% CPU)</sub> | **0**<br><sub>(peak 17 · 128 MiB / 0.1% CPU)</sub> | **0**<br><sub>(peak 16 · 91 MiB / 0.7% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | **0**<br><sub>(peak 8 · 68 MiB / 0.6% CPU)</sub> | *Not possible* | **0**<br><sub>(peak 16 · 100 MiB / 0.7% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | **0**<br><sub>(peak 8 · 71 MiB / 0.2% CPU)</sub> | *Not possible* | **0**<br><sub>(peak 16 · 99 MiB / 0.8% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | **0**<br><sub>(70 MiB / 0.1% CPU)</sub> | *Not possible* | **0**<br><sub>(80 MiB / 0.0% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | **0**<br><sub>(69 MiB / 0.0% CPU)</sub> | *Not possible (no QUIC)* | **0**<br><sub>(78 MiB / 0.0% CPU)</sub> |

TWP H2 HOL leads (~**3.31×** YARP). H3 is the protocol-shape win vs H2 HOL on the same lossy session; Win H3 GHA remains 0 (laptop re-measure kept above).

### Linux — lossy / high-RTT (H2 HOL / H3 loss)

Median of **3** repeats @ `a495a9ae`. Source: [34557784262](https://github.com/justcoding121/titanium-web-proxy/actions/runs/34557784262) (`compare-lossy`; H3 HAProxy/Envoy peers; lossy H3 uses `quic-http3`).

| Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---:|---:|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | **1,198**<br><sub>(144 MiB / 13.5% CPU)</sub> | **1,208**<br><sub>(100 MiB / 12.6% CPU)</sub> | 🥇 **1,210**<br><sub>(84 MiB / 7.3% CPU)</sub> | **1,200**<br><sub>(128 MiB / 9.4% CPU)</sub> | **1,195**<br><sub>(146 MiB / 17.3% CPU)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **315**<br><sub>(175 MiB / 6.8% CPU)</sub> | **40**<br><sub>(99 MiB / 0.3% CPU)</sub> | **40**<br><sub>(84 MiB / 0.2% CPU)</sub> | **40**<br><sub>(136 MiB / 0.4% CPU)</sub> | **40**<br><sub>(129 MiB / 1.4% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | **336**<br><sub>(148 MiB / 14.1% CPU)</sub> | **95**<br><sub>(109 MiB / 2.5% CPU)</sub> | **62**<br><sub>(85 MiB / 3.9% CPU)</sub> | 🥇 **1,225**<br><sub>(141 MiB / 21.5% CPU)</sub> | **299**<br><sub>(181 MiB / 20.8% CPU)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | 🥇 **358**<br><sub>(174 MiB / 7.0% CPU)</sub> | **40**<br><sub>(78 MiB / 0.2% CPU)</sub> | **41**<br><sub>(69 MiB / 0.2% CPU)</sub> | **40**<br><sub>(128 MiB / 0.3% CPU)</sub> | **40**<br><sub>(122 MiB / 1.0% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | **0**<br><sub>(peak 12 · 97 MiB / 0.5% CPU)</sub> | *Not possible* | 🥇 **42**<br><sub>(90 MiB / 0.4% CPU)</sub> | **40**<br><sub>(136 MiB / 0.3% CPU)</sub> | **41**<br><sub>(131 MiB / 1.5% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | **0**<br><sub>(peak 12 · 96 MiB / 0.7% CPU)</sub> | *Not possible* | **40**<br><sub>(85 MiB / 0.6% CPU)</sub> | **40**<br><sub>(137 MiB / 0.5% CPU)</sub> | 🥇 **40**<br><sub>(130 MiB / 1.7% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | **319**<br><sub>(140 MiB / 25.3% CPU)</sub> | *Not possible* | **63**<br><sub>(92 MiB / 4.3% CPU)</sub> | 🥇 **1,101**<br><sub>(145 MiB / 24.4% CPU)</sub> | **336**<br><sub>(185 MiB / 24.1% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | **338**<br><sub>(166 MiB / 15.2% CPU)</sub> | **91**<br><sub>(114 MiB / 2.9% CPU)</sub> | **59**<br><sub>(91 MiB / 4.4% CPU)</sub> | 🥇 **1,136**<br><sub>(140 MiB / 23.1% CPU)</sub> | **338**<br><sub>(187 MiB / 21.3% CPU)</sub> |

TWP H2 HOL ≫ YARP (~**7.7×**). H3 TWP÷YARP ≈ **1×**.

### Architecture-sensitive

These runs isolate slow app readers, origin-early response, HTTP/2 duplex, and WebSocket echo. See [TWP vs YARP IO model](Performance-Profiling#twp-vs-yarp-io-model). Laptop 1-rep numbers are on [Performance Local Lab](Performance-Local-Lab#architecture-sensitive).

Median of **3** repeats on matched 4 vCPU / 16 GiB runners @ `9a2b3a1e` ([34441578556](https://github.com/justcoding121/titanium-web-proxy/actions/runs/34441578556)). Slow consumer = 256 KiB GET, 16 KiB read + 8 ms sleep. Early response = 64 KiB POST, origin writes after 8 KiB. Duplex HTTP/2 = overlapping 64 KiB POST on H2 TLS↔H2 TLS. WebSocket row = H1 Upgrade echo round-trips/sec (not RFC 8441; see WebSocket RFC 8441 table below).

Lossy-link runs (slow **network**) are already published above; they are not a slow **app** reader.

#### Windows

*Not possible:* **HAProxy** and **Envoy** columns are omitted (no official Windows port).

| Scenario | Client | Origin | TWP | nginx | YARP |
|---|---|---|---:|---:|---:|
| Slow consumer (256 KiB GET, throttled client read) | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **243**<br><sub>(101 MiB / 5.9% CPU)</sub> | **204**<br><sub>(143 MiB / 24.6% CPU)</sub> | **241**<br><sub>(109 MiB / 4.8% CPU)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · TLS | HTTP/1 · plain | **256**<br><sub>(131 MiB / 3.7% CPU)</sub> | **230**<br><sub>(142 MiB / 24.4% CPU)</sub> | 🥇 **256**<br><sub>(111 MiB / 5.4% CPU)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **264**<br><sub>(101 MiB / 17.4% CPU)</sub> | *Not possible (no QUIC)* | **255**<br><sub>(158 MiB / 20.4% CPU)</sub> |
| Early response (origin writes after first request chunk) | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **5,611**<br><sub>(99 MiB / 45.5% CPU)</sub> | **363**<br><sub>(143 MiB / 24.7% CPU)</sub> | **3,919**<br><sub>(138 MiB / 53.9% CPU)</sub> |
| Early response (origin writes after first request chunk) | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **3,934**<br><sub>(169 MiB / 48.8% CPU)</sub> | **0**<br><sub>(peak 318 · 144 MiB / 24.8% CPU)</sub> | **3,200**<br><sub>(135 MiB / 53.4% CPU)</sub> |
| Early response (origin writes after first request chunk) | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **3,012**<br><sub>(152 MiB / 42.0% CPU)</sub> | *Not possible (no QUIC)* | **2,602**<br><sub>(203 MiB / 51.0% CPU)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · plain | HTTP/1 · plain | **248**<br><sub>(112 MiB / 3.6% CPU)</sub> | **248**<br><sub>(127 MiB / 9.4% CPU)</sub> | 🥇 **256**<br><sub>(104 MiB / 6.0% CPU)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · TLS | HTTP/2 · TLS | **0**<br><sub>(peak 8 · 70 MiB / 1.0% CPU)</sub> | *Not possible* | 🥇 **256**<br><sub>(136 MiB / 9.3% CPU)</sub> |
| Early response (origin writes after first request chunk) | HTTP/2 · TLS | HTTP/2 · TLS | **0**<br><sub>(92 MiB / 0.1% CPU)</sub> | *Not possible* | **0**<br><sub>(110 MiB / 0.1% CPU)</sub> |
| Duplex (both directions live) | HTTP/2 · TLS | HTTP/2 · TLS | **0**<br><sub>(91 MiB / 0.1% CPU)</sub> | *Not possible* | **0**<br><sub>(111 MiB / 0.1% CPU)</sub> |
| Duplex (WebSocket / H1 Upgrade) | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **24,498**<br><sub>(97 MiB / 43.0% CPU)</sub> | **12,337**<br><sub>(143 MiB / 24.6% CPU)</sub> | **23,100**<br><sub>(89 MiB / 44.6% CPU)</sub> |

#### Linux

| Scenario | Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---|---:|---:|---:|---:|---:|
| Slow consumer (256 KiB GET, throttled client read) | HTTP/1 · TLS | HTTP/1 · plain | **467**<br><sub>(119 MiB / 9.7% CPU)</sub> | **412**<br><sub>(100 MiB / 9.7% CPU)</sub> | 🥇 **472**<br><sub>(82 MiB / 5.9% CPU)</sub> | **466**<br><sub>(139 MiB / 6.3% CPU)</sub> | **419**<br><sub>(148 MiB / 14.5% CPU)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · TLS | HTTP/1 · plain | **472**<br><sub>(145 MiB / 20.3% CPU)</sub> | **459**<br><sub>(100 MiB / 22.4% CPU)</sub> | 🥇 **474**<br><sub>(84 MiB / 9.3% CPU)</sub> | **466**<br><sub>(157 MiB / 7.6% CPU)</sub> | **473**<br><sub>(152 MiB / 25.1% CPU)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **472**<br><sub>(131 MiB / 35.5% CPU)</sub> | **0**<br><sub>(peak 379 · 124 MiB / 22.5% CPU)</sub> | **468**<br><sub>(88 MiB / 11.7% CPU)</sub> | **0**<br><sub>(145 MiB / 0.1% CPU)</sub> | **470**<br><sub>(199 MiB / 42.5% CPU)</sub> |
| Early response (origin writes after first request chunk) | HTTP/1 · TLS | HTTP/1 · plain | **4,751**<br><sub>(132 MiB / 47.4% CPU)</sub> | **3,736**<br><sub>(100 MiB / 50.2% CPU)</sub> | 🥇 **4,951**<br><sub>(85 MiB / 43.0% CPU)</sub> | **0**<br><sub>(peak 2,679 · 130 MiB / 25.6% CPU)</sub> | **3,212**<br><sub>(170 MiB / 56.5% CPU)</sub> |
| Early response (origin writes after first request chunk) | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **3,300**<br><sub>(232 MiB / 48.8% CPU)</sub> | **0**<br><sub>(peak 1,308 · 115 MiB / 23.5% CPU)</sub> | **2,508**<br><sub>(84 MiB / 23.9% CPU)</sub> | **0**<br><sub>(peak 2,660 · 150 MiB / 24.1% CPU)</sub> | **2,234**<br><sub>(171 MiB / 49.0% CPU)</sub> |
| Early response (origin writes after first request chunk) | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **2,868**<br><sub>(195 MiB / 45.0% CPU)</sub> | **0**<br><sub>(peak 456 · 115 MiB / 24.8% CPU)</sub> | **1,977**<br><sub>(91 MiB / 34.7% CPU)</sub> | **0**<br><sub>(127 MiB / 0.1% CPU)</sub> | **2,117**<br><sub>(244 MiB / 48.3% CPU)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · plain | HTTP/1 · plain | **476**<br><sub>(140 MiB / 15.2% CPU)</sub> | **454**<br><sub>(79 MiB / 11.8% CPU)</sub> | 🥇 **478**<br><sub>(68 MiB / 6.4% CPU)</sub> | **473**<br><sub>(151 MiB / 4.6% CPU)</sub> | **478**<br><sub>(148 MiB / 15.8% CPU)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · TLS | HTTP/2 · TLS | **8**<br><sub>(97 MiB / 2.5% CPU)</sub> | *Not possible* | **448**<br><sub>(88 MiB / 21.4% CPU)</sub> | **455**<br><sub>(148 MiB / 12.7% CPU)</sub> | 🥇 **462**<br><sub>(159 MiB / 31.1% CPU)</sub> |
| Early response (origin writes after first request chunk) | HTTP/2 · TLS | HTTP/2 · TLS | **0**<br><sub>(108 MiB / 0.1% CPU)</sub> | *Not possible* | 🥇 **0**<br><sub>(93 MiB / 0.1% CPU)</sub> | **0**<br><sub>(peak 1,942 · 154 MiB / 20.0% CPU)</sub> | **0**<br><sub>(148 MiB / 0.2% CPU)</sub> |
| Duplex (both directions live) | HTTP/2 · TLS | HTTP/2 · TLS | **0**<br><sub>(108 MiB / 0.1% CPU)</sub> | *Not possible* | **0**<br><sub>(93 MiB / 0.1% CPU)</sub> | **0**<br><sub>(peak 1,994 · 156 MiB / 20.8% CPU)</sub> | **0**<br><sub>(145 MiB / 0.2% CPU)</sub> |
| Duplex (WebSocket / H1 Upgrade) | HTTP/1 · TLS | HTTP/1 · plain | **31,261**<br><sub>(126 MiB / 43.2% CPU)</sub> | 🥇 **34,370**<br><sub>(100 MiB / 35.8% CPU)</sub> | **33,060**<br><sub>(82 MiB / 38.5% CPU)</sub> | **32,066**<br><sub>(127 MiB / 39.8% CPU)</sub> | **27,020**<br><sub>(125 MiB / 43.8% CPU)</sub> |

Slow consumer is sleep-bound; H1/H2/H3 sit in the same band. Early-response H1/H2/H3: TWP leads (H1 early ≈ **2.00×** / **1.47×** YARP Win/Linux). **Duplex H2**: YARP leads by design — Win ≈ **0.59×** (1,270 / 2,135), Linux ≈ **0.15×** (282 / 1,882); irreducible concurrent-copier cell (see [IO model](Performance-Profiling#twp-vs-yarp-io-model)). WebSocket: TWP÷YARP Windows ≈ **1.06×**; Linux nginx leads.

### TLS termination cost (H1 TLS → cleartext origin)

Isolates keep-alive tiny GET vs **new connection per request** (handshake-dominated) vs keep-alive **256 KiB**. Product comparison uses RPS and end-to-end latency; TWP can also capture `ClientTlsTiming` when `TWP_RPS_CAPTURE_TLS=1` (child process) — nginx/YARP have no equivalent hook.

#### Windows

Median of **3** repeats on `windows-latest` @ `9a2b3a1e`. Source: Actions [34441599658](https://github.com/justcoding121/titanium-web-proxy/actions/runs/34441599658). Absolute RPS on GHA swings hard; prefer **TWP÷YARP**.

*Not possible:* **HAProxy** and **Envoy** columns are omitted (no official Windows port).

| Workload | TWP | nginx | YARP |
|---|---:|---:|---:|
| Keep-alive · tiny GET | 🥇 **20,612**<br><sub>(87 MiB / 47.9% CPU)</sub> | **8,972**<br><sub>(142 MiB / 24.8% CPU)</sub> | **18,392**<br><sub>(105 MiB / 50.9% CPU)</sub> |
| New-connection · tiny GET | 🥇 **732**<br><sub>(88 MiB / 9.5% CPU)</sub> | **0**<br><sub>(peak 248 · 140 MiB / 24.4% CPU)</sub> | **725**<br><sub>(113 MiB / 10.7% CPU)</sub> |
| Keep-alive · 256 KiB GET | 🥇 **2,741**<br><sub>(130 MiB / 47.0% CPU)</sub> | **0**<br><sub>(peak 162 · 142 MiB / 24.6% CPU)</sub> | **2,699**<br><sub>(136 MiB / 49.4% CPU)</sub> |

#### Linux

Median of **3** repeats @ `9a2b3a1e`. Source: Actions [34441599658](https://github.com/justcoding121/titanium-web-proxy/actions/runs/34441599658).

| Workload | TWP | nginx | HAProxy | Envoy | YARP |
|---|---:|---:|---:|---:|---:|
| Keep-alive · tiny GET | **36,114**<br><sub>(108 MiB / 49.4% CPU)</sub> | 🥇 **44,372**<br><sub>(102 MiB / 40.8% CPU)</sub> | **43,256**<br><sub>(84 MiB / 43.3% CPU)</sub> | **27,636**<br><sub>(128 MiB / 57.3% CPU)</sub> | **32,712**<br><sub>(135 MiB / 49.8% CPU)</sub> |
| New-connection · tiny GET | **1,549**<br><sub>(128 MiB / 40.0% CPU)</sub> | 🥇 **1,598**<br><sub>(103 MiB / 36.2% CPU)</sub> | **1,454**<br><sub>(85 MiB / 37.7% CPU)</sub> | **1,379**<br><sub>(129 MiB / 44.5% CPU)</sub> | **1,525**<br><sub>(149 MiB / 38.9% CPU)</sub> |
| Keep-alive · 256 KiB GET | **3,790**<br><sub>(126 MiB / 31.8% CPU)</sub> | **2,421**<br><sub>(101 MiB / 48.7% CPU)</sub> | 🥇 **3,940**<br><sub>(84 MiB / 28.4% CPU)</sub> | **3,544**<br><sub>(145 MiB / 32.3% CPU)</sub> | **3,031**<br><sub>(160 MiB / 42.1% CPU)</sub> |

All three workloads are **>1.00×** YARP on both OS. nginx leads Linux keep-alive tiny and Linux new-connection; TWP is second, YARP third.

## Unary gRPC (H2 TLS)

Unary Echo **RPC/s** @ c=64 over H2 TLS→H2 TLS for Titanium, YARP, nginx (`grpc_pass`), HAProxy, and Envoy (OS-possible peers only). Not folded into the architecture-sensitive tables. Median of **3** repeats @ `9a2b3a1e` — [34441548073](https://github.com/justcoding121/titanium-web-proxy/actions/runs/34441548073).

| OS | Titanium | YARP | nginx | HAProxy | Envoy |
|---|---:|---:|---:|---:|---:|
| Windows | **53,762**<br><sub>(88 MiB / 23.6% CPU)</sub> | **30,754**<br><sub>(123 MiB / 46.9% CPU)</sub> | **0**<br><sub>(153 MiB / 24.8% CPU)</sub> | *Not possible* | *Not possible* |
| Linux | **42,675**<br><sub>(120 MiB / 30.3% CPU)</sub> | **24,232**<br><sub>(159 MiB / 41.6% CPU)</sub> | **0**<br><sub>(120 MiB / 24.9% CPU)</sub> | **8,683**<br><sub>(84 MiB / 24.6% CPU)</sub> | **16,110**<br><sub>(128 MiB / 20.6% CPU)</sub> |
| macOS | **14,924**<br><sub>(94 MiB / 20.1% CPU)</sub> | **8,581**<br><sub>(128 MiB / 28.4% CPU)</sub> | **0**<br><sub>(97 MiB / 2.5% CPU)</sub> | **0**<br><sub>(68 MiB / 5.1% CPU)</sub> | **0**<br><sub>(84 MiB / 19.0% CPU)</sub> |

## Unary gRPC (H2 TLS → h2c)

Unary Echo **RPC/s** @ c=64 over **H2 TLS → h2c** (edge TLS, cleartext H2 origin). Peers: Titanium, YARP, HAProxy, Envoy. Mode: `compare-grpc` (`*-grpc-h2c`). Win/Linux from prior paste; macOS @ `a495a9ae` — [34557768163](https://github.com/justcoding121/titanium-web-proxy/actions/runs/34557768163).

*Not possible:* **nginx** column omitted (no H2 upstream).

| OS | Titanium | YARP | HAProxy | Envoy |
|---|---:|---:|---:|---:|
| Windows | **60,942**<br><sub>(93 MiB / 22.8% CPU)</sub> | **33,648**<br><sub>(111 MiB / 49.7% CPU)</sub> | *Not possible* | *Not possible* |
| Linux | **44,967**<br><sub>(121 MiB / 29.2% CPU)</sub> | **24,678**<br><sub>(150 MiB / 44.1% CPU)</sub> | **7,735**<br><sub>(82 MiB / 24.6% CPU)</sub> | **12,516**<br><sub>(128 MiB / 22.6% CPU)</sub> |
| macOS | **14,333**<br><sub>(90 MiB / 23.8% CPU)</sub> | **9,316**<br><sub>(132 MiB / 34.8% CPU)</sub> | **0**<br><sub>(65 MiB / 17.6% CPU)</sub> | **0**<br><sub>(80 MiB / 20.6% CPU)</sub> |

## WebSocket (H1 TLS → H1 TLS)

WebSocket echo round-trips/sec over **dual-TLS** H1 (`proxy_ssl` style). Mode: `compare-ws-h1tls` (`*-duplex-ws-h1tls`).

| OS | Titanium | YARP | nginx | HAProxy | Envoy |
|---|---:|---:|---:|---:|---:|
| Windows | **27,472**<br><sub>(101 MiB / 44.3% CPU)</sub> | **26,068**<br><sub>(92 MiB / 44.1% CPU)</sub> | **13,043**<br><sub>(142 MiB / 24.7% CPU)</sub> | *Not possible* | *Not possible* |
| Linux | **25,489**<br><sub>(138 MiB / 43.2% CPU)</sub> | **22,409**<br><sub>(134 MiB / 44.1% CPU)</sub> | **27,071**<br><sub>(103 MiB / 36.8% CPU)</sub> | **26,782**<br><sub>(85 MiB / 38.9% CPU)</sub> | **27,011**<br><sub>(127 MiB / 38.1% CPU)</sub> |
| macOS | **8,868**<br><sub>(118 MiB / 35.0% CPU)</sub> | **12,323**<br><sub>(127 MiB / 28.2% CPU)</sub> | **6,231**<br><sub>(74 MiB / 21.1% CPU)</sub> | **11,067**<br><sub>(68 MiB / 30.2% CPU)</sub> | **4,940**<br><sub>(80 MiB / 30.3% CPU)</sub> |

## WebSocket (H2 TLS 8441 → H1)

WebSocket echo over **RFC 8441** extended CONNECT (H2 TLS client → H1 plain origin). Mode: `compare-ws-h2` (`*-duplex-ws-h2`).

*Not possible:* **nginx** column omitted (no RFC 8441 extended CONNECT reverse).

| OS | Titanium | YARP | HAProxy | Envoy |
|---|---:|---:|---:|---:|
| Windows | **25,713**<br><sub>(144 MiB / 42.9% CPU)</sub> | **0**<br><sub>(167 MiB / 20.9% CPU)</sub> | *Not possible* | *Not possible* |
| Linux | **1,531**<br><sub>(147 MiB / 16.0% CPU)</sub> | **0**<br><sub>(161 MiB / 25.4% CPU)</sub> | **1,534**<br><sub>(82 MiB / 5.3% CPU)</sub> | **0**<br><sub>(124 MiB / 0.3% CPU)</sub> |
| macOS | **11,061**<br><sub>(384 MiB / 43.3% CPU)</sub> | **0**<br><sub>(132 MiB / 17.7% CPU)</sub> | **10,080**<br><sub>(66 MiB / 33.1% CPU)</sub> | **0**<br><sub>(73 MiB / 0.3% CPU)</sub> |

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
- MITM overhead: **Lite÷Reverse ≥ 0.50** and **Full÷Reverse ≥ 0.50** (median of 3 GHA runs @ c=64). Absolute RPS moves with runner heat; ratios are the claim.
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
