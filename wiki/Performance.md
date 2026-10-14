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
- **Typical reverse** is H1 TLS→H1 or H2→H1 (~1.1× YARP on Win/Linux tiny GET). With **larger bodies**, see [Heavier reverse](#user-content-heavier-reverse-workloads) (@ `88187363`).

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

On the Linux H2 TLS→H1 plain row at c=64 (16 connections, @ `88187363`), proxy CPU was about **43%** for nginx, **45%** for HAProxy, and **54%** for Envoy. Windows nginx on the same H2 client shape stayed near **25%** (one core). That measured shape is the one published here.

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

Calibration for the shared 4 vCPU loopback shape: how close client + origin are to saturated before ranking reverse peers. Tiny keep-alive GET. Median of **3** repeats @ `88187363` — [38550341872](https://github.com/justcoding121/titanium-web-proxy/actions/runs/38550341872). Warmup 2s / measure 8s; concurrency 8, 16, 32, 64. Block A **% of origin-HttpClient** uses median **peak** RPS. Blocks B/C use peer÷YARP / ÷nginx on median peak (not % of H1 origin). **RPS cells** embed median RSS / CPU for the **proxy child** plus its **full descendant tree** (serve-proxy → nginx master → workers); origin-direct samples the **origin** child. Product matrices below use matched `dotnet-httpclient` only (not bombardier).


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
| origin-direct | dotnet-httpclient | **63,518**<br><sub>(55 MiB / 44.2% CPU · 27.8 µs CPU/req)</sub> | **100.0%** |
| origin-direct-bombardier | bombardier | **48,572**<br><sub>(55 MiB / 24.6% CPU · 20.3 µs CPU/req)</sub> | **76.5%** |
| bare-reverse-http1 | dotnet-httpclient | **31,387**<br><sub>(56 MiB / 46.6% CPU · 59.4 µs CPU/req)</sub> | **49.4%** |
| nginx-reverse-http1 | dotnet-httpclient | **19,584**<br><sub>(126 MiB / 24.7% CPU · 50.5 µs CPU/req)</sub> | **30.8%** |
| yarp-reverse-http1 | dotnet-httpclient | **27,207**<br><sub>(86 MiB / 50.3% CPU · 73.9 µs CPU/req)</sub> | **42.8%** |
| twp-reverse-http1 | dotnet-httpclient | 🥇 **31,719**<br><sub>(77 MiB / 49.5% CPU · 62.5 µs CPU/req)</sub> | **49.9%** |

**Linux** (`ubuntu-latest`)

| Arm | Generator | RPS | % of origin-HttpClient |
|---|---|---:|---:|
| origin-direct | dotnet-httpclient | **105,520**<br><sub>(81 MiB / 43.2% CPU · 16.4 µs CPU/req)</sub> | **100.0%** |
| origin-direct-bombardier | bombardier | **57,947**<br><sub>(80 MiB / 31.6% CPU · 21.8 µs CPU/req)</sub> | **54.9%** |
| bare-reverse-http1 | dotnet-httpclient | **47,669**<br><sub>(73 MiB / 43.7% CPU · 36.7 µs CPU/req)</sub> | **45.2%** |
| nginx-reverse-http1 | dotnet-httpclient | 🥇 **61,370**<br><sub>(76 MiB / 39.1% CPU · 25.5 µs CPU/req)</sub> | **58.2%** |
| yarp-reverse-http1 | dotnet-httpclient | **41,004**<br><sub>(118 MiB / 49.4% CPU · 48.2 µs CPU/req)</sub> | **38.9%** |
| twp-reverse-http1 | dotnet-httpclient | **46,843**<br><sub>(85 MiB / 49.0% CPU · 41.8 µs CPU/req)</sub> | **44.4%** |


**macOS** (`macos-15`, Apple Silicon)

| Arm | Generator | RPS | % of origin-HttpClient |
|---|---|---:|---:|
| origin-direct | dotnet-httpclient | **59,657**<br><sub>(89 MiB / 23.9% CPU · 16.0 µs CPU/req)</sub> | **100.0%** |
| origin-direct-bombardier | bombardier | **54,519**<br><sub>(89 MiB / 23.7% CPU · 17.4 µs CPU/req)</sub> | **91.4%** |
| bare-reverse-http1 | dotnet-httpclient | **20,943**<br><sub>(91 MiB / 19.3% CPU · 36.8 µs CPU/req)</sub> | **35.1%** |
| nginx-reverse-http1 | dotnet-httpclient | **29,506**<br><sub>(68 MiB / 19.8% CPU · 26.9 µs CPU/req)</sub> | **49.5%** |
| yarp-reverse-http1 | dotnet-httpclient | **23,030**<br><sub>(148 MiB / 33.0% CPU · 57.4 µs CPU/req)</sub> | **38.6%** |
| twp-reverse-http1 | dotnet-httpclient | 🥇 **34,852**<br><sub>(114 MiB / 36.2% CPU · 41.5 µs CPU/req)</sub> | **58.4%** |

On this macOS run `origin-direct` was **23.9%** CPU, so the % of origin-HttpClient column is not a ceiling when that CPU is near idle. Rank those Mac peers by RPS.

Reverse peers on Block A @ `88187363`: Windows TWP is **49.9%** of origin-direct and Linux TWP is **44.4%**. Prefer the **%** column over absolute RPS across runs. Bare and origin-direct are controls (not medal peers).

#### Block B — H2 TLS→H1

Peer ratios (÷YARP / ÷nginx) on median peak; **RPS cells** embed `(MiB / CPU%)`.

**Windows** (`windows-latest`)

| Arm | Generator | RPS | ÷YARP | ÷nginx |
|---|---|---:|---:|---:|
| nginx-reverse-http2 | dotnet-httpclient | **11,105**<br><sub>(142 MiB / 24.8% CPU · 89.2 µs CPU/req)</sub> | **0.33×** | **1.00×** |
| yarp-reverse-http2 | dotnet-httpclient | **33,327**<br><sub>(107 MiB / 51.8% CPU · 62.1 µs CPU/req)</sub> | **1.00×** | **3.00×** |
| twp-reverse-http2-cleartext | dotnet-httpclient | 🥇 **36,554**<br><sub>(122 MiB / 53.1% CPU · 58.1 µs CPU/req)</sub> | **1.10×** | **3.29×** |

**Linux** (`ubuntu-latest`)

| Arm | Generator | RPS | ÷YARP | ÷nginx |
|---|---|---:|---:|---:|
| nginx-reverse-http2 | dotnet-httpclient | **38,560**<br><sub>(101 MiB / 40.6% CPU · 42.1 µs CPU/req)</sub> | **1.02×** | **1.00×** |
| yarp-reverse-http2 | dotnet-httpclient | **37,890**<br><sub>(133 MiB / 48.5% CPU · 51.2 µs CPU/req)</sub> | **1.00×** | **0.98×** |
| twp-reverse-http2-cleartext | dotnet-httpclient | 🥇 **41,224**<br><sub>(153 MiB / 49.4% CPU · 47.9 µs CPU/req)</sub> | **1.09×** | **1.07×** |

**macOS** (`macos-15`, Apple Silicon)

| Arm | Generator | RPS | ÷YARP | ÷nginx |
|---|---|---:|---:|---:|
| nginx-reverse-http2 | dotnet-httpclient | **34,494**<br><sub>(83 MiB / 25.3% CPU · 29.3 µs CPU/req)</sub> | **0.90×** | **1.00×** |
| yarp-reverse-http2 | dotnet-httpclient | 🥇 **38,169**<br><sub>(158 MiB / 42.2% CPU · 44.2 µs CPU/req)</sub> | **1.00×** | **1.11×** |
| twp-reverse-http2-cleartext | dotnet-httpclient | **37,118**<br><sub>(175 MiB / 41.4% CPU · 44.6 µs CPU/req)</sub> | **0.97×** | **1.08×** |
#### Block C — H3→H1

Same layout as Block B. Requires QuicListener. nginx needs `http_v3_module` (Windows nginx has no QUIC). HAProxy needs `USE_QUIC` (GHA Linux/macOS require it). Envoy 1.20+ includes HTTP/3.

Medals are nginx / YARP / Titanium only, from the same run as Blocks A and B ([38550341872](https://github.com/justcoding121/titanium-web-proxy/actions/runs/38550341872)). On the Linux run HAProxy was **23,149** (88 MiB / 35.3% CPU) and Envoy **8,803** (132 MiB / 58.8% CPU). On the macOS run HAProxy was **20,965** (105 MiB / 29.1% CPU) and Envoy **9,246** (126 MiB / 56.5% CPU).

**Windows** (`windows-latest`)

| Arm | Generator | RPS | ÷YARP | ÷nginx |
|---|---|---:|---:|---:|
| nginx-reverse-http3-cleartext | dotnet-httpclient | *Not possible (no QUIC)* | — | — |
| yarp-reverse-http3-cleartext | dotnet-httpclient | **9,918**<br><sub>(157 MiB / 49.2% CPU · 198.4 µs CPU/req)</sub> | **1.00×** | — |
| twp-reverse-http3-cleartext | dotnet-httpclient | 🥇 **10,963**<br><sub>(113 MiB / 48.6% CPU · 177.3 µs CPU/req)</sub> | **1.11×** | — |

**Linux** (`ubuntu-latest`)

| Arm | Generator | RPS | ÷YARP | ÷nginx |
|---|---|---:|---:|---:|
| nginx-reverse-http3-cleartext | dotnet-httpclient | **22,691**<br><sub>(106 MiB / 28.1% CPU · 49.5 µs CPU/req)</sub> | **0.92×** | **1.00×** |
| yarp-reverse-http3-cleartext | dotnet-httpclient | **19,605**<br><sub>(198 MiB / 50.0% CPU · 102.0 µs CPU/req)</sub> | **1.00×** | **1.09×** |
| twp-reverse-http3-cleartext | dotnet-httpclient | 🥇 **23,355**<br><sub>(156 MiB / 50.8% CPU · 86.9 µs CPU/req)</sub> | **1.19×** | **1.29×** |

**macOS** (`macos-15`, Apple Silicon)

| Arm | Generator | RPS | ÷YARP | ÷nginx |
|---|---|---:|---:|---:|
| nginx-reverse-http3-cleartext | dotnet-httpclient | **11,572**<br><sub>(peak 20,033 · 85 MiB / 22.4% CPU · 77.4 µs CPU/req)</sub> | **1.38×** | **1.00×** |
| yarp-reverse-http3-cleartext | dotnet-httpclient | **14,527**<br><sub>(218 MiB / 43.8% CPU · 120.6 µs CPU/req)</sub> | **1.00×** | **0.73×** |
| twp-reverse-http3-cleartext | dotnet-httpclient | 🥇 **16,362**<br><sub>(140 MiB / 44.7% CPU · 109.2 µs CPU/req)</sub> | **1.13×** | **0.82×** |

## Windows — Titanium vs nginx vs HAProxy vs Envoy vs YARP

Client / origin: HTTP version and whether TLS is used (`plain` = cleartext, `TLS` = encrypted, `QUIC` = HTTP/3).

### Reverse

Median of **3 repeats** on `windows-latest` (4 vCPU / 16 GiB). Bare reverse 5×5 @ `88187363` — `compare-product` [38550284913](https://github.com/justcoding121/titanium-web-proxy/actions/runs/38550284913). Warmup 2s / measure 8s; concurrency 8, 16, 32, 64. Prefer TWP÷peer ratios over absolute RPS. **RPS cells** include median RSS / CPU at the peak-RPS step as `<br><sub>(MiB / CPU%)</sub>`. nginx terminate peers use `keepalive 256` + streaming buffers. **HAProxy / Envoy are Linux-only peers** (no official Windows port). Laptop High-perf / cool-paired numbers stay on the [local lab](Performance-Local-Lab). Product 5×5 is **~56-byte JSON keep-alive GET**; H2/H3 same-protocol cells are mostly header work with a tiny body (Titanium best case) — see [Why this comparison is fair](#user-content-why-this-comparison-is-fair).

**Load generators:** Reverse inbound H3 arms use **`dotnet-httpclient`** (`http_version=3.0`, `RequestVersionExact`). nginx/Windows is same-OS only (no QUIC). HAProxy/Envoy are Linux-only terminate peers.

| Client | Origin | TWP | nginx | YARP |
|---|---|---:|---:|---:|
| HTTP/1 · plain | HTTP/1 · plain | 🥇 **37,430**<br><sub>(81 MiB / 47.8% CPU · 51.1 µs CPU/req)</sub> | **29,433**<br><sub>(126 MiB / 24.8% CPU · 33.8 µs CPU/req)</sub> | **32,904**<br><sub>(89 MiB / 47.9% CPU · 58.3 µs CPU/req)</sub> |
| HTTP/1 · plain | HTTP/1 · TLS | 🥇 **34,096**<br><sub>(90 MiB / 50.9% CPU · 59.7 µs CPU/req)</sub> | **15,589**<br><sub>(137 MiB / 24.9% CPU · 63.8 µs CPU/req)</sub> | **30,982**<br><sub>(101 MiB / 49.1% CPU · 63.4 µs CPU/req)</sub> |
| HTTP/1 · plain | HTTP/2 · plain | 🥇 **36,976**<br><sub>(103 MiB / 46.7% CPU · 50.6 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **32,787**<br><sub>(90 MiB / 48% CPU · 58.6 µs CPU/req)</sub> |
| HTTP/1 · plain | HTTP/2 · TLS | 🥇 **40,192**<br><sub>(121 MiB / 48.5% CPU · 48.3 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **36,754**<br><sub>(100 MiB / 48% CPU · 52.3 µs CPU/req)</sub> |
| HTTP/1 · plain | HTTP/3 · QUIC | 🥇 **18,479**<br><sub>(111 MiB / 49.5% CPU · 107.2 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | **18,102**<br><sub>(119 MiB / 49.8% CPU · 109.9 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/1 · plain | 🥇 **21,672**<br><sub>(88 MiB / 46.8% CPU · 86.4 µs CPU/req)</sub> | **9,372**<br><sub>(142 MiB / 24.1% CPU · 102.9 µs CPU/req)</sub> | **19,328**<br><sub>(106 MiB / 45.4% CPU · 93.9 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/1 · TLS | 🥇 **21,235**<br><sub>(90 MiB / 48.2% CPU · 90.9 µs CPU/req)</sub> | **8,376**<br><sub>(144 MiB / 24.7% CPU · 118.0 µs CPU/req)</sub> | **19,324**<br><sub>(105 MiB / 48.3% CPU · 100.0 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/2 · plain | 🥇 **28,535**<br><sub>(110 MiB / 43.6% CPU · 61.0 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **26,359**<br><sub>(110 MiB / 45.2% CPU · 68.6 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/2 · TLS | 🥇 **25,344**<br><sub>(117 MiB / 47.3% CPU · 74.7 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **24,418**<br><sub>(107 MiB / 47.2% CPU · 77.4 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/3 · QUIC | 🥇 **19,344**<br><sub>(111 MiB / 50.7% CPU · 104.9 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | **17,188**<br><sub>(128 MiB / 48.3% CPU · 112.5 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | 🥇 **48,595**<br><sub>(120 MiB / 47.3% CPU · 38.9 µs CPU/req)</sub> | **16,428**<br><sub>(128 MiB / 24.7% CPU · 60.1 µs CPU/req)</sub> | **47,460**<br><sub>(94 MiB / 55.7% CPU · 46.9 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/1 · TLS | 🥇 **28,068**<br><sub>(119 MiB / 52.4% CPU · 74.7 µs CPU/req)</sub> | **6,423**<br><sub>(138 MiB / 24.7% CPU · 153.9 µs CPU/req)</sub> | **25,852**<br><sub>(105 MiB / 54.3% CPU · 84.0 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/2 · plain | 🥇 **71,440**<br><sub>(100 MiB / 34.6% CPU · 19.4 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **52,060**<br><sub>(97 MiB / 50.8% CPU · 39.0 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/2 · TLS | 🥇 **86,057**<br><sub>(116 MiB / 36.1% CPU · 16.8 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **63,619**<br><sub>(109 MiB / 49.1% CPU · 30.9 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/3 · QUIC | 🥇 **27,800**<br><sub>(138 MiB / 52.2% CPU · 75.1 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | **26,134**<br><sub>(124 MiB / 53.9% CPU · 82.5 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **31,026**<br><sub>(126 MiB / 51.9% CPU · 66.9 µs CPU/req)</sub> | **7,772**<br><sub>(142 MiB / 24.7% CPU · 127.1 µs CPU/req)</sub> | **27,808**<br><sub>(105 MiB / 50.8% CPU · 73.0 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/1 · TLS | 🥇 **37,212**<br><sub>(115 MiB / 49.5% CPU · 53.3 µs CPU/req)</sub> | **12,357**<br><sub>(145 MiB / 24.9% CPU · 80.6 µs CPU/req)</sub> | **34,602**<br><sub>(105 MiB / 51.2% CPU · 59.2 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | 🥇 **82,994**<br><sub>(119 MiB / 36.7% CPU · 17.7 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **59,748**<br><sub>(108 MiB / 52.6% CPU · 35.2 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **53,100**<br><sub>(112 MiB / 35.7% CPU · 26.9 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **40,755**<br><sub>(107 MiB / 47.7% CPU · 46.8 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/3 · QUIC | 🥇 **22,934**<br><sub>(155 MiB / 51.5% CPU · 89.8 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | **21,347**<br><sub>(127 MiB / 53.1% CPU · 99.5 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **9,080**<br><sub>(113 MiB / 46.7% CPU · 205.9 µs CPU/req)</sub> | *Not possible (no QUIC)* | **6,455**<br><sub>(152 MiB / 43.6% CPU · 270.2 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **8,412**<br><sub>(114 MiB / 50.3% CPU · 239.3 µs CPU/req)</sub> | *Not possible (no QUIC)* | **7,690**<br><sub>(161 MiB / 50% CPU · 260.0 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/2 · plain | 🥇 **50,871**<br><sub>(155 MiB / 46.9% CPU · 36.9 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **45,980**<br><sub>(176 MiB / 48.9% CPU · 42.5 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | 🥇 **12,058**<br><sub>(121 MiB / 48.1% CPU · 159.6 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **10,490**<br><sub>(156 MiB / 49.5% CPU · 188.6 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/3 · QUIC | 🥇 **13,576**<br><sub>(119 MiB / 46.6% CPU · 137.3 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | **11,023**<br><sub>(171 MiB / 49.2% CPU · 178.6 µs CPU/req)</sub> |

## Linux — Titanium vs nginx vs HAProxy vs Envoy vs YARP

### Reverse

Median of **3 repeats** on `ubuntu-latest` (4 vCPU / 16 GiB). Bare reverse 5×5 @ `88187363` — `compare-product` [38550284913](https://github.com/justcoding121/titanium-web-proxy/actions/runs/38550284913). Warmup 2s / measure 8s; concurrency 8, 16, 32, 64. **Linux nginx is the authoritative nginx baseline.** HAProxy (3.2 `USE_QUIC`) and Envoy (GitHub release, HTTP/3 compiled in) run on the same loopback shape as nginx/YARP. nginx terminate peers use `keepalive 256` + streaming buffers. The RPS workflow installs nginx.org mainline (`http_v3_module`), a QUIC-enabled HAProxy, Envoy, and `libmsquic`. Prefer ratios over absolute RPS. Product 5×5 is **~56-byte JSON keep-alive GET**; H2/H3 same-protocol cells are mostly header work with a tiny body (Titanium best case) — see [Why this comparison is fair](#user-content-why-this-comparison-is-fair).

| Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---:|---:|---:|---:|---:|
| HTTP/1 · plain | HTTP/1 · plain | **62,492**<br><sub>(83 MiB / 49.3% CPU · 31.5 µs CPU/req)</sub> | 🥇 **75,583**<br><sub>(76 MiB / 40.2% CPU · 21.3 µs CPU/req)</sub> | **64,687**<br><sub>(66 MiB / 42.7% CPU · 26.4 µs CPU/req)</sub> | **48,875**<br><sub>(115 MiB / 52.9% CPU · 43.3 µs CPU/req)</sub> | **53,812**<br><sub>(116 MiB / 47% CPU · 35.0 µs CPU/req)</sub> |
| HTTP/1 · plain | HTTP/1 · TLS | **36,279**<br><sub>(104 MiB / 48.2% CPU · 53.1 µs CPU/req)</sub> | 🥇 **44,496**<br><sub>(93 MiB / 40.6% CPU · 36.5 µs CPU/req)</sub> | **40,003**<br><sub>(71 MiB / 44.3% CPU · 44.3 µs CPU/req)</sub> | **28,475**<br><sub>(118 MiB / 56.4% CPU · 79.3 µs CPU/req)</sub> | **33,325**<br><sub>(128 MiB / 49.2% CPU · 59.1 µs CPU/req)</sub> |
| HTTP/1 · plain | HTTP/2 · plain | 🥇 **40,445**<br><sub>(127 MiB / 51.2% CPU · 50.6 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **25,662**<br><sub>(66 MiB / 43.8% CPU · 68.2 µs CPU/req)</sub> | **21,416**<br><sub>(116 MiB / 63.1% CPU · 117.8 µs CPU/req)</sub> | **35,594**<br><sub>(125 MiB / 49% CPU · 55.1 µs CPU/req)</sub> |
| HTTP/1 · plain | HTTP/2 · TLS | 🥇 **33,028**<br><sub>(149 MiB / 48.9% CPU · 59.2 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **26,523**<br><sub>(67 MiB / 46.7% CPU · 70.5 µs CPU/req)</sub> | **20,559**<br><sub>(117 MiB / 61.6% CPU · 119.9 µs CPU/req)</sub> | **30,034**<br><sub>(135 MiB / 47.4% CPU · 63.2 µs CPU/req)</sub> |
| HTTP/1 · plain | HTTP/3 · QUIC | 🥇 **23,130**<br><sub>(135 MiB / 54.3% CPU · 94.0 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **10,608**<br><sub>(121 MiB / 56.9% CPU · 214.4 µs CPU/req)</sub> | **21,364**<br><sub>(155 MiB / 49% CPU · 91.7 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/1 · plain | **23,622**<br><sub>(105 MiB / 49.7% CPU · 84.2 µs CPU/req)</sub> | 🥇 **28,279**<br><sub>(99 MiB / 41.9% CPU · 59.2 µs CPU/req)</sub> | **26,673**<br><sub>(82 MiB / 44.4% CPU · 66.5 µs CPU/req)</sub> | **16,744**<br><sub>(127 MiB / 58.6% CPU · 139.9 µs CPU/req)</sub> | **20,318**<br><sub>(132 MiB / 50.8% CPU · 99.9 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/1 · TLS | **34,580**<br><sub>(109 MiB / 46.2% CPU · 53.5 µs CPU/req)</sub> | 🥇 **43,561**<br><sub>(104 MiB / 38.6% CPU · 35.5 µs CPU/req)</sub> | **40,702**<br><sub>(85 MiB / 42% CPU · 41.3 µs CPU/req)</sub> | **29,743**<br><sub>(127 MiB / 53.6% CPU · 72.2 µs CPU/req)</sub> | **30,648**<br><sub>(142 MiB / 48% CPU · 62.6 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/2 · plain | 🥇 **35,383**<br><sub>(139 MiB / 50.8% CPU · 57.5 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **23,801**<br><sub>(83 MiB / 43.3% CPU · 72.8 µs CPU/req)</sub> | **23,065**<br><sub>(127 MiB / 58.3% CPU · 101.1 µs CPU/req)</sub> | **30,801**<br><sub>(144 MiB / 49.9% CPU · 64.8 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/2 · TLS | 🥇 **55,982**<br><sub>(160 MiB / 47.2% CPU · 33.7 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **50,034**<br><sub>(83 MiB / 45.5% CPU · 36.4 µs CPU/req)</sub> | **47,541**<br><sub>(126 MiB / 51.6% CPU · 43.4 µs CPU/req)</sub> | **52,007**<br><sub>(149 MiB / 46.6% CPU · 35.8 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/3 · QUIC | 🥇 **28,251**<br><sub>(157 MiB / 53.2% CPU · 75.3 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **14,076**<br><sub>(131 MiB / 54.2% CPU · 153.9 µs CPU/req)</sub> | **25,130**<br><sub>(169 MiB / 49.5% CPU · 78.8 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | 🥇 **66,631**<br><sub>(147 MiB / 51.5% CPU · 30.9 µs CPU/req)</sub> | **51,209**<br><sub>(78 MiB / 45.1% CPU · 35.2 µs CPU/req)</sub> | **64,393**<br><sub>(64 MiB / 43.9% CPU · 27.3 µs CPU/req)</sub> | **52,573**<br><sub>(116 MiB / 53.9% CPU · 41.0 µs CPU/req)</sub> | **64,640**<br><sub>(125 MiB / 48% CPU · 29.7 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/1 · TLS | 🥇 **27,473**<br><sub>(147 MiB / 51.1% CPU · 74.3 µs CPU/req)</sub> | **22,255**<br><sub>(95 MiB / 45.8% CPU · 82.3 µs CPU/req)</sub> | **27,447**<br><sub>(72 MiB / 45% CPU · 65.6 µs CPU/req)</sub> | **18,255**<br><sub>(119 MiB / 59.7% CPU · 130.9 µs CPU/req)</sub> | **24,655**<br><sub>(133 MiB / 51.6% CPU · 83.7 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/2 · plain | 🥇 **97,777**<br><sub>(141 MiB / 41.4% CPU · 16.9 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **52,788**<br><sub>(66 MiB / 44.1% CPU · 33.4 µs CPU/req)</sub> | **40,830**<br><sub>(116 MiB / 58.5% CPU · 57.3 µs CPU/req)</sub> | **64,454**<br><sub>(132 MiB / 47.6% CPU · 29.5 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/2 · TLS | 🥇 **50,675**<br><sub>(161 MiB / 40.6% CPU · 32.0 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **27,240**<br><sub>(68 MiB / 46% CPU · 67.5 µs CPU/req)</sub> | **21,941**<br><sub>(117 MiB / 59.6% CPU · 108.6 µs CPU/req)</sub> | **35,612**<br><sub>(138 MiB / 47.3% CPU · 53.1 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/3 · QUIC | 🥇 **27,796**<br><sub>(175 MiB / 51.3% CPU · 73.8 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **10,914**<br><sub>(119 MiB / 57.5% CPU · 210.7 µs CPU/req)</sub> | **24,618**<br><sub>(157 MiB / 48.1% CPU · 78.2 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **63,488**<br><sub>(151 MiB / 51.1% CPU · 32.2 µs CPU/req)</sub> | **52,870**<br><sub>(102 MiB / 42.5% CPU · 32.2 µs CPU/req)</sub> | **61,105**<br><sub>(81 MiB / 44.2% CPU · 29.0 µs CPU/req)</sub> | **51,574**<br><sub>(128 MiB / 53.2% CPU · 41.2 µs CPU/req)</sub> | **59,373**<br><sub>(135 MiB / 48.2% CPU · 32.5 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/1 · TLS | 🥇 **37,822**<br><sub>(149 MiB / 49.6% CPU · 52.4 µs CPU/req)</sub> | **32,386**<br><sub>(107 MiB / 42.3% CPU · 52.2 µs CPU/req)</sub> | **36,644**<br><sub>(85 MiB / 44.1% CPU · 48.2 µs CPU/req)</sub> | **28,175**<br><sub>(127 MiB / 55.6% CPU · 78.9 µs CPU/req)</sub> | **34,444**<br><sub>(135 MiB / 49% CPU · 56.9 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | 🥇 **56,696**<br><sub>(163 MiB / 41.7% CPU · 29.4 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **27,590**<br><sub>(83 MiB / 45.8% CPU · 66.4 µs CPU/req)</sub> | **22,395**<br><sub>(126 MiB / 61% CPU · 108.9 µs CPU/req)</sub> | **36,819**<br><sub>(139 MiB / 48.7% CPU · 52.9 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **68,127**<br><sub>(166 MiB / 40.9% CPU · 24.0 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **40,761**<br><sub>(83 MiB / 44.7% CPU · 43.8 µs CPU/req)</sub> | **35,700**<br><sub>(125 MiB / 56.3% CPU · 63.1 µs CPU/req)</sub> | **47,554**<br><sub>(140 MiB / 46.6% CPU · 39.2 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/3 · QUIC | 🥇 **35,713**<br><sub>(195 MiB / 52.1% CPU · 58.4 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **15,252**<br><sub>(127 MiB / 54.1% CPU · 141.8 µs CPU/req)</sub> | **31,004**<br><sub>(170 MiB / 47.6% CPU · 61.3 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | **22,420**<br><sub>(153 MiB / 51% CPU · 91.1 µs CPU/req)</sub> | **28,315**<br><sub>(107 MiB / 27.5% CPU · 38.8 µs CPU/req)</sub> | 🥇 **30,241**<br><sub>(87 MiB / 33.9% CPU · 44.9 µs CPU/req)</sub> | **11,510**<br><sub>(133 MiB / 55.7% CPU · 193.6 µs CPU/req)</sub> | **17,810**<br><sub>(199 MiB / 50.6% CPU · 113.7 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | **36,115**<br><sub>(186 MiB / 52.5% CPU · 58.1 µs CPU/req)</sub> | **0**<br><sub>(peak 23,614 · 115 MiB / 32.6% CPU)</sub> | 🥇 **43,349**<br><sub>(88 MiB / 34.6% CPU · 31.9 µs CPU/req)</sub> | **20,573**<br><sub>(133 MiB / 48.4% CPU · 94.0 µs CPU/req)</sub> | **29,449**<br><sub>(215 MiB / 48.8% CPU · 66.3 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/2 · plain | **22,203**<br><sub>(151 MiB / 54.1% CPU · 97.5 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | 🥇 **22,310**<br><sub>(87 MiB / 35.4% CPU · 63.5 µs CPU/req)</sub> | **9,831**<br><sub>(132 MiB / 58.8% CPU · 239.1 µs CPU/req)</sub> | **18,350**<br><sub>(202 MiB / 49.6% CPU · 108.2 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | **21,458**<br><sub>(158 MiB / 52.9% CPU · 98.7 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | 🥇 **23,532**<br><sub>(88 MiB / 35.3% CPU · 60.0 µs CPU/req)</sub> | **11,409**<br><sub>(133 MiB / 56% CPU · 196.2 µs CPU/req)</sub> | **18,753**<br><sub>(208 MiB / 49.2% CPU · 105.0 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/3 · QUIC | 🥇 **16,206**<br><sub>(155 MiB / 50.4% CPU · 124.5 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **6,252**<br><sub>(131 MiB / 56% CPU · 357.9 µs CPU/req)</sub> | **13,221**<br><sub>(206 MiB / 48.5% CPU · 146.8 µs CPU/req)</sub> |

### MITM (TWP only)

Same Client×Origin wires with interception on (`compare-product` [38550284913](https://github.com/justcoding121/titanium-web-proxy/actions/runs/38550284913)). **Lite** = no-op handlers (unchanged-lite finish). **Full** = append-only header mutation (harness: one probe header each way; product: generic append-only relay via `MitmCompressedRelayHelper`). nginx/HAProxy/Envoy/YARP cannot MITM. **Lite÷Reverse** / **Full÷Reverse** vs bare reverse (**same job / comparison-group shard**). Completion gate: Lite ≥ **0.25×** and Full ≥ **0.25×** reverse sustain @ c=64 (median of 3 GHA runs); reverse TWP ÷ closest peer ≥ **0.40×**.

**v1 append-only relay (2026-08-27):** Pre-fix H2→H2 Full÷Reverse was **0.13–0.16×** ([32960766249](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32960766249)). Post-fix @ `df172718`: H2 plain→H2 plain Full **0.77–0.79×**, H3→H1 Full **0.91–0.93×**, all MITM arms ≥ **0.70×** on median of [33041445371](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33041445371), [33055267086](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33055267086), [33055272140](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33055272140).

**v2 drop-only + non-unique append (2026-08-27):** `MitmStaticRebuildHelper` rebuilds static HPACK/QPACK after 1–4 unique header drops; trailing non-unique appends stay on compressed relay. @ `41f4adee`: all MITM arms ≥ **0.70×** on GHA median ([33087088466](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087088466), [33087091622](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087091622), [33105885748](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33105885748) Linux; [33087085235](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087085235), [33087088466](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087088466), [33087091622](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087091622) Windows). H2 plain→H2 plain Full **0.77–0.79×** (Win) / **0.78×** (Lin).

| Client | Origin | Lite sustain | Full sustain | Lite÷Reverse | Full÷Reverse |
|---|---|---:|---:|---:|---:|
| HTTP/1 · plain | HTTP/1 · plain | **60,365**<br><sub>(90 MiB / 49.5% CPU · 32.8 µs CPU/req)</sub> | **57,485**<br><sub>(93 MiB / 48.3% CPU · 33.6 µs CPU/req)</sub> | **0.97×** | **0.92×** |
| HTTP/1 · plain | HTTP/1 · TLS | **36,812**<br><sub>(108 MiB / 49.2% CPU · 53.5 µs CPU/req)</sub> | **36,058**<br><sub>(111 MiB / 49.1% CPU · 54.5 µs CPU/req)</sub> | **1.01×** | **0.99×** |
| HTTP/1 · plain | HTTP/2 · plain | **39,591**<br><sub>(118 MiB / 51.8% CPU · 52.3 µs CPU/req)</sub> | **38,439**<br><sub>(127 MiB / 52.5% CPU · 54.7 µs CPU/req)</sub> | **0.98×** | **0.95×** |
| HTTP/1 · plain | HTTP/2 · TLS | **32,228**<br><sub>(151 MiB / 49.7% CPU · 61.6 µs CPU/req)</sub> | **31,780**<br><sub>(148 MiB / 49.4% CPU · 62.1 µs CPU/req)</sub> | **0.98×** | **0.96×** |
| HTTP/1 · plain | HTTP/3 · QUIC | **22,629**<br><sub>(138 MiB / 53.6% CPU · 94.7 µs CPU/req)</sub> | **22,446**<br><sub>(142 MiB / 54.4% CPU · 96.9 µs CPU/req)</sub> | **0.98×** | **0.97×** |
| HTTP/1 · TLS | HTTP/1 · plain | **23,462**<br><sub>(109 MiB / 50.4% CPU · 85.9 µs CPU/req)</sub> | **22,737**<br><sub>(116 MiB / 50.4% CPU · 88.6 µs CPU/req)</sub> | **0.99×** | **0.96×** |
| HTTP/1 · TLS | HTTP/1 · TLS | **34,291**<br><sub>(117 MiB / 46.3% CPU · 54.0 µs CPU/req)</sub> | **33,368**<br><sub>(116 MiB / 46.3% CPU · 55.5 µs CPU/req)</sub> | **0.99×** | **0.96×** |
| HTTP/1 · TLS | HTTP/2 · plain | **34,126**<br><sub>(139 MiB / 51.9% CPU · 60.8 µs CPU/req)</sub> | **34,064**<br><sub>(145 MiB / 52.6% CPU · 61.8 µs CPU/req)</sub> | **0.96×** | **0.96×** |
| HTTP/1 · TLS | HTTP/2 · TLS | **55,217**<br><sub>(159 MiB / 48.4% CPU · 35.1 µs CPU/req)</sub> | **54,393**<br><sub>(161 MiB / 49.1% CPU · 36.1 µs CPU/req)</sub> | **0.99×** | **0.97×** |
| HTTP/1 · TLS | HTTP/3 · QUIC | **28,264**<br><sub>(168 MiB / 54.2% CPU · 76.7 µs CPU/req)</sub> | **27,297**<br><sub>(168 MiB / 53.5% CPU · 78.4 µs CPU/req)</sub> | **1×** | **0.97×** |
| HTTP/2 · plain | HTTP/1 · plain | **67,101**<br><sub>(143 MiB / 52.7% CPU · 31.4 µs CPU/req)</sub> | **65,903**<br><sub>(147 MiB / 51.7% CPU · 31.4 µs CPU/req)</sub> | **1.01×** | **0.99×** |
| HTTP/2 · plain | HTTP/1 · TLS | **27,378**<br><sub>(149 MiB / 52.7% CPU · 77.0 µs CPU/req)</sub> | **26,341**<br><sub>(160 MiB / 52.5% CPU · 79.8 µs CPU/req)</sub> | **1×** | **0.96×** |
| HTTP/2 · plain | HTTP/2 · plain | **81,093**<br><sub>(140 MiB / 44.6% CPU · 22.0 µs CPU/req)</sub> | **77,506**<br><sub>(127 MiB / 44.6% CPU · 23.0 µs CPU/req)</sub> | **0.83×** | **0.79×** |
| HTTP/2 · plain | HTTP/2 · TLS | **43,380**<br><sub>(160 MiB / 43.7% CPU · 40.3 µs CPU/req)</sub> | **43,569**<br><sub>(158 MiB / 44.1% CPU · 40.5 µs CPU/req)</sub> | **0.86×** | **0.86×** |
| HTTP/2 · plain | HTTP/3 · QUIC | **27,890**<br><sub>(169 MiB / 51.6% CPU · 74.1 µs CPU/req)</sub> | **27,438**<br><sub>(174 MiB / 52.2% CPU · 76.1 µs CPU/req)</sub> | **1×** | **0.99×** |
| HTTP/2 · TLS | HTTP/1 · plain | **61,941**<br><sub>(156 MiB / 51.8% CPU · 33.4 µs CPU/req)</sub> | **60,314**<br><sub>(158 MiB / 51.2% CPU · 33.9 µs CPU/req)</sub> | **0.98×** | **0.95×** |
| HTTP/2 · TLS | HTTP/1 · TLS | **36,981**<br><sub>(165 MiB / 50.5% CPU · 54.7 µs CPU/req)</sub> | **36,086**<br><sub>(167 MiB / 49.9% CPU · 55.3 µs CPU/req)</sub> | **0.98×** | **0.95×** |
| HTTP/2 · TLS | HTTP/2 · plain | **49,336**<br><sub>(160 MiB / 46% CPU · 37.3 µs CPU/req)</sub> | **47,521**<br><sub>(159 MiB / 45.7% CPU · 38.5 µs CPU/req)</sub> | **0.87×** | **0.84×** |
| HTTP/2 · TLS | HTTP/2 · TLS | **59,698**<br><sub>(174 MiB / 43.6% CPU · 29.2 µs CPU/req)</sub> | **58,520**<br><sub>(170 MiB / 44.3% CPU · 30.3 µs CPU/req)</sub> | **0.88×** | **0.86×** |
| HTTP/2 · TLS | HTTP/3 · QUIC | **35,010**<br><sub>(206 MiB / 51.6% CPU · 58.9 µs CPU/req)</sub> | **35,001**<br><sub>(204 MiB / 51.6% CPU · 59.0 µs CPU/req)</sub> | **0.98×** | **0.98×** |
| HTTP/3 · QUIC | HTTP/1 · plain | **21,703**<br><sub>(157 MiB / 52.3% CPU · 96.4 µs CPU/req)</sub> | **21,592**<br><sub>(152 MiB / 52.4% CPU · 97.0 µs CPU/req)</sub> | **0.97×** | **0.96×** |
| HTTP/3 · QUIC | HTTP/1 · TLS | **32,339**<br><sub>(186 MiB / 52.6% CPU · 65.0 µs CPU/req)</sub> | **32,448**<br><sub>(181 MiB / 54.1% CPU · 66.7 µs CPU/req)</sub> | **0.9×** | **0.9×** |
| HTTP/3 · QUIC | HTTP/2 · plain | **19,964**<br><sub>(156 MiB / 54.3% CPU · 108.7 µs CPU/req)</sub> | **19,988**<br><sub>(154 MiB / 54.2% CPU · 108.5 µs CPU/req)</sub> | **0.9×** | **0.9×** |
| HTTP/3 · QUIC | HTTP/2 · TLS | **21,956**<br><sub>(157 MiB / 53.8% CPU · 97.9 µs CPU/req)</sub> | **21,753**<br><sub>(158 MiB / 53.7% CPU · 98.7 µs CPU/req)</sub> | **1.02×** | **1.01×** |
| HTTP/3 · QUIC | HTTP/3 · QUIC | **15,942**<br><sub>(157 MiB / 52.2% CPU · 131.0 µs CPU/req)</sub> | **15,894**<br><sub>(156 MiB / 52.6% CPU · 132.5 µs CPU/req)</sub> | **0.98×** | **0.98×** |

## macOS — Titanium vs nginx vs HAProxy vs Envoy vs YARP

### Reverse

Median of **3 repeats** on `macos-15` (Apple Silicon M1, 3-core / 7 GB). Bare reverse 5×5 @ `88187363` — `compare-product` [38550284913](https://github.com/justcoding121/titanium-web-proxy/actions/runs/38550284913). Warmup 2s / measure 8s; concurrency 8, 16, 32, 64. Prefer TWP÷peer ratios over absolute RPS. **RPS cells** include median RSS / CPU at the peak-RPS step as `<br><sub>(MiB / CPU%)</sub>`. The RPS workflow installs Homebrew nginx (`http_v3_module`), Homebrew HAProxy with `USE_QUIC` (3.2 source fallback), Envoy (Homebrew bottle or pinned darwin-arm64 1.36.7), Homebrew `libmsquic` (+ `DYLD_*`), and YARP. Use the pinned `macos-15` label, not `macos-latest`. Product 5×5 is **~56-byte JSON keep-alive GET**; H2/H3 same-protocol cells are mostly header work with a tiny body (Titanium best case) — see [Why this comparison is fair](#user-content-why-this-comparison-is-fair).

| Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---:|---:|---:|---:|---:|
| HTTP/1 · plain | HTTP/1 · plain | **6,242**<br><sub>(114 MiB / 10.4% CPU · 50.2 µs CPU/req)</sub> | **15,858**<br><sub>(68 MiB / 13.3% CPU · 25.2 µs CPU/req)</sub> | 🥇 **20,000**<br><sub>(89 MiB / 20.4% CPU · 30.6 µs CPU/req)</sub> | **10,654**<br><sub>(112 MiB / 28.2% CPU · 79.5 µs CPU/req)</sub> | **7,365**<br><sub>(180 MiB / 11.6% CPU · 47.1 µs CPU/req)</sub> |
| HTTP/1 · plain | HTTP/1 · TLS | **9,894**<br><sub>(130 MiB / 17.3% CPU · 52.4 µs CPU/req)</sub> | **9,745**<br><sub>(79 MiB / 12.9% CPU · 39.8 µs CPU/req)</sub> | **6,932**<br><sub>(95 MiB / 10.4% CPU · 44.8 µs CPU/req)</sub> | 🥇 **12,908**<br><sub>(113 MiB / 27% CPU · 62.7 µs CPU/req)</sub> | **4,625**<br><sub>(197 MiB / 9.4% CPU · 61.0 µs CPU/req)</sub> |
| HTTP/1 · plain | HTTP/2 · plain | **10,580**<br><sub>(122 MiB / 11.3% CPU · 31.9 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **7,369**<br><sub>(95 MiB / 10.6% CPU · 43.2 µs CPU/req)</sub> | 🥇 **18,977**<br><sub>(111 MiB / 36.4% CPU · 57.6 µs CPU/req)</sub> | **8,702**<br><sub>(214 MiB / 11.1% CPU · 38.3 µs CPU/req)</sub> |
| HTTP/1 · plain | HTTP/2 · TLS | **11,027**<br><sub>(168 MiB / 18.4% CPU · 50.0 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | 🥇 **12,981**<br><sub>(98 MiB / 21.9% CPU · 50.6 µs CPU/req)</sub> | **5,359**<br><sub>(112 MiB / 14.7% CPU · 82.2 µs CPU/req)</sub> | **7,334**<br><sub>(178 MiB / 9.5% CPU · 38.7 µs CPU/req)</sub> |
| HTTP/1 · plain | HTTP/3 · QUIC | **4,000**<br><sub>(132 MiB / 11.6% CPU · 86.9 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **4,062**<br><sub>(116 MiB / 15.9% CPU · 117.1 µs CPU/req)</sub> | 🥇 **7,923**<br><sub>(165 MiB / 14.6% CPU · 55.4 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/1 · plain | **19,096**<br><sub>(130 MiB / 26.2% CPU · 41.2 µs CPU/req)</sub> | **12,818**<br><sub>(85 MiB / 16.2% CPU · 38.0 µs CPU/req)</sub> | 🥇 **26,805**<br><sub>(112 MiB / 24.8% CPU · 27.7 µs CPU/req)</sub> | **7,271**<br><sub>(121 MiB / 16.9% CPU · 69.8 µs CPU/req)</sub> | **10,073**<br><sub>(156 MiB / 13.5% CPU · 40.3 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/1 · TLS | **4,345**<br><sub>(158 MiB / 16.9% CPU · 116.6 µs CPU/req)</sub> | **5,987**<br><sub>(89 MiB / 12% CPU · 60.3 µs CPU/req)</sub> | 🥇 **14,239**<br><sub>(110 MiB / 23.6% CPU · 49.6 µs CPU/req)</sub> | **3,264**<br><sub>(peak 4,417 · 123 MiB / 23.4% CPU · 214.8 µs CPU/req)</sub> | **3,300**<br><sub>(peak 3,413 · 199 MiB / 15.2% CPU · 138.6 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/2 · plain | 🥇 **11,140**<br><sub>(173 MiB / 19.5% CPU · 52.5 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **6,351**<br><sub>(111 MiB / 13.5% CPU · 63.9 µs CPU/req)</sub> | **5,762**<br><sub>(121 MiB / 18% CPU · 93.5 µs CPU/req)</sub> | **5,649**<br><sub>(237 MiB / 8.5% CPU · 45.2 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/2 · TLS | **6,537**<br><sub>(206 MiB / 12.7% CPU · 58.5 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | 🥇 **11,999**<br><sub>(111 MiB / 20.5% CPU · 51.3 µs CPU/req)</sub> | **9,048**<br><sub>(121 MiB / 28.1% CPU · 93.2 µs CPU/req)</sub> | **9,667**<br><sub>(204 MiB / 14.5% CPU · 45.0 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/3 · QUIC | 🥇 **8,874**<br><sub>(166 MiB / 23.5% CPU · 79.4 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **6,706**<br><sub>(126 MiB / 33.6% CPU · 150.3 µs CPU/req)</sub> | **3,378**<br><sub>(172 MiB / 10.8% CPU · 95.5 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | 🥇 **16,038**<br><sub>(220 MiB / 20% CPU · 37.3 µs CPU/req)</sub> | **6,548**<br><sub>(70 MiB / 7.4% CPU · 34.0 µs CPU/req)</sub> | **14,749**<br><sub>(96 MiB / 15.6% CPU · 31.7 µs CPU/req)</sub> | **12,293**<br><sub>(111 MiB / 33.8% CPU · 82.4 µs CPU/req)</sub> | **5,158**<br><sub>(182 MiB / 8.5% CPU · 49.7 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/1 · TLS | **6,776**<br><sub>(242 MiB / 14% CPU · 62.0 µs CPU/req)</sub> | **15,402**<br><sub>(80 MiB / 17.4% CPU · 33.9 µs CPU/req)</sub> | 🥇 **17,906**<br><sub>(105 MiB / 25.3% CPU · 42.3 µs CPU/req)</sub> | **14,364**<br><sub>(114 MiB / 32% CPU · 66.9 µs CPU/req)</sub> | **9,519**<br><sub>(166 MiB / 21.9% CPU · 69.0 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/2 · plain | **10,070**<br><sub>(228 MiB / 4.4% CPU · 13.2 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | 🥇 **32,271**<br><sub>(121 MiB / 29.4% CPU · 27.4 µs CPU/req)</sub> | **12,111**<br><sub>(110 MiB / 14% CPU · 34.8 µs CPU/req)</sub> | **13,339**<br><sub>(169 MiB / 8.3% CPU · 18.6 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/2 · TLS | 🥇 **20,563**<br><sub>(251 MiB / 16.9% CPU · 24.6 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **16,800**<br><sub>(102 MiB / 24.1% CPU · 43.1 µs CPU/req)</sub> | **6,953**<br><sub>(112 MiB / 15.2% CPU · 65.6 µs CPU/req)</sub> | **9,670**<br><sub>(222 MiB / 10.3% CPU · 31.9 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/3 · QUIC | **8,452**<br><sub>(208 MiB / 16.1% CPU · 57.2 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **4,598**<br><sub>(115 MiB / 17.3% CPU · 112.6 µs CPU/req)</sub> | 🥇 **10,963**<br><sub>(167 MiB / 22.7% CPU · 62.0 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | **11,200**<br><sub>(213 MiB / 16.4% CPU · 43.8 µs CPU/req)</sub> | **7,670**<br><sub>(83 MiB / 9.1% CPU · 35.6 µs CPU/req)</sub> | **14,900**<br><sub>(107 MiB / 21.3% CPU · 42.9 µs CPU/req)</sub> | **6,002**<br><sub>(122 MiB / 18.8% CPU · 93.8 µs CPU/req)</sub> | 🥇 **15,402**<br><sub>(159 MiB / 18.5% CPU · 36.1 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/1 · TLS | **9,887**<br><sub>(200 MiB / 15.8% CPU · 47.9 µs CPU/req)</sub> | 🥇 **10,835**<br><sub>(89 MiB / 12.6% CPU · 35.0 µs CPU/req)</sub> | **7,766**<br><sub>(111 MiB / 17% CPU · 65.8 µs CPU/req)</sub> | **9,168**<br><sub>(123 MiB / 25.1% CPU · 82.3 µs CPU/req)</sub> | **3,626**<br><sub>(199 MiB / 9.9% CPU · 82.0 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | **7,690**<br><sub>(251 MiB / 6.9% CPU · 26.8 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **17,074**<br><sub>(114 MiB / 28% CPU · 49.1 µs CPU/req)</sub> | **13,606**<br><sub>(121 MiB / 20.1% CPU · 44.4 µs CPU/req)</sub> | 🥇 **21,647**<br><sub>(260 MiB / 15.4% CPU · 21.3 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | **5,276**<br><sub>(250 MiB / 6.3% CPU · 35.7 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | 🥇 **11,851**<br><sub>(112 MiB / 23.2% CPU · 58.8 µs CPU/req)</sub> | **8,169**<br><sub>(120 MiB / 14.9% CPU · 54.9 µs CPU/req)</sub> | **7,293**<br><sub>(257 MiB / 8% CPU · 32.8 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/3 · QUIC | **10,936**<br><sub>(203 MiB / 23.2% CPU · 63.8 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **18,630**<br><sub>(123 MiB / 42% CPU · 67.6 µs CPU/req)</sub> | 🥇 **23,022**<br><sub>(175 MiB / 37.1% CPU · 48.4 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **5,777**<br><sub>(145 MiB / 23.5% CPU · 121.9 µs CPU/req)</sub> | **5,501**<br><sub>(86 MiB / 6.6% CPU · 35.8 µs CPU/req)</sub> | **4,099**<br><sub>(105 MiB / 10.7% CPU · 78.0 µs CPU/req)</sub> | **5,457**<br><sub>(126 MiB / 33.4% CPU · 183.5 µs CPU/req)</sub> | **5,014**<br><sub>(249 MiB / 16.9% CPU · 100.9 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | **6,138**<br><sub>(191 MiB / 25.5% CPU · 124.5 µs CPU/req)</sub> | **4,947**<br><sub>(91 MiB / 7.8% CPU · 47.0 µs CPU/req)</sub> | 🥇 **10,604**<br><sub>(108 MiB / 20.3% CPU · 57.4 µs CPU/req)</sub> | **5,362**<br><sub>(127 MiB / 39.1% CPU · 219.0 µs CPU/req)</sub> | **2,660**<br><sub>(278 MiB / 13.9% CPU · 156.6 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/2 · plain | **3,671**<br><sub>(141 MiB / 10.9% CPU · 89.0 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | 🥇 **14,293**<br><sub>(107 MiB / 14.9% CPU · 31.3 µs CPU/req)</sub> | **11,338**<br><sub>(122 MiB / 39.7% CPU · 105.1 µs CPU/req)</sub> | **9,863**<br><sub>(259 MiB / 21.2% CPU · 64.5 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | 🥇 **8,545**<br><sub>(143 MiB / 22.3% CPU · 78.3 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **3,732**<br><sub>(108 MiB / 7.2% CPU · 57.5 µs CPU/req)</sub> | **4,660**<br><sub>(124 MiB / 24.9% CPU · 160.4 µs CPU/req)</sub> | **7,336**<br><sub>(281 MiB / 19.7% CPU · 80.5 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/3 · QUIC | **6,443**<br><sub>(138 MiB / 24.6% CPU · 114.4 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **4,101**<br><sub>(123 MiB / 34% CPU · 248.7 µs CPU/req)</sub> | 🥇 **7,204**<br><sub>(258 MiB / 22.5% CPU · 93.6 µs CPU/req)</sub> |

## Editions (CLI / Plus / Intercept)

**Note:** `twp-reverse-http1` and other library rows use Core with **probe-tuned** settings (no logging, no Via header, probe-warmed certs). Edition rows use `titanium run -c twp.yaml` **product defaults** — prefer the ÷baseline ratio column over absolute RPS. Inspector GUI is not spawnable in the harness; session-path overhead is `twp-cli-intercept-http1` (route `RequestHeaderSet` transform). Pre-origin Plus middleware (CIDR/WAF/JWT/rate-limit/cache) runs on H1 terminate-lite without `SessionEventArgs`; a cache hit skips the origin. JWT caches successful bearer validations. CORS only adds response headers on the way out, so it stays on terminate-lite. Circuit breaker and idempotent retry have to see the request or the status code, so they stay on the session path and should land near the intercept row. Maintainer gate thresholds live under [Maintainer notes](#user-content-maintainer-notes).

Median of **3** repeats on `ubuntu-latest` @ `88187363` (Actions [38550348130](https://github.com/justcoding121/titanium-web-proxy/actions/runs/38550348130)). Warmup 2s / measure 8s; concurrency 8–64; sustain = median peak RPS among SLO-pass steps @ **c=64**. **RPS cells** show sustain; `<sub>` holds peak (when higher) plus `(MiB / CPU%)`. The Gate column is that script's floor.

| Arm | Linux | Lin÷ | Gate |
|---|---:|---:|---|
| `twp-cli-reverse-http1` vs library | **64,175**<br><sub>(170 MiB / 49.3% CPU · 30.7 µs CPU/req)</sub> | **1.04×** | ≥ **0.50×** |
| `twp-cli-reverse-http1-tls` vs library TLS | **37,304**<br><sub>(203 MiB / 49.4% CPU · 53.0 µs CPU/req)</sub> | **1.00×** | ≥ **0.50×** |
| `twp-cli-reverse-http1-route` vs CLI | **32,919**<br><sub>(169 MiB / 50.5% CPU · 61.4 µs CPU/req)</sub> | **0.99×** | ≥ **0.50×** |
| `twp-cli-plus-base-http1` vs CLI | **33,573**<br><sub>(173 MiB / 49.8% CPU · 59.3 µs CPU/req)</sub> | **0.99×** | ≥ **0.50×** |
| `twp-cli-plus-cache-http1` (cold) vs CLI | **56,233**<br><sub>(172 MiB / 47.5% CPU · 33.8 µs CPU/req)</sub> | **1.72×** | ≥ **0.50×** |
| `twp-cli-intercept-http1` vs CLI | **24,552**<br><sub>(177 MiB / 52.9% CPU · 86.2 µs CPU/req)</sub> | **0.74×** | ≥ **0.50×** |
| `twp-cli-plus-waf-http1` vs CLI | **32,532**<br><sub>(171 MiB / 50.6% CPU · 62.2 µs CPU/req)</sub> | **0.97×** | ≥ **0.50×** |
| `twp-cli-plus-cidr-http1` vs CLI | **32,877**<br><sub>(171 MiB / 50.7% CPU · 61.6 µs CPU/req)</sub> | **1.01×** | ≥ **0.50×** |
| `twp-cli-plus-jwt-http1` vs CLI | **44,556**<br><sub>(193 MiB / 49.5% CPU · 44.4 µs CPU/req)</sub> | **0.93×** | ≥ **0.50×** |
| `twp-cli-plus-ratelimit-http1` vs CLI | **32,368**<br><sub>(173 MiB / 51.0% CPU · 63.0 µs CPU/req)</sub> | **1.00×** | ≥ **0.50×** |
| `twp-cli-plus-resilience-http1` vs CLI | **46,996**<br><sub>(184 MiB / 49.0% CPU · 41.7 µs CPU/req)</sub> | **0.99×** | ≥ **0.50×** |
| `twp-cli-plus-discovery-file-http1` vs CLI | **33,786**<br><sub>(173 MiB / 50.3% CPU · 59.6 µs CPU/req)</sub> | **1.02×** | ≥ **0.50×** |
| `twp-cli-plus-metrics-scrape-http1` vs CLI | **33,347**<br><sub>(174 MiB / 49.8% CPU · 59.7 µs CPU/req)</sub> | **0.99×** | ≥ **0.50×** |
| `twp-cli-plus-cache-hit-http1` vs cache cold | **57,046**<br><sub>(172 MiB / 48.5% CPU · 34.0 µs CPU/req)</sub> | **1.01×** | ≥ **0.50×** |
| `twp-cli-plus-cors-http1` vs CLI | **30,799**<br><sub>(173 MiB / 50.4% CPU · 65.4 µs CPU/req)</sub> | **0.92×** | ≥ **0.50×** |
| `twp-cli-plus-circuit-http1` vs CLI | **26,008**<br><sub>(180 MiB / 51.8% CPU · 79.7 µs CPU/req)</sub> | **0.78×** | ≥ **0.50×** |
| `twp-cli-plus-retry-http1` vs CLI | **27,089**<br><sub>(180 MiB / 51.2% CPU · 75.6 µs CPU/req)</sub> | **0.79×** | ≥ **0.50×** |
| `twp-cli-static-http1` vs CLI | **54,140**<br><sub>(169 MiB / 50.7% CPU · 37.5 µs CPU/req)</sub> | **1.66×** | ≥ **0.50×** |
| `twp-cli-logging-http1` vs CLI | **55,368**<br><sub>(235 MiB / 48.8% CPU · 35.3 µs CPU/req)</sub> | **1.00×** | ≥ **0.50×** |
| `twp-cli-lb-leasttime-http1` vs route | **45,938**<br><sub>(194 MiB / 50.3% CPU · 43.8 µs CPU/req)</sub> | **0.96×** | ≥ **0.50×** |
| `twp-cli-dialect-twp-http1` vs CLI | **65,370**<br><sub>(163 MiB / 49.1% CPU · 30.0 µs CPU/req)</sub> | **1.01×** | ≥ **0.50×** |

`validate-edition-gates.ps1` floors are **0.50×**. Each ÷ column uses the two arms from the same job. Circuit breaker and idempotent retry stay on the session path, so a ratio near intercept is expected. Laptop smoke ratios stay on [Performance Local Lab — Editions](Performance-Local-Lab#user-content-editions-cli--plus-stress).

## Heavier reverse workloads

Same runners and harness as the tiny-GET tables, but with larger bodies, POST, lossy links, TLS cost, and architecture-sensitive paths (slow consumer / early response / duplex). **PUT with the same body is the same proxy work as POST; DELETE with no body matches GET** — only POST is published. Bodies/POST/lossy stay **half-duplex**. Laptop numbers are on [Performance Local Lab](Performance-Local-Lab#user-content-architecture-sensitive). How maintainers refresh these tables (modes, shards, paste scripts) is under [Maintainer notes](#user-content-maintainer-notes). **Larger-body check:** 64 / 256 KiB H2 TLS→H2 TLS is where body copy dominates headers — ratios vs YARP are in the tables below (@ `88187363`), unlike the tiny-GET H2↔H2 medals above. Those cells also show goodput (MiB/s) and µs CPU/req, and the medal on those rows follows goodput.

Lossy link = **userspace** delay/drop shim (not kernel `netem`): TCP gets per-buffer delay + occasional whole-connection stalls (honest head-of-line for multiplexed HTTP/2); UDP is **loss% only** (no per-datagram delay) + drops (QUIC / MsQuic-safe). Lossy tables publish HTTP/1, HTTP/2, and HTTP/3.

### Windows — heavier reverse GET (64 KiB / 256 KiB)

Median of **3** repeats on `windows-latest` @ `88187363`. Source: Actions [38550291997](https://github.com/justcoding121/titanium-web-proxy/actions/runs/38550291997) (`compare-bodies`). Warmup 2s / measure 8s. **RPS cells** include `(MiB / CPU%)` footprints.

*Not possible:* **HAProxy** and **Envoy** columns are omitted (no official Windows port).




















| Body | Client | Origin | TWP | nginx | YARP |
|---|---|---|---:|---:|---:|
| 64 KiB | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **9,224**<br><sub>(91 MiB / 45.5% CPU · 577 MiB/s · 197.5 µs CPU/req)</sub> | **622**<br><sub>(142 MiB / 24.8% CPU · 39 MiB/s · 1590.9 µs CPU/req)</sub> | **8,011**<br><sub>(138 MiB / 45.9% CPU · 501 MiB/s · 229.1 µs CPU/req)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **8,238**<br><sub>(193 MiB / 49.8% CPU · 515 MiB/s · 241.9 µs CPU/req)</sub> | **578**<br><sub>(142 MiB / 24.8% CPU · 36 MiB/s · 1719.5 µs CPU/req)</sub> | **8,024**<br><sub>(127 MiB / 53.8% CPU · 502 MiB/s · 268.4 µs CPU/req)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **4,878**<br><sub>(141 MiB / 44.8% CPU · 305 MiB/s · 367.6 µs CPU/req)</sub> | *Not possible (no QUIC)* | **2,504**<br><sub>(183 MiB / 51.9% CPU · 156 MiB/s · 828.9 µs CPU/req)</sub> |
| 256 KiB | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **3,063**<br><sub>(138 MiB / 40.5% CPU · 766 MiB/s · 529.6 µs CPU/req)</sub> | **195**<br><sub>(142 MiB / 24.3% CPU · 49 MiB/s · 4982.1 µs CPU/req)</sub> | **2,736**<br><sub>(136 MiB / 43.9% CPU · 684 MiB/s · 642.0 µs CPU/req)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/1 · plain | **2,406**<br><sub>(185 MiB / 50.5% CPU · 601 MiB/s · 839.3 µs CPU/req)</sub> | **154**<br><sub>(143 MiB / 24.7% CPU · 38 MiB/s · 6426.0 µs CPU/req)</sub> | 🥇 **2,696**<br><sub>(132 MiB / 47.0% CPU · 674 MiB/s · 697.3 µs CPU/req)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **339**<br><sub>(90 MiB / 33.8% CPU · 85 MiB/s · 3990.6 µs CPU/req)</sub> | *Not possible (no QUIC)* | **146**<br><sub>(125 MiB / 49.8% CPU · 36 MiB/s · 13668.0 µs CPU/req)</sub> |
| 64 KiB | HTTP/2 · plain | HTTP/1 · plain | 🥇 **12,817**<br><sub>(168 MiB / 43.1% CPU · 801 MiB/s · 134.4 µs CPU/req)</sub> | **2,037**<br><sub>(127 MiB / 24.8% CPU · 127 MiB/s · 486.4 µs CPU/req)</sub> | **11,420**<br><sub>(114 MiB / 46.1% CPU · 714 MiB/s · 161.5 µs CPU/req)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/2 · plain | 🥇 **8,152**<br><sub>(170 MiB / 45.8% CPU · 510 MiB/s · 224.8 µs CPU/req)</sub> | *Not possible* | **7,312**<br><sub>(140 MiB / 53.3% CPU · 457 MiB/s · 291.4 µs CPU/req)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **6,073**<br><sub>(177 MiB / 41.1% CPU · 380 MiB/s · 270.5 µs CPU/req)</sub> | *Not possible* | **5,128**<br><sub>(144 MiB / 49.3% CPU · 320 MiB/s · 384.9 µs CPU/req)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/2 · TLS | 🥇 **3,838**<br><sub>(138 MiB / 42.8% CPU · 240 MiB/s · 446.2 µs CPU/req)</sub> | *Not possible* | **2,093**<br><sub>(182 MiB / 50.3% CPU · 131 MiB/s · 962.0 µs CPU/req)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **3,439**<br><sub>(145 MiB / 43.5% CPU · 215 MiB/s · 505.4 µs CPU/req)</sub> | *Not possible (no QUIC)* | **1,856**<br><sub>(187 MiB / 52.6% CPU · 116 MiB/s · 1133.6 µs CPU/req)</sub> |
| 256 KiB | HTTP/2 · plain | HTTP/1 · plain | 🥇 **4,263**<br><sub>(180 MiB / 35.5% CPU · 1,066 MiB/s · 332.6 µs CPU/req)</sub> | **772**<br><sub>(127 MiB / 24.6% CPU · 193 MiB/s · 1276.5 µs CPU/req)</sub> | **4,002**<br><sub>(116 MiB / 39.6% CPU · 1,001 MiB/s · 396.1 µs CPU/req)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/2 · plain | **2,186**<br><sub>(203 MiB / 46.6% CPU · 547 MiB/s · 852.4 µs CPU/req)</sub> | *Not possible* | 🥇 **2,221**<br><sub>(169 MiB / 46.6% CPU · 555 MiB/s · 839.8 µs CPU/req)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **1,673**<br><sub>(201 MiB / 42.2% CPU · 418 MiB/s · 1008.1 µs CPU/req)</sub> | *Not possible* | **1,410**<br><sub>(148 MiB / 45.9% CPU · 352 MiB/s · 1302.3 µs CPU/req)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/2 · TLS | **400**<br><sub>(139 MiB / 49.1% CPU · 100 MiB/s · 4903.5 µs CPU/req)</sub> | *Not possible* | 🥇 **1,284**<br><sub>(182 MiB / 48.6% CPU · 321 MiB/s · 1512.5 µs CPU/req)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/1 · TLS | **111**<br><sub>(peak 120 · 103 MiB / 42.7% CPU · 28 MiB/s · 15342.3 µs CPU/req)</sub> | *Not possible (no QUIC)* | 🥇 **239**<br><sub>(162 MiB / 50.1% CPU · 60 MiB/s · 8390.4 µs CPU/req)</sub> |

nginx/Windows collapses on large reverse bodies in this harness; treat as same-OS only. Prefer the ratio columns; absolute RPS swings by VM.

### Linux — heavier reverse GET (64 KiB / 256 KiB)

Median of **3** repeats @ `88187363`. Source: Actions [38550291997](https://github.com/justcoding121/titanium-web-proxy/actions/runs/38550291997) (`compare-bodies`). Warmup 2s / measure 8s.


| Body | Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---|---:|---:|---:|---:|---:|
| 64 KiB | HTTP/1 · TLS | HTTP/1 · plain | **8,159**<br><sub>(127 MiB / 44.0% CPU · 510 MiB/s · 215.7 µs CPU/req)</sub> | **5,438**<br><sub>(98 MiB / 51.7% CPU · 340 MiB/s · 380.1 µs CPU/req)</sub> | 🥇 **8,202**<br><sub>(87 MiB / 41.0% CPU · 513 MiB/s · 200.2 µs CPU/req)</sub> | **7,105**<br><sub>(131 MiB / 45.6% CPU · 444 MiB/s · 256.9 µs CPU/req)</sub> | **6,344**<br><sub>(160 MiB / 48.5% CPU · 397 MiB/s · 305.8 µs CPU/req)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/1 · plain | **14,150**<br><sub>(240 MiB / 42.9% CPU · 884 MiB/s · 121.2 µs CPU/req)</sub> | **8,886**<br><sub>(102 MiB / 49.1% CPU · 555 MiB/s · 220.8 µs CPU/req)</sub> | **12,198**<br><sub>(88 MiB / 46.0% CPU · 762 MiB/s · 150.7 µs CPU/req)</sub> | 🥇 **15,030**<br><sub>(134 MiB / 44.2% CPU · 939 MiB/s · 117.8 µs CPU/req)</sub> | **12,355**<br><sub>(159 MiB / 45.9% CPU · 772 MiB/s · 148.7 µs CPU/req)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/1 · plain | **8,368**<br><sub>(214 MiB / 46.0% CPU · 523 MiB/s · 220.0 µs CPU/req)</sub> | **3,214**<br><sub>(117 MiB / 22.1% CPU · 201 MiB/s · 275.2 µs CPU/req)</sub> | 🥇 **8,604**<br><sub>(95 MiB / 36.4% CPU · 538 MiB/s · 169.0 µs CPU/req)</sub> | **6,424**<br><sub>(138 MiB / 49.0% CPU · 402 MiB/s · 305.0 µs CPU/req)</sub> | **5,083**<br><sub>(239 MiB / 51.2% CPU · 318 MiB/s · 403.2 µs CPU/req)</sub> |
| 256 KiB | HTTP/1 · TLS | HTTP/1 · plain | **2,633**<br><sub>(133 MiB / 36.9% CPU · 658 MiB/s · 561.0 µs CPU/req)</sub> | **1,688**<br><sub>(99 MiB / 53.1% CPU · 422 MiB/s · 1258.1 µs CPU/req)</sub> | 🥇 **2,839**<br><sub>(87 MiB / 32.7% CPU · 710 MiB/s · 460.5 µs CPU/req)</sub> | **2,632**<br><sub>(146 MiB / 33.8% CPU · 658 MiB/s · 514.2 µs CPU/req)</sub> | **2,113**<br><sub>(170 MiB / 45.9% CPU · 528 MiB/s · 869.5 µs CPU/req)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/1 · plain | **2,064**<br><sub>(201 MiB / 41.2% CPU · 516 MiB/s · 799.0 µs CPU/req)</sub> | **1,482**<br><sub>(100 MiB / 49.9% CPU · 371 MiB/s · 1346.1 µs CPU/req)</sub> | **2,081**<br><sub>(85 MiB / 41.1% CPU · 520 MiB/s · 789.5 µs CPU/req)</sub> | 🥇 **2,416**<br><sub>(151 MiB / 36.0% CPU · 604 MiB/s · 596.0 µs CPU/req)</sub> | **1,854**<br><sub>(168 MiB / 46.1% CPU · 464 MiB/s · 995.3 µs CPU/req)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/1 · plain | **1,398**<br><sub>(176 MiB / 52.4% CPU · 349 MiB/s · 1499.2 µs CPU/req)</sub> | **702**<br><sub>(113 MiB / 37.4% CPU · 175 MiB/s · 2133.7 µs CPU/req)</sub> | 🥇 **1,598**<br><sub>(98 MiB / 36.6% CPU · 400 MiB/s · 917.0 µs CPU/req)</sub> | **1,352**<br><sub>(peak 1,381 · 143 MiB / 43.4% CPU · 338 MiB/s · 1282.6 µs CPU/req)</sub> | **1,202**<br><sub>(226 MiB / 52.8% CPU · 301 MiB/s · 1758.1 µs CPU/req)</sub> |
| 64 KiB | HTTP/2 · plain | HTTP/1 · plain | **14,662**<br><sub>(226 MiB / 43.6% CPU · 916 MiB/s · 118.9 µs CPU/req)</sub> | **11,305**<br><sub>(79 MiB / 43.8% CPU · 707 MiB/s · 154.9 µs CPU/req)</sub> | 🥇 **14,980**<br><sub>(74 MiB / 41.7% CPU · 936 MiB/s · 111.3 µs CPU/req)</sub> | **14,696**<br><sub>(124 MiB / 45.8% CPU · 919 MiB/s · 124.7 µs CPU/req)</sub> | **13,813**<br><sub>(142 MiB / 45.1% CPU · 863 MiB/s · 130.6 µs CPU/req)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/2 · plain | 🥇 **10,906**<br><sub>(211 MiB / 37.9% CPU · 682 MiB/s · 139.0 µs CPU/req)</sub> | *Not possible* | **8,244**<br><sub>(91 MiB / 44.5% CPU · 515 MiB/s · 215.9 µs CPU/req)</sub> | **9,658**<br><sub>(136 MiB / 44.1% CPU · 604 MiB/s · 182.6 µs CPU/req)</sub> | **8,041**<br><sub>(206 MiB / 48.5% CPU · 503 MiB/s · 241.1 µs CPU/req)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **4,949**<br><sub>(211 MiB / 41.7% CPU · 309 MiB/s · 337.2 µs CPU/req)</sub> | *Not possible* | **4,157**<br><sub>(89 MiB / 44.7% CPU · 260 MiB/s · 430.1 µs CPU/req)</sub> | **4,555**<br><sub>(141 MiB / 43.5% CPU · 285 MiB/s · 382.2 µs CPU/req)</sub> | **3,546**<br><sub>(172 MiB / 46.7% CPU · 222 MiB/s · 526.6 µs CPU/req)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/2 · TLS | **3,447**<br><sub>(181 MiB / 46.1% CPU · 215 MiB/s · 534.7 µs CPU/req)</sub> | *Not possible* | 🥇 **3,722**<br><sub>(97 MiB / 41.1% CPU · 233 MiB/s · 442.3 µs CPU/req)</sub> | **2,848**<br><sub>(146 MiB / 49.7% CPU · 178 MiB/s · 698.6 µs CPU/req)</sub> | **2,488**<br><sub>(245 MiB / 50.4% CPU · 155 MiB/s · 810.8 µs CPU/req)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **3,788**<br><sub>(213 MiB / 46.8% CPU · 237 MiB/s · 494.1 µs CPU/req)</sub> | **2,326**<br><sub>(118 MiB / 39.2% CPU · 145 MiB/s · 673.5 µs CPU/req)</sub> | **3,772**<br><sub>(97 MiB / 40.9% CPU · 236 MiB/s · 433.7 µs CPU/req)</sub> | **3,194**<br><sub>(139 MiB / 48.8% CPU · 200 MiB/s · 610.6 µs CPU/req)</sub> | **2,807**<br><sub>(258 MiB / 51.3% CPU · 175 MiB/s · 730.6 µs CPU/req)</sub> |
| 256 KiB | HTTP/2 · plain | HTTP/1 · plain | **4,636**<br><sub>(196 MiB / 31.6% CPU · 1,159 MiB/s · 272.5 µs CPU/req)</sub> | **3,377**<br><sub>(79 MiB / 47.7% CPU · 844 MiB/s · 564.6 µs CPU/req)</sub> | **4,947**<br><sub>(71 MiB / 30.6% CPU · 1,237 MiB/s · 247.8 µs CPU/req)</sub> | 🥇 **5,253**<br><sub>(146 MiB / 29.8% CPU · 1,313 MiB/s · 226.7 µs CPU/req)</sub> | **4,679**<br><sub>(149 MiB / 34.6% CPU · 1,170 MiB/s · 296.2 µs CPU/req)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/2 · plain | 🥇 **2,067**<br><sub>(239 MiB / 37.6% CPU · 517 MiB/s · 728.5 µs CPU/req)</sub> | *Not possible* | **1,919**<br><sub>(99 MiB / 38.9% CPU · 480 MiB/s · 809.9 µs CPU/req)</sub> | **1,995**<br><sub>(144 MiB / 37.1% CPU · 499 MiB/s · 744.1 µs CPU/req)</sub> | **1,504**<br><sub>(204 MiB / 45.1% CPU · 376 MiB/s · 1200.0 µs CPU/req)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **1,950**<br><sub>(249 MiB / 37.7% CPU · 487 MiB/s · 773.5 µs CPU/req)</sub> | *Not possible* | **1,890**<br><sub>(98 MiB / 39.3% CPU · 472 MiB/s · 832.1 µs CPU/req)</sub> | **1,940**<br><sub>(146 MiB / 38.6% CPU · 485 MiB/s · 794.9 µs CPU/req)</sub> | **1,434**<br><sub>(193 MiB / 43.4% CPU · 358 MiB/s · 1210.3 µs CPU/req)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/2 · TLS | **749**<br><sub>(194 MiB / 54.6% CPU · 187 MiB/s · 2916.6 µs CPU/req)</sub> | *Not possible* | 🥇 **1,312**<br><sub>(107 MiB / 36.1% CPU · 328 MiB/s · 1100.9 µs CPU/req)</sub> | **1,103**<br><sub>(154 MiB / 45.0% CPU · 276 MiB/s · 1630.6 µs CPU/req)</sub> | **886**<br><sub>(253 MiB / 47.1% CPU · 221 MiB/s · 2127.6 µs CPU/req)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/1 · TLS | **1,086**<br><sub>(193 MiB / 49.0% CPU · 271 MiB/s · 1804.8 µs CPU/req)</sub> | **663**<br><sub>(118 MiB / 40.2% CPU · 166 MiB/s · 2424.4 µs CPU/req)</sub> | 🥇 **1,168**<br><sub>(98 MiB / 39.0% CPU · 292 MiB/s · 1335.7 µs CPU/req)</sub> | **1,124**<br><sub>(152 MiB / 44.0% CPU · 281 MiB/s · 1568.0 µs CPU/req)</sub> | **895**<br><sub>(255 MiB / 51.3% CPU · 224 MiB/s · 2290.6 µs CPU/req)</sub> |

Prefer the ratio columns; absolute RPS swings by VM.

### macOS — heavier reverse GET (64 KiB / 256 KiB)

Median of **3** repeats on `macos-15` (Apple Silicon M1, 3-core / 7 GB) @ `88187363`. Source: Actions [38550291997](https://github.com/justcoding121/titanium-web-proxy/actions/runs/38550291997) (`compare-bodies`). Warmup 2s / measure 8s.

| Body | Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---|---:|---:|---:|---:|---:|
| 64 KiB | HTTP/1 · TLS | HTTP/1 · plain | **3,740**<br><sub>(192 MiB / 17.1% CPU · 234 MiB/s · 137.0 µs CPU/req)</sub> | **4,809**<br><sub>(84 MiB / 18.5% CPU · 301 MiB/s · 115.5 µs CPU/req)</sub> | 🥇 **6,451**<br><sub>(120 MiB / 20.5% CPU · 403 MiB/s · 95.2 µs CPU/req)</sub> | **4,944**<br><sub>(134 MiB / 29.6% CPU · 309 MiB/s · 179.3 µs CPU/req)</sub> | **3,247**<br><sub>(182 MiB / 25.7% CPU · 203 MiB/s · 237.6 µs CPU/req)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/1 · plain | **2,701**<br><sub>(487 MiB / 16.7% CPU · 169 MiB/s · 185.6 µs CPU/req)</sub> | 🥇 **4,722**<br><sub>(87 MiB / 18.3% CPU · 295 MiB/s · 116.1 µs CPU/req)</sub> | **4,408**<br><sub>(122 MiB / 26.5% CPU · 276 MiB/s · 180.4 µs CPU/req)</sub> | **3,614**<br><sub>(134 MiB / 18.5% CPU · 226 MiB/s · 153.4 µs CPU/req)</sub> | **1,874**<br><sub>(176 MiB / 14.9% CPU · 117 MiB/s · 238.6 µs CPU/req)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/1 · plain | **1,559**<br><sub>(279 MiB / 30.1% CPU · 97 MiB/s · 578.8 µs CPU/req)</sub> | 🥇 **1,718**<br><sub>(peak 1,951 · 95 MiB / 22.7% CPU · 107 MiB/s · 395.6 µs CPU/req)</sub> | **1,292**<br><sub>(126 MiB / 27.9% CPU · 81 MiB/s · 647.6 µs CPU/req)</sub> | **1,477**<br><sub>(133 MiB / 50.3% CPU · 92 MiB/s · 1021.8 µs CPU/req)</sub> | **792**<br><sub>(437 MiB / 27.9% CPU · 49 MiB/s · 1057.6 µs CPU/req)</sub> |
| 256 KiB | HTTP/1 · TLS | HTTP/1 · plain | **1,913**<br><sub>(210 MiB / 24.2% CPU · 478 MiB/s · 379.6 µs CPU/req)</sub> | **1,760**<br><sub>(84 MiB / 18.9% CPU · 440 MiB/s · 321.7 µs CPU/req)</sub> | 🥇 **2,238**<br><sub>(117 MiB / 25.3% CPU · 559 MiB/s · 339.6 µs CPU/req)</sub> | **1,933**<br><sub>(155 MiB / 22.5% CPU · 483 MiB/s · 349.7 µs CPU/req)</sub> | **1,463**<br><sub>(183 MiB / 31.3% CPU · 366 MiB/s · 642.3 µs CPU/req)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/1 · plain | **1,089**<br><sub>(426 MiB / 18.8% CPU · 272 MiB/s · 517.5 µs CPU/req)</sub> | **1,521**<br><sub>(86 MiB / 21.7% CPU · 380 MiB/s · 428.3 µs CPU/req)</sub> | 🥇 **1,971**<br><sub>(120 MiB / 30.6% CPU · 493 MiB/s · 466.3 µs CPU/req)</sub> | **1,777**<br><sub>(154 MiB / 27.2% CPU · 444 MiB/s · 458.7 µs CPU/req)</sub> | **1,439**<br><sub>(183 MiB / 30.8% CPU · 360 MiB/s · 641.4 µs CPU/req)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/1 · plain | **329**<br><sub>(183 MiB / 28.8% CPU · 82 MiB/s · 2623.3 µs CPU/req)</sub> | 🥇 **630**<br><sub>(95 MiB / 27.8% CPU · 158 MiB/s · 1323.2 µs CPU/req)</sub> | **246**<br><sub>(peak 428 · 105 MiB / 25.2% CPU · 62 MiB/s · 3064.5 µs CPU/req)</sub> | **367**<br><sub>(150 MiB / 51.5% CPU · 92 MiB/s · 4203.1 µs CPU/req)</sub> | **96**<br><sub>(peak 152 · 284 MiB / 18.1% CPU · 24 MiB/s · 5677.8 µs CPU/req)</sub> |
| 64 KiB | HTTP/2 · plain | HTTP/1 · plain | **2,341**<br><sub>(466 MiB / 13.0% CPU · 146 MiB/s · 166.3 µs CPU/req)</sub> | **3,687**<br><sub>(73 MiB / 11.6% CPU · 230 MiB/s · 94.7 µs CPU/req)</sub> | 🥇 **3,774**<br><sub>(109 MiB / 18.8% CPU · 236 MiB/s · 149.1 µs CPU/req)</sub> | **3,571**<br><sub>(122 MiB / 19.6% CPU · 223 MiB/s · 165.0 µs CPU/req)</sub> | **2,058**<br><sub>(212 MiB / 13.8% CPU · 129 MiB/s · 200.7 µs CPU/req)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/2 · plain | **2,430**<br><sub>(370 MiB / 13.1% CPU · 152 MiB/s · 161.5 µs CPU/req)</sub> | *Not possible* | 🥇 **3,285**<br><sub>(126 MiB / 25.0% CPU · 205 MiB/s · 228.2 µs CPU/req)</sub> | **2,631**<br><sub>(126 MiB / 18.5% CPU · 164 MiB/s · 210.9 µs CPU/req)</sub> | **3,190**<br><sub>(263 MiB / 23.1% CPU · 199 MiB/s · 216.8 µs CPU/req)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/2 · TLS | **2,725**<br><sub>(407 MiB / 21.0% CPU · 170 MiB/s · 231.4 µs CPU/req)</sub> | *Not possible* | 🥇 **4,009**<br><sub>(122 MiB / 30.9% CPU · 251 MiB/s · 230.8 µs CPU/req)</sub> | **3,328**<br><sub>(126 MiB / 23.1% CPU · 208 MiB/s · 208.1 µs CPU/req)</sub> | **1,803**<br><sub>(249 MiB / 21.6% CPU · 113 MiB/s · 359.5 µs CPU/req)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/2 · TLS | 🥇 **1,971**<br><sub>(235 MiB / 27.2% CPU · 123 MiB/s · 413.9 µs CPU/req)</sub> | *Not possible* | **1,448**<br><sub>(131 MiB / 26.1% CPU · 90 MiB/s · 539.8 µs CPU/req)</sub> | **1,573**<br><sub>(130 MiB / 46.1% CPU · 98 MiB/s · 879.0 µs CPU/req)</sub> | **907**<br><sub>(452 MiB / 23.4% CPU · 57 MiB/s · 774.3 µs CPU/req)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/1 · TLS | **1,697**<br><sub>(251 MiB / 29.6% CPU · 106 MiB/s · 523.7 µs CPU/req)</sub> | 🥇 **2,292**<br><sub>(101 MiB / 24.4% CPU · 143 MiB/s · 319.0 µs CPU/req)</sub> | **1,655**<br><sub>(129 MiB / 31.4% CPU · 103 MiB/s · 569.7 µs CPU/req)</sub> | **1,690**<br><sub>(132 MiB / 45.2% CPU · 106 MiB/s · 803.0 µs CPU/req)</sub> | **923**<br><sub>(400 MiB / 23.1% CPU · 58 MiB/s · 750.7 µs CPU/req)</sub> |
| 256 KiB | HTTP/2 · plain | HTTP/1 · plain | **1,439**<br><sub>(463 MiB / 13.5% CPU · 360 MiB/s · 281.0 µs CPU/req)</sub> | 🥇 **3,278**<br><sub>(73 MiB / 17.7% CPU · 819 MiB/s · 161.7 µs CPU/req)</sub> | **2,845**<br><sub>(109 MiB / 20.9% CPU · 711 MiB/s · 220.5 µs CPU/req)</sub> | **2,510**<br><sub>(145 MiB / 19.3% CPU · 628 MiB/s · 230.5 µs CPU/req)</sub> | **1,688**<br><sub>(212 MiB / 17.7% CPU · 422 MiB/s · 315.3 µs CPU/req)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/2 · plain | **1,401**<br><sub>(699 MiB / 28.0% CPU · 350 MiB/s · 599.1 µs CPU/req)</sub> | *Not possible* | 🥇 **1,635**<br><sub>(140 MiB / 28.9% CPU · 409 MiB/s · 530.8 µs CPU/req)</sub> | **1,588**<br><sub>(137 MiB / 21.1% CPU · 397 MiB/s · 399.1 µs CPU/req)</sub> | **1,185**<br><sub>(294 MiB / 29.2% CPU · 296 MiB/s · 739.9 µs CPU/req)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **1,377**<br><sub>(543 MiB / 29.2% CPU · 344 MiB/s · 635.3 µs CPU/req)</sub> | *Not possible* | **1,355**<br><sub>(134 MiB / 32.2% CPU · 339 MiB/s · 712.5 µs CPU/req)</sub> | **1,020**<br><sub>(130 MiB / 25.8% CPU · 255 MiB/s · 758.0 µs CPU/req)</sub> | **975**<br><sub>(270 MiB / 29.5% CPU · 244 MiB/s · 907.4 µs CPU/req)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/2 · TLS | **90**<br><sub>(peak 176 · 287 MiB / 25.3% CPU · 22 MiB/s · 8477.1 µs CPU/req)</sub> | *Not possible* | **335**<br><sub>(peak 482 · 114 MiB / 26.2% CPU · 84 MiB/s · 2343.6 µs CPU/req)</sub> | 🥇 **368**<br><sub>(144 MiB / 48.7% CPU · 92 MiB/s · 3967.7 µs CPU/req)</sub> | **253**<br><sub>(531 MiB / 30.4% CPU · 63 MiB/s · 3601.7 µs CPU/req)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/1 · TLS | **290**<br><sub>(peak 414 · 202 MiB / 31.3% CPU · 73 MiB/s · 3232.4 µs CPU/req)</sub> | 🥇 **562**<br><sub>(99 MiB / 26.7% CPU · 141 MiB/s · 1426.4 µs CPU/req)</sub> | **438**<br><sub>(peak 525 · 108 MiB / 28.1% CPU · 110 MiB/s · 1926.3 µs CPU/req)</sub> | **454**<br><sub>(151 MiB / 47.3% CPU · 114 MiB/s · 3122.1 µs CPU/req)</sub> | **190**<br><sub>(398 MiB / 23.4% CPU · 48 MiB/s · 3678.7 µs CPU/req)</sub> |

### Windows — POST 64 KiB request + 64 KiB response

Median of **3** repeats on `windows-latest` @ `88187363`. Source: Actions [38550300822](https://github.com/justcoding121/titanium-web-proxy/actions/runs/38550300822) (`compare-post`).

*Not possible:* **HAProxy** and **Envoy** columns are omitted (no official Windows port).




















| Client | Origin | TWP | nginx | YARP |
|---|---|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | 🥇 **5,990**<br><sub>(99 MiB / 44.7% CPU · 749 MiB/s · 298.4 µs CPU/req)</sub> | **394**<br><sub>(143 MiB / 24.9% CPU · 49 MiB/s · 2534.9 µs CPU/req)</sub> | **4,238**<br><sub>(136 MiB / 57.4% CPU · 530 MiB/s · 541.6 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **3,953**<br><sub>(201 MiB / 46.2% CPU · 494 MiB/s · 467.9 µs CPU/req)</sub> | **357**<br><sub>(147 MiB / 24.8% CPU · 45 MiB/s · 2772.3 µs CPU/req)</sub> | **3,771**<br><sub>(136 MiB / 47.2% CPU · 471 MiB/s · 500.2 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **2,219**<br><sub>(184 MiB / 46.0% CPU · 277 MiB/s · 829.3 µs CPU/req)</sub> | *Not possible (no QUIC)* | **1,497**<br><sub>(207 MiB / 52.8% CPU · 187 MiB/s · 1411.5 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | 🥇 **6,612**<br><sub>(198 MiB / 43.4% CPU · 827 MiB/s · 262.5 µs CPU/req)</sub> | **1,781**<br><sub>(131 MiB / 24.8% CPU · 223 MiB/s · 556.4 µs CPU/req)</sub> | **6,280**<br><sub>(114 MiB / 47.0% CPU · 785 MiB/s · 299.6 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | 🥇 **4,452**<br><sub>(167 MiB / 38.3% CPU · 556 MiB/s · 344.3 µs CPU/req)</sub> | *Not possible* | **3,587**<br><sub>(151 MiB / 46.6% CPU · 448 MiB/s · 519.3 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **3,558**<br><sub>(166 MiB / 39.0% CPU · 445 MiB/s · 438.8 µs CPU/req)</sub> | *Not possible* | **2,851**<br><sub>(149 MiB / 48.3% CPU · 356 MiB/s · 678.0 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | 🥇 **2,596**<br><sub>(199 MiB / 44.4% CPU · 325 MiB/s · 684.4 µs CPU/req)</sub> | *Not possible* | **1,585**<br><sub>(221 MiB / 51.6% CPU · 198 MiB/s · 1303.1 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **1,684**<br><sub>(162 MiB / 43.2% CPU · 211 MiB/s · 1024.9 µs CPU/req)</sub> | *Not possible (no QUIC)* | **1,262**<br><sub>(207 MiB / 52.4% CPU · 158 MiB/s · 1661.1 µs CPU/req)</sub> |

TWP leads H2→H1 POST (about **1.1–1.3×** YARP) and H3 POST (about **1.0–1.1×** YARP). H2 TLS→H2 TLS POST sustain is about **1.2–1.4×** YARP.

### Linux — POST 64 KiB request + 64 KiB response

Median of **3** repeats @ `88187363`. Source: Actions [38550300822](https://github.com/justcoding121/titanium-web-proxy/actions/runs/38550300822) (`compare-post`).


| Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---:|---:|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | **4,912**<br><sub>(136 MiB / 45.1% CPU · 614 MiB/s · 367.6 µs CPU/req)</sub> | **3,826**<br><sub>(99 MiB / 50.1% CPU · 478 MiB/s · 523.7 µs CPU/req)</sub> | 🥇 **5,131**<br><sub>(90 MiB / 41.8% CPU · 641 MiB/s · 325.9 µs CPU/req)</sub> | **5,078**<br><sub>(134 MiB / 41.5% CPU · 635 MiB/s · 326.6 µs CPU/req)</sub> | **3,256**<br><sub>(176 MiB / 55.0% CPU · 407 MiB/s · 675.6 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | **3,854**<br><sub>(269 MiB / 46.6% CPU · 482 MiB/s · 483.7 µs CPU/req)</sub> | **3,583**<br><sub>(106 MiB / 44.5% CPU · 448 MiB/s · 496.2 µs CPU/req)</sub> | 🥇 **4,067**<br><sub>(91 MiB / 43.3% CPU · 508 MiB/s · 426.2 µs CPU/req)</sub> | **3,856**<br><sub>(peak 4,641 · 126 MiB / 40.6% CPU · 482 MiB/s · 421.6 µs CPU/req)</sub> | **3,502**<br><sub>(177 MiB / 50.2% CPU · 438 MiB/s · 573.0 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **2,891**<br><sub>(224 MiB / 47.7% CPU · 361 MiB/s · 659.5 µs CPU/req)</sub> | **850**<br><sub>(111 MiB / 44.1% CPU · 106 MiB/s · 2076.2 µs CPU/req)</sub> | **2,481**<br><sub>(93 MiB / 44.1% CPU · 310 MiB/s · 711.9 µs CPU/req)</sub> | **0**<br><sub>(131 MiB / 0.3% CPU)</sub> | **2,111**<br><sub>(256 MiB / 49.7% CPU · 264 MiB/s · 942.5 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | **5,706**<br><sub>(255 MiB / 44.1% CPU · 713 MiB/s · 308.9 µs CPU/req)</sub> | **4,807**<br><sub>(86 MiB / 45.3% CPU · 601 MiB/s · 376.7 µs CPU/req)</sub> | 🥇 **6,132**<br><sub>(77 MiB / 40.1% CPU · 767 MiB/s · 261.4 µs CPU/req)</sub> | **4,386**<br><sub>(peak 5,669 · 117 MiB / 44.6% CPU · 548 MiB/s · 406.6 µs CPU/req)</sub> | **5,289**<br><sub>(157 MiB / 48.6% CPU · 661 MiB/s · 368.0 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | **8,148**<br><sub>(219 MiB / 35.1% CPU · 1,018 MiB/s · 172.4 µs CPU/req)</sub> | *Not possible* | **6,789**<br><sub>(93 MiB / 41.3% CPU · 849 MiB/s · 243.3 µs CPU/req)</sub> | 🥇 **8,353**<br><sub>(135 MiB / 39.4% CPU · 1,044 MiB/s · 188.8 µs CPU/req)</sub> | **5,511**<br><sub>(197 MiB / 47.7% CPU · 689 MiB/s · 346.0 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **6,055**<br><sub>(209 MiB / 37.5% CPU · 757 MiB/s · 247.5 µs CPU/req)</sub> | *Not possible* | **4,983**<br><sub>(92 MiB / 42.8% CPU · 623 MiB/s · 343.1 µs CPU/req)</sub> | **5,843**<br><sub>(142 MiB / 41.3% CPU · 730 MiB/s · 282.8 µs CPU/req)</sub> | **4,372**<br><sub>(195 MiB / 46.3% CPU · 546 MiB/s · 423.3 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | 🥇 **2,753**<br><sub>(244 MiB / 42.9% CPU · 344 MiB/s · 623.8 µs CPU/req)</sub> | *Not possible* | **2,498**<br><sub>(95 MiB / 39.5% CPU · 312 MiB/s · 632.3 µs CPU/req)</sub> | **0**<br><sub>(130 MiB / 0.2% CPU)</sub> | **2,034**<br><sub>(260 MiB / 48.8% CPU · 254 MiB/s · 958.6 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **2,178**<br><sub>(237 MiB / 47.2% CPU · 272 MiB/s · 866.7 µs CPU/req)</sub> | **852**<br><sub>(114 MiB / 51.7% CPU · 107 MiB/s · 2427.2 µs CPU/req)</sub> | **1,957**<br><sub>(96 MiB / 43.8% CPU · 245 MiB/s · 895.1 µs CPU/req)</sub> | **0**<br><sub>(132 MiB / 0.4% CPU)</sub> | **1,792**<br><sub>(259 MiB / 49.8% CPU · 224 MiB/s · 1112.2 µs CPU/req)</sub> |

Linux nginx H1/H2/H3 POST completed (nginx.org mainline). TWP÷YARP H1 ≈ **1.5×**; H2→H1 ≈ **1.2×**; H3 ≈ **1.1×**. TWP÷nginx H3 ≈ **6×**.

### macOS — POST 64 KiB request + 64 KiB response

Median of **3** repeats on `macos-15` (Apple Silicon M1, 3-core / 7 GB) @ `88187363`. Source: Actions [38550300822](https://github.com/justcoding121/titanium-web-proxy/actions/runs/38550300822) (`compare-post`).

| Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---:|---:|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | **2,939**<br><sub>(241 MiB / 25.1% CPU · 367 MiB/s · 256.1 µs CPU/req)</sub> | **3,017**<br><sub>(89 MiB / 19.0% CPU · 377 MiB/s · 188.7 µs CPU/req)</sub> | 🥇 **3,944**<br><sub>(120 MiB / 29.0% CPU · 493 MiB/s · 220.8 µs CPU/req)</sub> | **3,086**<br><sub>(133 MiB / 28.6% CPU · 386 MiB/s · 277.7 µs CPU/req)</sub> | **1,791**<br><sub>(214 MiB / 31.9% CPU · 224 MiB/s · 535.3 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | **1,548**<br><sub>(537 MiB / 18.9% CPU · 194 MiB/s · 366.4 µs CPU/req)</sub> | **2,346**<br><sub>(99 MiB / 19.0% CPU · 293 MiB/s · 243.3 µs CPU/req)</sub> | 🥇 **2,440**<br><sub>(124 MiB / 27.1% CPU · 305 MiB/s · 333.9 µs CPU/req)</sub> | **1,255**<br><sub>(peak 2,646 · 121 MiB / 17.2% CPU · 157 MiB/s · 410.3 µs CPU/req)</sub> | **1,574**<br><sub>(178 MiB / 23.9% CPU · 197 MiB/s · 454.5 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **1,255**<br><sub>(308 MiB / 29.3% CPU · 157 MiB/s · 701.5 µs CPU/req)</sub> | **1,154**<br><sub>(95 MiB / 24.2% CPU · 144 MiB/s · 627.9 µs CPU/req)</sub> | **887**<br><sub>(119 MiB / 29.1% CPU · 111 MiB/s · 985.8 µs CPU/req)</sub> | **1,055**<br><sub>(136 MiB / 46.9% CPU · 132 MiB/s · 1333.2 µs CPU/req)</sub> | **597**<br><sub>(437 MiB / 24.6% CPU · 75 MiB/s · 1234.8 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | **2,545**<br><sub>(492 MiB / 22.0% CPU · 318 MiB/s · 258.9 µs CPU/req)</sub> | 🥇 **3,354**<br><sub>(85 MiB / 16.6% CPU · 419 MiB/s · 148.1 µs CPU/req)</sub> | **3,182**<br><sub>(111 MiB / 21.5% CPU · 398 MiB/s · 202.8 µs CPU/req)</sub> | **2,372**<br><sub>(126 MiB / 20.1% CPU · 297 MiB/s · 253.6 µs CPU/req)</sub> | **1,930**<br><sub>(240 MiB / 21.8% CPU · 241 MiB/s · 339.3 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | **1,676**<br><sub>(431 MiB / 16.4% CPU · 210 MiB/s · 294.1 µs CPU/req)</sub> | *Not possible* | 🥇 **2,428**<br><sub>(131 MiB / 26.7% CPU · 304 MiB/s · 329.8 µs CPU/req)</sub> | **2,042**<br><sub>(131 MiB / 20.9% CPU · 255 MiB/s · 307.6 µs CPU/req)</sub> | **1,028**<br><sub>(238 MiB / 17.8% CPU · 128 MiB/s · 520.0 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | **1,895**<br><sub>(425 MiB / 23.7% CPU · 237 MiB/s · 375.2 µs CPU/req)</sub> | *Not possible* | 🥇 **1,915**<br><sub>(126 MiB / 29.1% CPU · 239 MiB/s · 456.6 µs CPU/req)</sub> | **1,792**<br><sub>(128 MiB / 28.2% CPU · 224 MiB/s · 472.8 µs CPU/req)</sub> | **889**<br><sub>(229 MiB / 19.3% CPU · 111 MiB/s · 650.5 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | 🥇 **737**<br><sub>(345 MiB / 28.2% CPU · 92 MiB/s · 1148.0 µs CPU/req)</sub> | *Not possible* | **553**<br><sub>(121 MiB / 31.6% CPU · 69 MiB/s · 1714.4 µs CPU/req)</sub> | **677**<br><sub>(136 MiB / 46.3% CPU · 85 MiB/s · 2051.8 µs CPU/req)</sub> | **566**<br><sub>(417 MiB / 30.3% CPU · 71 MiB/s · 1607.6 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | **835**<br><sub>(269 MiB / 31.4% CPU · 104 MiB/s · 1128.8 µs CPU/req)</sub> | **1,084**<br><sub>(101 MiB / 21.0% CPU · 135 MiB/s · 582.2 µs CPU/req)</sub> | 🥇 **1,196**<br><sub>(127 MiB / 33.9% CPU · 149 MiB/s · 849.7 µs CPU/req)</sub> | **492**<br><sub>(132 MiB / 29.5% CPU · 62 MiB/s · 1799.5 µs CPU/req)</sub> | **744**<br><sub>(407 MiB / 30.8% CPU · 93 MiB/s · 1243.9 µs CPU/req)</sub> |

### Windows — lossy / high-RTT (H2 HOL / H3 loss)

Userspace **5 ms** one-way delay + **1%** TCP connection stall (H1/H2); UDP is **loss% only** (no per-datagram delay; MsQuic-safe) + **1%** datagram drop (H3); **64 KiB** GET. Median of **3** repeats on `windows-latest` @ `88187363` — [38550307008](https://github.com/justcoding121/titanium-web-proxy/actions/runs/38550307008) (`compare-lossy`).

*Not possible:* **HAProxy** and **Envoy** columns are omitted (no official Windows port).


| Client | Origin | TWP | nginx | YARP |
|---|---|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | **661**<br><sub>(91 MiB / 4.2% CPU · 41 MiB/s · 256.5 µs CPU/req)</sub> | **648**<br><sub>(142 MiB / 18.6% CPU · 41 MiB/s · 1144.7 µs CPU/req)</sub> | 🥇 **662**<br><sub>(120 MiB / 4.9% CPU · 41 MiB/s · 295.3 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **248**<br><sub>(154 MiB / 2.8% CPU · 15 MiB/s · 449.1 µs CPU/req)</sub> | **244**<br><sub>(144 MiB / 8.0% CPU · 15 MiB/s · 1314.2 µs CPU/req)</sub> | **226**<br><sub>(117 MiB / 3.5% CPU · 14 MiB/s · 613.3 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | **849**<br><sub>(123 MiB / 19.0% CPU · 53 MiB/s · 893.9 µs CPU/req)</sub> | *Not possible (no QUIC)* | 🥇 **890**<br><sub>(175 MiB / 30.6% CPU · 56 MiB/s · 1376.8 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | **248**<br><sub>(143 MiB / 2.0% CPU · 15 MiB/s · 321.2 µs CPU/req)</sub> | 🥇 **249**<br><sub>(127 MiB / 1.8% CPU · 16 MiB/s · 290.4 µs CPU/req)</sub> | **235**<br><sub>(101 MiB / 2.7% CPU · 15 MiB/s · 458.8 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | 🥇 **234**<br><sub>(125 MiB / 1.7% CPU · 15 MiB/s · 296.9 µs CPU/req)</sub> | *Not possible* | **229**<br><sub>(116 MiB / 2.6% CPU · 14 MiB/s · 463.3 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | **221**<br><sub>(128 MiB / 3.4% CPU · 14 MiB/s · 614.1 µs CPU/req)</sub> | *Not possible* | 🥇 **228**<br><sub>(120 MiB / 3.6% CPU · 14 MiB/s · 629.8 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | **1,110**<br><sub>(121 MiB / 14.5% CPU · 69 MiB/s · 521.5 µs CPU/req)</sub> | *Not possible* | 🥇 **1,370**<br><sub>(194 MiB / 26.2% CPU · 86 MiB/s · 764.6 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | **968**<br><sub>(128 MiB / 21.5% CPU · 61 MiB/s · 887.4 µs CPU/req)</sub> | *Not possible (no QUIC)* | 🥇 **1,051**<br><sub>(188 MiB / 31.9% CPU · 66 MiB/s · 1212.7 µs CPU/req)</sub> |

H1, H2, and H3 loss sustain are in the table. Prefer the ratio columns.

### Linux — lossy / high-RTT (H2 HOL / H3 loss)

Median of **3** repeats @ `88187363`. Source: [38550307008](https://github.com/justcoding121/titanium-web-proxy/actions/runs/38550307008) (`compare-lossy`; lossy H3 uses `quic-http3`, UDP drop-only).


| Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---:|---:|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | **1,201**<br><sub>(122 MiB / 13.2% CPU · 75 MiB/s · 438.4 µs CPU/req)</sub> | 🥇 **1,209**<br><sub>(99 MiB / 12.3% CPU · 76 MiB/s · 407.8 µs CPU/req)</sub> | **1,208**<br><sub>(84 MiB / 6.7% CPU · 76 MiB/s · 221.5 µs CPU/req)</sub> | **1,198**<br><sub>(128 MiB / 9.2% CPU · 75 MiB/s · 306.1 µs CPU/req)</sub> | **1,198**<br><sub>(145 MiB / 16.4% CPU · 75 MiB/s · 549.4 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **583**<br><sub>(193 MiB / 11.7% CPU · 36 MiB/s · 799.5 µs CPU/req)</sub> | **518**<br><sub>(100 MiB / 5.0% CPU · 32 MiB/s · 388.7 µs CPU/req)</sub> | **528**<br><sub>(85 MiB / 4.0% CPU · 33 MiB/s · 301.0 µs CPU/req)</sub> | **505**<br><sub>(130 MiB / 4.6% CPU · 32 MiB/s · 362.5 µs CPU/req)</sub> | **454**<br><sub>(140 MiB / 12.0% CPU · 28 MiB/s · 1058.2 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **1,147**<br><sub>(165 MiB / 24.3% CPU · 72 MiB/s · 847.7 µs CPU/req)</sub> | **1,079**<br><sub>(113 MiB / 23.9% CPU · 67 MiB/s · 884.5 µs CPU/req)</sub> | **956**<br><sub>(103 MiB / 18.7% CPU · 60 MiB/s · 783.9 µs CPU/req)</sub> | **1,020**<br><sub>(138 MiB / 25.4% CPU · 64 MiB/s · 996.1 µs CPU/req)</sub> | **1,123**<br><sub>(224 MiB / 31.3% CPU · 70 MiB/s · 1114.4 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | 🥇 **614**<br><sub>(194 MiB / 8.1% CPU · 38 MiB/s · 528.3 µs CPU/req)</sub> | **555**<br><sub>(78 MiB / 2.6% CPU · 35 MiB/s · 188.1 µs CPU/req)</sub> | **539**<br><sub>(72 MiB / 1.9% CPU · 34 MiB/s · 138.0 µs CPU/req)</sub> | **528**<br><sub>(122 MiB / 2.5% CPU · 33 MiB/s · 188.1 µs CPU/req)</sub> | **467**<br><sub>(135 MiB / 7.1% CPU · 29 MiB/s · 607.3 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | **390**<br><sub>(153 MiB / 5.7% CPU · 24 MiB/s · 581.8 µs CPU/req)</sub> | *Not possible* | 🥇 **550**<br><sub>(88 MiB / 2.9% CPU · 34 MiB/s · 208.0 µs CPU/req)</sub> | **505**<br><sub>(129 MiB / 3.7% CPU · 32 MiB/s · 294.8 µs CPU/req)</sub> | **418**<br><sub>(147 MiB / 8.7% CPU · 26 MiB/s · 827.9 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | **167**<br><sub>(158 MiB / 5.1% CPU · 10 MiB/s · 1215.8 µs CPU/req)</sub> | *Not possible* | 🥇 **525**<br><sub>(87 MiB / 5.3% CPU · 33 MiB/s · 406.6 µs CPU/req)</sub> | **502**<br><sub>(131 MiB / 6.1% CPU · 31 MiB/s · 484.1 µs CPU/req)</sub> | **472**<br><sub>(146 MiB / 14.6% CPU · 29 MiB/s · 1237.2 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | **1,542**<br><sub>(168 MiB / 19.8% CPU · 96 MiB/s · 513.0 µs CPU/req)</sub> | *Not possible* | **1,504**<br><sub>(104 MiB / 19.1% CPU · 94 MiB/s · 509.4 µs CPU/req)</sub> | 🥇 **1,699**<br><sub>(141 MiB / 25.3% CPU · 106 MiB/s · 595.2 µs CPU/req)</sub> | **1,656**<br><sub>(239 MiB / 29.2% CPU · 104 MiB/s · 706.4 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | **1,395**<br><sub>(178 MiB / 22.6% CPU · 87 MiB/s · 648.2 µs CPU/req)</sub> | 🥇 **1,441**<br><sub>(121 MiB / 23.5% CPU · 90 MiB/s · 653.2 µs CPU/req)</sub> | **1,311**<br><sub>(105 MiB / 20.1% CPU · 82 MiB/s · 612.2 µs CPU/req)</sub> | **1,382**<br><sub>(138 MiB / 25.6% CPU · 86 MiB/s · 740.5 µs CPU/req)</sub> | **1,410**<br><sub>(224 MiB / 30.9% CPU · 88 MiB/s · 878.3 µs CPU/req)</sub> |

TWP H2 HOL ≫ YARP (~**7.8×**). H3 TWP÷YARP ≈ **1.07×**.

### macOS — lossy / high-RTT (H2 HOL / H3 loss)

Median of **3** repeats on `macos-15` (Apple Silicon M1, 3-core / 7 GB) @ `88187363`. Source: [38550307008](https://github.com/justcoding121/titanium-web-proxy/actions/runs/38550307008) (`compare-lossy`; lossy H3 uses `quic-http3`, UDP drop-only).

| Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---:|---:|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | **364**<br><sub>(257 MiB / 3.6% CPU · 23 MiB/s · 301.2 µs CPU/req)</sub> | 🥇 **440**<br><sub>(85 MiB / 1.6% CPU · 28 MiB/s · 110.4 µs CPU/req)</sub> | **383**<br><sub>(117 MiB / 1.9% CPU · 24 MiB/s · 145.6 µs CPU/req)</sub> | **404**<br><sub>(130 MiB / 2.3% CPU · 25 MiB/s · 173.8 µs CPU/req)</sub> | **374**<br><sub>(221 MiB / 3.7% CPU · 23 MiB/s · 297.2 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **160**<br><sub>(438 MiB / 4.8% CPU · 10.0 MiB/s · 895.3 µs CPU/req)</sub> | **136**<br><sub>(85 MiB / 0.8% CPU · 8.5 MiB/s · 176.9 µs CPU/req)</sub> | **144**<br><sub>(114 MiB / 1.8% CPU · 9.0 MiB/s · 384.4 µs CPU/req)</sub> | **136**<br><sub>(129 MiB / 1.9% CPU · 8.5 MiB/s · 411.2 µs CPU/req)</sub> | **143**<br><sub>(224 MiB / 3.6% CPU · 9.0 MiB/s · 763.6 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | **933**<br><sub>(271 MiB / 21.7% CPU · 58 MiB/s · 697.4 µs CPU/req)</sub> | 🥇 **1,332**<br><sub>(99 MiB / 14.8% CPU · 83 MiB/s · 334.0 µs CPU/req)</sub> | **634**<br><sub>(123 MiB / 13.9% CPU · 40 MiB/s · 658.0 µs CPU/req)</sub> | **1,045**<br><sub>(132 MiB / 29.4% CPU · 65 MiB/s · 845.1 µs CPU/req)</sub> | **732**<br><sub>(428 MiB / 24.6% CPU · 46 MiB/s · 1005.7 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | **137**<br><sub>(316 MiB / 4.3% CPU · 8.6 MiB/s · 930.8 µs CPU/req)</sub> | **145**<br><sub>(72 MiB / 0.5% CPU · 9.1 MiB/s · 109.4 µs CPU/req)</sub> | 🥇 **152**<br><sub>(102 MiB / 1.0% CPU · 9.5 MiB/s · 199.7 µs CPU/req)</sub> | **136**<br><sub>(125 MiB / 1.4% CPU · 8.5 MiB/s · 300.4 µs CPU/req)</sub> | **145**<br><sub>(145 MiB / 3.4% CPU · 9.1 MiB/s · 696.8 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | **128**<br><sub>(315 MiB / 3.4% CPU · 8.0 MiB/s · 790.0 µs CPU/req)</sub> | *Not possible* | 🥇 **144**<br><sub>(120 MiB / 1.4% CPU · 9.0 MiB/s · 287.7 µs CPU/req)</sub> | **136**<br><sub>(124 MiB / 1.7% CPU · 8.5 MiB/s · 367.5 µs CPU/req)</sub> | **120**<br><sub>(277 MiB / 3.2% CPU · 7.5 MiB/s · 806.3 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | **116**<br><sub>(307 MiB / 3.2% CPU · 7.2 MiB/s · 820.8 µs CPU/req)</sub> | *Not possible* | 🥇 **138**<br><sub>(117 MiB / 2.0% CPU · 8.6 MiB/s · 435.1 µs CPU/req)</sub> | **120**<br><sub>(124 MiB / 2.0% CPU · 7.5 MiB/s · 513.8 µs CPU/req)</sub> | **136**<br><sub>(282 MiB / 4.2% CPU · 8.5 MiB/s · 924.1 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | **762**<br><sub>(180 MiB / 21.9% CPU · 48 MiB/s · 861.8 µs CPU/req)</sub> | *Not possible* | **544**<br><sub>(128 MiB / 13.8% CPU · 34 MiB/s · 759.9 µs CPU/req)</sub> | 🥇 **1,073**<br><sub>(130 MiB / 30.8% CPU · 67 MiB/s · 862.3 µs CPU/req)</sub> | **445**<br><sub>(410 MiB / 19.7% CPU · 28 MiB/s · 1330.5 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | **887**<br><sub>(208 MiB / 18.3% CPU · 55 MiB/s · 620.1 µs CPU/req)</sub> | **218**<br><sub>(peak 406 · 82 MiB / 3.0% CPU · 14 MiB/s · 409.3 µs CPU/req)</sub> | **401**<br><sub>(126 MiB / 14.1% CPU · 25 MiB/s · 1057.4 µs CPU/req)</sub> | 🥇 **1,023**<br><sub>(132 MiB / 29.2% CPU · 64 MiB/s · 855.3 µs CPU/req)</sub> | **727**<br><sub>(363 MiB / 21.4% CPU · 45 MiB/s · 882.5 µs CPU/req)</sub> |

### Architecture-sensitive

These runs isolate slow app readers, origin-early response, HTTP/2 duplex, and WebSocket echo. See [TWP vs YARP IO model](Performance-Profiling#user-content-twp-vs-yarp-io-model). Laptop 1-rep numbers are on [Performance Local Lab](Performance-Local-Lab#user-content-architecture-sensitive).

Median of **3** repeats on matched 4 vCPU / 16 GiB runners @ `88187363` ([38550317747](https://github.com/justcoding121/titanium-web-proxy/actions/runs/38550317747)). Slow consumer = 256 KiB GET, 16 KiB read + 8 ms sleep. Early response = 64 KiB POST, origin writes after 8 KiB. Duplex HTTP/2 = overlapping 64 KiB POST on H2 TLS↔H2 TLS. WebSocket row = H1 Upgrade echo round-trips/sec (not RFC 8441; see WebSocket RFC 8441 table below).

Lossy-link runs (slow **network**) are already published above; they are not a slow **app** reader.

#### Windows


*Not possible:* **HAProxy** and **Envoy** columns are omitted (no official Windows port).

| Scenario | Client | Origin | TWP | nginx | YARP |
|---|---|---|---:|---:|---:|
| Slow consumer (256 KiB GET, throttled client read) | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **248**<br><sub>(95 MiB / 5.3% CPU · 62 MiB/s · 860.7 µs CPU/req)</sub> | **223**<br><sub>(144 MiB / 24.6% CPU · 56 MiB/s · 4414.2 µs CPU/req)</sub> | **242**<br><sub>(111 MiB / 4.8% CPU · 60 MiB/s · 794.7 µs CPU/req)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · TLS | HTTP/1 · plain | **248**<br><sub>(137 MiB / 5.5% CPU · 62 MiB/s · 883.7 µs CPU/req)</sub> | **198**<br><sub>(142 MiB / 24.8% CPU · 50 MiB/s · 5004.0 µs CPU/req)</sub> | 🥇 **248**<br><sub>(121 MiB / 5.6% CPU · 62 MiB/s · 900.7 µs CPU/req)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/3 · QUIC | HTTP/1 · plain | **76**<br><sub>(109 MiB / 45.2% CPU · 19 MiB/s · 23910.1 µs CPU/req)</sub> | *Not possible (no QUIC)* | 🥇 **118**<br><sub>(152 MiB / 46.4% CPU · 29 MiB/s · 15765.7 µs CPU/req)</sub> |
| Early response (origin writes after first request chunk) | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **11,160**<br><sub>(97 MiB / 39.3% CPU · 1,395 MiB/s · 140.9 µs CPU/req)</sub> | **643**<br><sub>(143 MiB / 24.8% CPU · 80 MiB/s · 1541.6 µs CPU/req)</sub> | **7,477**<br><sub>(138 MiB / 55.2% CPU · 935 MiB/s · 295.2 µs CPU/req)</sub> |
| Early response (origin writes after first request chunk) | HTTP/2 · TLS | HTTP/1 · plain | **4,333**<br><sub>(217 MiB / 54.1% CPU · 542 MiB/s · 499.4 µs CPU/req)</sub> | **488**<br><sub>(147 MiB / 24.8% CPU · 61 MiB/s · 2029.9 µs CPU/req)</sub> | 🥇 **5,116**<br><sub>(139 MiB / 48.8% CPU · 639 MiB/s · 381.3 µs CPU/req)</sub> |
| Early response (origin writes after first request chunk) | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **1,769**<br><sub>(155 MiB / 44.7% CPU · 221 MiB/s · 1010.7 µs CPU/req)</sub> | *Not possible (no QUIC)* | **1,083**<br><sub>(209 MiB / 50.7% CPU · 135 MiB/s · 1873.2 µs CPU/req)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · plain | HTTP/1 · plain | **248**<br><sub>(131 MiB / 4.3% CPU · 62 MiB/s · 691.4 µs CPU/req)</sub> | **248**<br><sub>(128 MiB / 9.1% CPU · 62 MiB/s · 1465.3 µs CPU/req)</sub> | 🥇 **248**<br><sub>(109 MiB / 4.5% CPU · 62 MiB/s · 725.1 µs CPU/req)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · TLS | HTTP/2 · TLS | **244**<br><sub>(160 MiB / 9.0% CPU · 61 MiB/s · 1484.4 µs CPU/req)</sub> | *Not possible* | 🥇 **248**<br><sub>(144 MiB / 10.3% CPU · 62 MiB/s · 1672.1 µs CPU/req)</sub> |
| Early response (origin writes after first request chunk) | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **3,416**<br><sub>(202 MiB / 40.6% CPU · 427 MiB/s · 475.6 µs CPU/req)</sub> | *Not possible* | **3,191**<br><sub>(162 MiB / 56.2% CPU · 399 MiB/s · 704.4 µs CPU/req)</sub> |
| Duplex (both directions live) | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **3,548**<br><sub>(192 MiB / 38.9% CPU · 444 MiB/s · 438.3 µs CPU/req)</sub> | *Not possible* | **2,948**<br><sub>(163 MiB / 56.2% CPU · 369 MiB/s · 763.2 µs CPU/req)</sub> |
| Duplex (WebSocket / H1 Upgrade) | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **25,483**<br><sub>(99 MiB / 41.4% CPU · 65.0 µs CPU/req)</sub> | **12,803**<br><sub>(144 MiB / 24.6% CPU · 76.7 µs CPU/req)</sub> | **23,656**<br><sub>(89 MiB / 42.6% CPU · 72.1 µs CPU/req)</sub> |

#### Linux


| Scenario | Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---|---:|---:|---:|---:|---:|
| Slow consumer (256 KiB GET, throttled client read) | HTTP/1 · TLS | HTTP/1 · plain | **474**<br><sub>(119 MiB / 6.1% CPU · 118 MiB/s · 517.7 µs CPU/req)</sub> | **418**<br><sub>(100 MiB / 6.7% CPU · 105 MiB/s · 638.0 µs CPU/req)</sub> | **472**<br><sub>(86 MiB / 3.4% CPU · 118 MiB/s · 286.5 µs CPU/req)</sub> | 🥇 **475**<br><sub>(140 MiB / 4.2% CPU · 119 MiB/s · 353.1 µs CPU/req)</sub> | **402**<br><sub>(144 MiB / 9.7% CPU · 100 MiB/s · 964.9 µs CPU/req)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · TLS | HTTP/1 · plain | **477**<br><sub>(169 MiB / 14.5% CPU · 119 MiB/s · 1214.2 µs CPU/req)</sub> | **475**<br><sub>(101 MiB / 9.7% CPU · 119 MiB/s · 817.8 µs CPU/req)</sub> | **480**<br><sub>(85 MiB / 6.7% CPU · 120 MiB/s · 555.1 µs CPU/req)</sub> | 🥇 **480**<br><sub>(145 MiB / 6.4% CPU · 120 MiB/s · 536.4 µs CPU/req)</sub> | **477**<br><sub>(161 MiB / 16.6% CPU · 119 MiB/s · 1392.1 µs CPU/req)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/3 · QUIC | HTTP/1 · plain | **457**<br><sub>(144 MiB / 49.1% CPU · 114 MiB/s · 4292.2 µs CPU/req)</sub> | **465**<br><sub>(113 MiB / 26.1% CPU · 116 MiB/s · 2245.7 µs CPU/req)</sub> | **468**<br><sub>(92 MiB / 13.6% CPU · 117 MiB/s · 1161.9 µs CPU/req)</sub> | 🥇 **470**<br><sub>(151 MiB / 25.3% CPU · 117 MiB/s · 2158.4 µs CPU/req)</sub> | **452**<br><sub>(208 MiB / 51.7% CPU · 113 MiB/s · 4577.7 µs CPU/req)</sub> |
| Early response (origin writes after first request chunk) | HTTP/1 · TLS | HTTP/1 · plain | **4,792**<br><sub>(136 MiB / 47.3% CPU · 599 MiB/s · 394.9 µs CPU/req)</sub> | **3,654**<br><sub>(99 MiB / 50.8% CPU · 457 MiB/s · 555.6 µs CPU/req)</sub> | 🥇 **5,342**<br><sub>(89 MiB / 44.2% CPU · 668 MiB/s · 330.8 µs CPU/req)</sub> | **0**<br><sub>(peak 2,918 · 130 MiB / 26.3% CPU)</sub> | **3,176**<br><sub>(176 MiB / 56.2% CPU · 397 MiB/s · 707.9 µs CPU/req)</sub> |
| Early response (origin writes after first request chunk) | HTTP/2 · TLS | HTTP/1 · plain | **4,368**<br><sub>(268 MiB / 50.9% CPU · 546 MiB/s · 466.1 µs CPU/req)</sub> | **4,558**<br><sub>(peak 4,933 · 105 MiB / 42.6% CPU · 570 MiB/s · 373.8 µs CPU/req)</sub> | 🥇 **5,749**<br><sub>(90 MiB / 42.6% CPU · 719 MiB/s · 296.1 µs CPU/req)</sub> | **0**<br><sub>(peak 5,981 · 140 MiB / 41.6% CPU)</sub> | **4,901**<br><sub>(174 MiB / 49.1% CPU · 613 MiB/s · 401.1 µs CPU/req)</sub> |
| Early response (origin writes after first request chunk) | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **3,240**<br><sub>(247 MiB / 46.3% CPU · 405 MiB/s · 571.3 µs CPU/req)</sub> | **1,456**<br><sub>(117 MiB / 33.7% CPU · 182 MiB/s · 927.2 µs CPU/req)</sub> | **2,768**<br><sub>(95 MiB / 38.3% CPU · 346 MiB/s · 552.8 µs CPU/req)</sub> | **0**<br><sub>(peak 1 · 131 MiB / 0.2% CPU)</sub> | **2,118**<br><sub>(272 MiB / 50.3% CPU · 265 MiB/s · 949.4 µs CPU/req)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · plain | HTTP/1 · plain | **480**<br><sub>(156 MiB / 11.1% CPU · 120 MiB/s · 924.2 µs CPU/req)</sub> | **476**<br><sub>(78 MiB / 7.3% CPU · 119 MiB/s · 614.4 µs CPU/req)</sub> | 🥇 **480**<br><sub>(71 MiB / 4.3% CPU · 120 MiB/s · 355.0 µs CPU/req)</sub> | **480**<br><sub>(136 MiB / 5.0% CPU · 120 MiB/s · 416.7 µs CPU/req)</sub> | **476**<br><sub>(146 MiB / 11.1% CPU · 119 MiB/s · 928.0 µs CPU/req)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · TLS | HTTP/2 · TLS | **466**<br><sub>(214 MiB / 24.7% CPU · 117 MiB/s · 2119.2 µs CPU/req)</sub> | *Not possible* | **472**<br><sub>(94 MiB / 19.2% CPU · 118 MiB/s · 1629.5 µs CPU/req)</sub> | 🥇 **472**<br><sub>(140 MiB / 18.0% CPU · 118 MiB/s · 1527.3 µs CPU/req)</sub> | **467**<br><sub>(170 MiB / 30.2% CPU · 117 MiB/s · 2588.4 µs CPU/req)</sub> |
| Early response (origin writes after first request chunk) | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **2,719**<br><sub>(225 MiB / 41.3% CPU · 340 MiB/s · 608.1 µs CPU/req)</sub> | *Not possible* | **1,975**<br><sub>(92 MiB / 34.7% CPU · 247 MiB/s · 702.4 µs CPU/req)</sub> | **0**<br><sub>(peak 2,431 · 142 MiB / 36.6% CPU)</sub> | **1,891**<br><sub>(205 MiB / 53.8% CPU · 236 MiB/s · 1137.6 µs CPU/req)</sub> |
| Duplex (both directions live) | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **2,790**<br><sub>(229 MiB / 41.1% CPU · 349 MiB/s · 588.8 µs CPU/req)</sub> | *Not possible* | **2,056**<br><sub>(95 MiB / 36.0% CPU · 257 MiB/s · 700.2 µs CPU/req)</sub> | **0**<br><sub>(peak 2,309 · 142 MiB / 34.5% CPU)</sub> | **1,942**<br><sub>(201 MiB / 53.9% CPU · 243 MiB/s · 1110.4 µs CPU/req)</sub> |
| Duplex (WebSocket / H1 Upgrade) | HTTP/1 · TLS | HTTP/1 · plain | **38,303**<br><sub>(129 MiB / 44.4% CPU · 46.4 µs CPU/req)</sub> | 🥇 **42,732**<br><sub>(101 MiB / 36.6% CPU · 34.3 µs CPU/req)</sub> | **40,797**<br><sub>(82 MiB / 39.3% CPU · 38.5 µs CPU/req)</sub> | **38,975**<br><sub>(128 MiB / 41.5% CPU · 42.6 µs CPU/req)</sub> | **34,360**<br><sub>(126 MiB / 44.9% CPU · 52.2 µs CPU/req)</sub> |

#### macOS

Median of **3** repeats on `macos-15` (Apple Silicon M1, 3-core / 7 GB) @ `88187363`. Source: Actions [38550317747](https://github.com/justcoding121/titanium-web-proxy/actions/runs/38550317747) (`compare-arch`).

| Scenario | Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---|---:|---:|---:|---:|---:|
| Slow consumer (256 KiB GET, throttled client read) | HTTP/1 · TLS | HTTP/1 · plain | **98**<br><sub>(241 MiB / 5.0% CPU · 24 MiB/s · 1535.2 µs CPU/req)</sub> | 🥇 **109**<br><sub>(85 MiB / 0.9% CPU · 27 MiB/s · 258.0 µs CPU/req)</sub> | **99**<br><sub>(110 MiB / 1.1% CPU · 25 MiB/s · 331.3 µs CPU/req)</sub> | **84**<br><sub>(153 MiB / 1.2% CPU · 21 MiB/s · 441.9 µs CPU/req)</sub> | **95**<br><sub>(221 MiB / 3.1% CPU · 24 MiB/s · 985.3 µs CPU/req)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · TLS | HTTP/1 · plain | **96**<br><sub>(386 MiB / 6.5% CPU · 24 MiB/s · 2057.6 µs CPU/req)</sub> | 🥇 **104**<br><sub>(85 MiB / 1.8% CPU · 26 MiB/s · 528.4 µs CPU/req)</sub> | **96**<br><sub>(116 MiB / 2.2% CPU · 24 MiB/s · 700.0 µs CPU/req)</sub> | **88**<br><sub>(155 MiB / 2.0% CPU · 22 MiB/s · 671.2 µs CPU/req)</sub> | **88**<br><sub>(224 MiB / 5.0% CPU · 22 MiB/s · 1712.3 µs CPU/req)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/3 · QUIC | HTTP/1 · plain | **64**<br><sub>(132 MiB / 18.5% CPU · 16 MiB/s · 8722.1 µs CPU/req)</sub> | **104**<br><sub>(94 MiB / 4.6% CPU · 26 MiB/s · 1322.4 µs CPU/req)</sub> | 🥇 **122**<br><sub>(113 MiB / 11.4% CPU · 31 MiB/s · 2803.9 µs CPU/req)</sub> | **102**<br><sub>(149 MiB / 14.5% CPU · 26 MiB/s · 4255.6 µs CPU/req)</sub> | **80**<br><sub>(354 MiB / 16.9% CPU · 20 MiB/s · 6291.0 µs CPU/req)</sub> |
| Early response (origin writes after first request chunk) | HTTP/1 · TLS | HTTP/1 · plain | **2,837**<br><sub>(236 MiB / 22.3% CPU · 355 MiB/s · 235.4 µs CPU/req)</sub> | **0**<br><sub>(peak 3,234 · 89 MiB / 20.5% CPU)</sub> | 🥇 **3,800**<br><sub>(120 MiB / 28.5% CPU · 475 MiB/s · 224.8 µs CPU/req)</sub> | **0**<br><sub>(peak 3,445 · 131 MiB / 26.6% CPU)</sub> | **2,229**<br><sub>(205 MiB / 36.6% CPU · 279 MiB/s · 493.0 µs CPU/req)</sub> |
| Early response (origin writes after first request chunk) | HTTP/2 · TLS | HTTP/1 · plain | **2,443**<br><sub>(491 MiB / 30.8% CPU · 305 MiB/s · 378.6 µs CPU/req)</sub> | **2,712**<br><sub>(99 MiB / 17.6% CPU · 339 MiB/s · 194.2 µs CPU/req)</sub> | 🥇 **3,276**<br><sub>(125 MiB / 28.6% CPU · 410 MiB/s · 261.4 µs CPU/req)</sub> | **0**<br><sub>(peak 2,398 · 135 MiB / 23.0% CPU)</sub> | **1,544**<br><sub>(177 MiB / 22.0% CPU · 193 MiB/s · 426.6 µs CPU/req)</sub> |
| Early response (origin writes after first request chunk) | HTTP/3 · QUIC | HTTP/1 · plain | **600**<br><sub>(281 MiB / 25.6% CPU · 75 MiB/s · 1278.4 µs CPU/req)</sub> | 🥇 **957**<br><sub>(100 MiB / 20.0% CPU · 120 MiB/s · 626.3 µs CPU/req)</sub> | **452**<br><sub>(109 MiB / 25.8% CPU · 56 MiB/s · 1713.0 µs CPU/req)</sub> | **573**<br><sub>(134 MiB / 38.4% CPU · 72 MiB/s · 2013.4 µs CPU/req)</sub> | **348**<br><sub>(441 MiB / 26.1% CPU · 43 MiB/s · 2252.2 µs CPU/req)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · plain | HTTP/1 · plain | 🥇 **104**<br><sub>(228 MiB / 2.4% CPU · 26 MiB/s · 701.6 µs CPU/req)</sub> | **103**<br><sub>(73 MiB / 0.9% CPU · 26 MiB/s · 270.1 µs CPU/req)</sub> | **96**<br><sub>(102 MiB / 0.8% CPU · 24 MiB/s · 266.2 µs CPU/req)</sub> | **80**<br><sub>(145 MiB / 1.2% CPU · 20 MiB/s · 447.4 µs CPU/req)</sub> | **96**<br><sub>(145 MiB / 2.4% CPU · 24 MiB/s · 742.2 µs CPU/req)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **96**<br><sub>(476 MiB / 4.0% CPU · 24 MiB/s · 1241.9 µs CPU/req)</sub> | *Not possible* | **96**<br><sub>(125 MiB / 2.8% CPU · 24 MiB/s · 863.0 µs CPU/req)</sub> | **88**<br><sub>(131 MiB / 2.3% CPU · 22 MiB/s · 802.3 µs CPU/req)</sub> | **88**<br><sub>(350 MiB / 4.2% CPU · 22 MiB/s · 1434.9 µs CPU/req)</sub> |
| Early response (origin writes after first request chunk) | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **2,047**<br><sub>(472 MiB / 25.9% CPU · 256 MiB/s · 379.9 µs CPU/req)</sub> | *Not possible* | **1,258**<br><sub>(peak 1,377 · 115 MiB / 18.0% CPU · 157 MiB/s · 428.4 µs CPU/req)</sub> | **0**<br><sub>(peak 1,604 · 133 MiB / 23.3% CPU)</sub> | **1,474**<br><sub>(353 MiB / 28.6% CPU · 184 MiB/s · 581.7 µs CPU/req)</sub> |
| Duplex (both directions live) | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **1,796**<br><sub>(452 MiB / 22.9% CPU · 225 MiB/s · 382.8 µs CPU/req)</sub> | *Not possible* | **1,456**<br><sub>(119 MiB / 21.8% CPU · 182 MiB/s · 449.9 µs CPU/req)</sub> | **0**<br><sub>(peak 1,675 · 133 MiB / 23.7% CPU)</sub> | **1,708**<br><sub>(350 MiB / 32.1% CPU · 214 MiB/s · 563.3 µs CPU/req)</sub> |
| Duplex (WebSocket / H1 Upgrade) | HTTP/1 · TLS | HTTP/1 · plain | **12,802**<br><sub>(153 MiB / 14.1% CPU · 33.1 µs CPU/req)</sub> | 🥇 **20,969**<br><sub>(86 MiB / 15.3% CPU · 21.9 µs CPU/req)</sub> | **14,551**<br><sub>(103 MiB / 11.9% CPU · 24.5 µs CPU/req)</sub> | **8,266**<br><sub>(122 MiB / 10.3% CPU · 37.3 µs CPU/req)</sub> | **13,284**<br><sub>(148 MiB / 19.1% CPU · 43.2 µs CPU/req)</sub> |

Slow consumer is sleep-bound; H1/H2/H3 sit in the same band. Prefer the ratio columns for early-response, duplex, and WebSocket rows.

### TLS termination cost (H1 TLS → cleartext origin)

Isolates keep-alive tiny GET vs **new connection per request** (handshake-dominated) vs keep-alive **256 KiB**. Product comparison uses RPS and end-to-end latency; TWP can also capture `ClientTlsTiming` when `TWP_RPS_CAPTURE_TLS=1` (child process) — nginx/YARP have no equivalent hook.

#### Windows

Median of **3** repeats on `windows-latest` @ `88187363`. Source: Actions [38550312984](https://github.com/justcoding121/titanium-web-proxy/actions/runs/38550312984). Absolute RPS on GHA swings hard; prefer **TWP÷YARP**.


*Not possible:* **HAProxy** and **Envoy** columns are omitted (no official Windows port).

| Workload | TWP | nginx | YARP |
|---|---:|---:|---:|
| Keep-alive · tiny GET | 🥇 **20,020**<br><sub>(87 MiB / 49.9% CPU · 99.7 µs CPU/req)</sub> | **8,592**<br><sub>(142 MiB / 24.6% CPU · 114.8 µs CPU/req)</sub> | **18,086**<br><sub>(106 MiB / 50.7% CPU · 112.1 µs CPU/req)</sub> |
| New-connection · tiny GET | 🥇 **687**<br><sub>(82 MiB)</sub> | **245**<br><sub>(141 MiB / 1.8% CPU · 294.2 µs CPU/req)</sub> | **682**<br><sub>(110 MiB)</sub> |
| Keep-alive · 256 KiB GET | **4,146**<br><sub>(133 MiB / 34.2% CPU · 1,036 MiB/s · 329.7 µs CPU/req)</sub> | **313**<br><sub>(142 MiB / 24.6% CPU · 78 MiB/s · 3141.5 µs CPU/req)</sub> | 🥇 **4,347**<br><sub>(137 MiB / 39.7% CPU · 1,087 MiB/s · 365.3 µs CPU/req)</sub> |

#### Linux

Median of **3** repeats @ `88187363`. Source: Actions [38550312984](https://github.com/justcoding121/titanium-web-proxy/actions/runs/38550312984).


| Workload | TWP | nginx | HAProxy | Envoy | YARP |
|---|---:|---:|---:|---:|---:|
| Keep-alive · tiny GET | **23,709**<br><sub>(108 MiB / 50.0% CPU · 84.4 µs CPU/req)</sub> | 🥇 **28,097**<br><sub>(99 MiB / 41.7% CPU · 59.4 µs CPU/req)</sub> | **26,502**<br><sub>(82 MiB / 44.7% CPU · 67.5 µs CPU/req)</sub> | **16,671**<br><sub>(127 MiB / 58.8% CPU · 141.1 µs CPU/req)</sub> | **20,557**<br><sub>(135 MiB / 50.7% CPU · 98.7 µs CPU/req)</sub> |
| New-connection · tiny GET | **970**<br><sub>(121 MiB)</sub> | **1,006**<br><sub>(99 MiB / 1.0% CPU · 39.0 µs CPU/req)</sub> | **946**<br><sub>(84 MiB / 0.3% CPU · 12.7 µs CPU/req)</sub> | 🥇 **1,067**<br><sub>(130 MiB / 0.2% CPU · 9.4 µs CPU/req)</sub> | **963**<br><sub>(148 MiB / 0.2% CPU · 10.4 µs CPU/req)</sub> |
| Keep-alive · 256 KiB GET | **2,710**<br><sub>(124 MiB / 37.0% CPU · 678 MiB/s · 546.4 µs CPU/req)</sub> | **1,742**<br><sub>(98 MiB / 53.1% CPU · 436 MiB/s · 1219.5 µs CPU/req)</sub> | 🥇 **2,964**<br><sub>(86 MiB / 32.5% CPU · 741 MiB/s · 438.4 µs CPU/req)</sub> | **2,702**<br><sub>(145 MiB / 34.2% CPU · 676 MiB/s · 505.9 µs CPU/req)</sub> | **2,124**<br><sub>(162 MiB / 45.8% CPU · 531 MiB/s · 862.1 µs CPU/req)</sub> |

#### macOS

Median of **3** repeats on `macos-15` @ `88187363`. Source: Actions [38550312984](https://github.com/justcoding121/titanium-web-proxy/actions/runs/38550312984).

| Workload | TWP | nginx | HAProxy | Envoy | YARP |
|---|---:|---:|---:|---:|---:|
| Keep-alive · tiny GET | **5,953**<br><sub>(126 MiB / 11.9% CPU · 60.0 µs CPU/req)</sub> | **14,246**<br><sub>(84 MiB / 16.1% CPU · 33.9 µs CPU/req)</sub> | **9,554**<br><sub>(108 MiB / 10.5% CPU · 32.8 µs CPU/req)</sub> | **13,560**<br><sub>(122 MiB / 35.2% CPU · 77.9 µs CPU/req)</sub> | 🥇 **14,886**<br><sub>(160 MiB / 22.7% CPU · 45.8 µs CPU/req)</sub> |
| New-connection · tiny GET | **107**<br><sub>(108 MiB / 0.2% CPU · 44.7 µs CPU/req)</sub> | **609**<br><sub>(86 MiB)</sub> | **682**<br><sub>(105 MiB)</sub> | 🥇 **769**<br><sub>(132 MiB / 0.1% CPU · 2.0 µs CPU/req)</sub> | **114**<br><sub>(171 MiB / 0.2% CPU · 50.2 µs CPU/req)</sub> |
| Keep-alive · 256 KiB GET | **1,987**<br><sub>(206 MiB / 24.9% CPU · 497 MiB/s · 376.4 µs CPU/req)</sub> | **2,126**<br><sub>(84 MiB / 18.5% CPU · 532 MiB/s · 261.5 µs CPU/req)</sub> | 🥇 **2,394**<br><sub>(118 MiB / 23.2% CPU · 599 MiB/s · 291.3 µs CPU/req)</sub> | **1,909**<br><sub>(154 MiB / 22.2% CPU · 477 MiB/s · 348.7 µs CPU/req)</sub> | **1,680**<br><sub>(184 MiB / 28.0% CPU · 420 MiB/s · 500.8 µs CPU/req)</sub> |

Prefer **TWP÷YARP** in the table. Absolute RPS on GHA swings hard. New-connection is Darwin SslStream-bound for TWP and YARP (handshake p99 SLO **500 ms** on macOS only — Win/Linux stay at **200 ms**).

## Unary gRPC (H2 TLS)

Unary Echo **RPC/s** @ c=64 over H2 TLS→H2 TLS for Titanium, YARP, nginx (`grpc_pass`), HAProxy, and Envoy (OS-possible peers only). Not folded into the architecture-sensitive tables. Median of **3** repeats @ `88187363` — [38550284913](https://github.com/justcoding121/titanium-web-proxy/actions/runs/38550284913).

| OS | Titanium | YARP | nginx | HAProxy | Envoy |
|---|---:|---:|---:|---:|---:|
| Windows | 🥇 **42,728**<br><sub>(127 MiB / 32.9% CPU · 30.8 µs CPU/req)</sub> | **27,262**<br><sub>(124 MiB / 45.4% CPU · 66.6 µs CPU/req)</sub> | **0**<br><sub>(144 MiB / 24.9% CPU)</sub> | *Not possible* | *Not possible* |
| Linux | 🥇 **36,855**<br><sub>(169 MiB / 33.8% CPU · 36.7 µs CPU/req)</sub> | **23,039**<br><sub>(162 MiB / 42.5% CPU · 73.7 µs CPU/req)</sub> | **0**<br><sub>(110 MiB / 40.3% CPU)</sub> | **18,221**<br><sub>(83 MiB / 43.3% CPU · 95.0 µs CPU/req)</sub> | **21,370**<br><sub>(126 MiB / 52.9% CPU · 98.9 µs CPU/req)</sub> |
| macOS | 🥇 **10,349**<br><sub>(274 MiB / 11.9% CPU · 34.6 µs CPU/req)</sub> | **0**<br><sub>(209 MiB / 19.2% CPU)</sub> | **0**<br><sub>(83 MiB / 3.4% CPU)</sub> | **9,613**<br><sub>(116 MiB / 26.0% CPU · 81.0 µs CPU/req)</sub> | **0**<br><sub>(121 MiB / 20.7% CPU)</sub> |

## Unary gRPC (H2 TLS → h2c)

Unary Echo **RPC/s** @ c=64 over **H2 TLS → h2c** (edge TLS, cleartext H2 origin). Peers: Titanium, YARP, HAProxy, Envoy. Mode: `compare-grpc` (`*-grpc-h2c`). Median of **3** repeats @ `88187363` — [38550284913](https://github.com/justcoding121/titanium-web-proxy/actions/runs/38550284913).

*Not possible:* **nginx** column omitted (no H2 upstream).

| OS | Titanium | YARP | HAProxy | Envoy |
|---|---:|---:|---:|---:|
| Windows | 🥇 **48,373**<br><sub>(134 MiB / 30.0% CPU · 24.8 µs CPU/req)</sub> | **30,510**<br><sub>(119 MiB / 51.2% CPU · 67.1 µs CPU/req)</sub> | *Not possible* | *Not possible* |
| Linux | 🥇 **38,402**<br><sub>(166 MiB / 33.8% CPU · 35.2 µs CPU/req)</sub> | **23,085**<br><sub>(163 MiB / 44.0% CPU · 76.3 µs CPU/req)</sub> | **17,410**<br><sub>(81 MiB / 44.4% CPU · 102.1 µs CPU/req)</sub> | **17,851**<br><sub>(126 MiB / 56.9% CPU · 127.4 µs CPU/req)</sub> |
| macOS | 🥇 **9,974**<br><sub>(259 MiB / 15.3% CPU · 45.9 µs CPU/req)</sub> | **0**<br><sub>(224 MiB / 17.9% CPU)</sub> | **8,307**<br><sub>(117 MiB / 16.8% CPU · 60.5 µs CPU/req)</sub> | **0**<br><sub>(120 MiB / 15.4% CPU)</sub> |

## WebSocket (H1 TLS → H1 TLS)

WebSocket echo round-trips/sec over **dual-TLS** H1 (`proxy_ssl` style). Mode: `compare-ws-h1tls` (`*-duplex-ws-h1tls`). Median of **3** repeats @ `88187363`. Source: [38550328997](https://github.com/justcoding121/titanium-web-proxy/actions/runs/38550328997).

| OS | Titanium | YARP | nginx | HAProxy | Envoy |
|---|---:|---:|---:|---:|---:|
| Windows | 🥇 **21,671**<br><sub>(101 MiB / 43.8% CPU · 80.8 µs CPU/req)</sub> | **21,008**<br><sub>(94 MiB / 42.6% CPU · 81.2 µs CPU/req)</sub> | **8,984**<br><sub>(143 MiB / 24.9% CPU · 110.7 µs CPU/req)</sub> | *Not possible* | *Not possible* |
| Linux | **32,846**<br><sub>(144 MiB / 27.1% CPU · 33.0 µs CPU/req)</sub> | **30,950**<br><sub>(136 MiB / 29.9% CPU · 38.6 µs CPU/req)</sub> | 🥇 **44,021**<br><sub>(104 MiB / 23.1% CPU · 21.0 µs CPU/req)</sub> | **34,164**<br><sub>(86 MiB / 23.2% CPU · 27.2 µs CPU/req)</sub> | **37,939**<br><sub>(128 MiB / 24.4% CPU · 25.8 µs CPU/req)</sub> |
| macOS | **5,752**<br><sub>(158 MiB / 12.8% CPU · 66.9 µs CPU/req)</sub> | **6,926**<br><sub>(152 MiB / 15.1% CPU · 65.5 µs CPU/req)</sub> | **10,042**<br><sub>(94 MiB / 12.9% CPU · 38.4 µs CPU/req)</sub> | 🥇 **18,931**<br><sub>(106 MiB / 21.6% CPU · 34.3 µs CPU/req)</sub> | **10,632**<br><sub>(119 MiB / 21.3% CPU · 60.2 µs CPU/req)</sub> |

## WebSocket (H2 TLS 8441 → H1)

WebSocket echo over **RFC 8441** extended CONNECT (H2 TLS client → H1 plain origin). Mode: `compare-ws-h2` (`*-duplex-ws-h2`).

The probe client sets `NoDelay` and writes each HTTP/2 frame as one TLS record. Median of **3** repeats @ `88187363`. Source: [38550335474](https://github.com/justcoding121/titanium-web-proxy/actions/runs/38550335474).

*Not possible:* **nginx** column omitted (no RFC 8441 extended CONNECT).

| OS | Titanium | YARP | HAProxy | Envoy |
|---|---:|---:|---:|---:|
| Windows | 🥇 **47,603**<br><sub>(169 MiB / 32.5% CPU · 27.3 µs CPU/req)</sub> | **0**<br><sub>(178 MiB / 14.4% CPU)</sub> | *Not possible* | *Not possible* |
| Linux | 🥇 **41,546**<br><sub>(200 MiB / 49.4% CPU · 47.6 µs CPU/req)</sub> | **0**<br><sub>(192 MiB / 42.9% CPU)</sub> | **37,370**<br><sub>(84 MiB / 40.4% CPU · 43.3 µs CPU/req)</sub> | **0**<br><sub>(124 MiB / 0.3% CPU)</sub> |
| macOS | **6,345**<br><sub>(242 MiB / 16.4% CPU · 77.5 µs CPU/req)</sub> | **0**<br><sub>(177 MiB / 11.4% CPU)</sub> | 🥇 **9,226**<br><sub>(109 MiB / 18.2% CPU · 59.1 µs CPU/req)</sub> | **0**<br><sub>(116 MiB / 0.5% CPU)</sub> |

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

`--arm-shard` takes a comparison-group key (`h1c-h1c`, from `--print-groups`), not an `i/n` index, so a re-run measures the same row after a peer install flake. `i/n` remains for smoke modes. Paste unions `rps-csv-<os>-shard-*` (`paste-compare-product-wiki.ps1 -RunIds …`). Table “Source: Actions” may list several run URLs for one table — **do not mix SHAs**. A single-row leg allows **60** minutes of ramp and **75** minutes overall. Free GitHub accounts run **20** jobs at once, **5** of them macOS. Give a full suite a quiet window: do not push pull requests or start other macOS jobs while it runs. Re-run infrastructure failures only (`gh run rerun --failed`). A failed gate is a finding and is never re-rolled; publish the last attempt, never the best of several. The RPS tables on this page are @ `88187363`: product [38550284913](https://github.com/justcoding121/titanium-web-proxy/actions/runs/38550284913), saturation [38550341872](https://github.com/justcoding121/titanium-web-proxy/actions/runs/38550341872), bodies [38550291997](https://github.com/justcoding121/titanium-web-proxy/actions/runs/38550291997), POST [38550300822](https://github.com/justcoding121/titanium-web-proxy/actions/runs/38550300822), lossy [38550307008](https://github.com/justcoding121/titanium-web-proxy/actions/runs/38550307008), arch [38550317747](https://github.com/justcoding121/titanium-web-proxy/actions/runs/38550317747), TLS cost [38550312984](https://github.com/justcoding121/titanium-web-proxy/actions/runs/38550312984), gRPC [38550322348](https://github.com/justcoding121/titanium-web-proxy/actions/runs/38550322348), WebSocket H1 TLS [38550328997](https://github.com/justcoding121/titanium-web-proxy/actions/runs/38550328997), WebSocket H2 [38550335474](https://github.com/justcoding121/titanium-web-proxy/actions/runs/38550335474), editions [38550348130](https://github.com/justcoding121/titanium-web-proxy/actions/runs/38550348130). Suite aggregates for product, arch, and lossy failed post-ramp gates on a cluster of legs: Linux lossy H2 TLS→H2 TLS was about **0.35×** three peers that agreed with each other; Windows H3→H2 TLS slow-consumer was **0.27×** YARP; macOS had four reverse wires at or under **0.50×** YARP (H2 TLS→h2c **0.36**, H3→h2c **0.37**, H2 TLS→H3 **0.47**, H1 plain→H3 **0.50**), and on H3→h2c the peers rose while Titanium fell versus `347a9a1b`. Published cells stay that last attempt. Gate floors are unchanged. The follow-up is a same-VM paired A/B of `347a9a1b` against `88187363` on Linux, plus a same-job peer-ratio re-measure of the macOS and Windows legs. Reverse product rows, bodies, POST, lossy, arch, TLS cost, gRPC, both WebSocket modes, and saturation run on Windows, Linux, and macOS. TWP MITM arms run on Linux only. `compare-editions` (CLI and Plus) runs on Linux only. `compare-spot` on pull requests to beta and stable stays on all three OS. The historical v2 MITM note below the product tables stays @ `41f4adee` because those run ids are from that fix, not this re-measure.

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
