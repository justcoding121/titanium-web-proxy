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
- **Typical reverse** is H1 TLS→H1 or H2→H1 (~1.1× YARP on Win/Linux tiny GET; Mac H1 is near parity). With **larger bodies**, see [Heavier reverse](#heavier-reverse-workloads) (ratios @ `062f4e72`).

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

Calibration for the shared 4 vCPU loopback shape: how close client + origin are to saturated before ranking reverse peers. Tiny keep-alive GET. Median of **3** repeats @ `062f4e72` — [36316345990](https://github.com/justcoding121/titanium-web-proxy/actions/runs/36316345990). Warmup 2s / measure 8s; concurrency 8, 16, 32, 64. Block A **% of origin-HttpClient** uses median **peak** RPS. Blocks B/C use peer÷YARP / ÷nginx on median peak (not % of H1 origin). **RPS cells** embed median RSS / CPU for the **proxy child** plus its **full descendant tree** (serve-proxy → nginx master → workers); origin-direct samples the **origin** child. Product matrices below use matched `dotnet-httpclient` only (not bombardier).


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
| origin-direct | dotnet-httpclient | **81,947**<br><sub>(55 MiB / 43.3% CPU)</sub> | **100.0%** |
| origin-direct-bombardier | bombardier | **61,877**<br><sub>(56 MiB / 24.5% CPU)</sub> | **75.5%** |
| bare-reverse-http1 | dotnet-httpclient | **40,281**<br><sub>(62 MiB / 47.6% CPU)</sub> | **49.2%** |
| nginx-reverse-http1 | dotnet-httpclient | **25,294**<br><sub>(125 MiB / 24.8% CPU)</sub> | **30.9%** |
| yarp-reverse-http1 | dotnet-httpclient | **35,488**<br><sub>(87 MiB / 51.4% CPU)</sub> | **43.3%** |
| twp-reverse-http1 | dotnet-httpclient | 🥇 **41,962**<br><sub>(73 MiB / 47.1% CPU)</sub> | **51.2%** |

**Linux** (`ubuntu-latest`)

| Arm | Generator | RPS | % of origin-HttpClient |
|---|---|---:|---:|
| origin-direct | dotnet-httpclient | **100,865**<br><sub>(80 MiB / 44.0% CPU)</sub> | **100.0%** |
| origin-direct-bombardier | bombardier | **60,872**<br><sub>(80 MiB / 37.7% CPU)</sub> | **60.3%** |
| bare-reverse-http1 | dotnet-httpclient | **45,816**<br><sub>(69 MiB / 45.9% CPU)</sub> | **45.4%** |
| nginx-reverse-http1 | dotnet-httpclient | 🥇 **56,607**<br><sub>(76 MiB / 39.7% CPU)</sub> | **56.1%** |
| yarp-reverse-http1 | dotnet-httpclient | **41,100**<br><sub>(116 MiB / 48.6% CPU)</sub> | **40.7%** |
| twp-reverse-http1 | dotnet-httpclient | **48,351**<br><sub>(84 MiB / 49.3% CPU)</sub> | **47.9%** |

Reverse peers are about **50–48%** of the origin-direct HttpClient peak on this runner class (Win TWP **50.1%**, Lin TWP **48.3%**). Prefer the **%** column over absolute RPS across runs. Bare and origin-direct are controls (not medal peers).

#### Block B — H2 TLS→H1

Peer ratios (÷YARP / ÷nginx) on median peak; **RPS cells** embed `(MiB / CPU%)`.

**Windows** (`windows-latest`)

| Arm | Generator | RPS | ÷YARP | ÷nginx |
|---|---|---:|---:|---:|
| nginx-reverse-http2 | dotnet-httpclient | **14,723**<br><sub>(142 MiB / 24.3% CPU)</sub> | **0.32×** | **1.00×** |
| yarp-reverse-http2 | dotnet-httpclient | **46,246**<br><sub>(95 MiB / 51.4% CPU)</sub> | **1.00×** | **3.14×** |
| twp-reverse-http2-cleartext | dotnet-httpclient | 🥇 **53,176**<br><sub>(106 MiB / 53.6% CPU)</sub> | **1.15×** | **3.61×** |

**Linux** (`ubuntu-latest`)

| Arm | Generator | RPS | ÷YARP | ÷nginx |
|---|---|---:|---:|---:|
| nginx-reverse-http2 | dotnet-httpclient | **22,691**<br><sub>(102 MiB / 18.6% CPU)</sub> | **0.52×** | **1.00×** |
| yarp-reverse-http2 | dotnet-httpclient | **43,822**<br><sub>(122 MiB / 48.0% CPU)</sub> | **1.00×** | **1.93×** |
| twp-reverse-http2-cleartext | dotnet-httpclient | 🥇 **48,839**<br><sub>(132 MiB / 51.2% CPU)</sub> | **1.11×** | **2.15×** |

#### Block C — H3→H1

Same layout as Block B. Requires QuicListener. nginx needs `http_v3_module` (Windows nginx has no QUIC). HAProxy needs `USE_QUIC` (GHA Linux/macOS require it). Envoy 1.20+ includes HTTP/3.

**Windows** (`windows-latest`)

| Arm | Generator | RPS | ÷YARP | ÷nginx |
|---|---|---:|---:|---:|
| nginx-reverse-http3-cleartext | dotnet-httpclient | *Not possible (no QUIC)* | — | — |
| yarp-reverse-http3-cleartext | dotnet-httpclient | **24,989**<br><sub>(169 MiB / 48.7% CPU)</sub> | **1.00×** | — |
| twp-reverse-http3-cleartext | dotnet-httpclient | 🥇 **25,957**<br><sub>(118 MiB / 44.4% CPU)</sub> | **1.04×** | — |

**Linux** (`ubuntu-latest`)

| Arm | Generator | RPS | ÷YARP | ÷nginx |
|---|---|---:|---:|---:|
| nginx-reverse-http3-cleartext | dotnet-httpclient | **0**<br><sub>(peak 24,818 · 111 MiB / 21.4% CPU)</sub> | **0.91×** | **1.00×** |
| yarp-reverse-http3-cleartext | dotnet-httpclient | **27,354**<br><sub>(196 MiB / 48.8% CPU)</sub> | **1.00×** | **1.10×** |
| twp-reverse-http3-cleartext | dotnet-httpclient | 🥇 **30,286**<br><sub>(157 MiB / 50.6% CPU)</sub> | **1.11×** | **1.22×** |

## Windows — Titanium vs nginx vs HAProxy vs Envoy vs YARP

Client / origin: HTTP version and whether TLS is used (`plain` = cleartext, `TLS` = encrypted, `QUIC` = HTTP/3).

### Reverse

Median of **3 repeats** on `windows-latest` (4 vCPU / 16 GiB). Bare reverse 5×5 @ `062f4e72` — `compare-product` [36316337559](https://github.com/justcoding121/titanium-web-proxy/actions/runs/36316337559). Warmup 2s / measure 8s; concurrency 8, 16, 32, 64. Prefer TWP÷peer ratios over absolute RPS. **RPS cells** include median RSS / CPU at the peak-RPS step as `<br><sub>(MiB / CPU%)</sub>`. nginx terminate peers use `keepalive 256` + streaming buffers. **HAProxy / Envoy are Linux-only peers** (no official Windows port). Laptop High-perf / cool-paired numbers stay on the [local lab](Performance-Local-Lab). Product 5×5 is **~56-byte JSON keep-alive GET**; H2/H3 same-protocol cells are mostly header work with a tiny body (Titanium best case) — see [Why this comparison is fair](#why-this-comparison-is-fair).

**Load generators:** Reverse inbound H3 arms use **`dotnet-httpclient`** (`http_version=3.0`, `RequestVersionExact`). nginx/Windows is same-OS only (no QUIC). HAProxy/Envoy are Linux-only terminate peers.

| Client | Origin | TWP | nginx | YARP |
|---|---|---:|---:|---:|
| HTTP/1 · plain | HTTP/1 · plain | 🥇 **26,132**<br><sub>(73 MiB / 47% CPU)</sub> | **13,741**<br><sub>(125 MiB / 24.9% CPU)</sub> | **21,464**<br><sub>(87 MiB / 51.5% CPU)</sub> |
| HTTP/1 · plain | HTTP/1 · TLS | 🥇 **23,281**<br><sub>(87 MiB / 46.2% CPU)</sub> | **10,344**<br><sub>(136 MiB / 24.4% CPU)</sub> | **19,748**<br><sub>(98 MiB / 46.1% CPU)</sub> |
| HTTP/1 · plain | HTTP/2 · plain | 🥇 **35,150**<br><sub>(105 MiB / 46.5% CPU)</sub> | *Not possible (no H2 upstream)* | **30,465**<br><sub>(93 MiB / 47.3% CPU)</sub> |
| HTTP/1 · plain | HTTP/2 · TLS | 🥇 **32,932**<br><sub>(120 MiB / 46.2% CPU)</sub> | *Not possible (no H2 upstream)* | **30,355**<br><sub>(98 MiB / 47% CPU)</sub> |
| HTTP/1 · plain | HTTP/3 · QUIC | **19,159**<br><sub>(109 MiB / 50.6% CPU)</sub> | *Not possible (no H3 upstream)* | 🥇 **19,220**<br><sub>(120 MiB / 49.4% CPU)</sub> |
| HTTP/1 · TLS | HTTP/1 · plain | 🥇 **19,726**<br><sub>(85 MiB / 47.9% CPU)</sub> | **8,676**<br><sub>(142 MiB / 24.8% CPU)</sub> | **17,467**<br><sub>(102 MiB / 49.2% CPU)</sub> |
| HTTP/1 · TLS | HTTP/1 · TLS | 🥇 **19,096**<br><sub>(88 MiB / 46.4% CPU)</sub> | **6,974**<br><sub>(143 MiB / 24.8% CPU)</sub> | **17,111**<br><sub>(104 MiB / 50.6% CPU)</sub> |
| HTTP/1 · TLS | HTTP/2 · plain | 🥇 **32,937**<br><sub>(110 MiB / 46.8% CPU)</sub> | *Not possible (no H2 upstream)* | **30,172**<br><sub>(104 MiB / 49.2% CPU)</sub> |
| HTTP/1 · TLS | HTTP/2 · TLS | 🥇 **25,079**<br><sub>(121 MiB / 43% CPU)</sub> | *Not possible (no H2 upstream)* | **23,728**<br><sub>(112 MiB / 46.6% CPU)</sub> |
| HTTP/1 · TLS | HTTP/3 · QUIC | 🥇 **16,135**<br><sub>(112 MiB / 49.4% CPU)</sub> | *Not possible (no H3 upstream)* | **11,194**<br><sub>(121 MiB / 40.7% CPU)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | 🥇 **37,635**<br><sub>(95 MiB / 51.1% CPU)</sub> | **12,080**<br><sub>(127 MiB / 24.8% CPU)</sub> | **35,030**<br><sub>(86 MiB / 50.8% CPU)</sub> |
| HTTP/2 · plain | HTTP/1 · TLS | 🥇 **30,012**<br><sub>(101 MiB / 52.8% CPU)</sub> | **6,302**<br><sub>(138 MiB / 24.7% CPU)</sub> | **26,182**<br><sub>(92 MiB / 53.1% CPU)</sub> |
| HTTP/2 · plain | HTTP/2 · plain | 🥇 **105,034**<br><sub>(59 MiB / 28% CPU)</sub> | *Not possible (no H2 upstream)* | **64,546**<br><sub>(93 MiB / 49% CPU)</sub> |
| HTTP/2 · plain | HTTP/2 · TLS | 🥇 **90,318**<br><sub>(65 MiB / 28.5% CPU)</sub> | *Not possible (no H2 upstream)* | **55,483**<br><sub>(103 MiB / 49.6% CPU)</sub> |
| HTTP/2 · plain | HTTP/3 · QUIC | 🥇 **29,631**<br><sub>(113 MiB / 51.2% CPU)</sub> | *Not possible (no H3 upstream)* | **27,872**<br><sub>(119 MiB / 52.1% CPU)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **35,156**<br><sub>(97 MiB / 51.8% CPU)</sub> | **8,216**<br><sub>(141 MiB / 24.5% CPU)</sub> | **29,292**<br><sub>(94 MiB / 53.6% CPU)</sub> |
| HTTP/2 · TLS | HTTP/1 · TLS | 🥇 **32,894**<br><sub>(103 MiB / 51.4% CPU)</sub> | **7,872**<br><sub>(144 MiB / 24.5% CPU)</sub> | **28,986**<br><sub>(98 MiB / 49.8% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | 🥇 **97,698**<br><sub>(75 MiB / 30% CPU)</sub> | *Not possible (no H2 upstream)* | **51,446**<br><sub>(98 MiB / 54.2% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **83,738**<br><sub>(79 MiB / 30.4% CPU)</sub> | *Not possible (no H2 upstream)* | **49,289**<br><sub>(97 MiB / 50% CPU)</sub> |
| HTTP/2 · TLS | HTTP/3 · QUIC | 🥇 **30,136**<br><sub>(133 MiB / 53.4% CPU)</sub> | *Not possible (no H3 upstream)* | **26,591**<br><sub>(130 MiB / 52% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | **13,905**<br><sub>(104 MiB / 45.1% CPU)</sub> | *Not possible (no QUIC)* | 🥇 **15,302**<br><sub>(140 MiB / 50% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **13,869**<br><sub>(112 MiB / 45.1% CPU)</sub> | *Not possible (no QUIC)* | **13,422**<br><sub>(144 MiB / 52.7% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/2 · plain | 🥇 **33,965**<br><sub>(143 MiB / 45% CPU)</sub> | *Not possible (no H2 upstream)* | **23,831**<br><sub>(155 MiB / 50% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | 🥇 **29,351**<br><sub>(130 MiB / 45.6% CPU)</sub> | *Not possible (no H2 upstream)* | **22,144**<br><sub>(151 MiB / 46.9% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/3 · QUIC | 🥇 **16,521**<br><sub>(120 MiB / 46.6% CPU)</sub> | *Not possible (no H3 upstream)* | **12,404**<br><sub>(151 MiB / 50.1% CPU)</sub> |

### MITM (TWP only)

Same Client×Origin wires with interception on (`compare-product` [36316337559](https://github.com/justcoding121/titanium-web-proxy/actions/runs/36316337559)). **Lite** = no-op handlers (unchanged-lite finish). **Full** = append-only header mutation (harness: one probe header each way; product: generic append-only relay via `MitmCompressedRelayHelper`). nginx/HAProxy/Envoy/YARP cannot MITM. **Lite÷Reverse** / **Full÷Reverse** vs bare reverse (**same job / comparison-group shard**). Completion gate: Lite ≥ **0.50×** and Full ≥ **0.50×** reverse sustain @ c=64 (median of 3 GHA runs); reverse TWP÷YARP ≥ **0.60×** (no terminate-peer gate).

**v1 append-only relay (2026-08-27):** Pre-fix H2→H2 Full÷Reverse was **0.13–0.16×** ([32960766249](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32960766249)). Post-fix @ `df172718`: H2 plain→H2 plain Full **0.77–0.79×**, H3→H1 Full **0.91–0.93×**, all MITM arms ≥ **0.70×** on median of [33041445371](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33041445371), [33055267086](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33055267086), [33055272140](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33055272140).

**v2 drop-only + non-unique append (2026-08-27):** `MitmStaticRebuildHelper` rebuilds static HPACK/QPACK after 1–4 unique header drops; trailing non-unique appends stay on compressed relay. @ `062f4e72`: all MITM arms ≥ **0.70×** on GHA median ([33087088466](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087088466), [33087091622](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087091622), [33105885748](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33105885748) Linux; [33087085235](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087085235), [33087088466](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087088466), [33087091622](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087091622) Windows). H2 plain→H2 plain Full **0.77–0.79×** (Win) / **0.78×** (Lin).

| Client | Origin | Lite sustain | Full sustain | Lite÷Reverse | Full÷Reverse |
|---|---|---:|---:|---:|---:|
| HTTP/1 · plain | HTTP/1 · plain | **25,912**<br><sub>(77 MiB / 49.9% CPU)</sub> | **25,182**<br><sub>(80 MiB / 50.8% CPU)</sub> | **0.99×** | **0.96×** |
| HTTP/1 · plain | HTTP/1 · TLS | **24,279**<br><sub>(93 MiB / 48.5% CPU)</sub> | **23,312**<br><sub>(93 MiB / 48.4% CPU)</sub> | **1.04×** | **1×** |
| HTTP/1 · plain | HTTP/2 · plain | **34,117**<br><sub>(105 MiB / 46% CPU)</sub> | **33,233**<br><sub>(101 MiB / 48.3% CPU)</sub> | **0.97×** | **0.95×** |
| HTTP/1 · plain | HTTP/2 · TLS | **32,406**<br><sub>(120 MiB / 48% CPU)</sub> | **31,682**<br><sub>(125 MiB / 46.2% CPU)</sub> | **0.98×** | **0.96×** |
| HTTP/1 · plain | HTTP/3 · QUIC | **19,260**<br><sub>(112 MiB / 52.2% CPU)</sub> | **18,171**<br><sub>(107 MiB / 53.3% CPU)</sub> | **1.01×** | **0.95×** |
| HTTP/1 · TLS | HTTP/1 · plain | **18,543**<br><sub>(93 MiB / 47.5% CPU)</sub> | **19,011**<br><sub>(93 MiB / 49.1% CPU)</sub> | **0.94×** | **0.96×** |
| HTTP/1 · TLS | HTTP/1 · TLS | **19,190**<br><sub>(94 MiB / 52.1% CPU)</sub> | **18,962**<br><sub>(95 MiB / 50.3% CPU)</sub> | **1×** | **0.99×** |
| HTTP/1 · TLS | HTTP/2 · plain | **30,935**<br><sub>(113 MiB / 47.4% CPU)</sub> | **30,451**<br><sub>(112 MiB / 47.5% CPU)</sub> | **0.94×** | **0.92×** |
| HTTP/1 · TLS | HTTP/2 · TLS | **23,496**<br><sub>(124 MiB / 47% CPU)</sub> | **22,982**<br><sub>(128 MiB / 44.3% CPU)</sub> | **0.94×** | **0.92×** |
| HTTP/1 · TLS | HTTP/3 · QUIC | **15,852**<br><sub>(116 MiB / 53.7% CPU)</sub> | **15,638**<br><sub>(110 MiB / 54.4% CPU)</sub> | **0.98×** | **0.97×** |
| HTTP/2 · plain | HTTP/1 · plain | **37,307**<br><sub>(96 MiB / 54.3% CPU)</sub> | **36,655**<br><sub>(96 MiB / 52.6% CPU)</sub> | **0.99×** | **0.97×** |
| HTTP/2 · plain | HTTP/1 · TLS | **27,384**<br><sub>(105 MiB / 51.4% CPU)</sub> | **28,769**<br><sub>(104 MiB / 54.7% CPU)</sub> | **0.91×** | **0.96×** |
| HTTP/2 · plain | HTTP/2 · plain | **85,102**<br><sub>(68 MiB / 41% CPU)</sub> | **79,801**<br><sub>(73 MiB / 41.9% CPU)</sub> | **0.81×** | **0.76×** |
| HTTP/2 · plain | HTTP/2 · TLS | **73,070**<br><sub>(75 MiB / 37.6% CPU)</sub> | **71,004**<br><sub>(79 MiB / 39.1% CPU)</sub> | **0.81×** | **0.79×** |
| HTTP/2 · plain | HTTP/3 · QUIC | **29,912**<br><sub>(120 MiB / 52.2% CPU)</sub> | **29,619**<br><sub>(114 MiB / 54.7% CPU)</sub> | **1.01×** | **1×** |
| HTTP/2 · TLS | HTTP/1 · plain | **35,018**<br><sub>(102 MiB / 55.8% CPU)</sub> | **34,025**<br><sub>(103 MiB / 54.8% CPU)</sub> | **1×** | **0.97×** |
| HTTP/2 · TLS | HTTP/1 · TLS | **31,319**<br><sub>(103 MiB / 51.9% CPU)</sub> | **31,163**<br><sub>(104 MiB / 53% CPU)</sub> | **0.95×** | **0.95×** |
| HTTP/2 · TLS | HTTP/2 · plain | **72,603**<br><sub>(87 MiB / 41.7% CPU)</sub> | **70,407**<br><sub>(88 MiB / 42.6% CPU)</sub> | **0.74×** | **0.72×** |
| HTTP/2 · TLS | HTTP/2 · TLS | **68,254**<br><sub>(80 MiB / 39.7% CPU)</sub> | **64,195**<br><sub>(88 MiB / 39.1% CPU)</sub> | **0.82×** | **0.77×** |
| HTTP/2 · TLS | HTTP/3 · QUIC | **29,366**<br><sub>(133 MiB / 53.8% CPU)</sub> | **29,025**<br><sub>(137 MiB / 53.3% CPU)</sub> | **0.97×** | **0.96×** |
| HTTP/3 · QUIC | HTTP/1 · plain | **13,598**<br><sub>(110 MiB / 44.9% CPU)</sub> | **13,406**<br><sub>(111 MiB / 47% CPU)</sub> | **0.98×** | **0.96×** |
| HTTP/3 · QUIC | HTTP/1 · TLS | **12,981**<br><sub>(119 MiB / 46.7% CPU)</sub> | **12,231**<br><sub>(119 MiB / 46.5% CPU)</sub> | **0.94×** | **0.88×** |
| HTTP/3 · QUIC | HTTP/2 · plain | **32,478**<br><sub>(144 MiB / 48.3% CPU)</sub> | **30,700**<br><sub>(144 MiB / 47.8% CPU)</sub> | **0.96×** | **0.9×** |
| HTTP/3 · QUIC | HTTP/2 · TLS | **28,407**<br><sub>(132 MiB / 46.6% CPU)</sub> | **29,145**<br><sub>(136 MiB / 48.9% CPU)</sub> | **0.97×** | **0.99×** |
| HTTP/3 · QUIC | HTTP/3 · QUIC | **16,059**<br><sub>(118 MiB / 48.2% CPU)</sub> | **15,466**<br><sub>(119 MiB / 50.1% CPU)</sub> | **0.97×** | **0.94×** |

## Linux — Titanium vs nginx vs HAProxy vs Envoy vs YARP

### Reverse

Median of **3 repeats** on `ubuntu-latest` (4 vCPU / 16 GiB). Bare reverse 5×5 @ `062f4e72` — `compare-product` [36316337559](https://github.com/justcoding121/titanium-web-proxy/actions/runs/36316337559). Warmup 2s / measure 8s; concurrency 8, 16, 32, 64. **Linux nginx is the authoritative nginx baseline.** HAProxy (3.2 `USE_QUIC`) and Envoy (GitHub release, HTTP/3 compiled in) run on the same loopback shape as nginx/YARP. nginx terminate peers use `keepalive 256` + streaming buffers. The RPS workflow installs nginx.org mainline (`http_v3_module`), a QUIC-enabled HAProxy, Envoy, and `libmsquic`. Prefer ratios over absolute RPS. Product 5×5 is **~56-byte JSON keep-alive GET**; H2/H3 same-protocol cells are mostly header work with a tiny body (Titanium best case) — see [Why this comparison is fair](#why-this-comparison-is-fair).

| Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---:|---:|---:|---:|---:|
| HTTP/1 · plain | HTTP/1 · plain | **32,068**<br><sub>(84 MiB / 49.5% CPU)</sub> | 🥇 **38,046**<br><sub>(75 MiB / 41.5% CPU)</sub> | **35,850**<br><sub>(64 MiB / 43.6% CPU)</sub> | **19,688**<br><sub>(115 MiB / 61.8% CPU)</sub> | **27,600**<br><sub>(116 MiB / 50.6% CPU)</sub> |
| HTTP/1 · plain | HTTP/1 · TLS | **24,009**<br><sub>(106 MiB / 49.9% CPU)</sub> | 🥇 **28,186**<br><sub>(92 MiB / 42.3% CPU)</sub> | **27,418**<br><sub>(67 MiB / 44% CPU)</sub> | **16,904**<br><sub>(119 MiB / 59.8% CPU)</sub> | **21,523**<br><sub>(132 MiB / 50.7% CPU)</sub> |
| HTTP/1 · plain | HTTP/2 · plain | 🥇 **41,208**<br><sub>(128 MiB / 51% CPU)</sub> | *Not possible (no H2 upstream)* | **27,098**<br><sub>(66 MiB / 42.9% CPU)</sub> | **21,693**<br><sub>(115 MiB / 63% CPU)</sub> | **35,496**<br><sub>(127 MiB / 49.4% CPU)</sub> |
| HTTP/1 · plain | HTTP/2 · TLS | 🥇 **33,216**<br><sub>(145 MiB / 48.7% CPU)</sub> | *Not possible (no H2 upstream)* | **28,930**<br><sub>(67 MiB / 45.1% CPU)</sub> | **20,302**<br><sub>(117 MiB / 61.7% CPU)</sub> | **29,814**<br><sub>(133 MiB / 47.7% CPU)</sub> |
| HTTP/1 · plain | HTTP/3 · QUIC | 🥇 **23,771**<br><sub>(138 MiB / 53.4% CPU)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **10,592**<br><sub>(121 MiB / 58% CPU)</sub> | **21,179**<br><sub>(152 MiB / 49.3% CPU)</sub> |
| HTTP/1 · TLS | HTTP/1 · plain | **23,675**<br><sub>(109 MiB / 48.9% CPU)</sub> | **27,522**<br><sub>(100 MiB / 41.9% CPU)</sub> | 🥇 **27,772**<br><sub>(84 MiB / 43% CPU)</sub> | **17,158**<br><sub>(127 MiB / 58% CPU)</sub> | **20,024**<br><sub>(137 MiB / 50.1% CPU)</sub> |
| HTTP/1 · TLS | HTTP/1 · TLS | **19,957**<br><sub>(113 MiB / 48.4% CPU)</sub> | 🥇 **22,753**<br><sub>(102 MiB / 41.3% CPU)</sub> | **22,597**<br><sub>(85 MiB / 42.7% CPU)</sub> | **15,384**<br><sub>(128 MiB / 55.3% CPU)</sub> | **17,153**<br><sub>(142 MiB / 49.7% CPU)</sub> |
| HTTP/1 · TLS | HTTP/2 · plain | 🥇 **28,023**<br><sub>(136 MiB / 49.3% CPU)</sub> | *Not possible (no H2 upstream)* | **21,596**<br><sub>(83 MiB / 42.4% CPU)</sub> | **17,837**<br><sub>(126 MiB / 59% CPU)</sub> | **24,975**<br><sub>(148 MiB / 48.6% CPU)</sub> |
| HTTP/1 · TLS | HTTP/2 · TLS | 🥇 **24,510**<br><sub>(158 MiB / 48.6% CPU)</sub> | *Not possible (no H2 upstream)* | **23,302**<br><sub>(85 MiB / 42.9% CPU)</sub> | **17,241**<br><sub>(126 MiB / 57.8% CPU)</sub> | **21,822**<br><sub>(142 MiB / 47.8% CPU)</sub> |
| HTTP/1 · TLS | HTTP/3 · QUIC | 🥇 **18,206**<br><sub>(159 MiB / 52.2% CPU)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **9,802**<br><sub>(130 MiB / 54.9% CPU)</sub> | **16,477**<br><sub>(158 MiB / 49.6% CPU)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | 🥇 **36,705**<br><sub>(120 MiB / 52.6% CPU)</sub> | **16,202**<br><sub>(79 MiB / 19% CPU)</sub> | **23,717**<br><sub>(66 MiB / 24.5% CPU)</sub> | **14,147**<br><sub>(118 MiB / 23.2% CPU)</sub> | **33,698**<br><sub>(114 MiB / 49.8% CPU)</sub> |
| HTTP/2 · plain | HTTP/1 · TLS | 🥇 **28,024**<br><sub>(124 MiB / 51% CPU)</sub> | **12,736**<br><sub>(101 MiB / 19.6% CPU)</sub> | **18,554**<br><sub>(70 MiB / 24.6% CPU)</sub> | **13,012**<br><sub>(118 MiB / 23.5% CPU)</sub> | **25,354**<br><sub>(131 MiB / 50.3% CPU)</sub> |
| HTTP/2 · plain | HTTP/2 · plain | 🥇 **86,270**<br><sub>(90 MiB / 37.1% CPU)</sub> | *Not possible (no H2 upstream)* | **24,700**<br><sub>(68 MiB / 24.5% CPU)</sub> | **17,194**<br><sub>(117 MiB / 22.4% CPU)</sub> | **48,406**<br><sub>(123 MiB / 46.7% CPU)</sub> |
| HTTP/2 · plain | HTTP/2 · TLS | 🥇 **56,886**<br><sub>(94 MiB / 36.2% CPU)</sub> | *Not possible (no H2 upstream)* | **19,789**<br><sub>(67 MiB / 24.5% CPU)</sub> | **14,728**<br><sub>(117 MiB / 21.8% CPU)</sub> | **38,757**<br><sub>(124 MiB / 45.9% CPU)</sub> |
| HTTP/2 · plain | HTTP/3 · QUIC | 🥇 **28,329**<br><sub>(146 MiB / 50.2% CPU)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **4,312**<br><sub>(122 MiB / 24.9% CPU)</sub> | **26,399**<br><sub>(154 MiB / 47.2% CPU)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **33,902**<br><sub>(125 MiB / 52.1% CPU)</sub> | **14,946**<br><sub>(100 MiB / 19.3% CPU)</sub> | **20,553**<br><sub>(82 MiB / 24.6% CPU)</sub> | **14,134**<br><sub>(127 MiB / 22.8% CPU)</sub> | **29,098**<br><sub>(121 MiB / 49.8% CPU)</sub> |
| HTTP/2 · TLS | HTTP/1 · TLS | 🥇 **26,277**<br><sub>(117 MiB / 50.1% CPU)</sub> | **12,448**<br><sub>(110 MiB / 19.8% CPU)</sub> | **16,812**<br><sub>(85 MiB / 24.3% CPU)</sub> | **12,724**<br><sub>(127 MiB / 23.4% CPU)</sub> | **22,583**<br><sub>(127 MiB / 49.6% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | 🥇 **73,527**<br><sub>(99 MiB / 36.7% CPU)</sub> | *Not possible (no H2 upstream)* | **20,977**<br><sub>(82 MiB / 24.4% CPU)</sub> | **16,180**<br><sub>(126 MiB / 22.4% CPU)</sub> | **39,253**<br><sub>(123 MiB / 47% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **54,331**<br><sub>(103 MiB / 35.8% CPU)</sub> | *Not possible (no H2 upstream)* | **19,146**<br><sub>(83 MiB / 23.9% CPU)</sub> | **15,645**<br><sub>(126 MiB / 21.3% CPU)</sub> | **33,663**<br><sub>(134 MiB / 45.7% CPU)</sub> |
| HTTP/2 · TLS | HTTP/3 · QUIC | 🥇 **26,176**<br><sub>(151 MiB / 49.7% CPU)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **4,236**<br><sub>(129 MiB / 24.9% CPU)</sub> | **22,137**<br><sub>(159 MiB / 47.1% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | **20,745**<br><sub>(145 MiB / 49.6% CPU)</sub> | **0**<br><sub>(peak 15,282 · 107 MiB / 22.5% CPU)</sub> | 🥇 **22,949**<br><sub>(87 MiB / 26.9% CPU)</sub> | **4**<br><sub>(peak 6 · 131 MiB / 0.1% CPU)</sub> | **18,596**<br><sub>(182 MiB / 50.7% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | **16,503**<br><sub>(159 MiB / 47.5% CPU)</sub> | **0**<br><sub>(peak 11,690 · 116 MiB / 23.4% CPU)</sub> | 🥇 **17,675**<br><sub>(92 MiB / 29.4% CPU)</sub> | **4**<br><sub>(133 MiB / 0.1% CPU)</sub> | **15,235**<br><sub>(193 MiB / 51.4% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/2 · plain | 🥇 **28,820**<br><sub>(157 MiB / 53% CPU)</sub> | *Not possible (no H2 upstream)* | **24,574**<br><sub>(87 MiB / 24.3% CPU)</sub> | **401**<br><sub>(131 MiB / 2.2% CPU)</sub> | **23,176**<br><sub>(197 MiB / 48% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | 🥇 **25,281**<br><sub>(159 MiB / 50.2% CPU)</sub> | *Not possible (no H2 upstream)* | **21,116**<br><sub>(87 MiB / 25.4% CPU)</sub> | **2,043**<br><sub>(132 MiB / 10.6% CPU)</sub> | **21,210**<br><sub>(190 MiB / 47.5% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/3 · QUIC | 🥇 **20,534**<br><sub>(150 MiB / 46.3% CPU)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **2,414**<br><sub>(129 MiB / 24.9% CPU)</sub> | **15,716**<br><sub>(205 MiB / 47.9% CPU)</sub> |

### MITM (TWP only)

Same Client×Origin wires with interception on (`compare-product` [36316337559](https://github.com/justcoding121/titanium-web-proxy/actions/runs/36316337559)). **Lite** = no-op handlers (unchanged-lite finish). **Full** = append-only header mutation (harness: one probe header each way; product: generic append-only relay via `MitmCompressedRelayHelper`). nginx/HAProxy/Envoy/YARP cannot MITM. **Lite÷Reverse** / **Full÷Reverse** vs bare reverse (**same job / comparison-group shard**). Completion gate: Lite ≥ **0.50×** and Full ≥ **0.50×** reverse sustain @ c=64 (median of 3 GHA runs); reverse TWP÷YARP ≥ **0.60×** (no terminate-peer gate).

**v1 append-only relay (2026-08-27):** Pre-fix H2→H2 Full÷Reverse was **0.13–0.16×** ([32960766249](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32960766249)). Post-fix @ `df172718`: H2 plain→H2 plain Full **0.77–0.79×**, H3→H1 Full **0.91–0.93×**, all MITM arms ≥ **0.70×** on median of [33041445371](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33041445371), [33055267086](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33055267086), [33055272140](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33055272140).

**v2 drop-only + non-unique append (2026-08-27):** `MitmStaticRebuildHelper` rebuilds static HPACK/QPACK after 1–4 unique header drops; trailing non-unique appends stay on compressed relay. @ `062f4e72`: all MITM arms ≥ **0.70×** on GHA median ([33087088466](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087088466), [33087091622](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087091622), [33105885748](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33105885748) Linux; [33087085235](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087085235), [33087088466](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087088466), [33087091622](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087091622) Windows). H2 plain→H2 plain Full **0.77–0.79×** (Win) / **0.78×** (Lin).

| Client | Origin | Lite sustain | Full sustain | Lite÷Reverse | Full÷Reverse |
|---|---|---:|---:|---:|---:|
| HTTP/1 · plain | HTTP/1 · plain | **32,207**<br><sub>(93 MiB / 50.4% CPU)</sub> | **31,320**<br><sub>(96 MiB / 50.2% CPU)</sub> | **1×** | **0.98×** |
| HTTP/1 · plain | HTTP/1 · TLS | **23,973**<br><sub>(110 MiB / 50.6% CPU)</sub> | **23,791**<br><sub>(111 MiB / 50.1% CPU)</sub> | **1×** | **0.99×** |
| HTTP/1 · plain | HTTP/2 · plain | **39,553**<br><sub>(131 MiB / 52.9% CPU)</sub> | **39,540**<br><sub>(123 MiB / 53% CPU)</sub> | **0.96×** | **0.96×** |
| HTTP/1 · plain | HTTP/2 · TLS | **32,023**<br><sub>(148 MiB / 49.2% CPU)</sub> | **31,653**<br><sub>(150 MiB / 50% CPU)</sub> | **0.96×** | **0.95×** |
| HTTP/1 · plain | HTTP/3 · QUIC | **22,964**<br><sub>(139 MiB / 53.7% CPU)</sub> | **22,221**<br><sub>(137 MiB / 53.2% CPU)</sub> | **0.97×** | **0.93×** |
| HTTP/1 · TLS | HTTP/1 · plain | **23,979**<br><sub>(119 MiB / 50% CPU)</sub> | **23,363**<br><sub>(116 MiB / 50% CPU)</sub> | **1.01×** | **0.99×** |
| HTTP/1 · TLS | HTTP/1 · TLS | **19,942**<br><sub>(115 MiB / 49.2% CPU)</sub> | **19,522**<br><sub>(116 MiB / 49.4% CPU)</sub> | **1×** | **0.98×** |
| HTTP/1 · TLS | HTTP/2 · plain | **27,638**<br><sub>(142 MiB / 50.4% CPU)</sub> | **26,687**<br><sub>(144 MiB / 50.7% CPU)</sub> | **0.99×** | **0.95×** |
| HTTP/1 · TLS | HTTP/2 · TLS | **24,110**<br><sub>(174 MiB / 48.5% CPU)</sub> | **23,002**<br><sub>(167 MiB / 48.5% CPU)</sub> | **0.98×** | **0.94×** |
| HTTP/1 · TLS | HTTP/3 · QUIC | **18,019**<br><sub>(154 MiB / 52.9% CPU)</sub> | **17,943**<br><sub>(159 MiB / 53.1% CPU)</sub> | **0.99×** | **0.99×** |
| HTTP/2 · plain | HTTP/1 · plain | **37,433**<br><sub>(120 MiB / 55.3% CPU)</sub> | **37,065**<br><sub>(121 MiB / 54.1% CPU)</sub> | **1.02×** | **1.01×** |
| HTTP/2 · plain | HTTP/1 · TLS | **28,557**<br><sub>(126 MiB / 52.8% CPU)</sub> | **27,769**<br><sub>(124 MiB / 52.2% CPU)</sub> | **1.02×** | **0.99×** |
| HTTP/2 · plain | HTTP/2 · plain | **61,783**<br><sub>(94 MiB / 41.7% CPU)</sub> | **59,183**<br><sub>(95 MiB / 42.1% CPU)</sub> | **0.72×** | **0.69×** |
| HTTP/2 · plain | HTTP/2 · TLS | **47,663**<br><sub>(97 MiB / 39.6% CPU)</sub> | **44,383**<br><sub>(104 MiB / 39.9% CPU)</sub> | **0.84×** | **0.78×** |
| HTTP/2 · plain | HTTP/3 · QUIC | **27,938**<br><sub>(141 MiB / 50.8% CPU)</sub> | **27,561**<br><sub>(157 MiB / 51.1% CPU)</sub> | **0.99×** | **0.97×** |
| HTTP/2 · TLS | HTTP/1 · plain | **34,790**<br><sub>(121 MiB / 54.4% CPU)</sub> | **35,035**<br><sub>(130 MiB / 54.2% CPU)</sub> | **1.03×** | **1.03×** |
| HTTP/2 · TLS | HTTP/1 · TLS | **27,095**<br><sub>(124 MiB / 52% CPU)</sub> | **25,637**<br><sub>(125 MiB / 51.6% CPU)</sub> | **1.03×** | **0.98×** |
| HTTP/2 · TLS | HTTP/2 · plain | **55,669**<br><sub>(109 MiB / 41.2% CPU)</sub> | **50,722**<br><sub>(110 MiB / 40.8% CPU)</sub> | **0.76×** | **0.69×** |
| HTTP/2 · TLS | HTTP/2 · TLS | **45,000**<br><sub>(112 MiB / 40.3% CPU)</sub> | **42,724**<br><sub>(116 MiB / 39.9% CPU)</sub> | **0.83×** | **0.79×** |
| HTTP/2 · TLS | HTTP/3 · QUIC | **26,187**<br><sub>(153 MiB / 50.5% CPU)</sub> | **24,525**<br><sub>(155 MiB / 50.4% CPU)</sub> | **1×** | **0.94×** |
| HTTP/3 · QUIC | HTTP/1 · plain | **20,124**<br><sub>(147 MiB / 51% CPU)</sub> | **19,056**<br><sub>(144 MiB / 50.7% CPU)</sub> | **0.97×** | **0.92×** |
| HTTP/3 · QUIC | HTTP/1 · TLS | **15,859**<br><sub>(165 MiB / 48.6% CPU)</sub> | **15,618**<br><sub>(166 MiB / 48.4% CPU)</sub> | **0.96×** | **0.95×** |
| HTTP/3 · QUIC | HTTP/2 · plain | **28,275**<br><sub>(161 MiB / 53.2% CPU)</sub> | **27,317**<br><sub>(154 MiB / 52.6% CPU)</sub> | **0.98×** | **0.95×** |
| HTTP/3 · QUIC | HTTP/2 · TLS | **24,572**<br><sub>(158 MiB / 50.9% CPU)</sub> | **23,917**<br><sub>(157 MiB / 50.8% CPU)</sub> | **0.97×** | **0.95×** |
| HTTP/3 · QUIC | HTTP/3 · QUIC | **19,155**<br><sub>(161 MiB / 48.8% CPU)</sub> | **18,852**<br><sub>(155 MiB / 50.1% CPU)</sub> | **0.93×** | **0.92×** |

## macOS — Titanium vs nginx vs HAProxy vs Envoy vs YARP

### Reverse

Median of **3 repeats** on `macos-15-intel` (4-core / 14 GB). Bare reverse 5×5 @ `062f4e72` — `compare-product` [36316337559](https://github.com/justcoding121/titanium-web-proxy/actions/runs/36316337559). Warmup 2s / measure 8s; concurrency 8, 16, 32, 64. Prefer TWP÷peer ratios over absolute RPS. **RPS cells** include median RSS / CPU at the peak-RPS step as `<br><sub>(MiB / CPU%)</sub>`. The RPS workflow installs Homebrew nginx (`http_v3_module`), Homebrew HAProxy with `USE_QUIC` (3.2 source fallback), Envoy (Homebrew bottle or pinned darwin-amd64 1.36.7), Homebrew `libmsquic` (+ `DYLD_*`), and YARP. Do not publish from `macos-latest` (3-core / 7 GB). Product 5×5 is **~56-byte JSON keep-alive GET**; H2/H3 same-protocol cells are mostly header work with a tiny body (Titanium best case) — see [Why this comparison is fair](#why-this-comparison-is-fair).

| Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---:|---:|---:|---:|---:|
| HTTP/1 · plain | HTTP/1 · plain | 🥇 **14,150**<br><sub>(82 MiB / 36.6% CPU)</sub> | **12,354**<br><sub>(52 MiB / 18.6% CPU)</sub> | **13,344**<br><sub>(50 MiB / 29.3% CPU)</sub> | **5,172**<br><sub>(73 MiB / 38.8% CPU)</sub> | **12,251**<br><sub>(104 MiB / 36.2% CPU)</sub> |
| HTTP/1 · plain | HTTP/1 · TLS | **10,687**<br><sub>(93 MiB / 34.3% CPU)</sub> | **8,422**<br><sub>(peak 9,177 · 72 MiB / 24.3% CPU)</sub> | **10,881**<br><sub>(55 MiB / 32.9% CPU)</sub> | **3,574**<br><sub>(75 MiB / 22.6% CPU)</sub> | 🥇 **13,796**<br><sub>(121 MiB / 42.1% CPU)</sub> |
| HTTP/1 · plain | HTTP/2 · plain | 🥇 **27,265**<br><sub>(91 MiB / 42.4% CPU)</sub> | *Not possible (no H2 upstream)* | **14,486**<br><sub>(51 MiB / 29.5% CPU)</sub> | **11,178**<br><sub>(71 MiB / 44.8% CPU)</sub> | **26,944**<br><sub>(110 MiB / 40.3% CPU)</sub> |
| HTTP/1 · plain | HTTP/2 · TLS | **13,622**<br><sub>(129 MiB / 41.3% CPU)</sub> | *Not possible (no H2 upstream)* | **11,889**<br><sub>(52 MiB / 33.8% CPU)</sub> | **6,582**<br><sub>(73 MiB / 41.6% CPU)</sub> | 🥇 **16,313**<br><sub>(115 MiB / 36.5% CPU)</sub> |
| HTTP/1 · plain | HTTP/3 · QUIC | **4,657**<br><sub>(92 MiB / 47% CPU)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **3,714**<br><sub>(75 MiB / 39.7% CPU)</sub> | 🥇 **6,127**<br><sub>(109 MiB / 39.9% CPU)</sub> |
| HTTP/1 · TLS | HTTP/1 · plain | 🥇 **16,566**<br><sub>(94 MiB / 35.1% CPU)</sub> | **11,419**<br><sub>(73 MiB / 26% CPU)</sub> | **16,514**<br><sub>(66 MiB / 30.8% CPU)</sub> | **5,566**<br><sub>(79 MiB / 34.8% CPU)</sub> | **11,777**<br><sub>(114 MiB / 40.9% CPU)</sub> |
| HTTP/1 · TLS | HTTP/1 · TLS | **8,112**<br><sub>(97 MiB / 37.9% CPU)</sub> | **6,630**<br><sub>(83 MiB / 24.1% CPU)</sub> | 🥇 **14,160**<br><sub>(68 MiB / 36% CPU)</sub> | **0**<br><sub>(peak 3,615 · 80 MiB / 36.5% CPU)</sub> | **8,714**<br><sub>(176 MiB / 39.2% CPU)</sub> |
| HTTP/1 · TLS | HTTP/2 · plain | 🥇 **16,605**<br><sub>(100 MiB / 40.1% CPU)</sub> | *Not possible (no H2 upstream)* | **10,223**<br><sub>(66 MiB / 32.9% CPU)</sub> | **4,308**<br><sub>(80 MiB / 33.6% CPU)</sub> | **12,064**<br><sub>(118 MiB / 35.2% CPU)</sub> |
| HTTP/1 · TLS | HTTP/2 · TLS | **17,511**<br><sub>(163 MiB / 38.4% CPU)</sub> | *Not possible (no H2 upstream)* | **15,524**<br><sub>(66 MiB / 35.7% CPU)</sub> | **10,193**<br><sub>(79 MiB / 45.7% CPU)</sub> | 🥇 **21,692**<br><sub>(118 MiB / 37% CPU)</sub> |
| HTTP/1 · TLS | HTTP/3 · QUIC | **3,134**<br><sub>(111 MiB / 52.8% CPU)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **0**<br><sub>(peak 3,100 · 82 MiB / 40.6% CPU)</sub> | 🥇 **4,655**<br><sub>(127 MiB / 37% CPU)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | 🥇 **17,941**<br><sub>(91 MiB / 40.8% CPU)</sub> | **16,505**<br><sub>(58 MiB / 19.3% CPU)</sub> | **9,846**<br><sub>(56 MiB / 20.5% CPU)</sub> | **4,538**<br><sub>(74 MiB / 22.1% CPU)</sub> | **16,421**<br><sub>(105 MiB / 45% CPU)</sub> |
| HTTP/2 · plain | HTTP/1 · TLS | 🥇 **28,339**<br><sub>(95 MiB / 46.4% CPU)</sub> | **13,971**<br><sub>(86 MiB / 20.8% CPU)</sub> | **6,975**<br><sub>(60 MiB / 20.7% CPU)</sub> | **4,129**<br><sub>(78 MiB / 20.9% CPU)</sub> | **17,112**<br><sub>(115 MiB / 45.6% CPU)</sub> |
| HTTP/2 · plain | HTTP/2 · plain | 🥇 **33,032**<br><sub>(73 MiB / 30.3% CPU)</sub> | *Not possible (no H2 upstream)* | **9,792**<br><sub>(56 MiB / 21% CPU)</sub> | **6,021**<br><sub>(71 MiB / 21.7% CPU)</sub> | **22,659**<br><sub>(109 MiB / 40.5% CPU)</sub> |
| HTTP/2 · plain | HTTP/2 · TLS | 🥇 **35,626**<br><sub>(77 MiB / 32.1% CPU)</sub> | *Not possible (no H2 upstream)* | **9,098**<br><sub>(57 MiB / 21.5% CPU)</sub> | **7,362**<br><sub>(73 MiB / 21.3% CPU)</sub> | **25,202**<br><sub>(116 MiB / 43.4% CPU)</sub> |
| HTTP/2 · plain | HTTP/3 · QUIC | **5,286**<br><sub>(97 MiB / 53.2% CPU)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **2,494**<br><sub>(74 MiB / 23.6% CPU)</sub> | 🥇 **8,668**<br><sub>(110 MiB / 38.8% CPU)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **15,215**<br><sub>(91 MiB / 42.8% CPU)</sub> | **9,248**<br><sub>(73 MiB / 17.5% CPU)</sub> | **6,806**<br><sub>(67 MiB / 20% CPU)</sub> | **3,693**<br><sub>(82 MiB / 22.9% CPU)</sub> | **14,538**<br><sub>(113 MiB / 44.5% CPU)</sub> |
| HTTP/2 · TLS | HTTP/1 · TLS | 🥇 **17,010**<br><sub>(97 MiB / 48% CPU)</sub> | **12,186**<br><sub>(92 MiB / 20.3% CPU)</sub> | **8,640**<br><sub>(68 MiB / 21.2% CPU)</sub> | **4,904**<br><sub>(83 MiB / 21.8% CPU)</sub> | **14,144**<br><sub>(121 MiB / 45.2% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | 🥇 **49,129**<br><sub>(82 MiB / 32% CPU)</sub> | *Not possible (no H2 upstream)* | **10,414**<br><sub>(68 MiB / 21.6% CPU)</sub> | **6,679**<br><sub>(79 MiB / 21.6% CPU)</sub> | **32,155**<br><sub>(113 MiB / 42.4% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **28,784**<br><sub>(84 MiB / 32.8% CPU)</sub> | *Not possible (no H2 upstream)* | **6,644**<br><sub>(69 MiB / 20.8% CPU)</sub> | **5,631**<br><sub>(79 MiB / 21.5% CPU)</sub> | **19,311**<br><sub>(118 MiB / 41.4% CPU)</sub> |
| HTTP/2 · TLS | HTTP/3 · QUIC | **7,362**<br><sub>(106 MiB / 47.7% CPU)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **3,153**<br><sub>(81 MiB / 23.9% CPU)</sub> | 🥇 **8,068**<br><sub>(120 MiB / 39.2% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | **5,820**<br><sub>(101 MiB / 45.8% CPU)</sub> | **0**<br><sub>(peak 8,300 · 63 MiB / 12.8% CPU)</sub> | 🥇 **8,718**<br><sub>(66 MiB / 23.7% CPU)</sub> | **1,878**<br><sub>(87 MiB / 39.8% CPU)</sub> | **7,161**<br><sub>(173 MiB / 40.2% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | **4,414**<br><sub>(119 MiB / 46.4% CPU)</sub> | **0**<br><sub>(peak 4,195 · 66 MiB / 15.3% CPU)</sub> | 🥇 **5,497**<br><sub>(69 MiB / 24.1% CPU)</sub> | **1,014**<br><sub>(peak 1,129 · 87 MiB / 24.7% CPU)</sub> | **4,267**<br><sub>(203 MiB / 39.9% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/2 · plain | **6,645**<br><sub>(98 MiB / 48.6% CPU)</sub> | *Not possible (no H2 upstream)* | 🥇 **9,410**<br><sub>(66 MiB / 24.5% CPU)</sub> | **2,112**<br><sub>(83 MiB / 23.8% CPU)</sub> | **8,572**<br><sub>(184 MiB / 38.5% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | **5,759**<br><sub>(106 MiB / 46% CPU)</sub> | *Not possible (no H2 upstream)* | 🥇 **8,831**<br><sub>(68 MiB / 26.6% CPU)</sub> | **2,856**<br><sub>(84 MiB / 23.9% CPU)</sub> | **7,749**<br><sub>(165 MiB / 40.2% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/3 · QUIC | **3,310**<br><sub>(97 MiB / 45.1% CPU)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **717**<br><sub>(peak 985 · 82 MiB / 27.9% CPU)</sub> | 🥇 **4,356**<br><sub>(174 MiB / 37.8% CPU)</sub> |

### MITM (TWP only)

Same Client×Origin wires with interception on (`compare-product` [36316337559](https://github.com/justcoding121/titanium-web-proxy/actions/runs/36316337559)). **Lite** = no-op handlers (unchanged-lite finish). **Full** = append-only header mutation (harness: one probe header each way; product: generic append-only relay via `MitmCompressedRelayHelper`). nginx/HAProxy/Envoy/YARP cannot MITM. **Lite÷Reverse** / **Full÷Reverse** vs bare reverse (**same job / comparison-group shard**). Completion gate: Lite ≥ **0.50×** and Full ≥ **0.50×** reverse sustain @ c=64 (median of 3 GHA runs); reverse TWP÷YARP ≥ **0.60×** (no terminate-peer gate).

**v1 append-only relay (2026-08-27):** Pre-fix H2→H2 Full÷Reverse was **0.13–0.16×** ([32960766249](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32960766249)). Post-fix @ `df172718`: H2 plain→H2 plain Full **0.77–0.79×**, H3→H1 Full **0.91–0.93×**, all MITM arms ≥ **0.70×** on median of [33041445371](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33041445371), [33055267086](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33055267086), [33055272140](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33055272140).

**v2 drop-only + non-unique append (2026-08-27):** `MitmStaticRebuildHelper` rebuilds static HPACK/QPACK after 1–4 unique header drops; trailing non-unique appends stay on compressed relay. @ `062f4e72`: all MITM arms ≥ **0.70×** on GHA median ([33087088466](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087088466), [33087091622](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087091622), [33105885748](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33105885748) Linux; [33087085235](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087085235), [33087088466](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087088466), [33087091622](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087091622) Windows). H2 plain→H2 plain Full **0.77–0.79×** (Win) / **0.78×** (Lin).

| Client | Origin | Lite sustain | Full sustain | Lite÷Reverse | Full÷Reverse |
|---|---|---:|---:|---:|---:|
| HTTP/1 · plain | HTTP/1 · plain | **20,198**<br><sub>(83 MiB / 39.4% CPU)</sub> | **13,154**<br><sub>(84 MiB / 36% CPU)</sub> | **1.43×** | **0.93×** |
| HTTP/1 · plain | HTTP/1 · TLS | **11,466**<br><sub>(94 MiB / 40.8% CPU)</sub> | **11,390**<br><sub>(94 MiB / 39.6% CPU)</sub> | **1.07×** | **1.07×** |
| HTTP/1 · plain | HTTP/2 · plain | **20,822**<br><sub>(102 MiB / 43.9% CPU)</sub> | **27,705**<br><sub>(94 MiB / 44.2% CPU)</sub> | **0.76×** | **1.02×** |
| HTTP/1 · plain | HTTP/2 · TLS | **18,022**<br><sub>(108 MiB / 41.5% CPU)</sub> | **14,550**<br><sub>(112 MiB / 44.2% CPU)</sub> | **1.32×** | **1.07×** |
| HTTP/1 · plain | HTTP/3 · QUIC | **5,347**<br><sub>(91 MiB / 50% CPU)</sub> | **4,294**<br><sub>(121 MiB / 49.1% CPU)</sub> | **1.15×** | **0.92×** |
| HTTP/1 · TLS | HTTP/1 · plain | **12,517**<br><sub>(95 MiB / 38.3% CPU)</sub> | **18,376**<br><sub>(97 MiB / 38.8% CPU)</sub> | **0.76×** | **1.11×** |
| HTTP/1 · TLS | HTTP/1 · TLS | **12,612**<br><sub>(123 MiB / 39.7% CPU)</sub> | **9,706**<br><sub>(98 MiB / 38.5% CPU)</sub> | **1.55×** | **1.2×** |
| HTTP/1 · TLS | HTTP/2 · plain | **10,790**<br><sub>(99 MiB / 40.2% CPU)</sub> | **9,126**<br><sub>(106 MiB / 39.3% CPU)</sub> | **0.65×** | **0.55×** |
| HTTP/1 · TLS | HTTP/2 · TLS | **11,012**<br><sub>(162 MiB / 39.7% CPU)</sub> | **12,692**<br><sub>(143 MiB / 40.1% CPU)</sub> | **0.63×** | **0.72×** |
| HTTP/1 · TLS | HTTP/3 · QUIC | **3,725**<br><sub>(98 MiB / 51.7% CPU)</sub> | **3,651**<br><sub>(100 MiB / 54.7% CPU)</sub> | **1.19×** | **1.17×** |
| HTTP/2 · plain | HTTP/1 · plain | **20,944**<br><sub>(88 MiB / 46.2% CPU)</sub> | **18,267**<br><sub>(91 MiB / 46.9% CPU)</sub> | **1.17×** | **1.02×** |
| HTTP/2 · plain | HTTP/1 · TLS | **17,904**<br><sub>(98 MiB / 45.8% CPU)</sub> | **24,934**<br><sub>(96 MiB / 46.3% CPU)</sub> | **0.63×** | **0.88×** |
| HTTP/2 · plain | HTTP/2 · plain | **35,358**<br><sub>(78 MiB / 35.8% CPU)</sub> | **31,757**<br><sub>(76 MiB / 35.7% CPU)</sub> | **1.07×** | **0.96×** |
| HTTP/2 · plain | HTTP/2 · TLS | **27,471**<br><sub>(81 MiB / 36.3% CPU)</sub> | **26,425**<br><sub>(81 MiB / 36.6% CPU)</sub> | **0.77×** | **0.74×** |
| HTTP/2 · plain | HTTP/3 · QUIC | **5,955**<br><sub>(97 MiB / 54% CPU)</sub> | **7,529**<br><sub>(96 MiB / 49% CPU)</sub> | **1.13×** | **1.42×** |
| HTTP/2 · TLS | HTTP/1 · plain | **23,200**<br><sub>(94 MiB / 47.2% CPU)</sub> | **17,408**<br><sub>(94 MiB / 44.6% CPU)</sub> | **1.52×** | **1.14×** |
| HTTP/2 · TLS | HTTP/1 · TLS | **12,256**<br><sub>(96 MiB / 46.8% CPU)</sub> | **13,481**<br><sub>(96 MiB / 46.9% CPU)</sub> | **0.72×** | **0.79×** |
| HTTP/2 · TLS | HTTP/2 · plain | **33,914**<br><sub>(88 MiB / 37.8% CPU)</sub> | **31,335**<br><sub>(86 MiB / 37.7% CPU)</sub> | **0.69×** | **0.64×** |
| HTTP/2 · TLS | HTTP/2 · TLS | **36,476**<br><sub>(91 MiB / 38.2% CPU)</sub> | **28,290**<br><sub>(89 MiB / 37.7% CPU)</sub> | **1.27×** | **0.98×** |
| HTTP/2 · TLS | HTTP/3 · QUIC | **4,809**<br><sub>(109 MiB / 48.4% CPU)</sub> | **5,645**<br><sub>(107 MiB / 47.2% CPU)</sub> | **0.65×** | **0.77×** |
| HTTP/3 · QUIC | HTTP/1 · plain | **5,665**<br><sub>(102 MiB / 44.3% CPU)</sub> | **5,714**<br><sub>(104 MiB / 44.7% CPU)</sub> | **0.97×** | **0.98×** |
| HTTP/3 · QUIC | HTTP/1 · TLS | **3,710**<br><sub>(116 MiB / 45.3% CPU)</sub> | **5,485**<br><sub>(117 MiB / 45.8% CPU)</sub> | **0.84×** | **1.24×** |
| HTTP/3 · QUIC | HTTP/2 · plain | **5,469**<br><sub>(99 MiB / 42.6% CPU)</sub> | **5,520**<br><sub>(96 MiB / 44.1% CPU)</sub> | **0.82×** | **0.83×** |
| HTTP/3 · QUIC | HTTP/2 · TLS | **8,152**<br><sub>(105 MiB / 46.9% CPU)</sub> | **7,060**<br><sub>(104 MiB / 44.8% CPU)</sub> | **1.42×** | **1.23×** |
| HTTP/3 · QUIC | HTTP/3 · QUIC | **3,096**<br><sub>(94 MiB / 38.9% CPU)</sub> | **3,910**<br><sub>(96 MiB / 42.9% CPU)</sub> | **0.94×** | **1.18×** |

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

Same runners and harness as the tiny-GET tables, but with larger bodies, POST, lossy links, TLS cost, and architecture-sensitive paths (slow consumer / early response / duplex). **PUT with the same body is the same proxy work as POST; DELETE with no body matches GET** — only POST is published. Bodies/POST/lossy stay **half-duplex**. Laptop numbers are on [Performance Local Lab](Performance-Local-Lab#architecture-sensitive). How maintainers refresh these tables (modes, shards, paste scripts) is under [Maintainer notes](#maintainer-notes). **Larger-body check:** 64 / 256 KiB H2 TLS→H2 TLS is where body copy dominates headers — ratios vs YARP are in the tables below (@ `062f4e72`), unlike the tiny-GET H2↔H2 medals above.

Lossy link = **userspace** delay/drop shim (not kernel `netem`): TCP gets per-buffer delay + occasional whole-connection stalls (honest head-of-line for multiplexed HTTP/2); UDP is **loss% only** (no per-datagram delay) + drops (QUIC / MsQuic-safe). Lossy tables publish HTTP/1, HTTP/2, and HTTP/3.

### Windows — heavier reverse GET (64 KiB / 256 KiB)

Median of **3** repeats on `windows-latest` @ `062f4e72`. Source: Actions [36315367905](https://github.com/justcoding121/titanium-web-proxy/actions/runs/36315367905) (`compare-bodies`). Warmup 2s / measure 8s. **RPS cells** include `(MiB / CPU%)` footprints.

*Not possible:* **HAProxy** and **Envoy** columns are omitted (no official Windows port).









| Body | Client | Origin | TWP | nginx | YARP |
|---|---|---|---:|---:|---:|
| 64 KiB | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **9,238**<br><sub>(93 MiB / 44.9% CPU)</sub> | **614**<br><sub>(142 MiB / 24.7% CPU)</sub> | **8,008**<br><sub>(135 MiB / 49.3% CPU)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **11,528**<br><sub>(188 MiB / 41.8% CPU)</sub> | **765**<br><sub>(142 MiB / 24.8% CPU)</sub> | **9,493**<br><sub>(131 MiB / 49.1% CPU)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **4,074**<br><sub>(136 MiB / 40.1% CPU)</sub> | *Not possible (no QUIC)* | **3,846**<br><sub>(191 MiB / 48.5% CPU)</sub> |
| 256 KiB | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **2,772**<br><sub>(128 MiB / 48.2% CPU)</sub> | **167**<br><sub>(142 MiB / 24.8% CPU)</sub> | **2,580**<br><sub>(136 MiB / 47.9% CPU)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **3,456**<br><sub>(152 MiB / 36.6% CPU)</sub> | **205**<br><sub>(142 MiB / 24.9% CPU)</sub> | **2,459**<br><sub>(134 MiB / 43.1% CPU)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **1,150**<br><sub>(108 MiB / 39.9% CPU)</sub> | *Not possible (no QUIC)* | **1,104**<br><sub>(173 MiB / 43.5% CPU)</sub> |
| 64 KiB | HTTP/2 · plain | HTTP/1 · plain | 🥇 **15,706**<br><sub>(164 MiB / 40.8% CPU)</sub> | **3,757**<br><sub>(127 MiB / 24.6% CPU)</sub> | **13,694**<br><sub>(109 MiB / 43.7% CPU)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/2 · plain | 🥇 **9,703**<br><sub>(120 MiB / 38.5% CPU)</sub> | *Not possible* | **6,800**<br><sub>(139 MiB / 50.5% CPU)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **10,325**<br><sub>(106 MiB / 37.8% CPU)</sub> | *Not possible* | **8,046**<br><sub>(132 MiB / 48.3% CPU)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/2 · TLS | **3,407**<br><sub>(121 MiB / 44.0% CPU)</sub> | *Not possible* | 🥇 **3,587**<br><sub>(187 MiB / 48.2% CPU)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **5,999**<br><sub>(157 MiB / 41.6% CPU)</sub> | *Not possible (no QUIC)* | **4,431**<br><sub>(199 MiB / 48.1% CPU)</sub> |
| 256 KiB | HTTP/2 · plain | HTTP/1 · plain | 🥇 **4,557**<br><sub>(146 MiB / 30.6% CPU)</sub> | **1,162**<br><sub>(127 MiB / 24.2% CPU)</sub> | **3,575**<br><sub>(124 MiB / 33.5% CPU)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/2 · plain | 🥇 **2,301**<br><sub>(149 MiB / 32.9% CPU)</sub> | *Not possible* | **1,688**<br><sub>(168 MiB / 46.7% CPU)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **2,334**<br><sub>(139 MiB / 33.3% CPU)</sub> | *Not possible* | **1,863**<br><sub>(146 MiB / 41.9% CPU)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/2 · TLS | **880**<br><sub>(148 MiB / 42.7% CPU)</sub> | *Not possible* | 🥇 **970**<br><sub>(184 MiB / 43.1% CPU)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **1,403**<br><sub>(147 MiB / 40.6% CPU)</sub> | *Not possible (no QUIC)* | **1,202**<br><sub>(198 MiB / 45.5% CPU)</sub> |

nginx/Windows collapses on large reverse bodies in this harness; treat as same-OS only. H1 TLS **64 KiB** ≈ **1.13×** YARP; **256 KiB** ≈ **1.08×**. H2→H1 64 KiB ≈ **1.17×**; H3→H1 64 KiB ≈ **1.06×**.

### Linux — heavier reverse GET (64 KiB / 256 KiB)

Median of **3** repeats @ `062f4e72`. Source: Actions [36315367905](https://github.com/justcoding121/titanium-web-proxy/actions/runs/36315367905) (`compare-bodies`). Warmup 2s / measure 8s.

| Body | Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---|---:|---:|---:|---:|---:|
| 64 KiB | HTTP/1 · TLS | HTTP/1 · plain | **13,638**<br><sub>(124 MiB / 40.8% CPU)</sub> | **9,832**<br><sub>(101 MiB / 44.7% CPU)</sub> | 🥇 **15,569**<br><sub>(85 MiB / 36.2% CPU)</sub> | **12,525**<br><sub>(132 MiB / 44.2% CPU)</sub> | **10,469**<br><sub>(169 MiB / 47.6% CPU)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **5,576**<br><sub>(213 MiB / 39.6% CPU)</sub> | **1,910**<br><sub>(101 MiB / 19.1% CPU)</sub> | **4,757**<br><sub>(84 MiB / 24.0% CPU)</sub> | **4,513**<br><sub>(140 MiB / 23.5% CPU)</sub> | **4,837**<br><sub>(162 MiB / 47.2% CPU)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **7,830**<br><sub>(193 MiB / 43.2% CPU)</sub> | **2,365**<br><sub>(103 MiB / 16.1% CPU)</sub> | **6,357**<br><sub>(90 MiB / 28.8% CPU)</sub> | **26**<br><sub>(139 MiB / 0.3% CPU)</sub> | **5,265**<br><sub>(232 MiB / 52.2% CPU)</sub> |
| 256 KiB | HTTP/1 · TLS | HTTP/1 · plain | **4,129**<br><sub>(128 MiB / 31.4% CPU)</sub> | **2,797**<br><sub>(100 MiB / 42.4% CPU)</sub> | 🥇 **4,298**<br><sub>(83 MiB / 26.5% CPU)</sub> | **3,746**<br><sub>(146 MiB / 31.9% CPU)</sub> | **3,045**<br><sub>(167 MiB / 42.7% CPU)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/1 · plain | **1,499**<br><sub>(221 MiB / 35.7% CPU)</sub> | **573**<br><sub>(100 MiB / 24.3% CPU)</sub> | **1,743**<br><sub>(84 MiB / 22.5% CPU)</sub> | 🥇 **1,838**<br><sub>(160 MiB / 20.6% CPU)</sub> | **1,375**<br><sub>(160 MiB / 44.8% CPU)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **1,886**<br><sub>(162 MiB / 41.0% CPU)</sub> | **563**<br><sub>(113 MiB / 15.4% CPU)</sub> | **1,778**<br><sub>(91 MiB / 28.6% CPU)</sub> | **1,068**<br><sub>(158 MiB / 19.9% CPU)</sub> | **1,546**<br><sub>(224 MiB / 47.9% CPU)</sub> |
| 64 KiB | HTTP/2 · plain | HTTP/1 · plain | 🥇 **10,124**<br><sub>(238 MiB / 43.5% CPU)</sub> | **2,934**<br><sub>(80 MiB / 15.8% CPU)</sub> | **7,425**<br><sub>(70 MiB / 24.6% CPU)</sub> | **6,485**<br><sub>(133 MiB / 24.0% CPU)</sub> | **8,318**<br><sub>(155 MiB / 46.3% CPU)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/2 · plain | 🥇 **8,568**<br><sub>(167 MiB / 32.3% CPU)</sub> | *Not possible* | **4,779**<br><sub>(86 MiB / 23.8% CPU)</sub> | **7,639**<br><sub>(143 MiB / 22.2% CPU)</sub> | **7,274**<br><sub>(184 MiB / 47.2% CPU)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **4,110**<br><sub>(139 MiB / 39.5% CPU)</sub> | *Not possible* | **1,589**<br><sub>(83 MiB / 24.6% CPU)</sub> | **3,269**<br><sub>(149 MiB / 22.8% CPU)</sub> | **3,347**<br><sub>(175 MiB / 45.5% CPU)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/2 · TLS | **3,909**<br><sub>(171 MiB / 53.9% CPU)</sub> | *Not possible* | **4,171**<br><sub>(92 MiB / 32.8% CPU)</sub> | **30**<br><sub>(143 MiB / 0.3% CPU)</sub> | 🥇 **4,201**<br><sub>(244 MiB / 49.4% CPU)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **3,948**<br><sub>(198 MiB / 43.8% CPU)</sub> | **1,462**<br><sub>(peak 1,482 · 113 MiB / 22.4% CPU)</sub> | **3,141**<br><sub>(94 MiB / 31.2% CPU)</sub> | **42**<br><sub>(139 MiB / 0.6% CPU)</sub> | **3,184**<br><sub>(235 MiB / 49.6% CPU)</sub> |
| 256 KiB | HTTP/2 · plain | HTTP/1 · plain | **2,870**<br><sub>(195 MiB / 35.7% CPU)</sub> | **989**<br><sub>(79 MiB / 20.2% CPU)</sub> | **2,822**<br><sub>(69 MiB / 22.7% CPU)</sub> | 🥇 **3,044**<br><sub>(154 MiB / 21.8% CPU)</sub> | **2,907**<br><sub>(155 MiB / 38.4% CPU)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/2 · plain | 🥇 **2,544**<br><sub>(173 MiB / 31.8% CPU)</sub> | *Not possible* | **1,421**<br><sub>(92 MiB / 23.6% CPU)</sub> | **2,307**<br><sub>(166 MiB / 22.4% CPU)</sub> | **1,886**<br><sub>(194 MiB / 41.8% CPU)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **1,133**<br><sub>(160 MiB / 36.1% CPU)</sub> | *Not possible* | **430**<br><sub>(83 MiB / 24.5% CPU)</sub> | **980**<br><sub>(158 MiB / 22.2% CPU)</sub> | **945**<br><sub>(183 MiB / 41.7% CPU)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/2 · TLS | **1,034**<br><sub>(190 MiB / 51.7% CPU)</sub> | *Not possible* | **1,068**<br><sub>(96 MiB / 31.7% CPU)</sub> | **884**<br><sub>(152 MiB / 21.4% CPU)</sub> | 🥇 **1,247**<br><sub>(243 MiB / 46.7% CPU)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **1,122**<br><sub>(179 MiB / 44.4% CPU)</sub> | **367**<br><sub>(120 MiB / 23.9% CPU)</sub> | **896**<br><sub>(96 MiB / 32.1% CPU)</sub> | **685**<br><sub>(147 MiB / 22.0% CPU)</sub> | **1,000**<br><sub>(248 MiB / 47.8% CPU)</sub> |

On this GHA pass TWP÷YARP H1 TLS ≈ **1.21×** (64 KiB) / **1.26×** (256 KiB); H2→H1 ≈ **1.20×** / **1.15×**; H3→H1 ≈ **1.29×** / **1.16×**. TWP÷nginx H1 TLS ≈ **1.43** / **1.85**. Absolute RPS swings by VM; prefer ratios.

### Windows — POST 64 KiB request + 64 KiB response

Median of **3** repeats on `windows-latest` @ `062f4e72`. Source: Actions [36315371509](https://github.com/justcoding121/titanium-web-proxy/actions/runs/36315371509) (`compare-post`).

*Not possible:* **HAProxy** and **Envoy** columns are omitted (no official Windows port).









| Client | Origin | TWP | nginx | YARP |
|---|---|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | 🥇 **4,512**<br><sub>(97 MiB / 41.8% CPU)</sub> | **320**<br><sub>(143 MiB / 24.9% CPU)</sub> | **3,092**<br><sub>(137 MiB / 56.3% CPU)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **3,099**<br><sub>(181 MiB / 45.6% CPU)</sub> | **325**<br><sub>(144 MiB / 24.8% CPU)</sub> | **2,667**<br><sub>(135 MiB / 52.5% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | **1,390**<br><sub>(171 MiB / 40.1% CPU)</sub> | *Not possible (no QUIC)* | 🥇 **1,491**<br><sub>(216 MiB / 48.4% CPU)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | 🥇 **5,097**<br><sub>(174 MiB / 41.1% CPU)</sub> | **1,226**<br><sub>(130 MiB / 24.6% CPU)</sub> | **4,378**<br><sub>(123 MiB / 50.0% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | 🥇 **3,858**<br><sub>(118 MiB / 36.0% CPU)</sub> | *Not possible* | **2,750**<br><sub>(145 MiB / 52.1% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **2,734**<br><sub>(124 MiB / 36.2% CPU)</sub> | *Not possible* | **2,109**<br><sub>(142 MiB / 48.4% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | **1,363**<br><sub>(184 MiB / 45.2% CPU)</sub> | *Not possible* | 🥇 **1,456**<br><sub>(214 MiB / 47.3% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | **1,387**<br><sub>(185 MiB / 41.5% CPU)</sub> | *Not possible (no QUIC)* | 🥇 **1,407**<br><sub>(205 MiB / 46.9% CPU)</sub> |

TWP leads H1 POST (~**1.4×** YARP), H2→H1 POST (~**1.5×** YARP), and H3 POST (~**1.05×** YARP). H2 TLS→H2 TLS POST sustain is healthy on this pass (@ `062f4e72`).

### Linux — POST 64 KiB request + 64 KiB response

Median of **3** repeats @ `062f4e72`. Source: Actions [36315371509](https://github.com/justcoding121/titanium-web-proxy/actions/runs/36315371509) (`compare-post`).

| Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---:|---:|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | **4,695**<br><sub>(131 MiB / 45.2% CPU)</sub> | **3,658**<br><sub>(100 MiB / 49.4% CPU)</sub> | 🥇 **5,000**<br><sub>(83 MiB / 40.8% CPU)</sub> | **4,992**<br><sub>(133 MiB / 41.7% CPU)</sub> | **3,218**<br><sub>(171 MiB / 55.5% CPU)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **2,952**<br><sub>(209 MiB / 44.9% CPU)</sub> | **1,504**<br><sub>(112 MiB / 24.1% CPU)</sub> | **1,763**<br><sub>(84 MiB / 23.3% CPU)</sub> | **0**<br><sub>(peak 2,700 · 143 MiB / 20.7% CPU)</sub> | **2,512**<br><sub>(163 MiB / 48.1% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **2,902**<br><sub>(223 MiB / 44.2% CPU)</sub> | **450**<br><sub>(110 MiB / 24.9% CPU)</sub> | **1,787**<br><sub>(92 MiB / 31.3% CPU)</sub> | **0**<br><sub>(126 MiB / 0.1% CPU)</sub> | **2,618**<br><sub>(241 MiB / 49.8% CPU)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | 🥇 **5,603**<br><sub>(239 MiB / 44.8% CPU)</sub> | **2,477**<br><sub>(97 MiB / 24.5% CPU)</sub> | **2,584**<br><sub>(67 MiB / 23.2% CPU)</sub> | **3,999**<br><sub>(peak 4,644 · 120 MiB / 21.8% CPU)</sub> | **4,436**<br><sub>(160 MiB / 48.8% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | 🥇 **3,252**<br><sub>(160 MiB / 41.1% CPU)</sub> | *Not possible* | **1,328**<br><sub>(87 MiB / 24.1% CPU)</sub> | **2,887**<br><sub>(143 MiB / 23.4% CPU)</sub> | **2,417**<br><sub>(185 MiB / 47.1% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **2,336**<br><sub>(151 MiB / 40.5% CPU)</sub> | *Not possible* | **1,107**<br><sub>(86 MiB / 24.4% CPU)</sub> | **2,037**<br><sub>(151 MiB / 23.0% CPU)</sub> | **1,912**<br><sub>(175 MiB / 46.1% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | 🥇 **1,908**<br><sub>(232 MiB / 49.6% CPU)</sub> | *Not possible* | **1,139**<br><sub>(95 MiB / 31.5% CPU)</sub> | **0**<br><sub>(126 MiB / 0.1% CPU)</sub> | **1,801**<br><sub>(251 MiB / 47.1% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **2,190**<br><sub>(240 MiB / 44.9% CPU)</sub> | **374**<br><sub>(119 MiB / 25.0% CPU)</sub> | **1,685**<br><sub>(97 MiB / 36.1% CPU)</sub> | **0**<br><sub>(126 MiB / 0.1% CPU)</sub> | **1,974**<br><sub>(273 MiB / 48.3% CPU)</sub> |

Linux nginx H1/H2/H3 POST completed (nginx.org mainline). TWP÷YARP H1 ≈ **1.5×**; H2→H1 ≈ **1.2×**; H3 ≈ **1.1×**. TWP÷nginx H3 ≈ **6×**.

### Windows — lossy / high-RTT (H2 HOL / H3 loss)

Userspace **5 ms** one-way delay + **1%** TCP connection stall (H1/H2); UDP is **loss% only** (no per-datagram delay; MsQuic-safe) + **1%** datagram drop (H3); **64 KiB** GET. Median of **3** repeats on `windows-latest` @ `062f4e72` — [36315373597](https://github.com/justcoding121/titanium-web-proxy/actions/runs/36315373597) (`compare-lossy`).

*Not possible:* **HAProxy** and **Envoy** columns are omitted (no official Windows port).

| Client | Origin | TWP | nginx | YARP |
|---|---|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | 🥇 **662**<br><sub>(87 MiB / 4.3% CPU)</sub> | **637**<br><sub>(142 MiB / 20.4% CPU)</sub> | **662**<br><sub>(111 MiB / 5.6% CPU)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **61**<br><sub>(peak 87 · 113 MiB / 1.5% CPU)</sub> | **18**<br><sub>(142 MiB / 0.8% CPU)</sub> | **16**<br><sub>(85 MiB / 0.8% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | **898**<br><sub>(127 MiB / 16.5% CPU)</sub> | *Not possible (no QUIC)* | 🥇 **1,111**<br><sub>(184 MiB / 30.0% CPU)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | 🥇 **60**<br><sub>(peak 88 · 101 MiB / 1.9% CPU)</sub> | **18**<br><sub>(128 MiB / 0.1% CPU)</sub> | **18**<br><sub>(77 MiB / 0.9% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | **16**<br><sub>(77 MiB / 0.8% CPU)</sub> | *Not possible* | 🥇 **16**<br><sub>(84 MiB / 1.1% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | **16**<br><sub>(78 MiB / 0.9% CPU)</sub> | *Not possible* | 🥇 **16**<br><sub>(85 MiB / 1.2% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | **871**<br><sub>(138 MiB / 31.5% CPU)</sub> | *Not possible* | 🥇 **1,023**<br><sub>(192 MiB / 30.8% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | **892**<br><sub>(128 MiB / 17.4% CPU)</sub> | *Not possible (no QUIC)* | 🥇 **1,062**<br><sub>(183 MiB / 30.9% CPU)</sub> |

H1 is near parity with YARP. H2 HOL and H3 loss sustain are non-zero on this Windows GHA pass (@ `062f4e72`); see table.

### Linux — lossy / high-RTT (H2 HOL / H3 loss)

Median of **3** repeats @ `062f4e72`. Source: [36315373597](https://github.com/justcoding121/titanium-web-proxy/actions/runs/36315373597) (`compare-lossy`; lossy H3 uses `quic-http3`, UDP drop-only).

| Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---:|---:|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | **1,200**<br><sub>(128 MiB / 13.3% CPU)</sub> | **1,208**<br><sub>(100 MiB / 12.3% CPU)</sub> | 🥇 **1,209**<br><sub>(82 MiB / 7.4% CPU)</sub> | **1,198**<br><sub>(127 MiB / 9.7% CPU)</sub> | **1,194**<br><sub>(148 MiB / 17.7% CPU)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **313**<br><sub>(193 MiB / 6.7% CPU)</sub> | **40**<br><sub>(99 MiB / 0.3% CPU)</sub> | **42**<br><sub>(83 MiB / 0.2% CPU)</sub> | **40**<br><sub>(138 MiB / 0.4% CPU)</sub> | **40**<br><sub>(127 MiB / 1.4% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **1,150**<br><sub>(157 MiB / 24.5% CPU)</sub> | **1,083**<br><sub>(114 MiB / 23.7% CPU)</sub> | **944**<br><sub>(88 MiB / 19.4% CPU)</sub> | **990**<br><sub>(139 MiB / 25.9% CPU)</sub> | **1,118**<br><sub>(226 MiB / 31.8% CPU)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | 🥇 **324**<br><sub>(175 MiB / 6.2% CPU)</sub> | **40**<br><sub>(78 MiB / 0.2% CPU)</sub> | **41**<br><sub>(67 MiB / 0.2% CPU)</sub> | **40**<br><sub>(128 MiB / 0.3% CPU)</sub> | **41**<br><sub>(126 MiB / 1.0% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | **40**<br><sub>(107 MiB / 0.9% CPU)</sub> | *Not possible* | 🥇 **42**<br><sub>(88 MiB / 0.4% CPU)</sub> | **40**<br><sub>(134 MiB / 0.3% CPU)</sub> | **40**<br><sub>(128 MiB / 1.4% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | **40**<br><sub>(109 MiB / 1.0% CPU)</sub> | *Not possible* | 🥇 **42**<br><sub>(83 MiB / 0.6% CPU)</sub> | **40**<br><sub>(137 MiB / 0.5% CPU)</sub> | **40**<br><sub>(128 MiB / 1.5% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | 🥇 **1,039**<br><sub>(175 MiB / 40.2% CPU)</sub> | *Not possible* | **844**<br><sub>(92 MiB / 22.7% CPU)</sub> | **950**<br><sub>(140 MiB / 26.4% CPU)</sub> | **1,002**<br><sub>(227 MiB / 32.8% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **1,098**<br><sub>(174 MiB / 25.9% CPU)</sub> | **1,006**<br><sub>(116 MiB / 24.5% CPU)</sub> | **910**<br><sub>(92 MiB / 21.3% CPU)</sub> | **968**<br><sub>(138 MiB / 26.4% CPU)</sub> | **1,067**<br><sub>(227 MiB / 32.0% CPU)</sub> |

TWP H2 HOL ≫ YARP (~**7.8×**). H3 TWP÷YARP ≈ **1.07×**.

### Architecture-sensitive

These runs isolate slow app readers, origin-early response, HTTP/2 duplex, and WebSocket echo. See [TWP vs YARP IO model](Performance-Profiling#twp-vs-yarp-io-model). Laptop 1-rep numbers are on [Performance Local Lab](Performance-Local-Lab#architecture-sensitive).

Median of **3** repeats on matched 4 vCPU / 16 GiB runners @ `062f4e72` ([36315375597](https://github.com/justcoding121/titanium-web-proxy/actions/runs/36315375597)). Slow consumer = 256 KiB GET, 16 KiB read + 8 ms sleep. Early response = 64 KiB POST, origin writes after 8 KiB. Duplex HTTP/2 = overlapping 64 KiB POST on H2 TLS↔H2 TLS. WebSocket row = H1 Upgrade echo round-trips/sec (not RFC 8441; see WebSocket RFC 8441 table below).

Lossy-link runs (slow **network**) are already published above; they are not a slow **app** reader.

#### Windows

*Not possible:* **HAProxy** and **Envoy** columns are omitted (no official Windows port).

| Scenario | Client | Origin | TWP | nginx | YARP |
|---|---|---|---:|---:|---:|
| Slow consumer (256 KiB GET, throttled client read) | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **248**<br><sub>(99 MiB / 5.2% CPU)</sub> | **218**<br><sub>(144 MiB / 24.5% CPU)</sub> | **240**<br><sub>(112 MiB / 4.7% CPU)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · TLS | HTTP/1 · plain | **256**<br><sub>(118 MiB / 6.0% CPU)</sub> | **163**<br><sub>(142 MiB / 24.9% CPU)</sub> | 🥇 **256**<br><sub>(112 MiB / 7.1% CPU)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **272**<br><sub>(101 MiB / 15.2% CPU)</sub> | *Not possible (no QUIC)* | **265**<br><sub>(161 MiB / 19.0% CPU)</sub> |
| Early response (origin writes after first request chunk) | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **8,931**<br><sub>(98 MiB / 43.4% CPU)</sub> | **580**<br><sub>(143 MiB / 24.9% CPU)</sub> | **6,023**<br><sub>(138 MiB / 48.8% CPU)</sub> |
| Early response (origin writes after first request chunk) | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **3,707**<br><sub>(187 MiB / 52.3% CPU)</sub> | **0**<br><sub>(peak 318 · 142 MiB / 24.6% CPU)</sub> | **3,244**<br><sub>(135 MiB / 51.1% CPU)</sub> |
| Early response (origin writes after first request chunk) | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **2,107**<br><sub>(148 MiB / 41.7% CPU)</sub> | *Not possible (no QUIC)* | **1,833**<br><sub>(201 MiB / 51.3% CPU)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · plain | HTTP/1 · plain | **248**<br><sub>(115 MiB / 3.0% CPU)</sub> | **248**<br><sub>(127 MiB / 8.5% CPU)</sub> | 🥇 **256**<br><sub>(104 MiB / 4.1% CPU)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · TLS | HTTP/2 · TLS | **252**<br><sub>(127 MiB / 5.0% CPU)</sub> | *Not possible* | 🥇 **256**<br><sub>(136 MiB / 7.1% CPU)</sub> |
| Early response (origin writes after first request chunk) | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **2,315**<br><sub>(peak 2,576 · 118 MiB / 28.4% CPU)</sub> | *Not possible* | **30**<br><sub>(117 MiB / 0.6% CPU)</sub> |
| Duplex (both directions live) | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **2,236**<br><sub>(peak 2,580 · 118 MiB / 26.0% CPU)</sub> | *Not possible* | **25**<br><sub>(103 MiB / 0.5% CPU)</sub> |
| Duplex (WebSocket / H1 Upgrade) | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **40,584**<br><sub>(98 MiB / 41.8% CPU)</sub> | **29,498**<br><sub>(143 MiB / 24.7% CPU)</sub> | **38,067**<br><sub>(89 MiB / 44.6% CPU)</sub> |

#### Linux

| Scenario | Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---|---:|---:|---:|---:|---:|
| Slow consumer (256 KiB GET, throttled client read) | HTTP/1 · TLS | HTTP/1 · plain | **468**<br><sub>(122 MiB / 9.5% CPU)</sub> | **412**<br><sub>(100 MiB / 9.9% CPU)</sub> | 🥇 **472**<br><sub>(85 MiB / 6.0% CPU)</sub> | **472**<br><sub>(138 MiB / 5.9% CPU)</sub> | **419**<br><sub>(142 MiB / 14.0% CPU)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · TLS | HTTP/1 · plain | **476**<br><sub>(150 MiB / 12.2% CPU)</sub> | **430**<br><sub>(102 MiB / 6.7% CPU)</sub> | **480**<br><sub>(84 MiB / 4.2% CPU)</sub> | 🥇 **480**<br><sub>(156 MiB / 4.3% CPU)</sub> | **475**<br><sub>(152 MiB / 14.3% CPU)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **474**<br><sub>(131 MiB / 33.6% CPU)</sub> | **120**<br><sub>(peak 121 · 97 MiB / 5.9% CPU)</sub> | **471**<br><sub>(88 MiB / 10.4% CPU)</sub> | **205**<br><sub>(138 MiB / 7.3% CPU)</sub> | **472**<br><sub>(206 MiB / 38.5% CPU)</sub> |
| Early response (origin writes after first request chunk) | HTTP/1 · TLS | HTTP/1 · plain | **4,848**<br><sub>(140 MiB / 45.4% CPU)</sub> | **0**<br><sub>(peak 3,529 · 101 MiB / 46.2% CPU)</sub> | 🥇 **5,088**<br><sub>(86 MiB / 41.7% CPU)</sub> | **0**<br><sub>(peak 3,211 · 129 MiB / 26.8% CPU)</sub> | **3,368**<br><sub>(174 MiB / 55.6% CPU)</sub> |
| Early response (origin writes after first request chunk) | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **2,536**<br><sub>(210 MiB / 49.9% CPU)</sub> | **0**<br><sub>(peak 1,337 · 113 MiB / 23.8% CPU)</sub> | **2,470**<br><sub>(85 MiB / 24.0% CPU)</sub> | **0**<br><sub>(peak 2,729 · 149 MiB / 23.9% CPU)</sub> | **2,264**<br><sub>(168 MiB / 48.4% CPU)</sub> |
| Early response (origin writes after first request chunk) | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **4,440**<br><sub>(222 MiB / 43.5% CPU)</sub> | **0**<br><sub>(peak 1,098 · 127 MiB / 23.4% CPU)</sub> | **3,306**<br><sub>(93 MiB / 31.7% CPU)</sub> | **0**<br><sub>(125 MiB / 0.1% CPU)</sub> | **3,172**<br><sub>(264 MiB / 48.4% CPU)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · plain | HTTP/1 · plain | 🥇 **478**<br><sub>(144 MiB / 15.3% CPU)</sub> | **467**<br><sub>(79 MiB / 11.8% CPU)</sub> | **472**<br><sub>(69 MiB / 6.2% CPU)</sub> | **468**<br><sub>(146 MiB / 4.3% CPU)</sub> | **472**<br><sub>(148 MiB / 14.5% CPU)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · TLS | HTTP/2 · TLS | **464**<br><sub>(149 MiB / 21.5% CPU)</sub> | *Not possible* | **459**<br><sub>(90 MiB / 19.4% CPU)</sub> | **462**<br><sub>(150 MiB / 12.4% CPU)</sub> | 🥇 **465**<br><sub>(162 MiB / 29.2% CPU)</sub> |
| Early response (origin writes after first request chunk) | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **2,910**<br><sub>(peak 3,320 · 141 MiB / 26.3% CPU)</sub> | *Not possible* | **165**<br><sub>(peak 2,617 · 80 MiB / 1.2% CPU)</sub> | **0**<br><sub>(peak 3,498 · 150 MiB / 19.0% CPU)</sub> | **21**<br><sub>(138 MiB / 0.6% CPU)</sub> |
| Duplex (both directions live) | HTTP/2 · TLS | HTTP/2 · TLS | **2,741**<br><sub>(peak 3,233 · 147 MiB / 25.5% CPU)</sub> | *Not possible* | 🥇 **2,907**<br><sub>(80 MiB / 23.6% CPU)</sub> | **0**<br><sub>(peak 3,505 · 157 MiB / 19.0% CPU)</sub> | **199**<br><sub>(141 MiB / 3.2% CPU)</sub> |
| Duplex (WebSocket / H1 Upgrade) | HTTP/1 · TLS | HTTP/1 · plain | **35,322**<br><sub>(126 MiB / 44.7% CPU)</sub> | 🥇 **40,018**<br><sub>(100 MiB / 36.8% CPU)</sub> | **37,514**<br><sub>(83 MiB / 39.9% CPU)</sub> | **36,817**<br><sub>(128 MiB / 41.4% CPU)</sub> | **31,486**<br><sub>(124 MiB / 45.1% CPU)</sub> |

Slow consumer is sleep-bound; H1/H2/H3 sit in the same band. Early-response H1: TWP leads (~**1.46×** / **1.45×** YARP Win/Linux). **Duplex H2** sustain is non-zero for TWP on this GHA pass (@ `062f4e72`). WebSocket: TWP÷YARP Windows ≈ **1.07×**; Linux nginx leads.

### TLS termination cost (H1 TLS → cleartext origin)

Isolates keep-alive tiny GET vs **new connection per request** (handshake-dominated) vs keep-alive **256 KiB**. Product comparison uses RPS and end-to-end latency; TWP can also capture `ClientTlsTiming` when `TWP_RPS_CAPTURE_TLS=1` (child process) — nginx/YARP have no equivalent hook.

#### Windows

Median of **3** repeats on `windows-latest` @ `062f4e72`. Source: Actions [36316343827](https://github.com/justcoding121/titanium-web-proxy/actions/runs/36316343827). Absolute RPS on GHA swings hard; prefer **TWP÷YARP**.

*Not possible:* **HAProxy** and **Envoy** columns are omitted (no official Windows port).

| Workload | TWP | nginx | YARP |
|---|---:|---:|---:|
| Keep-alive · tiny GET | 🥇 **33,382**<br><sub>(87 MiB / 46.5% CPU)</sub> | **19,102**<br><sub>(142 MiB / 24.8% CPU)</sub> | **30,738**<br><sub>(100 MiB / 49.1% CPU)</sub> |
| New-connection · tiny GET | 🥇 **1,045**<br><sub>(88 MiB / 10.1% CPU)</sub> | **360**<br><sub>(141 MiB / 24.4% CPU)</sub> | **1,006**<br><sub>(116 MiB / 10.5% CPU)</sub> |
| Keep-alive · 256 KiB GET | 🥇 **3,575**<br><sub>(133 MiB / 38.9% CPU)</sub> | **260**<br><sub>(143 MiB / 24.7% CPU)</sub> | **3,391**<br><sub>(136 MiB / 45.1% CPU)</sub> |

#### Linux

Median of **3** repeats @ `062f4e72`. Source: Actions [36316343827](https://github.com/justcoding121/titanium-web-proxy/actions/runs/36316343827).

| Workload | TWP | nginx | HAProxy | Envoy | YARP |
|---|---:|---:|---:|---:|---:|
| Keep-alive · tiny GET | **25,007**<br><sub>(107 MiB / 49.1% CPU)</sub> | 🥇 **28,835**<br><sub>(100 MiB / 41.6% CPU)</sub> | **28,632**<br><sub>(82 MiB / 43.5% CPU)</sub> | **17,647**<br><sub>(127 MiB / 58.7% CPU)</sub> | **21,144**<br><sub>(136 MiB / 51.2% CPU)</sub> |
| New-connection · tiny GET | **984**<br><sub>(120 MiB / 47.7% CPU)</sub> | **1,024**<br><sub>(100 MiB / 44.8% CPU)</sub> | **964**<br><sub>(82 MiB / 44.3% CPU)</sub> | 🥇 **1,093**<br><sub>(128 MiB / 41.6% CPU)</sub> | **984**<br><sub>(147 MiB / 46.6% CPU)</sub> |
| Keep-alive · 256 KiB GET | **2,749**<br><sub>(127 MiB / 36.8% CPU)</sub> | **1,790**<br><sub>(99 MiB / 52.5% CPU)</sub> | 🥇 **3,026**<br><sub>(82 MiB / 33.3% CPU)</sub> | **2,756**<br><sub>(145 MiB / 33.6% CPU)</sub> | **2,180**<br><sub>(160 MiB / 45.2% CPU)</sub> |

#### macOS

Median of **3** repeats on `macos-15-intel` @ `062f4e72`. Source: Actions [36325271707](https://github.com/justcoding121/titanium-web-proxy/actions/runs/36325271707).

| Workload | TWP | nginx | HAProxy | Envoy | YARP |
|---|---:|---:|---:|---:|---:|
| Keep-alive · tiny GET | **8,347**<br><sub>(94 MiB / 35.1% CPU)</sub> | **5,697**<br><sub>(73 MiB / 18.6% CPU)</sub> | 🥇 **8,782**<br><sub>(65 MiB / 32.6% CPU)</sub> | **1,758**<br><sub>(75 MiB / 20.6% CPU)</sub> | **8,456**<br><sub>(118 MiB / 37.0% CPU)</sub> |
| New-connection · tiny GET | **0**<br><sub>(peak 73 · 79 MiB / 11.1% CPU)</sub> | 🥇 **333**<br><sub>(70 MiB / 15.6% CPU)</sub> | **321**<br><sub>(61 MiB / 19.3% CPU)</sub> | **318**<br><sub>(79 MiB / 18.1% CPU)</sub> | **0**<br><sub>(peak 72 · 113 MiB / 6.0% CPU)</sub> |
| Keep-alive · 256 KiB GET | **1,034**<br><sub>(184 MiB / 27.6% CPU)</sub> | **226**<br><sub>(peak 229 · 70 MiB / 15.8% CPU)</sub> | **596**<br><sub>(66 MiB / 23.7% CPU)</sub> | **268**<br><sub>(90 MiB / 10.0% CPU)</sub> | 🥇 **1,120**<br><sub>(119 MiB / 36.8% CPU)</sub> |

All three workloads are **>1.00×** YARP on Windows and Linux. On Linux, HAProxy leads keep-alive tiny (near-tie with nginx) and Envoy leads new-connection; TWP stays ahead of YARP on all three. On macOS, HAProxy leads keep-alive tiny; YARP leads keep-alive 256 KiB; new-connection sustain @ c=64 is peer-led (TWP/YARP 0).

## Unary gRPC (H2 TLS)

Unary Echo **RPC/s** @ c=64 over H2 TLS→H2 TLS for Titanium, YARP, nginx (`grpc_pass`), HAProxy, and Envoy (OS-possible peers only). Not folded into the architecture-sensitive tables. Median of **3** repeats @ `062f4e72` — [36316348044](https://github.com/justcoding121/titanium-web-proxy/actions/runs/36316348044).

| OS | Titanium | YARP | nginx | HAProxy | Envoy |
|---|---:|---:|---:|---:|---:|
| Windows | 🥇 **49,194**<br><sub>(91 MiB / 22.5% CPU)</sub> | **30,196**<br><sub>(123 MiB / 43.0% CPU)</sub> | **0**<br><sub>(143 MiB / 24.8% CPU)</sub> | *Not possible* | *Not possible* |
| Linux | 🥇 **35,993**<br><sub>(112 MiB / 28.9% CPU)</sub> | **21,659**<br><sub>(164 MiB / 42.9% CPU)</sub> | **0**<br><sub>(114 MiB / 24.7% CPU)</sub> | **6,659**<br><sub>(81 MiB / 24.6% CPU)</sub> | **11,794**<br><sub>(128 MiB / 21.6% CPU)</sub> |
| macOS | 🥇 **24,542**<br><sub>(95 MiB / 20.2% CPU)</sub> | **17,832**<br><sub>(132 MiB / 35.7% CPU)</sub> | **0**<br><sub>(67 MiB / 2.9% CPU)</sub> | **0**<br><sub>(63 MiB / 18.0% CPU)</sub> | **0**<br><sub>(80 MiB / 19.8% CPU)</sub> |

## Unary gRPC (H2 TLS → h2c)

Unary Echo **RPC/s** @ c=64 over **H2 TLS → h2c** (edge TLS, cleartext H2 origin). Peers: Titanium, YARP, HAProxy, Envoy. Mode: `compare-grpc` (`*-grpc-h2c`). Median of **3** repeats @ `062f4e72` — [36316350020](https://github.com/justcoding121/titanium-web-proxy/actions/runs/36316350020).

*Not possible:* **nginx** column omitted (no H2 upstream).

| OS | Titanium | YARP | HAProxy | Envoy |
|---|---:|---:|---:|---:|
| Windows | 🥇 **58,581**<br><sub>(89 MiB / 25.8% CPU)</sub> | **33,942**<br><sub>(110 MiB / 49.5% CPU)</sub> | *Not possible* | *Not possible* |
| Linux | 🥇 **43,440**<br><sub>(116 MiB / 29.6% CPU)</sub> | **24,412**<br><sub>(158 MiB / 43.6% CPU)</sub> | **7,515**<br><sub>(85 MiB / 24.6% CPU)</sub> | **11,639**<br><sub>(127 MiB / 22.6% CPU)</sub> |
| macOS | 🥇 **16,859**<br><sub>(94 MiB / 21.5% CPU)</sub> | **12,427**<br><sub>(134 MiB / 36.3% CPU)</sub> | **0**<br><sub>(63 MiB / 17.5% CPU)</sub> | **0**<br><sub>(80 MiB / 19.9% CPU)</sub> |

## WebSocket (H1 TLS → H1 TLS)

WebSocket echo round-trips/sec over **dual-TLS** H1 (`proxy_ssl` style). Mode: `compare-ws-h1tls` (`*-duplex-ws-h1tls`).

| OS | Titanium | YARP | nginx | HAProxy | Envoy |
|---|---:|---:|---:|---:|---:|
| Windows | 🥇 **22,171**<br><sub>(101 MiB / 44.0% CPU)</sub> | **21,423**<br><sub>(92 MiB / 42.5% CPU)</sub> | **9,602**<br><sub>(143 MiB / 24.7% CPU)</sub> | *Not possible* | *Not possible* |
| Linux | **25,380**<br><sub>(137 MiB / 43.7% CPU)</sub> | **22,863**<br><sub>(134 MiB / 44.0% CPU)</sub> | **27,209**<br><sub>(104 MiB / 37.0% CPU)</sub> | **25,894**<br><sub>(83 MiB / 39.8% CPU)</sub> | 🥇 **27,455**<br><sub>(127 MiB / 38.2% CPU)</sub> |
| macOS | **12,958**<br><sub>(138 MiB / 31.2% CPU)</sub> | 🥇 **15,493**<br><sub>(115 MiB / 32.1% CPU)</sub> | **9,651**<br><sub>(74 MiB / 24.8% CPU)</sub> | **13,876**<br><sub>(64 MiB / 32.1% CPU)</sub> | **9,535**<br><sub>(80 MiB / 28.7% CPU)</sub> |

## WebSocket (H2 TLS 8441 → H1)

WebSocket echo over **RFC 8441** extended CONNECT (H2 TLS client → H1 plain origin). Mode: `compare-ws-h2` (`*-duplex-ws-h2`).

*Not possible:* **nginx** column omitted (no RFC 8441 extended CONNECT).

| OS | Titanium | YARP | HAProxy | Envoy |
|---|---:|---:|---:|---:|
| Windows | 🥇 **35,307**<br><sub>(166 MiB / 46.3% CPU)</sub> | **0**<br><sub>(169 MiB / 20.5% CPU)</sub> | *Not possible* | *Not possible* |
| Linux | **1,536**<br><sub>(149 MiB / 11.8% CPU)</sub> | **0**<br><sub>(175 MiB / 18.8% CPU)</sub> | 🥇 **1,538**<br><sub>(84 MiB / 3.6% CPU)</sub> | **0**<br><sub>(123 MiB / 0.3% CPU)</sub> |
| macOS | **8,679**<br><sub>(275 MiB / 40.6% CPU)</sub> | **0**<br><sub>(106 MiB / 17.9% CPU)</sub> | 🥇 **9,250**<br><sub>(66 MiB / 32.4% CPU)</sub> | **0**<br><sub>(73 MiB / 0.5% CPU)</sub> |

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
