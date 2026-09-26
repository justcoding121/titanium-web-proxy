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

Calibration for the shared 4 vCPU loopback shape: how close client + origin are to saturated before ranking reverse peers. Tiny keep-alive GET. Median of **3** repeats @ `e781b009` — [36251508629](https://github.com/justcoding121/titanium-web-proxy/actions/runs/36251508629). Warmup 2s / measure 8s; concurrency 8, 16, 32, 64. Block A **% of origin-HttpClient** uses median **peak** RPS. Blocks B/C use peer÷YARP / ÷nginx on median peak (not % of H1 origin). **RPS cells** embed median RSS / CPU for the **proxy child** plus its **full descendant tree** (serve-proxy → nginx master → workers); origin-direct samples the **origin** child. Product matrices below use matched `dotnet-httpclient` only (not bombardier).


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
| origin-direct | dotnet-httpclient | **50,354**<br><sub>(55 MiB / 40.7% CPU)</sub> | **100.0%** |
| origin-direct-bombardier | bombardier | **38,994**<br><sub>(56 MiB / 22.9% CPU)</sub> | **77.4%** |
| bare-reverse-http1 | dotnet-httpclient | **25,417**<br><sub>(59 MiB / 42.3% CPU)</sub> | **50.5%** |
| nginx-reverse-http1 | dotnet-httpclient | **13,199**<br><sub>(125 MiB / 24.9% CPU)</sub> | **26.2%** |
| yarp-reverse-http1 | dotnet-httpclient | **21,066**<br><sub>(87 MiB / 50.2% CPU)</sub> | **41.8%** |
| twp-reverse-http1 | dotnet-httpclient | 🥇 **25,595**<br><sub>(74 MiB / 47.2% CPU)</sub> | **50.8%** |

**Linux** (`ubuntu-latest`)

| Arm | Generator | RPS | % of origin-HttpClient |
|---|---|---:|---:|
| origin-direct | dotnet-httpclient | **69,844**<br><sub>(80 MiB / 41.8% CPU)</sub> | **100.0%** |
| origin-direct-bombardier | bombardier | **41,778**<br><sub>(80 MiB / 35.1% CPU)</sub> | **59.8%** |
| bare-reverse-http1 | dotnet-httpclient | **32,654**<br><sub>(70 MiB / 45.0% CPU)</sub> | **46.8%** |
| nginx-reverse-http1 | dotnet-httpclient | 🥇 **38,455**<br><sub>(75 MiB / 40.9% CPU)</sub> | **55.1%** |
| yarp-reverse-http1 | dotnet-httpclient | **28,138**<br><sub>(115 MiB / 50.5% CPU)</sub> | **40.3%** |
| twp-reverse-http1 | dotnet-httpclient | **32,670**<br><sub>(86 MiB / 49.0% CPU)</sub> | **46.8%** |

Reverse peers are about **50–48%** of the origin-direct HttpClient peak on this runner class (Win TWP **50.1%**, Lin TWP **48.3%**). Prefer the **%** column over absolute RPS across runs. Bare and origin-direct are controls (not medal peers).

#### Block B — H2 TLS→H1

Peer ratios (÷YARP / ÷nginx) on median peak; **RPS cells** embed `(MiB / CPU%)`.

**Windows** (`windows-latest`)

| Arm | Generator | RPS | ÷YARP | ÷nginx |
|---|---|---:|---:|---:|
| nginx-reverse-http2 | dotnet-httpclient | **7,968**<br><sub>(141 MiB / 24.4% CPU)</sub> | **0.27×** | **1.00×** |
| yarp-reverse-http2 | dotnet-httpclient | **29,138**<br><sub>(98 MiB / 54.5% CPU)</sub> | **1.00×** | **3.66×** |
| twp-reverse-http2-cleartext | dotnet-httpclient | 🥇 **34,935**<br><sub>(96 MiB / 50.9% CPU)</sub> | **1.20×** | **4.38×** |

**Linux** (`ubuntu-latest`)

| Arm | Generator | RPS | ÷YARP | ÷nginx |
|---|---|---:|---:|---:|
| nginx-reverse-http2 | dotnet-httpclient | **15,007**<br><sub>(100 MiB / 19.2% CPU)</sub> | **0.51×** | **1.00×** |
| yarp-reverse-http2 | dotnet-httpclient | **29,402**<br><sub>(121 MiB / 50.1% CPU)</sub> | **1.00×** | **1.96×** |
| twp-reverse-http2-cleartext | dotnet-httpclient | 🥇 **35,036**<br><sub>(116 MiB / 51.8% CPU)</sub> | **1.19×** | **2.33×** |

#### Block C — H3→H1

Same layout as Block B. Requires QuicListener. nginx needs `http_v3_module` (Windows nginx has no QUIC). HAProxy needs `USE_QUIC` (GHA Linux/macOS require it). Envoy 1.20+ includes HTTP/3.

**Windows** (`windows-latest`)

| Arm | Generator | RPS | ÷YARP | ÷nginx |
|---|---|---:|---:|---:|
| nginx-reverse-http3-cleartext | dotnet-httpclient | *Not possible (no QUIC)* | — | — |
| yarp-reverse-http3-cleartext | dotnet-httpclient | **14,409**<br><sub>(143 MiB / 49.6% CPU)</sub> | **1.00×** | — |
| twp-reverse-http3-cleartext | dotnet-httpclient | 🥇 **15,002**<br><sub>(104 MiB / 43.9% CPU)</sub> | **1.04×** | — |

**Linux** (`ubuntu-latest`)

| Arm | Generator | RPS | ÷YARP | ÷nginx |
|---|---|---:|---:|---:|
| nginx-reverse-http3-cleartext | dotnet-httpclient | **0**<br><sub>(peak 15,190 · 106 MiB / 22.3% CPU)</sub> | **0.80×** | **1.00×** |
| yarp-reverse-http3-cleartext | dotnet-httpclient | **18,996**<br><sub>(181 MiB / 51.0% CPU)</sub> | **1.00×** | **1.25×** |
| twp-reverse-http3-cleartext | dotnet-httpclient | 🥇 **21,142**<br><sub>(146 MiB / 49.8% CPU)</sub> | **1.11×** | **1.39×** |

## Windows — Titanium vs nginx vs HAProxy vs Envoy vs YARP

Client / origin: HTTP version and whether TLS is used (`plain` = cleartext, `TLS` = encrypted, `QUIC` = HTTP/3).

### Reverse

Median of **3 repeats** on `windows-latest` (4 vCPU / 16 GiB). Bare reverse 5×5 @ `e781b009` — `compare-product` [36251478808](https://github.com/justcoding121/titanium-web-proxy/actions/runs/36251478808). Warmup 2s / measure 8s; concurrency 8, 16, 32, 64. Prefer TWP÷peer ratios over absolute RPS. **RPS cells** include median RSS / CPU at the peak-RPS step as `<br><sub>(MiB / CPU%)</sub>`. nginx terminate peers use `keepalive 256` + streaming buffers. **HAProxy / Envoy are Linux-only peers** (no official Windows port). Laptop High-perf / cool-paired numbers stay on the [local lab](Performance-Local-Lab). Product 5×5 is **~56-byte JSON keep-alive GET**; H2/H3 same-protocol cells are mostly header work with a tiny body (Titanium best case) — see [Why this comparison is fair](#why-this-comparison-is-fair).

**Load generators:** Reverse inbound H3 arms use **`dotnet-httpclient`** (`http_version=3.0`, `RequestVersionExact`). nginx/Windows is same-OS only (no QUIC). HAProxy/Envoy are Linux-only terminate peers.

| Client | Origin | TWP | nginx | YARP |
|---|---|---:|---:|---:|
| HTTP/1 · plain | HTTP/1 · plain | 🥇 **40,699**<br><sub>(75 MiB / 48.2% CPU)</sub> | **30,653**<br><sub>(126 MiB / 24.8% CPU)</sub> | **36,953**<br><sub>(85 MiB / 49.2% CPU)</sub> |
| HTTP/1 · plain | HTTP/1 · TLS | 🥇 **62,536**<br><sub>(84 MiB / 49.5% CPU)</sub> | **25,965**<br><sub>(136 MiB / 24.8% CPU)</sub> | **57,902**<br><sub>(98 MiB / 49.9% CPU)</sub> |
| HTTP/1 · plain | HTTP/2 · plain | 🥇 **46,462**<br><sub>(113 MiB / 47.4% CPU)</sub> | *Not possible (no H2 upstream)* | **40,305**<br><sub>(90 MiB / 49.4% CPU)</sub> |
| HTTP/1 · plain | HTTP/2 · TLS | 🥇 **50,004**<br><sub>(136 MiB / 45.8% CPU)</sub> | *Not possible (no H2 upstream)* | **47,443**<br><sub>(97 MiB / 48.7% CPU)</sub> |
| HTTP/1 · plain | HTTP/3 · QUIC | 🥇 **51,874**<br><sub>(136 MiB / 53.8% CPU)</sub> | *Not possible (no H3 upstream)* | **0**<br><sub>(peak 52,963 · 151 MiB / 50.4% CPU)</sub> |
| HTTP/1 · TLS | HTTP/1 · plain | 🥇 **26,575**<br><sub>(90 MiB / 47.6% CPU)</sub> | **12,693**<br><sub>(142 MiB / 24.7% CPU)</sub> | **22,938**<br><sub>(102 MiB / 49.6% CPU)</sub> |
| HTTP/1 · TLS | HTTP/1 · TLS | 🥇 **33,225**<br><sub>(82 MiB / 45.4% CPU)</sub> | **15,171**<br><sub>(144 MiB / 24.8% CPU)</sub> | **29,712**<br><sub>(104 MiB / 49.1% CPU)</sub> |
| HTTP/1 · TLS | HTTP/2 · plain | 🥇 **82,768**<br><sub>(128 MiB / 46.6% CPU)</sub> | *Not possible (no H2 upstream)* | **76,557**<br><sub>(111 MiB / 47% CPU)</sub> |
| HTTP/1 · TLS | HTTP/2 · TLS | 🥇 **32,332**<br><sub>(117 MiB / 43.8% CPU)</sub> | *Not possible (no H2 upstream)* | **30,560**<br><sub>(107 MiB / 48.4% CPU)</sub> |
| HTTP/1 · TLS | HTTP/3 · QUIC | 🥇 **23,029**<br><sub>(111 MiB / 49.2% CPU)</sub> | *Not possible (no H3 upstream)* | **0**<br><sub>(peak 22,422 · 128 MiB / 51.8% CPU)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | 🥇 **92,546**<br><sub>(99 MiB / 48.1% CPU)</sub> | **24,770**<br><sub>(127 MiB / 24.8% CPU)</sub> | **90,282**<br><sub>(90 MiB / 44.9% CPU)</sub> |
| HTTP/2 · plain | HTTP/1 · TLS | 🥇 **36,633**<br><sub>(107 MiB / 50% CPU)</sub> | **8,641**<br><sub>(138 MiB / 24.8% CPU)</sub> | **33,020**<br><sub>(98 MiB / 49.4% CPU)</sub> |
| HTTP/2 · plain | HTTP/2 · plain | 🥇 **130,879**<br><sub>(60 MiB / 31.8% CPU)</sub> | *Not possible (no H2 upstream)* | **74,692**<br><sub>(90 MiB / 48.6% CPU)</sub> |
| HTTP/2 · plain | HTTP/2 · TLS | 🥇 **198,427**<br><sub>(63 MiB / 29.3% CPU)</sub> | *Not possible (no H2 upstream)* | **126,272**<br><sub>(97 MiB / 48.4% CPU)</sub> |
| HTTP/2 · plain | HTTP/3 · QUIC | 🥇 **34,160**<br><sub>(133 MiB / 52.4% CPU)</sub> | *Not possible (no H3 upstream)* | **0**<br><sub>(peak 31,622 · 126 MiB / 50.9% CPU)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **50,088**<br><sub>(104 MiB / 48.1% CPU)</sub> | **19,610**<br><sub>(142 MiB / 24.1% CPU)</sub> | **45,998**<br><sub>(96 MiB / 49.2% CPU)</sub> |
| HTTP/2 · TLS | HTTP/1 · TLS | 🥇 **80,429**<br><sub>(110 MiB / 47.3% CPU)</sub> | **18,096**<br><sub>(144 MiB / 24.4% CPU)</sub> | **74,193**<br><sub>(96 MiB / 49.9% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | 🥇 **109,252**<br><sub>(76 MiB / 27.5% CPU)</sub> | *Not possible (no H2 upstream)* | **59,361**<br><sub>(103 MiB / 53.6% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **108,782**<br><sub>(74 MiB / 29.9% CPU)</sub> | *Not possible (no H2 upstream)* | **62,970**<br><sub>(98 MiB / 50.3% CPU)</sub> |
| HTTP/2 · TLS | HTTP/3 · QUIC | 🥇 **73,369**<br><sub>(182 MiB / 52.3% CPU)</sub> | *Not possible (no H3 upstream)* | **0**<br><sub>(peak 67,255 · 159 MiB / 52% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **19,862**<br><sub>(111 MiB / 42.2% CPU)</sub> | *Not possible (no QUIC)* | **17,490**<br><sub>(142 MiB / 50.2% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **23,388**<br><sub>(123 MiB / 40.4% CPU)</sub> | *Not possible (no QUIC)* | **21,012**<br><sub>(152 MiB / 47.1% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/2 · plain | 🥇 **80,216**<br><sub>(205 MiB / 50.2% CPU)</sub> | *Not possible (no H2 upstream)* | **70,768**<br><sub>(198 MiB / 49.1% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | 🥇 **34,568**<br><sub>(135 MiB / 46.5% CPU)</sub> | *Not possible (no H2 upstream)* | **24,914**<br><sub>(157 MiB / 47.9% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/3 · QUIC | 🥇 **28,879**<br><sub>(134 MiB / 44% CPU)</sub> | *Not possible (no H3 upstream)* | **0**<br><sub>(peak 15,490 · 171 MiB / 50.6% CPU)</sub> |

### MITM (TWP only)

Same Client×Origin wires with interception on (`compare-product` [36251478808](https://github.com/justcoding121/titanium-web-proxy/actions/runs/36251478808)). **Lite** = no-op handlers (unchanged-lite finish). **Full** = append-only header mutation (harness: one probe header each way; product: generic append-only relay via `MitmCompressedRelayHelper`). nginx/HAProxy/Envoy/YARP cannot MITM. **Lite÷Reverse** / **Full÷Reverse** vs bare reverse (**same job / comparison-group shard**). Completion gate: Lite ≥ **0.50×** and Full ≥ **0.50×** reverse sustain @ c=64 (median of 3 GHA runs); reverse TWP÷YARP ≥ **0.75×** (no terminate-peer gate).

**v1 append-only relay (2026-08-27):** Pre-fix H2→H2 Full÷Reverse was **0.13–0.16×** ([32960766249](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32960766249)). Post-fix @ `df172718`: H2 plain→H2 plain Full **0.77–0.79×**, H3→H1 Full **0.91–0.93×**, all MITM arms ≥ **0.70×** on median of [33041445371](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33041445371), [33055267086](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33055267086), [33055272140](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33055272140).

**v2 drop-only + non-unique append (2026-08-27):** `MitmStaticRebuildHelper` rebuilds static HPACK/QPACK after 1–4 unique header drops; trailing non-unique appends stay on compressed relay. @ `e781b009`: all MITM arms ≥ **0.70×** on GHA median ([33087088466](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087088466), [33087091622](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087091622), [33105885748](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33105885748) Linux; [33087085235](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087085235), [33087088466](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087088466), [33087091622](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087091622) Windows). H2 plain→H2 plain Full **0.77–0.79×** (Win) / **0.78×** (Lin).

| Client | Origin | Lite sustain | Full sustain | Lite÷Reverse | Full÷Reverse |
|---|---|---:|---:|---:|---:|
| HTTP/1 · plain | HTTP/1 · plain | **40,695**<br><sub>(79 MiB / 49% CPU)</sub> | **39,758**<br><sub>(82 MiB / 46.3% CPU)</sub> | **1×** | **0.98×** |
| HTTP/1 · plain | HTTP/1 · TLS | **62,359**<br><sub>(94 MiB / 48.4% CPU)</sub> | **61,159**<br><sub>(93 MiB / 48.1% CPU)</sub> | **1×** | **0.98×** |
| HTTP/1 · plain | HTTP/2 · plain | **45,510**<br><sub>(113 MiB / 50.4% CPU)</sub> | **44,765**<br><sub>(114 MiB / 49.7% CPU)</sub> | **0.98×** | **0.96×** |
| HTTP/1 · plain | HTTP/2 · TLS | **48,819**<br><sub>(129 MiB / 47.9% CPU)</sub> | **47,554**<br><sub>(126 MiB / 50.2% CPU)</sub> | **0.98×** | **0.95×** |
| HTTP/1 · plain | HTTP/3 · QUIC | **52,760**<br><sub>(156 MiB / 53.5% CPU)</sub> | **51,331**<br><sub>(155 MiB / 55.4% CPU)</sub> | **1.02×** | **0.99×** |
| HTTP/1 · TLS | HTTP/1 · plain | **26,316**<br><sub>(93 MiB / 45.3% CPU)</sub> | **25,823**<br><sub>(93 MiB / 49.1% CPU)</sub> | **0.99×** | **0.97×** |
| HTTP/1 · TLS | HTTP/1 · TLS | **33,123**<br><sub>(94 MiB / 47.7% CPU)</sub> | **32,537**<br><sub>(95 MiB / 47.1% CPU)</sub> | **1×** | **0.98×** |
| HTTP/1 · TLS | HTTP/2 · plain | **81,036**<br><sub>(135 MiB / 50.1% CPU)</sub> | **78,392**<br><sub>(140 MiB / 51.8% CPU)</sub> | **0.98×** | **0.95×** |
| HTTP/1 · TLS | HTTP/2 · TLS | **31,795**<br><sub>(127 MiB / 45.1% CPU)</sub> | **31,636**<br><sub>(127 MiB / 46% CPU)</sub> | **0.98×** | **0.98×** |
| HTTP/1 · TLS | HTTP/3 · QUIC | **22,722**<br><sub>(123 MiB / 51.5% CPU)</sub> | **22,543**<br><sub>(115 MiB / 51.2% CPU)</sub> | **0.99×** | **0.98×** |
| HTTP/2 · plain | HTTP/1 · plain | **93,769**<br><sub>(102 MiB / 50.2% CPU)</sub> | **90,948**<br><sub>(105 MiB / 52.4% CPU)</sub> | **1.01×** | **0.98×** |
| HTTP/2 · plain | HTTP/1 · TLS | **36,190**<br><sub>(100 MiB / 52.4% CPU)</sub> | **35,584**<br><sub>(105 MiB / 52% CPU)</sub> | **0.99×** | **0.97×** |
| HTTP/2 · plain | HTTP/2 · plain | **107,157**<br><sub>(73 MiB / 40.8% CPU)</sub> | **102,737**<br><sub>(68 MiB / 41.7% CPU)</sub> | **0.82×** | **0.78×** |
| HTTP/2 · plain | HTTP/2 · TLS | **159,741**<br><sub>(75 MiB / 39.1% CPU)</sub> | **152,395**<br><sub>(71 MiB / 38.2% CPU)</sub> | **0.81×** | **0.77×** |
| HTTP/2 · plain | HTTP/3 · QUIC | **33,936**<br><sub>(135 MiB / 54.8% CPU)</sub> | **33,095**<br><sub>(131 MiB / 53.1% CPU)</sub> | **0.99×** | **0.97×** |
| HTTP/2 · TLS | HTTP/1 · plain | **50,527**<br><sub>(105 MiB / 53.6% CPU)</sub> | **48,526**<br><sub>(106 MiB / 53.8% CPU)</sub> | **1.01×** | **0.97×** |
| HTTP/2 · TLS | HTTP/1 · TLS | **79,563**<br><sub>(114 MiB / 49.5% CPU)</sub> | **78,408**<br><sub>(110 MiB / 51.4% CPU)</sub> | **0.99×** | **0.97×** |
| HTTP/2 · TLS | HTTP/2 · plain | **86,038**<br><sub>(87 MiB / 41.7% CPU)</sub> | **81,234**<br><sub>(88 MiB / 41.3% CPU)</sub> | **0.79×** | **0.74×** |
| HTTP/2 · TLS | HTTP/2 · TLS | **92,175**<br><sub>(84 MiB / 39.4% CPU)</sub> | **88,157**<br><sub>(87 MiB / 39% CPU)</sub> | **0.85×** | **0.81×** |
| HTTP/2 · TLS | HTTP/3 · QUIC | **70,658**<br><sub>(178 MiB / 52.1% CPU)</sub> | **70,185**<br><sub>(168 MiB / 53% CPU)</sub> | **0.96×** | **0.96×** |
| HTTP/3 · QUIC | HTTP/1 · plain | **18,850**<br><sub>(119 MiB / 44.4% CPU)</sub> | **18,514**<br><sub>(118 MiB / 43.9% CPU)</sub> | **0.95×** | **0.93×** |
| HTTP/3 · QUIC | HTTP/1 · TLS | **22,409**<br><sub>(122 MiB / 43.8% CPU)</sub> | **21,820**<br><sub>(129 MiB / 43.5% CPU)</sub> | **0.96×** | **0.93×** |
| HTTP/3 · QUIC | HTTP/2 · plain | **73,470**<br><sub>(191 MiB / 50% CPU)</sub> | **71,607**<br><sub>(188 MiB / 52.3% CPU)</sub> | **0.92×** | **0.89×** |
| HTTP/3 · QUIC | HTTP/2 · TLS | **32,016**<br><sub>(150 MiB / 48.7% CPU)</sub> | **31,288**<br><sub>(143 MiB / 49.8% CPU)</sub> | **0.93×** | **0.91×** |
| HTTP/3 · QUIC | HTTP/3 · QUIC | **25,631**<br><sub>(131 MiB / 44.9% CPU)</sub> | **19,385**<br><sub>(131 MiB / 45.9% CPU)</sub> | **0.89×** | **0.67×** |

## Linux — Titanium vs nginx vs HAProxy vs Envoy vs YARP

### Reverse

Median of **3 repeats** on `ubuntu-latest` (4 vCPU / 16 GiB). Bare reverse 5×5 @ `e781b009` — `compare-product` [36251478808](https://github.com/justcoding121/titanium-web-proxy/actions/runs/36251478808). Warmup 2s / measure 8s; concurrency 8, 16, 32, 64. **Linux nginx is the authoritative nginx baseline.** HAProxy (3.2 `USE_QUIC`) and Envoy (GitHub release, HTTP/3 compiled in) run on the same loopback shape as nginx/YARP. nginx terminate peers use `keepalive 256` + streaming buffers. The RPS workflow installs nginx.org mainline (`http_v3_module`), a QUIC-enabled HAProxy, Envoy, and `libmsquic`. Prefer ratios over absolute RPS. Product 5×5 is **~56-byte JSON keep-alive GET**; H2/H3 same-protocol cells are mostly header work with a tiny body (Titanium best case) — see [Why this comparison is fair](#why-this-comparison-is-fair).

| Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---:|---:|---:|---:|---:|
| HTTP/1 · plain | HTTP/1 · plain | **31,991**<br><sub>(85 MiB / 49.1% CPU)</sub> | 🥇 **38,425**<br><sub>(76 MiB / 41.1% CPU)</sub> | **36,995**<br><sub>(64 MiB / 43.8% CPU)</sub> | **19,785**<br><sub>(116 MiB / 62.4% CPU)</sub> | **28,259**<br><sub>(119 MiB / 50% CPU)</sub> |
| HTTP/1 · plain | HTTP/1 · TLS | **29,310**<br><sub>(105 MiB / 48.8% CPU)</sub> | 🥇 **34,771**<br><sub>(93 MiB / 41.6% CPU)</sub> | **33,130**<br><sub>(69 MiB / 42.7% CPU)</sub> | **22,029**<br><sub>(118 MiB / 56% CPU)</sub> | **26,023**<br><sub>(131 MiB / 49.7% CPU)</sub> |
| HTTP/1 · plain | HTTP/2 · plain | 🥇 **58,396**<br><sub>(134 MiB / 51.7% CPU)</sub> | *Not possible (no H2 upstream)* | **38,925**<br><sub>(65 MiB / 40.9% CPU)</sub> | **35,912**<br><sub>(116 MiB / 60.2% CPU)</sub> | **52,933**<br><sub>(126 MiB / 48.7% CPU)</sub> |
| HTTP/1 · plain | HTTP/2 · TLS | 🥇 **32,904**<br><sub>(149 MiB / 49.1% CPU)</sub> | *Not possible (no H2 upstream)* | **29,367**<br><sub>(66 MiB / 44.5% CPU)</sub> | **20,330**<br><sub>(117 MiB / 62.2% CPU)</sub> | **29,644**<br><sub>(136 MiB / 47.9% CPU)</sub> |
| HTTP/1 · plain | HTTP/3 · QUIC | 🥇 **27,674**<br><sub>(139 MiB / 53.3% CPU)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **0**<br><sub>(peak 3,994 · 120 MiB / 26.4% CPU)</sub> | **0**<br><sub>(peak 24,493 · 155 MiB / 48.4% CPU)</sub> |
| HTTP/1 · TLS | HTTP/1 · plain | **37,270**<br><sub>(110 MiB / 48.2% CPU)</sub> | 🥇 **44,002**<br><sub>(102 MiB / 41.1% CPU)</sub> | **43,014**<br><sub>(85 MiB / 43.1% CPU)</sub> | **27,822**<br><sub>(127 MiB / 56.7% CPU)</sub> | **32,557**<br><sub>(134 MiB / 49.6% CPU)</sub> |
| HTTP/1 · TLS | HTTP/1 · TLS | **20,325**<br><sub>(111 MiB / 48.3% CPU)</sub> | **22,014**<br><sub>(103 MiB / 41.4% CPU)</sub> | 🥇 **22,541**<br><sub>(83 MiB / 43% CPU)</sub> | **15,325**<br><sub>(127 MiB / 55.1% CPU)</sub> | **17,190**<br><sub>(141 MiB / 49.5% CPU)</sub> |
| HTTP/1 · TLS | HTTP/2 · plain | 🥇 **34,977**<br><sub>(141 MiB / 50.3% CPU)</sub> | *Not possible (no H2 upstream)* | **25,503**<br><sub>(83 MiB / 41.5% CPU)</sub> | **23,640**<br><sub>(126 MiB / 58.2% CPU)</sub> | **30,597**<br><sub>(142 MiB / 49.7% CPU)</sub> |
| HTTP/1 · TLS | HTTP/2 · TLS | 🥇 **39,028**<br><sub>(155 MiB / 48% CPU)</sub> | *Not possible (no H2 upstream)* | **38,160**<br><sub>(84 MiB / 42.9% CPU)</sub> | **28,602**<br><sub>(125 MiB / 57.4% CPU)</sub> | **35,554**<br><sub>(140 MiB / 48.5% CPU)</sub> |
| HTTP/1 · TLS | HTTP/3 · QUIC | 🥇 **18,414**<br><sub>(158 MiB / 51.7% CPU)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **0**<br><sub>(peak 2,614 · 130 MiB / 22.1% CPU)</sub> | **0**<br><sub>(peak 16,578 · 159 MiB / 49.8% CPU)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | 🥇 **41,344**<br><sub>(120 MiB / 52.7% CPU)</sub> | **18,300**<br><sub>(79 MiB / 18.9% CPU)</sub> | **27,934**<br><sub>(67 MiB / 24.5% CPU)</sub> | **18,185**<br><sub>(118 MiB / 22.9% CPU)</sub> | **39,798**<br><sub>(113 MiB / 48.4% CPU)</sub> |
| HTTP/2 · plain | HTTP/1 · TLS | 🥇 **42,611**<br><sub>(134 MiB / 50% CPU)</sub> | **19,532**<br><sub>(102 MiB / 19.3% CPU)</sub> | **29,154**<br><sub>(70 MiB / 24.6% CPU)</sub> | **21,831**<br><sub>(119 MiB / 23% CPU)</sub> | **40,681**<br><sub>(126 MiB / 48.3% CPU)</sub> |
| HTTP/2 · plain | HTTP/2 · plain | 🥇 **86,393**<br><sub>(92 MiB / 36.4% CPU)</sub> | *Not possible (no H2 upstream)* | **24,896**<br><sub>(67 MiB / 24.5% CPU)</sub> | **17,189**<br><sub>(115 MiB / 22.3% CPU)</sub> | **48,826**<br><sub>(124 MiB / 47.2% CPU)</sub> |
| HTTP/2 · plain | HTTP/2 · TLS | 🥇 **67,057**<br><sub>(90 MiB / 36.3% CPU)</sub> | *Not possible (no H2 upstream)* | **24,519**<br><sub>(67 MiB / 24.5% CPU)</sub> | **22,257**<br><sub>(119 MiB / 20.6% CPU)</sub> | **45,281**<br><sub>(130 MiB / 45.3% CPU)</sub> |
| HTTP/2 · plain | HTTP/3 · QUIC | 🥇 **39,130**<br><sub>(162 MiB / 52.2% CPU)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **0**<br><sub>(peak 7,157 · 121 MiB / 24.9% CPU)</sub> | **0**<br><sub>(peak 36,903 · 159 MiB / 45.6% CPU)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **34,278**<br><sub>(117 MiB / 51.6% CPU)</sub> | **15,212**<br><sub>(100 MiB / 19.2% CPU)</sub> | **20,389**<br><sub>(82 MiB / 24.6% CPU)</sub> | **13,275**<br><sub>(128 MiB / 23.2% CPU)</sub> | **29,194**<br><sub>(121 MiB / 50% CPU)</sub> |
| HTTP/2 · TLS | HTTP/1 · TLS | 🥇 **30,837**<br><sub>(124 MiB / 49.2% CPU)</sub> | **15,299**<br><sub>(109 MiB / 19.6% CPU)</sub> | **21,277**<br><sub>(84 MiB / 24.5% CPU)</sub> | **16,662**<br><sub>(130 MiB / 22.7% CPU)</sub> | **27,935**<br><sub>(131 MiB / 48.4% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | 🥇 **112,462**<br><sub>(101 MiB / 38.5% CPU)</sub> | *Not possible (no H2 upstream)* | **34,240**<br><sub>(84 MiB / 24.2% CPU)</sub> | **31,524**<br><sub>(127 MiB / 21.3% CPU)</sub> | **58,054**<br><sub>(130 MiB / 46.2% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **54,387**<br><sub>(101 MiB / 35.5% CPU)</sub> | *Not possible (no H2 upstream)* | **18,904**<br><sub>(81 MiB / 24.6% CPU)</sub> | **15,233**<br><sub>(125 MiB / 20.9% CPU)</sub> | **33,746**<br><sub>(128 MiB / 46% CPU)</sub> |
| HTTP/2 · TLS | HTTP/3 · QUIC | 🥇 **29,007**<br><sub>(159 MiB / 50.5% CPU)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **0**<br><sub>(peak 5,227 · 129 MiB / 24.6% CPU)</sub> | **0**<br><sub>(peak 25,242 · 158 MiB / 46.2% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | **31,202**<br><sub>(153 MiB / 51.4% CPU)</sub> | **0**<br><sub>(peak 24,751 · 111 MiB / 21.4% CPU)</sub> | 🥇 **34,222**<br><sub>(88 MiB / 25.9% CPU)</sub> | **1,678**<br><sub>(136 MiB / 6.1% CPU)</sub> | **27,759**<br><sub>(193 MiB / 49.5% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | **16,786**<br><sub>(161 MiB / 47.8% CPU)</sub> | **0**<br><sub>(peak 11,750 · 116 MiB / 23.1% CPU)</sub> | 🥇 **17,715**<br><sub>(91 MiB / 29.3% CPU)</sub> | **3,733**<br><sub>(136 MiB / 24.4% CPU)</sub> | **15,259**<br><sub>(197 MiB / 51.7% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/2 · plain | 🥇 **31,995**<br><sub>(159 MiB / 55.4% CPU)</sub> | *Not possible (no H2 upstream)* | **29,500**<br><sub>(86 MiB / 25.1% CPU)</sub> | **30**<br><sub>(131 MiB / 0.2% CPU)</sub> | **27,180**<br><sub>(199 MiB / 48.6% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | 🥇 **35,588**<br><sub>(162 MiB / 52% CPU)</sub> | *Not possible (no H2 upstream)* | **32,299**<br><sub>(88 MiB / 24.7% CPU)</sub> | **94**<br><sub>(130 MiB / 0.5% CPU)</sub> | **30,826**<br><sub>(205 MiB / 47% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/3 · QUIC | 🥇 **20,435**<br><sub>(154 MiB / 46.6% CPU)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **0**<br><sub>(peak 1,874 · 130 MiB / 25% CPU)</sub> | **0**<br><sub>(peak 15,612 · 205 MiB / 47.7% CPU)</sub> |

### MITM (TWP only)

Same Client×Origin wires with interception on (`compare-product` [36251478808](https://github.com/justcoding121/titanium-web-proxy/actions/runs/36251478808)). **Lite** = no-op handlers (unchanged-lite finish). **Full** = append-only header mutation (harness: one probe header each way; product: generic append-only relay via `MitmCompressedRelayHelper`). nginx/HAProxy/Envoy/YARP cannot MITM. **Lite÷Reverse** / **Full÷Reverse** vs bare reverse (**same job / comparison-group shard**). Completion gate: Lite ≥ **0.50×** and Full ≥ **0.50×** reverse sustain @ c=64 (median of 3 GHA runs); reverse TWP÷YARP ≥ **0.75×** (no terminate-peer gate).

**v1 append-only relay (2026-08-27):** Pre-fix H2→H2 Full÷Reverse was **0.13–0.16×** ([32960766249](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32960766249)). Post-fix @ `df172718`: H2 plain→H2 plain Full **0.77–0.79×**, H3→H1 Full **0.91–0.93×**, all MITM arms ≥ **0.70×** on median of [33041445371](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33041445371), [33055267086](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33055267086), [33055272140](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33055272140).

**v2 drop-only + non-unique append (2026-08-27):** `MitmStaticRebuildHelper` rebuilds static HPACK/QPACK after 1–4 unique header drops; trailing non-unique appends stay on compressed relay. @ `e781b009`: all MITM arms ≥ **0.70×** on GHA median ([33087088466](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087088466), [33087091622](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087091622), [33105885748](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33105885748) Linux; [33087085235](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087085235), [33087088466](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087088466), [33087091622](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087091622) Windows). H2 plain→H2 plain Full **0.77–0.79×** (Win) / **0.78×** (Lin).

| Client | Origin | Lite sustain | Full sustain | Lite÷Reverse | Full÷Reverse |
|---|---|---:|---:|---:|---:|
| HTTP/1 · plain | HTTP/1 · plain | **32,833**<br><sub>(97 MiB / 50.3% CPU)</sub> | **31,423**<br><sub>(94 MiB / 50.2% CPU)</sub> | **1.03×** | **0.98×** |
| HTTP/1 · plain | HTTP/1 · TLS | **29,508**<br><sub>(113 MiB / 49.7% CPU)</sub> | **29,251**<br><sub>(114 MiB / 49.7% CPU)</sub> | **1.01×** | **1×** |
| HTTP/1 · plain | HTTP/2 · plain | **59,357**<br><sub>(142 MiB / 54.1% CPU)</sub> | **58,377**<br><sub>(137 MiB / 53.9% CPU)</sub> | **1.02×** | **1×** |
| HTTP/1 · plain | HTTP/2 · TLS | **32,771**<br><sub>(149 MiB / 49.5% CPU)</sub> | **31,696**<br><sub>(149 MiB / 49.5% CPU)</sub> | **1×** | **0.96×** |
| HTTP/1 · plain | HTTP/3 · QUIC | **26,971**<br><sub>(141 MiB / 55.7% CPU)</sub> | **25,974**<br><sub>(139 MiB / 54.5% CPU)</sub> | **0.97×** | **0.94×** |
| HTTP/1 · TLS | HTTP/1 · plain | **37,501**<br><sub>(122 MiB / 48.9% CPU)</sub> | **36,320**<br><sub>(119 MiB / 48.7% CPU)</sub> | **1.01×** | **0.97×** |
| HTTP/1 · TLS | HTTP/1 · TLS | **20,534**<br><sub>(118 MiB / 49.1% CPU)</sub> | **19,612**<br><sub>(117 MiB / 48.7% CPU)</sub> | **1.01×** | **0.96×** |
| HTTP/1 · TLS | HTTP/2 · plain | **34,281**<br><sub>(148 MiB / 51.7% CPU)</sub> | **33,278**<br><sub>(148 MiB / 51.6% CPU)</sub> | **0.98×** | **0.95×** |
| HTTP/1 · TLS | HTTP/2 · TLS | **38,746**<br><sub>(159 MiB / 49.2% CPU)</sub> | **37,443**<br><sub>(160 MiB / 49.1% CPU)</sub> | **0.99×** | **0.96×** |
| HTTP/1 · TLS | HTTP/3 · QUIC | **18,396**<br><sub>(161 MiB / 53% CPU)</sub> | **18,253**<br><sub>(154 MiB / 52.5% CPU)</sub> | **1×** | **0.99×** |
| HTTP/2 · plain | HTTP/1 · plain | **42,250**<br><sub>(121 MiB / 54.2% CPU)</sub> | **39,693**<br><sub>(123 MiB / 54.2% CPU)</sub> | **1.02×** | **0.96×** |
| HTTP/2 · plain | HTTP/1 · TLS | **42,967**<br><sub>(128 MiB / 51.6% CPU)</sub> | **41,922**<br><sub>(131 MiB / 51.2% CPU)</sub> | **1.01×** | **0.98×** |
| HTTP/2 · plain | HTTP/2 · plain | **63,723**<br><sub>(94 MiB / 42.4% CPU)</sub> | **58,831**<br><sub>(96 MiB / 40.9% CPU)</sub> | **0.74×** | **0.68×** |
| HTTP/2 · plain | HTTP/2 · TLS | **54,849**<br><sub>(96 MiB / 41.2% CPU)</sub> | **52,007**<br><sub>(94 MiB / 40.7% CPU)</sub> | **0.82×** | **0.78×** |
| HTTP/2 · plain | HTTP/3 · QUIC | **39,408**<br><sub>(169 MiB / 51.8% CPU)</sub> | **37,860**<br><sub>(167 MiB / 52.2% CPU)</sub> | **1.01×** | **0.97×** |
| HTTP/2 · TLS | HTTP/1 · plain | **35,214**<br><sub>(116 MiB / 54% CPU)</sub> | **35,175**<br><sub>(124 MiB / 54.3% CPU)</sub> | **1.03×** | **1.03×** |
| HTTP/2 · TLS | HTTP/1 · TLS | **31,922**<br><sub>(123 MiB / 51.5% CPU)</sub> | **31,046**<br><sub>(119 MiB / 51.2% CPU)</sub> | **1.04×** | **1.01×** |
| HTTP/2 · TLS | HTTP/2 · plain | **84,011**<br><sub>(113 MiB / 41.9% CPU)</sub> | **76,541**<br><sub>(113 MiB / 41.7% CPU)</sub> | **0.75×** | **0.68×** |
| HTTP/2 · TLS | HTTP/2 · TLS | **46,148**<br><sub>(111 MiB / 39.2% CPU)</sub> | **43,294**<br><sub>(117 MiB / 40.2% CPU)</sub> | **0.85×** | **0.8×** |
| HTTP/2 · TLS | HTTP/3 · QUIC | **28,493**<br><sub>(168 MiB / 51.4% CPU)</sub> | **28,317**<br><sub>(165 MiB / 51.7% CPU)</sub> | **0.98×** | **0.98×** |
| HTTP/3 · QUIC | HTTP/1 · plain | **30,452**<br><sub>(157 MiB / 52.8% CPU)</sub> | **30,204**<br><sub>(162 MiB / 52.7% CPU)</sub> | **0.98×** | **0.97×** |
| HTTP/3 · QUIC | HTTP/1 · TLS | **15,855**<br><sub>(158 MiB / 48.6% CPU)</sub> | **15,608**<br><sub>(166 MiB / 48.4% CPU)</sub> | **0.94×** | **0.93×** |
| HTTP/3 · QUIC | HTTP/2 · plain | **30,574**<br><sub>(171 MiB / 57% CPU)</sub> | **29,635**<br><sub>(163 MiB / 55.8% CPU)</sub> | **0.96×** | **0.93×** |
| HTTP/3 · QUIC | HTTP/2 · TLS | **33,942**<br><sub>(177 MiB / 52.6% CPU)</sub> | **33,807**<br><sub>(179 MiB / 53.2% CPU)</sub> | **0.95×** | **0.95×** |
| HTTP/3 · QUIC | HTTP/3 · QUIC | **19,370**<br><sub>(158 MiB / 49.6% CPU)</sub> | **19,232**<br><sub>(155 MiB / 49.7% CPU)</sub> | **0.95×** | **0.94×** |

## macOS — Titanium vs nginx vs HAProxy vs Envoy vs YARP

### Reverse

Median of **3 repeats** on `macos-15-intel` (4-core / 14 GB). Bare reverse 5×5 @ `e781b009` — `compare-product` [36251478808](https://github.com/justcoding121/titanium-web-proxy/actions/runs/36251478808). Warmup 2s / measure 8s; concurrency 8, 16, 32, 64. Prefer TWP÷peer ratios over absolute RPS. **RPS cells** include median RSS / CPU at the peak-RPS step as `<br><sub>(MiB / CPU%)</sub>`. The RPS workflow installs Homebrew nginx (`http_v3_module`), Homebrew HAProxy with `USE_QUIC` (3.2 source fallback), Envoy (Homebrew bottle or pinned darwin-amd64 1.36.7), Homebrew `libmsquic` (+ `DYLD_*`), and YARP. Do not publish from `macos-latest` (3-core / 7 GB). Product 5×5 is **~56-byte JSON keep-alive GET**; H2/H3 same-protocol cells are mostly header work with a tiny body (Titanium best case) — see [Why this comparison is fair](#why-this-comparison-is-fair).

| Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---:|---:|---:|---:|---:|
| HTTP/1 · plain | HTTP/1 · plain | 🥇 **28,420**<br><sub>(82 MiB / 38.9% CPU)</sub> | **19,080**<br><sub>(52 MiB / 22.6% CPU)</sub> | **27,986**<br><sub>(50 MiB / 30.4% CPU)</sub> | **10,329**<br><sub>(72 MiB / 49.8% CPU)</sub> | **23,006**<br><sub>(104 MiB / 40.8% CPU)</sub> |
| HTTP/1 · plain | HTTP/1 · TLS | 🥇 **17,635**<br><sub>(93 MiB / 39.8% CPU)</sub> | **8,225**<br><sub>(73 MiB / 19.2% CPU)</sub> | **16,094**<br><sub>(55 MiB / 34.2% CPU)</sub> | **6,051**<br><sub>(74 MiB / 45% CPU)</sub> | **14,220**<br><sub>(122 MiB / 42.8% CPU)</sub> |
| HTTP/1 · plain | HTTP/2 · plain | **16,008**<br><sub>(88 MiB / 42.8% CPU)</sub> | *Not possible (no H2 upstream)* | **11,216**<br><sub>(51 MiB / 30.2% CPU)</sub> | **3,950**<br><sub>(73 MiB / 23% CPU)</sub> | 🥇 **17,475**<br><sub>(110 MiB / 38.7% CPU)</sub> |
| HTTP/1 · plain | HTTP/2 · TLS | **27,468**<br><sub>(113 MiB / 42.5% CPU)</sub> | *Not possible (no H2 upstream)* | **21,896**<br><sub>(52 MiB / 37.6% CPU)</sub> | **10,383**<br><sub>(73 MiB / 41.5% CPU)</sub> | 🥇 **31,556**<br><sub>(110 MiB / 41.1% CPU)</sub> |
| HTTP/1 · plain | HTTP/3 · QUIC | **5,941**<br><sub>(91 MiB / 48.3% CPU)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **4,626**<br><sub>(75 MiB / 42.9% CPU)</sub> | 🥇 **7,328**<br><sub>(109 MiB / 39.8% CPU)</sub> |
| HTTP/1 · TLS | HTTP/1 · plain | **9,433**<br><sub>(94 MiB / 35% CPU)</sub> | **8,610**<br><sub>(72 MiB / 25.3% CPU)</sub> | 🥇 **10,312**<br><sub>(65 MiB / 31.5% CPU)</sub> | **5,491**<br><sub>(79 MiB / 46.1% CPU)</sub> | **8,788**<br><sub>(118 MiB / 39.8% CPU)</sub> |
| HTTP/1 · TLS | HTTP/1 · TLS | 🥇 **17,619**<br><sub>(97 MiB / 38.9% CPU)</sub> | **8,674**<br><sub>(84 MiB / 20.2% CPU)</sub> | **16,200**<br><sub>(68 MiB / 36.2% CPU)</sub> | **6,173**<br><sub>(80 MiB / 26% CPU)</sub> | **15,099**<br><sub>(124 MiB / 39.9% CPU)</sub> |
| HTTP/1 · TLS | HTTP/2 · plain | 🥇 **17,010**<br><sub>(96 MiB / 39.8% CPU)</sub> | *Not possible (no H2 upstream)* | **10,677**<br><sub>(66 MiB / 31.8% CPU)</sub> | **6,237**<br><sub>(79 MiB / 35.8% CPU)</sub> | **16,092**<br><sub>(117 MiB / 36.7% CPU)</sub> |
| HTTP/1 · TLS | HTTP/2 · TLS | **8,228**<br><sub>(158 MiB / 36.1% CPU)</sub> | *Not possible (no H2 upstream)* | **7,960**<br><sub>(66 MiB / 35.2% CPU)</sub> | **4,738**<br><sub>(81 MiB / 22.8% CPU)</sub> | 🥇 **9,326**<br><sub>(117 MiB / 35.2% CPU)</sub> |
| HTTP/1 · TLS | HTTP/3 · QUIC | **4,532**<br><sub>(103 MiB / 56.2% CPU)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **6,219**<br><sub>(82 MiB / 45.8% CPU)</sub> | 🥇 **7,280**<br><sub>(122 MiB / 36.2% CPU)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | 🥇 **22,728**<br><sub>(89 MiB / 42.3% CPU)</sub> | **15,249**<br><sub>(58 MiB / 19.2% CPU)</sub> | **13,207**<br><sub>(56 MiB / 21% CPU)</sub> | **4,710**<br><sub>(75 MiB / 22.1% CPU)</sub> | **19,511**<br><sub>(103 MiB / 41.4% CPU)</sub> |
| HTTP/2 · plain | HTTP/1 · TLS | 🥇 **12,870**<br><sub>(96 MiB / 43.9% CPU)</sub> | **8,660**<br><sub>(85 MiB / 19.1% CPU)</sub> | **6,300**<br><sub>(59 MiB / 21.4% CPU)</sub> | **3,021**<br><sub>(77 MiB / 21.2% CPU)</sub> | **11,831**<br><sub>(120 MiB / 44.4% CPU)</sub> |
| HTTP/2 · plain | HTTP/2 · plain | 🥇 **56,228**<br><sub>(73 MiB / 33.6% CPU)</sub> | *Not possible (no H2 upstream)* | **17,978**<br><sub>(56 MiB / 21.3% CPU)</sub> | **10,690**<br><sub>(71 MiB / 21.9% CPU)</sub> | **44,814**<br><sub>(108 MiB / 43.2% CPU)</sub> |
| HTTP/2 · plain | HTTP/2 · TLS | 🥇 **39,833**<br><sub>(77 MiB / 33.8% CPU)</sub> | *Not possible (no H2 upstream)* | **10,109**<br><sub>(58 MiB / 20.7% CPU)</sub> | **7,431**<br><sub>(73 MiB / 20.8% CPU)</sub> | **29,937**<br><sub>(111 MiB / 40.3% CPU)</sub> |
| HTTP/2 · plain | HTTP/3 · QUIC | **5,272**<br><sub>(95 MiB / 46.8% CPU)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **2,973**<br><sub>(75 MiB / 22.8% CPU)</sub> | 🥇 **5,279**<br><sub>(116 MiB / 35.5% CPU)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **29,835**<br><sub>(94 MiB / 45.4% CPU)</sub> | **12,990**<br><sub>(73 MiB / 18.6% CPU)</sub> | **10,579**<br><sub>(67 MiB / 21% CPU)</sub> | **4,384**<br><sub>(82 MiB / 22.8% CPU)</sub> | **19,194**<br><sub>(112 MiB / 42.9% CPU)</sub> |
| HTTP/2 · TLS | HTTP/1 · TLS | 🥇 **16,948**<br><sub>(95 MiB / 45.3% CPU)</sub> | **11,326**<br><sub>(92 MiB / 19.2% CPU)</sub> | **8,132**<br><sub>(69 MiB / 21.8% CPU)</sub> | **4,219**<br><sub>(83 MiB / 21.5% CPU)</sub> | **14,603**<br><sub>(122 MiB / 45% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | 🥇 **33,503**<br><sub>(83 MiB / 32.7% CPU)</sub> | *Not possible (no H2 upstream)* | **9,517**<br><sub>(68 MiB / 21.5% CPU)</sub> | **6,823**<br><sub>(79 MiB / 21.6% CPU)</sub> | **20,691**<br><sub>(117 MiB / 40.8% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **41,611**<br><sub>(85 MiB / 33.6% CPU)</sub> | *Not possible (no H2 upstream)* | **7,670**<br><sub>(68 MiB / 21.2% CPU)</sub> | **6,498**<br><sub>(79 MiB / 21.3% CPU)</sub> | **27,304**<br><sub>(116 MiB / 42% CPU)</sub> |
| HTTP/2 · TLS | HTTP/3 · QUIC | **5,194**<br><sub>(106 MiB / 43.4% CPU)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **1,987**<br><sub>(81 MiB / 23.9% CPU)</sub> | 🥇 **5,419**<br><sub>(123 MiB / 39.1% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | **4,247**<br><sub>(102 MiB / 42.2% CPU)</sub> | **0**<br><sub>(peak 5,655 · 62 MiB / 12.6% CPU)</sub> | 🥇 **7,185**<br><sub>(66 MiB / 21.5% CPU)</sub> | **1,229**<br><sub>(86 MiB / 29.5% CPU)</sub> | **6,834**<br><sub>(173 MiB / 37.3% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | **5,799**<br><sub>(119 MiB / 47% CPU)</sub> | *Not measured* | 🥇 **9,008**<br><sub>(70 MiB / 25.1% CPU)</sub> | **2,095**<br><sub>(87 MiB / 25% CPU)</sub> | **8,499**<br><sub>(191 MiB / 41.1% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/2 · plain | **4,985**<br><sub>(98 MiB / 40.8% CPU)</sub> | *Not possible (no H2 upstream)* | 🥇 **7,518**<br><sub>(66 MiB / 22.2% CPU)</sub> | **1,739**<br><sub>(84 MiB / 33.2% CPU)</sub> | **5,366**<br><sub>(199 MiB / 37% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | **3,388**<br><sub>(105 MiB / 42.6% CPU)</sub> | *Not possible (no H2 upstream)* | **6,612**<br><sub>(68 MiB / 23.6% CPU)</sub> | **1,661**<br><sub>(83 MiB / 29.1% CPU)</sub> | 🥇 **7,285**<br><sub>(190 MiB / 37.4% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/3 · QUIC | **5,392**<br><sub>(97 MiB / 45.3% CPU)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **1,432**<br><sub>(81 MiB / 27.6% CPU)</sub> | 🥇 **6,526**<br><sub>(179 MiB / 38.3% CPU)</sub> |

### MITM (TWP only)

Same Client×Origin wires with interception on (`compare-product` [36251478808](https://github.com/justcoding121/titanium-web-proxy/actions/runs/36251478808)). **Lite** = no-op handlers (unchanged-lite finish). **Full** = append-only header mutation (harness: one probe header each way; product: generic append-only relay via `MitmCompressedRelayHelper`). nginx/HAProxy/Envoy/YARP cannot MITM. **Lite÷Reverse** / **Full÷Reverse** vs bare reverse (**same job / comparison-group shard**). Completion gate: Lite ≥ **0.50×** and Full ≥ **0.50×** reverse sustain @ c=64 (median of 3 GHA runs); reverse TWP÷YARP ≥ **0.75×** (no terminate-peer gate).

**v1 append-only relay (2026-08-27):** Pre-fix H2→H2 Full÷Reverse was **0.13–0.16×** ([32960766249](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32960766249)). Post-fix @ `df172718`: H2 plain→H2 plain Full **0.77–0.79×**, H3→H1 Full **0.91–0.93×**, all MITM arms ≥ **0.70×** on median of [33041445371](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33041445371), [33055267086](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33055267086), [33055272140](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33055272140).

**v2 drop-only + non-unique append (2026-08-27):** `MitmStaticRebuildHelper` rebuilds static HPACK/QPACK after 1–4 unique header drops; trailing non-unique appends stay on compressed relay. @ `e781b009`: all MITM arms ≥ **0.70×** on GHA median ([33087088466](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087088466), [33087091622](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087091622), [33105885748](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33105885748) Linux; [33087085235](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087085235), [33087088466](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087088466), [33087091622](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087091622) Windows). H2 plain→H2 plain Full **0.77–0.79×** (Win) / **0.78×** (Lin).

| Client | Origin | Lite sustain | Full sustain | Lite÷Reverse | Full÷Reverse |
|---|---|---:|---:|---:|---:|
| HTTP/1 · plain | HTTP/1 · plain | **25,054**<br><sub>(84 MiB / 39.1% CPU)</sub> | **17,033**<br><sub>(81 MiB / 39.5% CPU)</sub> | **0.88×** | **0.6×** |
| HTTP/1 · plain | HTTP/1 · TLS | **9,161**<br><sub>(94 MiB / 38.8% CPU)</sub> | **9,303**<br><sub>(94 MiB / 40.2% CPU)</sub> | **0.52×** | **0.53×** |
| HTTP/1 · plain | HTTP/2 · plain | **16,268**<br><sub>(92 MiB / 41.9% CPU)</sub> | **25,165**<br><sub>(98 MiB / 44.3% CPU)</sub> | **1.02×** | **1.57×** |
| HTTP/1 · plain | HTTP/2 · TLS | **23,297**<br><sub>(108 MiB / 41.3% CPU)</sub> | **14,457**<br><sub>(105 MiB / 42.5% CPU)</sub> | **0.85×** | **0.53×** |
| HTTP/1 · plain | HTTP/3 · QUIC | **4,017**<br><sub>(91 MiB / 46.3% CPU)</sub> | **5,035**<br><sub>(90 MiB / 51.2% CPU)</sub> | **0.68×** | **0.85×** |
| HTTP/1 · TLS | HTTP/1 · plain | **9,222**<br><sub>(95 MiB / 35.2% CPU)</sub> | **13,799**<br><sub>(95 MiB / 36.6% CPU)</sub> | **0.98×** | **1.46×** |
| HTTP/1 · TLS | HTTP/1 · TLS | **14,957**<br><sub>(98 MiB / 39.4% CPU)</sub> | **13,050**<br><sub>(99 MiB / 40.5% CPU)</sub> | **0.85×** | **0.74×** |
| HTTP/1 · TLS | HTTP/2 · plain | **8,552**<br><sub>(97 MiB / 39.4% CPU)</sub> | **9,777**<br><sub>(99 MiB / 39.1% CPU)</sub> | **0.5×** | **0.57×** |
| HTTP/1 · TLS | HTTP/2 · TLS | **10,393**<br><sub>(156 MiB / 37.7% CPU)</sub> | **10,511**<br><sub>(165 MiB / 37.5% CPU)</sub> | **1.26×** | **1.28×** |
| HTTP/1 · TLS | HTTP/3 · QUIC | **5,675**<br><sub>(100 MiB / 54.6% CPU)</sub> | **4,619**<br><sub>(130 MiB / 53.8% CPU)</sub> | **1.25×** | **1.02×** |
| HTTP/2 · plain | HTTP/1 · plain | **15,166**<br><sub>(88 MiB / 44.2% CPU)</sub> | **16,528**<br><sub>(90 MiB / 46.6% CPU)</sub> | **0.67×** | **0.73×** |
| HTTP/2 · plain | HTTP/1 · TLS | **14,234**<br><sub>(97 MiB / 44.8% CPU)</sub> | **21,297**<br><sub>(96 MiB / 45.5% CPU)</sub> | **1.11×** | **1.65×** |
| HTTP/2 · plain | HTTP/2 · plain | **42,763**<br><sub>(76 MiB / 37.2% CPU)</sub> | **53,348**<br><sub>(76 MiB / 37.8% CPU)</sub> | **0.76×** | **0.95×** |
| HTTP/2 · plain | HTTP/2 · TLS | **22,263**<br><sub>(82 MiB / 37% CPU)</sub> | **21,821**<br><sub>(81 MiB / 35.4% CPU)</sub> | **0.56×** | **0.55×** |
| HTTP/2 · plain | HTTP/3 · QUIC | **6,148**<br><sub>(94 MiB / 49.4% CPU)</sub> | **4,692**<br><sub>(94 MiB / 47% CPU)</sub> | **1.17×** | **0.89×** |
| HTTP/2 · TLS | HTTP/1 · plain | **27,071**<br><sub>(94 MiB / 45.1% CPU)</sub> | **29,962**<br><sub>(90 MiB / 46.9% CPU)</sub> | **0.91×** | **1×** |
| HTTP/2 · TLS | HTTP/1 · TLS | **11,892**<br><sub>(96 MiB / 45.5% CPU)</sub> | **11,153**<br><sub>(97 MiB / 45.1% CPU)</sub> | **0.7×** | **0.66×** |
| HTTP/2 · TLS | HTTP/2 · plain | **39,208**<br><sub>(86 MiB / 37% CPU)</sub> | **35,580**<br><sub>(85 MiB / 37.3% CPU)</sub> | **1.17×** | **1.06×** |
| HTTP/2 · TLS | HTTP/2 · TLS | **31,117**<br><sub>(89 MiB / 38% CPU)</sub> | **43,110**<br><sub>(88 MiB / 36.9% CPU)</sub> | **0.75×** | **1.04×** |
| HTTP/2 · TLS | HTTP/3 · QUIC | **4,940**<br><sub>(107 MiB / 48.7% CPU)</sub> | **4,669**<br><sub>(107 MiB / 45.8% CPU)</sub> | **0.95×** | **0.9×** |
| HTTP/3 · QUIC | HTTP/1 · plain | **5,871**<br><sub>(103 MiB / 46.2% CPU)</sub> | **5,448**<br><sub>(102 MiB / 43.2% CPU)</sub> | **1.38×** | **1.28×** |
| HTTP/3 · QUIC | HTTP/1 · TLS | **5,237**<br><sub>(117 MiB / 47.1% CPU)</sub> | **6,337**<br><sub>(117 MiB / 45.6% CPU)</sub> | **0.9×** | **1.09×** |
| HTTP/3 · QUIC | HTTP/2 · plain | **5,420**<br><sub>(99 MiB / 47.9% CPU)</sub> | **3,619**<br><sub>(99 MiB / 37.7% CPU)</sub> | **1.09×** | **0.73×** |
| HTTP/3 · QUIC | HTTP/2 · TLS | **6,682**<br><sub>(108 MiB / 47% CPU)</sub> | **5,301**<br><sub>(106 MiB / 49.5% CPU)</sub> | **1.97×** | **1.56×** |
| HTTP/3 · QUIC | HTTP/3 · QUIC | **4,347**<br><sub>(96 MiB / 42.6% CPU)</sub> | **4,213**<br><sub>(96 MiB / 53.2% CPU)</sub> | **0.81×** | **0.78×** |

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

Median of **3** repeats on `windows-latest` @ `e781b009`. Source: Actions [36251486030](https://github.com/justcoding121/titanium-web-proxy/actions/runs/36251486030) (`compare-bodies`). Warmup 2s / measure 8s. **RPS cells** include `(MiB / CPU%)` footprints.

*Not possible:* **HAProxy** and **Envoy** columns are omitted (no official Windows port).


| Body | Client | Origin | TWP | nginx | YARP |
|---|---|---|---:|---:|---:|
| 64 KiB | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **9,445**<br><sub>(90 MiB / 45.9% CPU)</sub> | **595**<br><sub>(141 MiB / 24.7% CPU)</sub> | **8,196**<br><sub>(131 MiB / 47.0% CPU)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **8,099**<br><sub>(173 MiB / 43.9% CPU)</sub> | **571**<br><sub>(142 MiB / 24.8% CPU)</sub> | **6,993**<br><sub>(134 MiB / 50.8% CPU)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **4,236**<br><sub>(136 MiB / 38.8% CPU)</sub> | *Not possible (no QUIC)* | **3,927**<br><sub>(183 MiB / 50.0% CPU)</sub> |
| 256 KiB | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **2,868**<br><sub>(127 MiB / 45.0% CPU)</sub> | **0**<br><sub>(peak 144 · 142 MiB / 24.7% CPU)</sub> | **2,668**<br><sub>(137 MiB / 46.8% CPU)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **2,517**<br><sub>(153 MiB / 41.8% CPU)</sub> | **0**<br><sub>(peak 146 · 141 MiB / 24.9% CPU)</sub> | **1,632**<br><sub>(134 MiB / 34.4% CPU)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/1 · plain | **0**<br><sub>(peak 269 · 224 MiB / 37.1% CPU)</sub> | *Not possible (no QUIC)* | 🥇 **1,098**<br><sub>(185 MiB / 44.4% CPU)</sub> |
| 64 KiB | HTTP/2 · plain | HTTP/1 · plain | 🥇 **11,299**<br><sub>(163 MiB / 41.3% CPU)</sub> | **2,093**<br><sub>(127 MiB / 24.6% CPU)</sub> | **9,592**<br><sub>(119 MiB / 45.0% CPU)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/2 · plain | **4,002**<br><sub>(82 MiB / 38.0% CPU)</sub> | *Not possible* | 🥇 **7,282**<br><sub>(142 MiB / 50.3% CPU)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/2 · TLS | **3,497**<br><sub>(78 MiB / 35.2% CPU)</sub> | *Not possible* | 🥇 **5,746**<br><sub>(139 MiB / 47.4% CPU)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/2 · TLS | **0**<br><sub>(111 MiB / 0.0% CPU)</sub> | *Not possible* | 🥇 **3,808**<br><sub>(189 MiB / 46.2% CPU)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **3,784**<br><sub>(144 MiB / 41.0% CPU)</sub> | *Not possible (no QUIC)* | **3,284**<br><sub>(176 MiB / 46.9% CPU)</sub> |
| 256 KiB | HTTP/2 · plain | HTTP/1 · plain | 🥇 **3,663**<br><sub>(149 MiB / 31.1% CPU)</sub> | **665**<br><sub>(127 MiB / 24.3% CPU)</sub> | **2,688**<br><sub>(123 MiB / 37.6% CPU)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/2 · plain | **1,445**<br><sub>(84 MiB / 33.4% CPU)</sub> | *Not possible* | 🥇 **1,872**<br><sub>(169 MiB / 45.3% CPU)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/2 · TLS | **1,310**<br><sub>(87 MiB / 33.5% CPU)</sub> | *Not possible* | 🥇 **1,449**<br><sub>(146 MiB / 45.0% CPU)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/2 · TLS | **0**<br><sub>(118 MiB / 0.0% CPU)</sub> | *Not possible* | 🥇 **996**<br><sub>(197 MiB / 44.5% CPU)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/1 · TLS | **0**<br><sub>(peak 75 · 197 MiB / 31.6% CPU)</sub> | *Not possible (no QUIC)* | 🥇 **933**<br><sub>(196 MiB / 44.0% CPU)</sub> |

nginx/Windows collapses on large reverse bodies in this harness; treat as same-OS only. H1 TLS **64 KiB** ≈ **1.13×** YARP; **256 KiB** ≈ **1.08×**. H2→H1 64 KiB ≈ **1.17×**; H3→H1 64 KiB ≈ **1.06×**.

### Linux — heavier reverse GET (64 KiB / 256 KiB)

Median of **3** repeats @ `e781b009`. Source: Actions [36251486030](https://github.com/justcoding121/titanium-web-proxy/actions/runs/36251486030) (`compare-bodies`). Warmup 2s / measure 8s.

| Body | Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---|---:|---:|---:|---:|---:|
| 64 KiB | HTTP/1 · TLS | HTTP/1 · plain | **8,090**<br><sub>(129 MiB / 43.9% CPU)</sub> | **5,474**<br><sub>(99 MiB / 51.7% CPU)</sub> | 🥇 **8,626**<br><sub>(83 MiB / 39.6% CPU)</sub> | **7,411**<br><sub>(131 MiB / 45.8% CPU)</sub> | **6,439**<br><sub>(167 MiB / 48.2% CPU)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **5,856**<br><sub>(216 MiB / 41.2% CPU)</sub> | **1,852**<br><sub>(101 MiB / 18.2% CPU)</sub> | **4,943**<br><sub>(84 MiB / 24.4% CPU)</sub> | **4,424**<br><sub>(142 MiB / 23.8% CPU)</sub> | **4,965**<br><sub>(163 MiB / 47.6% CPU)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **5,497**<br><sub>(175 MiB / 43.4% CPU)</sub> | *Not measured* | **4,578**<br><sub>(89 MiB / 32.8% CPU)</sub> | **9**<br><sub>(139 MiB / 0.2% CPU)</sub> | **4,326**<br><sub>(226 MiB / 51.5% CPU)</sub> |
| 256 KiB | HTTP/1 · TLS | HTTP/1 · plain | **2,709**<br><sub>(124 MiB / 37.1% CPU)</sub> | **1,682**<br><sub>(99 MiB / 53.2% CPU)</sub> | 🥇 **2,918**<br><sub>(83 MiB / 33.3% CPU)</sub> | **2,690**<br><sub>(146 MiB / 34.2% CPU)</sub> | **2,113**<br><sub>(169 MiB / 45.7% CPU)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/1 · plain | **1,532**<br><sub>(233 MiB / 34.6% CPU)</sub> | **597**<br><sub>(100 MiB / 24.7% CPU)</sub> | **1,662**<br><sub>(82 MiB / 21.1% CPU)</sub> | 🥇 **1,888**<br><sub>(161 MiB / 21.0% CPU)</sub> | **1,364**<br><sub>(161 MiB / 46.2% CPU)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **1,462**<br><sub>(165 MiB / 43.5% CPU)</sub> | **0**<br><sub>(peak 166 · 121 MiB / 20.3% CPU)</sub> | **1,283**<br><sub>(88 MiB / 31.4% CPU)</sub> | **9**<br><sub>(peak 749 · 155 MiB / 20.4% CPU)</sub> | **1,280**<br><sub>(219 MiB / 48.1% CPU)</sub> |
| 64 KiB | HTTP/2 · plain | HTTP/1 · plain | 🥇 **9,887**<br><sub>(237 MiB / 42.4% CPU)</sub> | **2,514**<br><sub>(80 MiB / 14.0% CPU)</sub> | **7,223**<br><sub>(69 MiB / 24.5% CPU)</sub> | **6,187**<br><sub>(129 MiB / 23.8% CPU)</sub> | **8,074**<br><sub>(150 MiB / 46.2% CPU)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/2 · plain | **3,224**<br><sub>(103 MiB / 36.7% CPU)</sub> | *Not possible* | **2,130**<br><sub>(86 MiB / 24.7% CPU)</sub> | 🥇 **4,680**<br><sub>(141 MiB / 22.8% CPU)</sub> | **4,586**<br><sub>(181 MiB / 45.4% CPU)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/2 · TLS | **1,442**<br><sub>(102 MiB / 23.5% CPU)</sub> | *Not possible* | **1,592**<br><sub>(83 MiB / 24.6% CPU)</sub> | **3,212**<br><sub>(150 MiB / 23.0% CPU)</sub> | 🥇 **3,345**<br><sub>(175 MiB / 45.5% CPU)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/2 · TLS | **0**<br><sub>(158 MiB / 0.1% CPU)</sub> | *Not possible* | **2,066**<br><sub>(90 MiB / 34.8% CPU)</sub> | **0**<br><sub>(140 MiB / 0.1% CPU)</sub> | 🥇 **3,070**<br><sub>(234 MiB / 48.8% CPU)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **3,953**<br><sub>(200 MiB / 44.2% CPU)</sub> | **0**<br><sub>(peak 1,453 · 144 MiB / 23.3% CPU)</sub> | **3,100**<br><sub>(95 MiB / 31.8% CPU)</sub> | **1**<br><sub>(137 MiB / 0.1% CPU)</sub> | **3,123**<br><sub>(245 MiB / 49.7% CPU)</sub> |
| 256 KiB | HTTP/2 · plain | HTTP/1 · plain | **2,839**<br><sub>(203 MiB / 35.5% CPU)</sub> | **940**<br><sub>(79 MiB / 22.5% CPU)</sub> | **2,830**<br><sub>(69 MiB / 22.4% CPU)</sub> | 🥇 **3,065**<br><sub>(153 MiB / 22.8% CPU)</sub> | **2,907**<br><sub>(153 MiB / 38.8% CPU)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/2 · plain | **1,119**<br><sub>(109 MiB / 34.0% CPU)</sub> | *Not possible* | **604**<br><sub>(88 MiB / 24.4% CPU)</sub> | 🥇 **1,784**<br><sub>(171 MiB / 23.1% CPU)</sub> | **1,234**<br><sub>(186 MiB / 43.3% CPU)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/2 · TLS | **818**<br><sub>(110 MiB / 33.0% CPU)</sub> | *Not possible* | **426**<br><sub>(83 MiB / 24.5% CPU)</sub> | 🥇 **988**<br><sub>(158 MiB / 22.3% CPU)</sub> | **945**<br><sub>(174 MiB / 42.9% CPU)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/2 · TLS | **0**<br><sub>(peak 794 · 194 MiB / 50.7% CPU)</sub> | *Not possible* | **663**<br><sub>(94 MiB / 37.6% CPU)</sub> | **0**<br><sub>(160 MiB / 0.2% CPU)</sub> | 🥇 **880**<br><sub>(228 MiB / 46.2% CPU)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/1 · TLS | **0**<br><sub>(peak 960 · 295 MiB / 45.1% CPU)</sub> | **0**<br><sub>(peak 121 · 143 MiB / 22.2% CPU)</sub> | **896**<br><sub>(94 MiB / 32.3% CPU)</sub> | **0**<br><sub>(145 MiB / 0.2% CPU)</sub> | 🥇 **1,020**<br><sub>(251 MiB / 47.6% CPU)</sub> |

On this GHA pass TWP÷YARP H1 TLS ≈ **1.21×** (64 KiB) / **1.26×** (256 KiB); H2→H1 ≈ **1.20×** / **1.15×**; H3→H1 ≈ **1.29×** / **1.16×**. TWP÷nginx H1 TLS ≈ **1.43** / **1.85**. Absolute RPS swings by VM; prefer ratios.

### Windows — POST 64 KiB request + 64 KiB response

Median of **3** repeats on `windows-latest` @ `e781b009`. Source: Actions [36251490198](https://github.com/justcoding121/titanium-web-proxy/actions/runs/36251490198) (`compare-post`).

*Not possible:* **HAProxy** and **Envoy** columns are omitted (no official Windows port).


| Client | Origin | TWP | nginx | YARP |
|---|---|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | 🥇 **5,879**<br><sub>(106 MiB / 44.2% CPU)</sub> | **362**<br><sub>(143 MiB / 24.8% CPU)</sub> | **3,081**<br><sub>(135 MiB / 41.0% CPU)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **4,088**<br><sub>(183 MiB / 48.9% CPU)</sub> | **350**<br><sub>(144 MiB / 24.8% CPU)</sub> | **3,124**<br><sub>(136 MiB / 43.9% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **2,024**<br><sub>(173 MiB / 40.7% CPU)</sub> | *Not possible (no QUIC)* | **1,902**<br><sub>(210 MiB / 51.1% CPU)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | 🥇 **6,222**<br><sub>(176 MiB / 47.4% CPU)</sub> | **1,622**<br><sub>(130 MiB / 24.7% CPU)</sub> | **5,868**<br><sub>(126 MiB / 51.4% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | **8**<br><sub>(84 MiB / 0.2% CPU)</sub> | *Not possible* | 🥇 **2,980**<br><sub>(144 MiB / 36.5% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | **8**<br><sub>(88 MiB / 0.3% CPU)</sub> | *Not possible* | 🥇 **2,766**<br><sub>(149 MiB / 46.9% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | **0**<br><sub>(179 MiB / 55.5% CPU)</sub> | *Not possible* | 🥇 **1,875**<br><sub>(200 MiB / 48.3% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **1,941**<br><sub>(175 MiB / 41.7% CPU)</sub> | *Not possible (no QUIC)* | **1,773**<br><sub>(210 MiB / 48.0% CPU)</sub> |

TWP leads H1 POST (~**1.4×** YARP), H2→H1 POST (~**1.5×** YARP), and H3 POST (~**1.05×** YARP). H2 TLS→H2 TLS POST sustain is ~0 this pass (same concurrent-copier cell as duplex).

### Linux — POST 64 KiB request + 64 KiB response

Median of **3** repeats @ `e781b009`. Source: Actions [36251490198](https://github.com/justcoding121/titanium-web-proxy/actions/runs/36251490198) (`compare-post`).

| Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---:|---:|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | **10,924**<br><sub>(131 MiB / 41.0% CPU)</sub> | **7,189**<br><sub>(102 MiB / 47.3% CPU)</sub> | 🥇 **11,649**<br><sub>(85 MiB / 38.9% CPU)</sub> | **11,404**<br><sub>(132 MiB / 39.5% CPU)</sub> | **6,889**<br><sub>(171 MiB / 53.0% CPU)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **6,697**<br><sub>(250 MiB / 40.4% CPU)</sub> | **3,204**<br><sub>(120 MiB / 22.9% CPU)</sub> | **4,284**<br><sub>(84 MiB / 23.1% CPU)</sub> | **0**<br><sub>(peak 5,864 · 143 MiB / 22.4% CPU)</sub> | **5,328**<br><sub>(170 MiB / 44.7% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **6,073**<br><sub>(246 MiB / 41.3% CPU)</sub> | **888**<br><sub>(113 MiB / 24.9% CPU)</sub> | **4,482**<br><sub>(90 MiB / 34.3% CPU)</sub> | **0**<br><sub>(126 MiB / 0.0% CPU)</sub> | **5,497**<br><sub>(256 MiB / 47.6% CPU)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | 🥇 **10,589**<br><sub>(248 MiB / 42.3% CPU)</sub> | **4,164**<br><sub>(97 MiB / 22.1% CPU)</sub> | **5,607**<br><sub>(69 MiB / 23.3% CPU)</sub> | **0**<br><sub>(peak 9,417 · 132 MiB / 21.6% CPU)</sub> | **8,379**<br><sub>(155 MiB / 43.9% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | **8**<br><sub>(112 MiB / 0.1% CPU)</sub> | *Not possible* | **3,195**<br><sub>(88 MiB / 23.6% CPU)</sub> | **5,065**<br><sub>(141 MiB / 17.5% CPU)</sub> | 🥇 **5,672**<br><sub>(189 MiB / 45.1% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | **8**<br><sub>(118 MiB / 0.1% CPU)</sub> | *Not possible* | **2,705**<br><sub>(89 MiB / 23.8% CPU)</sub> | **4,374**<br><sub>(152 MiB / 22.0% CPU)</sub> | 🥇 **4,446**<br><sub>(186 MiB / 43.0% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | **0**<br><sub>(241 MiB / 0.1% CPU)</sub> | *Not possible* | **2,620**<br><sub>(97 MiB / 31.3% CPU)</sub> | **0**<br><sub>(126 MiB / 0.0% CPU)</sub> | 🥇 **4,092**<br><sub>(260 MiB / 46.0% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **4,868**<br><sub>(262 MiB / 43.1% CPU)</sub> | **876**<br><sub>(129 MiB / 25.0% CPU)</sub> | **3,711**<br><sub>(96 MiB / 34.9% CPU)</sub> | **0**<br><sub>(127 MiB / 0.0% CPU)</sub> | **4,367**<br><sub>(274 MiB / 46.5% CPU)</sub> |

Linux nginx H1/H2/H3 POST completed (nginx.org mainline). TWP÷YARP H1 ≈ **1.5×**; H2→H1 ≈ **1.2×**; H3 ≈ **1.1×**. TWP÷nginx H3 ≈ **6×**.

### Windows — lossy / high-RTT (H2 HOL / H3 loss)

Userspace **5 ms** one-way delay + **1%** TCP connection stall (H1/H2) or UDP datagram drop (H3); **64 KiB** GET. Median of **3** repeats on `windows-latest` @ `e781b009` — [36251492084](https://github.com/justcoding121/titanium-web-proxy/actions/runs/36251492084) (`compare-lossy`).

*Not possible:* **HAProxy** and **Envoy** columns are omitted (no official Windows port).

| Client | Origin | TWP | nginx | YARP |
|---|---|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | 🥇 **671**<br><sub>(88 MiB / 4.4% CPU)</sub> | **633**<br><sub>(142 MiB / 19.3% CPU)</sub> | **666**<br><sub>(122 MiB / 5.8% CPU)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | **0**<br><sub>(peak 87 · 126 MiB / 2.2% CPU)</sub> | **0**<br><sub>(peak 17 · 141 MiB / 0.9% CPU)</sub> | **0**<br><sub>(peak 16 · 99 MiB / 0.8% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | **0**<br><sub>(67 MiB / 0.0% CPU)</sub> | *Not possible (no QUIC)* | **0**<br><sub>(80 MiB / 0.0% CPU)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | **0**<br><sub>(peak 86 · 127 MiB / 2.1% CPU)</sub> | **0**<br><sub>(peak 17 · 127 MiB / 0.1% CPU)</sub> | **0**<br><sub>(peak 16 · 91 MiB / 0.9% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | **0**<br><sub>(peak 8 · 67 MiB / 0.7% CPU)</sub> | *Not possible* | **0**<br><sub>(peak 16 · 101 MiB / 1.1% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | **0**<br><sub>(peak 8 · 72 MiB / 0.4% CPU)</sub> | *Not possible* | **0**<br><sub>(peak 17 · 98 MiB / 0.9% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | **0**<br><sub>(70 MiB / 0.0% CPU)</sub> | *Not possible* | **0**<br><sub>(80 MiB / 0.0% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | **0**<br><sub>(69 MiB / 0.0% CPU)</sub> | *Not possible (no QUIC)* | **0**<br><sub>(80 MiB / 0.0% CPU)</sub> |

H1 is near parity with YARP (~**0.98×**). H2 and H3 sustain are **0** on this Windows GHA pass (lossy HOL / QUIC); Linux table below is the publishable H2 HOL / H3 comparison. Laptop re-measure kept for Windows H3.

### Linux — lossy / high-RTT (H2 HOL / H3 loss)

Median of **3** repeats @ `e781b009`. Source: [36251492084](https://github.com/justcoding121/titanium-web-proxy/actions/runs/36251492084) (`compare-lossy`; lossy H3 uses `quic-http3`).

| Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---:|---:|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | **1,201**<br><sub>(126 MiB / 12.1% CPU)</sub> | 🥇 **1,212**<br><sub>(99 MiB / 10.8% CPU)</sub> | **1,210**<br><sub>(82 MiB / 6.8% CPU)</sub> | **1,203**<br><sub>(127 MiB / 8.5% CPU)</sub> | **1,204**<br><sub>(147 MiB / 16.6% CPU)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **311**<br><sub>(177 MiB / 6.5% CPU)</sub> | **40**<br><sub>(99 MiB / 0.3% CPU)</sub> | **40**<br><sub>(84 MiB / 0.2% CPU)</sub> | **40**<br><sub>(138 MiB / 0.3% CPU)</sub> | **40**<br><sub>(127 MiB / 1.4% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | **360**<br><sub>(153 MiB / 13.4% CPU)</sub> | **88**<br><sub>(110 MiB / 2.0% CPU)</sub> | **58**<br><sub>(85 MiB / 3.1% CPU)</sub> | 🥇 **1,358**<br><sub>(140 MiB / 21.0% CPU)</sub> | **343**<br><sub>(190 MiB / 20.9% CPU)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | 🥇 **321**<br><sub>(169 MiB / 6.4% CPU)</sub> | **40**<br><sub>(78 MiB / 0.2% CPU)</sub> | **41**<br><sub>(69 MiB / 0.2% CPU)</sub> | **40**<br><sub>(127 MiB / 0.3% CPU)</sub> | **41**<br><sub>(121 MiB / 1.1% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | **0**<br><sub>(peak 13 · 94 MiB / 0.6% CPU)</sub> | *Not possible* | 🥇 **41**<br><sub>(88 MiB / 0.3% CPU)</sub> | **41**<br><sub>(136 MiB / 0.3% CPU)</sub> | **40**<br><sub>(129 MiB / 1.4% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | **0**<br><sub>(peak 11 · 94 MiB / 0.6% CPU)</sub> | *Not possible* | 🥇 **42**<br><sub>(83 MiB / 0.5% CPU)</sub> | **40**<br><sub>(135 MiB / 0.4% CPU)</sub> | **41**<br><sub>(131 MiB / 1.5% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | **0**<br><sub>(peak 336 · 157 MiB / 24.6% CPU)</sub> | *Not possible* | **69**<br><sub>(91 MiB / 3.9% CPU)</sub> | 🥇 **1,228**<br><sub>(144 MiB / 23.5% CPU)</sub> | **338**<br><sub>(185 MiB / 23.0% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | **344**<br><sub>(163 MiB / 13.9% CPU)</sub> | **94**<br><sub>(113 MiB / 2.1% CPU)</sub> | **62**<br><sub>(91 MiB / 3.6% CPU)</sub> | 🥇 **1,243**<br><sub>(140 MiB / 23.2% CPU)</sub> | **342**<br><sub>(185 MiB / 22.5% CPU)</sub> |

TWP H2 HOL ≫ YARP (~**7.8×**). H3 TWP÷YARP ≈ **1.07×**.

### Architecture-sensitive

These runs isolate slow app readers, origin-early response, HTTP/2 duplex, and WebSocket echo. See [TWP vs YARP IO model](Performance-Profiling#twp-vs-yarp-io-model). Laptop 1-rep numbers are on [Performance Local Lab](Performance-Local-Lab#architecture-sensitive).

Median of **3** repeats on matched 4 vCPU / 16 GiB runners @ `e781b009` ([36251495534](https://github.com/justcoding121/titanium-web-proxy/actions/runs/36251495534)). Slow consumer = 256 KiB GET, 16 KiB read + 8 ms sleep. Early response = 64 KiB POST, origin writes after 8 KiB. Duplex HTTP/2 = overlapping 64 KiB POST on H2 TLS↔H2 TLS. WebSocket row = H1 Upgrade echo round-trips/sec (not RFC 8441; see WebSocket RFC 8441 table below).

Lossy-link runs (slow **network**) are already published above; they are not a slow **app** reader.

#### Windows

*Not possible:* **HAProxy** and **Envoy** columns are omitted (no official Windows port).

| Scenario | Client | Origin | TWP | nginx | YARP |
|---|---|---|---:|---:|---:|
| Slow consumer (256 KiB GET, throttled client read) | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **248**<br><sub>(100 MiB / 4.2% CPU)</sub> | **204**<br><sub>(144 MiB / 24.7% CPU)</sub> | **240**<br><sub>(111 MiB / 4.3% CPU)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · TLS | HTTP/1 · plain | **249**<br><sub>(112 MiB / 4.5% CPU)</sub> | **157**<br><sub>(141 MiB / 24.8% CPU)</sub> | 🥇 **256**<br><sub>(111 MiB / 6.1% CPU)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/3 · QUIC | HTTP/1 · plain | **0**<br><sub>(peak 28 · 170 MiB / 23.3% CPU)</sub> | *Not possible (no QUIC)* | 🥇 **264**<br><sub>(168 MiB / 21.8% CPU)</sub> |
| Early response (origin writes after first request chunk) | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **4,358**<br><sub>(100 MiB / 35.8% CPU)</sub> | **302**<br><sub>(142 MiB / 24.7% CPU)</sub> | **3,075**<br><sub>(139 MiB / 41.8% CPU)</sub> |
| Early response (origin writes after first request chunk) | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **3,735**<br><sub>(188 MiB / 53.0% CPU)</sub> | **0**<br><sub>(peak 328 · 143 MiB / 24.9% CPU)</sub> | **3,467**<br><sub>(135 MiB / 52.8% CPU)</sub> |
| Early response (origin writes after first request chunk) | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **2,058**<br><sub>(144 MiB / 39.6% CPU)</sub> | *Not possible (no QUIC)* | **1,820**<br><sub>(208 MiB / 52.9% CPU)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · plain | HTTP/1 · plain | **248**<br><sub>(120 MiB / 3.6% CPU)</sub> | **255**<br><sub>(127 MiB / 7.9% CPU)</sub> | 🥇 **256**<br><sub>(107 MiB / 4.7% CPU)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · TLS | HTTP/2 · TLS | **0**<br><sub>(peak 8 · 72 MiB / 0.9% CPU)</sub> | *Not possible* | 🥇 **256**<br><sub>(133 MiB / 9.0% CPU)</sub> |
| Early response (origin writes after first request chunk) | HTTP/2 · TLS | HTTP/2 · TLS | **0**<br><sub>(97 MiB / 0.1% CPU)</sub> | *Not possible* | **0**<br><sub>(120 MiB / 0.3% CPU)</sub> |
| Duplex (both directions live) | HTTP/2 · TLS | HTTP/2 · TLS | **0**<br><sub>(95 MiB / 0.1% CPU)</sub> | *Not possible* | **0**<br><sub>(109 MiB / 0.2% CPU)</sub> |
| Duplex (WebSocket / H1 Upgrade) | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **24,675**<br><sub>(98 MiB / 42.5% CPU)</sub> | **12,521**<br><sub>(143 MiB / 24.8% CPU)</sub> | **22,938**<br><sub>(89 MiB / 43.7% CPU)</sub> |

#### Linux

| Scenario | Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---|---:|---:|---:|---:|---:|
| Slow consumer (256 KiB GET, throttled client read) | HTTP/1 · TLS | HTTP/1 · plain | **464**<br><sub>(122 MiB / 9.8% CPU)</sub> | **410**<br><sub>(100 MiB / 9.8% CPU)</sub> | 🥇 **473**<br><sub>(83 MiB / 5.8% CPU)</sub> | **471**<br><sub>(137 MiB / 6.0% CPU)</sub> | **417**<br><sub>(146 MiB / 14.1% CPU)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · TLS | HTTP/1 · plain | **474**<br><sub>(151 MiB / 18.8% CPU)</sub> | **463**<br><sub>(100 MiB / 17.4% CPU)</sub> | 🥇 **478**<br><sub>(84 MiB / 9.1% CPU)</sub> | **469**<br><sub>(156 MiB / 7.1% CPU)</sub> | **475**<br><sub>(146 MiB / 24.1% CPU)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/3 · QUIC | HTTP/1 · plain | **0**<br><sub>(peak 460 · 154 MiB / 24.2% CPU)</sub> | **0**<br><sub>(peak 451 · 124 MiB / 17.2% CPU)</sub> | **472**<br><sub>(89 MiB / 7.2% CPU)</sub> | **1**<br><sub>(155 MiB / 0.2% CPU)</sub> | 🥇 **476**<br><sub>(195 MiB / 35.4% CPU)</sub> |
| Early response (origin writes after first request chunk) | HTTP/1 · TLS | HTTP/1 · plain | **6,685**<br><sub>(138 MiB / 43.6% CPU)</sub> | **0**<br><sub>(peak 5,204 · 102 MiB / 47.9% CPU)</sub> | 🥇 **7,200**<br><sub>(85 MiB / 40.0% CPU)</sub> | **0**<br><sub>(peak 64 · 129 MiB / 2.4% CPU)</sub> | **4,429**<br><sub>(172 MiB / 55.4% CPU)</sub> |
| Early response (origin writes after first request chunk) | HTTP/2 · TLS | HTTP/1 · plain | **2,398**<br><sub>(210 MiB / 47.1% CPU)</sub> | **0**<br><sub>(peak 1,298 · 113 MiB / 23.2% CPU)</sub> | 🥇 **2,490**<br><sub>(83 MiB / 24.1% CPU)</sub> | **0**<br><sub>(peak 2,646 · 149 MiB / 23.5% CPU)</sub> | **2,233**<br><sub>(164 MiB / 48.5% CPU)</sub> |
| Early response (origin writes after first request chunk) | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **3,024**<br><sub>(208 MiB / 45.9% CPU)</sub> | **0**<br><sub>(peak 466 · 116 MiB / 24.7% CPU)</sub> | **1,717**<br><sub>(91 MiB / 29.6% CPU)</sub> | **0**<br><sub>(126 MiB / 0.1% CPU)</sub> | **2,157**<br><sub>(265 MiB / 48.6% CPU)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · plain | HTTP/1 · plain | **474**<br><sub>(142 MiB / 14.7% CPU)</sub> | **412**<br><sub>(78 MiB / 10.1% CPU)</sub> | **475**<br><sub>(69 MiB / 6.2% CPU)</sub> | **475**<br><sub>(143 MiB / 4.4% CPU)</sub> | 🥇 **478**<br><sub>(148 MiB / 15.6% CPU)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · TLS | HTTP/2 · TLS | **9**<br><sub>(99 MiB / 1.8% CPU)</sub> | *Not possible* | **465**<br><sub>(88 MiB / 14.0% CPU)</sub> | 🥇 **468**<br><sub>(152 MiB / 9.2% CPU)</sub> | **468**<br><sub>(163 MiB / 22.0% CPU)</sub> |
| Early response (origin writes after first request chunk) | HTTP/2 · TLS | HTTP/2 · TLS | **0**<br><sub>(113 MiB / 0.1% CPU)</sub> | *Not possible* | **0**<br><sub>(93 MiB / 0.1% CPU)</sub> | **0**<br><sub>(peak 2,137 · 155 MiB / 20.9% CPU)</sub> | **0**<br><sub>(147 MiB / 0.2% CPU)</sub> |
| Duplex (both directions live) | HTTP/2 · TLS | HTTP/2 · TLS | **0**<br><sub>(106 MiB / 0.1% CPU)</sub> | *Not possible* | **0**<br><sub>(94 MiB / 0.1% CPU)</sub> | **0**<br><sub>(peak 2,126 · 148 MiB / 21.0% CPU)</sub> | **0**<br><sub>(148 MiB / 0.2% CPU)</sub> |
| Duplex (WebSocket / H1 Upgrade) | HTTP/1 · TLS | HTTP/1 · plain | **45,012**<br><sub>(126 MiB / 44.6% CPU)</sub> | 🥇 **51,237**<br><sub>(102 MiB / 36.5% CPU)</sub> | **49,593**<br><sub>(83 MiB / 39.4% CPU)</sub> | **45,569**<br><sub>(127 MiB / 41.8% CPU)</sub> | **41,065**<br><sub>(127 MiB / 44.5% CPU)</sub> |

Slow consumer is sleep-bound; H1/H2/H3 sit in the same band. Early-response H1: TWP leads (~**1.46×** / **1.45×** YARP Win/Linux). **Duplex H2** sustain is **0** for TWP and YARP on this GHA pass (same concurrent-copier cell; see [IO model](Performance-Profiling#twp-vs-yarp-io-model) and laptop numbers). WebSocket: TWP÷YARP Windows ≈ **1.07×**; Linux nginx leads.

### TLS termination cost (H1 TLS → cleartext origin)

Isolates keep-alive tiny GET vs **new connection per request** (handshake-dominated) vs keep-alive **256 KiB**. Product comparison uses RPS and end-to-end latency; TWP can also capture `ClientTlsTiming` when `TWP_RPS_CAPTURE_TLS=1` (child process) — nginx/YARP have no equivalent hook.

#### Windows

Median of **3** repeats on `windows-latest` @ `e781b009`. Source: Actions [36251493745](https://github.com/justcoding121/titanium-web-proxy/actions/runs/36251493745). Absolute RPS on GHA swings hard; prefer **TWP÷YARP**.

*Not possible:* **HAProxy** and **Envoy** columns are omitted (no official Windows port).

| Workload | TWP | nginx | YARP |
|---|---:|---:|---:|
| Keep-alive · tiny GET | 🥇 **35,237**<br><sub>(85 MiB / 47.6% CPU)</sub> | **16,443**<br><sub>(142 MiB / 24.7% CPU)</sub> | **30,344**<br><sub>(102 MiB / 52.1% CPU)</sub> |
| New-connection · tiny GET | 🥇 **974**<br><sub>(85 MiB / 8.5% CPU)</sub> | **0**<br><sub>(peak 290 · 141 MiB / 23.3% CPU)</sub> | **961**<br><sub>(117 MiB / 9.4% CPU)</sub> |
| Keep-alive · 256 KiB GET | 🥇 **3,857**<br><sub>(132 MiB / 44.1% CPU)</sub> | **241**<br><sub>(143 MiB / 24.6% CPU)</sub> | **3,577**<br><sub>(129 MiB / 46.7% CPU)</sub> |

#### Linux

Median of **3** repeats @ `e781b009`. Source: Actions [36251493745](https://github.com/justcoding121/titanium-web-proxy/actions/runs/36251493745).

| Workload | TWP | nginx | HAProxy | Envoy | YARP |
|---|---:|---:|---:|---:|---:|
| Keep-alive · tiny GET | **24,318**<br><sub>(107 MiB / 48.8% CPU)</sub> | 🥇 **28,359**<br><sub>(98 MiB / 40.9% CPU)</sub> | **28,157**<br><sub>(82 MiB / 42.9% CPU)</sub> | **16,565**<br><sub>(126 MiB / 58.9% CPU)</sub> | **20,111**<br><sub>(136 MiB / 50.2% CPU)</sub> |
| New-connection · tiny GET | **956**<br><sub>(118 MiB / 47.2% CPU)</sub> | **1,003**<br><sub>(100 MiB / 44.2% CPU)</sub> | **934**<br><sub>(84 MiB / 43.6% CPU)</sub> | 🥇 **1,062**<br><sub>(128 MiB / 41.2% CPU)</sub> | **944**<br><sub>(151 MiB / 45.8% CPU)</sub> |
| Keep-alive · 256 KiB GET | **2,683**<br><sub>(127 MiB / 36.4% CPU)</sub> | **1,714**<br><sub>(100 MiB / 52.2% CPU)</sub> | 🥇 **2,908**<br><sub>(83 MiB / 33.1% CPU)</sub> | **2,676**<br><sub>(145 MiB / 34.1% CPU)</sub> | **2,112**<br><sub>(172 MiB / 46.1% CPU)</sub> |

All three workloads are **>1.00×** YARP on both OS. On Linux, HAProxy leads keep-alive tiny (near-tie with nginx) and Envoy leads new-connection; TWP stays ahead of YARP on all three.

## Unary gRPC (H2 TLS)

Unary Echo **RPC/s** @ c=64 over H2 TLS→H2 TLS for Titanium, YARP, nginx (`grpc_pass`), HAProxy, and Envoy (OS-possible peers only). Not folded into the architecture-sensitive tables. Median of **3** repeats @ `e781b009` — [36251478808](https://github.com/justcoding121/titanium-web-proxy/actions/runs/36251478808).

| OS | Titanium | YARP | nginx | HAProxy | Envoy |
|---|---:|---:|---:|---:|---:|
| Windows | **64,657**<br><sub>(91 MiB / 23.2% CPU)</sub> | **36,400**<br><sub>(118 MiB / 48.0% CPU)</sub> | **0**<br><sub>(145 MiB / 24.8% CPU)</sub> | *Not possible* | *Not possible* |
| Linux | **54,211**<br><sub>(125 MiB / 29.7% CPU)</sub> | **31,422**<br><sub>(151 MiB / 41.5% CPU)</sub> | **0**<br><sub>(113 MiB / 24.7% CPU)</sub> | **11,683**<br><sub>(84 MiB / 24.5% CPU)</sub> | **18,612**<br><sub>(128 MiB / 21.0% CPU)</sub> |
| macOS | **14,338**<br><sub>(95 MiB / 22.3% CPU)</sub> | **9,852**<br><sub>(154 MiB / 34.6% CPU)</sub> | **0**<br><sub>(63 MiB / 3.0% CPU)</sub> | **0**<br><sub>(63 MiB / 7.9% CPU)</sub> | **0**<br><sub>(80 MiB / 19.2% CPU)</sub> |

## Unary gRPC (H2 TLS → h2c)

Unary Echo **RPC/s** @ c=64 over **H2 TLS → h2c** (edge TLS, cleartext H2 origin). Peers: Titanium, YARP, HAProxy, Envoy. Mode: `compare-grpc` (`*-grpc-h2c`). Median of **3** repeats @ `e781b009` — [36251478808](https://github.com/justcoding121/titanium-web-proxy/actions/runs/36251478808).

*Not possible:* **nginx** column omitted (no H2 upstream).

| OS | Titanium | YARP | HAProxy | Envoy |
|---|---:|---:|---:|---:|
| Windows | **59,524**<br><sub>(92 MiB / 22.8% CPU)</sub> | **33,788**<br><sub>(119 MiB / 47.4% CPU)</sub> | *Not possible* | *Not possible* |
| Linux | **92,180**<br><sub>(137 MiB / 30.9% CPU)</sub> | **50,222**<br><sub>(146 MiB / 41.9% CPU)</sub> | **20,027**<br><sub>(86 MiB / 24.7% CPU)</sub> | **32,386**<br><sub>(127 MiB / 21.3% CPU)</sub> |
| macOS | **14,683**<br><sub>(92 MiB / 21.4% CPU)</sub> | **11,560**<br><sub>(137 MiB / 37.0% CPU)</sub> | **0**<br><sub>(65 MiB / 16.8% CPU)</sub> | **0**<br><sub>(80 MiB / 20.3% CPU)</sub> |

## WebSocket (H1 TLS → H1 TLS)

WebSocket echo round-trips/sec over **dual-TLS** H1 (`proxy_ssl` style). Mode: `compare-ws-h1tls` (`*-duplex-ws-h1tls`).

| OS | Titanium | YARP | nginx | HAProxy | Envoy |
|---|---:|---:|---:|---:|---:|
| Windows | **20,059**<br><sub>(80 MiB / 42.0% CPU)</sub> | **21,033**<br><sub>(93 MiB / 41.1% CPU)</sub> | **9,244**<br><sub>(143 MiB / 24.7% CPU)</sub> | *Not possible* | *Not possible* |
| Linux | **29,577**<br><sub>(142 MiB / 44.1% CPU)</sub> | **26,862**<br><sub>(134 MiB / 44.7% CPU)</sub> | **32,559**<br><sub>(103 MiB / 38.0% CPU)</sub> | **31,592**<br><sub>(85 MiB / 40.1% CPU)</sub> | **32,323**<br><sub>(127 MiB / 39.6% CPU)</sub> |
| macOS | **9,187**<br><sub>(130 MiB / 32.4% CPU)</sub> | **8,956**<br><sub>(105 MiB / 31.1% CPU)</sub> | **7,837**<br><sub>(82 MiB / 21.1% CPU)</sub> | **7,963**<br><sub>(68 MiB / 31.5% CPU)</sub> | **3,912**<br><sub>(77 MiB / 27.1% CPU)</sub> |

## WebSocket (H2 TLS 8441 → H1)

WebSocket echo over **RFC 8441** extended CONNECT (H2 TLS client → H1 plain origin). Mode: `compare-ws-h2` (`*-duplex-ws-h2`).

*Not possible:* **nginx** column omitted (no RFC 8441 extended CONNECT).

| OS | Titanium | YARP | HAProxy | Envoy |
|---|---:|---:|---:|---:|
| Windows | **23,808**<br><sub>(150 MiB / 41.3% CPU)</sub> | **0**<br><sub>(145 MiB / 19.7% CPU)</sub> | *Not possible* | *Not possible* |
| Linux | **1,531**<br><sub>(144 MiB / 17.0% CPU)</sub> | **0**<br><sub>(171 MiB / 24.9% CPU)</sub> | **1,534**<br><sub>(84 MiB / 5.2% CPU)</sub> | **0**<br><sub>(124 MiB / 0.3% CPU)</sub> |
| macOS | **18,565**<br><sub>(357 MiB / 42.7% CPU)</sub> | **0**<br><sub>(140 MiB / 15.5% CPU)</sub> | **18,924**<br><sub>(66 MiB / 35.3% CPU)</sub> | **0**<br><sub>(73 MiB / 0.3% CPU)</sub> |

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
