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
- **Typical reverse** is H1 TLS→H1 or H2→H1 (~1.1× YARP on Win/Linux tiny GET). With **larger bodies**, see [Heavier reverse](#user-content-heavier-reverse-workloads) (@ `e0c98136`).

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

On the Linux H2 TLS→H1 plain row at c=64 (16 connections, @ `e0c98136`), proxy CPU was about **44%** for nginx, **45%** for HAProxy, and **55%** for Envoy. Windows nginx on the same H2 client shape stayed near **25%** (one core). That measured shape is the one published here.

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

Calibration for the shared 4 vCPU loopback shape: how close client + origin are to saturated before ranking reverse peers. Tiny keep-alive GET. Median of **3** repeats @ `e0c98136` — [41268398087](https://github.com/justcoding121/titanium-web-proxy/actions/runs/41268398087). Warmup 2s / measure 8s; concurrency 8, 16, 32, 64. Block A **% of origin-HttpClient** uses median **peak** RPS. Blocks B/C use peer÷YARP / ÷nginx on median peak (not % of H1 origin). **RPS cells** embed median RSS / CPU for the **proxy child** plus its **full descendant tree** (serve-proxy → nginx master → workers); origin-direct samples the **origin** child. Product matrices below use matched `dotnet-httpclient` only (not bombardier).


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
| origin-direct | dotnet-httpclient | **50,614**<br><sub>(55 MiB / 41.3% CPU · 32.6 µs CPU/req)</sub> | **100.0%** |
| origin-direct-bombardier | bombardier | **38,473**<br><sub>(56 MiB / 22.8% CPU · 23.7 µs CPU/req)</sub> | **76.0%** |
| bare-reverse-http1 | dotnet-httpclient | **25,548**<br><sub>(56 MiB / 44.1% CPU · 69.0 µs CPU/req)</sub> | **50.5%** |
| nginx-reverse-http1 | dotnet-httpclient | **13,404**<br><sub>(127 MiB / 24.7% CPU · 73.8 µs CPU/req)</sub> | **26.5%** |
| yarp-reverse-http1 | dotnet-httpclient | **21,020**<br><sub>(87 MiB / 49.2% CPU · 93.6 µs CPU/req)</sub> | **41.5%** |
| twp-reverse-http1 | dotnet-httpclient | 🥇 **25,560**<br><sub>(77 MiB / 51.3% CPU · 80.3 µs CPU/req)</sub> | **50.5%** |

**Linux** (`ubuntu-latest`)

| Arm | Generator | RPS | % of origin-HttpClient |
|---|---|---:|---:|
| origin-direct | dotnet-httpclient | **126,893**<br><sub>(82 MiB / 43.6% CPU · 13.7 µs CPU/req)</sub> | **100.0%** |
| origin-direct-bombardier | bombardier | **67,582**<br><sub>(82 MiB / 29.5% CPU · 17.5 µs CPU/req)</sub> | **53.3%** |
| bare-reverse-http1 | dotnet-httpclient | **58,460**<br><sub>(72 MiB / 41.8% CPU · 28.6 µs CPU/req)</sub> | **46.1%** |
| nginx-reverse-http1 | dotnet-httpclient | 🥇 **76,516**<br><sub>(79 MiB / 38.0% CPU · 19.9 µs CPU/req)</sub> | **60.3%** |
| yarp-reverse-http1 | dotnet-httpclient | **51,966**<br><sub>(120 MiB / 49.3% CPU · 38.0 µs CPU/req)</sub> | **41.0%** |
| twp-reverse-http1 | dotnet-httpclient | **56,930**<br><sub>(86 MiB / 46.2% CPU · 32.5 µs CPU/req)</sub> | **44.9%** |


**macOS** (`macos-15`, Apple Silicon)

| Arm | Generator | RPS | % of origin-HttpClient |
|---|---|---:|---:|
| origin-direct | dotnet-httpclient | **65,682**<br><sub>(88 MiB / 32.2% CPU · 19.6 µs CPU/req)</sub> | **100.0%** |
| origin-direct-bombardier | bombardier | **46,925**<br><sub>(88 MiB / 24.3% CPU · 20.7 µs CPU/req)</sub> | **71.4%** |
| bare-reverse-http1 | dotnet-httpclient | **24,978**<br><sub>(91 MiB / 27.9% CPU · 44.8 µs CPU/req)</sub> | **38.0%** |
| nginx-reverse-http1 | dotnet-httpclient | **35,274**<br><sub>(67 MiB / 22.6% CPU · 25.6 µs CPU/req)</sub> | **53.7%** |
| yarp-reverse-http1 | dotnet-httpclient | **21,352**<br><sub>(147 MiB / 35.5% CPU · 66.4 µs CPU/req)</sub> | **32.5%** |
| twp-reverse-http1 | dotnet-httpclient | 🥇 **42,332**<br><sub>(119 MiB / 38.0% CPU · 35.9 µs CPU/req)</sub> | **64.5%** |

On this macOS run `origin-direct` was **32.2%** CPU, so the % of origin-HttpClient column is not a ceiling when that CPU is near idle. Rank those Mac peers by RPS.

Reverse peers on Block A @ `e0c98136`: Windows TWP is **50.5%** of origin-direct and Linux TWP is **44.9%**. Prefer the **%** column over absolute RPS across runs. Bare and origin-direct are controls (not medal peers).

#### Block B — H2 TLS→H1

Peer ratios (÷YARP / ÷nginx) on median peak; **RPS cells** embed `(MiB / CPU%)`.

**Windows** (`windows-latest`)

| Arm | Generator | RPS | ÷YARP | ÷nginx |
|---|---|---:|---:|---:|
| nginx-reverse-http2 | dotnet-httpclient | **7,680**<br><sub>(144 MiB / 24.8% CPU · 129.3 µs CPU/req)</sub> | **0.28×** | **1.00×** |
| yarp-reverse-http2 | dotnet-httpclient | **27,054**<br><sub>(105 MiB / 52.8% CPU · 78.1 µs CPU/req)</sub> | **1.00×** | **3.52×** |
| twp-reverse-http2-cleartext | dotnet-httpclient | 🥇 **29,913**<br><sub>(118 MiB / 50.7% CPU · 67.8 µs CPU/req)</sub> | **1.11×** | **3.89×** |

**Linux** (`ubuntu-latest`)

| Arm | Generator | RPS | ÷YARP | ÷nginx |
|---|---|---:|---:|---:|
| nginx-reverse-http2 | dotnet-httpclient | 🥇 **51,663**<br><sub>(110 MiB / 38.7% CPU · 30.0 µs CPU/req)</sub> | **1.15×** | **1.00×** |
| yarp-reverse-http2 | dotnet-httpclient | **44,761**<br><sub>(138 MiB / 46.9% CPU · 41.9 µs CPU/req)</sub> | **1.00×** | **0.87×** |
| twp-reverse-http2-cleartext | dotnet-httpclient | **46,715**<br><sub>(155 MiB / 47.4% CPU · 40.6 µs CPU/req)</sub> | **1.04×** | **0.90×** |

**macOS** (`macos-15`, Apple Silicon)

| Arm | Generator | RPS | ÷YARP | ÷nginx |
|---|---|---:|---:|---:|
| nginx-reverse-http2 | dotnet-httpclient | **29,043**<br><sub>(82 MiB / 24.8% CPU · 34.1 µs CPU/req)</sub> | **0.88×** | **1.00×** |
| yarp-reverse-http2 | dotnet-httpclient | **33,162**<br><sub>(157 MiB / 41.1% CPU · 49.6 µs CPU/req)</sub> | **1.00×** | **1.14×** |
| twp-reverse-http2-cleartext | dotnet-httpclient | 🥇 **35,960**<br><sub>(173 MiB / 40.4% CPU · 44.9 µs CPU/req)</sub> | **1.08×** | **1.24×** |
#### Block C — H3→H1

Same layout as Block B. Requires QuicListener. nginx needs `http_v3_module` (Windows nginx has no QUIC). HAProxy needs `USE_QUIC` (GHA Linux/macOS require it). Envoy 1.20+ includes HTTP/3.

Medals are nginx / YARP / Titanium only, from the same run as Blocks A and B ([40610501155](https://github.com/justcoding121/titanium-web-proxy/actions/runs/40610501155)).

**Windows** (`windows-latest`)

| Arm | Generator | RPS | ÷YARP | ÷nginx |
|---|---|---:|---:|---:|
| nginx-reverse-http3-cleartext | dotnet-httpclient | *Not possible (no QUIC)* | — | — |
| yarp-reverse-http3-cleartext | dotnet-httpclient | **7,756**<br><sub>(153 MiB / 49.6% CPU · 255.8 µs CPU/req)</sub> | **1.00×** | — |
| twp-reverse-http3-cleartext | dotnet-httpclient | 🥇 **8,521**<br><sub>(111 MiB / 48.9% CPU · 229.4 µs CPU/req)</sub> | **1.10×** | — |

**Linux** (`ubuntu-latest`)

| Arm | Generator | RPS | ÷YARP | ÷nginx |
|---|---|---:|---:|---:|
| nginx-reverse-http3-cleartext | dotnet-httpclient | 🥇 **35,995**<br><sub>(114 MiB / 25.8% CPU · 28.6 µs CPU/req)</sub> | **1.57×** | **1.00×** |
| yarp-reverse-http3-cleartext | dotnet-httpclient | **22,932**<br><sub>(207 MiB / 49.0% CPU · 85.5 µs CPU/req)</sub> | **1.00×** | **0.64×** |
| twp-reverse-http3-cleartext | dotnet-httpclient | **26,079**<br><sub>(167 MiB / 50.6% CPU · 77.7 µs CPU/req)</sub> | **1.14×** | **0.72×** |

**macOS** (`macos-15`, Apple Silicon)

| Arm | Generator | RPS | ÷YARP | ÷nginx |
|---|---|---:|---:|---:|
| nginx-reverse-http3-cleartext | dotnet-httpclient | 🥇 **23,930**<br><sub>(peak 25,429 · 84 MiB / 22.0% CPU · 36.8 µs CPU/req)</sub> | **1.41×** | **1.00×** |
| yarp-reverse-http3-cleartext | dotnet-httpclient | **17,972**<br><sub>(205 MiB / 43.1% CPU · 96.0 µs CPU/req)</sub> | **1.00×** | **0.71×** |
| twp-reverse-http3-cleartext | dotnet-httpclient | **14,594**<br><sub>(141 MiB / 45.2% CPU · 124.0 µs CPU/req)</sub> | **0.81×** | **0.57×** |

## Windows — Titanium vs nginx vs HAProxy vs Envoy vs YARP

Client / origin: HTTP version and whether TLS is used (`plain` = cleartext, `TLS` = encrypted, `QUIC` = HTTP/3).

### Reverse

Median of **3 repeats** on `windows-latest` (4 vCPU / 16 GiB). Bare reverse 5×5 @ `e0c98136` — `compare-product` [41268390260](https://github.com/justcoding121/titanium-web-proxy/actions/runs/41268390260). Warmup 2s / measure 8s; concurrency 8, 16, 32, 64. Prefer TWP÷peer ratios over absolute RPS. **RPS cells** include median RSS / CPU at the peak-RPS step as `<br><sub>(MiB / CPU%)</sub>`. nginx terminate peers use `keepalive 256` + streaming buffers. **HAProxy / Envoy are Linux-only peers** (no official Windows port). Laptop High-perf / cool-paired numbers stay on the [local lab](Performance-Local-Lab). Product 5×5 is **~56-byte JSON keep-alive GET**; H2/H3 same-protocol cells are mostly header work with a tiny body (Titanium best case) — see [Why this comparison is fair](#user-content-why-this-comparison-is-fair).

**Load generators:** Reverse inbound H3 arms use **`dotnet-httpclient`** (`http_version=3.0`, `RequestVersionExact`). nginx/Windows is same-OS only (no QUIC). HAProxy/Envoy are Linux-only terminate peers.

| Client | Origin | TWP | nginx | YARP |
|---|---|---:|---:|---:|
| HTTP/1 · plain | HTTP/1 · plain | 🥇 **25,730**<br><sub>(78 MiB / 48.4% CPU · 75.3 µs CPU/req)</sub> | **13,708**<br><sub>(127 MiB / 24.8% CPU · 72.3 µs CPU/req)</sub> | **21,449**<br><sub>(88 MiB / 49.1% CPU · 91.6 µs CPU/req)</sub> |
| HTTP/1 · plain | HTTP/1 · TLS | 🥇 **20,860**<br><sub>(91 MiB / 51.3% CPU · 98.4 µs CPU/req)</sub> | **8,315**<br><sub>(138 MiB / 24.7% CPU · 118.8 µs CPU/req)</sub> | **18,923**<br><sub>(101 MiB / 50.4% CPU · 106.6 µs CPU/req)</sub> |
| HTTP/1 · plain | HTTP/2 · plain | 🥇 **36,159**<br><sub>(100 MiB / 45.2% CPU · 50.0 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **32,295**<br><sub>(94 MiB / 49.6% CPU · 61.4 µs CPU/req)</sub> |
| HTTP/1 · plain | HTTP/2 · TLS | 🥇 **32,319**<br><sub>(118 MiB / 46% CPU · 56.9 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **30,067**<br><sub>(104 MiB / 47.4% CPU · 63.1 µs CPU/req)</sub> |
| HTTP/1 · plain | HTTP/3 · QUIC | 🥇 **22,051**<br><sub>(114 MiB / 51.8% CPU · 94.0 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | **21,643**<br><sub>(119 MiB / 50.5% CPU · 93.3 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/1 · plain | 🥇 **34,679**<br><sub>(89 MiB / 50.7% CPU · 58.4 µs CPU/req)</sub> | **16,062**<br><sub>(143 MiB / 24.9% CPU · 61.9 µs CPU/req)</sub> | **29,912**<br><sub>(104 MiB / 47.6% CPU · 63.6 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/1 · TLS | 🥇 **23,880**<br><sub>(91 MiB / 46.4% CPU · 77.7 µs CPU/req)</sub> | **9,345**<br><sub>(146 MiB / 24.6% CPU · 105.5 µs CPU/req)</sub> | **21,012**<br><sub>(105 MiB / 50.3% CPU · 95.8 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/2 · plain | 🥇 **27,287**<br><sub>(115 MiB / 46.5% CPU · 68.2 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **25,843**<br><sub>(107 MiB / 46.4% CPU · 71.9 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/2 · TLS | 🥇 **42,284**<br><sub>(123 MiB / 46.5% CPU · 44.0 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **39,922**<br><sub>(113 MiB / 46.8% CPU · 46.9 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/3 · QUIC | 🥇 **15,722**<br><sub>(117 MiB / 51.1% CPU · 130.0 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | **15,252**<br><sub>(130 MiB / 51.2% CPU · 134.3 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | 🥇 **32,723**<br><sub>(108 MiB / 48.7% CPU · 59.5 µs CPU/req)</sub> | **8,802**<br><sub>(129 MiB / 24.8% CPU · 112.5 µs CPU/req)</sub> | **24,918**<br><sub>(94 MiB / 48.4% CPU · 77.6 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/1 · TLS | 🥇 **28,367**<br><sub>(123 MiB / 53.4% CPU · 75.3 µs CPU/req)</sub> | **6,323**<br><sub>(139 MiB / 24.7% CPU · 156.2 µs CPU/req)</sub> | **25,883**<br><sub>(101 MiB / 53.2% CPU · 82.2 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/2 · plain | 🥇 **100,712**<br><sub>(102 MiB / 35.9% CPU · 14.3 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **73,932**<br><sub>(106 MiB / 53.7% CPU · 29.1 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/2 · TLS | 🥇 **59,833**<br><sub>(118 MiB / 37.2% CPU · 24.9 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **45,181**<br><sub>(105 MiB / 47.3% CPU · 41.9 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/3 · QUIC | 🥇 **25,448**<br><sub>(145 MiB / 51.6% CPU · 81.2 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | **23,933**<br><sub>(121 MiB / 51.5% CPU · 86.0 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **30,456**<br><sub>(129 MiB / 51% CPU · 67.0 µs CPU/req)</sub> | **7,838**<br><sub>(143 MiB / 24.6% CPU · 125.8 µs CPU/req)</sub> | **26,245**<br><sub>(103 MiB / 49.1% CPU · 74.8 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/1 · TLS | 🥇 **26,003**<br><sub>(141 MiB / 53.9% CPU · 82.9 µs CPU/req)</sub> | **6,038**<br><sub>(146 MiB / 24.2% CPU · 160.5 µs CPU/req)</sub> | **24,445**<br><sub>(106 MiB / 54% CPU · 88.3 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | 🥇 **135,795**<br><sub>(111 MiB / 35.1% CPU · 10.3 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **104,584**<br><sub>(113 MiB / 51.8% CPU · 19.8 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **59,362**<br><sub>(130 MiB / 26.8% CPU · 18.1 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **53,430**<br><sub>(106 MiB / 46.8% CPU · 35.0 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/3 · QUIC | 🥇 **22,474**<br><sub>(158 MiB / 52.7% CPU · 93.9 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | **21,255**<br><sub>(129 MiB / 53.3% CPU · 100.4 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **10,691**<br><sub>(114 MiB / 46.5% CPU · 173.8 µs CPU/req)</sub> | *Not possible (no QUIC)* | **9,663**<br><sub>(155 MiB / 49.7% CPU · 205.7 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **12,698**<br><sub>(125 MiB / 48.4% CPU · 152.4 µs CPU/req)</sub> | *Not possible (no QUIC)* | **10,844**<br><sub>(163 MiB / 48.7% CPU · 179.8 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/2 · plain | 🥇 **16,745**<br><sub>(125 MiB / 46.1% CPU · 110.2 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **14,591**<br><sub>(168 MiB / 50.8% CPU · 139.2 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | 🥇 **11,625**<br><sub>(114 MiB / 47.7% CPU · 164.2 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **9,550**<br><sub>(161 MiB / 49.4% CPU · 206.8 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/3 · QUIC | 🥇 **8,981**<br><sub>(114 MiB / 49.4% CPU · 220.2 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | **7,347**<br><sub>(162 MiB / 54.2% CPU · 294.8 µs CPU/req)</sub> |

## Linux — Titanium vs nginx vs HAProxy vs Envoy vs YARP

### Reverse

Median of **3 repeats** on `ubuntu-latest` (4 vCPU / 16 GiB). Bare reverse 5×5 @ `e0c98136` — `compare-product` [41268390260](https://github.com/justcoding121/titanium-web-proxy/actions/runs/41268390260). Warmup 2s / measure 8s; concurrency 8, 16, 32, 64. **Linux nginx is the authoritative nginx baseline.** HAProxy (3.2 `USE_QUIC`) and Envoy (GitHub release, HTTP/3 compiled in) run on the same loopback shape as nginx/YARP. nginx terminate peers use `keepalive 256` + streaming buffers. The RPS workflow installs nginx.org mainline (`http_v3_module`), a QUIC-enabled HAProxy, Envoy, and `libmsquic`. Prefer ratios over absolute RPS. Product 5×5 is **~56-byte JSON keep-alive GET**; H2/H3 same-protocol cells are mostly header work with a tiny body (Titanium best case) — see [Why this comparison is fair](#user-content-why-this-comparison-is-fair).

| Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---:|---:|---:|---:|---:|
| HTTP/1 · plain | HTTP/1 · plain | **33,547**<br><sub>(85 MiB / 48% CPU · 57.3 µs CPU/req)</sub> | 🥇 **40,590**<br><sub>(79 MiB / 42.6% CPU · 42.0 µs CPU/req)</sub> | **36,459**<br><sub>(65 MiB / 45.6% CPU · 50.0 µs CPU/req)</sub> | **20,446**<br><sub>(110 MiB / 63.7% CPU · 124.7 µs CPU/req)</sub> | **30,360**<br><sub>(116 MiB / 50.4% CPU · 66.4 µs CPU/req)</sub> |
| HTTP/1 · plain | HTTP/1 · TLS | **48,222**<br><sub>(115 MiB / 44% CPU · 36.5 µs CPU/req)</sub> | 🥇 **64,754**<br><sub>(102 MiB / 38.1% CPU · 23.6 µs CPU/req)</sub> | **58,258**<br><sub>(70 MiB / 43.2% CPU · 29.7 µs CPU/req)</sub> | **37,756**<br><sub>(111 MiB / 57% CPU · 60.4 µs CPU/req)</sub> | **46,122**<br><sub>(136 MiB / 48.1% CPU · 41.7 µs CPU/req)</sub> |
| HTTP/1 · plain | HTTP/2 · plain | 🥇 **41,812**<br><sub>(129 MiB / 48.3% CPU · 46.2 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **27,589**<br><sub>(66 MiB / 44.3% CPU · 64.3 µs CPU/req)</sub> | **21,840**<br><sub>(110 MiB / 64.4% CPU · 118.0 µs CPU/req)</sub> | **38,951**<br><sub>(123 MiB / 48.5% CPU · 49.8 µs CPU/req)</sub> |
| HTTP/1 · plain | HTTP/2 · TLS | 🥇 **34,582**<br><sub>(153 MiB / 47.5% CPU · 55.0 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **28,142**<br><sub>(67 MiB / 47.4% CPU · 67.3 µs CPU/req)</sub> | **20,895**<br><sub>(111 MiB / 62.8% CPU · 120.2 µs CPU/req)</sub> | **33,190**<br><sub>(132 MiB / 46.9% CPU · 56.5 µs CPU/req)</sub> |
| HTTP/1 · plain | HTTP/3 · QUIC | 🥇 **47,733**<br><sub>(164 MiB / 55.9% CPU · 46.8 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **28,769**<br><sub>(114 MiB / 49.5% CPU · 68.8 µs CPU/req)</sub> | **45,772**<br><sub>(183 MiB / 46.9% CPU · 41.0 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/1 · plain | **25,693**<br><sub>(114 MiB / 48.7% CPU · 75.8 µs CPU/req)</sub> | 🥇 **29,888**<br><sub>(109 MiB / 43.2% CPU · 57.8 µs CPU/req)</sub> | **28,456**<br><sub>(85 MiB / 46% CPU · 64.7 µs CPU/req)</sub> | **17,628**<br><sub>(121 MiB / 62.1% CPU · 141.0 µs CPU/req)</sub> | **22,595**<br><sub>(137 MiB / 51.2% CPU · 90.6 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/1 · TLS | **20,935**<br><sub>(115 MiB / 47.1% CPU · 89.9 µs CPU/req)</sub> | 🥇 **24,621**<br><sub>(113 MiB / 42.9% CPU · 69.7 µs CPU/req)</sub> | **23,658**<br><sub>(86 MiB / 45.6% CPU · 77.1 µs CPU/req)</sub> | **15,755**<br><sub>(122 MiB / 58.4% CPU · 148.3 µs CPU/req)</sub> | **18,790**<br><sub>(149 MiB / 50.3% CPU · 107.1 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/2 · plain | 🥇 **59,010**<br><sub>(155 MiB / 48% CPU · 32.5 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **49,440**<br><sub>(84 MiB / 40.1% CPU · 32.4 µs CPU/req)</sub> | **44,520**<br><sub>(122 MiB / 55.8% CPU · 50.1 µs CPU/req)</sub> | **56,988**<br><sub>(148 MiB / 50.1% CPU · 35.2 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/2 · TLS | 🥇 **62,572**<br><sub>(165 MiB / 46.7% CPU · 29.9 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **54,720**<br><sub>(85 MiB / 46.9% CPU · 34.3 µs CPU/req)</sub> | **51,109**<br><sub>(122 MiB / 53.2% CPU · 41.6 µs CPU/req)</sub> | **59,156**<br><sub>(149 MiB / 47.5% CPU · 32.1 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/3 · QUIC | 🥇 **30,736**<br><sub>(171 MiB / 50.9% CPU · 66.3 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **20,212**<br><sub>(125 MiB / 54.1% CPU · 107.0 µs CPU/req)</sub> | **29,863**<br><sub>(177 MiB / 49.7% CPU · 66.5 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | **37,520**<br><sub>(142 MiB / 52% CPU · 55.5 µs CPU/req)</sub> | **30,070**<br><sub>(81 MiB / 46.4% CPU · 61.8 µs CPU/req)</sub> | 🥇 **37,734**<br><sub>(65 MiB / 46.1% CPU · 48.8 µs CPU/req)</sub> | **20,860**<br><sub>(111 MiB / 65.8% CPU · 126.1 µs CPU/req)</sub> | **35,031**<br><sub>(127 MiB / 50.6% CPU · 57.8 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/1 · TLS | **33,749**<br><sub>(160 MiB / 45.7% CPU · 54.2 µs CPU/req)</sub> | **36,408**<br><sub>(106 MiB / 42.8% CPU · 47.0 µs CPU/req)</sub> | 🥇 **41,594**<br><sub>(70 MiB / 43.6% CPU · 41.9 µs CPU/req)</sub> | **24,279**<br><sub>(112 MiB / 60.2% CPU · 99.2 µs CPU/req)</sub> | **33,244**<br><sub>(139 MiB / 47.3% CPU · 56.9 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/2 · plain | 🥇 **107,622**<br><sub>(140 MiB / 40.1% CPU · 14.9 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **58,744**<br><sub>(65 MiB / 46.5% CPU · 31.7 µs CPU/req)</sub> | **42,035**<br><sub>(110 MiB / 58.8% CPU · 55.9 µs CPU/req)</sub> | **71,794**<br><sub>(137 MiB / 47.9% CPU · 26.7 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/2 · TLS | 🥇 **123,816**<br><sub>(164 MiB / 40.6% CPU · 13.1 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **69,572**<br><sub>(70 MiB / 46.1% CPU · 26.5 µs CPU/req)</sub> | **67,504**<br><sub>(111 MiB / 53.7% CPU · 31.8 µs CPU/req)</sub> | **91,041**<br><sub>(139 MiB / 45.4% CPU · 19.9 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/3 · QUIC | 🥇 **38,365**<br><sub>(188 MiB / 50.2% CPU · 52.3 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **15,831**<br><sub>(113 MiB / 55.3% CPU · 139.7 µs CPU/req)</sub> | **36,500**<br><sub>(164 MiB / 46.2% CPU · 50.6 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **47,096**<br><sub>(150 MiB / 49.6% CPU · 42.1 µs CPU/req)</sub> | **42,069**<br><sub>(110 MiB / 43.4% CPU · 41.3 µs CPU/req)</sub> | **47,048**<br><sub>(83 MiB / 45.7% CPU · 38.8 µs CPU/req)</sub> | **31,340**<br><sub>(122 MiB / 61.4% CPU · 78.4 µs CPU/req)</sub> | **45,013**<br><sub>(132 MiB / 49.1% CPU · 43.6 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/1 · TLS | **24,477**<br><sub>(164 MiB / 43% CPU · 70.3 µs CPU/req)</sub> | 🥇 **34,152**<br><sub>(119 MiB / 40.7% CPU · 47.7 µs CPU/req)</sub> | **32,300**<br><sub>(85 MiB / 41.4% CPU · 51.3 µs CPU/req)</sub> | **22,394**<br><sub>(122 MiB / 59.4% CPU · 106.1 µs CPU/req)</sub> | **23,156**<br><sub>(146 MiB / 45.2% CPU · 78.1 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | 🥇 **125,939**<br><sub>(154 MiB / 41.2% CPU · 13.1 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **63,454**<br><sub>(83 MiB / 46.7% CPU · 29.4 µs CPU/req)</sub> | **64,304**<br><sub>(121 MiB / 51.8% CPU · 32.2 µs CPU/req)</sub> | **88,094**<br><sub>(153 MiB / 47.2% CPU · 21.4 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **109,647**<br><sub>(171 MiB / 40.4% CPU · 14.8 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **65,458**<br><sub>(83 MiB / 46.8% CPU · 28.6 µs CPU/req)</sub> | **60,324**<br><sub>(121 MiB / 52.2% CPU · 34.6 µs CPU/req)</sub> | **80,369**<br><sub>(144 MiB / 45.4% CPU · 22.6 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/3 · QUIC | 🥇 **24,823**<br><sub>(184 MiB / 49.8% CPU · 80.2 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **10,125**<br><sub>(123 MiB / 56.5% CPU · 223.1 µs CPU/req)</sub> | **22,536**<br><sub>(166 MiB / 46.8% CPU · 83.0 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | **19,295**<br><sub>(154 MiB / 52.7% CPU · 109.3 µs CPU/req)</sub> | **21,507**<br><sub>(112 MiB / 32.8% CPU · 60.9 µs CPU/req)</sub> | 🥇 **24,691**<br><sub>(89 MiB / 35.7% CPU · 57.8 µs CPU/req)</sub> | **8,751**<br><sub>(127 MiB / 61.8% CPU · 282.6 µs CPU/req)</sub> | **16,168**<br><sub>(201 MiB / 50.8% CPU · 125.6 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | **16,061**<br><sub>(169 MiB / 52.9% CPU · 131.8 µs CPU/req)</sub> | **18,111**<br><sub>(119 MiB / 34.8% CPU · 76.9 µs CPU/req)</sub> | 🥇 **20,643**<br><sub>(90 MiB / 37.5% CPU · 72.6 µs CPU/req)</sub> | **8,426**<br><sub>(128 MiB / 59.9% CPU · 284.5 µs CPU/req)</sub> | **12,962**<br><sub>(211 MiB / 51.5% CPU · 158.9 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/2 · plain | **22,995**<br><sub>(159 MiB / 53.7% CPU · 93.4 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | 🥇 **24,962**<br><sub>(89 MiB / 36% CPU · 57.8 µs CPU/req)</sub> | **10,529**<br><sub>(127 MiB / 61.1% CPU · 232.1 µs CPU/req)</sub> | **19,159**<br><sub>(210 MiB / 50.6% CPU · 105.7 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | **28,547**<br><sub>(172 MiB / 53.5% CPU · 74.9 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | 🥇 **33,808**<br><sub>(89 MiB / 35.9% CPU · 42.5 µs CPU/req)</sub> | **15,617**<br><sub>(128 MiB / 56% CPU · 143.5 µs CPU/req)</sub> | **26,973**<br><sub>(213 MiB / 48.8% CPU · 72.4 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/3 · QUIC | 🥇 **17,829**<br><sub>(158 MiB / 48.2% CPU · 108.2 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **6,346**<br><sub>(127 MiB / 56.4% CPU · 355.8 µs CPU/req)</sub> | **14,296**<br><sub>(212 MiB / 48.4% CPU · 135.4 µs CPU/req)</sub> |

### MITM (TWP only)

Same Client×Origin wires with interception on (`compare-product` [41268390260](https://github.com/justcoding121/titanium-web-proxy/actions/runs/41268390260)). **Lite** = no-op handlers (unchanged-lite finish). **Full** = append-only header mutation (harness: one probe header each way; product: generic append-only relay via `MitmCompressedRelayHelper`). nginx/HAProxy/Envoy/YARP cannot MITM. **Lite÷Reverse** / **Full÷Reverse** vs bare reverse (**same job / comparison-group shard**). Completion gate: Lite ≥ **0.25×** and Full ≥ **0.25×** reverse sustain @ c=64 (median of 3 GHA runs); reverse TWP ÷ closest peer ≥ **0.40×**.

**v1 append-only relay (2026-08-27):** Pre-fix H2→H2 Full÷Reverse was **0.13–0.16×** ([32960766249](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32960766249)). Post-fix @ `df172718`: H2 plain→H2 plain Full **0.77–0.79×**, H3→H1 Full **0.91–0.93×**, all MITM arms ≥ **0.70×** on median of [33041445371](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33041445371), [33055267086](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33055267086), [33055272140](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33055272140).

**v2 drop-only + non-unique append (2026-08-27):** `MitmStaticRebuildHelper` rebuilds static HPACK/QPACK after 1–4 unique header drops; trailing non-unique appends stay on compressed relay. @ `41f4adee`: all MITM arms ≥ **0.70×** on GHA median ([33087088466](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087088466), [33087091622](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087091622), [33105885748](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33105885748) Linux; [33087085235](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087085235), [33087088466](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087088466), [33087091622](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087091622) Windows). H2 plain→H2 plain Full **0.77–0.79×** (Win) / **0.78×** (Lin).

| Client | Origin | Lite sustain | Full sustain | Lite÷Reverse | Full÷Reverse |
|---|---|---:|---:|---:|---:|
| HTTP/1 · plain | HTTP/1 · plain | **34,084**<br><sub>(91 MiB / 49.2% CPU · 57.7 µs CPU/req)</sub> | **33,067**<br><sub>(91 MiB / 48.7% CPU · 58.9 µs CPU/req)</sub> | **1.02×** | **0.99×** |
| HTTP/1 · plain | HTTP/1 · TLS | **47,934**<br><sub>(121 MiB / 44.3% CPU · 37.0 µs CPU/req)</sub> | **46,248**<br><sub>(114 MiB / 43.8% CPU · 37.9 µs CPU/req)</sub> | **0.99×** | **0.96×** |
| HTTP/1 · plain | HTTP/2 · plain | **39,814**<br><sub>(126 MiB / 49.9% CPU · 50.1 µs CPU/req)</sub> | **39,600**<br><sub>(128 MiB / 51.1% CPU · 51.6 µs CPU/req)</sub> | **0.95×** | **0.95×** |
| HTTP/1 · plain | HTTP/2 · TLS | **33,881**<br><sub>(157 MiB / 48.7% CPU · 57.5 µs CPU/req)</sub> | **31,708**<br><sub>(152 MiB / 47.4% CPU · 59.8 µs CPU/req)</sub> | **0.98×** | **0.92×** |
| HTTP/1 · plain | HTTP/3 · QUIC | **49,266**<br><sub>(174 MiB / 55.8% CPU · 45.3 µs CPU/req)</sub> | **47,850**<br><sub>(173 MiB / 54.8% CPU · 45.9 µs CPU/req)</sub> | **1.03×** | **1×** |
| HTTP/1 · TLS | HTTP/1 · plain | **25,064**<br><sub>(119 MiB / 50% CPU · 79.9 µs CPU/req)</sub> | **24,549**<br><sub>(122 MiB / 49.4% CPU · 80.5 µs CPU/req)</sub> | **0.98×** | **0.96×** |
| HTTP/1 · TLS | HTTP/1 · TLS | **21,519**<br><sub>(121 MiB / 48.7% CPU · 90.5 µs CPU/req)</sub> | **20,517**<br><sub>(120 MiB / 48.3% CPU · 94.2 µs CPU/req)</sub> | **1.03×** | **0.98×** |
| HTTP/1 · TLS | HTTP/2 · plain | **58,771**<br><sub>(162 MiB / 49.1% CPU · 33.4 µs CPU/req)</sub> | **55,820**<br><sub>(164 MiB / 49.5% CPU · 35.5 µs CPU/req)</sub> | **1×** | **0.95×** |
| HTTP/1 · TLS | HTTP/2 · TLS | **58,070**<br><sub>(178 MiB / 49.7% CPU · 34.3 µs CPU/req)</sub> | **57,914**<br><sub>(168 MiB / 48.7% CPU · 33.6 µs CPU/req)</sub> | **0.93×** | **0.93×** |
| HTTP/1 · TLS | HTTP/3 · QUIC | **30,801**<br><sub>(174 MiB / 51.6% CPU · 67.0 µs CPU/req)</sub> | **30,306**<br><sub>(176 MiB / 52.2% CPU · 68.9 µs CPU/req)</sub> | **1×** | **0.99×** |
| HTTP/2 · plain | HTTP/1 · plain | **37,138**<br><sub>(146 MiB / 53.5% CPU · 57.6 µs CPU/req)</sub> | **35,620**<br><sub>(146 MiB / 53.2% CPU · 59.7 µs CPU/req)</sub> | **0.99×** | **0.95×** |
| HTTP/2 · plain | HTTP/1 · TLS | **32,644**<br><sub>(159 MiB / 47% CPU · 57.6 µs CPU/req)</sub> | **32,785**<br><sub>(166 MiB / 48% CPU · 58.5 µs CPU/req)</sub> | **0.97×** | **0.97×** |
| HTTP/2 · plain | HTTP/2 · plain | **88,188**<br><sub>(151 MiB / 43.5% CPU · 19.7 µs CPU/req)</sub> | **83,880**<br><sub>(147 MiB / 43.9% CPU · 20.9 µs CPU/req)</sub> | **0.82×** | **0.78×** |
| HTTP/2 · plain | HTTP/2 · TLS | **105,400**<br><sub>(173 MiB / 43.7% CPU · 16.6 µs CPU/req)</sub> | **103,618**<br><sub>(167 MiB / 44% CPU · 17.0 µs CPU/req)</sub> | **0.85×** | **0.84×** |
| HTTP/2 · plain | HTTP/3 · QUIC | **38,356**<br><sub>(186 MiB / 50.6% CPU · 52.7 µs CPU/req)</sub> | **37,699**<br><sub>(180 MiB / 50.5% CPU · 53.6 µs CPU/req)</sub> | **1×** | **0.98×** |
| HTTP/2 · TLS | HTTP/1 · plain | **45,885**<br><sub>(168 MiB / 51.7% CPU · 45.1 µs CPU/req)</sub> | **45,838**<br><sub>(163 MiB / 51.2% CPU · 44.7 µs CPU/req)</sub> | **0.97×** | **0.97×** |
| HTTP/2 · TLS | HTTP/1 · TLS | **31,584**<br><sub>(167 MiB / 47.6% CPU · 60.3 µs CPU/req)</sub> | **29,984**<br><sub>(159 MiB / 46.4% CPU · 61.9 µs CPU/req)</sub> | **1.29×** | **1.23×** |
| HTTP/2 · TLS | HTTP/2 · plain | **109,664**<br><sub>(166 MiB / 45.9% CPU · 16.8 µs CPU/req)</sub> | **106,338**<br><sub>(170 MiB / 44.8% CPU · 16.8 µs CPU/req)</sub> | **0.87×** | **0.84×** |
| HTTP/2 · TLS | HTTP/2 · TLS | **96,101**<br><sub>(167 MiB / 44.4% CPU · 18.5 µs CPU/req)</sub> | **95,792**<br><sub>(172 MiB / 43.5% CPU · 18.2 µs CPU/req)</sub> | **0.88×** | **0.87×** |
| HTTP/2 · TLS | HTTP/3 · QUIC | **25,036**<br><sub>(198 MiB / 50.6% CPU · 80.9 µs CPU/req)</sub> | **24,356**<br><sub>(211 MiB / 50.4% CPU · 82.8 µs CPU/req)</sub> | **1.01×** | **0.98×** |
| HTTP/3 · QUIC | HTTP/1 · plain | **19,085**<br><sub>(155 MiB / 53.6% CPU · 112.4 µs CPU/req)</sub> | **17,644**<br><sub>(158 MiB / 53.5% CPU · 121.4 µs CPU/req)</sub> | **0.99×** | **0.91×** |
| HTTP/3 · QUIC | HTTP/1 · TLS | **15,991**<br><sub>(183 MiB / 53.6% CPU · 134.0 µs CPU/req)</sub> | **15,604**<br><sub>(172 MiB / 53.6% CPU · 137.3 µs CPU/req)</sub> | **1×** | **0.97×** |
| HTTP/3 · QUIC | HTTP/2 · plain | **22,595**<br><sub>(158 MiB / 53.4% CPU · 94.6 µs CPU/req)</sub> | **21,031**<br><sub>(160 MiB / 53.8% CPU · 102.4 µs CPU/req)</sub> | **0.98×** | **0.91×** |
| HTTP/3 · QUIC | HTTP/2 · TLS | **28,465**<br><sub>(174 MiB / 53.7% CPU · 75.4 µs CPU/req)</sub> | **29,049**<br><sub>(173 MiB / 53.7% CPU · 73.9 µs CPU/req)</sub> | **1×** | **1.02×** |
| HTTP/3 · QUIC | HTTP/3 · QUIC | **16,288**<br><sub>(160 MiB / 52.4% CPU · 128.6 µs CPU/req)</sub> | **16,541**<br><sub>(159 MiB / 51.6% CPU · 124.9 µs CPU/req)</sub> | **0.91×** | **0.93×** |

## macOS — Titanium vs nginx vs HAProxy vs Envoy vs YARP

### Reverse

Median of **3 repeats** on `macos-15` (Apple Silicon M1, 3-core / 7 GB). Bare reverse 5×5 @ `e0c98136` — `compare-product` [41268390260](https://github.com/justcoding121/titanium-web-proxy/actions/runs/41268390260). Warmup 2s / measure 8s; concurrency 8, 16, 32, 64. Prefer TWP÷peer ratios over absolute RPS. **RPS cells** include median RSS / CPU at the peak-RPS step as `<br><sub>(MiB / CPU%)</sub>`. The RPS workflow installs Homebrew nginx (`http_v3_module`), Homebrew HAProxy with `USE_QUIC` (3.2 source fallback), Envoy (Homebrew bottle or pinned darwin-arm64 1.36.7), Homebrew `libmsquic` (+ `DYLD_*`), and YARP. Use the pinned `macos-15` label, not `macos-latest`. Product 5×5 is **~56-byte JSON keep-alive GET**; H2/H3 same-protocol cells are mostly header work with a tiny body (Titanium best case) — see [Why this comparison is fair](#user-content-why-this-comparison-is-fair).

| Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---:|---:|---:|---:|---:|
| HTTP/1 · plain | HTTP/1 · plain | **28,398**<br><sub>(118 MiB / 32% CPU · 33.8 µs CPU/req)</sub> | 🥇 **45,081**<br><sub>(68 MiB / 24.3% CPU · 16.1 µs CPU/req)</sub> | **36,048**<br><sub>(165 MiB / 33.3% CPU · 27.7 µs CPU/req)</sub> | **27,245**<br><sub>(110 MiB / 45.7% CPU · 50.4 µs CPU/req)</sub> | **30,041**<br><sub>(148 MiB / 38.5% CPU · 38.4 µs CPU/req)</sub> |
| HTTP/1 · plain | HTTP/1 · TLS | **23,759**<br><sub>(130 MiB / 36.1% CPU · 45.6 µs CPU/req)</sub> | **18,562**<br><sub>(79 MiB / 21.9% CPU · 35.4 µs CPU/req)</sub> | 🥇 **27,042**<br><sub>(112 MiB / 33.8% CPU · 37.5 µs CPU/req)</sub> | **14,982**<br><sub>(112 MiB / 43.4% CPU · 86.9 µs CPU/req)</sub> | **24,245**<br><sub>(162 MiB / 38.6% CPU · 47.7 µs CPU/req)</sub> |
| HTTP/1 · plain | HTTP/2 · plain | 🥇 **25,422**<br><sub>(126 MiB / 26.9% CPU · 31.7 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **16,814**<br><sub>(97 MiB / 25% CPU · 44.6 µs CPU/req)</sub> | **7,506**<br><sub>(110 MiB / 19.3% CPU · 77.2 µs CPU/req)</sub> | **24,026**<br><sub>(158 MiB / 24.9% CPU · 31.1 µs CPU/req)</sub> |
| HTTP/1 · plain | HTTP/2 · TLS | 🥇 **20,831**<br><sub>(169 MiB / 25.7% CPU · 37.0 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **20,768**<br><sub>(126 MiB / 38% CPU · 54.9 µs CPU/req)</sub> | **14,270**<br><sub>(110 MiB / 40.6% CPU · 85.4 µs CPU/req)</sub> | **12,532**<br><sub>(165 MiB / 15.5% CPU · 37.0 µs CPU/req)</sub> |
| HTTP/1 · plain | HTTP/3 · QUIC | 🥇 **10,650**<br><sub>(137 MiB / 30.8% CPU · 86.7 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **9,056**<br><sub>(114 MiB / 31.4% CPU · 104.0 µs CPU/req)</sub> | **5,452**<br><sub>(165 MiB / 11.5% CPU · 63.2 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/1 · plain | **14,943**<br><sub>(130 MiB / 34.1% CPU · 68.5 µs CPU/req)</sub> | **21,336**<br><sub>(84 MiB / 23.7% CPU · 33.4 µs CPU/req)</sub> | 🥇 **24,394**<br><sub>(118 MiB / 33.5% CPU · 41.2 µs CPU/req)</sub> | **14,541**<br><sub>(120 MiB / 46.8% CPU · 96.6 µs CPU/req)</sub> | **17,088**<br><sub>(158 MiB / 34.9% CPU · 61.3 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/1 · TLS | **7,696**<br><sub>(159 MiB / 19.1% CPU · 74.5 µs CPU/req)</sub> | **12,474**<br><sub>(89 MiB / 19.1% CPU · 46.0 µs CPU/req)</sub> | **15,363**<br><sub>(116 MiB / 30.8% CPU · 60.1 µs CPU/req)</sub> | **14,179**<br><sub>(121 MiB / 34.5% CPU · 72.9 µs CPU/req)</sub> | 🥇 **15,771**<br><sub>(180 MiB / 29.2% CPU · 55.6 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/2 · plain | **14,155**<br><sub>(148 MiB / 24% CPU · 50.8 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **16,545**<br><sub>(126 MiB / 29.4% CPU · 53.3 µs CPU/req)</sub> | **22,599**<br><sub>(119 MiB / 47.7% CPU · 63.3 µs CPU/req)</sub> | 🥇 **24,594**<br><sub>(161 MiB / 30.1% CPU · 36.7 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/2 · TLS | **20,196**<br><sub>(192 MiB / 29.6% CPU · 43.9 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **20,398**<br><sub>(130 MiB / 38.9% CPU · 57.2 µs CPU/req)</sub> | **16,263**<br><sub>(119 MiB / 46.6% CPU · 85.9 µs CPU/req)</sub> | 🥇 **36,152**<br><sub>(172 MiB / 30.7% CPU · 25.5 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/3 · QUIC | **9,167**<br><sub>(173 MiB / 27.2% CPU · 89.1 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | 🥇 **12,795**<br><sub>(124 MiB / 37.7% CPU · 88.4 µs CPU/req)</sub> | **7,282**<br><sub>(175 MiB / 14.6% CPU · 60.2 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | **46,151**<br><sub>(165 MiB / 42% CPU · 27.3 µs CPU/req)</sub> | **38,857**<br><sub>(69 MiB / 24.9% CPU · 19.2 µs CPU/req)</sub> | 🥇 **51,181**<br><sub>(162 MiB / 37.9% CPU · 22.2 µs CPU/req)</sub> | **29,788**<br><sub>(111 MiB / 32% CPU · 32.3 µs CPU/req)</sub> | **43,170**<br><sub>(150 MiB / 42% CPU · 29.2 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/1 · TLS | **22,157**<br><sub>(191 MiB / 42.4% CPU · 57.3 µs CPU/req)</sub> | **18,972**<br><sub>(80 MiB / 23.3% CPU · 36.9 µs CPU/req)</sub> | 🥇 **26,445**<br><sub>(125 MiB / 39.7% CPU · 45.0 µs CPU/req)</sub> | **18,928**<br><sub>(112 MiB / 42.8% CPU · 67.8 µs CPU/req)</sub> | **23,326**<br><sub>(161 MiB / 42% CPU · 54.0 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/2 · plain | 🥇 **63,097**<br><sub>(188 MiB / 30.4% CPU · 14.4 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **36,291**<br><sub>(257 MiB / 46.6% CPU · 38.5 µs CPU/req)</sub> | **39,968**<br><sub>(109 MiB / 48% CPU · 36.0 µs CPU/req)</sub> | **58,758**<br><sub>(164 MiB / 41.8% CPU · 21.3 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/2 · TLS | **52,889**<br><sub>(201 MiB / 33% CPU · 18.7 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **37,697**<br><sub>(252 MiB / 46.4% CPU · 36.9 µs CPU/req)</sub> | **52,220**<br><sub>(110 MiB / 52% CPU · 29.9 µs CPU/req)</sub> | 🥇 **58,984**<br><sub>(164 MiB / 41.7% CPU · 21.2 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/3 · QUIC | **28,706**<br><sub>(200 MiB / 47.4% CPU · 49.5 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **21,263**<br><sub>(113 MiB / 32.8% CPU · 46.3 µs CPU/req)</sub> | 🥇 **35,245**<br><sub>(194 MiB / 43.3% CPU · 36.8 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | **36,431**<br><sub>(179 MiB / 39% CPU · 32.1 µs CPU/req)</sub> | **31,376**<br><sub>(83 MiB / 25.2% CPU · 24.1 µs CPU/req)</sub> | 🥇 **45,384**<br><sub>(158 MiB / 37.7% CPU · 24.9 µs CPU/req)</sub> | **33,956**<br><sub>(120 MiB / 48.3% CPU · 42.7 µs CPU/req)</sub> | **42,830**<br><sub>(156 MiB / 41.8% CPU · 29.3 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/1 · TLS | 🥇 **25,969**<br><sub>(188 MiB / 38.4% CPU · 44.4 µs CPU/req)</sub> | **18,721**<br><sub>(88 MiB / 24.7% CPU · 39.6 µs CPU/req)</sub> | **25,244**<br><sub>(131 MiB / 37.6% CPU · 44.7 µs CPU/req)</sub> | **19,057**<br><sub>(121 MiB / 44.4% CPU · 69.8 µs CPU/req)</sub> | **24,010**<br><sub>(165 MiB / 35.8% CPU · 44.8 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | 🥇 **79,967**<br><sub>(182 MiB / 32.7% CPU · 12.3 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **39,810**<br><sub>(137 MiB / 39.5% CPU · 29.8 µs CPU/req)</sub> | **36,640**<br><sub>(118 MiB / 48.4% CPU · 39.7 µs CPU/req)</sub> | **58,526**<br><sub>(167 MiB / 38.7% CPU · 19.8 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **65,347**<br><sub>(206 MiB / 35.2% CPU · 16.1 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **34,369**<br><sub>(159 MiB / 42% CPU · 36.7 µs CPU/req)</sub> | **37,989**<br><sub>(118 MiB / 46% CPU · 36.4 µs CPU/req)</sub> | **48,741**<br><sub>(167 MiB / 42.2% CPU · 25.9 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/3 · QUIC | **12,275**<br><sub>(207 MiB / 43.4% CPU · 106.1 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **13,076**<br><sub>(121 MiB / 50% CPU · 114.6 µs CPU/req)</sub> | 🥇 **23,499**<br><sub>(168 MiB / 42.2% CPU · 53.8 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | **15,528**<br><sub>(140 MiB / 43.9% CPU · 84.8 µs CPU/req)</sub> | **0**<br><sub>(peak 16,155 · 87 MiB / 20.7% CPU)</sub> | 🥇 **26,454**<br><sub>(105 MiB / 30.2% CPU · 34.2 µs CPU/req)</sub> | **11,679**<br><sub>(124 MiB / 55.2% CPU · 141.7 µs CPU/req)</sub> | **15,327**<br><sub>(207 MiB / 40.8% CPU · 79.9 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | **8,782**<br><sub>(154 MiB / 30.6% CPU · 104.7 µs CPU/req)</sub> | **0**<br><sub>(peak 13,765 · 91 MiB / 20.6% CPU)</sub> | 🥇 **11,731**<br><sub>(108 MiB / 20.4% CPU · 52.2 µs CPU/req)</sub> | **8,801**<br><sub>(125 MiB / 42.8% CPU · 145.8 µs CPU/req)</sub> | **11,455**<br><sub>(230 MiB / 31.1% CPU · 81.4 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/2 · plain | **22,061**<br><sub>(156 MiB / 47.6% CPU · 64.8 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | 🥇 **31,654**<br><sub>(105 MiB / 29.7% CPU · 28.2 µs CPU/req)</sub> | **22,908**<br><sub>(122 MiB / 57.7% CPU · 75.5 µs CPU/req)</sub> | **28,180**<br><sub>(210 MiB / 43.1% CPU · 45.8 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | **19,000**<br><sub>(149 MiB / 49% CPU · 77.4 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | 🥇 **22,794**<br><sub>(107 MiB / 32.1% CPU · 42.2 µs CPU/req)</sub> | **15,609**<br><sub>(122 MiB / 56.1% CPU · 107.8 µs CPU/req)</sub> | **20,960**<br><sub>(217 MiB / 44.8% CPU · 64.1 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/3 · QUIC | **13,056**<br><sub>(144 MiB / 43.5% CPU · 100.0 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **9,328**<br><sub>(121 MiB / 53.4% CPU · 171.8 µs CPU/req)</sub> | 🥇 **18,036**<br><sub>(216 MiB / 41.5% CPU · 69.0 µs CPU/req)</sub> |

## Editions (CLI / Plus / Intercept)

**Note:** `twp-reverse-http1` and other library rows use Core with **probe-tuned** settings (no logging, no Via header, probe-warmed certs). Edition rows use `titanium run -c twp.yaml` **product defaults** — prefer the ÷baseline ratio column over absolute RPS. Inspector GUI is not spawnable in the harness; session-path overhead is `twp-cli-intercept-http1` (route `RequestHeaderSet` transform). Pre-origin Plus middleware (CIDR/WAF deny-list/JWT/rate-limit/cache) runs on H1 terminate-lite without `SessionEventArgs`; a cache hit skips the origin. WAF screening (`waf.mode` `detect` or `block`, the default once `waf.enabled` is set) reads headers, cookies, and small bodies, so it uses the session path and lands near the intercept row. The `twp-cli-plus-waf-http1` row below was measured with the deny-list only; re-measure it with screening on before quoting it. JWT caches successful bearer validations. CORS only adds response headers on the way out, so it stays on terminate-lite. Circuit breaker and idempotent retry have to see the request or the status code, so they stay on the session path and should land near the intercept row. Maintainer gate thresholds live under [Maintainer notes](#user-content-maintainer-notes).

Median of **3** repeats on `ubuntu-latest`. Linux rows in this paste were re-measured @ `e0c98136` (Actions [41268478603](https://github.com/justcoding121/titanium-web-proxy/actions/runs/41268478603)). Every edition row in the table was re-measured in that run. Warmup 2s / measure 8s; concurrency 8–64; sustain = median peak RPS among SLO-pass steps @ **c=64**. **RPS cells** show sustain; `<sub>` holds peak (when higher) plus `(MiB / CPU%)`. The Gate column is that script's floor.

| Arm | Linux | Lin÷ | Gate |
|---|---:|---:|---|
| `twp-cli-reverse-http1` vs library | **51,498**<br><sub>(173 MiB / 47.8% CPU · 37.2 µs CPU/req)</sub> | **1.07×** | ≥ **0.50×** |
| `twp-cli-reverse-http1-tls` vs library TLS | **26,515**<br><sub>(209 MiB / 48.9% CPU · 73.8 µs CPU/req)</sub> | **1.05×** | ≥ **0.50×** |
| `twp-cli-reverse-http1-route` vs CLI | **37,243**<br><sub>(176 MiB / 48.9% CPU · 52.5 µs CPU/req)</sub> | **1.01×** | ≥ **0.50×** |
| `twp-cli-plus-base-http1` vs CLI | **51,014**<br><sub>(175 MiB / 47.6% CPU · 37.3 µs CPU/req)</sub> | **1.00×** | ≥ **0.50×** |
| `twp-cli-plus-cache-http1` (cold) vs CLI | **31,861**<br><sub>(178 MiB / 52.8% CPU · 66.3 µs CPU/req)</sub> | **0.87×** | ≥ **0.50×** |
| `twp-cli-intercept-http1` vs CLI | **25,892**<br><sub>(189 MiB / 52.1% CPU · 80.5 µs CPU/req)</sub> | **0.70×** | ≥ **0.50×** |
| `twp-cli-plus-waf-http1` vs CLI | **26,235**<br><sub>(247 MiB / 53.5% CPU · 81.6 µs CPU/req)</sub> | **0.72×** | ≥ **0.50×** |
| `twp-cli-plus-cidr-http1` vs CLI | **35,261**<br><sub>(178 MiB / 49.1% CPU · 55.7 µs CPU/req)</sub> | **0.98×** | ≥ **0.50×** |
| `twp-cli-plus-jwt-http1` vs CLI | **32,617**<br><sub>(199 MiB / 48.6% CPU · 59.6 µs CPU/req)</sub> | **0.90×** | ≥ **0.50×** |
| `twp-cli-plus-ratelimit-http1` vs CLI | **57,197**<br><sub>(245 MiB / 46.2% CPU · 32.3 µs CPU/req)</sub> | **0.96×** | ≥ **0.50×** |
| `twp-cli-plus-fleet-ratelimit-http1` vs CLI | **50,044**<br><sub>(183 MiB / 49.5% CPU · 39.6 µs CPU/req)</sub> | **0.94×** | ≥ **0.50×** |
| `twp-cli-plus-manager-agent-http1` vs CLI | **36,693**<br><sub>(182 MiB / 48.3% CPU · 52.6 µs CPU/req)</sub> | **1.02×** | ≥ **0.50×** |
| `twp-cli-plus-resilience-http1` vs CLI | **62,832**<br><sub>(185 MiB / 46.9% CPU · 29.9 µs CPU/req)</sub> | **1.01×** | ≥ **0.50×** |
| `twp-cli-plus-discovery-file-http1` vs CLI | **36,771**<br><sub>(182 MiB / 49.0% CPU · 53.3 µs CPU/req)</sub> | **0.99×** | ≥ **0.50×** |
| `twp-cli-plus-metrics-scrape-http1` vs CLI | **40,813**<br><sub>(180 MiB / 48.2% CPU · 47.2 µs CPU/req)</sub> | **1.02×** | ≥ **0.50×** |
| `twp-cli-plus-cache-hit-http1` vs cache cold | **57,931**<br><sub>(178 MiB / 48.5% CPU · 33.5 µs CPU/req)</sub> | **1.82×** | ≥ **0.50×** |
| `twp-cli-plus-cors-http1` vs CLI | **48,803**<br><sub>(179 MiB / 48.1% CPU · 39.4 µs CPU/req)</sub> | **0.94×** | ≥ **0.50×** |
| `twp-cli-plus-circuit-http1` vs CLI | **28,010**<br><sub>(186 MiB / 51.6% CPU · 73.7 µs CPU/req)</sub> | **0.76×** | ≥ **0.50×** |
| `twp-cli-plus-retry-http1` vs CLI | **28,108**<br><sub>(184 MiB / 51.0% CPU · 72.6 µs CPU/req)</sub> | **0.79×** | ≥ **0.50×** |
| `twp-cli-static-http1` vs CLI | **55,859**<br><sub>(172 MiB / 50.4% CPU · 36.1 µs CPU/req)</sub> | **1.55×** | ≥ **0.50×** |
| `twp-cli-logging-http1` vs CLI | **36,533**<br><sub>(173 MiB / 48.4% CPU · 53.0 µs CPU/req)</sub> | **0.99×** | ≥ **0.50×** |
| `twp-cli-lb-leasttime-http1` vs route | **33,426**<br><sub>(213 MiB / 69.9% CPU · 83.7 µs CPU/req)</sub> | **0.92×** | ≥ **0.50×** |
| `twp-cli-dialect-twp-http1` vs CLI | **37,229**<br><sub>(165 MiB / 49.0% CPU · 52.6 µs CPU/req)</sub> | **1.03×** | ≥ **0.50×** |
| `twp-cli-script-lua-noop-http1` vs route | **20,994**<br><sub>(196 MiB / 60.0% CPU · 114.3 µs CPU/req)</sub> | **0.60×** | ≥ **0.50×** |
| `twp-cli-script-lua-header-http1` vs route | **39,221**<br><sub>(197 MiB / 53.4% CPU · 54.5 µs CPU/req)</sub> | **0.59×** | ≥ **0.50×** |
| `twp-cli-script-wasm-noop-http1` vs route | **23,778**<br><sub>(198 MiB / 55.1% CPU · 92.7 µs CPU/req)</sub> | **0.66×** | ≥ **0.50×** |
| `twp-cli-script-lua-slow-upstream-http1` vs slow upstream | **3,032**<br><sub>(224 MiB / 27.8% CPU · 366.5 µs CPU/req)</sub> | **0.99×** | ≥ **0.50×** |
| `twp-cli-script-1in50-http1` vs route | **38,148**<br><sub>(192 MiB / 50.9% CPU · 53.4 µs CPU/req)</sub> | **0.75×** | ≥ **0.50×** |

`validate-edition-gates.ps1` floors are **0.50×**. Each ÷ column uses the two arms from the same job. Circuit breaker and idempotent retry stay on the session path, so a ratio near intercept is expected. Laptop smoke ratios stay on [Performance Local Lab — Editions](Performance-Local-Lab#user-content-editions-cli--plus-stress).

## Heavier reverse workloads

Same runners and harness as the tiny-GET tables, but with larger bodies, POST, lossy links, TLS cost, and architecture-sensitive paths (slow consumer / early response / duplex). **PUT with the same body is the same proxy work as POST; DELETE with no body matches GET** — only POST is published. Bodies/POST/lossy stay **half-duplex**. Laptop numbers are on [Performance Local Lab](Performance-Local-Lab#user-content-architecture-sensitive). How maintainers refresh these tables (modes, shards, paste scripts) is under [Maintainer notes](#user-content-maintainer-notes). **Larger-body check:** 64 / 256 KiB H2 TLS→H2 TLS is where body copy dominates headers — ratios vs YARP are in the tables below (@ `e0c98136`), unlike the tiny-GET H2↔H2 medals above. Those cells also show goodput (MiB/s) and µs CPU/req, and the medal on those rows follows goodput.

Lossy link = **userspace** delay/drop shim (not kernel `netem`): TCP gets per-buffer delay + occasional whole-connection stalls (honest head-of-line for multiplexed HTTP/2); UDP is **loss% only** (no per-datagram delay) + drops (QUIC / MsQuic-safe). Lossy tables publish HTTP/1, HTTP/2, and HTTP/3.

### Windows — heavier reverse GET (64 KiB / 256 KiB)

Median of **3** repeats on `windows-latest` @ `e0c98136`. Source: Actions [41268406222](https://github.com/justcoding121/titanium-web-proxy/actions/runs/41268406222) (`compare-bodies`). Warmup 2s / measure 8s. **RPS cells** include `(MiB / CPU%)` footprints.

*Not possible:* **HAProxy** and **Envoy** columns are omitted (no official Windows port).






















| Body | Client | Origin | TWP | nginx | YARP |
|---|---|---|---:|---:|---:|
| 64 KiB | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **9,508**<br><sub>(99 MiB / 44.8% CPU · 594 MiB/s · 188.3 µs CPU/req)</sub> | **629**<br><sub>(144 MiB / 24.6% CPU · 39 MiB/s · 1567.7 µs CPU/req)</sub> | **7,538**<br><sub>(135 MiB / 44.9% CPU · 471 MiB/s · 238.0 µs CPU/req)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **8,836**<br><sub>(185 MiB / 49.8% CPU · 552 MiB/s · 225.3 µs CPU/req)</sub> | **601**<br><sub>(144 MiB / 24.8% CPU · 38 MiB/s · 1651.1 µs CPU/req)</sub> | **8,518**<br><sub>(130 MiB / 53.2% CPU · 532 MiB/s · 249.8 µs CPU/req)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **5,353**<br><sub>(143 MiB / 45.4% CPU · 335 MiB/s · 339.2 µs CPU/req)</sub> | *Not possible (no QUIC)* | **2,950**<br><sub>(183 MiB / 52.2% CPU · 184 MiB/s · 708.0 µs CPU/req)</sub> |
| 256 KiB | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **2,839**<br><sub>(138 MiB / 47.2% CPU · 710 MiB/s · 665.1 µs CPU/req)</sub> | **167**<br><sub>(143 MiB / 24.7% CPU · 42 MiB/s · 5924.5 µs CPU/req)</sub> | **2,642**<br><sub>(138 MiB / 48.8% CPU · 661 MiB/s · 738.9 µs CPU/req)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/1 · plain | **2,418**<br><sub>(189 MiB / 49.2% CPU · 604 MiB/s · 813.7 µs CPU/req)</sub> | **146**<br><sub>(peak 158 · 144 MiB / 24.6% CPU · 36 MiB/s · 6767.3 µs CPU/req)</sub> | 🥇 **2,692**<br><sub>(133 MiB / 43.9% CPU · 673 MiB/s · 652.8 µs CPU/req)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **239**<br><sub>(97 MiB / 43.8% CPU · 60 MiB/s · 7322.2 µs CPU/req)</sub> | *Not possible (no QUIC)* | **165**<br><sub>(peak 267 · 134 MiB / 50.0% CPU · 41 MiB/s · 12101.7 µs CPU/req)</sub> |
| 64 KiB | HTTP/2 · plain | HTTP/1 · plain | 🥇 **12,802**<br><sub>(198 MiB / 45.3% CPU · 800 MiB/s · 141.5 µs CPU/req)</sub> | **2,035**<br><sub>(129 MiB / 24.7% CPU · 127 MiB/s · 486.1 µs CPU/req)</sub> | **11,501**<br><sub>(114 MiB / 48.8% CPU · 719 MiB/s · 169.9 µs CPU/req)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/2 · plain | 🥇 **9,863**<br><sub>(171 MiB / 44.4% CPU · 616 MiB/s · 180.0 µs CPU/req)</sub> | *Not possible* | **9,160**<br><sub>(146 MiB / 51.7% CPU · 573 MiB/s · 225.7 µs CPU/req)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **8,854**<br><sub>(173 MiB / 43.0% CPU · 553 MiB/s · 194.4 µs CPU/req)</sub> | *Not possible* | **7,690**<br><sub>(141 MiB / 50.6% CPU · 481 MiB/s · 263.1 µs CPU/req)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/2 · TLS | 🥇 **3,150**<br><sub>(134 MiB / 40.0% CPU · 197 MiB/s · 507.6 µs CPU/req)</sub> | *Not possible* | **1,577**<br><sub>(186 MiB / 50.3% CPU · 99 MiB/s · 1275.5 µs CPU/req)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **3,369**<br><sub>(145 MiB / 44.1% CPU · 211 MiB/s · 523.9 µs CPU/req)</sub> | *Not possible (no QUIC)* | **1,734**<br><sub>(193 MiB / 53.3% CPU · 108 MiB/s · 1229.9 µs CPU/req)</sub> |
| 256 KiB | HTTP/2 · plain | HTTP/1 · plain | 🥇 **6,073**<br><sub>(173 MiB / 36.2% CPU · 1,518 MiB/s · 238.6 µs CPU/req)</sub> | **1,254**<br><sub>(129 MiB / 24.4% CPU · 314 MiB/s · 779.3 µs CPU/req)</sub> | **5,674**<br><sub>(124 MiB / 38.8% CPU · 1,419 MiB/s · 273.4 µs CPU/req)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/2 · plain | 🥇 **2,790**<br><sub>(220 MiB / 47.8% CPU · 698 MiB/s · 685.1 µs CPU/req)</sub> | *Not possible* | **2,788**<br><sub>(175 MiB / 44.3% CPU · 697 MiB/s · 635.6 µs CPU/req)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **1,655**<br><sub>(211 MiB / 46.9% CPU · 414 MiB/s · 1133.5 µs CPU/req)</sub> | *Not possible* | **1,421**<br><sub>(151 MiB / 46.2% CPU · 355 MiB/s · 1299.5 µs CPU/req)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/2 · TLS | **152**<br><sub>(123 MiB / 46.6% CPU · 38 MiB/s · 12263.3 µs CPU/req)</sub> | *Not possible* | 🥇 **701**<br><sub>(193 MiB / 49.0% CPU · 175 MiB/s · 2798.1 µs CPU/req)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **722**<br><sub>(132 MiB / 45.2% CPU · 181 MiB/s · 2504.4 µs CPU/req)</sub> | *Not possible (no QUIC)* | **263**<br><sub>(peak 311 · 169 MiB / 52.2% CPU · 66 MiB/s · 7940.7 µs CPU/req)</sub> |

nginx/Windows collapses on large reverse bodies in this harness; treat as same-OS only. Prefer the ratio columns; absolute RPS swings by VM.

### Linux — heavier reverse GET (64 KiB / 256 KiB)

Median of **3** repeats @ `e0c98136`. Source: Actions [41268406222](https://github.com/justcoding121/titanium-web-proxy/actions/runs/41268406222) (`compare-bodies`). Warmup 2s / measure 8s.


| Body | Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---|---:|---:|---:|---:|---:|
| 64 KiB | HTTP/1 · TLS | HTTP/1 · plain | **8,325**<br><sub>(130 MiB / 43.0% CPU · 520 MiB/s · 206.4 µs CPU/req)</sub> | **5,751**<br><sub>(109 MiB / 53.1% CPU · 359 MiB/s · 369.3 µs CPU/req)</sub> | 🥇 **8,530**<br><sub>(89 MiB / 40.5% CPU · 533 MiB/s · 190.2 µs CPU/req)</sub> | **7,186**<br><sub>(127 MiB / 47.5% CPU · 449 MiB/s · 264.3 µs CPU/req)</sub> | **6,631**<br><sub>(165 MiB / 49.0% CPU · 414 MiB/s · 295.4 µs CPU/req)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **7,105**<br><sub>(260 MiB / 44.1% CPU · 444 MiB/s · 248.2 µs CPU/req)</sub> | **4,638**<br><sub>(109 MiB / 50.7% CPU · 290 MiB/s · 437.6 µs CPU/req)</sub> | **6,238**<br><sub>(89 MiB / 48.0% CPU · 390 MiB/s · 307.8 µs CPU/req)</sub> | **6,677**<br><sub>(129 MiB / 49.0% CPU · 417 MiB/s · 293.7 µs CPU/req)</sub> | **5,936**<br><sub>(166 MiB / 48.6% CPU · 371 MiB/s · 327.7 µs CPU/req)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/1 · plain | **5,759**<br><sub>(203 MiB / 47.0% CPU · 360 MiB/s · 326.2 µs CPU/req)</sub> | **2,524**<br><sub>(120 MiB / 36.6% CPU · 158 MiB/s · 579.4 µs CPU/req)</sub> | 🥇 **5,864**<br><sub>(98 MiB / 40.4% CPU · 366 MiB/s · 275.7 µs CPU/req)</sub> | **4,013**<br><sub>(135 MiB / 52.2% CPU · 251 MiB/s · 519.9 µs CPU/req)</sub> | **3,717**<br><sub>(237 MiB / 53.1% CPU · 232 MiB/s · 571.7 µs CPU/req)</sub> |
| 256 KiB | HTTP/1 · TLS | HTTP/1 · plain | **4,746**<br><sub>(132 MiB / 29.1% CPU · 1,186 MiB/s · 245.4 µs CPU/req)</sub> | **2,911**<br><sub>(110 MiB / 50.2% CPU · 728 MiB/s · 690.1 µs CPU/req)</sub> | 🥇 **4,882**<br><sub>(88 MiB / 25.2% CPU · 1,220 MiB/s · 206.4 µs CPU/req)</sub> | **4,164**<br><sub>(141 MiB / 31.5% CPU · 1,041 MiB/s · 302.3 µs CPU/req)</sub> | **3,713**<br><sub>(175 MiB / 39.7% CPU · 928 MiB/s · 428.1 µs CPU/req)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/1 · plain | **4,116**<br><sub>(219 MiB / 31.6% CPU · 1,029 MiB/s · 306.6 µs CPU/req)</sub> | **3,074**<br><sub>(110 MiB / 48.1% CPU · 769 MiB/s · 625.8 µs CPU/req)</sub> | **4,083**<br><sub>(88 MiB / 36.6% CPU · 1,021 MiB/s · 358.3 µs CPU/req)</sub> | 🥇 **4,479**<br><sub>(149 MiB / 32.4% CPU · 1,120 MiB/s · 289.3 µs CPU/req)</sub> | **3,718**<br><sub>(171 MiB / 37.6% CPU · 930 MiB/s · 404.7 µs CPU/req)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/1 · plain | **1,526**<br><sub>(179 MiB / 52.0% CPU · 381 MiB/s · 1363.0 µs CPU/req)</sub> | **761**<br><sub>(121 MiB / 39.2% CPU · 190 MiB/s · 2060.5 µs CPU/req)</sub> | 🥇 **1,798**<br><sub>(97 MiB / 37.4% CPU · 449 MiB/s · 832.6 µs CPU/req)</sub> | **1,509**<br><sub>(148 MiB / 45.6% CPU · 377 MiB/s · 1208.9 µs CPU/req)</sub> | **1,227**<br><sub>(237 MiB / 52.7% CPU · 307 MiB/s · 1718.8 µs CPU/req)</sub> |
| 64 KiB | HTTP/2 · plain | HTTP/1 · plain | 🥇 **11,469**<br><sub>(246 MiB / 42.9% CPU · 717 MiB/s · 149.7 µs CPU/req)</sub> | **7,031**<br><sub>(82 MiB / 52.7% CPU · 439 MiB/s · 299.7 µs CPU/req)</sub> | **10,680**<br><sub>(73 MiB / 46.3% CPU · 667 MiB/s · 173.5 µs CPU/req)</sub> | **9,886**<br><sub>(118 MiB / 53.8% CPU · 618 MiB/s · 217.7 µs CPU/req)</sub> | **10,964**<br><sub>(143 MiB / 46.8% CPU · 685 MiB/s · 170.6 µs CPU/req)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/2 · plain | 🥇 **7,812**<br><sub>(203 MiB / 39.5% CPU · 488 MiB/s · 202.0 µs CPU/req)</sub> | *Not possible* | **5,977**<br><sub>(91 MiB / 46.8% CPU · 374 MiB/s · 313.0 µs CPU/req)</sub> | **6,640**<br><sub>(130 MiB / 48.0% CPU · 415 MiB/s · 289.5 µs CPU/req)</sub> | **6,042**<br><sub>(201 MiB / 49.0% CPU · 378 MiB/s · 324.5 µs CPU/req)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **8,004**<br><sub>(214 MiB / 38.6% CPU · 500 MiB/s · 192.7 µs CPU/req)</sub> | *Not possible* | **6,804**<br><sub>(91 MiB / 47.2% CPU · 425 MiB/s · 277.4 µs CPU/req)</sub> | **7,570**<br><sub>(136 MiB / 46.2% CPU · 473 MiB/s · 244.0 µs CPU/req)</sub> | **6,507**<br><sub>(203 MiB / 46.5% CPU · 407 MiB/s · 285.8 µs CPU/req)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/2 · TLS | **7,984**<br><sub>(210 MiB / 43.0% CPU · 499 MiB/s · 215.4 µs CPU/req)</sub> | *Not possible* | 🥇 **8,094**<br><sub>(100 MiB / 38.5% CPU · 506 MiB/s · 190.3 µs CPU/req)</sub> | **5,894**<br><sub>(142 MiB / 46.0% CPU · 368 MiB/s · 312.2 µs CPU/req)</sub> | **5,719**<br><sub>(267 MiB / 49.3% CPU · 357 MiB/s · 344.9 µs CPU/req)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/1 · TLS | **8,276**<br><sub>(236 MiB / 44.3% CPU · 517 MiB/s · 214.2 µs CPU/req)</sub> | **4,076**<br><sub>(129 MiB / 37.7% CPU · 255 MiB/s · 369.9 µs CPU/req)</sub> | 🥇 **8,379**<br><sub>(98 MiB / 41.0% CPU · 524 MiB/s · 195.5 µs CPU/req)</sub> | **6,859**<br><sub>(135 MiB / 48.1% CPU · 429 MiB/s · 280.3 µs CPU/req)</sub> | **6,235**<br><sub>(275 MiB / 50.1% CPU · 390 MiB/s · 321.7 µs CPU/req)</sub> |
| 256 KiB | HTTP/2 · plain | HTTP/1 · plain | **3,437**<br><sub>(208 MiB / 32.6% CPU · 859 MiB/s · 379.6 µs CPU/req)</sub> | **2,233**<br><sub>(81 MiB / 52.2% CPU · 558 MiB/s · 935.9 µs CPU/req)</sub> | **3,506**<br><sub>(72 MiB / 34.4% CPU · 876 MiB/s · 392.2 µs CPU/req)</sub> | 🥇 **3,598**<br><sub>(135 MiB / 33.8% CPU · 900 MiB/s · 375.9 µs CPU/req)</sub> | **3,379**<br><sub>(152 MiB / 37.6% CPU · 845 MiB/s · 444.7 µs CPU/req)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/2 · plain | **2,844**<br><sub>(226 MiB / 34.3% CPU · 711 MiB/s · 482.1 µs CPU/req)</sub> | *Not possible* | **2,751**<br><sub>(100 MiB / 36.5% CPU · 688 MiB/s · 530.8 µs CPU/req)</sub> | 🥇 **2,862**<br><sub>(142 MiB / 35.7% CPU · 716 MiB/s · 498.6 µs CPU/req)</sub> | **2,263**<br><sub>(206 MiB / 42.5% CPU · 566 MiB/s · 750.9 µs CPU/req)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **2,073**<br><sub>(257 MiB / 36.3% CPU · 518 MiB/s · 701.0 µs CPU/req)</sub> | *Not possible* | **1,983**<br><sub>(98 MiB / 39.9% CPU · 496 MiB/s · 804.1 µs CPU/req)</sub> | **1,993**<br><sub>(141 MiB / 39.0% CPU · 498 MiB/s · 782.6 µs CPU/req)</sub> | **1,611**<br><sub>(200 MiB / 42.5% CPU · 403 MiB/s · 1055.6 µs CPU/req)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/2 · TLS | **992**<br><sub>(204 MiB / 57.1% CPU · 248 MiB/s · 2300.7 µs CPU/req)</sub> | *Not possible* | 🥇 **1,692**<br><sub>(109 MiB / 38.1% CPU · 423 MiB/s · 900.1 µs CPU/req)</sub> | **1,335**<br><sub>(peak 1,341 · 150 MiB / 46.3% CPU · 334 MiB/s · 1387.1 µs CPU/req)</sub> | **1,174**<br><sub>(246 MiB / 47.7% CPU · 294 MiB/s · 1624.3 µs CPU/req)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/1 · TLS | **1,611**<br><sub>(208 MiB / 47.9% CPU · 403 MiB/s · 1188.4 µs CPU/req)</sub> | **874**<br><sub>(128 MiB / 35.8% CPU · 218 MiB/s · 1640.3 µs CPU/req)</sub> | 🥇 **1,839**<br><sub>(99 MiB / 38.4% CPU · 460 MiB/s · 834.5 µs CPU/req)</sub> | **1,572**<br><sub>(149 MiB / 45.6% CPU · 393 MiB/s · 1160.1 µs CPU/req)</sub> | **1,342**<br><sub>(267 MiB / 48.4% CPU · 336 MiB/s · 1443.4 µs CPU/req)</sub> |

Prefer the ratio columns; absolute RPS swings by VM.

### macOS — heavier reverse GET (64 KiB / 256 KiB)

Median of **3** repeats on `macos-15` (Apple Silicon M1, 3-core / 7 GB) @ `e0c98136`. Source: Actions [41268406222](https://github.com/justcoding121/titanium-web-proxy/actions/runs/41268406222) (`compare-bodies`). Warmup 2s / measure 8s.

| Body | Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---|---:|---:|---:|---:|---:|
| 64 KiB | HTTP/1 · TLS | HTTP/1 · plain | **5,937**<br><sub>(178 MiB / 28.4% CPU · 371 MiB/s · 143.5 µs CPU/req)</sub> | 🥇 **6,829**<br><sub>(84 MiB / 25.3% CPU · 427 MiB/s · 111.1 µs CPU/req)</sub> | **6,591**<br><sub>(123 MiB / 28.2% CPU · 412 MiB/s · 128.6 µs CPU/req)</sub> | **4,744**<br><sub>(132 MiB / 27.8% CPU · 297 MiB/s · 175.9 µs CPU/req)</sub> | **4,020**<br><sub>(178 MiB / 32.9% CPU · 251 MiB/s · 245.3 µs CPU/req)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/1 · plain | **3,899**<br><sub>(465 MiB / 26.0% CPU · 244 MiB/s · 199.9 µs CPU/req)</sub> | 🥇 **4,588**<br><sub>(86 MiB / 24.7% CPU · 287 MiB/s · 161.5 µs CPU/req)</sub> | **4,098**<br><sub>(119 MiB / 26.4% CPU · 256 MiB/s · 193.5 µs CPU/req)</sub> | **3,383**<br><sub>(142 MiB / 25.5% CPU · 211 MiB/s · 226.3 µs CPU/req)</sub> | **1,753**<br><sub>(179 MiB / 18.4% CPU · 110 MiB/s · 314.5 µs CPU/req)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/1 · plain | **1,943**<br><sub>(295 MiB / 29.3% CPU · 121 MiB/s · 452.3 µs CPU/req)</sub> | 🥇 **2,647**<br><sub>(96 MiB / 29.1% CPU · 165 MiB/s · 329.2 µs CPU/req)</sub> | **1,660**<br><sub>(127 MiB / 32.3% CPU · 104 MiB/s · 584.6 µs CPU/req)</sub> | **1,847**<br><sub>(130 MiB / 53.3% CPU · 115 MiB/s · 865.1 µs CPU/req)</sub> | **1,446**<br><sub>(415 MiB / 40.1% CPU · 90 MiB/s · 831.9 µs CPU/req)</sub> |
| 256 KiB | HTTP/1 · TLS | HTTP/1 · plain | **2,726**<br><sub>(206 MiB / 26.1% CPU · 681 MiB/s · 287.7 µs CPU/req)</sub> | **2,101**<br><sub>(85 MiB / 23.0% CPU · 525 MiB/s · 328.8 µs CPU/req)</sub> | 🥇 **3,636**<br><sub>(117 MiB / 28.0% CPU · 909 MiB/s · 230.9 µs CPU/req)</sub> | **3,147**<br><sub>(153 MiB / 30.8% CPU · 787 MiB/s · 293.9 µs CPU/req)</sub> | **2,026**<br><sub>(181 MiB / 36.5% CPU · 506 MiB/s · 540.7 µs CPU/req)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/1 · plain | **988**<br><sub>(459 MiB / 20.8% CPU · 247 MiB/s · 630.8 µs CPU/req)</sub> | **2,209**<br><sub>(86 MiB / 25.7% CPU · 552 MiB/s · 349.4 µs CPU/req)</sub> | 🥇 **2,447**<br><sub>(117 MiB / 30.2% CPU · 612 MiB/s · 370.7 µs CPU/req)</sub> | **2,390**<br><sub>(153 MiB / 31.4% CPU · 598 MiB/s · 394.4 µs CPU/req)</sub> | **2,236**<br><sub>(178 MiB / 40.4% CPU · 559 MiB/s · 542.4 µs CPU/req)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/1 · plain | **398**<br><sub>(195 MiB / 30.3% CPU · 100 MiB/s · 2285.0 µs CPU/req)</sub> | 🥇 **733**<br><sub>(96 MiB / 27.2% CPU · 183 MiB/s · 1113.5 µs CPU/req)</sub> | **351**<br><sub>(peak 694 · 106 MiB / 22.4% CPU · 88 MiB/s · 1917.6 µs CPU/req)</sub> | **581**<br><sub>(148 MiB / 49.8% CPU · 145 MiB/s · 2571.6 µs CPU/req)</sub> | **154**<br><sub>(peak 166 · 268 MiB / 27.9% CPU · 38 MiB/s · 5440.9 µs CPU/req)</sub> |
| 64 KiB | HTTP/2 · plain | HTTP/1 · plain | **8,332**<br><sub>(453 MiB / 35.9% CPU · 521 MiB/s · 129.2 µs CPU/req)</sub> | 🥇 **11,008**<br><sub>(72 MiB / 22.6% CPU · 688 MiB/s · 61.5 µs CPU/req)</sub> | **9,015**<br><sub>(109 MiB / 35.1% CPU · 563 MiB/s · 116.7 µs CPU/req)</sub> | **8,540**<br><sub>(123 MiB / 26.7% CPU · 534 MiB/s · 93.8 µs CPU/req)</sub> | **8,110**<br><sub>(187 MiB / 40.6% CPU · 507 MiB/s · 150.1 µs CPU/req)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/2 · plain | **6,933**<br><sub>(404 MiB / 32.2% CPU · 433 MiB/s · 139.2 µs CPU/req)</sub> | *Not possible* | **6,298**<br><sub>(131 MiB / 36.3% CPU · 394 MiB/s · 173.1 µs CPU/req)</sub> | 🥇 **6,940**<br><sub>(125 MiB / 37.4% CPU · 434 MiB/s · 161.5 µs CPU/req)</sub> | **6,238**<br><sub>(265 MiB / 43.8% CPU · 390 MiB/s · 210.4 µs CPU/req)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/2 · TLS | **3,354**<br><sub>(443 MiB / 29.6% CPU · 210 MiB/s · 264.3 µs CPU/req)</sub> | *Not possible* | **3,631**<br><sub>(124 MiB / 28.6% CPU · 227 MiB/s · 236.0 µs CPU/req)</sub> | **3,963**<br><sub>(125 MiB / 30.5% CPU · 248 MiB/s · 230.9 µs CPU/req)</sub> | 🥇 **4,313**<br><sub>(238 MiB / 41.5% CPU · 270 MiB/s · 288.7 µs CPU/req)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/2 · TLS | 🥇 **2,624**<br><sub>(233 MiB / 40.2% CPU · 164 MiB/s · 460.1 µs CPU/req)</sub> | *Not possible* | **2,442**<br><sub>(133 MiB / 35.6% CPU · 153 MiB/s · 437.3 µs CPU/req)</sub> | **1,792**<br><sub>(129 MiB / 49.3% CPU · 112 MiB/s · 824.9 µs CPU/req)</sub> | **1,980**<br><sub>(426 MiB / 42.9% CPU · 124 MiB/s · 650.4 µs CPU/req)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/1 · TLS | **1,773**<br><sub>(249 MiB / 34.7% CPU · 111 MiB/s · 587.8 µs CPU/req)</sub> | 🥇 **1,810**<br><sub>(100 MiB / 23.8% CPU · 113 MiB/s · 394.9 µs CPU/req)</sub> | **1,426**<br><sub>(129 MiB / 30.7% CPU · 89 MiB/s · 646.6 µs CPU/req)</sub> | **1,172**<br><sub>(130 MiB / 43.7% CPU · 73 MiB/s · 1117.9 µs CPU/req)</sub> | **1,046**<br><sub>(429 MiB / 34.5% CPU · 65 MiB/s · 991.1 µs CPU/req)</sub> |
| 256 KiB | HTTP/2 · plain | HTTP/1 · plain | **2,834**<br><sub>(431 MiB / 29.6% CPU · 709 MiB/s · 313.5 µs CPU/req)</sub> | **2,999**<br><sub>(72 MiB / 20.4% CPU · 750 MiB/s · 204.6 µs CPU/req)</sub> | 🥇 **3,346**<br><sub>(102 MiB / 27.1% CPU · 836 MiB/s · 243.2 µs CPU/req)</sub> | **3,166**<br><sub>(144 MiB / 26.3% CPU · 791 MiB/s · 249.4 µs CPU/req)</sub> | **2,506**<br><sub>(192 MiB / 35.1% CPU · 626 MiB/s · 420.8 µs CPU/req)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/2 · plain | **1,573**<br><sub>(680 MiB / 27.5% CPU · 393 MiB/s · 525.2 µs CPU/req)</sub> | *Not possible* | **1,638**<br><sub>(138 MiB / 29.6% CPU · 410 MiB/s · 542.6 µs CPU/req)</sub> | 🥇 **1,822**<br><sub>(137 MiB / 27.0% CPU · 455 MiB/s · 444.0 µs CPU/req)</sub> | **1,308**<br><sub>(280 MiB / 25.8% CPU · 327 MiB/s · 592.3 µs CPU/req)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/2 · TLS | **1,242**<br><sub>(595 MiB / 28.1% CPU · 311 MiB/s · 677.4 µs CPU/req)</sub> | *Not possible* | **1,415**<br><sub>(131 MiB / 32.9% CPU · 354 MiB/s · 697.4 µs CPU/req)</sub> | **1,408**<br><sub>(129 MiB / 30.6% CPU · 352 MiB/s · 651.1 µs CPU/req)</sub> | 🥇 **1,482**<br><sub>(266 MiB / 37.2% CPU · 370 MiB/s · 753.3 µs CPU/req)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/2 · TLS | **165**<br><sub>(318 MiB / 28.8% CPU · 41 MiB/s · 5232.7 µs CPU/req)</sub> | *Not possible* | **271**<br><sub>(peak 504 · 113 MiB / 23.3% CPU · 68 MiB/s · 2575.1 µs CPU/req)</sub> | 🥇 **406**<br><sub>(139 MiB / 48.8% CPU · 102 MiB/s · 3607.4 µs CPU/req)</sub> | **210**<br><sub>(peak 249 · 537 MiB / 31.1% CPU · 53 MiB/s · 4428.0 µs CPU/req)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/1 · TLS | **370**<br><sub>(peak 386 · 222 MiB / 28.6% CPU · 92 MiB/s · 2323.4 µs CPU/req)</sub> | 🥇 **590**<br><sub>(98 MiB / 28.2% CPU · 147 MiB/s · 1432.6 µs CPU/req)</sub> | **501**<br><sub>(107 MiB / 23.2% CPU · 125 MiB/s · 1388.1 µs CPU/req)</sub> | **421**<br><sub>(149 MiB / 48.1% CPU · 105 MiB/s · 3429.1 µs CPU/req)</sub> | **239**<br><sub>(167 MiB / 39.7% CPU · 60 MiB/s · 4984.5 µs CPU/req)</sub> |

### Windows — POST 64 KiB request + 64 KiB response

Median of **3** repeats on `windows-latest` @ `e0c98136`. Source: Actions [41268415633](https://github.com/justcoding121/titanium-web-proxy/actions/runs/41268415633) (`compare-post`).

*Not possible:* **HAProxy** and **Envoy** columns are omitted (no official Windows port).






















| Client | Origin | TWP | nginx | YARP |
|---|---|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | 🥇 **11,778**<br><sub>(96 MiB / 41.1% CPU · 1,472 MiB/s · 139.7 µs CPU/req)</sub> | **720**<br><sub>(144 MiB / 24.9% CPU · 90 MiB/s · 1383.0 µs CPU/req)</sub> | **8,816**<br><sub>(136 MiB / 52.2% CPU · 1,102 MiB/s · 237.0 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **4,475**<br><sub>(223 MiB / 47.2% CPU · 559 MiB/s · 421.7 µs CPU/req)</sub> | **397**<br><sub>(148 MiB / 24.8% CPU · 50 MiB/s · 2493.2 µs CPU/req)</sub> | **4,337**<br><sub>(139 MiB / 47.7% CPU · 542 MiB/s · 440.3 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **2,307**<br><sub>(178 MiB / 47.7% CPU · 288 MiB/s · 826.5 µs CPU/req)</sub> | *Not possible (no QUIC)* | **1,690**<br><sub>(211 MiB / 51.5% CPU · 211 MiB/s · 1219.5 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | 🥇 **9,004**<br><sub>(210 MiB / 45.3% CPU · 1,125 MiB/s · 201.4 µs CPU/req)</sub> | **2,837**<br><sub>(133 MiB / 24.8% CPU · 355 MiB/s · 350.1 µs CPU/req)</sub> | **8,442**<br><sub>(116 MiB / 48.7% CPU · 1,055 MiB/s · 230.7 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | 🥇 **4,556**<br><sub>(179 MiB / 37.8% CPU · 570 MiB/s · 331.8 µs CPU/req)</sub> | *Not possible* | **3,537**<br><sub>(154 MiB / 47.8% CPU · 442 MiB/s · 540.9 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **3,392**<br><sub>(169 MiB / 40.0% CPU · 424 MiB/s · 471.6 µs CPU/req)</sub> | *Not possible* | **2,727**<br><sub>(154 MiB / 47.3% CPU · 341 MiB/s · 693.3 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | 🥇 **1,876**<br><sub>(194 MiB / 43.9% CPU · 235 MiB/s · 934.9 µs CPU/req)</sub> | *Not possible* | **1,063**<br><sub>(226 MiB / 50.1% CPU · 133 MiB/s · 1885.2 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **2,717**<br><sub>(176 MiB / 45.3% CPU · 340 MiB/s · 667.2 µs CPU/req)</sub> | *Not possible (no QUIC)* | **1,706**<br><sub>(212 MiB / 46.7% CPU · 213 MiB/s · 1095.3 µs CPU/req)</sub> |

TWP leads H2→H1 POST (about **1.1–1.3×** YARP) and H3 POST (about **1.0–1.1×** YARP). H2 TLS→H2 TLS POST sustain is about **1.2–1.4×** YARP.

### Linux — POST 64 KiB request + 64 KiB response

Median of **3** repeats @ `e0c98136`. Source: Actions [41268415633](https://github.com/justcoding121/titanium-web-proxy/actions/runs/41268415633) (`compare-post`).


| Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---:|---:|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | **4,355**<br><sub>(151 MiB / 44.3% CPU · 544 MiB/s · 407.1 µs CPU/req)</sub> | **3,731**<br><sub>(109 MiB / 49.9% CPU · 466 MiB/s · 534.7 µs CPU/req)</sub> | 🥇 **4,929**<br><sub>(90 MiB / 42.0% CPU · 616 MiB/s · 340.9 µs CPU/req)</sub> | **4,606**<br><sub>(128 MiB / 43.9% CPU · 576 MiB/s · 381.0 µs CPU/req)</sub> | **3,158**<br><sub>(172 MiB / 56.5% CPU · 395 MiB/s · 715.9 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | **4,022**<br><sub>(277 MiB / 47.9% CPU · 503 MiB/s · 476.1 µs CPU/req)</sub> | **3,514**<br><sub>(115 MiB / 46.5% CPU · 439 MiB/s · 529.6 µs CPU/req)</sub> | 🥇 **4,144**<br><sub>(91 MiB / 45.3% CPU · 518 MiB/s · 437.0 µs CPU/req)</sub> | **3,798**<br><sub>(peak 4,596 · 122 MiB / 41.7% CPU · 475 MiB/s · 438.9 µs CPU/req)</sub> | **3,694**<br><sub>(175 MiB / 50.2% CPU · 462 MiB/s · 544.2 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **4,492**<br><sub>(236 MiB / 46.9% CPU · 562 MiB/s · 418.0 µs CPU/req)</sub> | **1,570**<br><sub>(121 MiB / 50.7% CPU · 196 MiB/s · 1292.8 µs CPU/req)</sub> | **3,823**<br><sub>(96 MiB / 42.3% CPU · 478 MiB/s · 442.4 µs CPU/req)</sub> | **0**<br><sub>(125 MiB / 0.2% CPU)</sub> | **3,383**<br><sub>(257 MiB / 50.3% CPU · 423 MiB/s · 595.0 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | **10,196**<br><sub>(277 MiB / 40.7% CPU · 1,275 MiB/s · 159.8 µs CPU/req)</sub> | **8,780**<br><sub>(90 MiB / 46.7% CPU · 1,097 MiB/s · 212.6 µs CPU/req)</sub> | 🥇 **11,970**<br><sub>(75 MiB / 41.0% CPU · 1,496 MiB/s · 137.0 µs CPU/req)</sub> | **9,792**<br><sub>(peak 12,251 · 111 MiB / 39.9% CPU · 1,224 MiB/s · 162.8 µs CPU/req)</sub> | **10,286**<br><sub>(150 MiB / 44.6% CPU · 1,286 MiB/s · 173.3 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | 🥇 **6,253**<br><sub>(229 MiB / 36.5% CPU · 782 MiB/s · 233.2 µs CPU/req)</sub> | *Not possible* | **5,311**<br><sub>(93 MiB / 42.4% CPU · 664 MiB/s · 319.3 µs CPU/req)</sub> | **6,245**<br><sub>(132 MiB / 42.1% CPU · 781 MiB/s · 269.5 µs CPU/req)</sub> | **4,475**<br><sub>(200 MiB / 49.5% CPU · 559 MiB/s · 442.2 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **6,480**<br><sub>(228 MiB / 37.1% CPU · 810 MiB/s · 229.1 µs CPU/req)</sub> | *Not possible* | **5,522**<br><sub>(93 MiB / 45.5% CPU · 690 MiB/s · 329.9 µs CPU/req)</sub> | **6,151**<br><sub>(137 MiB / 43.0% CPU · 769 MiB/s · 279.5 µs CPU/req)</sub> | **4,692**<br><sub>(189 MiB / 47.0% CPU · 587 MiB/s · 400.9 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | 🥇 **3,104**<br><sub>(249 MiB / 42.8% CPU · 388 MiB/s · 552.0 µs CPU/req)</sub> | *Not possible* | **2,858**<br><sub>(97 MiB / 40.5% CPU · 357 MiB/s · 567.0 µs CPU/req)</sub> | **0**<br><sub>(125 MiB / 0.1% CPU)</sub> | **2,262**<br><sub>(266 MiB / 49.2% CPU · 283 MiB/s · 870.4 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **2,272**<br><sub>(246 MiB / 46.8% CPU · 284 MiB/s · 823.4 µs CPU/req)</sub> | **848**<br><sub>(123 MiB / 50.2% CPU · 106 MiB/s · 2368.8 µs CPU/req)</sub> | **2,132**<br><sub>(99 MiB / 45.5% CPU · 266 MiB/s · 853.1 µs CPU/req)</sub> | **0**<br><sub>(126 MiB / 0.3% CPU)</sub> | **1,843**<br><sub>(271 MiB / 49.7% CPU · 230 MiB/s · 1078.9 µs CPU/req)</sub> |

Linux nginx H1/H2/H3 POST completed (nginx.org mainline). TWP÷YARP H1 ≈ **1.5×**; H2→H1 ≈ **1.2×**; H3 ≈ **1.1×**. TWP÷nginx H3 ≈ **6×**.

### macOS — POST 64 KiB request + 64 KiB response

Median of **3** repeats on `macos-15` (Apple Silicon M1, 3-core / 7 GB) @ `e0c98136`. Source: Actions [41268415633](https://github.com/justcoding121/titanium-web-proxy/actions/runs/41268415633) (`compare-post`).

| Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---:|---:|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | **3,346**<br><sub>(255 MiB / 29.8% CPU · 418 MiB/s · 266.8 µs CPU/req)</sub> | **3,731**<br><sub>(89 MiB / 24.2% CPU · 466 MiB/s · 194.8 µs CPU/req)</sub> | 🥇 **4,124**<br><sub>(121 MiB / 31.3% CPU · 516 MiB/s · 227.4 µs CPU/req)</sub> | **3,640**<br><sub>(134 MiB / 31.3% CPU · 455 MiB/s · 258.0 µs CPU/req)</sub> | **2,727**<br><sub>(199 MiB / 43.8% CPU · 341 MiB/s · 482.3 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | **1,932**<br><sub>(527 MiB / 28.5% CPU · 242 MiB/s · 442.8 µs CPU/req)</sub> | 🥇 **2,574**<br><sub>(98 MiB / 21.1% CPU · 322 MiB/s · 246.2 µs CPU/req)</sub> | **2,230**<br><sub>(122 MiB / 26.1% CPU · 279 MiB/s · 351.6 µs CPU/req)</sub> | **1,925**<br><sub>(peak 2,811 · 120 MiB / 25.1% CPU · 241 MiB/s · 392.0 µs CPU/req)</sub> | **1,667**<br><sub>(183 MiB / 26.0% CPU · 208 MiB/s · 468.2 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | **1,323**<br><sub>(330 MiB / 34.1% CPU · 165 MiB/s · 772.2 µs CPU/req)</sub> | 🥇 **2,142**<br><sub>(96 MiB / 27.8% CPU · 268 MiB/s · 389.3 µs CPU/req)</sub> | **1,006**<br><sub>(119 MiB / 31.1% CPU · 126 MiB/s · 927.8 µs CPU/req)</sub> | **1,125**<br><sub>(139 MiB / 47.1% CPU · 141 MiB/s · 1255.4 µs CPU/req)</sub> | **1,549**<br><sub>(447 MiB / 38.5% CPU · 194 MiB/s · 744.9 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | **6,464**<br><sub>(555 MiB / 34.5% CPU · 808 MiB/s · 160.2 µs CPU/req)</sub> | **6,002**<br><sub>(85 MiB / 21.3% CPU · 750 MiB/s · 106.6 µs CPU/req)</sub> | 🥇 **6,755**<br><sub>(113 MiB / 31.8% CPU · 844 MiB/s · 141.3 µs CPU/req)</sub> | **5,607**<br><sub>(122 MiB / 31.8% CPU · 701 MiB/s · 169.9 µs CPU/req)</sub> | **4,614**<br><sub>(193 MiB / 42.5% CPU · 577 MiB/s · 276.0 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | **3,023**<br><sub>(438 MiB / 27.4% CPU · 378 MiB/s · 271.7 µs CPU/req)</sub> | *Not possible* | **4,178**<br><sub>(128 MiB / 33.0% CPU · 522 MiB/s · 236.6 µs CPU/req)</sub> | 🥇 **4,862**<br><sub>(127 MiB / 34.0% CPU · 608 MiB/s · 209.8 µs CPU/req)</sub> | **3,301**<br><sub>(241 MiB / 44.0% CPU · 413 MiB/s · 399.6 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | **2,343**<br><sub>(415 MiB / 32.2% CPU · 293 MiB/s · 412.3 µs CPU/req)</sub> | *Not possible* | **2,518**<br><sub>(128 MiB / 35.5% CPU · 315 MiB/s · 423.0 µs CPU/req)</sub> | 🥇 **3,279**<br><sub>(127 MiB / 33.5% CPU · 410 MiB/s · 306.3 µs CPU/req)</sub> | **1,774**<br><sub>(222 MiB / 40.3% CPU · 222 MiB/s · 682.3 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | **845**<br><sub>(349 MiB / 26.3% CPU · 106 MiB/s · 935.3 µs CPU/req)</sub> | *Not possible* | 🥇 **868**<br><sub>(123 MiB / 32.1% CPU · 109 MiB/s · 1108.0 µs CPU/req)</sub> | **721**<br><sub>(136 MiB / 47.0% CPU · 90 MiB/s · 1954.4 µs CPU/req)</sub> | **763**<br><sub>(397 MiB / 38.3% CPU · 95 MiB/s · 1506.7 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | **782**<br><sub>(276 MiB / 30.1% CPU · 98 MiB/s · 1155.2 µs CPU/req)</sub> | 🥇 **1,137**<br><sub>(101 MiB / 24.9% CPU · 142 MiB/s · 657.8 µs CPU/req)</sub> | **814**<br><sub>(125 MiB / 34.1% CPU · 102 MiB/s · 1257.2 µs CPU/req)</sub> | **380**<br><sub>(138 MiB / 28.6% CPU · 47 MiB/s · 2264.2 µs CPU/req)</sub> | **865**<br><sub>(410 MiB / 40.1% CPU · 108 MiB/s · 1393.1 µs CPU/req)</sub> |

### Windows — lossy / high-RTT (H2 HOL / H3 loss)

Userspace **5 ms** one-way delay + **1%** TCP connection stall (H1/H2); UDP is **loss% only** (no per-datagram delay; MsQuic-safe) + **1%** datagram drop (H3); **64 KiB** GET. Median of **3** repeats on `windows-latest` @ `e0c98136` — [41268425096](https://github.com/justcoding121/titanium-web-proxy/actions/runs/41268425096) (`compare-lossy`).

*Not possible:* **HAProxy** and **Envoy** columns are omitted (no official Windows port).


| Client | Origin | TWP | nginx | YARP |
|---|---|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | 🥇 **662**<br><sub>(88 MiB / 5.0% CPU · 41 MiB/s · 304.3 µs CPU/req)</sub> | **640**<br><sub>(143 MiB / 18.9% CPU · 40 MiB/s · 1183.9 µs CPU/req)</sub> | **660**<br><sub>(119 MiB / 5.5% CPU · 41 MiB/s · 334.0 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **248**<br><sub>(154 MiB / 2.9% CPU · 15 MiB/s · 468.1 µs CPU/req)</sub> | **244**<br><sub>(144 MiB / 7.8% CPU · 15 MiB/s · 1286.2 µs CPU/req)</sub> | **231**<br><sub>(113 MiB / 3.4% CPU · 14 MiB/s · 587.5 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | **990**<br><sub>(132 MiB / 12.3% CPU · 62 MiB/s · 498.4 µs CPU/req)</sub> | *Not possible (no QUIC)* | 🥇 **1,408**<br><sub>(183 MiB / 31.4% CPU · 88 MiB/s · 892.9 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | 🥇 **250**<br><sub>(153 MiB / 2.8% CPU · 16 MiB/s · 440.9 µs CPU/req)</sub> | **250**<br><sub>(129 MiB / 2.2% CPU · 16 MiB/s · 345.3 µs CPU/req)</sub> | **233**<br><sub>(103 MiB / 2.6% CPU · 15 MiB/s · 453.2 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | **216**<br><sub>(121 MiB / 2.8% CPU · 13 MiB/s · 526.4 µs CPU/req)</sub> | *Not possible* | 🥇 **227**<br><sub>(114 MiB / 3.4% CPU · 14 MiB/s · 598.3 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **233**<br><sub>(124 MiB / 2.1% CPU · 15 MiB/s · 367.2 µs CPU/req)</sub> | *Not possible* | **232**<br><sub>(117 MiB / 3.0% CPU · 14 MiB/s · 512.3 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | **944**<br><sub>(120 MiB / 18.9% CPU · 59 MiB/s · 799.6 µs CPU/req)</sub> | *Not possible* | 🥇 **1,062**<br><sub>(183 MiB / 30.3% CPU · 66 MiB/s · 1142.8 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | **984**<br><sub>(137 MiB / 17.4% CPU · 62 MiB/s · 708.5 µs CPU/req)</sub> | *Not possible (no QUIC)* | 🥇 **1,222**<br><sub>(183 MiB / 31.2% CPU · 76 MiB/s · 1022.9 µs CPU/req)</sub> |

H1, H2, and H3 loss sustain are in the table. Prefer the ratio columns.

### Linux — lossy / high-RTT (H2 HOL / H3 loss)

Median of **3** repeats @ `e0c98136`. Source: [41268425096](https://github.com/justcoding121/titanium-web-proxy/actions/runs/41268425096) (`compare-lossy`; lossy H3 uses `quic-http3`, UDP drop-only).


| Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---:|---:|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | **1,212**<br><sub>(127 MiB / 9.3% CPU · 76 MiB/s · 308.4 µs CPU/req)</sub> | 🥇 **1,220**<br><sub>(110 MiB / 8.1% CPU · 76 MiB/s · 266.3 µs CPU/req)</sub> | **1,218**<br><sub>(86 MiB / 4.2% CPU · 76 MiB/s · 136.3 µs CPU/req)</sub> | **1,209**<br><sub>(123 MiB / 6.7% CPU · 76 MiB/s · 220.0 µs CPU/req)</sub> | **1,213**<br><sub>(152 MiB / 13.0% CPU · 76 MiB/s · 427.4 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **627**<br><sub>(200 MiB / 9.9% CPU · 39 MiB/s · 630.9 µs CPU/req)</sub> | **584**<br><sub>(110 MiB / 4.3% CPU · 37 MiB/s · 297.1 µs CPU/req)</sub> | **570**<br><sub>(87 MiB / 3.1% CPU · 36 MiB/s · 215.5 µs CPU/req)</sub> | **509**<br><sub>(125 MiB / 3.6% CPU · 32 MiB/s · 283.1 µs CPU/req)</sub> | **511**<br><sub>(141 MiB / 9.6% CPU · 32 MiB/s · 747.6 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **1,222**<br><sub>(167 MiB / 24.0% CPU · 76 MiB/s · 784.8 µs CPU/req)</sub> | **1,114**<br><sub>(121 MiB / 23.6% CPU · 70 MiB/s · 846.6 µs CPU/req)</sub> | **988**<br><sub>(102 MiB / 18.8% CPU · 62 MiB/s · 759.6 µs CPU/req)</sub> | **1,061**<br><sub>(133 MiB / 26.8% CPU · 66 MiB/s · 1010.4 µs CPU/req)</sub> | **1,191**<br><sub>(242 MiB / 31.4% CPU · 74 MiB/s · 1053.8 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | 🥇 **652**<br><sub>(183 MiB / 8.5% CPU · 41 MiB/s · 518.6 µs CPU/req)</sub> | **599**<br><sub>(81 MiB / 3.0% CPU · 37 MiB/s · 198.2 µs CPU/req)</sub> | **549**<br><sub>(71 MiB / 2.2% CPU · 34 MiB/s · 161.7 µs CPU/req)</sub> | **507**<br><sub>(116 MiB / 3.1% CPU · 32 MiB/s · 242.8 µs CPU/req)</sub> | **515**<br><sub>(137 MiB / 7.2% CPU · 32 MiB/s · 556.3 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | **499**<br><sub>(152 MiB / 9.5% CPU · 31 MiB/s · 764.7 µs CPU/req)</sub> | *Not possible* | 🥇 **552**<br><sub>(89 MiB / 4.6% CPU · 34 MiB/s · 331.4 µs CPU/req)</sub> | **508**<br><sub>(124 MiB / 5.3% CPU · 32 MiB/s · 421.2 µs CPU/req)</sub> | **511**<br><sub>(149 MiB / 13.0% CPU · 32 MiB/s · 1020.3 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | **482**<br><sub>(162 MiB / 10.6% CPU · 30 MiB/s · 878.1 µs CPU/req)</sub> | *Not possible* | 🥇 **578**<br><sub>(89 MiB / 6.0% CPU · 36 MiB/s · 418.8 µs CPU/req)</sub> | **505**<br><sub>(127 MiB / 6.0% CPU · 32 MiB/s · 477.4 µs CPU/req)</sub> | **506**<br><sub>(150 MiB / 14.2% CPU · 32 MiB/s · 1119.6 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | 🥇 **1,486**<br><sub>(165 MiB / 22.2% CPU · 93 MiB/s · 598.5 µs CPU/req)</sub> | *Not possible* | **1,384**<br><sub>(106 MiB / 20.2% CPU · 86 MiB/s · 583.1 µs CPU/req)</sub> | **1,447**<br><sub>(136 MiB / 27.8% CPU · 90 MiB/s · 768.9 µs CPU/req)</sub> | **1,425**<br><sub>(235 MiB / 32.0% CPU · 89 MiB/s · 898.6 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **1,231**<br><sub>(179 MiB / 26.0% CPU · 77 MiB/s · 846.1 µs CPU/req)</sub> | **1,108**<br><sub>(126 MiB / 24.9% CPU · 69 MiB/s · 898.6 µs CPU/req)</sub> | **985**<br><sub>(105 MiB / 21.1% CPU · 62 MiB/s · 855.2 µs CPU/req)</sub> | **1,017**<br><sub>(134 MiB / 27.2% CPU · 64 MiB/s · 1069.6 µs CPU/req)</sub> | **784**<br><sub>(188 MiB / 25.7% CPU · 49 MiB/s · 1312.6 µs CPU/req)</sub> |

TWP H2 HOL ≫ YARP (~**7.8×**). H3 TWP÷YARP ≈ **1.07×**.

### macOS — lossy / high-RTT (H2 HOL / H3 loss)

Median of **3** repeats on `macos-15` (Apple Silicon M1, 3-core / 7 GB) @ `e0c98136`. Source: [41268425096](https://github.com/justcoding121/titanium-web-proxy/actions/runs/41268425096) (`compare-lossy`; lossy H3 uses `quic-http3`, UDP drop-only).

| Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---:|---:|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | **487**<br><sub>(251 MiB / 6.5% CPU · 30 MiB/s · 397.7 µs CPU/req)</sub> | 🥇 **515**<br><sub>(84 MiB / 3.8% CPU · 32 MiB/s · 223.9 µs CPU/req)</sub> | **513**<br><sub>(116 MiB / 3.5% CPU · 32 MiB/s · 203.6 µs CPU/req)</sub> | **419**<br><sub>(132 MiB / 2.9% CPU · 26 MiB/s · 206.1 µs CPU/req)</sub> | **479**<br><sub>(210 MiB / 5.6% CPU · 30 MiB/s · 352.5 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **184**<br><sub>(338 MiB / 8.7% CPU · 11 MiB/s · 1419.1 µs CPU/req)</sub> | **168**<br><sub>(85 MiB / 2.5% CPU · 10 MiB/s · 445.7 µs CPU/req)</sub> | **176**<br><sub>(114 MiB / 3.8% CPU · 11 MiB/s · 646.2 µs CPU/req)</sub> | **144**<br><sub>(124 MiB / 2.9% CPU · 9.0 MiB/s · 607.9 µs CPU/req)</sub> | **159**<br><sub>(220 MiB / 7.2% CPU · 10.0 MiB/s · 1358.8 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | **782**<br><sub>(271 MiB / 19.0% CPU · 49 MiB/s · 727.9 µs CPU/req)</sub> | **912**<br><sub>(87 MiB / 12.3% CPU · 57 MiB/s · 406.0 µs CPU/req)</sub> | **829**<br><sub>(122 MiB / 21.1% CPU · 52 MiB/s · 765.0 µs CPU/req)</sub> | 🥇 **1,102**<br><sub>(130 MiB / 34.7% CPU · 69 MiB/s · 945.8 µs CPU/req)</sub> | **980**<br><sub>(418 MiB / 30.3% CPU · 61 MiB/s · 925.9 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | **184**<br><sub>(322 MiB / 6.0% CPU · 11 MiB/s · 984.2 µs CPU/req)</sub> | **176**<br><sub>(72 MiB / 1.2% CPU · 11 MiB/s · 212.0 µs CPU/req)</sub> | 🥇 **184**<br><sub>(102 MiB / 2.3% CPU · 11 MiB/s · 369.1 µs CPU/req)</sub> | **152**<br><sub>(120 MiB / 1.9% CPU · 9.5 MiB/s · 377.7 µs CPU/req)</sub> | **184**<br><sub>(146 MiB / 6.2% CPU · 11 MiB/s · 1005.4 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | **163**<br><sub>(292 MiB / 4.6% CPU · 10 MiB/s · 844.5 µs CPU/req)</sub> | *Not possible* | 🥇 **176**<br><sub>(118 MiB / 1.9% CPU · 11 MiB/s · 326.1 µs CPU/req)</sub> | **149**<br><sub>(122 MiB / 2.6% CPU · 9.3 MiB/s · 525.2 µs CPU/req)</sub> | **168**<br><sub>(260 MiB / 3.9% CPU · 10 MiB/s · 700.3 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | **159**<br><sub>(278 MiB / 7.7% CPU · 10.0 MiB/s · 1457.6 µs CPU/req)</sub> | *Not possible* | 🥇 **182**<br><sub>(115 MiB / 4.3% CPU · 11 MiB/s · 704.6 µs CPU/req)</sub> | **152**<br><sub>(122 MiB / 3.8% CPU · 9.5 MiB/s · 743.6 µs CPU/req)</sub> | **168**<br><sub>(268 MiB / 7.8% CPU · 10 MiB/s · 1387.4 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | **786**<br><sub>(191 MiB / 20.2% CPU · 49 MiB/s · 772.1 µs CPU/req)</sub> | *Not possible* | **495**<br><sub>(130 MiB / 17.3% CPU · 31 MiB/s · 1046.3 µs CPU/req)</sub> | 🥇 **910**<br><sub>(128 MiB / 31.4% CPU · 57 MiB/s · 1034.0 µs CPU/req)</sub> | **895**<br><sub>(424 MiB / 28.1% CPU · 56 MiB/s · 941.2 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | **1,012**<br><sub>(201 MiB / 24.9% CPU · 63 MiB/s · 738.0 µs CPU/req)</sub> | 🥇 **1,052**<br><sub>(99 MiB / 18.8% CPU · 66 MiB/s · 535.8 µs CPU/req)</sub> | **535**<br><sub>(128 MiB / 18.4% CPU · 33 MiB/s · 1032.7 µs CPU/req)</sub> | **878**<br><sub>(131 MiB / 32.8% CPU · 55 MiB/s · 1121.1 µs CPU/req)</sub> | **940**<br><sub>(361 MiB / 31.7% CPU · 59 MiB/s · 1013.3 µs CPU/req)</sub> |

### Architecture-sensitive

These runs isolate slow app readers, origin-early response, HTTP/2 duplex, and WebSocket echo. See [TWP vs YARP IO model](Performance-Profiling#user-content-twp-vs-yarp-io-model). Laptop 1-rep numbers are on [Performance Local Lab](Performance-Local-Lab#user-content-architecture-sensitive).

Median of **3** repeats on matched 4 vCPU / 16 GiB runners @ `e0c98136` ([41268442564](https://github.com/justcoding121/titanium-web-proxy/actions/runs/41268442564)). Slow consumer = 256 KiB GET, 16 KiB read + 8 ms sleep. Early response = 64 KiB POST, origin writes after 8 KiB. Duplex HTTP/2 = overlapping 64 KiB POST on H2 TLS↔H2 TLS. WebSocket row = H1 Upgrade echo round-trips/sec (not RFC 8441; see WebSocket RFC 8441 table below).

Lossy-link runs (slow **network**) are already published above; they are not a slow **app** reader.

#### Windows


*Not possible:* **HAProxy** and **Envoy** columns are omitted (no official Windows port).

| Scenario | Client | Origin | TWP | nginx | YARP |
|---|---|---|---:|---:|---:|
| Slow consumer (256 KiB GET, throttled client read) | HTTP/1 · TLS | HTTP/1 · plain | **248**<br><sub>(94 MiB / 4.7% CPU · 62 MiB/s · 762.2 µs CPU/req)</sub> | **222**<br><sub>(145 MiB / 24.4% CPU · 55 MiB/s · 4409.9 µs CPU/req)</sub> | 🥇 **248**<br><sub>(111 MiB / 5.4% CPU · 62 MiB/s · 877.8 µs CPU/req)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · TLS | HTTP/1 · plain | **248**<br><sub>(143 MiB / 3.8% CPU · 62 MiB/s · 611.5 µs CPU/req)</sub> | **230**<br><sub>(144 MiB / 24.5% CPU · 58 MiB/s · 4253.5 µs CPU/req)</sub> | 🥇 **248**<br><sub>(121 MiB / 6.0% CPU · 62 MiB/s · 974.2 µs CPU/req)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **58**<br><sub>(111 MiB / 42.2% CPU · 14 MiB/s · 29204.2 µs CPU/req)</sub> | *Not possible (no QUIC)* | **57**<br><sub>(149 MiB / 30.2% CPU · 14 MiB/s · 21251.3 µs CPU/req)</sub> |
| Early response (origin writes after first request chunk) | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **7,867**<br><sub>(102 MiB / 43.0% CPU · 983 MiB/s · 218.5 µs CPU/req)</sub> | **499**<br><sub>(145 MiB / 24.8% CPU · 62 MiB/s · 1988.0 µs CPU/req)</sub> | **5,375**<br><sub>(139 MiB / 57.2% CPU · 672 MiB/s · 426.1 µs CPU/req)</sub> |
| Early response (origin writes after first request chunk) | HTTP/2 · TLS | HTTP/1 · plain | **3,362**<br><sub>(204 MiB / 53.5% CPU · 420 MiB/s · 636.2 µs CPU/req)</sub> | **367**<br><sub>(148 MiB / 24.9% CPU · 46 MiB/s · 2716.4 µs CPU/req)</sub> | 🥇 **4,116**<br><sub>(138 MiB / 49.0% CPU · 515 MiB/s · 476.5 µs CPU/req)</sub> |
| Early response (origin writes after first request chunk) | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **1,721**<br><sub>(158 MiB / 45.0% CPU · 215 MiB/s · 1046.7 µs CPU/req)</sub> | *Not possible (no QUIC)* | **1,053**<br><sub>(212 MiB / 50.2% CPU · 132 MiB/s · 1907.7 µs CPU/req)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · plain | HTTP/1 · plain | **248**<br><sub>(136 MiB / 3.8% CPU · 62 MiB/s · 608.8 µs CPU/req)</sub> | **248**<br><sub>(129 MiB / 8.8% CPU · 62 MiB/s · 1428.7 µs CPU/req)</sub> | 🥇 **251**<br><sub>(108 MiB / 3.7% CPU · 63 MiB/s · 595.1 µs CPU/req)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · TLS | HTTP/2 · TLS | **242**<br><sub>(167 MiB / 7.4% CPU · 60 MiB/s · 1218.5 µs CPU/req)</sub> | *Not possible* | 🥇 **248**<br><sub>(144 MiB / 9.9% CPU · 62 MiB/s · 1602.6 µs CPU/req)</sub> |
| Early response (origin writes after first request chunk) | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **3,338**<br><sub>(188 MiB / 41.5% CPU · 417 MiB/s · 497.0 µs CPU/req)</sub> | *Not possible* | **2,714**<br><sub>(163 MiB / 55.5% CPU · 339 MiB/s · 818.4 µs CPU/req)</sub> |
| Duplex (both directions live) | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **4,629**<br><sub>(195 MiB / 39.3% CPU · 579 MiB/s · 339.7 µs CPU/req)</sub> | *Not possible* | **3,967**<br><sub>(170 MiB / 56.5% CPU · 496 MiB/s · 569.7 µs CPU/req)</sub> |
| Duplex (WebSocket / H1 Upgrade) | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **37,158**<br><sub>(98 MiB / 41.5% CPU · 44.7 µs CPU/req)</sub> | **25,457**<br><sub>(145 MiB / 24.7% CPU · 38.9 µs CPU/req)</sub> | **34,106**<br><sub>(90 MiB / 43.6% CPU · 51.1 µs CPU/req)</sub> |

#### Linux


| Scenario | Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---|---:|---:|---:|---:|---:|
| Slow consumer (256 KiB GET, throttled client read) | HTTP/1 · TLS | HTTP/1 · plain | **466**<br><sub>(125 MiB / 9.4% CPU · 117 MiB/s · 805.8 µs CPU/req)</sub> | **468**<br><sub>(108 MiB / 14.7% CPU · 117 MiB/s · 1251.9 µs CPU/req)</sub> | 🥇 **473**<br><sub>(86 MiB / 5.7% CPU · 118 MiB/s · 484.0 µs CPU/req)</sub> | **468**<br><sub>(135 MiB / 7.5% CPU · 117 MiB/s · 636.8 µs CPU/req)</sub> | **468**<br><sub>(154 MiB / 13.8% CPU · 117 MiB/s · 1184.5 µs CPU/req)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **477**<br><sub>(169 MiB / 20.6% CPU · 119 MiB/s · 1729.6 µs CPU/req)</sub> | **476**<br><sub>(109 MiB / 19.5% CPU · 119 MiB/s · 1636.1 µs CPU/req)</sub> | **476**<br><sub>(85 MiB / 12.2% CPU · 119 MiB/s · 1023.8 µs CPU/req)</sub> | **475**<br><sub>(141 MiB / 10.3% CPU · 119 MiB/s · 867.0 µs CPU/req)</sub> | **474**<br><sub>(160 MiB / 22.6% CPU · 119 MiB/s · 1904.3 µs CPU/req)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/3 · QUIC | HTTP/1 · plain | **458**<br><sub>(151 MiB / 47.7% CPU · 115 MiB/s · 4161.4 µs CPU/req)</sub> | **466**<br><sub>(121 MiB / 26.0% CPU · 116 MiB/s · 2234.9 µs CPU/req)</sub> | 🥇 **474**<br><sub>(93 MiB / 12.9% CPU · 118 MiB/s · 1086.3 µs CPU/req)</sub> | **470**<br><sub>(146 MiB / 25.3% CPU · 118 MiB/s · 2151.4 µs CPU/req)</sub> | **456**<br><sub>(204 MiB / 50.7% CPU · 114 MiB/s · 4450.3 µs CPU/req)</sub> |
| Early response (origin writes after first request chunk) | HTTP/1 · TLS | HTTP/1 · plain | **9,612**<br><sub>(136 MiB / 41.9% CPU · 1,202 MiB/s · 174.5 µs CPU/req)</sub> | **7,367**<br><sub>(110 MiB / 50.5% CPU · 921 MiB/s · 274.0 µs CPU/req)</sub> | 🥇 **10,755**<br><sub>(91 MiB / 42.9% CPU · 1,344 MiB/s · 159.4 µs CPU/req)</sub> | **0**<br><sub>(peak 64 · 125 MiB / 1.9% CPU)</sub> | **5,967**<br><sub>(179 MiB / 55.7% CPU · 746 MiB/s · 373.3 µs CPU/req)</sub> |
| Early response (origin writes after first request chunk) | HTTP/2 · TLS | HTTP/1 · plain | **4,514**<br><sub>(265 MiB / 52.3% CPU · 564 MiB/s · 463.5 µs CPU/req)</sub> | **5,040**<br><sub>(117 MiB / 44.8% CPU · 630 MiB/s · 355.2 µs CPU/req)</sub> | 🥇 **6,035**<br><sub>(91 MiB / 43.5% CPU · 754 MiB/s · 288.3 µs CPU/req)</sub> | **0**<br><sub>(peak 5,936 · 137 MiB / 44.1% CPU)</sub> | **5,230**<br><sub>(178 MiB / 50.2% CPU · 654 MiB/s · 383.6 µs CPU/req)</sub> |
| Early response (origin writes after first request chunk) | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **4,104**<br><sub>(262 MiB / 45.1% CPU · 513 MiB/s · 439.6 µs CPU/req)</sub> | **1,686**<br><sub>(125 MiB / 40.3% CPU · 211 MiB/s · 955.6 µs CPU/req)</sub> | **3,548**<br><sub>(97 MiB / 40.9% CPU · 443 MiB/s · 460.7 µs CPU/req)</sub> | **0**<br><sub>(126 MiB / 0.2% CPU)</sub> | **2,851**<br><sub>(289 MiB / 46.8% CPU · 356 MiB/s · 657.0 µs CPU/req)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · plain | HTTP/1 · plain | **475**<br><sub>(155 MiB / 13.7% CPU · 119 MiB/s · 1155.6 µs CPU/req)</sub> | **480**<br><sub>(81 MiB / 13.7% CPU · 120 MiB/s · 1141.9 µs CPU/req)</sub> | 🥇 **480**<br><sub>(70 MiB / 6.6% CPU · 120 MiB/s · 549.3 µs CPU/req)</sub> | **477**<br><sub>(129 MiB / 7.1% CPU · 119 MiB/s · 594.1 µs CPU/req)</sub> | **475**<br><sub>(147 MiB / 13.8% CPU · 119 MiB/s · 1166.3 µs CPU/req)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · TLS | HTTP/2 · TLS | **476**<br><sub>(213 MiB / 13.2% CPU · 119 MiB/s · 1110.8 µs CPU/req)</sub> | *Not possible* | **478**<br><sub>(97 MiB / 9.2% CPU · 120 MiB/s · 769.7 µs CPU/req)</sub> | 🥇 **480**<br><sub>(135 MiB / 8.7% CPU · 120 MiB/s · 722.0 µs CPU/req)</sub> | **480**<br><sub>(184 MiB / 15.8% CPU · 120 MiB/s · 1312.5 µs CPU/req)</sub> |
| Early response (origin writes after first request chunk) | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **2,990**<br><sub>(220 MiB / 40.5% CPU · 374 MiB/s · 541.6 µs CPU/req)</sub> | *Not possible* | **2,130**<br><sub>(94 MiB / 36.0% CPU · 266 MiB/s · 675.3 µs CPU/req)</sub> | **0**<br><sub>(peak 2,403 · 138 MiB / 36.1% CPU)</sub> | **2,097**<br><sub>(203 MiB / 54.8% CPU · 262 MiB/s · 1045.7 µs CPU/req)</sub> |
| Duplex (both directions live) | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **5,468**<br><sub>(244 MiB / 37.1% CPU · 684 MiB/s · 271.7 µs CPU/req)</sub> | *Not possible* | **3,447**<br><sub>(93 MiB / 26.1% CPU · 431 MiB/s · 302.4 µs CPU/req)</sub> | **0**<br><sub>(peak 3,891 · 137 MiB / 30.0% CPU)</sub> | **3,918**<br><sub>(218 MiB / 56.6% CPU · 490 MiB/s · 577.7 µs CPU/req)</sub> |
| Duplex (WebSocket / H1 Upgrade) | HTTP/1 · TLS | HTTP/1 · plain | **33,315**<br><sub>(134 MiB / 41.9% CPU · 50.3 µs CPU/req)</sub> | 🥇 **38,089**<br><sub>(109 MiB / 37.1% CPU · 39.0 µs CPU/req)</sub> | **35,744**<br><sub>(84 MiB / 40.8% CPU · 45.7 µs CPU/req)</sub> | **33,964**<br><sub>(122 MiB / 41.6% CPU · 49.0 µs CPU/req)</sub> | **30,921**<br><sub>(130 MiB / 43.7% CPU · 56.5 µs CPU/req)</sub> |

#### macOS

Median of **3** repeats on `macos-15` (Apple Silicon M1, 3-core / 7 GB) @ `e0c98136`. Source: Actions [41268442564](https://github.com/justcoding121/titanium-web-proxy/actions/runs/41268442564) (`compare-arch`).

| Scenario | Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---|---:|---:|---:|---:|---:|
| Slow consumer (256 KiB GET, throttled client read) | HTTP/1 · TLS | HTTP/1 · plain | **105**<br><sub>(248 MiB / 4.1% CPU · 26 MiB/s · 1177.7 µs CPU/req)</sub> | 🥇 **120**<br><sub>(84 MiB / 2.3% CPU · 30 MiB/s · 575.0 µs CPU/req)</sub> | **104**<br><sub>(112 MiB / 1.3% CPU · 26 MiB/s · 381.5 µs CPU/req)</sub> | **105**<br><sub>(145 MiB / 2.4% CPU · 26 MiB/s · 673.4 µs CPU/req)</sub> | **112**<br><sub>(217 MiB / 4.5% CPU · 28 MiB/s · 1204.8 µs CPU/req)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · TLS | HTTP/1 · plain | **112**<br><sub>(380 MiB / 7.7% CPU · 28 MiB/s · 2064.5 µs CPU/req)</sub> | 🥇 **129**<br><sub>(85 MiB / 3.7% CPU · 32 MiB/s · 858.1 µs CPU/req)</sub> | **112**<br><sub>(114 MiB / 3.6% CPU · 28 MiB/s · 976.7 µs CPU/req)</sub> | **90**<br><sub>(153 MiB / 2.9% CPU · 22 MiB/s · 976.6 µs CPU/req)</sub> | **111**<br><sub>(226 MiB / 6.6% CPU · 28 MiB/s · 1788.2 µs CPU/req)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/3 · QUIC | HTTP/1 · plain | **150**<br><sub>(150 MiB / 20.8% CPU · 38 MiB/s · 4153.2 µs CPU/req)</sub> | **129**<br><sub>(95 MiB / 7.3% CPU · 32 MiB/s · 1698.4 µs CPU/req)</sub> | **129**<br><sub>(111 MiB / 12.2% CPU · 32 MiB/s · 2846.5 µs CPU/req)</sub> | **104**<br><sub>(145 MiB / 13.7% CPU · 26 MiB/s · 3941.3 µs CPU/req)</sub> | 🥇 **156**<br><sub>(327 MiB / 21.5% CPU · 39 MiB/s · 4149.8 µs CPU/req)</sub> |
| Early response (origin writes after first request chunk) | HTTP/1 · TLS | HTTP/1 · plain | **2,654**<br><sub>(247 MiB / 26.4% CPU · 332 MiB/s · 298.4 µs CPU/req)</sub> | **0**<br><sub>(peak 3,018 · 89 MiB / 19.3% CPU)</sub> | 🥇 **3,542**<br><sub>(118 MiB / 26.1% CPU · 443 MiB/s · 220.9 µs CPU/req)</sub> | **0**<br><sub>(peak 3,966 · 129 MiB / 26.9% CPU)</sub> | **2,267**<br><sub>(199 MiB / 37.8% CPU · 283 MiB/s · 500.2 µs CPU/req)</sub> |
| Early response (origin writes after first request chunk) | HTTP/2 · TLS | HTTP/1 · plain | **2,767**<br><sub>(514 MiB / 39.0% CPU · 346 MiB/s · 422.9 µs CPU/req)</sub> | **2,814**<br><sub>(98 MiB / 22.1% CPU · 352 MiB/s · 235.9 µs CPU/req)</sub> | 🥇 **3,578**<br><sub>(125 MiB / 34.0% CPU · 447 MiB/s · 285.2 µs CPU/req)</sub> | **0**<br><sub>(peak 3,118 · 132 MiB / 33.0% CPU)</sub> | **2,911**<br><sub>(183 MiB / 42.8% CPU · 364 MiB/s · 440.9 µs CPU/req)</sub> |
| Early response (origin writes after first request chunk) | HTTP/3 · QUIC | HTTP/1 · plain | **728**<br><sub>(277 MiB / 24.5% CPU · 91 MiB/s · 1011.5 µs CPU/req)</sub> | 🥇 **1,132**<br><sub>(99 MiB / 22.2% CPU · 142 MiB/s · 587.9 µs CPU/req)</sub> | **562**<br><sub>(108 MiB / 21.9% CPU · 70 MiB/s · 1168.4 µs CPU/req)</sub> | **919**<br><sub>(132 MiB / 42.1% CPU · 115 MiB/s · 1375.0 µs CPU/req)</sub> | **756**<br><sub>(428 MiB / 30.4% CPU · 94 MiB/s · 1206.4 µs CPU/req)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · plain | HTTP/1 · plain | **111**<br><sub>(200 MiB / 4.6% CPU · 28 MiB/s · 1246.9 µs CPU/req)</sub> | 🥇 **116**<br><sub>(72 MiB / 1.9% CPU · 29 MiB/s · 488.0 µs CPU/req)</sub> | **104**<br><sub>(100 MiB / 2.1% CPU · 26 MiB/s · 605.2 µs CPU/req)</sub> | **80**<br><sub>(137 MiB / 2.0% CPU · 20 MiB/s · 738.8 µs CPU/req)</sub> | **112**<br><sub>(146 MiB / 4.3% CPU · 28 MiB/s · 1158.2 µs CPU/req)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · TLS | HTTP/2 · TLS | **103**<br><sub>(406 MiB / 5.3% CPU · 26 MiB/s · 1547.9 µs CPU/req)</sub> | *Not possible* | 🥇 **120**<br><sub>(123 MiB / 5.3% CPU · 30 MiB/s · 1338.3 µs CPU/req)</sub> | **94**<br><sub>(129 MiB / 3.1% CPU · 23 MiB/s · 994.7 µs CPU/req)</sub> | **116**<br><sub>(304 MiB / 7.6% CPU · 29 MiB/s · 1969.9 µs CPU/req)</sub> |
| Early response (origin writes after first request chunk) | HTTP/2 · TLS | HTTP/2 · TLS | **1,658**<br><sub>(465 MiB / 19.9% CPU · 207 MiB/s · 360.8 µs CPU/req)</sub> | *Not possible* | **1,347**<br><sub>(peak 1,445 · 115 MiB / 24.3% CPU · 168 MiB/s · 541.8 µs CPU/req)</sub> | **0**<br><sub>(peak 1,234 · 118 MiB / 18.4% CPU)</sub> | 🥇 **2,800**<br><sub>(310 MiB / 48.6% CPU · 350 MiB/s · 520.3 µs CPU/req)</sub> |
| Duplex (both directions live) | HTTP/2 · TLS | HTTP/2 · TLS | **1,603**<br><sub>(503 MiB / 25.6% CPU · 200 MiB/s · 478.9 µs CPU/req)</sub> | *Not possible* | **1,525**<br><sub>(119 MiB / 23.0% CPU · 191 MiB/s · 451.9 µs CPU/req)</sub> | **0**<br><sub>(peak 1,327 · 130 MiB / 22.2% CPU)</sub> | 🥇 **2,249**<br><sub>(297 MiB / 46.0% CPU · 281 MiB/s · 614.3 µs CPU/req)</sub> |
| Duplex (WebSocket / H1 Upgrade) | HTTP/1 · TLS | HTTP/1 · plain | **16,957**<br><sub>(156 MiB / 28.9% CPU · 51.1 µs CPU/req)</sub> | 🥇 **33,295**<br><sub>(86 MiB / 21.0% CPU · 19.0 µs CPU/req)</sub> | **31,244**<br><sub>(103 MiB / 27.9% CPU · 26.8 µs CPU/req)</sub> | **24,743**<br><sub>(121 MiB / 26.4% CPU · 32.0 µs CPU/req)</sub> | **23,046**<br><sub>(172 MiB / 31.2% CPU · 40.6 µs CPU/req)</sub> |

Slow consumer is sleep-bound; H1/H2/H3 sit in the same band. Prefer the ratio columns for early-response, duplex, and WebSocket rows.

### TLS termination cost (H1 TLS → cleartext origin)

Isolates keep-alive tiny GET vs **new connection per request** (handshake-dominated) vs keep-alive **256 KiB**. Product comparison uses RPS and end-to-end latency; TWP can also capture `ClientTlsTiming` when `TWP_RPS_CAPTURE_TLS=1` (child process) — nginx/YARP have no equivalent hook.

#### Windows

Median of **3** repeats on `windows-latest` @ `e0c98136`. Source: Actions [41268433919](https://github.com/justcoding121/titanium-web-proxy/actions/runs/41268433919). Absolute RPS on GHA swings hard; prefer **TWP÷YARP**.


*Not possible:* **HAProxy** and **Envoy** columns are omitted (no official Windows port).

| Workload | TWP | nginx | YARP |
|---|---:|---:|---:|
| Keep-alive · tiny GET | 🥇 **56,807**<br><sub>(88 MiB / 46.3% CPU · 32.6 µs CPU/req)</sub> | **26,571**<br><sub>(144 MiB / 24.5% CPU · 36.8 µs CPU/req)</sub> | **49,229**<br><sub>(104 MiB / 46.4% CPU · 37.7 µs CPU/req)</sub> |
| New-connection · tiny GET | 🥇 **904**<br><sub>(84 MiB)</sub> | **309**<br><sub>(142 MiB / 1.5% CPU · 193.9 µs CPU/req)</sub> | **899**<br><sub>(108 MiB / 0.5% CPU · 20.9 µs CPU/req)</sub> |
| Keep-alive · 256 KiB GET | 🥇 **3,281**<br><sub>(136 MiB / 40.2% CPU · 820 MiB/s · 490.3 µs CPU/req)</sub> | **223**<br><sub>(144 MiB / 24.8% CPU · 56 MiB/s · 4448.6 µs CPU/req)</sub> | **3,128**<br><sub>(140 MiB / 44.8% CPU · 782 MiB/s · 573.4 µs CPU/req)</sub> |

#### Linux

Median of **3** repeats @ `e0c98136`. Source: Actions [41268433919](https://github.com/justcoding121/titanium-web-proxy/actions/runs/41268433919).


| Workload | TWP | nginx | HAProxy | Envoy | YARP |
|---|---:|---:|---:|---:|---:|
| Keep-alive · tiny GET | **25,096**<br><sub>(113 MiB / 48.3% CPU · 76.9 µs CPU/req)</sub> | 🥇 **29,705**<br><sub>(109 MiB / 42.5% CPU · 57.2 µs CPU/req)</sub> | **28,704**<br><sub>(85 MiB / 45.7% CPU · 63.7 µs CPU/req)</sub> | **17,315**<br><sub>(121 MiB / 61.2% CPU · 141.3 µs CPU/req)</sub> | **22,230**<br><sub>(139 MiB / 50.5% CPU · 90.8 µs CPU/req)</sub> |
| New-connection · tiny GET | **2,310**<br><sub>(143 MiB / 0.2% CPU · 4.3 µs CPU/req)</sub> | 🥇 **2,360**<br><sub>(111 MiB)</sub> | **2,289**<br><sub>(85 MiB)</sub> | **2,128**<br><sub>(126 MiB / 0.2% CPU · 4.7 µs CPU/req)</sub> | **2,278**<br><sub>(143 MiB)</sub> |
| Keep-alive · 256 KiB GET | **3,922**<br><sub>(131 MiB / 30.5% CPU · 981 MiB/s · 311.0 µs CPU/req)</sub> | **2,504**<br><sub>(110 MiB / 48.9% CPU · 626 MiB/s · 780.3 µs CPU/req)</sub> | 🥇 **4,084**<br><sub>(87 MiB / 26.4% CPU · 1,021 MiB/s · 258.8 µs CPU/req)</sub> | **3,500**<br><sub>(141 MiB / 33.9% CPU · 875 MiB/s · 387.8 µs CPU/req)</sub> | **3,068**<br><sub>(174 MiB / 41.8% CPU · 767 MiB/s · 545.4 µs CPU/req)</sub> |

#### macOS

Median of **3** repeats on `macos-15` @ `e0c98136`. Source: Actions [41268433919](https://github.com/justcoding121/titanium-web-proxy/actions/runs/41268433919).

| Workload | TWP | nginx | HAProxy | Envoy | YARP |
|---|---:|---:|---:|---:|---:|
| Keep-alive · tiny GET | **34,359**<br><sub>(128 MiB / 39.0% CPU · 34.0 µs CPU/req)</sub> | **34,192**<br><sub>(85 MiB / 25.4% CPU · 22.3 µs CPU/req)</sub> | 🥇 **40,514**<br><sub>(137 MiB / 35.9% CPU · 26.6 µs CPU/req)</sub> | **27,848**<br><sub>(120 MiB / 46.2% CPU · 49.8 µs CPU/req)</sub> | **33,378**<br><sub>(157 MiB / 40.7% CPU · 36.6 µs CPU/req)</sub> |
| New-connection · tiny GET | **127**<br><sub>(111 MiB / 0.2% CPU · 37.8 µs CPU/req)</sub> | **610**<br><sub>(86 MiB)</sub> | **618**<br><sub>(106 MiB)</sub> | 🥇 **784**<br><sub>(130 MiB / 0.1% CPU · 2.3 µs CPU/req)</sub> | **114**<br><sub>(peak 118 · 165 MiB / 0.1% CPU · 23.7 µs CPU/req)</sub> |
| Keep-alive · 256 KiB GET | **1,597**<br><sub>(212 MiB / 21.5% CPU · 399 MiB/s · 404.3 µs CPU/req)</sub> | **1,824**<br><sub>(84 MiB / 19.5% CPU · 456 MiB/s · 321.4 µs CPU/req)</sub> | 🥇 **2,376**<br><sub>(116 MiB / 25.8% CPU · 594 MiB/s · 325.1 µs CPU/req)</sub> | **2,286**<br><sub>(151 MiB / 24.8% CPU · 572 MiB/s · 325.7 µs CPU/req)</sub> | **1,702**<br><sub>(181 MiB / 34.3% CPU · 425 MiB/s · 604.2 µs CPU/req)</sub> |

Prefer **TWP÷YARP** in the table. Absolute RPS on GHA swings hard. New-connection is Darwin SslStream-bound for TWP and YARP (handshake p99 SLO **500 ms** on macOS only — Win/Linux stay at **200 ms**).

## Unary gRPC (H2 TLS)

Unary Echo **RPC/s** @ c=64 over H2 TLS→H2 TLS for Titanium, YARP, nginx (`grpc_pass`), HAProxy, and Envoy (OS-possible peers only). Not folded into the architecture-sensitive tables. Median of **3** repeats @ `e0c98136` — [41268451568](https://github.com/justcoding121/titanium-web-proxy/actions/runs/41268451568).

| OS | Titanium | YARP | nginx | HAProxy | Envoy |
|---|---:|---:|---:|---:|---:|
| Windows | 🥇 **41,136**<br><sub>(128 MiB / 33.3% CPU · 32.4 µs CPU/req)</sub> | **26,603**<br><sub>(127 MiB / 45.2% CPU · 67.9 µs CPU/req)</sub> | **0**<br><sub>(145 MiB / 24.8% CPU)</sub> | *Not possible* | *Not possible* |
| Linux | 🥇 **33,602**<br><sub>(181 MiB / 32.9% CPU · 39.1 µs CPU/req)</sub> | **21,648**<br><sub>(159 MiB / 43.1% CPU · 79.6 µs CPU/req)</sub> | **0**<br><sub>(112 MiB / 45.3% CPU)</sub> | **15,644**<br><sub>(83 MiB / 46.5% CPU · 118.8 µs CPU/req)</sub> | **16,498**<br><sub>(121 MiB / 56.8% CPU · 137.7 µs CPU/req)</sub> |
| macOS | **21,551**<br><sub>(253 MiB / 25.7% CPU · 35.8 µs CPU/req)</sub> | 🥇 **37,618**<br><sub>(185 MiB / 38.1% CPU · 30.4 µs CPU/req)</sub> | **0**<br><sub>(85 MiB / 2.7% CPU)</sub> | **16,156**<br><sub>(113 MiB / 37.5% CPU · 69.6 µs CPU/req)</sub> | **18,786**<br><sub>(118 MiB / 42.9% CPU · 68.6 µs CPU/req)</sub> |

## Unary gRPC (H2 TLS → h2c)

Unary Echo **RPC/s** @ c=64 over **H2 TLS → h2c** (edge TLS, cleartext H2 origin). Peers: Titanium, YARP, HAProxy, Envoy. Mode: `compare-grpc` (`*-grpc-h2c`). Median of **3** repeats @ `e0c98136` — [41268451568](https://github.com/justcoding121/titanium-web-proxy/actions/runs/41268451568).

*Not possible:* **nginx** column omitted (no H2 upstream).

| OS | Titanium | YARP | HAProxy | Envoy |
|---|---:|---:|---:|---:|
| Windows | 🥇 **98,445**<br><sub>(128 MiB / 26.9% CPU · 10.9 µs CPU/req)</sub> | **62,604**<br><sub>(124 MiB / 49.8% CPU · 31.8 µs CPU/req)</sub> | *Not possible* | *Not possible* |
| Linux | 🥇 **43,852**<br><sub>(165 MiB / 28.8% CPU · 26.2 µs CPU/req)</sub> | **26,267**<br><sub>(162 MiB / 42.6% CPU · 64.9 µs CPU/req)</sub> | **27,118**<br><sub>(84 MiB / 41.2% CPU · 60.8 µs CPU/req)</sub> | **21,716**<br><sub>(121 MiB / 56.4% CPU · 103.9 µs CPU/req)</sub> |
| macOS | **17,858**<br><sub>(264 MiB / 19.4% CPU · 32.7 µs CPU/req)</sub> | **24,615**<br><sub>(190 MiB / 28.8% CPU · 35.1 µs CPU/req)</sub> | **22,552**<br><sub>(132 MiB / 33.0% CPU · 43.9 µs CPU/req)</sub> | 🥇 **32,626**<br><sub>(119 MiB / 43.7% CPU · 40.2 µs CPU/req)</sub> |

## WebSocket (H1 TLS → H1 TLS)

WebSocket echo round-trips/sec over **dual-TLS** H1 (`proxy_ssl` style). Mode: `compare-ws-h1tls` (`*-duplex-ws-h1tls`). Median of **3** repeats @ `e0c98136`. Source: [41268460863](https://github.com/justcoding121/titanium-web-proxy/actions/runs/41268460863).

| OS | Titanium | YARP | nginx | HAProxy | Envoy |
|---|---:|---:|---:|---:|---:|
| Windows | 🥇 **54,143**<br><sub>(102 MiB / 47.1% CPU · 34.8 µs CPU/req)</sub> | **50,894**<br><sub>(94 MiB / 45.0% CPU · 35.4 µs CPU/req)</sub> | **26,559**<br><sub>(144 MiB / 24.8% CPU · 37.3 µs CPU/req)</sub> | *Not possible* | *Not possible* |
| Linux | **41,770**<br><sub>(154 MiB / 43.2% CPU · 41.4 µs CPU/req)</sub> | **39,070**<br><sub>(142 MiB / 44.5% CPU · 45.5 µs CPU/req)</sub> | 🥇 **48,977**<br><sub>(115 MiB / 38.3% CPU · 31.3 µs CPU/req)</sub> | **45,161**<br><sub>(86 MiB / 41.6% CPU · 36.9 µs CPU/req)</sub> | **45,000**<br><sub>(122 MiB / 41.6% CPU · 37.0 µs CPU/req)</sub> |
| macOS | **20,195**<br><sub>(158 MiB / 26.8% CPU · 39.8 µs CPU/req)</sub> | **17,548**<br><sub>(175 MiB / 32.4% CPU · 55.3 µs CPU/req)</sub> | 🥇 **26,487**<br><sub>(84 MiB / 21.7% CPU · 24.6 µs CPU/req)</sub> | **22,289**<br><sub>(108 MiB / 30.4% CPU · 40.9 µs CPU/req)</sub> | **20,486**<br><sub>(118 MiB / 24.6% CPU · 36.0 µs CPU/req)</sub> |

## WebSocket (H2 TLS 8441 → H1)

WebSocket echo over **RFC 8441** extended CONNECT (H2 TLS client → H1 plain origin). Mode: `compare-ws-h2` (`*-duplex-ws-h2`).

The probe client sets `NoDelay` and writes each HTTP/2 frame as one TLS record. Median of **3** repeats @ `e0c98136`. Source: [41268469915](https://github.com/justcoding121/titanium-web-proxy/actions/runs/41268469915).

*Not possible:* **nginx** column omitted (no RFC 8441 extended CONNECT).

| OS | Titanium | YARP | HAProxy | Envoy |
|---|---:|---:|---:|---:|
| Windows | 🥇 **25,419**<br><sub>(170 MiB / 45.8% CPU · 72.1 µs CPU/req)</sub> | **0**<br><sub>(149 MiB / 19.8% CPU)</sub> | *Not possible* | *Not possible* |
| Linux | 🥇 **47,626**<br><sub>(190 MiB / 37.3% CPU · 31.3 µs CPU/req)</sub> | **0**<br><sub>(284 MiB / 45.7% CPU)</sub> | **47,626**<br><sub>(85 MiB / 37.6% CPU · 31.6 µs CPU/req)</sub> | **0**<br><sub>(118 MiB / 0.2% CPU)</sub> |
| macOS | 🥇 **26,979**<br><sub>(400 MiB / 30.6% CPU · 34.0 µs CPU/req)</sub> | **0**<br><sub>(199 MiB / 7.8% CPU)</sub> | **24,949**<br><sub>(110 MiB / 25.1% CPU · 30.2 µs CPU/req)</sub> | **0**<br><sub>(114 MiB / 0.3% CPU)</sub> |

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

`--arm-shard` takes a comparison-group key (`h1c-h1c`, from `--print-groups`), not an `i/n` index, so a re-run measures the same row after a peer install flake. `i/n` remains for smoke modes. Paste unions `rps-csv-<os>-shard-*` (`paste-compare-product-wiki.ps1 -RunIds …`). Table “Source: Actions” may list several run URLs for one table — **do not mix SHAs**. A single-row leg allows **60** minutes of ramp and **75** minutes overall. Free GitHub accounts run **20** jobs at once, **5** of them macOS. Give a full suite a quiet window: do not push pull requests or start other macOS jobs while it runs. Re-run infrastructure failures only (`gh run rerun --failed`). A failed gate is a finding and is never re-rolled; publish the last attempt, never the best of several. The RPS tables on this page are @ `e0c98136`: product [41268390260](https://github.com/justcoding121/titanium-web-proxy/actions/runs/41268390260), saturation [41268398087](https://github.com/justcoding121/titanium-web-proxy/actions/runs/41268398087), bodies [41268406222](https://github.com/justcoding121/titanium-web-proxy/actions/runs/41268406222), POST [41268415633](https://github.com/justcoding121/titanium-web-proxy/actions/runs/41268415633), lossy [41268425096](https://github.com/justcoding121/titanium-web-proxy/actions/runs/41268425096), arch [41268442564](https://github.com/justcoding121/titanium-web-proxy/actions/runs/41268442564), TLS cost [41268433919](https://github.com/justcoding121/titanium-web-proxy/actions/runs/41268433919), gRPC [41268451568](https://github.com/justcoding121/titanium-web-proxy/actions/runs/41268451568), WebSocket H1 TLS [41268460863](https://github.com/justcoding121/titanium-web-proxy/actions/runs/41268460863), WebSocket H2 [41268469915](https://github.com/justcoding121/titanium-web-proxy/actions/runs/41268469915), editions [41268478603](https://github.com/justcoding121/titanium-web-proxy/actions/runs/41268478603). All eleven suite aggregates were green. The prior refresh @ `8567b4c0` published a bodies gate miss (Windows H3→H2 TLS 256 KiB **0.389×** closest peer vs floor **0.40**). Older notes on the `88187363` cluster and paired re-measures remain historical only. Reverse product rows, bodies, POST, lossy, arch, TLS cost, gRPC, both WebSocket modes, and saturation run on Windows, Linux, and macOS. TWP MITM arms run on Linux only. `compare-editions` (CLI and Plus) runs on Linux only. `compare-spot` on pull requests to beta and stable stays on all three OS. The historical v2 MITM note below the product tables stays @ `41f4adee` because those run ids are from that fix, not this re-measure.

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
