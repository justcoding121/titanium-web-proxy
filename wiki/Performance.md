Throughput and footprint of **Titanium** as a reverse / edge proxy and as a decrypting (**MITM**) proxy, measured on the same harness against **YARP**, **nginx**, **HAProxy**, and **Envoy** where each OS can run them.

**RPS** is requests per second (gRPC tables use **RPC/s**). Numbers are Release builds on matched GitHub-hosted runners: Windows and Linux at **4 vCPU / 16 GiB**, macOS at **`macos-15` Apple Silicon 3-core / 7 GB**. We compare products within an OS, not OS against OS, so read within one table — absolute RPS is not comparable across operating systems. *Not possible* means that product cannot run that path on that OS; *Not measured* means the path exists but no published number yet.

For pooling knobs and certificate first-visit tuning, see [Performance and pooling](Home#user-content-performance-and-pooling). Laptop cool A/B tables (not publishable) live on [Performance Local Lab](Performance-Local-Lab).

## Why this comparison is fair

- Same load generator, same origin process, and the same warmup / measure windows (2s / 8s) with the same concurrency ramp (8, 16, 32, 64).
- Every reverse arm is three OS processes: load generator + origin + proxy. Origin-direct omits the proxy; peers are never in-process with the client.
- Same runner class per table (`windows-latest` / `ubuntu-latest` / `macos-15`). Laptop numbers are never mixed into these tables.
- Peers use equivalent TLS/ALPN and streaming-friendly settings on the same loopback shape. HAProxy and Envoy are Linux/macOS only — *Not possible* on Windows, so their columns are omitted there.
- MITM (HTTPS decryption with forged certificates) is Titanium-only; peers cannot MITM. The published MITM table is Linux only. It shows Titanium MITM overhead versus its own reverse path on the same wires.
- **Tiny keep-alive GET** (~56-byte JSON) is the industry RPS shape (same class as wrk / TechEmpower). It is also real for small JSON APIs and health checks.
- **HTTP/2 and HTTP/3** spread that concurrency across `min(concurrency, max(4 × cores, 16))` client connections, the same connection count h2load and wrk fix with `-c`. On these 4 vCPU runners that is 8, 16, 16, and 16 connections at c = 8, 16, 32, 64.
- **Same-protocol H2↔H2 / H3↔H3** on that shape is Titanium’s **best case**: with interception off, Titanium copies frames instead of decoding and re-encoding headers (peers do a full HTTP decode). Medals there are not the typical reverse-proxy job.
- **Typical reverse** is H1 TLS→H1 or H2→H1 (~1.1× YARP on Win/Linux tiny GET). With **larger bodies**, see [Heavier reverse](#user-content-heavier-reverse-workloads) (@ `347a9a1b`).

## How to read the tables

- Bold RPS is **sustain** (last concurrency that still met error/latency SLOs). When peak differs, it appears in the same cell as `<sub>(peak N · …)</sub>` with RSS/CPU. When sustain equals peak, peak is omitted. Footnotes also include microseconds of CPU per request when CPU was sampled.
- 🥇 = best among OS-possible peers on that row (highest sustain RPS; on a tie, lower memory then lower CPU%). On 64 KiB and 256 KiB rows the medal follows goodput (sustain RPS × request and response bytes). On tiny Reverse that is Titanium / nginx / YARP (plus HAProxy / Envoy on Linux/macOS). The same rule applies to heavier reverse, architecture-sensitive, TLS-cost, gRPC, and WebSocket peer tables. Gold on tiny GET is that frame-copy best case, not “Titanium is 1.7× on all reverse.” For larger-body H2→H2, see the [heavier tables](#user-content-heavier-reverse-workloads).
- **MITM** is published on Linux only (Titanium-only, no peer medal). **Lite÷Reverse** / **Full÷Reverse** = Titanium MITM sustain ÷ Titanium reverse sustain on the same Client×Origin pair — the overhead of decrypting and intercepting versus bare reverse, not versus nginx or YARP.
- *Not possible* = cannot do that path. *Not measured* = path exists but no published number yet. When every row would be *Not possible* for a peer, that column is omitted and a note above the table explains why.

## Contents

- [Why this comparison is fair](#user-content-why-this-comparison-is-fair)
- [How to read the tables](#user-content-how-to-read-the-tables)
- [Measurement environment](#user-content-measurement-environment)
    - [Windows (GitHub-hosted `windows-latest`)](#user-content-windows-github-hosted-windows-latest)
    - [Linux (GitHub-hosted `ubuntu-latest`)](#user-content-linux-github-hosted-ubuntu-latest)
    - [macOS (GitHub-hosted `macos-15`, Apple Silicon)](#user-content-macos-github-hosted-macos-15-apple-silicon)
    - [Saturation control](#user-content-saturation-control)
- [Windows — Titanium vs nginx vs HAProxy vs Envoy vs YARP](#user-content-windows--titanium-vs-nginx-vs-haproxy-vs-envoy-vs-yarp)
- [Linux — Titanium vs nginx vs HAProxy vs Envoy vs YARP](#user-content-linux--titanium-vs-nginx-vs-haproxy-vs-envoy-vs-yarp)
- [macOS — Titanium vs nginx vs HAProxy vs Envoy vs YARP](#user-content-macos--titanium-vs-nginx-vs-haproxy-vs-envoy-vs-yarp)
- [Editions (CLI / Plus / Intercept)](#user-content-editions-cli--plus--intercept)
- [Heavier reverse workloads](#user-content-heavier-reverse-workloads)
- [Unary gRPC (H2 TLS)](#user-content-unary-grpc-h2-tls)
- [Unary gRPC (H2 TLS → h2c)](#user-content-unary-grpc-h2-tls--h2c)
- [WebSocket (H1 TLS → H1 TLS)](#user-content-websocket-h1-tls--h1-tls)
- [WebSocket (H2 TLS 8441 → H1)](#user-content-websocket-h2-tls-8441--h1)
- [Other measurements](#user-content-other-measurements)
- [Raising limits on large hosts](#user-content-raising-limits-on-large-hosts)
- [Maintainer notes](#user-content-maintainer-notes)

## Measurement environment

All three OS use public-repo GitHub-hosted runners: **Windows / Linux** at **4 vCPU / 16 GiB / 14 GB SSD**, **macOS** at **`macos-15` Apple Silicon (M1) 3-core / 7 GB** (pinned, not `macos-latest`). macOS is Apple Silicon because that is what Mac users run today; the runner sizes differ, which is fine because we compare products within an OS. Same harness knobs: warmup 2s / measure 8s; concurrency 8, 16, 32, 64; median of 3 repeats. Prefer Titanium÷YARP / Titanium÷nginx ratios over absolute RPS.

HTTP/2 and HTTP/3 open `min(concurrency, max(4 × cores, 16))` client connections, one `SocketsHttpHandler` each. On the 4 vCPU runners that is 8, 16, 16, and 16 connections at c = 8, 16, 32, 64. HTTP/1 stays one connection per in-flight request. The generator is in-process .NET `SocketsHttpHandler` on the same machine as the origin and the proxy. Client, origin, and proxy share those cores, so the proxy's CPU share stays below a full box.

nginx TCP and TLS `listen` lines use `reuseport`. Linux and Windows keep `worker_processes auto`. macOS stays `worker_processes 1`. HAProxy `nbthread` and Envoy `--concurrency` stay at the processor count.

Large-body flow control is the same class on every peer: nginx `http2_chunk_size 16k`; HAProxy `tune.bufsize 65536` and `tune.h2.initial-window-size 65536`; Envoy stream window 65535 bytes and connection window 1 MiB. Titanium's defaults stay a 65535-byte connection window and a 16384-byte max frame.

Cells for 64 KiB and 256 KiB bodies include goodput (MiB/s) and microseconds of CPU per request. Those rows are ranked on goodput. Smaller bodies stay ranked on RPS.

On the Linux H2 TLS→H1 plain row at c=64 (16 connections, @ `347a9a1b`), proxy CPU was about **43%** for nginx, **45%** for HAProxy, and **54%** for Envoy. Windows nginx on the same H2 client shape stayed near **25%** (one core). That measured shape is the one published here.

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

Calibration for the shared 4 vCPU loopback shape: how close client + origin are to saturated before ranking reverse peers. Tiny keep-alive GET. Median of **3** repeats @ `347a9a1b` — [37894269584](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37894269584). Warmup 2s / measure 8s; concurrency 8, 16, 32, 64. Block A **% of origin-HttpClient** uses median **peak** RPS. Blocks B/C use peer÷YARP / ÷nginx on median peak (not % of H1 origin). **RPS cells** embed median RSS / CPU for the **proxy child** plus its **full descendant tree** (serve-proxy → nginx master → workers); origin-direct samples the **origin** child. Product matrices below use matched `dotnet-httpclient` only (not bombardier).


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
| origin-direct | dotnet-httpclient | **54,679**<br><sub>(55 MiB / 43.3% CPU · 31.7 µs CPU/req)</sub> | **100.0%** |
| origin-direct-bombardier | bombardier | **45,437**<br><sub>(56 MiB / 23.9% CPU · 21.0 µs CPU/req)</sub> | **83.1%** |
| bare-reverse-http1 | dotnet-httpclient | **28,420**<br><sub>(61 MiB / 44.8% CPU · 63.0 µs CPU/req)</sub> | **52.0%** |
| nginx-reverse-http1 | dotnet-httpclient | **18,054**<br><sub>(126 MiB / 24.8% CPU · 54.9 µs CPU/req)</sub> | **33.0%** |
| yarp-reverse-http1 | dotnet-httpclient | **19,937**<br><sub>(88 MiB / 49.1% CPU · 98.5 µs CPU/req)</sub> | **36.5%** |
| twp-reverse-http1 | dotnet-httpclient | 🥇 **22,938**<br><sub>(78 MiB / 49.2% CPU · 85.8 µs CPU/req)</sub> | **42.0%** |

**Linux** (`ubuntu-latest`)

| Arm | Generator | RPS | % of origin-HttpClient |
|---|---|---:|---:|
| origin-direct | dotnet-httpclient | **71,093**<br><sub>(80 MiB / 42.5% CPU · 23.9 µs CPU/req)</sub> | **100.0%** |
| origin-direct-bombardier | bombardier | **42,042**<br><sub>(79 MiB / 34.9% CPU · 33.2 µs CPU/req)</sub> | **59.1%** |
| bare-reverse-http1 | dotnet-httpclient | **32,616**<br><sub>(67 MiB / 44.8% CPU · 54.9 µs CPU/req)</sub> | **45.9%** |
| nginx-reverse-http1 | dotnet-httpclient | 🥇 **39,065**<br><sub>(76 MiB / 41.8% CPU · 42.8 µs CPU/req)</sub> | **54.9%** |
| yarp-reverse-http1 | dotnet-httpclient | **27,818**<br><sub>(116 MiB / 50.4% CPU · 72.5 µs CPU/req)</sub> | **39.1%** |
| twp-reverse-http1 | dotnet-httpclient | **31,693**<br><sub>(90 MiB / 50.1% CPU · 63.2 µs CPU/req)</sub> | **44.6%** |


**macOS** (`macos-15`, Apple Silicon)

| Arm | Generator | RPS | % of origin-HttpClient |
|---|---|---:|---:|
| origin-direct | dotnet-httpclient | **80,725**<br><sub>(89 MiB / 33.6% CPU · 16.7 µs CPU/req)</sub> | **100.0%** |
| origin-direct-bombardier | bombardier | **55,886**<br><sub>(89 MiB / 24.5% CPU · 17.5 µs CPU/req)</sub> | **69.2%** |
| bare-reverse-http1 | dotnet-httpclient | **21,288**<br><sub>(93 MiB / 29.4% CPU · 55.2 µs CPU/req)</sub> | **26.4%** |
| nginx-reverse-http1 | dotnet-httpclient | **25,318**<br><sub>(68 MiB / 21.0% CPU · 33.1 µs CPU/req)</sub> | **31.4%** |
| yarp-reverse-http1 | dotnet-httpclient | **21,749**<br><sub>(148 MiB / 34.6% CPU · 63.6 µs CPU/req)</sub> | **26.9%** |
| twp-reverse-http1 | dotnet-httpclient | 🥇 **26,443**<br><sub>(115 MiB / 33.8% CPU · 51.1 µs CPU/req)</sub> | **32.8%** |

On this macOS run `origin-direct` was **33.6%** CPU, so the % of origin-HttpClient column is not a ceiling when that CPU is near idle. Rank those Mac peers by RPS.

Reverse peers on Block A @ `347a9a1b`: Windows TWP is **42.0%** of origin-direct and Linux TWP is **44.6%**. Prefer the **%** column over absolute RPS across runs. Bare and origin-direct are controls (not medal peers).

#### Block B — H2 TLS→H1

Peer ratios (÷YARP / ÷nginx) on median peak; **RPS cells** embed `(MiB / CPU%)`.

**Windows** (`windows-latest`)

| Arm | Generator | RPS | ÷YARP | ÷nginx |
|---|---|---:|---:|---:|
| nginx-reverse-http2 | dotnet-httpclient | **10,756**<br><sub>(142 MiB / 24.9% CPU · 92.5 µs CPU/req)</sub> | **0.37×** | **1.00×** |
| yarp-reverse-http2 | dotnet-httpclient | **28,772**<br><sub>(105 MiB / 48.1% CPU · 66.9 µs CPU/req)</sub> | **1.00×** | **2.67×** |
| twp-reverse-http2-cleartext | dotnet-httpclient | 🥇 **34,898**<br><sub>(122 MiB / 49.7% CPU · 57.0 µs CPU/req)</sub> | **1.21×** | **3.24×** |

**Linux** (`ubuntu-latest`)

| Arm | Generator | RPS | ÷YARP | ÷nginx |
|---|---|---:|---:|---:|
| nginx-reverse-http2 | dotnet-httpclient | **24,735**<br><sub>(99 MiB / 44.0% CPU · 71.1 µs CPU/req)</sub> | **0.90×** | **1.00×** |
| yarp-reverse-http2 | dotnet-httpclient | **27,509**<br><sub>(131 MiB / 50.6% CPU · 73.5 µs CPU/req)</sub> | **1.00×** | **1.11×** |
| twp-reverse-http2-cleartext | dotnet-httpclient | 🥇 **32,190**<br><sub>(152 MiB / 52.3% CPU · 65.0 µs CPU/req)</sub> | **1.17×** | **1.30×** |

**macOS** (`macos-15`, Apple Silicon)

| Arm | Generator | RPS | ÷YARP | ÷nginx |
|---|---|---:|---:|---:|
| nginx-reverse-http2 | dotnet-httpclient | **22,858**<br><sub>(83 MiB / 23.6% CPU · 41.3 µs CPU/req)</sub> | **0.89×** | **1.00×** |
| yarp-reverse-http2 | dotnet-httpclient | **25,641**<br><sub>(158 MiB / 39.3% CPU · 61.3 µs CPU/req)</sub> | **1.00×** | **1.12×** |
| twp-reverse-http2-cleartext | dotnet-httpclient | 🥇 **29,293**<br><sub>(182 MiB / 41.2% CPU · 56.2 µs CPU/req)</sub> | **1.14×** | **1.28×** |
#### Block C — H3→H1

Same layout as Block B. Requires QuicListener. nginx needs `http_v3_module` (Windows nginx has no QUIC). HAProxy needs `USE_QUIC` (GHA Linux/macOS require it). Envoy 1.20+ includes HTTP/3.

Medals are nginx / YARP / Titanium only, from the same run as Blocks A and B ([37894269584](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37894269584)). On the Linux run HAProxy was **23,149** (88 MiB / 35.3% CPU) and Envoy **8,803** (132 MiB / 58.8% CPU). On the macOS run HAProxy was **20,965** (105 MiB / 29.1% CPU) and Envoy **9,246** (126 MiB / 56.5% CPU).

**Windows** (`windows-latest`)

| Arm | Generator | RPS | ÷YARP | ÷nginx |
|---|---|---:|---:|---:|
| nginx-reverse-http3-cleartext | dotnet-httpclient | *Not possible (no QUIC)* | — | — |
| yarp-reverse-http3-cleartext | dotnet-httpclient | **8,326**<br><sub>(156 MiB / 50.2% CPU · 241.0 µs CPU/req)</sub> | **1.00×** | — |
| twp-reverse-http3-cleartext | dotnet-httpclient | 🥇 **11,259**<br><sub>(111 MiB / 47.3% CPU · 168.2 µs CPU/req)</sub> | **1.35×** | — |

**Linux** (`ubuntu-latest`)

| Arm | Generator | RPS | ÷YARP | ÷nginx |
|---|---|---:|---:|---:|
| nginx-reverse-http3-cleartext | dotnet-httpclient | 🥇 **20,668**<br><sub>(104 MiB / 32.0% CPU · 62.0 µs CPU/req)</sub> | **1.36×** | **1.00×** |
| yarp-reverse-http3-cleartext | dotnet-httpclient | **15,196**<br><sub>(193 MiB / 51.7% CPU · 136.0 µs CPU/req)</sub> | **1.00×** | **0.74×** |
| twp-reverse-http3-cleartext | dotnet-httpclient | **19,035**<br><sub>(148 MiB / 52.8% CPU · 110.9 µs CPU/req)</sub> | **1.25×** | **0.92×** |

**macOS** (`macos-15`, Apple Silicon)

| Arm | Generator | RPS | ÷YARP | ÷nginx |
|---|---|---:|---:|---:|
| nginx-reverse-http3-cleartext | dotnet-httpclient | 🥇 **14,695**<br><sub>(peak 16,280 · 84 MiB / 23.9% CPU · 65.1 µs CPU/req)</sub> | **1.18×** | **1.00×** |
| yarp-reverse-http3-cleartext | dotnet-httpclient | **13,771**<br><sub>(208 MiB / 43.9% CPU · 127.5 µs CPU/req)</sub> | **1.00×** | **0.85×** |
| twp-reverse-http3-cleartext | dotnet-httpclient | **12,234**<br><sub>(143 MiB / 45.3% CPU · 148.1 µs CPU/req)</sub> | **0.89×** | **0.75×** |

## Windows — Titanium vs nginx vs HAProxy vs Envoy vs YARP

Client / origin: HTTP version and whether TLS is used (`plain` = cleartext, `TLS` = encrypted, `QUIC` = HTTP/3).

### Reverse

Median of **3 repeats** on `windows-latest` (4 vCPU / 16 GiB). Bare reverse 5×5 @ `347a9a1b` — `compare-product` [37894265694](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37894265694). Warmup 2s / measure 8s; concurrency 8, 16, 32, 64. Prefer TWP÷peer ratios over absolute RPS. **RPS cells** include median RSS / CPU at the peak-RPS step as `<br><sub>(MiB / CPU%)</sub>`. nginx terminate peers use `keepalive 256` + streaming buffers. **HAProxy / Envoy are Linux-only peers** (no official Windows port). Laptop High-perf / cool-paired numbers stay on the [local lab](Performance-Local-Lab). Product 5×5 is **~56-byte JSON keep-alive GET**; H2/H3 same-protocol cells are mostly header work with a tiny body (Titanium best case) — see [Why this comparison is fair](#user-content-why-this-comparison-is-fair).

**Load generators:** Reverse inbound H3 arms use **`dotnet-httpclient`** (`http_version=3.0`, `RequestVersionExact`). nginx/Windows is same-OS only (no QUIC). HAProxy/Envoy are Linux-only terminate peers.

| Client | Origin | TWP | nginx | YARP |
|---|---|---:|---:|---:|
| HTTP/1 · plain | HTTP/1 · plain | 🥇 **41,895**<br><sub>(76 MiB / 50.1% CPU · 47.9 µs CPU/req)</sub> | **24,573**<br><sub>(126 MiB / 24.7% CPU · 40.3 µs CPU/req)</sub> | **35,559**<br><sub>(87 MiB / 50.8% CPU · 57.2 µs CPU/req)</sub> |
| HTTP/1 · plain | HTTP/1 · TLS | 🥇 **21,502**<br><sub>(89 MiB / 47.7% CPU · 88.7 µs CPU/req)</sub> | **8,596**<br><sub>(136 MiB / 24.8% CPU · 115.2 µs CPU/req)</sub> | **19,467**<br><sub>(99 MiB / 50.7% CPU · 104.3 µs CPU/req)</sub> |
| HTTP/1 · plain | HTTP/2 · plain | 🥇 **37,098**<br><sub>(100 MiB / 47.2% CPU · 50.9 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **32,769**<br><sub>(93 MiB / 47.9% CPU · 58.5 µs CPU/req)</sub> |
| HTTP/1 · plain | HTTP/2 · TLS | 🥇 **34,066**<br><sub>(117 MiB / 45.3% CPU · 53.2 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **31,673**<br><sub>(98 MiB / 47.8% CPU · 60.4 µs CPU/req)</sub> |
| HTTP/1 · plain | HTTP/3 · QUIC | 🥇 **25,420**<br><sub>(119 MiB / 52.6% CPU · 82.7 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | **25,138**<br><sub>(125 MiB / 49.8% CPU · 79.3 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/1 · plain | 🥇 **20,621**<br><sub>(89 MiB / 49.8% CPU · 96.6 µs CPU/req)</sub> | **8,988**<br><sub>(142 MiB / 24.7% CPU · 109.9 µs CPU/req)</sub> | **18,161**<br><sub>(106 MiB / 47.8% CPU · 105.3 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/1 · TLS | 🥇 **21,624**<br><sub>(90 MiB / 43.6% CPU · 80.6 µs CPU/req)</sub> | **9,228**<br><sub>(144 MiB / 24.7% CPU · 107.1 µs CPU/req)</sub> | **19,298**<br><sub>(102 MiB / 45.4% CPU · 94.1 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/2 · plain | 🥇 **47,569**<br><sub>(124 MiB / 45% CPU · 37.8 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **43,772**<br><sub>(107 MiB / 49.6% CPU · 45.4 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/2 · TLS | 🥇 **42,108**<br><sub>(123 MiB / 44.3% CPU · 42.1 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **39,578**<br><sub>(114 MiB / 48.8% CPU · 49.4 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/3 · QUIC | 🥇 **15,800**<br><sub>(112 MiB / 48% CPU · 121.5 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | **15,617**<br><sub>(125 MiB / 50.1% CPU · 128.4 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | 🥇 **72,300**<br><sub>(114 MiB / 49.7% CPU · 27.5 µs CPU/req)</sub> | **22,886**<br><sub>(127 MiB / 24.9% CPU · 43.5 µs CPU/req)</sub> | **71,382**<br><sub>(92 MiB / 49.9% CPU · 27.9 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/1 · TLS | 🥇 **36,646**<br><sub>(118 MiB / 47.3% CPU · 51.6 µs CPU/req)</sub> | **12,869**<br><sub>(138 MiB / 24.8% CPU · 77.0 µs CPU/req)</sub> | **34,565**<br><sub>(102 MiB / 50.9% CPU · 58.9 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/2 · plain | 🥇 **70,865**<br><sub>(101 MiB / 36.4% CPU · 20.5 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **51,952**<br><sub>(96 MiB / 53.9% CPU · 41.5 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/2 · TLS | 🥇 **56,553**<br><sub>(114 MiB / 34.3% CPU · 24.3 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **46,088**<br><sub>(106 MiB / 49.6% CPU · 43.0 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/3 · QUIC | 🥇 **25,566**<br><sub>(143 MiB / 49.3% CPU · 77.1 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | **23,756**<br><sub>(121 MiB / 53.4% CPU · 89.9 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **30,574**<br><sub>(129 MiB / 54% CPU · 70.6 µs CPU/req)</sub> | **7,883**<br><sub>(142 MiB / 24.9% CPU · 126.5 µs CPU/req)</sub> | **27,674**<br><sub>(104 MiB / 54.1% CPU · 78.2 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/1 · TLS | 🥇 **43,788**<br><sub>(125 MiB / 51.2% CPU · 46.8 µs CPU/req)</sub> | **11,791**<br><sub>(145 MiB / 24.8% CPU · 84.2 µs CPU/req)</sub> | **40,503**<br><sub>(110 MiB / 52.1% CPU · 51.4 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | 🥇 **149,764**<br><sub>(117 MiB / 38.6% CPU · 10.3 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **110,407**<br><sub>(112 MiB / 50.7% CPU · 18.4 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **59,531**<br><sub>(114 MiB / 35.4% CPU · 23.8 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **43,760**<br><sub>(108 MiB / 49.5% CPU · 45.3 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/3 · QUIC | 🥇 **22,586**<br><sub>(159 MiB / 53.4% CPU · 94.5 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | **21,269**<br><sub>(128 MiB / 51.5% CPU · 96.9 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **14,740**<br><sub>(118 MiB / 48.7% CPU · 132.1 µs CPU/req)</sub> | *Not possible (no QUIC)* | **13,436**<br><sub>(159 MiB / 49.3% CPU · 146.7 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **8,331**<br><sub>(116 MiB / 49.5% CPU · 237.6 µs CPU/req)</sub> | *Not possible (no QUIC)* | **7,461**<br><sub>(161 MiB / 50.9% CPU · 272.7 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/2 · plain | 🥇 **11,486**<br><sub>(110 MiB / 45.7% CPU · 159.2 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **9,851**<br><sub>(163 MiB / 51% CPU · 207.0 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | 🥇 **11,872**<br><sub>(115 MiB / 49.5% CPU · 166.8 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **9,374**<br><sub>(163 MiB / 50.1% CPU · 214.0 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/3 · QUIC | 🥇 **10,875**<br><sub>(113 MiB / 47.5% CPU · 174.6 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | **9,404**<br><sub>(165 MiB / 51% CPU · 216.9 µs CPU/req)</sub> |

## Linux — Titanium vs nginx vs HAProxy vs Envoy vs YARP

### Reverse

Median of **3 repeats** on `ubuntu-latest` (4 vCPU / 16 GiB). Bare reverse 5×5 @ `347a9a1b` — `compare-product` [37894265694](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37894265694). Warmup 2s / measure 8s; concurrency 8, 16, 32, 64. **Linux nginx is the authoritative nginx baseline.** HAProxy (3.2 `USE_QUIC`) and Envoy (GitHub release, HTTP/3 compiled in) run on the same loopback shape as nginx/YARP. nginx terminate peers use `keepalive 256` + streaming buffers. The RPS workflow installs nginx.org mainline (`http_v3_module`), a QUIC-enabled HAProxy, Envoy, and `libmsquic`. Prefer ratios over absolute RPS. Product 5×5 is **~56-byte JSON keep-alive GET**; H2/H3 same-protocol cells are mostly header work with a tiny body (Titanium best case) — see [Why this comparison is fair](#user-content-why-this-comparison-is-fair).

| Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---:|---:|---:|---:|---:|
| HTTP/1 · plain | HTTP/1 · plain | **64,228**<br><sub>(84 MiB / 49.6% CPU · 30.9 µs CPU/req)</sub> | 🥇 **75,892**<br><sub>(76 MiB / 41.6% CPU · 21.9 µs CPU/req)</sub> | **66,343**<br><sub>(65 MiB / 43% CPU · 25.9 µs CPU/req)</sub> | **51,303**<br><sub>(116 MiB / 53.2% CPU · 41.5 µs CPU/req)</sub> | **58,647**<br><sub>(116 MiB / 48% CPU · 32.7 µs CPU/req)</sub> |
| HTTP/1 · plain | HTTP/1 · TLS | **37,486**<br><sub>(103 MiB / 48.9% CPU · 52.2 µs CPU/req)</sub> | 🥇 **44,770**<br><sub>(93 MiB / 40.7% CPU · 36.4 µs CPU/req)</sub> | **40,066**<br><sub>(69 MiB / 43.7% CPU · 43.6 µs CPU/req)</sub> | **28,196**<br><sub>(118 MiB / 56.6% CPU · 80.3 µs CPU/req)</sub> | **33,739**<br><sub>(128 MiB / 49.2% CPU · 58.3 µs CPU/req)</sub> |
| HTTP/1 · plain | HTTP/2 · plain | 🥇 **40,473**<br><sub>(127 MiB / 50.9% CPU · 50.3 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **25,316**<br><sub>(65 MiB / 44.3% CPU · 70.0 µs CPU/req)</sub> | **21,937**<br><sub>(117 MiB / 63.3% CPU · 115.4 µs CPU/req)</sub> | **35,896**<br><sub>(125 MiB / 49.4% CPU · 55.1 µs CPU/req)</sub> |
| HTTP/1 · plain | HTTP/2 · TLS | 🥇 **33,280**<br><sub>(149 MiB / 48.5% CPU · 58.3 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **26,550**<br><sub>(67 MiB / 47.1% CPU · 71.0 µs CPU/req)</sub> | **20,356**<br><sub>(117 MiB / 61.7% CPU · 121.3 µs CPU/req)</sub> | **29,304**<br><sub>(130 MiB / 48.3% CPU · 65.9 µs CPU/req)</sub> |
| HTTP/1 · plain | HTTP/3 · QUIC | 🥇 **34,236**<br><sub>(151 MiB / 51.4% CPU · 60.1 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **21,633**<br><sub>(120 MiB / 53.3% CPU · 98.5 µs CPU/req)</sub> | **31,704**<br><sub>(161 MiB / 47.8% CPU · 60.4 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/1 · plain | **36,908**<br><sub>(109 MiB / 49.3% CPU · 53.4 µs CPU/req)</sub> | 🥇 **44,050**<br><sub>(100 MiB / 41.1% CPU · 37.3 µs CPU/req)</sub> | **41,626**<br><sub>(82 MiB / 44% CPU · 42.3 µs CPU/req)</sub> | **27,533**<br><sub>(128 MiB / 56.8% CPU · 82.5 µs CPU/req)</sub> | **32,632**<br><sub>(133 MiB / 49.7% CPU · 61.0 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/1 · TLS | **19,374**<br><sub>(111 MiB / 48.2% CPU · 99.6 µs CPU/req)</sub> | 🥇 **22,751**<br><sub>(103 MiB / 41.4% CPU · 72.9 µs CPU/req)</sub> | **21,392**<br><sub>(84 MiB / 43.2% CPU · 80.8 µs CPU/req)</sub> | **14,807**<br><sub>(126 MiB / 55.6% CPU · 150.3 µs CPU/req)</sub> | **16,971**<br><sub>(140 MiB / 49.8% CPU · 117.4 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/2 · plain | 🥇 **64,818**<br><sub>(159 MiB / 49.2% CPU · 30.4 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **41,799**<br><sub>(83 MiB / 43.8% CPU · 41.9 µs CPU/req)</sub> | **53,747**<br><sub>(126 MiB / 52.2% CPU · 38.9 µs CPU/req)</sub> | **60,539**<br><sub>(146 MiB / 48.6% CPU · 32.1 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/2 · TLS | 🥇 **24,735**<br><sub>(147 MiB / 48.6% CPU · 78.6 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **22,739**<br><sub>(85 MiB / 44.2% CPU · 77.8 µs CPU/req)</sub> | **17,725**<br><sub>(126 MiB / 57.9% CPU · 130.7 µs CPU/req)</sub> | **22,719**<br><sub>(158 MiB / 48.1% CPU · 84.7 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/3 · QUIC | 🥇 **46,386**<br><sub>(177 MiB / 55.5% CPU · 47.8 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **28,458**<br><sub>(129 MiB / 48.9% CPU · 68.8 µs CPU/req)</sub> | **41,152**<br><sub>(193 MiB / 48.2% CPU · 46.9 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | 🥇 **36,675**<br><sub>(128 MiB / 52.8% CPU · 57.6 µs CPU/req)</sub> | **28,216**<br><sub>(78 MiB / 45.2% CPU · 64.1 µs CPU/req)</sub> | **34,966**<br><sub>(66 MiB / 45.8% CPU · 52.4 µs CPU/req)</sub> | **20,281**<br><sub>(116 MiB / 63.4% CPU · 125.0 µs CPU/req)</sub> | **32,739**<br><sub>(122 MiB / 51.2% CPU · 62.5 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/1 · TLS | 🥇 **39,899**<br><sub>(146 MiB / 50.4% CPU · 50.5 µs CPU/req)</sub> | **33,740**<br><sub>(97 MiB / 44.3% CPU · 52.6 µs CPU/req)</sub> | **39,842**<br><sub>(71 MiB / 44% CPU · 44.2 µs CPU/req)</sub> | **27,898**<br><sub>(118 MiB / 57% CPU · 81.8 µs CPU/req)</sub> | **37,209**<br><sub>(135 MiB / 49.4% CPU · 53.1 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/2 · plain | 🥇 **82,151**<br><sub>(139 MiB / 36.2% CPU · 17.6 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **50,981**<br><sub>(66 MiB / 43% CPU · 33.8 µs CPU/req)</sub> | **30,683**<br><sub>(116 MiB / 59.9% CPU · 78.1 µs CPU/req)</sub> | **50,734**<br><sub>(138 MiB / 48.2% CPU · 38.0 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/2 · TLS | 🥇 **52,747**<br><sub>(155 MiB / 40.5% CPU · 30.7 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **27,366**<br><sub>(68 MiB / 46.2% CPU · 67.5 µs CPU/req)</sub> | **22,079**<br><sub>(116 MiB / 61.2% CPU · 110.8 µs CPU/req)</sub> | **36,532**<br><sub>(134 MiB / 47.5% CPU · 52.0 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/3 · QUIC | 🥇 **27,422**<br><sub>(181 MiB / 51.8% CPU · 75.5 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **10,359**<br><sub>(119 MiB / 56.8% CPU · 219.3 µs CPU/req)</sub> | **24,349**<br><sub>(159 MiB / 48% CPU · 78.9 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **66,059**<br><sub>(155 MiB / 51.7% CPU · 31.3 µs CPU/req)</sub> | **54,382**<br><sub>(101 MiB / 42.9% CPU · 31.6 µs CPU/req)</sub> | **63,277**<br><sub>(80 MiB / 44.7% CPU · 28.3 µs CPU/req)</sub> | **51,875**<br><sub>(127 MiB / 53.7% CPU · 41.4 µs CPU/req)</sub> | **61,086**<br><sub>(129 MiB / 48.4% CPU · 31.7 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/1 · TLS | 🥇 **24,652**<br><sub>(146 MiB / 50.7% CPU · 82.2 µs CPU/req)</sub> | **20,829**<br><sub>(105 MiB / 43.9% CPU · 84.3 µs CPU/req)</sub> | **23,714**<br><sub>(83 MiB / 45% CPU · 76.0 µs CPU/req)</sub> | **17,107**<br><sub>(128 MiB / 59.2% CPU · 138.4 µs CPU/req)</sub> | **21,872**<br><sub>(141 MiB / 50.9% CPU · 93.0 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | 🥇 **118,389**<br><sub>(158 MiB / 42.7% CPU · 14.4 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **60,468**<br><sub>(83 MiB / 44.1% CPU · 29.1 µs CPU/req)</sub> | **64,191**<br><sub>(126 MiB / 53.6% CPU · 33.4 µs CPU/req)</sub> | **82,189**<br><sub>(147 MiB / 47.7% CPU · 23.2 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **62,888**<br><sub>(171 MiB / 36% CPU · 22.9 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **44,087**<br><sub>(82 MiB / 40.3% CPU · 36.5 µs CPU/req)</sub> | **35,506**<br><sub>(126 MiB / 54% CPU · 60.9 µs CPU/req)</sub> | **43,060**<br><sub>(141 MiB / 46.1% CPU · 42.8 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/3 · QUIC | 🥇 **31,167**<br><sub>(202 MiB / 49.4% CPU · 63.5 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **20,220**<br><sub>(129 MiB / 52% CPU · 102.8 µs CPU/req)</sub> | **27,927**<br><sub>(170 MiB / 47.6% CPU · 68.2 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | **38,394**<br><sub>(170 MiB / 54.2% CPU · 56.5 µs CPU/req)</sub> | **44,806**<br><sub>(107 MiB / 31.4% CPU · 28.0 µs CPU/req)</sub> | 🥇 **47,891**<br><sub>(88 MiB / 35.1% CPU · 29.3 µs CPU/req)</sub> | **23,008**<br><sub>(131 MiB / 48.4% CPU · 84.1 µs CPU/req)</sub> | **33,631**<br><sub>(207 MiB / 48.7% CPU · 57.9 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | **17,778**<br><sub>(162 MiB / 52% CPU · 117.0 µs CPU/req)</sub> | **20,652**<br><sub>(109 MiB / 32.1% CPU · 62.3 µs CPU/req)</sub> | 🥇 **22,003**<br><sub>(90 MiB / 36.4% CPU · 66.2 µs CPU/req)</sub> | **9,595**<br><sub>(132 MiB / 56.5% CPU · 235.5 µs CPU/req)</sub> | **15,357**<br><sub>(203 MiB / 50.6% CPU · 131.7 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/2 · plain | **20,844**<br><sub>(152 MiB / 53.6% CPU · 102.8 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | 🥇 **22,442**<br><sub>(88 MiB / 35.6% CPU · 63.4 µs CPU/req)</sub> | **10,012**<br><sub>(130 MiB / 58.5% CPU · 233.8 µs CPU/req)</sub> | **18,229**<br><sub>(199 MiB / 50.5% CPU · 110.9 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | **42,691**<br><sub>(187 MiB / 54.9% CPU · 51.4 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | 🥇 **47,507**<br><sub>(89 MiB / 35.4% CPU · 29.8 µs CPU/req)</sub> | **27,562**<br><sub>(132 MiB / 49.5% CPU · 71.8 µs CPU/req)</sub> | **35,130**<br><sub>(212 MiB / 47.8% CPU · 54.4 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/3 · QUIC | 🥇 **31,786**<br><sub>(187 MiB / 50.8% CPU · 64.0 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **15,965**<br><sub>(130 MiB / 47% CPU · 117.6 µs CPU/req)</sub> | **26,277**<br><sub>(226 MiB / 46.8% CPU · 71.3 µs CPU/req)</sub> |

### MITM (TWP only)

Same Client×Origin wires with interception on (`compare-product` [37894265694](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37894265694)). **Lite** = no-op handlers (unchanged-lite finish). **Full** = append-only header mutation (harness: one probe header each way; product: generic append-only relay via `MitmCompressedRelayHelper`). nginx/HAProxy/Envoy/YARP cannot MITM. **Lite÷Reverse** / **Full÷Reverse** vs bare reverse (**same job / comparison-group shard**). Completion gate: Lite ≥ **0.25×** and Full ≥ **0.25×** reverse sustain @ c=64 (median of 3 GHA runs); reverse TWP ÷ closest peer ≥ **0.40×**.

**v1 append-only relay (2026-08-27):** Pre-fix H2→H2 Full÷Reverse was **0.13–0.16×** ([32960766249](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32960766249)). Post-fix @ `df172718`: H2 plain→H2 plain Full **0.77–0.79×**, H3→H1 Full **0.91–0.93×**, all MITM arms ≥ **0.70×** on median of [33041445371](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33041445371), [33055267086](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33055267086), [33055272140](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33055272140).

**v2 drop-only + non-unique append (2026-08-27):** `MitmStaticRebuildHelper` rebuilds static HPACK/QPACK after 1–4 unique header drops; trailing non-unique appends stay on compressed relay. @ `41f4adee`: all MITM arms ≥ **0.70×** on GHA median ([33087088466](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087088466), [33087091622](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087091622), [33105885748](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33105885748) Linux; [33087085235](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087085235), [33087088466](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087088466), [33087091622](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087091622) Windows). H2 plain→H2 plain Full **0.77–0.79×** (Win) / **0.78×** (Lin).

| Client | Origin | Lite sustain | Full sustain | Lite÷Reverse | Full÷Reverse |
|---|---|---:|---:|---:|---:|
| HTTP/1 · plain | HTTP/1 · plain | **63,891**<br><sub>(91 MiB / 49.3% CPU · 30.9 µs CPU/req)</sub> | **64,016**<br><sub>(97 MiB / 50% CPU · 31.3 µs CPU/req)</sub> | **0.99×** | **1×** |
| HTTP/1 · plain | HTTP/1 · TLS | **37,206**<br><sub>(110 MiB / 49.5% CPU · 53.2 µs CPU/req)</sub> | **36,152**<br><sub>(108 MiB / 49% CPU · 54.2 µs CPU/req)</sub> | **0.99×** | **0.96×** |
| HTTP/1 · plain | HTTP/2 · plain | **40,361**<br><sub>(124 MiB / 51.8% CPU · 51.4 µs CPU/req)</sub> | **39,048**<br><sub>(121 MiB / 52.5% CPU · 53.7 µs CPU/req)</sub> | **1×** | **0.96×** |
| HTTP/1 · plain | HTTP/2 · TLS | **32,102**<br><sub>(151 MiB / 50.5% CPU · 63.0 µs CPU/req)</sub> | **31,698**<br><sub>(143 MiB / 50.6% CPU · 63.8 µs CPU/req)</sub> | **0.96×** | **0.95×** |
| HTTP/1 · plain | HTTP/3 · QUIC | **33,872**<br><sub>(155 MiB / 52.6% CPU · 62.1 µs CPU/req)</sub> | **33,429**<br><sub>(165 MiB / 52.7% CPU · 63.0 µs CPU/req)</sub> | **0.99×** | **0.98×** |
| HTTP/1 · TLS | HTTP/1 · plain | **36,687**<br><sub>(111 MiB / 49.7% CPU · 54.2 µs CPU/req)</sub> | **35,754**<br><sub>(116 MiB / 49.5% CPU · 55.4 µs CPU/req)</sub> | **0.99×** | **0.97×** |
| HTTP/1 · TLS | HTTP/1 · TLS | **19,061**<br><sub>(116 MiB / 49.1% CPU · 103.1 µs CPU/req)</sub> | **18,785**<br><sub>(112 MiB / 49.2% CPU · 104.8 µs CPU/req)</sub> | **0.98×** | **0.97×** |
| HTTP/1 · TLS | HTTP/2 · plain | **66,361**<br><sub>(149 MiB / 50.6% CPU · 30.5 µs CPU/req)</sub> | **67,207**<br><sub>(162 MiB / 50.7% CPU · 30.2 µs CPU/req)</sub> | **1.02×** | **1.04×** |
| HTTP/1 · TLS | HTTP/2 · TLS | **24,166**<br><sub>(161 MiB / 48.8% CPU · 80.9 µs CPU/req)</sub> | **23,520**<br><sub>(164 MiB / 48.5% CPU · 82.6 µs CPU/req)</sub> | **0.98×** | **0.95×** |
| HTTP/1 · TLS | HTTP/3 · QUIC | **46,192**<br><sub>(187 MiB / 55.7% CPU · 48.2 µs CPU/req)</sub> | **46,704**<br><sub>(182 MiB / 55.2% CPU · 47.3 µs CPU/req)</sub> | **1×** | **1.01×** |
| HTTP/2 · plain | HTTP/1 · plain | **35,964**<br><sub>(148 MiB / 54.6% CPU · 60.7 µs CPU/req)</sub> | **35,586**<br><sub>(140 MiB / 54.4% CPU · 61.1 µs CPU/req)</sub> | **0.98×** | **0.97×** |
| HTTP/2 · plain | HTTP/1 · TLS | **39,010**<br><sub>(153 MiB / 50.7% CPU · 52.0 µs CPU/req)</sub> | **39,440**<br><sub>(158 MiB / 51.6% CPU · 52.3 µs CPU/req)</sub> | **0.98×** | **0.99×** |
| HTTP/2 · plain | HTTP/2 · plain | **68,466**<br><sub>(141 MiB / 40.8% CPU · 23.8 µs CPU/req)</sub> | **65,366**<br><sub>(155 MiB / 41.3% CPU · 25.3 µs CPU/req)</sub> | **0.83×** | **0.8×** |
| HTTP/2 · plain | HTTP/2 · TLS | **46,177**<br><sub>(157 MiB / 44.4% CPU · 38.5 µs CPU/req)</sub> | **44,918**<br><sub>(164 MiB / 44.5% CPU · 39.6 µs CPU/req)</sub> | **0.88×** | **0.85×** |
| HTTP/2 · plain | HTTP/3 · QUIC | **27,583**<br><sub>(161 MiB / 51.6% CPU · 74.9 µs CPU/req)</sub> | **26,927**<br><sub>(178 MiB / 52% CPU · 77.3 µs CPU/req)</sub> | **1.01×** | **0.98×** |
| HTTP/2 · TLS | HTTP/1 · plain | **65,865**<br><sub>(161 MiB / 52.9% CPU · 32.1 µs CPU/req)</sub> | **62,445**<br><sub>(152 MiB / 51.8% CPU · 33.2 µs CPU/req)</sub> | **1×** | **0.95×** |
| HTTP/2 · TLS | HTTP/1 · TLS | **24,118**<br><sub>(156 MiB / 51.7% CPU · 85.8 µs CPU/req)</sub> | **23,614**<br><sub>(151 MiB / 51.8% CPU · 87.7 µs CPU/req)</sub> | **0.98×** | **0.96×** |
| HTTP/2 · TLS | HTTP/2 · plain | **106,576**<br><sub>(167 MiB / 46.4% CPU · 17.4 µs CPU/req)</sub> | **101,467**<br><sub>(161 MiB / 45.3% CPU · 17.9 µs CPU/req)</sub> | **0.9×** | **0.86×** |
| HTTP/2 · TLS | HTTP/2 · TLS | **54,057**<br><sub>(173 MiB / 39.7% CPU · 29.4 µs CPU/req)</sub> | **52,338**<br><sub>(174 MiB / 39.6% CPU · 30.2 µs CPU/req)</sub> | **0.86×** | **0.83×** |
| HTTP/2 · TLS | HTTP/3 · QUIC | **31,279**<br><sub>(199 MiB / 50% CPU · 63.9 µs CPU/req)</sub> | **30,630**<br><sub>(201 MiB / 50% CPU · 65.4 µs CPU/req)</sub> | **1×** | **0.98×** |
| HTTP/3 · QUIC | HTTP/1 · plain | **38,172**<br><sub>(171 MiB / 54.7% CPU · 57.4 µs CPU/req)</sub> | **37,783**<br><sub>(172 MiB / 54.1% CPU · 57.2 µs CPU/req)</sub> | **0.99×** | **0.98×** |
| HTTP/3 · QUIC | HTTP/1 · TLS | **17,712**<br><sub>(178 MiB / 53% CPU · 119.7 µs CPU/req)</sub> | **17,019**<br><sub>(179 MiB / 53.5% CPU · 125.7 µs CPU/req)</sub> | **1×** | **0.96×** |
| HTTP/3 · QUIC | HTTP/2 · plain | **20,828**<br><sub>(155 MiB / 54.7% CPU · 105.0 µs CPU/req)</sub> | **20,673**<br><sub>(153 MiB / 53.5% CPU · 103.4 µs CPU/req)</sub> | **1×** | **0.99×** |
| HTTP/3 · QUIC | HTTP/2 · TLS | **44,064**<br><sub>(185 MiB / 55.3% CPU · 50.2 µs CPU/req)</sub> | **41,935**<br><sub>(184 MiB / 54.6% CPU · 52.1 µs CPU/req)</sub> | **1.03×** | **0.98×** |
| HTTP/3 · QUIC | HTTP/3 · QUIC | **31,980**<br><sub>(190 MiB / 53.8% CPU · 67.3 µs CPU/req)</sub> | **31,937**<br><sub>(188 MiB / 53.6% CPU · 67.2 µs CPU/req)</sub> | **1.01×** | **1×** |

## macOS — Titanium vs nginx vs HAProxy vs Envoy vs YARP

### Reverse

Median of **3 repeats** on `macos-15` (Apple Silicon M1, 3-core / 7 GB). Bare reverse 5×5 @ `347a9a1b` — `compare-product` [37894265694](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37894265694). Warmup 2s / measure 8s; concurrency 8, 16, 32, 64. Prefer TWP÷peer ratios over absolute RPS. **RPS cells** include median RSS / CPU at the peak-RPS step as `<br><sub>(MiB / CPU%)</sub>`. The RPS workflow installs Homebrew nginx (`http_v3_module`), Homebrew HAProxy with `USE_QUIC` (3.2 source fallback), Envoy (Homebrew bottle or pinned darwin-arm64 1.36.7), Homebrew `libmsquic` (+ `DYLD_*`), and YARP. Use the pinned `macos-15` label, not `macos-latest`. Product 5×5 is **~56-byte JSON keep-alive GET**; H2/H3 same-protocol cells are mostly header work with a tiny body (Titanium best case) — see [Why this comparison is fair](#user-content-why-this-comparison-is-fair).

| Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---:|---:|---:|---:|---:|
| HTTP/1 · plain | HTTP/1 · plain | **8,017**<br><sub>(112 MiB / 12.3% CPU · 45.9 µs CPU/req)</sub> | 🥇 **21,398**<br><sub>(68 MiB / 14.6% CPU · 20.5 µs CPU/req)</sub> | **14,085**<br><sub>(91 MiB / 12% CPU · 25.5 µs CPU/req)</sub> | **15,090**<br><sub>(111 MiB / 32.1% CPU · 63.8 µs CPU/req)</sub> | **16,292**<br><sub>(151 MiB / 23.4% CPU · 43.1 µs CPU/req)</sub> |
| HTTP/1 · plain | HTTP/1 · TLS | **12,757**<br><sub>(138 MiB / 20.7% CPU · 48.6 µs CPU/req)</sub> | 🥇 **13,777**<br><sub>(80 MiB / 14.5% CPU · 31.5 µs CPU/req)</sub> | **13,495**<br><sub>(97 MiB / 17.8% CPU · 39.6 µs CPU/req)</sub> | **7,051**<br><sub>(114 MiB / 19.7% CPU · 83.8 µs CPU/req)</sub> | **7,462**<br><sub>(166 MiB / 14.2% CPU · 57.2 µs CPU/req)</sub> |
| HTTP/1 · plain | HTTP/2 · plain | **40,596**<br><sub>(144 MiB / 37.7% CPU · 27.9 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **26,326**<br><sub>(152 MiB / 30.9% CPU · 35.2 µs CPU/req)</sub> | **32,386**<br><sub>(110 MiB / 35.6% CPU · 33.0 µs CPU/req)</sub> | 🥇 **41,511**<br><sub>(157 MiB / 35.5% CPU · 25.6 µs CPU/req)</sub> |
| HTTP/1 · plain | HTTP/2 · TLS | **11,105**<br><sub>(165 MiB / 10.5% CPU · 28.4 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **9,771**<br><sub>(98 MiB / 13.1% CPU · 40.3 µs CPU/req)</sub> | **12,345**<br><sub>(113 MiB / 32.7% CPU · 79.5 µs CPU/req)</sub> | 🥇 **17,373**<br><sub>(165 MiB / 12.8% CPU · 22.1 µs CPU/req)</sub> |
| HTTP/1 · plain | HTTP/3 · QUIC | 🥇 **11,185**<br><sub>(135 MiB / 32.2% CPU · 86.4 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **8,466**<br><sub>(116 MiB / 19.3% CPU · 68.4 µs CPU/req)</sub> | **8,636**<br><sub>(168 MiB / 17.4% CPU · 60.6 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/1 · plain | **11,183**<br><sub>(143 MiB / 18% CPU · 48.3 µs CPU/req)</sub> | **7,352**<br><sub>(84 MiB / 7.6% CPU · 30.8 µs CPU/req)</sub> | 🥇 **25,611**<br><sub>(109 MiB / 27.8% CPU · 32.6 µs CPU/req)</sub> | **12,533**<br><sub>(122 MiB / 32.5% CPU · 77.8 µs CPU/req)</sub> | **7,245**<br><sub>(163 MiB / 12.6% CPU · 52.3 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/1 · TLS | **6,183**<br><sub>(155 MiB / 16% CPU · 77.7 µs CPU/req)</sub> | **8,177**<br><sub>(89 MiB / 11.9% CPU · 43.7 µs CPU/req)</sub> | 🥇 **15,794**<br><sub>(111 MiB / 25.3% CPU · 48.0 µs CPU/req)</sub> | **6,601**<br><sub>(122 MiB / 23.6% CPU · 107.5 µs CPU/req)</sub> | **4,022**<br><sub>(peak 4,081 · 212 MiB / 14.7% CPU · 109.5 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/2 · plain | 🥇 **15,178**<br><sub>(172 MiB / 20.5% CPU · 40.5 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **8,095**<br><sub>(111 MiB / 13.9% CPU · 51.6 µs CPU/req)</sub> | **6,372**<br><sub>(121 MiB / 13.6% CPU · 64.2 µs CPU/req)</sub> | **6,712**<br><sub>(163 MiB / 9.5% CPU · 42.3 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/2 · TLS | **6,575**<br><sub>(205 MiB / 13.8% CPU · 63.0 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | 🥇 **13,369**<br><sub>(113 MiB / 23.3% CPU · 52.4 µs CPU/req)</sub> | **10,252**<br><sub>(121 MiB / 25% CPU · 73.2 µs CPU/req)</sub> | **7,665**<br><sub>(178 MiB / 11.8% CPU · 46.4 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/3 · QUIC | **3,586**<br><sub>(155 MiB / 16.8% CPU · 140.5 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | 🥇 **6,819**<br><sub>(125 MiB / 32.2% CPU · 141.8 µs CPU/req)</sub> | **6,622**<br><sub>(242 MiB / 18.7% CPU · 84.6 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | **19,844**<br><sub>(174 MiB / 22.4% CPU · 33.9 µs CPU/req)</sub> | 🥇 **31,905**<br><sub>(70 MiB / 19.8% CPU · 18.6 µs CPU/req)</sub> | **22,507**<br><sub>(98 MiB / 20.4% CPU · 27.2 µs CPU/req)</sub> | **13,807**<br><sub>(112 MiB / 29.8% CPU · 64.9 µs CPU/req)</sub> | **11,922**<br><sub>(153 MiB / 11.2% CPU · 28.3 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/1 · TLS | **21,541**<br><sub>(186 MiB / 24.1% CPU · 33.6 µs CPU/req)</sub> | **24,028**<br><sub>(81 MiB / 23.2% CPU · 29.0 µs CPU/req)</sub> | 🥇 **32,024**<br><sub>(123 MiB / 33.3% CPU · 31.2 µs CPU/req)</sub> | **21,090**<br><sub>(114 MiB / 31.2% CPU · 44.4 µs CPU/req)</sub> | **26,538**<br><sub>(169 MiB / 35.7% CPU · 40.4 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/2 · plain | **15,417**<br><sub>(234 MiB / 9% CPU · 17.5 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | 🥇 **22,039**<br><sub>(97 MiB / 26.8% CPU · 36.5 µs CPU/req)</sub> | **7,774**<br><sub>(111 MiB / 15.9% CPU · 61.3 µs CPU/req)</sub> | **6,314**<br><sub>(183 MiB / 7.1% CPU · 33.9 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/2 · TLS | 🥇 **50,715**<br><sub>(253 MiB / 19.7% CPU · 11.6 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **40,466**<br><sub>(140 MiB / 32.2% CPU · 23.8 µs CPU/req)</sub> | **18,846**<br><sub>(112 MiB / 17.3% CPU · 27.5 µs CPU/req)</sub> | **46,422**<br><sub>(165 MiB / 20.5% CPU · 13.2 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/3 · QUIC | **5,033**<br><sub>(216 MiB / 15.1% CPU · 90.1 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | 🥇 **5,439**<br><sub>(115 MiB / 21.8% CPU · 120.0 µs CPU/req)</sub> | **4,763**<br><sub>(167 MiB / 12.2% CPU · 76.7 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | **16,748**<br><sub>(167 MiB / 22.6% CPU · 40.6 µs CPU/req)</sub> | **16,527**<br><sub>(83 MiB / 14.9% CPU · 27.0 µs CPU/req)</sub> | 🥇 **25,396**<br><sub>(109 MiB / 27% CPU · 31.9 µs CPU/req)</sub> | **14,801**<br><sub>(121 MiB / 32.7% CPU · 66.3 µs CPU/req)</sub> | **10,417**<br><sub>(190 MiB / 14.9% CPU · 42.8 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/1 · TLS | 🥇 **11,125**<br><sub>(191 MiB / 22.8% CPU · 61.5 µs CPU/req)</sub> | **6,580**<br><sub>(88 MiB / 9.5% CPU · 43.4 µs CPU/req)</sub> | **5,712**<br><sub>(111 MiB / 15% CPU · 78.7 µs CPU/req)</sub> | **5,754**<br><sub>(123 MiB / 20.1% CPU · 104.8 µs CPU/req)</sub> | **5,663**<br><sub>(198 MiB / 14.9% CPU · 78.8 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | 🥇 **15,756**<br><sub>(273 MiB / 15.7% CPU · 29.8 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **10,404**<br><sub>(112 MiB / 17.8% CPU · 51.4 µs CPU/req)</sub> | **11,612**<br><sub>(120 MiB / 20.3% CPU · 52.4 µs CPU/req)</sub> | **5,846**<br><sub>(217 MiB / 9.4% CPU · 48.0 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | **12,987**<br><sub>(255 MiB / 15% CPU · 34.7 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **16,248**<br><sub>(112 MiB / 26.3% CPU · 48.6 µs CPU/req)</sub> | 🥇 **24,647**<br><sub>(121 MiB / 28.2% CPU · 34.4 µs CPU/req)</sub> | **12,327**<br><sub>(180 MiB / 13.9% CPU · 33.9 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/3 · QUIC | 🥇 **6,638**<br><sub>(256 MiB / 20.4% CPU · 92.0 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **4,546**<br><sub>(123 MiB / 15.6% CPU · 102.7 µs CPU/req)</sub> | **4,682**<br><sub>(186 MiB / 10.7% CPU · 68.3 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | **12,271**<br><sub>(141 MiB / 43.2% CPU · 105.6 µs CPU/req)</sub> | **0**<br><sub>(peak 9,959 · 86 MiB / 16.8% CPU)</sub> | 🥇 **18,976**<br><sub>(105 MiB / 31.1% CPU · 49.2 µs CPU/req)</sub> | **8,425**<br><sub>(125 MiB / 53.7% CPU · 191.1 µs CPU/req)</sub> | **13,415**<br><sub>(221 MiB / 44.2% CPU · 98.9 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | **6,400**<br><sub>(171 MiB / 23.4% CPU · 109.6 µs CPU/req)</sub> | **0**<br><sub>(peak 4,768 · 91 MiB / 8.4% CPU)</sub> | 🥇 **11,567**<br><sub>(108 MiB / 21.4% CPU · 55.4 µs CPU/req)</sub> | **4,881**<br><sub>(127 MiB / 27.9% CPU · 171.7 µs CPU/req)</sub> | **4,408**<br><sub>(283 MiB / 18.1% CPU · 123.0 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/2 · plain | **8,501**<br><sub>(142 MiB / 13.4% CPU · 47.3 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **11,924**<br><sub>(107 MiB / 17.2% CPU · 43.3 µs CPU/req)</sub> | **7,540**<br><sub>(123 MiB / 22.3% CPU · 88.9 µs CPU/req)</sub> | 🥇 **12,019**<br><sub>(251 MiB / 21.3% CPU · 53.2 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | **8,591**<br><sub>(144 MiB / 16.4% CPU · 57.2 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **8,401**<br><sub>(108 MiB / 17.4% CPU · 62.1 µs CPU/req)</sub> | 🥇 **13,678**<br><sub>(123 MiB / 42.5% CPU · 93.2 µs CPU/req)</sub> | **9,810**<br><sub>(274 MiB / 18.9% CPU · 57.8 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/3 · QUIC | 🥇 **6,226**<br><sub>(136 MiB / 32% CPU · 153.9 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **3,782**<br><sub>(123 MiB / 32.4% CPU · 256.9 µs CPU/req)</sub> | **2,526**<br><sub>(261 MiB / 12.8% CPU · 151.9 µs CPU/req)</sub> |

## Editions (CLI / Plus / Intercept)

**Note:** `twp-reverse-http1` and other library rows use Core with **probe-tuned** settings (no logging, no Via header, probe-warmed certs). Edition rows use `titanium run -c twp.yaml` **product defaults** — prefer the ÷baseline ratio column over absolute RPS. Inspector GUI is not spawnable in the harness; session-path overhead is `twp-cli-intercept-http1` (route `RequestHeaderSet` transform). Pre-origin Plus middleware (CIDR/WAF/JWT/rate-limit/cache) runs on H1 terminate-lite without `SessionEventArgs`; a cache hit skips the origin. JWT caches successful bearer validations. CORS only adds response headers on the way out, so it stays on terminate-lite. Circuit breaker and idempotent retry have to see the request or the status code, so they stay on the session path and should land near the intercept row. Maintainer gate thresholds live under [Maintainer notes](#user-content-maintainer-notes).

Median of **3** repeats on `ubuntu-latest`. Linux rows in this paste were re-measured @ `347a9a1b` (Actions [37894305384](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37894305384)). Other edition rows stay @ `the previous run`. Warmup 2s / measure 8s; concurrency 8–64; sustain = median peak RPS among SLO-pass steps @ **c=64**. **RPS cells** show sustain; `<sub>` holds peak (when higher) plus `(MiB / CPU%)`. The Gate column is that script's floor.

| Arm | Linux | Lin÷ | Gate |
|---|---:|---:|---|
| `twp-cli-reverse-http1` vs library | **33,440**<br><sub>(167 MiB / 50.1% CPU · 60.0 µs CPU/req)</sub> | **1.08×** | ≥ **0.50×** |
| `twp-cli-reverse-http1-tls` vs library TLS | **24,017**<br><sub>(198 MiB / 50.8% CPU · 84.6 µs CPU/req)</sub> | **1.03×** | ≥ **0.50×** |
| `twp-cli-reverse-http1-route` vs CLI | **33,827**<br><sub>(169 MiB / 49.7% CPU · 58.8 µs CPU/req)</sub> | **1.01×** | ≥ **0.50×** |
| `twp-cli-plus-base-http1` vs CLI | **32,853**<br><sub>(172 MiB / 49.4% CPU · 60.1 µs CPU/req)</sub> | **1.01×** | ≥ **0.50×** |
| `twp-cli-plus-cache-http1` (cold) vs CLI | **57,846**<br><sub>(169 MiB / 48.4% CPU · 33.4 µs CPU/req)</sub> | **1.70×** | ≥ **0.50×** |
| `twp-cli-intercept-http1` vs CLI | **24,338**<br><sub>(176 MiB / 53.2% CPU · 87.5 µs CPU/req)</sub> | **0.74×** | ≥ **0.50×** |
| `twp-cli-plus-waf-http1` vs CLI | **61,281**<br><sub>(172 MiB / 49.1% CPU · 32.0 µs CPU/req)</sub> | **0.97×** | ≥ **0.50×** |
| `twp-cli-plus-cidr-http1` vs CLI | **46,680**<br><sub>(171 MiB / 49.8% CPU · 42.6 µs CPU/req)</sub> | **0.99×** | ≥ **0.50×** |
| `twp-cli-plus-jwt-http1` vs CLI | **42,804**<br><sub>(202 MiB / 49.1% CPU · 45.9 µs CPU/req)</sub> | **0.92×** | ≥ **0.50×** |
| `twp-cli-plus-ratelimit-http1` vs CLI | **46,628**<br><sub>(173 MiB / 49.6% CPU · 42.5 µs CPU/req)</sub> | **0.98×** | ≥ **0.50×** |
| `twp-cli-plus-resilience-http1` vs CLI | **63,941**<br><sub>(175 MiB / 48.9% CPU · 30.6 µs CPU/req)</sub> | **0.98×** | ≥ **0.50×** |
| `twp-cli-plus-discovery-file-http1` vs CLI | **32,904**<br><sub>(173 MiB / 50.1% CPU · 60.9 µs CPU/req)</sub> | **1.02×** | ≥ **0.50×** |
| `twp-cli-plus-metrics-scrape-http1` vs CLI | **48,514**<br><sub>(177 MiB / 50.3% CPU · 41.5 µs CPU/req)</sub> | **1.01×** | ≥ **0.50×** |
| `twp-cli-plus-cache-hit-http1` vs cache cold | **60,240**<br><sub>(171 MiB / 49.5% CPU · 32.9 µs CPU/req)</sub> | **1.04×** | ≥ **0.50×** |
| `twp-cli-plus-cors-http1` vs CLI | **44,497**<br><sub>(174 MiB / 49.7% CPU · 44.7 µs CPU/req)</sub> | **0.94×** | ≥ **0.50×** |
| `twp-cli-plus-circuit-http1` vs CLI | **25,983**<br><sub>(179 MiB / 51.6% CPU · 79.4 µs CPU/req)</sub> | **0.79×** | ≥ **0.50×** |
| `twp-cli-plus-retry-http1` vs CLI | **50,626**<br><sub>(178 MiB / 49.2% CPU · 38.8 µs CPU/req)</sub> | **0.81×** | ≥ **0.50×** |
| `twp-cli-static-http1` vs CLI | **116,931**<br><sub>(168 MiB / 48.4% CPU · 16.6 µs CPU/req)</sub> | **1.76×** | ≥ **0.50×** |
| `twp-cli-logging-http1` vs CLI | **33,425**<br><sub>(168 MiB / 50.2% CPU · 60.1 µs CPU/req)</sub> | **1.01×** | ≥ **0.50×** |
| `twp-cli-lb-leasttime-http1` vs route | **31,228**<br><sub>(191 MiB / 52.2% CPU · 66.9 µs CPU/req)</sub> | **0.94×** | ≥ **0.50×** |
| `twp-cli-dialect-twp-http1` vs CLI | **33,600**<br><sub>(163 MiB / 49.7% CPU · 59.1 µs CPU/req)</sub> | **1.00×** | ≥ **0.50×** |

`validate-edition-gates.ps1` floors are **0.50×**. Each ÷ column uses the two arms from the same job. Circuit breaker and idempotent retry stay on the session path, so a ratio near intercept is expected. Laptop smoke ratios stay on [Performance Local Lab — Editions](Performance-Local-Lab#user-content-editions-cli--plus-stress).

## Heavier reverse workloads

Same runners and harness as the tiny-GET tables, but with larger bodies, POST, lossy links, TLS cost, and architecture-sensitive paths (slow consumer / early response / duplex). **PUT with the same body is the same proxy work as POST; DELETE with no body matches GET** — only POST is published. Bodies/POST/lossy stay **half-duplex**. Laptop numbers are on [Performance Local Lab](Performance-Local-Lab#user-content-architecture-sensitive). How maintainers refresh these tables (modes, shards, paste scripts) is under [Maintainer notes](#user-content-maintainer-notes). **Larger-body check:** 64 / 256 KiB H2 TLS→H2 TLS is where body copy dominates headers — ratios vs YARP are in the tables below (@ `347a9a1b`), unlike the tiny-GET H2↔H2 medals above. Those cells also show goodput (MiB/s) and µs CPU/req, and the medal on those rows follows goodput.

Lossy link = **userspace** delay/drop shim (not kernel `netem`): TCP gets per-buffer delay + occasional whole-connection stalls (honest head-of-line for multiplexed HTTP/2); UDP is **loss% only** (no per-datagram delay) + drops (QUIC / MsQuic-safe). Lossy tables publish HTTP/1, HTTP/2, and HTTP/3.

### Windows — heavier reverse GET (64 KiB / 256 KiB)

Median of **3** repeats on `windows-latest` @ `347a9a1b`. Source: Actions [37894273314](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37894273314) (`compare-bodies`). Warmup 2s / measure 8s. **RPS cells** include `(MiB / CPU%)` footprints.

*Not possible:* **HAProxy** and **Envoy** columns are omitted (no official Windows port).



















| Body | Client | Origin | TWP | nginx | YARP |
|---|---|---|---:|---:|---:|
| 64 KiB | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **11,227**<br><sub>(102 MiB / 40.7% CPU · 702 MiB/s · 145.0 µs CPU/req)</sub> | **779**<br><sub>(143 MiB / 24.8% CPU · 49 MiB/s · 1271.5 µs CPU/req)</sub> | **9,394**<br><sub>(135 MiB / 47.6% CPU · 587 MiB/s · 202.6 µs CPU/req)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **18,471**<br><sub>(199 MiB / 52.6% CPU · 1,154 MiB/s · 113.9 µs CPU/req)</sub> | **1,220**<br><sub>(142 MiB / 24.8% CPU · 76 MiB/s · 814.2 µs CPU/req)</sub> | **16,977**<br><sub>(117 MiB / 52.0% CPU · 1,061 MiB/s · 122.6 µs CPU/req)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **3,936**<br><sub>(146 MiB / 43.9% CPU · 246 MiB/s · 446.2 µs CPU/req)</sub> | *Not possible (no QUIC)* | **2,009**<br><sub>(176 MiB / 51.7% CPU · 126 MiB/s · 1028.7 µs CPU/req)</sub> |
| 256 KiB | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **3,171**<br><sub>(134 MiB / 41.9% CPU · 793 MiB/s · 528.7 µs CPU/req)</sub> | **206**<br><sub>(142 MiB / 24.8% CPU · 52 MiB/s · 4798.1 µs CPU/req)</sub> | **2,721**<br><sub>(135 MiB / 42.9% CPU · 680 MiB/s · 630.7 µs CPU/req)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/1 · plain | **2,404**<br><sub>(172 MiB / 50.8% CPU · 601 MiB/s · 845.0 µs CPU/req)</sub> | **155**<br><sub>(142 MiB / 24.8% CPU · 39 MiB/s · 6411.4 µs CPU/req)</sub> | 🥇 **2,706**<br><sub>(123 MiB / 47.2% CPU · 677 MiB/s · 698.0 µs CPU/req)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **451**<br><sub>(121 MiB / 47.5% CPU · 113 MiB/s · 4215.7 µs CPU/req)</sub> | *Not possible (no QUIC)* | **349**<br><sub>(153 MiB / 50.7% CPU · 87 MiB/s · 5808.0 µs CPU/req)</sub> |
| 64 KiB | HTTP/2 · plain | HTTP/1 · plain | 🥇 **12,488**<br><sub>(188 MiB / 43.5% CPU · 781 MiB/s · 139.5 µs CPU/req)</sub> | **2,333**<br><sub>(128 MiB / 24.8% CPU · 146 MiB/s · 425.3 µs CPU/req)</sub> | **10,679**<br><sub>(120 MiB / 47.7% CPU · 667 MiB/s · 178.6 µs CPU/req)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/2 · plain | 🥇 **8,387**<br><sub>(171 MiB / 46.0% CPU · 524 MiB/s · 219.2 µs CPU/req)</sub> | *Not possible* | **7,640**<br><sub>(134 MiB / 52.4% CPU · 477 MiB/s · 274.4 µs CPU/req)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **6,162**<br><sub>(168 MiB / 43.1% CPU · 385 MiB/s · 280.0 µs CPU/req)</sub> | *Not possible* | **5,304**<br><sub>(139 MiB / 49.9% CPU · 331 MiB/s · 376.2 µs CPU/req)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/2 · TLS | 🥇 **3,231**<br><sub>(131 MiB / 41.0% CPU · 202 MiB/s · 508.1 µs CPU/req)</sub> | *Not possible* | **1,685**<br><sub>(182 MiB / 50.6% CPU · 105 MiB/s · 1201.5 µs CPU/req)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **7,482**<br><sub>(159 MiB / 42.7% CPU · 468 MiB/s · 228.3 µs CPU/req)</sub> | *Not possible (no QUIC)* | **4,844**<br><sub>(194 MiB / 50.7% CPU · 303 MiB/s · 418.5 µs CPU/req)</sub> |
| 256 KiB | HTTP/2 · plain | HTTP/1 · plain | 🥇 **5,944**<br><sub>(179 MiB / 30.0% CPU · 1,486 MiB/s · 202.1 µs CPU/req)</sub> | **1,465**<br><sub>(128 MiB / 24.1% CPU · 366 MiB/s · 659.2 µs CPU/req)</sub> | **5,776**<br><sub>(117 MiB / 35.6% CPU · 1,444 MiB/s · 246.9 µs CPU/req)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/2 · plain | **3,293**<br><sub>(200 MiB / 42.5% CPU · 823 MiB/s · 516.8 µs CPU/req)</sub> | *Not possible* | 🥇 **3,407**<br><sub>(167 MiB / 43.8% CPU · 852 MiB/s · 514.7 µs CPU/req)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **2,826**<br><sub>(204 MiB / 42.3% CPU · 707 MiB/s · 598.3 µs CPU/req)</sub> | *Not possible* | **2,775**<br><sub>(145 MiB / 42.2% CPU · 694 MiB/s · 608.8 µs CPU/req)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/2 · TLS | **486**<br><sub>(154 MiB / 49.4% CPU · 122 MiB/s · 4064.2 µs CPU/req)</sub> | *Not possible* | 🥇 **535**<br><sub>(195 MiB / 49.8% CPU · 134 MiB/s · 3718.3 µs CPU/req)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **172**<br><sub>(109 MiB / 46.7% CPU · 43 MiB/s · 10878.8 µs CPU/req)</sub> | *Not possible (no QUIC)* | **112**<br><sub>(peak 158 · 129 MiB / 48.6% CPU · 28 MiB/s · 17402.5 µs CPU/req)</sub> |

nginx/Windows collapses on large reverse bodies in this harness; treat as same-OS only. Prefer the ratio columns; absolute RPS swings by VM.

### Linux — heavier reverse GET (64 KiB / 256 KiB)

Median of **3** repeats @ `347a9a1b`. Source: Actions [37894273314](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37894273314) (`compare-bodies`). Warmup 2s / measure 8s.


| Body | Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---|---:|---:|---:|---:|---:|
| 64 KiB | HTTP/1 · TLS | HTTP/1 · plain | **8,187**<br><sub>(125 MiB / 44.4% CPU · 512 MiB/s · 216.8 µs CPU/req)</sub> | **5,463**<br><sub>(98 MiB / 51.3% CPU · 341 MiB/s · 375.8 µs CPU/req)</sub> | 🥇 **8,384**<br><sub>(89 MiB / 40.7% CPU · 524 MiB/s · 194.0 µs CPU/req)</sub> | **7,098**<br><sub>(132 MiB / 45.4% CPU · 444 MiB/s · 255.6 µs CPU/req)</sub> | **6,237**<br><sub>(159 MiB / 48.6% CPU · 390 MiB/s · 312.0 µs CPU/req)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **6,737**<br><sub>(265 MiB / 44.8% CPU · 421 MiB/s · 265.7 µs CPU/req)</sub> | **4,347**<br><sub>(100 MiB / 50.0% CPU · 272 MiB/s · 460.0 µs CPU/req)</sub> | **6,088**<br><sub>(87 MiB / 46.8% CPU · 381 MiB/s · 307.3 µs CPU/req)</sub> | **6,619**<br><sub>(134 MiB / 47.1% CPU · 414 MiB/s · 284.6 µs CPU/req)</sub> | **5,944**<br><sub>(166 MiB / 49.0% CPU · 371 MiB/s · 329.5 µs CPU/req)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/1 · plain | **10,218**<br><sub>(225 MiB / 45.7% CPU · 639 MiB/s · 178.9 µs CPU/req)</sub> | **4,818**<br><sub>(118 MiB / 40.4% CPU · 301 MiB/s · 335.7 µs CPU/req)</sub> | 🥇 **10,478**<br><sub>(96 MiB / 36.6% CPU · 655 MiB/s · 139.7 µs CPU/req)</sub> | **8,066**<br><sub>(141 MiB / 44.5% CPU · 504 MiB/s · 220.9 µs CPU/req)</sub> | **6,880**<br><sub>(248 MiB / 51.5% CPU · 430 MiB/s · 299.3 µs CPU/req)</sub> |
| 256 KiB | HTTP/1 · TLS | HTTP/1 · plain | **4,663**<br><sub>(123 MiB / 29.6% CPU · 1,166 MiB/s · 253.9 µs CPU/req)</sub> | **2,972**<br><sub>(101 MiB / 48.5% CPU · 743 MiB/s · 652.1 µs CPU/req)</sub> | 🥇 **4,888**<br><sub>(86 MiB / 25.9% CPU · 1,222 MiB/s · 212.0 µs CPU/req)</sub> | **4,231**<br><sub>(145 MiB / 31.0% CPU · 1,058 MiB/s · 293.4 µs CPU/req)</sub> | **3,790**<br><sub>(165 MiB / 39.9% CPU · 947 MiB/s · 421.2 µs CPU/req)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/1 · plain | **2,824**<br><sub>(223 MiB / 38.4% CPU · 706 MiB/s · 544.1 µs CPU/req)</sub> | **2,214**<br><sub>(102 MiB / 47.6% CPU · 553 MiB/s · 860.2 µs CPU/req)</sub> | **2,930**<br><sub>(86 MiB / 37.0% CPU · 732 MiB/s · 504.5 µs CPU/req)</sub> | 🥇 **3,213**<br><sub>(152 MiB / 34.0% CPU · 803 MiB/s · 423.3 µs CPU/req)</sub> | **2,548**<br><sub>(161 MiB / 42.3% CPU · 637 MiB/s · 663.7 µs CPU/req)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/1 · plain | **1,522**<br><sub>(180 MiB / 48.6% CPU · 380 MiB/s · 1277.9 µs CPU/req)</sub> | **731**<br><sub>(117 MiB / 30.0% CPU · 183 MiB/s · 1640.7 µs CPU/req)</sub> | 🥇 **1,846**<br><sub>(97 MiB / 34.4% CPU · 462 MiB/s · 745.7 µs CPU/req)</sub> | **1,605**<br><sub>(153 MiB / 45.4% CPU · 401 MiB/s · 1132.4 µs CPU/req)</sub> | **1,251**<br><sub>(236 MiB / 51.5% CPU · 313 MiB/s · 1647.1 µs CPU/req)</sub> |
| 64 KiB | HTTP/2 · plain | HTTP/1 · plain | **13,787**<br><sub>(251 MiB / 42.6% CPU · 862 MiB/s · 123.7 µs CPU/req)</sub> | **8,804**<br><sub>(79 MiB / 49.8% CPU · 550 MiB/s · 226.4 µs CPU/req)</sub> | **12,595**<br><sub>(73 MiB / 43.9% CPU · 787 MiB/s · 139.3 µs CPU/req)</sub> | 🥇 **13,803**<br><sub>(124 MiB / 47.1% CPU · 863 MiB/s · 136.4 µs CPU/req)</sub> | **13,419**<br><sub>(144 MiB / 44.0% CPU · 839 MiB/s · 131.1 µs CPU/req)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/2 · plain | 🥇 **10,871**<br><sub>(207 MiB / 38.4% CPU · 679 MiB/s · 141.2 µs CPU/req)</sub> | *Not possible* | **8,314**<br><sub>(90 MiB / 43.7% CPU · 520 MiB/s · 210.1 µs CPU/req)</sub> | **9,624**<br><sub>(135 MiB / 43.7% CPU · 602 MiB/s · 181.7 µs CPU/req)</sub> | **7,964**<br><sub>(206 MiB / 48.1% CPU · 498 MiB/s · 241.8 µs CPU/req)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **4,879**<br><sub>(208 MiB / 41.7% CPU · 305 MiB/s · 342.2 µs CPU/req)</sub> | *Not possible* | **4,132**<br><sub>(90 MiB / 44.5% CPU · 258 MiB/s · 431.0 µs CPU/req)</sub> | **4,614**<br><sub>(141 MiB / 43.8% CPU · 288 MiB/s · 379.4 µs CPU/req)</sub> | **3,485**<br><sub>(181 MiB / 46.8% CPU · 218 MiB/s · 537.4 µs CPU/req)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/2 · TLS | **7,789**<br><sub>(204 MiB / 44.8% CPU · 487 MiB/s · 229.8 µs CPU/req)</sub> | *Not possible* | 🥇 **7,921**<br><sub>(99 MiB / 40.3% CPU · 495 MiB/s · 203.5 µs CPU/req)</sub> | **5,884**<br><sub>(147 MiB / 46.6% CPU · 368 MiB/s · 317.1 µs CPU/req)</sub> | **5,260**<br><sub>(274 MiB / 49.3% CPU · 329 MiB/s · 374.9 µs CPU/req)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/1 · TLS | **7,056**<br><sub>(227 MiB / 43.8% CPU · 441 MiB/s · 248.2 µs CPU/req)</sub> | **3,793**<br><sub>(121 MiB / 34.8% CPU · 237 MiB/s · 367.1 µs CPU/req)</sub> | 🥇 **7,456**<br><sub>(98 MiB / 42.2% CPU · 466 MiB/s · 226.2 µs CPU/req)</sub> | **6,581**<br><sub>(139 MiB / 46.2% CPU · 411 MiB/s · 280.9 µs CPU/req)</sub> | **5,555**<br><sub>(269 MiB / 49.9% CPU · 347 MiB/s · 358.9 µs CPU/req)</sub> |
| 256 KiB | HTTP/2 · plain | HTTP/1 · plain | **2,870**<br><sub>(212 MiB / 36.3% CPU · 717 MiB/s · 506.4 µs CPU/req)</sub> | **2,161**<br><sub>(78 MiB / 50.5% CPU · 540 MiB/s · 934.5 µs CPU/req)</sub> | 🥇 **3,142**<br><sub>(72 MiB / 32.7% CPU · 786 MiB/s · 416.4 µs CPU/req)</sub> | **3,142**<br><sub>(144 MiB / 33.2% CPU · 786 MiB/s · 422.1 µs CPU/req)</sub> | **2,769**<br><sub>(151 MiB / 37.8% CPU · 692 MiB/s · 546.0 µs CPU/req)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/2 · plain | 🥇 **2,057**<br><sub>(250 MiB / 37.8% CPU · 514 MiB/s · 735.2 µs CPU/req)</sub> | *Not possible* | **1,940**<br><sub>(99 MiB / 38.8% CPU · 485 MiB/s · 800.6 µs CPU/req)</sub> | **2,046**<br><sub>(144 MiB / 37.4% CPU · 512 MiB/s · 731.6 µs CPU/req)</sub> | **1,491**<br><sub>(212 MiB / 44.8% CPU · 373 MiB/s · 1203.0 µs CPU/req)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/2 · TLS | **1,675**<br><sub>(275 MiB / 36.9% CPU · 419 MiB/s · 880.2 µs CPU/req)</sub> | *Not possible* | 🥇 **1,731**<br><sub>(96 MiB / 35.6% CPU · 433 MiB/s · 822.2 µs CPU/req)</sub> | **1,720**<br><sub>(145 MiB / 37.2% CPU · 430 MiB/s · 864.3 µs CPU/req)</sub> | **1,281**<br><sub>(189 MiB / 43.6% CPU · 320 MiB/s · 1360.6 µs CPU/req)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/2 · TLS | **1,248**<br><sub>(203 MiB / 57.9% CPU · 312 MiB/s · 1856.0 µs CPU/req)</sub> | *Not possible* | 🥇 **2,094**<br><sub>(106 MiB / 37.3% CPU · 523 MiB/s · 712.3 µs CPU/req)</sub> | **1,708**<br><sub>(157 MiB / 42.9% CPU · 427 MiB/s · 1005.4 µs CPU/req)</sub> | **1,478**<br><sub>(243 MiB / 46.9% CPU · 369 MiB/s · 1269.4 µs CPU/req)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/1 · TLS | **1,196**<br><sub>(207 MiB / 47.9% CPU · 299 MiB/s · 1601.8 µs CPU/req)</sub> | **706**<br><sub>(120 MiB / 31.2% CPU · 177 MiB/s · 1768.1 µs CPU/req)</sub> | 🥇 **1,470**<br><sub>(97 MiB / 36.3% CPU · 367 MiB/s · 989.0 µs CPU/req)</sub> | **1,306**<br><sub>(153 MiB / 44.8% CPU · 327 MiB/s · 1372.5 µs CPU/req)</sub> | **1,035**<br><sub>(252 MiB / 49.0% CPU · 259 MiB/s · 1891.8 µs CPU/req)</sub> |

Prefer the ratio columns; absolute RPS swings by VM.

### macOS — heavier reverse GET (64 KiB / 256 KiB)

Median of **3** repeats on `macos-15` (Apple Silicon M1, 3-core / 7 GB) @ `347a9a1b`. Source: Actions [37894273314](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37894273314) (`compare-bodies`). Warmup 2s / measure 8s.

| Body | Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---|---:|---:|---:|---:|---:|
| 64 KiB | HTTP/1 · TLS | HTTP/1 · plain | **3,882**<br><sub>(190 MiB / 19.9% CPU · 243 MiB/s · 153.5 µs CPU/req)</sub> | 🥇 **5,421**<br><sub>(85 MiB / 18.8% CPU · 339 MiB/s · 104.0 µs CPU/req)</sub> | **5,263**<br><sub>(122 MiB / 22.1% CPU · 329 MiB/s · 125.9 µs CPU/req)</sub> | **5,034**<br><sub>(132 MiB / 29.8% CPU · 315 MiB/s · 177.6 µs CPU/req)</sub> | **2,592**<br><sub>(180 MiB / 18.3% CPU · 162 MiB/s · 212.1 µs CPU/req)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/1 · plain | **2,429**<br><sub>(491 MiB / 17.8% CPU · 152 MiB/s · 219.2 µs CPU/req)</sub> | **4,360**<br><sub>(87 MiB / 20.4% CPU · 272 MiB/s · 140.2 µs CPU/req)</sub> | 🥇 **4,478**<br><sub>(120 MiB / 26.0% CPU · 280 MiB/s · 174.1 µs CPU/req)</sub> | **4,087**<br><sub>(144 MiB / 27.6% CPU · 255 MiB/s · 202.3 µs CPU/req)</sub> | **2,104**<br><sub>(176 MiB / 17.6% CPU · 131 MiB/s · 250.4 µs CPU/req)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/1 · plain | **1,686**<br><sub>(280 MiB / 30.9% CPU · 105 MiB/s · 549.4 µs CPU/req)</sub> | **1,592**<br><sub>(95 MiB / 22.1% CPU · 100 MiB/s · 415.9 µs CPU/req)</sub> | 🥇 **1,775**<br><sub>(129 MiB / 32.5% CPU · 111 MiB/s · 548.7 µs CPU/req)</sub> | **1,530**<br><sub>(133 MiB / 52.5% CPU · 96 MiB/s · 1028.9 µs CPU/req)</sub> | **772**<br><sub>(467 MiB / 27.8% CPU · 48 MiB/s · 1080.8 µs CPU/req)</sub> |
| 256 KiB | HTTP/1 · TLS | HTTP/1 · plain | **2,257**<br><sub>(209 MiB / 26.2% CPU · 564 MiB/s · 348.8 µs CPU/req)</sub> | **1,844**<br><sub>(84 MiB / 20.3% CPU · 461 MiB/s · 330.2 µs CPU/req)</sub> | 🥇 **2,587**<br><sub>(117 MiB / 26.1% CPU · 647 MiB/s · 302.6 µs CPU/req)</sub> | **1,887**<br><sub>(155 MiB / 26.3% CPU · 472 MiB/s · 418.8 µs CPU/req)</sub> | **1,807**<br><sub>(181 MiB / 33.5% CPU · 452 MiB/s · 556.9 µs CPU/req)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/1 · plain | **1,432**<br><sub>(441 MiB / 24.6% CPU · 358 MiB/s · 516.0 µs CPU/req)</sub> | **2,206**<br><sub>(86 MiB / 25.0% CPU · 552 MiB/s · 340.4 µs CPU/req)</sub> | 🥇 **2,376**<br><sub>(116 MiB / 28.9% CPU · 594 MiB/s · 364.7 µs CPU/req)</sub> | **2,189**<br><sub>(154 MiB / 28.6% CPU · 547 MiB/s · 392.6 µs CPU/req)</sub> | **1,994**<br><sub>(177 MiB / 35.3% CPU · 499 MiB/s · 531.7 µs CPU/req)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/1 · plain | **445**<br><sub>(186 MiB / 33.1% CPU · 111 MiB/s · 2232.0 µs CPU/req)</sub> | 🥇 **728**<br><sub>(95 MiB / 27.2% CPU · 182 MiB/s · 1123.1 µs CPU/req)</sub> | **246**<br><sub>(peak 582 · 106 MiB / 22.1% CPU · 62 MiB/s · 2687.0 µs CPU/req)</sub> | **598**<br><sub>(152 MiB / 51.8% CPU · 150 MiB/s · 2596.2 µs CPU/req)</sub> | **423**<br><sub>(438 MiB / 34.5% CPU · 106 MiB/s · 2443.3 µs CPU/req)</sub> |
| 64 KiB | HTTP/2 · plain | HTTP/1 · plain | **4,343**<br><sub>(479 MiB / 17.3% CPU · 271 MiB/s · 119.5 µs CPU/req)</sub> | **6,288**<br><sub>(74 MiB / 14.2% CPU · 393 MiB/s · 67.6 µs CPU/req)</sub> | 🥇 **10,177**<br><sub>(107 MiB / 25.8% CPU · 636 MiB/s · 76.2 µs CPU/req)</sub> | **6,581**<br><sub>(126 MiB / 20.1% CPU · 411 MiB/s · 91.8 µs CPU/req)</sub> | **3,844**<br><sub>(205 MiB / 12.5% CPU · 240 MiB/s · 97.5 µs CPU/req)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/2 · plain | **2,648**<br><sub>(378 MiB / 11.9% CPU · 166 MiB/s · 134.3 µs CPU/req)</sub> | *Not possible* | 🥇 **3,792**<br><sub>(128 MiB / 23.5% CPU · 237 MiB/s · 186.0 µs CPU/req)</sub> | **2,953**<br><sub>(125 MiB / 22.0% CPU · 185 MiB/s · 223.4 µs CPU/req)</sub> | **3,377**<br><sub>(270 MiB / 18.2% CPU · 211 MiB/s · 161.9 µs CPU/req)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/2 · TLS | **2,153**<br><sub>(398 MiB / 13.1% CPU · 135 MiB/s · 182.5 µs CPU/req)</sub> | *Not possible* | 🥇 **3,636**<br><sub>(123 MiB / 30.8% CPU · 227 MiB/s · 254.1 µs CPU/req)</sub> | **2,972**<br><sub>(126 MiB / 20.1% CPU · 186 MiB/s · 202.5 µs CPU/req)</sub> | **2,020**<br><sub>(259 MiB / 19.3% CPU · 126 MiB/s · 286.6 µs CPU/req)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/2 · TLS | **1,807**<br><sub>(221 MiB / 31.8% CPU · 113 MiB/s · 527.1 µs CPU/req)</sub> | *Not possible* | **1,032**<br><sub>(128 MiB / 29.7% CPU · 64 MiB/s · 863.9 µs CPU/req)</sub> | 🥇 **1,827**<br><sub>(130 MiB / 46.7% CPU · 114 MiB/s · 766.7 µs CPU/req)</sub> | **1,040**<br><sub>(455 MiB / 25.7% CPU · 65 MiB/s · 741.0 µs CPU/req)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/1 · TLS | **1,556**<br><sub>(246 MiB / 26.2% CPU · 97 MiB/s · 505.5 µs CPU/req)</sub> | 🥇 **2,090**<br><sub>(99 MiB / 23.6% CPU · 131 MiB/s · 338.2 µs CPU/req)</sub> | **1,634**<br><sub>(131 MiB / 30.1% CPU · 102 MiB/s · 553.5 µs CPU/req)</sub> | **1,419**<br><sub>(132 MiB / 47.4% CPU · 89 MiB/s · 1001.6 µs CPU/req)</sub> | **746**<br><sub>(421 MiB / 22.8% CPU · 47 MiB/s · 917.9 µs CPU/req)</sub> |
| 256 KiB | HTTP/2 · plain | HTTP/1 · plain | **1,206**<br><sub>(456 MiB / 16.4% CPU · 302 MiB/s · 407.1 µs CPU/req)</sub> | 🥇 **2,395**<br><sub>(72 MiB / 18.8% CPU · 599 MiB/s · 235.3 µs CPU/req)</sub> | **2,307**<br><sub>(102 MiB / 23.3% CPU · 577 MiB/s · 303.6 µs CPU/req)</sub> | **2,361**<br><sub>(145 MiB / 25.4% CPU · 590 MiB/s · 323.3 µs CPU/req)</sub> | **1,839**<br><sub>(211 MiB / 23.2% CPU · 460 MiB/s · 379.2 µs CPU/req)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/2 · plain | **1,280**<br><sub>(703 MiB / 26.5% CPU · 320 MiB/s · 621.1 µs CPU/req)</sub> | *Not possible* | **1,780**<br><sub>(138 MiB / 28.0% CPU · 445 MiB/s · 472.6 µs CPU/req)</sub> | 🥇 **1,938**<br><sub>(143 MiB / 27.5% CPU · 484 MiB/s · 425.3 µs CPU/req)</sub> | **1,026**<br><sub>(279 MiB / 28.9% CPU · 257 MiB/s · 843.5 µs CPU/req)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **1,552**<br><sub>(607 MiB / 32.4% CPU · 388 MiB/s · 626.1 µs CPU/req)</sub> | *Not possible* | **1,142**<br><sub>(131 MiB / 33.0% CPU · 286 MiB/s · 868.2 µs CPU/req)</sub> | **1,250**<br><sub>(128 MiB / 32.6% CPU · 313 MiB/s · 782.0 µs CPU/req)</sub> | **1,254**<br><sub>(257 MiB / 41.0% CPU · 313 MiB/s · 980.6 µs CPU/req)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/2 · TLS | **200**<br><sub>(peak 221 · 329 MiB / 33.6% CPU · 50 MiB/s · 5024.0 µs CPU/req)</sub> | *Not possible* | **249**<br><sub>(peak 403 · 112 MiB / 25.2% CPU · 62 MiB/s · 3037.3 µs CPU/req)</sub> | 🥇 **346**<br><sub>(138 MiB / 47.8% CPU · 87 MiB/s · 4138.6 µs CPU/req)</sub> | **185**<br><sub>(peak 216 · 511 MiB / 23.4% CPU · 46 MiB/s · 3797.5 µs CPU/req)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/1 · TLS | **317**<br><sub>(195 MiB / 34.4% CPU · 79 MiB/s · 3261.4 µs CPU/req)</sub> | 🥇 **473**<br><sub>(92 MiB / 24.9% CPU · 118 MiB/s · 1576.4 µs CPU/req)</sub> | **258**<br><sub>(peak 479 · 107 MiB / 22.1% CPU · 64 MiB/s · 2575.9 µs CPU/req)</sub> | **423**<br><sub>(151 MiB / 49.7% CPU · 106 MiB/s · 3524.6 µs CPU/req)</sub> | **114**<br><sub>(peak 124 · 310 MiB / 19.4% CPU · 29 MiB/s · 5100.8 µs CPU/req)</sub> |

### Windows — POST 64 KiB request + 64 KiB response

Median of **3** repeats on `windows-latest` @ `347a9a1b`. Source: Actions [37894276720](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37894276720) (`compare-post`).

*Not possible:* **HAProxy** and **Envoy** columns are omitted (no official Windows port).



















| Client | Origin | TWP | nginx | YARP |
|---|---|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | 🥇 **13,348**<br><sub>(95 MiB / 41.4% CPU · 1,669 MiB/s · 124.1 µs CPU/req)</sub> | **718**<br><sub>(143 MiB / 24.9% CPU · 90 MiB/s · 1384.8 µs CPU/req)</sub> | **8,762**<br><sub>(138 MiB / 50.2% CPU · 1,095 MiB/s · 229.3 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **4,382**<br><sub>(215 MiB / 45.3% CPU · 548 MiB/s · 413.2 µs CPU/req)</sub> | **368**<br><sub>(146 MiB / 24.6% CPU · 46 MiB/s · 2679.0 µs CPU/req)</sub> | **4,203**<br><sub>(138 MiB / 48.6% CPU · 525 MiB/s · 462.4 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **2,231**<br><sub>(165 MiB / 46.7% CPU · 279 MiB/s · 837.8 µs CPU/req)</sub> | *Not possible (no QUIC)* | **1,431**<br><sub>(209 MiB / 51.8% CPU · 179 MiB/s · 1449.3 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | 🥇 **7,351**<br><sub>(217 MiB / 43.4% CPU · 919 MiB/s · 236.1 µs CPU/req)</sub> | **3,226**<br><sub>(131 MiB / 24.8% CPU · 403 MiB/s · 307.3 µs CPU/req)</sub> | **6,963**<br><sub>(119 MiB / 46.9% CPU · 870 MiB/s · 269.4 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | 🥇 **5,008**<br><sub>(192 MiB / 37.9% CPU · 626 MiB/s · 302.3 µs CPU/req)</sub> | *Not possible* | **3,943**<br><sub>(151 MiB / 43.9% CPU · 493 MiB/s · 445.5 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **4,498**<br><sub>(180 MiB / 37.6% CPU · 562 MiB/s · 334.0 µs CPU/req)</sub> | *Not possible* | **3,708**<br><sub>(150 MiB / 48.4% CPU · 463 MiB/s · 521.9 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | 🥇 **4,226**<br><sub>(181 MiB / 43.6% CPU · 528 MiB/s · 412.3 µs CPU/req)</sub> | *Not possible* | **2,737**<br><sub>(213 MiB / 48.8% CPU · 342 MiB/s · 712.8 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **2,005**<br><sub>(174 MiB / 45.4% CPU · 251 MiB/s · 905.0 µs CPU/req)</sub> | *Not possible (no QUIC)* | **1,299**<br><sub>(203 MiB / 52.5% CPU · 162 MiB/s · 1615.5 µs CPU/req)</sub> |

TWP leads H2→H1 POST (about **1.1–1.3×** YARP) and H3 POST (about **1.0–1.1×** YARP). H2 TLS→H2 TLS POST sustain is about **1.2–1.4×** YARP.

### Linux — POST 64 KiB request + 64 KiB response

Median of **3** repeats @ `347a9a1b`. Source: Actions [37894276720](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37894276720) (`compare-post`).


| Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---:|---:|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | **4,743**<br><sub>(132 MiB / 44.7% CPU · 593 MiB/s · 376.6 µs CPU/req)</sub> | **3,728**<br><sub>(98 MiB / 49.6% CPU · 466 MiB/s · 531.9 µs CPU/req)</sub> | 🥇 **5,163**<br><sub>(90 MiB / 42.1% CPU · 645 MiB/s · 326.1 µs CPU/req)</sub> | **5,097**<br><sub>(132 MiB / 41.5% CPU · 637 MiB/s · 325.3 µs CPU/req)</sub> | **3,178**<br><sub>(176 MiB / 54.9% CPU · 397 MiB/s · 690.4 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | **3,656**<br><sub>(254 MiB / 47.8% CPU · 457 MiB/s · 523.1 µs CPU/req)</sub> | **3,193**<br><sub>(106 MiB / 46.1% CPU · 399 MiB/s · 577.9 µs CPU/req)</sub> | 🥇 **3,853**<br><sub>(90 MiB / 44.5% CPU · 482 MiB/s · 461.4 µs CPU/req)</sub> | **3,590**<br><sub>(peak 4,368 · 125 MiB / 41.8% CPU · 449 MiB/s · 466.2 µs CPU/req)</sub> | **3,341**<br><sub>(171 MiB / 49.9% CPU · 418 MiB/s · 597.9 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **2,895**<br><sub>(218 MiB / 48.4% CPU · 362 MiB/s · 668.3 µs CPU/req)</sub> | **989**<br><sub>(111 MiB / 48.5% CPU · 124 MiB/s · 1961.4 µs CPU/req)</sub> | **2,504**<br><sub>(92 MiB / 43.6% CPU · 313 MiB/s · 696.7 µs CPU/req)</sub> | **0**<br><sub>(130 MiB / 0.2% CPU)</sub> | **2,251**<br><sub>(250 MiB / 50.9% CPU · 281 MiB/s · 905.0 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | **9,955**<br><sub>(266 MiB / 41.2% CPU · 1,244 MiB/s · 165.4 µs CPU/req)</sub> | **8,057**<br><sub>(86 MiB / 44.6% CPU · 1,007 MiB/s · 221.3 µs CPU/req)</sub> | 🥇 **11,193**<br><sub>(76 MiB / 37.9% CPU · 1,399 MiB/s · 135.6 µs CPU/req)</sub> | **9,130**<br><sub>(peak 10,992 · 117 MiB / 39.4% CPU · 1,141 MiB/s · 172.7 µs CPU/req)</sub> | **9,211**<br><sub>(152 MiB / 45.2% CPU · 1,151 MiB/s · 196.4 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | **7,801**<br><sub>(220 MiB / 35.2% CPU · 975 MiB/s · 180.3 µs CPU/req)</sub> | *Not possible* | **6,656**<br><sub>(92 MiB / 40.6% CPU · 832 MiB/s · 244.2 µs CPU/req)</sub> | 🥇 **8,181**<br><sub>(136 MiB / 39.3% CPU · 1,023 MiB/s · 192.2 µs CPU/req)</sub> | **5,406**<br><sub>(198 MiB / 47.7% CPU · 676 MiB/s · 353.1 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | **2,827**<br><sub>(204 MiB / 41.7% CPU · 353 MiB/s · 589.8 µs CPU/req)</sub> | *Not possible* | **2,379**<br><sub>(90 MiB / 42.5% CPU · 297 MiB/s · 714.9 µs CPU/req)</sub> | 🥇 **2,843**<br><sub>(142 MiB / 40.1% CPU · 355 MiB/s · 564.9 µs CPU/req)</sub> | **1,920**<br><sub>(183 MiB / 46.7% CPU · 240 MiB/s · 972.0 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | 🥇 **1,980**<br><sub>(231 MiB / 44.2% CPU · 248 MiB/s · 893.3 µs CPU/req)</sub> | *Not possible* | **1,712**<br><sub>(97 MiB / 43.5% CPU · 214 MiB/s · 1016.5 µs CPU/req)</sub> | **0**<br><sub>(131 MiB / 0.3% CPU)</sub> | **1,504**<br><sub>(259 MiB / 48.4% CPU · 188 MiB/s · 1286.0 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **2,199**<br><sub>(238 MiB / 47.5% CPU · 275 MiB/s · 863.0 µs CPU/req)</sub> | **864**<br><sub>(114 MiB / 52.1% CPU · 108 MiB/s · 2409.3 µs CPU/req)</sub> | **1,982**<br><sub>(96 MiB / 44.1% CPU · 248 MiB/s · 890.8 µs CPU/req)</sub> | **0**<br><sub>(131 MiB / 0.3% CPU)</sub> | **1,749**<br><sub>(272 MiB / 49.3% CPU · 219 MiB/s · 1128.4 µs CPU/req)</sub> |

Linux nginx H1/H2/H3 POST completed (nginx.org mainline). TWP÷YARP H1 ≈ **1.5×**; H2→H1 ≈ **1.2×**; H3 ≈ **1.1×**. TWP÷nginx H3 ≈ **6×**.

### macOS — POST 64 KiB request + 64 KiB response

Median of **3** repeats on `macos-15` (Apple Silicon M1, 3-core / 7 GB) @ `347a9a1b`. Source: Actions [37894276720](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37894276720) (`compare-post`).

| Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---:|---:|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | **2,187**<br><sub>(241 MiB / 21.1% CPU · 273 MiB/s · 289.3 µs CPU/req)</sub> | **3,384**<br><sub>(89 MiB / 18.2% CPU · 423 MiB/s · 161.3 µs CPU/req)</sub> | **3,230**<br><sub>(121 MiB / 24.4% CPU · 404 MiB/s · 226.4 µs CPU/req)</sub> | 🥇 **3,575**<br><sub>(134 MiB / 29.4% CPU · 447 MiB/s · 246.7 µs CPU/req)</sub> | **1,748**<br><sub>(201 MiB / 28.3% CPU · 218 MiB/s · 486.4 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | **2,100**<br><sub>(553 MiB / 25.1% CPU · 262 MiB/s · 359.1 µs CPU/req)</sub> | 🥇 **3,062**<br><sub>(99 MiB / 19.8% CPU · 383 MiB/s · 194.4 µs CPU/req)</sub> | **2,813**<br><sub>(122 MiB / 27.1% CPU · 352 MiB/s · 289.3 µs CPU/req)</sub> | **2,003**<br><sub>(peak 3,282 · 122 MiB / 15.1% CPU · 250 MiB/s · 226.0 µs CPU/req)</sub> | **1,903**<br><sub>(187 MiB / 28.0% CPU · 238 MiB/s · 441.9 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | **974**<br><sub>(301 MiB / 40.8% CPU · 122 MiB/s · 1256.3 µs CPU/req)</sub> | 🥇 **1,898**<br><sub>(95 MiB / 26.8% CPU · 237 MiB/s · 422.8 µs CPU/req)</sub> | **801**<br><sub>(111 MiB / 33.6% CPU · 100 MiB/s · 1260.0 µs CPU/req)</sub> | **819**<br><sub>(134 MiB / 49.6% CPU · 102 MiB/s · 1819.3 µs CPU/req)</sub> | **955**<br><sub>(441 MiB / 41.0% CPU · 119 MiB/s · 1288.6 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | **4,610**<br><sub>(493 MiB / 34.0% CPU · 576 MiB/s · 221.6 µs CPU/req)</sub> | **5,677**<br><sub>(86 MiB / 21.2% CPU · 710 MiB/s · 112.2 µs CPU/req)</sub> | **5,265**<br><sub>(114 MiB / 30.9% CPU · 658 MiB/s · 175.9 µs CPU/req)</sub> | 🥇 **6,859**<br><sub>(135 MiB / 27.1% CPU · 857 MiB/s · 118.5 µs CPU/req)</sub> | **4,715**<br><sub>(195 MiB / 43.1% CPU · 589 MiB/s · 274.2 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | **1,685**<br><sub>(418 MiB / 19.1% CPU · 211 MiB/s · 340.6 µs CPU/req)</sub> | *Not possible* | **1,979**<br><sub>(127 MiB / 27.9% CPU · 247 MiB/s · 423.2 µs CPU/req)</sub> | 🥇 **2,136**<br><sub>(132 MiB / 25.5% CPU · 267 MiB/s · 358.3 µs CPU/req)</sub> | **996**<br><sub>(218 MiB / 18.4% CPU · 125 MiB/s · 555.3 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | **1,561**<br><sub>(408 MiB / 21.5% CPU · 195 MiB/s · 413.1 µs CPU/req)</sub> | *Not possible* | 🥇 **2,662**<br><sub>(126 MiB / 28.9% CPU · 333 MiB/s · 325.9 µs CPU/req)</sub> | **2,489**<br><sub>(130 MiB / 27.1% CPU · 311 MiB/s · 327.2 µs CPU/req)</sub> | **1,088**<br><sub>(206 MiB / 18.6% CPU · 136 MiB/s · 513.2 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | **651**<br><sub>(326 MiB / 26.8% CPU · 81 MiB/s · 1235.1 µs CPU/req)</sub> | *Not possible* | **705**<br><sub>(121 MiB / 33.0% CPU · 88 MiB/s · 1407.0 µs CPU/req)</sub> | 🥇 **754**<br><sub>(138 MiB / 47.9% CPU · 94 MiB/s · 1906.4 µs CPU/req)</sub> | **659**<br><sub>(447 MiB / 27.6% CPU · 82 MiB/s · 1256.2 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | **900**<br><sub>(290 MiB / 30.5% CPU · 112 MiB/s · 1017.3 µs CPU/req)</sub> | **744**<br><sub>(99 MiB / 17.2% CPU · 93 MiB/s · 694.6 µs CPU/req)</sub> | 🥇 **934**<br><sub>(126 MiB / 32.5% CPU · 117 MiB/s · 1043.5 µs CPU/req)</sub> | **382**<br><sub>(142 MiB / 22.5% CPU · 48 MiB/s · 1763.4 µs CPU/req)</sub> | **462**<br><sub>(437 MiB / 25.1% CPU · 58 MiB/s · 1632.2 µs CPU/req)</sub> |

### Windows — lossy / high-RTT (H2 HOL / H3 loss)

Userspace **5 ms** one-way delay + **1%** TCP connection stall (H1/H2); UDP is **loss% only** (no per-datagram delay; MsQuic-safe) + **1%** datagram drop (H3); **64 KiB** GET. Median of **3** repeats on `windows-latest` @ `347a9a1b` — [37894280112](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37894280112) (`compare-lossy`).

*Not possible:* **HAProxy** and **Envoy** columns are omitted (no official Windows port).


| Client | Origin | TWP | nginx | YARP |
|---|---|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | 🥇 **665**<br><sub>(87 MiB / 3.7% CPU · 42 MiB/s · 223.8 µs CPU/req)</sub> | **648**<br><sub>(142 MiB / 17.7% CPU · 40 MiB/s · 1091.6 µs CPU/req)</sub> | **662**<br><sub>(120 MiB / 5.2% CPU · 41 MiB/s · 313.5 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **248**<br><sub>(156 MiB / 3.5% CPU · 15 MiB/s · 570.0 µs CPU/req)</sub> | **244**<br><sub>(143 MiB / 8.6% CPU · 15 MiB/s · 1403.9 µs CPU/req)</sub> | **228**<br><sub>(118 MiB / 2.6% CPU · 14 MiB/s · 463.9 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | **1,114**<br><sub>(124 MiB / 17.3% CPU · 70 MiB/s · 621.8 µs CPU/req)</sub> | *Not possible (no QUIC)* | 🥇 **1,332**<br><sub>(192 MiB / 31.0% CPU · 83 MiB/s · 929.7 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | 🥇 **256**<br><sub>(152 MiB / 1.3% CPU · 16 MiB/s · 197.1 µs CPU/req)</sub> | **252**<br><sub>(128 MiB / 1.2% CPU · 16 MiB/s · 195.5 µs CPU/req)</sub> | **235**<br><sub>(103 MiB / 1.0% CPU · 15 MiB/s · 172.0 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | **218**<br><sub>(122 MiB / 2.7% CPU · 14 MiB/s · 502.8 µs CPU/req)</sub> | *Not possible* | 🥇 **230**<br><sub>(114 MiB / 3.7% CPU · 14 MiB/s · 641.7 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | **226**<br><sub>(127 MiB / 1.1% CPU · 14 MiB/s · 201.9 µs CPU/req)</sub> | *Not possible* | 🥇 **234**<br><sub>(117 MiB / 2.6% CPU · 15 MiB/s · 440.3 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | 🥇 **885**<br><sub>(121 MiB / 15.2% CPU · 55 MiB/s · 689.5 µs CPU/req)</sub> | *Not possible* | **868**<br><sub>(191 MiB / 22.9% CPU · 54 MiB/s · 1053.6 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **848**<br><sub>(133 MiB / 19.6% CPU · 53 MiB/s · 926.6 µs CPU/req)</sub> | *Not possible (no QUIC)* | **815**<br><sub>(178 MiB / 29.6% CPU · 51 MiB/s · 1451.7 µs CPU/req)</sub> |

H1, H2, and H3 loss sustain are in the table. Prefer the ratio columns.

### Linux — lossy / high-RTT (H2 HOL / H3 loss)

Median of **3** repeats @ `347a9a1b`. Source: [37894280112](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37894280112) (`compare-lossy`; lossy H3 uses `quic-http3`, UDP drop-only).


| Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---:|---:|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | **1,200**<br><sub>(125 MiB / 12.8% CPU · 75 MiB/s · 427.3 µs CPU/req)</sub> | 🥇 **1,210**<br><sub>(99 MiB / 12.4% CPU · 76 MiB/s · 410.4 µs CPU/req)</sub> | **1,207**<br><sub>(85 MiB / 6.6% CPU · 75 MiB/s · 220.0 µs CPU/req)</sub> | **1,200**<br><sub>(129 MiB / 8.9% CPU · 75 MiB/s · 297.8 µs CPU/req)</sub> | **1,197**<br><sub>(154 MiB / 16.9% CPU · 75 MiB/s · 564.1 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **593**<br><sub>(197 MiB / 9.4% CPU · 37 MiB/s · 636.1 µs CPU/req)</sub> | **533**<br><sub>(101 MiB / 3.5% CPU · 33 MiB/s · 262.7 µs CPU/req)</sub> | **519**<br><sub>(85 MiB / 2.7% CPU · 32 MiB/s · 208.8 µs CPU/req)</sub> | **506**<br><sub>(130 MiB / 3.5% CPU · 32 MiB/s · 274.4 µs CPU/req)</sub> | **440**<br><sub>(141 MiB / 8.8% CPU · 28 MiB/s · 797.6 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **1,146**<br><sub>(166 MiB / 24.4% CPU · 72 MiB/s · 851.2 µs CPU/req)</sub> | **1,086**<br><sub>(113 MiB / 23.5% CPU · 68 MiB/s · 865.0 µs CPU/req)</sub> | **971**<br><sub>(102 MiB / 18.8% CPU · 61 MiB/s · 774.8 µs CPU/req)</sub> | **1,025**<br><sub>(138 MiB / 25.5% CPU · 64 MiB/s · 994.8 µs CPU/req)</sub> | **1,119**<br><sub>(238 MiB / 31.4% CPU · 70 MiB/s · 1122.1 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | 🥇 **614**<br><sub>(189 MiB / 10.4% CPU · 38 MiB/s · 677.0 µs CPU/req)</sub> | **532**<br><sub>(78 MiB / 3.5% CPU · 33 MiB/s · 262.4 µs CPU/req)</sub> | **533**<br><sub>(70 MiB / 2.8% CPU · 33 MiB/s · 207.3 µs CPU/req)</sub> | **524**<br><sub>(123 MiB / 3.6% CPU · 33 MiB/s · 277.4 µs CPU/req)</sub> | **492**<br><sub>(133 MiB / 9.4% CPU · 31 MiB/s · 763.3 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | **337**<br><sub>(156 MiB / 4.8% CPU · 21 MiB/s · 563.3 µs CPU/req)</sub> | *Not possible* | 🥇 **533**<br><sub>(87 MiB / 2.3% CPU · 33 MiB/s · 175.5 µs CPU/req)</sub> | **512**<br><sub>(129 MiB / 3.4% CPU · 32 MiB/s · 263.5 µs CPU/req)</sub> | **358**<br><sub>(150 MiB / 7.0% CPU · 22 MiB/s · 782.6 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | **250**<br><sub>(160 MiB / 6.8% CPU · 16 MiB/s · 1081.0 µs CPU/req)</sub> | *Not possible* | 🥇 **543**<br><sub>(86 MiB / 5.5% CPU · 34 MiB/s · 407.9 µs CPU/req)</sub> | **485**<br><sub>(133 MiB / 5.9% CPU · 30 MiB/s · 484.0 µs CPU/req)</sub> | **405**<br><sub>(146 MiB / 12.7% CPU · 25 MiB/s · 1250.7 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | 🥇 **1,110**<br><sub>(160 MiB / 26.0% CPU · 69 MiB/s · 937.7 µs CPU/req)</sub> | *Not possible* | **917**<br><sub>(104 MiB / 20.5% CPU · 57 MiB/s · 892.3 µs CPU/req)</sub> | **959**<br><sub>(140 MiB / 27.1% CPU · 60 MiB/s · 1128.6 µs CPU/req)</sub> | **1,041**<br><sub>(228 MiB / 33.0% CPU · 65 MiB/s · 1269.3 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | **1,438**<br><sub>(179 MiB / 24.2% CPU · 90 MiB/s · 672.7 µs CPU/req)</sub> | 🥇 **1,461**<br><sub>(121 MiB / 22.6% CPU · 91 MiB/s · 620.1 µs CPU/req)</sub> | **1,374**<br><sub>(103 MiB / 19.8% CPU · 86 MiB/s · 576.8 µs CPU/req)</sub> | **1,407**<br><sub>(138 MiB / 27.7% CPU · 88 MiB/s · 787.3 µs CPU/req)</sub> | **1,411**<br><sub>(212 MiB / 32.8% CPU · 88 MiB/s · 930.1 µs CPU/req)</sub> |

TWP H2 HOL ≫ YARP (~**7.8×**). H3 TWP÷YARP ≈ **1.07×**.

### macOS — lossy / high-RTT (H2 HOL / H3 loss)

Median of **3** repeats on `macos-15` (Apple Silicon M1, 3-core / 7 GB) @ `347a9a1b`. Source: [37894280112](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37894280112) (`compare-lossy`; lossy H3 uses `quic-http3`, UDP drop-only).

| Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---:|---:|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | **453**<br><sub>(253 MiB / 6.1% CPU · 28 MiB/s · 403.7 µs CPU/req)</sub> | **462**<br><sub>(85 MiB / 1.7% CPU · 29 MiB/s · 113.0 µs CPU/req)</sub> | 🥇 **465**<br><sub>(116 MiB / 2.7% CPU · 29 MiB/s · 175.0 µs CPU/req)</sub> | **396**<br><sub>(131 MiB / 2.5% CPU · 25 MiB/s · 185.8 µs CPU/req)</sub> | **360**<br><sub>(226 MiB / 4.4% CPU · 23 MiB/s · 363.9 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **166**<br><sub>(361 MiB / 6.5% CPU · 10 MiB/s · 1167.6 µs CPU/req)</sub> | **144**<br><sub>(86 MiB / 1.1% CPU · 9.0 MiB/s · 227.6 µs CPU/req)</sub> | **136**<br><sub>(114 MiB / 1.4% CPU · 8.5 MiB/s · 309.3 µs CPU/req)</sub> | **135**<br><sub>(127 MiB / 1.4% CPU · 8.5 MiB/s · 305.8 µs CPU/req)</sub> | **135**<br><sub>(224 MiB / 3.7% CPU · 8.5 MiB/s · 817.6 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | **879**<br><sub>(272 MiB / 22.9% CPU · 55 MiB/s · 781.6 µs CPU/req)</sub> | 🥇 **1,097**<br><sub>(98 MiB / 14.8% CPU · 69 MiB/s · 405.9 µs CPU/req)</sub> | **497**<br><sub>(122 MiB / 16.2% CPU · 31 MiB/s · 978.9 µs CPU/req)</sub> | **938**<br><sub>(131 MiB / 31.3% CPU · 59 MiB/s · 1001.0 µs CPU/req)</sub> | **636**<br><sub>(412 MiB / 22.8% CPU · 40 MiB/s · 1075.0 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | **176**<br><sub>(342 MiB / 6.8% CPU · 11 MiB/s · 1151.2 µs CPU/req)</sub> | **168**<br><sub>(72 MiB / 1.1% CPU · 10 MiB/s · 195.1 µs CPU/req)</sub> | 🥇 **186**<br><sub>(99 MiB / 2.0% CPU · 12 MiB/s · 322.2 µs CPU/req)</sub> | **160**<br><sub>(127 MiB / 2.0% CPU · 10.0 MiB/s · 383.5 µs CPU/req)</sub> | **176**<br><sub>(154 MiB / 4.5% CPU · 11 MiB/s · 761.1 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | **128**<br><sub>(326 MiB / 3.4% CPU · 8.0 MiB/s · 804.5 µs CPU/req)</sub> | *Not possible* | 🥇 **143**<br><sub>(121 MiB / 1.2% CPU · 9.0 MiB/s · 249.0 µs CPU/req)</sub> | **121**<br><sub>(123 MiB / 1.5% CPU · 7.5 MiB/s · 375.6 µs CPU/req)</sub> | **140**<br><sub>(275 MiB / 4.4% CPU · 8.7 MiB/s · 947.8 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | **158**<br><sub>(300 MiB / 3.5% CPU · 9.9 MiB/s · 674.5 µs CPU/req)</sub> | *Not possible* | **152**<br><sub>(118 MiB / 1.9% CPU · 9.5 MiB/s · 381.7 µs CPU/req)</sub> | **136**<br><sub>(124 MiB / 2.1% CPU · 8.5 MiB/s · 459.8 µs CPU/req)</sub> | 🥇 **160**<br><sub>(257 MiB / 5.9% CPU · 10 MiB/s · 1099.3 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | **817**<br><sub>(183 MiB / 19.6% CPU · 51 MiB/s · 719.6 µs CPU/req)</sub> | *Not possible* | **562**<br><sub>(126 MiB / 18.4% CPU · 35 MiB/s · 983.8 µs CPU/req)</sub> | 🥇 **877**<br><sub>(130 MiB / 29.9% CPU · 55 MiB/s · 1023.4 µs CPU/req)</sub> | **623**<br><sub>(449 MiB / 20.9% CPU · 39 MiB/s · 1003.9 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | **561**<br><sub>(209 MiB / 16.9% CPU · 35 MiB/s · 903.9 µs CPU/req)</sub> | 🥇 **1,050**<br><sub>(99 MiB / 14.2% CPU · 66 MiB/s · 405.7 µs CPU/req)</sub> | **467**<br><sub>(126 MiB / 14.6% CPU · 29 MiB/s · 940.9 µs CPU/req)</sub> | **878**<br><sub>(132 MiB / 31.6% CPU · 55 MiB/s · 1081.2 µs CPU/req)</sub> | **688**<br><sub>(398 MiB / 22.7% CPU · 43 MiB/s · 990.6 µs CPU/req)</sub> |

### Architecture-sensitive

These runs isolate slow app readers, origin-early response, HTTP/2 duplex, and WebSocket echo. See [TWP vs YARP IO model](Performance-Profiling#user-content-twp-vs-yarp-io-model). Laptop 1-rep numbers are on [Performance Local Lab](Performance-Local-Lab#user-content-architecture-sensitive).

Median of **3** repeats on matched 4 vCPU / 16 GiB runners @ `347a9a1b` ([37894283777](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37894283777)). Slow consumer = 256 KiB GET, 16 KiB read + 8 ms sleep. Early response = 64 KiB POST, origin writes after 8 KiB. Duplex HTTP/2 = overlapping 64 KiB POST on H2 TLS↔H2 TLS. WebSocket row = H1 Upgrade echo round-trips/sec (not RFC 8441; see WebSocket RFC 8441 table below).

Lossy-link runs (slow **network**) are already published above; they are not a slow **app** reader.

#### Windows


*Not possible:* **HAProxy** and **Envoy** columns are omitted (no official Windows port).

| Scenario | Client | Origin | TWP | nginx | YARP |
|---|---|---|---:|---:|---:|
| Slow consumer (256 KiB GET, throttled client read) | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **249**<br><sub>(94 MiB / 5.4% CPU · 62 MiB/s · 861.7 µs CPU/req)</sub> | **221**<br><sub>(144 MiB / 24.7% CPU · 55 MiB/s · 4462.9 µs CPU/req)</sub> | **241**<br><sub>(111 MiB / 5.2% CPU · 60 MiB/s · 867.5 µs CPU/req)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · TLS | HTTP/1 · plain | **248**<br><sub>(143 MiB / 5.4% CPU · 62 MiB/s · 878.5 µs CPU/req)</sub> | **163**<br><sub>(142 MiB / 24.6% CPU · 41 MiB/s · 6042.9 µs CPU/req)</sub> | 🥇 **248**<br><sub>(122 MiB / 6.2% CPU · 62 MiB/s · 996.0 µs CPU/req)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/3 · QUIC | HTTP/1 · plain | **90**<br><sub>(107 MiB / 48.4% CPU · 22 MiB/s · 21559.0 µs CPU/req)</sub> | *Not possible (no QUIC)* | 🥇 **195**<br><sub>(152 MiB / 47.1% CPU · 49 MiB/s · 9652.0 µs CPU/req)</sub> |
| Early response (origin writes after first request chunk) | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **7,995**<br><sub>(98 MiB / 42.6% CPU · 999 MiB/s · 212.9 µs CPU/req)</sub> | **507**<br><sub>(144 MiB / 24.8% CPU · 63 MiB/s · 1956.6 µs CPU/req)</sub> | **5,524**<br><sub>(139 MiB / 56.2% CPU · 691 MiB/s · 407.1 µs CPU/req)</sub> |
| Early response (origin writes after first request chunk) | HTTP/2 · TLS | HTTP/1 · plain | **4,506**<br><sub>(227 MiB / 54.1% CPU · 563 MiB/s · 479.9 µs CPU/req)</sub> | **483**<br><sub>(147 MiB / 24.8% CPU · 60 MiB/s · 2055.1 µs CPU/req)</sub> | 🥇 **5,500**<br><sub>(138 MiB / 49.3% CPU · 688 MiB/s · 358.7 µs CPU/req)</sub> |
| Early response (origin writes after first request chunk) | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **1,676**<br><sub>(156 MiB / 45.2% CPU · 209 MiB/s · 1079.1 µs CPU/req)</sub> | *Not possible (no QUIC)* | **1,058**<br><sub>(199 MiB / 51.9% CPU · 132 MiB/s · 1962.4 µs CPU/req)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · plain | HTTP/1 · plain | **248**<br><sub>(137 MiB / 4.0% CPU · 62 MiB/s · 653.5 µs CPU/req)</sub> | **248**<br><sub>(128 MiB / 5.6% CPU · 62 MiB/s · 905.2 µs CPU/req)</sub> | 🥇 **248**<br><sub>(109 MiB / 4.6% CPU · 62 MiB/s · 739.0 µs CPU/req)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · TLS | HTTP/2 · TLS | **248**<br><sub>(159 MiB / 8.2% CPU · 62 MiB/s · 1326.3 µs CPU/req)</sub> | *Not possible* | 🥇 **248**<br><sub>(148 MiB / 10.2% CPU · 62 MiB/s · 1641.6 µs CPU/req)</sub> |
| Early response (origin writes after first request chunk) | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **3,276**<br><sub>(193 MiB / 39.0% CPU · 409 MiB/s · 475.9 µs CPU/req)</sub> | *Not possible* | **2,702**<br><sub>(161 MiB / 55.2% CPU · 338 MiB/s · 817.5 µs CPU/req)</sub> |
| Duplex (both directions live) | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **4,450**<br><sub>(198 MiB / 40.4% CPU · 556 MiB/s · 362.8 µs CPU/req)</sub> | *Not possible* | **3,488**<br><sub>(166 MiB / 55.9% CPU · 436 MiB/s · 640.7 µs CPU/req)</sub> |
| Duplex (WebSocket / H1 Upgrade) | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **58,122**<br><sub>(99 MiB / 43.0% CPU · 29.6 µs CPU/req)</sub> | **33,270**<br><sub>(143 MiB / 24.8% CPU · 29.8 µs CPU/req)</sub> | **54,564**<br><sub>(89 MiB / 44.6% CPU · 32.7 µs CPU/req)</sub> |

#### Linux


| Scenario | Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---|---:|---:|---:|---:|---:|
| Slow consumer (256 KiB GET, throttled client read) | HTTP/1 · TLS | HTTP/1 · plain | **464**<br><sub>(117 MiB / 9.5% CPU · 116 MiB/s · 820.7 µs CPU/req)</sub> | **416**<br><sub>(98 MiB / 9.8% CPU · 104 MiB/s · 939.9 µs CPU/req)</sub> | 🥇 **468**<br><sub>(86 MiB / 5.3% CPU · 117 MiB/s · 457.0 µs CPU/req)</sub> | **466**<br><sub>(138 MiB / 6.2% CPU · 116 MiB/s · 528.0 µs CPU/req)</sub> | **409**<br><sub>(147 MiB / 13.9% CPU · 102 MiB/s · 1356.8 µs CPU/req)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · TLS | HTTP/1 · plain | **479**<br><sub>(165 MiB / 17.3% CPU · 120 MiB/s · 1446.1 µs CPU/req)</sub> | **476**<br><sub>(101 MiB / 12.2% CPU · 119 MiB/s · 1021.9 µs CPU/req)</sub> | **480**<br><sub>(85 MiB / 8.4% CPU · 120 MiB/s · 697.0 µs CPU/req)</sub> | 🥇 **480**<br><sub>(144 MiB / 8.4% CPU · 120 MiB/s · 701.8 µs CPU/req)</sub> | **476**<br><sub>(161 MiB / 18.7% CPU · 119 MiB/s · 1570.6 µs CPU/req)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/3 · QUIC | HTTP/1 · plain | **463**<br><sub>(149 MiB / 46.2% CPU · 116 MiB/s · 3995.7 µs CPU/req)</sub> | **465**<br><sub>(113 MiB / 23.6% CPU · 116 MiB/s · 2029.7 µs CPU/req)</sub> | 🥇 **472**<br><sub>(90 MiB / 12.3% CPU · 118 MiB/s · 1038.7 µs CPU/req)</sub> | **464**<br><sub>(151 MiB / 21.0% CPU · 116 MiB/s · 1812.1 µs CPU/req)</sub> | **457**<br><sub>(211 MiB / 50.0% CPU · 114 MiB/s · 4377.4 µs CPU/req)</sub> |
| Early response (origin writes after first request chunk) | HTTP/1 · TLS | HTTP/1 · plain | **4,705**<br><sub>(133 MiB / 47.2% CPU · 588 MiB/s · 401.2 µs CPU/req)</sub> | **3,592**<br><sub>(98 MiB / 50.3% CPU · 449 MiB/s · 560.1 µs CPU/req)</sub> | 🥇 **5,250**<br><sub>(89 MiB / 44.0% CPU · 656 MiB/s · 334.9 µs CPU/req)</sub> | **0**<br><sub>(peak 2,853 · 130 MiB / 26.4% CPU)</sub> | **3,121**<br><sub>(173 MiB / 56.6% CPU · 390 MiB/s · 725.6 µs CPU/req)</sub> |
| Early response (origin writes after first request chunk) | HTTP/2 · TLS | HTTP/1 · plain | **4,891**<br><sub>(277 MiB / 49.1% CPU · 611 MiB/s · 401.5 µs CPU/req)</sub> | **6,125**<br><sub>(108 MiB / 39.8% CPU · 766 MiB/s · 260.1 µs CPU/req)</sub> | 🥇 **6,805**<br><sub>(89 MiB / 40.4% CPU · 851 MiB/s · 237.3 µs CPU/req)</sub> | **0**<br><sub>(peak 6,586 · 140 MiB / 41.3% CPU)</sub> | **5,453**<br><sub>(175 MiB / 50.8% CPU · 682 MiB/s · 372.7 µs CPU/req)</sub> |
| Early response (origin writes after first request chunk) | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **2,724**<br><sub>(238 MiB / 46.7% CPU · 341 MiB/s · 685.6 µs CPU/req)</sub> | **1,178**<br><sub>(114 MiB / 41.9% CPU · 147 MiB/s · 1423.0 µs CPU/req)</sub> | **2,188**<br><sub>(94 MiB / 40.4% CPU · 274 MiB/s · 738.3 µs CPU/req)</sub> | **0**<br><sub>(peak 1 · 130 MiB / 0.3% CPU)</sub> | **1,784**<br><sub>(263 MiB / 49.3% CPU · 223 MiB/s · 1105.7 µs CPU/req)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · plain | HTTP/1 · plain | **477**<br><sub>(156 MiB / 17.0% CPU · 119 MiB/s · 1421.6 µs CPU/req)</sub> | **476**<br><sub>(78 MiB / 13.9% CPU · 119 MiB/s · 1169.5 µs CPU/req)</sub> | 🥇 **478**<br><sub>(70 MiB / 7.2% CPU · 119 MiB/s · 598.8 µs CPU/req)</sub> | **473**<br><sub>(134 MiB / 7.1% CPU · 118 MiB/s · 600.0 µs CPU/req)</sub> | **472**<br><sub>(146 MiB / 15.1% CPU · 118 MiB/s · 1274.1 µs CPU/req)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · TLS | HTTP/2 · TLS | **464**<br><sub>(201 MiB / 24.4% CPU · 116 MiB/s · 2098.2 µs CPU/req)</sub> | *Not possible* | **468**<br><sub>(94 MiB / 19.6% CPU · 117 MiB/s · 1677.6 µs CPU/req)</sub> | 🥇 **468**<br><sub>(140 MiB / 18.3% CPU · 117 MiB/s · 1561.6 µs CPU/req)</sub> | **463**<br><sub>(170 MiB / 30.6% CPU · 116 MiB/s · 2647.6 µs CPU/req)</sub> |
| Early response (origin writes after first request chunk) | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **2,885**<br><sub>(221 MiB / 42.3% CPU · 361 MiB/s · 586.1 µs CPU/req)</sub> | *Not possible* | **2,046**<br><sub>(92 MiB / 34.6% CPU · 256 MiB/s · 675.7 µs CPU/req)</sub> | **0**<br><sub>(peak 2,529 · 144 MiB / 36.2% CPU)</sub> | **1,978**<br><sub>(202 MiB / 54.2% CPU · 247 MiB/s · 1095.4 µs CPU/req)</sub> |
| Duplex (both directions live) | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **2,794**<br><sub>(231 MiB / 41.5% CPU · 349 MiB/s · 594.1 µs CPU/req)</sub> | *Not possible* | **2,124**<br><sub>(93 MiB / 36.4% CPU · 265 MiB/s · 686.4 µs CPU/req)</sub> | **0**<br><sub>(peak 2,284 · 142 MiB / 33.9% CPU)</sub> | **1,952**<br><sub>(202 MiB / 53.8% CPU · 244 MiB/s · 1103.1 µs CPU/req)</sub> |
| Duplex (WebSocket / H1 Upgrade) | HTTP/1 · TLS | HTTP/1 · plain | **31,044**<br><sub>(127 MiB / 44.1% CPU · 56.8 µs CPU/req)</sub> | 🥇 **33,626**<br><sub>(99 MiB / 36.7% CPU · 43.6 µs CPU/req)</sub> | **32,927**<br><sub>(83 MiB / 39.1% CPU · 47.5 µs CPU/req)</sub> | **31,915**<br><sub>(127 MiB / 40.4% CPU · 50.7 µs CPU/req)</sub> | **27,699**<br><sub>(125 MiB / 44.5% CPU · 64.3 µs CPU/req)</sub> |

#### macOS

Median of **3** repeats on `macos-15` (Apple Silicon M1, 3-core / 7 GB) @ `347a9a1b`. Source: Actions [37894283777](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37894283777) (`compare-arch`).

| Scenario | Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---|---:|---:|---:|---:|---:|
| Slow consumer (256 KiB GET, throttled client read) | HTTP/1 · TLS | HTTP/1 · plain | **112**<br><sub>(258 MiB / 2.3% CPU · 28 MiB/s · 624.6 µs CPU/req)</sub> | 🥇 **128**<br><sub>(84 MiB / 2.5% CPU · 32 MiB/s · 581.7 µs CPU/req)</sub> | **111**<br><sub>(112 MiB / 0.8% CPU · 28 MiB/s · 216.2 µs CPU/req)</sub> | **88**<br><sub>(146 MiB / 0.8% CPU · 22 MiB/s · 287.3 µs CPU/req)</sub> | **104**<br><sub>(219 MiB / 1.9% CPU · 26 MiB/s · 537.6 µs CPU/req)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · TLS | HTTP/1 · plain | **88**<br><sub>(348 MiB / 5.1% CPU · 22 MiB/s · 1756.8 µs CPU/req)</sub> | **103**<br><sub>(85 MiB / 1.7% CPU · 26 MiB/s · 485.9 µs CPU/req)</sub> | 🥇 **104**<br><sub>(113 MiB / 2.5% CPU · 26 MiB/s · 721.2 µs CPU/req)</sub> | **87**<br><sub>(155 MiB / 1.9% CPU · 22 MiB/s · 666.7 µs CPU/req)</sub> | **103**<br><sub>(232 MiB / 4.4% CPU · 26 MiB/s · 1288.6 µs CPU/req)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/3 · QUIC | HTTP/1 · plain | **88**<br><sub>(136 MiB / 19.3% CPU · 22 MiB/s · 6619.9 µs CPU/req)</sub> | **96**<br><sub>(96 MiB / 3.6% CPU · 24 MiB/s · 1127.7 µs CPU/req)</sub> | 🥇 **110**<br><sub>(111 MiB / 7.1% CPU · 27 MiB/s · 1949.0 µs CPU/req)</sub> | **87**<br><sub>(149 MiB / 8.6% CPU · 22 MiB/s · 2969.0 µs CPU/req)</sub> | **72**<br><sub>(343 MiB / 18.6% CPU · 18 MiB/s · 7787.4 µs CPU/req)</sub> |
| Early response (origin writes after first request chunk) | HTTP/1 · TLS | HTTP/1 · plain | **2,953**<br><sub>(253 MiB / 18.1% CPU · 369 MiB/s · 183.8 µs CPU/req)</sub> | **5,554**<br><sub>(89 MiB / 21.9% CPU · 694 MiB/s · 118.1 µs CPU/req)</sub> | 🥇 **5,662**<br><sub>(119 MiB / 27.4% CPU · 708 MiB/s · 145.3 µs CPU/req)</sub> | **3,014**<br><sub>(peak 5,208 · 120 MiB / 26.0% CPU · 377 MiB/s · 258.8 µs CPU/req)</sub> | **3,148**<br><sub>(194 MiB / 34.5% CPU · 393 MiB/s · 328.4 µs CPU/req)</sub> |
| Early response (origin writes after first request chunk) | HTTP/2 · TLS | HTTP/1 · plain | **1,955**<br><sub>(507 MiB / 23.2% CPU · 244 MiB/s · 356.4 µs CPU/req)</sub> | **3,910**<br><sub>(99 MiB / 20.0% CPU · 489 MiB/s · 153.8 µs CPU/req)</sub> | 🥇 **4,087**<br><sub>(124 MiB / 27.9% CPU · 511 MiB/s · 205.0 µs CPU/req)</sub> | **0**<br><sub>(peak 4,052 · 147 MiB / 22.4% CPU)</sub> | **2,946**<br><sub>(183 MiB / 26.1% CPU · 368 MiB/s · 266.0 µs CPU/req)</sub> |
| Early response (origin writes after first request chunk) | HTTP/3 · QUIC | HTTP/1 · plain | **594**<br><sub>(241 MiB / 23.4% CPU · 74 MiB/s · 1183.7 µs CPU/req)</sub> | 🥇 **1,221**<br><sub>(99 MiB / 21.9% CPU · 153 MiB/s · 538.0 µs CPU/req)</sub> | **629**<br><sub>(117 MiB / 26.4% CPU · 79 MiB/s · 1257.7 µs CPU/req)</sub> | **805**<br><sub>(134 MiB / 39.6% CPU · 101 MiB/s · 1474.1 µs CPU/req)</sub> | **648**<br><sub>(431 MiB / 31.8% CPU · 81 MiB/s · 1473.4 µs CPU/req)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · plain | HTTP/1 · plain | **96**<br><sub>(213 MiB / 2.7% CPU · 24 MiB/s · 851.8 µs CPU/req)</sub> | **103**<br><sub>(72 MiB / 1.2% CPU · 26 MiB/s · 348.5 µs CPU/req)</sub> | 🥇 **104**<br><sub>(99 MiB / 1.4% CPU · 26 MiB/s · 404.2 µs CPU/req)</sub> | **81**<br><sub>(145 MiB / 1.3% CPU · 20 MiB/s · 482.8 µs CPU/req)</sub> | **104**<br><sub>(146 MiB / 2.8% CPU · 26 MiB/s · 815.8 µs CPU/req)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · TLS | HTTP/2 · TLS | **95**<br><sub>(492 MiB / 3.6% CPU · 24 MiB/s · 1130.1 µs CPU/req)</sub> | *Not possible* | **96**<br><sub>(126 MiB / 3.3% CPU · 24 MiB/s · 1029.3 µs CPU/req)</sub> | **88**<br><sub>(131 MiB / 3.4% CPU · 22 MiB/s · 1161.0 µs CPU/req)</sub> | 🥇 **96**<br><sub>(343 MiB / 4.0% CPU · 24 MiB/s · 1257.6 µs CPU/req)</sub> |
| Early response (origin writes after first request chunk) | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **1,906**<br><sub>(490 MiB / 24.3% CPU · 238 MiB/s · 382.3 µs CPU/req)</sub> | *Not possible* | **1,438**<br><sub>(117 MiB / 18.8% CPU · 180 MiB/s · 391.7 µs CPU/req)</sub> | **0**<br><sub>(peak 1,868 · 132 MiB / 22.9% CPU)</sub> | **1,469**<br><sub>(311 MiB / 30.2% CPU · 184 MiB/s · 616.9 µs CPU/req)</sub> |
| Duplex (both directions live) | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **2,168**<br><sub>(500 MiB / 20.2% CPU · 271 MiB/s · 279.0 µs CPU/req)</sub> | *Not possible* | **1,718**<br><sub>(117 MiB / 19.1% CPU · 215 MiB/s · 333.9 µs CPU/req)</sub> | **0**<br><sub>(peak 1,803 · 132 MiB / 20.7% CPU)</sub> | **1,969**<br><sub>(350 MiB / 28.8% CPU · 246 MiB/s · 438.1 µs CPU/req)</sub> |
| Duplex (WebSocket / H1 Upgrade) | HTTP/1 · TLS | HTTP/1 · plain | **18,376**<br><sub>(164 MiB / 16.7% CPU · 27.3 µs CPU/req)</sub> | **8,654**<br><sub>(86 MiB / 7.4% CPU · 25.8 µs CPU/req)</sub> | **11,992**<br><sub>(103 MiB / 13.3% CPU · 33.4 µs CPU/req)</sub> | 🥇 **19,505**<br><sub>(122 MiB / 24.5% CPU · 37.7 µs CPU/req)</sub> | **15,853**<br><sub>(148 MiB / 18.7% CPU · 35.3 µs CPU/req)</sub> |

Slow consumer is sleep-bound; H1/H2/H3 sit in the same band. Prefer the ratio columns for early-response, duplex, and WebSocket rows.

### TLS termination cost (H1 TLS → cleartext origin)

Isolates keep-alive tiny GET vs **new connection per request** (handshake-dominated) vs keep-alive **256 KiB**. Product comparison uses RPS and end-to-end latency; TWP can also capture `ClientTlsTiming` when `TWP_RPS_CAPTURE_TLS=1` (child process) — nginx/YARP have no equivalent hook.

#### Windows

Median of **3** repeats on `windows-latest` @ `347a9a1b`. Source: Actions [37894288020](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37894288020). Absolute RPS on GHA swings hard; prefer **TWP÷YARP**.


*Not possible:* **HAProxy** and **Envoy** columns are omitted (no official Windows port).

| Workload | TWP | nginx | YARP |
|---|---:|---:|---:|
| Keep-alive · tiny GET | 🥇 **20,399**<br><sub>(91 MiB / 49.1% CPU · 96.2 µs CPU/req)</sub> | **8,708**<br><sub>(142 MiB / 24.8% CPU · 113.7 µs CPU/req)</sub> | **18,269**<br><sub>(105 MiB / 51.6% CPU · 113.0 µs CPU/req)</sub> |
| New-connection · tiny GET | 🥇 **711**<br><sub>(85 MiB / 0.4% CPU · 20.8 µs CPU/req)</sub> | **251**<br><sub>(141 MiB / 2.7% CPU · 426.6 µs CPU/req)</sub> | **702**<br><sub>(112 MiB)</sub> |
| Keep-alive · 256 KiB GET | 🥇 **2,797**<br><sub>(136 MiB / 44.9% CPU · 699 MiB/s · 641.5 µs CPU/req)</sub> | **169**<br><sub>(142 MiB / 24.7% CPU · 42 MiB/s · 5860.2 µs CPU/req)</sub> | **2,550**<br><sub>(136 MiB / 45.9% CPU · 638 MiB/s · 719.9 µs CPU/req)</sub> |

#### Linux

Median of **3** repeats @ `347a9a1b`. Source: Actions [37894288020](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37894288020).


| Workload | TWP | nginx | HAProxy | Envoy | YARP |
|---|---:|---:|---:|---:|---:|
| Keep-alive · tiny GET | **57,209**<br><sub>(106 MiB / 48.4% CPU · 33.8 µs CPU/req)</sub> | 🥇 **66,624**<br><sub>(101 MiB / 41.9% CPU · 25.2 µs CPU/req)</sub> | **61,007**<br><sub>(84 MiB / 44.0% CPU · 28.8 µs CPU/req)</sub> | **49,512**<br><sub>(128 MiB / 52.7% CPU · 42.6 µs CPU/req)</sub> | **51,922**<br><sub>(135 MiB / 48.7% CPU · 37.5 µs CPU/req)</sub> |
| New-connection · tiny GET | **2,036**<br><sub>(145 MiB / 0.2% CPU · 4.9 µs CPU/req)</sub> | 🥇 **2,100**<br><sub>(101 MiB)</sub> | **1,978**<br><sub>(85 MiB)</sub> | **1,865**<br><sub>(129 MiB / 0.2% CPU · 5.4 µs CPU/req)</sub> | **2,050**<br><sub>(141 MiB / 0.5% CPU · 9.6 µs CPU/req)</sub> |
| Keep-alive · 256 KiB GET | **4,681**<br><sub>(123 MiB / 29.4% CPU · 1,170 MiB/s · 251.4 µs CPU/req)</sub> | **3,000**<br><sub>(100 MiB / 48.0% CPU · 750 MiB/s · 640.2 µs CPU/req)</sub> | 🥇 **4,759**<br><sub>(87 MiB / 25.9% CPU · 1,190 MiB/s · 217.4 µs CPU/req)</sub> | **4,201**<br><sub>(146 MiB / 31.0% CPU · 1,050 MiB/s · 295.5 µs CPU/req)</sub> | **3,688**<br><sub>(167 MiB / 40.5% CPU · 922 MiB/s · 438.9 µs CPU/req)</sub> |

#### macOS

Median of **3** repeats on `macos-15` @ `347a9a1b`. Source: Actions [37894288020](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37894288020).

| Workload | TWP | nginx | HAProxy | Envoy | YARP |
|---|---:|---:|---:|---:|---:|
| Keep-alive · tiny GET | **7,274**<br><sub>(124 MiB / 14.5% CPU · 59.9 µs CPU/req)</sub> | **14,449**<br><sub>(85 MiB / 12.7% CPU · 26.3 µs CPU/req)</sub> | 🥇 **24,157**<br><sub>(109 MiB / 25.7% CPU · 31.9 µs CPU/req)</sub> | **7,662**<br><sub>(122 MiB / 19.1% CPU · 74.8 µs CPU/req)</sub> | **12,277**<br><sub>(160 MiB / 22.7% CPU · 55.6 µs CPU/req)</sub> |
| New-connection · tiny GET | **116**<br><sub>(108 MiB / 0.4% CPU · 105.9 µs CPU/req)</sub> | **624**<br><sub>(86 MiB)</sub> | 🥇 **804**<br><sub>(105 MiB)</sub> | **592**<br><sub>(131 MiB / 0.1% CPU · 3.5 µs CPU/req)</sub> | **122**<br><sub>(186 MiB)</sub> |
| Keep-alive · 256 KiB GET | **1,982**<br><sub>(213 MiB / 24.0% CPU · 495 MiB/s · 363.0 µs CPU/req)</sub> | **1,669**<br><sub>(84 MiB / 19.3% CPU · 417 MiB/s · 346.2 µs CPU/req)</sub> | 🥇 **2,430**<br><sub>(117 MiB / 27.2% CPU · 608 MiB/s · 336.1 µs CPU/req)</sub> | **1,957**<br><sub>(154 MiB / 24.8% CPU · 489 MiB/s · 380.8 µs CPU/req)</sub> | **1,334**<br><sub>(183 MiB / 29.9% CPU · 333 MiB/s · 673.1 µs CPU/req)</sub> |

Prefer **TWP÷YARP** in the table. Absolute RPS on GHA swings hard. New-connection is Darwin SslStream-bound for TWP and YARP (handshake p99 SLO **500 ms** on macOS only — Win/Linux stay at **200 ms**).

## Unary gRPC (H2 TLS)

Unary Echo **RPC/s** @ c=64 over H2 TLS→H2 TLS for Titanium, YARP, nginx (`grpc_pass`), HAProxy, and Envoy (OS-possible peers only). Not folded into the architecture-sensitive tables. Median of **3** repeats @ `347a9a1b` — [37894265694](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37894265694).

| OS | Titanium | YARP | nginx | HAProxy | Envoy |
|---|---:|---:|---:|---:|---:|
| Windows | 🥇 **92,344**<br><sub>(132 MiB / 32.1% CPU · 13.9 µs CPU/req)</sub> | **58,419**<br><sub>(123 MiB / 48.2% CPU · 33.0 µs CPU/req)</sub> | **0**<br><sub>(144 MiB / 24.9% CPU)</sub> | *Not possible* | *Not possible* |
| Linux | 🥇 **73,756**<br><sub>(180 MiB / 35.0% CPU · 19.0 µs CPU/req)</sub> | **44,086**<br><sub>(157 MiB / 41.8% CPU · 38.0 µs CPU/req)</sub> | **0**<br><sub>(113 MiB / 41.4% CPU)</sub> | **35,189**<br><sub>(84 MiB / 44.6% CPU · 50.7 µs CPU/req)</sub> | **45,257**<br><sub>(126 MiB / 50.0% CPU · 44.2 µs CPU/req)</sub> |
| macOS | **11,690**<br><sub>(263 MiB / 13.4% CPU · 34.3 µs CPU/req)</sub> | **16,360**<br><sub>(216 MiB / 16.6% CPU · 30.4 µs CPU/req)</sub> | **0**<br><sub>(86 MiB / 3.4% CPU)</sub> | **12,026**<br><sub>(120 MiB / 28.5% CPU · 71.2 µs CPU/req)</sub> | 🥇 **22,448**<br><sub>(121 MiB / 41.7% CPU · 55.7 µs CPU/req)</sub> |

## Unary gRPC (H2 TLS → h2c)

Unary Echo **RPC/s** @ c=64 over **H2 TLS → h2c** (edge TLS, cleartext H2 origin). Peers: Titanium, YARP, HAProxy, Envoy. Mode: `compare-grpc` (`*-grpc-h2c`). Median of **3** repeats @ `347a9a1b` — [37894265694](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37894265694).

*Not possible:* **nginx** column omitted (no H2 upstream).

| OS | Titanium | YARP | HAProxy | Envoy |
|---|---:|---:|---:|---:|
| Windows | 🥇 **94,072**<br><sub>(132 MiB / 31.2% CPU · 13.3 µs CPU/req)</sub> | **60,317**<br><sub>(125 MiB / 49.4% CPU · 32.8 µs CPU/req)</sub> | *Not possible* | *Not possible* |
| Linux | 🥇 **79,344**<br><sub>(171 MiB / 34.5% CPU · 17.4 µs CPU/req)</sub> | **47,864**<br><sub>(155 MiB / 42.3% CPU · 35.3 µs CPU/req)</sub> | **37,142**<br><sub>(82 MiB / 43.8% CPU · 47.2 µs CPU/req)</sub> | **46,487**<br><sub>(125 MiB / 49.4% CPU · 42.5 µs CPU/req)</sub> |
| macOS | 🥇 **13,468**<br><sub>(275 MiB / 7.0% CPU · 15.5 µs CPU/req)</sub> | **11,969**<br><sub>(211 MiB / 11.1% CPU · 27.9 µs CPU/req)</sub> | **8,382**<br><sub>(118 MiB / 12.7% CPU · 45.4 µs CPU/req)</sub> | **9,235**<br><sub>(120 MiB / 18.9% CPU · 61.5 µs CPU/req)</sub> |

## WebSocket (H1 TLS → H1 TLS)

WebSocket echo round-trips/sec over **dual-TLS** H1 (`proxy_ssl` style). Mode: `compare-ws-h1tls` (`*-duplex-ws-h1tls`). Median of **3** repeats @ `347a9a1b`. Source: [37894297605](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37894297605).

| OS | Titanium | YARP | nginx | HAProxy | Envoy |
|---|---:|---:|---:|---:|---:|
| Windows | 🥇 **54,658**<br><sub>(102 MiB / 47.9% CPU · 35.1 µs CPU/req)</sub> | **50,899**<br><sub>(94 MiB / 43.5% CPU · 34.2 µs CPU/req)</sub> | **27,060**<br><sub>(143 MiB / 24.7% CPU · 36.6 µs CPU/req)</sub> | *Not possible* | *Not possible* |
| Linux | **55,626**<br><sub>(146 MiB / 45.9% CPU · 33.0 µs CPU/req)</sub> | **49,951**<br><sub>(124 MiB / 45.1% CPU · 36.1 µs CPU/req)</sub> | 🥇 **59,863**<br><sub>(105 MiB / 40.0% CPU · 26.7 µs CPU/req)</sub> | **57,439**<br><sub>(84 MiB / 42.8% CPU · 29.8 µs CPU/req)</sub> | **58,169**<br><sub>(127 MiB / 42.1% CPU · 29.0 µs CPU/req)</sub> |
| macOS | **22,089**<br><sub>(153 MiB / 31.6% CPU · 43.0 µs CPU/req)</sub> | **25,225**<br><sub>(205 MiB / 33.8% CPU · 40.2 µs CPU/req)</sub> | 🥇 **33,426**<br><sub>(86 MiB / 21.9% CPU · 19.6 µs CPU/req)</sub> | **30,248**<br><sub>(102 MiB / 28.7% CPU · 28.4 µs CPU/req)</sub> | **23,837**<br><sub>(119 MiB / 28.9% CPU · 36.4 µs CPU/req)</sub> |

## WebSocket (H2 TLS 8441 → H1)

WebSocket echo over **RFC 8441** extended CONNECT (H2 TLS client → H1 plain origin). Mode: `compare-ws-h2` (`*-duplex-ws-h2`).

The probe client sets `NoDelay` and writes each HTTP/2 frame as one TLS record. Median of **3** repeats @ `347a9a1b`. Source: [37894301831](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37894301831).

*Not possible:* **nginx** column omitted (no RFC 8441 extended CONNECT).

| OS | Titanium | YARP | HAProxy | Envoy |
|---|---:|---:|---:|---:|
| Windows | 🥇 **31,416**<br><sub>(159 MiB / 46.6% CPU · 59.3 µs CPU/req)</sub> | **0**<br><sub>(152 MiB / 20.2% CPU)</sub> | *Not possible* | *Not possible* |
| Linux | 🥇 **47,639**<br><sub>(190 MiB / 41.0% CPU · 34.5 µs CPU/req)</sub> | **0**<br><sub>(225 MiB / 41.0% CPU)</sub> | **47,630**<br><sub>(84 MiB / 37.1% CPU · 31.1 µs CPU/req)</sub> | **0**<br><sub>(123 MiB / 0.2% CPU)</sub> |
| macOS | 🥇 **10,128**<br><sub>(302 MiB / 22.9% CPU · 67.7 µs CPU/req)</sub> | **0**<br><sub>(172 MiB / 10.9% CPU)</sub> | **9,453**<br><sub>(111 MiB / 15.6% CPU · 49.4 µs CPU/req)</sub> | **0**<br><sub>(116 MiB / 0.5% CPU)</sub> |

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
| Editions | `compare-editions` | Linux only; one job per wiki row (feature arm + its baseline) |
| Pre-wiki smoke | `compare-product-smoke` (Linux, `repeats=1`) | ~30–60 min; required before full product |
| Release / wiki | `compare-product` reverse via [RPS suite](https://github.com/justcoding121/titanium-web-proxy/blob/develop/.github/workflows/rps-suite.yml) | one job per wiki row on Win/Linux/macOS; MITM arms (`twp-mitm-*`) on Linux only, same job as that row's reverse peers |
| Unary gRPC | `compare-grpc` | one job per row (H2 TLS and h2c) on Win/Linux/macOS |
| WebSocket dual-TLS | `compare-ws-h1tls` | one row, three OS |
| WebSocket RFC 8441 | `compare-ws-h2` | one row, three OS (nginx N/A) |
| Heavier tables | `compare-bodies`, `post`, `lossy`, `arch`, `tls-cost`, `compare-saturation` | one suite run each, one job per row, on Win/Linux/macOS |

See [PERF-GATES.md](https://github.com/justcoding121/titanium-web-proxy/blob/develop/tools/RpsLoadProbe/PERF-GATES.md).

### Arm shards (comparison groups)

`--arm-shard` takes a comparison-group key (`h1c-h1c`, from `--print-groups`), not an `i/n` index, so a re-run measures the same row after a peer install flake. `i/n` remains for smoke modes. Paste unions `rps-csv-<os>-shard-*` (`paste-compare-product-wiki.ps1 -RunIds …`). Table “Source: Actions” may list several run URLs for one table — **do not mix SHAs**. A single-row leg allows **60** minutes of ramp and **75** minutes overall. Free GitHub accounts run **20** jobs at once, **5** of them macOS. Give a full suite a quiet window: do not push pull requests or start other macOS jobs while it runs. Re-run infrastructure failures only (`gh run rerun --failed`). A failed gate is a finding and is never re-rolled; publish the last attempt, never the best of several. The RPS tables on this page are @ `347a9a1b`: product [37894265694](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37894265694), saturation [37894269584](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37894269584), bodies [37894273314](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37894273314), POST [37894276720](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37894276720), lossy [37894280112](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37894280112), arch [37894283777](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37894283777), TLS cost [37894288020](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37894288020), gRPC [37894294007](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37894294007), WebSocket H1 TLS [37894297605](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37894297605), WebSocket H2 [37894301831](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37894301831), editions [37894305384](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37894305384). Reverse product rows, bodies, POST, lossy, arch, TLS cost, gRPC, both WebSocket modes, and saturation run on Windows, Linux, and macOS. TWP MITM arms run on Linux only. `compare-editions` (CLI and Plus) runs on Linux only. `compare-spot` on pull requests to beta and stable stays on all three OS. The historical v2 MITM note below the product tables stays @ `41f4adee` because those run ids are from that fix, not this re-measure.

One wiki row = one GHA job’s Client×Origin cell set: TWP + YARP + nginx + HAProxy + Envoy (when the OS can run them) stay on the **same VM**. On Linux that job also runs TWP Lite and Full, so **Lite÷Reverse** stays a same-job ratio. Windows and macOS product jobs omit `twp-mitm-*`. Shards split **rows**, not individual proxies — so **TWP÷YARP** remains a same-job ratio. Do not compare **absolute** RPS across shards (different VMs).

### Gate thresholds

- Reverse product signal: **TWP ÷ closest peer ≥ 0.50** (the peer among YARP, nginx, HAProxy, and Envoy whose sustain is nearest). Edition CLI/Plus ratios floor at **0.50**.
- MITM overhead: **Lite÷Reverse ≥ 0.25** and **Full÷Reverse ≥ 0.25** (median of 3 GHA runs @ c=64). Absolute RPS moves with runner heat; ratios are the claim.
- Do **not** enable the HTTP/2 MITM multi-origin relay pool (`MaxOriginHttp2ConnectionsPerAuthority` > 1 under interception): Tip A/B showed Lite err%~29 and RSS blow-up, which fails the Lite/Full÷Reverse floors. Multi-origin remains gate-off compressed-relay only.
- Editions: see [PERF-GATES.md](https://github.com/justcoding121/titanium-web-proxy/blob/develop/tools/RpsLoadProbe/PERF-GATES.md) and `validate-edition-gates.ps1`.

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
