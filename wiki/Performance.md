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

Calibration for the shared 4 vCPU loopback shape: how close client + origin are to saturated before ranking reverse peers. Tiny keep-alive GET. Median of **3** repeats @ `7c029d65` — [36280073777](https://github.com/justcoding121/titanium-web-proxy/actions/runs/36280073777). Warmup 2s / measure 8s; concurrency 8, 16, 32, 64. Block A **% of origin-HttpClient** uses median **peak** RPS. Blocks B/C use peer÷YARP / ÷nginx on median peak (not % of H1 origin). **RPS cells** embed median RSS / CPU for the **proxy child** plus its **full descendant tree** (serve-proxy → nginx master → workers); origin-direct samples the **origin** child. Product matrices below use matched `dotnet-httpclient` only (not bombardier).


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
| origin-direct | dotnet-httpclient | **50,406**<br><sub>(55 MiB / 40.5% CPU)</sub> | **100.0%** |
| origin-direct-bombardier | bombardier | **38,033**<br><sub>(56 MiB / 21.5% CPU)</sub> | **75.5%** |
| bare-reverse-http1 | dotnet-httpclient | **24,857**<br><sub>(60 MiB / 45.8% CPU)</sub> | **49.3%** |
| nginx-reverse-http1 | dotnet-httpclient | **13,382**<br><sub>(126 MiB / 24.8% CPU)</sub> | **26.5%** |
| yarp-reverse-http1 | dotnet-httpclient | **21,189**<br><sub>(87 MiB / 50.2% CPU)</sub> | **42.0%** |
| twp-reverse-http1 | dotnet-httpclient | 🥇 **25,132**<br><sub>(73 MiB / 47.3% CPU)</sub> | **49.9%** |

**Linux** (`ubuntu-latest`)

| Arm | Generator | RPS | % of origin-HttpClient |
|---|---|---:|---:|
| origin-direct | dotnet-httpclient | **69,525**<br><sub>(79 MiB / 42.2% CPU)</sub> | **100.0%** |
| origin-direct-bombardier | bombardier | **42,176**<br><sub>(80 MiB / 35.3% CPU)</sub> | **60.7%** |
| bare-reverse-http1 | dotnet-httpclient | **32,525**<br><sub>(67 MiB / 45.0% CPU)</sub> | **46.8%** |
| nginx-reverse-http1 | dotnet-httpclient | 🥇 **39,769**<br><sub>(76 MiB / 40.7% CPU)</sub> | **57.2%** |
| yarp-reverse-http1 | dotnet-httpclient | **28,101**<br><sub>(116 MiB / 50.2% CPU)</sub> | **40.4%** |
| twp-reverse-http1 | dotnet-httpclient | **32,762**<br><sub>(86 MiB / 49.4% CPU)</sub> | **47.1%** |

Reverse peers are about **50–48%** of the origin-direct HttpClient peak on this runner class (Win TWP **50.1%**, Lin TWP **48.3%**). Prefer the **%** column over absolute RPS across runs. Bare and origin-direct are controls (not medal peers).

#### Block B — H2 TLS→H1

Peer ratios (÷YARP / ÷nginx) on median peak; **RPS cells** embed `(MiB / CPU%)`.

**Windows** (`windows-latest`)

| Arm | Generator | RPS | ÷YARP | ÷nginx |
|---|---|---:|---:|---:|
| nginx-reverse-http2 | dotnet-httpclient | **7,818**<br><sub>(142 MiB / 24.6% CPU)</sub> | **0.27×** | **1.00×** |
| yarp-reverse-http2 | dotnet-httpclient | **28,628**<br><sub>(92 MiB / 53.2% CPU)</sub> | **1.00×** | **3.66×** |
| twp-reverse-http2-cleartext | dotnet-httpclient | 🥇 **34,727**<br><sub>(98 MiB / 53.2% CPU)</sub> | **1.21×** | **4.44×** |

**Linux** (`ubuntu-latest`)

| Arm | Generator | RPS | ÷YARP | ÷nginx |
|---|---|---:|---:|---:|
| nginx-reverse-http2 | dotnet-httpclient | **15,432**<br><sub>(100 MiB / 19.2% CPU)</sub> | **0.53×** | **1.00×** |
| yarp-reverse-http2 | dotnet-httpclient | **29,258**<br><sub>(121 MiB / 50.3% CPU)</sub> | **1.00×** | **1.90×** |
| twp-reverse-http2-cleartext | dotnet-httpclient | 🥇 **34,950**<br><sub>(119 MiB / 52.4% CPU)</sub> | **1.19×** | **2.26×** |

#### Block C — H3→H1

Same layout as Block B. Requires QuicListener. nginx needs `http_v3_module` (Windows nginx has no QUIC). HAProxy needs `USE_QUIC` (GHA Linux/macOS require it). Envoy 1.20+ includes HTTP/3.

**Windows** (`windows-latest`)

| Arm | Generator | RPS | ÷YARP | ÷nginx |
|---|---|---:|---:|---:|
| nginx-reverse-http3-cleartext | dotnet-httpclient | *Not possible (no QUIC)* | — | — |
| yarp-reverse-http3-cleartext | dotnet-httpclient | 🥇 **15,941**<br><sub>(142 MiB / 50.6% CPU)</sub> | **1.00×** | — |
| twp-reverse-http3-cleartext | dotnet-httpclient | **14,638**<br><sub>(103 MiB / 44.7% CPU)</sub> | **0.92×** | — |

**Linux** (`ubuntu-latest`)

| Arm | Generator | RPS | ÷YARP | ÷nginx |
|---|---|---:|---:|---:|
| nginx-reverse-http3-cleartext | dotnet-httpclient | **0**<br><sub>(peak 15,339 · 107 MiB / 22.5% CPU)</sub> | **0.81×** | **1.00×** |
| yarp-reverse-http3-cleartext | dotnet-httpclient | **18,986**<br><sub>(181 MiB / 50.0% CPU)</sub> | **1.00×** | **1.24×** |
| twp-reverse-http3-cleartext | dotnet-httpclient | 🥇 **21,033**<br><sub>(146 MiB / 50.0% CPU)</sub> | **1.11×** | **1.37×** |

## Windows — Titanium vs nginx vs HAProxy vs Envoy vs YARP

Client / origin: HTTP version and whether TLS is used (`plain` = cleartext, `TLS` = encrypted, `QUIC` = HTTP/3).

### Reverse

Median of **3 repeats** on `windows-latest` (4 vCPU / 16 GiB). Bare reverse 5×5 @ `7c029d65` — `compare-product` [36280055132](https://github.com/justcoding121/titanium-web-proxy/actions/runs/36280055132). Warmup 2s / measure 8s; concurrency 8, 16, 32, 64. Prefer TWP÷peer ratios over absolute RPS. **RPS cells** include median RSS / CPU at the peak-RPS step as `<br><sub>(MiB / CPU%)</sub>`. nginx terminate peers use `keepalive 256` + streaming buffers. **HAProxy / Envoy are Linux-only peers** (no official Windows port). Laptop High-perf / cool-paired numbers stay on the [local lab](Performance-Local-Lab). Product 5×5 is **~56-byte JSON keep-alive GET**; H2/H3 same-protocol cells are mostly header work with a tiny body (Titanium best case) — see [Why this comparison is fair](#why-this-comparison-is-fair).

**Load generators:** Reverse inbound H3 arms use **`dotnet-httpclient`** (`http_version=3.0`, `RequestVersionExact`). nginx/Windows is same-OS only (no QUIC). HAProxy/Envoy are Linux-only terminate peers.

| Client | Origin | TWP | nginx | YARP |
|---|---|---:|---:|---:|
| HTTP/1 · plain | HTTP/1 · plain | 🥇 **70,001**<br><sub>(74 MiB / 47.9% CPU)</sub> | **40,289**<br><sub>(126 MiB / 24.7% CPU)</sub> | **64,189**<br><sub>(93 MiB / 49.2% CPU)</sub> |
| HTTP/1 · plain | HTTP/1 · TLS | 🥇 **35,007**<br><sub>(90 MiB / 48.1% CPU)</sub> | **18,438**<br><sub>(136 MiB / 24.7% CPU)</sub> | **31,660**<br><sub>(100 MiB / 49.7% CPU)</sub> |
| HTTP/1 · plain | HTTP/2 · plain | 🥇 **37,968**<br><sub>(103 MiB / 49.3% CPU)</sub> | *Not possible (no H2 upstream)* | **33,411**<br><sub>(93 MiB / 47.5% CPU)</sub> |
| HTTP/1 · plain | HTTP/2 · TLS | 🥇 **87,873**<br><sub>(132 MiB / 45.2% CPU)</sub> | *Not possible (no H2 upstream)* | **87,457**<br><sub>(102 MiB / 46.5% CPU)</sub> |
| HTTP/1 · plain | HTTP/3 · QUIC | 🥇 **25,825**<br><sub>(116 MiB / 50.6% CPU)</sub> | *Not possible (no H3 upstream)* | **25,452**<br><sub>(125 MiB / 49.4% CPU)</sub> |
| HTTP/1 · TLS | HTTP/1 · plain | 🥇 **21,206**<br><sub>(86 MiB / 47.7% CPU)</sub> | **8,740**<br><sub>(142 MiB / 24.8% CPU)</sub> | **18,305**<br><sub>(103 MiB / 49.9% CPU)</sub> |
| HTTP/1 · TLS | HTTP/1 · TLS | 🥇 **54,030**<br><sub>(82 MiB / 48.4% CPU)</sub> | **20,953**<br><sub>(144 MiB / 24.9% CPU)</sub> | **52,697**<br><sub>(102 MiB / 47.3% CPU)</sub> |
| HTTP/1 · TLS | HTTP/2 · plain | 🥇 **46,696**<br><sub>(127 MiB / 48% CPU)</sub> | *Not possible (no H2 upstream)* | **43,256**<br><sub>(112 MiB / 49.2% CPU)</sub> |
| HTTP/1 · TLS | HTTP/2 · TLS | 🥇 **26,005**<br><sub>(119 MiB / 45% CPU)</sub> | *Not possible (no H2 upstream)* | **24,861**<br><sub>(111 MiB / 46.4% CPU)</sub> |
| HTTP/1 · TLS | HTTP/3 · QUIC | 🥇 **46,460**<br><sub>(140 MiB / 49.5% CPU)</sub> | *Not possible (no H3 upstream)* | **43,920**<br><sub>(148 MiB / 48.8% CPU)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | 🥇 **50,766**<br><sub>(102 MiB / 50.5% CPU)</sub> | **22,524**<br><sub>(127 MiB / 24.8% CPU)</sub> | **49,024**<br><sub>(84 MiB / 50.9% CPU)</sub> |
| HTTP/2 · plain | HTTP/1 · TLS | 🥇 **31,399**<br><sub>(100 MiB / 53% CPU)</sub> | **6,601**<br><sub>(137 MiB / 24.8% CPU)</sub> | **27,526**<br><sub>(93 MiB / 49.7% CPU)</sub> |
| HTTP/2 · plain | HTTP/2 · plain | 🥇 **238,555**<br><sub>(59 MiB / 29.4% CPU)</sub> | *Not possible (no H2 upstream)* | **138,202**<br><sub>(95 MiB / 49.6% CPU)</sub> |
| HTTP/2 · plain | HTTP/2 · TLS | 🥇 **114,540**<br><sub>(64 MiB / 30.5% CPU)</sub> | *Not possible (no H2 upstream)* | **69,921**<br><sub>(101 MiB / 50.3% CPU)</sub> |
| HTTP/2 · plain | HTTP/3 · QUIC | 🥇 **32,118**<br><sub>(115 MiB / 52.4% CPU)</sub> | *Not possible (no H3 upstream)* | **29,584**<br><sub>(124 MiB / 51.6% CPU)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **93,202**<br><sub>(113 MiB / 50.8% CPU)</sub> | **23,736**<br><sub>(142 MiB / 24.5% CPU)</sub> | **85,953**<br><sub>(99 MiB / 49.6% CPU)</sub> |
| HTTP/2 · TLS | HTTP/1 · TLS | 🥇 **40,545**<br><sub>(103 MiB / 47.8% CPU)</sub> | **13,978**<br><sub>(144 MiB / 24.6% CPU)</sub> | **40,223**<br><sub>(100 MiB / 50% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | 🥇 **102,427**<br><sub>(81 MiB / 29.6% CPU)</sub> | *Not possible (no H2 upstream)* | **55,095**<br><sub>(99 MiB / 51.1% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **192,994**<br><sub>(74 MiB / 29% CPU)</sub> | *Not possible (no H2 upstream)* | **113,745**<br><sub>(105 MiB / 48.3% CPU)</sub> |
| HTTP/2 · TLS | HTTP/3 · QUIC | 🥇 **37,617**<br><sub>(141 MiB / 53.2% CPU)</sub> | *Not possible (no H3 upstream)* | **33,690**<br><sub>(132 MiB / 52.1% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **15,120**<br><sub>(105 MiB / 45.4% CPU)</sub> | *Not possible (no QUIC)* | **14,558**<br><sub>(141 MiB / 51.9% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **48,537**<br><sub>(150 MiB / 45.9% CPU)</sub> | *Not possible (no QUIC)* | **46,501**<br><sub>(178 MiB / 46.8% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/2 · plain | 🥇 **42,556**<br><sub>(151 MiB / 46.4% CPU)</sub> | *Not possible (no H2 upstream)* | **30,671**<br><sub>(164 MiB / 50.9% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | 🥇 **32,304**<br><sub>(134 MiB / 46.3% CPU)</sub> | *Not possible (no H2 upstream)* | **21,898**<br><sub>(151 MiB / 48.8% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/3 · QUIC | 🥇 **58,333**<br><sub>(193 MiB / 44.9% CPU)</sub> | *Not possible (no H3 upstream)* | **48,534**<br><sub>(215 MiB / 49.9% CPU)</sub> |

### MITM (TWP only)

Same Client×Origin wires with interception on (`compare-product` [36280055132](https://github.com/justcoding121/titanium-web-proxy/actions/runs/36280055132)). **Lite** = no-op handlers (unchanged-lite finish). **Full** = append-only header mutation (harness: one probe header each way; product: generic append-only relay via `MitmCompressedRelayHelper`). nginx/HAProxy/Envoy/YARP cannot MITM. **Lite÷Reverse** / **Full÷Reverse** vs bare reverse (**same job / comparison-group shard**). Completion gate: Lite ≥ **0.50×** and Full ≥ **0.50×** reverse sustain @ c=64 (median of 3 GHA runs); reverse TWP÷YARP ≥ **0.75×** (no terminate-peer gate).

**v1 append-only relay (2026-08-27):** Pre-fix H2→H2 Full÷Reverse was **0.13–0.16×** ([32960766249](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32960766249)). Post-fix @ `df172718`: H2 plain→H2 plain Full **0.77–0.79×**, H3→H1 Full **0.91–0.93×**, all MITM arms ≥ **0.70×** on median of [33041445371](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33041445371), [33055267086](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33055267086), [33055272140](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33055272140).

**v2 drop-only + non-unique append (2026-08-27):** `MitmStaticRebuildHelper` rebuilds static HPACK/QPACK after 1–4 unique header drops; trailing non-unique appends stay on compressed relay. @ `7c029d65`: all MITM arms ≥ **0.70×** on GHA median ([33087088466](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087088466), [33087091622](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087091622), [33105885748](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33105885748) Linux; [33087085235](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087085235), [33087088466](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087088466), [33087091622](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087091622) Windows). H2 plain→H2 plain Full **0.77–0.79×** (Win) / **0.78×** (Lin).

| Client | Origin | Lite sustain | Full sustain | Lite÷Reverse | Full÷Reverse |
|---|---|---:|---:|---:|---:|
| HTTP/1 · plain | HTTP/1 · plain | **71,708**<br><sub>(79 MiB / 46.5% CPU)</sub> | **69,884**<br><sub>(81 MiB / 49.3% CPU)</sub> | **1.02×** | **1×** |
| HTTP/1 · plain | HTTP/1 · TLS | **34,803**<br><sub>(95 MiB / 49.7% CPU)</sub> | **34,182**<br><sub>(94 MiB / 48.6% CPU)</sub> | **0.99×** | **0.98×** |
| HTTP/1 · plain | HTTP/2 · plain | **36,544**<br><sub>(107 MiB / 48.4% CPU)</sub> | **36,174**<br><sub>(111 MiB / 48.1% CPU)</sub> | **0.96×** | **0.95×** |
| HTTP/1 · plain | HTTP/2 · TLS | **90,543**<br><sub>(145 MiB / 47% CPU)</sub> | **86,431**<br><sub>(142 MiB / 48.2% CPU)</sub> | **1.03×** | **0.98×** |
| HTTP/1 · plain | HTTP/3 · QUIC | **25,263**<br><sub>(112 MiB / 50.3% CPU)</sub> | **25,304**<br><sub>(123 MiB / 52.6% CPU)</sub> | **0.98×** | **0.98×** |
| HTTP/1 · TLS | HTTP/1 · plain | **20,553**<br><sub>(93 MiB / 49.3% CPU)</sub> | **20,474**<br><sub>(93 MiB / 48% CPU)</sub> | **0.97×** | **0.97×** |
| HTTP/1 · TLS | HTTP/1 · TLS | **56,388**<br><sub>(94 MiB / 47.2% CPU)</sub> | **54,369**<br><sub>(95 MiB / 46.6% CPU)</sub> | **1.04×** | **1.01×** |
| HTTP/1 · TLS | HTTP/2 · plain | **45,467**<br><sub>(127 MiB / 49.6% CPU)</sub> | **44,069**<br><sub>(135 MiB / 50.3% CPU)</sub> | **0.97×** | **0.94×** |
| HTTP/1 · TLS | HTTP/2 · TLS | **25,540**<br><sub>(128 MiB / 45.3% CPU)</sub> | **25,133**<br><sub>(121 MiB / 45% CPU)</sub> | **0.98×** | **0.97×** |
| HTTP/1 · TLS | HTTP/3 · QUIC | **48,086**<br><sub>(154 MiB / 51.5% CPU)</sub> | **46,119**<br><sub>(159 MiB / 51.9% CPU)</sub> | **1.03×** | **0.99×** |
| HTTP/2 · plain | HTTP/1 · plain | **50,967**<br><sub>(95 MiB / 53.7% CPU)</sub> | **49,216**<br><sub>(95 MiB / 52.6% CPU)</sub> | **1×** | **0.97×** |
| HTTP/2 · plain | HTTP/1 · TLS | **30,983**<br><sub>(97 MiB / 54.3% CPU)</sub> | **30,432**<br><sub>(95 MiB / 54.6% CPU)</sub> | **0.99×** | **0.97×** |
| HTTP/2 · plain | HTTP/2 · plain | **185,888**<br><sub>(68 MiB / 41.1% CPU)</sub> | **169,263**<br><sub>(69 MiB / 41.7% CPU)</sub> | **0.78×** | **0.71×** |
| HTTP/2 · plain | HTTP/2 · TLS | **97,367**<br><sub>(78 MiB / 38.1% CPU)</sub> | **92,198**<br><sub>(76 MiB / 40.5% CPU)</sub> | **0.85×** | **0.8×** |
| HTTP/2 · plain | HTTP/3 · QUIC | **31,649**<br><sub>(127 MiB / 53.8% CPU)</sub> | **31,139**<br><sub>(123 MiB / 55.1% CPU)</sub> | **0.99×** | **0.97×** |
| HTTP/2 · TLS | HTTP/1 · plain | **92,624**<br><sub>(114 MiB / 50.8% CPU)</sub> | **91,171**<br><sub>(115 MiB / 51.3% CPU)</sub> | **0.99×** | **0.98×** |
| HTTP/2 · TLS | HTTP/1 · TLS | **44,102**<br><sub>(108 MiB / 53.9% CPU)</sub> | **43,130**<br><sub>(105 MiB / 53.1% CPU)</sub> | **1.09×** | **1.06×** |
| HTTP/2 · TLS | HTTP/2 · plain | **79,908**<br><sub>(87 MiB / 43.7% CPU)</sub> | **74,480**<br><sub>(88 MiB / 41.6% CPU)</sub> | **0.78×** | **0.73×** |
| HTTP/2 · TLS | HTTP/2 · TLS | **153,600**<br><sub>(84 MiB / 39% CPU)</sub> | **147,769**<br><sub>(83 MiB / 37.8% CPU)</sub> | **0.8×** | **0.77×** |
| HTTP/2 · TLS | HTTP/3 · QUIC | **37,132**<br><sub>(144 MiB / 53.5% CPU)</sub> | **36,744**<br><sub>(148 MiB / 52.3% CPU)</sub> | **0.99×** | **0.98×** |
| HTTP/3 · QUIC | HTTP/1 · plain | **14,496**<br><sub>(113 MiB / 46% CPU)</sub> | **14,106**<br><sub>(110 MiB / 46.2% CPU)</sub> | **0.96×** | **0.93×** |
| HTTP/3 · QUIC | HTTP/1 · TLS | **45,204**<br><sub>(155 MiB / 48.3% CPU)</sub> | **46,471**<br><sub>(155 MiB / 47% CPU)</sub> | **0.93×** | **0.96×** |
| HTTP/3 · QUIC | HTTP/2 · plain | **41,313**<br><sub>(154 MiB / 48.1% CPU)</sub> | **40,136**<br><sub>(159 MiB / 47.8% CPU)</sub> | **0.97×** | **0.94×** |
| HTTP/3 · QUIC | HTTP/2 · TLS | **31,124**<br><sub>(143 MiB / 45.9% CPU)</sub> | **30,167**<br><sub>(137 MiB / 47.5% CPU)</sub> | **0.96×** | **0.93×** |
| HTTP/3 · QUIC | HTTP/3 · QUIC | **53,256**<br><sub>(196 MiB / 49.1% CPU)</sub> | **52,686**<br><sub>(191 MiB / 49.8% CPU)</sub> | **0.91×** | **0.9×** |

## Linux — Titanium vs nginx vs HAProxy vs Envoy vs YARP

### Reverse

Median of **3 repeats** on `ubuntu-latest` (4 vCPU / 16 GiB). Bare reverse 5×5 @ `7c029d65` — `compare-product` [36280055132](https://github.com/justcoding121/titanium-web-proxy/actions/runs/36280055132). Warmup 2s / measure 8s; concurrency 8, 16, 32, 64. **Linux nginx is the authoritative nginx baseline.** HAProxy (3.2 `USE_QUIC`) and Envoy (GitHub release, HTTP/3 compiled in) run on the same loopback shape as nginx/YARP. nginx terminate peers use `keepalive 256` + streaming buffers. The RPS workflow installs nginx.org mainline (`http_v3_module`), a QUIC-enabled HAProxy, Envoy, and `libmsquic`. Prefer ratios over absolute RPS. Product 5×5 is **~56-byte JSON keep-alive GET**; H2/H3 same-protocol cells are mostly header work with a tiny body (Titanium best case) — see [Why this comparison is fair](#why-this-comparison-is-fair).

| Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---:|---:|---:|---:|---:|
| HTTP/1 · plain | HTTP/1 · plain | **32,679**<br><sub>(84 MiB / 49.9% CPU)</sub> | 🥇 **39,015**<br><sub>(76 MiB / 40.3% CPU)</sub> | **36,515**<br><sub>(64 MiB / 42.5% CPU)</sub> | **19,594**<br><sub>(115 MiB / 62.6% CPU)</sub> | **27,950**<br><sub>(116 MiB / 50% CPU)</sub> |
| HTTP/1 · plain | HTTP/1 · TLS | **55,910**<br><sub>(106 MiB / 48.3% CPU)</sub> | 🥇 **67,099**<br><sub>(93 MiB / 41.5% CPU)</sub> | **61,618**<br><sub>(68 MiB / 41.7% CPU)</sub> | **49,248**<br><sub>(118 MiB / 52.4% CPU)</sub> | **52,158**<br><sub>(131 MiB / 47.4% CPU)</sub> |
| HTTP/1 · plain | HTTP/2 · plain | 🥇 **52,489**<br><sub>(124 MiB / 49.2% CPU)</sub> | *Not possible (no H2 upstream)* | **39,114**<br><sub>(64 MiB / 40.2% CPU)</sub> | **30,468**<br><sub>(115 MiB / 62.3% CPU)</sub> | **47,681**<br><sub>(125 MiB / 49.7% CPU)</sub> |
| HTTP/1 · plain | HTTP/2 · TLS | 🥇 **33,155**<br><sub>(145 MiB / 48.5% CPU)</sub> | *Not possible (no H2 upstream)* | **28,912**<br><sub>(65 MiB / 44.6% CPU)</sub> | **20,062**<br><sub>(117 MiB / 62.2% CPU)</sub> | **29,665**<br><sub>(133 MiB / 47.8% CPU)</sub> |
| HTTP/1 · plain | HTTP/3 · QUIC | 🥇 **54,274**<br><sub>(164 MiB / 54.8% CPU)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **30,134**<br><sub>(121 MiB / 47.7% CPU)</sub> | **46,610**<br><sub>(173 MiB / 47% CPU)</sub> |
| HTTP/1 · TLS | HTTP/1 · plain | **34,291**<br><sub>(111 MiB / 47% CPU)</sub> | **42,313**<br><sub>(103 MiB / 39.9% CPU)</sub> | 🥇 **42,800**<br><sub>(84 MiB / 42.7% CPU)</sub> | **24,810**<br><sub>(127 MiB / 59.3% CPU)</sub> | **30,044**<br><sub>(134 MiB / 49.6% CPU)</sub> |
| HTTP/1 · TLS | HTTP/1 · TLS | **19,943**<br><sub>(111 MiB / 48% CPU)</sub> | **22,791**<br><sub>(103 MiB / 41.6% CPU)</sub> | 🥇 **22,892**<br><sub>(85 MiB / 42.9% CPU)</sub> | **15,220**<br><sub>(128 MiB / 55.9% CPU)</sub> | **17,412**<br><sub>(138 MiB / 50% CPU)</sub> |
| HTTP/1 · TLS | HTTP/2 · plain | 🥇 **72,450**<br><sub>(152 MiB / 50.6% CPU)</sub> | *Not possible (no H2 upstream)* | **49,302**<br><sub>(84 MiB / 42.8% CPU)</sub> | **57,184**<br><sub>(125 MiB / 52.9% CPU)</sub> | **65,110**<br><sub>(145 MiB / 48.7% CPU)</sub> |
| HTTP/1 · TLS | HTTP/2 · TLS | **32,756**<br><sub>(159 MiB / 46.3% CPU)</sub> | *Not possible (no H2 upstream)* | 🥇 **35,996**<br><sub>(83 MiB / 41.5% CPU)</sub> | **24,764**<br><sub>(125 MiB / 58.1% CPU)</sub> | **31,093**<br><sub>(140 MiB / 47.4% CPU)</sub> |
| HTTP/1 · TLS | HTTP/3 · QUIC | 🥇 **18,502**<br><sub>(159 MiB / 52.2% CPU)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **9,822**<br><sub>(130 MiB / 55.3% CPU)</sub> | **16,483**<br><sub>(160 MiB / 50.1% CPU)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | 🥇 **75,930**<br><sub>(123 MiB / 52.3% CPU)</sub> | **30,389**<br><sub>(79 MiB / 18.6% CPU)</sub> | **48,507**<br><sub>(66 MiB / 24.5% CPU)</sub> | **34,408**<br><sub>(118 MiB / 22.9% CPU)</sub> | **75,776**<br><sub>(113 MiB / 47.3% CPU)</sub> |
| HTTP/2 · plain | HTTP/1 · TLS | 🥇 **35,264**<br><sub>(130 MiB / 46.5% CPU)</sub> | **22,081**<br><sub>(101 MiB / 19.4% CPU)</sub> | **29,384**<br><sub>(71 MiB / 24.5% CPU)</sub> | **16,638**<br><sub>(120 MiB / 23.4% CPU)</sub> | **34,108**<br><sub>(125 MiB / 46.8% CPU)</sub> |
| HTTP/2 · plain | HTTP/2 · plain | 🥇 **83,999**<br><sub>(88 MiB / 35.8% CPU)</sub> | *Not possible (no H2 upstream)* | **24,930**<br><sub>(66 MiB / 24.4% CPU)</sub> | **15,870**<br><sub>(116 MiB / 22.7% CPU)</sub> | **48,608**<br><sub>(120 MiB / 47% CPU)</sub> |
| HTTP/2 · plain | HTTP/2 · TLS | 🥇 **135,665**<br><sub>(89 MiB / 41.8% CPU)</sub> | *Not possible (no H2 upstream)* | **44,762**<br><sub>(67 MiB / 24.5% CPU)</sub> | **43,297**<br><sub>(119 MiB / 21.1% CPU)</sub> | **91,859**<br><sub>(139 MiB / 44.5% CPU)</sub> |
| HTTP/2 · plain | HTTP/3 · QUIC | 🥇 **28,630**<br><sub>(159 MiB / 49.1% CPU)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **6,135**<br><sub>(121 MiB / 24.8% CPU)</sub> | **27,237**<br><sub>(156 MiB / 46.1% CPU)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **34,446**<br><sub>(115 MiB / 52% CPU)</sub> | **15,396**<br><sub>(100 MiB / 19.3% CPU)</sub> | **20,902**<br><sub>(80 MiB / 24.6% CPU)</sub> | **13,665**<br><sub>(128 MiB / 23% CPU)</sub> | **29,304**<br><sub>(122 MiB / 49.9% CPU)</sub> |
| HTTP/2 · TLS | HTTP/1 · TLS | 🥇 **61,153**<br><sub>(132 MiB / 50.2% CPU)</sub> | **27,615**<br><sub>(111 MiB / 19% CPU)</sub> | **38,794**<br><sub>(86 MiB / 24.6% CPU)</sub> | **31,754**<br><sub>(127 MiB / 22.7% CPU)</sub> | **57,545**<br><sub>(126 MiB / 46.8% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | 🥇 **82,786**<br><sub>(100 MiB / 33.1% CPU)</sub> | *Not possible (no H2 upstream)* | **33,706**<br><sub>(84 MiB / 24.2% CPU)</sub> | **19,595**<br><sub>(126 MiB / 21.5% CPU)</sub> | **45,168**<br><sub>(131 MiB / 45.7% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **54,495**<br><sub>(105 MiB / 35.6% CPU)</sub> | *Not possible (no H2 upstream)* | **19,480**<br><sub>(82 MiB / 24.4% CPU)</sub> | **15,870**<br><sub>(125 MiB / 21.3% CPU)</sub> | **33,695**<br><sub>(129 MiB / 46.2% CPU)</sub> |
| HTTP/2 · TLS | HTTP/3 · QUIC | 🥇 **55,217**<br><sub>(191 MiB / 52.4% CPU)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **17,298**<br><sub>(130 MiB / 24.7% CPU)</sub> | **49,234**<br><sub>(173 MiB / 45.6% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | **24,864**<br><sub>(150 MiB / 46.4% CPU)</sub> | **0**<br><sub>(peak 27,388 · 111 MiB / 20% CPU)</sub> | 🥇 **30,899**<br><sub>(86 MiB / 24.9% CPU)</sub> | **1,231**<br><sub>(134 MiB / 5.3% CPU)</sub> | **22,944**<br><sub>(183 MiB / 48.4% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | **16,430**<br><sub>(155 MiB / 48% CPU)</sub> | **0**<br><sub>(peak 11,638 · 117 MiB / 23.1% CPU)</sub> | 🥇 **17,695**<br><sub>(90 MiB / 29.2% CPU)</sub> | **199**<br><sub>(134 MiB / 1.4% CPU)</sub> | **15,361**<br><sub>(197 MiB / 51.7% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/2 · plain | 🥇 **63,152**<br><sub>(200 MiB / 57.4% CPU)</sub> | *Not possible (no H2 upstream)* | **56,727**<br><sub>(88 MiB / 25.2% CPU)</sub> | **4**<br><sub>(130 MiB / 0% CPU)</sub> | **53,018**<br><sub>(230 MiB / 46.8% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | **26,713**<br><sub>(158 MiB / 47.6% CPU)</sub> | *Not possible (no H2 upstream)* | 🥇 **29,230**<br><sub>(86 MiB / 24.3% CPU)</sub> | **467**<br><sub>(130 MiB / 2% CPU)</sub> | **23,675**<br><sub>(198 MiB / 47% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/3 · QUIC | 🥇 **20,511**<br><sub>(154 MiB / 46.7% CPU)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **2,337**<br><sub>(130 MiB / 25% CPU)</sub> | **15,864**<br><sub>(209 MiB / 48.2% CPU)</sub> |

### MITM (TWP only)

Same Client×Origin wires with interception on (`compare-product` [36280055132](https://github.com/justcoding121/titanium-web-proxy/actions/runs/36280055132)). **Lite** = no-op handlers (unchanged-lite finish). **Full** = append-only header mutation (harness: one probe header each way; product: generic append-only relay via `MitmCompressedRelayHelper`). nginx/HAProxy/Envoy/YARP cannot MITM. **Lite÷Reverse** / **Full÷Reverse** vs bare reverse (**same job / comparison-group shard**). Completion gate: Lite ≥ **0.50×** and Full ≥ **0.50×** reverse sustain @ c=64 (median of 3 GHA runs); reverse TWP÷YARP ≥ **0.75×** (no terminate-peer gate).

**v1 append-only relay (2026-08-27):** Pre-fix H2→H2 Full÷Reverse was **0.13–0.16×** ([32960766249](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32960766249)). Post-fix @ `df172718`: H2 plain→H2 plain Full **0.77–0.79×**, H3→H1 Full **0.91–0.93×**, all MITM arms ≥ **0.70×** on median of [33041445371](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33041445371), [33055267086](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33055267086), [33055272140](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33055272140).

**v2 drop-only + non-unique append (2026-08-27):** `MitmStaticRebuildHelper` rebuilds static HPACK/QPACK after 1–4 unique header drops; trailing non-unique appends stay on compressed relay. @ `7c029d65`: all MITM arms ≥ **0.70×** on GHA median ([33087088466](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087088466), [33087091622](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087091622), [33105885748](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33105885748) Linux; [33087085235](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087085235), [33087088466](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087088466), [33087091622](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087091622) Windows). H2 plain→H2 plain Full **0.77–0.79×** (Win) / **0.78×** (Lin).

| Client | Origin | Lite sustain | Full sustain | Lite÷Reverse | Full÷Reverse |
|---|---|---:|---:|---:|---:|
| HTTP/1 · plain | HTTP/1 · plain | **31,971**<br><sub>(96 MiB / 50.2% CPU)</sub> | **31,764**<br><sub>(97 MiB / 49.9% CPU)</sub> | **0.98×** | **0.97×** |
| HTTP/1 · plain | HTTP/1 · TLS | **56,428**<br><sub>(110 MiB / 48.5% CPU)</sub> | **55,415**<br><sub>(112 MiB / 47.9% CPU)</sub> | **1.01×** | **0.99×** |
| HTTP/1 · plain | HTTP/2 · plain | **50,258**<br><sub>(136 MiB / 52.7% CPU)</sub> | **48,307**<br><sub>(136 MiB / 51.4% CPU)</sub> | **0.96×** | **0.92×** |
| HTTP/1 · plain | HTTP/2 · TLS | **31,956**<br><sub>(149 MiB / 50.8% CPU)</sub> | **30,947**<br><sub>(148 MiB / 50.2% CPU)</sub> | **0.96×** | **0.93×** |
| HTTP/1 · plain | HTTP/3 · QUIC | **54,044**<br><sub>(174 MiB / 55.8% CPU)</sub> | **52,333**<br><sub>(170 MiB / 56.8% CPU)</sub> | **1×** | **0.96×** |
| HTTP/1 · TLS | HTTP/1 · plain | **34,218**<br><sub>(117 MiB / 48.9% CPU)</sub> | **33,840**<br><sub>(116 MiB / 47.8% CPU)</sub> | **1×** | **0.99×** |
| HTTP/1 · TLS | HTTP/1 · TLS | **19,887**<br><sub>(116 MiB / 49.5% CPU)</sub> | **19,473**<br><sub>(116 MiB / 48.9% CPU)</sub> | **1×** | **0.98×** |
| HTTP/1 · TLS | HTTP/2 · plain | **72,292**<br><sub>(158 MiB / 51.5% CPU)</sub> | **70,736**<br><sub>(163 MiB / 51.9% CPU)</sub> | **1×** | **0.98×** |
| HTTP/1 · TLS | HTTP/2 · TLS | **32,128**<br><sub>(158 MiB / 49% CPU)</sub> | **31,714**<br><sub>(160 MiB / 49.5% CPU)</sub> | **0.98×** | **0.97×** |
| HTTP/1 · TLS | HTTP/3 · QUIC | **18,059**<br><sub>(159 MiB / 52.7% CPU)</sub> | **17,550**<br><sub>(159 MiB / 53% CPU)</sub> | **0.98×** | **0.95×** |
| HTTP/2 · plain | HTTP/1 · plain | **82,766**<br><sub>(126 MiB / 53.7% CPU)</sub> | **77,401**<br><sub>(129 MiB / 52.7% CPU)</sub> | **1.09×** | **1.02×** |
| HTTP/2 · plain | HTTP/1 · TLS | **36,562**<br><sub>(131 MiB / 48.8% CPU)</sub> | **35,107**<br><sub>(129 MiB / 48.4% CPU)</sub> | **1.04×** | **1×** |
| HTTP/2 · plain | HTTP/2 · plain | **62,402**<br><sub>(94 MiB / 41.9% CPU)</sub> | **58,540**<br><sub>(91 MiB / 42.9% CPU)</sub> | **0.74×** | **0.7×** |
| HTTP/2 · plain | HTTP/2 · TLS | **113,714**<br><sub>(101 MiB / 44.7% CPU)</sub> | **106,610**<br><sub>(103 MiB / 43.6% CPU)</sub> | **0.84×** | **0.79×** |
| HTTP/2 · plain | HTTP/3 · QUIC | **28,493**<br><sub>(152 MiB / 49% CPU)</sub> | **27,938**<br><sub>(148 MiB / 49% CPU)</sub> | **1×** | **0.98×** |
| HTTP/2 · TLS | HTTP/1 · plain | **35,434**<br><sub>(127 MiB / 54.2% CPU)</sub> | **33,322**<br><sub>(119 MiB / 53.6% CPU)</sub> | **1.03×** | **0.97×** |
| HTTP/2 · TLS | HTTP/1 · TLS | **63,715**<br><sub>(134 MiB / 51.2% CPU)</sub> | **61,002**<br><sub>(132 MiB / 50.8% CPU)</sub> | **1.04×** | **1×** |
| HTTP/2 · TLS | HTTP/2 · plain | **62,257**<br><sub>(111 MiB / 37% CPU)</sub> | **58,784**<br><sub>(110 MiB / 37.3% CPU)</sub> | **0.75×** | **0.71×** |
| HTTP/2 · TLS | HTTP/2 · TLS | **45,764**<br><sub>(107 MiB / 39.7% CPU)</sub> | **42,377**<br><sub>(120 MiB / 39.8% CPU)</sub> | **0.84×** | **0.78×** |
| HTTP/2 · TLS | HTTP/3 · QUIC | **55,867**<br><sub>(199 MiB / 52.4% CPU)</sub> | **55,513**<br><sub>(195 MiB / 53.1% CPU)</sub> | **1.01×** | **1.01×** |
| HTTP/3 · QUIC | HTTP/1 · plain | **23,809**<br><sub>(152 MiB / 47.2% CPU)</sub> | **23,463**<br><sub>(157 MiB / 47.9% CPU)</sub> | **0.96×** | **0.94×** |
| HTTP/3 · QUIC | HTTP/1 · TLS | **15,723**<br><sub>(166 MiB / 48.3% CPU)</sub> | **15,378**<br><sub>(167 MiB / 48.6% CPU)</sub> | **0.96×** | **0.94×** |
| HTTP/3 · QUIC | HTTP/2 · plain | **62,381**<br><sub>(203 MiB / 58.5% CPU)</sub> | **58,027**<br><sub>(207 MiB / 56% CPU)</sub> | **0.99×** | **0.92×** |
| HTTP/3 · QUIC | HTTP/2 · TLS | **26,432**<br><sub>(157 MiB / 48.5% CPU)</sub> | **25,516**<br><sub>(163 MiB / 48.1% CPU)</sub> | **0.99×** | **0.96×** |
| HTTP/3 · QUIC | HTTP/3 · QUIC | **19,477**<br><sub>(162 MiB / 49.8% CPU)</sub> | **18,853**<br><sub>(156 MiB / 49.1% CPU)</sub> | **0.95×** | **0.92×** |

## macOS — Titanium vs nginx vs HAProxy vs Envoy vs YARP

### Reverse

Median of **3 repeats** on `macos-15-intel` (4-core / 14 GB). Bare reverse 5×5 @ `7c029d65` — `compare-product` [36280055132](https://github.com/justcoding121/titanium-web-proxy/actions/runs/36280055132). Warmup 2s / measure 8s; concurrency 8, 16, 32, 64. Prefer TWP÷peer ratios over absolute RPS. **RPS cells** include median RSS / CPU at the peak-RPS step as `<br><sub>(MiB / CPU%)</sub>`. The RPS workflow installs Homebrew nginx (`http_v3_module`), Homebrew HAProxy with `USE_QUIC` (3.2 source fallback), Envoy (Homebrew bottle or pinned darwin-amd64 1.36.7), Homebrew `libmsquic` (+ `DYLD_*`), and YARP. Do not publish from `macos-latest` (3-core / 7 GB). Product 5×5 is **~56-byte JSON keep-alive GET**; H2/H3 same-protocol cells are mostly header work with a tiny body (Titanium best case) — see [Why this comparison is fair](#why-this-comparison-is-fair).

| Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---:|---:|---:|---:|---:|
| HTTP/1 · plain | HTTP/1 · plain | 🥇 **29,278**<br><sub>(83 MiB / 38.4% CPU)</sub> | **20,302**<br><sub>(52 MiB / 23.8% CPU)</sub> | **29,256**<br><sub>(50 MiB / 31.5% CPU)</sub> | **7,537**<br><sub>(72 MiB / 42.8% CPU)</sub> | **22,568**<br><sub>(102 MiB / 41.8% CPU)</sub> |
| HTTP/1 · plain | HTTP/1 · TLS | **12,196**<br><sub>(92 MiB / 38% CPU)</sub> | **5,606**<br><sub>(73 MiB / 18.7% CPU)</sub> | 🥇 **12,355**<br><sub>(55 MiB / 33.6% CPU)</sub> | **3,922**<br><sub>(76 MiB / 22% CPU)</sub> | **11,564**<br><sub>(123 MiB / 41.2% CPU)</sub> |
| HTTP/1 · plain | HTTP/2 · plain | **16,747**<br><sub>(90 MiB / 40% CPU)</sub> | *Not possible (no H2 upstream)* | **13,145**<br><sub>(51 MiB / 30.1% CPU)</sub> | **6,732**<br><sub>(72 MiB / 45.8% CPU)</sub> | 🥇 **20,220**<br><sub>(113 MiB / 40.4% CPU)</sub> |
| HTTP/1 · plain | HTTP/2 · TLS | **16,775**<br><sub>(102 MiB / 39.4% CPU)</sub> | *Not possible (no H2 upstream)* | **13,898**<br><sub>(52 MiB / 32.9% CPU)</sub> | **5,491**<br><sub>(74 MiB / 32.3% CPU)</sub> | 🥇 **23,399**<br><sub>(113 MiB / 41% CPU)</sub> |
| HTTP/1 · plain | HTTP/3 · QUIC | **4,846**<br><sub>(90 MiB / 48.5% CPU)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **4,997**<br><sub>(75 MiB / 45.8% CPU)</sub> | 🥇 **5,643**<br><sub>(108 MiB / 38.2% CPU)</sub> |
| HTTP/1 · TLS | HTTP/1 · plain | **11,057**<br><sub>(94 MiB / 37.9% CPU)</sub> | **8,733**<br><sub>(73 MiB / 25.5% CPU)</sub> | 🥇 **11,526**<br><sub>(65 MiB / 32.4% CPU)</sub> | **4,621**<br><sub>(79 MiB / 46.1% CPU)</sub> | **9,821**<br><sub>(111 MiB / 36.4% CPU)</sub> |
| HTTP/1 · TLS | HTTP/1 · TLS | 🥇 **19,232**<br><sub>(97 MiB / 38.2% CPU)</sub> | **11,874**<br><sub>(82 MiB / 31.1% CPU)</sub> | **17,496**<br><sub>(68 MiB / 36.1% CPU)</sub> | **5,120**<br><sub>(81 MiB / 41.5% CPU)</sub> | **15,033**<br><sub>(118 MiB / 38.1% CPU)</sub> |
| HTTP/1 · TLS | HTTP/2 · plain | **10,238**<br><sub>(106 MiB / 35.1% CPU)</sub> | *Not possible (no H2 upstream)* | **11,152**<br><sub>(66 MiB / 30.7% CPU)</sub> | **5,734**<br><sub>(80 MiB / 23.1% CPU)</sub> | 🥇 **19,135**<br><sub>(122 MiB / 35.6% CPU)</sub> |
| HTTP/1 · TLS | HTTP/2 · TLS | **9,031**<br><sub>(160 MiB / 33% CPU)</sub> | *Not possible (no H2 upstream)* | **11,281**<br><sub>(66 MiB / 35.4% CPU)</sub> | **5,358**<br><sub>(79 MiB / 46.6% CPU)</sub> | 🥇 **11,341**<br><sub>(119 MiB / 36.7% CPU)</sub> |
| HTTP/1 · TLS | HTTP/3 · QUIC | **4,095**<br><sub>(107 MiB / 52.2% CPU)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **4,453**<br><sub>(81 MiB / 46.8% CPU)</sub> | 🥇 **7,153**<br><sub>(119 MiB / 37% CPU)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | 🥇 **26,528**<br><sub>(88 MiB / 43.5% CPU)</sub> | **16,328**<br><sub>(58 MiB / 19.2% CPU)</sub> | **16,702**<br><sub>(56 MiB / 21% CPU)</sub> | **4,270**<br><sub>(74 MiB / 23.1% CPU)</sub> | **16,891**<br><sub>(105 MiB / 42.4% CPU)</sub> |
| HTTP/2 · plain | HTTP/1 · TLS | **11,894**<br><sub>(98 MiB / 42.2% CPU)</sub> | **12,121**<br><sub>(86 MiB / 19% CPU)</sub> | **8,519**<br><sub>(59 MiB / 21.4% CPU)</sub> | **3,123**<br><sub>(77 MiB / 21.2% CPU)</sub> | 🥇 **14,027**<br><sub>(124 MiB / 46.5% CPU)</sub> |
| HTTP/2 · plain | HTTP/2 · plain | 🥇 **46,976**<br><sub>(74 MiB / 31.4% CPU)</sub> | *Not possible (no H2 upstream)* | **16,415**<br><sub>(56 MiB / 20.9% CPU)</sub> | **10,964**<br><sub>(71 MiB / 22.1% CPU)</sub> | **37,743**<br><sub>(105 MiB / 41.7% CPU)</sub> |
| HTTP/2 · plain | HTTP/2 · TLS | 🥇 **49,028**<br><sub>(77 MiB / 32.3% CPU)</sub> | *Not possible (no H2 upstream)* | **14,393**<br><sub>(58 MiB / 20.8% CPU)</sub> | **9,638**<br><sub>(73 MiB / 21.2% CPU)</sub> | **33,329**<br><sub>(111 MiB / 40.6% CPU)</sub> |
| HTTP/2 · plain | HTTP/3 · QUIC | **5,021**<br><sub>(96 MiB / 48.5% CPU)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **2,358**<br><sub>(75 MiB / 23.4% CPU)</sub> | 🥇 **6,305**<br><sub>(118 MiB / 38.5% CPU)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **35,362**<br><sub>(91 MiB / 46.8% CPU)</sub> | **22,272**<br><sub>(73 MiB / 20.2% CPU)</sub> | **15,957**<br><sub>(68 MiB / 21.5% CPU)</sub> | **4,790**<br><sub>(82 MiB / 22.6% CPU)</sub> | **27,024**<br><sub>(111 MiB / 44.6% CPU)</sub> |
| HTTP/2 · TLS | HTTP/1 · TLS | 🥇 **23,602**<br><sub>(97 MiB / 45.5% CPU)</sub> | **14,733**<br><sub>(90 MiB / 19.8% CPU)</sub> | **8,573**<br><sub>(69 MiB / 21.3% CPU)</sub> | **5,354**<br><sub>(83 MiB / 21.4% CPU)</sub> | **19,955**<br><sub>(120 MiB / 45.1% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | 🥇 **36,114**<br><sub>(85 MiB / 34.3% CPU)</sub> | *Not possible (no H2 upstream)* | **9,099**<br><sub>(67 MiB / 21% CPU)</sub> | **7,358**<br><sub>(79 MiB / 21.4% CPU)</sub> | **23,009**<br><sub>(115 MiB / 41.2% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **37,727**<br><sub>(85 MiB / 34.5% CPU)</sub> | *Not possible (no H2 upstream)* | **11,747**<br><sub>(68 MiB / 21.5% CPU)</sub> | **7,325**<br><sub>(79 MiB / 21.1% CPU)</sub> | **26,161**<br><sub>(116 MiB / 43.4% CPU)</sub> |
| HTTP/2 · TLS | HTTP/3 · QUIC | **7,558**<br><sub>(108 MiB / 45.3% CPU)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **3,943**<br><sub>(81 MiB / 23.9% CPU)</sub> | 🥇 **8,976**<br><sub>(119 MiB / 39.4% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | **5,073**<br><sub>(101 MiB / 41.8% CPU)</sub> | *Not measured* | 🥇 **9,349**<br><sub>(66 MiB / 24.3% CPU)</sub> | **1,304**<br><sub>(85 MiB / 27.9% CPU)</sub> | **6,346**<br><sub>(182 MiB / 40.6% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | **5,025**<br><sub>(118 MiB / 46.4% CPU)</sub> | **0**<br><sub>(peak 10,294 · 67 MiB / 17.2% CPU)</sub> | 🥇 **10,808**<br><sub>(69 MiB / 26.4% CPU)</sub> | **2,348**<br><sub>(87 MiB / 22.9% CPU)</sub> | **8,115**<br><sub>(189 MiB / 41.2% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/2 · plain | **7,916**<br><sub>(99 MiB / 46.8% CPU)</sub> | *Not possible (no H2 upstream)* | 🥇 **12,490**<br><sub>(66 MiB / 23.2% CPU)</sub> | **3,178**<br><sub>(83 MiB / 35.8% CPU)</sub> | **10,252**<br><sub>(164 MiB / 38.8% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | **5,692**<br><sub>(107 MiB / 44.4% CPU)</sub> | *Not possible (no H2 upstream)* | **7,002**<br><sub>(68 MiB / 21.5% CPU)</sub> | **2,473**<br><sub>(83 MiB / 34.8% CPU)</sub> | 🥇 **7,106**<br><sub>(166 MiB / 40% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/3 · QUIC | **5,588**<br><sub>(100 MiB / 54% CPU)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **1,637**<br><sub>(81 MiB / 28.4% CPU)</sub> | 🥇 **7,695**<br><sub>(167 MiB / 38.7% CPU)</sub> |

### MITM (TWP only)

Same Client×Origin wires with interception on (`compare-product` [36280055132](https://github.com/justcoding121/titanium-web-proxy/actions/runs/36280055132)). **Lite** = no-op handlers (unchanged-lite finish). **Full** = append-only header mutation (harness: one probe header each way; product: generic append-only relay via `MitmCompressedRelayHelper`). nginx/HAProxy/Envoy/YARP cannot MITM. **Lite÷Reverse** / **Full÷Reverse** vs bare reverse (**same job / comparison-group shard**). Completion gate: Lite ≥ **0.50×** and Full ≥ **0.50×** reverse sustain @ c=64 (median of 3 GHA runs); reverse TWP÷YARP ≥ **0.75×** (no terminate-peer gate).

**v1 append-only relay (2026-08-27):** Pre-fix H2→H2 Full÷Reverse was **0.13–0.16×** ([32960766249](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32960766249)). Post-fix @ `df172718`: H2 plain→H2 plain Full **0.77–0.79×**, H3→H1 Full **0.91–0.93×**, all MITM arms ≥ **0.70×** on median of [33041445371](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33041445371), [33055267086](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33055267086), [33055272140](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33055272140).

**v2 drop-only + non-unique append (2026-08-27):** `MitmStaticRebuildHelper` rebuilds static HPACK/QPACK after 1–4 unique header drops; trailing non-unique appends stay on compressed relay. @ `7c029d65`: all MITM arms ≥ **0.70×** on GHA median ([33087088466](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087088466), [33087091622](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087091622), [33105885748](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33105885748) Linux; [33087085235](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087085235), [33087088466](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087088466), [33087091622](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087091622) Windows). H2 plain→H2 plain Full **0.77–0.79×** (Win) / **0.78×** (Lin).

| Client | Origin | Lite sustain | Full sustain | Lite÷Reverse | Full÷Reverse |
|---|---|---:|---:|---:|---:|
| HTTP/1 · plain | HTTP/1 · plain | **29,878**<br><sub>(84 MiB / 39.6% CPU)</sub> | **28,689**<br><sub>(84 MiB / 39% CPU)</sub> | **1.02×** | **0.98×** |
| HTTP/1 · plain | HTTP/1 · TLS | **17,049**<br><sub>(95 MiB / 40.8% CPU)</sub> | **20,338**<br><sub>(95 MiB / 41% CPU)</sub> | **1.4×** | **1.67×** |
| HTTP/1 · plain | HTTP/2 · plain | **17,020**<br><sub>(94 MiB / 44.2% CPU)</sub> | **14,672**<br><sub>(90 MiB / 41.1% CPU)</sub> | **1.02×** | **0.88×** |
| HTTP/1 · plain | HTTP/2 · TLS | **24,563**<br><sub>(122 MiB / 40.3% CPU)</sub> | **25,136**<br><sub>(108 MiB / 43.3% CPU)</sub> | **1.46×** | **1.5×** |
| HTTP/1 · plain | HTTP/3 · QUIC | **5,502**<br><sub>(93 MiB / 50.5% CPU)</sub> | **6,196**<br><sub>(92 MiB / 50.4% CPU)</sub> | **1.14×** | **1.28×** |
| HTTP/1 · TLS | HTTP/1 · plain | **10,251**<br><sub>(94 MiB / 36.3% CPU)</sub> | **9,238**<br><sub>(94 MiB / 35.5% CPU)</sub> | **0.93×** | **0.84×** |
| HTTP/1 · TLS | HTTP/1 · TLS | **16,705**<br><sub>(99 MiB / 38.5% CPU)</sub> | **16,786**<br><sub>(99 MiB / 39% CPU)</sub> | **0.87×** | **0.87×** |
| HTTP/1 · TLS | HTTP/2 · plain | **14,852**<br><sub>(103 MiB / 39% CPU)</sub> | **18,711**<br><sub>(113 MiB / 41.8% CPU)</sub> | **1.45×** | **1.83×** |
| HTTP/1 · TLS | HTTP/2 · TLS | **9,504**<br><sub>(163 MiB / 38.5% CPU)</sub> | **8,095**<br><sub>(161 MiB / 37.1% CPU)</sub> | **1.05×** | **0.9×** |
| HTTP/1 · TLS | HTTP/3 · QUIC | **4,621**<br><sub>(101 MiB / 55.4% CPU)</sub> | **5,378**<br><sub>(103 MiB / 55.4% CPU)</sub> | **1.13×** | **1.31×** |
| HTTP/2 · plain | HTTP/1 · plain | **26,099**<br><sub>(91 MiB / 44.4% CPU)</sub> | **25,269**<br><sub>(89 MiB / 45.6% CPU)</sub> | **0.98×** | **0.95×** |
| HTTP/2 · plain | HTTP/1 · TLS | **15,550**<br><sub>(97 MiB / 45.3% CPU)</sub> | **14,004**<br><sub>(96 MiB / 45.1% CPU)</sub> | **1.31×** | **1.18×** |
| HTTP/2 · plain | HTTP/2 · plain | **51,811**<br><sub>(75 MiB / 36.6% CPU)</sub> | **51,108**<br><sub>(77 MiB / 36.7% CPU)</sub> | **1.1×** | **1.09×** |
| HTTP/2 · plain | HTTP/2 · TLS | **44,560**<br><sub>(81 MiB / 36.4% CPU)</sub> | **25,687**<br><sub>(81 MiB / 35.8% CPU)</sub> | **0.91×** | **0.52×** |
| HTTP/2 · plain | HTTP/3 · QUIC | **5,491**<br><sub>(95 MiB / 46.5% CPU)</sub> | **4,836**<br><sub>(95 MiB / 55.1% CPU)</sub> | **1.09×** | **0.96×** |
| HTTP/2 · TLS | HTTP/1 · plain | **26,262**<br><sub>(92 MiB / 48.7% CPU)</sub> | **30,831**<br><sub>(92 MiB / 47.5% CPU)</sub> | **0.74×** | **0.87×** |
| HTTP/2 · TLS | HTTP/1 · TLS | **23,998**<br><sub>(96 MiB / 48.5% CPU)</sub> | **15,013**<br><sub>(98 MiB / 46.8% CPU)</sub> | **1.02×** | **0.64×** |
| HTTP/2 · TLS | HTTP/2 · plain | **28,484**<br><sub>(86 MiB / 39.4% CPU)</sub> | **29,652**<br><sub>(88 MiB / 38.7% CPU)</sub> | **0.79×** | **0.82×** |
| HTTP/2 · TLS | HTTP/2 · TLS | **29,601**<br><sub>(88 MiB / 33.1% CPU)</sub> | **45,433**<br><sub>(91 MiB / 37.1% CPU)</sub> | **0.78×** | **1.2×** |
| HTTP/2 · TLS | HTTP/3 · QUIC | **7,320**<br><sub>(107 MiB / 46% CPU)</sub> | **5,490**<br><sub>(108 MiB / 49.4% CPU)</sub> | **0.97×** | **0.73×** |
| HTTP/3 · QUIC | HTTP/1 · plain | **4,439**<br><sub>(103 MiB / 44.3% CPU)</sub> | **4,603**<br><sub>(102 MiB / 43.5% CPU)</sub> | **0.88×** | **0.91×** |
| HTTP/3 · QUIC | HTTP/1 · TLS | **7,109**<br><sub>(116 MiB / 46% CPU)</sub> | **6,857**<br><sub>(116 MiB / 46.7% CPU)</sub> | **1.41×** | **1.36×** |
| HTTP/3 · QUIC | HTTP/2 · plain | **7,698**<br><sub>(100 MiB / 45.7% CPU)</sub> | **6,238**<br><sub>(98 MiB / 47.9% CPU)</sub> | **0.97×** | **0.79×** |
| HTTP/3 · QUIC | HTTP/2 · TLS | **5,467**<br><sub>(108 MiB / 43.2% CPU)</sub> | **5,022**<br><sub>(107 MiB / 49.4% CPU)</sub> | **0.96×** | **0.88×** |
| HTTP/3 · QUIC | HTTP/3 · QUIC | **5,501**<br><sub>(96 MiB / 43.6% CPU)</sub> | **5,507**<br><sub>(97 MiB / 42.7% CPU)</sub> | **0.98×** | **0.99×** |

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

Median of **3** repeats on `windows-latest` @ `7c029d65`. Source: Actions [36280061242](https://github.com/justcoding121/titanium-web-proxy/actions/runs/36280061242) (`compare-bodies`). Warmup 2s / measure 8s. **RPS cells** include `(MiB / CPU%)` footprints.

*Not possible:* **HAProxy** and **Envoy** columns are omitted (no official Windows port).



| Body | Client | Origin | TWP | nginx | YARP |
|---|---|---|---:|---:|---:|
| 64 KiB | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **9,430**<br><sub>(92 MiB / 46.5% CPU)</sub> | **596**<br><sub>(142 MiB / 24.6% CPU)</sub> | **8,118**<br><sub>(135 MiB / 47.0% CPU)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **9,011**<br><sub>(184 MiB / 46.4% CPU)</sub> | **624**<br><sub>(142 MiB / 24.8% CPU)</sub> | **7,507**<br><sub>(134 MiB / 50.0% CPU)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **4,186**<br><sub>(131 MiB / 38.2% CPU)</sub> | *Not possible (no QUIC)* | **3,886**<br><sub>(190 MiB / 47.8% CPU)</sub> |
| 256 KiB | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **2,216**<br><sub>(123 MiB / 42.4% CPU)</sub> | **0**<br><sub>(peak 156 · 143 MiB / 24.8% CPU)</sub> | **1,759**<br><sub>(136 MiB / 36.7% CPU)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **2,658**<br><sub>(149 MiB / 38.8% CPU)</sub> | **0**<br><sub>(peak 157 · 142 MiB / 24.6% CPU)</sub> | **1,793**<br><sub>(133 MiB / 38.0% CPU)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **1,094**<br><sub>(110 MiB / 39.5% CPU)</sub> | *Not possible (no QUIC)* | **1,088**<br><sub>(175 MiB / 43.9% CPU)</sub> |
| 64 KiB | HTTP/2 · plain | HTTP/1 · plain | 🥇 **12,639**<br><sub>(177 MiB / 40.9% CPU)</sub> | **2,592**<br><sub>(127 MiB / 24.6% CPU)</sub> | **10,708**<br><sub>(118 MiB / 44.1% CPU)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/2 · plain | 🥇 **9,991**<br><sub>(114 MiB / 38.0% CPU)</sub> | *Not possible* | **7,401**<br><sub>(137 MiB / 50.8% CPU)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **8,061**<br><sub>(117 MiB / 37.0% CPU)</sub> | *Not possible* | **6,243**<br><sub>(128 MiB / 48.1% CPU)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/2 · TLS | **3,532**<br><sub>(124 MiB / 44.6% CPU)</sub> | *Not possible* | 🥇 **3,830**<br><sub>(189 MiB / 46.0% CPU)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **4,677**<br><sub>(148 MiB / 41.8% CPU)</sub> | *Not possible (no QUIC)* | **3,630**<br><sub>(193 MiB / 48.7% CPU)</sub> |
| 256 KiB | HTTP/2 · plain | HTTP/1 · plain | 🥇 **3,649**<br><sub>(140 MiB / 31.1% CPU)</sub> | **790**<br><sub>(127 MiB / 24.4% CPU)</sub> | **2,978**<br><sub>(123 MiB / 38.7% CPU)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/2 · plain | **0**<br><sub>(69 MiB / 0.3% CPU)</sub> | *Not possible* | 🥇 **1,850**<br><sub>(161 MiB / 46.2% CPU)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/2 · TLS | **0**<br><sub>(70 MiB / 0.4% CPU)</sub> | *Not possible* | 🥇 **1,233**<br><sub>(149 MiB / 40.7% CPU)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/2 · TLS | **877**<br><sub>(147 MiB / 42.1% CPU)</sub> | *Not possible* | 🥇 **964**<br><sub>(186 MiB / 42.7% CPU)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **1,128**<br><sub>(145 MiB / 39.6% CPU)</sub> | *Not possible (no QUIC)* | **1,001**<br><sub>(191 MiB / 43.8% CPU)</sub> |

nginx/Windows collapses on large reverse bodies in this harness; treat as same-OS only. H1 TLS **64 KiB** ≈ **1.13×** YARP; **256 KiB** ≈ **1.08×**. H2→H1 64 KiB ≈ **1.17×**; H3→H1 64 KiB ≈ **1.06×**.

### Linux — heavier reverse GET (64 KiB / 256 KiB)

Median of **3** repeats @ `7c029d65`. Source: Actions [36280061242](https://github.com/justcoding121/titanium-web-proxy/actions/runs/36280061242) (`compare-bodies`). Warmup 2s / measure 8s.

| Body | Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---|---:|---:|---:|---:|---:|
| 64 KiB | HTTP/1 · TLS | HTTP/1 · plain | **8,449**<br><sub>(121 MiB / 44.1% CPU)</sub> | **5,494**<br><sub>(100 MiB / 51.6% CPU)</sub> | 🥇 **8,951**<br><sub>(84 MiB / 39.9% CPU)</sub> | **7,504**<br><sub>(130 MiB / 46.1% CPU)</sub> | **6,572**<br><sub>(167 MiB / 49.1% CPU)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/1 · plain | **7,489**<br><sub>(235 MiB / 39.9% CPU)</sub> | **2,667**<br><sub>(102 MiB / 12.9% CPU)</sub> | 🥇 **7,813**<br><sub>(86 MiB / 24.0% CPU)</sub> | **5,529**<br><sub>(138 MiB / 23.6% CPU)</sub> | **6,195**<br><sub>(161 MiB / 47.9% CPU)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **5,575**<br><sub>(179 MiB / 44.3% CPU)</sub> | **0**<br><sub>(peak 1,640 · 133 MiB / 21.9% CPU)</sub> | **4,150**<br><sub>(88 MiB / 28.4% CPU)</sub> | **1**<br><sub>(138 MiB / 0.1% CPU)</sub> | **4,310**<br><sub>(224 MiB / 51.4% CPU)</sub> |
| 256 KiB | HTTP/1 · TLS | HTTP/1 · plain | **2,755**<br><sub>(125 MiB / 37.1% CPU)</sub> | **1,583**<br><sub>(100 MiB / 51.6% CPU)</sub> | 🥇 **2,969**<br><sub>(84 MiB / 33.3% CPU)</sub> | **2,689**<br><sub>(145 MiB / 34.0% CPU)</sub> | **2,103**<br><sub>(171 MiB / 45.7% CPU)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/1 · plain | **2,011**<br><sub>(202 MiB / 32.4% CPU)</sub> | **1,041**<br><sub>(101 MiB / 17.3% CPU)</sub> | 🥇 **2,484**<br><sub>(84 MiB / 20.5% CPU)</sub> | **2,066**<br><sub>(162 MiB / 23.5% CPU)</sub> | **1,630**<br><sub>(162 MiB / 42.0% CPU)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **1,437**<br><sub>(157 MiB / 42.8% CPU)</sub> | **0**<br><sub>(0 MiB / 0.0% CPU)</sub> | **1,282**<br><sub>(89 MiB / 31.2% CPU)</sub> | **0**<br><sub>(156 MiB / 0.2% CPU)</sub> | **1,277**<br><sub>(220 MiB / 47.5% CPU)</sub> |
| 64 KiB | HTTP/2 · plain | HTTP/1 · plain | 🥇 **10,488**<br><sub>(232 MiB / 40.9% CPU)</sub> | **4,135**<br><sub>(80 MiB / 14.9% CPU)</sub> | **9,750**<br><sub>(69 MiB / 24.2% CPU)</sub> | **7,969**<br><sub>(131 MiB / 23.7% CPU)</sub> | **9,359**<br><sub>(156 MiB / 46.3% CPU)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/2 · plain | 🥇 **5,775**<br><sub>(157 MiB / 35.1% CPU)</sub> | *Not possible* | **2,152**<br><sub>(90 MiB / 24.7% CPU)</sub> | **4,521**<br><sub>(143 MiB / 22.4% CPU)</sub> | **4,503**<br><sub>(181 MiB / 45.8% CPU)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **5,596**<br><sub>(147 MiB / 33.9% CPU)</sub> | *Not possible* | **2,785**<br><sub>(82 MiB / 24.5% CPU)</sub> | **4,278**<br><sub>(149 MiB / 23.6% CPU)</sub> | **4,581**<br><sub>(181 MiB / 45.7% CPU)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/2 · TLS | **2,938**<br><sub>(166 MiB / 53.3% CPU)</sub> | *Not possible* | **2,136**<br><sub>(93 MiB / 35.5% CPU)</sub> | **126**<br><sub>(145 MiB / 1.5% CPU)</sub> | 🥇 **3,032**<br><sub>(248 MiB / 49.2% CPU)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **4,726**<br><sub>(206 MiB / 41.8% CPU)</sub> | **0**<br><sub>(peak 1,555 · 143 MiB / 16.0% CPU)</sub> | **3,874**<br><sub>(95 MiB / 29.9% CPU)</sub> | **5**<br><sub>(137 MiB / 0.1% CPU)</sub> | **3,792**<br><sub>(241 MiB / 49.0% CPU)</sub> |
| 256 KiB | HTTP/2 · plain | HTTP/1 · plain | **2,963**<br><sub>(209 MiB / 33.1% CPU)</sub> | **1,813**<br><sub>(80 MiB / 16.2% CPU)</sub> | 🥇 **3,376**<br><sub>(68 MiB / 19.1% CPU)</sub> | **3,017**<br><sub>(157 MiB / 23.2% CPU)</sub> | **2,771**<br><sub>(156 MiB / 39.9% CPU)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/2 · plain | **0**<br><sub>(105 MiB / 0.2% CPU)</sub> | *Not possible* | **614**<br><sub>(91 MiB / 24.3% CPU)</sub> | 🥇 **1,739**<br><sub>(167 MiB / 22.8% CPU)</sub> | **1,158**<br><sub>(187 MiB / 39.3% CPU)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/2 · TLS | **0**<br><sub>(107 MiB / 0.3% CPU)</sub> | *Not possible* | **786**<br><sub>(86 MiB / 24.0% CPU)</sub> | 🥇 **1,265**<br><sub>(161 MiB / 22.9% CPU)</sub> | **1,236**<br><sub>(185 MiB / 42.0% CPU)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/2 · TLS | **832**<br><sub>(181 MiB / 50.3% CPU)</sub> | *Not possible* | **657**<br><sub>(92 MiB / 37.4% CPU)</sub> | **642**<br><sub>(162 MiB / 22.1% CPU)</sub> | 🥇 **909**<br><sub>(225 MiB / 46.9% CPU)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **1,267**<br><sub>(185 MiB / 42.5% CPU)</sub> | **438**<br><sub>(142 MiB / 17.4% CPU)</sub> | **1,156**<br><sub>(95 MiB / 29.5% CPU)</sub> | **0**<br><sub>(147 MiB / 0.1% CPU)</sub> | **1,114**<br><sub>(243 MiB / 47.5% CPU)</sub> |

On this GHA pass TWP÷YARP H1 TLS ≈ **1.21×** (64 KiB) / **1.26×** (256 KiB); H2→H1 ≈ **1.20×** / **1.15×**; H3→H1 ≈ **1.29×** / **1.16×**. TWP÷nginx H1 TLS ≈ **1.43** / **1.85**. Absolute RPS swings by VM; prefer ratios.

### Windows — POST 64 KiB request + 64 KiB response

Median of **3** repeats on `windows-latest` @ `7c029d65`. Source: Actions [36280064605](https://github.com/justcoding121/titanium-web-proxy/actions/runs/36280064605) (`compare-post`).

*Not possible:* **HAProxy** and **Envoy** columns are omitted (no official Windows port).



| Client | Origin | TWP | nginx | YARP |
|---|---|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | 🥇 **6,028**<br><sub>(96 MiB / 44.9% CPU)</sub> | **361**<br><sub>(142 MiB / 24.7% CPU)</sub> | **4,272**<br><sub>(138 MiB / 55.6% CPU)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **4,176**<br><sub>(182 MiB / 48.0% CPU)</sub> | **348**<br><sub>(144 MiB / 24.9% CPU)</sub> | **3,470**<br><sub>(135 MiB / 50.3% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | **2,062**<br><sub>(170 MiB / 39.2% CPU)</sub> | *Not possible (no QUIC)* | 🥇 **2,073**<br><sub>(207 MiB / 48.9% CPU)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | 🥇 **6,512**<br><sub>(179 MiB / 45.9% CPU)</sub> | **1,632**<br><sub>(130 MiB / 23.9% CPU)</sub> | **6,041**<br><sub>(124 MiB / 51.2% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | *Not measured* | *Not possible* | 🥇 **3,720**<br><sub>(142 MiB / 48.3% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | *Not measured* | *Not possible* | 🥇 **2,913**<br><sub>(146 MiB / 47.5% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | **1,832**<br><sub>(181 MiB / 47.0% CPU)</sub> | *Not possible* | 🥇 **1,947**<br><sub>(207 MiB / 48.7% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **1,960**<br><sub>(182 MiB / 40.0% CPU)</sub> | *Not possible (no QUIC)* | **1,930**<br><sub>(206 MiB / 49.4% CPU)</sub> |

TWP leads H1 POST (~**1.4×** YARP), H2→H1 POST (~**1.5×** YARP), and H3 POST (~**1.05×** YARP). H2 TLS→H2 TLS POST sustain is ~0 this pass (same concurrent-copier cell as duplex).

### Linux — POST 64 KiB request + 64 KiB response

Median of **3** repeats @ `7c029d65`. Source: Actions [36280064605](https://github.com/justcoding121/titanium-web-proxy/actions/runs/36280064605) (`compare-post`).

| Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---:|---:|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | **4,856**<br><sub>(136 MiB / 45.0% CPU)</sub> | **3,698**<br><sub>(100 MiB / 49.5% CPU)</sub> | 🥇 **5,122**<br><sub>(83 MiB / 41.5% CPU)</sub> | **5,054**<br><sub>(132 MiB / 42.0% CPU)</sub> | **3,237**<br><sub>(174 MiB / 55.3% CPU)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **3,118**<br><sub>(225 MiB / 47.0% CPU)</sub> | **1,397**<br><sub>(112 MiB / 22.3% CPU)</sub> | **1,868**<br><sub>(84 MiB / 24.6% CPU)</sub> | **0**<br><sub>(peak 3,018 · 144 MiB / 22.3% CPU)</sub> | **2,510**<br><sub>(167 MiB / 48.9% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **2,869**<br><sub>(218 MiB / 43.5% CPU)</sub> | **464**<br><sub>(109 MiB / 24.8% CPU)</sub> | **2,079**<br><sub>(89 MiB / 35.2% CPU)</sub> | **0**<br><sub>(126 MiB / 0.1% CPU)</sub> | **2,611**<br><sub>(261 MiB / 49.3% CPU)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | 🥇 **5,651**<br><sub>(231 MiB / 44.9% CPU)</sub> | **2,338**<br><sub>(97 MiB / 22.6% CPU)</sub> | **2,780**<br><sub>(69 MiB / 24.5% CPU)</sub> | **0**<br><sub>(peak 4,576 · 130 MiB / 23.7% CPU)</sub> | **4,387**<br><sub>(160 MiB / 47.6% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | *Not measured* | *Not possible* | **1,316**<br><sub>(88 MiB / 24.1% CPU)</sub> | 🥇 **2,936**<br><sub>(139 MiB / 24.0% CPU)</sub> | **2,433**<br><sub>(180 MiB / 47.1% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | **0**<br><sub>(peak 411 · 212 MiB / 27.5% CPU)</sub> | *Not possible* | **1,033**<br><sub>(85 MiB / 24.6% CPU)</sub> | 🥇 **2,106**<br><sub>(152 MiB / 23.3% CPU)</sub> | **1,926**<br><sub>(178 MiB / 46.1% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | 🥇 **1,907**<br><sub>(240 MiB / 49.5% CPU)</sub> | *Not possible* | **1,150**<br><sub>(95 MiB / 31.3% CPU)</sub> | **0**<br><sub>(127 MiB / 0.1% CPU)</sub> | **1,835**<br><sub>(244 MiB / 47.5% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **2,174**<br><sub>(240 MiB / 44.9% CPU)</sub> | **374**<br><sub>(119 MiB / 25.0% CPU)</sub> | **1,542**<br><sub>(96 MiB / 33.1% CPU)</sub> | **0**<br><sub>(126 MiB / 0.1% CPU)</sub> | **2,008**<br><sub>(255 MiB / 48.5% CPU)</sub> |

Linux nginx H1/H2/H3 POST completed (nginx.org mainline). TWP÷YARP H1 ≈ **1.5×**; H2→H1 ≈ **1.2×**; H3 ≈ **1.1×**. TWP÷nginx H3 ≈ **6×**.

### Windows — lossy / high-RTT (H2 HOL / H3 loss)

Userspace **5 ms** one-way delay + **1%** TCP connection stall (H1/H2) or UDP datagram drop (H3); **64 KiB** GET. Median of **3** repeats on `windows-latest` @ `7c029d65` — [36280066037](https://github.com/justcoding121/titanium-web-proxy/actions/runs/36280066037) (`compare-lossy`).

*Not possible:* **HAProxy** and **Envoy** columns are omitted (no official Windows port).

| Client | Origin | TWP | nginx | YARP |
|---|---|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | **661**<br><sub>(87 MiB / 3.5% CPU)</sub> | **625**<br><sub>(142 MiB / 19.8% CPU)</sub> | 🥇 **662**<br><sub>(113 MiB / 4.6% CPU)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | **0**<br><sub>(peak 85 · 123 MiB / 1.6% CPU)</sub> | **0**<br><sub>(peak 17 · 142 MiB / 0.6% CPU)</sub> | **0**<br><sub>(peak 16 · 99 MiB / 0.8% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | **0**<br><sub>(67 MiB / 0.0% CPU)</sub> | *Not possible (no QUIC)* | **0**<br><sub>(79 MiB / 0.0% CPU)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | **0**<br><sub>(peak 86 · 135 MiB / 1.3% CPU)</sub> | **0**<br><sub>(peak 17 · 128 MiB / 0.1% CPU)</sub> | **0**<br><sub>(peak 16 · 92 MiB / 0.9% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | **0**<br><sub>(peak 16 · 85 MiB / 0.5% CPU)</sub> | *Not possible* | **0**<br><sub>(peak 16 · 100 MiB / 0.8% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | **0**<br><sub>(peak 16 · 87 MiB / 0.3% CPU)</sub> | *Not possible* | **0**<br><sub>(peak 16 · 98 MiB / 0.8% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | **0**<br><sub>(70 MiB / 0.0% CPU)</sub> | *Not possible* | **0**<br><sub>(79 MiB / 0.0% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | **0**<br><sub>(69 MiB / 0.0% CPU)</sub> | *Not possible (no QUIC)* | **0**<br><sub>(80 MiB / 0.0% CPU)</sub> |

H1 is near parity with YARP (~**0.98×**). H2 and H3 sustain are **0** on this Windows GHA pass (lossy HOL / QUIC); Linux table below is the publishable H2 HOL / H3 comparison. Laptop re-measure kept for Windows H3.

### Linux — lossy / high-RTT (H2 HOL / H3 loss)

Median of **3** repeats @ `7c029d65`. Source: [36280066037](https://github.com/justcoding121/titanium-web-proxy/actions/runs/36280066037) (`compare-lossy`; lossy H3 uses `quic-http3`).

| Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---:|---:|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | **1,198**<br><sub>(126 MiB / 12.9% CPU)</sub> | 🥇 **1,210**<br><sub>(100 MiB / 12.1% CPU)</sub> | **1,208**<br><sub>(82 MiB / 7.3% CPU)</sub> | **1,200**<br><sub>(126 MiB / 9.1% CPU)</sub> | **1,194**<br><sub>(152 MiB / 16.4% CPU)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **320**<br><sub>(177 MiB / 6.9% CPU)</sub> | **40**<br><sub>(99 MiB / 0.3% CPU)</sub> | **41**<br><sub>(85 MiB / 0.2% CPU)</sub> | **40**<br><sub>(139 MiB / 0.3% CPU)</sub> | **40**<br><sub>(128 MiB / 1.3% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | **329**<br><sub>(151 MiB / 13.6% CPU)</sub> | **92**<br><sub>(109 MiB / 2.4% CPU)</sub> | **57**<br><sub>(85 MiB / 3.3% CPU)</sub> | 🥇 **1,273**<br><sub>(140 MiB / 22.3% CPU)</sub> | **358**<br><sub>(180 MiB / 21.8% CPU)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | 🥇 **318**<br><sub>(179 MiB / 6.0% CPU)</sub> | **40**<br><sub>(78 MiB / 0.2% CPU)</sub> | **41**<br><sub>(68 MiB / 0.2% CPU)</sub> | **40**<br><sub>(127 MiB / 0.3% CPU)</sub> | **41**<br><sub>(125 MiB / 1.2% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | 🥇 **45**<br><sub>(111 MiB / 1.0% CPU)</sub> | *Not possible* | **41**<br><sub>(89 MiB / 0.4% CPU)</sub> | **40**<br><sub>(133 MiB / 0.3% CPU)</sub> | **40**<br><sub>(130 MiB / 1.3% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **40**<br><sub>(107 MiB / 1.0% CPU)</sub> | *Not possible* | **40**<br><sub>(82 MiB / 0.6% CPU)</sub> | **40**<br><sub>(135 MiB / 0.4% CPU)</sub> | **40**<br><sub>(126 MiB / 1.4% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | **341**<br><sub>(139 MiB / 25.1% CPU)</sub> | *Not possible* | **62**<br><sub>(91 MiB / 4.1% CPU)</sub> | 🥇 **1,125**<br><sub>(145 MiB / 23.4% CPU)</sub> | **326**<br><sub>(186 MiB / 24.0% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | **328**<br><sub>(165 MiB / 14.7% CPU)</sub> | **92**<br><sub>(113 MiB / 2.6% CPU)</sub> | **59**<br><sub>(90 MiB / 3.9% CPU)</sub> | 🥇 **1,190**<br><sub>(140 MiB / 23.9% CPU)</sub> | **326**<br><sub>(186 MiB / 22.0% CPU)</sub> |

TWP H2 HOL ≫ YARP (~**7.8×**). H3 TWP÷YARP ≈ **1.07×**.

### Architecture-sensitive

These runs isolate slow app readers, origin-early response, HTTP/2 duplex, and WebSocket echo. See [TWP vs YARP IO model](Performance-Profiling#twp-vs-yarp-io-model). Laptop 1-rep numbers are on [Performance Local Lab](Performance-Local-Lab#architecture-sensitive).

Median of **3** repeats on matched 4 vCPU / 16 GiB runners @ `7c029d65` ([36280067475](https://github.com/justcoding121/titanium-web-proxy/actions/runs/36280067475)). Slow consumer = 256 KiB GET, 16 KiB read + 8 ms sleep. Early response = 64 KiB POST, origin writes after 8 KiB. Duplex HTTP/2 = overlapping 64 KiB POST on H2 TLS↔H2 TLS. WebSocket row = H1 Upgrade echo round-trips/sec (not RFC 8441; see WebSocket RFC 8441 table below).

Lossy-link runs (slow **network**) are already published above; they are not a slow **app** reader.

#### Windows

*Not possible:* **HAProxy** and **Envoy** columns are omitted (no official Windows port).

| Scenario | Client | Origin | TWP | nginx | YARP |
|---|---|---|---:|---:|---:|
| Slow consumer (256 KiB GET, throttled client read) | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **248**<br><sub>(99 MiB / 5.7% CPU)</sub> | **221**<br><sub>(144 MiB / 24.7% CPU)</sub> | **242**<br><sub>(110 MiB / 5.2% CPU)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · TLS | HTTP/1 · plain | **248**<br><sub>(116 MiB / 5.6% CPU)</sub> | **162**<br><sub>(142 MiB / 24.7% CPU)</sub> | 🥇 **256**<br><sub>(111 MiB / 7.6% CPU)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/3 · QUIC | HTTP/1 · plain | **261**<br><sub>(104 MiB / 19.7% CPU)</sub> | *Not possible (no QUIC)* | 🥇 **262**<br><sub>(164 MiB / 21.7% CPU)</sub> |
| Early response (origin writes after first request chunk) | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **5,347**<br><sub>(95 MiB / 43.0% CPU)</sub> | **299**<br><sub>(143 MiB / 24.6% CPU)</sub> | **3,853**<br><sub>(136 MiB / 53.9% CPU)</sub> |
| Early response (origin writes after first request chunk) | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **3,308**<br><sub>(186 MiB / 50.4% CPU)</sub> | **0**<br><sub>(peak 318 · 144 MiB / 24.7% CPU)</sub> | **3,160**<br><sub>(140 MiB / 53.7% CPU)</sub> |
| Early response (origin writes after first request chunk) | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **2,038**<br><sub>(141 MiB / 39.3% CPU)</sub> | *Not possible (no QUIC)* | **1,811**<br><sub>(194 MiB / 53.0% CPU)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · plain | HTTP/1 · plain | **248**<br><sub>(112 MiB / 3.1% CPU)</sub> | **248**<br><sub>(127 MiB / 9.1% CPU)</sub> | 🥇 **256**<br><sub>(106 MiB / 4.3% CPU)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · TLS | HTTP/2 · TLS | **0**<br><sub>(70 MiB / 0.3% CPU)</sub> | *Not possible* | 🥇 **256**<br><sub>(134 MiB / 10.8% CPU)</sub> |
| Early response (origin writes after first request chunk) | HTTP/2 · TLS | HTTP/2 · TLS | **0**<br><sub>(peak 922 · 175 MiB / 27.9% CPU)</sub> | *Not possible* | **0**<br><sub>(110 MiB / 0.1% CPU)</sub> |
| Duplex (both directions live) | HTTP/2 · TLS | HTTP/2 · TLS | **0**<br><sub>(peak 742 · 171 MiB / 30.4% CPU)</sub> | *Not possible* | **0**<br><sub>(113 MiB / 0.3% CPU)</sub> |
| Duplex (WebSocket / H1 Upgrade) | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **24,840**<br><sub>(96 MiB / 43.7% CPU)</sub> | **12,297**<br><sub>(143 MiB / 24.8% CPU)</sub> | **22,940**<br><sub>(89 MiB / 44.1% CPU)</sub> |

#### Linux

| Scenario | Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---|---:|---:|---:|---:|---:|
| Slow consumer (256 KiB GET, throttled client read) | HTTP/1 · TLS | HTTP/1 · plain | **464**<br><sub>(123 MiB / 9.3% CPU)</sub> | **420**<br><sub>(100 MiB / 9.9% CPU)</sub> | 🥇 **469**<br><sub>(83 MiB / 6.1% CPU)</sub> | **469**<br><sub>(137 MiB / 6.4% CPU)</sub> | **406**<br><sub>(145 MiB / 14.3% CPU)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **481**<br><sub>(144 MiB / 9.5% CPU)</sub> | **424**<br><sub>(102 MiB / 6.5% CPU)</sub> | **479**<br><sub>(84 MiB / 4.2% CPU)</sub> | **475**<br><sub>(158 MiB / 3.7% CPU)</sub> | **480**<br><sub>(147 MiB / 12.4% CPU)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **472**<br><sub>(132 MiB / 33.5% CPU)</sub> | **0**<br><sub>(peak 121 · 120 MiB / 22.1% CPU)</sub> | **470**<br><sub>(86 MiB / 11.7% CPU)</sub> | **0**<br><sub>(149 MiB / 0.1% CPU)</sub> | **469**<br><sub>(195 MiB / 41.3% CPU)</sub> |
| Early response (origin writes after first request chunk) | HTTP/1 · TLS | HTTP/1 · plain | **4,780**<br><sub>(142 MiB / 47.6% CPU)</sub> | **3,515**<br><sub>(100 MiB / 50.0% CPU)</sub> | 🥇 **5,004**<br><sub>(84 MiB / 43.0% CPU)</sub> | **0**<br><sub>(peak 2,752 · 130 MiB / 25.9% CPU)</sub> | **3,160**<br><sub>(176 MiB / 56.5% CPU)</sub> |
| Early response (origin writes after first request chunk) | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **2,463**<br><sub>(211 MiB / 49.1% CPU)</sub> | **0**<br><sub>(peak 1,193 · 112 MiB / 21.7% CPU)</sub> | **2,458**<br><sub>(84 MiB / 24.6% CPU)</sub> | **0**<br><sub>(peak 2,428 · 149 MiB / 23.0% CPU)</sub> | **2,145**<br><sub>(170 MiB / 48.3% CPU)</sub> |
| Early response (origin writes after first request chunk) | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **6,568**<br><sub>(226 MiB / 44.5% CPU)</sub> | **0**<br><sub>(peak 941 · 120 MiB / 24.6% CPU)</sub> | **4,196**<br><sub>(92 MiB / 34.3% CPU)</sub> | **0**<br><sub>(128 MiB / 0.1% CPU)</sub> | **4,264**<br><sub>(269 MiB / 46.2% CPU)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · plain | HTTP/1 · plain | **473**<br><sub>(150 MiB / 15.2% CPU)</sub> | **420**<br><sub>(79 MiB / 10.2% CPU)</sub> | **474**<br><sub>(69 MiB / 6.3% CPU)</sub> | **472**<br><sub>(145 MiB / 4.6% CPU)</sub> | 🥇 **475**<br><sub>(148 MiB / 15.3% CPU)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · TLS | HTTP/2 · TLS | **0**<br><sub>(100 MiB / 0.5% CPU)</sub> | *Not possible* | **458**<br><sub>(88 MiB / 22.0% CPU)</sub> | **463**<br><sub>(151 MiB / 12.8% CPU)</sub> | 🥇 **466**<br><sub>(157 MiB / 31.5% CPU)</sub> |
| Early response (origin writes after first request chunk) | HTTP/2 · TLS | HTTP/2 · TLS | **0**<br><sub>(peak 1,068 · 254 MiB / 22.4% CPU)</sub> | *Not possible* | **0**<br><sub>(95 MiB / 0.1% CPU)</sub> | **0**<br><sub>(peak 3,711 · 158 MiB / 19.3% CPU)</sub> | **0**<br><sub>(147 MiB / 0.1% CPU)</sub> |
| Duplex (both directions live) | HTTP/2 · TLS | HTTP/2 · TLS | **0**<br><sub>(peak 1,508 · 254 MiB / 27.6% CPU)</sub> | *Not possible* | **0**<br><sub>(95 MiB / 0.1% CPU)</sub> | **0**<br><sub>(peak 4,563 · 159 MiB / 20.4% CPU)</sub> | **0**<br><sub>(141 MiB / 0.1% CPU)</sub> |
| Duplex (WebSocket / H1 Upgrade) | HTTP/1 · TLS | HTTP/1 · plain | **29,352**<br><sub>(125 MiB / 44.3% CPU)</sub> | 🥇 **33,468**<br><sub>(101 MiB / 36.1% CPU)</sub> | **32,494**<br><sub>(83 MiB / 39.1% CPU)</sub> | **31,645**<br><sub>(127 MiB / 39.7% CPU)</sub> | **27,670**<br><sub>(125 MiB / 44.3% CPU)</sub> |

Slow consumer is sleep-bound; H1/H2/H3 sit in the same band. Early-response H1: TWP leads (~**1.46×** / **1.45×** YARP Win/Linux). **Duplex H2** sustain is **0** for TWP and YARP on this GHA pass (same concurrent-copier cell; see [IO model](Performance-Profiling#twp-vs-yarp-io-model) and laptop numbers). WebSocket: TWP÷YARP Windows ≈ **1.07×**; Linux nginx leads.

### TLS termination cost (H1 TLS → cleartext origin)

Isolates keep-alive tiny GET vs **new connection per request** (handshake-dominated) vs keep-alive **256 KiB**. Product comparison uses RPS and end-to-end latency; TWP can also capture `ClientTlsTiming` when `TWP_RPS_CAPTURE_TLS=1` (child process) — nginx/YARP have no equivalent hook.

#### Windows

Median of **3** repeats on `windows-latest` @ `7c029d65`. Source: Actions [36280072285](https://github.com/justcoding121/titanium-web-proxy/actions/runs/36280072285). Absolute RPS on GHA swings hard; prefer **TWP÷YARP**.

*Not possible:* **HAProxy** and **Envoy** columns are omitted (no official Windows port).

| Workload | TWP | nginx | YARP |
|---|---:|---:|---:|
| Keep-alive · tiny GET | 🥇 **21,073**<br><sub>(84 MiB / 47.5% CPU)</sub> | **8,793**<br><sub>(141 MiB / 24.7% CPU)</sub> | **18,431**<br><sub>(101 MiB / 51.5% CPU)</sub> |
| New-connection · tiny GET | 🥇 **730**<br><sub>(88 MiB / 10.1% CPU)</sub> | **0**<br><sub>(peak 245 · 140 MiB / 24.4% CPU)</sub> | **711**<br><sub>(115 MiB / 9.8% CPU)</sub> |
| Keep-alive · 256 KiB GET | 🥇 **2,821**<br><sub>(124 MiB / 45.6% CPU)</sub> | **0**<br><sub>(peak 142 · 142 MiB / 24.6% CPU)</sub> | **2,679**<br><sub>(138 MiB / 47.1% CPU)</sub> |

#### Linux

Median of **3** repeats @ `7c029d65`. Source: Actions [36280072285](https://github.com/justcoding121/titanium-web-proxy/actions/runs/36280072285).

| Workload | TWP | nginx | HAProxy | Envoy | YARP |
|---|---:|---:|---:|---:|---:|
| Keep-alive · tiny GET | **38,415**<br><sub>(112 MiB / 48.7% CPU)</sub> | **44,689**<br><sub>(102 MiB / 40.8% CPU)</sub> | 🥇 **44,694**<br><sub>(83 MiB / 43.4% CPU)</sub> | **27,927**<br><sub>(126 MiB / 56.5% CPU)</sub> | **33,495**<br><sub>(136 MiB / 50.5% CPU)</sub> |
| New-connection · tiny GET | **1,538**<br><sub>(127 MiB / 39.6% CPU)</sub> | 🥇 **1,618**<br><sub>(105 MiB / 36.5% CPU)</sub> | **1,486**<br><sub>(85 MiB / 36.4% CPU)</sub> | **1,376**<br><sub>(129 MiB / 44.3% CPU)</sub> | **1,494**<br><sub>(151 MiB / 38.6% CPU)</sub> |
| Keep-alive · 256 KiB GET | **3,850**<br><sub>(125 MiB / 31.5% CPU)</sub> | **2,468**<br><sub>(101 MiB / 47.8% CPU)</sub> | 🥇 **4,127**<br><sub>(85 MiB / 28.3% CPU)</sub> | **3,616**<br><sub>(146 MiB / 32.1% CPU)</sub> | **3,054**<br><sub>(167 MiB / 42.5% CPU)</sub> |

All three workloads are **>1.00×** YARP on both OS. On Linux, HAProxy leads keep-alive tiny (near-tie with nginx) and Envoy leads new-connection; TWP stays ahead of YARP on all three.

## Unary gRPC (H2 TLS)

Unary Echo **RPC/s** @ c=64 over H2 TLS→H2 TLS for Titanium, YARP, nginx (`grpc_pass`), HAProxy, and Envoy (OS-possible peers only). Not folded into the architecture-sensitive tables. Median of **3** repeats @ `7c029d65` — [36280055132](https://github.com/justcoding121/titanium-web-proxy/actions/runs/36280055132).

| OS | Titanium | YARP | nginx | HAProxy | Envoy |
|---|---:|---:|---:|---:|---:|
| Windows | **51,561**<br><sub>(97 MiB / 21.9% CPU)</sub> | **30,145**<br><sub>(107 MiB / 44.5% CPU)</sub> | **0**<br><sub>(144 MiB / 24.7% CPU)</sub> | *Not possible* | *Not possible* |
| Linux | **54,300**<br><sub>(128 MiB / 30.2% CPU)</sub> | **31,534**<br><sub>(153 MiB / 41.5% CPU)</sub> | **0**<br><sub>(112 MiB / 24.9% CPU)</sub> | **11,542**<br><sub>(83 MiB / 24.6% CPU)</sub> | **20,961**<br><sub>(128 MiB / 21.1% CPU)</sub> |
| macOS | **19,010**<br><sub>(95 MiB / 24.0% CPU)</sub> | **10,097**<br><sub>(132 MiB / 34.0% CPU)</sub> | **0**<br><sub>(66 MiB / 2.9% CPU)</sub> | **0**<br><sub>(62 MiB / 15.0% CPU)</sub> | **0**<br><sub>(80 MiB / 20.0% CPU)</sub> |

## Unary gRPC (H2 TLS → h2c)

Unary Echo **RPC/s** @ c=64 over **H2 TLS → h2c** (edge TLS, cleartext H2 origin). Peers: Titanium, YARP, HAProxy, Envoy. Mode: `compare-grpc` (`*-grpc-h2c`). Median of **3** repeats @ `7c029d65` — [36280055132](https://github.com/justcoding121/titanium-web-proxy/actions/runs/36280055132).

*Not possible:* **nginx** column omitted (no H2 upstream).

| OS | Titanium | YARP | HAProxy | Envoy |
|---|---:|---:|---:|---:|
| Windows | **59,259**<br><sub>(90 MiB / 24.3% CPU)</sub> | **33,940**<br><sub>(118 MiB / 49.0% CPU)</sub> | *Not possible* | *Not possible* |
| Linux | **44,517**<br><sub>(120 MiB / 29.6% CPU)</sub> | **24,807**<br><sub>(149 MiB / 43.5% CPU)</sub> | **7,804**<br><sub>(85 MiB / 24.5% CPU)</sub> | **12,644**<br><sub>(126 MiB / 22.4% CPU)</sub> |
| macOS | **13,917**<br><sub>(91 MiB / 21.9% CPU)</sub> | **9,872**<br><sub>(155 MiB / 33.8% CPU)</sub> | **0**<br><sub>(65 MiB / 16.5% CPU)</sub> | **0**<br><sub>(80 MiB / 20.5% CPU)</sub> |

## WebSocket (H1 TLS → H1 TLS)

WebSocket echo round-trips/sec over **dual-TLS** H1 (`proxy_ssl` style). Mode: `compare-ws-h1tls` (`*-duplex-ws-h1tls`).

| OS | Titanium | YARP | nginx | HAProxy | Envoy |
|---|---:|---:|---:|---:|---:|
| Windows | **22,604**<br><sub>(101 MiB / 45.4% CPU)</sub> | **21,725**<br><sub>(93 MiB / 42.4% CPU)</sub> | **9,686**<br><sub>(142 MiB / 24.8% CPU)</sub> | *Not possible* | *Not possible* |
| Linux | **34,976**<br><sub>(143 MiB / 41.9% CPU)</sub> | **32,706**<br><sub>(135 MiB / 43.0% CPU)</sub> | **41,394**<br><sub>(105 MiB / 35.1% CPU)</sub> | **39,836**<br><sub>(84 MiB / 37.6% CPU)</sub> | **40,385**<br><sub>(127 MiB / 37.7% CPU)</sub> |
| macOS | **14,388**<br><sub>(117 MiB / 33.2% CPU)</sub> | **16,877**<br><sub>(127 MiB / 32.7% CPU)</sub> | **10,952**<br><sub>(82 MiB / 20.3% CPU)</sub> | **14,824**<br><sub>(68 MiB / 32.4% CPU)</sub> | **9,633**<br><sub>(80 MiB / 29.5% CPU)</sub> |

## WebSocket (H2 TLS 8441 → H1)

WebSocket echo over **RFC 8441** extended CONNECT (H2 TLS client → H1 plain origin). Mode: `compare-ws-h2` (`*-duplex-ws-h2`).

*Not possible:* **nginx** column omitted (no RFC 8441 extended CONNECT).

| OS | Titanium | YARP | HAProxy | Envoy |
|---|---:|---:|---:|---:|
| Windows | **23,799**<br><sub>(161 MiB / 43.1% CPU)</sub> | **0**<br><sub>(147 MiB / 19.4% CPU)</sub> | *Not possible* | *Not possible* |
| Linux | **1,531**<br><sub>(143 MiB / 16.6% CPU)</sub> | **0**<br><sub>(173 MiB / 26.3% CPU)</sub> | **1,533**<br><sub>(84 MiB / 5.4% CPU)</sub> | **0**<br><sub>(123 MiB / 0.4% CPU)</sub> |
| macOS | **9,457**<br><sub>(259 MiB / 38.6% CPU)</sub> | **0**<br><sub>(102 MiB / 22.3% CPU)</sub> | **9,684**<br><sub>(65 MiB / 32.1% CPU)</sub> | **0**<br><sub>(73 MiB / 0.5% CPU)</sub> |

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
