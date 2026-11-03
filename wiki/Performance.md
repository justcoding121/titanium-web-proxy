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
- **Typical reverse** is H1 TLS→H1 or H2→H1 (~1.1× YARP on Win/Linux tiny GET). With **larger bodies**, see [Heavier reverse](#user-content-heavier-reverse-workloads) (@ `4038c6b1`).

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

On the Linux H2 TLS→H1 plain row at c=64 (16 connections, @ `4038c6b1`), proxy CPU was about **44%** for nginx, **45%** for HAProxy, and **55%** for Envoy. Windows nginx on the same H2 client shape stayed near **25%** (one core). That measured shape is the one published here.

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

Calibration for the shared 4 vCPU loopback shape: how close client + origin are to saturated before ranking reverse peers. Tiny keep-alive GET. Median of **3** repeats @ `4038c6b1` — [41648522721](https://github.com/justcoding121/titanium-web-proxy/actions/runs/41648522721). Warmup 2s / measure 8s; concurrency 8, 16, 32, 64. Block A **% of origin-HttpClient** uses median **peak** RPS. Blocks B/C use peer÷YARP / ÷nginx on median peak (not % of H1 origin). **RPS cells** embed median RSS / CPU for the **proxy child** plus its **full descendant tree** (serve-proxy → nginx master → workers); origin-direct samples the **origin** child. Product matrices below use matched `dotnet-httpclient` only (not bombardier).


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
| origin-direct | dotnet-httpclient | **50,798**<br><sub>(55 MiB / 41.6% CPU · 32.7 µs CPU/req)</sub> | **100.0%** |
| origin-direct-bombardier | bombardier | **35,782**<br><sub>(56 MiB / 23.0% CPU · 25.7 µs CPU/req)</sub> | **70.4%** |
| bare-reverse-http1 | dotnet-httpclient | **25,573**<br><sub>(56 MiB / 42.2% CPU · 66.0 µs CPU/req)</sub> | **50.3%** |
| nginx-reverse-http1 | dotnet-httpclient | **13,338**<br><sub>(127 MiB / 24.6% CPU · 73.7 µs CPU/req)</sub> | **26.3%** |
| yarp-reverse-http1 | dotnet-httpclient | **21,324**<br><sub>(89 MiB / 49.9% CPU · 93.7 µs CPU/req)</sub> | **42.0%** |
| twp-reverse-http1 | dotnet-httpclient | 🥇 **25,461**<br><sub>(79 MiB / 49.1% CPU · 77.2 µs CPU/req)</sub> | **50.1%** |

**Linux** (`ubuntu-latest`)

| Arm | Generator | RPS | % of origin-HttpClient |
|---|---|---:|---:|
| origin-direct | dotnet-httpclient | **76,467**<br><sub>(82 MiB / 42.6% CPU · 22.3 µs CPU/req)</sub> | **100.0%** |
| origin-direct-bombardier | bombardier | **43,708**<br><sub>(82 MiB / 33.4% CPU · 30.5 µs CPU/req)</sub> | **57.2%** |
| bare-reverse-http1 | dotnet-httpclient | **34,657**<br><sub>(69 MiB / 44.7% CPU · 51.6 µs CPU/req)</sub> | **45.3%** |
| nginx-reverse-http1 | dotnet-httpclient | 🥇 **41,054**<br><sub>(79 MiB / 42.6% CPU · 41.5 µs CPU/req)</sub> | **53.7%** |
| yarp-reverse-http1 | dotnet-httpclient | **30,225**<br><sub>(116 MiB / 50.0% CPU · 66.1 µs CPU/req)</sub> | **39.5%** |
| twp-reverse-http1 | dotnet-httpclient | **33,833**<br><sub>(85 MiB / 47.8% CPU · 56.5 µs CPU/req)</sub> | **44.2%** |


**macOS** (`macos-15`, Apple Silicon)

| Arm | Generator | RPS | % of origin-HttpClient |
|---|---|---:|---:|
| origin-direct | dotnet-httpclient | **101,565**<br><sub>(89 MiB / 35.8% CPU · 14.1 µs CPU/req)</sub> | **100.0%** |
| origin-direct-bombardier | bombardier | **76,762**<br><sub>(89 MiB / 25.4% CPU · 13.3 µs CPU/req)</sub> | **75.6%** |
| bare-reverse-http1 | dotnet-httpclient | **43,617**<br><sub>(93 MiB / 36.1% CPU · 33.1 µs CPU/req)</sub> | **42.9%** |
| nginx-reverse-http1 | dotnet-httpclient | 🥇 **46,438**<br><sub>(67 MiB / 24.7% CPU · 21.3 µs CPU/req)</sub> | **45.7%** |
| yarp-reverse-http1 | dotnet-httpclient | **38,873**<br><sub>(150 MiB / 40.9% CPU · 42.1 µs CPU/req)</sub> | **38.3%** |
| twp-reverse-http1 | dotnet-httpclient | **40,592**<br><sub>(116 MiB / 37.7% CPU · 37.2 µs CPU/req)</sub> | **40.0%** |

On this macOS run `origin-direct` was **35.8%** CPU, so the % of origin-HttpClient column is not a ceiling when that CPU is near idle. Rank those Mac peers by RPS.

Reverse peers on Block A @ `4038c6b1`: Windows TWP is **50.1%** of origin-direct and Linux TWP is **44.2%**. Prefer the **%** column over absolute RPS across runs. Bare and origin-direct are controls (not medal peers).

#### Block B — H2 TLS→H1

Peer ratios (÷YARP / ÷nginx) on median peak; **RPS cells** embed `(MiB / CPU%)`.

**Windows** (`windows-latest`)

| Arm | Generator | RPS | ÷YARP | ÷nginx |
|---|---|---:|---:|---:|
| nginx-reverse-http2 | dotnet-httpclient | **7,810**<br><sub>(144 MiB / 24.7% CPU · 126.7 µs CPU/req)</sub> | **0.29×** | **1.00×** |
| yarp-reverse-http2 | dotnet-httpclient | **27,360**<br><sub>(102 MiB / 53.3% CPU · 78.0 µs CPU/req)</sub> | **1.00×** | **3.50×** |
| twp-reverse-http2-cleartext | dotnet-httpclient | 🥇 **30,094**<br><sub>(130 MiB / 52.2% CPU · 69.4 µs CPU/req)</sub> | **1.10×** | **3.85×** |

**Linux** (`ubuntu-latest`)

| Arm | Generator | RPS | ÷YARP | ÷nginx |
|---|---|---:|---:|---:|
| nginx-reverse-http2 | dotnet-httpclient | **27,580**<br><sub>(110 MiB / 44.9% CPU · 65.1 µs CPU/req)</sub> | **0.92×** | **1.00×** |
| yarp-reverse-http2 | dotnet-httpclient | **29,881**<br><sub>(134 MiB / 50.0% CPU · 67.0 µs CPU/req)</sub> | **1.00×** | **1.08×** |
| twp-reverse-http2-cleartext | dotnet-httpclient | 🥇 **32,720**<br><sub>(150 MiB / 51.2% CPU · 62.6 µs CPU/req)</sub> | **1.10×** | **1.19×** |

**macOS** (`macos-15`, Apple Silicon)

| Arm | Generator | RPS | ÷YARP | ÷nginx |
|---|---|---:|---:|---:|
| nginx-reverse-http2 | dotnet-httpclient | **36,695**<br><sub>(83 MiB / 26.5% CPU · 28.9 µs CPU/req)</sub> | **0.80×** | **1.00×** |
| yarp-reverse-http2 | dotnet-httpclient | **45,978**<br><sub>(156 MiB / 42.8% CPU · 37.2 µs CPU/req)</sub> | **1.00×** | **1.25×** |
| twp-reverse-http2-cleartext | dotnet-httpclient | 🥇 **49,834**<br><sub>(184 MiB / 42.5% CPU · 34.1 µs CPU/req)</sub> | **1.08×** | **1.36×** |
#### Block C — H3→H1

Same layout as Block B. Requires QuicListener. nginx needs `http_v3_module` (Windows nginx has no QUIC). HAProxy needs `USE_QUIC` (GHA Linux/macOS require it). Envoy 1.20+ includes HTTP/3.

Medals are nginx / YARP / Titanium only, from the same run as Blocks A and B ([40610501155](https://github.com/justcoding121/titanium-web-proxy/actions/runs/40610501155)).

**Windows** (`windows-latest`)

| Arm | Generator | RPS | ÷YARP | ÷nginx |
|---|---|---:|---:|---:|
| nginx-reverse-http3-cleartext | dotnet-httpclient | *Not possible (no QUIC)* | — | — |
| yarp-reverse-http3-cleartext | dotnet-httpclient | **7,906**<br><sub>(154 MiB / 48.7% CPU · 246.5 µs CPU/req)</sub> | **1.00×** | — |
| twp-reverse-http3-cleartext | dotnet-httpclient | 🥇 **9,040**<br><sub>(108 MiB / 48.9% CPU · 216.1 µs CPU/req)</sub> | **1.14×** | — |

**Linux** (`ubuntu-latest`)

| Arm | Generator | RPS | ÷YARP | ÷nginx |
|---|---|---:|---:|---:|
| nginx-reverse-http3-cleartext | dotnet-httpclient | **17,360**<br><sub>(111 MiB / 31.0% CPU · 71.5 µs CPU/req)</sub> | **0.94×** | **1.00×** |
| yarp-reverse-http3-cleartext | dotnet-httpclient | **14,624**<br><sub>(201 MiB / 51.1% CPU · 139.7 µs CPU/req)</sub> | **1.00×** | **1.07×** |
| twp-reverse-http3-cleartext | dotnet-httpclient | 🥇 **19,302**<br><sub>(151 MiB / 51.7% CPU · 107.1 µs CPU/req)</sub> | **1.32×** | **1.41×** |

**macOS** (`macos-15`, Apple Silicon)

| Arm | Generator | RPS | ÷YARP | ÷nginx |
|---|---|---:|---:|---:|
| nginx-reverse-http3-cleartext | dotnet-httpclient | 🥇 **24,150**<br><sub>(peak 28,041 · 85 MiB / 21.4% CPU · 35.5 µs CPU/req)</sub> | **1.30×** | **1.00×** |
| yarp-reverse-http3-cleartext | dotnet-httpclient | **21,491**<br><sub>(203 MiB / 43.9% CPU · 81.7 µs CPU/req)</sub> | **1.00×** | **0.77×** |
| twp-reverse-http3-cleartext | dotnet-httpclient | **19,648**<br><sub>(143 MiB / 46.2% CPU · 94.2 µs CPU/req)</sub> | **0.91×** | **0.70×** |

## Windows — Titanium vs nginx vs HAProxy vs Envoy vs YARP

Client / origin: HTTP version and whether TLS is used (`plain` = cleartext, `TLS` = encrypted, `QUIC` = HTTP/3).

### Reverse

Median of **3 repeats** on `windows-latest` (4 vCPU / 16 GiB). Bare reverse 5×5 @ `4038c6b1` — `compare-product` [41648502269](https://github.com/justcoding121/titanium-web-proxy/actions/runs/41648502269). Warmup 2s / measure 8s; concurrency 8, 16, 32, 64. Prefer TWP÷peer ratios over absolute RPS. **RPS cells** include median RSS / CPU at the peak-RPS step as `<br><sub>(MiB / CPU%)</sub>`. nginx terminate peers use `keepalive 256` + streaming buffers. **HAProxy / Envoy are Linux-only peers** (no official Windows port). Laptop High-perf / cool-paired numbers stay on the [local lab](Performance-Local-Lab). Product 5×5 is **~56-byte JSON keep-alive GET**; H2/H3 same-protocol cells are mostly header work with a tiny body (Titanium best case) — see [Why this comparison is fair](#user-content-why-this-comparison-is-fair).

**Load generators:** Reverse inbound H3 arms use **`dotnet-httpclient`** (`http_version=3.0`, `RequestVersionExact`). nginx/Windows is same-OS only (no QUIC). HAProxy/Envoy are Linux-only terminate peers.

| Client | Origin | TWP | nginx | YARP |
|---|---|---:|---:|---:|
| HTTP/1 · plain | HTTP/1 · plain | 🥇 **25,282**<br><sub>(78 MiB / 49.7% CPU · 78.6 µs CPU/req)</sub> | **13,744**<br><sub>(127 MiB / 24.7% CPU · 71.9 µs CPU/req)</sub> | **21,222**<br><sub>(88 MiB / 50.4% CPU · 95.0 µs CPU/req)</sub> |
| HTTP/1 · plain | HTTP/1 · TLS | 🥇 **26,934**<br><sub>(91 MiB / 50% CPU · 74.2 µs CPU/req)</sub> | **11,902**<br><sub>(138 MiB / 24.8% CPU · 83.2 µs CPU/req)</sub> | **23,958**<br><sub>(96 MiB / 49.9% CPU · 83.3 µs CPU/req)</sub> |
| HTTP/1 · plain | HTTP/2 · plain | 🥇 **60,146**<br><sub>(119 MiB / 50.4% CPU · 33.5 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **52,174**<br><sub>(95 MiB / 48.4% CPU · 37.1 µs CPU/req)</sub> |
| HTTP/1 · plain | HTTP/2 · TLS | 🥇 **39,268**<br><sub>(125 MiB / 48.4% CPU · 49.3 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **36,015**<br><sub>(100 MiB / 47.5% CPU · 52.7 µs CPU/req)</sub> |
| HTTP/1 · plain | HTTP/3 · QUIC | 🥇 **16,258**<br><sub>(108 MiB / 50.7% CPU · 124.7 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | **15,583**<br><sub>(117 MiB / 49.8% CPU · 127.9 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/1 · plain | 🥇 **20,179**<br><sub>(90 MiB / 49.4% CPU · 98.0 µs CPU/req)</sub> | **8,462**<br><sub>(144 MiB / 24.6% CPU · 116.1 µs CPU/req)</sub> | **17,674**<br><sub>(110 MiB / 48.6% CPU · 109.9 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/1 · TLS | 🥇 **19,439**<br><sub>(88 MiB / 46.2% CPU · 95.0 µs CPU/req)</sub> | **6,988**<br><sub>(145 MiB / 24.8% CPU · 141.7 µs CPU/req)</sub> | **17,442**<br><sub>(105 MiB / 49% CPU · 112.3 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/2 · plain | 🥇 **29,523**<br><sub>(112 MiB / 45% CPU · 60.9 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **28,076**<br><sub>(115 MiB / 47.7% CPU · 67.9 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/2 · TLS | 🥇 **24,970**<br><sub>(121 MiB / 46.5% CPU · 74.5 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **24,262**<br><sub>(112 MiB / 47.1% CPU · 77.7 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/3 · QUIC | 🥇 **15,814**<br><sub>(122 MiB / 48.8% CPU · 123.4 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | **15,266**<br><sub>(125 MiB / 48.9% CPU · 128.1 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | 🥇 **32,534**<br><sub>(123 MiB / 51.3% CPU · 63.1 µs CPU/req)</sub> | **8,735**<br><sub>(129 MiB / 24.8% CPU · 113.7 µs CPU/req)</sub> | **29,181**<br><sub>(97 MiB / 53.6% CPU · 73.4 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/1 · TLS | 🥇 **39,396**<br><sub>(126 MiB / 49.2% CPU · 50.0 µs CPU/req)</sub> | **11,013**<br><sub>(139 MiB / 24.8% CPU · 89.9 µs CPU/req)</sub> | **38,026**<br><sub>(103 MiB / 52.3% CPU · 55.0 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/2 · plain | 🥇 **69,401**<br><sub>(90 MiB / 33.4% CPU · 19.3 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **51,270**<br><sub>(100 MiB / 50.1% CPU · 39.1 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/2 · TLS | 🥇 **60,253**<br><sub>(118 MiB / 35.6% CPU · 23.6 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **44,482**<br><sub>(105 MiB / 49.9% CPU · 44.9 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/3 · QUIC | 🥇 **25,723**<br><sub>(144 MiB / 50.4% CPU · 78.3 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | **23,905**<br><sub>(122 MiB / 51.4% CPU · 86.0 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **30,010**<br><sub>(128 MiB / 54.8% CPU · 73.0 µs CPU/req)</sub> | **7,565**<br><sub>(144 MiB / 24.8% CPU · 130.9 µs CPU/req)</sub> | **26,650**<br><sub>(106 MiB / 50.6% CPU · 76.0 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/1 · TLS | 🥇 **25,320**<br><sub>(132 MiB / 52.5% CPU · 83.0 µs CPU/req)</sub> | **6,018**<br><sub>(146 MiB / 24.9% CPU · 165.6 µs CPU/req)</sub> | **24,097**<br><sub>(107 MiB / 52.5% CPU · 87.2 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | 🥇 **80,292**<br><sub>(120 MiB / 36.4% CPU · 18.2 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **57,102**<br><sub>(111 MiB / 50.2% CPU · 35.2 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **54,224**<br><sub>(114 MiB / 36.4% CPU · 26.8 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **39,992**<br><sub>(109 MiB / 45.2% CPU · 45.2 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/3 · QUIC | 🥇 **22,620**<br><sub>(157 MiB / 51.6% CPU · 91.3 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | **21,299**<br><sub>(131 MiB / 54.9% CPU · 103.2 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **14,056**<br><sub>(118 MiB / 47.1% CPU · 134.0 µs CPU/req)</sub> | *Not possible (no QUIC)* | **13,722**<br><sub>(162 MiB / 50% CPU · 145.8 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **8,304**<br><sub>(115 MiB / 49.1% CPU · 236.4 µs CPU/req)</sub> | *Not possible (no QUIC)* | **7,426**<br><sub>(159 MiB / 52% CPU · 280.3 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/2 · plain | 🥇 **12,696**<br><sub>(117 MiB / 48.5% CPU · 152.9 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **10,808**<br><sub>(160 MiB / 50.2% CPU · 185.9 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | 🥇 **14,028**<br><sub>(123 MiB / 48.1% CPU · 137.2 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **12,780**<br><sub>(166 MiB / 49.4% CPU · 154.5 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/3 · QUIC | 🥇 **13,670**<br><sub>(120 MiB / 45.6% CPU · 133.3 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | **12,368**<br><sub>(171 MiB / 51% CPU · 164.9 µs CPU/req)</sub> |

## Linux — Titanium vs nginx vs HAProxy vs Envoy vs YARP

### Reverse

Median of **3 repeats** on `ubuntu-latest` (4 vCPU / 16 GiB). Bare reverse 5×5 @ `4038c6b1` — `compare-product` [41648502269](https://github.com/justcoding121/titanium-web-proxy/actions/runs/41648502269). Warmup 2s / measure 8s; concurrency 8, 16, 32, 64. **Linux nginx is the authoritative nginx baseline.** HAProxy (3.2 `USE_QUIC`) and Envoy (GitHub release, HTTP/3 compiled in) run on the same loopback shape as nginx/YARP. nginx terminate peers use `keepalive 256` + streaming buffers. The RPS workflow installs nginx.org mainline (`http_v3_module`), a QUIC-enabled HAProxy, Envoy, and `libmsquic`. Prefer ratios over absolute RPS. Product 5×5 is **~56-byte JSON keep-alive GET**; H2/H3 same-protocol cells are mostly header work with a tiny body (Titanium best case) — see [Why this comparison is fair](#user-content-why-this-comparison-is-fair).

| Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---:|---:|---:|---:|---:|
| HTTP/1 · plain | HTTP/1 · plain | **33,819**<br><sub>(87 MiB / 47.9% CPU · 56.6 µs CPU/req)</sub> | 🥇 **42,177**<br><sub>(79 MiB / 41.1% CPU · 38.9 µs CPU/req)</sub> | **36,174**<br><sub>(65 MiB / 45.4% CPU · 50.2 µs CPU/req)</sub> | **20,009**<br><sub>(110 MiB / 65% CPU · 129.9 µs CPU/req)</sub> | **30,167**<br><sub>(118 MiB / 49.5% CPU · 65.7 µs CPU/req)</sub> |
| HTTP/1 · plain | HTTP/1 · TLS | **56,194**<br><sub>(110 MiB / 45.9% CPU · 32.7 µs CPU/req)</sub> | 🥇 **65,456**<br><sub>(102 MiB / 41.2% CPU · 25.2 µs CPU/req)</sub> | **59,848**<br><sub>(70 MiB / 43.4% CPU · 29.0 µs CPU/req)</sub> | **48,170**<br><sub>(112 MiB / 52.9% CPU · 43.9 µs CPU/req)</sub> | **52,620**<br><sub>(135 MiB / 46.7% CPU · 35.5 µs CPU/req)</sub> |
| HTTP/1 · plain | HTTP/2 · plain | 🥇 **41,853**<br><sub>(128 MiB / 49.6% CPU · 47.4 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **26,182**<br><sub>(66 MiB / 45.2% CPU · 69.1 µs CPU/req)</sub> | **21,922**<br><sub>(110 MiB / 64% CPU · 116.8 µs CPU/req)</sub> | **38,646**<br><sub>(126 MiB / 49% CPU · 50.7 µs CPU/req)</sub> |
| HTTP/1 · plain | HTTP/2 · TLS | 🥇 **77,784**<br><sub>(171 MiB / 49.7% CPU · 25.5 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **69,931**<br><sub>(70 MiB / 44.8% CPU · 25.6 µs CPU/req)</sub> | **60,723**<br><sub>(111 MiB / 54.6% CPU · 36.0 µs CPU/req)</sub> | **74,713**<br><sub>(133 MiB / 45.8% CPU · 24.5 µs CPU/req)</sub> |
| HTTP/1 · plain | HTTP/3 · QUIC | 🥇 **23,848**<br><sub>(145 MiB / 53.3% CPU · 89.4 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **10,745**<br><sub>(114 MiB / 58.4% CPU · 217.5 µs CPU/req)</sub> | **23,343**<br><sub>(157 MiB / 48.8% CPU · 83.7 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/1 · plain | **57,024**<br><sub>(116 MiB / 46.9% CPU · 32.9 µs CPU/req)</sub> | 🥇 **66,310**<br><sub>(110 MiB / 42.2% CPU · 25.5 µs CPU/req)</sub> | **61,608**<br><sub>(85 MiB / 44.3% CPU · 28.8 µs CPU/req)</sub> | **47,962**<br><sub>(122 MiB / 54.4% CPU · 45.4 µs CPU/req)</sub> | **53,437**<br><sub>(137 MiB / 47.9% CPU · 35.9 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/1 · TLS | **37,382**<br><sub>(125 MiB / 43.6% CPU · 46.6 µs CPU/req)</sub> | 🥇 **50,959**<br><sub>(116 MiB / 38.5% CPU · 30.2 µs CPU/req)</sub> | **46,381**<br><sub>(87 MiB / 41.9% CPU · 36.1 µs CPU/req)</sub> | **32,411**<br><sub>(122 MiB / 54.8% CPU · 67.6 µs CPU/req)</sub> | **35,050**<br><sub>(145 MiB / 48.2% CPU · 55.0 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/2 · plain | 🥇 **48,862**<br><sub>(149 MiB / 48.8% CPU · 39.9 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **40,178**<br><sub>(85 MiB / 41.2% CPU · 41.0 µs CPU/req)</sub> | **36,270**<br><sub>(122 MiB / 57% CPU · 62.9 µs CPU/req)</sub> | **46,228**<br><sub>(148 MiB / 49.8% CPU · 43.1 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/2 · TLS | 🥇 **26,329**<br><sub>(162 MiB / 47.3% CPU · 71.9 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **24,569**<br><sub>(85 MiB / 46.9% CPU · 76.3 µs CPU/req)</sub> | **17,675**<br><sub>(122 MiB / 59.5% CPU · 134.6 µs CPU/req)</sub> | **24,760**<br><sub>(150 MiB / 48.6% CPU · 78.4 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/3 · QUIC | 🥇 **40,908**<br><sub>(176 MiB / 52.6% CPU · 51.4 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **26,084**<br><sub>(125 MiB / 49.7% CPU · 76.2 µs CPU/req)</sub> | **39,504**<br><sub>(184 MiB / 47.8% CPU · 48.4 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | **52,226**<br><sub>(150 MiB / 48.4% CPU · 37.1 µs CPU/req)</sub> | **55,004**<br><sub>(81 MiB / 41.9% CPU · 30.5 µs CPU/req)</sub> | 🥇 **62,826**<br><sub>(65 MiB / 43.4% CPU · 27.6 µs CPU/req)</sub> | **36,359**<br><sub>(111 MiB / 59.6% CPU · 65.6 µs CPU/req)</sub> | **51,592**<br><sub>(125 MiB / 47.9% CPU · 37.2 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/1 · TLS | **28,631**<br><sub>(150 MiB / 50.3% CPU · 70.2 µs CPU/req)</sub> | **24,396**<br><sub>(105 MiB / 47.3% CPU · 77.6 µs CPU/req)</sub> | 🥇 **29,292**<br><sub>(70 MiB / 46.6% CPU · 63.6 µs CPU/req)</sub> | **18,755**<br><sub>(112 MiB / 62.8% CPU · 134.0 µs CPU/req)</sub> | **26,933**<br><sub>(135 MiB / 51% CPU · 75.8 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/2 · plain | 🥇 **112,842**<br><sub>(142 MiB / 35.5% CPU · 12.6 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **74,282**<br><sub>(66 MiB / 42.9% CPU · 23.1 µs CPU/req)</sub> | **45,492**<br><sub>(110 MiB / 56.2% CPU · 49.4 µs CPU/req)</sub> | **74,479**<br><sub>(138 MiB / 47.3% CPU · 25.4 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/2 · TLS | 🥇 **55,118**<br><sub>(149 MiB / 38.9% CPU · 28.2 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **30,250**<br><sub>(68 MiB / 46.9% CPU · 62.1 µs CPU/req)</sub> | **22,772**<br><sub>(111 MiB / 60.3% CPU · 106.0 µs CPU/req)</sub> | **39,059**<br><sub>(134 MiB / 46.9% CPU · 48.0 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/3 · QUIC | 🥇 **37,614**<br><sub>(189 MiB / 49.8% CPU · 53.0 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **15,300**<br><sub>(113 MiB / 55.8% CPU · 145.8 µs CPU/req)</sub> | **35,162**<br><sub>(163 MiB / 46.7% CPU · 53.1 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **64,226**<br><sub>(159 MiB / 50.7% CPU · 31.6 µs CPU/req)</sub> | **54,469**<br><sub>(111 MiB / 43.4% CPU · 31.8 µs CPU/req)</sub> | **63,119**<br><sub>(83 MiB / 45.4% CPU · 28.8 µs CPU/req)</sub> | **49,970**<br><sub>(122 MiB / 54.6% CPU · 43.7 µs CPU/req)</sub> | **61,748**<br><sub>(137 MiB / 47.5% CPU · 30.8 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/1 · TLS | **37,512**<br><sub>(157 MiB / 45.2% CPU · 48.2 µs CPU/req)</sub> | **41,618**<br><sub>(119 MiB / 38.4% CPU · 36.9 µs CPU/req)</sub> | 🥇 **45,693**<br><sub>(85 MiB / 41.4% CPU · 36.2 µs CPU/req)</sub> | **30,716**<br><sub>(122 MiB / 55.3% CPU · 72.1 µs CPU/req)</sub> | **35,951**<br><sub>(141 MiB / 46% CPU · 51.2 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | 🥇 **64,038**<br><sub>(165 MiB / 34% CPU · 21.3 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **42,942**<br><sub>(83 MiB / 44.4% CPU · 41.3 µs CPU/req)</sub> | **27,335**<br><sub>(121 MiB / 58.8% CPU · 86.0 µs CPU/req)</sub> | **41,695**<br><sub>(148 MiB / 46.4% CPU · 44.6 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **101,669**<br><sub>(169 MiB / 39.8% CPU · 15.7 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **60,808**<br><sub>(84 MiB / 45.9% CPU · 30.2 µs CPU/req)</sub> | **59,916**<br><sub>(121 MiB / 52.9% CPU · 35.3 µs CPU/req)</sub> | **73,873**<br><sub>(144 MiB / 45% CPU · 24.4 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/3 · QUIC | 🥇 **34,948**<br><sub>(198 MiB / 48.3% CPU · 55.3 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **22,779**<br><sub>(124 MiB / 52.6% CPU · 92.4 µs CPU/req)</sub> | **32,278**<br><sub>(182 MiB / 47.3% CPU · 58.7 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | **21,544**<br><sub>(156 MiB / 52.3% CPU · 97.1 µs CPU/req)</sub> | **24,903**<br><sub>(112 MiB / 31.1% CPU · 50.0 µs CPU/req)</sub> | 🥇 **28,616**<br><sub>(89 MiB / 35.7% CPU · 49.9 µs CPU/req)</sub> | **10,864**<br><sub>(128 MiB / 59.7% CPU · 219.9 µs CPU/req)</sub> | **18,070**<br><sub>(203 MiB / 50.3% CPU · 111.4 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | **15,640**<br><sub>(167 MiB / 52.8% CPU · 135.2 µs CPU/req)</sub> | **18,911**<br><sub>(120 MiB / 34.8% CPU · 73.5 µs CPU/req)</sub> | 🥇 **21,337**<br><sub>(90 MiB / 37.3% CPU · 69.9 µs CPU/req)</sub> | **8,465**<br><sub>(128 MiB / 59.8% CPU · 282.4 µs CPU/req)</sub> | **13,503**<br><sub>(212 MiB / 51.7% CPU · 153.1 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/2 · plain | **46,856**<br><sub>(196 MiB / 55.9% CPU · 47.7 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | 🥇 **53,445**<br><sub>(89 MiB / 35.5% CPU · 26.6 µs CPU/req)</sub> | **29,468**<br><sub>(128 MiB / 49% CPU · 66.5 µs CPU/req)</sub> | **40,759**<br><sub>(218 MiB / 48% CPU · 47.1 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | **23,410**<br><sub>(164 MiB / 52.1% CPU · 89.0 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | 🥇 **26,224**<br><sub>(89 MiB / 36.1% CPU · 55.1 µs CPU/req)</sub> | **11,665**<br><sub>(127 MiB / 57.8% CPU · 198.2 µs CPU/req)</sub> | **20,359**<br><sub>(209 MiB / 48.6% CPU · 95.6 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/3 · QUIC | 🥇 **27,039**<br><sub>(194 MiB / 49.5% CPU · 73.2 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **14,504**<br><sub>(128 MiB / 48.2% CPU · 132.8 µs CPU/req)</sub> | **25,133**<br><sub>(227 MiB / 46% CPU · 73.2 µs CPU/req)</sub> |

### MITM (TWP only)

Same Client×Origin wires with interception on (`compare-product` [41648502269](https://github.com/justcoding121/titanium-web-proxy/actions/runs/41648502269)). **Lite** = no-op handlers (unchanged-lite finish). **Full** = append-only header mutation (harness: one probe header each way; product: generic append-only relay via `MitmCompressedRelayHelper`). nginx/HAProxy/Envoy/YARP cannot MITM. **Lite÷Reverse** / **Full÷Reverse** vs bare reverse (**same job / comparison-group shard**). Completion gate: Lite ≥ **0.25×** and Full ≥ **0.25×** reverse sustain @ c=64 (median of 3 GHA runs); reverse TWP ÷ closest peer ≥ **0.40×**.

**v1 append-only relay (2026-08-27):** Pre-fix H2→H2 Full÷Reverse was **0.13–0.16×** ([32960766249](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32960766249)). Post-fix @ `df172718`: H2 plain→H2 plain Full **0.77–0.79×**, H3→H1 Full **0.91–0.93×**, all MITM arms ≥ **0.70×** on median of [33041445371](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33041445371), [33055267086](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33055267086), [33055272140](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33055272140).

**v2 drop-only + non-unique append (2026-08-27):** `MitmStaticRebuildHelper` rebuilds static HPACK/QPACK after 1–4 unique header drops; trailing non-unique appends stay on compressed relay. @ `41f4adee`: all MITM arms ≥ **0.70×** on GHA median ([33087088466](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087088466), [33087091622](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087091622), [33105885748](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33105885748) Linux; [33087085235](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087085235), [33087088466](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087088466), [33087091622](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087091622) Windows). H2 plain→H2 plain Full **0.77–0.79×** (Win) / **0.78×** (Lin).

| Client | Origin | Lite sustain | Full sustain | Lite÷Reverse | Full÷Reverse |
|---|---|---:|---:|---:|---:|
| HTTP/1 · plain | HTTP/1 · plain | **34,415**<br><sub>(94 MiB / 49% CPU · 56.9 µs CPU/req)</sub> | **33,793**<br><sub>(94 MiB / 48.1% CPU · 56.9 µs CPU/req)</sub> | **1.02×** | **1×** |
| HTTP/1 · plain | HTTP/1 · TLS | **57,309**<br><sub>(114 MiB / 46.3% CPU · 32.3 µs CPU/req)</sub> | **54,939**<br><sub>(114 MiB / 45.6% CPU · 33.2 µs CPU/req)</sub> | **1.02×** | **0.98×** |
| HTTP/1 · plain | HTTP/2 · plain | **40,679**<br><sub>(128 MiB / 51.4% CPU · 50.5 µs CPU/req)</sub> | **39,185**<br><sub>(133 MiB / 51.3% CPU · 52.3 µs CPU/req)</sub> | **0.97×** | **0.94×** |
| HTTP/1 · plain | HTTP/2 · TLS | **75,551**<br><sub>(161 MiB / 47.4% CPU · 25.1 µs CPU/req)</sub> | **74,437**<br><sub>(162 MiB / 47.9% CPU · 25.7 µs CPU/req)</sub> | **0.97×** | **0.96×** |
| HTTP/1 · plain | HTTP/3 · QUIC | **23,337**<br><sub>(145 MiB / 53.9% CPU · 92.4 µs CPU/req)</sub> | **22,952**<br><sub>(147 MiB / 52.2% CPU · 91.0 µs CPU/req)</sub> | **0.98×** | **0.96×** |
| HTTP/1 · TLS | HTTP/1 · plain | **56,464**<br><sub>(121 MiB / 47.7% CPU · 33.8 µs CPU/req)</sub> | **55,915**<br><sub>(121 MiB / 46.8% CPU · 33.5 µs CPU/req)</sub> | **0.99×** | **0.98×** |
| HTTP/1 · TLS | HTTP/1 · TLS | **38,358**<br><sub>(123 MiB / 43.9% CPU · 45.7 µs CPU/req)</sub> | **38,834**<br><sub>(125 MiB / 44.6% CPU · 45.9 µs CPU/req)</sub> | **1.03×** | **1.04×** |
| HTTP/1 · TLS | HTTP/2 · plain | **47,204**<br><sub>(153 MiB / 49.8% CPU · 42.2 µs CPU/req)</sub> | **45,396**<br><sub>(162 MiB / 50.3% CPU · 44.3 µs CPU/req)</sub> | **0.97×** | **0.93×** |
| HTTP/1 · TLS | HTTP/2 · TLS | **24,772**<br><sub>(165 MiB / 47.9% CPU · 77.3 µs CPU/req)</sub> | **24,366**<br><sub>(168 MiB / 48.9% CPU · 80.2 µs CPU/req)</sub> | **0.94×** | **0.93×** |
| HTTP/1 · TLS | HTTP/3 · QUIC | **41,098**<br><sub>(187 MiB / 55.3% CPU · 53.8 µs CPU/req)</sub> | **41,752**<br><sub>(184 MiB / 54.8% CPU · 52.5 µs CPU/req)</sub> | **1×** | **1.02×** |
| HTTP/2 · plain | HTTP/1 · plain | **50,448**<br><sub>(155 MiB / 49.8% CPU · 39.5 µs CPU/req)</sub> | **48,898**<br><sub>(153 MiB / 49.8% CPU · 40.7 µs CPU/req)</sub> | **0.97×** | **0.94×** |
| HTTP/2 · plain | HTTP/1 · TLS | **27,886**<br><sub>(167 MiB / 51.3% CPU · 73.6 µs CPU/req)</sub> | **26,274**<br><sub>(155 MiB / 50.6% CPU · 77.0 µs CPU/req)</sub> | **0.97×** | **0.92×** |
| HTTP/2 · plain | HTTP/2 · plain | **95,691**<br><sub>(156 MiB / 41% CPU · 17.1 µs CPU/req)</sub> | **89,375**<br><sub>(152 MiB / 40.6% CPU · 18.2 µs CPU/req)</sub> | **0.85×** | **0.79×** |
| HTTP/2 · plain | HTTP/2 · TLS | **47,094**<br><sub>(162 MiB / 43.1% CPU · 36.6 µs CPU/req)</sub> | **44,408**<br><sub>(171 MiB / 42.2% CPU · 38.0 µs CPU/req)</sub> | **0.85×** | **0.81×** |
| HTTP/2 · plain | HTTP/3 · QUIC | **36,234**<br><sub>(182 MiB / 50.6% CPU · 55.8 µs CPU/req)</sub> | **36,179**<br><sub>(186 MiB / 49.8% CPU · 55.1 µs CPU/req)</sub> | **0.96×** | **0.96×** |
| HTTP/2 · TLS | HTTP/1 · plain | **61,216**<br><sub>(160 MiB / 50.5% CPU · 33.0 µs CPU/req)</sub> | **61,581**<br><sub>(166 MiB / 50.9% CPU · 33.1 µs CPU/req)</sub> | **0.95×** | **0.96×** |
| HTTP/2 · TLS | HTTP/1 · TLS | **35,924**<br><sub>(166 MiB / 46.7% CPU · 52.0 µs CPU/req)</sub> | **34,702**<br><sub>(176 MiB / 46.2% CPU · 53.3 µs CPU/req)</sub> | **0.96×** | **0.93×** |
| HTTP/2 · TLS | HTTP/2 · plain | **54,324**<br><sub>(156 MiB / 39.4% CPU · 29.0 µs CPU/req)</sub> | **51,351**<br><sub>(172 MiB / 38.2% CPU · 29.8 µs CPU/req)</sub> | **0.85×** | **0.8×** |
| HTTP/2 · TLS | HTTP/2 · TLS | **91,744**<br><sub>(171 MiB / 43.8% CPU · 19.1 µs CPU/req)</sub> | **84,456**<br><sub>(189 MiB / 43.3% CPU · 20.5 µs CPU/req)</sub> | **0.9×** | **0.83×** |
| HTTP/2 · TLS | HTTP/3 · QUIC | **34,902**<br><sub>(200 MiB / 48.6% CPU · 55.7 µs CPU/req)</sub> | **33,578**<br><sub>(199 MiB / 48.6% CPU · 58.0 µs CPU/req)</sub> | **1×** | **0.96×** |
| HTTP/3 · QUIC | HTTP/1 · plain | **21,273**<br><sub>(155 MiB / 53.3% CPU · 100.3 µs CPU/req)</sub> | **20,373**<br><sub>(157 MiB / 53.5% CPU · 105.1 µs CPU/req)</sub> | **0.99×** | **0.95×** |
| HTTP/3 · QUIC | HTTP/1 · TLS | **15,821**<br><sub>(186 MiB / 53% CPU · 134.0 µs CPU/req)</sub> | **14,979**<br><sub>(175 MiB / 53.2% CPU · 142.2 µs CPU/req)</sub> | **1.01×** | **0.96×** |
| HTTP/3 · QUIC | HTTP/2 · plain | **46,165**<br><sub>(195 MiB / 56.3% CPU · 48.8 µs CPU/req)</sub> | **45,316**<br><sub>(200 MiB / 56.8% CPU · 50.1 µs CPU/req)</sub> | **0.99×** | **0.97×** |
| HTTP/3 · QUIC | HTTP/2 · TLS | **23,522**<br><sub>(166 MiB / 52.8% CPU · 89.8 µs CPU/req)</sub> | **21,336**<br><sub>(168 MiB / 53.2% CPU · 99.7 µs CPU/req)</sub> | **1×** | **0.91×** |
| HTTP/3 · QUIC | HTTP/3 · QUIC | **28,114**<br><sub>(189 MiB / 52.4% CPU · 74.5 µs CPU/req)</sub> | **29,724**<br><sub>(190 MiB / 53.1% CPU · 71.5 µs CPU/req)</sub> | **1.04×** | **1.1×** |

## macOS — Titanium vs nginx vs HAProxy vs Envoy vs YARP

### Reverse

Median of **3 repeats** on `macos-15` (Apple Silicon M1, 3-core / 7 GB). Bare reverse 5×5 @ `4038c6b1` — `compare-product` [41648502269](https://github.com/justcoding121/titanium-web-proxy/actions/runs/41648502269). Warmup 2s / measure 8s; concurrency 8, 16, 32, 64. Prefer TWP÷peer ratios over absolute RPS. **RPS cells** include median RSS / CPU at the peak-RPS step as `<br><sub>(MiB / CPU%)</sub>`. The RPS workflow installs Homebrew nginx (`http_v3_module`), Homebrew HAProxy with `USE_QUIC` (3.2 source fallback), Envoy (Homebrew bottle or pinned darwin-arm64 1.36.7), Homebrew `libmsquic` (+ `DYLD_*`), and YARP. Use the pinned `macos-15` label, not `macos-latest`. Product 5×5 is **~56-byte JSON keep-alive GET**; H2/H3 same-protocol cells are mostly header work with a tiny body (Titanium best case) — see [Why this comparison is fair](#user-content-why-this-comparison-is-fair).

| Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---:|---:|---:|---:|---:|
| HTTP/1 · plain | HTTP/1 · plain | **16,429**<br><sub>(115 MiB / 23.6% CPU · 43.0 µs CPU/req)</sub> | 🥇 **25,321**<br><sub>(68 MiB / 10.1% CPU · 11.9 µs CPU/req)</sub> | **24,160**<br><sub>(91 MiB / 23.2% CPU · 28.8 µs CPU/req)</sub> | **14,522**<br><sub>(110 MiB / 38.9% CPU · 80.4 µs CPU/req)</sub> | **17,881**<br><sub>(147 MiB / 26.2% CPU · 44.0 µs CPU/req)</sub> |
| HTTP/1 · plain | HTTP/1 · TLS | **8,118**<br><sub>(127 MiB / 21.6% CPU · 80.0 µs CPU/req)</sub> | **14,877**<br><sub>(80 MiB / 19.4% CPU · 39.2 µs CPU/req)</sub> | 🥇 **16,492**<br><sub>(99 MiB / 26.2% CPU · 47.6 µs CPU/req)</sub> | **10,992**<br><sub>(113 MiB / 31.6% CPU · 86.1 µs CPU/req)</sub> | **13,634**<br><sub>(162 MiB / 27.9% CPU · 61.4 µs CPU/req)</sub> |
| HTTP/1 · plain | HTTP/2 · plain | **13,884**<br><sub>(123 MiB / 15.3% CPU · 33.0 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **12,277**<br><sub>(98 MiB / 20.2% CPU · 49.2 µs CPU/req)</sub> | **21,867**<br><sub>(110 MiB / 40.2% CPU · 55.1 µs CPU/req)</sub> | 🥇 **23,519**<br><sub>(159 MiB / 23% CPU · 29.4 µs CPU/req)</sub> |
| HTTP/1 · plain | HTTP/2 · TLS | **6,205**<br><sub>(175 MiB / 10% CPU · 48.3 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | 🥇 **16,864**<br><sub>(130 MiB / 25.9% CPU · 46.1 µs CPU/req)</sub> | **12,229**<br><sub>(111 MiB / 32.6% CPU · 80.1 µs CPU/req)</sub> | **15,570**<br><sub>(165 MiB / 23.5% CPU · 45.3 µs CPU/req)</sub> |
| HTTP/1 · plain | HTTP/3 · QUIC | **6,135**<br><sub>(139 MiB / 15.2% CPU · 74.1 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | 🥇 **15,251**<br><sub>(115 MiB / 35.7% CPU · 70.3 µs CPU/req)</sub> | **10,000**<br><sub>(162 MiB / 21.9% CPU · 65.7 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/1 · plain | **10,400**<br><sub>(127 MiB / 22% CPU · 63.6 µs CPU/req)</sub> | **13,064**<br><sub>(84 MiB / 20% CPU · 45.9 µs CPU/req)</sub> | 🥇 **16,243**<br><sub>(109 MiB / 22.8% CPU · 42.1 µs CPU/req)</sub> | **8,883**<br><sub>(120 MiB / 32.2% CPU · 108.8 µs CPU/req)</sub> | **14,557**<br><sub>(159 MiB / 28.8% CPU · 59.4 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/1 · TLS | **13,920**<br><sub>(143 MiB / 28% CPU · 60.4 µs CPU/req)</sub> | 🥇 **16,053**<br><sub>(88 MiB / 17.4% CPU · 32.6 µs CPU/req)</sub> | **13,443**<br><sub>(110 MiB / 23.6% CPU · 52.7 µs CPU/req)</sub> | **4,684**<br><sub>(122 MiB / 20.1% CPU · 128.8 µs CPU/req)</sub> | **15,862**<br><sub>(184 MiB / 28.2% CPU · 53.3 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/2 · plain | **6,061**<br><sub>(175 MiB / 17% CPU · 84.1 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **11,989**<br><sub>(114 MiB / 26% CPU · 65.0 µs CPU/req)</sub> | 🥇 **16,809**<br><sub>(119 MiB / 34.9% CPU · 62.3 µs CPU/req)</sub> | **14,188**<br><sub>(183 MiB / 22.9% CPU · 48.5 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/2 · TLS | **14,633**<br><sub>(196 MiB / 26.7% CPU · 54.8 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **19,616**<br><sub>(117 MiB / 31% CPU · 47.4 µs CPU/req)</sub> | **17,413**<br><sub>(120 MiB / 37.3% CPU · 64.3 µs CPU/req)</sub> | 🥇 **21,925**<br><sub>(172 MiB / 29.5% CPU · 40.3 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/3 · QUIC | **4,515**<br><sub>(167 MiB / 19.2% CPU · 127.4 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **4,927**<br><sub>(124 MiB / 22.2% CPU · 135.0 µs CPU/req)</sub> | 🥇 **8,069**<br><sub>(173 MiB / 20.2% CPU · 75.0 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | **16,149**<br><sub>(191 MiB / 18.9% CPU · 35.1 µs CPU/req)</sub> | 🥇 **31,676**<br><sub>(69 MiB / 24.1% CPU · 22.8 µs CPU/req)</sub> | **22,052**<br><sub>(98 MiB / 26.1% CPU · 35.5 µs CPU/req)</sub> | **12,552**<br><sub>(110 MiB / 32.3% CPU · 77.2 µs CPU/req)</sub> | **20,220**<br><sub>(149 MiB / 30% CPU · 44.5 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/1 · TLS | **16,565**<br><sub>(202 MiB / 25.5% CPU · 46.1 µs CPU/req)</sub> | **18,652**<br><sub>(80 MiB / 21.6% CPU · 34.7 µs CPU/req)</sub> | 🥇 **21,943**<br><sub>(102 MiB / 28.8% CPU · 39.3 µs CPU/req)</sub> | **15,147**<br><sub>(112 MiB / 34.5% CPU · 68.3 µs CPU/req)</sub> | **15,559**<br><sub>(167 MiB / 29.8% CPU · 57.6 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/2 · plain | **49,371**<br><sub>(174 MiB / 19.6% CPU · 11.9 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **42,395**<br><sub>(157 MiB / 32.1% CPU · 22.7 µs CPU/req)</sub> | **39,525**<br><sub>(109 MiB / 30.9% CPU · 23.4 µs CPU/req)</sub> | 🥇 **54,971**<br><sub>(153 MiB / 31% CPU · 16.9 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/2 · TLS | **23,750**<br><sub>(256 MiB / 21.7% CPU · 27.4 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **22,535**<br><sub>(142 MiB / 28.9% CPU · 38.5 µs CPU/req)</sub> | **14,938**<br><sub>(111 MiB / 21.7% CPU · 43.5 µs CPU/req)</sub> | 🥇 **33,098**<br><sub>(168 MiB / 29% CPU · 26.3 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/3 · QUIC | 🥇 **8,388**<br><sub>(208 MiB / 24.6% CPU · 88.1 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **5,837**<br><sub>(113 MiB / 14.5% CPU · 74.3 µs CPU/req)</sub> | **7,672**<br><sub>(164 MiB / 13.9% CPU · 54.4 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | **16,593**<br><sub>(172 MiB / 27% CPU · 48.8 µs CPU/req)</sub> | **16,737**<br><sub>(83 MiB / 18.1% CPU · 32.5 µs CPU/req)</sub> | **17,449**<br><sub>(109 MiB / 24.7% CPU · 42.4 µs CPU/req)</sub> | **7,119**<br><sub>(120 MiB / 22.3% CPU · 93.8 µs CPU/req)</sub> | 🥇 **23,600**<br><sub>(161 MiB / 29.5% CPU · 37.5 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/1 · TLS | **17,328**<br><sub>(185 MiB / 32.5% CPU · 56.3 µs CPU/req)</sub> | **12,890**<br><sub>(88 MiB / 21.2% CPU · 49.2 µs CPU/req)</sub> | 🥇 **20,182**<br><sub>(119 MiB / 29.8% CPU · 44.3 µs CPU/req)</sub> | **7,700**<br><sub>(121 MiB / 19.8% CPU · 77.0 µs CPU/req)</sub> | **15,385**<br><sub>(166 MiB / 29.9% CPU · 58.3 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | **12,641**<br><sub>(266 MiB / 13.3% CPU · 31.5 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | 🥇 **22,838**<br><sub>(126 MiB / 30.8% CPU · 40.5 µs CPU/req)</sub> | **16,129**<br><sub>(119 MiB / 32.3% CPU · 60.1 µs CPU/req)</sub> | **6,709**<br><sub>(223 MiB / 8.7% CPU · 38.8 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | **10,955**<br><sub>(265 MiB / 9.6% CPU · 26.3 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **22,138**<br><sub>(133 MiB / 33.6% CPU · 45.5 µs CPU/req)</sub> | **23,222**<br><sub>(119 MiB / 36.1% CPU · 46.7 µs CPU/req)</sub> | 🥇 **32,138**<br><sub>(172 MiB / 22.8% CPU · 21.3 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/3 · QUIC | 🥇 **20,806**<br><sub>(204 MiB / 34.5% CPU · 49.8 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **6,966**<br><sub>(122 MiB / 27.8% CPU · 119.6 µs CPU/req)</sub> | **5,692**<br><sub>(170 MiB / 11.5% CPU · 60.5 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | **11,872**<br><sub>(144 MiB / 30.4% CPU · 76.8 µs CPU/req)</sub> | 🥇 **24,151**<br><sub>(87 MiB / 18.3% CPU · 22.7 µs CPU/req)</sub> | **14,871**<br><sub>(105 MiB / 23% CPU · 46.5 µs CPU/req)</sub> | **7,544**<br><sub>(125 MiB / 46.9% CPU · 186.6 µs CPU/req)</sub> | **10,593**<br><sub>(213 MiB / 32.7% CPU · 92.6 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | **4,090**<br><sub>(163 MiB / 25% CPU · 183.1 µs CPU/req)</sub> | *Not measured* | 🥇 **13,941**<br><sub>(108 MiB / 25.2% CPU · 54.1 µs CPU/req)</sub> | **4,781**<br><sub>(125 MiB / 41.5% CPU · 260.6 µs CPU/req)</sub> | **8,105**<br><sub>(272 MiB / 33.6% CPU · 124.5 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/2 · plain | **7,482**<br><sub>(142 MiB / 21.2% CPU · 85.2 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | 🥇 **11,964**<br><sub>(108 MiB / 21.7% CPU · 54.4 µs CPU/req)</sub> | **10,027**<br><sub>(122 MiB / 39.6% CPU · 118.4 µs CPU/req)</sub> | **10,317**<br><sub>(223 MiB / 34.5% CPU · 100.3 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | **9,482**<br><sub>(146 MiB / 26.6% CPU · 84.3 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | 🥇 **19,121**<br><sub>(107 MiB / 24.8% CPU · 38.9 µs CPU/req)</sub> | **7,789**<br><sub>(123 MiB / 41% CPU · 158.0 µs CPU/req)</sub> | **11,344**<br><sub>(255 MiB / 30.1% CPU · 79.6 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/3 · QUIC | 🥇 **4,764**<br><sub>(141 MiB / 21.7% CPU · 136.4 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **3,090**<br><sub>(121 MiB / 30.2% CPU · 293.2 µs CPU/req)</sub> | **2,356**<br><sub>(238 MiB / 14.1% CPU · 180.0 µs CPU/req)</sub> |

## Editions (CLI / Plus / Intercept)

**Note:** `twp-reverse-http1` and other library rows use Core with **probe-tuned** settings (no logging, no Via header, probe-warmed certs). Edition rows use `titanium run -c twp.yaml` **product defaults** — prefer the ÷baseline ratio column over absolute RPS. Inspector GUI is not spawnable in the harness; session-path overhead is `twp-cli-intercept-http1` (route `RequestHeaderSet` transform). Pre-origin Plus middleware (CIDR/WAF deny-list/JWT/rate-limit/cache) runs on H1 terminate-lite without `SessionEventArgs`; a cache hit skips the origin. WAF is split into two rows: `twp-cli-plus-waf-http1` is deny-list only (`waf.mode=off`, denyPaths that miss `/`); `twp-cli-plus-waf-screen-http1` turns screening on (`waf.mode=detect`, no denyPaths) so headers/cookies/small bodies force the session path. `twp-cli-extauth-http1` pays one allow subrequest per request. `twp-cli-plugin-dotnet-noop-http1` is route middleware whose .NET `PassthroughPlugin` only calls `next`. JWT caches successful bearer validations. CORS only adds response headers on the way out, so it stays on terminate-lite. Circuit breaker and idempotent retry have to see the request or the status code, so they stay on the session path and should land near the intercept row. Maintainer gate thresholds live under [Maintainer notes](#user-content-maintainer-notes).

Median of **3** repeats on `ubuntu-latest`. Linux rows in this paste were re-measured @ `4038c6b1` (Actions [41648708854](https://github.com/justcoding121/titanium-web-proxy/actions/runs/41648708854)). Every edition row in the table was re-measured in that run. Warmup 2s / measure 8s; concurrency 8–64; sustain = median peak RPS among SLO-pass steps @ **c=64**. **RPS cells** show sustain; `<sub>` holds peak (when higher) plus `(MiB / CPU%)`. The Gate column is that script's floor.

| Arm | Linux | Lin÷ | Gate |
|---|---:|---:|---|
| `twp-cli-reverse-http1` vs library | **52,310**<br><sub>(174 MiB / 47.9% CPU · 36.6 µs CPU/req)</sub> | **1.05×** | ≥ **0.50×** |
| `twp-cli-reverse-http1-tls` vs library TLS | **24,656**<br><sub>(214 MiB / 47.9% CPU · 77.7 µs CPU/req)</sub> | **1.02×** | ≥ **0.50×** |
| `twp-cli-reverse-http1-route` vs CLI | **40,303**<br><sub>(178 MiB / 48.3% CPU · 47.9 µs CPU/req)</sub> | **1.00×** | ≥ **0.50×** |
| `twp-cli-plus-base-http1` vs CLI | **72,047**<br><sub>(244 MiB / 45.3% CPU · 25.2 µs CPU/req)</sub> | **1.02×** | ≥ **0.50×** |
| `twp-cli-plus-cache-http1` (cold) vs CLI | **60,891**<br><sub>(180 MiB / 50.5% CPU · 33.2 µs CPU/req)</sub> | **0.91×** | ≥ **0.50×** |
| `twp-cli-intercept-http1` vs CLI | **52,222**<br><sub>(191 MiB / 48.6% CPU · 37.3 µs CPU/req)</sub> | **0.71×** | ≥ **0.50×** |
| `twp-cli-plus-waf-http1` vs CLI | **35,354**<br><sub>(241 MiB / 49.7% CPU · 56.2 µs CPU/req)</sub> | **0.96×** | ≥ **0.50×** |
| `twp-cli-plus-waf-screen-http1` vs CLI | **25,732**<br><sub>(250 MiB / 52.9% CPU · 82.2 µs CPU/req)</sub> | **0.69×** | ≥ **0.50×** |
| `twp-cli-plus-cidr-http1` vs CLI | **47,485**<br><sub>(189 MiB / 47.0% CPU · 39.6 µs CPU/req)</sub> | **0.99×** | ≥ **0.50×** |
| `twp-cli-plus-jwt-http1` vs CLI | **46,616**<br><sub>(205 MiB / 48.1% CPU · 41.2 µs CPU/req)</sub> | **0.91×** | ≥ **0.50×** |
| `twp-cli-plus-ratelimit-http1` vs CLI | **50,939**<br><sub>(181 MiB / 48.4% CPU · 38.0 µs CPU/req)</sub> | **0.98×** | ≥ **0.50×** |
| `twp-cli-plus-fleet-ratelimit-http1` vs CLI | **68,374**<br><sub>(185 MiB / 48.7% CPU · 28.5 µs CPU/req)</sub> | **0.95×** | ≥ **0.50×** |
| `twp-cli-plus-manager-agent-http1` vs CLI | **36,573**<br><sub>(184 MiB / 48.7% CPU · 53.3 µs CPU/req)</sub> | **0.99×** | ≥ **0.50×** |
| `twp-cli-plus-resilience-http1` vs CLI | **66,295**<br><sub>(185 MiB / 47.6% CPU · 28.7 µs CPU/req)</sub> | **1.04×** | ≥ **0.50×** |
| `twp-cli-plus-discovery-file-http1` vs CLI | **51,161**<br><sub>(185 MiB / 48.3% CPU · 37.8 µs CPU/req)</sub> | **1.00×** | ≥ **0.50×** |
| `twp-cli-plus-metrics-scrape-http1` vs CLI | **36,569**<br><sub>(182 MiB / 48.4% CPU · 52.9 µs CPU/req)</sub> | **1.03×** | ≥ **0.50×** |
| `twp-cli-plus-cache-hit-http1` vs cache cold | **118,028**<br><sub>(180 MiB / 48.4% CPU · 16.4 µs CPU/req)</sub> | **1.94×** | ≥ **0.50×** |
| `twp-cli-plus-cors-http1` vs CLI | **47,361**<br><sub>(181 MiB / 48.7% CPU · 41.1 µs CPU/req)</sub> | **0.93×** | ≥ **0.50×** |
| `twp-cli-plus-circuit-http1` vs CLI | **27,175**<br><sub>(185 MiB / 51.4% CPU · 75.6 µs CPU/req)</sub> | **0.74×** | ≥ **0.50×** |
| `twp-cli-plus-retry-http1` vs CLI | **40,668**<br><sub>(189 MiB / 50.0% CPU · 49.1 µs CPU/req)</sub> | **0.79×** | ≥ **0.50×** |
| `twp-cli-extauth-http1` vs CLI | **26,946**<br><sub>(192 MiB / 50.2% CPU · 74.5 µs CPU/req)</sub> | **0.52×** | ≥ **0.50×** |
| `twp-cli-static-http1` vs CLI | **120,936**<br><sub>(236 MiB / 49.6% CPU · 16.4 µs CPU/req)</sub> | **1.86×** | ≥ **0.50×** |
| `twp-cli-logging-http1` vs CLI | **40,436**<br><sub>(176 MiB / 48.4% CPU · 47.9 µs CPU/req)</sub> | **1.01×** | ≥ **0.50×** |
| `twp-cli-lb-leasttime-http1` vs route | **33,175**<br><sub>(211 MiB / 70.8% CPU · 85.3 µs CPU/req)</sub> | **0.92×** | ≥ **0.50×** |
| `twp-cli-dialect-twp-http1` vs CLI | **37,250**<br><sub>(171 MiB / 48.9% CPU · 52.5 µs CPU/req)</sub> | **1.01×** | ≥ **0.50×** |
| `twp-cli-script-lua-noop-http1` vs route | **35,503**<br><sub>(263 MiB / 60.1% CPU · 67.7 µs CPU/req)</sub> | **0.61×** | ≥ **0.50×** |
| `twp-cli-script-lua-header-http1` vs route | **26,315**<br><sub>(200 MiB / 61.7% CPU · 93.7 µs CPU/req)</sub> | **0.57×** | ≥ **0.50×** |
| `twp-cli-script-wasm-noop-http1` vs route | **24,145**<br><sub>(199 MiB / 54.9% CPU · 91.0 µs CPU/req)</sub> | **0.65×** | ≥ **0.50×** |
| `twp-cli-script-lua-slow-upstream-http1` vs slow upstream | **3,014**<br><sub>(224 MiB / 29.1% CPU · 386.0 µs CPU/req)</sub> | **0.98×** | ≥ **0.50×** |
| `twp-cli-script-1in50-http1` vs route | **45,938**<br><sub>(255 MiB / 50.6% CPU · 44.1 µs CPU/req)</sub> | **0.81×** | ≥ **0.50×** |
| `twp-cli-plugin-dotnet-noop-http1` vs route | **26,117**<br><sub>(192 MiB / 53.2% CPU · 81.4 µs CPU/req)</sub> | **0.71×** | ≥ **0.50×** |
| `twp-cli-geo-http1` vs route | **70,433**<br><sub>(178 MiB / 48.0% CPU · 27.3 µs CPU/req)</sub> | **1.01×** | ≥ **0.50×** |
| `twp-cli-compress-h2-h2` vs route | **35,548**<br><sub>(178 MiB / 48.9% CPU · 55.0 µs CPU/req)</sub> | **1.01×** | ≥ **0.50×** |
| `twp-cli-compress-h3` vs route | **35,276**<br><sub>(178 MiB / 48.2% CPU · 54.7 µs CPU/req)</sub> | **0.98×** | ≥ **0.50×** |
| `twp-cli-health-grpc-http1` vs route | **39,515**<br><sub>(180 MiB / 48.1% CPU · 48.7 µs CPU/req)</sub> | **0.98×** | ≥ **0.50×** |
| `twp-cli-zoneaware-http1` vs route | **39,807**<br><sub>(178 MiB / 48.3% CPU · 48.5 µs CPU/req)</sub> | **1.00×** | ≥ **0.50×** |
| `twp-cli-tcp-tlsterm` vs route | **36,576**<br><sub>(180 MiB / 49.1% CPU · 53.7 µs CPU/req)</sub> | **1.02×** | ≥ **0.50×** |
| `twp-ws-h3` vs route | **36,545**<br><sub>(178 MiB / 49.6% CPU · 54.3 µs CPU/req)</sub> | **1.02×** | ≥ **0.50×** |
| `twp-cli-compress-zstd-http1` vs route | **34,989**<br><sub>(180 MiB / 48.1% CPU · 55.0 µs CPU/req)</sub> | **1.00×** | ≥ **0.50×** |
| `twp-cli-overload-http1` vs route | **35,374**<br><sub>(177 MiB / 48.5% CPU · 54.8 µs CPU/req)</sub> | **0.99×** | ≥ **0.50×** |
| `twp-cli-hotrestart-http1` vs route | **36,432**<br><sub>(178 MiB / 48.8% CPU · 53.6 µs CPU/req)</sub> | **1.00×** | ≥ **0.50×** |

`validate-edition-gates.ps1` floors are **0.50×**. Each ÷ column uses the two arms from the same job. Circuit breaker and idempotent retry stay on the session path, so a ratio near intercept is expected. Laptop smoke ratios stay on [Performance Local Lab — Editions](Performance-Local-Lab#user-content-editions-cli--plus-stress).

## Heavier reverse workloads

Same runners and harness as the tiny-GET tables, but with larger bodies, POST, lossy links, TLS cost, and architecture-sensitive paths (slow consumer / early response / duplex). **PUT with the same body is the same proxy work as POST; DELETE with no body matches GET** — only POST is published. Bodies/POST/lossy stay **half-duplex**. Laptop numbers are on [Performance Local Lab](Performance-Local-Lab#user-content-architecture-sensitive). How maintainers refresh these tables (modes, shards, paste scripts) is under [Maintainer notes](#user-content-maintainer-notes). **Larger-body check:** 64 / 256 KiB H2 TLS→H2 TLS is where body copy dominates headers — ratios vs YARP are in the tables below (@ `4038c6b1`), unlike the tiny-GET H2↔H2 medals above. Those cells also show goodput (MiB/s) and µs CPU/req, and the medal on those rows follows goodput.

Lossy link = **userspace** delay/drop shim (not kernel `netem`): TCP gets per-buffer delay + occasional whole-connection stalls (honest head-of-line for multiplexed HTTP/2); UDP is **loss% only** (no per-datagram delay) + drops (QUIC / MsQuic-safe). Lossy tables publish HTTP/1, HTTP/2, and HTTP/3.

### Windows — heavier reverse GET (64 KiB / 256 KiB)

Median of **3** repeats on `windows-latest` @ `4038c6b1`. Source: Actions [41648543503](https://github.com/justcoding121/titanium-web-proxy/actions/runs/41648543503) (`compare-bodies`). Warmup 2s / measure 8s. **RPS cells** include `(MiB / CPU%)` footprints.

*Not possible:* **HAProxy** and **Envoy** columns are omitted (no official Windows port).























| Body | Client | Origin | TWP | nginx | YARP |
|---|---|---|---:|---:|---:|
| 64 KiB | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **9,005**<br><sub>(106 MiB / 47.5% CPU · 563 MiB/s · 211.1 µs CPU/req)</sub> | **625**<br><sub>(143 MiB / 24.8% CPU · 39 MiB/s · 1588.0 µs CPU/req)</sub> | **7,942**<br><sub>(134 MiB / 49.2% CPU · 496 MiB/s · 247.7 µs CPU/req)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **8,186**<br><sub>(202 MiB / 52.1% CPU · 512 MiB/s · 254.7 µs CPU/req)</sub> | **577**<br><sub>(144 MiB / 24.7% CPU · 36 MiB/s · 1713.2 µs CPU/req)</sub> | **7,875**<br><sub>(132 MiB / 54.9% CPU · 492 MiB/s · 279.0 µs CPU/req)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **5,238**<br><sub>(142 MiB / 44.1% CPU · 327 MiB/s · 337.1 µs CPU/req)</sub> | *Not possible (no QUIC)* | **2,575**<br><sub>(179 MiB / 48.2% CPU · 161 MiB/s · 749.4 µs CPU/req)</sub> |
| 256 KiB | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **3,066**<br><sub>(133 MiB / 45.4% CPU · 766 MiB/s · 592.6 µs CPU/req)</sub> | **185**<br><sub>(144 MiB / 24.8% CPU · 46 MiB/s · 5349.9 µs CPU/req)</sub> | **2,719**<br><sub>(141 MiB / 46.0% CPU · 680 MiB/s · 677.5 µs CPU/req)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/1 · plain | **2,380**<br><sub>(183 MiB / 51.3% CPU · 595 MiB/s · 862.1 µs CPU/req)</sub> | **153**<br><sub>(peak 154 · 144 MiB / 24.8% CPU · 38 MiB/s · 6478.4 µs CPU/req)</sub> | 🥇 **2,658**<br><sub>(130 MiB / 45.5% CPU · 664 MiB/s · 685.1 µs CPU/req)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **301**<br><sub>(117 MiB / 42.1% CPU · 75 MiB/s · 5606.1 µs CPU/req)</sub> | *Not possible (no QUIC)* | **158**<br><sub>(120 MiB / 50.5% CPU · 40 MiB/s · 12734.4 µs CPU/req)</sub> |
| 64 KiB | HTTP/2 · plain | HTTP/1 · plain | 🥇 **25,746**<br><sub>(197 MiB / 41.0% CPU · 1,609 MiB/s · 63.7 µs CPU/req)</sub> | **4,602**<br><sub>(129 MiB / 24.9% CPU · 288 MiB/s · 216.5 µs CPU/req)</sub> | **21,890**<br><sub>(106 MiB / 45.5% CPU · 1,368 MiB/s · 83.1 µs CPU/req)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/2 · plain | 🥇 **9,078**<br><sub>(182 MiB / 46.5% CPU · 567 MiB/s · 204.7 µs CPU/req)</sub> | *Not possible* | **8,218**<br><sub>(151 MiB / 52.3% CPU · 514 MiB/s · 254.4 µs CPU/req)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **6,150**<br><sub>(174 MiB / 41.5% CPU · 384 MiB/s · 269.9 µs CPU/req)</sub> | *Not possible* | **5,221**<br><sub>(138 MiB / 49.4% CPU · 326 MiB/s · 378.6 µs CPU/req)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/2 · TLS | 🥇 **3,198**<br><sub>(136 MiB / 42.6% CPU · 200 MiB/s · 533.3 µs CPU/req)</sub> | *Not possible* | **1,609**<br><sub>(179 MiB / 51.8% CPU · 101 MiB/s · 1287.9 µs CPU/req)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **3,815**<br><sub>(150 MiB / 43.8% CPU · 238 MiB/s · 459.7 µs CPU/req)</sub> | *Not possible (no QUIC)* | **2,258**<br><sub>(191 MiB / 52.6% CPU · 141 MiB/s · 931.2 µs CPU/req)</sub> |
| 256 KiB | HTTP/2 · plain | HTTP/1 · plain | 🥇 **4,240**<br><sub>(187 MiB / 37.9% CPU · 1,060 MiB/s · 357.9 µs CPU/req)</sub> | **666**<br><sub>(129 MiB / 24.5% CPU · 167 MiB/s · 1468.6 µs CPU/req)</sub> | **3,990**<br><sub>(114 MiB / 42.5% CPU · 997 MiB/s · 425.8 µs CPU/req)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/2 · plain | 🥇 **2,140**<br><sub>(217 MiB / 42.9% CPU · 535 MiB/s · 802.0 µs CPU/req)</sub> | *Not possible* | **2,089**<br><sub>(159 MiB / 45.4% CPU · 522 MiB/s · 869.0 µs CPU/req)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **2,739**<br><sub>(209 MiB / 42.8% CPU · 685 MiB/s · 624.7 µs CPU/req)</sub> | *Not possible* | **2,516**<br><sub>(158 MiB / 43.2% CPU · 629 MiB/s · 687.2 µs CPU/req)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/2 · TLS | **0**<br><sub>(peak 156 · 125 MiB / 49.2% CPU)</sub> | *Not possible* | 🥇 **306**<br><sub>(175 MiB / 50.5% CPU · 76 MiB/s · 6606.0 µs CPU/req)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/1 · TLS | **153**<br><sub>(91 MiB / 47.7% CPU · 38 MiB/s · 12511.1 µs CPU/req)</sub> | *Not possible (no QUIC)* | 🥇 **286**<br><sub>(190 MiB / 50.3% CPU · 71 MiB/s · 7039.2 µs CPU/req)</sub> |

nginx/Windows collapses on large reverse bodies in this harness; treat as same-OS only. Prefer the ratio columns; absolute RPS swings by VM.

### Linux — heavier reverse GET (64 KiB / 256 KiB)

Median of **3** repeats @ `4038c6b1`. Source: Actions [41648543503](https://github.com/justcoding121/titanium-web-proxy/actions/runs/41648543503) (`compare-bodies`). Warmup 2s / measure 8s.


| Body | Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---|---:|---:|---:|---:|---:|
| 64 KiB | HTTP/1 · TLS | HTTP/1 · plain | **8,260**<br><sub>(126 MiB / 42.6% CPU · 516 MiB/s · 206.4 µs CPU/req)</sub> | **5,791**<br><sub>(109 MiB / 52.8% CPU · 362 MiB/s · 364.4 µs CPU/req)</sub> | 🥇 **8,549**<br><sub>(89 MiB / 40.2% CPU · 534 MiB/s · 188.2 µs CPU/req)</sub> | **7,414**<br><sub>(128 MiB / 48.0% CPU · 463 MiB/s · 258.9 µs CPU/req)</sub> | **6,760**<br><sub>(168 MiB / 49.5% CPU · 422 MiB/s · 292.8 µs CPU/req)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/1 · plain | **9,932**<br><sub>(278 MiB / 41.6% CPU · 621 MiB/s · 167.5 µs CPU/req)</sub> | **8,865**<br><sub>(112 MiB / 43.6% CPU · 554 MiB/s · 196.9 µs CPU/req)</sub> | **10,110**<br><sub>(90 MiB / 43.2% CPU · 632 MiB/s · 170.8 µs CPU/req)</sub> | 🥇 **10,639**<br><sub>(129 MiB / 44.6% CPU · 665 MiB/s · 167.8 µs CPU/req)</sub> | **8,811**<br><sub>(170 MiB / 47.5% CPU · 551 MiB/s · 215.5 µs CPU/req)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **5,976**<br><sub>(203 MiB / 45.7% CPU · 373 MiB/s · 306.2 µs CPU/req)</sub> | **2,783**<br><sub>(121 MiB / 37.2% CPU · 174 MiB/s · 535.0 µs CPU/req)</sub> | **5,839**<br><sub>(97 MiB / 37.9% CPU · 365 MiB/s · 259.4 µs CPU/req)</sub> | **4,571**<br><sub>(136 MiB / 49.7% CPU · 286 MiB/s · 435.0 µs CPU/req)</sub> | **3,905**<br><sub>(239 MiB / 52.1% CPU · 244 MiB/s · 534.2 µs CPU/req)</sub> |
| 256 KiB | HTTP/1 · TLS | HTTP/1 · plain | **3,360**<br><sub>(134 MiB / 30.1% CPU · 840 MiB/s · 358.1 µs CPU/req)</sub> | **2,403**<br><sub>(110 MiB / 44.2% CPU · 601 MiB/s · 735.9 µs CPU/req)</sub> | 🥇 **3,496**<br><sub>(88 MiB / 28.1% CPU · 874 MiB/s · 321.6 µs CPU/req)</sub> | **2,960**<br><sub>(141 MiB / 36.8% CPU · 740 MiB/s · 497.3 µs CPU/req)</sub> | **2,525**<br><sub>(171 MiB / 43.6% CPU · 631 MiB/s · 691.6 µs CPU/req)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/1 · plain | **3,744**<br><sub>(208 MiB / 33.2% CPU · 936 MiB/s · 354.8 µs CPU/req)</sub> | **2,800**<br><sub>(111 MiB / 48.3% CPU · 700 MiB/s · 690.4 µs CPU/req)</sub> | **3,693**<br><sub>(89 MiB / 37.0% CPU · 923 MiB/s · 400.6 µs CPU/req)</sub> | 🥇 **4,026**<br><sub>(150 MiB / 33.6% CPU · 1,007 MiB/s · 334.0 µs CPU/req)</sub> | **3,348**<br><sub>(172 MiB / 39.3% CPU · 837 MiB/s · 469.3 µs CPU/req)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/1 · plain | **1,422**<br><sub>(180 MiB / 48.9% CPU · 356 MiB/s · 1374.4 µs CPU/req)</sub> | **719**<br><sub>(121 MiB / 37.8% CPU · 180 MiB/s · 2101.0 µs CPU/req)</sub> | 🥇 **1,653**<br><sub>(98 MiB / 36.0% CPU · 413 MiB/s · 870.1 µs CPU/req)</sub> | **1,420**<br><sub>(150 MiB / 45.1% CPU · 355 MiB/s · 1271.5 µs CPU/req)</sub> | **1,166**<br><sub>(242 MiB / 52.0% CPU · 291 MiB/s · 1784.3 µs CPU/req)</sub> |
| 64 KiB | HTTP/2 · plain | HTTP/1 · plain | 🥇 **15,372**<br><sub>(265 MiB / 41.4% CPU · 961 MiB/s · 107.7 µs CPU/req)</sub> | **9,424**<br><sub>(82 MiB / 51.5% CPU · 589 MiB/s · 218.7 µs CPU/req)</sub> | **14,664**<br><sub>(73 MiB / 45.5% CPU · 917 MiB/s · 124.1 µs CPU/req)</sub> | **14,306**<br><sub>(118 MiB / 49.4% CPU · 894 MiB/s · 138.1 µs CPU/req)</sub> | **14,767**<br><sub>(144 MiB / 44.5% CPU · 923 MiB/s · 120.5 µs CPU/req)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/2 · plain | 🥇 **7,242**<br><sub>(201 MiB / 38.7% CPU · 453 MiB/s · 213.9 µs CPU/req)</sub> | *Not possible* | **5,765**<br><sub>(92 MiB / 44.8% CPU · 360 MiB/s · 310.8 µs CPU/req)</sub> | **6,496**<br><sub>(131 MiB / 48.0% CPU · 406 MiB/s · 295.7 µs CPU/req)</sub> | **5,675**<br><sub>(204 MiB / 48.0% CPU · 355 MiB/s · 338.1 µs CPU/req)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/2 · TLS | **9,978**<br><sub>(218 MiB / 37.2% CPU · 624 MiB/s · 149.2 µs CPU/req)</sub> | *Not possible* | **9,070**<br><sub>(92 MiB / 47.7% CPU · 567 MiB/s · 210.4 µs CPU/req)</sub> | 🥇 **10,301**<br><sub>(138 MiB / 45.5% CPU · 644 MiB/s · 176.7 µs CPU/req)</sub> | **8,831**<br><sub>(214 MiB / 45.0% CPU · 552 MiB/s · 204.0 µs CPU/req)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/2 · TLS | **4,528**<br><sub>(184 MiB / 42.9% CPU · 283 MiB/s · 379.0 µs CPU/req)</sub> | *Not possible* | 🥇 **4,640**<br><sub>(98 MiB / 37.6% CPU · 290 MiB/s · 323.9 µs CPU/req)</sub> | **3,275**<br><sub>(142 MiB / 49.8% CPU · 205 MiB/s · 608.6 µs CPU/req)</sub> | **3,160**<br><sub>(247 MiB / 49.4% CPU · 198 MiB/s · 625.2 µs CPU/req)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/1 · TLS | **6,285**<br><sub>(224 MiB / 44.2% CPU · 393 MiB/s · 281.5 µs CPU/req)</sub> | **3,075**<br><sub>(128 MiB / 26.7% CPU · 192 MiB/s · 347.6 µs CPU/req)</sub> | 🥇 **6,981**<br><sub>(98 MiB / 37.9% CPU · 436 MiB/s · 217.1 µs CPU/req)</sub> | **5,283**<br><sub>(136 MiB / 49.6% CPU · 330 MiB/s · 375.8 µs CPU/req)</sub> | **4,488**<br><sub>(263 MiB / 51.1% CPU · 281 MiB/s · 455.2 µs CPU/req)</sub> |
| 256 KiB | HTTP/2 · plain | HTTP/1 · plain | **4,583**<br><sub>(204 MiB / 29.2% CPU · 1,146 MiB/s · 254.8 µs CPU/req)</sub> | **3,346**<br><sub>(81 MiB / 50.2% CPU · 836 MiB/s · 599.9 µs CPU/req)</sub> | **4,783**<br><sub>(72 MiB / 29.8% CPU · 1,196 MiB/s · 248.9 µs CPU/req)</sub> | 🥇 **4,907**<br><sub>(139 MiB / 30.0% CPU · 1,227 MiB/s · 244.6 µs CPU/req)</sub> | **4,443**<br><sub>(154 MiB / 33.1% CPU · 1,111 MiB/s · 298.3 µs CPU/req)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/2 · plain | **3,377**<br><sub>(233 MiB / 33.2% CPU · 844 MiB/s · 393.3 µs CPU/req)</sub> | *Not possible* | **3,289**<br><sub>(101 MiB / 34.7% CPU · 822 MiB/s · 421.6 µs CPU/req)</sub> | 🥇 **3,387**<br><sub>(144 MiB / 34.0% CPU · 847 MiB/s · 401.2 µs CPU/req)</sub> | **2,695**<br><sub>(210 MiB / 41.1% CPU · 674 MiB/s · 610.2 µs CPU/req)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **1,984**<br><sub>(254 MiB / 36.0% CPU · 496 MiB/s · 726.9 µs CPU/req)</sub> | *Not possible* | **1,900**<br><sub>(98 MiB / 39.3% CPU · 475 MiB/s · 826.9 µs CPU/req)</sub> | **1,981**<br><sub>(141 MiB / 38.6% CPU · 495 MiB/s · 780.5 µs CPU/req)</sub> | **1,554**<br><sub>(200 MiB / 42.1% CPU · 388 MiB/s · 1084.2 µs CPU/req)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/2 · TLS | **946**<br><sub>(208 MiB / 57.1% CPU · 237 MiB/s · 2413.4 µs CPU/req)</sub> | *Not possible* | 🥇 **1,696**<br><sub>(108 MiB / 38.3% CPU · 424 MiB/s · 904.0 µs CPU/req)</sub> | **1,326**<br><sub>(149 MiB / 46.1% CPU · 332 MiB/s · 1389.3 µs CPU/req)</sub> | **1,128**<br><sub>(245 MiB / 47.3% CPU · 282 MiB/s · 1676.4 µs CPU/req)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/1 · TLS | **1,945**<br><sub>(219 MiB / 45.7% CPU · 486 MiB/s · 939.3 µs CPU/req)</sub> | **1,008**<br><sub>(129 MiB / 33.7% CPU · 252 MiB/s · 1335.4 µs CPU/req)</sub> | 🥇 **2,284**<br><sub>(100 MiB / 37.1% CPU · 571 MiB/s · 649.2 µs CPU/req)</sub> | **2,010**<br><sub>(150 MiB / 42.5% CPU · 503 MiB/s · 846.7 µs CPU/req)</sub> | **1,622**<br><sub>(265 MiB / 47.3% CPU · 406 MiB/s · 1166.1 µs CPU/req)</sub> |

Prefer the ratio columns; absolute RPS swings by VM.

### macOS — heavier reverse GET (64 KiB / 256 KiB)

Median of **3** repeats on `macos-15` (Apple Silicon M1, 3-core / 7 GB) @ `4038c6b1`. Source: Actions [41648543503](https://github.com/justcoding121/titanium-web-proxy/actions/runs/41648543503) (`compare-bodies`). Warmup 2s / measure 8s.

| Body | Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---|---:|---:|---:|---:|---:|
| 64 KiB | HTTP/1 · TLS | HTTP/1 · plain | **3,904**<br><sub>(175 MiB / 24.5% CPU · 244 MiB/s · 188.1 µs CPU/req)</sub> | **4,349**<br><sub>(84 MiB / 18.8% CPU · 272 MiB/s · 129.8 µs CPU/req)</sub> | 🥇 **5,934**<br><sub>(120 MiB / 26.5% CPU · 371 MiB/s · 134.2 µs CPU/req)</sub> | **4,713**<br><sub>(140 MiB / 27.6% CPU · 295 MiB/s · 175.5 µs CPU/req)</sub> | **3,293**<br><sub>(182 MiB / 26.2% CPU · 206 MiB/s · 238.7 µs CPU/req)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/1 · plain | **4,727**<br><sub>(465 MiB / 30.3% CPU · 295 MiB/s · 192.2 µs CPU/req)</sub> | **4,061**<br><sub>(87 MiB / 19.3% CPU · 254 MiB/s · 142.5 µs CPU/req)</sub> | **4,386**<br><sub>(121 MiB / 32.4% CPU · 274 MiB/s · 221.3 µs CPU/req)</sub> | 🥇 **5,396**<br><sub>(132 MiB / 31.7% CPU · 337 MiB/s · 176.1 µs CPU/req)</sub> | **3,704**<br><sub>(166 MiB / 30.7% CPU · 232 MiB/s · 248.5 µs CPU/req)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/1 · plain | **1,758**<br><sub>(281 MiB / 27.6% CPU · 110 MiB/s · 470.2 µs CPU/req)</sub> | 🥇 **2,328**<br><sub>(97 MiB / 25.4% CPU · 145 MiB/s · 327.5 µs CPU/req)</sub> | **1,572**<br><sub>(128 MiB / 30.8% CPU · 98 MiB/s · 588.5 µs CPU/req)</sub> | **1,731**<br><sub>(131 MiB / 49.1% CPU · 108 MiB/s · 851.7 µs CPU/req)</sub> | **1,016**<br><sub>(436 MiB / 30.9% CPU · 64 MiB/s · 910.7 µs CPU/req)</sub> |
| 256 KiB | HTTP/1 · TLS | HTTP/1 · plain | **2,007**<br><sub>(214 MiB / 26.6% CPU · 502 MiB/s · 397.6 µs CPU/req)</sub> | **1,686**<br><sub>(84 MiB / 20.0% CPU · 422 MiB/s · 356.0 µs CPU/req)</sub> | 🥇 **2,211**<br><sub>(119 MiB / 26.2% CPU · 553 MiB/s · 355.3 µs CPU/req)</sub> | **1,902**<br><sub>(153 MiB / 24.6% CPU · 476 MiB/s · 387.2 µs CPU/req)</sub> | **1,250**<br><sub>(184 MiB / 26.6% CPU · 312 MiB/s · 637.4 µs CPU/req)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/1 · plain | **949**<br><sub>(422 MiB / 21.5% CPU · 237 MiB/s · 679.9 µs CPU/req)</sub> | 🥇 **1,892**<br><sub>(87 MiB / 21.9% CPU · 473 MiB/s · 347.2 µs CPU/req)</sub> | **1,892**<br><sub>(116 MiB / 29.5% CPU · 473 MiB/s · 468.2 µs CPU/req)</sub> | **1,747**<br><sub>(154 MiB / 25.8% CPU · 437 MiB/s · 443.6 µs CPU/req)</sub> | **1,312**<br><sub>(178 MiB / 33.9% CPU · 328 MiB/s · 774.1 µs CPU/req)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/1 · plain | **332**<br><sub>(185 MiB / 28.9% CPU · 83 MiB/s · 2606.5 µs CPU/req)</sub> | 🥇 **865**<br><sub>(96 MiB / 29.6% CPU · 216 MiB/s · 1024.7 µs CPU/req)</sub> | **344**<br><sub>(peak 559 · 106 MiB / 25.4% CPU · 86 MiB/s · 2215.8 µs CPU/req)</sub> | **396**<br><sub>(147 MiB / 48.6% CPU · 99 MiB/s · 3689.3 µs CPU/req)</sub> | **107**<br><sub>(286 MiB / 21.6% CPU · 27 MiB/s · 6078.7 µs CPU/req)</sub> |
| 64 KiB | HTTP/2 · plain | HTTP/1 · plain | **5,138**<br><sub>(453 MiB / 27.0% CPU · 321 MiB/s · 157.4 µs CPU/req)</sub> | 🥇 **8,177**<br><sub>(73 MiB / 14.9% CPU · 511 MiB/s · 54.6 µs CPU/req)</sub> | **5,492**<br><sub>(108 MiB / 23.8% CPU · 343 MiB/s · 130.2 µs CPU/req)</sub> | **5,124**<br><sub>(133 MiB / 26.3% CPU · 320 MiB/s · 153.7 µs CPU/req)</sub> | **2,406**<br><sub>(197 MiB / 14.2% CPU · 150 MiB/s · 176.4 µs CPU/req)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/2 · plain | **3,537**<br><sub>(403 MiB / 18.1% CPU · 221 MiB/s · 153.6 µs CPU/req)</sub> | *Not possible* | **3,522**<br><sub>(124 MiB / 26.3% CPU · 220 MiB/s · 224.2 µs CPU/req)</sub> | 🥇 **3,834**<br><sub>(124 MiB / 22.7% CPU · 240 MiB/s · 177.5 µs CPU/req)</sub> | **2,034**<br><sub>(265 MiB / 19.7% CPU · 127 MiB/s · 290.0 µs CPU/req)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/2 · TLS | **1,820**<br><sub>(397 MiB / 14.0% CPU · 114 MiB/s · 230.1 µs CPU/req)</sub> | *Not possible* | **2,932**<br><sub>(123 MiB / 28.3% CPU · 183 MiB/s · 289.8 µs CPU/req)</sub> | 🥇 **3,246**<br><sub>(125 MiB / 29.0% CPU · 203 MiB/s · 268.4 µs CPU/req)</sub> | **3,144**<br><sub>(248 MiB / 34.8% CPU · 196 MiB/s · 332.5 µs CPU/req)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/2 · TLS | 🥇 **1,366**<br><sub>(235 MiB / 30.2% CPU · 85 MiB/s · 662.5 µs CPU/req)</sub> | *Not possible* | **1,215**<br><sub>(131 MiB / 30.6% CPU · 76 MiB/s · 754.9 µs CPU/req)</sub> | **1,146**<br><sub>(128 MiB / 47.3% CPU · 72 MiB/s · 1236.9 µs CPU/req)</sub> | **732**<br><sub>(466 MiB / 24.8% CPU · 46 MiB/s · 1014.1 µs CPU/req)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/1 · TLS | **1,934**<br><sub>(253 MiB / 28.9% CPU · 121 MiB/s · 449.0 µs CPU/req)</sub> | **1,994**<br><sub>(100 MiB / 21.9% CPU · 125 MiB/s · 330.3 µs CPU/req)</sub> | 🥇 **2,069**<br><sub>(132 MiB / 33.0% CPU · 129 MiB/s · 478.6 µs CPU/req)</sub> | **1,628**<br><sub>(132 MiB / 48.3% CPU · 102 MiB/s · 889.6 µs CPU/req)</sub> | **1,082**<br><sub>(429 MiB / 33.8% CPU · 68 MiB/s · 936.0 µs CPU/req)</sub> |
| 256 KiB | HTTP/2 · plain | HTTP/1 · plain | **1,116**<br><sub>(467 MiB / 14.8% CPU · 279 MiB/s · 398.8 µs CPU/req)</sub> | 🥇 **2,453**<br><sub>(72 MiB / 16.4% CPU · 613 MiB/s · 200.8 µs CPU/req)</sub> | **2,420**<br><sub>(102 MiB / 23.6% CPU · 605 MiB/s · 293.0 µs CPU/req)</sub> | **2,307**<br><sub>(144 MiB / 28.0% CPU · 577 MiB/s · 364.6 µs CPU/req)</sub> | **1,461**<br><sub>(213 MiB / 24.2% CPU · 365 MiB/s · 496.9 µs CPU/req)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/2 · plain | **1,226**<br><sub>(661 MiB / 22.9% CPU · 306 MiB/s · 561.6 µs CPU/req)</sub> | *Not possible* | **1,556**<br><sub>(139 MiB / 28.8% CPU · 389 MiB/s · 555.4 µs CPU/req)</sub> | 🥇 **1,631**<br><sub>(134 MiB / 27.7% CPU · 408 MiB/s · 509.6 µs CPU/req)</sub> | **1,153**<br><sub>(281 MiB / 31.1% CPU · 288 MiB/s · 810.0 µs CPU/req)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/2 · TLS | **799**<br><sub>(553 MiB / 28.8% CPU · 200 MiB/s · 1080.8 µs CPU/req)</sub> | *Not possible* | 🥇 **988**<br><sub>(131 MiB / 31.7% CPU · 247 MiB/s · 963.1 µs CPU/req)</sub> | **807**<br><sub>(131 MiB / 25.3% CPU · 202 MiB/s · 940.1 µs CPU/req)</sub> | **754**<br><sub>(285 MiB / 30.7% CPU · 188 MiB/s · 1220.7 µs CPU/req)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/2 · TLS | **138**<br><sub>(324 MiB / 26.5% CPU · 34 MiB/s · 5777.8 µs CPU/req)</sub> | *Not possible* | **358**<br><sub>(peak 438 · 121 MiB / 27.2% CPU · 89 MiB/s · 2286.1 µs CPU/req)</sub> | 🥇 **448**<br><sub>(137 MiB / 49.0% CPU · 112 MiB/s · 3284.6 µs CPU/req)</sub> | **249**<br><sub>(473 MiB / 29.2% CPU · 62 MiB/s · 3518.7 µs CPU/req)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/1 · TLS | **287**<br><sub>(peak 361 · 190 MiB / 37.6% CPU · 72 MiB/s · 3929.6 µs CPU/req)</sub> | 🥇 **427**<br><sub>(peak 461 · 90 MiB / 26.4% CPU · 107 MiB/s · 1855.5 µs CPU/req)</sub> | **230**<br><sub>(peak 418 · 107 MiB / 24.8% CPU · 57 MiB/s · 3237.6 µs CPU/req)</sub> | **368**<br><sub>(133 MiB / 48.2% CPU · 92 MiB/s · 3928.0 µs CPU/req)</sub> | **267**<br><sub>(406 MiB / 33.6% CPU · 67 MiB/s · 3778.9 µs CPU/req)</sub> |

### Windows — POST 64 KiB request + 64 KiB response

Median of **3** repeats on `windows-latest` @ `4038c6b1`. Source: Actions [41648564004](https://github.com/justcoding121/titanium-web-proxy/actions/runs/41648564004) (`compare-post`).

*Not possible:* **HAProxy** and **Envoy** columns are omitted (no official Windows port).























| Client | Origin | TWP | nginx | YARP |
|---|---|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | 🥇 **5,453**<br><sub>(104 MiB / 46.7% CPU · 682 MiB/s · 342.3 µs CPU/req)</sub> | **383**<br><sub>(144 MiB / 24.8% CPU · 48 MiB/s · 2586.6 µs CPU/req)</sub> | **3,982**<br><sub>(136 MiB / 57.3% CPU · 498 MiB/s · 575.2 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **4,500**<br><sub>(218 MiB / 45.8% CPU · 563 MiB/s · 407.3 µs CPU/req)</sub> | **370**<br><sub>(148 MiB / 24.7% CPU · 46 MiB/s · 2664.9 µs CPU/req)</sub> | **4,313**<br><sub>(138 MiB / 49.1% CPU · 539 MiB/s · 455.6 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **2,083**<br><sub>(171 MiB / 44.9% CPU · 260 MiB/s · 862.0 µs CPU/req)</sub> | *Not possible (no QUIC)* | **1,345**<br><sub>(201 MiB / 52.4% CPU · 168 MiB/s · 1558.2 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | 🥇 **6,674**<br><sub>(210 MiB / 45.3% CPU · 834 MiB/s · 271.3 µs CPU/req)</sub> | **1,761**<br><sub>(133 MiB / 24.8% CPU · 220 MiB/s · 563.5 µs CPU/req)</sub> | **6,201**<br><sub>(118 MiB / 45.2% CPU · 775 MiB/s · 291.4 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | 🥇 **4,787**<br><sub>(172 MiB / 37.4% CPU · 598 MiB/s · 312.8 µs CPU/req)</sub> | *Not possible* | **3,794**<br><sub>(152 MiB / 45.4% CPU · 474 MiB/s · 478.7 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **3,362**<br><sub>(178 MiB / 41.2% CPU · 420 MiB/s · 489.7 µs CPU/req)</sub> | *Not possible* | **2,703**<br><sub>(153 MiB / 46.2% CPU · 338 MiB/s · 684.4 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | 🥇 **1,856**<br><sub>(191 MiB / 45.1% CPU · 232 MiB/s · 972.2 µs CPU/req)</sub> | *Not possible* | **1,051**<br><sub>(212 MiB / 50.2% CPU · 131 MiB/s · 1909.8 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **1,965**<br><sub>(178 MiB / 45.5% CPU · 246 MiB/s · 927.2 µs CPU/req)</sub> | *Not possible (no QUIC)* | **1,252**<br><sub>(205 MiB / 53.0% CPU · 157 MiB/s · 1692.9 µs CPU/req)</sub> |

TWP leads H2→H1 POST (about **1.1–1.3×** YARP) and H3 POST (about **1.0–1.1×** YARP). H2 TLS→H2 TLS POST sustain is about **1.2–1.4×** YARP.

### Linux — POST 64 KiB request + 64 KiB response

Median of **3** repeats @ `4038c6b1`. Source: Actions [41648564004](https://github.com/justcoding121/titanium-web-proxy/actions/runs/41648564004) (`compare-post`).


| Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---:|---:|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | **8,754**<br><sub>(136 MiB / 40.6% CPU · 1,094 MiB/s · 185.3 µs CPU/req)</sub> | **7,373**<br><sub>(110 MiB / 44.6% CPU · 922 MiB/s · 242.1 µs CPU/req)</sub> | 🥇 **8,990**<br><sub>(91 MiB / 40.5% CPU · 1,124 MiB/s · 180.3 µs CPU/req)</sub> | **8,630**<br><sub>(128 MiB / 43.4% CPU · 1,079 MiB/s · 201.1 µs CPU/req)</sub> | **5,959**<br><sub>(179 MiB / 54.6% CPU · 745 MiB/s · 366.5 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | **5,445**<br><sub>(293 MiB / 45.4% CPU · 681 MiB/s · 333.7 µs CPU/req)</sub> | **5,232**<br><sub>(117 MiB / 45.3% CPU · 654 MiB/s · 346.0 µs CPU/req)</sub> | 🥇 **5,985**<br><sub>(92 MiB / 43.7% CPU · 748 MiB/s · 292.1 µs CPU/req)</sub> | **5,469**<br><sub>(peak 6,348 · 122 MiB / 43.1% CPU · 684 MiB/s · 315.5 µs CPU/req)</sub> | **5,152**<br><sub>(176 MiB / 49.5% CPU · 644 MiB/s · 384.5 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **4,956**<br><sub>(242 MiB / 45.0% CPU · 620 MiB/s · 363.0 µs CPU/req)</sub> | **1,880**<br><sub>(121 MiB / 49.0% CPU · 235 MiB/s · 1043.1 µs CPU/req)</sub> | **4,728**<br><sub>(97 MiB / 41.0% CPU · 591 MiB/s · 346.5 µs CPU/req)</sub> | **0**<br><sub>(126 MiB / 0.2% CPU)</sub> | **4,147**<br><sub>(267 MiB / 48.9% CPU · 518 MiB/s · 471.4 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | **6,188**<br><sub>(278 MiB / 44.0% CPU · 774 MiB/s · 284.3 µs CPU/req)</sub> | **5,072**<br><sub>(90 MiB / 47.1% CPU · 634 MiB/s · 371.8 µs CPU/req)</sub> | 🥇 **6,780**<br><sub>(77 MiB / 41.6% CPU · 847 MiB/s · 245.7 µs CPU/req)</sub> | **4,637**<br><sub>(peak 6,156 · 111 MiB / 45.9% CPU · 580 MiB/s · 396.0 µs CPU/req)</sub> | **5,971**<br><sub>(160 MiB / 48.0% CPU · 746 MiB/s · 321.6 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | **5,998**<br><sub>(219 MiB / 36.1% CPU · 750 MiB/s · 241.0 µs CPU/req)</sub> | *Not possible* | **5,121**<br><sub>(93 MiB / 42.2% CPU · 640 MiB/s · 329.9 µs CPU/req)</sub> | 🥇 **6,040**<br><sub>(132 MiB / 42.0% CPU · 755 MiB/s · 278.3 µs CPU/req)</sub> | **4,178**<br><sub>(192 MiB / 49.5% CPU · 522 MiB/s · 473.9 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **3,076**<br><sub>(220 MiB / 41.3% CPU · 385 MiB/s · 537.0 µs CPU/req)</sub> | *Not possible* | **2,529**<br><sub>(92 MiB / 43.8% CPU · 316 MiB/s · 692.6 µs CPU/req)</sub> | **3,004**<br><sub>(137 MiB / 41.7% CPU · 376 MiB/s · 555.0 µs CPU/req)</sub> | **2,168**<br><sub>(191 MiB / 47.5% CPU · 271 MiB/s · 875.9 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | 🥇 **3,325**<br><sub>(253 MiB / 42.7% CPU · 416 MiB/s · 514.2 µs CPU/req)</sub> | *Not possible* | **2,734**<br><sub>(98 MiB / 44.0% CPU · 342 MiB/s · 644.5 µs CPU/req)</sub> | **0**<br><sub>(126 MiB / 0.1% CPU)</sub> | **2,327**<br><sub>(265 MiB / 47.7% CPU · 291 MiB/s · 820.1 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **3,967**<br><sub>(254 MiB / 46.0% CPU · 496 MiB/s · 464.3 µs CPU/req)</sub> | **1,703**<br><sub>(127 MiB / 40.1% CPU · 213 MiB/s · 941.9 µs CPU/req)</sub> | **3,843**<br><sub>(97 MiB / 41.2% CPU · 480 MiB/s · 429.4 µs CPU/req)</sub> | **0**<br><sub>(126 MiB / 0.2% CPU)</sub> | **3,149**<br><sub>(281 MiB / 49.7% CPU · 394 MiB/s · 631.5 µs CPU/req)</sub> |

Linux nginx H1/H2/H3 POST completed (nginx.org mainline). TWP÷YARP H1 ≈ **1.5×**; H2→H1 ≈ **1.2×**; H3 ≈ **1.1×**. TWP÷nginx H3 ≈ **6×**.

### macOS — POST 64 KiB request + 64 KiB response

Median of **3** repeats on `macos-15` (Apple Silicon M1, 3-core / 7 GB) @ `4038c6b1`. Source: Actions [41648564004](https://github.com/justcoding121/titanium-web-proxy/actions/runs/41648564004) (`compare-post`).

| Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---:|---:|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | **2,795**<br><sub>(262 MiB / 29.5% CPU · 349 MiB/s · 316.6 µs CPU/req)</sub> | **3,161**<br><sub>(88 MiB / 18.3% CPU · 395 MiB/s · 173.9 µs CPU/req)</sub> | 🥇 **3,421**<br><sub>(120 MiB / 27.0% CPU · 428 MiB/s · 237.0 µs CPU/req)</sub> | **3,235**<br><sub>(131 MiB / 28.6% CPU · 404 MiB/s · 265.0 µs CPU/req)</sub> | **2,007**<br><sub>(198 MiB / 29.4% CPU · 251 MiB/s · 440.1 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | **2,215**<br><sub>(542 MiB / 27.1% CPU · 277 MiB/s · 367.1 µs CPU/req)</sub> | 🥇 **3,374**<br><sub>(99 MiB / 24.6% CPU · 422 MiB/s · 219.1 µs CPU/req)</sub> | **2,834**<br><sub>(123 MiB / 29.1% CPU · 354 MiB/s · 308.5 µs CPU/req)</sub> | **2,058**<br><sub>(peak 3,408 · 121 MiB / 19.9% CPU · 257 MiB/s · 290.7 µs CPU/req)</sub> | **1,828**<br><sub>(185 MiB / 24.1% CPU · 229 MiB/s · 396.2 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | **1,004**<br><sub>(329 MiB / 32.9% CPU · 126 MiB/s · 982.4 µs CPU/req)</sub> | 🥇 **1,059**<br><sub>(96 MiB / 24.5% CPU · 132 MiB/s · 695.1 µs CPU/req)</sub> | **597**<br><sub>(peak 616 · 111 MiB / 27.2% CPU · 75 MiB/s · 1367.5 µs CPU/req)</sub> | **715**<br><sub>(136 MiB / 47.4% CPU · 89 MiB/s · 1987.8 µs CPU/req)</sub> | **577**<br><sub>(448 MiB / 26.3% CPU · 72 MiB/s · 1367.7 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | **3,141**<br><sub>(537 MiB / 24.0% CPU · 393 MiB/s · 228.8 µs CPU/req)</sub> | **3,257**<br><sub>(85 MiB / 13.6% CPU · 407 MiB/s · 125.5 µs CPU/req)</sub> | 🥇 **5,278**<br><sub>(114 MiB / 24.6% CPU · 660 MiB/s · 140.1 µs CPU/req)</sub> | **5,013**<br><sub>(133 MiB / 27.6% CPU · 627 MiB/s · 165.1 µs CPU/req)</sub> | **2,222**<br><sub>(196 MiB / 21.4% CPU · 278 MiB/s · 289.0 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | **2,231**<br><sub>(421 MiB / 22.2% CPU · 279 MiB/s · 298.8 µs CPU/req)</sub> | *Not possible* | 🥇 **2,880**<br><sub>(130 MiB / 25.5% CPU · 360 MiB/s · 265.3 µs CPU/req)</sub> | **2,470**<br><sub>(128 MiB / 28.6% CPU · 309 MiB/s · 347.6 µs CPU/req)</sub> | **2,731**<br><sub>(227 MiB / 34.8% CPU · 341 MiB/s · 382.1 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | **2,094**<br><sub>(424 MiB / 23.9% CPU · 262 MiB/s · 342.7 µs CPU/req)</sub> | *Not possible* | **2,453**<br><sub>(125 MiB / 30.4% CPU · 307 MiB/s · 372.4 µs CPU/req)</sub> | 🥇 **2,637**<br><sub>(128 MiB / 28.2% CPU · 330 MiB/s · 320.9 µs CPU/req)</sub> | **955**<br><sub>(220 MiB / 18.0% CPU · 119 MiB/s · 566.7 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | **830**<br><sub>(366 MiB / 27.3% CPU · 104 MiB/s · 987.4 µs CPU/req)</sub> | *Not possible* | **862**<br><sub>(124 MiB / 31.5% CPU · 108 MiB/s · 1095.0 µs CPU/req)</sub> | 🥇 **872**<br><sub>(136 MiB / 44.9% CPU · 109 MiB/s · 1543.5 µs CPU/req)</sub> | **590**<br><sub>(416 MiB / 28.1% CPU · 74 MiB/s · 1429.1 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | **625**<br><sub>(285 MiB / 27.4% CPU · 78 MiB/s · 1313.4 µs CPU/req)</sub> | 🥇 **778**<br><sub>(101 MiB / 20.6% CPU · 97 MiB/s · 793.9 µs CPU/req)</sub> | **241**<br><sub>(103 MiB / 11.2% CPU · 30 MiB/s · 1393.0 µs CPU/req)</sub> | **330**<br><sub>(peak 379 · 132 MiB / 23.7% CPU · 41 MiB/s · 2156.0 µs CPU/req)</sub> | **655**<br><sub>(399 MiB / 32.2% CPU · 82 MiB/s · 1472.5 µs CPU/req)</sub> |

### Windows — lossy / high-RTT (H2 HOL / H3 loss)

Userspace **5 ms** one-way delay + **1%** TCP connection stall (H1/H2); UDP is **loss% only** (no per-datagram delay; MsQuic-safe) + **1%** datagram drop (H3); **64 KiB** GET. Median of **3** repeats on `windows-latest` @ `4038c6b1` — [41648583393](https://github.com/justcoding121/titanium-web-proxy/actions/runs/41648583393) (`compare-lossy`).

*Not possible:* **HAProxy** and **Envoy** columns are omitted (no official Windows port).


| Client | Origin | TWP | nginx | YARP |
|---|---|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | 🥇 **679**<br><sub>(92 MiB / 5.3% CPU · 42 MiB/s · 310.4 µs CPU/req)</sub> | **643**<br><sub>(143 MiB / 18.6% CPU · 40 MiB/s · 1154.9 µs CPU/req)</sub> | **662**<br><sub>(117 MiB / 5.1% CPU · 41 MiB/s · 308.7 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **248**<br><sub>(168 MiB / 3.0% CPU · 15 MiB/s · 490.9 µs CPU/req)</sub> | **243**<br><sub>(144 MiB / 7.8% CPU · 15 MiB/s · 1286.1 µs CPU/req)</sub> | **230**<br><sub>(118 MiB / 2.9% CPU · 14 MiB/s · 509.6 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | **1,020**<br><sub>(119 MiB / 12.8% CPU · 64 MiB/s · 502.2 µs CPU/req)</sub> | *Not possible (no QUIC)* | 🥇 **1,430**<br><sub>(180 MiB / 29.4% CPU · 89 MiB/s · 822.7 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | 🥇 **255**<br><sub>(140 MiB / 2.3% CPU · 16 MiB/s · 354.1 µs CPU/req)</sub> | **250**<br><sub>(129 MiB / 2.0% CPU · 16 MiB/s · 313.9 µs CPU/req)</sub> | **231**<br><sub>(105 MiB / 2.6% CPU · 14 MiB/s · 459.3 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | **224**<br><sub>(121 MiB / 3.3% CPU · 14 MiB/s · 595.4 µs CPU/req)</sub> | *Not possible* | 🥇 **230**<br><sub>(115 MiB / 3.7% CPU · 14 MiB/s · 650.7 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | **224**<br><sub>(124 MiB / 2.8% CPU · 14 MiB/s · 493.1 µs CPU/req)</sub> | *Not possible* | 🥇 **231**<br><sub>(115 MiB / 4.4% CPU · 14 MiB/s · 767.4 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | **884**<br><sub>(122 MiB / 20.7% CPU · 55 MiB/s · 936.7 µs CPU/req)</sub> | *Not possible* | 🥇 **884**<br><sub>(187 MiB / 29.7% CPU · 55 MiB/s · 1342.2 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **867**<br><sub>(138 MiB / 19.6% CPU · 54 MiB/s · 903.0 µs CPU/req)</sub> | *Not possible (no QUIC)* | **854**<br><sub>(177 MiB / 31.2% CPU · 53 MiB/s · 1463.1 µs CPU/req)</sub> |

H1, H2, and H3 loss sustain are in the table. Prefer the ratio columns.

### Linux — lossy / high-RTT (H2 HOL / H3 loss)

Median of **3** repeats @ `4038c6b1`. Source: [41648583393](https://github.com/justcoding121/titanium-web-proxy/actions/runs/41648583393) (`compare-lossy`; lossy H3 uses `quic-http3`, UDP drop-only).


| Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---:|---:|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | **1,213**<br><sub>(128 MiB / 9.4% CPU · 76 MiB/s · 310.9 µs CPU/req)</sub> | 🥇 **1,221**<br><sub>(110 MiB / 8.1% CPU · 76 MiB/s · 263.7 µs CPU/req)</sub> | **1,216**<br><sub>(87 MiB / 4.0% CPU · 76 MiB/s · 130.3 µs CPU/req)</sub> | **1,209**<br><sub>(123 MiB / 6.3% CPU · 76 MiB/s · 207.1 µs CPU/req)</sub> | **1,211**<br><sub>(158 MiB / 12.3% CPU · 76 MiB/s · 406.9 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **626**<br><sub>(201 MiB / 10.2% CPU · 39 MiB/s · 651.0 µs CPU/req)</sub> | **583**<br><sub>(110 MiB / 4.3% CPU · 36 MiB/s · 297.9 µs CPU/req)</sub> | **572**<br><sub>(87 MiB / 3.2% CPU · 36 MiB/s · 221.0 µs CPU/req)</sub> | **509**<br><sub>(125 MiB / 3.9% CPU · 32 MiB/s · 303.9 µs CPU/req)</sub> | **512**<br><sub>(148 MiB / 9.9% CPU · 32 MiB/s · 775.8 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | **1,385**<br><sub>(173 MiB / 16.2% CPU · 87 MiB/s · 467.9 µs CPU/req)</sub> | **0**<br><sub>(peak 1,918 · 126 MiB / 22.3% CPU)</sub> | **1,606**<br><sub>(104 MiB / 17.8% CPU · 100 MiB/s · 444.4 µs CPU/req)</sub> | 🥇 **1,826**<br><sub>(135 MiB / 23.6% CPU · 114 MiB/s · 516.6 µs CPU/req)</sub> | **1,566**<br><sub>(230 MiB / 25.3% CPU · 98 MiB/s · 645.1 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | 🥇 **648**<br><sub>(189 MiB / 10.8% CPU · 40 MiB/s · 669.1 µs CPU/req)</sub> | **582**<br><sub>(81 MiB / 4.8% CPU · 36 MiB/s · 327.6 µs CPU/req)</sub> | **540**<br><sub>(71 MiB / 2.9% CPU · 34 MiB/s · 217.0 µs CPU/req)</sub> | **510**<br><sub>(115 MiB / 3.5% CPU · 32 MiB/s · 276.3 µs CPU/req)</sub> | **513**<br><sub>(139 MiB / 9.7% CPU · 32 MiB/s · 752.1 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | **509**<br><sub>(154 MiB / 6.6% CPU · 32 MiB/s · 519.8 µs CPU/req)</sub> | *Not possible* | 🥇 **548**<br><sub>(90 MiB / 2.5% CPU · 34 MiB/s · 183.2 µs CPU/req)</sub> | **512**<br><sub>(125 MiB / 3.3% CPU · 32 MiB/s · 259.3 µs CPU/req)</sub> | **516**<br><sub>(158 MiB / 8.5% CPU · 32 MiB/s · 658.3 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | **497**<br><sub>(166 MiB / 8.8% CPU · 31 MiB/s · 706.5 µs CPU/req)</sub> | *Not possible* | 🥇 **587**<br><sub>(89 MiB / 4.2% CPU · 37 MiB/s · 285.4 µs CPU/req)</sub> | **509**<br><sub>(127 MiB / 4.9% CPU · 32 MiB/s · 387.5 µs CPU/req)</sub> | **512**<br><sub>(158 MiB / 11.4% CPU · 32 MiB/s · 889.0 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | **1,490**<br><sub>(167 MiB / 21.5% CPU · 93 MiB/s · 578.2 µs CPU/req)</sub> | *Not possible* | **1,698**<br><sub>(105 MiB / 19.5% CPU · 106 MiB/s · 459.4 µs CPU/req)</sub> | 🥇 **1,756**<br><sub>(137 MiB / 29.5% CPU · 110 MiB/s · 671.0 µs CPU/req)</sub> | **1,513**<br><sub>(239 MiB / 31.2% CPU · 95 MiB/s · 825.5 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **1,266**<br><sub>(182 MiB / 26.6% CPU · 79 MiB/s · 840.8 µs CPU/req)</sub> | **1,152**<br><sub>(126 MiB / 24.5% CPU · 72 MiB/s · 849.6 µs CPU/req)</sub> | **1,088**<br><sub>(105 MiB / 20.9% CPU · 68 MiB/s · 767.7 µs CPU/req)</sub> | **1,085**<br><sub>(134 MiB / 27.6% CPU · 68 MiB/s · 1018.5 µs CPU/req)</sub> | **1,179**<br><sub>(229 MiB / 32.6% CPU · 74 MiB/s · 1105.3 µs CPU/req)</sub> |

TWP H2 HOL ≫ YARP (~**7.8×**). H3 TWP÷YARP ≈ **1.07×**.

### macOS — lossy / high-RTT (H2 HOL / H3 loss)

Median of **3** repeats on `macos-15` (Apple Silicon M1, 3-core / 7 GB) @ `4038c6b1`. Source: [41648583393](https://github.com/justcoding121/titanium-web-proxy/actions/runs/41648583393) (`compare-lossy`; lossy H3 uses `quic-http3`, UDP drop-only).

| Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---:|---:|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | **462**<br><sub>(226 MiB / 7.3% CPU · 29 MiB/s · 477.3 µs CPU/req)</sub> | 🥇 **551**<br><sub>(84 MiB / 2.3% CPU · 34 MiB/s · 123.1 µs CPU/req)</sub> | **513**<br><sub>(116 MiB / 4.0% CPU · 32 MiB/s · 234.4 µs CPU/req)</sub> | **448**<br><sub>(131 MiB / 4.7% CPU · 28 MiB/s · 313.5 µs CPU/req)</sub> | **486**<br><sub>(212 MiB / 6.2% CPU · 30 MiB/s · 386.1 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | **171**<br><sub>(375 MiB / 6.6% CPU · 11 MiB/s · 1162.1 µs CPU/req)</sub> | **174**<br><sub>(85 MiB / 1.9% CPU · 11 MiB/s · 332.6 µs CPU/req)</sub> | 🥇 **176**<br><sub>(112 MiB / 2.7% CPU · 11 MiB/s · 462.7 µs CPU/req)</sub> | **152**<br><sub>(129 MiB / 2.1% CPU · 9.5 MiB/s · 411.9 µs CPU/req)</sub> | **160**<br><sub>(222 MiB / 5.2% CPU · 10.0 MiB/s · 985.0 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | **954**<br><sub>(269 MiB / 20.2% CPU · 60 MiB/s · 634.9 µs CPU/req)</sub> | 🥇 **1,009**<br><sub>(95 MiB / 12.4% CPU · 63 MiB/s · 370.1 µs CPU/req)</sub> | **619**<br><sub>(123 MiB / 17.4% CPU · 39 MiB/s · 841.8 µs CPU/req)</sub> | **944**<br><sub>(131 MiB / 30.1% CPU · 59 MiB/s · 957.9 µs CPU/req)</sub> | **812**<br><sub>(454 MiB / 24.3% CPU · 51 MiB/s · 898.4 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | 🥇 **192**<br><sub>(328 MiB / 4.2% CPU · 12 MiB/s · 653.8 µs CPU/req)</sub> | **171**<br><sub>(72 MiB / 0.8% CPU · 11 MiB/s · 133.6 µs CPU/req)</sub> | **192**<br><sub>(102 MiB / 1.1% CPU · 12 MiB/s · 170.6 µs CPU/req)</sub> | **152**<br><sub>(121 MiB / 1.8% CPU · 9.5 MiB/s · 361.9 µs CPU/req)</sub> | **183**<br><sub>(146 MiB / 4.8% CPU · 11 MiB/s · 788.5 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | **160**<br><sub>(309 MiB / 5.0% CPU · 10.0 MiB/s · 944.3 µs CPU/req)</sub> | *Not possible* | **168**<br><sub>(121 MiB / 2.1% CPU · 10 MiB/s · 375.9 µs CPU/req)</sub> | **144**<br><sub>(122 MiB / 2.4% CPU · 9.0 MiB/s · 490.6 µs CPU/req)</sub> | 🥇 **168**<br><sub>(256 MiB / 5.6% CPU · 10 MiB/s · 997.0 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | **163**<br><sub>(288 MiB / 4.7% CPU · 10 MiB/s · 874.0 µs CPU/req)</sub> | *Not possible* | 🥇 **168**<br><sub>(116 MiB / 3.6% CPU · 10 MiB/s · 645.4 µs CPU/req)</sub> | **144**<br><sub>(122 MiB / 3.1% CPU · 9.0 MiB/s · 650.2 µs CPU/req)</sub> | **152**<br><sub>(261 MiB / 6.4% CPU · 9.5 MiB/s · 1272.4 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | **616**<br><sub>(202 MiB / 20.2% CPU · 38 MiB/s · 983.4 µs CPU/req)</sub> | *Not possible* | **425**<br><sub>(129 MiB / 16.5% CPU · 27 MiB/s · 1167.0 µs CPU/req)</sub> | 🥇 **669**<br><sub>(129 MiB / 31.3% CPU · 42 MiB/s · 1402.0 µs CPU/req)</sub> | **538**<br><sub>(386 MiB / 28.3% CPU · 34 MiB/s · 1577.7 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | **665**<br><sub>(211 MiB / 20.8% CPU · 42 MiB/s · 938.5 µs CPU/req)</sub> | 🥇 **883**<br><sub>(98 MiB / 16.1% CPU · 55 MiB/s · 545.4 µs CPU/req)</sub> | **507**<br><sub>(127 MiB / 18.0% CPU · 32 MiB/s · 1062.1 µs CPU/req)</sub> | **844**<br><sub>(131 MiB / 29.3% CPU · 53 MiB/s · 1043.5 µs CPU/req)</sub> | **470**<br><sub>(384 MiB / 18.2% CPU · 29 MiB/s · 1161.4 µs CPU/req)</sub> |

### Architecture-sensitive

These runs isolate slow app readers, origin-early response, HTTP/2 duplex, and WebSocket echo. See [TWP vs YARP IO model](Performance-Profiling#user-content-twp-vs-yarp-io-model). Laptop 1-rep numbers are on [Performance Local Lab](Performance-Local-Lab#user-content-architecture-sensitive).

Median of **3** repeats on matched 4 vCPU / 16 GiB runners @ `4038c6b1` ([41648624388](https://github.com/justcoding121/titanium-web-proxy/actions/runs/41648624388)). Slow consumer = 256 KiB GET, 16 KiB read + 8 ms sleep. Early response = 64 KiB POST, origin writes after 8 KiB. Duplex HTTP/2 = overlapping 64 KiB POST on H2 TLS↔H2 TLS. WebSocket row = H1 Upgrade echo round-trips/sec (not RFC 8441; see WebSocket RFC 8441 table below).

Lossy-link runs (slow **network**) are already published above; they are not a slow **app** reader.

#### Windows


*Not possible:* **HAProxy** and **Envoy** columns are omitted (no official Windows port).

| Scenario | Client | Origin | TWP | nginx | YARP |
|---|---|---|---:|---:|---:|
| Slow consumer (256 KiB GET, throttled client read) | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **249**<br><sub>(98 MiB / 2.7% CPU · 62 MiB/s · 433.7 µs CPU/req)</sub> | **242**<br><sub>(146 MiB / 16.9% CPU · 61 MiB/s · 2790.6 µs CPU/req)</sub> | **248**<br><sub>(112 MiB / 2.2% CPU · 62 MiB/s · 357.0 µs CPU/req)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · TLS | HTTP/1 · plain | **248**<br><sub>(160 MiB / 4.1% CPU · 62 MiB/s · 664.0 µs CPU/req)</sub> | **230**<br><sub>(144 MiB / 24.7% CPU · 57 MiB/s · 4301.3 µs CPU/req)</sub> | 🥇 **248**<br><sub>(122 MiB / 6.0% CPU · 62 MiB/s · 965.7 µs CPU/req)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **175**<br><sub>(113 MiB / 42.0% CPU · 44 MiB/s · 9624.3 µs CPU/req)</sub> | *Not possible (no QUIC)* | **116**<br><sub>(152 MiB / 46.1% CPU · 29 MiB/s · 15852.1 µs CPU/req)</sub> |
| Early response (origin writes after first request chunk) | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **7,263**<br><sub>(101 MiB / 42.6% CPU · 908 MiB/s · 234.7 µs CPU/req)</sub> | **434**<br><sub>(145 MiB / 24.9% CPU · 54 MiB/s · 2291.9 µs CPU/req)</sub> | **5,052**<br><sub>(140 MiB / 54.5% CPU · 631 MiB/s · 431.5 µs CPU/req)</sub> |
| Early response (origin writes after first request chunk) | HTTP/2 · TLS | HTTP/1 · plain | **3,597**<br><sub>(200 MiB / 51.1% CPU · 450 MiB/s · 568.3 µs CPU/req)</sub> | **380**<br><sub>(148 MiB / 24.8% CPU · 48 MiB/s · 2606.4 µs CPU/req)</sub> | 🥇 **4,209**<br><sub>(137 MiB / 48.0% CPU · 526 MiB/s · 456.3 µs CPU/req)</sub> |
| Early response (origin writes after first request chunk) | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **1,721**<br><sub>(156 MiB / 45.5% CPU · 215 MiB/s · 1057.0 µs CPU/req)</sub> | *Not possible (no QUIC)* | **1,024**<br><sub>(212 MiB / 50.8% CPU · 128 MiB/s · 1984.6 µs CPU/req)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · plain | HTTP/1 · plain | **248**<br><sub>(135 MiB / 3.4% CPU · 62 MiB/s · 554.1 µs CPU/req)</sub> | **248**<br><sub>(129 MiB / 8.5% CPU · 62 MiB/s · 1374.8 µs CPU/req)</sub> | 🥇 **248**<br><sub>(111 MiB / 3.3% CPU · 62 MiB/s · 534.5 µs CPU/req)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · TLS | HTTP/2 · TLS | **248**<br><sub>(168 MiB / 9.5% CPU · 62 MiB/s · 1528.3 µs CPU/req)</sub> | *Not possible* | 🥇 **248**<br><sub>(140 MiB / 9.2% CPU · 62 MiB/s · 1485.7 µs CPU/req)</sub> |
| Early response (origin writes after first request chunk) | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **3,203**<br><sub>(204 MiB / 41.8% CPU · 400 MiB/s · 522.0 µs CPU/req)</sub> | *Not possible* | **2,693**<br><sub>(163 MiB / 54.3% CPU · 337 MiB/s · 806.4 µs CPU/req)</sub> |
| Duplex (both directions live) | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **3,206**<br><sub>(191 MiB / 38.8% CPU · 401 MiB/s · 484.5 µs CPU/req)</sub> | *Not possible* | **2,608**<br><sub>(169 MiB / 54.2% CPU · 326 MiB/s · 831.0 µs CPU/req)</sub> |
| Duplex (WebSocket / H1 Upgrade) | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **29,925**<br><sub>(100 MiB / 43.2% CPU · 57.8 µs CPU/req)</sub> | **17,051**<br><sub>(145 MiB / 24.8% CPU · 58.1 µs CPU/req)</sub> | **27,377**<br><sub>(89 MiB / 44.9% CPU · 65.7 µs CPU/req)</sub> |

#### Linux


| Scenario | Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---|---:|---:|---:|---:|---:|
| Slow consumer (256 KiB GET, throttled client read) | HTTP/1 · TLS | HTTP/1 · plain | **476**<br><sub>(127 MiB / 4.2% CPU · 119 MiB/s · 356.1 µs CPU/req)</sub> | **474**<br><sub>(110 MiB / 7.8% CPU · 119 MiB/s · 654.4 µs CPU/req)</sub> | 🥇 **480**<br><sub>(87 MiB / 2.4% CPU · 120 MiB/s · 202.6 µs CPU/req)</sub> | **475**<br><sub>(135 MiB / 4.0% CPU · 119 MiB/s · 333.5 µs CPU/req)</sub> | **476**<br><sub>(167 MiB / 7.3% CPU · 119 MiB/s · 610.5 µs CPU/req)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **478**<br><sub>(170 MiB / 20.7% CPU · 120 MiB/s · 1731.6 µs CPU/req)</sub> | **476**<br><sub>(109 MiB / 19.6% CPU · 119 MiB/s · 1648.8 µs CPU/req)</sub> | **475**<br><sub>(87 MiB / 12.4% CPU · 119 MiB/s · 1045.9 µs CPU/req)</sub> | **475**<br><sub>(140 MiB / 10.4% CPU · 119 MiB/s · 876.4 µs CPU/req)</sub> | **474**<br><sub>(160 MiB / 23.0% CPU · 119 MiB/s · 1941.8 µs CPU/req)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/3 · QUIC | HTTP/1 · plain | **466**<br><sub>(157 MiB / 42.6% CPU · 117 MiB/s · 3655.9 µs CPU/req)</sub> | **472**<br><sub>(124 MiB / 14.8% CPU · 118 MiB/s · 1252.1 µs CPU/req)</sub> | **472**<br><sub>(94 MiB / 8.7% CPU · 118 MiB/s · 737.9 µs CPU/req)</sub> | 🥇 **473**<br><sub>(147 MiB / 19.6% CPU · 118 MiB/s · 1659.5 µs CPU/req)</sub> | **465**<br><sub>(205 MiB / 46.8% CPU · 116 MiB/s · 4021.5 µs CPU/req)</sub> |
| Early response (origin writes after first request chunk) | HTTP/1 · TLS | HTTP/1 · plain | **5,303**<br><sub>(150 MiB / 44.9% CPU · 663 MiB/s · 338.4 µs CPU/req)</sub> | **4,316**<br><sub>(110 MiB / 50.3% CPU · 539 MiB/s · 466.3 µs CPU/req)</sub> | 🥇 **5,733**<br><sub>(91 MiB / 43.9% CPU · 717 MiB/s · 306.3 µs CPU/req)</sub> | **0**<br><sub>(peak 3,179 · 125 MiB / 26.0% CPU)</sub> | **3,661**<br><sub>(180 MiB / 56.0% CPU · 458 MiB/s · 612.1 µs CPU/req)</sub> |
| Early response (origin writes after first request chunk) | HTTP/2 · TLS | HTTP/1 · plain | **5,467**<br><sub>(278 MiB / 51.7% CPU · 683 MiB/s · 378.2 µs CPU/req)</sub> | **6,503**<br><sub>(118 MiB / 45.4% CPU · 813 MiB/s · 279.1 µs CPU/req)</sub> | 🥇 **7,774**<br><sub>(92 MiB / 42.9% CPU · 972 MiB/s · 220.8 µs CPU/req)</sub> | **0**<br><sub>(peak 7,706 · 136 MiB / 43.0% CPU)</sub> | **6,560**<br><sub>(174 MiB / 47.9% CPU · 820 MiB/s · 292.1 µs CPU/req)</sub> |
| Early response (origin writes after first request chunk) | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **2,883**<br><sub>(240 MiB / 45.5% CPU · 360 MiB/s · 630.7 µs CPU/req)</sub> | **1,150**<br><sub>(121 MiB / 43.0% CPU · 144 MiB/s · 1493.4 µs CPU/req)</sub> | **2,437**<br><sub>(97 MiB / 41.0% CPU · 305 MiB/s · 673.2 µs CPU/req)</sub> | **0**<br><sub>(peak 1 · 127 MiB / 0.2% CPU)</sub> | **1,880**<br><sub>(280 MiB / 49.9% CPU · 235 MiB/s · 1060.8 µs CPU/req)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · plain | HTTP/1 · plain | 🥇 **480**<br><sub>(165 MiB / 8.6% CPU · 120 MiB/s · 717.2 µs CPU/req)</sub> | **480**<br><sub>(82 MiB / 5.1% CPU · 120 MiB/s · 421.8 µs CPU/req)</sub> | **480**<br><sub>(71 MiB / 3.2% CPU · 120 MiB/s · 264.2 µs CPU/req)</sub> | **475**<br><sub>(132 MiB / 3.7% CPU · 119 MiB/s · 308.4 µs CPU/req)</sub> | **480**<br><sub>(147 MiB / 8.2% CPU · 120 MiB/s · 686.8 µs CPU/req)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · TLS | HTTP/2 · TLS | **466**<br><sub>(206 MiB / 23.9% CPU · 117 MiB/s · 2053.6 µs CPU/req)</sub> | *Not possible* | **471**<br><sub>(96 MiB / 19.2% CPU · 118 MiB/s · 1627.0 µs CPU/req)</sub> | 🥇 **472**<br><sub>(135 MiB / 17.2% CPU · 118 MiB/s · 1460.2 µs CPU/req)</sub> | **466**<br><sub>(173 MiB / 29.4% CPU · 116 MiB/s · 2522.7 µs CPU/req)</sub> |
| Early response (origin writes after first request chunk) | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **5,921**<br><sub>(256 MiB / 36.6% CPU · 740 MiB/s · 247.0 µs CPU/req)</sub> | *Not possible* | **3,764**<br><sub>(98 MiB / 24.1% CPU · 470 MiB/s · 256.4 µs CPU/req)</sub> | **0**<br><sub>(peak 4,301 · 145 MiB / 30.0% CPU)</sub> | **4,424**<br><sub>(233 MiB / 56.5% CPU · 553 MiB/s · 510.9 µs CPU/req)</sub> |
| Duplex (both directions live) | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **3,044**<br><sub>(224 MiB / 40.9% CPU · 381 MiB/s · 537.7 µs CPU/req)</sub> | *Not possible* | **2,147**<br><sub>(95 MiB / 36.7% CPU · 268 MiB/s · 684.2 µs CPU/req)</sub> | **0**<br><sub>(peak 2,587 · 138 MiB / 37.4% CPU)</sub> | **2,161**<br><sub>(210 MiB / 54.6% CPU · 270 MiB/s · 1010.5 µs CPU/req)</sub> |
| Duplex (WebSocket / H1 Upgrade) | HTTP/1 · TLS | HTTP/1 · plain | **68,270**<br><sub>(134 MiB / 45.5% CPU · 26.7 µs CPU/req)</sub> | 🥇 **78,671**<br><sub>(110 MiB / 39.8% CPU · 20.3 µs CPU/req)</sub> | **72,395**<br><sub>(85 MiB / 43.0% CPU · 23.8 µs CPU/req)</sub> | **71,223**<br><sub>(122 MiB / 44.8% CPU · 25.2 µs CPU/req)</sub> | **64,504**<br><sub>(130 MiB / 45.8% CPU · 28.4 µs CPU/req)</sub> |

#### macOS

Median of **3** repeats on `macos-15` (Apple Silicon M1, 3-core / 7 GB) @ `4038c6b1`. Source: Actions [41648624388](https://github.com/justcoding121/titanium-web-proxy/actions/runs/41648624388) (`compare-arch`).

| Scenario | Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---|---:|---:|---:|---:|---:|
| Slow consumer (256 KiB GET, throttled client read) | HTTP/1 · TLS | HTTP/1 · plain | **115**<br><sub>(256 MiB / 4.2% CPU · 29 MiB/s · 1088.8 µs CPU/req)</sub> | **128**<br><sub>(84 MiB / 2.5% CPU · 32 MiB/s · 592.0 µs CPU/req)</sub> | 🥇 **128**<br><sub>(111 MiB / 2.3% CPU · 32 MiB/s · 529.7 µs CPU/req)</sub> | **90**<br><sub>(146 MiB / 1.9% CPU · 23 MiB/s · 647.1 µs CPU/req)</sub> | **96**<br><sub>(217 MiB / 3.8% CPU · 24 MiB/s · 1196.2 µs CPU/req)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · TLS | HTTP/1 · plain | **112**<br><sub>(383 MiB / 9.0% CPU · 28 MiB/s · 2417.2 µs CPU/req)</sub> | 🥇 **128**<br><sub>(85 MiB / 3.2% CPU · 32 MiB/s · 761.2 µs CPU/req)</sub> | **104**<br><sub>(113 MiB / 2.9% CPU · 26 MiB/s · 831.6 µs CPU/req)</sub> | **88**<br><sub>(145 MiB / 3.1% CPU · 22 MiB/s · 1069.5 µs CPU/req)</sub> | **97**<br><sub>(221 MiB / 6.6% CPU · 24 MiB/s · 2047.4 µs CPU/req)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/3 · QUIC | HTTP/1 · plain | **131**<br><sub>(146 MiB / 25.3% CPU · 33 MiB/s · 5804.9 µs CPU/req)</sub> | **107**<br><sub>(93 MiB / 4.6% CPU · 27 MiB/s · 1284.9 µs CPU/req)</sub> | **133**<br><sub>(110 MiB / 13.7% CPU · 33 MiB/s · 3087.8 µs CPU/req)</sub> | **105**<br><sub>(147 MiB / 13.9% CPU · 26 MiB/s · 3968.7 µs CPU/req)</sub> | 🥇 **152**<br><sub>(350 MiB / 24.5% CPU · 38 MiB/s · 4837.2 µs CPU/req)</sub> |
| Early response (origin writes after first request chunk) | HTTP/1 · TLS | HTTP/1 · plain | **3,391**<br><sub>(251 MiB / 30.0% CPU · 424 MiB/s · 265.8 µs CPU/req)</sub> | **4,158**<br><sub>(89 MiB / 20.8% CPU · 520 MiB/s · 149.8 µs CPU/req)</sub> | 🥇 **4,681**<br><sub>(121 MiB / 28.8% CPU · 585 MiB/s · 184.4 µs CPU/req)</sub> | **0**<br><sub>(peak 3,789 · 130 MiB / 31.0% CPU)</sub> | **2,719**<br><sub>(196 MiB / 35.2% CPU · 340 MiB/s · 388.0 µs CPU/req)</sub> |
| Early response (origin writes after first request chunk) | HTTP/2 · TLS | HTTP/1 · plain | **2,310**<br><sub>(534 MiB / 22.3% CPU · 289 MiB/s · 290.1 µs CPU/req)</sub> | **2,782**<br><sub>(97 MiB / 19.7% CPU · 348 MiB/s · 212.1 µs CPU/req)</sub> | 🥇 **3,776**<br><sub>(126 MiB / 29.4% CPU · 472 MiB/s · 233.6 µs CPU/req)</sub> | **0**<br><sub>(peak 2,844 · 142 MiB / 27.8% CPU)</sub> | **2,066**<br><sub>(183 MiB / 24.1% CPU · 258 MiB/s · 349.4 µs CPU/req)</sub> |
| Early response (origin writes after first request chunk) | HTTP/3 · QUIC | HTTP/1 · plain | **999**<br><sub>(292 MiB / 29.0% CPU · 125 MiB/s · 871.5 µs CPU/req)</sub> | 🥇 **1,243**<br><sub>(100 MiB / 21.9% CPU · 155 MiB/s · 528.0 µs CPU/req)</sub> | **361**<br><sub>(105 MiB / 22.9% CPU · 45 MiB/s · 1903.5 µs CPU/req)</sub> | **810**<br><sub>(132 MiB / 39.7% CPU · 101 MiB/s · 1469.5 µs CPU/req)</sub> | **816**<br><sub>(390 MiB / 33.8% CPU · 102 MiB/s · 1241.9 µs CPU/req)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · plain | HTTP/1 · plain | 🥇 **120**<br><sub>(228 MiB / 4.5% CPU · 30 MiB/s · 1115.9 µs CPU/req)</sub> | **120**<br><sub>(72 MiB / 1.7% CPU · 30 MiB/s · 435.7 µs CPU/req)</sub> | **104**<br><sub>(100 MiB / 1.7% CPU · 26 MiB/s · 481.7 µs CPU/req)</sub> | **88**<br><sub>(144 MiB / 1.4% CPU · 22 MiB/s · 484.6 µs CPU/req)</sub> | **111**<br><sub>(145 MiB / 3.3% CPU · 28 MiB/s · 898.4 µs CPU/req)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · TLS | HTTP/2 · TLS | **88**<br><sub>(450 MiB / 5.3% CPU · 22 MiB/s · 1795.2 µs CPU/req)</sub> | *Not possible* | 🥇 **120**<br><sub>(127 MiB / 5.2% CPU · 30 MiB/s · 1296.1 µs CPU/req)</sub> | **88**<br><sub>(129 MiB / 3.5% CPU · 22 MiB/s · 1201.4 µs CPU/req)</sub> | **96**<br><sub>(341 MiB / 5.6% CPU · 24 MiB/s · 1746.1 µs CPU/req)</sub> |
| Early response (origin writes after first request chunk) | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **1,918**<br><sub>(503 MiB / 24.2% CPU · 240 MiB/s · 379.2 µs CPU/req)</sub> | *Not possible* | **1,533**<br><sub>(118 MiB / 22.7% CPU · 192 MiB/s · 445.0 µs CPU/req)</sub> | **0**<br><sub>(peak 525 · 117 MiB / 8.2% CPU)</sub> | **1,786**<br><sub>(323 MiB / 35.9% CPU · 223 MiB/s · 603.2 µs CPU/req)</sub> |
| Duplex (both directions live) | HTTP/2 · TLS | HTTP/2 · TLS | **1,678**<br><sub>(468 MiB / 23.2% CPU · 210 MiB/s · 414.7 µs CPU/req)</sub> | *Not possible* | **1,468**<br><sub>(119 MiB / 23.9% CPU · 184 MiB/s · 487.8 µs CPU/req)</sub> | **0**<br><sub>(peak 2,025 · 131 MiB / 21.9% CPU)</sub> | 🥇 **1,692**<br><sub>(375 MiB / 27.1% CPU · 212 MiB/s · 480.6 µs CPU/req)</sub> |
| Duplex (WebSocket / H1 Upgrade) | HTTP/1 · TLS | HTTP/1 · plain | **16,791**<br><sub>(166 MiB / 23.7% CPU · 42.4 µs CPU/req)</sub> | **24,739**<br><sub>(86 MiB / 20.5% CPU · 24.9 µs CPU/req)</sub> | **26,656**<br><sub>(103 MiB / 26.2% CPU · 29.4 µs CPU/req)</sub> | 🥇 **31,260**<br><sub>(120 MiB / 28.5% CPU · 27.3 µs CPU/req)</sub> | **22,752**<br><sub>(181 MiB / 25.7% CPU · 33.8 µs CPU/req)</sub> |

Slow consumer is sleep-bound; H1/H2/H3 sit in the same band. Prefer the ratio columns for early-response, duplex, and WebSocket rows.

### TLS termination cost (H1 TLS → cleartext origin)

Isolates keep-alive tiny GET vs **new connection per request** (handshake-dominated) vs keep-alive **256 KiB**. Product comparison uses RPS and end-to-end latency; TWP can also capture `ClientTlsTiming` when `TWP_RPS_CAPTURE_TLS=1` (child process) — nginx/YARP have no equivalent hook.

#### Windows

Median of **3** repeats on `windows-latest` @ `4038c6b1`. Source: Actions [41648603685](https://github.com/justcoding121/titanium-web-proxy/actions/runs/41648603685). Absolute RPS on GHA swings hard; prefer **TWP÷YARP**.


*Not possible:* **HAProxy** and **Envoy** columns are omitted (no official Windows port).

| Workload | TWP | nginx | YARP |
|---|---:|---:|---:|
| Keep-alive · tiny GET | 🥇 **28,997**<br><sub>(95 MiB / 44.6% CPU · 61.5 µs CPU/req)</sub> | **15,920**<br><sub>(144 MiB / 24.6% CPU · 61.9 µs CPU/req)</sub> | **25,665**<br><sub>(102 MiB / 48.4% CPU · 75.4 µs CPU/req)</sub> |
| New-connection · tiny GET | 🥇 **694**<br><sub>(83 MiB)</sub> | **248**<br><sub>(143 MiB / 1.7% CPU · 280.8 µs CPU/req)</sub> | **692**<br><sub>(107 MiB / 0.4% CPU · 22.0 µs CPU/req)</sub> |
| Keep-alive · 256 KiB GET | 🥇 **2,885**<br><sub>(131 MiB / 45.6% CPU · 721 MiB/s · 631.7 µs CPU/req)</sub> | **166**<br><sub>(144 MiB / 24.8% CPU · 42 MiB/s · 5963.9 µs CPU/req)</sub> | **2,678**<br><sub>(137 MiB / 45.3% CPU · 669 MiB/s · 676.4 µs CPU/req)</sub> |

#### Linux

Median of **3** repeats @ `4038c6b1`. Source: Actions [41648603685](https://github.com/justcoding121/titanium-web-proxy/actions/runs/41648603685).


| Workload | TWP | nginx | HAProxy | Envoy | YARP |
|---|---:|---:|---:|---:|---:|
| Keep-alive · tiny GET | **25,412**<br><sub>(117 MiB / 48.1% CPU · 75.8 µs CPU/req)</sub> | 🥇 **30,314**<br><sub>(109 MiB / 42.6% CPU · 56.3 µs CPU/req)</sub> | **28,721**<br><sub>(85 MiB / 46.0% CPU · 64.0 µs CPU/req)</sub> | **17,916**<br><sub>(122 MiB / 61.7% CPU · 137.8 µs CPU/req)</sub> | **22,886**<br><sub>(137 MiB / 51.0% CPU · 89.1 µs CPU/req)</sub> |
| New-connection · tiny GET | **1,056**<br><sub>(134 MiB / 0.5% CPU · 18.6 µs CPU/req)</sub> | **1,086**<br><sub>(109 MiB / 0.2% CPU · 9.2 µs CPU/req)</sub> | **1,084**<br><sub>(86 MiB / 0.2% CPU · 9.2 µs CPU/req)</sub> | 🥇 **1,222**<br><sub>(125 MiB / 0.5% CPU · 16.0 µs CPU/req)</sub> | **1,040**<br><sub>(148 MiB)</sub> |
| Keep-alive · 256 KiB GET | **4,954**<br><sub>(132 MiB / 27.6% CPU · 1,238 MiB/s · 223.0 µs CPU/req)</sub> | **3,586**<br><sub>(111 MiB / 37.7% CPU · 896 MiB/s · 420.3 µs CPU/req)</sub> | 🥇 **5,098**<br><sub>(87 MiB / 24.6% CPU · 1,275 MiB/s · 193.0 µs CPU/req)</sub> | **4,501**<br><sub>(141 MiB / 30.8% CPU · 1,125 MiB/s · 273.5 µs CPU/req)</sub> | **3,786**<br><sub>(169 MiB / 40.8% CPU · 947 MiB/s · 431.2 µs CPU/req)</sub> |

#### macOS

Median of **3** repeats on `macos-15` @ `4038c6b1`. Source: Actions [41648603685](https://github.com/justcoding121/titanium-web-proxy/actions/runs/41648603685).

| Workload | TWP | nginx | HAProxy | Envoy | YARP |
|---|---:|---:|---:|---:|---:|
| Keep-alive · tiny GET | **14,359**<br><sub>(127 MiB / 24.5% CPU · 51.3 µs CPU/req)</sub> | 🥇 **20,551**<br><sub>(84 MiB / 22.4% CPU · 32.7 µs CPU/req)</sub> | **17,431**<br><sub>(108 MiB / 22.2% CPU · 38.2 µs CPU/req)</sub> | **16,099**<br><sub>(120 MiB / 37.8% CPU · 70.4 µs CPU/req)</sub> | **17,488**<br><sub>(159 MiB / 29.6% CPU · 50.8 µs CPU/req)</sub> |
| New-connection · tiny GET | **113**<br><sub>(111 MiB / 0.1% CPU · 34.5 µs CPU/req)</sub> | **629**<br><sub>(86 MiB)</sub> | 🥇 **787**<br><sub>(104 MiB)</sub> | **688**<br><sub>(130 MiB / 0.1% CPU · 3.1 µs CPU/req)</sub> | **114**<br><sub>(186 MiB / 0.1% CPU · 13.2 µs CPU/req)</sub> |
| Keep-alive · 256 KiB GET | **1,939**<br><sub>(209 MiB / 23.3% CPU · 485 MiB/s · 360.0 µs CPU/req)</sub> | **1,799**<br><sub>(84 MiB / 18.5% CPU · 450 MiB/s · 308.8 µs CPU/req)</sub> | 🥇 **2,010**<br><sub>(119 MiB / 27.8% CPU · 503 MiB/s · 415.1 µs CPU/req)</sub> | **1,943**<br><sub>(153 MiB / 25.1% CPU · 486 MiB/s · 387.1 µs CPU/req)</sub> | **1,668**<br><sub>(182 MiB / 34.1% CPU · 417 MiB/s · 613.5 µs CPU/req)</sub> |

Prefer **TWP÷YARP** in the table. Absolute RPS on GHA swings hard. New-connection is Darwin SslStream-bound for TWP and YARP (handshake p99 SLO **500 ms** on macOS only — Win/Linux stay at **200 ms**).

## Unary gRPC (H2 TLS)

Unary Echo **RPC/s** @ c=64 over H2 TLS→H2 TLS for Titanium, YARP, nginx (`grpc_pass`), HAProxy, and Envoy (OS-possible peers only). Not folded into the architecture-sensitive tables. Median of **3** repeats @ `4038c6b1` — [41648645024](https://github.com/justcoding121/titanium-web-proxy/actions/runs/41648645024).

| OS | Titanium | YARP | nginx | HAProxy | Envoy |
|---|---:|---:|---:|---:|---:|
| Windows | 🥇 **59,263**<br><sub>(141 MiB / 31.8% CPU · 21.4 µs CPU/req)</sub> | **38,226**<br><sub>(126 MiB / 46.4% CPU · 48.5 µs CPU/req)</sub> | **0**<br><sub>(146 MiB / 24.9% CPU)</sub> | *Not possible* | *Not possible* |
| Linux | 🥇 **34,158**<br><sub>(178 MiB / 32.9% CPU · 38.6 µs CPU/req)</sub> | **20,956**<br><sub>(174 MiB / 42.5% CPU · 81.2 µs CPU/req)</sub> | **0**<br><sub>(112 MiB / 44.7% CPU)</sub> | **16,196**<br><sub>(83 MiB / 45.9% CPU · 113.4 µs CPU/req)</sub> | **15,999**<br><sub>(121 MiB / 58.0% CPU · 145.0 µs CPU/req)</sub> |
| macOS | **9,502**<br><sub>(282 MiB / 14.3% CPU · 45.0 µs CPU/req)</sub> | 🥇 **15,952**<br><sub>(208 MiB / 29.6% CPU · 55.7 µs CPU/req)</sub> | **0**<br><sub>(83 MiB / 3.3% CPU)</sub> | **0**<br><sub>(108 MiB / 23.5% CPU)</sub> | **0**<br><sub>(120 MiB / 25.7% CPU)</sub> |

## Unary gRPC (H2 TLS → h2c)

Unary Echo **RPC/s** @ c=64 over **H2 TLS → h2c** (edge TLS, cleartext H2 origin). Peers: Titanium, YARP, HAProxy, Envoy. Mode: `compare-grpc` (`*-grpc-h2c`). Median of **3** repeats @ `4038c6b1` — [41648645024](https://github.com/justcoding121/titanium-web-proxy/actions/runs/41648645024).

*Not possible:* **nginx** column omitted (no H2 upstream).

| OS | Titanium | YARP | HAProxy | Envoy |
|---|---:|---:|---:|---:|
| Windows | 🥇 **96,412**<br><sub>(140 MiB / 33.0% CPU · 13.7 µs CPU/req)</sub> | **61,597**<br><sub>(122 MiB / 47.2% CPU · 30.7 µs CPU/req)</sub> | *Not possible* | *Not possible* |
| Linux | 🥇 **81,284**<br><sub>(179 MiB / 33.4% CPU · 16.4 µs CPU/req)</sub> | **49,250**<br><sub>(155 MiB / 42.3% CPU · 34.4 µs CPU/req)</sub> | **38,331**<br><sub>(84 MiB / 45.3% CPU · 47.2 µs CPU/req)</sub> | **45,709**<br><sub>(121 MiB / 49.8% CPU · 43.5 µs CPU/req)</sub> |
| macOS | **9,768**<br><sub>(270 MiB / 14.2% CPU · 43.6 µs CPU/req)</sub> | 🥇 **14,907**<br><sub>(220 MiB / 20.9% CPU · 42.1 µs CPU/req)</sub> | **13,170**<br><sub>(118 MiB / 28.1% CPU · 63.9 µs CPU/req)</sub> | **2,631**<br><sub>(116 MiB / 20.0% CPU · 228.5 µs CPU/req)</sub> |

## WebSocket (H1 TLS → H1 TLS)

WebSocket echo round-trips/sec over **dual-TLS** H1 (`proxy_ssl` style). Mode: `compare-ws-h1tls` (`*-duplex-ws-h1tls`). Median of **3** repeats @ `4038c6b1` — [41648666424](https://github.com/justcoding121/titanium-web-proxy/actions/runs/41648666424).

| OS | Titanium | YARP | nginx | HAProxy | Envoy |
|---|---:|---:|---:|---:|---:|
| Windows | 🥇 **38,400**<br><sub>(108 MiB / 44.5% CPU · 46.4 µs CPU/req)</sub> | **35,964**<br><sub>(94 MiB / 43.8% CPU · 48.7 µs CPU/req)</sub> | **18,469**<br><sub>(144 MiB / 24.7% CPU · 53.5 µs CPU/req)</sub> | *Not possible* | *Not possible* |
| Linux | **27,808**<br><sub>(152 MiB / 43.2% CPU · 62.2 µs CPU/req)</sub> | **26,648**<br><sub>(142 MiB / 43.7% CPU · 65.5 µs CPU/req)</sub> | 🥇 **32,014**<br><sub>(113 MiB / 37.2% CPU · 46.5 µs CPU/req)</sub> | **29,249**<br><sub>(86 MiB / 40.9% CPU · 55.9 µs CPU/req)</sub> | **30,122**<br><sub>(122 MiB / 40.4% CPU · 53.6 µs CPU/req)</sub> |
| macOS | **12,236**<br><sub>(158 MiB / 26.7% CPU · 65.5 µs CPU/req)</sub> | **12,820**<br><sub>(144 MiB / 21.6% CPU · 50.5 µs CPU/req)</sub> | 🥇 **14,677**<br><sub>(86 MiB / 18.6% CPU · 38.0 µs CPU/req)</sub> | **11,915**<br><sub>(99 MiB / 21.1% CPU · 53.1 µs CPU/req)</sub> | **5,641**<br><sub>(118 MiB / 21.4% CPU · 113.6 µs CPU/req)</sub> |

## WebSocket (H2 TLS 8441 → H1)

WebSocket echo over **RFC 8441** extended CONNECT (H2 TLS client → H1 plain origin). Mode: `compare-ws-h2` (`*-duplex-ws-h2`).

The probe client sets `NoDelay` and writes each HTTP/2 frame as one TLS record. Median of **3** repeats @ `4038c6b1` — [41648687793](https://github.com/justcoding121/titanium-web-proxy/actions/runs/41648687793).

*Not possible:* **nginx** column omitted (no RFC 8441 extended CONNECT).

| OS | Titanium | YARP | HAProxy | Envoy |
|---|---:|---:|---:|---:|
| Windows | 🥇 **47,642**<br><sub>(175 MiB / 42.9% CPU · 36.0 µs CPU/req)</sub> | **0**<br><sub>(192 MiB / 17.2% CPU)</sub> | *Not possible* | *Not possible* |
| Linux | 🥇 **31,995**<br><sub>(205 MiB / 47.9% CPU · 59.8 µs CPU/req)</sub> | **0**<br><sub>(202 MiB / 53.8% CPU)</sub> | **25,852**<br><sub>(85 MiB / 42.7% CPU · 66.1 µs CPU/req)</sub> | **0**<br><sub>(119 MiB / 0.3% CPU)</sub> |
| macOS | **16,716**<br><sub>(282 MiB / 29.4% CPU · 52.7 µs CPU/req)</sub> | **0**<br><sub>(177 MiB / 10.3% CPU)</sub> | 🥇 **18,595**<br><sub>(111 MiB / 28.6% CPU · 46.1 µs CPU/req)</sub> | **0**<br><sub>(115 MiB / 0.5% CPU)</sub> |

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

`--arm-shard` takes a comparison-group key (`h1c-h1c`, from `--print-groups`), not an `i/n` index, so a re-run measures the same row after a peer install flake. `i/n` remains for smoke modes. Paste unions `rps-csv-<os>-shard-*` (`paste-compare-product-wiki.ps1 -RunIds …`). Table “Source: Actions” may list several run URLs for one table — **do not mix SHAs**. A single-row leg allows **60** minutes of ramp and **75** minutes overall. Free GitHub accounts run **20** jobs at once, **5** of them macOS. Give a full suite a quiet window: do not push pull requests or start other macOS jobs while it runs. Re-run infrastructure failures only (`gh run rerun --failed`). A failed gate is a finding and is never re-rolled; publish the last attempt, never the best of several. The RPS tables on this page are @ `4038c6b1`: product [41648502269](https://github.com/justcoding121/titanium-web-proxy/actions/runs/41648502269), saturation [41648522721](https://github.com/justcoding121/titanium-web-proxy/actions/runs/41648522721), bodies [41648543503](https://github.com/justcoding121/titanium-web-proxy/actions/runs/41648543503), POST [41648564004](https://github.com/justcoding121/titanium-web-proxy/actions/runs/41648564004), lossy [41648583393](https://github.com/justcoding121/titanium-web-proxy/actions/runs/41648583393), arch [41648624388](https://github.com/justcoding121/titanium-web-proxy/actions/runs/41648624388), TLS cost [41648603685](https://github.com/justcoding121/titanium-web-proxy/actions/runs/41648603685), gRPC [41648645024](https://github.com/justcoding121/titanium-web-proxy/actions/runs/41648645024), WebSocket H1 TLS [41648666424](https://github.com/justcoding121/titanium-web-proxy/actions/runs/41648666424), WebSocket H2 [41648687793](https://github.com/justcoding121/titanium-web-proxy/actions/runs/41648687793), editions [41648708854](https://github.com/justcoding121/titanium-web-proxy/actions/runs/41648708854). All eleven suite aggregates were green. The prior refresh @ `e0c98136` remains historical only. Older notes on the `8567b4c0` bodies gate miss and the `88187363` cluster remain historical only. Reverse product rows, bodies, POST, lossy, arch, TLS cost, gRPC, both WebSocket modes, and saturation run on Windows, Linux, and macOS. TWP MITM arms run on Linux only. `compare-editions` (CLI and Plus) runs on Linux only. `compare-spot` on pull requests to beta and stable stays on all three OS. The historical v2 MITM note below the product tables stays @ `41f4adee` because those run ids are from that fix, not this re-measure.

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
