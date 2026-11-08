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
- **Typical reverse** is H1 TLS→H1 or H2→H1 (~1.1× YARP on Win/Linux tiny GET). With **larger bodies**, see [Heavier reverse](#user-content-heavier-reverse-workloads) (@ `b6700ebe`).

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

On the Linux H2 TLS→H1 plain row at c=64 (16 connections, @ `b6700ebe`), proxy CPU was about **44%** for nginx, **45%** for HAProxy, and **60%** for Envoy. Windows nginx on the same H2 client shape stayed near **25%** (one core). That measured shape is the one published here.

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

Calibration for the shared 4 vCPU loopback shape: how close client + origin are to saturated before ranking reverse peers. Tiny keep-alive GET. Median of **3** repeats @ `b6700ebe` — [42394388112](https://github.com/justcoding121/titanium-web-proxy/actions/runs/42394388112). Warmup 2s / measure 8s; concurrency 8, 16, 32, 64. Block A **% of origin-HttpClient** uses median **peak** RPS. Blocks B/C use peer÷YARP / ÷nginx on median peak (not % of H1 origin). **RPS cells** embed median RSS / CPU for the **proxy child** plus its **full descendant tree** (serve-proxy → nginx master → workers); origin-direct samples the **origin** child. Product matrices below use matched `dotnet-httpclient` only (not bombardier).


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
| origin-direct | dotnet-httpclient | **50,571**<br><sub>(55 MiB / 40.4% CPU · 32.0 µs CPU/req)</sub> | **100.0%** |
| origin-direct-bombardier | bombardier | **38,450**<br><sub>(56 MiB / 21.5% CPU · 22.4 µs CPU/req)</sub> | **76.0%** |
| bare-reverse-http1 | dotnet-httpclient | **25,484**<br><sub>(56 MiB / 46.0% CPU · 72.2 µs CPU/req)</sub> | **50.4%** |
| nginx-reverse-http1 | dotnet-httpclient | **13,572**<br><sub>(127 MiB / 24.9% CPU · 73.5 µs CPU/req)</sub> | **26.8%** |
| yarp-reverse-http1 | dotnet-httpclient | **21,312**<br><sub>(88 MiB / 47.9% CPU · 90.0 µs CPU/req)</sub> | **42.1%** |
| twp-reverse-http1 | dotnet-httpclient | 🥇 **25,576**<br><sub>(83 MiB / 49.2% CPU · 77.0 µs CPU/req)</sub> | **50.6%** |

**Linux** (`ubuntu-latest`)

| Arm | Generator | RPS | % of origin-HttpClient |
|---|---|---:|---:|
| origin-direct | dotnet-httpclient | **132,411**<br><sub>(82 MiB / 43.8% CPU · 13.2 µs CPU/req)</sub> | **100.0%** |
| origin-direct-bombardier | bombardier | **81,813**<br><sub>(81 MiB / 36.4% CPU · 17.8 µs CPU/req)</sub> | **61.8%** |
| bare-reverse-http1 | dotnet-httpclient | **59,810**<br><sub>(69 MiB / 45.2% CPU · 30.2 µs CPU/req)</sub> | **45.2%** |
| nginx-reverse-http1 | dotnet-httpclient | 🥇 **77,844**<br><sub>(79 MiB / 40.8% CPU · 21.0 µs CPU/req)</sub> | **58.8%** |
| yarp-reverse-http1 | dotnet-httpclient | **56,966**<br><sub>(122 MiB / 46.4% CPU · 32.6 µs CPU/req)</sub> | **43.0%** |
| twp-reverse-http1 | dotnet-httpclient | **62,052**<br><sub>(85 MiB / 47.7% CPU · 30.8 µs CPU/req)</sub> | **46.9%** |


**macOS** (`macos-15`, Apple Silicon)

| Arm | Generator | RPS | % of origin-HttpClient |
|---|---|---:|---:|
| origin-direct | dotnet-httpclient | **79,925**<br><sub>(89 MiB / 33.6% CPU · 16.8 µs CPU/req)</sub> | **100.0%** |
| origin-direct-bombardier | bombardier | **56,250**<br><sub>(89 MiB / 24.7% CPU · 17.6 µs CPU/req)</sub> | **70.4%** |
| bare-reverse-http1 | dotnet-httpclient | **27,005**<br><sub>(92 MiB / 29.3% CPU · 43.4 µs CPU/req)</sub> | **33.8%** |
| nginx-reverse-http1 | dotnet-httpclient | 🥇 **35,324**<br><sub>(67 MiB / 21.3% CPU · 24.1 µs CPU/req)</sub> | **44.2%** |
| yarp-reverse-http1 | dotnet-httpclient | **28,200**<br><sub>(148 MiB / 36.8% CPU · 52.3 µs CPU/req)</sub> | **35.3%** |
| twp-reverse-http1 | dotnet-httpclient | **23,725**<br><sub>(117 MiB / 31.6% CPU · 53.3 µs CPU/req)</sub> | **29.7%** |

On this macOS run `origin-direct` was **33.6%** CPU, so the % of origin-HttpClient column is not a ceiling when that CPU is near idle. Rank those Mac peers by RPS.

Reverse peers on Block A @ `b6700ebe`: Windows TWP is **50.6%** of origin-direct and Linux TWP is **46.9%**. Prefer the **%** column over absolute RPS across runs. Bare and origin-direct are controls (not medal peers).

#### Block B — H2 TLS→H1

Peer ratios (÷YARP / ÷nginx) on median peak; **RPS cells** embed `(MiB / CPU%)`.

**Windows** (`windows-latest`)

| Arm | Generator | RPS | ÷YARP | ÷nginx |
|---|---|---:|---:|---:|
| nginx-reverse-http2 | dotnet-httpclient | **7,635**<br><sub>(144 MiB / 24.6% CPU · 129.1 µs CPU/req)</sub> | **0.28×** | **1.00×** |
| yarp-reverse-http2 | dotnet-httpclient | **27,390**<br><sub>(105 MiB / 55.5% CPU · 81.0 µs CPU/req)</sub> | **1.00×** | **3.59×** |
| twp-reverse-http2-cleartext | dotnet-httpclient | 🥇 **29,652**<br><sub>(123 MiB / 54.8% CPU · 73.9 µs CPU/req)</sub> | **1.08×** | **3.88×** |

**Linux** (`ubuntu-latest`)

| Arm | Generator | RPS | ÷YARP | ÷nginx |
|---|---|---:|---:|---:|
| nginx-reverse-http2 | dotnet-httpclient | **55,330**<br><sub>(110 MiB / 43.2% CPU · 31.2 µs CPU/req)</sub> | **0.90×** | **1.00×** |
| yarp-reverse-http2 | dotnet-httpclient | **61,799**<br><sub>(138 MiB / 47.1% CPU · 30.5 µs CPU/req)</sub> | **1.00×** | **1.12×** |
| twp-reverse-http2-cleartext | dotnet-httpclient | 🥇 **63,922**<br><sub>(159 MiB / 49.7% CPU · 31.1 µs CPU/req)</sub> | **1.03×** | **1.16×** |

**macOS** (`macos-15`, Apple Silicon)

| Arm | Generator | RPS | ÷YARP | ÷nginx |
|---|---|---:|---:|---:|
| nginx-reverse-http2 | dotnet-httpclient | **29,491**<br><sub>(83 MiB / 25.1% CPU · 34.0 µs CPU/req)</sub> | **0.83×** | **1.00×** |
| yarp-reverse-http2 | dotnet-httpclient | 🥇 **35,682**<br><sub>(156 MiB / 40.9% CPU · 45.8 µs CPU/req)</sub> | **1.00×** | **1.21×** |
| twp-reverse-http2-cleartext | dotnet-httpclient | **33,429**<br><sub>(171 MiB / 41.4% CPU · 49.5 µs CPU/req)</sub> | **0.94×** | **1.13×** |
#### Block C — H3→H1

Same layout as Block B. Requires QuicListener. nginx needs `http_v3_module` (Windows nginx has no QUIC). HAProxy needs `USE_QUIC` (GHA Linux/macOS require it). Envoy 1.20+ includes HTTP/3.

Medals are nginx / YARP / Titanium only, from the same run as Blocks A and B ([40610501155](https://github.com/justcoding121/titanium-web-proxy/actions/runs/40610501155)).

**Windows** (`windows-latest`)

| Arm | Generator | RPS | ÷YARP | ÷nginx |
|---|---|---:|---:|---:|
| nginx-reverse-http3-cleartext | dotnet-httpclient | *Not possible (no QUIC)* | — | — |
| yarp-reverse-http3-cleartext | dotnet-httpclient | **8,335**<br><sub>(153 MiB / 51.4% CPU · 246.7 µs CPU/req)</sub> | **1.00×** | — |
| twp-reverse-http3-cleartext | dotnet-httpclient | 🥇 **8,951**<br><sub>(113 MiB / 48.8% CPU · 217.9 µs CPU/req)</sub> | **1.07×** | — |

**Linux** (`ubuntu-latest`)

| Arm | Generator | RPS | ÷YARP | ÷nginx |
|---|---|---:|---:|---:|
| nginx-reverse-http3-cleartext | dotnet-httpclient | 🥇 **47,493**<br><sub>(115 MiB / 30.7% CPU · 25.8 µs CPU/req)</sub> | **1.53×** | **1.00×** |
| yarp-reverse-http3-cleartext | dotnet-httpclient | **31,122**<br><sub>(213 MiB / 47.5% CPU · 61.1 µs CPU/req)</sub> | **1.00×** | **0.66×** |
| twp-reverse-http3-cleartext | dotnet-httpclient | **36,165**<br><sub>(176 MiB / 52.9% CPU · 58.5 µs CPU/req)</sub> | **1.16×** | **0.76×** |

**macOS** (`macos-15`, Apple Silicon)

| Arm | Generator | RPS | ÷YARP | ÷nginx |
|---|---|---:|---:|---:|
| nginx-reverse-http3-cleartext | dotnet-httpclient | **12,973**<br><sub>(peak 24,041 · 83 MiB / 21.9% CPU · 67.6 µs CPU/req)</sub> | **1.58×** | **1.00×** |
| yarp-reverse-http3-cleartext | dotnet-httpclient | 🥇 **15,237**<br><sub>(203 MiB / 45.0% CPU · 118.1 µs CPU/req)</sub> | **1.00×** | **0.63×** |
| twp-reverse-http3-cleartext | dotnet-httpclient | **13,044**<br><sub>(144 MiB / 42.5% CPU · 130.2 µs CPU/req)</sub> | **0.86×** | **0.54×** |

## Windows — Titanium vs nginx vs HAProxy vs Envoy vs YARP

Client / origin: HTTP version and whether TLS is used (`plain` = cleartext, `TLS` = encrypted, `QUIC` = HTTP/3).

### Reverse

Median of **3 repeats** on `windows-latest` (4 vCPU / 16 GiB). Bare reverse 5×5 @ `b6700ebe` — `compare-product` [42394333642](https://github.com/justcoding121/titanium-web-proxy/actions/runs/42394333642). Warmup 2s / measure 8s; concurrency 8, 16, 32, 64. Prefer TWP÷peer ratios over absolute RPS. **RPS cells** include median RSS / CPU at the peak-RPS step as `<br><sub>(MiB / CPU%)</sub>`. nginx terminate peers use `keepalive 256` + streaming buffers. **HAProxy / Envoy are Linux-only peers** (no official Windows port). Laptop High-perf / cool-paired numbers stay on the [local lab](Performance-Local-Lab). Product 5×5 is **~56-byte JSON keep-alive GET**; H2/H3 same-protocol cells are mostly header work with a tiny body (Titanium best case) — see [Why this comparison is fair](#user-content-why-this-comparison-is-fair).

**Load generators:** Reverse inbound H3 arms use **`dotnet-httpclient`** (`http_version=3.0`, `RequestVersionExact`). nginx/Windows is same-OS only (no QUIC). HAProxy/Envoy are Linux-only terminate peers.

| Client | Origin | TWP | nginx | YARP |
|---|---|---:|---:|---:|
| HTTP/1 · plain | HTTP/1 · plain | 🥇 **25,245**<br><sub>(78 MiB / 48.2% CPU · 76.4 µs CPU/req)</sub> | **12,900**<br><sub>(127 MiB / 24.4% CPU · 75.7 µs CPU/req)</sub> | **20,976**<br><sub>(86 MiB / 48.5% CPU · 92.5 µs CPU/req)</sub> |
| HTTP/1 · plain | HTTP/1 · TLS | 🥇 **21,086**<br><sub>(94 MiB / 50.2% CPU · 95.3 µs CPU/req)</sub> | **8,342**<br><sub>(137 MiB / 24.6% CPU · 118.1 µs CPU/req)</sub> | **18,956**<br><sub>(100 MiB / 50.1% CPU · 105.7 µs CPU/req)</sub> |
| HTTP/1 · plain | HTTP/2 · plain | 🥇 **37,568**<br><sub>(105 MiB / 47.9% CPU · 51.0 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **32,941**<br><sub>(92 MiB / 47.3% CPU · 57.5 µs CPU/req)</sub> |
| HTTP/1 · plain | HTTP/2 · TLS | 🥇 **77,810**<br><sub>(135 MiB / 48.4% CPU · 24.9 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **72,196**<br><sub>(108 MiB / 50.6% CPU · 28.0 µs CPU/req)</sub> |
| HTTP/1 · plain | HTTP/3 · QUIC | **22,209**<br><sub>(115 MiB / 49% CPU · 88.3 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | 🥇 **22,286**<br><sub>(122 MiB / 48.5% CPU · 87.0 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/1 · plain | 🥇 **34,281**<br><sub>(86 MiB / 45.7% CPU · 53.3 µs CPU/req)</sub> | **16,182**<br><sub>(144 MiB / 24.9% CPU · 61.5 µs CPU/req)</sub> | **29,665**<br><sub>(102 MiB / 50.3% CPU · 67.8 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/1 · TLS | 🥇 **31,801**<br><sub>(88 MiB / 45.5% CPU · 57.2 µs CPU/req)</sub> | **12,319**<br><sub>(146 MiB / 24.7% CPU · 80.1 µs CPU/req)</sub> | **27,550**<br><sub>(103 MiB / 48% CPU · 69.6 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/2 · plain | 🥇 **46,033**<br><sub>(119 MiB / 45.3% CPU · 39.4 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **43,299**<br><sub>(111 MiB / 48.4% CPU · 44.7 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/2 · TLS | 🥇 **44,288**<br><sub>(142 MiB / 47.4% CPU · 42.8 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **43,640**<br><sub>(110 MiB / 47.7% CPU · 43.7 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/3 · QUIC | 🥇 **37,232**<br><sub>(131 MiB / 50.4% CPU · 54.2 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | **36,272**<br><sub>(149 MiB / 50.8% CPU · 56.1 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | 🥇 **32,721**<br><sub>(112 MiB / 51.7% CPU · 63.2 µs CPU/req)</sub> | **8,884**<br><sub>(129 MiB / 24.8% CPU · 111.5 µs CPU/req)</sub> | **29,830**<br><sub>(94 MiB / 51.1% CPU · 68.5 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/1 · TLS | 🥇 **68,514**<br><sub>(129 MiB / 47.7% CPU · 27.8 µs CPU/req)</sub> | **16,821**<br><sub>(139 MiB / 24.9% CPU · 59.1 µs CPU/req)</sub> | **64,886**<br><sub>(99 MiB / 50.1% CPU · 30.9 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/2 · plain | 🥇 **103,658**<br><sub>(96 MiB / 35.8% CPU · 13.8 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **73,490**<br><sub>(108 MiB / 51.9% CPU · 28.2 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/2 · TLS | 🥇 **84,816**<br><sub>(108 MiB / 35.1% CPU · 16.6 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **64,129**<br><sub>(100 MiB / 49.4% CPU · 30.8 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/3 · QUIC | 🥇 **24,982**<br><sub>(138 MiB / 52.3% CPU · 83.8 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | **23,799**<br><sub>(122 MiB / 51.9% CPU · 87.2 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **45,128**<br><sub>(123 MiB / 53.9% CPU · 47.8 µs CPU/req)</sub> | **13,070**<br><sub>(144 MiB / 24.9% CPU · 76.1 µs CPU/req)</sub> | **41,463**<br><sub>(105 MiB / 50% CPU · 48.2 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/1 · TLS | 🥇 **39,717**<br><sub>(136 MiB / 49.8% CPU · 50.2 µs CPU/req)</sub> | **13,593**<br><sub>(147 MiB / 24.9% CPU · 73.2 µs CPU/req)</sub> | **36,599**<br><sub>(106 MiB / 50% CPU · 54.6 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | 🥇 **88,404**<br><sub>(130 MiB / 37% CPU · 16.7 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **64,926**<br><sub>(112 MiB / 51.4% CPU · 31.7 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **65,786**<br><sub>(131 MiB / 35.1% CPU · 21.4 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **47,854**<br><sub>(112 MiB / 50.3% CPU · 42.1 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/3 · QUIC | 🥇 **32,721**<br><sub>(156 MiB / 53.3% CPU · 65.2 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | **29,836**<br><sub>(133 MiB / 54.1% CPU · 72.5 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **9,239**<br><sub>(114 MiB / 50.5% CPU · 218.7 µs CPU/req)</sub> | *Not possible (no QUIC)* | **7,685**<br><sub>(154 MiB / 48.5% CPU · 252.4 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **13,542**<br><sub>(120 MiB / 50.8% CPU · 150.0 µs CPU/req)</sub> | *Not possible (no QUIC)* | **12,452**<br><sub>(160 MiB / 53% CPU · 170.3 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/2 · plain | 🥇 **36,534**<br><sub>(137 MiB / 51.9% CPU · 56.9 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **24,260**<br><sub>(178 MiB / 48.8% CPU · 80.4 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | 🥇 **18,345**<br><sub>(127 MiB / 49.3% CPU · 107.4 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **14,824**<br><sub>(163 MiB / 50.2% CPU · 135.6 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/3 · QUIC | 🥇 **20,894**<br><sub>(118 MiB / 0.1% CPU · 0.2 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | **17,801**<br><sub>(176 MiB / 48.4% CPU · 108.8 µs CPU/req)</sub> |

## Linux — Titanium vs nginx vs HAProxy vs Envoy vs YARP

### Reverse

Median of **3 repeats** on `ubuntu-latest` (4 vCPU / 16 GiB). Bare reverse 5×5 @ `b6700ebe` — `compare-product` [42394333642](https://github.com/justcoding121/titanium-web-proxy/actions/runs/42394333642). Warmup 2s / measure 8s; concurrency 8, 16, 32, 64. **Linux nginx is the authoritative nginx baseline.** HAProxy (3.2 `USE_QUIC`) and Envoy (GitHub release, HTTP/3 compiled in) run on the same loopback shape as nginx/YARP. nginx terminate peers use `keepalive 256` + streaming buffers. The RPS workflow installs nginx.org mainline (`http_v3_module`), a QUIC-enabled HAProxy, Envoy, and `libmsquic`. Prefer ratios over absolute RPS. Product 5×5 is **~56-byte JSON keep-alive GET**; H2/H3 same-protocol cells are mostly header work with a tiny body (Titanium best case) — see [Why this comparison is fair](#user-content-why-this-comparison-is-fair).

| Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---:|---:|---:|---:|---:|
| HTTP/1 · plain | HTTP/1 · plain | **74,166**<br><sub>(85 MiB / 44.9% CPU · 24.2 µs CPU/req)</sub> | 🥇 **101,573**<br><sub>(79 MiB / 38.2% CPU · 15.0 µs CPU/req)</sub> | **86,995**<br><sub>(65 MiB / 43.7% CPU · 20.1 µs CPU/req)</sub> | **52,865**<br><sub>(110 MiB / 61.5% CPU · 46.5 µs CPU/req)</sub> | **67,151**<br><sub>(116 MiB / 48.7% CPU · 29.0 µs CPU/req)</sub> |
| HTTP/1 · plain | HTTP/1 · TLS | **57,360**<br><sub>(109 MiB / 46.8% CPU · 32.6 µs CPU/req)</sub> | 🥇 **67,493**<br><sub>(102 MiB / 43.2% CPU · 25.6 µs CPU/req)</sub> | **61,964**<br><sub>(70 MiB / 44% CPU · 28.4 µs CPU/req)</sub> | **49,167**<br><sub>(111 MiB / 53.7% CPU · 43.7 µs CPU/req)</sub> | **53,367**<br><sub>(134 MiB / 46.9% CPU · 35.2 µs CPU/req)</sub> |
| HTTP/1 · plain | HTTP/2 · plain | 🥇 **41,776**<br><sub>(131 MiB / 50% CPU · 47.9 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **27,040**<br><sub>(66 MiB / 45.1% CPU · 66.7 µs CPU/req)</sub> | **21,862**<br><sub>(110 MiB / 64.3% CPU · 117.7 µs CPU/req)</sub> | **37,505**<br><sub>(128 MiB / 46.7% CPU · 49.8 µs CPU/req)</sub> |
| HTTP/1 · plain | HTTP/2 · TLS | 🥇 **35,089**<br><sub>(159 MiB / 47.8% CPU · 54.5 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **28,682**<br><sub>(67 MiB / 48.7% CPU · 68.0 µs CPU/req)</sub> | **20,990**<br><sub>(111 MiB / 63% CPU · 120.1 µs CPU/req)</sub> | **34,015**<br><sub>(132 MiB / 47.7% CPU · 56.1 µs CPU/req)</sub> |
| HTTP/1 · plain | HTTP/3 · QUIC | **22,689**<br><sub>(143 MiB / 52.5% CPU · 92.6 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **10,527**<br><sub>(114 MiB / 58.6% CPU · 222.8 µs CPU/req)</sub> | 🥇 **22,841**<br><sub>(154 MiB / 48.4% CPU · 84.7 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/1 · plain | **26,023**<br><sub>(117 MiB / 48.5% CPU · 74.5 µs CPU/req)</sub> | 🥇 **29,974**<br><sub>(109 MiB / 42.9% CPU · 57.2 µs CPU/req)</sub> | **28,315**<br><sub>(84 MiB / 45.7% CPU · 64.6 µs CPU/req)</sub> | **17,702**<br><sub>(122 MiB / 60.6% CPU · 136.9 µs CPU/req)</sub> | **22,636**<br><sub>(139 MiB / 51.1% CPU · 90.3 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/1 · TLS | **21,571**<br><sub>(114 MiB / 47.2% CPU · 87.5 µs CPU/req)</sub> | 🥇 **24,988**<br><sub>(114 MiB / 42.7% CPU · 68.4 µs CPU/req)</sub> | **23,533**<br><sub>(86 MiB / 44.8% CPU · 76.1 µs CPU/req)</sub> | **16,100**<br><sub>(122 MiB / 58.1% CPU · 144.4 µs CPU/req)</sub> | **18,644**<br><sub>(150 MiB / 50.2% CPU · 107.8 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/2 · plain | 🥇 **47,338**<br><sub>(159 MiB / 50% CPU · 42.2 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **33,636**<br><sub>(85 MiB / 44.5% CPU · 52.9 µs CPU/req)</sub> | **29,741**<br><sub>(122 MiB / 60.7% CPU · 81.6 µs CPU/req)</sub> | **44,876**<br><sub>(149 MiB / 48.9% CPU · 43.6 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/2 · TLS | 🥇 **25,252**<br><sub>(162 MiB / 46.3% CPU · 73.4 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **24,079**<br><sub>(85 MiB / 46% CPU · 76.4 µs CPU/req)</sub> | **17,383**<br><sub>(122 MiB / 59.7% CPU · 137.4 µs CPU/req)</sub> | **24,696**<br><sub>(145 MiB / 47.8% CPU · 77.5 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/3 · QUIC | 🥇 **28,553**<br><sub>(181 MiB / 52.4% CPU · 73.4 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **14,474**<br><sub>(125 MiB / 55.4% CPU · 153.1 µs CPU/req)</sub> | **27,918**<br><sub>(171 MiB / 49% CPU · 70.3 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | 🥇 **39,696**<br><sub>(148 MiB / 52.3% CPU · 52.7 µs CPU/req)</sub> | **31,705**<br><sub>(81 MiB / 47% CPU · 59.2 µs CPU/req)</sub> | **38,590**<br><sub>(65 MiB / 46.4% CPU · 48.1 µs CPU/req)</sub> | **21,525**<br><sub>(110 MiB / 66.4% CPU · 123.4 µs CPU/req)</sub> | **35,710**<br><sub>(125 MiB / 51% CPU · 57.2 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/1 · TLS | **28,422**<br><sub>(156 MiB / 50.4% CPU · 70.9 µs CPU/req)</sub> | **23,912**<br><sub>(105 MiB / 47.1% CPU · 78.7 µs CPU/req)</sub> | 🥇 **29,080**<br><sub>(70 MiB / 45.9% CPU · 63.2 µs CPU/req)</sub> | **18,105**<br><sub>(112 MiB / 61.7% CPU · 136.3 µs CPU/req)</sub> | **26,154**<br><sub>(138 MiB / 50.4% CPU · 77.1 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/2 · plain | 🥇 **83,668**<br><sub>(148 MiB / 34.3% CPU · 16.4 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **55,902**<br><sub>(65 MiB / 45.1% CPU · 32.3 µs CPU/req)</sub> | **29,485**<br><sub>(110 MiB / 60.8% CPU · 82.6 µs CPU/req)</sub> | **52,130**<br><sub>(135 MiB / 48.1% CPU · 36.9 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/2 · TLS | 🥇 **56,568**<br><sub>(159 MiB / 39% CPU · 27.6 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **30,935**<br><sub>(67 MiB / 47% CPU · 60.7 µs CPU/req)</sub> | **22,654**<br><sub>(110 MiB / 61.8% CPU · 109.0 µs CPU/req)</sub> | **40,074**<br><sub>(136 MiB / 47.2% CPU · 47.1 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/3 · QUIC | 🥇 **49,018**<br><sub>(203 MiB / 51.6% CPU · 42.1 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **28,981**<br><sub>(113 MiB / 48.9% CPU · 67.5 µs CPU/req)</sub> | **46,764**<br><sub>(179 MiB / 45.2% CPU · 38.7 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | **47,116**<br><sub>(167 MiB / 51% CPU · 43.3 µs CPU/req)</sub> | **42,585**<br><sub>(110 MiB / 43.5% CPU · 40.9 µs CPU/req)</sub> | 🥇 **47,443**<br><sub>(83 MiB / 45.4% CPU · 38.3 µs CPU/req)</sub> | **31,703**<br><sub>(122 MiB / 60.2% CPU · 76.0 µs CPU/req)</sub> | **44,073**<br><sub>(134 MiB / 48.7% CPU · 44.2 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/1 · TLS | 🥇 **26,299**<br><sub>(159 MiB / 49.9% CPU · 75.9 µs CPU/req)</sub> | **22,771**<br><sub>(117 MiB / 44.9% CPU · 78.8 µs CPU/req)</sub> | **25,692**<br><sub>(84 MiB / 46.5% CPU · 72.4 µs CPU/req)</sub> | **17,662**<br><sub>(122 MiB / 61.6% CPU · 139.6 µs CPU/req)</sub> | **23,531**<br><sub>(146 MiB / 50.3% CPU · 85.6 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | 🥇 **63,964**<br><sub>(163 MiB / 40.2% CPU · 25.2 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **36,073**<br><sub>(83 MiB / 46.1% CPU · 51.1 µs CPU/req)</sub> | **29,942**<br><sub>(121 MiB / 56.8% CPU · 75.8 µs CPU/req)</sub> | **44,813**<br><sub>(145 MiB / 48.1% CPU · 43.0 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **105,530**<br><sub>(180 MiB / 39.5% CPU · 15.0 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **60,235**<br><sub>(84 MiB / 45.6% CPU · 30.3 µs CPU/req)</sub> | **60,465**<br><sub>(121 MiB / 51.8% CPU · 34.3 µs CPU/req)</sub> | **73,700**<br><sub>(147 MiB / 44.6% CPU · 24.2 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/3 · QUIC | 🥇 **24,611**<br><sub>(186 MiB / 50.4% CPU · 81.8 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **10,068**<br><sub>(124 MiB / 57% CPU · 226.5 µs CPU/req)</sub> | **22,695**<br><sub>(164 MiB / 47.8% CPU · 84.2 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | **27,631**<br><sub>(162 MiB / 52.9% CPU · 76.5 µs CPU/req)</sub> | **33,715**<br><sub>(114 MiB / 31.1% CPU · 36.9 µs CPU/req)</sub> | 🥇 **34,530**<br><sub>(88 MiB / 34.6% CPU · 40.0 µs CPU/req)</sub> | **13,662**<br><sub>(128 MiB / 60.1% CPU · 176.0 µs CPU/req)</sub> | **24,096**<br><sub>(204 MiB / 49.5% CPU · 82.2 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | **22,404**<br><sub>(172 MiB / 51.9% CPU · 92.6 µs CPU/req)</sub> | **27,474**<br><sub>(123 MiB / 33.1% CPU · 48.2 µs CPU/req)</sub> | 🥇 **30,472**<br><sub>(90 MiB / 36.5% CPU · 47.9 µs CPU/req)</sub> | **11,639**<br><sub>(128 MiB / 56.6% CPU · 194.5 µs CPU/req)</sub> | **19,551**<br><sub>(213 MiB / 50% CPU · 102.4 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/2 · plain | **20,461**<br><sub>(156 MiB / 53.7% CPU · 105.0 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | 🥇 **24,156**<br><sub>(89 MiB / 35.9% CPU · 59.4 µs CPU/req)</sub> | **10,277**<br><sub>(127 MiB / 60% CPU · 233.6 µs CPU/req)</sub> | **19,465**<br><sub>(201 MiB / 50.3% CPU · 103.3 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | **29,580**<br><sub>(170 MiB / 52.4% CPU · 70.9 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | 🥇 **33,916**<br><sub>(90 MiB / 36.4% CPU · 42.9 µs CPU/req)</sub> | **14,829**<br><sub>(128 MiB / 55.2% CPU · 148.9 µs CPU/req)</sub> | **26,524**<br><sub>(214 MiB / 49% CPU · 73.9 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/3 · QUIC | 🥇 **17,128**<br><sub>(159 MiB / 49.1% CPU · 114.6 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **6,351**<br><sub>(128 MiB / 57.3% CPU · 361.0 µs CPU/req)</sub> | **13,592**<br><sub>(210 MiB / 48.1% CPU · 141.4 µs CPU/req)</sub> |

### MITM (TWP only)

Same Client×Origin wires with interception on (`compare-product` [42394333642](https://github.com/justcoding121/titanium-web-proxy/actions/runs/42394333642)). **Lite** = no-op handlers (unchanged-lite finish). **Full** = append-only header mutation (harness: one probe header each way; product: generic append-only relay via `MitmCompressedRelayHelper`). nginx/HAProxy/Envoy/YARP cannot MITM. **Lite÷Reverse** / **Full÷Reverse** vs bare reverse (**same job / comparison-group shard**). Completion gate: Lite ≥ **0.25×** and Full ≥ **0.25×** reverse sustain @ c=64 (median of 3 GHA runs); reverse TWP ÷ closest peer ≥ **0.40×**.

**v1 append-only relay (2026-08-27):** Pre-fix H2→H2 Full÷Reverse was **0.13–0.16×** ([32960766249](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32960766249)). Post-fix @ `df172718`: H2 plain→H2 plain Full **0.77–0.79×**, H3→H1 Full **0.91–0.93×**, all MITM arms ≥ **0.70×** on median of [33041445371](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33041445371), [33055267086](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33055267086), [33055272140](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33055272140).

**v2 drop-only + non-unique append (2026-08-27):** `MitmStaticRebuildHelper` rebuilds static HPACK/QPACK after 1–4 unique header drops; trailing non-unique appends stay on compressed relay. @ `41f4adee`: all MITM arms ≥ **0.70×** on GHA median ([33087088466](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087088466), [33087091622](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087091622), [33105885748](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33105885748) Linux; [33087085235](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087085235), [33087088466](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087088466), [33087091622](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087091622) Windows). H2 plain→H2 plain Full **0.77–0.79×** (Win) / **0.78×** (Lin).

| Client | Origin | Lite sustain | Full sustain | Lite÷Reverse | Full÷Reverse |
|---|---|---:|---:|---:|---:|
| HTTP/1 · plain | HTTP/1 · plain | **71,668**<br><sub>(91 MiB / 45.4% CPU · 25.4 µs CPU/req)</sub> | **70,781**<br><sub>(99 MiB / 46.6% CPU · 26.3 µs CPU/req)</sub> | **0.97×** | **0.95×** |
| HTTP/1 · plain | HTTP/1 · TLS | **56,288**<br><sub>(115 MiB / 47.2% CPU · 33.5 µs CPU/req)</sub> | **56,802**<br><sub>(118 MiB / 46.8% CPU · 33.0 µs CPU/req)</sub> | **0.98×** | **0.99×** |
| HTTP/1 · plain | HTTP/2 · plain | **39,372**<br><sub>(123 MiB / 48.8% CPU · 49.6 µs CPU/req)</sub> | **39,262**<br><sub>(130 MiB / 51.1% CPU · 52.0 µs CPU/req)</sub> | **0.94×** | **0.94×** |
| HTTP/1 · plain | HTTP/2 · TLS | **34,599**<br><sub>(153 MiB / 49.6% CPU · 57.3 µs CPU/req)</sub> | **33,083**<br><sub>(157 MiB / 49.5% CPU · 59.9 µs CPU/req)</sub> | **0.99×** | **0.94×** |
| HTTP/1 · plain | HTTP/3 · QUIC | **22,500**<br><sub>(146 MiB / 53.2% CPU · 94.5 µs CPU/req)</sub> | **22,332**<br><sub>(147 MiB / 53.5% CPU · 95.8 µs CPU/req)</sub> | **0.99×** | **0.98×** |
| HTTP/1 · TLS | HTTP/1 · plain | **25,643**<br><sub>(123 MiB / 49.6% CPU · 77.4 µs CPU/req)</sub> | **24,475**<br><sub>(121 MiB / 48.6% CPU · 79.3 µs CPU/req)</sub> | **0.99×** | **0.94×** |
| HTTP/1 · TLS | HTTP/1 · TLS | **20,922**<br><sub>(123 MiB / 48.6% CPU · 93.0 µs CPU/req)</sub> | **20,581**<br><sub>(123 MiB / 48.2% CPU · 93.8 µs CPU/req)</sub> | **0.97×** | **0.95×** |
| HTTP/1 · TLS | HTTP/2 · plain | **47,220**<br><sub>(153 MiB / 51.4% CPU · 43.5 µs CPU/req)</sub> | **45,773**<br><sub>(149 MiB / 51.8% CPU · 45.3 µs CPU/req)</sub> | **1×** | **0.97×** |
| HTTP/1 · TLS | HTTP/2 · TLS | **24,962**<br><sub>(166 MiB / 48% CPU · 76.9 µs CPU/req)</sub> | **24,136**<br><sub>(167 MiB / 48.2% CPU · 79.9 µs CPU/req)</sub> | **0.99×** | **0.96×** |
| HTTP/1 · TLS | HTTP/3 · QUIC | **27,983**<br><sub>(185 MiB / 52.8% CPU · 75.5 µs CPU/req)</sub> | **28,203**<br><sub>(179 MiB / 53.7% CPU · 76.1 µs CPU/req)</sub> | **0.98×** | **0.99×** |
| HTTP/2 · plain | HTTP/1 · plain | **37,668**<br><sub>(145 MiB / 53.5% CPU · 56.8 µs CPU/req)</sub> | **37,096**<br><sub>(145 MiB / 53.2% CPU · 57.3 µs CPU/req)</sub> | **0.95×** | **0.93×** |
| HTTP/2 · plain | HTTP/1 · TLS | **27,031**<br><sub>(169 MiB / 50.9% CPU · 75.4 µs CPU/req)</sub> | **26,313**<br><sub>(166 MiB / 50.6% CPU · 76.9 µs CPU/req)</sub> | **0.95×** | **0.93×** |
| HTTP/2 · plain | HTTP/2 · plain | **68,059**<br><sub>(151 MiB / 39.5% CPU · 23.2 µs CPU/req)</sub> | **64,548**<br><sub>(140 MiB / 39.3% CPU · 24.3 µs CPU/req)</sub> | **0.81×** | **0.77×** |
| HTTP/2 · plain | HTTP/2 · TLS | **48,478**<br><sub>(173 MiB / 43.2% CPU · 35.7 µs CPU/req)</sub> | **45,087**<br><sub>(169 MiB / 41.4% CPU · 36.7 µs CPU/req)</sub> | **0.86×** | **0.8×** |
| HTTP/2 · plain | HTTP/3 · QUIC | **51,116**<br><sub>(205 MiB / 51.2% CPU · 40.1 µs CPU/req)</sub> | **52,074**<br><sub>(204 MiB / 52.5% CPU · 40.3 µs CPU/req)</sub> | **1.04×** | **1.06×** |
| HTTP/2 · TLS | HTTP/1 · plain | **46,058**<br><sub>(167 MiB / 51.8% CPU · 45.0 µs CPU/req)</sub> | **44,216**<br><sub>(164 MiB / 50.5% CPU · 45.6 µs CPU/req)</sub> | **0.98×** | **0.94×** |
| HTTP/2 · TLS | HTTP/1 · TLS | **24,828**<br><sub>(157 MiB / 50.3% CPU · 81.1 µs CPU/req)</sub> | **24,597**<br><sub>(161 MiB / 50.4% CPU · 81.9 µs CPU/req)</sub> | **0.94×** | **0.94×** |
| HTTP/2 · TLS | HTTP/2 · plain | **57,680**<br><sub>(172 MiB / 44.6% CPU · 30.9 µs CPU/req)</sub> | **54,586**<br><sub>(159 MiB / 43.8% CPU · 32.1 µs CPU/req)</sub> | **0.9×** | **0.85×** |
| HTTP/2 · TLS | HTTP/2 · TLS | **90,051**<br><sub>(178 MiB / 42.9% CPU · 19.1 µs CPU/req)</sub> | **87,395**<br><sub>(185 MiB / 43% CPU · 19.7 µs CPU/req)</sub> | **0.85×** | **0.83×** |
| HTTP/2 · TLS | HTTP/3 · QUIC | **25,038**<br><sub>(200 MiB / 51.3% CPU · 82.0 µs CPU/req)</sub> | **24,487**<br><sub>(192 MiB / 50.5% CPU · 82.5 µs CPU/req)</sub> | **1.02×** | **0.99×** |
| HTTP/3 · QUIC | HTTP/1 · plain | **26,418**<br><sub>(156 MiB / 53.2% CPU · 80.5 µs CPU/req)</sub> | **25,716**<br><sub>(163 MiB / 53.6% CPU · 83.4 µs CPU/req)</sub> | **0.96×** | **0.93×** |
| HTTP/3 · QUIC | HTTP/1 · TLS | **21,500**<br><sub>(173 MiB / 52.7% CPU · 98.0 µs CPU/req)</sub> | **21,433**<br><sub>(189 MiB / 52% CPU · 97.1 µs CPU/req)</sub> | **0.96×** | **0.96×** |
| HTTP/3 · QUIC | HTTP/2 · plain | **21,778**<br><sub>(157 MiB / 54.7% CPU · 100.4 µs CPU/req)</sub> | **21,394**<br><sub>(162 MiB / 54.2% CPU · 101.3 µs CPU/req)</sub> | **1.06×** | **1.05×** |
| HTTP/3 · QUIC | HTTP/2 · TLS | **30,675**<br><sub>(175 MiB / 52.9% CPU · 69.0 µs CPU/req)</sub> | **27,060**<br><sub>(179 MiB / 53.2% CPU · 78.7 µs CPU/req)</sub> | **1.04×** | **0.91×** |
| HTTP/3 · QUIC | HTTP/3 · QUIC | **16,750**<br><sub>(162 MiB / 51.4% CPU · 122.7 µs CPU/req)</sub> | **15,613**<br><sub>(159 MiB / 51.6% CPU · 132.3 µs CPU/req)</sub> | **0.98×** | **0.91×** |

## macOS — Titanium vs nginx vs HAProxy vs Envoy vs YARP

### Reverse

Median of **3 repeats** on `macos-15` (Apple Silicon M1, 3-core / 7 GB). Bare reverse 5×5 @ `b6700ebe` — `compare-product` [42394333642](https://github.com/justcoding121/titanium-web-proxy/actions/runs/42394333642). Warmup 2s / measure 8s; concurrency 8, 16, 32, 64. Prefer TWP÷peer ratios over absolute RPS. **RPS cells** include median RSS / CPU at the peak-RPS step as `<br><sub>(MiB / CPU%)</sub>`. The RPS workflow installs Homebrew nginx (`http_v3_module`), Homebrew HAProxy with `USE_QUIC` (3.2 source fallback), Envoy (Homebrew bottle or pinned darwin-arm64 1.36.7), Homebrew `libmsquic` (+ `DYLD_*`), and YARP. Use the pinned `macos-15` label, not `macos-latest`. Product 5×5 is **~56-byte JSON keep-alive GET**; H2/H3 same-protocol cells are mostly header work with a tiny body (Titanium best case) — see [Why this comparison is fair](#user-content-why-this-comparison-is-fair).

| Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---:|---:|---:|---:|---:|
| HTTP/1 · plain | HTTP/1 · plain | **15,494**<br><sub>(116 MiB / 21.5% CPU · 41.6 µs CPU/req)</sub> | **12,600**<br><sub>(68 MiB / 8.2% CPU · 19.5 µs CPU/req)</sub> | 🥇 **24,583**<br><sub>(95 MiB / 22.4% CPU · 27.3 µs CPU/req)</sub> | **10,121**<br><sub>(111 MiB / 27.3% CPU · 80.9 µs CPU/req)</sub> | **17,522**<br><sub>(147 MiB / 30.4% CPU · 52.1 µs CPU/req)</sub> |
| HTTP/1 · plain | HTTP/1 · TLS | **18,322**<br><sub>(131 MiB / 27% CPU · 44.2 µs CPU/req)</sub> | **23,794**<br><sub>(79 MiB / 16.4% CPU · 20.6 µs CPU/req)</sub> | 🥇 **31,194**<br><sub>(106 MiB / 29.8% CPU · 28.6 µs CPU/req)</sub> | **21,080**<br><sub>(112 MiB / 34.2% CPU · 48.6 µs CPU/req)</sub> | **22,272**<br><sub>(161 MiB / 28.2% CPU · 38.0 µs CPU/req)</sub> |
| HTTP/1 · plain | HTTP/2 · plain | **6,524**<br><sub>(126 MiB / 10.6% CPU · 48.6 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **8,142**<br><sub>(96 MiB / 15.5% CPU · 57.1 µs CPU/req)</sub> | **12,188**<br><sub>(110 MiB / 27.6% CPU · 67.8 µs CPU/req)</sub> | 🥇 **24,773**<br><sub>(167 MiB / 28% CPU · 33.9 µs CPU/req)</sub> |
| HTTP/1 · plain | HTTP/2 · TLS | **27,194**<br><sub>(176 MiB / 32.6% CPU · 36.0 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | 🥇 **28,479**<br><sub>(192 MiB / 41.5% CPU · 43.7 µs CPU/req)</sub> | **21,297**<br><sub>(111 MiB / 42.8% CPU · 60.2 µs CPU/req)</sub> | **26,829**<br><sub>(162 MiB / 32.6% CPU · 36.5 µs CPU/req)</sub> |
| HTTP/1 · plain | HTTP/3 · QUIC | **12,317**<br><sub>(138 MiB / 44.8% CPU · 109.0 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **14,672**<br><sub>(114 MiB / 42.7% CPU · 87.4 µs CPU/req)</sub> | 🥇 **16,548**<br><sub>(171 MiB / 41.3% CPU · 74.8 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/1 · plain | **20,966**<br><sub>(134 MiB / 29% CPU · 41.6 µs CPU/req)</sub> | **18,310**<br><sub>(84 MiB / 23.5% CPU · 38.5 µs CPU/req)</sub> | 🥇 **23,756**<br><sub>(111 MiB / 31.4% CPU · 39.7 µs CPU/req)</sub> | **15,316**<br><sub>(121 MiB / 43.2% CPU · 84.7 µs CPU/req)</sub> | **3,501**<br><sub>(peak 4,365 · 156 MiB / 10.7% CPU · 91.5 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/1 · TLS | **24,552**<br><sub>(135 MiB / 31.3% CPU · 38.2 µs CPU/req)</sub> | **27,195**<br><sub>(89 MiB / 23% CPU · 25.4 µs CPU/req)</sub> | 🥇 **27,523**<br><sub>(111 MiB / 29.6% CPU · 32.2 µs CPU/req)</sub> | **21,142**<br><sub>(121 MiB / 36.2% CPU · 51.3 µs CPU/req)</sub> | **21,933**<br><sub>(164 MiB / 31.4% CPU · 43.0 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/2 · plain | **20,754**<br><sub>(143 MiB / 37.3% CPU · 53.9 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **15,362**<br><sub>(119 MiB / 33.1% CPU · 64.6 µs CPU/req)</sub> | **15,799**<br><sub>(120 MiB / 47.4% CPU · 90.1 µs CPU/req)</sub> | 🥇 **22,333**<br><sub>(180 MiB / 33.7% CPU · 45.2 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/2 · TLS | **10,805**<br><sub>(209 MiB / 18.5% CPU · 51.4 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | 🥇 **11,452**<br><sub>(113 MiB / 20.7% CPU · 54.2 µs CPU/req)</sub> | **6,971**<br><sub>(121 MiB / 18.5% CPU · 79.6 µs CPU/req)</sub> | **5,600**<br><sub>(190 MiB / 11.3% CPU · 60.5 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/3 · QUIC | 🥇 **7,665**<br><sub>(165 MiB / 24.7% CPU · 96.8 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **5,111**<br><sub>(124 MiB / 25.6% CPU · 150.5 µs CPU/req)</sub> | **7,173**<br><sub>(184 MiB / 18.5% CPU · 77.3 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | **14,904**<br><sub>(191 MiB / 25.5% CPU · 51.3 µs CPU/req)</sub> | **28,591**<br><sub>(69 MiB / 16.3% CPU · 17.1 µs CPU/req)</sub> | 🥇 **31,283**<br><sub>(110 MiB / 27.5% CPU · 26.4 µs CPU/req)</sub> | **15,104**<br><sub>(110 MiB / 30.1% CPU · 59.9 µs CPU/req)</sub> | **13,326**<br><sub>(152 MiB / 16.7% CPU · 37.6 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/1 · TLS | **21,748**<br><sub>(184 MiB / 41.2% CPU · 56.9 µs CPU/req)</sub> | 🥇 **23,048**<br><sub>(81 MiB / 22.5% CPU · 29.3 µs CPU/req)</sub> | **22,938**<br><sub>(118 MiB / 35.9% CPU · 46.9 µs CPU/req)</sub> | **18,409**<br><sub>(113 MiB / 47.5% CPU · 77.4 µs CPU/req)</sub> | **20,951**<br><sub>(163 MiB / 40.2% CPU · 57.5 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/2 · plain | **39,559**<br><sub>(232 MiB / 17.1% CPU · 13.0 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **21,244**<br><sub>(134 MiB / 26.1% CPU · 36.8 µs CPU/req)</sub> | **13,770**<br><sub>(109 MiB / 24% CPU · 52.2 µs CPU/req)</sub> | 🥇 **48,017**<br><sub>(162 MiB / 34.3% CPU · 21.4 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/2 · TLS | **13,768**<br><sub>(244 MiB / 11.7% CPU · 25.5 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | 🥇 **21,941**<br><sub>(105 MiB / 27.2% CPU · 37.2 µs CPU/req)</sub> | **8,992**<br><sub>(110 MiB / 20.2% CPU · 67.6 µs CPU/req)</sub> | **9,700**<br><sub>(174 MiB / 9.7% CPU · 29.9 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/3 · QUIC | 🥇 **14,215**<br><sub>(197 MiB / 29.4% CPU · 62.0 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **9,064**<br><sub>(113 MiB / 34.8% CPU · 115.1 µs CPU/req)</sub> | **7,012**<br><sub>(166 MiB / 13.4% CPU · 57.2 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | **10,965**<br><sub>(197 MiB / 17.9% CPU · 49.1 µs CPU/req)</sub> | **9,922**<br><sub>(83 MiB / 10.4% CPU · 31.6 µs CPU/req)</sub> | 🥇 **19,954**<br><sub>(110 MiB / 24.8% CPU · 37.3 µs CPU/req)</sub> | **6,082**<br><sub>(121 MiB / 18.3% CPU · 90.3 µs CPU/req)</sub> | **19,860**<br><sub>(158 MiB / 30.1% CPU · 45.4 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/1 · TLS | **9,397**<br><sub>(220 MiB / 21.3% CPU · 67.9 µs CPU/req)</sub> | **13,014**<br><sub>(89 MiB / 17.2% CPU · 39.7 µs CPU/req)</sub> | 🥇 **21,790**<br><sub>(113 MiB / 31.2% CPU · 43.0 µs CPU/req)</sub> | **6,365**<br><sub>(121 MiB / 16.1% CPU · 75.7 µs CPU/req)</sub> | **12,837**<br><sub>(167 MiB / 24.9% CPU · 58.2 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | 🥇 **53,729**<br><sub>(236 MiB / 24.7% CPU · 13.8 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **34,705**<br><sub>(118 MiB / 27.6% CPU · 23.8 µs CPU/req)</sub> | **40,312**<br><sub>(119 MiB / 18.2% CPU · 13.6 µs CPU/req)</sub> | **46,530**<br><sub>(169 MiB / 34.7% CPU · 22.4 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | **19,276**<br><sub>(244 MiB / 14% CPU · 21.8 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **24,866**<br><sub>(117 MiB / 21.3% CPU · 25.7 µs CPU/req)</sub> | **27,287**<br><sub>(119 MiB / 31.5% CPU · 34.6 µs CPU/req)</sub> | 🥇 **30,477**<br><sub>(171 MiB / 30.2% CPU · 29.7 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/3 · QUIC | 🥇 **9,164**<br><sub>(208 MiB / 18.4% CPU · 60.3 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **5,637**<br><sub>(122 MiB / 22.8% CPU · 121.3 µs CPU/req)</sub> | **7,398**<br><sub>(170 MiB / 16.9% CPU · 68.5 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | **11,993**<br><sub>(145 MiB / 36.6% CPU · 91.7 µs CPU/req)</sub> | **0**<br><sub>(peak 16,394 · 87 MiB / 20.8% CPU)</sub> | 🥇 **22,865**<br><sub>(105 MiB / 30.3% CPU · 39.7 µs CPU/req)</sub> | **12,490**<br><sub>(124 MiB / 52.9% CPU · 127.2 µs CPU/req)</sub> | **10,838**<br><sub>(207 MiB / 44.4% CPU · 123.0 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | **9,110**<br><sub>(155 MiB / 31.8% CPU · 104.6 µs CPU/req)</sub> | **0**<br><sub>(peak 9,847 · 91 MiB / 19.9% CPU)</sub> | 🥇 **12,990**<br><sub>(108 MiB / 21.8% CPU · 50.4 µs CPU/req)</sub> | **4,800**<br><sub>(126 MiB / 31.9% CPU · 199.4 µs CPU/req)</sub> | **8,322**<br><sub>(236 MiB / 32.5% CPU · 117.2 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/2 · plain | **10,738**<br><sub>(142 MiB / 22.6% CPU · 63.0 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | 🥇 **12,021**<br><sub>(108 MiB / 17.4% CPU · 43.4 µs CPU/req)</sub> | **6,601**<br><sub>(122 MiB / 34.5% CPU · 156.9 µs CPU/req)</sub> | **5,731**<br><sub>(279 MiB / 17.4% CPU · 91.2 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | **6,972**<br><sub>(148 MiB / 24.2% CPU · 104.2 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | 🥇 **19,436**<br><sub>(108 MiB / 26% CPU · 40.1 µs CPU/req)</sub> | **7,034**<br><sub>(123 MiB / 35.8% CPU · 152.6 µs CPU/req)</sub> | **6,799**<br><sub>(267 MiB / 18.9% CPU · 83.2 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/3 · QUIC | **4,705**<br><sub>(125 MiB / 16.1% CPU · 102.8 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | 🥇 **5,526**<br><sub>(120 MiB / 29% CPU · 157.7 µs CPU/req)</sub> | **4,875**<br><sub>(224 MiB / 25.6% CPU · 157.9 µs CPU/req)</sub> |

## Editions (CLI / Plus / Intercept)

**Note:** `twp-reverse-http1` and other library rows use Core with **probe-tuned** settings (no logging, no Via header, probe-warmed certs). Edition rows use `titanium run -c twp.yaml` **product defaults** — prefer the ÷baseline ratio column over absolute RPS. Inspector GUI is not spawnable in the harness; session-path overhead is `twp-cli-intercept-http1` (route `RequestHeaderSet` transform). Pre-origin Plus middleware (CIDR/WAF deny-list/JWT/rate-limit/cache) runs on H1 terminate-lite without `SessionEventArgs`; a cache hit skips the origin. WAF is split into two rows: `twp-cli-plus-waf-http1` is deny-list only (`waf.mode=off`, denyPaths that miss `/`); `twp-cli-plus-waf-screen-http1` turns screening on (`waf.mode=detect`, no denyPaths) so headers/cookies/small bodies force the session path. `twp-cli-extauth-http1` pays one allow subrequest per request. `twp-cli-plugin-dotnet-noop-http1` is route middleware whose .NET `PassthroughPlugin` only calls `next`. JWT caches successful bearer validations. CORS only adds response headers on the way out, so it stays on terminate-lite. Circuit breaker and idempotent retry have to see the request or the status code, so they stay on the session path and should land near the intercept row. Maintainer gate thresholds live under [Maintainer notes](#user-content-maintainer-notes).

Median of **3** repeats on `ubuntu-latest`. Linux rows in this paste were re-measured @ `b6700ebe` (Actions [42394393340](https://github.com/justcoding121/titanium-web-proxy/actions/runs/42394393340)). Every edition row in the table was re-measured in that run. Warmup 2s / measure 8s; concurrency 8–64; sustain = median peak RPS among SLO-pass steps @ **c=64**. **RPS cells** show sustain; `<sub>` holds peak (when higher) plus `(MiB / CPU%)`. The Gate column is that script's floor.

| Arm | Linux | Lin÷ | Gate |
|---|---:|---:|---|
| `twp-cli-reverse-http1` vs library | **34,750**<br><sub>(173 MiB / 47.6% CPU · 54.8 µs CPU/req)</sub> | **1.04×** | ≥ **0.50×** |
| `twp-cli-reverse-http1-tls` vs library TLS | **44,581**<br><sub>(273 MiB / 45.4% CPU · 40.7 µs CPU/req)</sub> | **1.02×** | ≥ **0.50×** |
| `twp-cli-reverse-http1-route` vs CLI | **63,780**<br><sub>(243 MiB / 46.8% CPU · 29.3 µs CPU/req)</sub> | **0.99×** | ≥ **0.50×** |
| `twp-cli-plus-base-http1` vs CLI | **70,472**<br><sub>(181 MiB / 47.6% CPU · 27.0 µs CPU/req)</sub> | **0.99×** | ≥ **0.50×** |
| `twp-cli-plus-cache-http1` (cold) vs CLI | **31,727**<br><sub>(184 MiB / 52.5% CPU · 66.3 µs CPU/req)</sub> | **0.90×** | ≥ **0.50×** |
| `twp-cli-intercept-http1` vs CLI | **37,853**<br><sub>(192 MiB / 51.0% CPU · 53.9 µs CPU/req)</sub> | **0.73×** | ≥ **0.50×** |
| `twp-cli-plus-waf-http1` vs CLI | **46,340**<br><sub>(253 MiB / 47.5% CPU · 41.0 µs CPU/req)</sub> | **0.96×** | ≥ **0.50×** |
| `twp-cli-plus-waf-screen-http1` vs CLI | **50,369**<br><sub>(252 MiB / 49.1% CPU · 39.0 µs CPU/req)</sub> | **0.77×** | ≥ **0.50×** |
| `twp-cli-plus-cidr-http1` vs CLI | **50,662**<br><sub>(182 MiB / 48.3% CPU · 38.1 µs CPU/req)</sub> | **0.98×** | ≥ **0.50×** |
| `twp-cli-plus-jwt-http1` vs CLI | **47,013**<br><sub>(204 MiB / 47.9% CPU · 40.8 µs CPU/req)</sub> | **0.91×** | ≥ **0.50×** |
| `twp-cli-plus-ratelimit-http1` vs CLI | **50,445**<br><sub>(180 MiB / 48.5% CPU · 38.5 µs CPU/req)</sub> | **0.97×** | ≥ **0.50×** |
| `twp-cli-plus-fleet-ratelimit-http1` vs CLI | **34,050**<br><sub>(184 MiB / 50.0% CPU · 58.8 µs CPU/req)</sub> | **0.93×** | ≥ **0.50×** |
| `twp-cli-plus-manager-agent-http1` vs CLI | **63,117**<br><sub>(186 MiB / 47.5% CPU · 30.1 µs CPU/req)</sub> | **0.98×** | ≥ **0.50×** |
| `twp-cli-plus-resilience-http1` vs CLI | **67,783**<br><sub>(187 MiB / 48.2% CPU · 28.4 µs CPU/req)</sub> | **1.02×** | ≥ **0.50×** |
| `twp-cli-plus-discovery-file-http1` vs CLI | **72,480**<br><sub>(185 MiB / 48.7% CPU · 26.9 µs CPU/req)</sub> | **1.01×** | ≥ **0.50×** |
| `twp-cli-plus-metrics-scrape-http1` vs CLI | **72,702**<br><sub>(184 MiB / 48.5% CPU · 26.7 µs CPU/req)</sub> | **1.02×** | ≥ **0.50×** |
| `twp-cli-plus-cache-hit-http1` vs cache cold | **58,194**<br><sub>(180 MiB / 48.4% CPU · 33.3 µs CPU/req)</sub> | **1.83×** | ≥ **0.50×** |
| `twp-cli-plus-cors-http1` vs CLI | **32,978**<br><sub>(181 MiB / 49.2% CPU · 59.6 µs CPU/req)</sub> | **0.91×** | ≥ **0.50×** |
| `twp-cli-plus-circuit-http1` vs CLI | **40,850**<br><sub>(187 MiB / 50.1% CPU · 49.0 µs CPU/req)</sub> | **0.79×** | ≥ **0.50×** |
| `twp-cli-plus-retry-http1` vs CLI | **49,312**<br><sub>(251 MiB / 48.6% CPU · 39.5 µs CPU/req)</sub> | **0.85×** | ≥ **0.50×** |
| `twp-cli-extauth-http1` vs CLI | **25,116**<br><sub>(193 MiB / 50.0% CPU · 79.7 µs CPU/req)</sub> | **0.50×** | ≥ **0.50×** |
| `twp-cli-static-http1` vs CLI | **55,668**<br><sub>(175 MiB / 50.8% CPU · 36.5 µs CPU/req)</sub> | **1.52×** | ≥ **0.50×** |
| `twp-cli-logging-http1` vs CLI | **66,355**<br><sub>(175 MiB / 47.4% CPU · 28.6 µs CPU/req)</sub> | **1.01×** | ≥ **0.50×** |
| `twp-cli-lb-leasttime-http1` vs route | **48,001**<br><sub>(213 MiB / 70.7% CPU · 58.9 µs CPU/req)</sub> | **0.93×** | ≥ **0.50×** |
| `twp-cli-dialect-twp-http1` vs CLI | **36,562**<br><sub>(171 MiB / 48.4% CPU · 52.9 µs CPU/req)</sub> | **0.99×** | ≥ **0.50×** |
| `twp-cli-script-lua-noop-http1` vs route | **21,350**<br><sub>(198 MiB / 59.1% CPU · 110.7 µs CPU/req)</sub> | **0.59×** | ≥ **0.50×** |
| `twp-cli-script-lua-header-http1` vs route | **27,239**<br><sub>(200 MiB / 61.3% CPU · 90.0 µs CPU/req)</sub> | **0.56×** | ≥ **0.50×** |
| `twp-cli-script-wasm-noop-http1` vs route | **35,360**<br><sub>(203 MiB / 52.7% CPU · 59.7 µs CPU/req)</sub> | **0.70×** | ≥ **0.50×** |
| `twp-cli-script-lua-slow-upstream-http1` vs slow upstream | **3,065**<br><sub>(225 MiB / 20.5% CPU · 267.0 µs CPU/req)</sub> | **0.99×** | ≥ **0.50×** |
| `twp-cli-script-1in50-http1` vs route | **53,506**<br><sub>(192 MiB / 49.7% CPU · 37.2 µs CPU/req)</sub> | **0.76×** | ≥ **0.50×** |
| `twp-cli-plugin-dotnet-noop-http1` vs route | **25,605**<br><sub>(192 MiB / 53.3% CPU · 83.3 µs CPU/req)</sub> | **0.70×** | ≥ **0.50×** |
| `twp-cli-geo-http1` vs route | **35,088**<br><sub>(181 MiB / 48.9% CPU · 55.8 µs CPU/req)</sub> | **0.96×** | ≥ **0.50×** |
| `twp-cli-compress-h2-h2` vs route | **65,807**<br><sub>(181 MiB / 47.6% CPU · 28.9 µs CPU/req)</sub> | **1.06×** | ≥ **0.50×** |
| `twp-cli-compress-h3` vs route | **36,027**<br><sub>(178 MiB / 48.6% CPU · 53.9 µs CPU/req)</sub> | **1.00×** | ≥ **0.50×** |
| `twp-cli-health-grpc-http1` vs route | **46,182**<br><sub>(189 MiB / 46.7% CPU · 40.5 µs CPU/req)</sub> | **0.99×** | ≥ **0.50×** |
| `twp-cli-zoneaware-http1` vs route | **51,344**<br><sub>(181 MiB / 48.4% CPU · 37.7 µs CPU/req)</sub> | **0.99×** | ≥ **0.50×** |
| `twp-cli-tcp-tlsterm` vs route | **58,900**<br><sub>(245 MiB / 47.7% CPU · 32.4 µs CPU/req)</sub> | **1.06×** | ≥ **0.50×** |
| `twp-ws-h3` vs route | **43,120**<br><sub>(179 MiB / 48.7% CPU · 45.2 µs CPU/req)</sub> | **1.00×** | ≥ **0.50×** |
| `twp-cli-compress-zstd-http1` vs route | **66,714**<br><sub>(178 MiB / 48.1% CPU · 28.8 µs CPU/req)</sub> | **1.03×** | ≥ **0.50×** |
| `twp-cli-overload-http1` vs route | **37,143**<br><sub>(180 MiB / 48.7% CPU · 52.4 µs CPU/req)</sub> | **1.02×** | ≥ **0.50×** |
| `twp-cli-hotrestart-http1` vs route | **52,023**<br><sub>(179 MiB / 48.1% CPU · 37.0 µs CPU/req)</sub> | **1.00×** | ≥ **0.50×** |

`validate-edition-gates.ps1` floors are **0.50×**. Each ÷ column uses the two arms from the same job. Circuit breaker and idempotent retry stay on the session path, so a ratio near intercept is expected. Laptop smoke ratios stay on [Performance Local Lab — Editions](Performance-Local-Lab#user-content-editions-cli--plus-stress).

## Heavier reverse workloads

Same runners and harness as the tiny-GET tables, but with larger bodies, POST, lossy links, TLS cost, and architecture-sensitive paths (slow consumer / early response / duplex). **PUT with the same body is the same proxy work as POST; DELETE with no body matches GET** — only POST is published. Bodies/POST/lossy stay **half-duplex**. Laptop numbers are on [Performance Local Lab](Performance-Local-Lab#user-content-architecture-sensitive). How maintainers refresh these tables (modes, shards, paste scripts) is under [Maintainer notes](#user-content-maintainer-notes). **Larger-body check:** 64 / 256 KiB H2 TLS→H2 TLS is where body copy dominates headers — ratios vs YARP are in the tables below (@ `b6700ebe`), unlike the tiny-GET H2↔H2 medals above. Those cells also show goodput (MiB/s) and µs CPU/req, and the medal on those rows follows goodput.

Lossy link = **userspace** delay/drop shim (not kernel `netem`): TCP gets per-buffer delay + occasional whole-connection stalls (honest head-of-line for multiplexed HTTP/2); UDP is **loss% only** (no per-datagram delay) + drops (QUIC / MsQuic-safe). Lossy tables publish HTTP/1, HTTP/2, and HTTP/3.

### Windows — heavier reverse GET (64 KiB / 256 KiB)

Median of **3** repeats on `windows-latest` @ `b6700ebe`. Source: Actions [42394339299](https://github.com/justcoding121/titanium-web-proxy/actions/runs/42394339299) (`compare-bodies`). Warmup 2s / measure 8s. **RPS cells** include `(MiB / CPU%)` footprints.

*Not possible:* **HAProxy** and **Envoy** columns are omitted (no official Windows port).
























| Body | Client | Origin | TWP | nginx | YARP |
|---|---|---|---:|---:|---:|
| 64 KiB | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **12,768**<br><sub>(95 MiB / 43.8% CPU · 798 MiB/s · 137.2 µs CPU/req)</sub> | **914**<br><sub>(144 MiB / 24.8% CPU · 57 MiB/s · 1085.3 µs CPU/req)</sub> | **10,471**<br><sub>(134 MiB / 49.8% CPU · 654 MiB/s · 190.1 µs CPU/req)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **10,794**<br><sub>(203 MiB / 52.0% CPU · 675 MiB/s · 192.8 µs CPU/req)</sub> | **814**<br><sub>(144 MiB / 24.9% CPU · 51 MiB/s · 1226.2 µs CPU/req)</sub> | **10,267**<br><sub>(122 MiB / 50.2% CPU · 642 MiB/s · 195.7 µs CPU/req)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **3,873**<br><sub>(141 MiB / 45.2% CPU · 242 MiB/s · 466.6 µs CPU/req)</sub> | *Not possible (no QUIC)* | **1,782**<br><sub>(179 MiB / 53.0% CPU · 111 MiB/s · 1189.6 µs CPU/req)</sub> |
| 256 KiB | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **2,865**<br><sub>(138 MiB / 47.2% CPU · 716 MiB/s · 659.2 µs CPU/req)</sub> | **168**<br><sub>(144 MiB / 24.8% CPU · 42 MiB/s · 5875.4 µs CPU/req)</sub> | **2,681**<br><sub>(134 MiB / 48.1% CPU · 670 MiB/s · 718.4 µs CPU/req)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/1 · plain | **2,368**<br><sub>(200 MiB / 50.2% CPU · 592 MiB/s · 848.8 µs CPU/req)</sub> | **152**<br><sub>(144 MiB / 24.9% CPU · 38 MiB/s · 6533.8 µs CPU/req)</sub> | 🥇 **2,628**<br><sub>(134 MiB / 45.8% CPU · 657 MiB/s · 697.0 µs CPU/req)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **464**<br><sub>(104 MiB / 46.2% CPU · 116 MiB/s · 3983.6 µs CPU/req)</sub> | *Not possible (no QUIC)* | **347**<br><sub>(171 MiB / 50.9% CPU · 87 MiB/s · 5859.4 µs CPU/req)</sub> |
| 64 KiB | HTTP/2 · plain | HTTP/1 · plain | 🥇 **12,442**<br><sub>(182 MiB / 43.3% CPU · 778 MiB/s · 139.1 µs CPU/req)</sub> | **2,015**<br><sub>(129 MiB / 24.8% CPU · 126 MiB/s · 491.3 µs CPU/req)</sub> | **11,690**<br><sub>(121 MiB / 47.9% CPU · 731 MiB/s · 163.7 µs CPU/req)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/2 · plain | **16,617**<br><sub>(174 MiB / 40.0% CPU · 1,039 MiB/s · 96.4 µs CPU/req)</sub> | *Not possible* | 🥇 **16,684**<br><sub>(137 MiB / 50.8% CPU · 1,043 MiB/s · 121.9 µs CPU/req)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **6,243**<br><sub>(183 MiB / 45.8% CPU · 390 MiB/s · 293.3 µs CPU/req)</sub> | *Not possible* | **5,436**<br><sub>(149 MiB / 47.9% CPU · 340 MiB/s · 352.4 µs CPU/req)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/2 · TLS | 🥇 **3,234**<br><sub>(132 MiB / 42.4% CPU · 202 MiB/s · 525.0 µs CPU/req)</sub> | *Not possible* | **1,578**<br><sub>(186 MiB / 48.7% CPU · 99 MiB/s · 1234.3 µs CPU/req)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **3,408**<br><sub>(148 MiB / 44.7% CPU · 213 MiB/s · 524.6 µs CPU/req)</sub> | *Not possible (no QUIC)* | **1,742**<br><sub>(200 MiB / 52.3% CPU · 109 MiB/s · 1202.1 µs CPU/req)</sub> |
| 256 KiB | HTTP/2 · plain | HTTP/1 · plain | 🥇 **4,070**<br><sub>(186 MiB / 36.3% CPU · 1,018 MiB/s · 356.8 µs CPU/req)</sub> | **665**<br><sub>(129 MiB / 24.7% CPU · 166 MiB/s · 1484.2 µs CPU/req)</sub> | **3,709**<br><sub>(124 MiB / 39.8% CPU · 927 MiB/s · 429.5 µs CPU/req)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/2 · plain | 🥇 **2,664**<br><sub>(210 MiB / 45.5% CPU · 666 MiB/s · 683.5 µs CPU/req)</sub> | *Not possible* | **2,648**<br><sub>(182 MiB / 48.2% CPU · 662 MiB/s · 728.5 µs CPU/req)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **1,669**<br><sub>(206 MiB / 43.9% CPU · 417 MiB/s · 1051.3 µs CPU/req)</sub> | *Not possible* | **1,343**<br><sub>(155 MiB / 44.0% CPU · 336 MiB/s · 1309.8 µs CPU/req)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/2 · TLS | **318**<br><sub>(149 MiB / 51.2% CPU · 80 MiB/s · 6437.5 µs CPU/req)</sub> | *Not possible* | 🥇 **528**<br><sub>(191 MiB / 47.7% CPU · 132 MiB/s · 3618.7 µs CPU/req)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/1 · TLS | **93**<br><sub>(86 MiB / 46.2% CPU · 23 MiB/s · 19862.5 µs CPU/req)</sub> | *Not possible (no QUIC)* | 🥇 **386**<br><sub>(123 MiB / 45.8% CPU · 97 MiB/s · 4746.8 µs CPU/req)</sub> |

nginx/Windows collapses on large reverse bodies in this harness; treat as same-OS only. Prefer the ratio columns; absolute RPS swings by VM.

### Linux — heavier reverse GET (64 KiB / 256 KiB)

Median of **3** repeats @ `b6700ebe`. Source: Actions [42394339299](https://github.com/justcoding121/titanium-web-proxy/actions/runs/42394339299) (`compare-bodies`). Warmup 2s / measure 8s.


| Body | Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---|---:|---:|---:|---:|---:|
| 64 KiB | HTTP/1 · TLS | HTTP/1 · plain | **13,968**<br><sub>(128 MiB / 38.2% CPU · 873 MiB/s · 109.5 µs CPU/req)</sub> | **10,496**<br><sub>(111 MiB / 44.0% CPU · 656 MiB/s · 167.5 µs CPU/req)</sub> | 🥇 **14,000**<br><sub>(89 MiB / 38.4% CPU · 875 MiB/s · 109.7 µs CPU/req)</sub> | **12,387**<br><sub>(127 MiB / 44.4% CPU · 774 MiB/s · 143.2 µs CPU/req)</sub> | **10,861**<br><sub>(171 MiB / 47.2% CPU · 679 MiB/s · 173.9 µs CPU/req)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/1 · plain | **13,520**<br><sub>(263 MiB / 41.6% CPU · 845 MiB/s · 123.0 µs CPU/req)</sub> | **9,534**<br><sub>(111 MiB / 49.0% CPU · 596 MiB/s · 205.8 µs CPU/req)</sub> | **12,669**<br><sub>(90 MiB / 46.9% CPU · 792 MiB/s · 148.2 µs CPU/req)</sub> | 🥇 **15,084**<br><sub>(130 MiB / 45.0% CPU · 943 MiB/s · 119.2 µs CPU/req)</sub> | **12,743**<br><sub>(155 MiB / 45.7% CPU · 796 MiB/s · 143.6 µs CPU/req)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/1 · plain | **8,955**<br><sub>(228 MiB / 44.3% CPU · 560 MiB/s · 197.9 µs CPU/req)</sub> | **3,505**<br><sub>(124 MiB / 30.8% CPU · 219 MiB/s · 351.8 µs CPU/req)</sub> | 🥇 **9,612**<br><sub>(97 MiB / 36.2% CPU · 601 MiB/s · 150.7 µs CPU/req)</sub> | **6,841**<br><sub>(135 MiB / 45.3% CPU · 428 MiB/s · 265.0 µs CPU/req)</sub> | **6,032**<br><sub>(256 MiB / 49.4% CPU · 377 MiB/s · 327.7 µs CPU/req)</sub> |
| 256 KiB | HTTP/1 · TLS | HTTP/1 · plain | **2,750**<br><sub>(134 MiB / 35.8% CPU · 687 MiB/s · 520.5 µs CPU/req)</sub> | **1,737**<br><sub>(109 MiB / 52.4% CPU · 434 MiB/s · 1207.7 µs CPU/req)</sub> | 🥇 **2,982**<br><sub>(88 MiB / 32.6% CPU · 746 MiB/s · 436.7 µs CPU/req)</sub> | **2,649**<br><sub>(141 MiB / 35.5% CPU · 662 MiB/s · 536.0 µs CPU/req)</sub> | **2,226**<br><sub>(174 MiB / 45.6% CPU · 557 MiB/s · 818.9 µs CPU/req)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/1 · plain | **2,964**<br><sub>(224 MiB / 35.6% CPU · 741 MiB/s · 480.4 µs CPU/req)</sub> | **2,284**<br><sub>(110 MiB / 49.1% CPU · 571 MiB/s · 860.8 µs CPU/req)</sub> | **2,992**<br><sub>(88 MiB / 37.9% CPU · 748 MiB/s · 507.1 µs CPU/req)</sub> | 🥇 **3,240**<br><sub>(151 MiB / 35.6% CPU · 810 MiB/s · 439.9 µs CPU/req)</sub> | **2,707**<br><sub>(163 MiB / 41.6% CPU · 677 MiB/s · 615.1 µs CPU/req)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/1 · plain | **1,762**<br><sub>(184 MiB / 47.1% CPU · 441 MiB/s · 1068.5 µs CPU/req)</sub> | **788**<br><sub>(123 MiB / 27.6% CPU · 197 MiB/s · 1401.5 µs CPU/req)</sub> | 🥇 **2,122**<br><sub>(97 MiB / 33.3% CPU · 531 MiB/s · 627.4 µs CPU/req)</sub> | **1,818**<br><sub>(150 MiB / 45.1% CPU · 455 MiB/s · 991.2 µs CPU/req)</sub> | **1,430**<br><sub>(239 MiB / 50.3% CPU · 358 MiB/s · 1407.3 µs CPU/req)</sub> |
| 64 KiB | HTTP/2 · plain | HTTP/1 · plain | 🥇 **11,476**<br><sub>(251 MiB / 43.3% CPU · 717 MiB/s · 150.8 µs CPU/req)</sub> | **7,004**<br><sub>(81 MiB / 52.8% CPU · 438 MiB/s · 301.4 µs CPU/req)</sub> | **10,483**<br><sub>(73 MiB / 47.2% CPU · 655 MiB/s · 180.2 µs CPU/req)</sub> | **9,773**<br><sub>(118 MiB / 53.9% CPU · 611 MiB/s · 220.7 µs CPU/req)</sub> | **10,940**<br><sub>(149 MiB / 47.3% CPU · 684 MiB/s · 173.0 µs CPU/req)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/2 · plain | 🥇 **7,572**<br><sub>(205 MiB / 39.1% CPU · 473 MiB/s · 206.6 µs CPU/req)</sub> | *Not possible* | **5,861**<br><sub>(92 MiB / 45.2% CPU · 366 MiB/s · 308.5 µs CPU/req)</sub> | **6,583**<br><sub>(131 MiB / 47.2% CPU · 411 MiB/s · 286.6 µs CPU/req)</sub> | **5,785**<br><sub>(204 MiB / 49.1% CPU · 362 MiB/s · 339.7 µs CPU/req)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **10,665**<br><sub>(219 MiB / 37.8% CPU · 667 MiB/s · 141.8 µs CPU/req)</sub> | *Not possible* | **8,865**<br><sub>(92 MiB / 47.9% CPU · 554 MiB/s · 215.9 µs CPU/req)</sub> | **9,713**<br><sub>(137 MiB / 45.7% CPU · 607 MiB/s · 188.0 µs CPU/req)</sub> | **8,978**<br><sub>(203 MiB / 45.0% CPU · 561 MiB/s · 200.5 µs CPU/req)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/2 · TLS | **3,650**<br><sub>(179 MiB / 44.4% CPU · 228 MiB/s · 486.6 µs CPU/req)</sub> | *Not possible* | 🥇 **3,798**<br><sub>(98 MiB / 40.9% CPU · 237 MiB/s · 430.3 µs CPU/req)</sub> | **2,704**<br><sub>(140 MiB / 49.5% CPU · 169 MiB/s · 732.8 µs CPU/req)</sub> | **2,594**<br><sub>(243 MiB / 49.2% CPU · 162 MiB/s · 758.8 µs CPU/req)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/1 · TLS | **6,108**<br><sub>(230 MiB / 45.1% CPU · 382 MiB/s · 295.3 µs CPU/req)</sub> | **2,938**<br><sub>(129 MiB / 34.0% CPU · 184 MiB/s · 462.7 µs CPU/req)</sub> | 🥇 **6,112**<br><sub>(97 MiB / 41.0% CPU · 382 MiB/s · 268.4 µs CPU/req)</sub> | **4,749**<br><sub>(136 MiB / 51.0% CPU · 297 MiB/s · 429.2 µs CPU/req)</sub> | **4,117**<br><sub>(260 MiB / 51.6% CPU · 257 MiB/s · 501.2 µs CPU/req)</sub> |
| 256 KiB | HTTP/2 · plain | HTTP/1 · plain | **3,864**<br><sub>(209 MiB / 30.8% CPU · 966 MiB/s · 319.3 µs CPU/req)</sub> | **2,873**<br><sub>(81 MiB / 49.5% CPU · 718 MiB/s · 689.3 µs CPU/req)</sub> | **4,242**<br><sub>(72 MiB / 31.3% CPU · 1,060 MiB/s · 294.9 µs CPU/req)</sub> | 🥇 **4,271**<br><sub>(138 MiB / 32.4% CPU · 1,068 MiB/s · 303.8 µs CPU/req)</sub> | **3,909**<br><sub>(143 MiB / 34.6% CPU · 977 MiB/s · 354.2 µs CPU/req)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/2 · plain | **3,114**<br><sub>(231 MiB / 34.6% CPU · 779 MiB/s · 444.8 µs CPU/req)</sub> | *Not possible* | **3,177**<br><sub>(101 MiB / 33.6% CPU · 794 MiB/s · 422.9 µs CPU/req)</sub> | 🥇 **3,246**<br><sub>(142 MiB / 35.1% CPU · 812 MiB/s · 432.5 µs CPU/req)</sub> | **2,541**<br><sub>(207 MiB / 44.2% CPU · 635 MiB/s · 696.5 µs CPU/req)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/2 · TLS | **2,415**<br><sub>(238 MiB / 34.5% CPU · 604 MiB/s · 570.7 µs CPU/req)</sub> | *Not possible* | 🥇 **2,612**<br><sub>(98 MiB / 33.4% CPU · 653 MiB/s · 510.9 µs CPU/req)</sub> | **2,586**<br><sub>(144 MiB / 33.9% CPU · 647 MiB/s · 523.5 µs CPU/req)</sub> | **2,155**<br><sub>(212 MiB / 42.1% CPU · 539 MiB/s · 782.3 µs CPU/req)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/2 · TLS | **1,244**<br><sub>(212 MiB / 55.3% CPU · 311 MiB/s · 1777.6 µs CPU/req)</sub> | *Not possible* | 🥇 **2,077**<br><sub>(110 MiB / 36.9% CPU · 519 MiB/s · 711.3 µs CPU/req)</sub> | **1,699**<br><sub>(152 MiB / 44.4% CPU · 425 MiB/s · 1044.5 µs CPU/req)</sub> | **1,452**<br><sub>(249 MiB / 46.3% CPU · 363 MiB/s · 1276.6 µs CPU/req)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/1 · TLS | **1,848**<br><sub>(209 MiB / 46.7% CPU · 462 MiB/s · 1009.8 µs CPU/req)</sub> | **963**<br><sub>(130 MiB / 33.4% CPU · 241 MiB/s · 1386.5 µs CPU/req)</sub> | 🥇 **2,090**<br><sub>(99 MiB / 35.9% CPU · 522 MiB/s · 687.6 µs CPU/req)</sub> | **1,958**<br><sub>(150 MiB / 43.5% CPU · 490 MiB/s · 889.3 µs CPU/req)</sub> | **1,645**<br><sub>(268 MiB / 48.1% CPU · 411 MiB/s · 1168.3 µs CPU/req)</sub> |

Prefer the ratio columns; absolute RPS swings by VM.

### macOS — heavier reverse GET (64 KiB / 256 KiB)

Median of **3** repeats on `macos-15` (Apple Silicon M1, 3-core / 7 GB) @ `b6700ebe`. Source: Actions [42394339299](https://github.com/justcoding121/titanium-web-proxy/actions/runs/42394339299) (`compare-bodies`). Warmup 2s / measure 8s.

| Body | Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---|---:|---:|---:|---:|---:|
| 64 KiB | HTTP/1 · TLS | HTTP/1 · plain | **3,010**<br><sub>(181 MiB / 17.3% CPU · 188 MiB/s · 172.6 µs CPU/req)</sub> | **4,683**<br><sub>(84 MiB / 19.3% CPU · 293 MiB/s · 123.7 µs CPU/req)</sub> | 🥇 **6,320**<br><sub>(121 MiB / 27.3% CPU · 395 MiB/s · 129.8 µs CPU/req)</sub> | **5,155**<br><sub>(133 MiB / 27.0% CPU · 322 MiB/s · 157.2 µs CPU/req)</sub> | **3,835**<br><sub>(182 MiB / 26.0% CPU · 240 MiB/s · 203.7 µs CPU/req)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/1 · plain | **5,721**<br><sub>(474 MiB / 33.3% CPU · 358 MiB/s · 174.8 µs CPU/req)</sub> | **4,067**<br><sub>(87 MiB / 24.5% CPU · 254 MiB/s · 180.9 µs CPU/req)</sub> | **6,662**<br><sub>(124 MiB / 35.3% CPU · 416 MiB/s · 158.9 µs CPU/req)</sub> | **5,684**<br><sub>(142 MiB / 33.8% CPU · 355 MiB/s · 178.6 µs CPU/req)</sub> | 🥇 **7,942**<br><sub>(177 MiB / 44.7% CPU · 496 MiB/s · 168.7 µs CPU/req)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/1 · plain | **1,344**<br><sub>(287 MiB / 27.0% CPU · 84 MiB/s · 602.2 µs CPU/req)</sub> | 🥇 **1,960**<br><sub>(95 MiB / 21.7% CPU · 123 MiB/s · 332.7 µs CPU/req)</sub> | **1,465**<br><sub>(126 MiB / 28.4% CPU · 92 MiB/s · 582.5 µs CPU/req)</sub> | **1,430**<br><sub>(130 MiB / 48.1% CPU · 89 MiB/s · 1008.4 µs CPU/req)</sub> | **624**<br><sub>(469 MiB / 23.0% CPU · 39 MiB/s · 1106.4 µs CPU/req)</sub> |
| 256 KiB | HTTP/1 · TLS | HTTP/1 · plain | **1,863**<br><sub>(217 MiB / 25.4% CPU · 466 MiB/s · 408.6 µs CPU/req)</sub> | **1,648**<br><sub>(84 MiB / 19.9% CPU · 412 MiB/s · 362.0 µs CPU/req)</sub> | 🥇 **1,978**<br><sub>(117 MiB / 26.0% CPU · 495 MiB/s · 394.9 µs CPU/req)</sub> | **1,776**<br><sub>(148 MiB / 24.7% CPU · 444 MiB/s · 417.6 µs CPU/req)</sub> | **1,212**<br><sub>(184 MiB / 28.1% CPU · 303 MiB/s · 696.0 µs CPU/req)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/1 · plain | **1,069**<br><sub>(435 MiB / 19.3% CPU · 267 MiB/s · 541.7 µs CPU/req)</sub> | **2,073**<br><sub>(86 MiB / 23.8% CPU · 518 MiB/s · 345.0 µs CPU/req)</sub> | **2,308**<br><sub>(115 MiB / 28.1% CPU · 577 MiB/s · 365.3 µs CPU/req)</sub> | 🥇 **2,551**<br><sub>(153 MiB / 28.5% CPU · 638 MiB/s · 334.8 µs CPU/req)</sub> | **2,267**<br><sub>(179 MiB / 42.0% CPU · 567 MiB/s · 555.6 µs CPU/req)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/1 · plain | **520**<br><sub>(193 MiB / 29.2% CPU · 130 MiB/s · 1686.3 µs CPU/req)</sub> | 🥇 **862**<br><sub>(96 MiB / 29.2% CPU · 215 MiB/s · 1016.7 µs CPU/req)</sub> | **512**<br><sub>(peak 812 · 115 MiB / 25.0% CPU · 128 MiB/s · 1467.2 µs CPU/req)</sub> | **517**<br><sub>(147 MiB / 47.9% CPU · 129 MiB/s · 2779.4 µs CPU/req)</sub> | **213**<br><sub>(333 MiB / 19.2% CPU · 53 MiB/s · 2701.7 µs CPU/req)</sub> |
| 64 KiB | HTTP/2 · plain | HTTP/1 · plain | **5,760**<br><sub>(466 MiB / 19.7% CPU · 360 MiB/s · 102.6 µs CPU/req)</sub> | **6,670**<br><sub>(74 MiB / 15.5% CPU · 417 MiB/s · 69.7 µs CPU/req)</sub> | **7,842**<br><sub>(107 MiB / 27.4% CPU · 490 MiB/s · 104.8 µs CPU/req)</sub> | 🥇 **7,982**<br><sub>(123 MiB / 27.1% CPU · 499 MiB/s · 101.7 µs CPU/req)</sub> | **3,084**<br><sub>(190 MiB / 16.1% CPU · 193 MiB/s · 156.5 µs CPU/req)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/2 · plain | **2,294**<br><sub>(417 MiB / 14.9% CPU · 143 MiB/s · 195.0 µs CPU/req)</sub> | *Not possible* | 🥇 **3,225**<br><sub>(128 MiB / 23.6% CPU · 202 MiB/s · 219.0 µs CPU/req)</sub> | **2,364**<br><sub>(125 MiB / 17.9% CPU · 148 MiB/s · 227.5 µs CPU/req)</sub> | **1,969**<br><sub>(261 MiB / 19.8% CPU · 123 MiB/s · 302.2 µs CPU/req)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/2 · TLS | **3,005**<br><sub>(445 MiB / 18.6% CPU · 188 MiB/s · 185.6 µs CPU/req)</sub> | *Not possible* | **4,471**<br><sub>(125 MiB / 28.6% CPU · 279 MiB/s · 191.6 µs CPU/req)</sub> | **3,020**<br><sub>(125 MiB / 21.0% CPU · 189 MiB/s · 208.2 µs CPU/req)</sub> | 🥇 **5,085**<br><sub>(239 MiB / 37.6% CPU · 318 MiB/s · 221.8 µs CPU/req)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/2 · TLS | 🥇 **1,454**<br><sub>(231 MiB / 29.6% CPU · 91 MiB/s · 611.0 µs CPU/req)</sub> | *Not possible* | **1,386**<br><sub>(131 MiB / 30.7% CPU · 87 MiB/s · 664.5 µs CPU/req)</sub> | **1,120**<br><sub>(129 MiB / 46.0% CPU · 70 MiB/s · 1231.1 µs CPU/req)</sub> | **618**<br><sub>(443 MiB / 24.4% CPU · 39 MiB/s · 1182.1 µs CPU/req)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/1 · TLS | **1,935**<br><sub>(269 MiB / 30.2% CPU · 121 MiB/s · 468.8 µs CPU/req)</sub> | 🥇 **2,617**<br><sub>(100 MiB / 27.6% CPU · 164 MiB/s · 317.0 µs CPU/req)</sub> | **1,107**<br><sub>(129 MiB / 28.3% CPU · 69 MiB/s · 767.5 µs CPU/req)</sub> | **1,358**<br><sub>(131 MiB / 47.8% CPU · 85 MiB/s · 1055.0 µs CPU/req)</sub> | **774**<br><sub>(402 MiB / 22.9% CPU · 48 MiB/s · 886.5 µs CPU/req)</sub> |
| 256 KiB | HTTP/2 · plain | HTTP/1 · plain | **1,210**<br><sub>(495 MiB / 16.4% CPU · 302 MiB/s · 406.2 µs CPU/req)</sub> | 🥇 **2,985**<br><sub>(72 MiB / 20.5% CPU · 746 MiB/s · 205.9 µs CPU/req)</sub> | **2,592**<br><sub>(104 MiB / 24.4% CPU · 648 MiB/s · 282.6 µs CPU/req)</sub> | **2,670**<br><sub>(144 MiB / 25.3% CPU · 668 MiB/s · 284.0 µs CPU/req)</sub> | **2,249**<br><sub>(214 MiB / 31.8% CPU · 562 MiB/s · 424.2 µs CPU/req)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/2 · plain | **1,336**<br><sub>(652 MiB / 24.8% CPU · 334 MiB/s · 557.4 µs CPU/req)</sub> | *Not possible* | 🥇 **1,551**<br><sub>(140 MiB / 28.9% CPU · 388 MiB/s · 558.1 µs CPU/req)</sub> | **1,064**<br><sub>(135 MiB / 15.8% CPU · 266 MiB/s · 445.9 µs CPU/req)</sub> | **1,284**<br><sub>(306 MiB / 28.6% CPU · 321 MiB/s · 668.5 µs CPU/req)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/2 · TLS | **859**<br><sub>(546 MiB / 26.6% CPU · 215 MiB/s · 929.0 µs CPU/req)</sub> | *Not possible* | 🥇 **1,000**<br><sub>(130 MiB / 32.1% CPU · 250 MiB/s · 962.2 µs CPU/req)</sub> | **759**<br><sub>(127 MiB / 22.7% CPU · 190 MiB/s · 899.3 µs CPU/req)</sub> | **879**<br><sub>(284 MiB / 32.7% CPU · 220 MiB/s · 1117.3 µs CPU/req)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/2 · TLS | **288**<br><sub>(450 MiB / 35.3% CPU · 72 MiB/s · 3671.8 µs CPU/req)</sub> | *Not possible* | **274**<br><sub>(peak 521 · 113 MiB / 25.0% CPU · 68 MiB/s · 2737.9 µs CPU/req)</sub> | 🥇 **429**<br><sub>(138 MiB / 49.4% CPU · 107 MiB/s · 3450.2 µs CPU/req)</sub> | **245**<br><sub>(peak 269 · 409 MiB / 31.0% CPU · 61 MiB/s · 3798.4 µs CPU/req)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/1 · TLS | **187**<br><sub>(peak 206 · 149 MiB / 30.7% CPU · 47 MiB/s · 4942.1 µs CPU/req)</sub> | 🥇 **359**<br><sub>(peak 369 · 92 MiB / 26.8% CPU · 90 MiB/s · 2238.3 µs CPU/req)</sub> | **162**<br><sub>(103 MiB / 21.7% CPU · 40 MiB/s · 4033.4 µs CPU/req)</sub> | **250**<br><sub>(133 MiB / 47.6% CPU · 63 MiB/s · 5697.0 µs CPU/req)</sub> | **195**<br><sub>(380 MiB / 36.5% CPU · 49 MiB/s · 5607.0 µs CPU/req)</sub> |

### Windows — POST 64 KiB request + 64 KiB response

Median of **3** repeats on `windows-latest` @ `b6700ebe`. Source: Actions [42394345253](https://github.com/justcoding121/titanium-web-proxy/actions/runs/42394345253) (`compare-post`).

*Not possible:* **HAProxy** and **Envoy** columns are omitted (no official Windows port).
























| Client | Origin | TWP | nginx | YARP |
|---|---|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | 🥇 **10,943**<br><sub>(100 MiB / 43.0% CPU · 1,368 MiB/s · 157.1 µs CPU/req)</sub> | **667**<br><sub>(144 MiB / 24.9% CPU · 83 MiB/s · 1492.4 µs CPU/req)</sub> | **7,036**<br><sub>(136 MiB / 53.6% CPU · 879 MiB/s · 305.0 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **4,360**<br><sub>(215 MiB / 45.9% CPU · 545 MiB/s · 421.1 µs CPU/req)</sub> | **368**<br><sub>(148 MiB / 24.7% CPU · 46 MiB/s · 2687.7 µs CPU/req)</sub> | **4,312**<br><sub>(141 MiB / 46.8% CPU · 539 MiB/s · 434.3 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **2,254**<br><sub>(167 MiB / 47.3% CPU · 282 MiB/s · 839.9 µs CPU/req)</sub> | *Not possible (no QUIC)* | **1,367**<br><sub>(208 MiB / 52.1% CPU · 171 MiB/s · 1524.0 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | 🥇 **9,060**<br><sub>(194 MiB / 46.2% CPU · 1,132 MiB/s · 203.8 µs CPU/req)</sub> | **2,867**<br><sub>(133 MiB / 24.7% CPU · 358 MiB/s · 344.8 µs CPU/req)</sub> | **8,778**<br><sub>(118 MiB / 46.7% CPU · 1,097 MiB/s · 212.9 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | 🥇 **9,231**<br><sub>(176 MiB / 37.6% CPU · 1,154 MiB/s · 162.8 µs CPU/req)</sub> | *Not possible* | **7,306**<br><sub>(143 MiB / 46.6% CPU · 913 MiB/s · 255.2 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **4,468**<br><sub>(182 MiB / 40.5% CPU · 558 MiB/s · 362.3 µs CPU/req)</sub> | *Not possible* | **3,681**<br><sub>(156 MiB / 46.9% CPU · 460 MiB/s · 509.4 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | 🥇 **2,512**<br><sub>(197 MiB / 43.0% CPU · 314 MiB/s · 684.8 µs CPU/req)</sub> | *Not possible* | **1,563**<br><sub>(217 MiB / 50.3% CPU · 195 MiB/s · 1286.9 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **1,885**<br><sub>(177 MiB / 48.9% CPU · 236 MiB/s · 1037.9 µs CPU/req)</sub> | *Not possible (no QUIC)* | **1,237**<br><sub>(205 MiB / 52.7% CPU · 155 MiB/s · 1705.4 µs CPU/req)</sub> |

TWP leads H2→H1 POST (about **1.1–1.3×** YARP) and H3 POST (about **1.0–1.1×** YARP). H2 TLS→H2 TLS POST sustain is about **1.2–1.4×** YARP.

### Linux — POST 64 KiB request + 64 KiB response

Median of **3** repeats @ `b6700ebe`. Source: Actions [42394345253](https://github.com/justcoding121/titanium-web-proxy/actions/runs/42394345253) (`compare-post`).


| Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---:|---:|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | **4,780**<br><sub>(142 MiB / 44.9% CPU · 597 MiB/s · 375.8 µs CPU/req)</sub> | **3,878**<br><sub>(109 MiB / 50.7% CPU · 485 MiB/s · 523.1 µs CPU/req)</sub> | 🥇 **5,242**<br><sub>(90 MiB / 41.7% CPU · 655 MiB/s · 318.4 µs CPU/req)</sub> | **4,888**<br><sub>(128 MiB / 44.3% CPU · 611 MiB/s · 362.5 µs CPU/req)</sub> | **3,298**<br><sub>(175 MiB / 56.8% CPU · 412 MiB/s · 689.0 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | **7,467**<br><sub>(281 MiB / 44.1% CPU · 933 MiB/s · 236.2 µs CPU/req)</sub> | **6,943**<br><sub>(119 MiB / 45.7% CPU · 868 MiB/s · 263.4 µs CPU/req)</sub> | 🥇 **8,157**<br><sub>(91 MiB / 44.8% CPU · 1,020 MiB/s · 219.6 µs CPU/req)</sub> | **7,668**<br><sub>(peak 8,762 · 122 MiB / 40.9% CPU · 958 MiB/s · 213.3 µs CPU/req)</sub> | **6,818**<br><sub>(178 MiB / 47.6% CPU · 852 MiB/s · 279.1 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **5,280**<br><sub>(249 MiB / 45.4% CPU · 660 MiB/s · 344.3 µs CPU/req)</sub> | **1,817**<br><sub>(121 MiB / 47.7% CPU · 227 MiB/s · 1050.7 µs CPU/req)</sub> | **4,840**<br><sub>(95 MiB / 40.4% CPU · 605 MiB/s · 333.8 µs CPU/req)</sub> | **0**<br><sub>(126 MiB / 0.1% CPU)</sub> | **4,041**<br><sub>(270 MiB / 49.4% CPU · 505 MiB/s · 488.8 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | **6,270**<br><sub>(270 MiB / 42.5% CPU · 784 MiB/s · 270.9 µs CPU/req)</sub> | **5,336**<br><sub>(88 MiB / 46.4% CPU · 667 MiB/s · 348.0 µs CPU/req)</sub> | 🥇 **7,295**<br><sub>(76 MiB / 38.2% CPU · 912 MiB/s · 209.7 µs CPU/req)</sub> | **4,994**<br><sub>(peak 6,715 · 111 MiB / 44.2% CPU · 624 MiB/s · 354.1 µs CPU/req)</sub> | **6,050**<br><sub>(162 MiB / 47.1% CPU · 756 MiB/s · 311.2 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | 🥇 **6,120**<br><sub>(212 MiB / 36.2% CPU · 765 MiB/s · 236.8 µs CPU/req)</sub> | *Not possible* | **5,148**<br><sub>(93 MiB / 42.5% CPU · 644 MiB/s · 329.8 µs CPU/req)</sub> | **6,065**<br><sub>(131 MiB / 42.1% CPU · 758 MiB/s · 277.6 µs CPU/req)</sub> | **4,396**<br><sub>(195 MiB / 49.5% CPU · 550 MiB/s · 450.4 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | **5,508**<br><sub>(216 MiB / 36.2% CPU · 688 MiB/s · 263.1 µs CPU/req)</sub> | *Not possible* | **5,110**<br><sub>(92 MiB / 38.4% CPU · 639 MiB/s · 300.2 µs CPU/req)</sub> | 🥇 **5,510**<br><sub>(135 MiB / 40.4% CPU · 689 MiB/s · 293.5 µs CPU/req)</sub> | **4,211**<br><sub>(198 MiB / 47.1% CPU · 526 MiB/s · 447.4 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | 🥇 **2,575**<br><sub>(250 MiB / 43.2% CPU · 322 MiB/s · 671.3 µs CPU/req)</sub> | *Not possible* | **2,263**<br><sub>(97 MiB / 40.6% CPU · 283 MiB/s · 716.9 µs CPU/req)</sub> | **0**<br><sub>(126 MiB / 0.2% CPU)</sub> | **1,895**<br><sub>(258 MiB / 48.9% CPU · 237 MiB/s · 1032.1 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **3,349**<br><sub>(258 MiB / 45.7% CPU · 419 MiB/s · 545.6 µs CPU/req)</sub> | **1,314**<br><sub>(125 MiB / 49.4% CPU · 164 MiB/s · 1504.4 µs CPU/req)</sub> | **3,163**<br><sub>(98 MiB / 45.3% CPU · 395 MiB/s · 573.3 µs CPU/req)</sub> | **0**<br><sub>(126 MiB / 0.2% CPU)</sub> | **2,713**<br><sub>(275 MiB / 48.7% CPU · 339 MiB/s · 717.7 µs CPU/req)</sub> |

Linux nginx H1/H2/H3 POST completed (nginx.org mainline). TWP÷YARP H1 ≈ **1.5×**; H2→H1 ≈ **1.2×**; H3 ≈ **1.1×**. TWP÷nginx H3 ≈ **6×**.

### macOS — POST 64 KiB request + 64 KiB response

Median of **3** repeats on `macos-15` (Apple Silicon M1, 3-core / 7 GB) @ `b6700ebe`. Source: Actions [42394345253](https://github.com/justcoding121/titanium-web-proxy/actions/runs/42394345253) (`compare-post`).

| Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---:|---:|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | **3,047**<br><sub>(244 MiB / 27.5% CPU · 381 MiB/s · 270.7 µs CPU/req)</sub> | **3,429**<br><sub>(89 MiB / 20.3% CPU · 429 MiB/s · 177.7 µs CPU/req)</sub> | 🥇 **3,504**<br><sub>(120 MiB / 26.2% CPU · 438 MiB/s · 224.4 µs CPU/req)</sub> | **3,013**<br><sub>(134 MiB / 29.8% CPU · 377 MiB/s · 296.8 µs CPU/req)</sub> | **1,722**<br><sub>(206 MiB / 30.9% CPU · 215 MiB/s · 538.9 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | **1,155**<br><sub>(518 MiB / 25.7% CPU · 144 MiB/s · 667.2 µs CPU/req)</sub> | 🥇 **2,082**<br><sub>(98 MiB / 19.8% CPU · 260 MiB/s · 284.9 µs CPU/req)</sub> | **2,051**<br><sub>(121 MiB / 25.8% CPU · 256 MiB/s · 376.9 µs CPU/req)</sub> | **1,088**<br><sub>(peak 1,934 · 120 MiB / 19.9% CPU · 136 MiB/s · 549.6 µs CPU/req)</sub> | **1,366**<br><sub>(199 MiB / 28.4% CPU · 171 MiB/s · 623.7 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | **926**<br><sub>(312 MiB / 24.4% CPU · 116 MiB/s · 790.2 µs CPU/req)</sub> | 🥇 **1,233**<br><sub>(96 MiB / 25.3% CPU · 154 MiB/s · 615.0 µs CPU/req)</sub> | **1,207**<br><sub>(111 MiB / 30.9% CPU · 151 MiB/s · 768.6 µs CPU/req)</sub> | **833**<br><sub>(135 MiB / 45.7% CPU · 104 MiB/s · 1645.4 µs CPU/req)</sub> | **1,024**<br><sub>(451 MiB / 38.5% CPU · 128 MiB/s · 1129.0 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | **2,866**<br><sub>(535 MiB / 23.4% CPU · 358 MiB/s · 244.9 µs CPU/req)</sub> | 🥇 **3,554**<br><sub>(85 MiB / 13.5% CPU · 444 MiB/s · 114.3 µs CPU/req)</sub> | **3,392**<br><sub>(113 MiB / 20.4% CPU · 424 MiB/s · 180.3 µs CPU/req)</sub> | **2,700**<br><sub>(121 MiB / 22.2% CPU · 338 MiB/s · 247.1 µs CPU/req)</sub> | **2,704**<br><sub>(223 MiB / 23.3% CPU · 338 MiB/s · 258.6 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | **1,825**<br><sub>(391 MiB / 16.6% CPU · 228 MiB/s · 273.5 µs CPU/req)</sub> | *Not possible* | 🥇 **2,830**<br><sub>(128 MiB / 27.9% CPU · 354 MiB/s · 295.4 µs CPU/req)</sub> | **2,720**<br><sub>(129 MiB / 24.4% CPU · 340 MiB/s · 268.9 µs CPU/req)</sub> | **1,043**<br><sub>(232 MiB / 19.1% CPU · 130 MiB/s · 550.0 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | **1,618**<br><sub>(433 MiB / 18.1% CPU · 202 MiB/s · 335.4 µs CPU/req)</sub> | *Not possible* | 🥇 **2,519**<br><sub>(126 MiB / 31.9% CPU · 315 MiB/s · 380.1 µs CPU/req)</sub> | **2,071**<br><sub>(129 MiB / 26.4% CPU · 259 MiB/s · 382.8 µs CPU/req)</sub> | **941**<br><sub>(218 MiB / 19.2% CPU · 118 MiB/s · 611.9 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | 🥇 **704**<br><sub>(321 MiB / 28.2% CPU · 88 MiB/s · 1200.9 µs CPU/req)</sub> | *Not possible* | **617**<br><sub>(123 MiB / 31.6% CPU · 77 MiB/s · 1536.0 µs CPU/req)</sub> | **684**<br><sub>(134 MiB / 47.7% CPU · 86 MiB/s · 2090.0 µs CPU/req)</sub> | **603**<br><sub>(402 MiB / 32.9% CPU · 75 MiB/s · 1633.2 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **920**<br><sub>(262 MiB / 29.1% CPU · 115 MiB/s · 950.2 µs CPU/req)</sub> | **781**<br><sub>(101 MiB / 18.7% CPU · 98 MiB/s · 718.2 µs CPU/req)</sub> | **666**<br><sub>(113 MiB / 30.3% CPU · 83 MiB/s · 1365.0 µs CPU/req)</sub> | **305**<br><sub>(peak 318 · 132 MiB / 26.2% CPU · 38 MiB/s · 2581.1 µs CPU/req)</sub> | **511**<br><sub>(462 MiB / 30.3% CPU · 64 MiB/s · 1778.3 µs CPU/req)</sub> |

### Windows — lossy / high-RTT (H2 HOL / H3 loss)

Userspace **5 ms** one-way delay + **1%** TCP connection stall (H1/H2); UDP is **loss% only** (no per-datagram delay; MsQuic-safe) + **1%** datagram drop (H3); **64 KiB** GET. Median of **3** repeats on `windows-latest` @ `b6700ebe` — [42394351712](https://github.com/justcoding121/titanium-web-proxy/actions/runs/42394351712) (`compare-lossy`).

*Not possible:* **HAProxy** and **Envoy** columns are omitted (no official Windows port).


| Client | Origin | TWP | nginx | YARP |
|---|---|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | 🥇 **663**<br><sub>(91 MiB / 3.5% CPU · 41 MiB/s · 208.6 µs CPU/req)</sub> | **650**<br><sub>(144 MiB / 15.6% CPU · 41 MiB/s · 959.4 µs CPU/req)</sub> | **663**<br><sub>(119 MiB / 3.4% CPU · 41 MiB/s · 202.7 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **248**<br><sub>(160 MiB / 3.1% CPU · 15 MiB/s · 497.2 µs CPU/req)</sub> | **243**<br><sub>(144 MiB / 8.5% CPU · 15 MiB/s · 1395.3 µs CPU/req)</sub> | **230**<br><sub>(119 MiB / 4.0% CPU · 14 MiB/s · 687.0 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | **838**<br><sub>(123 MiB / 19.1% CPU · 52 MiB/s · 912.5 µs CPU/req)</sub> | *Not possible (no QUIC)* | 🥇 **858**<br><sub>(180 MiB / 30.9% CPU · 54 MiB/s · 1439.0 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | 🥇 **256**<br><sub>(154 MiB / 2.5% CPU · 16 MiB/s · 391.1 µs CPU/req)</sub> | **250**<br><sub>(129 MiB / 2.3% CPU · 16 MiB/s · 368.6 µs CPU/req)</sub> | **231**<br><sub>(105 MiB / 2.5% CPU · 14 MiB/s · 432.3 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | **223**<br><sub>(123 MiB / 3.1% CPU · 14 MiB/s · 562.2 µs CPU/req)</sub> | *Not possible* | 🥇 **232**<br><sub>(114 MiB / 4.3% CPU · 15 MiB/s · 744.9 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | **227**<br><sub>(127 MiB / 2.9% CPU · 14 MiB/s · 507.9 µs CPU/req)</sub> | *Not possible* | 🥇 **231**<br><sub>(118 MiB / 3.2% CPU · 14 MiB/s · 558.8 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | 🥇 **870**<br><sub>(119 MiB / 19.6% CPU · 54 MiB/s · 901.7 µs CPU/req)</sub> | *Not possible* | **864**<br><sub>(185 MiB / 31.1% CPU · 54 MiB/s · 1438.3 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | **839**<br><sub>(133 MiB / 19.0% CPU · 52 MiB/s · 903.8 µs CPU/req)</sub> | *Not possible (no QUIC)* | 🥇 **852**<br><sub>(182 MiB / 31.1% CPU · 53 MiB/s · 1458.6 µs CPU/req)</sub> |

H1, H2, and H3 loss sustain are in the table. Prefer the ratio columns.

### Linux — lossy / high-RTT (H2 HOL / H3 loss)

Median of **3** repeats @ `b6700ebe`. Source: [42394351712](https://github.com/justcoding121/titanium-web-proxy/actions/runs/42394351712) (`compare-lossy`; lossy H3 uses `quic-http3`, UDP drop-only).


| Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---:|---:|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | **1,214**<br><sub>(127 MiB / 6.9% CPU · 76 MiB/s · 228.7 µs CPU/req)</sub> | 🥇 **1,222**<br><sub>(109 MiB / 5.8% CPU · 76 MiB/s · 191.2 µs CPU/req)</sub> | **1,219**<br><sub>(86 MiB / 3.4% CPU · 76 MiB/s · 111.6 µs CPU/req)</sub> | **1,210**<br><sub>(124 MiB / 6.0% CPU · 76 MiB/s · 198.1 µs CPU/req)</sub> | **1,214**<br><sub>(155 MiB / 10.5% CPU · 76 MiB/s · 345.5 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **626**<br><sub>(198 MiB / 13.1% CPU · 39 MiB/s · 835.8 µs CPU/req)</sub> | **574**<br><sub>(109 MiB / 6.4% CPU · 36 MiB/s · 443.3 µs CPU/req)</sub> | **557**<br><sub>(87 MiB / 4.4% CPU · 35 MiB/s · 314.4 µs CPU/req)</sub> | **507**<br><sub>(125 MiB / 4.9% CPU · 32 MiB/s · 388.6 µs CPU/req)</sub> | **511**<br><sub>(143 MiB / 12.3% CPU · 32 MiB/s · 962.6 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | **1,462**<br><sub>(175 MiB / 20.3% CPU · 91 MiB/s · 554.4 µs CPU/req)</sub> | 🥇 **1,602**<br><sub>(124 MiB / 23.0% CPU · 100 MiB/s · 573.9 µs CPU/req)</sub> | **1,463**<br><sub>(104 MiB / 17.8% CPU · 91 MiB/s · 487.6 µs CPU/req)</sub> | **1,528**<br><sub>(134 MiB / 25.4% CPU · 95 MiB/s · 665.6 µs CPU/req)</sub> | **1,541**<br><sub>(229 MiB / 29.4% CPU · 96 MiB/s · 762.4 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | 🥇 **651**<br><sub>(195 MiB / 9.1% CPU · 41 MiB/s · 557.4 µs CPU/req)</sub> | **594**<br><sub>(81 MiB / 3.6% CPU · 37 MiB/s · 243.3 µs CPU/req)</sub> | **546**<br><sub>(71 MiB / 2.5% CPU · 34 MiB/s · 180.9 µs CPU/req)</sub> | **506**<br><sub>(116 MiB / 3.5% CPU · 32 MiB/s · 276.1 µs CPU/req)</sub> | **512**<br><sub>(138 MiB / 8.4% CPU · 32 MiB/s · 653.0 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | **505**<br><sub>(162 MiB / 7.1% CPU · 32 MiB/s · 561.3 µs CPU/req)</sub> | *Not possible* | 🥇 **550**<br><sub>(88 MiB / 2.7% CPU · 34 MiB/s · 199.2 µs CPU/req)</sub> | **509**<br><sub>(125 MiB / 3.5% CPU · 32 MiB/s · 278.8 µs CPU/req)</sub> | **517**<br><sub>(159 MiB / 9.3% CPU · 32 MiB/s · 721.3 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | **496**<br><sub>(165 MiB / 8.6% CPU · 31 MiB/s · 694.1 µs CPU/req)</sub> | *Not possible* | 🥇 **594**<br><sub>(89 MiB / 4.2% CPU · 37 MiB/s · 283.3 µs CPU/req)</sub> | **510**<br><sub>(127 MiB / 4.7% CPU · 32 MiB/s · 366.3 µs CPU/req)</sub> | **510**<br><sub>(157 MiB / 11.2% CPU · 32 MiB/s · 878.4 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | 🥇 **1,194**<br><sub>(166 MiB / 26.3% CPU · 75 MiB/s · 880.1 µs CPU/req)</sub> | *Not possible* | **945**<br><sub>(106 MiB / 20.8% CPU · 59 MiB/s · 879.4 µs CPU/req)</sub> | **985**<br><sub>(136 MiB / 28.2% CPU · 62 MiB/s · 1144.8 µs CPU/req)</sub> | **1,099**<br><sub>(242 MiB / 33.2% CPU · 69 MiB/s · 1206.7 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **1,196**<br><sub>(183 MiB / 25.4% CPU · 75 MiB/s · 849.7 µs CPU/req)</sub> | **1,097**<br><sub>(128 MiB / 23.9% CPU · 69 MiB/s · 869.8 µs CPU/req)</sub> | **1,019**<br><sub>(106 MiB / 20.7% CPU · 64 MiB/s · 812.0 µs CPU/req)</sub> | **997**<br><sub>(134 MiB / 27.4% CPU · 62 MiB/s · 1098.3 µs CPU/req)</sub> | **1,129**<br><sub>(216 MiB / 32.0% CPU · 71 MiB/s · 1134.8 µs CPU/req)</sub> |

TWP H2 HOL ≫ YARP (~**7.8×**). H3 TWP÷YARP ≈ **1.07×**.

### macOS — lossy / high-RTT (H2 HOL / H3 loss)

Median of **3** repeats on `macos-15` (Apple Silicon M1, 3-core / 7 GB) @ `b6700ebe`. Source: [42394351712](https://github.com/justcoding121/titanium-web-proxy/actions/runs/42394351712) (`compare-lossy`; lossy H3 uses `quic-http3`, UDP drop-only).

| Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---:|---:|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | **481**<br><sub>(259 MiB / 3.9% CPU · 30 MiB/s · 243.0 µs CPU/req)</sub> | 🥇 **528**<br><sub>(85 MiB / 3.6% CPU · 33 MiB/s · 206.3 µs CPU/req)</sub> | **506**<br><sub>(115 MiB / 2.9% CPU · 32 MiB/s · 174.4 µs CPU/req)</sub> | **450**<br><sub>(130 MiB / 3.5% CPU · 28 MiB/s · 235.8 µs CPU/req)</sub> | **477**<br><sub>(220 MiB / 3.7% CPU · 30 MiB/s · 233.1 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | **140**<br><sub>(426 MiB / 4.3% CPU · 8.8 MiB/s · 930.0 µs CPU/req)</sub> | **136**<br><sub>(84 MiB / 1.1% CPU · 8.5 MiB/s · 249.4 µs CPU/req)</sub> | **144**<br><sub>(112 MiB / 1.6% CPU · 9.0 MiB/s · 327.5 µs CPU/req)</sub> | 🥇 **161**<br><sub>(126 MiB / 2.7% CPU · 10 MiB/s · 507.8 µs CPU/req)</sub> | **144**<br><sub>(232 MiB / 4.2% CPU · 9.0 MiB/s · 882.5 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | **834**<br><sub>(280 MiB / 20.7% CPU · 52 MiB/s · 743.6 µs CPU/req)</sub> | **1,095**<br><sub>(96 MiB / 15.8% CPU · 68 MiB/s · 431.4 µs CPU/req)</sub> | **690**<br><sub>(124 MiB / 18.2% CPU · 43 MiB/s · 791.6 µs CPU/req)</sub> | 🥇 **1,117**<br><sub>(131 MiB / 28.2% CPU · 70 MiB/s · 757.9 µs CPU/req)</sub> | **787**<br><sub>(426 MiB / 22.4% CPU · 49 MiB/s · 855.9 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | 🥇 **175**<br><sub>(352 MiB / 5.2% CPU · 11 MiB/s · 893.8 µs CPU/req)</sub> | **159**<br><sub>(72 MiB / 1.2% CPU · 9.9 MiB/s · 218.6 µs CPU/req)</sub> | **143**<br><sub>(99 MiB / 0.7% CPU · 8.9 MiB/s · 152.9 µs CPU/req)</sub> | **136**<br><sub>(118 MiB / 0.8% CPU · 8.5 MiB/s · 187.8 µs CPU/req)</sub> | **168**<br><sub>(144 MiB / 3.5% CPU · 10 MiB/s · 620.8 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | **144**<br><sub>(310 MiB / 4.0% CPU · 9.0 MiB/s · 838.3 µs CPU/req)</sub> | *Not possible* | 🥇 **160**<br><sub>(122 MiB / 1.1% CPU · 10.0 MiB/s · 216.3 µs CPU/req)</sub> | **133**<br><sub>(123 MiB / 1.8% CPU · 8.3 MiB/s · 406.9 µs CPU/req)</sub> | **136**<br><sub>(270 MiB / 3.1% CPU · 8.5 MiB/s · 687.0 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **152**<br><sub>(311 MiB / 6.1% CPU · 9.5 MiB/s · 1213.4 µs CPU/req)</sub> | *Not possible* | **144**<br><sub>(118 MiB / 2.3% CPU · 9.0 MiB/s · 475.3 µs CPU/req)</sub> | **132**<br><sub>(123 MiB / 2.1% CPU · 8.2 MiB/s · 479.9 µs CPU/req)</sub> | **112**<br><sub>(263 MiB / 3.1% CPU · 7.0 MiB/s · 847.5 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | **823**<br><sub>(192 MiB / 21.2% CPU · 51 MiB/s · 772.9 µs CPU/req)</sub> | *Not possible* | **536**<br><sub>(126 MiB / 19.4% CPU · 33 MiB/s · 1084.9 µs CPU/req)</sub> | 🥇 **1,005**<br><sub>(127 MiB / 28.7% CPU · 63 MiB/s · 856.5 µs CPU/req)</sub> | **580**<br><sub>(427 MiB / 23.7% CPU · 36 MiB/s · 1224.6 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **582**<br><sub>(204 MiB / 22.9% CPU · 36 MiB/s · 1177.3 µs CPU/req)</sub> | **488**<br><sub>(97 MiB / 15.4% CPU · 30 MiB/s · 949.8 µs CPU/req)</sub> | **252**<br><sub>(126 MiB / 14.7% CPU · 16 MiB/s · 1744.8 µs CPU/req)</sub> | **516**<br><sub>(131 MiB / 28.0% CPU · 32 MiB/s · 1626.3 µs CPU/req)</sub> | **310**<br><sub>(334 MiB / 22.9% CPU · 19 MiB/s · 2219.5 µs CPU/req)</sub> |

### Architecture-sensitive

These runs isolate slow app readers, origin-early response, HTTP/2 duplex, and WebSocket echo. See [TWP vs YARP IO model](Performance-Profiling#user-content-twp-vs-yarp-io-model). Laptop 1-rep numbers are on [Performance Local Lab](Performance-Local-Lab#user-content-architecture-sensitive).

Median of **3** repeats on matched 4 vCPU / 16 GiB runners @ `b6700ebe` ([42394364277](https://github.com/justcoding121/titanium-web-proxy/actions/runs/42394364277)). Slow consumer = 256 KiB GET, 16 KiB read + 8 ms sleep. Early response = 64 KiB POST, origin writes after 8 KiB. Duplex HTTP/2 = overlapping 64 KiB POST on H2 TLS↔H2 TLS. WebSocket row = H1 Upgrade echo round-trips/sec (not RFC 8441; see WebSocket RFC 8441 table below).

Lossy-link runs (slow **network**) are already published above; they are not a slow **app** reader.

#### Windows


*Not possible:* **HAProxy** and **Envoy** columns are omitted (no official Windows port).

| Scenario | Client | Origin | TWP | nginx | YARP |
|---|---|---|---:|---:|---:|
| Slow consumer (256 KiB GET, throttled client read) | HTTP/1 · TLS | HTTP/1 · plain | **248**<br><sub>(90 MiB / 4.1% CPU · 62 MiB/s · 656.7 µs CPU/req)</sub> | **235**<br><sub>(145 MiB / 20.2% CPU · 59 MiB/s · 3449.5 µs CPU/req)</sub> | 🥇 **248**<br><sub>(111 MiB / 4.5% CPU · 62 MiB/s · 727.6 µs CPU/req)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · TLS | HTTP/1 · plain | **248**<br><sub>(154 MiB / 5.4% CPU · 62 MiB/s · 865.6 µs CPU/req)</sub> | **202**<br><sub>(144 MiB / 24.9% CPU · 51 MiB/s · 4917.4 µs CPU/req)</sub> | 🥇 **248**<br><sub>(122 MiB / 6.9% CPU · 62 MiB/s · 1117.7 µs CPU/req)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **124**<br><sub>(111 MiB / 44.0% CPU · 31 MiB/s · 14210.0 µs CPU/req)</sub> | *Not possible (no QUIC)* | **103**<br><sub>(156 MiB / 44.3% CPU · 26 MiB/s · 17148.9 µs CPU/req)</sub> |
| Early response (origin writes after first request chunk) | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **5,708**<br><sub>(103 MiB / 45.0% CPU · 714 MiB/s · 315.7 µs CPU/req)</sub> | **358**<br><sub>(145 MiB / 24.8% CPU · 45 MiB/s · 2764.2 µs CPU/req)</sub> | **4,093**<br><sub>(141 MiB / 55.2% CPU · 512 MiB/s · 539.9 µs CPU/req)</sub> |
| Early response (origin writes after first request chunk) | HTTP/2 · TLS | HTTP/1 · plain | **3,367**<br><sub>(234 MiB / 56.1% CPU · 421 MiB/s · 666.3 µs CPU/req)</sub> | **368**<br><sub>(148 MiB / 24.9% CPU · 46 MiB/s · 2705.4 µs CPU/req)</sub> | 🥇 **4,069**<br><sub>(140 MiB / 48.3% CPU · 509 MiB/s · 475.1 µs CPU/req)</sub> |
| Early response (origin writes after first request chunk) | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **1,708**<br><sub>(156 MiB / 45.0% CPU · 214 MiB/s · 1053.5 µs CPU/req)</sub> | *Not possible (no QUIC)* | **1,088**<br><sub>(199 MiB / 51.0% CPU · 136 MiB/s · 1875.1 µs CPU/req)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · plain | HTTP/1 · plain | **248**<br><sub>(133 MiB / 4.2% CPU · 62 MiB/s · 680.1 µs CPU/req)</sub> | **248**<br><sub>(129 MiB / 9.2% CPU · 62 MiB/s · 1486.3 µs CPU/req)</sub> | 🥇 **256**<br><sub>(109 MiB / 4.2% CPU · 64 MiB/s · 663.5 µs CPU/req)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **248**<br><sub>(169 MiB / 6.9% CPU · 62 MiB/s · 1108.5 µs CPU/req)</sub> | *Not possible* | **248**<br><sub>(147 MiB / 8.5% CPU · 62 MiB/s · 1377.5 µs CPU/req)</sub> |
| Early response (origin writes after first request chunk) | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **4,236**<br><sub>(204 MiB / 37.0% CPU · 529 MiB/s · 349.5 µs CPU/req)</sub> | *Not possible* | **3,593**<br><sub>(168 MiB / 55.2% CPU · 449 MiB/s · 614.9 µs CPU/req)</sub> |
| Duplex (both directions live) | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **4,370**<br><sub>(202 MiB / 42.2% CPU · 546 MiB/s · 386.0 µs CPU/req)</sub> | *Not possible* | **3,710**<br><sub>(154 MiB / 57.5% CPU · 464 MiB/s · 620.4 µs CPU/req)</sub> |
| Duplex (WebSocket / H1 Upgrade) | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **24,511**<br><sub>(97 MiB / 42.2% CPU · 68.9 µs CPU/req)</sub> | **12,493**<br><sub>(144 MiB / 24.8% CPU · 79.3 µs CPU/req)</sub> | **23,323**<br><sub>(89 MiB / 43.5% CPU · 74.6 µs CPU/req)</sub> |

#### Linux


| Scenario | Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---|---:|---:|---:|---:|---:|
| Slow consumer (256 KiB GET, throttled client read) | HTTP/1 · TLS | HTTP/1 · plain | **473**<br><sub>(125 MiB / 5.8% CPU · 118 MiB/s · 495.1 µs CPU/req)</sub> | **473**<br><sub>(110 MiB / 9.9% CPU · 118 MiB/s · 835.4 µs CPU/req)</sub> | **474**<br><sub>(87 MiB / 3.3% CPU · 118 MiB/s · 277.1 µs CPU/req)</sub> | 🥇 **475**<br><sub>(135 MiB / 5.4% CPU · 119 MiB/s · 453.0 µs CPU/req)</sub> | **472**<br><sub>(158 MiB / 9.6% CPU · 118 MiB/s · 813.9 µs CPU/req)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **482**<br><sub>(169 MiB / 13.4% CPU · 120 MiB/s · 1111.4 µs CPU/req)</sub> | **481**<br><sub>(110 MiB / 10.2% CPU · 120 MiB/s · 852.1 µs CPU/req)</sub> | **482**<br><sub>(86 MiB / 5.7% CPU · 120 MiB/s · 471.8 µs CPU/req)</sub> | **480**<br><sub>(142 MiB / 5.4% CPU · 120 MiB/s · 447.5 µs CPU/req)</sub> | **475**<br><sub>(159 MiB / 11.1% CPU · 119 MiB/s · 933.8 µs CPU/req)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/3 · QUIC | HTTP/1 · plain | **458**<br><sub>(152 MiB / 48.4% CPU · 115 MiB/s · 4224.4 µs CPU/req)</sub> | **466**<br><sub>(120 MiB / 25.5% CPU · 117 MiB/s · 2189.5 µs CPU/req)</sub> | **467**<br><sub>(93 MiB / 12.6% CPU · 117 MiB/s · 1078.1 µs CPU/req)</sub> | 🥇 **470**<br><sub>(147 MiB / 25.7% CPU · 117 MiB/s · 2186.1 µs CPU/req)</sub> | **457**<br><sub>(212 MiB / 50.4% CPU · 114 MiB/s · 4406.7 µs CPU/req)</sub> |
| Early response (origin writes after first request chunk) | HTTP/1 · TLS | HTTP/1 · plain | **6,326**<br><sub>(147 MiB / 44.5% CPU · 791 MiB/s · 281.1 µs CPU/req)</sub> | **5,436**<br><sub>(110 MiB / 47.4% CPU · 679 MiB/s · 348.4 µs CPU/req)</sub> | 🥇 **6,948**<br><sub>(92 MiB / 43.1% CPU · 868 MiB/s · 248.0 µs CPU/req)</sub> | **0**<br><sub>(peak 64 · 125 MiB / 3.0% CPU)</sub> | **4,264**<br><sub>(182 MiB / 57.0% CPU · 533 MiB/s · 535.1 µs CPU/req)</sub> |
| Early response (origin writes after first request chunk) | HTTP/2 · TLS | HTTP/1 · plain | **4,584**<br><sub>(262 MiB / 52.3% CPU · 573 MiB/s · 456.2 µs CPU/req)</sub> | **5,227**<br><sub>(118 MiB / 45.4% CPU · 653 MiB/s · 347.2 µs CPU/req)</sub> | 🥇 **6,258**<br><sub>(92 MiB / 43.2% CPU · 782 MiB/s · 276.4 µs CPU/req)</sub> | **0**<br><sub>(peak 6,178 · 136 MiB / 43.8% CPU)</sub> | **5,322**<br><sub>(172 MiB / 49.6% CPU · 665 MiB/s · 372.6 µs CPU/req)</sub> |
| Early response (origin writes after first request chunk) | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **5,361**<br><sub>(274 MiB / 46.9% CPU · 670 MiB/s · 349.8 µs CPU/req)</sub> | **1,860**<br><sub>(peak 2,004 · 123 MiB / 37.3% CPU · 233 MiB/s · 802.2 µs CPU/req)</sub> | **4,285**<br><sub>(97 MiB / 38.1% CPU · 536 MiB/s · 356.0 µs CPU/req)</sub> | **0**<br><sub>(126 MiB / 0.1% CPU)</sub> | **3,199**<br><sub>(304 MiB / 47.9% CPU · 400 MiB/s · 598.7 µs CPU/req)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · plain | HTTP/1 · plain | **480**<br><sub>(164 MiB / 10.8% CPU · 120 MiB/s · 903.0 µs CPU/req)</sub> | 🥇 **480**<br><sub>(81 MiB / 9.9% CPU · 120 MiB/s · 827.0 µs CPU/req)</sub> | **480**<br><sub>(72 MiB / 4.4% CPU · 120 MiB/s · 365.3 µs CPU/req)</sub> | **480**<br><sub>(129 MiB / 4.4% CPU · 120 MiB/s · 369.2 µs CPU/req)</sub> | **477**<br><sub>(148 MiB / 9.9% CPU · 119 MiB/s · 830.5 µs CPU/req)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · TLS | HTTP/2 · TLS | **468**<br><sub>(210 MiB / 23.6% CPU · 117 MiB/s · 2019.2 µs CPU/req)</sub> | *Not possible* | **471**<br><sub>(97 MiB / 18.9% CPU · 118 MiB/s · 1608.7 µs CPU/req)</sub> | 🥇 **473**<br><sub>(135 MiB / 16.7% CPU · 118 MiB/s · 1409.1 µs CPU/req)</sub> | **468**<br><sub>(173 MiB / 29.2% CPU · 117 MiB/s · 2492.8 µs CPU/req)</sub> |
| Early response (origin writes after first request chunk) | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **6,248**<br><sub>(246 MiB / 37.0% CPU · 781 MiB/s · 237.1 µs CPU/req)</sub> | *Not possible* | **3,848**<br><sub>(94 MiB / 31.4% CPU · 481 MiB/s · 325.8 µs CPU/req)</sub> | **0**<br><sub>(peak 4,180 · 139 MiB / 32.1% CPU)</sub> | **4,499**<br><sub>(218 MiB / 53.9% CPU · 562 MiB/s · 479.1 µs CPU/req)</sub> |
| Duplex (both directions live) | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **4,584**<br><sub>(249 MiB / 38.4% CPU · 573 MiB/s · 335.0 µs CPU/req)</sub> | *Not possible* | **3,345**<br><sub>(88 MiB / 37.0% CPU · 418 MiB/s · 442.7 µs CPU/req)</sub> | **0**<br><sub>(peak 3,637 · 139 MiB / 36.0% CPU)</sub> | **3,411**<br><sub>(213 MiB / 55.1% CPU · 426 MiB/s · 645.7 µs CPU/req)</sub> |
| Duplex (WebSocket / H1 Upgrade) | HTTP/1 · TLS | HTTP/1 · plain | **33,768**<br><sub>(134 MiB / 43.5% CPU · 51.6 µs CPU/req)</sub> | 🥇 **37,574**<br><sub>(109 MiB / 37.2% CPU · 39.6 µs CPU/req)</sub> | **35,966**<br><sub>(84 MiB / 40.6% CPU · 45.1 µs CPU/req)</sub> | **33,915**<br><sub>(122 MiB / 42.4% CPU · 50.0 µs CPU/req)</sub> | **30,998**<br><sub>(129 MiB / 44.4% CPU · 57.3 µs CPU/req)</sub> |

#### macOS

Median of **3** repeats on `macos-15` (Apple Silicon M1, 3-core / 7 GB) @ `b6700ebe`. Source: Actions [42394364277](https://github.com/justcoding121/titanium-web-proxy/actions/runs/42394364277) (`compare-arch`).

| Scenario | Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---|---:|---:|---:|---:|---:|
| Slow consumer (256 KiB GET, throttled client read) | HTTP/1 · TLS | HTTP/1 · plain | **97**<br><sub>(248 MiB / 4.6% CPU · 24 MiB/s · 1439.5 µs CPU/req)</sub> | 🥇 **127**<br><sub>(85 MiB / 2.6% CPU · 32 MiB/s · 623.2 µs CPU/req)</sub> | **112**<br><sub>(111 MiB / 1.4% CPU · 28 MiB/s · 363.0 µs CPU/req)</sub> | **88**<br><sub>(145 MiB / 2.0% CPU · 22 MiB/s · 695.5 µs CPU/req)</sub> | **111**<br><sub>(217 MiB / 3.9% CPU · 28 MiB/s · 1036.8 µs CPU/req)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **111**<br><sub>(372 MiB / 8.8% CPU · 28 MiB/s · 2358.5 µs CPU/req)</sub> | **110**<br><sub>(84 MiB / 3.3% CPU · 27 MiB/s · 892.3 µs CPU/req)</sub> | **103**<br><sub>(110 MiB / 2.6% CPU · 26 MiB/s · 766.0 µs CPU/req)</sub> | **80**<br><sub>(153 MiB / 2.0% CPU · 20 MiB/s · 739.7 µs CPU/req)</sub> | **96**<br><sub>(222 MiB / 4.9% CPU · 24 MiB/s · 1534.4 µs CPU/req)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/3 · QUIC | HTTP/1 · plain | **71**<br><sub>(137 MiB / 22.8% CPU · 18 MiB/s · 9669.5 µs CPU/req)</sub> | 🥇 **120**<br><sub>(95 MiB / 7.0% CPU · 30 MiB/s · 1741.9 µs CPU/req)</sub> | **118**<br><sub>(112 MiB / 13.7% CPU · 30 MiB/s · 3471.3 µs CPU/req)</sub> | **96**<br><sub>(145 MiB / 14.6% CPU · 24 MiB/s · 4557.9 µs CPU/req)</sub> | **72**<br><sub>(363 MiB / 18.2% CPU · 18 MiB/s · 7636.0 µs CPU/req)</sub> |
| Early response (origin writes after first request chunk) | HTTP/1 · TLS | HTTP/1 · plain | **1,972**<br><sub>(281 MiB / 24.6% CPU · 246 MiB/s · 373.8 µs CPU/req)</sub> | **0**<br><sub>(peak 2,410 · 89 MiB / 20.0% CPU)</sub> | 🥇 **2,687**<br><sub>(122 MiB / 26.4% CPU · 336 MiB/s · 294.4 µs CPU/req)</sub> | **0**<br><sub>(peak 2,581 · 130 MiB / 29.3% CPU)</sub> | **1,501**<br><sub>(228 MiB / 30.8% CPU · 188 MiB/s · 615.6 µs CPU/req)</sub> |
| Early response (origin writes after first request chunk) | HTTP/2 · TLS | HTTP/1 · plain | **2,301**<br><sub>(533 MiB / 27.0% CPU · 288 MiB/s · 351.6 µs CPU/req)</sub> | **3,244**<br><sub>(99 MiB / 23.2% CPU · 406 MiB/s · 214.5 µs CPU/req)</sub> | **3,206**<br><sub>(122 MiB / 29.0% CPU · 401 MiB/s · 271.1 µs CPU/req)</sub> | **0**<br><sub>(peak 3,977 · 143 MiB / 26.5% CPU)</sub> | 🥇 **3,338**<br><sub>(175 MiB / 41.6% CPU · 417 MiB/s · 374.2 µs CPU/req)</sub> |
| Early response (origin writes after first request chunk) | HTTP/3 · QUIC | HTTP/1 · plain | **1,503**<br><sub>(271 MiB / 36.5% CPU · 188 MiB/s · 728.7 µs CPU/req)</sub> | 🥇 **1,958**<br><sub>(91 MiB / 22.7% CPU · 245 MiB/s · 348.2 µs CPU/req)</sub> | **998**<br><sub>(119 MiB / 31.1% CPU · 125 MiB/s · 936.1 µs CPU/req)</sub> | **1,290**<br><sub>(130 MiB / 41.6% CPU · 161 MiB/s · 967.8 µs CPU/req)</sub> | **828**<br><sub>(405 MiB / 30.2% CPU · 104 MiB/s · 1093.5 µs CPU/req)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · plain | HTTP/1 · plain | **104**<br><sub>(225 MiB / 4.3% CPU · 26 MiB/s · 1255.5 µs CPU/req)</sub> | 🥇 **120**<br><sub>(72 MiB / 1.6% CPU · 30 MiB/s · 413.2 µs CPU/req)</sub> | **120**<br><sub>(101 MiB / 2.1% CPU · 30 MiB/s · 522.2 µs CPU/req)</sub> | **88**<br><sub>(144 MiB / 2.1% CPU · 22 MiB/s · 716.7 µs CPU/req)</sub> | **112**<br><sub>(145 MiB / 4.9% CPU · 28 MiB/s · 1307.6 µs CPU/req)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · TLS | HTTP/2 · TLS | **104**<br><sub>(495 MiB / 7.9% CPU · 26 MiB/s · 2267.3 µs CPU/req)</sub> | *Not possible* | 🥇 **119**<br><sub>(126 MiB / 5.0% CPU · 30 MiB/s · 1268.5 µs CPU/req)</sub> | **96**<br><sub>(130 MiB / 3.9% CPU · 24 MiB/s · 1215.7 µs CPU/req)</sub> | **112**<br><sub>(308 MiB / 7.6% CPU · 28 MiB/s · 2032.3 µs CPU/req)</sub> |
| Early response (origin writes after first request chunk) | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **1,852**<br><sub>(444 MiB / 23.6% CPU · 232 MiB/s · 381.5 µs CPU/req)</sub> | *Not possible* | **1,672**<br><sub>(peak 1,735 · 118 MiB / 20.4% CPU · 209 MiB/s · 366.3 µs CPU/req)</sub> | **0**<br><sub>(peak 1,682 · 131 MiB / 22.6% CPU)</sub> | **1,407**<br><sub>(325 MiB / 34.1% CPU · 176 MiB/s · 727.7 µs CPU/req)</sub> |
| Duplex (both directions live) | HTTP/2 · TLS | HTTP/2 · TLS | **2,108**<br><sub>(515 MiB / 26.1% CPU · 263 MiB/s · 371.4 µs CPU/req)</sub> | *Not possible* | **1,976**<br><sub>(121 MiB / 21.4% CPU · 247 MiB/s · 325.1 µs CPU/req)</sub> | **0**<br><sub>(peak 1,940 · 129 MiB / 20.3% CPU)</sub> | 🥇 **2,260**<br><sub>(268 MiB / 26.6% CPU · 282 MiB/s · 353.7 µs CPU/req)</sub> |
| Duplex (WebSocket / H1 Upgrade) | HTTP/1 · TLS | HTTP/1 · plain | **10,119**<br><sub>(166 MiB / 18.3% CPU · 54.2 µs CPU/req)</sub> | **7,838**<br><sub>(86 MiB / 9.4% CPU · 36.1 µs CPU/req)</sub> | **8,962**<br><sub>(103 MiB / 12.0% CPU · 40.1 µs CPU/req)</sub> | 🥇 **10,361**<br><sub>(121 MiB / 16.0% CPU · 46.3 µs CPU/req)</sub> | **5,012**<br><sub>(145 MiB / 11.3% CPU · 67.6 µs CPU/req)</sub> |

Slow consumer is sleep-bound; H1/H2/H3 sit in the same band. Prefer the ratio columns for early-response, duplex, and WebSocket rows.

### TLS termination cost (H1 TLS → cleartext origin)

Isolates keep-alive tiny GET vs **new connection per request** (handshake-dominated) vs keep-alive **256 KiB**. Product comparison uses RPS and end-to-end latency; TWP can also capture `ClientTlsTiming` when `TWP_RPS_CAPTURE_TLS=1` (child process) — nginx/YARP have no equivalent hook.

#### Windows

Median of **3** repeats on `windows-latest` @ `b6700ebe`. Source: Actions [42394358002](https://github.com/justcoding121/titanium-web-proxy/actions/runs/42394358002). Absolute RPS on GHA swings hard; prefer **TWP÷YARP**.


*Not possible:* **HAProxy** and **Envoy** columns are omitted (no official Windows port).

| Workload | TWP | nginx | YARP |
|---|---:|---:|---:|
| Keep-alive · tiny GET | 🥇 **33,730**<br><sub>(91 MiB / 46.7% CPU · 55.4 µs CPU/req)</sub> | **16,006**<br><sub>(144 MiB / 24.7% CPU · 61.8 µs CPU/req)</sub> | **29,474**<br><sub>(105 MiB / 50.2% CPU · 68.2 µs CPU/req)</sub> |
| New-connection · tiny GET | 🥇 **726**<br><sub>(87 MiB / 1.4% CPU · 78.8 µs CPU/req)</sub> | **252**<br><sub>(143 MiB / 1.9% CPU · 296.8 µs CPU/req)</sub> | **701**<br><sub>(114 MiB / 0.5% CPU · 27.4 µs CPU/req)</sub> |
| Keep-alive · 256 KiB GET | 🥇 **3,806**<br><sub>(139 MiB / 42.8% CPU · 951 MiB/s · 449.7 µs CPU/req)</sub> | **242**<br><sub>(144 MiB / 24.9% CPU · 61 MiB/s · 4107.4 µs CPU/req)</sub> | **3,379**<br><sub>(135 MiB / 48.1% CPU · 845 MiB/s · 569.1 µs CPU/req)</sub> |

#### Linux

Median of **3** repeats @ `b6700ebe`. Source: Actions [42394358002](https://github.com/justcoding121/titanium-web-proxy/actions/runs/42394358002).


| Workload | TWP | nginx | HAProxy | Envoy | YARP |
|---|---:|---:|---:|---:|---:|
| Keep-alive · tiny GET | **26,158**<br><sub>(113 MiB / 48.7% CPU · 74.5 µs CPU/req)</sub> | 🥇 **30,214**<br><sub>(109 MiB / 42.2% CPU · 55.9 µs CPU/req)</sub> | **28,504**<br><sub>(84 MiB / 45.2% CPU · 63.5 µs CPU/req)</sub> | **18,176**<br><sub>(121 MiB / 60.8% CPU · 133.8 µs CPU/req)</sub> | **22,562**<br><sub>(136 MiB / 51.2% CPU · 90.8 µs CPU/req)</sub> |
| New-connection · tiny GET | **1,061**<br><sub>(130 MiB / 1.0% CPU · 37.3 µs CPU/req)</sub> | **1,084**<br><sub>(109 MiB / 1.0% CPU · 36.2 µs CPU/req)</sub> | **1,098**<br><sub>(85 MiB / 0.2% CPU · 9.1 µs CPU/req)</sub> | 🥇 **1,239**<br><sub>(125 MiB / 0.5% CPU · 15.8 µs CPU/req)</sub> | **1,065**<br><sub>(149 MiB / 0.2% CPU · 9.4 µs CPU/req)</sub> |
| Keep-alive · 256 KiB GET | **3,705**<br><sub>(135 MiB / 30.4% CPU · 926 MiB/s · 328.0 µs CPU/req)</sub> | **2,597**<br><sub>(110 MiB / 45.1% CPU · 649 MiB/s · 694.4 µs CPU/req)</sub> | 🥇 **3,834**<br><sub>(88 MiB / 27.1% CPU · 958 MiB/s · 282.6 µs CPU/req)</sub> | **3,309**<br><sub>(141 MiB / 35.7% CPU · 827 MiB/s · 431.9 µs CPU/req)</sub> | **2,828**<br><sub>(178 MiB / 42.3% CPU · 707 MiB/s · 598.8 µs CPU/req)</sub> |

#### macOS

Median of **3** repeats on `macos-15` @ `b6700ebe`. Source: Actions [42394358002](https://github.com/justcoding121/titanium-web-proxy/actions/runs/42394358002).

| Workload | TWP | nginx | HAProxy | Envoy | YARP |
|---|---:|---:|---:|---:|---:|
| Keep-alive · tiny GET | **6,953**<br><sub>(152 MiB / 16.4% CPU · 70.6 µs CPU/req)</sub> | **8,838**<br><sub>(83 MiB / 14.4% CPU · 48.8 µs CPU/req)</sub> | 🥇 **10,060**<br><sub>(108 MiB / 18.5% CPU · 55.2 µs CPU/req)</sub> | **7,044**<br><sub>(120 MiB / 34.3% CPU · 145.9 µs CPU/req)</sub> | **7,218**<br><sub>(212 MiB / 19.6% CPU · 81.3 µs CPU/req)</sub> |
| New-connection · tiny GET | **108**<br><sub>(111 MiB / 0.2% CPU · 64.1 µs CPU/req)</sub> | **617**<br><sub>(86 MiB)</sub> | **624**<br><sub>(105 MiB)</sub> | 🥇 **688**<br><sub>(130 MiB / 0.1% CPU · 2.2 µs CPU/req)</sub> | **110**<br><sub>(159 MiB / 0.1% CPU · 19.1 µs CPU/req)</sub> |
| Keep-alive · 256 KiB GET | **1,927**<br><sub>(210 MiB / 28.3% CPU · 482 MiB/s · 440.2 µs CPU/req)</sub> | **1,883**<br><sub>(84 MiB / 25.1% CPU · 471 MiB/s · 399.2 µs CPU/req)</sub> | **2,060**<br><sub>(119 MiB / 28.9% CPU · 515 MiB/s · 421.4 µs CPU/req)</sub> | **1,592**<br><sub>(154 MiB / 25.8% CPU · 398 MiB/s · 486.3 µs CPU/req)</sub> | 🥇 **2,195**<br><sub>(182 MiB / 39.6% CPU · 549 MiB/s · 541.2 µs CPU/req)</sub> |

Prefer **TWP÷YARP** in the table. Absolute RPS on GHA swings hard. New-connection is Darwin SslStream-bound for TWP and YARP (handshake p99 SLO **500 ms** on macOS only — Win/Linux stay at **200 ms**).

## Unary gRPC (H2 TLS)

Unary Echo **RPC/s** @ c=64 over H2 TLS→H2 TLS for Titanium, YARP, nginx (`grpc_pass`), HAProxy, and Envoy (OS-possible peers only). Not folded into the architecture-sensitive tables. Median of **3** repeats @ `b6700ebe` — [42394333642](https://github.com/justcoding121/titanium-web-proxy/actions/runs/42394333642).

| OS | Titanium | YARP | nginx | HAProxy | Envoy |
|---|---:|---:|---:|---:|---:|
| Windows | 🥇 **45,256**<br><sub>(128 MiB / 32.6% CPU · 28.9 µs CPU/req)</sub> | **28,362**<br><sub>(124 MiB / 46.4% CPU · 65.5 µs CPU/req)</sub> | **0**<br><sub>(145 MiB / 24.9% CPU)</sub> | *Not possible* | *Not possible* |
| Linux | 🥇 **35,120**<br><sub>(173 MiB / 33.2% CPU · 37.8 µs CPU/req)</sub> | **21,874**<br><sub>(159 MiB / 43.0% CPU · 78.6 µs CPU/req)</sub> | **0**<br><sub>(112 MiB / 45.0% CPU)</sub> | **17,201**<br><sub>(85 MiB / 46.3% CPU · 107.6 µs CPU/req)</sub> | **16,771**<br><sub>(121 MiB / 58.1% CPU · 138.5 µs CPU/req)</sub> |
| macOS | 🥇 **12,313**<br><sub>(275 MiB / 15.7% CPU · 38.3 µs CPU/req)</sub> | **10,122**<br><sub>(218 MiB / 20.4% CPU · 60.5 µs CPU/req)</sub> | **0**<br><sub>(85 MiB / 2.8% CPU)</sub> | **11,748**<br><sub>(116 MiB / 27.8% CPU · 71.0 µs CPU/req)</sub> | **8,963**<br><sub>(119 MiB / 16.0% CPU · 53.6 µs CPU/req)</sub> |

## Unary gRPC (H2 TLS → h2c)

Unary Echo **RPC/s** @ c=64 over **H2 TLS → h2c** (edge TLS, cleartext H2 origin). Peers: Titanium, YARP, HAProxy, Envoy. Mode: `compare-grpc` (`*-grpc-h2c`). Median of **3** repeats @ `b6700ebe` — [42394333642](https://github.com/justcoding121/titanium-web-proxy/actions/runs/42394333642).

*Not possible:* **nginx** column omitted (no H2 upstream).

| OS | Titanium | YARP | HAProxy | Envoy |
|---|---:|---:|---:|---:|
| Windows | 🥇 **47,183**<br><sub>(127 MiB / 31.5% CPU · 26.7 µs CPU/req)</sub> | **29,671**<br><sub>(135 MiB / 49.4% CPU · 66.5 µs CPU/req)</sub> | *Not possible* | *Not possible* |
| Linux | 🥇 **88,125**<br><sub>(171 MiB / 33.9% CPU · 15.4 µs CPU/req)</sub> | **52,711**<br><sub>(155 MiB / 42.8% CPU · 32.4 µs CPU/req)</sub> | **41,698**<br><sub>(84 MiB / 45.3% CPU · 43.5 µs CPU/req)</sub> | **49,768**<br><sub>(121 MiB / 49.5% CPU · 39.8 µs CPU/req)</sub> |
| macOS | 🥇 **17,711**<br><sub>(293 MiB / 13.6% CPU · 23.0 µs CPU/req)</sub> | **8,131**<br><sub>(265 MiB / 12.4% CPU · 45.6 µs CPU/req)</sub> | **8,124**<br><sub>(118 MiB / 17.8% CPU · 65.6 µs CPU/req)</sub> | **8,811**<br><sub>(120 MiB / 20.0% CPU · 68.3 µs CPU/req)</sub> |

## WebSocket (H1 TLS → H1 TLS)

WebSocket echo round-trips/sec over **dual-TLS** H1 (`proxy_ssl` style). Mode: `compare-ws-h1tls` (`*-duplex-ws-h1tls`). Median of **3** repeats @ `b6700ebe` — [42394333642](https://github.com/justcoding121/titanium-web-proxy/actions/runs/42394333642).

| OS | Titanium | YARP | nginx | HAProxy | Envoy |
|---|---:|---:|---:|---:|---:|
| Windows | 🥇 **21,585**<br><sub>(103 MiB / 44.1% CPU · 81.8 µs CPU/req)</sub> | **20,378**<br><sub>(92 MiB / 41.5% CPU · 81.5 µs CPU/req)</sub> | **9,093**<br><sub>(145 MiB / 24.7% CPU · 108.5 µs CPU/req)</sub> | *Not possible* | *Not possible* |
| Linux | **28,908**<br><sub>(158 MiB / 43.1% CPU · 59.6 µs CPU/req)</sub> | **25,520**<br><sub>(139 MiB / 43.6% CPU · 68.3 µs CPU/req)</sub> | 🥇 **31,541**<br><sub>(113 MiB / 37.6% CPU · 47.7 µs CPU/req)</sub> | **29,109**<br><sub>(86 MiB / 40.9% CPU · 56.1 µs CPU/req)</sub> | **30,270**<br><sub>(122 MiB / 39.8% CPU · 52.6 µs CPU/req)</sub> |
| macOS | **17,768**<br><sub>(149 MiB / 27.0% CPU · 45.6 µs CPU/req)</sub> | **18,625**<br><sub>(167 MiB / 25.5% CPU · 41.0 µs CPU/req)</sub> | **12,593**<br><sub>(84 MiB / 17.5% CPU · 41.8 µs CPU/req)</sub> | 🥇 **25,159**<br><sub>(108 MiB / 28.4% CPU · 33.8 µs CPU/req)</sub> | **22,794**<br><sub>(118 MiB / 20.3% CPU · 26.7 µs CPU/req)</sub> |

## WebSocket (H2 TLS 8441 → H1)

WebSocket echo over **RFC 8441** extended CONNECT (H2 TLS client → H1 plain origin). Mode: `compare-ws-h2` (`*-duplex-ws-h2`).

The probe client sets `NoDelay` and writes each HTTP/2 frame as one TLS record. Median of **3** repeats @ `b6700ebe` — [42394333642](https://github.com/justcoding121/titanium-web-proxy/actions/runs/42394333642).

*Not possible:* **nginx** column omitted (no RFC 8441 extended CONNECT).

| OS | Titanium | YARP | HAProxy | Envoy |
|---|---:|---:|---:|---:|
| Windows | 🥇 **39,541**<br><sub>(177 MiB / 45.5% CPU · 46.0 µs CPU/req)</sub> | **0**<br><sub>(158 MiB / 18.1% CPU)</sub> | *Not possible* | *Not possible* |
| Linux | 🥇 **30,776**<br><sub>(202 MiB / 48.4% CPU · 62.9 µs CPU/req)</sub> | **0**<br><sub>(191 MiB / 54.3% CPU)</sub> | **26,511**<br><sub>(84 MiB / 42.8% CPU · 64.6 µs CPU/req)</sub> | **0**<br><sub>(118 MiB / 0.3% CPU)</sub> |
| macOS | **24,172**<br><sub>(306 MiB / 29.6% CPU · 36.8 µs CPU/req)</sub> | **0**<br><sub>(199 MiB / 7.4% CPU)</sub> | 🥇 **32,840**<br><sub>(113 MiB / 29.9% CPU · 27.3 µs CPU/req)</sub> | **0**<br><sub>(114 MiB / 0.6% CPU)</sub> |

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

`--arm-shard` takes a comparison-group key (`h1c-h1c`, from `--print-groups`), not an `i/n` index, so a re-run measures the same row after a peer install flake. `i/n` remains for smoke modes. Paste unions `rps-csv-<os>-shard-*` (`paste-compare-product-wiki.ps1 -RunIds …`). Table “Source: Actions” may list several run URLs for one table — **do not mix SHAs**. A single-row leg allows **60** minutes of ramp and **75** minutes overall. Free GitHub accounts run **20** jobs at once, **5** of them macOS. Give a full suite a quiet window: do not push pull requests or start other macOS jobs while it runs. Re-run infrastructure failures only (`gh run rerun --failed`). A failed gate is a finding and is never re-rolled; publish the last attempt, never the best of several. The RPS tables on this page are @ `b6700ebe`: product [42394333642](https://github.com/justcoding121/titanium-web-proxy/actions/runs/42394333642), saturation [42394388112](https://github.com/justcoding121/titanium-web-proxy/actions/runs/42394388112), bodies [42394339299](https://github.com/justcoding121/titanium-web-proxy/actions/runs/42394339299), POST [42394345253](https://github.com/justcoding121/titanium-web-proxy/actions/runs/42394345253), lossy [42394351712](https://github.com/justcoding121/titanium-web-proxy/actions/runs/42394351712), arch [42394364277](https://github.com/justcoding121/titanium-web-proxy/actions/runs/42394364277), TLS cost [42394358002](https://github.com/justcoding121/titanium-web-proxy/actions/runs/42394358002), gRPC [42394370091](https://github.com/justcoding121/titanium-web-proxy/actions/runs/42394370091), WebSocket H1 TLS [42394376449](https://github.com/justcoding121/titanium-web-proxy/actions/runs/42394376449), WebSocket H2 [42394382718](https://github.com/justcoding121/titanium-web-proxy/actions/runs/42394382718), editions [42394393340](https://github.com/justcoding121/titanium-web-proxy/actions/runs/42394393340). Ten suite aggregates were green; editions failed the `edition-extauth` floor once (`CLI+extAuth ÷ CLI = 0.499`, gate 0.50) and that finding is published without a re-roll. The prior refresh @ `4038c6b1` remains historical only. Older notes on the `8567b4c0` bodies gate miss and the `88187363` cluster remain historical only. Reverse product rows, bodies, POST, lossy, arch, TLS cost, gRPC, both WebSocket modes, and saturation run on Windows, Linux, and macOS. TWP MITM arms run on Linux only. `compare-editions` (CLI and Plus) runs on Linux only. `compare-spot` on pull requests to beta and stable stays on all three OS. The historical v2 MITM note below the product tables stays @ `41f4adee` because those run ids are from that fix, not this re-measure.

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
