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
- **Typical reverse** is H1 TLS→H1 or H2→H1 (~1.1× YARP on Win/Linux tiny GET). With **larger bodies**, see [Heavier reverse](#user-content-heavier-reverse-workloads) (@ `8567b4c0`).

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

On the Linux H2 TLS→H1 plain row at c=64 (16 connections, @ `8567b4c0`), proxy CPU was about **44%** for nginx, **45%** for HAProxy, and **55%** for Envoy. Windows nginx on the same H2 client shape stayed near **25%** (one core). That measured shape is the one published here.

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

Calibration for the shared 4 vCPU loopback shape: how close client + origin are to saturated before ranking reverse peers. Tiny keep-alive GET. Median of **3** repeats @ `8567b4c0` — [40610501155](https://github.com/justcoding121/titanium-web-proxy/actions/runs/40610501155). Warmup 2s / measure 8s; concurrency 8, 16, 32, 64. Block A **% of origin-HttpClient** uses median **peak** RPS. Blocks B/C use peer÷YARP / ÷nginx on median peak (not % of H1 origin). **RPS cells** embed median RSS / CPU for the **proxy child** plus its **full descendant tree** (serve-proxy → nginx master → workers); origin-direct samples the **origin** child. Product matrices below use matched `dotnet-httpclient` only (not bombardier).


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
| origin-direct | dotnet-httpclient | **50,719**<br><sub>(55 MiB / 39.6% CPU · 31.2 µs CPU/req)</sub> | **100.0%** |
| origin-direct-bombardier | bombardier | **36,488**<br><sub>(56 MiB / 21.9% CPU · 24.0 µs CPU/req)</sub> | **71.9%** |
| bare-reverse-http1 | dotnet-httpclient | **25,158**<br><sub>(59 MiB / 44.7% CPU · 71.1 µs CPU/req)</sub> | **49.6%** |
| nginx-reverse-http1 | dotnet-httpclient | **13,453**<br><sub>(127 MiB / 24.8% CPU · 73.8 µs CPU/req)</sub> | **26.5%** |
| yarp-reverse-http1 | dotnet-httpclient | **20,973**<br><sub>(87 MiB / 50.0% CPU · 95.4 µs CPU/req)</sub> | **41.4%** |
| twp-reverse-http1 | dotnet-httpclient | 🥇 **24,885**<br><sub>(78 MiB / 48.0% CPU · 77.1 µs CPU/req)</sub> | **49.1%** |

**Linux** (`ubuntu-latest`)

| Arm | Generator | RPS | % of origin-HttpClient |
|---|---|---:|---:|
| origin-direct | dotnet-httpclient | **150,393**<br><sub>(89 MiB / 44.6% CPU · 11.9 µs CPU/req)</sub> | **100.0%** |
| origin-direct-bombardier | bombardier | **93,145**<br><sub>(89 MiB / 38.2% CPU · 16.4 µs CPU/req)</sub> | **61.9%** |
| bare-reverse-http1 | dotnet-httpclient | **66,537**<br><sub>(73 MiB / 46.1% CPU · 27.7 µs CPU/req)</sub> | **44.2%** |
| nginx-reverse-http1 | dotnet-httpclient | 🥇 **84,226**<br><sub>(84 MiB / 42.2% CPU · 20.1 µs CPU/req)</sub> | **56.0%** |
| yarp-reverse-http1 | dotnet-httpclient | **63,760**<br><sub>(128 MiB / 47.6% CPU · 29.9 µs CPU/req)</sub> | **42.4%** |
| twp-reverse-http1 | dotnet-httpclient | **71,638**<br><sub>(91 MiB / 49.0% CPU · 27.4 µs CPU/req)</sub> | **47.6%** |


**macOS** (`macos-15`, Apple Silicon)

| Arm | Generator | RPS | % of origin-HttpClient |
|---|---|---:|---:|
| origin-direct | dotnet-httpclient | **56,094**<br><sub>(89 MiB / 27.3% CPU · 19.5 µs CPU/req)</sub> | **100.0%** |
| origin-direct-bombardier | bombardier | **46,288**<br><sub>(89 MiB / 22.7% CPU · 19.7 µs CPU/req)</sub> | **82.5%** |
| bare-reverse-http1 | dotnet-httpclient | **16,018**<br><sub>(91 MiB / 18.8% CPU · 46.9 µs CPU/req)</sub> | **28.6%** |
| nginx-reverse-http1 | dotnet-httpclient | 🥇 **21,285**<br><sub>(68 MiB / 19.3% CPU · 36.2 µs CPU/req)</sub> | **37.9%** |
| yarp-reverse-http1 | dotnet-httpclient | **4,853**<br><sub>(172 MiB / 10.6% CPU · 87.0 µs CPU/req)</sub> | **8.7%** |
| twp-reverse-http1 | dotnet-httpclient | **17,056**<br><sub>(116 MiB / 23.8% CPU · 55.8 µs CPU/req)</sub> | **30.4%** |

On this macOS run `origin-direct` was **27.3%** CPU, so the % of origin-HttpClient column is not a ceiling when that CPU is near idle. Rank those Mac peers by RPS.

Reverse peers on Block A @ `8567b4c0`: Windows TWP is **49.1%** of origin-direct and Linux TWP is **47.6%**. Prefer the **%** column over absolute RPS across runs. Bare and origin-direct are controls (not medal peers).

#### Block B — H2 TLS→H1

Peer ratios (÷YARP / ÷nginx) on median peak; **RPS cells** embed `(MiB / CPU%)`.

**Windows** (`windows-latest`)

| Arm | Generator | RPS | ÷YARP | ÷nginx |
|---|---|---:|---:|---:|
| nginx-reverse-http2 | dotnet-httpclient | **7,615**<br><sub>(144 MiB / 24.9% CPU · 130.5 µs CPU/req)</sub> | **0.28×** | **1.00×** |
| yarp-reverse-http2 | dotnet-httpclient | **27,426**<br><sub>(105 MiB / 54.6% CPU · 79.6 µs CPU/req)</sub> | **1.00×** | **3.60×** |
| twp-reverse-http2-cleartext | dotnet-httpclient | 🥇 **29,827**<br><sub>(135 MiB / 52.7% CPU · 70.6 µs CPU/req)</sub> | **1.09×** | **3.92×** |

**Linux** (`ubuntu-latest`)

| Arm | Generator | RPS | ÷YARP | ÷nginx |
|---|---|---:|---:|---:|
| nginx-reverse-http2 | dotnet-httpclient | **60,786**<br><sub>(115 MiB / 44.8% CPU · 29.5 µs CPU/req)</sub> | **0.90×** | **1.00×** |
| yarp-reverse-http2 | dotnet-httpclient | **67,224**<br><sub>(146 MiB / 47.5% CPU · 28.3 µs CPU/req)</sub> | **1.00×** | **1.11×** |
| twp-reverse-http2-cleartext | dotnet-httpclient | 🥇 **71,324**<br><sub>(163 MiB / 51.5% CPU · 28.9 µs CPU/req)</sub> | **1.06×** | **1.17×** |

**macOS** (`macos-15`, Apple Silicon)

| Arm | Generator | RPS | ÷YARP | ÷nginx |
|---|---|---:|---:|---:|
| nginx-reverse-http2 | dotnet-httpclient | **20,164**<br><sub>(83 MiB / 23.0% CPU · 45.5 µs CPU/req)</sub> | **0.52×** | **1.00×** |
| yarp-reverse-http2 | dotnet-httpclient | 🥇 **38,646**<br><sub>(158 MiB / 42.2% CPU · 43.7 µs CPU/req)</sub> | **1.00×** | **1.92×** |
| twp-reverse-http2-cleartext | dotnet-httpclient | **37,266**<br><sub>(177 MiB / 39.7% CPU · 42.6 µs CPU/req)</sub> | **0.96×** | **1.85×** |
#### Block C — H3→H1

Same layout as Block B. Requires QuicListener. nginx needs `http_v3_module` (Windows nginx has no QUIC). HAProxy needs `USE_QUIC` (GHA Linux/macOS require it). Envoy 1.20+ includes HTTP/3.

Medals are nginx / YARP / Titanium only, from the same run as Blocks A and B ([40610501155](https://github.com/justcoding121/titanium-web-proxy/actions/runs/40610501155)).

**Windows** (`windows-latest`)

| Arm | Generator | RPS | ÷YARP | ÷nginx |
|---|---|---:|---:|---:|
| nginx-reverse-http3-cleartext | dotnet-httpclient | *Not possible (no QUIC)* | — | — |
| yarp-reverse-http3-cleartext | dotnet-httpclient | **8,008**<br><sub>(153 MiB / 51.0% CPU · 254.5 µs CPU/req)</sub> | **1.00×** | — |
| twp-reverse-http3-cleartext | dotnet-httpclient | 🥇 **8,943**<br><sub>(111 MiB / 47.1% CPU · 210.5 µs CPU/req)</sub> | **1.12×** | — |

**Linux** (`ubuntu-latest`)

| Arm | Generator | RPS | ÷YARP | ÷nginx |
|---|---|---:|---:|---:|
| nginx-reverse-http3-cleartext | dotnet-httpclient | **41,307**<br><sub>(peak 51,736 · 119 MiB / 29.9% CPU · 29.0 µs CPU/req)</sub> | **1.48×** | **1.00×** |
| yarp-reverse-http3-cleartext | dotnet-httpclient | **34,900**<br><sub>(222 MiB / 48.3% CPU · 55.4 µs CPU/req)</sub> | **1.00×** | **0.67×** |
| twp-reverse-http3-cleartext | dotnet-httpclient | 🥇 **42,663**<br><sub>(181 MiB / 52.5% CPU · 49.3 µs CPU/req)</sub> | **1.22×** | **0.82×** |

**macOS** (`macos-15`, Apple Silicon)

| Arm | Generator | RPS | ÷YARP | ÷nginx |
|---|---|---:|---:|---:|
| nginx-reverse-http3-cleartext | dotnet-httpclient | **6,944**<br><sub>(81 MiB / 17.8% CPU · 102.7 µs CPU/req)</sub> | **0.65×** | **1.00×** |
| yarp-reverse-http3-cleartext | dotnet-httpclient | **10,655**<br><sub>(211 MiB / 44.2% CPU · 166.1 µs CPU/req)</sub> | **1.00×** | **1.53×** |
| twp-reverse-http3-cleartext | dotnet-httpclient | 🥇 **12,020**<br><sub>(142 MiB / 43.4% CPU · 144.6 µs CPU/req)</sub> | **1.13×** | **1.73×** |

## Windows — Titanium vs nginx vs HAProxy vs Envoy vs YARP

Client / origin: HTTP version and whether TLS is used (`plain` = cleartext, `TLS` = encrypted, `QUIC` = HTTP/3).

### Reverse

Median of **3 repeats** on `windows-latest` (4 vCPU / 16 GiB). Bare reverse 5×5 @ `8567b4c0` — `compare-product` [40610469359](https://github.com/justcoding121/titanium-web-proxy/actions/runs/40610469359). Warmup 2s / measure 8s; concurrency 8, 16, 32, 64. Prefer TWP÷peer ratios over absolute RPS. **RPS cells** include median RSS / CPU at the peak-RPS step as `<br><sub>(MiB / CPU%)</sub>`. nginx terminate peers use `keepalive 256` + streaming buffers. **HAProxy / Envoy are Linux-only peers** (no official Windows port). Laptop High-perf / cool-paired numbers stay on the [local lab](Performance-Local-Lab). Product 5×5 is **~56-byte JSON keep-alive GET**; H2/H3 same-protocol cells are mostly header work with a tiny body (Titanium best case) — see [Why this comparison is fair](#user-content-why-this-comparison-is-fair).

**Load generators:** Reverse inbound H3 arms use **`dotnet-httpclient`** (`http_version=3.0`, `RequestVersionExact`). nginx/Windows is same-OS only (no QUIC). HAProxy/Envoy are Linux-only terminate peers.

| Client | Origin | TWP | nginx | YARP |
|---|---|---:|---:|---:|
| HTTP/1 · plain | HTTP/1 · plain | 🥇 **57,613**<br><sub>(77 MiB / 48.9% CPU · 33.9 µs CPU/req)</sub> | **34,223**<br><sub>(127 MiB / 24.8% CPU · 29.0 µs CPU/req)</sub> | **50,898**<br><sub>(89 MiB / 47% CPU · 37.0 µs CPU/req)</sub> |
| HTTP/1 · plain | HTTP/1 · TLS | 🥇 **20,795**<br><sub>(90 MiB / 52.2% CPU · 100.4 µs CPU/req)</sub> | **8,478**<br><sub>(137 MiB / 24.8% CPU · 117.0 µs CPU/req)</sub> | **19,078**<br><sub>(97 MiB / 51.2% CPU · 107.5 µs CPU/req)</sub> |
| HTTP/1 · plain | HTTP/2 · plain | 🥇 **37,763**<br><sub>(104 MiB / 45.4% CPU · 48.1 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **33,256**<br><sub>(99 MiB / 49% CPU · 58.9 µs CPU/req)</sub> |
| HTTP/1 · plain | HTTP/2 · TLS | 🥇 **32,630**<br><sub>(114 MiB / 46% CPU · 56.4 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **29,982**<br><sub>(99 MiB / 47.7% CPU · 63.6 µs CPU/req)</sub> |
| HTTP/1 · plain | HTTP/3 · QUIC | 🥇 **18,222**<br><sub>(114 MiB / 53% CPU · 116.4 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | **18,002**<br><sub>(120 MiB / 50.8% CPU · 112.9 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/1 · plain | 🥇 **19,982**<br><sub>(92 MiB / 49.3% CPU · 98.7 µs CPU/req)</sub> | **8,657**<br><sub>(144 MiB / 24.7% CPU · 114.1 µs CPU/req)</sub> | **17,847**<br><sub>(104 MiB / 48.5% CPU · 108.7 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/1 · TLS | 🥇 **18,287**<br><sub>(91 MiB / 48.5% CPU · 106.1 µs CPU/req)</sub> | **6,641**<br><sub>(145 MiB / 24.8% CPU · 149.4 µs CPU/req)</sub> | **16,566**<br><sub>(106 MiB / 48.8% CPU · 117.9 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/2 · plain | 🥇 **29,957**<br><sub>(115 MiB / 45.5% CPU · 60.8 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **27,829**<br><sub>(117 MiB / 47.7% CPU · 68.5 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/2 · TLS | 🥇 **25,404**<br><sub>(118 MiB / 46% CPU · 72.4 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **24,364**<br><sub>(115 MiB / 47.6% CPU · 78.1 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/3 · QUIC | 🥇 **16,660**<br><sub>(114 MiB / 49.8% CPU · 119.5 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | **15,918**<br><sub>(128 MiB / 51.4% CPU · 129.0 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | 🥇 **32,171**<br><sub>(110 MiB / 50.1% CPU · 62.2 µs CPU/req)</sub> | **8,759**<br><sub>(128 MiB / 24.8% CPU · 113.0 µs CPU/req)</sub> | **29,401**<br><sub>(96 MiB / 53.6% CPU · 73.0 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/1 · TLS | 🥇 **43,735**<br><sub>(133 MiB / 51.3% CPU · 46.9 µs CPU/req)</sub> | **11,624**<br><sub>(140 MiB / 24.8% CPU · 85.3 µs CPU/req)</sub> | **40,687**<br><sub>(101 MiB / 49.7% CPU · 48.9 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/2 · plain | 🥇 **100,794**<br><sub>(100 MiB / 34.4% CPU · 13.6 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **73,163**<br><sub>(105 MiB / 52.6% CPU · 28.7 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/2 · TLS | 🥇 **60,473**<br><sub>(113 MiB / 36.2% CPU · 24.0 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **45,292**<br><sub>(104 MiB / 49.6% CPU · 43.8 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/3 · QUIC | 🥇 **25,493**<br><sub>(155 MiB / 51.9% CPU · 81.5 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | **23,839**<br><sub>(119 MiB / 51.8% CPU · 87.0 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **30,198**<br><sub>(117 MiB / 54.3% CPU · 72.0 µs CPU/req)</sub> | **7,634**<br><sub>(144 MiB / 24.9% CPU · 130.5 µs CPU/req)</sub> | **27,638**<br><sub>(107 MiB / 54.3% CPU · 78.6 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/1 · TLS | 🥇 **66,119**<br><sub>(132 MiB / 50.7% CPU · 30.7 µs CPU/req)</sub> | **17,774**<br><sub>(146 MiB / 24.9% CPU · 56.1 µs CPU/req)</sub> | **63,872**<br><sub>(108 MiB / 47.5% CPU · 29.7 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | 🥇 **92,409**<br><sub>(130 MiB / 35.6% CPU · 15.4 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **66,920**<br><sub>(106 MiB / 51% CPU · 30.5 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **53,947**<br><sub>(126 MiB / 37.4% CPU · 27.8 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **40,524**<br><sub>(108 MiB / 48.5% CPU · 47.8 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/3 · QUIC | 🥇 **22,890**<br><sub>(147 MiB / 53.4% CPU · 93.2 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | **21,297**<br><sub>(127 MiB / 51.8% CPU · 97.3 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **9,109**<br><sub>(116 MiB / 47.3% CPU · 207.7 µs CPU/req)</sub> | *Not possible (no QUIC)* | **7,865**<br><sub>(153 MiB / 52.4% CPU · 266.4 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **8,353**<br><sub>(113 MiB / 51.2% CPU · 245.1 µs CPU/req)</sub> | *Not possible (no QUIC)* | **7,546**<br><sub>(161 MiB / 51% CPU · 270.1 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/2 · plain | 🥇 **14,168**<br><sub>(119 MiB / 48.1% CPU · 135.8 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **12,487**<br><sub>(160 MiB / 50.4% CPU · 161.4 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | 🥇 **10,658**<br><sub>(121 MiB / 46.3% CPU · 173.9 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **9,321**<br><sub>(163 MiB / 49.5% CPU · 212.5 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/3 · QUIC | 🥇 **13,955**<br><sub>(121 MiB / 46.9% CPU · 134.3 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | **12,708**<br><sub>(171 MiB / 50.4% CPU · 158.6 µs CPU/req)</sub> |

## Linux — Titanium vs nginx vs HAProxy vs Envoy vs YARP

### Reverse

Median of **3 repeats** on `ubuntu-latest` (4 vCPU / 16 GiB). Bare reverse 5×5 @ `8567b4c0` — `compare-product` [40610469359](https://github.com/justcoding121/titanium-web-proxy/actions/runs/40610469359). Warmup 2s / measure 8s; concurrency 8, 16, 32, 64. **Linux nginx is the authoritative nginx baseline.** HAProxy (3.2 `USE_QUIC`) and Envoy (GitHub release, HTTP/3 compiled in) run on the same loopback shape as nginx/YARP. nginx terminate peers use `keepalive 256` + streaming buffers. The RPS workflow installs nginx.org mainline (`http_v3_module`), a QUIC-enabled HAProxy, Envoy, and `libmsquic`. Prefer ratios over absolute RPS. Product 5×5 is **~56-byte JSON keep-alive GET**; H2/H3 same-protocol cells are mostly header work with a tiny body (Titanium best case) — see [Why this comparison is fair](#user-content-why-this-comparison-is-fair).

| Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---:|---:|---:|---:|---:|
| HTTP/1 · plain | HTTP/1 · plain | **33,926**<br><sub>(89 MiB / 48.6% CPU · 57.3 µs CPU/req)</sub> | 🥇 **42,131**<br><sub>(83 MiB / 42.1% CPU · 40.0 µs CPU/req)</sub> | **36,271**<br><sub>(69 MiB / 45.9% CPU · 50.6 µs CPU/req)</sub> | **20,694**<br><sub>(115 MiB / 65% CPU · 125.6 µs CPU/req)</sub> | **30,814**<br><sub>(128 MiB / 50.2% CPU · 65.1 µs CPU/req)</sub> |
| HTTP/1 · plain | HTTP/1 · TLS | **40,611**<br><sub>(114 MiB / 47.4% CPU · 46.7 µs CPU/req)</sub> | 🥇 **48,441**<br><sub>(106 MiB / 41.9% CPU · 34.6 µs CPU/req)</sub> | **44,230**<br><sub>(74 MiB / 44.8% CPU · 40.5 µs CPU/req)</sub> | **30,278**<br><sub>(116 MiB / 59% CPU · 77.9 µs CPU/req)</sub> | **37,084**<br><sub>(143 MiB / 48.8% CPU · 52.6 µs CPU/req)</sub> |
| HTTP/1 · plain | HTTP/2 · plain | 🥇 **42,579**<br><sub>(126 MiB / 49.2% CPU · 46.3 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **26,163**<br><sub>(65 MiB / 43.9% CPU · 67.2 µs CPU/req)</sub> | **21,823**<br><sub>(110 MiB / 64.2% CPU · 117.7 µs CPU/req)</sub> | **39,304**<br><sub>(131 MiB / 48.9% CPU · 49.8 µs CPU/req)</sub> |
| HTTP/1 · plain | HTTP/2 · TLS | 🥇 **69,554**<br><sub>(171 MiB / 46.1% CPU · 26.5 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **59,350**<br><sub>(71 MiB / 46.1% CPU · 31.1 µs CPU/req)</sub> | **55,386**<br><sub>(115 MiB / 54.6% CPU · 39.4 µs CPU/req)</sub> | **66,551**<br><sub>(148 MiB / 44.7% CPU · 26.9 µs CPU/req)</sub> |
| HTTP/1 · plain | HTTP/3 · QUIC | 🥇 **24,029**<br><sub>(149 MiB / 51.6% CPU · 85.9 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **10,833**<br><sub>(119 MiB / 58.5% CPU · 215.9 µs CPU/req)</sub> | **23,368**<br><sub>(169 MiB / 49.1% CPU · 84.1 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/1 · plain | **39,579**<br><sub>(112 MiB / 47.8% CPU · 48.3 µs CPU/req)</sub> | 🥇 **48,528**<br><sub>(110 MiB / 41.7% CPU · 34.4 µs CPU/req)</sub> | **45,456**<br><sub>(84 MiB / 45.9% CPU · 40.4 µs CPU/req)</sub> | **29,162**<br><sub>(121 MiB / 58.9% CPU · 80.8 µs CPU/req)</sub> | **36,187**<br><sub>(135 MiB / 49.9% CPU · 55.2 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/1 · TLS | **21,170**<br><sub>(122 MiB / 47.9% CPU · 90.5 µs CPU/req)</sub> | 🥇 **25,249**<br><sub>(119 MiB / 42.7% CPU · 67.6 µs CPU/req)</sub> | **23,821**<br><sub>(90 MiB / 45.9% CPU · 77.0 µs CPU/req)</sub> | **16,442**<br><sub>(127 MiB / 58.7% CPU · 142.8 µs CPU/req)</sub> | **19,166**<br><sub>(156 MiB / 50.7% CPU · 105.8 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/2 · plain | 🥇 **36,299**<br><sub>(147 MiB / 51.1% CPU · 56.3 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **25,693**<br><sub>(89 MiB / 45.4% CPU · 70.7 µs CPU/req)</sub> | **24,440**<br><sub>(127 MiB / 60.2% CPU · 98.6 µs CPU/req)</sub> | **34,729**<br><sub>(159 MiB / 49.6% CPU · 57.1 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/2 · TLS | **61,432**<br><sub>(179 MiB / 47.4% CPU · 30.8 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **58,210**<br><sub>(87 MiB / 46% CPU · 31.6 µs CPU/req)</sub> | **53,680**<br><sub>(121 MiB / 53.4% CPU · 39.8 µs CPU/req)</sub> | 🥇 **62,361**<br><sub>(151 MiB / 47% CPU · 30.2 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/3 · QUIC | 🥇 **18,463**<br><sub>(158 MiB / 52.4% CPU · 113.5 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **10,031**<br><sub>(130 MiB / 56.9% CPU · 227.0 µs CPU/req)</sub> | **17,795**<br><sub>(176 MiB / 49.9% CPU · 112.3 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | 🥇 **74,591**<br><sub>(157 MiB / 51.7% CPU · 27.7 µs CPU/req)</sub> | **54,222**<br><sub>(87 MiB / 46.5% CPU · 34.3 µs CPU/req)</sub> | **70,307**<br><sub>(69 MiB / 44% CPU · 25.0 µs CPU/req)</sub> | **53,997**<br><sub>(115 MiB / 54.5% CPU · 40.4 µs CPU/req)</sub> | **66,208**<br><sub>(137 MiB / 47.4% CPU · 28.6 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/1 · TLS | **36,752**<br><sub>(160 MiB / 47.1% CPU · 51.2 µs CPU/req)</sub> | **38,587**<br><sub>(112 MiB / 43.2% CPU · 44.8 µs CPU/req)</sub> | 🥇 **43,726**<br><sub>(75 MiB / 44.2% CPU · 40.4 µs CPU/req)</sub> | **24,952**<br><sub>(116 MiB / 60.9% CPU · 97.7 µs CPU/req)</sub> | **35,782**<br><sub>(147 MiB / 47.8% CPU · 53.5 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/2 · plain | 🥇 **74,679**<br><sub>(153 MiB / 39.2% CPU · 21.0 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **38,190**<br><sub>(70 MiB / 46.3% CPU · 48.5 µs CPU/req)</sub> | **22,834**<br><sub>(114 MiB / 61.2% CPU · 107.3 µs CPU/req)</sub> | **46,980**<br><sub>(144 MiB / 48.5% CPU · 41.3 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/2 · TLS | 🥇 **79,736**<br><sub>(160 MiB / 38.9% CPU · 19.5 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **46,520**<br><sub>(72 MiB / 46.9% CPU · 40.3 µs CPU/req)</sub> | **38,366**<br><sub>(115 MiB / 57.3% CPU · 59.7 µs CPU/req)</sub> | **58,460**<br><sub>(152 MiB / 45.8% CPU · 31.3 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/3 · QUIC | 🥇 **30,583**<br><sub>(180 MiB / 50.6% CPU · 66.2 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **12,452**<br><sub>(118 MiB / 56.2% CPU · 180.7 µs CPU/req)</sub> | **28,524**<br><sub>(171 MiB / 46.7% CPU · 65.4 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **66,104**<br><sub>(163 MiB / 50.8% CPU · 30.7 µs CPU/req)</sub> | **55,224**<br><sub>(110 MiB / 43.8% CPU · 31.7 µs CPU/req)</sub> | **64,204**<br><sub>(82 MiB / 45.4% CPU · 28.3 µs CPU/req)</sub> | **53,088**<br><sub>(122 MiB / 54.7% CPU · 41.2 µs CPU/req)</sub> | **62,304**<br><sub>(138 MiB / 47.5% CPU · 30.5 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/1 · TLS | **25,778**<br><sub>(158 MiB / 49.3% CPU · 76.5 µs CPU/req)</sub> | **22,692**<br><sub>(122 MiB / 44.8% CPU · 79.0 µs CPU/req)</sub> | 🥇 **26,350**<br><sub>(89 MiB / 46.5% CPU · 70.6 µs CPU/req)</sub> | **17,052**<br><sub>(127 MiB / 62.2% CPU · 145.8 µs CPU/req)</sub> | **23,560**<br><sub>(156 MiB / 50.6% CPU · 85.8 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | 🥇 **61,641**<br><sub>(165 MiB / 40.1% CPU · 26.0 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **31,300**<br><sub>(83 MiB / 48.6% CPU · 62.1 µs CPU/req)</sub> | **24,233**<br><sub>(121 MiB / 61% CPU · 100.7 µs CPU/req)</sub> | **40,286**<br><sub>(144 MiB / 46.4% CPU · 46.1 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **64,316**<br><sub>(174 MiB / 38.4% CPU · 23.9 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **40,470**<br><sub>(88 MiB / 46.2% CPU · 45.7 µs CPU/req)</sub> | **33,019**<br><sub>(126 MiB / 55.3% CPU · 67.0 µs CPU/req)</sub> | **46,059**<br><sub>(156 MiB / 46.2% CPU · 40.1 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/3 · QUIC | 🥇 **36,322**<br><sub>(200 MiB / 50.2% CPU · 55.2 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **14,671**<br><sub>(129 MiB / 55.8% CPU · 152.0 µs CPU/req)</sub> | **33,132**<br><sub>(180 MiB / 47.2% CPU · 57.0 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | **25,575**<br><sub>(168 MiB / 51.2% CPU · 80.0 µs CPU/req)</sub> | **35,683**<br><sub>(121 MiB / 26.9% CPU · 30.1 µs CPU/req)</sub> | 🥇 **37,236**<br><sub>(94 MiB / 32.4% CPU · 34.9 µs CPU/req)</sub> | **14,174**<br><sub>(133 MiB / 52.3% CPU · 147.5 µs CPU/req)</sub> | **21,729**<br><sub>(215 MiB / 48.3% CPU · 88.9 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | **30,547**<br><sub>(185 MiB / 50.2% CPU · 65.7 µs CPU/req)</sub> | **38,370**<br><sub>(123 MiB / 32.6% CPU · 34.0 µs CPU/req)</sub> | 🥇 **42,276**<br><sub>(90 MiB / 35.6% CPU · 33.7 µs CPU/req)</sub> | **18,257**<br><sub>(128 MiB / 49.8% CPU · 109.2 µs CPU/req)</sub> | **25,585**<br><sub>(220 MiB / 47.7% CPU · 74.6 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/2 · plain | **21,951**<br><sub>(161 MiB / 51.6% CPU · 93.9 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | 🥇 **24,234**<br><sub>(94 MiB / 36.2% CPU · 59.8 µs CPU/req)</sub> | **10,129**<br><sub>(133 MiB / 60.1% CPU · 237.4 µs CPU/req)</sub> | **19,552**<br><sub>(217 MiB / 50.7% CPU · 103.8 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | 🥇 **20,938**<br><sub>(169 MiB / 52% CPU · 99.3 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **20,632**<br><sub>(94 MiB / 35.8% CPU · 69.4 µs CPU/req)</sub> | **9,561**<br><sub>(133 MiB / 59.9% CPU · 250.6 µs CPU/req)</sub> | **18,036**<br><sub>(219 MiB / 49.6% CPU · 110.0 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/3 · QUIC | 🥇 **25,605**<br><sub>(173 MiB / 49.4% CPU · 77.1 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **9,233**<br><sub>(127 MiB / 53% CPU · 229.6 µs CPU/req)</sub> | **19,553**<br><sub>(216 MiB / 47.3% CPU · 96.8 µs CPU/req)</sub> |

### MITM (TWP only)

Same Client×Origin wires with interception on (`compare-product` [40610469359](https://github.com/justcoding121/titanium-web-proxy/actions/runs/40610469359)). **Lite** = no-op handlers (unchanged-lite finish). **Full** = append-only header mutation (harness: one probe header each way; product: generic append-only relay via `MitmCompressedRelayHelper`). nginx/HAProxy/Envoy/YARP cannot MITM. **Lite÷Reverse** / **Full÷Reverse** vs bare reverse (**same job / comparison-group shard**). Completion gate: Lite ≥ **0.25×** and Full ≥ **0.25×** reverse sustain @ c=64 (median of 3 GHA runs); reverse TWP ÷ closest peer ≥ **0.40×**.

**v1 append-only relay (2026-08-27):** Pre-fix H2→H2 Full÷Reverse was **0.13–0.16×** ([32960766249](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32960766249)). Post-fix @ `df172718`: H2 plain→H2 plain Full **0.77–0.79×**, H3→H1 Full **0.91–0.93×**, all MITM arms ≥ **0.70×** on median of [33041445371](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33041445371), [33055267086](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33055267086), [33055272140](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33055272140).

**v2 drop-only + non-unique append (2026-08-27):** `MitmStaticRebuildHelper` rebuilds static HPACK/QPACK after 1–4 unique header drops; trailing non-unique appends stay on compressed relay. @ `41f4adee`: all MITM arms ≥ **0.70×** on GHA median ([33087088466](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087088466), [33087091622](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087091622), [33105885748](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33105885748) Linux; [33087085235](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087085235), [33087088466](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087088466), [33087091622](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087091622) Windows). H2 plain→H2 plain Full **0.77–0.79×** (Win) / **0.78×** (Lin).

| Client | Origin | Lite sustain | Full sustain | Lite÷Reverse | Full÷Reverse |
|---|---|---:|---:|---:|---:|
| HTTP/1 · plain | HTTP/1 · plain | **34,190**<br><sub>(100 MiB / 50% CPU · 58.5 µs CPU/req)</sub> | **33,237**<br><sub>(98 MiB / 49.9% CPU · 60.0 µs CPU/req)</sub> | **1.01×** | **0.98×** |
| HTTP/1 · plain | HTTP/1 · TLS | **38,761**<br><sub>(117 MiB / 47.4% CPU · 48.9 µs CPU/req)</sub> | **38,617**<br><sub>(121 MiB / 47.7% CPU · 49.4 µs CPU/req)</sub> | **0.95×** | **0.95×** |
| HTTP/1 · plain | HTTP/2 · plain | **41,325**<br><sub>(131 MiB / 51.3% CPU · 49.6 µs CPU/req)</sub> | **38,942**<br><sub>(133 MiB / 49.1% CPU · 50.5 µs CPU/req)</sub> | **0.97×** | **0.91×** |
| HTTP/1 · plain | HTTP/2 · TLS | **68,920**<br><sub>(163 MiB / 48.9% CPU · 28.4 µs CPU/req)</sub> | **68,095**<br><sub>(171 MiB / 48.9% CPU · 28.7 µs CPU/req)</sub> | **0.99×** | **0.98×** |
| HTTP/1 · plain | HTTP/3 · QUIC | **23,934**<br><sub>(144 MiB / 54.2% CPU · 90.6 µs CPU/req)</sub> | **23,245**<br><sub>(149 MiB / 53.3% CPU · 91.6 µs CPU/req)</sub> | **1×** | **0.97×** |
| HTTP/1 · TLS | HTTP/1 · plain | **39,889**<br><sub>(121 MiB / 49.3% CPU · 49.5 µs CPU/req)</sub> | **38,741**<br><sub>(118 MiB / 48.3% CPU · 49.9 µs CPU/req)</sub> | **1.01×** | **0.98×** |
| HTTP/1 · TLS | HTTP/1 · TLS | **21,072**<br><sub>(127 MiB / 48.8% CPU · 92.7 µs CPU/req)</sub> | **20,767**<br><sub>(127 MiB / 49.8% CPU · 95.9 µs CPU/req)</sub> | **1×** | **0.98×** |
| HTTP/1 · TLS | HTTP/2 · plain | **35,267**<br><sub>(154 MiB / 50.4% CPU · 57.2 µs CPU/req)</sub> | **35,153**<br><sub>(159 MiB / 52.2% CPU · 59.4 µs CPU/req)</sub> | **0.97×** | **0.97×** |
| HTTP/1 · TLS | HTTP/2 · TLS | **60,165**<br><sub>(167 MiB / 48.2% CPU · 32.1 µs CPU/req)</sub> | **61,622**<br><sub>(177 MiB / 48.7% CPU · 31.6 µs CPU/req)</sub> | **0.98×** | **1×** |
| HTTP/1 · TLS | HTTP/3 · QUIC | **18,236**<br><sub>(164 MiB / 52.1% CPU · 114.2 µs CPU/req)</sub> | **18,295**<br><sub>(167 MiB / 51.9% CPU · 113.5 µs CPU/req)</sub> | **0.99×** | **0.99×** |
| HTTP/2 · plain | HTTP/1 · plain | **67,043**<br><sub>(160 MiB / 51.5% CPU · 30.7 µs CPU/req)</sub> | **67,884**<br><sub>(164 MiB / 51% CPU · 30.0 µs CPU/req)</sub> | **0.9×** | **0.91×** |
| HTTP/2 · plain | HTTP/1 · TLS | **35,299**<br><sub>(167 MiB / 48.2% CPU · 54.6 µs CPU/req)</sub> | **34,189**<br><sub>(165 MiB / 48% CPU · 56.2 µs CPU/req)</sub> | **0.96×** | **0.93×** |
| HTTP/2 · plain | HTTP/2 · plain | **60,760**<br><sub>(150 MiB / 43.9% CPU · 28.9 µs CPU/req)</sub> | **56,469**<br><sub>(157 MiB / 43.2% CPU · 30.6 µs CPU/req)</sub> | **0.81×** | **0.76×** |
| HTTP/2 · plain | HTTP/2 · TLS | **68,016**<br><sub>(166 MiB / 42.5% CPU · 25.0 µs CPU/req)</sub> | **64,460**<br><sub>(156 MiB / 42.7% CPU · 26.5 µs CPU/req)</sub> | **0.85×** | **0.81×** |
| HTTP/2 · plain | HTTP/3 · QUIC | **29,725**<br><sub>(190 MiB / 50.9% CPU · 68.5 µs CPU/req)</sub> | **29,228**<br><sub>(185 MiB / 50.8% CPU · 69.5 µs CPU/req)</sub> | **0.97×** | **0.96×** |
| HTTP/2 · TLS | HTTP/1 · plain | **65,362**<br><sub>(164 MiB / 51% CPU · 31.2 µs CPU/req)</sub> | **61,837**<br><sub>(159 MiB / 50.7% CPU · 32.8 µs CPU/req)</sub> | **0.99×** | **0.94×** |
| HTTP/2 · TLS | HTTP/1 · TLS | **25,703**<br><sub>(154 MiB / 51.1% CPU · 79.5 µs CPU/req)</sub> | **24,878**<br><sub>(164 MiB / 50.9% CPU · 81.8 µs CPU/req)</sub> | **1×** | **0.97×** |
| HTTP/2 · TLS | HTTP/2 · plain | **52,838**<br><sub>(166 MiB / 43.7% CPU · 33.1 µs CPU/req)</sub> | **51,725**<br><sub>(166 MiB / 44% CPU · 34.0 µs CPU/req)</sub> | **0.86×** | **0.84×** |
| HTTP/2 · TLS | HTTP/2 · TLS | **56,499**<br><sub>(180 MiB / 42.6% CPU · 30.2 µs CPU/req)</sub> | **53,324**<br><sub>(181 MiB / 42.6% CPU · 31.9 µs CPU/req)</sub> | **0.88×** | **0.83×** |
| HTTP/2 · TLS | HTTP/3 · QUIC | **35,594**<br><sub>(209 MiB / 51% CPU · 57.3 µs CPU/req)</sub> | **34,856**<br><sub>(211 MiB / 50.8% CPU · 58.3 µs CPU/req)</sub> | **0.98×** | **0.96×** |
| HTTP/3 · QUIC | HTTP/1 · plain | **24,985**<br><sub>(169 MiB / 51.8% CPU · 82.9 µs CPU/req)</sub> | **24,101**<br><sub>(176 MiB / 51.7% CPU · 85.8 µs CPU/req)</sub> | **0.98×** | **0.94×** |
| HTTP/3 · QUIC | HTTP/1 · TLS | **30,257**<br><sub>(190 MiB / 51.4% CPU · 68.0 µs CPU/req)</sub> | **30,932**<br><sub>(192 MiB / 52.5% CPU · 67.9 µs CPU/req)</sub> | **0.99×** | **1.01×** |
| HTTP/3 · QUIC | HTTP/2 · plain | **22,784**<br><sub>(164 MiB / 54.2% CPU · 95.1 µs CPU/req)</sub> | **21,052**<br><sub>(163 MiB / 54.8% CPU · 104.1 µs CPU/req)</sub> | **1.04×** | **0.96×** |
| HTTP/3 · QUIC | HTTP/2 · TLS | **20,671**<br><sub>(167 MiB / 53% CPU · 102.7 µs CPU/req)</sub> | **18,710**<br><sub>(167 MiB / 51.3% CPU · 109.6 µs CPU/req)</sub> | **0.99×** | **0.89×** |
| HTTP/3 · QUIC | HTTP/3 · QUIC | **22,698**<br><sub>(175 MiB / 52.4% CPU · 92.3 µs CPU/req)</sub> | **22,430**<br><sub>(172 MiB / 52% CPU · 92.8 µs CPU/req)</sub> | **0.89×** | **0.88×** |

## macOS — Titanium vs nginx vs HAProxy vs Envoy vs YARP

### Reverse

Median of **3 repeats** on `macos-15` (Apple Silicon M1, 3-core / 7 GB). Bare reverse 5×5 @ `8567b4c0` — `compare-product` [40610469359](https://github.com/justcoding121/titanium-web-proxy/actions/runs/40610469359). Warmup 2s / measure 8s; concurrency 8, 16, 32, 64. Prefer TWP÷peer ratios over absolute RPS. **RPS cells** include median RSS / CPU at the peak-RPS step as `<br><sub>(MiB / CPU%)</sub>`. The RPS workflow installs Homebrew nginx (`http_v3_module`), Homebrew HAProxy with `USE_QUIC` (3.2 source fallback), Envoy (Homebrew bottle or pinned darwin-arm64 1.36.7), Homebrew `libmsquic` (+ `DYLD_*`), and YARP. Use the pinned `macos-15` label, not `macos-latest`. Product 5×5 is **~56-byte JSON keep-alive GET**; H2/H3 same-protocol cells are mostly header work with a tiny body (Titanium best case) — see [Why this comparison is fair](#user-content-why-this-comparison-is-fair).

| Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---:|---:|---:|---:|---:|
| HTTP/1 · plain | HTTP/1 · plain | **8,986**<br><sub>(106 MiB / 14.9% CPU · 49.8 µs CPU/req)</sub> | 🥇 **23,126**<br><sub>(68 MiB / 15.7% CPU · 20.3 µs CPU/req)</sub> | **7,172**<br><sub>(90 MiB / 10.7% CPU · 44.8 µs CPU/req)</sub> | **20,491**<br><sub>(112 MiB / 34.7% CPU · 50.8 µs CPU/req)</sub> | **5,849**<br><sub>(135 MiB / 11.8% CPU · 60.6 µs CPU/req)</sub> |
| HTTP/1 · plain | HTTP/1 · TLS | **5,600**<br><sub>(129 MiB / 13.8% CPU · 74.1 µs CPU/req)</sub> | 🥇 **12,175**<br><sub>(78 MiB / 13.1% CPU · 32.2 µs CPU/req)</sub> | **6,751**<br><sub>(96 MiB / 10.8% CPU · 47.9 µs CPU/req)</sub> | **9,036**<br><sub>(114 MiB / 27.1% CPU · 90.0 µs CPU/req)</sub> | **8,561**<br><sub>(197 MiB / 15.4% CPU · 53.8 µs CPU/req)</sub> |
| HTTP/1 · plain | HTTP/2 · plain | **7,414**<br><sub>(122 MiB / 9.8% CPU · 39.7 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | 🥇 **7,990**<br><sub>(95 MiB / 11.2% CPU · 41.9 µs CPU/req)</sub> | **7,179**<br><sub>(111 MiB / 18.2% CPU · 75.9 µs CPU/req)</sub> | **4,055**<br><sub>(165 MiB / 5.6% CPU · 41.1 µs CPU/req)</sub> |
| HTTP/1 · plain | HTTP/2 · TLS | **8,751**<br><sub>(166 MiB / 10.3% CPU · 35.3 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **10,726**<br><sub>(99 MiB / 15.4% CPU · 42.9 µs CPU/req)</sub> | 🥇 **11,615**<br><sub>(112 MiB / 29.1% CPU · 75.2 µs CPU/req)</sub> | **5,951**<br><sub>(197 MiB / 8% CPU · 40.4 µs CPU/req)</sub> |
| HTTP/1 · plain | HTTP/3 · QUIC | 🥇 **10,082**<br><sub>(137 MiB / 28.1% CPU · 83.6 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **4,496**<br><sub>(116 MiB / 15.5% CPU · 103.4 µs CPU/req)</sub> | **3,896**<br><sub>(166 MiB / 8.5% CPU · 65.4 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/1 · plain | **9,967**<br><sub>(128 MiB / 14.4% CPU · 43.4 µs CPU/req)</sub> | **15,116**<br><sub>(84 MiB / 17.6% CPU · 35.0 µs CPU/req)</sub> | 🥇 **15,600**<br><sub>(109 MiB / 20.3% CPU · 39.0 µs CPU/req)</sub> | **12,095**<br><sub>(122 MiB / 25.4% CPU · 63.1 µs CPU/req)</sub> | **7,608**<br><sub>(205 MiB / 13.1% CPU · 51.8 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/1 · TLS | **5,547**<br><sub>(156 MiB / 15% CPU · 81.3 µs CPU/req)</sub> | 🥇 **8,523**<br><sub>(88 MiB / 15.1% CPU · 53.2 µs CPU/req)</sub> | **7,141**<br><sub>(111 MiB / 15.2% CPU · 64.1 µs CPU/req)</sub> | **4,232**<br><sub>(peak 4,981 · 123 MiB / 19.7% CPU · 139.5 µs CPU/req)</sub> | **5,746**<br><sub>(203 MiB / 17.6% CPU · 91.6 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/2 · plain | **4,787**<br><sub>(155 MiB / 11.4% CPU · 71.4 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | 🥇 **11,837**<br><sub>(113 MiB / 23.4% CPU · 59.2 µs CPU/req)</sub> | **7,998**<br><sub>(122 MiB / 15.7% CPU · 59.0 µs CPU/req)</sub> | **6,537**<br><sub>(190 MiB / 8.4% CPU · 38.4 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/2 · TLS | **6,292**<br><sub>(197 MiB / 12.5% CPU · 59.4 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | 🥇 **17,994**<br><sub>(113 MiB / 27% CPU · 45.0 µs CPU/req)</sub> | **7,289**<br><sub>(122 MiB / 22.8% CPU · 93.8 µs CPU/req)</sub> | **13,929**<br><sub>(235 MiB / 17.4% CPU · 37.5 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/3 · QUIC | **5,334**<br><sub>(160 MiB / 14.8% CPU · 83.3 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | 🥇 **9,759**<br><sub>(126 MiB / 23% CPU · 70.6 µs CPU/req)</sub> | **2,884**<br><sub>(171 MiB / 9.7% CPU · 101.3 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | **5,505**<br><sub>(202 MiB / 10.2% CPU · 55.7 µs CPU/req)</sub> | 🥇 **15,743**<br><sub>(70 MiB / 14.5% CPU · 27.7 µs CPU/req)</sub> | **8,688**<br><sub>(93 MiB / 12.6% CPU · 43.5 µs CPU/req)</sub> | **10,133**<br><sub>(112 MiB / 27.3% CPU · 80.7 µs CPU/req)</sub> | **12,726**<br><sub>(181 MiB / 19.2% CPU · 45.4 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/1 · TLS | **6,003**<br><sub>(242 MiB / 14.8% CPU · 73.9 µs CPU/req)</sub> | **6,684**<br><sub>(80 MiB / 9.1% CPU · 40.9 µs CPU/req)</sub> | **7,528**<br><sub>(97 MiB / 14% CPU · 55.7 µs CPU/req)</sub> | **8,194**<br><sub>(114 MiB / 20.8% CPU · 76.2 µs CPU/req)</sub> | 🥇 **11,048**<br><sub>(214 MiB / 22.5% CPU · 61.0 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/2 · plain | **9,714**<br><sub>(269 MiB / 6.8% CPU · 20.9 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | 🥇 **23,844**<br><sub>(97 MiB / 25.3% CPU · 31.9 µs CPU/req)</sub> | **7,102**<br><sub>(110 MiB / 12.8% CPU · 54.2 µs CPU/req)</sub> | **12,077**<br><sub>(171 MiB / 11.7% CPU · 29.1 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/2 · TLS | **8,425**<br><sub>(234 MiB / 8.1% CPU · 28.8 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | 🥇 **19,379**<br><sub>(101 MiB / 19.5% CPU · 30.1 µs CPU/req)</sub> | **11,295**<br><sub>(112 MiB / 15.3% CPU · 40.7 µs CPU/req)</sub> | **5,805**<br><sub>(181 MiB / 5.6% CPU · 28.7 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/3 · QUIC | 🥇 **8,584**<br><sub>(200 MiB / 20.6% CPU · 72.1 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **6,262**<br><sub>(114 MiB / 24.7% CPU · 118.2 µs CPU/req)</sub> | **3,293**<br><sub>(219 MiB / 8.8% CPU · 80.3 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | **18,832**<br><sub>(172 MiB / 23.9% CPU · 38.0 µs CPU/req)</sub> | **13,355**<br><sub>(83 MiB / 11.7% CPU · 26.3 µs CPU/req)</sub> | 🥇 **30,425**<br><sub>(109 MiB / 28% CPU · 27.6 µs CPU/req)</sub> | **10,772**<br><sub>(121 MiB / 25.2% CPU · 70.3 µs CPU/req)</sub> | **8,080**<br><sub>(148 MiB / 9.4% CPU · 34.8 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/1 · TLS | 🥇 **9,510**<br><sub>(221 MiB / 20.5% CPU · 64.8 µs CPU/req)</sub> | **8,352**<br><sub>(87 MiB / 13.5% CPU · 48.6 µs CPU/req)</sub> | **6,177**<br><sub>(109 MiB / 12.8% CPU · 62.3 µs CPU/req)</sub> | **6,622**<br><sub>(122 MiB / 23.8% CPU · 107.9 µs CPU/req)</sub> | **4,059**<br><sub>(215 MiB / 11.4% CPU · 84.2 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | 🥇 **30,098**<br><sub>(263 MiB / 14.5% CPU · 14.4 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **15,205**<br><sub>(114 MiB / 18.3% CPU · 36.1 µs CPU/req)</sub> | **6,378**<br><sub>(121 MiB / 12% CPU · 56.3 µs CPU/req)</sub> | **7,853**<br><sub>(178 MiB / 6.6% CPU · 25.1 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **18,032**<br><sub>(260 MiB / 15.3% CPU · 25.5 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **6,837**<br><sub>(113 MiB / 12% CPU · 52.5 µs CPU/req)</sub> | **8,880**<br><sub>(121 MiB / 17% CPU · 57.6 µs CPU/req)</sub> | **5,181**<br><sub>(207 MiB / 7.1% CPU · 40.9 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/3 · QUIC | 🥇 **9,726**<br><sub>(226 MiB / 21.3% CPU · 65.6 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **3,518**<br><sub>(123 MiB / 13.2% CPU · 112.9 µs CPU/req)</sub> | **3,513**<br><sub>(168 MiB / 9.6% CPU · 82.2 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | **3,816**<br><sub>(156 MiB / 15.2% CPU · 119.5 µs CPU/req)</sub> | **0**<br><sub>(peak 10,062 · 87 MiB / 11.3% CPU)</sub> | **5,642**<br><sub>(105 MiB / 12.5% CPU · 66.4 µs CPU/req)</sub> | **3,952**<br><sub>(126 MiB / 30% CPU · 227.7 µs CPU/req)</sub> | 🥇 **8,477**<br><sub>(241 MiB / 30.3% CPU · 107.4 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | **12,386**<br><sub>(152 MiB / 43.2% CPU · 104.7 µs CPU/req)</sub> | **15,973**<br><sub>(91 MiB / 23% CPU · 43.2 µs CPU/req)</sub> | 🥇 **28,895**<br><sub>(107 MiB / 32.1% CPU · 33.3 µs CPU/req)</sub> | **9,708**<br><sub>(126 MiB / 51.4% CPU · 159.0 µs CPU/req)</sub> | **14,489**<br><sub>(212 MiB / 44.8% CPU · 92.8 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/2 · plain | **3,283**<br><sub>(141 MiB / 9.8% CPU · 89.3 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **4,768**<br><sub>(107 MiB / 6.4% CPU · 40.4 µs CPU/req)</sub> | 🥇 **7,382**<br><sub>(123 MiB / 27.9% CPU · 113.3 µs CPU/req)</sub> | **5,892**<br><sub>(245 MiB / 10.6% CPU · 54.1 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | **5,495**<br><sub>(145 MiB / 12.1% CPU · 66.2 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | 🥇 **10,436**<br><sub>(108 MiB / 16.5% CPU · 47.5 µs CPU/req)</sub> | **5,640**<br><sub>(124 MiB / 33.1% CPU · 176.2 µs CPU/req)</sub> | **7,109**<br><sub>(272 MiB / 13.3% CPU · 56.0 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/3 · QUIC | 🥇 **8,238**<br><sub>(140 MiB / 32.2% CPU · 117.4 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **5,974**<br><sub>(122 MiB / 32.3% CPU · 162.0 µs CPU/req)</sub> | **8,136**<br><sub>(254 MiB / 25.8% CPU · 95.1 µs CPU/req)</sub> |

## Editions (CLI / Plus / Intercept)

**Note:** `twp-reverse-http1` and other library rows use Core with **probe-tuned** settings (no logging, no Via header, probe-warmed certs). Edition rows use `titanium run -c twp.yaml` **product defaults** — prefer the ÷baseline ratio column over absolute RPS. Inspector GUI is not spawnable in the harness; session-path overhead is `twp-cli-intercept-http1` (route `RequestHeaderSet` transform). Pre-origin Plus middleware (CIDR/WAF deny-list/JWT/rate-limit/cache) runs on H1 terminate-lite without `SessionEventArgs`; a cache hit skips the origin. WAF screening (`waf.mode` `detect` or `block`, the default once `waf.enabled` is set) reads headers, cookies, and small bodies, so it uses the session path and lands near the intercept row. The `twp-cli-plus-waf-http1` row below was measured with the deny-list only; re-measure it with screening on before quoting it. JWT caches successful bearer validations. CORS only adds response headers on the way out, so it stays on terminate-lite. Circuit breaker and idempotent retry have to see the request or the status code, so they stay on the session path and should land near the intercept row. Maintainer gate thresholds live under [Maintainer notes](#user-content-maintainer-notes).

Median of **3** repeats on `ubuntu-latest`. Linux rows in this paste were re-measured @ `8567b4c0` (Actions [40610503982](https://github.com/justcoding121/titanium-web-proxy/actions/runs/40610503982)). Every edition row in the table was re-measured in that run. Warmup 2s / measure 8s; concurrency 8–64; sustain = median peak RPS among SLO-pass steps @ **c=64**. **RPS cells** show sustain; `<sub>` holds peak (when higher) plus `(MiB / CPU%)`. The Gate column is that script's floor.

| Arm | Linux | Lin÷ | Gate |
|---|---:|---:|---|
| `twp-cli-reverse-http1` vs library | **46,052**<br><sub>(179 MiB / 47.1% CPU · 40.9 µs CPU/req)</sub> | **1.01×** | ≥ **0.50×** |
| `twp-cli-reverse-http1-tls` vs library TLS | **58,766**<br><sub>(221 MiB / 47.5% CPU · 32.3 µs CPU/req)</sub> | **1.04×** | ≥ **0.50×** |
| `twp-cli-reverse-http1-route` vs CLI | **36,548**<br><sub>(188 MiB / 49.2% CPU · 53.9 µs CPU/req)</sub> | **1.01×** | ≥ **0.50×** |
| `twp-cli-plus-base-http1` vs CLI | **67,695**<br><sub>(256 MiB / 45.6% CPU · 26.9 µs CPU/req)</sub> | **0.99×** | ≥ **0.50×** |
| `twp-cli-plus-cache-http1` (cold) vs CLI | **113,476**<br><sub>(191 MiB / 47.1% CPU · 16.6 µs CPU/req)</sub> | **1.75×** | ≥ **0.50×** |
| `twp-cli-intercept-http1` vs CLI | **29,530**<br><sub>(196 MiB / 51.0% CPU · 69.1 µs CPU/req)</sub> | **0.75×** | ≥ **0.50×** |
| `twp-cli-plus-waf-http1` vs CLI | **34,406**<br><sub>(188 MiB / 48.8% CPU · 56.8 µs CPU/req)</sub> | **0.97×** | ≥ **0.50×** |
| `twp-cli-plus-cidr-http1` vs CLI | **65,024**<br><sub>(189 MiB / 48.3% CPU · 29.7 µs CPU/req)</sub> | **0.95×** | ≥ **0.50×** |
| `twp-cli-plus-jwt-http1` vs CLI | **46,708**<br><sub>(197 MiB / 48.1% CPU · 41.2 µs CPU/req)</sub> | **0.94×** | ≥ **0.50×** |
| `twp-cli-plus-ratelimit-http1` vs CLI | **63,766**<br><sub>(243 MiB / 47.8% CPU · 30.0 µs CPU/req)</sub> | **1.00×** | ≥ **0.50×** |
| `twp-cli-plus-resilience-http1` vs CLI | **64,029**<br><sub>(179 MiB / 47.3% CPU · 29.6 µs CPU/req)</sub> | **1.00×** | ≥ **0.50×** |
| `twp-cli-plus-discovery-file-http1` vs CLI | **67,313**<br><sub>(177 MiB / 48.2% CPU · 28.6 µs CPU/req)</sub> | **1.01×** | ≥ **0.50×** |
| `twp-cli-plus-metrics-scrape-http1` vs CLI | **34,990**<br><sub>(192 MiB / 48.6% CPU · 55.5 µs CPU/req)</sub> | **0.99×** | ≥ **0.50×** |
| `twp-cli-plus-cache-hit-http1` vs cache cold | **116,990**<br><sub>(190 MiB / 47.4% CPU · 16.2 µs CPU/req)</sub> | **1.03×** | ≥ **0.50×** |
| `twp-cli-plus-cors-http1` vs CLI | **61,848**<br><sub>(191 MiB / 47.4% CPU · 30.6 µs CPU/req)</sub> | **0.93×** | ≥ **0.50×** |
| `twp-cli-plus-circuit-http1` vs CLI | **27,828**<br><sub>(199 MiB / 50.8% CPU · 73.0 µs CPU/req)</sub> | **0.77×** | ≥ **0.50×** |
| `twp-cli-plus-retry-http1` vs CLI | **32,404**<br><sub>(196 MiB / 50.4% CPU · 62.2 µs CPU/req)</sub> | **0.83×** | ≥ **0.50×** |
| `twp-cli-static-http1` vs CLI | **55,500**<br><sub>(187 MiB / 49.8% CPU · 35.9 µs CPU/req)</sub> | **1.61×** | ≥ **0.50×** |
| `twp-cli-logging-http1` vs CLI | **36,296**<br><sub>(185 MiB / 49.2% CPU · 54.3 µs CPU/req)</sub> | **1.00×** | ≥ **0.50×** |
| `twp-cli-lb-leasttime-http1` vs route | **37,915**<br><sub>(221 MiB / 70.3% CPU · 74.2 µs CPU/req)</sub> | **0.97×** | ≥ **0.50×** |
| `twp-cli-dialect-twp-http1` vs CLI | **35,746**<br><sub>(163 MiB / 49.1% CPU · 54.9 µs CPU/req)</sub> | **1.00×** | ≥ **0.50×** |

`validate-edition-gates.ps1` floors are **0.50×**. Each ÷ column uses the two arms from the same job. Circuit breaker and idempotent retry stay on the session path, so a ratio near intercept is expected. Laptop smoke ratios stay on [Performance Local Lab — Editions](Performance-Local-Lab#user-content-editions-cli--plus-stress).

## Heavier reverse workloads

Same runners and harness as the tiny-GET tables, but with larger bodies, POST, lossy links, TLS cost, and architecture-sensitive paths (slow consumer / early response / duplex). **PUT with the same body is the same proxy work as POST; DELETE with no body matches GET** — only POST is published. Bodies/POST/lossy stay **half-duplex**. Laptop numbers are on [Performance Local Lab](Performance-Local-Lab#user-content-architecture-sensitive). How maintainers refresh these tables (modes, shards, paste scripts) is under [Maintainer notes](#user-content-maintainer-notes). **Larger-body check:** 64 / 256 KiB H2 TLS→H2 TLS is where body copy dominates headers — ratios vs YARP are in the tables below (@ `8567b4c0`), unlike the tiny-GET H2↔H2 medals above. Those cells also show goodput (MiB/s) and µs CPU/req, and the medal on those rows follows goodput.

Lossy link = **userspace** delay/drop shim (not kernel `netem`): TCP gets per-buffer delay + occasional whole-connection stalls (honest head-of-line for multiplexed HTTP/2); UDP is **loss% only** (no per-datagram delay) + drops (QUIC / MsQuic-safe). Lossy tables publish HTTP/1, HTTP/2, and HTTP/3.

### Windows — heavier reverse GET (64 KiB / 256 KiB)

Median of **3** repeats on `windows-latest` @ `8567b4c0`. Source: Actions [40610472761](https://github.com/justcoding121/titanium-web-proxy/actions/runs/40610472761) (`compare-bodies`). Warmup 2s / measure 8s. **RPS cells** include `(MiB / CPU%)` footprints.

*Not possible:* **HAProxy** and **Envoy** columns are omitted (no official Windows port).





















| Body | Client | Origin | TWP | nginx | YARP |
|---|---|---|---:|---:|---:|
| 64 KiB | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **8,981**<br><sub>(98 MiB / 47.0% CPU · 561 MiB/s · 209.2 µs CPU/req)</sub> | **619**<br><sub>(143 MiB / 24.7% CPU · 39 MiB/s · 1593.0 µs CPU/req)</sub> | **7,856**<br><sub>(133 MiB / 47.7% CPU · 491 MiB/s · 243.0 µs CPU/req)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **7,798**<br><sub>(206 MiB / 49.5% CPU · 487 MiB/s · 253.7 µs CPU/req)</sub> | **574**<br><sub>(143 MiB / 24.8% CPU · 36 MiB/s · 1727.7 µs CPU/req)</sub> | **7,503**<br><sub>(132 MiB / 55.6% CPU · 469 MiB/s · 296.7 µs CPU/req)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **7,724**<br><sub>(145 MiB / 42.6% CPU · 483 MiB/s · 220.9 µs CPU/req)</sub> | *Not possible (no QUIC)* | **4,177**<br><sub>(182 MiB / 53.4% CPU · 261 MiB/s · 510.8 µs CPU/req)</sub> |
| 256 KiB | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **3,588**<br><sub>(136 MiB / 44.8% CPU · 897 MiB/s · 499.7 µs CPU/req)</sub> | **243**<br><sub>(144 MiB / 24.5% CPU · 61 MiB/s · 4032.9 µs CPU/req)</sub> | **3,312**<br><sub>(135 MiB / 48.1% CPU · 828 MiB/s · 580.5 µs CPU/req)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/1 · plain | **3,896**<br><sub>(185 MiB / 40.4% CPU · 974 MiB/s · 414.8 µs CPU/req)</sub> | **290**<br><sub>(144 MiB / 24.9% CPU · 73 MiB/s · 3422.9 µs CPU/req)</sub> | 🥇 **4,250**<br><sub>(130 MiB / 41.8% CPU · 1,063 MiB/s · 393.3 µs CPU/req)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/1 · plain | **138**<br><sub>(94 MiB / 45.1% CPU · 34 MiB/s · 13094.3 µs CPU/req)</sub> | *Not possible (no QUIC)* | 🥇 **394**<br><sub>(159 MiB / 51.9% CPU · 99 MiB/s · 5264.4 µs CPU/req)</sub> |
| 64 KiB | HTTP/2 · plain | HTTP/1 · plain | 🥇 **13,119**<br><sub>(172 MiB / 44.3% CPU · 820 MiB/s · 135.0 µs CPU/req)</sub> | **1,984**<br><sub>(128 MiB / 24.8% CPU · 124 MiB/s · 499.1 µs CPU/req)</sub> | **11,627**<br><sub>(116 MiB / 46.9% CPU · 727 MiB/s · 161.5 µs CPU/req)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/2 · plain | 🥇 **11,324**<br><sub>(184 MiB / 44.8% CPU · 708 MiB/s · 158.3 µs CPU/req)</sub> | *Not possible* | **9,951**<br><sub>(136 MiB / 51.9% CPU · 622 MiB/s · 208.7 µs CPU/req)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **6,429**<br><sub>(172 MiB / 42.0% CPU · 402 MiB/s · 261.1 µs CPU/req)</sub> | *Not possible* | **5,526**<br><sub>(148 MiB / 49.2% CPU · 345 MiB/s · 356.4 µs CPU/req)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/2 · TLS | 🥇 **4,616**<br><sub>(132 MiB / 42.9% CPU · 289 MiB/s · 371.8 µs CPU/req)</sub> | *Not possible* | **2,430**<br><sub>(183 MiB / 50.4% CPU · 152 MiB/s · 828.8 µs CPU/req)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **3,342**<br><sub>(153 MiB / 43.1% CPU · 209 MiB/s · 515.9 µs CPU/req)</sub> | *Not possible (no QUIC)* | **2,090**<br><sub>(193 MiB / 50.9% CPU · 131 MiB/s · 974.3 µs CPU/req)</sub> |
| 256 KiB | HTTP/2 · plain | HTTP/1 · plain | 🥇 **4,185**<br><sub>(173 MiB / 33.2% CPU · 1,046 MiB/s · 317.5 µs CPU/req)</sub> | **646**<br><sub>(129 MiB / 24.5% CPU · 161 MiB/s · 1515.6 µs CPU/req)</sub> | **3,914**<br><sub>(119 MiB / 40.1% CPU · 979 MiB/s · 410.3 µs CPU/req)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/2 · plain | 🥇 **2,254**<br><sub>(196 MiB / 47.8% CPU · 564 MiB/s · 848.9 µs CPU/req)</sub> | *Not possible* | **2,164**<br><sub>(167 MiB / 46.6% CPU · 541 MiB/s · 860.7 µs CPU/req)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **1,536**<br><sub>(201 MiB / 42.3% CPU · 384 MiB/s · 1102.7 µs CPU/req)</sub> | *Not possible* | **1,316**<br><sub>(151 MiB / 46.2% CPU · 329 MiB/s · 1402.5 µs CPU/req)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/2 · TLS | **273**<br><sub>(144 MiB / 49.0% CPU · 68 MiB/s · 7193.3 µs CPU/req)</sub> | *Not possible* | 🥇 **796**<br><sub>(187 MiB / 48.8% CPU · 199 MiB/s · 2450.6 µs CPU/req)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **221**<br><sub>(96 MiB / 48.2% CPU · 55 MiB/s · 8723.7 µs CPU/req)</sub> | *Not possible (no QUIC)* | **136**<br><sub>(119 MiB / 48.6% CPU · 34 MiB/s · 14324.5 µs CPU/req)</sub> |

nginx/Windows collapses on large reverse bodies in this harness; treat as same-OS only. Prefer the ratio columns; absolute RPS swings by VM.

### Linux — heavier reverse GET (64 KiB / 256 KiB)

Median of **3** repeats @ `8567b4c0`. Source: Actions [40610472761](https://github.com/justcoding121/titanium-web-proxy/actions/runs/40610472761) (`compare-bodies`). Warmup 2s / measure 8s.


| Body | Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---|---:|---:|---:|---:|---:|
| 64 KiB | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **13,253**<br><sub>(133 MiB / 41.2% CPU · 828 MiB/s · 124.4 µs CPU/req)</sub> | **8,866**<br><sub>(110 MiB / 50.2% CPU · 554 MiB/s · 226.4 µs CPU/req)</sub> | **13,093**<br><sub>(88 MiB / 40.2% CPU · 818 MiB/s · 122.8 µs CPU/req)</sub> | **11,326**<br><sub>(127 MiB / 46.2% CPU · 708 MiB/s · 163.2 µs CPU/req)</sub> | **10,401**<br><sub>(165 MiB / 47.4% CPU · 650 MiB/s · 182.2 µs CPU/req)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/1 · plain | **11,333**<br><sub>(286 MiB / 42.9% CPU · 708 MiB/s · 151.3 µs CPU/req)</sub> | **7,641**<br><sub>(116 MiB / 50.2% CPU · 478 MiB/s · 262.6 µs CPU/req)</sub> | **10,277**<br><sub>(94 MiB / 47.6% CPU · 642 MiB/s · 185.4 µs CPU/req)</sub> | 🥇 **11,386**<br><sub>(135 MiB / 48.0% CPU · 712 MiB/s · 168.5 µs CPU/req)</sub> | **10,010**<br><sub>(177 MiB / 47.4% CPU · 626 MiB/s · 189.5 µs CPU/req)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/1 · plain | **5,407**<br><sub>(209 MiB / 44.8% CPU · 338 MiB/s · 331.3 µs CPU/req)</sub> | **2,733**<br><sub>(127 MiB / 38.7% CPU · 171 MiB/s · 566.0 µs CPU/req)</sub> | 🥇 **5,699**<br><sub>(101 MiB / 41.1% CPU · 356 MiB/s · 288.8 µs CPU/req)</sub> | **4,024**<br><sub>(141 MiB / 52.1% CPU · 251 MiB/s · 518.0 µs CPU/req)</sub> | **3,751**<br><sub>(247 MiB / 52.6% CPU · 234 MiB/s · 560.6 µs CPU/req)</sub> |
| 256 KiB | HTTP/1 · TLS | HTTP/1 · plain | **2,780**<br><sub>(132 MiB / 35.9% CPU · 695 MiB/s · 516.9 µs CPU/req)</sub> | **1,787**<br><sub>(109 MiB / 54.0% CPU · 447 MiB/s · 1207.4 µs CPU/req)</sub> | 🥇 **2,983**<br><sub>(87 MiB / 32.7% CPU · 746 MiB/s · 438.6 µs CPU/req)</sub> | **2,681**<br><sub>(140 MiB / 35.3% CPU · 670 MiB/s · 526.4 µs CPU/req)</sub> | **2,265**<br><sub>(173 MiB / 45.0% CPU · 566 MiB/s · 794.7 µs CPU/req)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/1 · plain | **3,629**<br><sub>(230 MiB / 33.5% CPU · 907 MiB/s · 369.2 µs CPU/req)</sub> | **2,724**<br><sub>(116 MiB / 49.2% CPU · 681 MiB/s · 723.2 µs CPU/req)</sub> | **3,658**<br><sub>(93 MiB / 37.5% CPU · 915 MiB/s · 410.1 µs CPU/req)</sub> | 🥇 **3,909**<br><sub>(152 MiB / 33.8% CPU · 977 MiB/s · 345.9 µs CPU/req)</sub> | **3,191**<br><sub>(182 MiB / 39.0% CPU · 798 MiB/s · 488.6 µs CPU/req)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/1 · plain | **2,068**<br><sub>(185 MiB / 50.5% CPU · 517 MiB/s · 977.2 µs CPU/req)</sub> | **963**<br><sub>(123 MiB / 35.5% CPU · 241 MiB/s · 1476.6 µs CPU/req)</sub> | 🥇 **2,417**<br><sub>(97 MiB / 34.0% CPU · 604 MiB/s · 563.2 µs CPU/req)</sub> | **2,089**<br><sub>(151 MiB / 45.8% CPU · 522 MiB/s · 877.2 µs CPU/req)</sub> | **1,706**<br><sub>(240 MiB / 51.4% CPU · 426 MiB/s · 1204.1 µs CPU/req)</sub> |
| 64 KiB | HTTP/2 · plain | HTTP/1 · plain | 🥇 **11,482**<br><sub>(255 MiB / 42.9% CPU · 718 MiB/s · 149.3 µs CPU/req)</sub> | **6,919**<br><sub>(86 MiB / 52.3% CPU · 432 MiB/s · 302.6 µs CPU/req)</sub> | **10,936**<br><sub>(78 MiB / 46.8% CPU · 684 MiB/s · 171.1 µs CPU/req)</sub> | **9,562**<br><sub>(123 MiB / 53.6% CPU · 598 MiB/s · 224.4 µs CPU/req)</sub> | **10,893**<br><sub>(163 MiB / 46.9% CPU · 681 MiB/s · 172.3 µs CPU/req)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/2 · plain | 🥇 **11,198**<br><sub>(211 MiB / 37.4% CPU · 700 MiB/s · 133.5 µs CPU/req)</sub> | *Not possible* | **8,776**<br><sub>(91 MiB / 45.9% CPU · 548 MiB/s · 209.2 µs CPU/req)</sub> | **10,005**<br><sub>(131 MiB / 46.3% CPU · 625 MiB/s · 184.9 µs CPU/req)</sub> | **8,814**<br><sub>(200 MiB / 48.5% CPU · 551 MiB/s · 220.1 µs CPU/req)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **10,734**<br><sub>(224 MiB / 37.9% CPU · 671 MiB/s · 141.3 µs CPU/req)</sub> | *Not possible* | **8,811**<br><sub>(92 MiB / 47.6% CPU · 551 MiB/s · 215.9 µs CPU/req)</sub> | **9,931**<br><sub>(137 MiB / 45.5% CPU · 621 MiB/s · 183.1 µs CPU/req)</sub> | **8,708**<br><sub>(203 MiB / 45.1% CPU · 544 MiB/s · 207.4 µs CPU/req)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/2 · TLS | **4,663**<br><sub>(185 MiB / 43.1% CPU · 291 MiB/s · 370.1 µs CPU/req)</sub> | *Not possible* | 🥇 **4,836**<br><sub>(98 MiB / 38.8% CPU · 302 MiB/s · 320.5 µs CPU/req)</sub> | **3,496**<br><sub>(141 MiB / 51.1% CPU · 218 MiB/s · 584.7 µs CPU/req)</sub> | **3,344**<br><sub>(256 MiB / 50.0% CPU · 209 MiB/s · 597.5 µs CPU/req)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/1 · TLS | **3,877**<br><sub>(224 MiB / 45.4% CPU · 242 MiB/s · 468.2 µs CPU/req)</sub> | **2,326**<br><sub>(131 MiB / 38.4% CPU · 145 MiB/s · 660.7 µs CPU/req)</sub> | 🥇 **4,024**<br><sub>(102 MiB / 42.1% CPU · 251 MiB/s · 419.0 µs CPU/req)</sub> | **3,226**<br><sub>(140 MiB / 51.1% CPU · 202 MiB/s · 634.3 µs CPU/req)</sub> | **2,669**<br><sub>(268 MiB / 52.0% CPU · 167 MiB/s · 779.0 µs CPU/req)</sub> |
| 256 KiB | HTTP/2 · plain | HTTP/1 · plain | **3,354**<br><sub>(204 MiB / 32.1% CPU · 838 MiB/s · 382.5 µs CPU/req)</sub> | **2,279**<br><sub>(81 MiB / 53.6% CPU · 570 MiB/s · 940.5 µs CPU/req)</sub> | **3,516**<br><sub>(71 MiB / 34.4% CPU · 879 MiB/s · 390.9 µs CPU/req)</sub> | 🥇 **3,634**<br><sub>(140 MiB / 35.2% CPU · 908 MiB/s · 388.0 µs CPU/req)</sub> | **3,246**<br><sub>(154 MiB / 38.0% CPU · 812 MiB/s · 468.7 µs CPU/req)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/2 · plain | 🥇 **3,444**<br><sub>(241 MiB / 32.1% CPU · 861 MiB/s · 372.6 µs CPU/req)</sub> | *Not possible* | **3,360**<br><sub>(106 MiB / 35.1% CPU · 840 MiB/s · 417.5 µs CPU/req)</sub> | **3,305**<br><sub>(147 MiB / 34.6% CPU · 826 MiB/s · 418.3 µs CPU/req)</sub> | **2,792**<br><sub>(221 MiB / 40.6% CPU · 698 MiB/s · 582.0 µs CPU/req)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **1,456**<br><sub>(262 MiB / 39.5% CPU · 364 MiB/s · 1086.8 µs CPU/req)</sub> | *Not possible* | **1,336**<br><sub>(103 MiB / 41.4% CPU · 334 MiB/s · 1239.3 µs CPU/req)</sub> | **1,382**<br><sub>(143 MiB / 38.2% CPU · 345 MiB/s · 1106.5 µs CPU/req)</sub> | **1,046**<br><sub>(198 MiB / 44.8% CPU · 262 MiB/s · 1712.9 µs CPU/req)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/2 · TLS | **719**<br><sub>(201 MiB / 58.8% CPU · 180 MiB/s · 3268.1 µs CPU/req)</sub> | *Not possible* | 🥇 **1,243**<br><sub>(106 MiB / 40.2% CPU · 311 MiB/s · 1294.9 µs CPU/req)</sub> | **1,035**<br><sub>(149 MiB / 47.0% CPU · 259 MiB/s · 1818.3 µs CPU/req)</sub> | **870**<br><sub>(259 MiB / 49.1% CPU · 217 MiB/s · 2256.4 µs CPU/req)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/1 · TLS | **1,060**<br><sub>(207 MiB / 48.9% CPU · 265 MiB/s · 1843.2 µs CPU/req)</sub> | **622**<br><sub>(131 MiB / 36.7% CPU · 155 MiB/s · 2363.5 µs CPU/req)</sub> | 🥇 **1,187**<br><sub>(104 MiB / 40.0% CPU · 297 MiB/s · 1346.9 µs CPU/req)</sub> | **1,033**<br><sub>(peak 1,099 · 143 MiB / 43.5% CPU · 258 MiB/s · 1683.5 µs CPU/req)</sub> | **909**<br><sub>(262 MiB / 49.3% CPU · 227 MiB/s · 2169.6 µs CPU/req)</sub> |

Prefer the ratio columns; absolute RPS swings by VM.

### macOS — heavier reverse GET (64 KiB / 256 KiB)

Median of **3** repeats on `macos-15` (Apple Silicon M1, 3-core / 7 GB) @ `8567b4c0`. Source: Actions [40610472761](https://github.com/justcoding121/titanium-web-proxy/actions/runs/40610472761) (`compare-bodies`). Warmup 2s / measure 8s.

| Body | Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---|---:|---:|---:|---:|---:|
| 64 KiB | HTTP/1 · TLS | HTTP/1 · plain | **5,379**<br><sub>(194 MiB / 17.6% CPU · 336 MiB/s · 98.4 µs CPU/req)</sub> | 🥇 **9,088**<br><sub>(84 MiB / 21.5% CPU · 568 MiB/s · 71.1 µs CPU/req)</sub> | **8,548**<br><sub>(120 MiB / 24.7% CPU · 534 MiB/s · 86.6 µs CPU/req)</sub> | **5,464**<br><sub>(131 MiB / 20.0% CPU · 342 MiB/s · 109.6 µs CPU/req)</sub> | **3,085**<br><sub>(185 MiB / 15.1% CPU · 193 MiB/s · 146.7 µs CPU/req)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/1 · plain | **2,416**<br><sub>(474 MiB / 18.2% CPU · 151 MiB/s · 225.9 µs CPU/req)</sub> | 🥇 **5,151**<br><sub>(87 MiB / 18.6% CPU · 322 MiB/s · 108.4 µs CPU/req)</sub> | **4,374**<br><sub>(124 MiB / 22.4% CPU · 273 MiB/s · 153.5 µs CPU/req)</sub> | **3,982**<br><sub>(144 MiB / 19.2% CPU · 249 MiB/s · 144.6 µs CPU/req)</sub> | **4,451**<br><sub>(173 MiB / 27.4% CPU · 278 MiB/s · 185.0 µs CPU/req)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/1 · plain | **1,367**<br><sub>(286 MiB / 25.6% CPU · 85 MiB/s · 561.7 µs CPU/req)</sub> | 🥇 **2,041**<br><sub>(96 MiB / 22.5% CPU · 128 MiB/s · 330.8 µs CPU/req)</sub> | **1,422**<br><sub>(126 MiB / 29.7% CPU · 89 MiB/s · 626.0 µs CPU/req)</sub> | **1,338**<br><sub>(132 MiB / 46.5% CPU · 84 MiB/s · 1043.1 µs CPU/req)</sub> | **604**<br><sub>(458 MiB / 21.6% CPU · 38 MiB/s · 1069.8 µs CPU/req)</sub> |
| 256 KiB | HTTP/1 · TLS | HTTP/1 · plain | **1,966**<br><sub>(228 MiB / 23.4% CPU · 492 MiB/s · 356.8 µs CPU/req)</sub> | **2,065**<br><sub>(85 MiB / 16.4% CPU · 516 MiB/s · 238.7 µs CPU/req)</sub> | **2,118**<br><sub>(119 MiB / 22.0% CPU · 529 MiB/s · 311.8 µs CPU/req)</sub> | 🥇 **2,118**<br><sub>(154 MiB / 22.0% CPU · 530 MiB/s · 311.5 µs CPU/req)</sub> | **1,656**<br><sub>(185 MiB / 32.1% CPU · 414 MiB/s · 581.4 µs CPU/req)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/1 · plain | **1,097**<br><sub>(453 MiB / 17.9% CPU · 274 MiB/s · 490.0 µs CPU/req)</sub> | **1,447**<br><sub>(86 MiB / 20.6% CPU · 362 MiB/s · 425.9 µs CPU/req)</sub> | 🥇 **1,922**<br><sub>(117 MiB / 30.5% CPU · 481 MiB/s · 475.4 µs CPU/req)</sub> | **1,795**<br><sub>(155 MiB / 27.7% CPU · 449 MiB/s · 463.2 µs CPU/req)</sub> | **1,266**<br><sub>(184 MiB / 30.9% CPU · 316 MiB/s · 732.9 µs CPU/req)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/1 · plain | **302**<br><sub>(187 MiB / 26.7% CPU · 76 MiB/s · 2653.5 µs CPU/req)</sub> | 🥇 **748**<br><sub>(98 MiB / 28.1% CPU · 187 MiB/s · 1129.4 µs CPU/req)</sub> | **313**<br><sub>(peak 456 · 107 MiB / 24.3% CPU · 78 MiB/s · 2326.6 µs CPU/req)</sub> | **610**<br><sub>(150 MiB / 51.9% CPU · 153 MiB/s · 2552.8 µs CPU/req)</sub> | **133**<br><sub>(308 MiB / 18.1% CPU · 33 MiB/s · 4070.5 µs CPU/req)</sub> |
| 64 KiB | HTTP/2 · plain | HTTP/1 · plain | **2,777**<br><sub>(461 MiB / 15.6% CPU · 174 MiB/s · 168.9 µs CPU/req)</sub> | 🥇 **7,275**<br><sub>(73 MiB / 16.5% CPU · 455 MiB/s · 67.9 µs CPU/req)</sub> | **4,388**<br><sub>(108 MiB / 18.2% CPU · 274 MiB/s · 124.7 µs CPU/req)</sub> | **4,757**<br><sub>(125 MiB / 23.8% CPU · 297 MiB/s · 150.4 µs CPU/req)</sub> | **2,289**<br><sub>(211 MiB / 13.7% CPU · 143 MiB/s · 179.7 µs CPU/req)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/2 · plain | **2,496**<br><sub>(395 MiB / 15.4% CPU · 156 MiB/s · 184.6 µs CPU/req)</sub> | *Not possible* | **4,410**<br><sub>(126 MiB / 27.5% CPU · 276 MiB/s · 186.8 µs CPU/req)</sub> | 🥇 **4,987**<br><sub>(126 MiB / 28.2% CPU · 312 MiB/s · 169.7 µs CPU/req)</sub> | **3,995**<br><sub>(260 MiB / 22.7% CPU · 250 MiB/s · 170.2 µs CPU/req)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/2 · TLS | **2,168**<br><sub>(416 MiB / 15.0% CPU · 136 MiB/s · 207.0 µs CPU/req)</sub> | *Not possible* | 🥇 **2,808**<br><sub>(122 MiB / 28.5% CPU · 175 MiB/s · 304.4 µs CPU/req)</sub> | **2,382**<br><sub>(127 MiB / 20.2% CPU · 149 MiB/s · 255.0 µs CPU/req)</sub> | **2,280**<br><sub>(255 MiB / 23.6% CPU · 142 MiB/s · 310.4 µs CPU/req)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/2 · TLS | **1,447**<br><sub>(218 MiB / 30.5% CPU · 90 MiB/s · 631.8 µs CPU/req)</sub> | *Not possible* | 🥇 **1,698**<br><sub>(132 MiB / 33.4% CPU · 106 MiB/s · 589.8 µs CPU/req)</sub> | **1,692**<br><sub>(130 MiB / 47.7% CPU · 106 MiB/s · 845.8 µs CPU/req)</sub> | **846**<br><sub>(468 MiB / 25.5% CPU · 53 MiB/s · 903.5 µs CPU/req)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/1 · TLS | **1,533**<br><sub>(254 MiB / 32.5% CPU · 96 MiB/s · 636.7 µs CPU/req)</sub> | 🥇 **1,724**<br><sub>(99 MiB / 22.8% CPU · 108 MiB/s · 395.9 µs CPU/req)</sub> | **1,334**<br><sub>(130 MiB / 30.3% CPU · 83 MiB/s · 682.5 µs CPU/req)</sub> | **1,381**<br><sub>(132 MiB / 46.8% CPU · 86 MiB/s · 1016.7 µs CPU/req)</sub> | **728**<br><sub>(381 MiB / 25.6% CPU · 46 MiB/s · 1053.7 µs CPU/req)</sub> |
| 256 KiB | HTTP/2 · plain | HTTP/1 · plain | **1,272**<br><sub>(480 MiB / 15.8% CPU · 318 MiB/s · 371.3 µs CPU/req)</sub> | 🥇 **2,759**<br><sub>(73 MiB / 16.9% CPU · 690 MiB/s · 184.2 µs CPU/req)</sub> | **2,106**<br><sub>(104 MiB / 23.1% CPU · 527 MiB/s · 329.3 µs CPU/req)</sub> | **2,320**<br><sub>(146 MiB / 21.7% CPU · 580 MiB/s · 280.5 µs CPU/req)</sub> | **1,848**<br><sub>(206 MiB / 19.4% CPU · 462 MiB/s · 314.9 µs CPU/req)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/2 · plain | **1,308**<br><sub>(678 MiB / 30.0% CPU · 327 MiB/s · 688.8 µs CPU/req)</sub> | *Not possible* | 🥇 **1,415**<br><sub>(137 MiB / 29.6% CPU · 354 MiB/s · 628.0 µs CPU/req)</sub> | **945**<br><sub>(132 MiB / 20.8% CPU · 236 MiB/s · 661.3 µs CPU/req)</sub> | **1,021**<br><sub>(307 MiB / 28.7% CPU · 255 MiB/s · 844.4 µs CPU/req)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/2 · TLS | **1,164**<br><sub>(590 MiB / 31.1% CPU · 291 MiB/s · 801.9 µs CPU/req)</sub> | *Not possible* | 🥇 **1,215**<br><sub>(134 MiB / 32.6% CPU · 304 MiB/s · 805.5 µs CPU/req)</sub> | **926**<br><sub>(129 MiB / 26.4% CPU · 231 MiB/s · 856.8 µs CPU/req)</sub> | **914**<br><sub>(290 MiB / 29.8% CPU · 228 MiB/s · 976.7 µs CPU/req)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/2 · TLS | **178**<br><sub>(peak 240 · 321 MiB / 35.9% CPU · 44 MiB/s · 6069.3 µs CPU/req)</sub> | *Not possible* | **294**<br><sub>(peak 525 · 112 MiB / 25.6% CPU · 73 MiB/s · 2614.2 µs CPU/req)</sub> | 🥇 **442**<br><sub>(138 MiB / 47.6% CPU · 110 MiB/s · 3231.7 µs CPU/req)</sub> | **258**<br><sub>(366 MiB / 32.7% CPU · 65 MiB/s · 3790.3 µs CPU/req)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/1 · TLS | **229**<br><sub>(peak 371 · 225 MiB / 27.7% CPU · 57 MiB/s · 3635.2 µs CPU/req)</sub> | 🥇 **550**<br><sub>(100 MiB / 25.8% CPU · 137 MiB/s · 1405.1 µs CPU/req)</sub> | **302**<br><sub>(peak 400 · 107 MiB / 24.9% CPU · 76 MiB/s · 2474.5 µs CPU/req)</sub> | **394**<br><sub>(152 MiB / 47.6% CPU · 98 MiB/s · 3624.4 µs CPU/req)</sub> | **288**<br><sub>(401 MiB / 33.9% CPU · 72 MiB/s · 3539.1 µs CPU/req)</sub> |

### Windows — POST 64 KiB request + 64 KiB response

Median of **3** repeats on `windows-latest` @ `8567b4c0`. Source: Actions [40610476097](https://github.com/justcoding121/titanium-web-proxy/actions/runs/40610476097) (`compare-post`).

*Not possible:* **HAProxy** and **Envoy** columns are omitted (no official Windows port).





















| Client | Origin | TWP | nginx | YARP |
|---|---|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | 🥇 **5,793**<br><sub>(103 MiB / 41.8% CPU · 724 MiB/s · 288.6 µs CPU/req)</sub> | **392**<br><sub>(144 MiB / 24.9% CPU · 49 MiB/s · 2539.0 µs CPU/req)</sub> | **4,217**<br><sub>(135 MiB / 56.7% CPU · 527 MiB/s · 537.4 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **8,214**<br><sub>(237 MiB / 49.5% CPU · 1,027 MiB/s · 241.2 µs CPU/req)</sub> | **636**<br><sub>(148 MiB / 24.8% CPU · 79 MiB/s · 1560.6 µs CPU/req)</sub> | **7,385**<br><sub>(134 MiB / 46.5% CPU · 923 MiB/s · 252.1 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **2,246**<br><sub>(165 MiB / 46.3% CPU · 281 MiB/s · 825.4 µs CPU/req)</sub> | *Not possible (no QUIC)* | **1,458**<br><sub>(210 MiB / 52.2% CPU · 182 MiB/s · 1431.9 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | 🥇 **6,396**<br><sub>(206 MiB / 44.0% CPU · 800 MiB/s · 275.2 µs CPU/req)</sub> | **1,761**<br><sub>(133 MiB / 24.7% CPU · 220 MiB/s · 560.8 µs CPU/req)</sub> | **6,209**<br><sub>(120 MiB / 48.8% CPU · 776 MiB/s · 314.0 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | 🥇 **4,670**<br><sub>(173 MiB / 36.4% CPU · 584 MiB/s · 312.0 µs CPU/req)</sub> | *Not possible* | **3,685**<br><sub>(150 MiB / 48.8% CPU · 461 MiB/s · 529.7 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **3,958**<br><sub>(168 MiB / 42.3% CPU · 495 MiB/s · 427.3 µs CPU/req)</sub> | *Not possible* | **3,107**<br><sub>(154 MiB / 44.7% CPU · 388 MiB/s · 575.2 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | 🥇 **2,639**<br><sub>(191 MiB / 43.5% CPU · 330 MiB/s · 658.6 µs CPU/req)</sub> | *Not possible* | **1,668**<br><sub>(225 MiB / 47.7% CPU · 208 MiB/s · 1144.9 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **3,824**<br><sub>(190 MiB / 46.2% CPU · 478 MiB/s · 483.7 µs CPU/req)</sub> | *Not possible (no QUIC)* | **2,689**<br><sub>(218 MiB / 49.7% CPU · 336 MiB/s · 739.4 µs CPU/req)</sub> |

TWP leads H2→H1 POST (about **1.1–1.3×** YARP) and H3 POST (about **1.0–1.1×** YARP). H2 TLS→H2 TLS POST sustain is about **1.2–1.4×** YARP.

### Linux — POST 64 KiB request + 64 KiB response

Median of **3** repeats @ `8567b4c0`. Source: Actions [40610476097](https://github.com/justcoding121/titanium-web-proxy/actions/runs/40610476097) (`compare-post`).


| Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---:|---:|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | **8,797**<br><sub>(143 MiB / 39.9% CPU · 1,100 MiB/s · 181.6 µs CPU/req)</sub> | **7,436**<br><sub>(116 MiB / 44.6% CPU · 930 MiB/s · 240.0 µs CPU/req)</sub> | 🥇 **9,008**<br><sub>(96 MiB / 40.1% CPU · 1,126 MiB/s · 178.3 µs CPU/req)</sub> | **8,825**<br><sub>(133 MiB / 43.0% CPU · 1,103 MiB/s · 194.7 µs CPU/req)</sub> | **5,955**<br><sub>(188 MiB / 54.5% CPU · 744 MiB/s · 366.2 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | **3,957**<br><sub>(294 MiB / 48.1% CPU · 495 MiB/s · 486.3 µs CPU/req)</sub> | **3,485**<br><sub>(122 MiB / 47.1% CPU · 436 MiB/s · 540.2 µs CPU/req)</sub> | 🥇 **4,096**<br><sub>(96 MiB / 45.3% CPU · 512 MiB/s · 442.5 µs CPU/req)</sub> | **3,827**<br><sub>(peak 4,535 · 127 MiB / 43.6% CPU · 478 MiB/s · 455.9 µs CPU/req)</sub> | **3,657**<br><sub>(184 MiB / 50.4% CPU · 457 MiB/s · 550.7 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **2,828**<br><sub>(234 MiB / 46.6% CPU · 354 MiB/s · 658.7 µs CPU/req)</sub> | **1,052**<br><sub>(124 MiB / 50.9% CPU · 131 MiB/s · 1935.2 µs CPU/req)</sub> | **2,580**<br><sub>(100 MiB / 43.6% CPU · 322 MiB/s · 676.5 µs CPU/req)</sub> | **0**<br><sub>(131 MiB / 0.2% CPU)</sub> | **2,378**<br><sub>(271 MiB / 51.6% CPU · 297 MiB/s · 868.0 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | **5,800**<br><sub>(247 MiB / 43.0% CPU · 725 MiB/s · 296.6 µs CPU/req)</sub> | **4,895**<br><sub>(93 MiB / 46.1% CPU · 612 MiB/s · 376.9 µs CPU/req)</sub> | 🥇 **6,400**<br><sub>(80 MiB / 40.1% CPU · 800 MiB/s · 250.5 µs CPU/req)</sub> | **4,620**<br><sub>(peak 5,929 · 116 MiB / 46.0% CPU · 577 MiB/s · 398.7 µs CPU/req)</sub> | **5,476**<br><sub>(174 MiB / 48.4% CPU · 684 MiB/s · 353.2 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | **6,545**<br><sub>(231 MiB / 35.2% CPU · 818 MiB/s · 215.2 µs CPU/req)</sub> | *Not possible* | **6,160**<br><sub>(98 MiB / 38.1% CPU · 770 MiB/s · 247.5 µs CPU/req)</sub> | 🥇 **6,698**<br><sub>(137 MiB / 41.6% CPU · 837 MiB/s · 248.6 µs CPU/req)</sub> | **4,812**<br><sub>(201 MiB / 50.3% CPU · 602 MiB/s · 418.3 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | **3,029**<br><sub>(228 MiB / 40.4% CPU · 379 MiB/s · 533.1 µs CPU/req)</sub> | *Not possible* | **2,526**<br><sub>(97 MiB / 44.2% CPU · 316 MiB/s · 699.7 µs CPU/req)</sub> | 🥇 **3,043**<br><sub>(141 MiB / 41.6% CPU · 380 MiB/s · 546.8 µs CPU/req)</sub> | **2,184**<br><sub>(200 MiB / 47.3% CPU · 273 MiB/s · 865.8 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | 🥇 **4,252**<br><sub>(259 MiB / 43.1% CPU · 532 MiB/s · 405.3 µs CPU/req)</sub> | *Not possible* | **3,912**<br><sub>(102 MiB / 39.4% CPU · 489 MiB/s · 402.4 µs CPU/req)</sub> | **0**<br><sub>(131 MiB / 0.1% CPU)</sub> | **3,184**<br><sub>(279 MiB / 49.7% CPU · 398 MiB/s · 624.3 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **4,194**<br><sub>(258 MiB / 45.9% CPU · 524 MiB/s · 437.4 µs CPU/req)</sub> | **1,377**<br><sub>(126 MiB / 41.5% CPU · 172 MiB/s · 1206.0 µs CPU/req)</sub> | **3,861**<br><sub>(98 MiB / 42.2% CPU · 483 MiB/s · 436.9 µs CPU/req)</sub> | **0**<br><sub>(126 MiB / 0.1% CPU)</sub> | **3,433**<br><sub>(286 MiB / 48.0% CPU · 429 MiB/s · 559.2 µs CPU/req)</sub> |

Linux nginx H1/H2/H3 POST completed (nginx.org mainline). TWP÷YARP H1 ≈ **1.5×**; H2→H1 ≈ **1.2×**; H3 ≈ **1.1×**. TWP÷nginx H3 ≈ **6×**.

### macOS — POST 64 KiB request + 64 KiB response

Median of **3** repeats on `macos-15` (Apple Silicon M1, 3-core / 7 GB) @ `8567b4c0`. Source: Actions [40610476097](https://github.com/justcoding121/titanium-web-proxy/actions/runs/40610476097) (`compare-post`).

| Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---:|---:|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | **2,961**<br><sub>(259 MiB / 26.3% CPU · 370 MiB/s · 266.2 µs CPU/req)</sub> | 🥇 **3,865**<br><sub>(89 MiB / 20.8% CPU · 483 MiB/s · 161.4 µs CPU/req)</sub> | **3,389**<br><sub>(119 MiB / 26.1% CPU · 424 MiB/s · 230.6 µs CPU/req)</sub> | **3,352**<br><sub>(133 MiB / 27.7% CPU · 419 MiB/s · 248.1 µs CPU/req)</sub> | **1,945**<br><sub>(200 MiB / 33.4% CPU · 243 MiB/s · 515.9 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | **2,109**<br><sub>(529 MiB / 21.2% CPU · 264 MiB/s · 302.0 µs CPU/req)</sub> | 🥇 **3,303**<br><sub>(99 MiB / 21.1% CPU · 413 MiB/s · 191.4 µs CPU/req)</sub> | **3,196**<br><sub>(124 MiB / 25.6% CPU · 400 MiB/s · 240.4 µs CPU/req)</sub> | **2,050**<br><sub>(peak 2,741 · 122 MiB / 24.6% CPU · 256 MiB/s · 359.3 µs CPU/req)</sub> | **1,616**<br><sub>(178 MiB / 23.4% CPU · 202 MiB/s · 433.8 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | **1,035**<br><sub>(330 MiB / 27.1% CPU · 129 MiB/s · 785.8 µs CPU/req)</sub> | 🥇 **1,285**<br><sub>(96 MiB / 23.2% CPU · 161 MiB/s · 542.6 µs CPU/req)</sub> | **978**<br><sub>(120 MiB / 30.8% CPU · 122 MiB/s · 944.7 µs CPU/req)</sub> | **858**<br><sub>(138 MiB / 47.8% CPU · 107 MiB/s · 1671.7 µs CPU/req)</sub> | **737**<br><sub>(435 MiB / 25.4% CPU · 92 MiB/s · 1033.9 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | **2,633**<br><sub>(508 MiB / 21.1% CPU · 329 MiB/s · 240.5 µs CPU/req)</sub> | **3,206**<br><sub>(86 MiB / 14.0% CPU · 401 MiB/s · 131.3 µs CPU/req)</sub> | 🥇 **3,281**<br><sub>(112 MiB / 21.8% CPU · 410 MiB/s · 199.1 µs CPU/req)</sub> | **1,958**<br><sub>(peak 2,661 · 122 MiB / 13.9% CPU · 245 MiB/s · 212.4 µs CPU/req)</sub> | **2,324**<br><sub>(243 MiB / 23.3% CPU · 290 MiB/s · 300.7 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | **1,876**<br><sub>(414 MiB / 17.4% CPU · 235 MiB/s · 277.4 µs CPU/req)</sub> | *Not possible* | 🥇 **2,798**<br><sub>(128 MiB / 26.4% CPU · 350 MiB/s · 282.6 µs CPU/req)</sub> | **2,684**<br><sub>(129 MiB / 20.2% CPU · 336 MiB/s · 226.3 µs CPU/req)</sub> | **878**<br><sub>(213 MiB / 15.2% CPU · 110 MiB/s · 518.2 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | **1,599**<br><sub>(457 MiB / 20.7% CPU · 200 MiB/s · 387.9 µs CPU/req)</sub> | *Not possible* | 🥇 **2,102**<br><sub>(129 MiB / 29.1% CPU · 263 MiB/s · 415.2 µs CPU/req)</sub> | **1,971**<br><sub>(130 MiB / 22.6% CPU · 246 MiB/s · 343.9 µs CPU/req)</sub> | **1,090**<br><sub>(225 MiB / 21.8% CPU · 136 MiB/s · 599.9 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | **835**<br><sub>(342 MiB / 23.6% CPU · 104 MiB/s · 847.4 µs CPU/req)</sub> | *Not possible* | 🥇 **1,062**<br><sub>(125 MiB / 33.5% CPU · 133 MiB/s · 947.6 µs CPU/req)</sub> | **765**<br><sub>(137 MiB / 47.4% CPU · 96 MiB/s · 1856.3 µs CPU/req)</sub> | **766**<br><sub>(412 MiB / 28.0% CPU · 96 MiB/s · 1095.9 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | **768**<br><sub>(261 MiB / 29.7% CPU · 96 MiB/s · 1161.2 µs CPU/req)</sub> | **782**<br><sub>(100 MiB / 19.5% CPU · 98 MiB/s · 749.0 µs CPU/req)</sub> | 🥇 **852**<br><sub>(125 MiB / 32.0% CPU · 106 MiB/s · 1127.0 µs CPU/req)</sub> | **343**<br><sub>(peak 358 · 133 MiB / 22.4% CPU · 43 MiB/s · 1954.2 µs CPU/req)</sub> | **606**<br><sub>(445 MiB / 33.5% CPU · 76 MiB/s · 1657.5 µs CPU/req)</sub> |

### Windows — lossy / high-RTT (H2 HOL / H3 loss)

Userspace **5 ms** one-way delay + **1%** TCP connection stall (H1/H2); UDP is **loss% only** (no per-datagram delay; MsQuic-safe) + **1%** datagram drop (H3); **64 KiB** GET. Median of **3** repeats on `windows-latest` @ `8567b4c0` — [40610479308](https://github.com/justcoding121/titanium-web-proxy/actions/runs/40610479308) (`compare-lossy`).

*Not possible:* **HAProxy** and **Envoy** columns are omitted (no official Windows port).


| Client | Origin | TWP | nginx | YARP |
|---|---|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | 🥇 **662**<br><sub>(89 MiB / 4.6% CPU · 41 MiB/s · 276.6 µs CPU/req)</sub> | **634**<br><sub>(143 MiB / 18.6% CPU · 40 MiB/s · 1174.6 µs CPU/req)</sub> | **660**<br><sub>(121 MiB / 5.3% CPU · 41 MiB/s · 322.2 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **248**<br><sub>(163 MiB / 3.4% CPU · 15 MiB/s · 547.4 µs CPU/req)</sub> | **243**<br><sub>(144 MiB / 8.7% CPU · 15 MiB/s · 1429.7 µs CPU/req)</sub> | **231**<br><sub>(117 MiB / 3.9% CPU · 14 MiB/s · 683.1 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | **868**<br><sub>(132 MiB / 19.0% CPU · 54 MiB/s · 874.5 µs CPU/req)</sub> | *Not possible (no QUIC)* | 🥇 **888**<br><sub>(185 MiB / 28.4% CPU · 55 MiB/s · 1280.0 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | 🥇 **264**<br><sub>(154 MiB / 1.1% CPU · 16 MiB/s · 174.5 µs CPU/req)</sub> | **259**<br><sub>(129 MiB / 1.0% CPU · 16 MiB/s · 154.2 µs CPU/req)</sub> | **236**<br><sub>(104 MiB / 0.7% CPU · 15 MiB/s · 118.8 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | **223**<br><sub>(124 MiB / 2.4% CPU · 14 MiB/s · 436.3 µs CPU/req)</sub> | *Not possible* | 🥇 **230**<br><sub>(118 MiB / 3.9% CPU · 14 MiB/s · 668.4 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | **224**<br><sub>(124 MiB / 3.3% CPU · 14 MiB/s · 588.2 µs CPU/req)</sub> | *Not possible* | 🥇 **234**<br><sub>(117 MiB / 2.2% CPU · 15 MiB/s · 385.3 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | 🥇 **876**<br><sub>(119 MiB / 20.6% CPU · 55 MiB/s · 939.2 µs CPU/req)</sub> | *Not possible* | **852**<br><sub>(182 MiB / 30.1% CPU · 53 MiB/s · 1413.0 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | **1,068**<br><sub>(135 MiB / 17.8% CPU · 67 MiB/s · 667.9 µs CPU/req)</sub> | *Not possible (no QUIC)* | 🥇 **1,215**<br><sub>(187 MiB / 31.5% CPU · 76 MiB/s · 1037.3 µs CPU/req)</sub> |

H1, H2, and H3 loss sustain are in the table. Prefer the ratio columns.

### Linux — lossy / high-RTT (H2 HOL / H3 loss)

Median of **3** repeats @ `8567b4c0`. Source: [40610479308](https://github.com/justcoding121/titanium-web-proxy/actions/runs/40610479308) (`compare-lossy`; lossy H3 uses `quic-http3`, UDP drop-only).


| Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---:|---:|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | **1,202**<br><sub>(133 MiB / 12.4% CPU · 75 MiB/s · 411.2 µs CPU/req)</sub> | 🥇 **1,212**<br><sub>(114 MiB / 12.0% CPU · 76 MiB/s · 397.4 µs CPU/req)</sub> | **1,209**<br><sub>(91 MiB / 6.4% CPU · 76 MiB/s · 212.0 µs CPU/req)</sub> | **1,201**<br><sub>(128 MiB / 8.8% CPU · 75 MiB/s · 293.0 µs CPU/req)</sub> | **1,199**<br><sub>(161 MiB / 15.8% CPU · 75 MiB/s · 527.1 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **628**<br><sub>(208 MiB / 12.5% CPU · 39 MiB/s · 795.2 µs CPU/req)</sub> | **568**<br><sub>(114 MiB / 6.0% CPU · 35 MiB/s · 420.8 µs CPU/req)</sub> | **557**<br><sub>(92 MiB / 4.4% CPU · 35 MiB/s · 315.3 µs CPU/req)</sub> | **508**<br><sub>(130 MiB / 4.7% CPU · 32 MiB/s · 371.8 µs CPU/req)</sub> | **509**<br><sub>(156 MiB / 12.2% CPU · 32 MiB/s · 959.7 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **1,319**<br><sub>(177 MiB / 23.3% CPU · 82 MiB/s · 705.9 µs CPU/req)</sub> | **1,242**<br><sub>(128 MiB / 23.1% CPU · 78 MiB/s · 743.0 µs CPU/req)</sub> | **1,081**<br><sub>(108 MiB / 18.9% CPU · 68 MiB/s · 697.7 µs CPU/req)</sub> | **1,210**<br><sub>(139 MiB / 26.3% CPU · 76 MiB/s · 868.5 µs CPU/req)</sub> | **1,311**<br><sub>(245 MiB / 31.0% CPU · 82 MiB/s · 947.1 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | 🥇 **654**<br><sub>(198 MiB / 8.6% CPU · 41 MiB/s · 528.1 µs CPU/req)</sub> | **594**<br><sub>(86 MiB / 3.2% CPU · 37 MiB/s · 217.6 µs CPU/req)</sub> | **547**<br><sub>(75 MiB / 2.3% CPU · 34 MiB/s · 168.9 µs CPU/req)</sub> | **509**<br><sub>(121 MiB / 3.3% CPU · 32 MiB/s · 256.1 µs CPU/req)</sub> | **512**<br><sub>(146 MiB / 7.5% CPU · 32 MiB/s · 588.1 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | **500**<br><sub>(165 MiB / 10.0% CPU · 31 MiB/s · 800.2 µs CPU/req)</sub> | *Not possible* | 🥇 **553**<br><sub>(94 MiB / 4.8% CPU · 35 MiB/s · 348.8 µs CPU/req)</sub> | **507**<br><sub>(129 MiB / 5.7% CPU · 32 MiB/s · 448.9 µs CPU/req)</sub> | **511**<br><sub>(164 MiB / 13.4% CPU · 32 MiB/s · 1047.0 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | **486**<br><sub>(174 MiB / 11.0% CPU · 30 MiB/s · 904.7 µs CPU/req)</sub> | *Not possible* | 🥇 **586**<br><sub>(93 MiB / 5.9% CPU · 37 MiB/s · 402.8 µs CPU/req)</sub> | **510**<br><sub>(132 MiB / 5.8% CPU · 32 MiB/s · 456.8 µs CPU/req)</sub> | **509**<br><sub>(161 MiB / 14.2% CPU · 32 MiB/s · 1117.7 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | 🥇 **1,507**<br><sub>(165 MiB / 23.6% CPU · 94 MiB/s · 627.2 µs CPU/req)</sub> | *Not possible* | **1,376**<br><sub>(106 MiB / 20.4% CPU · 86 MiB/s · 594.5 µs CPU/req)</sub> | **1,433**<br><sub>(136 MiB / 27.9% CPU · 90 MiB/s · 780.1 µs CPU/req)</sub> | **1,473**<br><sub>(247 MiB / 31.7% CPU · 92 MiB/s · 861.8 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | **1,620**<br><sub>(190 MiB / 18.1% CPU · 101 MiB/s · 446.8 µs CPU/req)</sub> | **0**<br><sub>(peak 2,032 · 135 MiB / 23.0% CPU)</sub> | **1,856**<br><sub>(111 MiB / 19.4% CPU · 116 MiB/s · 418.7 µs CPU/req)</sub> | 🥇 **2,010**<br><sub>(139 MiB / 24.5% CPU · 126 MiB/s · 487.9 µs CPU/req)</sub> | **1,680**<br><sub>(240 MiB / 25.3% CPU · 105 MiB/s · 601.8 µs CPU/req)</sub> |

TWP H2 HOL ≫ YARP (~**7.8×**). H3 TWP÷YARP ≈ **1.07×**.

### macOS — lossy / high-RTT (H2 HOL / H3 loss)

Median of **3** repeats on `macos-15` (Apple Silicon M1, 3-core / 7 GB) @ `8567b4c0`. Source: [40610479308](https://github.com/justcoding121/titanium-web-proxy/actions/runs/40610479308) (`compare-lossy`; lossy H3 uses `quic-http3`, UDP drop-only).

| Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---:|---:|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | **316**<br><sub>(244 MiB / 4.4% CPU · 20 MiB/s · 418.1 µs CPU/req)</sub> | **375**<br><sub>(84 MiB / 1.4% CPU · 23 MiB/s · 115.2 µs CPU/req)</sub> | 🥇 **441**<br><sub>(114 MiB / 2.3% CPU · 28 MiB/s · 157.9 µs CPU/req)</sub> | **415**<br><sub>(129 MiB / 3.0% CPU · 26 MiB/s · 220.6 µs CPU/req)</sub> | **309**<br><sub>(237 MiB / 4.1% CPU · 19 MiB/s · 394.9 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **151**<br><sub>(409 MiB / 4.1% CPU · 9.4 MiB/s · 818.5 µs CPU/req)</sub> | **137**<br><sub>(85 MiB / 1.0% CPU · 8.6 MiB/s · 212.6 µs CPU/req)</sub> | **146**<br><sub>(114 MiB / 1.6% CPU · 9.1 MiB/s · 334.2 µs CPU/req)</sub> | **127**<br><sub>(129 MiB / 1.5% CPU · 8.0 MiB/s · 360.3 µs CPU/req)</sub> | **128**<br><sub>(229 MiB / 3.0% CPU · 8.0 MiB/s · 708.9 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | **800**<br><sub>(271 MiB / 18.4% CPU · 50 MiB/s · 691.6 µs CPU/req)</sub> | **748**<br><sub>(89 MiB / 11.6% CPU · 47 MiB/s · 464.0 µs CPU/req)</sub> | **565**<br><sub>(122 MiB / 14.3% CPU · 35 MiB/s · 757.4 µs CPU/req)</sub> | 🥇 **1,056**<br><sub>(132 MiB / 30.6% CPU · 66 MiB/s · 869.8 µs CPU/req)</sub> | **501**<br><sub>(365 MiB / 19.1% CPU · 31 MiB/s · 1141.2 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | **137**<br><sub>(293 MiB / 2.9% CPU · 8.6 MiB/s · 630.2 µs CPU/req)</sub> | **136**<br><sub>(72 MiB / 0.5% CPU · 8.5 MiB/s · 112.7 µs CPU/req)</sub> | 🥇 **151**<br><sub>(103 MiB / 1.0% CPU · 9.4 MiB/s · 192.5 µs CPU/req)</sub> | **144**<br><sub>(122 MiB / 1.3% CPU · 9.0 MiB/s · 265.5 µs CPU/req)</sub> | **143**<br><sub>(145 MiB / 2.6% CPU · 9.0 MiB/s · 550.6 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | **104**<br><sub>(314 MiB / 2.3% CPU · 6.5 MiB/s · 660.2 µs CPU/req)</sub> | *Not possible* | 🥇 **140**<br><sub>(121 MiB / 1.7% CPU · 8.7 MiB/s · 367.2 µs CPU/req)</sub> | **128**<br><sub>(123 MiB / 1.6% CPU · 8.0 MiB/s · 366.5 µs CPU/req)</sub> | **120**<br><sub>(282 MiB / 3.5% CPU · 7.5 MiB/s · 885.7 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | **143**<br><sub>(332 MiB / 5.4% CPU · 9.0 MiB/s · 1129.7 µs CPU/req)</sub> | *Not possible* | 🥇 **152**<br><sub>(116 MiB / 2.6% CPU · 9.5 MiB/s · 511.9 µs CPU/req)</sub> | **130**<br><sub>(124 MiB / 1.9% CPU · 8.1 MiB/s · 435.1 µs CPU/req)</sub> | **146**<br><sub>(249 MiB / 5.3% CPU · 9.1 MiB/s · 1092.0 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | **812**<br><sub>(202 MiB / 19.0% CPU · 51 MiB/s · 700.6 µs CPU/req)</sub> | *Not possible* | **652**<br><sub>(127 MiB / 13.9% CPU · 41 MiB/s · 637.9 µs CPU/req)</sub> | 🥇 **1,138**<br><sub>(131 MiB / 27.2% CPU · 71 MiB/s · 716.4 µs CPU/req)</sub> | **1,035**<br><sub>(465 MiB / 22.7% CPU · 65 MiB/s · 657.9 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | **521**<br><sub>(207 MiB / 19.6% CPU · 33 MiB/s · 1130.5 µs CPU/req)</sub> | **646**<br><sub>(98 MiB / 13.3% CPU · 40 MiB/s · 617.9 µs CPU/req)</sub> | **267**<br><sub>(126 MiB / 13.7% CPU · 17 MiB/s · 1538.8 µs CPU/req)</sub> | 🥇 **720**<br><sub>(133 MiB / 31.4% CPU · 45 MiB/s · 1308.4 µs CPU/req)</sub> | **537**<br><sub>(338 MiB / 24.9% CPU · 34 MiB/s · 1391.3 µs CPU/req)</sub> |

### Architecture-sensitive

These runs isolate slow app readers, origin-early response, HTTP/2 duplex, and WebSocket echo. See [TWP vs YARP IO model](Performance-Profiling#user-content-twp-vs-yarp-io-model). Laptop 1-rep numbers are on [Performance Local Lab](Performance-Local-Lab#user-content-architecture-sensitive).

Median of **3** repeats on matched 4 vCPU / 16 GiB runners @ `8567b4c0` ([40610485919](https://github.com/justcoding121/titanium-web-proxy/actions/runs/40610485919)). Slow consumer = 256 KiB GET, 16 KiB read + 8 ms sleep. Early response = 64 KiB POST, origin writes after 8 KiB. Duplex HTTP/2 = overlapping 64 KiB POST on H2 TLS↔H2 TLS. WebSocket row = H1 Upgrade echo round-trips/sec (not RFC 8441; see WebSocket RFC 8441 table below).

Lossy-link runs (slow **network**) are already published above; they are not a slow **app** reader.

#### Windows


*Not possible:* **HAProxy** and **Envoy** columns are omitted (no official Windows port).

| Scenario | Client | Origin | TWP | nginx | YARP |
|---|---|---|---:|---:|---:|
| Slow consumer (256 KiB GET, throttled client read) | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **248**<br><sub>(97 MiB / 5.2% CPU · 62 MiB/s · 841.0 µs CPU/req)</sub> | **222**<br><sub>(145 MiB / 24.6% CPU · 55 MiB/s · 4438.4 µs CPU/req)</sub> | **248**<br><sub>(111 MiB / 5.6% CPU · 62 MiB/s · 899.8 µs CPU/req)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · TLS | HTTP/1 · plain | **248**<br><sub>(148 MiB / 6.7% CPU · 62 MiB/s · 1088.4 µs CPU/req)</sub> | **161**<br><sub>(144 MiB / 24.8% CPU · 40 MiB/s · 6155.2 µs CPU/req)</sub> | 🥇 **248**<br><sub>(122 MiB / 7.6% CPU · 62 MiB/s · 1223.6 µs CPU/req)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/3 · QUIC | HTTP/1 · plain | **236**<br><sub>(115 MiB / 46.8% CPU · 59 MiB/s · 7932.1 µs CPU/req)</sub> | *Not possible (no QUIC)* | 🥇 **239**<br><sub>(157 MiB / 39.9% CPU · 60 MiB/s · 6665.0 µs CPU/req)</sub> |
| Early response (origin writes after first request chunk) | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **11,965**<br><sub>(98 MiB / 41.6% CPU · 1,496 MiB/s · 139.2 µs CPU/req)</sub> | **674**<br><sub>(144 MiB / 24.8% CPU · 84 MiB/s · 1472.3 µs CPU/req)</sub> | **8,043**<br><sub>(139 MiB / 53.7% CPU · 1,005 MiB/s · 266.9 µs CPU/req)</sub> |
| Early response (origin writes after first request chunk) | HTTP/2 · TLS | HTTP/1 · plain | **6,829**<br><sub>(215 MiB / 51.5% CPU · 854 MiB/s · 301.7 µs CPU/req)</sub> | **688**<br><sub>(148 MiB / 24.8% CPU · 86 MiB/s · 1439.1 µs CPU/req)</sub> | 🥇 **7,982**<br><sub>(133 MiB / 46.3% CPU · 998 MiB/s · 232.2 µs CPU/req)</sub> |
| Early response (origin writes after first request chunk) | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **2,403**<br><sub>(173 MiB / 45.1% CPU · 300 MiB/s · 751.2 µs CPU/req)</sub> | *Not possible (no QUIC)* | **1,621**<br><sub>(220 MiB / 50.0% CPU · 203 MiB/s · 1234.9 µs CPU/req)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · plain | HTTP/1 · plain | **248**<br><sub>(142 MiB / 3.7% CPU · 62 MiB/s · 598.9 µs CPU/req)</sub> | **248**<br><sub>(129 MiB / 9.1% CPU · 62 MiB/s · 1475.0 µs CPU/req)</sub> | 🥇 **248**<br><sub>(109 MiB / 4.5% CPU · 62 MiB/s · 723.2 µs CPU/req)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · TLS | HTTP/2 · TLS | **250**<br><sub>(162 MiB / 4.8% CPU · 62 MiB/s · 771.2 µs CPU/req)</sub> | *Not possible* | 🥇 **256**<br><sub>(147 MiB / 4.5% CPU · 64 MiB/s · 709.2 µs CPU/req)</sub> |
| Early response (origin writes after first request chunk) | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **3,289**<br><sub>(189 MiB / 37.2% CPU · 411 MiB/s · 452.8 µs CPU/req)</sub> | *Not possible* | **2,705**<br><sub>(162 MiB / 56.7% CPU · 338 MiB/s · 838.2 µs CPU/req)</sub> |
| Duplex (both directions live) | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **3,354**<br><sub>(201 MiB / 39.8% CPU · 419 MiB/s · 475.0 µs CPU/req)</sub> | *Not possible* | **2,730**<br><sub>(166 MiB / 54.4% CPU · 341 MiB/s · 797.0 µs CPU/req)</sub> |
| Duplex (WebSocket / H1 Upgrade) | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **40,676**<br><sub>(96 MiB / 44.7% CPU · 44.0 µs CPU/req)</sub> | **22,931**<br><sub>(145 MiB / 24.9% CPU · 43.3 µs CPU/req)</sub> | **37,580**<br><sub>(89 MiB / 44.3% CPU · 47.1 µs CPU/req)</sub> |

#### Linux


| Scenario | Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---|---:|---:|---:|---:|---:|
| Slow consumer (256 KiB GET, throttled client read) | HTTP/1 · TLS | HTTP/1 · plain | **466**<br><sub>(131 MiB / 9.5% CPU · 117 MiB/s · 817.5 µs CPU/req)</sub> | **467**<br><sub>(113 MiB / 15.0% CPU · 117 MiB/s · 1282.5 µs CPU/req)</sub> | 🥇 **473**<br><sub>(91 MiB / 5.5% CPU · 118 MiB/s · 463.7 µs CPU/req)</sub> | **472**<br><sub>(141 MiB / 7.3% CPU · 118 MiB/s · 620.9 µs CPU/req)</sub> | **467**<br><sub>(161 MiB / 13.8% CPU · 117 MiB/s · 1184.7 µs CPU/req)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · TLS | HTTP/1 · plain | **475**<br><sub>(175 MiB / 20.3% CPU · 119 MiB/s · 1705.4 µs CPU/req)</sub> | **474**<br><sub>(114 MiB / 20.2% CPU · 118 MiB/s · 1703.3 µs CPU/req)</sub> | 🥇 **479**<br><sub>(90 MiB / 12.6% CPU · 120 MiB/s · 1054.7 µs CPU/req)</sub> | **474**<br><sub>(147 MiB / 10.9% CPU · 119 MiB/s · 923.1 µs CPU/req)</sub> | **473**<br><sub>(171 MiB / 23.1% CPU · 118 MiB/s · 1955.2 µs CPU/req)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/3 · QUIC | HTTP/1 · plain | **457**<br><sub>(157 MiB / 48.1% CPU · 114 MiB/s · 4210.1 µs CPU/req)</sub> | **465**<br><sub>(125 MiB / 26.0% CPU · 116 MiB/s · 2237.2 µs CPU/req)</sub> | 🥇 **469**<br><sub>(98 MiB / 13.7% CPU · 117 MiB/s · 1170.0 µs CPU/req)</sub> | **467**<br><sub>(152 MiB / 26.5% CPU · 117 MiB/s · 2271.0 µs CPU/req)</sub> | **459**<br><sub>(218 MiB / 49.6% CPU · 115 MiB/s · 4325.2 µs CPU/req)</sub> |
| Early response (origin writes after first request chunk) | HTTP/1 · TLS | HTTP/1 · plain | **5,034**<br><sub>(152 MiB / 47.1% CPU · 629 MiB/s · 374.7 µs CPU/req)</sub> | **3,971**<br><sub>(115 MiB / 52.8% CPU · 496 MiB/s · 531.6 µs CPU/req)</sub> | 🥇 **5,524**<br><sub>(95 MiB / 44.8% CPU · 690 MiB/s · 324.1 µs CPU/req)</sub> | **0**<br><sub>(peak 2,453 · 130 MiB / 24.6% CPU)</sub> | **3,445**<br><sub>(176 MiB / 58.0% CPU · 431 MiB/s · 673.1 µs CPU/req)</sub> |
| Early response (origin writes after first request chunk) | HTTP/2 · TLS | HTTP/1 · plain | **5,468**<br><sub>(273 MiB / 52.4% CPU · 684 MiB/s · 383.3 µs CPU/req)</sub> | **6,433**<br><sub>(119 MiB / 45.1% CPU · 804 MiB/s · 280.4 µs CPU/req)</sub> | 🥇 **7,754**<br><sub>(96 MiB / 44.0% CPU · 969 MiB/s · 227.1 µs CPU/req)</sub> | **0**<br><sub>(peak 7,985 · 141 MiB / 42.9% CPU)</sub> | **6,225**<br><sub>(182 MiB / 47.7% CPU · 778 MiB/s · 306.6 µs CPU/req)</sub> |
| Early response (origin writes after first request chunk) | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **2,768**<br><sub>(240 MiB / 44.9% CPU · 346 MiB/s · 649.1 µs CPU/req)</sub> | **1,134**<br><sub>(127 MiB / 40.3% CPU · 142 MiB/s · 1422.6 µs CPU/req)</sub> | **2,161**<br><sub>(101 MiB / 40.7% CPU · 270 MiB/s · 753.1 µs CPU/req)</sub> | **0**<br><sub>(129 MiB / 0.1% CPU)</sub> | **1,714**<br><sub>(291 MiB / 49.7% CPU · 214 MiB/s · 1160.6 µs CPU/req)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · plain | HTTP/1 · plain | **475**<br><sub>(160 MiB / 13.6% CPU · 119 MiB/s · 1140.6 µs CPU/req)</sub> | **480**<br><sub>(86 MiB / 13.5% CPU · 120 MiB/s · 1129.3 µs CPU/req)</sub> | 🥇 **480**<br><sub>(74 MiB / 6.4% CPU · 120 MiB/s · 534.2 µs CPU/req)</sub> | **474**<br><sub>(133 MiB / 6.4% CPU · 119 MiB/s · 541.3 µs CPU/req)</sub> | **480**<br><sub>(157 MiB / 14.7% CPU · 120 MiB/s · 1227.8 µs CPU/req)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · TLS | HTTP/2 · TLS | **476**<br><sub>(228 MiB / 12.5% CPU · 119 MiB/s · 1049.6 µs CPU/req)</sub> | *Not possible* | **480**<br><sub>(103 MiB / 9.2% CPU · 120 MiB/s · 767.7 µs CPU/req)</sub> | **478**<br><sub>(141 MiB / 8.1% CPU · 120 MiB/s · 677.3 µs CPU/req)</sub> | 🥇 **480**<br><sub>(191 MiB / 15.8% CPU · 120 MiB/s · 1318.6 µs CPU/req)</sub> |
| Early response (origin writes after first request chunk) | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **3,118**<br><sub>(230 MiB / 41.3% CPU · 390 MiB/s · 529.6 µs CPU/req)</sub> | *Not possible* | **2,208**<br><sub>(100 MiB / 36.4% CPU · 276 MiB/s · 659.8 µs CPU/req)</sub> | **0**<br><sub>(peak 2,692 · 144 MiB / 37.8% CPU)</sub> | **2,203**<br><sub>(217 MiB / 54.8% CPU · 275 MiB/s · 994.8 µs CPU/req)</sub> |
| Duplex (both directions live) | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **4,817**<br><sub>(237 MiB / 38.6% CPU · 602 MiB/s · 320.9 µs CPU/req)</sub> | *Not possible* | **3,235**<br><sub>(94 MiB / 33.5% CPU · 404 MiB/s · 414.4 µs CPU/req)</sub> | **0**<br><sub>(peak 3,793 · 138 MiB / 36.2% CPU)</sub> | **3,580**<br><sub>(216 MiB / 55.1% CPU · 447 MiB/s · 616.2 µs CPU/req)</sub> |
| Duplex (WebSocket / H1 Upgrade) | HTTP/1 · TLS | HTTP/1 · plain | **43,568**<br><sub>(138 MiB / 41.4% CPU · 38.0 µs CPU/req)</sub> | 🥇 **53,013**<br><sub>(115 MiB / 34.4% CPU · 25.9 µs CPU/req)</sub> | **49,874**<br><sub>(89 MiB / 38.5% CPU · 30.8 µs CPU/req)</sub> | **47,871**<br><sub>(127 MiB / 41.4% CPU · 34.6 µs CPU/req)</sub> | **41,874**<br><sub>(142 MiB / 43.9% CPU · 41.9 µs CPU/req)</sub> |

#### macOS

Median of **3** repeats on `macos-15` (Apple Silicon M1, 3-core / 7 GB) @ `8567b4c0`. Source: Actions [40610485919](https://github.com/justcoding121/titanium-web-proxy/actions/runs/40610485919) (`compare-arch`).

| Scenario | Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---|---:|---:|---:|---:|---:|
| Slow consumer (256 KiB GET, throttled client read) | HTTP/1 · TLS | HTTP/1 · plain | **98**<br><sub>(257 MiB / 3.8% CPU · 24 MiB/s · 1160.0 µs CPU/req)</sub> | 🥇 **120**<br><sub>(85 MiB / 1.0% CPU · 30 MiB/s · 240.2 µs CPU/req)</sub> | **104**<br><sub>(113 MiB / 1.2% CPU · 26 MiB/s · 341.4 µs CPU/req)</sub> | **83**<br><sub>(151 MiB / 1.0% CPU · 21 MiB/s · 355.1 µs CPU/req)</sub> | **96**<br><sub>(228 MiB / 2.1% CPU · 24 MiB/s · 660.8 µs CPU/req)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · TLS | HTTP/1 · plain | **106**<br><sub>(329 MiB / 8.7% CPU · 27 MiB/s · 2441.7 µs CPU/req)</sub> | **96**<br><sub>(85 MiB / 1.4% CPU · 24 MiB/s · 448.3 µs CPU/req)</sub> | 🥇 **111**<br><sub>(115 MiB / 2.8% CPU · 28 MiB/s · 742.6 µs CPU/req)</sub> | **96**<br><sub>(155 MiB / 1.9% CPU · 24 MiB/s · 593.1 µs CPU/req)</sub> | **88**<br><sub>(228 MiB / 3.7% CPU · 22 MiB/s · 1271.1 µs CPU/req)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/3 · QUIC | HTTP/1 · plain | **64**<br><sub>(133 MiB / 20.6% CPU · 16 MiB/s · 9682.9 µs CPU/req)</sub> | 🥇 **111**<br><sub>(95 MiB / 5.8% CPU · 28 MiB/s · 1551.2 µs CPU/req)</sub> | **111**<br><sub>(111 MiB / 11.0% CPU · 28 MiB/s · 2972.9 µs CPU/req)</sub> | **80**<br><sub>(145 MiB / 11.2% CPU · 20 MiB/s · 4197.7 µs CPU/req)</sub> | **87**<br><sub>(379 MiB / 24.6% CPU · 22 MiB/s · 8450.8 µs CPU/req)</sub> |
| Early response (origin writes after first request chunk) | HTTP/1 · TLS | HTTP/1 · plain | **2,544**<br><sub>(250 MiB / 23.2% CPU · 318 MiB/s · 273.3 µs CPU/req)</sub> | **0**<br><sub>(peak 3,658 · 89 MiB / 17.9% CPU)</sub> | 🥇 **3,536**<br><sub>(121 MiB / 25.0% CPU · 442 MiB/s · 212.3 µs CPU/req)</sub> | **0**<br><sub>(peak 2,908 · 134 MiB / 24.8% CPU)</sub> | **2,075**<br><sub>(199 MiB / 31.6% CPU · 259 MiB/s · 457.4 µs CPU/req)</sub> |
| Early response (origin writes after first request chunk) | HTTP/2 · TLS | HTTP/1 · plain | **2,205**<br><sub>(470 MiB / 31.3% CPU · 276 MiB/s · 425.3 µs CPU/req)</sub> | **2,922**<br><sub>(99 MiB / 20.0% CPU · 365 MiB/s · 205.5 µs CPU/req)</sub> | 🥇 **3,288**<br><sub>(124 MiB / 27.0% CPU · 411 MiB/s · 246.1 µs CPU/req)</sub> | **0**<br><sub>(peak 2,521 · 144 MiB / 22.6% CPU)</sub> | **1,566**<br><sub>(182 MiB / 20.9% CPU · 196 MiB/s · 399.8 µs CPU/req)</sub> |
| Early response (origin writes after first request chunk) | HTTP/3 · QUIC | HTTP/1 · plain | **725**<br><sub>(273 MiB / 27.9% CPU · 91 MiB/s · 1153.8 µs CPU/req)</sub> | 🥇 **1,163**<br><sub>(102 MiB / 22.1% CPU · 145 MiB/s · 569.6 µs CPU/req)</sub> | **168**<br><sub>(103 MiB / 10.4% CPU · 21 MiB/s · 1855.5 µs CPU/req)</sub> | **902**<br><sub>(134 MiB / 42.0% CPU · 113 MiB/s · 1398.4 µs CPU/req)</sub> | **489**<br><sub>(392 MiB / 24.7% CPU · 61 MiB/s · 1514.7 µs CPU/req)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · plain | HTTP/1 · plain | **96**<br><sub>(223 MiB / 3.1% CPU · 24 MiB/s · 959.2 µs CPU/req)</sub> | 🥇 **97**<br><sub>(72 MiB / 1.0% CPU · 24 MiB/s · 320.3 µs CPU/req)</sub> | **96**<br><sub>(100 MiB / 1.2% CPU · 24 MiB/s · 372.7 µs CPU/req)</sub> | **88**<br><sub>(145 MiB / 1.3% CPU · 22 MiB/s · 433.9 µs CPU/req)</sub> | **96**<br><sub>(146 MiB / 2.5% CPU · 24 MiB/s · 776.6 µs CPU/req)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · TLS | HTTP/2 · TLS | **88**<br><sub>(465 MiB / 3.7% CPU · 22 MiB/s · 1274.5 µs CPU/req)</sub> | *Not possible* | 🥇 **104**<br><sub>(126 MiB / 3.0% CPU · 26 MiB/s · 852.6 µs CPU/req)</sub> | **88**<br><sub>(131 MiB / 2.5% CPU · 22 MiB/s · 862.0 µs CPU/req)</sub> | **96**<br><sub>(346 MiB / 4.7% CPU · 24 MiB/s · 1468.6 µs CPU/req)</sub> |
| Early response (origin writes after first request chunk) | HTTP/2 · TLS | HTTP/2 · TLS | **1,740**<br><sub>(489 MiB / 20.3% CPU · 217 MiB/s · 350.2 µs CPU/req)</sub> | *Not possible* | **1,637**<br><sub>(116 MiB / 20.1% CPU · 205 MiB/s · 367.9 µs CPU/req)</sub> | **0**<br><sub>(peak 1,350 · 134 MiB / 18.3% CPU)</sub> | 🥇 **2,147**<br><sub>(356 MiB / 32.6% CPU · 268 MiB/s · 455.2 µs CPU/req)</sub> |
| Duplex (both directions live) | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **1,700**<br><sub>(449 MiB / 22.1% CPU · 213 MiB/s · 389.2 µs CPU/req)</sub> | *Not possible* | **1,327**<br><sub>(118 MiB / 23.4% CPU · 166 MiB/s · 529.3 µs CPU/req)</sub> | **0**<br><sub>(peak 1,120 · 133 MiB / 20.7% CPU)</sub> | **1,360**<br><sub>(326 MiB / 29.6% CPU · 170 MiB/s · 652.8 µs CPU/req)</sub> |
| Duplex (WebSocket / H1 Upgrade) | HTTP/1 · TLS | HTTP/1 · plain | **6,829**<br><sub>(148 MiB / 12.5% CPU · 54.8 µs CPU/req)</sub> | **5,892**<br><sub>(86 MiB / 6.8% CPU · 34.6 µs CPU/req)</sub> | **7,150**<br><sub>(103 MiB / 9.9% CPU · 41.5 µs CPU/req)</sub> | **5,979**<br><sub>(123 MiB / 11.8% CPU · 59.2 µs CPU/req)</sub> | 🥇 **8,414**<br><sub>(145 MiB / 15.7% CPU · 55.8 µs CPU/req)</sub> |

Slow consumer is sleep-bound; H1/H2/H3 sit in the same band. Prefer the ratio columns for early-response, duplex, and WebSocket rows.

### TLS termination cost (H1 TLS → cleartext origin)

Isolates keep-alive tiny GET vs **new connection per request** (handshake-dominated) vs keep-alive **256 KiB**. Product comparison uses RPS and end-to-end latency; TWP can also capture `ClientTlsTiming` when `TWP_RPS_CAPTURE_TLS=1` (child process) — nginx/YARP have no equivalent hook.

#### Windows

Median of **3** repeats on `windows-latest` @ `8567b4c0`. Source: Actions [40610482478](https://github.com/justcoding121/titanium-web-proxy/actions/runs/40610482478). Absolute RPS on GHA swings hard; prefer **TWP÷YARP**.


*Not possible:* **HAProxy** and **Envoy** columns are omitted (no official Windows port).

| Workload | TWP | nginx | YARP |
|---|---:|---:|---:|
| Keep-alive · tiny GET | 🥇 **33,328**<br><sub>(85 MiB / 45.1% CPU · 54.2 µs CPU/req)</sub> | **15,788**<br><sub>(144 MiB / 24.8% CPU · 62.9 µs CPU/req)</sub> | **29,263**<br><sub>(103 MiB / 48.3% CPU · 66.0 µs CPU/req)</sub> |
| New-connection · tiny GET | 🥇 **901**<br><sub>(83 MiB)</sub> | **302**<br><sub>(143 MiB / 2.2% CPU · 296.9 µs CPU/req)</sub> | **839**<br><sub>(101 MiB)</sub> |
| Keep-alive · 256 KiB GET | 🥇 **3,566**<br><sub>(139 MiB / 41.4% CPU · 892 MiB/s · 464.7 µs CPU/req)</sub> | **229**<br><sub>(144 MiB / 24.8% CPU · 57 MiB/s · 4319.4 µs CPU/req)</sub> | **3,089**<br><sub>(139 MiB / 42.7% CPU · 772 MiB/s · 553.4 µs CPU/req)</sub> |

#### Linux

Median of **3** repeats @ `8567b4c0`. Source: Actions [40610482478](https://github.com/justcoding121/titanium-web-proxy/actions/runs/40610482478).


| Workload | TWP | nginx | HAProxy | Envoy | YARP |
|---|---:|---:|---:|---:|---:|
| Keep-alive · tiny GET | **56,325**<br><sub>(120 MiB / 47.5% CPU · 33.7 µs CPU/req)</sub> | 🥇 **65,779**<br><sub>(115 MiB / 41.9% CPU · 25.5 µs CPU/req)</sub> | **61,267**<br><sub>(89 MiB / 44.8% CPU · 29.2 µs CPU/req)</sub> | **47,391**<br><sub>(126 MiB / 54.3% CPU · 45.8 µs CPU/req)</sub> | **51,975**<br><sub>(148 MiB / 48.0% CPU · 36.9 µs CPU/req)</sub> |
| New-connection · tiny GET | **1,063**<br><sub>(136 MiB / 1.0% CPU · 36.9 µs CPU/req)</sub> | **1,101**<br><sub>(114 MiB / 0.5% CPU · 17.8 µs CPU/req)</sub> | **1,097**<br><sub>(90 MiB)</sub> | 🥇 **1,249**<br><sub>(130 MiB / 0.2% CPU · 8.0 µs CPU/req)</sub> | **1,064**<br><sub>(160 MiB / 0.7% CPU · 27.8 µs CPU/req)</sub> |
| Keep-alive · 256 KiB GET | **4,702**<br><sub>(130 MiB / 27.9% CPU · 1,175 MiB/s · 237.5 µs CPU/req)</sub> | **3,127**<br><sub>(110 MiB / 48.6% CPU · 782 MiB/s · 621.8 µs CPU/req)</sub> | 🥇 **4,836**<br><sub>(87 MiB / 24.9% CPU · 1,209 MiB/s · 205.7 µs CPU/req)</sub> | **4,176**<br><sub>(141 MiB / 30.9% CPU · 1,044 MiB/s · 295.9 µs CPU/req)</sub> | **3,858**<br><sub>(162 MiB / 39.7% CPU · 964 MiB/s · 411.3 µs CPU/req)</sub> |

#### macOS

Median of **3** repeats on `macos-15` @ `8567b4c0`. Source: Actions [40610482478](https://github.com/justcoding121/titanium-web-proxy/actions/runs/40610482478).

| Workload | TWP | nginx | HAProxy | Envoy | YARP |
|---|---:|---:|---:|---:|---:|
| Keep-alive · tiny GET | 🥇 **8,300**<br><sub>(148 MiB / 17.8% CPU · 64.5 µs CPU/req)</sub> | **6,121**<br><sub>(84 MiB / 8.9% CPU · 43.7 µs CPU/req)</sub> | **7,206**<br><sub>(107 MiB / 13.2% CPU · 54.8 µs CPU/req)</sub> | **5,955**<br><sub>(121 MiB / 24.1% CPU · 121.4 µs CPU/req)</sub> | **4,630**<br><sub>(201 MiB / 13.7% CPU · 89.0 µs CPU/req)</sub> |
| New-connection · tiny GET | **120**<br><sub>(peak 127 · 109 MiB / 0.1% CPU · 32.4 µs CPU/req)</sub> | **590**<br><sub>(86 MiB)</sub> | **711**<br><sub>(106 MiB)</sub> | 🥇 **711**<br><sub>(131 MiB)</sub> | **129**<br><sub>(181 MiB / 0.6% CPU · 132.7 µs CPU/req)</sub> |
| Keep-alive · 256 KiB GET | **1,744**<br><sub>(220 MiB / 22.3% CPU · 436 MiB/s · 383.8 µs CPU/req)</sub> | **1,753**<br><sub>(84 MiB / 19.4% CPU · 438 MiB/s · 331.2 µs CPU/req)</sub> | 🥇 **2,151**<br><sub>(118 MiB / 24.5% CPU · 538 MiB/s · 341.5 µs CPU/req)</sub> | **1,566**<br><sub>(154 MiB / 20.3% CPU · 392 MiB/s · 389.5 µs CPU/req)</sub> | **1,596**<br><sub>(186 MiB / 30.7% CPU · 399 MiB/s · 576.5 µs CPU/req)</sub> |

Prefer **TWP÷YARP** in the table. Absolute RPS on GHA swings hard. New-connection is Darwin SslStream-bound for TWP and YARP (handshake p99 SLO **500 ms** on macOS only — Win/Linux stay at **200 ms**).

## Unary gRPC (H2 TLS)

Unary Echo **RPC/s** @ c=64 over H2 TLS→H2 TLS for Titanium, YARP, nginx (`grpc_pass`), HAProxy, and Envoy (OS-possible peers only). Not folded into the architecture-sensitive tables. Median of **3** repeats @ `8567b4c0` — [40610489817](https://github.com/justcoding121/titanium-web-proxy/actions/runs/40610489817).

| OS | Titanium | YARP | nginx | HAProxy | Envoy |
|---|---:|---:|---:|---:|---:|
| Windows | 🥇 **41,991**<br><sub>(134 MiB / 30.5% CPU · 29.0 µs CPU/req)</sub> | **27,221**<br><sub>(122 MiB / 44.3% CPU · 65.1 µs CPU/req)</sub> | **0**<br><sub>(145 MiB / 24.7% CPU)</sub> | *Not possible* | *Not possible* |
| Linux | 🥇 **34,436**<br><sub>(178 MiB / 32.7% CPU · 38.0 µs CPU/req)</sub> | **21,952**<br><sub>(183 MiB / 43.0% CPU · 78.3 µs CPU/req)</sub> | **0**<br><sub>(122 MiB / 46.0% CPU)</sub> | **16,028**<br><sub>(88 MiB / 47.2% CPU · 117.9 µs CPU/req)</sub> | **16,825**<br><sub>(127 MiB / 56.0% CPU · 133.0 µs CPU/req)</sub> |
| macOS | **5,967**<br><sub>(280 MiB / 13.4% CPU · 67.6 µs CPU/req)</sub> | 🥇 **9,857**<br><sub>(255 MiB / 14.7% CPU · 44.6 µs CPU/req)</sub> | **0**<br><sub>(84 MiB / 3.1% CPU)</sub> | **0**<br><sub>(117 MiB / 21.7% CPU)</sub> | **5,792**<br><sub>(120 MiB / 23.1% CPU · 119.5 µs CPU/req)</sub> |

## Unary gRPC (H2 TLS → h2c)

Unary Echo **RPC/s** @ c=64 over **H2 TLS → h2c** (edge TLS, cleartext H2 origin). Peers: Titanium, YARP, HAProxy, Envoy. Mode: `compare-grpc` (`*-grpc-h2c`). Median of **3** repeats @ `8567b4c0` — [40610489817](https://github.com/justcoding121/titanium-web-proxy/actions/runs/40610489817).

*Not possible:* **nginx** column omitted (no H2 upstream).

| OS | Titanium | YARP | HAProxy | Envoy |
|---|---:|---:|---:|---:|
| Windows | 🥇 **46,216**<br><sub>(133 MiB / 30.6% CPU · 26.5 µs CPU/req)</sub> | **29,457**<br><sub>(127 MiB / 46.1% CPU · 62.6 µs CPU/req)</sub> | *Not possible* | *Not possible* |
| Linux | 🥇 **38,998**<br><sub>(173 MiB / 32.4% CPU · 33.2 µs CPU/req)</sub> | **23,750**<br><sub>(174 MiB / 43.7% CPU · 73.6 µs CPU/req)</sub> | **17,730**<br><sub>(88 MiB / 45.4% CPU · 102.3 µs CPU/req)</sub> | **17,344**<br><sub>(126 MiB / 57.0% CPU · 131.5 µs CPU/req)</sub> |
| macOS | **0**<br><sub>(273 MiB / 8.5% CPU)</sub> | **0**<br><sub>(254 MiB / 14.6% CPU)</sub> | 🥇 **14,681**<br><sub>(123 MiB / 24.7% CPU · 50.4 µs CPU/req)</sub> | **11,087**<br><sub>(121 MiB / 20.8% CPU · 56.2 µs CPU/req)</sub> |

## WebSocket (H1 TLS → H1 TLS)

WebSocket echo round-trips/sec over **dual-TLS** H1 (`proxy_ssl` style). Mode: `compare-ws-h1tls` (`*-duplex-ws-h1tls`). Median of **3** repeats @ `8567b4c0`. Source: [40610493463](https://github.com/justcoding121/titanium-web-proxy/actions/runs/40610493463).

| OS | Titanium | YARP | nginx | HAProxy | Envoy |
|---|---:|---:|---:|---:|---:|
| Windows | 🥇 **22,772**<br><sub>(102 MiB / 44.9% CPU · 78.8 µs CPU/req)</sub> | **21,700**<br><sub>(94 MiB / 43.6% CPU · 80.4 µs CPU/req)</sub> | **9,443**<br><sub>(144 MiB / 24.8% CPU · 105.0 µs CPU/req)</sub> | *Not possible* | *Not possible* |
| Linux | **27,419**<br><sub>(153 MiB / 42.8% CPU · 62.4 µs CPU/req)</sub> | **25,634**<br><sub>(154 MiB / 43.9% CPU · 68.5 µs CPU/req)</sub> | 🥇 **31,173**<br><sub>(118 MiB / 37.5% CPU · 48.1 µs CPU/req)</sub> | **29,222**<br><sub>(90 MiB / 41.0% CPU · 56.2 µs CPU/req)</sub> | **30,098**<br><sub>(127 MiB / 39.7% CPU · 52.7 µs CPU/req)</sub> |
| macOS | **4,700**<br><sub>(131 MiB / 14.1% CPU · 90.1 µs CPU/req)</sub> | **4,652**<br><sub>(149 MiB / 9.9% CPU · 64.2 µs CPU/req)</sub> | 🥇 **12,092**<br><sub>(94 MiB / 13.7% CPU · 33.9 µs CPU/req)</sub> | **11,456**<br><sub>(106 MiB / 17.2% CPU · 45.0 µs CPU/req)</sub> | **6,470**<br><sub>(118 MiB / 16.7% CPU · 77.5 µs CPU/req)</sub> |

## WebSocket (H2 TLS 8441 → H1)

WebSocket echo over **RFC 8441** extended CONNECT (H2 TLS client → H1 plain origin). Mode: `compare-ws-h2` (`*-duplex-ws-h2`).

The probe client sets `NoDelay` and writes each HTTP/2 frame as one TLS record. Median of **3** repeats @ `8567b4c0`. Source: [40610497264](https://github.com/justcoding121/titanium-web-proxy/actions/runs/40610497264).

*Not possible:* **nginx** column omitted (no RFC 8441 extended CONNECT).

| OS | Titanium | YARP | HAProxy | Envoy |
|---|---:|---:|---:|---:|
| Windows | 🥇 **25,912**<br><sub>(171 MiB / 44.9% CPU · 69.4 µs CPU/req)</sub> | **0**<br><sub>(151 MiB / 19.6% CPU)</sub> | *Not possible* | *Not possible* |
| Linux | 🥇 **34,018**<br><sub>(211 MiB / 49.2% CPU · 57.9 µs CPU/req)</sub> | **0**<br><sub>(200 MiB / 54.6% CPU)</sub> | **29,910**<br><sub>(89 MiB / 42.4% CPU · 56.6 µs CPU/req)</sub> | **0**<br><sub>(124 MiB / 0.4% CPU)</sub> |
| macOS | **17,693**<br><sub>(310 MiB / 36.7% CPU · 62.2 µs CPU/req)</sub> | **0**<br><sub>(176 MiB / 11.3% CPU)</sub> | 🥇 **22,134**<br><sub>(112 MiB / 27.7% CPU · 37.5 µs CPU/req)</sub> | **0**<br><sub>(116 MiB / 1.0% CPU)</sub> |

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

`--arm-shard` takes a comparison-group key (`h1c-h1c`, from `--print-groups`), not an `i/n` index, so a re-run measures the same row after a peer install flake. `i/n` remains for smoke modes. Paste unions `rps-csv-<os>-shard-*` (`paste-compare-product-wiki.ps1 -RunIds …`). Table “Source: Actions” may list several run URLs for one table — **do not mix SHAs**. A single-row leg allows **60** minutes of ramp and **75** minutes overall. Free GitHub accounts run **20** jobs at once, **5** of them macOS. Give a full suite a quiet window: do not push pull requests or start other macOS jobs while it runs. Re-run infrastructure failures only (`gh run rerun --failed`). A failed gate is a finding and is never re-rolled; publish the last attempt, never the best of several. The RPS tables on this page are @ `8567b4c0`: product [40610469359](https://github.com/justcoding121/titanium-web-proxy/actions/runs/40610469359), saturation [40610501155](https://github.com/justcoding121/titanium-web-proxy/actions/runs/40610501155), bodies [40610472761](https://github.com/justcoding121/titanium-web-proxy/actions/runs/40610472761), POST [40610476097](https://github.com/justcoding121/titanium-web-proxy/actions/runs/40610476097), lossy [40610479308](https://github.com/justcoding121/titanium-web-proxy/actions/runs/40610479308), arch [40610485919](https://github.com/justcoding121/titanium-web-proxy/actions/runs/40610485919), TLS cost [40610482478](https://github.com/justcoding121/titanium-web-proxy/actions/runs/40610482478), gRPC [40610489817](https://github.com/justcoding121/titanium-web-proxy/actions/runs/40610489817), WebSocket H1 TLS [40610493463](https://github.com/justcoding121/titanium-web-proxy/actions/runs/40610493463), WebSocket H2 [40610497264](https://github.com/justcoding121/titanium-web-proxy/actions/runs/40610497264), editions [40610503982](https://github.com/justcoding121/titanium-web-proxy/actions/runs/40610503982). The bodies aggregate failed one gate: Windows H3→H2 TLS 256 KiB was **0.389×** the closest peer (floor **0.40**); that cell is the measured attempt. The previous refresh @ `88187363` had suite aggregates for product, arch, and lossy that failed post-ramp gates on a cluster of legs: Linux lossy H2 TLS→H2 TLS was about **0.35×** three peers that agreed with each other; Windows H3→H2 TLS slow-consumer was **0.27×** YARP; macOS had four reverse wires at or under **0.50×** YARP (H2 TLS→h2c **0.36**, H3→h2c **0.37**, H2 TLS→H3 **0.47**, H1 plain→H3 **0.50**), and on H3→h2c the peers rose while Titanium fell versus `347a9a1b`. Published cells stay that last attempt. Gate floors are unchanged. A same-VM Linux A/B of `347a9a1b` against `88187363` (5 pairs, c=64) found no CI-significant Titanium change: lossy H2 TLS −15% (CI −39% to +9%) [38582510672](https://github.com/justcoding121/titanium-web-proxy/actions/runs/38582510672), H3→h2c −1% [38582542878](https://github.com/justcoding121/titanium-web-proxy/actions/runs/38582542878), H2 TLS→h2c +3% [38582555901](https://github.com/justcoding121/titanium-web-proxy/actions/runs/38582555901), H3 slow-consumer 0% (CI ±5%) [38582567082](https://github.com/justcoding121/titanium-web-proxy/actions/runs/38582567082). Same-job re-measures cleared the 0.40 floor on both SHAs: macOS H3→h2c **1.83× / 1.12×** YARP ([38582888190](https://github.com/justcoding121/titanium-web-proxy/actions/runs/38582888190), [38582898066](https://github.com/justcoding121/titanium-web-proxy/actions/runs/38582898066)); macOS H2 TLS→h2c **3.24× / 4.51×** ([38582907076](https://github.com/justcoding121/titanium-web-proxy/actions/runs/38582907076), [38582928356](https://github.com/justcoding121/titanium-web-proxy/actions/runs/38582928356)); Windows slow-consumer **0.98× / 0.80×** ([38582936158](https://github.com/justcoding121/titanium-web-proxy/actions/runs/38582936158), [38582945932](https://github.com/justcoding121/titanium-web-proxy/actions/runs/38582945932)). The full-suite cells stay the last attempt. Reverse product rows, bodies, POST, lossy, arch, TLS cost, gRPC, both WebSocket modes, and saturation run on Windows, Linux, and macOS. TWP MITM arms run on Linux only. `compare-editions` (CLI and Plus) runs on Linux only. `compare-spot` on pull requests to beta and stable stays on all three OS. The historical v2 MITM note below the product tables stays @ `41f4adee` because those run ids are from that fix, not this re-measure.

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
