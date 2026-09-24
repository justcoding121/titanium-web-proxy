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
- **Typical reverse** is H1 TLS→H1 or H2→H1 (~1.1× YARP on Win/Linux tiny GET; Mac H1 is near parity). With **larger bodies**, see [Heavier reverse](#heavier-reverse-workloads): at 64 KiB H2 TLS→H2 TLS, Titanium is **behind** YARP (~0.57–0.81× across OS).

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

Calibration for the shared 4 vCPU loopback shape: how close client + origin are to saturated before ranking reverse peers. Tiny keep-alive GET. Median of **3** repeats @ `8bfa7852` — [35092854050](https://github.com/justcoding121/titanium-web-proxy/actions/runs/35092854050). Warmup 2s / measure 8s; concurrency 8, 16, 32, 64. Block A **% of origin-HttpClient** uses median **peak** RPS. Blocks B/C use peer÷YARP / ÷nginx on median peak (not % of H1 origin). **RPS cells** embed median RSS / CPU for the **proxy child** plus its **full descendant tree** (serve-proxy → nginx master → workers); origin-direct samples the **origin** child. Product matrices below use matched `dotnet-httpclient` only (not bombardier).

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
| origin-direct | dotnet-httpclient | **81,217**<br><sub>(55 MiB / 45.3% CPU)</sub> | **100.0%** |
| origin-direct-bombardier | bombardier | **61,721**<br><sub>(56 MiB / 25.1% CPU)</sub> | **76.0%** |
| bare-reverse-http1 | dotnet-httpclient | **39,529**<br><sub>(56 MiB / 44.1% CPU)</sub> | **48.7%** |
| nginx-reverse-http1 | dotnet-httpclient | **24,677**<br><sub>(125 MiB / 24.9% CPU)</sub> | **30.4%** |
| yarp-reverse-http1 | dotnet-httpclient | **34,602**<br><sub>(90 MiB / 49.2% CPU)</sub> | **42.6%** |
| twp-reverse-http1 | dotnet-httpclient | 🥇 **40,685**<br><sub>(76 MiB / 50.9% CPU)</sub> | **50.1%** |

**Linux** (`ubuntu-latest`)

| Arm | Generator | RPS | % of origin-HttpClient |
|---|---|---:|---:|
| origin-direct | dotnet-httpclient | **131,605**<br><sub>(81 MiB / 45.0% CPU)</sub> | **100.0%** |
| origin-direct-bombardier | bombardier | **81,228**<br><sub>(80 MiB / 38.3% CPU)</sub> | **61.7%** |
| bare-reverse-http1 | dotnet-httpclient | **58,771**<br><sub>(70 MiB / 46.0% CPU)</sub> | **44.7%** |
| nginx-reverse-http1 | dotnet-httpclient | 🥇 **74,446**<br><sub>(77 MiB / 39.5% CPU)</sub> | **56.6%** |
| yarp-reverse-http1 | dotnet-httpclient | **55,568**<br><sub>(115 MiB / 48.0% CPU)</sub> | **42.2%** |
| twp-reverse-http1 | dotnet-httpclient | **63,570**<br><sub>(92 MiB / 50.3% CPU)</sub> | **48.3%** |

Reverse peers are about **50–48%** of the origin-direct HttpClient peak on this runner class (Win TWP **50.1%**, Lin TWP **48.3%**). Prefer the **%** column over absolute RPS across runs. Bare and origin-direct are controls (not medal peers).

#### Block B — H2 TLS→H1

Peer ratios (÷YARP / ÷nginx) on median peak; **RPS cells** embed `(MiB / CPU%)`.

**Windows** (`windows-latest`)

| Arm | Generator | RPS | ÷YARP | ÷nginx |
|---|---|---:|---:|---:|
| nginx-reverse-http2 | dotnet-httpclient | **14,697**<br><sub>(141 MiB / 24.4% CPU)</sub> | **0.32×** | **1.00×** |
| yarp-reverse-http2 | dotnet-httpclient | **45,876**<br><sub>(96 MiB / 50.8% CPU)</sub> | **1.00×** | **3.12×** |
| twp-reverse-http2-cleartext | dotnet-httpclient | 🥇 **51,822**<br><sub>(103 MiB / 52.9% CPU)</sub> | **1.13×** | **3.53×** |

**Linux** (`ubuntu-latest`)

| Arm | Generator | RPS | ÷YARP | ÷nginx |
|---|---|---:|---:|---:|
| nginx-reverse-http2 | dotnet-httpclient | **29,423**<br><sub>(102 MiB / 18.9% CPU)</sub> | **0.46×** | **1.00×** |
| yarp-reverse-http2 | dotnet-httpclient | **64,174**<br><sub>(123 MiB / 46.6% CPU)</sub> | **1.00×** | **2.18×** |
| twp-reverse-http2-cleartext | dotnet-httpclient | 🥇 **66,465**<br><sub>(123 MiB / 51.4% CPU)</sub> | **1.04×** | **2.26×** |

#### Block C — H3→H1

Same layout as Block B. Requires QuicListener. nginx needs `http_v3_module` (Windows nginx has no QUIC). HAProxy needs `USE_QUIC` (GHA Linux/macOS require it). Envoy 1.20+ includes HTTP/3.

**Windows** (`windows-latest`)

| Arm | Generator | RPS | ÷YARP | ÷nginx |
|---|---|---:|---:|---:|
| nginx-reverse-http3-cleartext | dotnet-httpclient | *Not possible (no QUIC)* | — | — |
| yarp-reverse-http3-cleartext | dotnet-httpclient | **24,601**<br><sub>(148 MiB / 50.5% CPU)</sub> | **1.00×** | — |
| twp-reverse-http3-cleartext | dotnet-httpclient | 🥇 **24,701**<br><sub>(117 MiB / 43.9% CPU)</sub> | **1.00×** | — |

**Linux** (`ubuntu-latest`)

| Arm | Generator | RPS | ÷YARP | ÷nginx |
|---|---|---:|---:|---:|
| nginx-reverse-http3-cleartext | dotnet-httpclient | **0**<br><sub>(peak 38,114 · 113 MiB / 20.9% CPU)</sub> | **0.96×** | **1.00×** |
| yarp-reverse-http3-cleartext | dotnet-httpclient | **39,807**<br><sub>(215 MiB / 48.7% CPU)</sub> | **1.00×** | **1.04×** |
| twp-reverse-http3-cleartext | dotnet-httpclient | 🥇 **44,892**<br><sub>(177 MiB / 53.4% CPU)</sub> | **1.13×** | **1.18×** |

## Windows — Titanium vs nginx vs HAProxy vs Envoy vs YARP

Client / origin: HTTP version and whether TLS is used (`plain` = cleartext, `TLS` = encrypted, `QUIC` = HTTP/3).

### Reverse

Median of **3 repeats** on `windows-latest` (4 vCPU / 16 GiB). Bare reverse 5×5 @ `8bfa7852` — `compare-product` [35092827644](https://github.com/justcoding121/titanium-web-proxy/actions/runs/35092827644). Warmup 2s / measure 8s; concurrency 8, 16, 32, 64. Prefer TWP÷peer ratios over absolute RPS. **RPS cells** include median RSS / CPU at the peak-RPS step as `<br><sub>(MiB / CPU%)</sub>`. nginx terminate peers use `keepalive 256` + streaming buffers. **HAProxy / Envoy are Linux-only peers** (no official Windows port). Laptop High-perf / cool-paired numbers stay on the [local lab](Performance-Local-Lab). Product 5×5 is **~56-byte JSON keep-alive GET**; H2/H3 same-protocol cells are mostly header work with a tiny body (Titanium best case) — see [Why this comparison is fair](#why-this-comparison-is-fair).

**Load generators:** Reverse inbound H3 arms use **`dotnet-httpclient`** (`http_version=3.0`, `RequestVersionExact`). nginx/Windows is same-OS only (no QUIC). HAProxy/Envoy are Linux-only terminate peers.

| Client | Origin | TWP | nginx | YARP |
|---|---|---:|---:|---:|
| HTTP/1 · plain | HTTP/1 · plain | 🥇 **31,552**<br><sub>(75 MiB / 51.1% CPU)</sub> | **19,301**<br><sub>(125 MiB / 25% CPU)</sub> | **26,979**<br><sub>(90 MiB / 48.1% CPU)</sub> |
| HTTP/1 · plain | HTTP/1 · TLS | 🥇 **20,447**<br><sub>(90 MiB / 52.6% CPU)</sub> | **8,419**<br><sub>(136 MiB / 24.6% CPU)</sub> | **18,937**<br><sub>(100 MiB / 48.3% CPU)</sub> |
| HTTP/1 · plain | HTTP/2 · plain | 🥇 **36,987**<br><sub>(108 MiB / 45.2% CPU)</sub> | *Not possible (no H2 upstream)* | **32,831**<br><sub>(93 MiB / 46.7% CPU)</sub> |
| HTTP/1 · plain | HTTP/2 · TLS | 🥇 **39,580**<br><sub>(117 MiB / 47.3% CPU)</sub> | *Not possible (no H2 upstream)* | **36,173**<br><sub>(98 MiB / 48.9% CPU)</sub> |
| HTTP/1 · plain | HTTP/3 · QUIC | **11,519**<br><sub>(104 MiB / 35% CPU)</sub> | *Not possible (no H3 upstream)* | 🥇 **18,177**<br><sub>(118 MiB / 49.9% CPU)</sub> |
| HTTP/1 · TLS | HTTP/1 · plain | 🥇 **20,182**<br><sub>(86 MiB / 53.9% CPU)</sub> | **8,534**<br><sub>(142 MiB / 24.7% CPU)</sub> | **18,049**<br><sub>(102 MiB / 49.4% CPU)</sub> |
| HTTP/1 · TLS | HTTP/1 · TLS | 🥇 **23,518**<br><sub>(84 MiB / 47.8% CPU)</sub> | **9,376**<br><sub>(144 MiB / 24.8% CPU)</sub> | **20,816**<br><sub>(102 MiB / 51.2% CPU)</sub> |
| HTTP/1 · TLS | HTTP/2 · plain | 🥇 **27,742**<br><sub>(115 MiB / 46% CPU)</sub> | *Not possible (no H2 upstream)* | **26,111**<br><sub>(108 MiB / 49.6% CPU)</sub> |
| HTTP/1 · TLS | HTTP/2 · TLS | 🥇 **25,046**<br><sub>(119 MiB / 47% CPU)</sub> | *Not possible (no H2 upstream)* | **24,356**<br><sub>(108 MiB / 47.4% CPU)</sub> |
| HTTP/1 · TLS | HTTP/3 · QUIC | 🥇 **19,385**<br><sub>(111 MiB / 50.9% CPU)</sub> | *Not possible (no H3 upstream)* | **18,399**<br><sub>(129 MiB / 49.7% CPU)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | 🥇 **34,785**<br><sub>(95 MiB / 53.6% CPU)</sub> | **9,225**<br><sub>(127 MiB / 24.8% CPU)</sub> | **31,535**<br><sub>(84 MiB / 53.4% CPU)</sub> |
| HTTP/2 · plain | HTTP/1 · TLS | 🥇 **29,745**<br><sub>(98 MiB / 49% CPU)</sub> | **6,559**<br><sub>(138 MiB / 24.9% CPU)</sub> | **27,120**<br><sub>(96 MiB / 54% CPU)</sub> |
| HTTP/2 · plain | HTTP/2 · plain | 🥇 **114,586**<br><sub>(59 MiB / 27.4% CPU)</sub> | *Not possible (no H2 upstream)* | **69,424**<br><sub>(94 MiB / 48.1% CPU)</sub> |
| HTTP/2 · plain | HTTP/2 · TLS | 🥇 **86,454**<br><sub>(65 MiB / 28.5% CPU)</sub> | *Not possible (no H2 upstream)* | **55,488**<br><sub>(99 MiB / 46.8% CPU)</sub> |
| HTTP/2 · plain | HTTP/3 · QUIC | 🥇 **31,645**<br><sub>(121 MiB / 52.4% CPU)</sub> | *Not possible (no H3 upstream)* | **28,884**<br><sub>(120 MiB / 52.8% CPU)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **39,258**<br><sub>(103 MiB / 51.6% CPU)</sub> | **11,247**<br><sub>(141 MiB / 24.3% CPU)</sub> | **35,070**<br><sub>(94 MiB / 49% CPU)</sub> |
| HTTP/2 · TLS | HTTP/1 · TLS | 🥇 **29,467**<br><sub>(106 MiB / 54% CPU)</sub> | **6,379**<br><sub>(145 MiB / 24.6% CPU)</sub> | **25,223**<br><sub>(97 MiB / 51.8% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | 🥇 **101,094**<br><sub>(78 MiB / 26.5% CPU)</sub> | *Not possible (no H2 upstream)* | **54,554**<br><sub>(99 MiB / 50.8% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **88,782**<br><sub>(75 MiB / 28.5% CPU)</sub> | *Not possible (no H2 upstream)* | **53,218**<br><sub>(103 MiB / 48.9% CPU)</sub> |
| HTTP/2 · TLS | HTTP/3 · QUIC | 🥇 **30,154**<br><sub>(129 MiB / 54.2% CPU)</sub> | *Not possible (no H3 upstream)* | **26,201**<br><sub>(123 MiB / 51.9% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **14,399**<br><sub>(101 MiB / 45.8% CPU)</sub> | *Not possible (no QUIC)* | **14,330**<br><sub>(142 MiB / 51.1% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **17,592**<br><sub>(116 MiB / 44.1% CPU)</sub> | *Not possible (no QUIC)* | **15,607**<br><sub>(147 MiB / 48.8% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/2 · plain | 🥇 **34,116**<br><sub>(136 MiB / 47.7% CPU)</sub> | *Not possible (no H2 upstream)* | **23,176**<br><sub>(151 MiB / 47.4% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | 🥇 **31,135**<br><sub>(137 MiB / 47.1% CPU)</sub> | *Not possible (no H2 upstream)* | **21,466**<br><sub>(150 MiB / 48.8% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/3 · QUIC | **17,411**<br><sub>(119 MiB / 41.5% CPU)</sub> | *Not possible (no H3 upstream)* | 🥇 **18,619**<br><sub>(163 MiB / 48.3% CPU)</sub> |

### MITM (TWP only)

Same Client×Origin wires with interception on (`compare-product` [35092827644](https://github.com/justcoding121/titanium-web-proxy/actions/runs/35092827644)). **Lite** = no-op handlers (unchanged-lite finish). **Full** = append-only header mutation (harness: one probe header each way; product: generic append-only relay via `MitmCompressedRelayHelper`). nginx/HAProxy/Envoy/YARP cannot MITM. **Lite÷Reverse** / **Full÷Reverse** vs bare reverse (**same job / comparison-group shard**). Completion gate: Lite ≥ **0.50×** and Full ≥ **0.50×** reverse sustain @ c=64 (median of 3 GHA runs); reverse TWP÷YARP ≥ **0.75×** (no terminate-peer gate).

**v1 append-only relay (2026-08-27):** Pre-fix H2→H2 Full÷Reverse was **0.13–0.16×** ([32960766249](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32960766249)). Post-fix @ `df172718`: H2 plain→H2 plain Full **0.77–0.79×**, H3→H1 Full **0.91–0.93×**, all MITM arms ≥ **0.70×** on median of [33041445371](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33041445371), [33055267086](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33055267086), [33055272140](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33055272140).

**v2 drop-only + non-unique append (2026-08-27):** `MitmStaticRebuildHelper` rebuilds static HPACK/QPACK after 1–4 unique header drops; trailing non-unique appends stay on compressed relay. Historical GHA median @ `9a2b3a1e` ([33087088466](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087088466), [33087091622](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087091622), [33105885748](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33105885748) Linux; [33087085235](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087085235), [33087088466](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087088466), [33087091622](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087091622) Windows). Current suite (@ `8bfa7852`) still clears the MITM gates (Lite/Full ≥ **0.50×** reverse); see tables below for live Full÷Reverse.

| Client | Origin | Lite sustain | Full sustain | Lite÷Reverse | Full÷Reverse |
|---|---|---:|---:|---:|---:|
| HTTP/1 · plain | HTTP/1 · plain | **30,988**<br><sub>(80 MiB / 46.4% CPU)</sub> | **30,492**<br><sub>(82 MiB / 48.2% CPU)</sub> | **0.98×** | **0.97×** |
| HTTP/1 · plain | HTTP/1 · TLS | **20,384**<br><sub>(95 MiB / 52.8% CPU)</sub> | **20,198**<br><sub>(95 MiB / 52.5% CPU)</sub> | **1×** | **0.99×** |
| HTTP/1 · plain | HTTP/2 · plain | **35,601**<br><sub>(110 MiB / 48% CPU)</sub> | **35,122**<br><sub>(105 MiB / 49.1% CPU)</sub> | **0.96×** | **0.95×** |
| HTTP/1 · plain | HTTP/2 · TLS | **39,383**<br><sub>(120 MiB / 48.3% CPU)</sub> | **38,670**<br><sub>(120 MiB / 47.9% CPU)</sub> | **1×** | **0.98×** |
| HTTP/1 · plain | HTTP/3 · QUIC | **18,054**<br><sub>(103 MiB / 53.2% CPU)</sub> | **17,685**<br><sub>(107 MiB / 52.2% CPU)</sub> | **1.57×** | **1.54×** |
| HTTP/1 · TLS | HTTP/1 · plain | **19,722**<br><sub>(94 MiB / 51.6% CPU)</sub> | **19,661**<br><sub>(96 MiB / 51.5% CPU)</sub> | **0.98×** | **0.97×** |
| HTTP/1 · TLS | HTTP/1 · TLS | **23,590**<br><sub>(95 MiB / 48.8% CPU)</sub> | **23,182**<br><sub>(96 MiB / 46.8% CPU)</sub> | **1×** | **0.99×** |
| HTTP/1 · TLS | HTTP/2 · plain | **27,300**<br><sub>(110 MiB / 48.4% CPU)</sub> | **26,872**<br><sub>(112 MiB / 47.3% CPU)</sub> | **0.98×** | **0.97×** |
| HTTP/1 · TLS | HTTP/2 · TLS | **25,107**<br><sub>(124 MiB / 45.4% CPU)</sub> | **24,748**<br><sub>(127 MiB / 47.5% CPU)</sub> | **1×** | **0.99×** |
| HTTP/1 · TLS | HTTP/3 · QUIC | **18,795**<br><sub>(112 MiB / 50.6% CPU)</sub> | **18,674**<br><sub>(115 MiB / 53.7% CPU)</sub> | **0.97×** | **0.96×** |
| HTTP/2 · plain | HTTP/1 · plain | **33,811**<br><sub>(97 MiB / 55.8% CPU)</sub> | **33,355**<br><sub>(100 MiB / 55.9% CPU)</sub> | **0.97×** | **0.96×** |
| HTTP/2 · plain | HTTP/1 · TLS | **29,166**<br><sub>(105 MiB / 53.2% CPU)</sub> | **28,519**<br><sub>(103 MiB / 55.1% CPU)</sub> | **0.98×** | **0.96×** |
| HTTP/2 · plain | HTTP/2 · plain | **90,897**<br><sub>(69 MiB / 41.3% CPU)</sub> | **84,998**<br><sub>(70 MiB / 43% CPU)</sub> | **0.79×** | **0.74×** |
| HTTP/2 · plain | HTTP/2 · TLS | **70,465**<br><sub>(78 MiB / 38.9% CPU)</sub> | **66,829**<br><sub>(79 MiB / 38.1% CPU)</sub> | **0.82×** | **0.77×** |
| HTTP/2 · plain | HTTP/3 · QUIC | **31,297**<br><sub>(116 MiB / 54.3% CPU)</sub> | **30,424**<br><sub>(115 MiB / 55.6% CPU)</sub> | **0.99×** | **0.96×** |
| HTTP/2 · TLS | HTTP/1 · plain | **38,437**<br><sub>(103 MiB / 53.4% CPU)</sub> | **37,561**<br><sub>(112 MiB / 51.9% CPU)</sub> | **0.98×** | **0.96×** |
| HTTP/2 · TLS | HTTP/1 · TLS | **28,202**<br><sub>(105 MiB / 55.9% CPU)</sub> | **27,538**<br><sub>(102 MiB / 54.8% CPU)</sub> | **0.96×** | **0.93×** |
| HTTP/2 · TLS | HTTP/2 · plain | **79,360**<br><sub>(87 MiB / 40.9% CPU)</sub> | **74,807**<br><sub>(87 MiB / 40.4% CPU)</sub> | **0.79×** | **0.74×** |
| HTTP/2 · TLS | HTTP/2 · TLS | **74,209**<br><sub>(81 MiB / 39.1% CPU)</sub> | **70,572**<br><sub>(85 MiB / 39.3% CPU)</sub> | **0.84×** | **0.79×** |
| HTTP/2 · TLS | HTTP/3 · QUIC | **30,269**<br><sub>(131 MiB / 53.9% CPU)</sub> | **29,544**<br><sub>(130 MiB / 52.9% CPU)</sub> | **1×** | **0.98×** |
| HTTP/3 · QUIC | HTTP/1 · plain | **13,942**<br><sub>(113 MiB / 45.5% CPU)</sub> | **13,712**<br><sub>(111 MiB / 46.6% CPU)</sub> | **0.97×** | **0.95×** |
| HTTP/3 · QUIC | HTTP/1 · TLS | **16,217**<br><sub>(120 MiB / 44% CPU)</sub> | **15,113**<br><sub>(124 MiB / 44.8% CPU)</sub> | **0.92×** | **0.86×** |
| HTTP/3 · QUIC | HTTP/2 · plain | **31,662**<br><sub>(139 MiB / 51.8% CPU)</sub> | **29,770**<br><sub>(139 MiB / 50.7% CPU)</sub> | **0.93×** | **0.87×** |
| HTTP/3 · QUIC | HTTP/2 · TLS | **28,483**<br><sub>(130 MiB / 49.9% CPU)</sub> | **27,720**<br><sub>(133 MiB / 48.2% CPU)</sub> | **0.91×** | **0.89×** |
| HTTP/3 · QUIC | HTTP/3 · QUIC | **17,584**<br><sub>(123 MiB / 45.2% CPU)</sub> | **23,082**<br><sub>(123 MiB / 46.4% CPU)</sub> | **1.01×** | **1.33×** |

## Linux — Titanium vs nginx vs HAProxy vs Envoy vs YARP

### Reverse

Median of **3 repeats** on `ubuntu-latest` (4 vCPU / 16 GiB). Bare reverse 5×5 @ `8bfa7852` — `compare-product` [35092827644](https://github.com/justcoding121/titanium-web-proxy/actions/runs/35092827644). Warmup 2s / measure 8s; concurrency 8, 16, 32, 64. **Linux nginx is the authoritative nginx baseline.** HAProxy (3.2 `USE_QUIC`) and Envoy (GitHub release, HTTP/3 compiled in) run on the same loopback shape as nginx/YARP. nginx terminate peers use `keepalive 256` + streaming buffers. The RPS workflow installs nginx.org mainline (`http_v3_module`), a QUIC-enabled HAProxy, Envoy, and `libmsquic`. Prefer ratios over absolute RPS. Product 5×5 is **~56-byte JSON keep-alive GET**; H2/H3 same-protocol cells are mostly header work with a tiny body (Titanium best case) — see [Why this comparison is fair](#why-this-comparison-is-fair).

| Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---:|---:|---:|---:|---:|
| HTTP/1 · plain | HTTP/1 · plain | **52,118**<br><sub>(95 MiB / 48.8% CPU)</sub> | 🥇 **70,158**<br><sub>(76 MiB / 38.3% CPU)</sub> | **66,062**<br><sub>(64 MiB / 41.8% CPU)</sub> | **38,126**<br><sub>(116 MiB / 59.3% CPU)</sub> | **47,079**<br><sub>(115 MiB / 49.4% CPU)</sub> |
| HTTP/1 · plain | HTTP/1 · TLS | **23,523**<br><sub>(107 MiB / 51.1% CPU)</sub> | **28,019**<br><sub>(92 MiB / 42.1% CPU)</sub> | 🥇 **28,193**<br><sub>(68 MiB / 43.6% CPU)</sub> | **17,450**<br><sub>(118 MiB / 59% CPU)</sub> | **21,545**<br><sub>(127 MiB / 50.3% CPU)</sub> |
| HTTP/1 · plain | HTTP/2 · plain | 🥇 **40,142**<br><sub>(129 MiB / 51.1% CPU)</sub> | *Not possible (no H2 upstream)* | **26,857**<br><sub>(65 MiB / 42.4% CPU)</sub> | **22,067**<br><sub>(116 MiB / 63.3% CPU)</sub> | **35,268**<br><sub>(126 MiB / 48.8% CPU)</sub> |
| HTTP/1 · plain | HTTP/2 · TLS | **51,676**<br><sub>(158 MiB / 48.4% CPU)</sub> | *Not possible (no H2 upstream)* | 🥇 **54,114**<br><sub>(68 MiB / 41% CPU)</sub> | **37,450**<br><sub>(117 MiB / 57.4% CPU)</sub> | **48,422**<br><sub>(131 MiB / 48.2% CPU)</sub> |
| HTTP/1 · plain | HTTP/3 · QUIC | 🥇 **23,529**<br><sub>(143 MiB / 53.6% CPU)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **10,746**<br><sub>(120 MiB / 56.1% CPU)</sub> | **21,315**<br><sub>(156 MiB / 49.2% CPU)</sub> |
| HTTP/1 · TLS | HTTP/1 · plain | **22,984**<br><sub>(112 MiB / 49.8% CPU)</sub> | 🥇 **27,706**<br><sub>(100 MiB / 41.6% CPU)</sub> | **27,584**<br><sub>(83 MiB / 43.2% CPU)</sub> | **17,122**<br><sub>(127 MiB / 58% CPU)</sub> | **20,033**<br><sub>(134 MiB / 50.2% CPU)</sub> |
| HTTP/1 · TLS | HTTP/1 · TLS | **35,195**<br><sub>(112 MiB / 45.8% CPU)</sub> | **43,625**<br><sub>(105 MiB / 38.9% CPU)</sub> | 🥇 **44,407**<br><sub>(84 MiB / 40.7% CPU)</sub> | **30,389**<br><sub>(127 MiB / 53.8% CPU)</sub> | **31,410**<br><sub>(143 MiB / 48.3% CPU)</sub> |
| HTTP/1 · TLS | HTTP/2 · plain | 🥇 **27,813**<br><sub>(141 MiB / 50.6% CPU)</sub> | *Not possible (no H2 upstream)* | **21,585**<br><sub>(84 MiB / 42.3% CPU)</sub> | **18,519**<br><sub>(126 MiB / 58.2% CPU)</sub> | **25,000**<br><sub>(148 MiB / 49.7% CPU)</sub> |
| HTTP/1 · TLS | HTTP/2 · TLS | 🥇 **24,173**<br><sub>(155 MiB / 48.7% CPU)</sub> | *Not possible (no H2 upstream)* | **23,923**<br><sub>(84 MiB / 43.3% CPU)</sub> | **17,323**<br><sub>(126 MiB / 57.5% CPU)</sub> | **22,008**<br><sub>(148 MiB / 47.6% CPU)</sub> |
| HTTP/1 · TLS | HTTP/3 · QUIC | 🥇 **26,290**<br><sub>(157 MiB / 51.9% CPU)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **17,802**<br><sub>(130 MiB / 52.4% CPU)</sub> | **24,262**<br><sub>(171 MiB / 49.8% CPU)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | 🥇 **36,694**<br><sub>(120 MiB / 53.3% CPU)</sub> | **16,440**<br><sub>(79 MiB / 19% CPU)</sub> | **23,041**<br><sub>(66 MiB / 24.6% CPU)</sub> | **13,601**<br><sub>(118 MiB / 23.2% CPU)</sub> | **34,322**<br><sub>(114 MiB / 50.2% CPU)</sub> |
| HTTP/2 · plain | HTTP/1 · TLS | 🥇 **27,336**<br><sub>(129 MiB / 51% CPU)</sub> | **12,859**<br><sub>(100 MiB / 19.6% CPU)</sub> | **18,613**<br><sub>(70 MiB / 24.6% CPU)</sub> | **12,985**<br><sub>(119 MiB / 23.4% CPU)</sub> | **25,795**<br><sub>(127 MiB / 51% CPU)</sub> |
| HTTP/2 · plain | HTTP/2 · plain | 🥇 **118,021**<br><sub>(89 MiB / 36% CPU)</sub> | *Not possible (no H2 upstream)* | **48,169**<br><sub>(68 MiB / 24.2% CPU)</sub> | **25,730**<br><sub>(116 MiB / 21.3% CPU)</sub> | **68,950**<br><sub>(137 MiB / 48% CPU)</sub> |
| HTTP/2 · plain | HTTP/2 · TLS | 🥇 **58,227**<br><sub>(86 MiB / 35.1% CPU)</sub> | *Not possible (no H2 upstream)* | **19,761**<br><sub>(69 MiB / 24.6% CPU)</sub> | **15,929**<br><sub>(118 MiB / 21.6% CPU)</sub> | **39,519**<br><sub>(128 MiB / 45.6% CPU)</sub> |
| HTTP/2 · plain | HTTP/3 · QUIC | 🥇 **27,742**<br><sub>(147 MiB / 50.5% CPU)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **4,280**<br><sub>(121 MiB / 24.9% CPU)</sub> | **26,149**<br><sub>(153 MiB / 46.4% CPU)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **49,222**<br><sub>(125 MiB / 49.5% CPU)</sub> | **30,623**<br><sub>(102 MiB / 19.1% CPU)</sub> | **40,300**<br><sub>(81 MiB / 24.3% CPU)</sub> | **22,680**<br><sub>(129 MiB / 22.7% CPU)</sub> | **45,846**<br><sub>(123 MiB / 47.7% CPU)</sub> |
| HTTP/2 · TLS | HTTP/1 · TLS | 🥇 **26,240**<br><sub>(122 MiB / 50.9% CPU)</sub> | **12,497**<br><sub>(110 MiB / 19.9% CPU)</sub> | **16,768**<br><sub>(85 MiB / 24.4% CPU)</sub> | **13,048**<br><sub>(128 MiB / 23.3% CPU)</sub> | **23,132**<br><sub>(133 MiB / 50.3% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | 🥇 **75,608**<br><sub>(99 MiB / 36.6% CPU)</sub> | *Not possible (no H2 upstream)* | **21,385**<br><sub>(82 MiB / 24.4% CPU)</sub> | **17,016**<br><sub>(126 MiB / 22.1% CPU)</sub> | **39,430**<br><sub>(130 MiB / 47.6% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **81,336**<br><sub>(101 MiB / 34.1% CPU)</sub> | *Not possible (no H2 upstream)* | **38,308**<br><sub>(82 MiB / 24.2% CPU)</sub> | **24,040**<br><sub>(126 MiB / 21.6% CPU)</sub> | **46,396**<br><sub>(129 MiB / 44% CPU)</sub> |
| HTTP/2 · TLS | HTTP/3 · QUIC | 🥇 **25,960**<br><sub>(154 MiB / 50.3% CPU)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **4,339**<br><sub>(129 MiB / 24.8% CPU)</sub> | **22,717**<br><sub>(154 MiB / 47% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | **20,471**<br><sub>(147 MiB / 50% CPU)</sub> | **0**<br><sub>(peak 15,362 · 106 MiB / 22.5% CPU)</sub> | 🥇 **23,011**<br><sub>(87 MiB / 26.3% CPU)</sub> | **3,989**<br><sub>(136 MiB / 24.8% CPU)</sub> | **18,716**<br><sub>(183 MiB / 50.2% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | **23,817**<br><sub>(165 MiB / 45.2% CPU)</sub> | **0**<br><sub>(peak 27,931 · 123 MiB / 20.1% CPU)</sub> | 🥇 **29,255**<br><sub>(91 MiB / 25.8% CPU)</sub> | **14**<br><sub>(136 MiB / 0.1% CPU)</sub> | **22,334**<br><sub>(203 MiB / 47.7% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/2 · plain | 🥇 **28,969**<br><sub>(158 MiB / 53% CPU)</sub> | *Not possible (no H2 upstream)* | **24,863**<br><sub>(86 MiB / 25.8% CPU)</sub> | **26**<br><sub>(130 MiB / 0.2% CPU)</sub> | **23,885**<br><sub>(197 MiB / 48.2% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | 🥇 **25,108**<br><sub>(157 MiB / 50.6% CPU)</sub> | *Not possible (no H2 upstream)* | **20,697**<br><sub>(88 MiB / 25.4% CPU)</sub> | **1,471**<br><sub>(132 MiB / 7.8% CPU)</sub> | **21,179**<br><sub>(196 MiB / 47.3% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/3 · QUIC | 🥇 **22,764**<br><sub>(170 MiB / 44.9% CPU)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **448**<br><sub>(130 MiB / 2.2% CPU)</sub> | **19,268**<br><sub>(210 MiB / 47.5% CPU)</sub> |

### MITM (TWP only)

Same Client×Origin wires with interception on (`compare-product` [35092827644](https://github.com/justcoding121/titanium-web-proxy/actions/runs/35092827644)). **Lite** = no-op handlers (unchanged-lite finish). **Full** = append-only header mutation (harness: one probe header each way; product: generic append-only relay via `MitmCompressedRelayHelper`). nginx/HAProxy/Envoy/YARP cannot MITM. **Lite÷Reverse** / **Full÷Reverse** vs bare reverse (**same job / comparison-group shard**). Completion gate: Lite ≥ **0.50×** and Full ≥ **0.50×** reverse sustain @ c=64 (median of 3 GHA runs); reverse TWP÷YARP ≥ **0.75×** (no terminate-peer gate).

**v1 append-only relay (2026-08-27):** Pre-fix H2→H2 Full÷Reverse was **0.13–0.16×** ([32960766249](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32960766249)). Post-fix @ `df172718`: H2 plain→H2 plain Full **0.77–0.79×**, H3→H1 Full **0.91–0.93×**, all MITM arms ≥ **0.70×** on median of [33041445371](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33041445371), [33055267086](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33055267086), [33055272140](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33055272140).

**v2 drop-only + non-unique append (2026-08-27):** `MitmStaticRebuildHelper` rebuilds static HPACK/QPACK after 1–4 unique header drops; trailing non-unique appends stay on compressed relay. Historical GHA median @ `9a2b3a1e` ([33087088466](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087088466), [33087091622](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087091622), [33105885748](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33105885748) Linux; [33087085235](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087085235), [33087088466](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087088466), [33087091622](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087091622) Windows). Current suite (@ `8bfa7852`) still clears the MITM gates (Lite/Full ≥ **0.50×** reverse); see tables below for live Full÷Reverse.

| Client | Origin | Lite sustain | Full sustain | Lite÷Reverse | Full÷Reverse |
|---|---|---:|---:|---:|---:|
| HTTP/1 · plain | HTTP/1 · plain | **51,441**<br><sub>(97 MiB / 50.2% CPU)</sub> | **50,516**<br><sub>(98 MiB / 49.8% CPU)</sub> | **0.99×** | **0.97×** |
| HTTP/1 · plain | HTTP/1 · TLS | **23,548**<br><sub>(112 MiB / 52.2% CPU)</sub> | **23,482**<br><sub>(113 MiB / 51.3% CPU)</sub> | **1×** | **1×** |
| HTTP/1 · plain | HTTP/2 · plain | **40,450**<br><sub>(132 MiB / 53.8% CPU)</sub> | **38,305**<br><sub>(126 MiB / 53% CPU)</sub> | **1.01×** | **0.95×** |
| HTTP/1 · plain | HTTP/2 · TLS | **50,652**<br><sub>(153 MiB / 50.5% CPU)</sub> | **49,728**<br><sub>(154 MiB / 50.8% CPU)</sub> | **0.98×** | **0.96×** |
| HTTP/1 · plain | HTTP/3 · QUIC | **22,774**<br><sub>(142 MiB / 54.2% CPU)</sub> | **22,822**<br><sub>(137 MiB / 54.3% CPU)</sub> | **0.97×** | **0.97×** |
| HTTP/1 · TLS | HTTP/1 · plain | **23,114**<br><sub>(115 MiB / 50.9% CPU)</sub> | **22,808**<br><sub>(116 MiB / 50.3% CPU)</sub> | **1.01×** | **0.99×** |
| HTTP/1 · TLS | HTTP/1 · TLS | **34,966**<br><sub>(117 MiB / 46.7% CPU)</sub> | **34,270**<br><sub>(118 MiB / 46.8% CPU)</sub> | **0.99×** | **0.97×** |
| HTTP/1 · TLS | HTTP/2 · plain | **27,488**<br><sub>(143 MiB / 51.3% CPU)</sub> | **26,553**<br><sub>(148 MiB / 51.2% CPU)</sub> | **0.99×** | **0.95×** |
| HTTP/1 · TLS | HTTP/2 · TLS | **23,559**<br><sub>(170 MiB / 48.9% CPU)</sub> | **23,089**<br><sub>(169 MiB / 48.7% CPU)</sub> | **0.97×** | **0.96×** |
| HTTP/1 · TLS | HTTP/3 · QUIC | **25,937**<br><sub>(156 MiB / 52.5% CPU)</sub> | **26,697**<br><sub>(161 MiB / 53.2% CPU)</sub> | **0.99×** | **1.02×** |
| HTTP/2 · plain | HTTP/1 · plain | **36,272**<br><sub>(120 MiB / 54.6% CPU)</sub> | **34,343**<br><sub>(120 MiB / 54.4% CPU)</sub> | **0.99×** | **0.94×** |
| HTTP/2 · plain | HTTP/1 · TLS | **27,164**<br><sub>(124 MiB / 52.4% CPU)</sub> | **26,445**<br><sub>(129 MiB / 52.2% CPU)</sub> | **0.99×** | **0.97×** |
| HTTP/2 · plain | HTTP/2 · plain | **88,034**<br><sub>(101 MiB / 40.5% CPU)</sub> | **81,934**<br><sub>(98 MiB / 40.2% CPU)</sub> | **0.75×** | **0.69×** |
| HTTP/2 · plain | HTTP/2 · TLS | **48,275**<br><sub>(95 MiB / 40.2% CPU)</sub> | **45,097**<br><sub>(98 MiB / 40.2% CPU)</sub> | **0.83×** | **0.77×** |
| HTTP/2 · plain | HTTP/3 · QUIC | **27,595**<br><sub>(143 MiB / 50.5% CPU)</sub> | **27,350**<br><sub>(147 MiB / 51.1% CPU)</sub> | **0.99×** | **0.99×** |
| HTTP/2 · TLS | HTTP/1 · plain | **48,535**<br><sub>(132 MiB / 50.6% CPU)</sub> | **46,791**<br><sub>(134 MiB / 50% CPU)</sub> | **0.99×** | **0.95×** |
| HTTP/2 · TLS | HTTP/1 · TLS | **25,779**<br><sub>(123 MiB / 51.8% CPU)</sub> | **25,125**<br><sub>(129 MiB / 52.1% CPU)</sub> | **0.98×** | **0.96×** |
| HTTP/2 · TLS | HTTP/2 · plain | **55,993**<br><sub>(110 MiB / 41.3% CPU)</sub> | **53,218**<br><sub>(114 MiB / 41% CPU)</sub> | **0.74×** | **0.7×** |
| HTTP/2 · TLS | HTTP/2 · TLS | **64,140**<br><sub>(113 MiB / 36.2% CPU)</sub> | **60,738**<br><sub>(112 MiB / 36.2% CPU)</sub> | **0.79×** | **0.75×** |
| HTTP/2 · TLS | HTTP/3 · QUIC | **26,305**<br><sub>(160 MiB / 51.5% CPU)</sub> | **25,709**<br><sub>(152 MiB / 50.6% CPU)</sub> | **1.01×** | **0.99×** |
| HTTP/3 · QUIC | HTTP/1 · plain | **19,415**<br><sub>(144 MiB / 50.9% CPU)</sub> | **19,586**<br><sub>(145 MiB / 50.1% CPU)</sub> | **0.95×** | **0.96×** |
| HTTP/3 · QUIC | HTTP/1 · TLS | **23,253**<br><sub>(171 MiB / 47.3% CPU)</sub> | **22,767**<br><sub>(170 MiB / 46% CPU)</sub> | **0.98×** | **0.96×** |
| HTTP/3 · QUIC | HTTP/2 · plain | **27,212**<br><sub>(160 MiB / 54.2% CPU)</sub> | **26,934**<br><sub>(163 MiB / 54.5% CPU)</sub> | **0.94×** | **0.93×** |
| HTTP/3 · QUIC | HTTP/2 · TLS | **23,551**<br><sub>(152 MiB / 51.9% CPU)</sub> | **22,624**<br><sub>(153 MiB / 51.9% CPU)</sub> | **0.94×** | **0.9×** |
| HTTP/3 · QUIC | HTTP/3 · QUIC | **21,049**<br><sub>(171 MiB / 48.3% CPU)</sub> | **20,830**<br><sub>(171 MiB / 48.2% CPU)</sub> | **0.92×** | **0.92×** |

## macOS — Titanium vs nginx vs HAProxy vs Envoy vs YARP

### Reverse

Median of **3 repeats** on `macos-15-intel` (4-core / 14 GB). Bare reverse 5×5 @ `8bfa7852` — `compare-product` [35092827644](https://github.com/justcoding121/titanium-web-proxy/actions/runs/35092827644). Warmup 2s / measure 8s; concurrency 8, 16, 32, 64. Prefer TWP÷peer ratios over absolute RPS. **RPS cells** include median RSS / CPU at the peak-RPS step as `<br><sub>(MiB / CPU%)</sub>`. The RPS workflow installs Homebrew nginx (`http_v3_module`), Homebrew HAProxy with `USE_QUIC` (3.2 source fallback), Envoy (Homebrew bottle or pinned darwin-amd64 1.36.7), Homebrew `libmsquic` (+ `DYLD_*`), and YARP. Do not publish from `macos-latest` (3-core / 7 GB). Product 5×5 is **~56-byte JSON keep-alive GET**; H2/H3 same-protocol cells are mostly header work with a tiny body (Titanium best case) — see [Why this comparison is fair](#why-this-comparison-is-fair).

| Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---:|---:|---:|---:|---:|
| HTTP/1 · plain | HTTP/1 · plain | 🥇 **16,348**<br><sub>(84 MiB / 39.4% CPU)</sub> | **9,898**<br><sub>(52 MiB / 18.6% CPU)</sub> | **15,804**<br><sub>(50 MiB / 31.4% CPU)</sub> | **6,117**<br><sub>(72 MiB / 41.8% CPU)</sub> | **13,286**<br><sub>(104 MiB / 41.2% CPU)</sub> |
| HTTP/1 · plain | HTTP/1 · TLS | 🥇 **12,783**<br><sub>(94 MiB / 43.1% CPU)</sub> | **5,967**<br><sub>(74 MiB / 18.6% CPU)</sub> | **12,525**<br><sub>(55 MiB / 33.2% CPU)</sub> | **5,670**<br><sub>(73 MiB / 48% CPU)</sub> | **9,909**<br><sub>(124 MiB / 40.9% CPU)</sub> |
| HTTP/1 · plain | HTTP/2 · plain | 🥇 **20,904**<br><sub>(88 MiB / 43.3% CPU)</sub> | *Not possible (no H2 upstream)* | **8,778**<br><sub>(51 MiB / 30.6% CPU)</sub> | **6,122**<br><sub>(71 MiB / 44.3% CPU)</sub> | **15,420**<br><sub>(112 MiB / 40.6% CPU)</sub> |
| HTTP/1 · plain | HTTP/2 · TLS | **13,267**<br><sub>(101 MiB / 37.9% CPU)</sub> | *Not possible (no H2 upstream)* | **12,220**<br><sub>(52 MiB / 31.2% CPU)</sub> | **7,310**<br><sub>(73 MiB / 48.7% CPU)</sub> | 🥇 **18,474**<br><sub>(116 MiB / 41.3% CPU)</sub> |
| HTTP/1 · plain | HTTP/3 · QUIC | **4,693**<br><sub>(89 MiB / 48.9% CPU)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **3,091**<br><sub>(75 MiB / 23.7% CPU)</sub> | 🥇 **5,445**<br><sub>(115 MiB / 38.5% CPU)</sub> |
| HTTP/1 · TLS | HTTP/1 · plain | **7,955**<br><sub>(94 MiB / 34.9% CPU)</sub> | **6,371**<br><sub>(74 MiB / 25.2% CPU)</sub> | **7,738**<br><sub>(65 MiB / 31.1% CPU)</sub> | **6,444**<br><sub>(80 MiB / 41.2% CPU)</sub> | 🥇 **8,665**<br><sub>(132 MiB / 38.8% CPU)</sub> |
| HTTP/1 · TLS | HTTP/1 · TLS | **8,977**<br><sub>(98 MiB / 36.1% CPU)</sub> | **5,494**<br><sub>(83 MiB / 18.9% CPU)</sub> | 🥇 **9,125**<br><sub>(68 MiB / 35.1% CPU)</sub> | **3,695**<br><sub>(81 MiB / 37.8% CPU)</sub> | **7,820**<br><sub>(139 MiB / 37.4% CPU)</sub> |
| HTTP/1 · TLS | HTTP/2 · plain | **9,860**<br><sub>(97 MiB / 39.1% CPU)</sub> | *Not possible (no H2 upstream)* | **7,800**<br><sub>(66 MiB / 32.1% CPU)</sub> | **5,521**<br><sub>(79 MiB / 45.2% CPU)</sub> | 🥇 **10,651**<br><sub>(118 MiB / 34.5% CPU)</sub> |
| HTTP/1 · TLS | HTTP/2 · TLS | **7,860**<br><sub>(163 MiB / 37% CPU)</sub> | *Not possible (no H2 upstream)* | **7,448**<br><sub>(66 MiB / 35.7% CPU)</sub> | **4,624**<br><sub>(79 MiB / 40.9% CPU)</sub> | 🥇 **11,054**<br><sub>(127 MiB / 34.9% CPU)</sub> |
| HTTP/1 · TLS | HTTP/3 · QUIC | **2,705**<br><sub>(109 MiB / 46.2% CPU)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **3,636**<br><sub>(81 MiB / 32.3% CPU)</sub> | 🥇 **4,619**<br><sub>(123 MiB / 35.2% CPU)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | 🥇 **16,372**<br><sub>(91 MiB / 42.4% CPU)</sub> | **15,429**<br><sub>(58 MiB / 18.7% CPU)</sub> | **10,220**<br><sub>(56 MiB / 20.3% CPU)</sub> | **3,692**<br><sub>(75 MiB / 22.4% CPU)</sub> | **14,817**<br><sub>(105 MiB / 44.2% CPU)</sub> |
| HTTP/2 · plain | HTTP/1 · TLS | 🥇 **12,382**<br><sub>(94 MiB / 46.4% CPU)</sub> | **7,196**<br><sub>(85 MiB / 18.3% CPU)</sub> | **5,558**<br><sub>(59 MiB / 20.8% CPU)</sub> | **3,155**<br><sub>(77 MiB / 21.5% CPU)</sub> | **12,044**<br><sub>(125 MiB / 45.8% CPU)</sub> |
| HTTP/2 · plain | HTTP/2 · plain | 🥇 **36,700**<br><sub>(73 MiB / 30.8% CPU)</sub> | *Not possible (no H2 upstream)* | **9,528**<br><sub>(57 MiB / 19.9% CPU)</sub> | **6,036**<br><sub>(72 MiB / 22% CPU)</sub> | **24,939**<br><sub>(111 MiB / 40.4% CPU)</sub> |
| HTTP/2 · plain | HTTP/2 · TLS | 🥇 **30,656**<br><sub>(77 MiB / 30.9% CPU)</sub> | *Not possible (no H2 upstream)* | **8,666**<br><sub>(58 MiB / 20.9% CPU)</sub> | **5,469**<br><sub>(73 MiB / 21.3% CPU)</sub> | **26,491**<br><sub>(114 MiB / 38.9% CPU)</sub> |
| HTTP/2 · plain | HTTP/3 · QUIC | **5,092**<br><sub>(95 MiB / 56.1% CPU)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **1,973**<br><sub>(75 MiB / 22.8% CPU)</sub> | 🥇 **5,453**<br><sub>(118 MiB / 39.1% CPU)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **15,856**<br><sub>(92 MiB / 44.7% CPU)</sub> | **9,770**<br><sub>(73 MiB / 18.1% CPU)</sub> | **8,427**<br><sub>(67 MiB / 21.2% CPU)</sub> | **3,491**<br><sub>(81 MiB / 21.4% CPU)</sub> | **12,707**<br><sub>(115 MiB / 43.2% CPU)</sub> |
| HTTP/2 · TLS | HTTP/1 · TLS | 🥇 **13,319**<br><sub>(96 MiB / 44.8% CPU)</sub> | **6,541**<br><sub>(91 MiB / 19% CPU)</sub> | **8,793**<br><sub>(69 MiB / 21.4% CPU)</sub> | **3,229**<br><sub>(83 MiB / 21.7% CPU)</sub> | **11,692**<br><sub>(123 MiB / 44.6% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | 🥇 **27,511**<br><sub>(84 MiB / 31.1% CPU)</sub> | *Not possible (no H2 upstream)* | **8,259**<br><sub>(68 MiB / 20.9% CPU)</sub> | **5,966**<br><sub>(79 MiB / 21.7% CPU)</sub> | **19,566**<br><sub>(112 MiB / 42.1% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **30,544**<br><sub>(84 MiB / 31.1% CPU)</sub> | *Not possible (no H2 upstream)* | **7,040**<br><sub>(68 MiB / 21.3% CPU)</sub> | **5,660**<br><sub>(79 MiB / 21.4% CPU)</sub> | **19,845**<br><sub>(116 MiB / 40.9% CPU)</sub> |
| HTTP/2 · TLS | HTTP/3 · QUIC | **5,713**<br><sub>(106 MiB / 45.9% CPU)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **2,513**<br><sub>(80 MiB / 22.9% CPU)</sub> | 🥇 **7,097**<br><sub>(121 MiB / 40.4% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | **4,598**<br><sub>(100 MiB / 46.2% CPU)</sub> | **0**<br><sub>(peak 6,020 · 62 MiB / 13.4% CPU)</sub> | 🥇 **9,237**<br><sub>(67 MiB / 23.9% CPU)</sub> | **1,238**<br><sub>(86 MiB / 23.4% CPU)</sub> | **4,568**<br><sub>(183 MiB / 39% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | **4,263**<br><sub>(117 MiB / 43.4% CPU)</sub> | **0**<br><sub>(peak 7,101 · 67 MiB / 16.4% CPU)</sub> | 🥇 **8,098**<br><sub>(69 MiB / 27.4% CPU)</sub> | **1,138**<br><sub>(87 MiB / 27.8% CPU)</sub> | **5,599**<br><sub>(204 MiB / 41.9% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/2 · plain | **6,267**<br><sub>(97 MiB / 45% CPU)</sub> | *Not possible (no H2 upstream)* | 🥇 **8,644**<br><sub>(67 MiB / 21.8% CPU)</sub> | **2,182**<br><sub>(83 MiB / 31.8% CPU)</sub> | **6,180**<br><sub>(179 MiB / 38.4% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | **5,881**<br><sub>(107 MiB / 46% CPU)</sub> | *Not possible (no H2 upstream)* | 🥇 **8,089**<br><sub>(69 MiB / 24.6% CPU)</sub> | **2,252**<br><sub>(84 MiB / 30.9% CPU)</sub> | **7,557**<br><sub>(171 MiB / 39.6% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/3 · QUIC | **3,988**<br><sub>(97 MiB / 39.4% CPU)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **904**<br><sub>(81 MiB / 27.3% CPU)</sub> | 🥇 **4,700**<br><sub>(182 MiB / 40.8% CPU)</sub> |

### MITM (TWP only)

Same Client×Origin wires with interception on (`compare-product` [35092827644](https://github.com/justcoding121/titanium-web-proxy/actions/runs/35092827644)). **Lite** = no-op handlers (unchanged-lite finish). **Full** = append-only header mutation (harness: one probe header each way; product: generic append-only relay via `MitmCompressedRelayHelper`). nginx/HAProxy/Envoy/YARP cannot MITM. **Lite÷Reverse** / **Full÷Reverse** vs bare reverse (**same job / comparison-group shard**). Completion gate: Lite ≥ **0.50×** and Full ≥ **0.50×** reverse sustain @ c=64 (median of 3 GHA runs); reverse TWP÷YARP ≥ **0.75×** (no terminate-peer gate).

**v1 append-only relay (2026-08-27):** Pre-fix H2→H2 Full÷Reverse was **0.13–0.16×** ([32960766249](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32960766249)). Post-fix @ `df172718`: H2 plain→H2 plain Full **0.77–0.79×**, H3→H1 Full **0.91–0.93×**, all MITM arms ≥ **0.70×** on median of [33041445371](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33041445371), [33055267086](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33055267086), [33055272140](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33055272140).

**v2 drop-only + non-unique append (2026-08-27):** `MitmStaticRebuildHelper` rebuilds static HPACK/QPACK after 1–4 unique header drops; trailing non-unique appends stay on compressed relay. Historical GHA median @ `9a2b3a1e` ([33087088466](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087088466), [33087091622](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087091622), [33105885748](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33105885748) Linux; [33087085235](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087085235), [33087088466](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087088466), [33087091622](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087091622) Windows). Current suite (@ `8bfa7852`) still clears the MITM gates (Lite/Full ≥ **0.50×** reverse); see tables below for live Full÷Reverse.

| Client | Origin | Lite sustain | Full sustain | Lite÷Reverse | Full÷Reverse |
|---|---|---:|---:|---:|---:|
| HTTP/1 · plain | HTTP/1 · plain | **13,180**<br><sub>(84 MiB / 39.4% CPU)</sub> | **13,546**<br><sub>(85 MiB / 38.2% CPU)</sub> | **0.81×** | **0.83×** |
| HTTP/1 · plain | HTTP/1 · TLS | **11,128**<br><sub>(95 MiB / 40% CPU)</sub> | **10,009**<br><sub>(94 MiB / 37.9% CPU)</sub> | **0.87×** | **0.78×** |
| HTTP/1 · plain | HTTP/2 · plain | **18,046**<br><sub>(91 MiB / 44.7% CPU)</sub> | **12,264**<br><sub>(93 MiB / 43.8% CPU)</sub> | **0.86×** | **0.59×** |
| HTTP/1 · plain | HTTP/2 · TLS | **12,795**<br><sub>(106 MiB / 40.2% CPU)</sub> | **17,562**<br><sub>(106 MiB / 42.6% CPU)</sub> | **0.96×** | **1.32×** |
| HTTP/1 · plain | HTTP/3 · QUIC | **4,402**<br><sub>(91 MiB / 47.8% CPU)</sub> | **4,047**<br><sub>(93 MiB / 45.6% CPU)</sub> | **0.94×** | **0.86×** |
| HTTP/1 · TLS | HTTP/1 · plain | **11,591**<br><sub>(95 MiB / 39.4% CPU)</sub> | **7,671**<br><sub>(96 MiB / 38.9% CPU)</sub> | **1.46×** | **0.96×** |
| HTTP/1 · TLS | HTTP/1 · TLS | **8,412**<br><sub>(99 MiB / 37.9% CPU)</sub> | **10,725**<br><sub>(99 MiB / 38.9% CPU)</sub> | **0.94×** | **1.19×** |
| HTTP/1 · TLS | HTTP/2 · plain | **10,748**<br><sub>(100 MiB / 41.7% CPU)</sub> | **9,978**<br><sub>(105 MiB / 39.5% CPU)</sub> | **1.09×** | **1.01×** |
| HTTP/1 · TLS | HTTP/2 · TLS | **11,660**<br><sub>(161 MiB / 39.5% CPU)</sub> | **6,751**<br><sub>(156 MiB / 37.8% CPU)</sub> | **1.48×** | **0.86×** |
| HTTP/1 · TLS | HTTP/3 · QUIC | **2,975**<br><sub>(98 MiB / 54.7% CPU)</sub> | **3,009**<br><sub>(103 MiB / 49.8% CPU)</sub> | **1.1×** | **1.11×** |
| HTTP/2 · plain | HTTP/1 · plain | **16,657**<br><sub>(90 MiB / 43.4% CPU)</sub> | **16,350**<br><sub>(90 MiB / 44.4% CPU)</sub> | **1.02×** | **1×** |
| HTTP/2 · plain | HTTP/1 · TLS | **14,896**<br><sub>(98 MiB / 46.9% CPU)</sub> | **11,821**<br><sub>(97 MiB / 47% CPU)</sub> | **1.2×** | **0.95×** |
| HTTP/2 · plain | HTTP/2 · plain | **28,786**<br><sub>(79 MiB / 34.8% CPU)</sub> | **29,833**<br><sub>(76 MiB / 36.4% CPU)</sub> | **0.78×** | **0.81×** |
| HTTP/2 · plain | HTTP/2 · TLS | **26,546**<br><sub>(80 MiB / 38.2% CPU)</sub> | **34,124**<br><sub>(83 MiB / 36.4% CPU)</sub> | **0.87×** | **1.11×** |
| HTTP/2 · plain | HTTP/3 · QUIC | **4,078**<br><sub>(95 MiB / 52.4% CPU)</sub> | **4,254**<br><sub>(94 MiB / 53.3% CPU)</sub> | **0.8×** | **0.84×** |
| HTTP/2 · TLS | HTTP/1 · plain | **15,130**<br><sub>(95 MiB / 48.3% CPU)</sub> | **15,199**<br><sub>(92 MiB / 45.1% CPU)</sub> | **0.95×** | **0.96×** |
| HTTP/2 · TLS | HTTP/1 · TLS | **15,237**<br><sub>(100 MiB / 41.4% CPU)</sub> | **13,176**<br><sub>(97 MiB / 47% CPU)</sub> | **1.14×** | **0.99×** |
| HTTP/2 · TLS | HTTP/2 · plain | **26,045**<br><sub>(87 MiB / 38% CPU)</sub> | **31,311**<br><sub>(86 MiB / 36.2% CPU)</sub> | **0.95×** | **1.14×** |
| HTTP/2 · TLS | HTTP/2 · TLS | **29,113**<br><sub>(89 MiB / 36.4% CPU)</sub> | **28,038**<br><sub>(90 MiB / 38.6% CPU)</sub> | **0.95×** | **0.92×** |
| HTTP/2 · TLS | HTTP/3 · QUIC | **5,228**<br><sub>(107 MiB / 46.1% CPU)</sub> | **3,875**<br><sub>(107 MiB / 45.1% CPU)</sub> | **0.92×** | **0.68×** |
| HTTP/3 · QUIC | HTTP/1 · plain | **4,428**<br><sub>(101 MiB / 45.8% CPU)</sub> | **5,675**<br><sub>(102 MiB / 47.5% CPU)</sub> | **0.96×** | **1.23×** |
| HTTP/3 · QUIC | HTTP/1 · TLS | **4,527**<br><sub>(116 MiB / 46.2% CPU)</sub> | **4,613**<br><sub>(116 MiB / 44.1% CPU)</sub> | **1.06×** | **1.08×** |
| HTTP/3 · QUIC | HTTP/2 · plain | **5,291**<br><sub>(99 MiB / 49.4% CPU)</sub> | **4,946**<br><sub>(99 MiB / 48.2% CPU)</sub> | **0.84×** | **0.79×** |
| HTTP/3 · QUIC | HTTP/2 · TLS | **4,271**<br><sub>(105 MiB / 46.1% CPU)</sub> | **4,538**<br><sub>(107 MiB / 48.8% CPU)</sub> | **0.73×** | **0.77×** |
| HTTP/3 · QUIC | HTTP/3 · QUIC | **3,764**<br><sub>(95 MiB / 57.2% CPU)</sub> | **4,233**<br><sub>(93 MiB / 41.9% CPU)</sub> | **0.94×** | **1.06×** |

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

Same runners and harness as the tiny-GET tables, but with larger bodies, POST, lossy links, TLS cost, and architecture-sensitive paths (slow consumer / early response / duplex). **PUT with the same body is the same proxy work as POST; DELETE with no body matches GET** — only POST is published. Bodies/POST/lossy stay **half-duplex**. Laptop numbers are on [Performance Local Lab](Performance-Local-Lab#architecture-sensitive). How maintainers refresh these tables (modes, shards, paste scripts) is under [Maintainer notes](#maintainer-notes). **Larger-body check:** 64 / 256 KiB H2 TLS→H2 TLS is where body copy dominates headers — Titanium is **behind** YARP here (~0.57–0.81× at 64 KiB across OS), unlike the tiny-GET H2↔H2 medals above.

Lossy link = **userspace** delay/drop shim (not kernel `netem`): TCP gets per-buffer delay + occasional whole-connection stalls (honest head-of-line for multiplexed HTTP/2); UDP gets per-datagram delay + drops (QUIC). Lossy tables publish HTTP/1, HTTP/2, and HTTP/3.

### Windows — heavier reverse GET (64 KiB / 256 KiB)

Median of **3** repeats on `windows-latest` @ `8bfa7852`. Source: Actions [35092832912](https://github.com/justcoding121/titanium-web-proxy/actions/runs/35092832912) (`compare-bodies`). Warmup 2s / measure 8s. **RPS cells** include `(MiB / CPU%)` footprints.

*Not possible:* **HAProxy** and **Envoy** columns are omitted (no official Windows port).

| Body | Client | Origin | TWP | nginx | YARP |
|---|---|---|---:|---:|---:|
| 64 KiB | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **8,782**<br><sub>(114 MiB / 46.0% CPU)</sub> | **644**<br><sub>(142 MiB / 24.7% CPU)</sub> | **7,764**<br><sub>(135 MiB / 47.3% CPU)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **8,184**<br><sub>(174 MiB / 46.0% CPU)</sub> | **564**<br><sub>(142 MiB / 24.8% CPU)</sub> | **7,013**<br><sub>(135 MiB / 50.5% CPU)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **3,876**<br><sub>(132 MiB / 38.6% CPU)</sub> | *Not possible (no QUIC)* | **3,663**<br><sub>(188 MiB / 46.8% CPU)</sub> |
| 256 KiB | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **2,790**<br><sub>(129 MiB / 47.6% CPU)</sub> | **0**<br><sub>(peak 162 · 143 MiB / 24.8% CPU)</sub> | **2,586**<br><sub>(135 MiB / 48.0% CPU)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **2,537**<br><sub>(153 MiB / 40.3% CPU)</sub> | **0**<br><sub>(peak 142 · 142 MiB / 24.7% CPU)</sub> | **1,909**<br><sub>(134 MiB / 45.1% CPU)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **1,121**<br><sub>(107 MiB / 39.8% CPU)</sub> | *Not possible (no QUIC)* | **1,048**<br><sub>(170 MiB / 46.1% CPU)</sub> |
| 64 KiB | HTTP/2 · plain | HTTP/1 · plain | 🥇 **11,532**<br><sub>(170 MiB / 42.4% CPU)</sub> | **2,087**<br><sub>(127 MiB / 24.8% CPU)</sub> | **9,578**<br><sub>(118 MiB / 45.0% CPU)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/2 · plain | **3,848**<br><sub>(77 MiB / 35.2% CPU)</sub> | *Not possible* | 🥇 **6,673**<br><sub>(140 MiB / 47.9% CPU)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/2 · TLS | **3,495**<br><sub>(78 MiB / 37.3% CPU)</sub> | *Not possible* | 🥇 **6,000**<br><sub>(140 MiB / 47.2% CPU)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/2 · TLS | **3,396**<br><sub>(125 MiB / 43.6% CPU)</sub> | *Not possible* | 🥇 **3,552**<br><sub>(184 MiB / 46.8% CPU)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **3,934**<br><sub>(138 MiB / 41.5% CPU)</sub> | *Not possible (no QUIC)* | **3,307**<br><sub>(199 MiB / 49.0% CPU)</sub> |
| 256 KiB | HTTP/2 · plain | HTTP/1 · plain | 🥇 **3,784**<br><sub>(144 MiB / 30.6% CPU)</sub> | **655**<br><sub>(127 MiB / 24.3% CPU)</sub> | **2,823**<br><sub>(123 MiB / 38.1% CPU)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/2 · plain | **1,382**<br><sub>(86 MiB / 33.1% CPU)</sub> | *Not possible* | 🥇 **1,808**<br><sub>(163 MiB / 47.4% CPU)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/2 · TLS | **1,277**<br><sub>(87 MiB / 34.7% CPU)</sub> | *Not possible* | 🥇 **1,410**<br><sub>(146 MiB / 44.2% CPU)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/2 · TLS | **869**<br><sub>(151 MiB / 42.2% CPU)</sub> | *Not possible* | 🥇 **979**<br><sub>(191 MiB / 44.0% CPU)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **1,090**<br><sub>(148 MiB / 40.6% CPU)</sub> | *Not possible (no QUIC)* | **990**<br><sub>(198 MiB / 44.3% CPU)</sub> |

nginx/Windows collapses on large reverse bodies in this harness; treat as same-OS only. H1 TLS **64 KiB** ≈ **1.13×** YARP; **256 KiB** ≈ **1.08×**. H2→H1 64 KiB ≈ **1.17×**; H3→H1 64 KiB ≈ **1.06×**.

### Linux — heavier reverse GET (64 KiB / 256 KiB)

Median of **3** repeats @ `8bfa7852`. Source: Actions [35092832912](https://github.com/justcoding121/titanium-web-proxy/actions/runs/35092832912) (`compare-bodies`). Warmup 2s / measure 8s.

| Body | Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---|---:|---:|---:|---:|---:|
| 64 KiB | HTTP/1 · TLS | HTTP/1 · plain | **8,545**<br><sub>(175 MiB / 44.2% CPU)</sub> | **5,990**<br><sub>(100 MiB / 49.8% CPU)</sub> | 🥇 **9,584**<br><sub>(83 MiB / 39.5% CPU)</sub> | **8,284**<br><sub>(132 MiB / 43.3% CPU)</sub> | **7,035**<br><sub>(164 MiB / 47.9% CPU)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **5,771**<br><sub>(221 MiB / 40.2% CPU)</sub> | **1,831**<br><sub>(101 MiB / 16.9% CPU)</sub> | **4,786**<br><sub>(84 MiB / 24.4% CPU)</sub> | **4,353**<br><sub>(140 MiB / 23.6% CPU)</sub> | **4,800**<br><sub>(162 MiB / 48.5% CPU)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **5,873**<br><sub>(183 MiB / 43.5% CPU)</sub> | **0**<br><sub>(peak 1,851 · 129 MiB / 23.6% CPU)</sub> | **5,030**<br><sub>(88 MiB / 32.8% CPU)</sub> | **123**<br><sub>(139 MiB / 1.1% CPU)</sub> | **4,568**<br><sub>(231 MiB / 51.3% CPU)</sub> |
| 256 KiB | HTTP/1 · TLS | HTTP/1 · plain | **2,794**<br><sub>(129 MiB / 35.7% CPU)</sub> | **1,514**<br><sub>(100 MiB / 47.3% CPU)</sub> | 🥇 **3,024**<br><sub>(83 MiB / 32.8% CPU)</sub> | **2,727**<br><sub>(145 MiB / 32.2% CPU)</sub> | **2,211**<br><sub>(170 MiB / 44.0% CPU)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/1 · plain | **1,563**<br><sub>(195 MiB / 37.2% CPU)</sub> | **595**<br><sub>(100 MiB / 21.3% CPU)</sub> | **1,588**<br><sub>(83 MiB / 21.1% CPU)</sub> | 🥇 **1,900**<br><sub>(158 MiB / 20.8% CPU)</sub> | **1,355**<br><sub>(161 MiB / 45.0% CPU)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **1,519**<br><sub>(159 MiB / 42.6% CPU)</sub> | **0**<br><sub>(118 MiB / 20.6% CPU)</sub> | **1,344**<br><sub>(89 MiB / 30.3% CPU)</sub> | **25**<br><sub>(157 MiB / 0.7% CPU)</sub> | **1,304**<br><sub>(218 MiB / 47.4% CPU)</sub> |
| 64 KiB | HTTP/2 · plain | HTTP/1 · plain | 🥇 **9,681**<br><sub>(237 MiB / 42.5% CPU)</sub> | **2,321**<br><sub>(80 MiB / 12.9% CPU)</sub> | **7,167**<br><sub>(70 MiB / 24.3% CPU)</sub> | **6,374**<br><sub>(132 MiB / 23.9% CPU)</sub> | **8,267**<br><sub>(153 MiB / 45.9% CPU)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/2 · plain | **3,709**<br><sub>(103 MiB / 38.7% CPU)</sub> | *Not possible* | **2,594**<br><sub>(86 MiB / 24.7% CPU)</sub> | 🥇 **5,209**<br><sub>(144 MiB / 22.1% CPU)</sub> | **4,981**<br><sub>(184 MiB / 46.5% CPU)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/2 · TLS | **2,716**<br><sub>(102 MiB / 38.4% CPU)</sub> | *Not possible* | **1,584**<br><sub>(83 MiB / 24.5% CPU)</sub> | **3,262**<br><sub>(146 MiB / 23.0% CPU)</sub> | 🥇 **3,359**<br><sub>(171 MiB / 45.6% CPU)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/2 · TLS | **3,060**<br><sub>(164 MiB / 53.0% CPU)</sub> | *Not possible* | **2,380**<br><sub>(90 MiB / 35.2% CPU)</sub> | **1**<br><sub>(144 MiB / 0.1% CPU)</sub> | 🥇 **3,219**<br><sub>(240 MiB / 49.1% CPU)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **3,926**<br><sub>(203 MiB / 44.0% CPU)</sub> | **0**<br><sub>(peak 1,472 · 143 MiB / 23.0% CPU)</sub> | **3,080**<br><sub>(94 MiB / 31.8% CPU)</sub> | **0**<br><sub>(140 MiB / 0.1% CPU)</sub> | **3,182**<br><sub>(240 MiB / 49.5% CPU)</sub> |
| 256 KiB | HTTP/2 · plain | HTTP/1 · plain | **2,768**<br><sub>(211 MiB / 36.3% CPU)</sub> | **877**<br><sub>(79 MiB / 20.1% CPU)</sub> | **2,746**<br><sub>(70 MiB / 22.6% CPU)</sub> | 🥇 **3,006**<br><sub>(152 MiB / 22.6% CPU)</sub> | **2,859**<br><sub>(157 MiB / 38.6% CPU)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/2 · plain | **1,203**<br><sub>(113 MiB / 33.7% CPU)</sub> | *Not possible* | **772**<br><sub>(90 MiB / 24.3% CPU)</sub> | 🥇 **1,769**<br><sub>(169 MiB / 22.0% CPU)</sub> | **1,350**<br><sub>(187 MiB / 41.8% CPU)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/2 · TLS | **804**<br><sub>(109 MiB / 33.0% CPU)</sub> | *Not possible* | **433**<br><sub>(82 MiB / 24.6% CPU)</sub> | 🥇 **983**<br><sub>(162 MiB / 22.5% CPU)</sub> | **954**<br><sub>(179 MiB / 41.9% CPU)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/2 · TLS | **847**<br><sub>(193 MiB / 51.2% CPU)</sub> | *Not possible* | **706**<br><sub>(93 MiB / 36.1% CPU)</sub> | **0**<br><sub>(165 MiB / 0.1% CPU)</sub> | 🥇 **948**<br><sub>(230 MiB / 46.3% CPU)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **1,090**<br><sub>(208 MiB / 45.0% CPU)</sub> | **0**<br><sub>(peak 216 · 144 MiB / 21.9% CPU)</sub> | **872**<br><sub>(96 MiB / 32.0% CPU)</sub> | **0**<br><sub>(148 MiB / 0.2% CPU)</sub> | **989**<br><sub>(245 MiB / 47.6% CPU)</sub> |

On this GHA pass TWP÷YARP H1 TLS ≈ **1.21×** (64 KiB) / **1.26×** (256 KiB); H2→H1 ≈ **1.20×** / **1.15×**; H3→H1 ≈ **1.29×** / **1.16×**. TWP÷nginx H1 TLS ≈ **1.43** / **1.85**. Absolute RPS swings by VM; prefer ratios.

### Windows — POST 64 KiB request + 64 KiB response

Median of **3** repeats on `windows-latest` @ `8bfa7852`. Source: Actions [35092838467](https://github.com/justcoding121/titanium-web-proxy/actions/runs/35092838467) (`compare-post`).

*Not possible:* **HAProxy** and **Envoy** columns are omitted (no official Windows port).

| Client | Origin | TWP | nginx | YARP |
|---|---|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | 🥇 **6,265**<br><sub>(98 MiB / 43.7% CPU)</sub> | **361**<br><sub>(142 MiB / 24.7% CPU)</sub> | **4,403**<br><sub>(137 MiB / 54.9% CPU)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **4,552**<br><sub>(190 MiB / 48.0% CPU)</sub> | **364**<br><sub>(144 MiB / 24.9% CPU)</sub> | **2,959**<br><sub>(134 MiB / 38.4% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **2,026**<br><sub>(179 MiB / 39.5% CPU)</sub> | *Not possible (no QUIC)* | **1,928**<br><sub>(218 MiB / 49.6% CPU)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | 🥇 **6,752**<br><sub>(189 MiB / 45.0% CPU)</sub> | **1,799**<br><sub>(130 MiB / 24.7% CPU)</sub> | **6,174**<br><sub>(123 MiB / 52.5% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | **8**<br><sub>(88 MiB / 0.2% CPU)</sub> | *Not possible* | 🥇 **3,734**<br><sub>(145 MiB / 47.7% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | **8**<br><sub>(89 MiB / 0.1% CPU)</sub> | *Not possible* | 🥇 **2,795**<br><sub>(145 MiB / 46.3% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | **1,801**<br><sub>(180 MiB / 45.2% CPU)</sub> | *Not possible* | 🥇 **1,870**<br><sub>(209 MiB / 49.3% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **1,930**<br><sub>(180 MiB / 41.5% CPU)</sub> | *Not possible (no QUIC)* | **1,830**<br><sub>(221 MiB / 48.5% CPU)</sub> |

TWP leads H1 POST (~**1.4×** YARP), H2→H1 POST (~**1.5×** YARP), and H3 POST (~**1.05×** YARP). H2 TLS→H2 TLS POST sustain is ~0 this pass (same concurrent-copier cell as duplex).

### Linux — POST 64 KiB request + 64 KiB response

Median of **3** repeats @ `8bfa7852`. Source: Actions [35092838467](https://github.com/justcoding121/titanium-web-proxy/actions/runs/35092838467) (`compare-post`).

| Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---:|---:|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | **4,706**<br><sub>(131 MiB / 45.2% CPU)</sub> | **3,494**<br><sub>(100 MiB / 48.9% CPU)</sub> | **4,766**<br><sub>(85 MiB / 41.1% CPU)</sub> | 🥇 **4,816**<br><sub>(133 MiB / 42.2% CPU)</sub> | **3,114**<br><sub>(174 MiB / 55.3% CPU)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **3,051**<br><sub>(210 MiB / 47.8% CPU)</sub> | **1,296**<br><sub>(113 MiB / 21.6% CPU)</sub> | **1,773**<br><sub>(82 MiB / 24.1% CPU)</sub> | **0**<br><sub>(peak 2,797 · 142 MiB / 22.5% CPU)</sub> | **2,483**<br><sub>(170 MiB / 48.6% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **2,881**<br><sub>(226 MiB / 44.1% CPU)</sub> | **457**<br><sub>(109 MiB / 25.0% CPU)</sub> | **2,078**<br><sub>(91 MiB / 35.9% CPU)</sub> | **0**<br><sub>(125 MiB / 0.1% CPU)</sub> | **2,552**<br><sub>(259 MiB / 49.6% CPU)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | 🥇 **5,393**<br><sub>(226 MiB / 44.3% CPU)</sub> | **2,294**<br><sub>(97 MiB / 22.5% CPU)</sub> | **2,629**<br><sub>(69 MiB / 23.7% CPU)</sub> | **0**<br><sub>(peak 4,450 · 133 MiB / 23.6% CPU)</sub> | **4,330**<br><sub>(160 MiB / 48.2% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | **2**<br><sub>(117 MiB / 0.1% CPU)</sub> | *Not possible* | **1,363**<br><sub>(86 MiB / 24.4% CPU)</sub> | 🥇 **2,826**<br><sub>(141 MiB / 23.5% CPU)</sub> | **2,367**<br><sub>(181 MiB / 47.3% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | **8**<br><sub>(121 MiB / 0.3% CPU)</sub> | *Not possible* | **1,048**<br><sub>(87 MiB / 24.5% CPU)</sub> | 🥇 **2,023**<br><sub>(147 MiB / 23.1% CPU)</sub> | **1,880**<br><sub>(175 MiB / 46.6% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | 🥇 **1,834**<br><sub>(240 MiB / 49.0% CPU)</sub> | *Not possible* | **1,118**<br><sub>(95 MiB / 31.6% CPU)</sub> | **0**<br><sub>(126 MiB / 0.1% CPU)</sub> | **1,761**<br><sub>(255 MiB / 47.1% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **2,088**<br><sub>(238 MiB / 44.9% CPU)</sub> | **367**<br><sub>(120 MiB / 24.9% CPU)</sub> | **1,492**<br><sub>(95 MiB / 32.4% CPU)</sub> | **0**<br><sub>(126 MiB / 0.1% CPU)</sub> | **1,944**<br><sub>(257 MiB / 47.9% CPU)</sub> |

Linux nginx H1/H2/H3 POST completed (nginx.org mainline). TWP÷YARP H1 ≈ **1.5×**; H2→H1 ≈ **1.2×**; H3 ≈ **1.1×**. TWP÷nginx H3 ≈ **6×**.

### Windows — lossy / high-RTT (H2 HOL / H3 loss)

Userspace **5 ms** one-way delay + **1%** TCP connection stall (H1/H2) or UDP datagram drop (H3); **64 KiB** GET. Median of **3** repeats on `windows-latest` @ `8bfa7852` — [35092841035](https://github.com/justcoding121/titanium-web-proxy/actions/runs/35092841035) (`compare-lossy`).

*Not possible:* **HAProxy** and **Envoy** columns are omitted (no official Windows port).

| Client | Origin | TWP | nginx | YARP |
|---|---|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | **663**<br><sub>(112 MiB / 4.2% CPU)</sub> | **653**<br><sub>(142 MiB / 16.7% CPU)</sub> | 🥇 **679**<br><sub>(123 MiB / 3.9% CPU)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | **0**<br><sub>(peak 89 · 121 MiB / 1.4% CPU)</sub> | **0**<br><sub>(peak 17 · 142 MiB / 0.5% CPU)</sub> | **0**<br><sub>(peak 16 · 100 MiB / 0.7% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | **0**<br><sub>(67 MiB / 0.0% CPU)</sub> | *Not possible (no QUIC)* | **0**<br><sub>(79 MiB / 0.0% CPU)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | **0**<br><sub>(peak 88 · 131 MiB / 0.9% CPU)</sub> | **0**<br><sub>(peak 17 · 128 MiB / 0.1% CPU)</sub> | **0**<br><sub>(peak 16 · 91 MiB / 0.7% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | **0**<br><sub>(peak 8 · 67 MiB / 0.5% CPU)</sub> | *Not possible* | **0**<br><sub>(peak 16 · 101 MiB / 0.9% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | **0**<br><sub>(peak 9 · 72 MiB / 0.3% CPU)</sub> | *Not possible* | **0**<br><sub>(peak 15 · 100 MiB / 0.6% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | **0**<br><sub>(71 MiB / 0.0% CPU)</sub> | *Not possible* | **0**<br><sub>(80 MiB / 0.0% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | **0**<br><sub>(69 MiB / 0.0% CPU)</sub> | *Not possible (no QUIC)* | **0**<br><sub>(79 MiB / 0.0% CPU)</sub> |

H1 is near parity with YARP (~**0.98×**). H2 and H3 sustain are **0** on this Windows GHA pass (lossy HOL / QUIC); Linux table below is the publishable H2 HOL / H3 comparison. Laptop re-measure kept for Windows H3.

### Linux — lossy / high-RTT (H2 HOL / H3 loss)

Median of **3** repeats @ `8bfa7852`. Source: [35092841035](https://github.com/justcoding121/titanium-web-proxy/actions/runs/35092841035) (`compare-lossy`; lossy H3 uses `quic-http3`).

| Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---:|---:|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | **1,214**<br><sub>(148 MiB / 10.2% CPU)</sub> | 🥇 **1,219**<br><sub>(102 MiB / 8.2% CPU)</sub> | **1,218**<br><sub>(83 MiB / 4.8% CPU)</sub> | **1,209**<br><sub>(128 MiB / 6.6% CPU)</sub> | **1,211**<br><sub>(146 MiB / 13.1% CPU)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **312**<br><sub>(186 MiB / 5.2% CPU)</sub> | **40**<br><sub>(101 MiB / 0.2% CPU)</sub> | **40**<br><sub>(84 MiB / 0.2% CPU)</sub> | **40**<br><sub>(137 MiB / 0.3% CPU)</sub> | **40**<br><sub>(129 MiB / 1.1% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | **342**<br><sub>(158 MiB / 10.4% CPU)</sub> | **100**<br><sub>(112 MiB / 1.8% CPU)</sub> | **60**<br><sub>(86 MiB / 2.7% CPU)</sub> | 🥇 **1,578**<br><sub>(140 MiB / 20.3% CPU)</sub> | **321**<br><sub>(178 MiB / 15.2% CPU)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | 🥇 **337**<br><sub>(177 MiB / 5.2% CPU)</sub> | **40**<br><sub>(78 MiB / 0.1% CPU)</sub> | **41**<br><sub>(67 MiB / 0.1% CPU)</sub> | **40**<br><sub>(127 MiB / 0.3% CPU)</sub> | **41**<br><sub>(123 MiB / 1.0% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | **0**<br><sub>(peak 16 · 97 MiB / 0.5% CPU)</sub> | *Not possible* | 🥇 **42**<br><sub>(90 MiB / 0.3% CPU)</sub> | **40**<br><sub>(136 MiB / 0.2% CPU)</sub> | **41**<br><sub>(133 MiB / 1.0% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | **0**<br><sub>(peak 13 · 98 MiB / 0.5% CPU)</sub> | *Not possible* | 🥇 **41**<br><sub>(84 MiB / 0.4% CPU)</sub> | **40**<br><sub>(138 MiB / 0.4% CPU)</sub> | **41**<br><sub>(131 MiB / 1.2% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | **325**<br><sub>(143 MiB / 19.3% CPU)</sub> | *Not possible* | **66**<br><sub>(91 MiB / 3.2% CPU)</sub> | 🥇 **1,459**<br><sub>(145 MiB / 23.4% CPU)</sub> | **349**<br><sub>(190 MiB / 20.2% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | **325**<br><sub>(165 MiB / 10.9% CPU)</sub> | **99**<br><sub>(118 MiB / 1.9% CPU)</sub> | **63**<br><sub>(90 MiB / 3.1% CPU)</sub> | 🥇 **1,456**<br><sub>(141 MiB / 22.1% CPU)</sub> | **350**<br><sub>(185 MiB / 18.6% CPU)</sub> |

TWP H2 HOL ≫ YARP (~**7.8×**). H3 TWP÷YARP ≈ **1.07×**.

### Architecture-sensitive

These runs isolate slow app readers, origin-early response, HTTP/2 duplex, and WebSocket echo. See [TWP vs YARP IO model](Performance-Profiling#twp-vs-yarp-io-model). Laptop 1-rep numbers are on [Performance Local Lab](Performance-Local-Lab#architecture-sensitive).

Median of **3** repeats on matched 4 vCPU / 16 GiB runners @ `8bfa7852` ([35092846209](https://github.com/justcoding121/titanium-web-proxy/actions/runs/35092846209)). Slow consumer = 256 KiB GET, 16 KiB read + 8 ms sleep. Early response = 64 KiB POST, origin writes after 8 KiB. Duplex HTTP/2 = overlapping 64 KiB POST on H2 TLS↔H2 TLS. WebSocket row = H1 Upgrade echo round-trips/sec (not RFC 8441; see WebSocket RFC 8441 table below).

Lossy-link runs (slow **network**) are already published above; they are not a slow **app** reader.

#### Windows

*Not possible:* **HAProxy** and **Envoy** columns are omitted (no official Windows port).

| Scenario | Client | Origin | TWP | nginx | YARP |
|---|---|---|---:|---:|---:|
| Slow consumer (256 KiB GET, throttled client read) | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **248**<br><sub>(100 MiB / 4.9% CPU)</sub> | **192**<br><sub>(143 MiB / 24.7% CPU)</sub> | **240**<br><sub>(111 MiB / 4.6% CPU)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · TLS | HTTP/1 · plain | **248**<br><sub>(121 MiB / 4.6% CPU)</sub> | **180**<br><sub>(142 MiB / 24.7% CPU)</sub> | 🥇 **253**<br><sub>(112 MiB / 6.7% CPU)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/3 · QUIC | HTTP/1 · plain | **273**<br><sub>(102 MiB / 15.2% CPU)</sub> | *Not possible (no QUIC)* | 🥇 **274**<br><sub>(162 MiB / 15.7% CPU)</sub> |
| Early response (origin writes after first request chunk) | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **11,064**<br><sub>(101 MiB / 41.4% CPU)</sub> | **679**<br><sub>(143 MiB / 24.9% CPU)</sub> | **7,558**<br><sub>(138 MiB / 53.7% CPU)</sub> |
| Early response (origin writes after first request chunk) | HTTP/2 · TLS | HTTP/1 · plain | **2,870**<br><sub>(181 MiB / 40.4% CPU)</sub> | **0**<br><sub>(peak 343 · 144 MiB / 24.9% CPU)</sub> | 🥇 **3,336**<br><sub>(135 MiB / 51.9% CPU)</sub> |
| Early response (origin writes after first request chunk) | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **2,141**<br><sub>(149 MiB / 41.8% CPU)</sub> | *Not possible (no QUIC)* | **1,888**<br><sub>(199 MiB / 52.7% CPU)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · plain | HTTP/1 · plain | **248**<br><sub>(106 MiB / 3.9% CPU)</sub> | **248**<br><sub>(127 MiB / 8.7% CPU)</sub> | 🥇 **256**<br><sub>(104 MiB / 4.3% CPU)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · TLS | HTTP/2 · TLS | **0**<br><sub>(peak 8 · 72 MiB / 0.3% CPU)</sub> | *Not possible* | 🥇 **256**<br><sub>(141 MiB / 4.9% CPU)</sub> |
| Early response (origin writes after first request chunk) | HTTP/2 · TLS | HTTP/2 · TLS | **0**<br><sub>(97 MiB / 0.1% CPU)</sub> | *Not possible* | **0**<br><sub>(123 MiB / 0.2% CPU)</sub> |
| Duplex (both directions live) | HTTP/2 · TLS | HTTP/2 · TLS | **0**<br><sub>(95 MiB / 0.1% CPU)</sub> | *Not possible* | **0**<br><sub>(114 MiB / 0.2% CPU)</sub> |
| Duplex (WebSocket / H1 Upgrade) | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **66,034**<br><sub>(97 MiB / 41.3% CPU)</sub> | **36,248**<br><sub>(143 MiB / 24.6% CPU)</sub> | **61,591**<br><sub>(89 MiB / 44.1% CPU)</sub> |

#### Linux

| Scenario | Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---|---:|---:|---:|---:|---:|
| Slow consumer (256 KiB GET, throttled client read) | HTTP/1 · TLS | HTTP/1 · plain | **468**<br><sub>(125 MiB / 9.2% CPU)</sub> | **416**<br><sub>(101 MiB / 9.4% CPU)</sub> | 🥇 **472**<br><sub>(84 MiB / 5.8% CPU)</sub> | **469**<br><sub>(139 MiB / 6.2% CPU)</sub> | **419**<br><sub>(145 MiB / 14.0% CPU)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · TLS | HTTP/1 · plain | **473**<br><sub>(157 MiB / 12.2% CPU)</sub> | **427**<br><sub>(102 MiB / 9.4% CPU)</sub> | **474**<br><sub>(84 MiB / 5.4% CPU)</sub> | 🥇 **480**<br><sub>(157 MiB / 5.0% CPU)</sub> | **474**<br><sub>(148 MiB / 16.2% CPU)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **480**<br><sub>(135 MiB / 31.0% CPU)</sub> | **0**<br><sub>(peak 347 · 124 MiB / 18.5% CPU)</sub> | **479**<br><sub>(90 MiB / 7.8% CPU)</sub> | **1**<br><sub>(156 MiB / 0.2% CPU)</sub> | **476**<br><sub>(196 MiB / 37.0% CPU)</sub> |
| Early response (origin writes after first request chunk) | HTTP/1 · TLS | HTTP/1 · plain | **6,746**<br><sub>(148 MiB / 44.2% CPU)</sub> | **5,410**<br><sub>(101 MiB / 48.0% CPU)</sub> | 🥇 **7,255**<br><sub>(84 MiB / 39.9% CPU)</sub> | **0**<br><sub>(peak 673 · 130 MiB / 6.6% CPU)</sub> | **4,645**<br><sub>(176 MiB / 55.1% CPU)</sub> |
| Early response (origin writes after first request chunk) | HTTP/2 · TLS | HTTP/1 · plain | **2,465**<br><sub>(210 MiB / 48.0% CPU)</sub> | **0**<br><sub>(peak 858 · 112 MiB / 15.2% CPU)</sub> | 🥇 **2,509**<br><sub>(85 MiB / 24.7% CPU)</sub> | **0**<br><sub>(peak 2,574 · 145 MiB / 23.1% CPU)</sub> | **2,226**<br><sub>(171 MiB / 48.6% CPU)</sub> |
| Early response (origin writes after first request chunk) | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **4,647**<br><sub>(224 MiB / 46.4% CPU)</sub> | **0**<br><sub>(peak 706 · 122 MiB / 24.5% CPU)</sub> | **2,866**<br><sub>(93 MiB / 32.9% CPU)</sub> | **0**<br><sub>(126 MiB / 0.1% CPU)</sub> | **3,107**<br><sub>(258 MiB / 47.4% CPU)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · plain | HTTP/1 · plain | **475**<br><sub>(139 MiB / 14.4% CPU)</sub> | **463**<br><sub>(79 MiB / 11.5% CPU)</sub> | 🥇 **476**<br><sub>(68 MiB / 6.2% CPU)</sub> | **472**<br><sub>(150 MiB / 4.5% CPU)</sub> | **474**<br><sub>(149 MiB / 14.7% CPU)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · TLS | HTTP/2 · TLS | **9**<br><sub>(96 MiB / 1.8% CPU)</sub> | *Not possible* | **447**<br><sub>(90 MiB / 12.9% CPU)</sub> | **470**<br><sub>(157 MiB / 9.3% CPU)</sub> | 🥇 **470**<br><sub>(172 MiB / 21.4% CPU)</sub> |
| Early response (origin writes after first request chunk) | HTTP/2 · TLS | HTTP/2 · TLS | **0**<br><sub>(117 MiB / 0.1% CPU)</sub> | *Not possible* | 🥇 **8**<br><sub>(95 MiB / 0.2% CPU)</sub> | **0**<br><sub>(peak 2,844 · 152 MiB / 19.7% CPU)</sub> | **0**<br><sub>(141 MiB / 0.1% CPU)</sub> |
| Duplex (both directions live) | HTTP/2 · TLS | HTTP/2 · TLS | **0**<br><sub>(118 MiB / 0.1% CPU)</sub> | *Not possible* | **0**<br><sub>(93 MiB / 0.1% CPU)</sub> | **0**<br><sub>(peak 2,526 · 156 MiB / 17.2% CPU)</sub> | **0**<br><sub>(146 MiB / 0.2% CPU)</sub> |
| Duplex (WebSocket / H1 Upgrade) | HTTP/1 · TLS | HTTP/1 · plain | **45,832**<br><sub>(128 MiB / 44.4% CPU)</sub> | 🥇 **49,425**<br><sub>(102 MiB / 36.9% CPU)</sub> | **48,657**<br><sub>(82 MiB / 39.8% CPU)</sub> | **46,062**<br><sub>(127 MiB / 41.2% CPU)</sub> | **41,388**<br><sub>(126 MiB / 45.3% CPU)</sub> |

Slow consumer is sleep-bound; H1/H2/H3 sit in the same band. Early-response H1: TWP leads (~**1.46×** / **1.45×** YARP Win/Linux). **Duplex H2** sustain is **0** for TWP and YARP on this GHA pass (same concurrent-copier cell; see [IO model](Performance-Profiling#twp-vs-yarp-io-model) and laptop numbers). WebSocket: TWP÷YARP Windows ≈ **1.07×**; Linux nginx leads.

### TLS termination cost (H1 TLS → cleartext origin)

Isolates keep-alive tiny GET vs **new connection per request** (handshake-dominated) vs keep-alive **256 KiB**. Product comparison uses RPS and end-to-end latency; TWP can also capture `ClientTlsTiming` when `TWP_RPS_CAPTURE_TLS=1` (child process) — nginx/YARP have no equivalent hook.

#### Windows

Median of **3** repeats on `windows-latest` @ `8bfa7852`. Source: Actions [35092843724](https://github.com/justcoding121/titanium-web-proxy/actions/runs/35092843724). Absolute RPS on GHA swings hard; prefer **TWP÷YARP**.

*Not possible:* **HAProxy** and **Envoy** columns are omitted (no official Windows port).

| Workload | TWP | nginx | YARP |
|---|---:|---:|---:|
| Keep-alive · tiny GET | 🥇 **21,760**<br><sub>(85 MiB / 49.0% CPU)</sub> | **9,611**<br><sub>(142 MiB / 24.8% CPU)</sub> | **19,339**<br><sub>(101 MiB / 48.2% CPU)</sub> |
| New-connection · tiny GET | 🥇 **747**<br><sub>(89 MiB / 9.4% CPU)</sub> | **0**<br><sub>(peak 247 · 140 MiB / 24.2% CPU)</sub> | **732**<br><sub>(113 MiB / 9.5% CPU)</sub> |
| Keep-alive · 256 KiB GET | 🥇 **2,948**<br><sub>(133 MiB / 45.9% CPU)</sub> | **0**<br><sub>(peak 158 · 143 MiB / 24.5% CPU)</sub> | **2,718**<br><sub>(128 MiB / 45.9% CPU)</sub> |

#### Linux

Median of **3** repeats @ `8bfa7852`. Source: Actions [35092843724](https://github.com/justcoding121/titanium-web-proxy/actions/runs/35092843724).

| Workload | TWP | nginx | HAProxy | Envoy | YARP |
|---|---:|---:|---:|---:|---:|
| Keep-alive · tiny GET | **23,990**<br><sub>(112 MiB / 50.2% CPU)</sub> | **28,472**<br><sub>(100 MiB / 40.9% CPU)</sub> | 🥇 **28,484**<br><sub>(83 MiB / 43.2% CPU)</sub> | **17,372**<br><sub>(127 MiB / 58.6% CPU)</sub> | **20,581**<br><sub>(136 MiB / 50.5% CPU)</sub> |
| New-connection · tiny GET | **976**<br><sub>(116 MiB / 47.4% CPU)</sub> | **1,017**<br><sub>(100 MiB / 44.5% CPU)</sub> | **956**<br><sub>(82 MiB / 44.1% CPU)</sub> | 🥇 **1,072**<br><sub>(129 MiB / 41.4% CPU)</sub> | **967**<br><sub>(152 MiB / 46.4% CPU)</sub> |
| Keep-alive · 256 KiB GET | **2,708**<br><sub>(128 MiB / 36.8% CPU)</sub> | **1,740**<br><sub>(100 MiB / 53.7% CPU)</sub> | 🥇 **2,966**<br><sub>(84 MiB / 33.2% CPU)</sub> | **2,734**<br><sub>(146 MiB / 34.2% CPU)</sub> | **2,145**<br><sub>(172 MiB / 45.3% CPU)</sub> |

All three workloads are **>1.00×** YARP on both OS. On Linux, HAProxy leads keep-alive tiny (near-tie with nginx) and Envoy leads new-connection; TWP stays ahead of YARP on all three.

## Unary gRPC (H2 TLS)

Unary Echo **RPC/s** @ c=64 over H2 TLS→H2 TLS for Titanium, YARP, nginx (`grpc_pass`), HAProxy, and Envoy (OS-possible peers only). Not folded into the architecture-sensitive tables. Median of **3** repeats @ `8bfa7852` — [35092856793](https://github.com/justcoding121/titanium-web-proxy/actions/runs/35092856793).

| OS | Titanium | YARP | nginx | HAProxy | Envoy |
|---|---:|---:|---:|---:|---:|
| Windows | **111,791**<br><sub>(116 MiB / 22.8% CPU)</sub> | **63,757**<br><sub>(120 MiB / 41.9% CPU)</sub> | **0**<br><sub>(143 MiB / 24.9% CPU)</sub> | *Not possible* | *Not possible* |
| Linux | **78,834**<br><sub>(121 MiB / 32.5% CPU)</sub> | **45,952**<br><sub>(149 MiB / 42.2% CPU)</sub> | **0**<br><sub>(131 MiB / 25.1% CPU)</sub> | **18,339**<br><sub>(83 MiB / 24.2% CPU)</sub> | **30,733**<br><sub>(127 MiB / 21.4% CPU)</sub> |
| macOS | **18,711**<br><sub>(97 MiB / 24.0% CPU)</sub> | **13,574**<br><sub>(137 MiB / 34.5% CPU)</sub> | **0**<br><sub>(61 MiB / 3.1% CPU)</sub> | **0**<br><sub>(63 MiB / 18.4% CPU)</sub> | **7,199**<br><sub>(80 MiB / 20.7% CPU)</sub> |

## Unary gRPC (H2 TLS → h2c)

Unary Echo **RPC/s** @ c=64 over **H2 TLS → h2c** (edge TLS, cleartext H2 origin). Peers: Titanium, YARP, HAProxy, Envoy. Mode: `compare-grpc` (`*-grpc-h2c`). Median of **3** repeats @ `8bfa7852` — [35092859116](https://github.com/justcoding121/titanium-web-proxy/actions/runs/35092859116).

*Not possible:* **nginx** column omitted (no H2 upstream).

| OS | Titanium | YARP | HAProxy | Envoy |
|---|---:|---:|---:|---:|
| Windows | **60,626**<br><sub>(93 MiB / 24.4% CPU)</sub> | **34,219**<br><sub>(117 MiB / 47.2% CPU)</sub> | *Not possible* | *Not possible* |
| Linux | **43,638**<br><sub>(126 MiB / 28.5% CPU)</sub> | **24,134**<br><sub>(169 MiB / 43.3% CPU)</sub> | **7,783**<br><sub>(82 MiB / 24.4% CPU)</sub> | **12,047**<br><sub>(128 MiB / 22.2% CPU)</sub> |
| macOS | **24,524**<br><sub>(95 MiB / 24.8% CPU)</sub> | **16,722**<br><sub>(130 MiB / 35.8% CPU)</sub> | **0**<br><sub>(65 MiB / 17.4% CPU)</sub> | **0**<br><sub>(79 MiB / 20.6% CPU)</sub> |

## WebSocket (H1 TLS → H1 TLS)

WebSocket echo round-trips/sec over **dual-TLS** H1 (`proxy_ssl` style). Mode: `compare-ws-h1tls` (`*-duplex-ws-h1tls`).

| OS | Titanium | YARP | nginx | HAProxy | Envoy |
|---|---:|---:|---:|---:|---:|
| Windows | **66,023**<br><sub>(102 MiB / 42.5% CPU)</sub> | **63,442**<br><sub>(90 MiB / 46.5% CPU)</sub> | **31,084**<br><sub>(143 MiB / 24.7% CPU)</sub> | *Not possible* | *Not possible* |
| Linux | **38,814**<br><sub>(137 MiB / 44.3% CPU)</sub> | **34,209**<br><sub>(136 MiB / 45.8% CPU)</sub> | **41,105**<br><sub>(108 MiB / 38.2% CPU)</sub> | **42,059**<br><sub>(85 MiB / 39.8% CPU)</sub> | **40,348**<br><sub>(127 MiB / 40.1% CPU)</sub> |
| macOS | **10,520**<br><sub>(144 MiB / 33.3% CPU)</sub> | **8,988**<br><sub>(145 MiB / 33.6% CPU)</sub> | **10,184**<br><sub>(81 MiB / 26.4% CPU)</sub> | **10,985**<br><sub>(67 MiB / 32.0% CPU)</sub> | **15,732**<br><sub>(81 MiB / 30.8% CPU)</sub> |

## WebSocket (H2 TLS 8441 → H1)

WebSocket echo over **RFC 8441** extended CONNECT (H2 TLS client → H1 plain origin). Mode: `compare-ws-h2` (`*-duplex-ws-h2`).

*Not possible:* **nginx** column omitted (no RFC 8441 extended CONNECT reverse).

| OS | Titanium | YARP | HAProxy | Envoy |
|---|---:|---:|---:|---:|
| Windows | **23,244**<br><sub>(172 MiB / 41.7% CPU)</sub> | **0**<br><sub>(165 MiB / 19.8% CPU)</sub> | *Not possible* | *Not possible* |
| Linux | **1,530**<br><sub>(143 MiB / 16.9% CPU)</sub> | **0**<br><sub>(172 MiB / 25.2% CPU)</sub> | **1,535**<br><sub>(82 MiB / 5.4% CPU)</sub> | **0**<br><sub>(123 MiB / 0.3% CPU)</sub> |
| macOS | **4,262**<br><sub>(219 MiB / 29.2% CPU)</sub> | **0**<br><sub>(86 MiB / 12.1% CPU)</sub> | **4,087**<br><sub>(66 MiB / 35.2% CPU)</sub> | **0**<br><sub>(73 MiB / 0.6% CPU)</sub> |

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
