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
- **Typical reverse** is H1 TLS→H1 or H2→H1 (~1.1× YARP on Win/Linux tiny GET). With **larger bodies**, see [Heavier reverse](#user-content-heavier-reverse-workloads) (@ `482f5984`).

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

On the Linux H2 TLS→H1 plain row at c=64 (16 connections, @ `482f5984`), proxy CPU was about **44%** for nginx, **46%** for HAProxy, and **63%** for Envoy. Windows nginx on the same H2 client shape stayed near **25%** (one core). That measured shape is the one published here.

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

Calibration for the shared 4 vCPU loopback shape: how close client + origin are to saturated before ranking reverse peers. Tiny keep-alive GET. Median of **3** repeats @ `482f5984` — [37210519205](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37210519205). Warmup 2s / measure 8s; concurrency 8, 16, 32, 64. Block A **% of origin-HttpClient** uses median **peak** RPS. Blocks B/C use peer÷YARP / ÷nginx on median peak (not % of H1 origin). **RPS cells** embed median RSS / CPU for the **proxy child** plus its **full descendant tree** (serve-proxy → nginx master → workers); origin-direct samples the **origin** child. Product matrices below use matched `dotnet-httpclient` only (not bombardier).


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
| origin-direct | dotnet-httpclient | **51,094**<br><sub>(55 MiB / 40.5% CPU · 31.7 µs CPU/req)</sub> | **100.0%** |
| origin-direct-bombardier | bombardier | **38,568**<br><sub>(56 MiB / 21.2% CPU · 21.9 µs CPU/req)</sub> | **75.5%** |
| bare-reverse-http1 | dotnet-httpclient | **25,550**<br><sub>(55 MiB / 43.1% CPU · 67.6 µs CPU/req)</sub> | **50.0%** |
| nginx-reverse-http1 | dotnet-httpclient | **13,356**<br><sub>(126 MiB / 24.8% CPU · 74.2 µs CPU/req)</sub> | **26.1%** |
| yarp-reverse-http1 | dotnet-httpclient | **21,546**<br><sub>(88 MiB / 49.4% CPU · 91.7 µs CPU/req)</sub> | **42.2%** |
| twp-reverse-http1 | dotnet-httpclient | 🥇 **25,433**<br><sub>(72 MiB / 47.2% CPU · 74.3 µs CPU/req)</sub> | **49.8%** |

**Linux** (`ubuntu-latest`)

| Arm | Generator | RPS | % of origin-HttpClient |
|---|---|---:|---:|
| origin-direct | dotnet-httpclient | **70,838**<br><sub>(80 MiB / 42.9% CPU · 24.2 µs CPU/req)</sub> | **100.0%** |
| origin-direct-bombardier | bombardier | **41,999**<br><sub>(80 MiB / 34.9% CPU · 33.2 µs CPU/req)</sub> | **59.3%** |
| bare-reverse-http1 | dotnet-httpclient | **32,377**<br><sub>(67 MiB / 44.4% CPU · 54.8 µs CPU/req)</sub> | **45.7%** |
| nginx-reverse-http1 | dotnet-httpclient | 🥇 **39,318**<br><sub>(76 MiB / 41.3% CPU · 42.0 µs CPU/req)</sub> | **55.5%** |
| yarp-reverse-http1 | dotnet-httpclient | **27,756**<br><sub>(115 MiB / 50.0% CPU · 72.1 µs CPU/req)</sub> | **39.2%** |
| twp-reverse-http1 | dotnet-httpclient | **31,478**<br><sub>(83 MiB / 49.5% CPU · 62.9 µs CPU/req)</sub> | **44.4%** |


**macOS** (`macos-15`, Apple Silicon)

| Arm | Generator | RPS | % of origin-HttpClient |
|---|---|---:|---:|
| origin-direct | dotnet-httpclient | **85,921**<br><sub>(90 MiB / 34.0% CPU · 15.8 µs CPU/req)</sub> | **100.0%** |
| origin-direct-bombardier | bombardier | **63,764**<br><sub>(89 MiB / 24.6% CPU · 15.4 µs CPU/req)</sub> | **74.2%** |
| bare-reverse-http1 | dotnet-httpclient | **33,705**<br><sub>(92 MiB / 32.5% CPU · 38.6 µs CPU/req)</sub> | **39.2%** |
| nginx-reverse-http1 | dotnet-httpclient | **26,475**<br><sub>(68 MiB / 21.2% CPU · 32.1 µs CPU/req)</sub> | **30.8%** |
| yarp-reverse-http1 | dotnet-httpclient | 🥇 **29,622**<br><sub>(147 MiB / 37.5% CPU · 50.6 µs CPU/req)</sub> | **34.5%** |
| twp-reverse-http1 | dotnet-httpclient | **23,988**<br><sub>(117 MiB / 33.2% CPU · 55.4 µs CPU/req)</sub> | **27.9%** |

On this macOS run `origin-direct` was **34.0%** CPU, so the % of origin-HttpClient column is not a ceiling when that CPU is near idle. Rank those Mac peers by RPS.

Reverse peers on Block A @ `482f5984`: Windows TWP is **49.8%** of origin-direct and Linux TWP is **44.4%**. Prefer the **%** column over absolute RPS across runs. Bare and origin-direct are controls (not medal peers).

#### Block B — H2 TLS→H1

Peer ratios (÷YARP / ÷nginx) on median peak; **RPS cells** embed `(MiB / CPU%)`.

**Windows** (`windows-latest`)

| Arm | Generator | RPS | ÷YARP | ÷nginx |
|---|---|---:|---:|---:|
| nginx-reverse-http2 | dotnet-httpclient | **7,670**<br><sub>(143 MiB / 24.8% CPU · 129.2 µs CPU/req)</sub> | **0.28×** | **1.00×** |
| yarp-reverse-http2 | dotnet-httpclient | **27,825**<br><sub>(105 MiB / 54.6% CPU · 78.4 µs CPU/req)</sub> | **1.00×** | **3.63×** |
| twp-reverse-http2-cleartext | dotnet-httpclient | 🥇 **30,416**<br><sub>(135 MiB / 50.7% CPU · 66.6 µs CPU/req)</sub> | **1.09×** | **3.97×** |

**Linux** (`ubuntu-latest`)

| Arm | Generator | RPS | ÷YARP | ÷nginx |
|---|---|---:|---:|---:|
| nginx-reverse-http2 | dotnet-httpclient | **24,784**<br><sub>(100 MiB / 44.0% CPU · 71.0 µs CPU/req)</sub> | **0.89×** | **1.00×** |
| yarp-reverse-http2 | dotnet-httpclient | **27,758**<br><sub>(133 MiB / 50.4% CPU · 72.7 µs CPU/req)</sub> | **1.00×** | **1.12×** |
| twp-reverse-http2-cleartext | dotnet-httpclient | 🥇 **31,314**<br><sub>(146 MiB / 52.2% CPU · 66.7 µs CPU/req)</sub> | **1.13×** | **1.26×** |

**macOS** (`macos-15`, Apple Silicon)

| Arm | Generator | RPS | ÷YARP | ÷nginx |
|---|---|---:|---:|---:|
| nginx-reverse-http2 | dotnet-httpclient | 🥇 **33,612**<br><sub>(83 MiB / 25.6% CPU · 30.5 µs CPU/req)</sub> | **1.08×** | **1.00×** |
| yarp-reverse-http2 | dotnet-httpclient | **31,109**<br><sub>(157 MiB / 41.3% CPU · 53.1 µs CPU/req)</sub> | **1.00×** | **0.93×** |
| twp-reverse-http2-cleartext | dotnet-httpclient | **33,241**<br><sub>(171 MiB / 39.8% CPU · 47.9 µs CPU/req)</sub> | **1.07×** | **0.99×** |
#### Block C — H3→H1

Same layout as Block B. Requires QuicListener. nginx needs `http_v3_module` (Windows nginx has no QUIC). HAProxy needs `USE_QUIC` (GHA Linux/macOS require it). Envoy 1.20+ includes HTTP/3.

Medals are nginx / YARP / Titanium only, from the same run as Blocks A and B ([37156912437](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37156912437)). On the Linux run HAProxy was **23,420** (87 MiB / 26.7% CPU) and Envoy **3,838** (136 MiB / 24.5% CPU). On the macOS run HAProxy was **33,360** (101 MiB / 27.7% CPU) and Envoy **6,764** (129 MiB / 38.3% CPU).

**Windows** (`windows-latest`)

| Arm | Generator | RPS | ÷YARP | ÷nginx |
|---|---|---:|---:|---:|
| nginx-reverse-http3-cleartext | dotnet-httpclient | *Not possible (no QUIC)* | — | — |
| yarp-reverse-http3-cleartext | dotnet-httpclient | **8,351**<br><sub>(154 MiB / 52.2% CPU · 249.9 µs CPU/req)</sub> | **1.00×** | — |
| twp-reverse-http3-cleartext | dotnet-httpclient | 🥇 **9,367**<br><sub>(110 MiB / 47.6% CPU · 203.1 µs CPU/req)</sub> | **1.12×** | — |

**Linux** (`ubuntu-latest`)

| Arm | Generator | RPS | ÷YARP | ÷nginx |
|---|---|---:|---:|---:|
| nginx-reverse-http3-cleartext | dotnet-httpclient | 🥇 **20,832**<br><sub>(104 MiB / 32.3% CPU · 61.9 µs CPU/req)</sub> | **1.40×** | **1.00×** |
| yarp-reverse-http3-cleartext | dotnet-httpclient | **14,884**<br><sub>(194 MiB / 50.8% CPU · 136.5 µs CPU/req)</sub> | **1.00×** | **0.71×** |
| twp-reverse-http3-cleartext | dotnet-httpclient | **18,094**<br><sub>(148 MiB / 52.8% CPU · 116.7 µs CPU/req)</sub> | **1.22×** | **0.87×** |

**macOS** (`macos-15`, Apple Silicon)

| Arm | Generator | RPS | ÷YARP | ÷nginx |
|---|---|---:|---:|---:|
| nginx-reverse-http3-cleartext | dotnet-httpclient | 🥇 **23,761**<br><sub>(84 MiB / 21.6% CPU · 36.4 µs CPU/req)</sub> | **1.13×** | **1.00×** |
| yarp-reverse-http3-cleartext | dotnet-httpclient | **17,661**<br><sub>(213 MiB / 43.4% CPU · 98.2 µs CPU/req)</sub> | **1.00×** | **0.88×** |
| twp-reverse-http3-cleartext | dotnet-httpclient | **15,433**<br><sub>(139 MiB / 43.6% CPU · 113.1 µs CPU/req)</sub> | **0.87×** | **0.77×** |

## Windows — Titanium vs nginx vs HAProxy vs Envoy vs YARP

Client / origin: HTTP version and whether TLS is used (`plain` = cleartext, `TLS` = encrypted, `QUIC` = HTTP/3).

### Reverse

Median of **3 repeats** on `windows-latest` (4 vCPU / 16 GiB). Bare reverse 5×5 @ `482f5984` — `compare-product` [37210516555](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37210516555). Warmup 2s / measure 8s; concurrency 8, 16, 32, 64. Prefer TWP÷peer ratios over absolute RPS. **RPS cells** include median RSS / CPU at the peak-RPS step as `<br><sub>(MiB / CPU%)</sub>`. nginx terminate peers use `keepalive 256` + streaming buffers. **HAProxy / Envoy are Linux-only peers** (no official Windows port). Laptop High-perf / cool-paired numbers stay on the [local lab](Performance-Local-Lab). Product 5×5 is **~56-byte JSON keep-alive GET**; H2/H3 same-protocol cells are mostly header work with a tiny body (Titanium best case) — see [Why this comparison is fair](#user-content-why-this-comparison-is-fair).

**Load generators:** Reverse inbound H3 arms use **`dotnet-httpclient`** (`http_version=3.0`, `RequestVersionExact`). nginx/Windows is same-OS only (no QUIC). HAProxy/Envoy are Linux-only terminate peers.

| Client | Origin | TWP | nginx | YARP |
|---|---|---:|---:|---:|
| HTTP/1 · plain | HTTP/1 · plain | 🥇 **41,064**<br><sub>(73 MiB / 49.7% CPU · 48.4 µs CPU/req)</sub> | **25,199**<br><sub>(126 MiB / 24.9% CPU · 39.5 µs CPU/req)</sub> | **34,970**<br><sub>(90 MiB / 50.1% CPU · 57.3 µs CPU/req)</sub> |
| HTTP/1 · plain | HTTP/1 · TLS | 🥇 **21,793**<br><sub>(88 MiB / 52.7% CPU · 96.8 µs CPU/req)</sub> | **8,735**<br><sub>(136 MiB / 24.9% CPU · 113.9 µs CPU/req)</sub> | **19,712**<br><sub>(100 MiB / 51.6% CPU · 104.8 µs CPU/req)</sub> |
| HTTP/1 · plain | HTTP/2 · plain | 🥇 **100,939**<br><sub>(113 MiB / 46.5% CPU · 18.4 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **96,552**<br><sub>(103 MiB / 51.5% CPU · 21.3 µs CPU/req)</sub> |
| HTTP/1 · plain | HTTP/2 · TLS | 🥇 **32,512**<br><sub>(118 MiB / 45.7% CPU · 56.2 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **30,037**<br><sub>(104 MiB / 47.3% CPU · 63.0 µs CPU/req)</sub> |
| HTTP/1 · plain | HTTP/3 · QUIC | 🥇 **18,652**<br><sub>(105 MiB / 49.8% CPU · 106.8 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | **18,139**<br><sub>(118 MiB / 50.6% CPU · 111.6 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/1 · plain | 🥇 **19,964**<br><sub>(84 MiB / 51.6% CPU · 103.3 µs CPU/req)</sub> | **8,536**<br><sub>(142 MiB / 24.7% CPU · 115.7 µs CPU/req)</sub> | **17,826**<br><sub>(101 MiB / 50.8% CPU · 114.0 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/1 · TLS | 🥇 **18,995**<br><sub>(85 MiB / 47.5% CPU · 100.0 µs CPU/req)</sub> | **6,769**<br><sub>(144 MiB / 24.8% CPU · 146.2 µs CPU/req)</sub> | **16,718**<br><sub>(105 MiB / 47.6% CPU · 113.9 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/2 · plain | 🥇 **78,921**<br><sub>(132 MiB / 46.6% CPU · 23.6 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **73,677**<br><sub>(110 MiB / 48.6% CPU · 26.4 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/2 · TLS | 🥇 **25,246**<br><sub>(118 MiB / 43.4% CPU · 68.7 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **24,295**<br><sub>(108 MiB / 45.7% CPU · 75.3 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/3 · QUIC | 🥇 **16,278**<br><sub>(109 MiB / 50.2% CPU · 123.3 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | **15,554**<br><sub>(124 MiB / 47.8% CPU · 122.9 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | 🥇 **33,712**<br><sub>(120 MiB / 51.6% CPU · 61.3 µs CPU/req)</sub> | **9,207**<br><sub>(127 MiB / 24.7% CPU · 107.4 µs CPU/req)</sub> | **30,048**<br><sub>(92 MiB / 53.3% CPU · 70.9 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/1 · TLS | 🥇 **29,159**<br><sub>(121 MiB / 52.8% CPU · 72.4 µs CPU/req)</sub> | **6,709**<br><sub>(138 MiB / 24.8% CPU · 147.6 µs CPU/req)</sub> | **26,307**<br><sub>(104 MiB / 53.5% CPU · 81.3 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/2 · plain | 🥇 **101,074**<br><sub>(101 MiB / 34.1% CPU · 13.5 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **73,374**<br><sub>(109 MiB / 54.1% CPU · 29.5 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/2 · TLS | 🥇 **85,259**<br><sub>(117 MiB / 37.5% CPU · 17.6 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **64,463**<br><sub>(105 MiB / 49.6% CPU · 30.7 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/3 · QUIC | 🥇 **26,252**<br><sub>(150 MiB / 49.6% CPU · 75.6 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | **24,448**<br><sub>(124 MiB / 53.8% CPU · 88.1 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **30,535**<br><sub>(116 MiB / 52.3% CPU · 68.5 µs CPU/req)</sub> | **7,738**<br><sub>(142 MiB / 24.8% CPU · 128.2 µs CPU/req)</sub> | **27,544**<br><sub>(105 MiB / 54.7% CPU · 79.4 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/1 · TLS | 🥇 **27,044**<br><sub>(114 MiB / 51.6% CPU · 76.3 µs CPU/req)</sub> | **6,205**<br><sub>(144 MiB / 24.8% CPU · 160.1 µs CPU/req)</sub> | **25,109**<br><sub>(106 MiB / 53% CPU · 84.5 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | 🥇 **67,582**<br><sub>(127 MiB / 37.1% CPU · 21.9 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **50,109**<br><sub>(109 MiB / 50.1% CPU · 40.0 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **54,470**<br><sub>(125 MiB / 38.1% CPU · 28.0 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **40,097**<br><sub>(110 MiB / 45.8% CPU · 45.7 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/3 · QUIC | 🥇 **24,806**<br><sub>(158 MiB / 50.1% CPU · 80.8 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | **22,946**<br><sub>(127 MiB / 50.7% CPU · 88.4 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **9,045**<br><sub>(115 MiB / 47.6% CPU · 210.6 µs CPU/req)</sub> | *Not possible (no QUIC)* | **7,808**<br><sub>(154 MiB / 50% CPU · 256.1 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **8,169**<br><sub>(114 MiB / 48.9% CPU · 239.2 µs CPU/req)</sub> | *Not possible (no QUIC)* | **7,295**<br><sub>(162 MiB / 51.2% CPU · 281.0 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/2 · plain | 🥇 **11,590**<br><sub>(114 MiB / 48.5% CPU · 167.5 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **9,980**<br><sub>(162 MiB / 50.6% CPU · 202.9 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | 🥇 **12,365**<br><sub>(112 MiB / 49.5% CPU · 160.2 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **9,597**<br><sub>(161 MiB / 48.7% CPU · 203.0 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/3 · QUIC | 🥇 **12,979**<br><sub>(118 MiB / 44.5% CPU · 137.1 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | **11,229**<br><sub>(167 MiB / 49.6% CPU · 176.8 µs CPU/req)</sub> |

## Linux — Titanium vs nginx vs HAProxy vs Envoy vs YARP

### Reverse

Median of **3 repeats** on `ubuntu-latest` (4 vCPU / 16 GiB). Bare reverse 5×5 @ `482f5984` — `compare-product` [37210516555](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37210516555). Warmup 2s / measure 8s; concurrency 8, 16, 32, 64. **Linux nginx is the authoritative nginx baseline.** HAProxy (3.2 `USE_QUIC`) and Envoy (GitHub release, HTTP/3 compiled in) run on the same loopback shape as nginx/YARP. nginx terminate peers use `keepalive 256` + streaming buffers. The RPS workflow installs nginx.org mainline (`http_v3_module`), a QUIC-enabled HAProxy, Envoy, and `libmsquic`. Prefer ratios over absolute RPS. Product 5×5 is **~56-byte JSON keep-alive GET**; H2/H3 same-protocol cells are mostly header work with a tiny body (Titanium best case) — see [Why this comparison is fair](#user-content-why-this-comparison-is-fair).

| Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---:|---:|---:|---:|---:|
| HTTP/1 · plain | HTTP/1 · plain | **31,368**<br><sub>(84 MiB / 49.9% CPU · 63.6 µs CPU/req)</sub> | 🥇 **39,240**<br><sub>(76 MiB / 39.6% CPU · 40.4 µs CPU/req)</sub> | **33,736**<br><sub>(64 MiB / 43.1% CPU · 51.1 µs CPU/req)</sub> | **22,017**<br><sub>(115 MiB / 58.4% CPU · 106.2 µs CPU/req)</sub> | **28,120**<br><sub>(115 MiB / 49.1% CPU · 69.9 µs CPU/req)</sub> |
| HTTP/1 · plain | HTTP/1 · TLS | **44,998**<br><sub>(104 MiB / 45.4% CPU · 40.4 µs CPU/req)</sub> | 🥇 **60,927**<br><sub>(93 MiB / 39.5% CPU · 25.9 µs CPU/req)</sub> | **54,443**<br><sub>(71 MiB / 42.7% CPU · 31.4 µs CPU/req)</sub> | **36,352**<br><sub>(118 MiB / 56% CPU · 61.6 µs CPU/req)</sub> | **41,324**<br><sub>(127 MiB / 47.8% CPU · 46.2 µs CPU/req)</sub> |
| HTTP/1 · plain | HTTP/2 · plain | 🥇 **60,291**<br><sub>(135 MiB / 50.9% CPU · 33.8 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **36,999**<br><sub>(65 MiB / 42.1% CPU · 45.5 µs CPU/req)</sub> | **35,073**<br><sub>(116 MiB / 60% CPU · 68.5 µs CPU/req)</sub> | **52,826**<br><sub>(128 MiB / 48.8% CPU · 36.9 µs CPU/req)</sub> |
| HTTP/1 · plain | HTTP/2 · TLS | 🥇 **60,446**<br><sub>(164 MiB / 48.4% CPU · 32.0 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **54,142**<br><sub>(68 MiB / 44.3% CPU · 32.7 µs CPU/req)</sub> | **42,351**<br><sub>(117 MiB / 57.8% CPU · 54.6 µs CPU/req)</sub> | **54,565**<br><sub>(129 MiB / 48% CPU · 35.2 µs CPU/req)</sub> |
| HTTP/1 · plain | HTTP/3 · QUIC | 🥇 **34,726**<br><sub>(153 MiB / 54.3% CPU · 62.5 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **15,311**<br><sub>(121 MiB / 55.7% CPU · 145.4 µs CPU/req)</sub> | **30,783**<br><sub>(162 MiB / 48.7% CPU · 63.3 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/1 · plain | **40,461**<br><sub>(108 MiB / 48% CPU · 47.4 µs CPU/req)</sub> | 🥇 **51,258**<br><sub>(100 MiB / 39.6% CPU · 30.9 µs CPU/req)</sub> | **48,775**<br><sub>(83 MiB / 43.7% CPU · 35.9 µs CPU/req)</sub> | **32,377**<br><sub>(126 MiB / 57.2% CPU · 70.6 µs CPU/req)</sub> | **35,042**<br><sub>(133 MiB / 49.7% CPU · 56.8 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/1 · TLS | **19,677**<br><sub>(109 MiB / 48.6% CPU · 98.8 µs CPU/req)</sub> | 🥇 **22,947**<br><sub>(102 MiB / 41.6% CPU · 72.5 µs CPU/req)</sub> | **21,710**<br><sub>(84 MiB / 43.9% CPU · 80.9 µs CPU/req)</sub> | **15,563**<br><sub>(128 MiB / 56% CPU · 144.0 µs CPU/req)</sub> | **17,121**<br><sub>(134 MiB / 50.2% CPU · 117.4 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/2 · plain | 🥇 **35,461**<br><sub>(137 MiB / 50.3% CPU · 56.7 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **24,128**<br><sub>(82 MiB / 43.3% CPU · 71.8 µs CPU/req)</sub> | **23,550**<br><sub>(125 MiB / 58.6% CPU · 99.5 µs CPU/req)</sub> | **30,984**<br><sub>(139 MiB / 50% CPU · 64.6 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/2 · TLS | 🥇 **24,465**<br><sub>(151 MiB / 48% CPU · 78.5 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **22,261**<br><sub>(84 MiB / 44.6% CPU · 80.2 µs CPU/req)</sub> | **17,405**<br><sub>(125 MiB / 57.7% CPU · 132.7 µs CPU/req)</sub> | **22,072**<br><sub>(147 MiB / 47.4% CPU · 86.0 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/3 · QUIC | 🥇 **18,149**<br><sub>(146 MiB / 52.4% CPU · 115.5 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **9,550**<br><sub>(130 MiB / 54.6% CPU · 228.8 µs CPU/req)</sub> | **15,733**<br><sub>(166 MiB / 49.3% CPU · 125.2 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | 🥇 **36,632**<br><sub>(147 MiB / 52.6% CPU · 57.4 µs CPU/req)</sub> | **28,526**<br><sub>(78 MiB / 44.9% CPU · 63.0 µs CPU/req)</sub> | **34,975**<br><sub>(64 MiB / 45.4% CPU · 52.0 µs CPU/req)</sub> | **20,275**<br><sub>(116 MiB / 64.2% CPU · 126.6 µs CPU/req)</sub> | **32,231**<br><sub>(125 MiB / 50.7% CPU · 62.9 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/1 · TLS | 🥇 **27,860**<br><sub>(143 MiB / 50.8% CPU · 73.0 µs CPU/req)</sub> | **22,457**<br><sub>(95 MiB / 45.5% CPU · 81.1 µs CPU/req)</sub> | **26,956**<br><sub>(70 MiB / 45.1% CPU · 66.9 µs CPU/req)</sub> | **17,954**<br><sub>(119 MiB / 60.2% CPU · 134.2 µs CPU/req)</sub> | **24,772**<br><sub>(132 MiB / 51.7% CPU · 83.4 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/2 · plain | 🥇 **68,356**<br><sub>(148 MiB / 40.2% CPU · 23.5 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **35,625**<br><sub>(66 MiB / 46.5% CPU · 52.2 µs CPU/req)</sub> | **25,150**<br><sub>(116 MiB / 62.2% CPU · 98.9 µs CPU/req)</sub> | **44,979**<br><sub>(131 MiB / 48.9% CPU · 43.5 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/2 · TLS | 🥇 **53,290**<br><sub>(149 MiB / 40.9% CPU · 30.7 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **28,317**<br><sub>(67 MiB / 46.3% CPU · 65.4 µs CPU/req)</sub> | **23,506**<br><sub>(117 MiB / 60.6% CPU · 103.2 µs CPU/req)</sub> | **36,595**<br><sub>(132 MiB / 47.6% CPU · 52.0 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/3 · QUIC | 🥇 **34,025**<br><sub>(187 MiB / 49% CPU · 57.6 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **21,067**<br><sub>(119 MiB / 52.3% CPU · 99.3 µs CPU/req)</sub> | **31,150**<br><sub>(162 MiB / 47.2% CPU · 60.6 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **32,176**<br><sub>(146 MiB / 52.6% CPU · 65.4 µs CPU/req)</sub> | **24,820**<br><sub>(100 MiB / 43.8% CPU · 70.5 µs CPU/req)</sub> | **28,818**<br><sub>(82 MiB / 45.7% CPU · 63.4 µs CPU/req)</sub> | **18,805**<br><sub>(128 MiB / 62.6% CPU · 133.2 µs CPU/req)</sub> | **28,031**<br><sub>(131 MiB / 50.7% CPU · 72.3 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/1 · TLS | 🥇 **37,010**<br><sub>(153 MiB / 48.5% CPU · 52.4 µs CPU/req)</sub> | **32,273**<br><sub>(107 MiB / 41.6% CPU · 51.5 µs CPU/req)</sub> | **35,974**<br><sub>(83 MiB / 43.7% CPU · 48.6 µs CPU/req)</sub> | **27,671**<br><sub>(127 MiB / 54.8% CPU · 79.3 µs CPU/req)</sub> | **33,660**<br><sub>(135 MiB / 48.4% CPU · 57.5 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | 🥇 **57,068**<br><sub>(159 MiB / 41.7% CPU · 29.2 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **27,957**<br><sub>(81 MiB / 46% CPU · 65.8 µs CPU/req)</sub> | **23,395**<br><sub>(125 MiB / 60.6% CPU · 103.6 µs CPU/req)</sub> | **37,460**<br><sub>(133 MiB / 49% CPU · 52.3 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **47,294**<br><sub>(168 MiB / 41.3% CPU · 34.9 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **26,102**<br><sub>(81 MiB / 45.3% CPU · 69.5 µs CPU/req)</sub> | **21,911**<br><sub>(125 MiB / 59.8% CPU · 109.1 µs CPU/req)</sub> | **31,896**<br><sub>(140 MiB / 47.4% CPU · 59.5 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/3 · QUIC | 🥇 **26,262**<br><sub>(193 MiB / 49.7% CPU · 75.7 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **13,400**<br><sub>(128 MiB / 55.5% CPU · 165.8 µs CPU/req)</sub> | **23,056**<br><sub>(168 MiB / 47.8% CPU · 82.9 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | **27,227**<br><sub>(157 MiB / 53.2% CPU · 78.2 µs CPU/req)</sub> | **30,960**<br><sub>(107 MiB / 31.4% CPU · 40.6 µs CPU/req)</sub> | 🥇 **33,254**<br><sub>(88 MiB / 35.8% CPU · 43.1 µs CPU/req)</sub> | **13,361**<br><sub>(131 MiB / 54% CPU · 161.6 µs CPU/req)</sub> | **21,286**<br><sub>(201 MiB / 50.1% CPU · 94.1 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | **14,890**<br><sub>(161 MiB / 53.2% CPU · 142.9 µs CPU/req)</sub> | **16,983**<br><sub>(110 MiB / 33.7% CPU · 79.5 µs CPU/req)</sub> | 🥇 **18,883**<br><sub>(89 MiB / 36.4% CPU · 77.0 µs CPU/req)</sub> | **7,950**<br><sub>(132 MiB / 55.7% CPU · 280.2 µs CPU/req)</sub> | **11,768**<br><sub>(208 MiB / 51.9% CPU · 176.5 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/2 · plain | 🥇 **47,599**<br><sub>(185 MiB / 55% CPU · 46.2 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **46,086**<br><sub>(88 MiB / 31.7% CPU · 27.5 µs CPU/req)</sub> | **28,244**<br><sub>(132 MiB / 48% CPU · 67.9 µs CPU/req)</sub> | **40,712**<br><sub>(218 MiB / 48.2% CPU · 47.4 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | **19,990**<br><sub>(157 MiB / 52.2% CPU · 104.4 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | 🥇 **20,129**<br><sub>(88 MiB / 36% CPU · 71.6 µs CPU/req)</sub> | **9,707**<br><sub>(132 MiB / 58.6% CPU · 241.3 µs CPU/req)</sub> | **16,272**<br><sub>(203 MiB / 49.2% CPU · 121.0 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/3 · QUIC | 🥇 **16,696**<br><sub>(151 MiB / 50% CPU · 119.9 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **6,059**<br><sub>(132 MiB / 56% CPU · 369.7 µs CPU/req)</sub> | **12,891**<br><sub>(205 MiB / 49.2% CPU · 152.8 µs CPU/req)</sub> |

### MITM (TWP only)

Same Client×Origin wires with interception on (`compare-product` [37210516555](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37210516555)). **Lite** = no-op handlers (unchanged-lite finish). **Full** = append-only header mutation (harness: one probe header each way; product: generic append-only relay via `MitmCompressedRelayHelper`). nginx/HAProxy/Envoy/YARP cannot MITM. **Lite÷Reverse** / **Full÷Reverse** vs bare reverse (**same job / comparison-group shard**). Completion gate: Lite ≥ **0.25×** and Full ≥ **0.25×** reverse sustain @ c=64 (median of 3 GHA runs); reverse TWP ÷ closest peer ≥ **0.50×**.

**v1 append-only relay (2026-08-27):** Pre-fix H2→H2 Full÷Reverse was **0.13–0.16×** ([32960766249](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32960766249)). Post-fix @ `df172718`: H2 plain→H2 plain Full **0.77–0.79×**, H3→H1 Full **0.91–0.93×**, all MITM arms ≥ **0.70×** on median of [33041445371](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33041445371), [33055267086](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33055267086), [33055272140](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33055272140).

**v2 drop-only + non-unique append (2026-08-27):** `MitmStaticRebuildHelper` rebuilds static HPACK/QPACK after 1–4 unique header drops; trailing non-unique appends stay on compressed relay. @ `41f4adee`: all MITM arms ≥ **0.70×** on GHA median ([33087088466](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087088466), [33087091622](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087091622), [33105885748](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33105885748) Linux; [33087085235](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087085235), [33087088466](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087088466), [33087091622](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087091622) Windows). H2 plain→H2 plain Full **0.77–0.79×** (Win) / **0.78×** (Lin).

| Client | Origin | Lite sustain | Full sustain | Lite÷Reverse | Full÷Reverse |
|---|---|---:|---:|---:|---:|
| HTTP/1 · plain | HTTP/1 · plain | **31,196**<br><sub>(91 MiB / 49.8% CPU · 63.9 µs CPU/req)</sub> | **30,622**<br><sub>(92 MiB / 49.3% CPU · 64.4 µs CPU/req)</sub> | **0.99×** | **0.98×** |
| HTTP/1 · plain | HTTP/1 · TLS | **44,825**<br><sub>(108 MiB / 46.4% CPU · 41.4 µs CPU/req)</sub> | **44,084**<br><sub>(114 MiB / 46% CPU · 41.7 µs CPU/req)</sub> | **1×** | **0.98×** |
| HTTP/1 · plain | HTTP/2 · plain | **58,818**<br><sub>(125 MiB / 52.7% CPU · 35.9 µs CPU/req)</sub> | **56,959**<br><sub>(134 MiB / 53.3% CPU · 37.5 µs CPU/req)</sub> | **0.98×** | **0.94×** |
| HTTP/1 · plain | HTTP/2 · TLS | **58,159**<br><sub>(147 MiB / 49.8% CPU · 34.2 µs CPU/req)</sub> | **57,510**<br><sub>(157 MiB / 49.6% CPU · 34.5 µs CPU/req)</sub> | **0.96×** | **0.95×** |
| HTTP/1 · plain | HTTP/3 · QUIC | **33,518**<br><sub>(148 MiB / 55.3% CPU · 66.0 µs CPU/req)</sub> | **35,182**<br><sub>(146 MiB / 55.4% CPU · 62.9 µs CPU/req)</sub> | **0.97×** | **1.01×** |
| HTTP/1 · TLS | HTTP/1 · plain | **39,529**<br><sub>(113 MiB / 48% CPU · 48.6 µs CPU/req)</sub> | **38,574**<br><sub>(113 MiB / 47.6% CPU · 49.4 µs CPU/req)</sub> | **0.98×** | **0.95×** |
| HTTP/1 · TLS | HTTP/1 · TLS | **19,874**<br><sub>(115 MiB / 49.1% CPU · 98.8 µs CPU/req)</sub> | **19,276**<br><sub>(114 MiB / 49.1% CPU · 101.9 µs CPU/req)</sub> | **1.01×** | **0.98×** |
| HTTP/1 · TLS | HTTP/2 · plain | **34,414**<br><sub>(141 MiB / 52.8% CPU · 61.3 µs CPU/req)</sub> | **32,977**<br><sub>(141 MiB / 53% CPU · 64.2 µs CPU/req)</sub> | **0.97×** | **0.93×** |
| HTTP/1 · TLS | HTTP/2 · TLS | **23,969**<br><sub>(165 MiB / 49.1% CPU · 81.9 µs CPU/req)</sub> | **23,259**<br><sub>(160 MiB / 49.1% CPU · 84.5 µs CPU/req)</sub> | **0.98×** | **0.95×** |
| HTTP/1 · TLS | HTTP/3 · QUIC | **18,005**<br><sub>(154 MiB / 52% CPU · 115.5 µs CPU/req)</sub> | **16,894**<br><sub>(142 MiB / 53.1% CPU · 125.7 µs CPU/req)</sub> | **0.99×** | **0.93×** |
| HTTP/2 · plain | HTTP/1 · plain | **35,085**<br><sub>(142 MiB / 53.8% CPU · 61.3 µs CPU/req)</sub> | **34,932**<br><sub>(144 MiB / 53.7% CPU · 61.5 µs CPU/req)</sub> | **0.96×** | **0.95×** |
| HTTP/2 · plain | HTTP/1 · TLS | **27,257**<br><sub>(147 MiB / 52% CPU · 76.3 µs CPU/req)</sub> | **26,616**<br><sub>(153 MiB / 51.9% CPU · 78.0 µs CPU/req)</sub> | **0.98×** | **0.96×** |
| HTTP/2 · plain | HTTP/2 · plain | **59,650**<br><sub>(148 MiB / 45.6% CPU · 30.6 µs CPU/req)</sub> | **57,308**<br><sub>(149 MiB / 44.9% CPU · 31.4 µs CPU/req)</sub> | **0.87×** | **0.84×** |
| HTTP/2 · plain | HTTP/2 · TLS | **46,667**<br><sub>(150 MiB / 44.6% CPU · 38.2 µs CPU/req)</sub> | **45,069**<br><sub>(165 MiB / 44.5% CPU · 39.5 µs CPU/req)</sub> | **0.88×** | **0.85×** |
| HTTP/2 · plain | HTTP/3 · QUIC | **34,316**<br><sub>(177 MiB / 50.2% CPU · 58.5 µs CPU/req)</sub> | **33,710**<br><sub>(178 MiB / 50% CPU · 59.4 µs CPU/req)</sub> | **1.01×** | **0.99×** |
| HTTP/2 · TLS | HTTP/1 · plain | **31,906**<br><sub>(147 MiB / 53.6% CPU · 67.3 µs CPU/req)</sub> | **30,787**<br><sub>(147 MiB / 53.1% CPU · 69.0 µs CPU/req)</sub> | **0.99×** | **0.96×** |
| HTTP/2 · TLS | HTTP/1 · TLS | **35,377**<br><sub>(158 MiB / 49.6% CPU · 56.0 µs CPU/req)</sub> | **35,136**<br><sub>(151 MiB / 49.4% CPU · 56.3 µs CPU/req)</sub> | **0.96×** | **0.95×** |
| HTTP/2 · TLS | HTTP/2 · plain | **50,844**<br><sub>(163 MiB / 45.9% CPU · 36.1 µs CPU/req)</sub> | **48,505**<br><sub>(154 MiB / 45.8% CPU · 37.8 µs CPU/req)</sub> | **0.89×** | **0.85×** |
| HTTP/2 · TLS | HTTP/2 · TLS | **41,140**<br><sub>(168 MiB / 44.3% CPU · 43.1 µs CPU/req)</sub> | **40,144**<br><sub>(165 MiB / 44.4% CPU · 44.2 µs CPU/req)</sub> | **0.87×** | **0.85×** |
| HTTP/2 · TLS | HTTP/3 · QUIC | **25,613**<br><sub>(199 MiB / 49.2% CPU · 76.9 µs CPU/req)</sub> | **25,243**<br><sub>(190 MiB / 49.6% CPU · 78.5 µs CPU/req)</sub> | **0.98×** | **0.96×** |
| HTTP/3 · QUIC | HTTP/1 · plain | **26,000**<br><sub>(158 MiB / 54% CPU · 83.1 µs CPU/req)</sub> | **25,657**<br><sub>(153 MiB / 54.3% CPU · 84.7 µs CPU/req)</sub> | **0.95×** | **0.94×** |
| HTTP/3 · QUIC | HTTP/1 · TLS | **14,382**<br><sub>(164 MiB / 53.4% CPU · 148.5 µs CPU/req)</sub> | **13,988**<br><sub>(162 MiB / 53.2% CPU · 152.1 µs CPU/req)</sub> | **0.97×** | **0.94×** |
| HTTP/3 · QUIC | HTTP/2 · plain | **45,075**<br><sub>(185 MiB / 55.8% CPU · 49.5 µs CPU/req)</sub> | **45,689**<br><sub>(188 MiB / 55.1% CPU · 48.2 µs CPU/req)</sub> | **0.95×** | **0.96×** |
| HTTP/3 · QUIC | HTTP/2 · TLS | **19,839**<br><sub>(155 MiB / 53.3% CPU · 107.5 µs CPU/req)</sub> | **18,948**<br><sub>(160 MiB / 52.6% CPU · 111.1 µs CPU/req)</sub> | **0.99×** | **0.95×** |
| HTTP/3 · QUIC | HTTP/3 · QUIC | **16,001**<br><sub>(153 MiB / 52.7% CPU · 131.8 µs CPU/req)</sub> | **15,955**<br><sub>(156 MiB / 52.9% CPU · 132.6 µs CPU/req)</sub> | **0.96×** | **0.96×** |

## macOS — Titanium vs nginx vs HAProxy vs Envoy vs YARP

### Reverse

Median of **3 repeats** on `macos-15` (Apple Silicon M1, 3-core / 7 GB). Bare reverse 5×5 @ `482f5984` — `compare-product` [37210516555](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37210516555). Warmup 2s / measure 8s; concurrency 8, 16, 32, 64. Prefer TWP÷peer ratios over absolute RPS. **RPS cells** include median RSS / CPU at the peak-RPS step as `<br><sub>(MiB / CPU%)</sub>`. The RPS workflow installs Homebrew nginx (`http_v3_module`), Homebrew HAProxy with `USE_QUIC` (3.2 source fallback), Envoy (Homebrew bottle or pinned darwin-arm64 1.36.7), Homebrew `libmsquic` (+ `DYLD_*`), and YARP. Use the pinned `macos-15` label, not `macos-latest`. Product 5×5 is **~56-byte JSON keep-alive GET**; H2/H3 same-protocol cells are mostly header work with a tiny body (Titanium best case) — see [Why this comparison is fair](#user-content-why-this-comparison-is-fair).

| Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---:|---:|---:|---:|---:|
| HTTP/1 · plain | HTTP/1 · plain | **7,105**<br><sub>(114 MiB / 10.7% CPU · 45.3 µs CPU/req)</sub> | 🥇 **17,906**<br><sub>(68 MiB / 11.6% CPU · 19.4 µs CPU/req)</sub> | **7,751**<br><sub>(91 MiB / 10.3% CPU · 40.0 µs CPU/req)</sub> | **7,226**<br><sub>(111 MiB / 24.3% CPU · 101.0 µs CPU/req)</sub> | **9,245**<br><sub>(169 MiB / 13.6% CPU · 44.1 µs CPU/req)</sub> |
| HTTP/1 · plain | HTTP/1 · TLS | **6,570**<br><sub>(126 MiB / 15.5% CPU · 70.6 µs CPU/req)</sub> | **12,385**<br><sub>(78 MiB / 16.3% CPU · 39.5 µs CPU/req)</sub> | 🥇 **13,060**<br><sub>(96 MiB / 21.3% CPU · 49.0 µs CPU/req)</sub> | **9,394**<br><sub>(113 MiB / 29% CPU · 92.6 µs CPU/req)</sub> | **5,214**<br><sub>(203 MiB / 13.6% CPU · 78.4 µs CPU/req)</sub> |
| HTTP/1 · plain | HTTP/2 · plain | 🥇 **45,183**<br><sub>(144 MiB / 26.9% CPU · 17.9 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **34,431**<br><sub>(104 MiB / 30.4% CPU · 26.5 µs CPU/req)</sub> | **34,198**<br><sub>(111 MiB / 34.5% CPU · 30.3 µs CPU/req)</sub> | **36,843**<br><sub>(163 MiB / 21.4% CPU · 17.4 µs CPU/req)</sub> |
| HTTP/1 · plain | HTTP/2 · TLS | **17,960**<br><sub>(164 MiB / 17.5% CPU · 29.2 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **13,000**<br><sub>(99 MiB / 13.4% CPU · 30.8 µs CPU/req)</sub> | **10,582**<br><sub>(113 MiB / 19.7% CPU · 55.9 µs CPU/req)</sub> | 🥇 **18,772**<br><sub>(167 MiB / 16.3% CPU · 26.1 µs CPU/req)</sub> |
| HTTP/1 · plain | HTTP/3 · QUIC | **11,126**<br><sub>(133 MiB / 23.9% CPU · 64.5 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **3,729**<br><sub>(117 MiB / 18.5% CPU · 148.9 µs CPU/req)</sub> | 🥇 **11,631**<br><sub>(163 MiB / 19% CPU · 49.1 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/1 · plain | **6,420**<br><sub>(128 MiB / 11.6% CPU · 54.2 µs CPU/req)</sub> | 🥇 **18,160**<br><sub>(85 MiB / 15.8% CPU · 26.0 µs CPU/req)</sub> | **15,854**<br><sub>(108 MiB / 19.8% CPU · 37.4 µs CPU/req)</sub> | **8,061**<br><sub>(122 MiB / 20.2% CPU · 75.1 µs CPU/req)</sub> | **10,810**<br><sub>(162 MiB / 20.4% CPU · 56.6 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/1 · TLS | 🥇 **7,991**<br><sub>(153 MiB / 20.5% CPU · 76.8 µs CPU/req)</sub> | **7,624**<br><sub>(89 MiB / 11.2% CPU · 43.9 µs CPU/req)</sub> | **6,204**<br><sub>(109 MiB / 13.3% CPU · 64.2 µs CPU/req)</sub> | **6,747**<br><sub>(123 MiB / 25% CPU · 111.1 µs CPU/req)</sub> | **3,900**<br><sub>(224 MiB / 11.9% CPU · 91.3 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/2 · plain | **11,822**<br><sub>(143 MiB / 11% CPU · 27.8 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **9,010**<br><sub>(112 MiB / 14.4% CPU · 47.9 µs CPU/req)</sub> | **22,406**<br><sub>(120 MiB / 27.8% CPU · 37.2 µs CPU/req)</sub> | 🥇 **27,679**<br><sub>(172 MiB / 22.6% CPU · 24.5 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/2 · TLS | **28,926**<br><sub>(197 MiB / 35.6% CPU · 36.9 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **23,046**<br><sub>(131 MiB / 37.1% CPU · 48.3 µs CPU/req)</sub> | **24,438**<br><sub>(121 MiB / 48% CPU · 58.9 µs CPU/req)</sub> | 🥇 **34,479**<br><sub>(171 MiB / 36.4% CPU · 31.7 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/3 · QUIC | 🥇 **5,392**<br><sub>(161 MiB / 13.9% CPU · 77.2 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **4,701**<br><sub>(125 MiB / 20.7% CPU · 132.3 µs CPU/req)</sub> | **5,267**<br><sub>(182 MiB / 10.7% CPU · 60.9 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | **22,542**<br><sub>(173 MiB / 22.7% CPU · 30.2 µs CPU/req)</sub> | **18,571**<br><sub>(70 MiB / 14.2% CPU · 23.0 µs CPU/req)</sub> | 🥇 **28,814**<br><sub>(98 MiB / 26.2% CPU · 27.3 µs CPU/req)</sub> | **15,206**<br><sub>(112 MiB / 34.8% CPU · 68.6 µs CPU/req)</sub> | **18,839**<br><sub>(153 MiB / 24.5% CPU · 39.0 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/1 · TLS | 🥇 **15,111**<br><sub>(200 MiB / 20.4% CPU · 40.5 µs CPU/req)</sub> | **7,653**<br><sub>(81 MiB / 7.8% CPU · 30.7 µs CPU/req)</sub> | **8,962**<br><sub>(97 MiB / 12.7% CPU · 42.6 µs CPU/req)</sub> | **11,431**<br><sub>(114 MiB / 26% CPU · 68.2 µs CPU/req)</sub> | **9,622**<br><sub>(164 MiB / 22.4% CPU · 69.7 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/2 · plain | **19,941**<br><sub>(220 MiB / 9.9% CPU · 14.9 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **11,895**<br><sub>(101 MiB / 12.7% CPU · 32.0 µs CPU/req)</sub> | **21,864**<br><sub>(110 MiB / 24.9% CPU · 34.1 µs CPU/req)</sub> | 🥇 **23,982**<br><sub>(163 MiB / 17.6% CPU · 22.0 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/2 · TLS | 🥇 **31,236**<br><sub>(240 MiB / 14.2% CPU · 13.6 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **25,521**<br><sub>(104 MiB / 24.2% CPU · 28.4 µs CPU/req)</sub> | **17,483**<br><sub>(111 MiB / 18% CPU · 30.8 µs CPU/req)</sub> | **17,015**<br><sub>(171 MiB / 10.6% CPU · 18.7 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/3 · QUIC | **7,398**<br><sub>(206 MiB / 13.8% CPU · 55.9 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **12,701**<br><sub>(116 MiB / 31.4% CPU · 74.2 µs CPU/req)</sub> | 🥇 **20,539**<br><sub>(167 MiB / 26.4% CPU · 38.6 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | **19,283**<br><sub>(181 MiB / 23.9% CPU · 37.2 µs CPU/req)</sub> | **19,033**<br><sub>(83 MiB / 22.6% CPU · 35.7 µs CPU/req)</sub> | 🥇 **25,232**<br><sub>(118 MiB / 33.6% CPU · 39.9 µs CPU/req)</sub> | **17,737**<br><sub>(121 MiB / 46.6% CPU · 78.9 µs CPU/req)</sub> | **12,200**<br><sub>(159 MiB / 21.8% CPU · 53.5 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/1 · TLS | **6,993**<br><sub>(216 MiB / 14.6% CPU · 62.6 µs CPU/req)</sub> | **7,862**<br><sub>(88 MiB / 9.2% CPU · 35.3 µs CPU/req)</sub> | 🥇 **15,084**<br><sub>(110 MiB / 24.3% CPU · 48.4 µs CPU/req)</sub> | **8,194**<br><sub>(122 MiB / 19.7% CPU · 72.0 µs CPU/req)</sub> | **11,945**<br><sub>(166 MiB / 20.8% CPU · 52.1 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | **9,534**<br><sub>(238 MiB / 7.7% CPU · 24.4 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **12,695**<br><sub>(111 MiB / 20.9% CPU · 49.5 µs CPU/req)</sub> | **7,376**<br><sub>(119 MiB / 16.5% CPU · 67.2 µs CPU/req)</sub> | 🥇 **16,833**<br><sub>(248 MiB / 13.4% CPU · 23.8 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | **12,624**<br><sub>(265 MiB / 8.1% CPU · 19.2 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | 🥇 **29,595**<br><sub>(121 MiB / 27.6% CPU · 27.9 µs CPU/req)</sub> | **11,210**<br><sub>(120 MiB / 14.8% CPU · 39.5 µs CPU/req)</sub> | **6,121**<br><sub>(184 MiB / 6.8% CPU · 33.4 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/3 · QUIC | **5,549**<br><sub>(208 MiB / 13.5% CPU · 72.9 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **7,792**<br><sub>(123 MiB / 23.3% CPU · 89.8 µs CPU/req)</sub> | 🥇 **10,099**<br><sub>(172 MiB / 17.1% CPU · 50.9 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | **6,298**<br><sub>(145 MiB / 23.7% CPU · 112.8 µs CPU/req)</sub> | 🥇 **12,690**<br><sub>(86 MiB / 12.2% CPU · 28.8 µs CPU/req)</sub> | **10,825**<br><sub>(104 MiB / 15% CPU · 41.5 µs CPU/req)</sub> | **4,599**<br><sub>(126 MiB / 24.3% CPU · 158.5 µs CPU/req)</sub> | **3,324**<br><sub>(252 MiB / 14.2% CPU · 127.9 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **10,902**<br><sub>(162 MiB / 26.8% CPU · 73.9 µs CPU/req)</sub> | *Not measured* | **9,558**<br><sub>(108 MiB / 19.9% CPU · 62.4 µs CPU/req)</sub> | **4,522**<br><sub>(126 MiB / 28% CPU · 185.9 µs CPU/req)</sub> | **3,907**<br><sub>(267 MiB / 20.6% CPU · 158.5 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/2 · plain | **5,108**<br><sub>(132 MiB / 16.1% CPU · 94.6 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **5,022**<br><sub>(108 MiB / 11% CPU · 65.9 µs CPU/req)</sub> | **4,249**<br><sub>(124 MiB / 25% CPU · 176.8 µs CPU/req)</sub> | 🥇 **5,792**<br><sub>(266 MiB / 14.8% CPU · 76.9 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | **7,887**<br><sub>(142 MiB / 17.7% CPU · 67.2 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **7,318**<br><sub>(107 MiB / 14.8% CPU · 60.7 µs CPU/req)</sub> | **5,970**<br><sub>(122 MiB / 28.2% CPU · 141.7 µs CPU/req)</sub> | 🥇 **8,025**<br><sub>(275 MiB / 17.7% CPU · 66.3 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/3 · QUIC | **4,485**<br><sub>(137 MiB / 17% CPU · 113.9 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | 🥇 **6,850**<br><sub>(122 MiB / 34.9% CPU · 152.7 µs CPU/req)</sub> | **3,931**<br><sub>(253 MiB / 20.4% CPU · 156.0 µs CPU/req)</sub> |

## Editions (CLI / Plus / Intercept)

**Note:** `twp-reverse-http1` and other library rows use Core with **probe-tuned** settings (no logging, no Via header, probe-warmed certs). Edition rows use `titanium run -c twp.yaml` **product defaults** — prefer the ÷baseline ratio column over absolute RPS. Inspector GUI is not spawnable in the harness; session-path overhead is `twp-cli-intercept-http1` (route `RequestHeaderSet` transform). Pre-origin Plus middleware (CIDR/WAF/JWT/rate-limit/cache) runs on H1 terminate-lite without `SessionEventArgs`; a cache hit skips the origin. JWT caches successful bearer validations. CORS only adds response headers on the way out, so it stays on terminate-lite. Circuit breaker and idempotent retry have to see the request or the status code, so they stay on the session path and should land near the intercept row. Maintainer gate thresholds live under [Maintainer notes](#user-content-maintainer-notes).

Median of **3** repeats on `ubuntu-latest`. Every row in this table was re-measured @ `482f5984` (Actions [37210543774](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37210543774)). Warmup 2s / measure 8s; concurrency 8–64; sustain = median peak RPS among SLO-pass steps @ **c=64**. **RPS cells** show sustain; `<sub>` holds peak (when higher) plus `(MiB / CPU%)`. The Gate column is that script's floor.

| Arm | Linux | Lin÷ | Gate |
|---|---:|---:|---|
| `twp-cli-reverse-http1` vs library | **33,245**<br><sub>(164 MiB / 49.9% CPU · 60.0 µs CPU/req)</sub> | **1.05×** | ≥ **0.50×** |
| `twp-cli-reverse-http1-tls` vs library TLS | **24,014**<br><sub>(195 MiB / 50.4% CPU · 84.0 µs CPU/req)</sub> | **1.03×** | ≥ **0.50×** |
| `twp-cli-reverse-http1-route` vs CLI | **63,157**<br><sub>(169 MiB / 49.0% CPU · 31.0 µs CPU/req)</sub> | **0.96×** | ≥ **0.50×** |
| `twp-cli-plus-base-http1` vs CLI | **48,080**<br><sub>(170 MiB / 49.4% CPU · 41.1 µs CPU/req)</sub> | **0.99×** | ≥ **0.50×** |
| `twp-cli-plus-cache-http1` (cold) vs CLI | **58,197**<br><sub>(168 MiB / 47.7% CPU · 32.8 µs CPU/req)</sub> | **1.74×** | ≥ **0.50×** |
| `twp-cli-intercept-http1` vs CLI | **24,191**<br><sub>(176 MiB / 53.0% CPU · 87.6 µs CPU/req)</sub> | **0.73×** | ≥ **0.50×** |
| `twp-cli-plus-waf-http1` vs CLI | **32,992**<br><sub>(169 MiB / 50.4% CPU · 61.1 µs CPU/req)</sub> | **0.97×** | ≥ **0.50×** |
| `twp-cli-plus-cidr-http1` vs CLI | **64,841**<br><sub>(235 MiB / 48.2% CPU · 29.8 µs CPU/req)</sub> | **1.01×** | ≥ **0.50×** |
| `twp-cli-plus-jwt-http1` vs CLI | **30,337**<br><sub>(192 MiB / 50.4% CPU · 66.4 µs CPU/req)</sub> | **0.91×** | ≥ **0.50×** |
| `twp-cli-plus-ratelimit-http1` vs CLI | **62,173**<br><sub>(235 MiB / 48.0% CPU · 30.9 µs CPU/req)</sub> | **0.97×** | ≥ **0.50×** |
| `twp-cli-plus-resilience-http1` vs CLI | **55,849**<br><sub>(240 MiB / 48.8% CPU · 34.9 µs CPU/req)</sub> | **1.03×** | ≥ **0.50×** |
| `twp-cli-plus-discovery-file-http1` vs CLI | **37,627**<br><sub>(169 MiB / 49.4% CPU · 52.5 µs CPU/req)</sub> | **1.01×** | ≥ **0.50×** |
| `twp-cli-plus-metrics-scrape-http1` vs CLI | **33,316**<br><sub>(174 MiB / 49.7% CPU · 59.7 µs CPU/req)</sub> | **1.01×** | ≥ **0.50×** |
| `twp-cli-plus-cache-hit-http1` vs cache cold | **59,678**<br><sub>(171 MiB / 49.1% CPU · 32.9 µs CPU/req)</sub> | **1.03×** | ≥ **0.50×** |
| `twp-cli-plus-cors-http1` vs CLI | **31,610**<br><sub>(166 MiB / 50.9% CPU · 64.4 µs CPU/req)</sub> | **0.98×** | ≥ **0.50×** |
| `twp-cli-plus-circuit-http1` vs CLI | **51,728**<br><sub>(175 MiB / 49.6% CPU · 38.4 µs CPU/req)</sub> | **0.82×** | ≥ **0.50×** |
| `twp-cli-plus-retry-http1` vs CLI | **26,733**<br><sub>(174 MiB / 50.5% CPU · 75.5 µs CPU/req)</sub> | **0.82×** | ≥ **0.50×** |
| `twp-cli-static-http1` vs CLI | **111,716**<br><sub>(166 MiB / 48.1% CPU · 17.2 µs CPU/req)</sub> | **1.74×** | ≥ **0.50×** |
| `twp-cli-logging-http1` vs CLI | **33,316**<br><sub>(163 MiB / 50.2% CPU · 60.3 µs CPU/req)</sub> | **1.00×** | ≥ **0.50×** |
| `twp-cli-lb-leasttime-http1` vs route | **31,121**<br><sub>(193 MiB / 51.8% CPU · 66.6 µs CPU/req)</sub> | **0.94×** | ≥ **0.50×** |
| `twp-cli-dialect-twp-http1` vs CLI | **33,668**<br><sub>(161 MiB / 49.7% CPU · 59.0 µs CPU/req)</sub> | **1.02×** | ≥ **0.50×** |

`validate-edition-gates.ps1` floors are **0.50×**. Each ÷ column uses the two arms from the same job. Circuit breaker and idempotent retry stay on the session path, so a ratio near intercept is expected. Laptop smoke ratios stay on [Performance Local Lab — Editions](Performance-Local-Lab#user-content-editions-cli--plus-stress).

## Heavier reverse workloads

Same runners and harness as the tiny-GET tables, but with larger bodies, POST, lossy links, TLS cost, and architecture-sensitive paths (slow consumer / early response / duplex). **PUT with the same body is the same proxy work as POST; DELETE with no body matches GET** — only POST is published. Bodies/POST/lossy stay **half-duplex**. Laptop numbers are on [Performance Local Lab](Performance-Local-Lab#user-content-architecture-sensitive). How maintainers refresh these tables (modes, shards, paste scripts) is under [Maintainer notes](#user-content-maintainer-notes). **Larger-body check:** 64 / 256 KiB H2 TLS→H2 TLS is where body copy dominates headers — ratios vs YARP are in the tables below (@ `482f5984`), unlike the tiny-GET H2↔H2 medals above. Those cells also show goodput (MiB/s) and µs CPU/req, and the medal on those rows follows goodput.

Lossy link = **userspace** delay/drop shim (not kernel `netem`): TCP gets per-buffer delay + occasional whole-connection stalls (honest head-of-line for multiplexed HTTP/2); UDP is **loss% only** (no per-datagram delay) + drops (QUIC / MsQuic-safe). Lossy tables publish HTTP/1, HTTP/2, and HTTP/3.

### Windows — heavier reverse GET (64 KiB / 256 KiB)

Median of **3** repeats on `windows-latest` @ `482f5984`. Source: Actions [37210521790](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37210521790) (`compare-bodies`). Warmup 2s / measure 8s. **RPS cells** include `(MiB / CPU%)` footprints.

*Not possible:* **HAProxy** and **Envoy** columns are omitted (no official Windows port).

















| Body | Client | Origin | TWP | nginx | YARP |
|---|---|---|---:|---:|---:|
| 64 KiB | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **9,157**<br><sub>(89 MiB / 46.1% CPU · 572 MiB/s · 201.2 µs CPU/req)</sub> | **619**<br><sub>(143 MiB / 24.8% CPU · 39 MiB/s · 1599.1 µs CPU/req)</sub> | **7,890**<br><sub>(134 MiB / 46.9% CPU · 493 MiB/s · 237.7 µs CPU/req)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **8,611**<br><sub>(217 MiB / 51.0% CPU · 538 MiB/s · 237.1 µs CPU/req)</sub> | **584**<br><sub>(142 MiB / 24.8% CPU · 36 MiB/s · 1698.4 µs CPU/req)</sub> | **8,259**<br><sub>(126 MiB / 51.6% CPU · 516 MiB/s · 250.0 µs CPU/req)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **4,524**<br><sub>(139 MiB / 45.4% CPU · 283 MiB/s · 401.3 µs CPU/req)</sub> | *Not possible (no QUIC)* | **2,379**<br><sub>(183 MiB / 51.3% CPU · 149 MiB/s · 862.8 µs CPU/req)</sub> |
| 256 KiB | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **2,857**<br><sub>(120 MiB / 46.5% CPU · 714 MiB/s · 650.8 µs CPU/req)</sub> | **166**<br><sub>(142 MiB / 24.8% CPU · 41 MiB/s · 5987.9 µs CPU/req)</sub> | **2,616**<br><sub>(136 MiB / 48.7% CPU · 654 MiB/s · 744.5 µs CPU/req)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/1 · plain | **3,083**<br><sub>(203 MiB / 48.3% CPU · 771 MiB/s · 626.7 µs CPU/req)</sub> | **220**<br><sub>(143 MiB / 24.9% CPU · 55 MiB/s · 4517.2 µs CPU/req)</sub> | 🥇 **3,226**<br><sub>(124 MiB / 46.7% CPU · 807 MiB/s · 579.0 µs CPU/req)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **1,995**<br><sub>(118 MiB / 42.1% CPU · 499 MiB/s · 843.9 µs CPU/req)</sub> | *Not possible (no QUIC)* | **1,469**<br><sub>(182 MiB / 42.0% CPU · 367 MiB/s · 1143.3 µs CPU/req)</sub> |
| 64 KiB | HTTP/2 · plain | HTTP/1 · plain | 🥇 **12,018**<br><sub>(191 MiB / 44.0% CPU · 751 MiB/s · 146.5 µs CPU/req)</sub> | **1,999**<br><sub>(127 MiB / 24.7% CPU · 125 MiB/s · 493.9 µs CPU/req)</sub> | **10,459**<br><sub>(121 MiB / 50.7% CPU · 654 MiB/s · 194.0 µs CPU/req)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/2 · plain | 🥇 **8,329**<br><sub>(169 MiB / 45.9% CPU · 521 MiB/s · 220.6 µs CPU/req)</sub> | *Not possible* | **7,536**<br><sub>(131 MiB / 52.7% CPU · 471 MiB/s · 279.5 µs CPU/req)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **6,344**<br><sub>(168 MiB / 41.4% CPU · 396 MiB/s · 261.1 µs CPU/req)</sub> | *Not possible* | **5,415**<br><sub>(132 MiB / 49.2% CPU · 338 MiB/s · 363.1 µs CPU/req)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/2 · TLS | 🥇 **4,650**<br><sub>(135 MiB / 43.7% CPU · 291 MiB/s · 376.0 µs CPU/req)</sub> | *Not possible* | **2,836**<br><sub>(186 MiB / 52.7% CPU · 177 MiB/s · 743.1 µs CPU/req)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **3,290**<br><sub>(146 MiB / 46.4% CPU · 206 MiB/s · 564.1 µs CPU/req)</sub> | *Not possible (no QUIC)* | **1,821**<br><sub>(196 MiB / 50.2% CPU · 114 MiB/s · 1101.7 µs CPU/req)</sub> |
| 256 KiB | HTTP/2 · plain | HTTP/1 · plain | 🥇 **4,300**<br><sub>(176 MiB / 34.6% CPU · 1,075 MiB/s · 322.1 µs CPU/req)</sub> | **654**<br><sub>(127 MiB / 24.9% CPU · 163 MiB/s · 1522.6 µs CPU/req)</sub> | **3,909**<br><sub>(116 MiB / 42.2% CPU · 977 MiB/s · 432.3 µs CPU/req)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/2 · plain | **3,668**<br><sub>(203 MiB / 44.6% CPU · 917 MiB/s · 486.0 µs CPU/req)</sub> | *Not possible* | 🥇 **3,797**<br><sub>(170 MiB / 44.1% CPU · 949 MiB/s · 464.1 µs CPU/req)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **2,082**<br><sub>(200 MiB / 42.7% CPU · 521 MiB/s · 819.4 µs CPU/req)</sub> | *Not possible* | **1,768**<br><sub>(146 MiB / 42.8% CPU · 442 MiB/s · 968.1 µs CPU/req)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/2 · TLS | **870**<br><sub>(145 MiB / 49.1% CPU · 217 MiB/s · 2258.0 µs CPU/req)</sub> | *Not possible* | 🥇 **1,338**<br><sub>(194 MiB / 48.6% CPU · 334 MiB/s · 1452.8 µs CPU/req)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/1 · TLS | **771**<br><sub>(peak 1,607 · 122 MiB / 43.9% CPU · 193 MiB/s · 2280.8 µs CPU/req)</sub> | *Not possible (no QUIC)* | 🥇 **1,514**<br><sub>(186 MiB / 46.1% CPU · 379 MiB/s · 1216.8 µs CPU/req)</sub> |

nginx/Windows collapses on large reverse bodies in this harness; treat as same-OS only. Prefer the ratio columns; absolute RPS swings by VM.

### Linux — heavier reverse GET (64 KiB / 256 KiB)

Median of **3** repeats @ `482f5984`. Source: Actions [37210521790](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37210521790) (`compare-bodies`). Warmup 2s / measure 8s.


| Body | Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---|---:|---:|---:|---:|---:|
| 64 KiB | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **12,766**<br><sub>(127 MiB / 41.8% CPU · 798 MiB/s · 130.9 µs CPU/req)</sub> | **8,147**<br><sub>(100 MiB / 49.4% CPU · 509 MiB/s · 242.6 µs CPU/req)</sub> | **12,654**<br><sub>(88 MiB / 39.8% CPU · 791 MiB/s · 125.9 µs CPU/req)</sub> | **11,049**<br><sub>(131 MiB / 43.7% CPU · 691 MiB/s · 158.2 µs CPU/req)</sub> | **9,584**<br><sub>(166 MiB / 46.8% CPU · 599 MiB/s · 195.2 µs CPU/req)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/1 · plain | **14,876**<br><sub>(245 MiB / 42.9% CPU · 930 MiB/s · 115.3 µs CPU/req)</sub> | **9,240**<br><sub>(102 MiB / 48.3% CPU · 578 MiB/s · 209.2 µs CPU/req)</sub> | **12,108**<br><sub>(87 MiB / 45.4% CPU · 757 MiB/s · 150.1 µs CPU/req)</sub> | 🥇 **15,828**<br><sub>(134 MiB / 44.0% CPU · 989 MiB/s · 111.2 µs CPU/req)</sub> | **12,621**<br><sub>(157 MiB / 45.8% CPU · 789 MiB/s · 145.1 µs CPU/req)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/1 · plain | **5,606**<br><sub>(203 MiB / 45.3% CPU · 350 MiB/s · 323.3 µs CPU/req)</sub> | **2,589**<br><sub>(116 MiB / 28.7% CPU · 162 MiB/s · 443.7 µs CPU/req)</sub> | 🥇 **5,814**<br><sub>(94 MiB / 36.3% CPU · 363 MiB/s · 250.0 µs CPU/req)</sub> | **4,457**<br><sub>(138 MiB / 51.2% CPU · 279 MiB/s · 459.6 µs CPU/req)</sub> | **3,738**<br><sub>(237 MiB / 51.5% CPU · 234 MiB/s · 551.6 µs CPU/req)</sub> |
| 256 KiB | HTTP/1 · TLS | HTTP/1 · plain | **3,855**<br><sub>(123 MiB / 31.7% CPU · 964 MiB/s · 328.6 µs CPU/req)</sub> | **2,448**<br><sub>(100 MiB / 48.4% CPU · 612 MiB/s · 790.1 µs CPU/req)</sub> | 🥇 **4,131**<br><sub>(87 MiB / 27.6% CPU · 1,033 MiB/s · 267.7 µs CPU/req)</sub> | **3,563**<br><sub>(145 MiB / 31.7% CPU · 891 MiB/s · 356.3 µs CPU/req)</sub> | **3,031**<br><sub>(166 MiB / 41.9% CPU · 758 MiB/s · 552.4 µs CPU/req)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/1 · plain | **2,052**<br><sub>(210 MiB / 41.7% CPU · 513 MiB/s · 812.5 µs CPU/req)</sub> | **1,546**<br><sub>(99 MiB / 50.5% CPU · 386 MiB/s · 1308.1 µs CPU/req)</sub> | **2,086**<br><sub>(87 MiB / 41.5% CPU · 522 MiB/s · 795.0 µs CPU/req)</sub> | 🥇 **2,412**<br><sub>(152 MiB / 36.1% CPU · 603 MiB/s · 599.1 µs CPU/req)</sub> | **1,813**<br><sub>(164 MiB / 46.1% CPU · 453 MiB/s · 1016.8 µs CPU/req)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/1 · plain | **1,600**<br><sub>(174 MiB / 49.6% CPU · 400 MiB/s · 1241.4 µs CPU/req)</sub> | **746**<br><sub>(116 MiB / 28.5% CPU · 187 MiB/s · 1526.1 µs CPU/req)</sub> | 🥇 **1,932**<br><sub>(95 MiB / 34.8% CPU · 483 MiB/s · 720.7 µs CPU/req)</sub> | **1,634**<br><sub>(154 MiB / 45.3% CPU · 408 MiB/s · 1109.7 µs CPU/req)</sub> | **1,284**<br><sub>(238 MiB / 51.7% CPU · 321 MiB/s · 1611.7 µs CPU/req)</sub> |
| 64 KiB | HTTP/2 · plain | HTTP/1 · plain | 🥇 **11,786**<br><sub>(262 MiB / 43.3% CPU · 737 MiB/s · 147.0 µs CPU/req)</sub> | **6,946**<br><sub>(78 MiB / 50.4% CPU · 434 MiB/s · 290.5 µs CPU/req)</sub> | **10,740**<br><sub>(72 MiB / 44.3% CPU · 671 MiB/s · 165.1 µs CPU/req)</sub> | **10,789**<br><sub>(124 MiB / 47.5% CPU · 674 MiB/s · 176.0 µs CPU/req)</sub> | **10,911**<br><sub>(150 MiB / 45.0% CPU · 682 MiB/s · 164.9 µs CPU/req)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/2 · plain | 🥇 **8,816**<br><sub>(208 MiB / 36.5% CPU · 551 MiB/s · 165.4 µs CPU/req)</sub> | *Not possible* | **7,247**<br><sub>(91 MiB / 42.0% CPU · 453 MiB/s · 231.9 µs CPU/req)</sub> | **8,232**<br><sub>(136 MiB / 43.0% CPU · 515 MiB/s · 209.1 µs CPU/req)</sub> | **6,821**<br><sub>(195 MiB / 48.0% CPU · 426 MiB/s · 281.4 µs CPU/req)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **5,010**<br><sub>(206 MiB / 41.6% CPU · 313 MiB/s · 331.8 µs CPU/req)</sub> | *Not possible* | **4,209**<br><sub>(91 MiB / 44.7% CPU · 263 MiB/s · 424.4 µs CPU/req)</sub> | **4,765**<br><sub>(141 MiB / 43.9% CPU · 298 MiB/s · 368.1 µs CPU/req)</sub> | **3,614**<br><sub>(180 MiB / 46.7% CPU · 226 MiB/s · 517.1 µs CPU/req)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/2 · TLS | **3,400**<br><sub>(181 MiB / 45.5% CPU · 213 MiB/s · 535.0 µs CPU/req)</sub> | *Not possible* | 🥇 **3,644**<br><sub>(99 MiB / 40.4% CPU · 228 MiB/s · 443.2 µs CPU/req)</sub> | **2,851**<br><sub>(145 MiB / 49.2% CPU · 178 MiB/s · 690.5 µs CPU/req)</sub> | **2,478**<br><sub>(248 MiB / 49.2% CPU · 155 MiB/s · 794.2 µs CPU/req)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/1 · TLS | **3,756**<br><sub>(214 MiB / 45.7% CPU · 235 MiB/s · 486.5 µs CPU/req)</sub> | **2,286**<br><sub>(117 MiB / 38.5% CPU · 143 MiB/s · 673.3 µs CPU/req)</sub> | 🥇 **3,924**<br><sub>(95 MiB / 41.1% CPU · 245 MiB/s · 419.5 µs CPU/req)</sub> | **3,254**<br><sub>(138 MiB / 49.6% CPU · 203 MiB/s · 609.5 µs CPU/req)</sub> | **2,762**<br><sub>(248 MiB / 51.5% CPU · 173 MiB/s · 745.3 µs CPU/req)</sub> |
| 256 KiB | HTTP/2 · plain | HTTP/1 · plain | **3,834**<br><sub>(205 MiB / 34.4% CPU · 959 MiB/s · 359.0 µs CPU/req)</sub> | **2,872**<br><sub>(78 MiB / 47.4% CPU · 718 MiB/s · 660.1 µs CPU/req)</sub> | **4,154**<br><sub>(72 MiB / 30.5% CPU · 1,038 MiB/s · 293.3 µs CPU/req)</sub> | 🥇 **4,238**<br><sub>(143 MiB / 30.0% CPU · 1,060 MiB/s · 283.0 µs CPU/req)</sub> | **3,745**<br><sub>(151 MiB / 36.0% CPU · 936 MiB/s · 384.0 µs CPU/req)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/2 · plain | 🥇 **2,137**<br><sub>(227 MiB / 37.9% CPU · 534 MiB/s · 708.9 µs CPU/req)</sub> | *Not possible* | **1,978**<br><sub>(99 MiB / 39.2% CPU · 494 MiB/s · 792.4 µs CPU/req)</sub> | **2,052**<br><sub>(146 MiB / 37.5% CPU · 513 MiB/s · 731.7 µs CPU/req)</sub> | **1,553**<br><sub>(206 MiB / 45.2% CPU · 388 MiB/s · 1165.8 µs CPU/req)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **1,368**<br><sub>(272 MiB / 40.3% CPU · 342 MiB/s · 1179.3 µs CPU/req)</sub> | *Not possible* | **1,282**<br><sub>(97 MiB / 40.1% CPU · 321 MiB/s · 1249.4 µs CPU/req)</sub> | **1,351**<br><sub>(143 MiB / 37.9% CPU · 338 MiB/s · 1121.2 µs CPU/req)</sub> | **963**<br><sub>(181 MiB / 44.2% CPU · 241 MiB/s · 1836.8 µs CPU/req)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/2 · TLS | **1,125**<br><sub>(204 MiB / 55.9% CPU · 281 MiB/s · 1988.4 µs CPU/req)</sub> | *Not possible* | 🥇 **2,020**<br><sub>(105 MiB / 33.9% CPU · 505 MiB/s · 671.7 µs CPU/req)</sub> | **1,706**<br><sub>(156 MiB / 41.3% CPU · 426 MiB/s · 968.0 µs CPU/req)</sub> | **1,328**<br><sub>(238 MiB / 47.5% CPU · 332 MiB/s · 1431.3 µs CPU/req)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/1 · TLS | **985**<br><sub>(192 MiB / 49.7% CPU · 246 MiB/s · 2018.7 µs CPU/req)</sub> | **605**<br><sub>(117 MiB / 38.6% CPU · 151 MiB/s · 2550.8 µs CPU/req)</sub> | 🥇 **1,140**<br><sub>(98 MiB / 39.8% CPU · 285 MiB/s · 1396.2 µs CPU/req)</sub> | **1,081**<br><sub>(153 MiB / 44.0% CPU · 270 MiB/s · 1628.9 µs CPU/req)</sub> | **796**<br><sub>(256 MiB / 49.9% CPU · 199 MiB/s · 2507.3 µs CPU/req)</sub> |

Prefer the ratio columns; absolute RPS swings by VM.

### macOS — heavier reverse GET (64 KiB / 256 KiB)

Median of **3** repeats on `macos-15` (Apple Silicon M1, 3-core / 7 GB) @ `482f5984`. Source: Actions [37210521790](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37210521790) (`compare-bodies`). Warmup 2s / measure 8s.

| Body | Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---|---:|---:|---:|---:|---:|
| 64 KiB | HTTP/1 · TLS | HTTP/1 · plain | **4,134**<br><sub>(177 MiB / 14.9% CPU · 258 MiB/s · 108.4 µs CPU/req)</sub> | 🥇 **9,185**<br><sub>(84 MiB / 21.7% CPU · 574 MiB/s · 70.9 µs CPU/req)</sub> | **9,097**<br><sub>(120 MiB / 25.8% CPU · 569 MiB/s · 85.0 µs CPU/req)</sub> | **7,800**<br><sub>(133 MiB / 26.8% CPU · 488 MiB/s · 103.1 µs CPU/req)</sub> | **5,900**<br><sub>(183 MiB / 27.1% CPU · 369 MiB/s · 138.1 µs CPU/req)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/1 · plain | **3,149**<br><sub>(469 MiB / 26.1% CPU · 197 MiB/s · 249.0 µs CPU/req)</sub> | **4,113**<br><sub>(87 MiB / 13.3% CPU · 257 MiB/s · 97.2 µs CPU/req)</sub> | **4,297**<br><sub>(120 MiB / 23.9% CPU · 269 MiB/s · 167.2 µs CPU/req)</sub> | 🥇 **5,386**<br><sub>(135 MiB / 20.3% CPU · 337 MiB/s · 113.0 µs CPU/req)</sub> | **2,376**<br><sub>(171 MiB / 16.5% CPU · 149 MiB/s · 208.4 µs CPU/req)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/1 · plain | **2,786**<br><sub>(278 MiB / 29.7% CPU · 174 MiB/s · 319.8 µs CPU/req)</sub> | 🥇 **3,796**<br><sub>(97 MiB / 28.1% CPU · 237 MiB/s · 221.9 µs CPU/req)</sub> | **2,692**<br><sub>(129 MiB / 31.3% CPU · 168 MiB/s · 348.8 µs CPU/req)</sub> | **2,141**<br><sub>(132 MiB / 44.8% CPU · 134 MiB/s · 627.5 µs CPU/req)</sub> | **2,136**<br><sub>(446 MiB / 37.1% CPU · 133 MiB/s · 521.2 µs CPU/req)</sub> |
| 256 KiB | HTTP/1 · TLS | HTTP/1 · plain | **2,115**<br><sub>(215 MiB / 25.1% CPU · 529 MiB/s · 355.5 µs CPU/req)</sub> | **1,844**<br><sub>(84 MiB / 18.5% CPU · 461 MiB/s · 301.6 µs CPU/req)</sub> | 🥇 **2,503**<br><sub>(117 MiB / 20.9% CPU · 626 MiB/s · 250.0 µs CPU/req)</sub> | **2,018**<br><sub>(154 MiB / 21.3% CPU · 504 MiB/s · 316.9 µs CPU/req)</sub> | **1,527**<br><sub>(183 MiB / 27.6% CPU · 382 MiB/s · 541.4 µs CPU/req)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/1 · plain | **1,053**<br><sub>(447 MiB / 19.2% CPU · 263 MiB/s · 548.5 µs CPU/req)</sub> | **1,725**<br><sub>(85 MiB / 22.6% CPU · 431 MiB/s · 393.0 µs CPU/req)</sub> | **2,034**<br><sub>(117 MiB / 28.1% CPU · 509 MiB/s · 413.9 µs CPU/req)</sub> | 🥇 **2,175**<br><sub>(154 MiB / 27.0% CPU · 544 MiB/s · 372.7 µs CPU/req)</sub> | **1,353**<br><sub>(178 MiB / 31.0% CPU · 338 MiB/s · 687.2 µs CPU/req)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/1 · plain | **508**<br><sub>(190 MiB / 32.1% CPU · 127 MiB/s · 1893.7 µs CPU/req)</sub> | 🥇 **841**<br><sub>(95 MiB / 28.5% CPU · 210 MiB/s · 1017.0 µs CPU/req)</sub> | **515**<br><sub>(peak 521 · 106 MiB / 29.0% CPU · 129 MiB/s · 1691.0 µs CPU/req)</sub> | **530**<br><sub>(159 MiB / 51.6% CPU · 132 MiB/s · 2920.7 µs CPU/req)</sub> | **256**<br><sub>(211 MiB / 29.4% CPU · 64 MiB/s · 3446.0 µs CPU/req)</sub> |
| 64 KiB | HTTP/2 · plain | HTTP/1 · plain | 🥇 **9,299**<br><sub>(455 MiB / 22.4% CPU · 581 MiB/s · 72.4 µs CPU/req)</sub> | **7,686**<br><sub>(73 MiB / 12.3% CPU · 480 MiB/s · 47.9 µs CPU/req)</sub> | **8,105**<br><sub>(105 MiB / 20.6% CPU · 507 MiB/s · 76.4 µs CPU/req)</sub> | **7,881**<br><sub>(135 MiB / 21.8% CPU · 493 MiB/s · 82.8 µs CPU/req)</sub> | **7,453**<br><sub>(187 MiB / 21.8% CPU · 466 MiB/s · 87.7 µs CPU/req)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/2 · plain | 🥇 **10,070**<br><sub>(394 MiB / 36.2% CPU · 629 MiB/s · 107.9 µs CPU/req)</sub> | *Not possible* | **6,370**<br><sub>(127 MiB / 34.9% CPU · 398 MiB/s · 164.5 µs CPU/req)</sub> | **6,797**<br><sub>(126 MiB / 34.7% CPU · 425 MiB/s · 153.1 µs CPU/req)</sub> | **7,814**<br><sub>(272 MiB / 47.4% CPU · 488 MiB/s · 181.9 µs CPU/req)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/2 · TLS | **2,166**<br><sub>(386 MiB / 15.5% CPU · 135 MiB/s · 214.1 µs CPU/req)</sub> | *Not possible* | 🥇 **3,441**<br><sub>(123 MiB / 26.2% CPU · 215 MiB/s · 228.1 µs CPU/req)</sub> | **3,436**<br><sub>(126 MiB / 24.7% CPU · 215 MiB/s · 215.7 µs CPU/req)</sub> | **2,101**<br><sub>(253 MiB / 20.7% CPU · 131 MiB/s · 296.1 µs CPU/req)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/2 · TLS | 🥇 **1,658**<br><sub>(222 MiB / 27.7% CPU · 104 MiB/s · 501.4 µs CPU/req)</sub> | *Not possible* | **1,338**<br><sub>(126 MiB / 27.5% CPU · 84 MiB/s · 616.2 µs CPU/req)</sub> | **1,558**<br><sub>(130 MiB / 45.1% CPU · 97 MiB/s · 868.5 µs CPU/req)</sub> | **1,003**<br><sub>(479 MiB / 24.2% CPU · 63 MiB/s · 722.3 µs CPU/req)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/1 · TLS | **1,232**<br><sub>(233 MiB / 29.2% CPU · 77 MiB/s · 710.0 µs CPU/req)</sub> | 🥇 **1,653**<br><sub>(101 MiB / 22.6% CPU · 103 MiB/s · 409.2 µs CPU/req)</sub> | **1,147**<br><sub>(129 MiB / 31.4% CPU · 72 MiB/s · 822.2 µs CPU/req)</sub> | **972**<br><sub>(133 MiB / 45.9% CPU · 61 MiB/s · 1416.6 µs CPU/req)</sub> | **550**<br><sub>(383 MiB / 25.7% CPU · 34 MiB/s · 1403.4 µs CPU/req)</sub> |
| 256 KiB | HTTP/2 · plain | HTTP/1 · plain | **1,124**<br><sub>(424 MiB / 12.3% CPU · 281 MiB/s · 329.5 µs CPU/req)</sub> | **2,862**<br><sub>(73 MiB / 17.9% CPU · 716 MiB/s · 187.6 µs CPU/req)</sub> | **2,852**<br><sub>(104 MiB / 24.2% CPU · 713 MiB/s · 254.1 µs CPU/req)</sub> | 🥇 **3,088**<br><sub>(145 MiB / 21.9% CPU · 772 MiB/s · 212.4 µs CPU/req)</sub> | **2,097**<br><sub>(210 MiB / 23.4% CPU · 524 MiB/s · 335.0 µs CPU/req)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/2 · plain | **1,355**<br><sub>(667 MiB / 28.7% CPU · 339 MiB/s · 634.9 µs CPU/req)</sub> | *Not possible* | 🥇 **1,522**<br><sub>(142 MiB / 29.7% CPU · 380 MiB/s · 585.4 µs CPU/req)</sub> | **1,015**<br><sub>(136 MiB / 22.8% CPU · 254 MiB/s · 673.2 µs CPU/req)</sub> | **1,080**<br><sub>(317 MiB / 29.9% CPU · 270 MiB/s · 830.7 µs CPU/req)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/2 · TLS | **1,070**<br><sub>(640 MiB / 26.4% CPU · 267 MiB/s · 741.4 µs CPU/req)</sub> | *Not possible* | 🥇 **1,205**<br><sub>(131 MiB / 31.0% CPU · 301 MiB/s · 770.9 µs CPU/req)</sub> | **1,028**<br><sub>(130 MiB / 24.9% CPU · 257 MiB/s · 725.9 µs CPU/req)</sub> | **919**<br><sub>(277 MiB / 31.2% CPU · 230 MiB/s · 1018.9 µs CPU/req)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/2 · TLS | **412**<br><sub>(499 MiB / 33.5% CPU · 103 MiB/s · 2440.4 µs CPU/req)</sub> | *Not possible* | 🥇 **528**<br><sub>(peak 652 · 122 MiB / 32.4% CPU · 132 MiB/s · 1841.0 µs CPU/req)</sub> | **412**<br><sub>(139 MiB / 50.1% CPU · 103 MiB/s · 3652.9 µs CPU/req)</sub> | **429**<br><sub>(558 MiB / 34.8% CPU · 107 MiB/s · 2432.3 µs CPU/req)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/1 · TLS | **300**<br><sub>(peak 316 · 216 MiB / 32.6% CPU · 75 MiB/s · 3270.5 µs CPU/req)</sub> | 🥇 **502**<br><sub>(peak 532 · 98 MiB / 26.1% CPU · 125 MiB/s · 1561.3 µs CPU/req)</sub> | **277**<br><sub>(peak 357 · 107 MiB / 25.9% CPU · 69 MiB/s · 2806.3 µs CPU/req)</sub> | **485**<br><sub>(152 MiB / 47.4% CPU · 121 MiB/s · 2931.4 µs CPU/req)</sub> | **295**<br><sub>(425 MiB / 30.5% CPU · 74 MiB/s · 3101.7 µs CPU/req)</sub> |

### Windows — POST 64 KiB request + 64 KiB response

Median of **3** repeats on `windows-latest` @ `482f5984`. Source: Actions [37210524513](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37210524513) (`compare-post`).

*Not possible:* **HAProxy** and **Envoy** columns are omitted (no official Windows port).

















| Client | Origin | TWP | nginx | YARP |
|---|---|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | 🥇 **5,918**<br><sub>(93 MiB / 44.6% CPU · 740 MiB/s · 301.5 µs CPU/req)</sub> | **392**<br><sub>(142 MiB / 24.9% CPU · 49 MiB/s · 2538.8 µs CPU/req)</sub> | **4,163**<br><sub>(138 MiB / 53.9% CPU · 520 MiB/s · 518.2 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **4,206**<br><sub>(216 MiB / 46.9% CPU · 526 MiB/s · 446.5 µs CPU/req)</sub> | **359**<br><sub>(147 MiB / 24.4% CPU · 45 MiB/s · 2722.1 µs CPU/req)</sub> | **4,053**<br><sub>(140 MiB / 47.7% CPU · 507 MiB/s · 471.0 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **2,848**<br><sub>(202 MiB / 46.7% CPU · 356 MiB/s · 656.4 µs CPU/req)</sub> | *Not possible (no QUIC)* | **1,628**<br><sub>(218 MiB / 48.1% CPU · 203 MiB/s · 1180.9 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | 🥇 **8,704**<br><sub>(211 MiB / 45.0% CPU · 1,088 MiB/s · 206.9 µs CPU/req)</sub> | **3,912**<br><sub>(132 MiB / 24.8% CPU · 489 MiB/s · 253.6 µs CPU/req)</sub> | **8,145**<br><sub>(120 MiB / 48.4% CPU · 1,018 MiB/s · 237.8 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | 🥇 **4,553**<br><sub>(169 MiB / 40.4% CPU · 569 MiB/s · 355.3 µs CPU/req)</sub> | *Not possible* | **3,588**<br><sub>(142 MiB / 46.4% CPU · 448 MiB/s · 517.7 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **3,570**<br><sub>(171 MiB / 39.1% CPU · 446 MiB/s · 437.9 µs CPU/req)</sub> | *Not possible* | **2,886**<br><sub>(157 MiB / 47.5% CPU · 361 MiB/s · 658.9 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | 🥇 **1,889**<br><sub>(190 MiB / 44.2% CPU · 236 MiB/s · 936.7 µs CPU/req)</sub> | *Not possible* | **1,135**<br><sub>(225 MiB / 47.8% CPU · 142 MiB/s · 1684.2 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **1,908**<br><sub>(192 MiB / 46.7% CPU · 239 MiB/s · 978.5 µs CPU/req)</sub> | *Not possible (no QUIC)* | **1,292**<br><sub>(219 MiB / 50.7% CPU · 161 MiB/s · 1570.3 µs CPU/req)</sub> |

TWP leads H2→H1 POST (about **1.1–1.3×** YARP) and H3 POST (about **1.0–1.1×** YARP). H2 TLS→H2 TLS POST sustain is about **1.2–1.4×** YARP.

### Linux — POST 64 KiB request + 64 KiB response

Median of **3** repeats @ `482f5984`. Source: Actions [37210524513](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37210524513) (`compare-post`).


| Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---:|---:|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | **10,424**<br><sub>(133 MiB / 39.9% CPU · 1,303 MiB/s · 153.0 µs CPU/req)</sub> | **7,771**<br><sub>(100 MiB / 48.2% CPU · 971 MiB/s · 248.1 µs CPU/req)</sub> | 🥇 **11,193**<br><sub>(88 MiB / 39.3% CPU · 1,399 MiB/s · 140.3 µs CPU/req)</sub> | **10,756**<br><sub>(133 MiB / 39.1% CPU · 1,344 MiB/s · 145.5 µs CPU/req)</sub> | **6,818**<br><sub>(170 MiB / 53.0% CPU · 852 MiB/s · 310.9 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | **3,766**<br><sub>(252 MiB / 47.6% CPU · 471 MiB/s · 506.1 µs CPU/req)</sub> | **3,294**<br><sub>(106 MiB / 46.1% CPU · 412 MiB/s · 560.2 µs CPU/req)</sub> | 🥇 **3,941**<br><sub>(91 MiB / 44.6% CPU · 493 MiB/s · 452.4 µs CPU/req)</sub> | **3,640**<br><sub>(peak 4,454 · 126 MiB / 42.0% CPU · 455 MiB/s · 461.1 µs CPU/req)</sub> | **3,380**<br><sub>(169 MiB / 50.2% CPU · 423 MiB/s · 593.7 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **3,008**<br><sub>(244 MiB / 45.2% CPU · 376 MiB/s · 601.7 µs CPU/req)</sub> | **968**<br><sub>(111 MiB / 42.4% CPU · 121 MiB/s · 1753.1 µs CPU/req)</sub> | **2,580**<br><sub>(93 MiB / 42.8% CPU · 323 MiB/s · 664.0 µs CPU/req)</sub> | **0**<br><sub>(130 MiB / 0.3% CPU)</sub> | **2,178**<br><sub>(248 MiB / 50.3% CPU · 272 MiB/s · 924.2 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | **5,843**<br><sub>(275 MiB / 44.1% CPU · 730 MiB/s · 302.2 µs CPU/req)</sub> | **4,768**<br><sub>(85 MiB / 45.0% CPU · 596 MiB/s · 377.3 µs CPU/req)</sub> | 🥇 **6,440**<br><sub>(74 MiB / 40.1% CPU · 805 MiB/s · 249.3 µs CPU/req)</sub> | **4,368**<br><sub>(peak 5,801 · 116 MiB / 44.4% CPU · 546 MiB/s · 406.7 µs CPU/req)</sub> | **5,532**<br><sub>(160 MiB / 47.9% CPU · 692 MiB/s · 346.5 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | 🥇 **3,960**<br><sub>(204 MiB / 39.8% CPU · 495 MiB/s · 401.9 µs CPU/req)</sub> | *Not possible* | **3,326**<br><sub>(93 MiB / 42.0% CPU · 416 MiB/s · 504.6 µs CPU/req)</sub> | **3,914**<br><sub>(136 MiB / 40.6% CPU · 489 MiB/s · 414.8 µs CPU/req)</sub> | **2,662**<br><sub>(189 MiB / 49.1% CPU · 333 MiB/s · 738.1 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **5,239**<br><sub>(214 MiB / 37.6% CPU · 655 MiB/s · 287.1 µs CPU/req)</sub> | *Not possible* | **4,683**<br><sub>(91 MiB / 37.9% CPU · 585 MiB/s · 323.5 µs CPU/req)</sub> | **5,172**<br><sub>(141 MiB / 39.4% CPU · 647 MiB/s · 304.5 µs CPU/req)</sub> | **3,632**<br><sub>(197 MiB / 47.1% CPU · 454 MiB/s · 518.6 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | 🥇 **1,989**<br><sub>(242 MiB / 44.9% CPU · 249 MiB/s · 902.9 µs CPU/req)</sub> | *Not possible* | **1,768**<br><sub>(96 MiB / 44.0% CPU · 221 MiB/s · 995.8 µs CPU/req)</sub> | **0**<br><sub>(130 MiB / 0.2% CPU)</sub> | **1,532**<br><sub>(253 MiB / 48.6% CPU · 191 MiB/s · 1270.1 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **2,230**<br><sub>(249 MiB / 46.5% CPU · 279 MiB/s · 834.7 µs CPU/req)</sub> | **872**<br><sub>(113 MiB / 52.1% CPU · 109 MiB/s · 2392.8 µs CPU/req)</sub> | **1,994**<br><sub>(96 MiB / 44.3% CPU · 249 MiB/s · 888.5 µs CPU/req)</sub> | **0**<br><sub>(132 MiB / 0.3% CPU)</sub> | **1,797**<br><sub>(276 MiB / 49.4% CPU · 225 MiB/s · 1099.6 µs CPU/req)</sub> |

Linux nginx H1/H2/H3 POST completed (nginx.org mainline). TWP÷YARP H1 ≈ **1.5×**; H2→H1 ≈ **1.2×**; H3 ≈ **1.1×**. TWP÷nginx H3 ≈ **6×**.

### macOS — POST 64 KiB request + 64 KiB response

Median of **3** repeats on `macos-15` (Apple Silicon M1, 3-core / 7 GB) @ `482f5984`. Source: Actions [37210524513](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37210524513) (`compare-post`).

| Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---:|---:|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | **2,466**<br><sub>(250 MiB / 21.9% CPU · 308 MiB/s · 267.0 µs CPU/req)</sub> | 🥇 **3,778**<br><sub>(88 MiB / 20.1% CPU · 472 MiB/s · 159.7 µs CPU/req)</sub> | **3,778**<br><sub>(120 MiB / 26.9% CPU · 472 MiB/s · 213.4 µs CPU/req)</sub> | **3,534**<br><sub>(131 MiB / 25.9% CPU · 442 MiB/s · 219.9 µs CPU/req)</sub> | **1,959**<br><sub>(212 MiB / 30.1% CPU · 245 MiB/s · 461.2 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | **2,035**<br><sub>(550 MiB / 24.9% CPU · 254 MiB/s · 367.7 µs CPU/req)</sub> | **2,606**<br><sub>(99 MiB / 18.9% CPU · 326 MiB/s · 218.0 µs CPU/req)</sub> | 🥇 **2,857**<br><sub>(124 MiB / 29.5% CPU · 357 MiB/s · 309.7 µs CPU/req)</sub> | **1,932**<br><sub>(peak 2,713 · 123 MiB / 19.4% CPU · 241 MiB/s · 300.7 µs CPU/req)</sub> | **1,679**<br><sub>(186 MiB / 24.4% CPU · 210 MiB/s · 435.2 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | **1,402**<br><sub>(408 MiB / 28.3% CPU · 175 MiB/s · 605.3 µs CPU/req)</sub> | 🥇 **1,842**<br><sub>(97 MiB / 24.3% CPU · 230 MiB/s · 395.6 µs CPU/req)</sub> | **961**<br><sub>(102 MiB / 24.2% CPU · 120 MiB/s · 756.9 µs CPU/req)</sub> | **1,122**<br><sub>(130 MiB / 42.4% CPU · 140 MiB/s · 1133.2 µs CPU/req)</sub> | **1,330**<br><sub>(469 MiB / 30.2% CPU · 166 MiB/s · 682.5 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | **2,022**<br><sub>(528 MiB / 19.7% CPU · 253 MiB/s · 291.8 µs CPU/req)</sub> | 🥇 **6,081**<br><sub>(86 MiB / 17.7% CPU · 760 MiB/s · 87.1 µs CPU/req)</sub> | **4,151**<br><sub>(115 MiB / 22.8% CPU · 519 MiB/s · 164.6 µs CPU/req)</sub> | **4,234**<br><sub>(136 MiB / 19.5% CPU · 529 MiB/s · 138.2 µs CPU/req)</sub> | **2,082**<br><sub>(215 MiB / 19.1% CPU · 260 MiB/s · 274.7 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | **2,391**<br><sub>(413 MiB / 17.3% CPU · 299 MiB/s · 216.5 µs CPU/req)</sub> | *Not possible* | 🥇 **2,970**<br><sub>(128 MiB / 28.5% CPU · 371 MiB/s · 287.9 µs CPU/req)</sub> | **2,717**<br><sub>(128 MiB / 23.4% CPU · 340 MiB/s · 258.1 µs CPU/req)</sub> | **997**<br><sub>(230 MiB / 16.8% CPU · 125 MiB/s · 504.6 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | **1,866**<br><sub>(449 MiB / 24.3% CPU · 233 MiB/s · 390.2 µs CPU/req)</sub> | *Not possible* | 🥇 **2,847**<br><sub>(130 MiB / 32.1% CPU · 356 MiB/s · 338.2 µs CPU/req)</sub> | **2,722**<br><sub>(128 MiB / 27.2% CPU · 340 MiB/s · 299.5 µs CPU/req)</sub> | **1,261**<br><sub>(221 MiB / 25.1% CPU · 158 MiB/s · 595.9 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | **663**<br><sub>(241 MiB / 26.9% CPU · 83 MiB/s · 1218.0 µs CPU/req)</sub> | *Not possible* | 🥇 **676**<br><sub>(123 MiB / 32.5% CPU · 84 MiB/s · 1441.2 µs CPU/req)</sub> | **603**<br><sub>(137 MiB / 45.2% CPU · 75 MiB/s · 2249.0 µs CPU/req)</sub> | **610**<br><sub>(456 MiB / 26.8% CPU · 76 MiB/s · 1319.1 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | **1,048**<br><sub>(309 MiB / 30.1% CPU · 131 MiB/s · 860.6 µs CPU/req)</sub> | 🥇 **1,060**<br><sub>(102 MiB / 20.8% CPU · 133 MiB/s · 589.1 µs CPU/req)</sub> | **867**<br><sub>(124 MiB / 30.4% CPU · 108 MiB/s · 1052.0 µs CPU/req)</sub> | **474**<br><sub>(140 MiB / 28.5% CPU · 59 MiB/s · 1803.8 µs CPU/req)</sub> | **983**<br><sub>(420 MiB / 33.0% CPU · 123 MiB/s · 1005.8 µs CPU/req)</sub> |

### Windows — lossy / high-RTT (H2 HOL / H3 loss)

Userspace **5 ms** one-way delay + **1%** TCP connection stall (H1/H2); UDP is **loss% only** (no per-datagram delay; MsQuic-safe) + **1%** datagram drop (H3); **64 KiB** GET. Median of **3** repeats on `windows-latest` @ `482f5984` — [37210527573](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37210527573) (`compare-lossy`).

*Not possible:* **HAProxy** and **Envoy** columns are omitted (no official Windows port).


| Client | Origin | TWP | nginx | YARP |
|---|---|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | 🥇 **664**<br><sub>(85 MiB / 4.2% CPU · 41 MiB/s · 252.5 µs CPU/req)</sub> | **639**<br><sub>(143 MiB / 19.3% CPU · 40 MiB/s · 1208.4 µs CPU/req)</sub> | **661**<br><sub>(122 MiB / 5.6% CPU · 41 MiB/s · 337.6 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **248**<br><sub>(150 MiB / 2.6% CPU · 15 MiB/s · 418.4 µs CPU/req)</sub> | **245**<br><sub>(143 MiB / 6.3% CPU · 15 MiB/s · 1033.5 µs CPU/req)</sub> | **234**<br><sub>(119 MiB / 2.5% CPU · 15 MiB/s · 430.0 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | **836**<br><sub>(123 MiB / 18.2% CPU · 52 MiB/s · 869.3 µs CPU/req)</sub> | *Not possible (no QUIC)* | 🥇 **841**<br><sub>(174 MiB / 31.1% CPU · 53 MiB/s · 1478.5 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | 🥇 **253**<br><sub>(153 MiB / 2.7% CPU · 16 MiB/s · 425.0 µs CPU/req)</sub> | **250**<br><sub>(128 MiB / 2.4% CPU · 16 MiB/s · 383.0 µs CPU/req)</sub> | **231**<br><sub>(106 MiB / 2.4% CPU · 14 MiB/s · 414.9 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | **220**<br><sub>(122 MiB / 2.1% CPU · 14 MiB/s · 382.5 µs CPU/req)</sub> | *Not possible* | 🥇 **234**<br><sub>(129 MiB / 3.4% CPU · 15 MiB/s · 580.7 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **231**<br><sub>(131 MiB / 2.9% CPU · 14 MiB/s · 499.6 µs CPU/req)</sub> | *Not possible* | **229**<br><sub>(114 MiB / 4.7% CPU · 14 MiB/s · 827.6 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | **893**<br><sub>(116 MiB / 20.6% CPU · 56 MiB/s · 922.1 µs CPU/req)</sub> | *Not possible* | 🥇 **912**<br><sub>(185 MiB / 31.2% CPU · 57 MiB/s · 1368.3 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **851**<br><sub>(131 MiB / 20.1% CPU · 53 MiB/s · 945.8 µs CPU/req)</sub> | *Not possible (no QUIC)* | **844**<br><sub>(177 MiB / 31.1% CPU · 53 MiB/s · 1474.8 µs CPU/req)</sub> |

H1, H2, and H3 loss sustain are in the table. Prefer the ratio columns.

### Linux — lossy / high-RTT (H2 HOL / H3 loss)

Median of **3** repeats @ `482f5984`. Source: [37210527573](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37210527573) (`compare-lossy`; lossy H3 uses `quic-http3`, UDP drop-only).


| Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---:|---:|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | **1,200**<br><sub>(121 MiB / 13.1% CPU · 75 MiB/s · 437.9 µs CPU/req)</sub> | 🥇 **1,209**<br><sub>(99 MiB / 12.7% CPU · 76 MiB/s · 420.8 µs CPU/req)</sub> | **1,205**<br><sub>(84 MiB / 6.8% CPU · 75 MiB/s · 225.8 µs CPU/req)</sub> | **1,195**<br><sub>(129 MiB / 9.1% CPU · 75 MiB/s · 304.2 µs CPU/req)</sub> | **1,197**<br><sub>(155 MiB / 16.4% CPU · 75 MiB/s · 548.8 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **548**<br><sub>(189 MiB / 11.7% CPU · 34 MiB/s · 855.4 µs CPU/req)</sub> | **507**<br><sub>(99 MiB / 5.1% CPU · 32 MiB/s · 405.4 µs CPU/req)</sub> | **511**<br><sub>(84 MiB / 4.1% CPU · 32 MiB/s · 320.9 µs CPU/req)</sub> | **496**<br><sub>(130 MiB / 4.9% CPU · 31 MiB/s · 393.7 µs CPU/req)</sub> | **474**<br><sub>(143 MiB / 12.3% CPU · 30 MiB/s · 1033.9 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **1,195**<br><sub>(163 MiB / 24.2% CPU · 75 MiB/s · 810.0 µs CPU/req)</sub> | **1,096**<br><sub>(113 MiB / 23.5% CPU · 68 MiB/s · 857.1 µs CPU/req)</sub> | **978**<br><sub>(101 MiB / 18.8% CPU · 61 MiB/s · 769.4 µs CPU/req)</sub> | **1,022**<br><sub>(138 MiB / 25.8% CPU · 64 MiB/s · 1007.9 µs CPU/req)</sub> | **1,144**<br><sub>(231 MiB / 31.2% CPU · 72 MiB/s · 1090.8 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | 🥇 **619**<br><sub>(186 MiB / 10.0% CPU · 39 MiB/s · 646.0 µs CPU/req)</sub> | **552**<br><sub>(78 MiB / 3.4% CPU · 34 MiB/s · 248.0 µs CPU/req)</sub> | **533**<br><sub>(71 MiB / 2.4% CPU · 33 MiB/s · 177.1 µs CPU/req)</sub> | **521**<br><sub>(122 MiB / 3.1% CPU · 33 MiB/s · 241.7 µs CPU/req)</sub> | **474**<br><sub>(133 MiB / 8.5% CPU · 30 MiB/s · 720.1 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | **397**<br><sub>(148 MiB / 7.7% CPU · 25 MiB/s · 771.4 µs CPU/req)</sub> | *Not possible* | 🥇 **532**<br><sub>(86 MiB / 3.9% CPU · 33 MiB/s · 291.9 µs CPU/req)</sub> | **506**<br><sub>(130 MiB / 4.8% CPU · 32 MiB/s · 375.7 µs CPU/req)</sub> | **415**<br><sub>(148 MiB / 11.7% CPU · 26 MiB/s · 1125.1 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | **286**<br><sub>(161 MiB / 4.0% CPU · 18 MiB/s · 561.6 µs CPU/req)</sub> | *Not possible* | 🥇 **552**<br><sub>(88 MiB / 2.6% CPU · 34 MiB/s · 187.7 µs CPU/req)</sub> | **512**<br><sub>(132 MiB / 2.9% CPU · 32 MiB/s · 224.3 µs CPU/req)</sub> | **389**<br><sub>(148 MiB / 6.5% CPU · 24 MiB/s · 669.6 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | 🥇 **1,146**<br><sub>(160 MiB / 25.7% CPU · 72 MiB/s · 896.6 µs CPU/req)</sub> | *Not possible* | **921**<br><sub>(105 MiB / 20.5% CPU · 58 MiB/s · 891.9 µs CPU/req)</sub> | **957**<br><sub>(141 MiB / 26.4% CPU · 60 MiB/s · 1103.9 µs CPU/req)</sub> | **1,055**<br><sub>(229 MiB / 32.7% CPU · 66 MiB/s · 1238.8 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **1,102**<br><sub>(179 MiB / 26.1% CPU · 69 MiB/s · 945.9 µs CPU/req)</sub> | **1,015**<br><sub>(117 MiB / 24.6% CPU · 63 MiB/s · 967.1 µs CPU/req)</sub> | **947**<br><sub>(103 MiB / 20.3% CPU · 59 MiB/s · 857.0 µs CPU/req)</sub> | **996**<br><sub>(137 MiB / 25.9% CPU · 62 MiB/s · 1042.7 µs CPU/req)</sub> | **1,048**<br><sub>(219 MiB / 32.9% CPU · 66 MiB/s · 1256.0 µs CPU/req)</sub> |

TWP H2 HOL ≫ YARP (~**7.8×**). H3 TWP÷YARP ≈ **1.07×**.

### macOS — lossy / high-RTT (H2 HOL / H3 loss)

Median of **3** repeats on `macos-15` (Apple Silicon M1, 3-core / 7 GB) @ `482f5984`. Source: [37210527573](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37210527573) (`compare-lossy`; lossy H3 uses `quic-http3`, UDP drop-only).

| Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---:|---:|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | **366**<br><sub>(233 MiB / 4.3% CPU · 23 MiB/s · 351.7 µs CPU/req)</sub> | 🥇 **475**<br><sub>(85 MiB / 1.6% CPU · 30 MiB/s · 99.7 µs CPU/req)</sub> | **399**<br><sub>(115 MiB / 1.6% CPU · 25 MiB/s · 124.0 µs CPU/req)</sub> | **443**<br><sub>(129 MiB / 2.9% CPU · 28 MiB/s · 195.1 µs CPU/req)</sub> | **393**<br><sub>(226 MiB / 7.3% CPU · 25 MiB/s · 556.6 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **161**<br><sub>(414 MiB / 4.5% CPU · 10 MiB/s · 845.7 µs CPU/req)</sub> | **143**<br><sub>(85 MiB / 0.8% CPU · 9.0 MiB/s · 156.9 µs CPU/req)</sub> | **144**<br><sub>(116 MiB / 1.4% CPU · 9.0 MiB/s · 286.4 µs CPU/req)</sub> | **120**<br><sub>(130 MiB / 1.2% CPU · 7.5 MiB/s · 290.2 µs CPU/req)</sub> | **112**<br><sub>(230 MiB / 2.2% CPU · 7.0 MiB/s · 601.6 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | **1,052**<br><sub>(267 MiB / 19.7% CPU · 66 MiB/s · 561.2 µs CPU/req)</sub> | **203**<br><sub>(peak 358 · 81 MiB / 3.7% CPU · 13 MiB/s · 544.5 µs CPU/req)</sub> | **728**<br><sub>(123 MiB / 15.0% CPU · 46 MiB/s · 618.0 µs CPU/req)</sub> | 🥇 **1,216**<br><sub>(132 MiB / 28.4% CPU · 76 MiB/s · 700.2 µs CPU/req)</sub> | **960**<br><sub>(408 MiB / 22.3% CPU · 60 MiB/s · 698.2 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | **144**<br><sub>(357 MiB / 2.7% CPU · 9.0 MiB/s · 571.6 µs CPU/req)</sub> | **136**<br><sub>(73 MiB / 0.4% CPU · 8.5 MiB/s · 92.9 µs CPU/req)</sub> | **143**<br><sub>(104 MiB / 0.7% CPU · 8.9 MiB/s · 148.8 µs CPU/req)</sub> | **128**<br><sub>(129 MiB / 0.6% CPU · 8.0 MiB/s · 143.1 µs CPU/req)</sub> | 🥇 **144**<br><sub>(144 MiB / 2.0% CPU · 9.0 MiB/s · 423.2 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | **136**<br><sub>(297 MiB / 1.9% CPU · 8.5 MiB/s · 431.1 µs CPU/req)</sub> | *Not possible* | **136**<br><sub>(121 MiB / 0.8% CPU · 8.5 MiB/s · 181.0 µs CPU/req)</sub> | **121**<br><sub>(123 MiB / 1.4% CPU · 7.6 MiB/s · 353.4 µs CPU/req)</sub> | 🥇 **159**<br><sub>(277 MiB / 7.8% CPU · 9.9 MiB/s · 1470.7 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | **176**<br><sub>(313 MiB / 7.3% CPU · 11 MiB/s · 1238.9 µs CPU/req)</sub> | *Not possible* | 🥇 **179**<br><sub>(119 MiB / 3.1% CPU · 11 MiB/s · 521.8 µs CPU/req)</sub> | **151**<br><sub>(124 MiB / 2.8% CPU · 9.5 MiB/s · 556.8 µs CPU/req)</sub> | **168**<br><sub>(247 MiB / 6.2% CPU · 10 MiB/s · 1107.3 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | 🥇 **968**<br><sub>(199 MiB / 24.5% CPU · 60 MiB/s · 758.5 µs CPU/req)</sub> | *Not possible* | **442**<br><sub>(132 MiB / 16.2% CPU · 28 MiB/s · 1099.9 µs CPU/req)</sub> | **768**<br><sub>(131 MiB / 27.4% CPU · 48 MiB/s · 1071.2 µs CPU/req)</sub> | **477**<br><sub>(433 MiB / 19.2% CPU · 30 MiB/s · 1206.8 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | **880**<br><sub>(210 MiB / 21.1% CPU · 55 MiB/s · 718.5 µs CPU/req)</sub> | 🥇 **1,004**<br><sub>(99 MiB / 13.5% CPU · 63 MiB/s · 402.3 µs CPU/req)</sub> | **436**<br><sub>(126 MiB / 14.2% CPU · 27 MiB/s · 976.4 µs CPU/req)</sub> | **952**<br><sub>(132 MiB / 27.3% CPU · 60 MiB/s · 859.7 µs CPU/req)</sub> | **545**<br><sub>(356 MiB / 22.6% CPU · 34 MiB/s · 1245.9 µs CPU/req)</sub> |

### Architecture-sensitive

These runs isolate slow app readers, origin-early response, HTTP/2 duplex, and WebSocket echo. See [TWP vs YARP IO model](Performance-Profiling#user-content-twp-vs-yarp-io-model). Laptop 1-rep numbers are on [Performance Local Lab](Performance-Local-Lab#user-content-architecture-sensitive).

Median of **3** repeats on matched 4 vCPU / 16 GiB runners @ `482f5984` ([37210530448](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37210530448)). Slow consumer = 256 KiB GET, 16 KiB read + 8 ms sleep. Early response = 64 KiB POST, origin writes after 8 KiB. Duplex HTTP/2 = overlapping 64 KiB POST on H2 TLS↔H2 TLS. WebSocket row = H1 Upgrade echo round-trips/sec (not RFC 8441; see WebSocket RFC 8441 table below).

Lossy-link runs (slow **network**) are already published above; they are not a slow **app** reader.

#### Windows


*Not possible:* **HAProxy** and **Envoy** columns are omitted (no official Windows port).

| Scenario | Client | Origin | TWP | nginx | YARP |
|---|---|---|---:|---:|---:|
| Slow consumer (256 KiB GET, throttled client read) | HTTP/1 · TLS | HTTP/1 · plain | **250**<br><sub>(95 MiB / 3.0% CPU · 63 MiB/s · 479.6 µs CPU/req)</sub> | **242**<br><sub>(144 MiB / 16.1% CPU · 60 MiB/s · 2663.9 µs CPU/req)</sub> | 🥇 **256**<br><sub>(110 MiB / 3.0% CPU · 64 MiB/s · 477.1 µs CPU/req)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · TLS | HTTP/1 · plain | **248**<br><sub>(143 MiB / 6.0% CPU · 62 MiB/s · 970.5 µs CPU/req)</sub> | **158**<br><sub>(142 MiB / 24.8% CPU · 40 MiB/s · 6268.8 µs CPU/req)</sub> | 🥇 **248**<br><sub>(121 MiB / 6.7% CPU · 62 MiB/s · 1086.4 µs CPU/req)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/3 · QUIC | HTTP/1 · plain | **64**<br><sub>(108 MiB / 46.1% CPU · 16 MiB/s · 28974.8 µs CPU/req)</sub> | *Not possible (no QUIC)* | 🥇 **86**<br><sub>(153 MiB / 47.5% CPU · 22 MiB/s · 21972.2 µs CPU/req)</sub> |
| Early response (origin writes after first request chunk) | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **4,529**<br><sub>(101 MiB / 35.5% CPU · 566 MiB/s · 313.2 µs CPU/req)</sub> | **341**<br><sub>(143 MiB / 24.8% CPU · 43 MiB/s · 2903.3 µs CPU/req)</sub> | **4,090**<br><sub>(139 MiB / 56.7% CPU · 511 MiB/s · 554.5 µs CPU/req)</sub> |
| Early response (origin writes after first request chunk) | HTTP/2 · TLS | HTTP/1 · plain | **5,802**<br><sub>(213 MiB / 49.5% CPU · 725 MiB/s · 341.1 µs CPU/req)</sub> | **653**<br><sub>(146 MiB / 25.0% CPU · 82 MiB/s · 1530.0 µs CPU/req)</sub> | 🥇 **7,528**<br><sub>(138 MiB / 46.5% CPU · 941 MiB/s · 247.2 µs CPU/req)</sub> |
| Early response (origin writes after first request chunk) | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **1,677**<br><sub>(163 MiB / 44.8% CPU · 210 MiB/s · 1068.6 µs CPU/req)</sub> | *Not possible (no QUIC)* | **1,013**<br><sub>(194 MiB / 49.4% CPU · 127 MiB/s · 1951.5 µs CPU/req)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · plain | HTTP/1 · plain | **248**<br><sub>(132 MiB / 3.4% CPU · 62 MiB/s · 555.7 µs CPU/req)</sub> | **248**<br><sub>(128 MiB / 7.9% CPU · 62 MiB/s · 1271.4 µs CPU/req)</sub> | 🥇 **248**<br><sub>(110 MiB / 3.8% CPU · 62 MiB/s · 616.9 µs CPU/req)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · TLS | HTTP/2 · TLS | **243**<br><sub>(160 MiB / 8.9% CPU · 61 MiB/s · 1468.3 µs CPU/req)</sub> | *Not possible* | 🥇 **248**<br><sub>(146 MiB / 8.9% CPU · 62 MiB/s · 1431.9 µs CPU/req)</sub> |
| Early response (origin writes after first request chunk) | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **3,292**<br><sub>(196 MiB / 41.2% CPU · 412 MiB/s · 500.1 µs CPU/req)</sub> | *Not possible* | **2,707**<br><sub>(162 MiB / 54.6% CPU · 338 MiB/s · 806.2 µs CPU/req)</sub> |
| Duplex (both directions live) | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **6,092**<br><sub>(191 MiB / 40.2% CPU · 762 MiB/s · 263.8 µs CPU/req)</sub> | *Not possible* | **4,571**<br><sub>(172 MiB / 53.6% CPU · 571 MiB/s · 468.7 µs CPU/req)</sub> |
| Duplex (WebSocket / H1 Upgrade) | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **25,553**<br><sub>(97 MiB / 42.8% CPU · 67.0 µs CPU/req)</sub> | **12,474**<br><sub>(143 MiB / 24.7% CPU · 79.1 µs CPU/req)</sub> | **23,549**<br><sub>(89 MiB / 42.0% CPU · 71.3 µs CPU/req)</sub> |

#### Linux


| Scenario | Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---|---:|---:|---:|---:|---:|
| Slow consumer (256 KiB GET, throttled client read) | HTTP/1 · TLS | HTTP/1 · plain | **466**<br><sub>(118 MiB / 6.5% CPU · 117 MiB/s · 559.5 µs CPU/req)</sub> | **424**<br><sub>(101 MiB / 5.9% CPU · 106 MiB/s · 555.1 µs CPU/req)</sub> | **472**<br><sub>(85 MiB / 3.6% CPU · 118 MiB/s · 303.3 µs CPU/req)</sub> | 🥇 **472**<br><sub>(139 MiB / 5.1% CPU · 118 MiB/s · 431.1 µs CPU/req)</sub> | **405**<br><sub>(147 MiB / 9.9% CPU · 101 MiB/s · 974.1 µs CPU/req)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · TLS | HTTP/1 · plain | **477**<br><sub>(167 MiB / 15.2% CPU · 119 MiB/s · 1269.6 µs CPU/req)</sub> | **476**<br><sub>(101 MiB / 10.1% CPU · 119 MiB/s · 848.8 µs CPU/req)</sub> | 🥇 **479**<br><sub>(83 MiB / 7.0% CPU · 120 MiB/s · 581.8 µs CPU/req)</sub> | **478**<br><sub>(143 MiB / 6.9% CPU · 120 MiB/s · 575.4 µs CPU/req)</sub> | **474**<br><sub>(158 MiB / 16.6% CPU · 118 MiB/s · 1401.7 µs CPU/req)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/3 · QUIC | HTTP/1 · plain | **462**<br><sub>(147 MiB / 48.6% CPU · 115 MiB/s · 4208.8 µs CPU/req)</sub> | **462**<br><sub>(112 MiB / 26.6% CPU · 116 MiB/s · 2300.3 µs CPU/req)</sub> | 🥇 **472**<br><sub>(92 MiB / 13.7% CPU · 118 MiB/s · 1158.8 µs CPU/req)</sub> | **461**<br><sub>(150 MiB / 24.9% CPU · 115 MiB/s · 2160.1 µs CPU/req)</sub> | **454**<br><sub>(199 MiB / 50.6% CPU · 114 MiB/s · 4456.3 µs CPU/req)</sub> |
| Early response (origin writes after first request chunk) | HTTP/1 · TLS | HTTP/1 · plain | **9,667**<br><sub>(132 MiB / 43.1% CPU · 1,208 MiB/s · 178.2 µs CPU/req)</sub> | **7,106**<br><sub>(peak 7,239 · 101 MiB / 49.9% CPU · 888 MiB/s · 281.1 µs CPU/req)</sub> | 🥇 **10,897**<br><sub>(90 MiB / 42.9% CPU · 1,362 MiB/s · 157.6 µs CPU/req)</sub> | **0**<br><sub>(peak 64 · 130 MiB / 1.8% CPU)</sub> | **6,468**<br><sub>(175 MiB / 54.2% CPU · 809 MiB/s · 335.0 µs CPU/req)</sub> |
| Early response (origin writes after first request chunk) | HTTP/2 · TLS | HTTP/1 · plain | **3,036**<br><sub>(272 MiB / 52.8% CPU · 379 MiB/s · 695.4 µs CPU/req)</sub> | **3,219**<br><sub>(106 MiB / 45.4% CPU · 402 MiB/s · 564.0 µs CPU/req)</sub> | 🥇 **3,862**<br><sub>(91 MiB / 43.6% CPU · 483 MiB/s · 451.3 µs CPU/req)</sub> | **0**<br><sub>(peak 3,872 · 140 MiB / 42.3% CPU)</sub> | **3,306**<br><sub>(169 MiB / 50.1% CPU · 413 MiB/s · 606.7 µs CPU/req)</sub> |
| Early response (origin writes after first request chunk) | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **3,355**<br><sub>(254 MiB / 45.1% CPU · 419 MiB/s · 537.7 µs CPU/req)</sub> | **1,554**<br><sub>(116 MiB / 29.2% CPU · 194 MiB/s · 751.1 µs CPU/req)</sub> | **3,056**<br><sub>(96 MiB / 37.6% CPU · 382 MiB/s · 491.9 µs CPU/req)</sub> | **0**<br><sub>(127 MiB / 0.1% CPU)</sub> | **2,201**<br><sub>(287 MiB / 48.0% CPU · 275 MiB/s · 872.2 µs CPU/req)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · plain | HTTP/1 · plain | 🥇 **480**<br><sub>(157 MiB / 17.2% CPU · 120 MiB/s · 1434.5 µs CPU/req)</sub> | **478**<br><sub>(77 MiB / 14.0% CPU · 119 MiB/s · 1173.6 µs CPU/req)</sub> | **477**<br><sub>(70 MiB / 7.3% CPU · 119 MiB/s · 613.8 µs CPU/req)</sub> | **472**<br><sub>(134 MiB / 7.3% CPU · 118 MiB/s · 615.9 µs CPU/req)</sub> | **478**<br><sub>(141 MiB / 16.6% CPU · 119 MiB/s · 1393.1 µs CPU/req)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · TLS | HTTP/2 · TLS | **463**<br><sub>(189 MiB / 23.9% CPU · 116 MiB/s · 2065.6 µs CPU/req)</sub> | *Not possible* | **469**<br><sub>(95 MiB / 19.9% CPU · 117 MiB/s · 1696.7 µs CPU/req)</sub> | 🥇 **469**<br><sub>(140 MiB / 19.0% CPU · 117 MiB/s · 1621.1 µs CPU/req)</sub> | **467**<br><sub>(170 MiB / 32.2% CPU · 117 MiB/s · 2764.7 µs CPU/req)</sub> |
| Early response (origin writes after first request chunk) | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **2,854**<br><sub>(228 MiB / 41.6% CPU · 357 MiB/s · 582.5 µs CPU/req)</sub> | *Not possible* | **2,039**<br><sub>(93 MiB / 34.4% CPU · 255 MiB/s · 674.8 µs CPU/req)</sub> | **0**<br><sub>(peak 2,457 · 142 MiB / 35.5% CPU)</sub> | **1,955**<br><sub>(201 MiB / 53.9% CPU · 244 MiB/s · 1102.0 µs CPU/req)</sub> |
| Duplex (both directions live) | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **4,025**<br><sub>(227 MiB / 38.7% CPU · 503 MiB/s · 384.5 µs CPU/req)</sub> | *Not possible* | **2,873**<br><sub>(93 MiB / 33.9% CPU · 359 MiB/s · 471.3 µs CPU/req)</sub> | **0**<br><sub>(peak 3,222 · 144 MiB / 36.0% CPU)</sub> | **2,870**<br><sub>(214 MiB / 54.2% CPU · 359 MiB/s · 755.0 µs CPU/req)</sub> |
| Duplex (WebSocket / H1 Upgrade) | HTTP/1 · TLS | HTTP/1 · plain | **29,881**<br><sub>(126 MiB / 43.6% CPU · 58.4 µs CPU/req)</sub> | 🥇 **33,514**<br><sub>(99 MiB / 36.1% CPU · 43.1 µs CPU/req)</sub> | **32,603**<br><sub>(83 MiB / 39.0% CPU · 47.8 µs CPU/req)</sub> | **31,792**<br><sub>(127 MiB / 39.8% CPU · 50.1 µs CPU/req)</sub> | **27,275**<br><sub>(126 MiB / 44.1% CPU · 64.7 µs CPU/req)</sub> |

#### macOS

Median of **3** repeats on `macos-15` (Apple Silicon M1, 3-core / 7 GB) @ `482f5984`. Source: Actions [37210530448](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37210530448) (`compare-arch`).

| Scenario | Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---|---:|---:|---:|---:|---:|
| Slow consumer (256 KiB GET, throttled client read) | HTTP/1 · TLS | HTTP/1 · plain | **96**<br><sub>(246 MiB / 2.4% CPU · 24 MiB/s · 748.4 µs CPU/req)</sub> | **103**<br><sub>(84 MiB / 1.0% CPU · 26 MiB/s · 291.8 µs CPU/req)</sub> | 🥇 **108**<br><sub>(111 MiB / 1.1% CPU · 27 MiB/s · 306.9 µs CPU/req)</sub> | **90**<br><sub>(150 MiB / 1.3% CPU · 23 MiB/s · 438.5 µs CPU/req)</sub> | **88**<br><sub>(205 MiB / 2.9% CPU · 22 MiB/s · 984.1 µs CPU/req)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · TLS | HTTP/1 · plain | **96**<br><sub>(370 MiB / 4.1% CPU · 24 MiB/s · 1299.2 µs CPU/req)</sub> | **96**<br><sub>(84 MiB / 1.8% CPU · 24 MiB/s · 547.4 µs CPU/req)</sub> | 🥇 **112**<br><sub>(114 MiB / 2.2% CPU · 28 MiB/s · 584.5 µs CPU/req)</sub> | **88**<br><sub>(153 MiB / 1.9% CPU · 22 MiB/s · 667.0 µs CPU/req)</sub> | **88**<br><sub>(221 MiB / 4.3% CPU · 22 MiB/s · 1462.4 µs CPU/req)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/3 · QUIC | HTTP/1 · plain | **71**<br><sub>(132 MiB / 19.2% CPU · 18 MiB/s · 8111.0 µs CPU/req)</sub> | **104**<br><sub>(94 MiB / 4.6% CPU · 26 MiB/s · 1332.4 µs CPU/req)</sub> | 🥇 **120**<br><sub>(110 MiB / 12.6% CPU · 30 MiB/s · 3163.0 µs CPU/req)</sub> | **88**<br><sub>(149 MiB / 10.8% CPU · 22 MiB/s · 3679.2 µs CPU/req)</sub> | **113**<br><sub>(333 MiB / 21.1% CPU · 28 MiB/s · 5597.2 µs CPU/req)</sub> |
| Early response (origin writes after first request chunk) | HTTP/1 · TLS | HTTP/1 · plain | **4,028**<br><sub>(238 MiB / 25.5% CPU · 504 MiB/s · 190.1 µs CPU/req)</sub> | **2,920**<br><sub>(peak 3,205 · 85 MiB / 17.1% CPU · 365 MiB/s · 176.1 µs CPU/req)</sub> | 🥇 **4,105**<br><sub>(120 MiB / 29.0% CPU · 513 MiB/s · 211.7 µs CPU/req)</sub> | **0**<br><sub>(peak 3,376 · 134 MiB / 26.5% CPU)</sub> | **1,911**<br><sub>(203 MiB / 28.4% CPU · 239 MiB/s · 446.2 µs CPU/req)</sub> |
| Early response (origin writes after first request chunk) | HTTP/2 · TLS | HTTP/1 · plain | **1,631**<br><sub>(514 MiB / 30.5% CPU · 204 MiB/s · 561.6 µs CPU/req)</sub> | **2,380**<br><sub>(peak 3,110 · 98 MiB / 16.4% CPU · 297 MiB/s · 207.1 µs CPU/req)</sub> | 🥇 **3,369**<br><sub>(122 MiB / 27.7% CPU · 421 MiB/s · 247.0 µs CPU/req)</sub> | **0**<br><sub>(peak 3,882 · 144 MiB / 27.4% CPU)</sub> | **1,801**<br><sub>(192 MiB / 20.8% CPU · 225 MiB/s · 346.2 µs CPU/req)</sub> |
| Early response (origin writes after first request chunk) | HTTP/3 · QUIC | HTTP/1 · plain | **744**<br><sub>(255 MiB / 26.4% CPU · 93 MiB/s · 1065.8 µs CPU/req)</sub> | 🥇 **1,512**<br><sub>(100 MiB / 22.1% CPU · 189 MiB/s · 438.4 µs CPU/req)</sub> | **0**<br><sub>(65 MiB)</sub> | **542**<br><sub>(125 MiB / 30.7% CPU · 68 MiB/s · 1699.6 µs CPU/req)</sub> | **408**<br><sub>(436 MiB / 23.6% CPU · 51 MiB/s · 1734.3 µs CPU/req)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · plain | HTTP/1 · plain | **96**<br><sub>(208 MiB / 2.1% CPU · 24 MiB/s · 651.4 µs CPU/req)</sub> | 🥇 **120**<br><sub>(72 MiB / 1.9% CPU · 30 MiB/s · 478.3 µs CPU/req)</sub> | **104**<br><sub>(99 MiB / 1.2% CPU · 26 MiB/s · 349.4 µs CPU/req)</sub> | **81**<br><sub>(145 MiB / 1.0% CPU · 20 MiB/s · 383.4 µs CPU/req)</sub> | **104**<br><sub>(145 MiB / 3.2% CPU · 26 MiB/s · 922.0 µs CPU/req)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **104**<br><sub>(481 MiB / 2.8% CPU · 26 MiB/s · 805.8 µs CPU/req)</sub> | *Not possible* | **96**<br><sub>(130 MiB / 2.0% CPU · 24 MiB/s · 633.9 µs CPU/req)</sub> | **80**<br><sub>(130 MiB / 1.5% CPU · 20 MiB/s · 554.0 µs CPU/req)</sub> | **96**<br><sub>(347 MiB / 3.2% CPU · 24 MiB/s · 987.5 µs CPU/req)</sub> |
| Early response (origin writes after first request chunk) | HTTP/2 · TLS | HTTP/2 · TLS | **1,546**<br><sub>(446 MiB / 22.0% CPU · 193 MiB/s · 426.1 µs CPU/req)</sub> | *Not possible* | **1,609**<br><sub>(119 MiB / 20.9% CPU · 201 MiB/s · 389.2 µs CPU/req)</sub> | **0**<br><sub>(peak 1,610 · 134 MiB / 24.2% CPU)</sub> | 🥇 **1,645**<br><sub>(349 MiB / 30.2% CPU · 206 MiB/s · 550.9 µs CPU/req)</sub> |
| Duplex (both directions live) | HTTP/2 · TLS | HTTP/2 · TLS | **1,812**<br><sub>(460 MiB / 17.6% CPU · 227 MiB/s · 291.5 µs CPU/req)</sub> | *Not possible* | 🥇 **1,945**<br><sub>(peak 2,028 · 119 MiB / 17.9% CPU · 243 MiB/s · 275.3 µs CPU/req)</sub> | **0**<br><sub>(peak 1,669 · 131 MiB / 21.3% CPU)</sub> | **1,677**<br><sub>(368 MiB / 31.5% CPU · 210 MiB/s · 563.4 µs CPU/req)</sub> |
| Duplex (WebSocket / H1 Upgrade) | HTTP/1 · TLS | HTTP/1 · plain | **5,470**<br><sub>(156 MiB / 10.0% CPU · 54.7 µs CPU/req)</sub> | 🥇 **17,987**<br><sub>(86 MiB / 15.6% CPU · 26.1 µs CPU/req)</sub> | **10,901**<br><sub>(103 MiB / 13.7% CPU · 37.8 µs CPU/req)</sub> | **12,646**<br><sub>(121 MiB / 21.1% CPU · 50.1 µs CPU/req)</sub> | **15,211**<br><sub>(152 MiB / 21.5% CPU · 42.5 µs CPU/req)</sub> |

Slow consumer is sleep-bound; H1/H2/H3 sit in the same band. Prefer the ratio columns for early-response, duplex, and WebSocket rows.

### TLS termination cost (H1 TLS → cleartext origin)

Isolates keep-alive tiny GET vs **new connection per request** (handshake-dominated) vs keep-alive **256 KiB**. Product comparison uses RPS and end-to-end latency; TWP can also capture `ClientTlsTiming` when `TWP_RPS_CAPTURE_TLS=1` (child process) — nginx/YARP have no equivalent hook.

#### Windows

Median of **3** repeats on `windows-latest` @ `482f5984`. Source: Actions [37210533058](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37210533058). Absolute RPS on GHA swings hard; prefer **TWP÷YARP**.


*Not possible:* **HAProxy** and **Envoy** columns are omitted (no official Windows port).

| Workload | TWP | nginx | YARP |
|---|---:|---:|---:|
| Keep-alive · tiny GET | 🥇 **55,831**<br><sub>(85 MiB / 47.4% CPU · 34.0 µs CPU/req)</sub> | **24,990**<br><sub>(142 MiB / 24.4% CPU · 39.1 µs CPU/req)</sub> | **52,516**<br><sub>(105 MiB / 49.0% CPU · 37.3 µs CPU/req)</sub> |
| New-connection · tiny GET | 🥇 **928**<br><sub>(78 MiB)</sub> | **319**<br><sub>(141 MiB / 1.9% CPU · 234.8 µs CPU/req)</sub> | **920**<br><sub>(105 MiB)</sub> |
| Keep-alive · 256 KiB GET | 🥇 **5,229**<br><sub>(128 MiB / 37.9% CPU · 1,307 MiB/s · 289.6 µs CPU/req)</sub> | **340**<br><sub>(142 MiB / 24.8% CPU · 85 MiB/s · 2915.5 µs CPU/req)</sub> | **4,577**<br><sub>(135 MiB / 43.5% CPU · 1,144 MiB/s · 380.1 µs CPU/req)</sub> |

#### Linux

Median of **3** repeats @ `482f5984`. Source: Actions [37210533058](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37210533058).


| Workload | TWP | nginx | HAProxy | Envoy | YARP |
|---|---:|---:|---:|---:|---:|
| Keep-alive · tiny GET | **23,748**<br><sub>(107 MiB / 49.8% CPU · 83.9 µs CPU/req)</sub> | 🥇 **27,308**<br><sub>(98 MiB / 42.0% CPU · 61.4 µs CPU/req)</sub> | **26,617**<br><sub>(82 MiB / 44.8% CPU · 67.3 µs CPU/req)</sub> | **17,004**<br><sub>(128 MiB / 59.3% CPU · 139.4 µs CPU/req)</sub> | **20,349**<br><sub>(131 MiB / 50.0% CPU · 98.3 µs CPU/req)</sub> |
| New-connection · tiny GET | **969**<br><sub>(124 MiB / 0.2% CPU · 10.3 µs CPU/req)</sub> | **1,004**<br><sub>(98 MiB / 1.0% CPU · 39.1 µs CPU/req)</sub> | **950**<br><sub>(85 MiB / 0.3% CPU · 13.1 µs CPU/req)</sub> | 🥇 **1,072**<br><sub>(128 MiB)</sub> | **962**<br><sub>(147 MiB / 0.7% CPU · 30.4 µs CPU/req)</sub> |
| Keep-alive · 256 KiB GET | **2,661**<br><sub>(132 MiB / 36.8% CPU · 665 MiB/s · 552.8 µs CPU/req)</sub> | **1,701**<br><sub>(99 MiB / 53.3% CPU · 425 MiB/s · 1253.0 µs CPU/req)</sub> | 🥇 **2,926**<br><sub>(86 MiB / 32.5% CPU · 731 MiB/s · 444.9 µs CPU/req)</sub> | **2,662**<br><sub>(144 MiB / 34.1% CPU · 665 MiB/s · 512.3 µs CPU/req)</sub> | **2,122**<br><sub>(160 MiB / 45.4% CPU · 531 MiB/s · 855.0 µs CPU/req)</sub> |

#### macOS

Median of **3** repeats on `macos-15` @ `482f5984`. Source: Actions [37210533058](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37210533058).

| Workload | TWP | nginx | HAProxy | Envoy | YARP |
|---|---:|---:|---:|---:|---:|
| Keep-alive · tiny GET | **13,222**<br><sub>(123 MiB / 21.6% CPU · 48.9 µs CPU/req)</sub> | 🥇 **14,502**<br><sub>(84 MiB / 15.7% CPU · 32.5 µs CPU/req)</sub> | **13,941**<br><sub>(108 MiB / 17.7% CPU · 38.1 µs CPU/req)</sub> | **7,183**<br><sub>(121 MiB / 22.1% CPU · 92.5 µs CPU/req)</sub> | **6,401**<br><sub>(160 MiB / 13.9% CPU · 65.3 µs CPU/req)</sub> |
| New-connection · tiny GET | **125**<br><sub>(peak 130 · 107 MiB / 0.2% CPU · 38.5 µs CPU/req)</sub> | **619**<br><sub>(86 MiB)</sub> | **707**<br><sub>(105 MiB)</sub> | 🥇 **764**<br><sub>(131 MiB / 0.1% CPU · 2.4 µs CPU/req)</sub> | **126**<br><sub>(158 MiB / 0.1% CPU · 11.9 µs CPU/req)</sub> |
| Keep-alive · 256 KiB GET | **1,756**<br><sub>(217 MiB / 21.8% CPU · 439 MiB/s · 372.2 µs CPU/req)</sub> | **1,898**<br><sub>(85 MiB / 18.2% CPU · 475 MiB/s · 287.6 µs CPU/req)</sub> | **2,178**<br><sub>(116 MiB / 24.6% CPU · 544 MiB/s · 338.9 µs CPU/req)</sub> | 🥇 **2,258**<br><sub>(155 MiB / 26.4% CPU · 564 MiB/s · 350.4 µs CPU/req)</sub> | **1,599**<br><sub>(181 MiB / 31.9% CPU · 400 MiB/s · 598.3 µs CPU/req)</sub> |

Prefer **TWP÷YARP** in the table. Absolute RPS on GHA swings hard. New-connection is Darwin SslStream-bound for TWP and YARP (handshake p99 SLO **500 ms** on macOS only — Win/Linux stay at **200 ms**).

## Unary gRPC (H2 TLS)

Unary Echo **RPC/s** @ c=64 over H2 TLS→H2 TLS for Titanium, YARP, nginx (`grpc_pass`), HAProxy, and Envoy (OS-possible peers only). Not folded into the architecture-sensitive tables. Median of **3** repeats @ `482f5984` — [37210536200](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37210536200).

| OS | Titanium | YARP | nginx | HAProxy | Envoy |
|---|---:|---:|---:|---:|---:|
| Windows | 🥇 **42,967**<br><sub>(128 MiB / 31.4% CPU · 29.3 µs CPU/req)</sub> | **27,337**<br><sub>(128 MiB / 47.0% CPU · 68.7 µs CPU/req)</sub> | **0**<br><sub>(144 MiB / 24.8% CPU)</sub> | *Not possible* | *Not possible* |
| Linux | 🥇 **33,244**<br><sub>(168 MiB / 34.1% CPU · 41.1 µs CPU/req)</sub> | **20,500**<br><sub>(152 MiB / 43.1% CPU · 84.1 µs CPU/req)</sub> | **0**<br><sub>(110 MiB / 41.4% CPU)</sub> | **15,252**<br><sub>(81 MiB / 44.6% CPU · 117.0 µs CPU/req)</sub> | **16,458**<br><sub>(126 MiB / 56.0% CPU · 136.2 µs CPU/req)</sub> |
| macOS | **0**<br><sub>(251 MiB / 10.8% CPU)</sub> | **0**<br><sub>(263 MiB / 9.2% CPU)</sub> | **0**<br><sub>(86 MiB / 2.5% CPU)</sub> | **17,350**<br><sub>(123 MiB / 29.4% CPU · 50.9 µs CPU/req)</sub> | 🥇 **20,494**<br><sub>(121 MiB / 33.2% CPU · 48.6 µs CPU/req)</sub> |

## Unary gRPC (H2 TLS → h2c)

Unary Echo **RPC/s** @ c=64 over **H2 TLS → h2c** (edge TLS, cleartext H2 origin). Peers: Titanium, YARP, HAProxy, Envoy. Mode: `compare-grpc` (`*-grpc-h2c`). Median of **3** repeats @ `482f5984` — [37210536200](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37210536200).

*Not possible:* **nginx** column omitted (no H2 upstream).

| OS | Titanium | YARP | HAProxy | Envoy |
|---|---:|---:|---:|---:|
| Windows | 🥇 **47,293**<br><sub>(129 MiB / 33.0% CPU · 27.9 µs CPU/req)</sub> | **29,812**<br><sub>(136 MiB / 44.9% CPU · 60.2 µs CPU/req)</sub> | *Not possible* | *Not possible* |
| Linux | 🥇 **39,632**<br><sub>(163 MiB / 33.7% CPU · 34.0 µs CPU/req)</sub> | **23,644**<br><sub>(153 MiB / 44.0% CPU · 74.5 µs CPU/req)</sub> | **18,026**<br><sub>(83 MiB / 43.9% CPU · 97.3 µs CPU/req)</sub> | **17,986**<br><sub>(125 MiB / 56.5% CPU · 125.7 µs CPU/req)</sub> |
| macOS | **8,312**<br><sub>(273 MiB / 7.4% CPU · 26.7 µs CPU/req)</sub> | **8,959**<br><sub>(235 MiB / 19.6% CPU · 65.6 µs CPU/req)</sub> | **6,113**<br><sub>(110 MiB / 21.3% CPU · 104.3 µs CPU/req)</sub> | 🥇 **9,239**<br><sub>(120 MiB / 15.6% CPU · 50.7 µs CPU/req)</sub> |

## WebSocket (H1 TLS → H1 TLS)

WebSocket echo round-trips/sec over **dual-TLS** H1 (`proxy_ssl` style). Mode: `compare-ws-h1tls` (`*-duplex-ws-h1tls`). Median of **3** repeats @ `482f5984`. Source: [37210538613](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37210538613).

| OS | Titanium | YARP | nginx | HAProxy | Envoy |
|---|---:|---:|---:|---:|---:|
| Windows | 🥇 **29,526**<br><sub>(99 MiB / 42.1% CPU · 57.1 µs CPU/req)</sub> | **27,745**<br><sub>(93 MiB / 44.1% CPU · 63.5 µs CPU/req)</sub> | **13,706**<br><sub>(142 MiB / 24.8% CPU · 72.2 µs CPU/req)</sub> | *Not possible* | *Not possible* |
| Linux | **40,864**<br><sub>(137 MiB / 41.9% CPU · 41.0 µs CPU/req)</sub> | **37,220**<br><sub>(135 MiB / 44.0% CPU · 47.2 µs CPU/req)</sub> | **48,858**<br><sub>(104 MiB / 34.4% CPU · 28.2 µs CPU/req)</sub> | **48,324**<br><sub>(84 MiB / 37.1% CPU · 30.7 µs CPU/req)</sub> | 🥇 **49,049**<br><sub>(127 MiB / 37.5% CPU · 30.6 µs CPU/req)</sub> |
| macOS | **6,336**<br><sub>(147 MiB / 14.8% CPU · 70.1 µs CPU/req)</sub> | **6,772**<br><sub>(149 MiB / 16.4% CPU · 72.8 µs CPU/req)</sub> | 🥇 **12,168**<br><sub>(91 MiB / 10.3% CPU · 25.4 µs CPU/req)</sub> | **6,870**<br><sub>(107 MiB / 11.8% CPU · 51.3 µs CPU/req)</sub> | **6,721**<br><sub>(123 MiB / 18.3% CPU · 81.5 µs CPU/req)</sub> |

## WebSocket (H2 TLS 8441 → H1)

WebSocket echo over **RFC 8441** extended CONNECT (H2 TLS client → H1 plain origin). Mode: `compare-ws-h2` (`*-duplex-ws-h2`).

The probe client sets `NoDelay` and writes each HTTP/2 frame as one TLS record. Median of **3** repeats @ `482f5984`. Source: [37210541741](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37210541741).

*Not possible:* **nginx** column omitted (no RFC 8441 extended CONNECT).

| OS | Titanium | YARP | HAProxy | Envoy |
|---|---:|---:|---:|---:|
| Windows | 🥇 **34,615**<br><sub>(152 MiB / 44.4% CPU · 51.3 µs CPU/req)</sub> | **0**<br><sub>(160 MiB / 19.9% CPU)</sub> | *Not possible* | *Not possible* |
| Linux | 🥇 **41,579**<br><sub>(204 MiB / 49.5% CPU · 47.6 µs CPU/req)</sub> | **0**<br><sub>(191 MiB / 43.2% CPU)</sub> | **36,813**<br><sub>(85 MiB / 41.2% CPU · 44.7 µs CPU/req)</sub> | **0**<br><sub>(123 MiB / 0.3% CPU)</sub> |
| macOS | **6,839**<br><sub>(260 MiB / 21.9% CPU · 96.0 µs CPU/req)</sub> | **0**<br><sub>(186 MiB / 10.5% CPU)</sub> | 🥇 **9,231**<br><sub>(109 MiB / 16.7% CPU · 54.3 µs CPU/req)</sub> | **0**<br><sub>(116 MiB / 0.4% CPU)</sub> |

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

`--arm-shard` takes a comparison-group key (`h1c-h1c`, from `--print-groups`), not an `i/n` index, so a re-run measures the same row after a peer install flake. `i/n` remains for smoke modes. Paste unions `rps-csv-<os>-shard-*` (`paste-compare-product-wiki.ps1 -RunIds …`). Table “Source: Actions” may list several run URLs for one table — **do not mix SHAs**. A single-row leg allows **60** minutes of ramp and **75** minutes overall. Free GitHub accounts run **20** jobs at once, **5** of them macOS. Give a full suite a quiet window: do not push pull requests or start other macOS jobs while it runs. Re-run infrastructure failures only (`gh run rerun --failed`). A failed gate is a finding and is never re-rolled; publish the last attempt, never the best of several. The RPS tables on this page are @ `482f5984`: product [37210516555](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37210516555), saturation [37210519205](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37210519205), bodies [37210521790](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37210521790), POST [37210524513](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37210524513), lossy [37210527573](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37210527573), arch [37210530448](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37210530448), TLS cost [37210533058](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37210533058), gRPC [37210536200](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37210536200), WebSocket H1 TLS [37210538613](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37210538613), WebSocket H2 [37210541741](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37210541741), editions [37210543774](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37210543774). Reverse product rows, bodies, POST, lossy, arch, TLS cost, gRPC, both WebSocket modes, and saturation run on Windows, Linux, and macOS. TWP MITM arms run on Linux only. `compare-editions` (CLI and Plus) runs on Linux only. `compare-spot` on pull requests to beta and stable stays on all three OS. The historical v2 MITM note below the product tables stays @ `41f4adee` because those run ids are from that fix, not this re-measure.

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
