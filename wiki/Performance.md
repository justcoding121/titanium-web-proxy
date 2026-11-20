Throughput and footprint of **Titanium** as a reverse / edge proxy and as a decrypting (**MITM**) proxy, measured on the same harness against **YARP**, **nginx**, **HAProxy**, and **Envoy** where each OS can run them.

**RPS** is requests per second (gRPC tables use **RPC/s**). Numbers are Release builds on matched GitHub-hosted runners: Windows and Linux at **4 vCPU / 16 GiB**, macOS at **`macos-15` Apple Silicon 3-core / 7 GB**. We compare products within an OS, not OS against OS, so read within one table — absolute RPS is not comparable across operating systems. *Not possible* means that product cannot run that path on that OS; *Not measured* means the path exists but no published number yet.

For pooling knobs and certificate first-visit tuning, see [Performance and pooling](Home#user-content-performance-and-pooling). Laptop cool A/B tables (not publishable) live on [Performance Local Lab](Performance-Local-Lab).

## Why this comparison is fair

- Same load generator, same origin process, and the same warmup / measure windows (2s / 8s) with the same concurrency ramp (8, 16, 32, 64).
- Every reverse arm is three OS processes: load generator + origin + proxy. Origin-direct omits the proxy; peers are never in-process with the client.
- Same runner class per table (`windows-latest` / `ubuntu-26.04` / `macos-15`). Laptop numbers are never mixed into these tables.
- Peers use equivalent TLS/ALPN and streaming-friendly settings on the same loopback shape. HAProxy and Envoy are Linux/macOS only — *Not possible* on Windows, so their columns are omitted there.
- MITM (HTTPS decryption with forged certificates) is Titanium-only; peers cannot MITM. The published MITM table is Linux only. It shows Titanium MITM overhead versus its own reverse path on the same wires.
- **Tiny keep-alive GET** (~56-byte JSON) is the industry RPS shape (same class as wrk / TechEmpower). It is also real for small JSON APIs and health checks.
- **HTTP/2 and HTTP/3** spread that concurrency across `min(concurrency, max(4 × cores, 16))` client connections, the same connection count h2load and wrk fix with `-c`. On these 4 vCPU runners that is 8, 16, 16, and 16 connections at c = 8, 16, 32, 64.
- **Same-protocol H2↔H2 / H3↔H3** on that shape is Titanium’s **best case**: with interception off, Titanium copies frames instead of decoding and re-encoding headers (peers do a full HTTP decode). Medals there are not the typical reverse-proxy job.
- **Typical reverse** is H1 TLS→H1 or H2→H1 (~1.1× YARP on Win/Linux tiny GET). With **larger bodies**, see [Heavier reverse](#user-content-heavier-reverse-workloads) (@ `4a97cb0d`).

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
    - [Linux (GitHub-hosted `ubuntu-26.04`)](#user-content-linux-github-hosted-ubuntu-2604)
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

On the Linux H2 TLS→H1 plain row at c=64 (16 connections, @ `4a97cb0d`), proxy CPU was about **44%** for nginx, **45%** for HAProxy, and **60%** for Envoy. Windows nginx on the same H2 client shape stayed near **25%** (one core). That measured shape is the one published here.

Laptop High-perf / cool-paired Windows numbers live on [Performance Local Lab](Performance-Local-Lab). Do not mix those absolutes into the tables below.

### Windows (GitHub-hosted `windows-latest`)

|||
|---|---|
| OS | Windows Server (GitHub-hosted `windows-latest`) |
| CPU | **4** logical processors |
| RAM | **16** GiB |
| Runtime | .NET **11.0.0** (net10.0 IL; `DOTNET_ROLL_FORWARD=LatestMajor`) |
| nginx | nginx/Windows **1.31.3** (same-OS only; no QUIC) |
| HAProxy | *Not possible* on Windows (no official port) |
| Envoy | *Not possible* on Windows (upstream discontinued Windows builds) |
| YARP | Yarp.ReverseProxy **2.3.0** |
| Harness | RpsLoadProbe Release; median of 3 repeats |

### Linux (GitHub-hosted `ubuntu-26.04`)

|||
|---|---|
| OS | Ubuntu **26.04.1** LTS (pinned `ubuntu-26.04`, not `ubuntu-latest`) |
| CPU | **4** logical processors (AMD EPYC) |
| RAM | **16** GiB |
| Runtime | .NET **11.0.0** (net10.0 IL; `DOTNET_ROLL_FORWARD=LatestMajor`) |
| nginx | nginx/**1.31.9** (nginx.org mainline, `--with-http_v3_module`) |
| HAProxy | HAProxy **3.2.23** built with `USE_QUIC` (GHA) |
| Envoy | pinned GitHub release static binary **1.36.7** (HTTP/3 compiled in) |
| YARP | Yarp.ReverseProxy **2.3.0** |
| Harness | RpsLoadProbe Release; median of 3 repeats where noted |

### macOS (GitHub-hosted `macos-15`, Apple Silicon)

|||
|---|---|
| OS | macOS 15 (GitHub-hosted `macos-15`, Apple Silicon arm64) |
| CPU | **3** logical processors (Apple M1) |
| RAM | **7** GB |
| Runtime | .NET **11.0.0** (net10.0 IL; `DOTNET_ROLL_FORWARD=LatestMajor`) |
| nginx | Homebrew nginx **1.31.8** with `--with-http_v3_module` (workflow fails if missing) |
| HAProxy | Homebrew `haproxy` **3.4.7** with `USE_QUIC` (workflow fails if missing) |
| Envoy | Homebrew bottle **1.40.2** when present; else pinned darwin-arm64. HTTP/3 compiled in. |
| MsQuic | Homebrew `libmsquic` + `openssl@3` on `DYLD_LIBRARY_PATH` / `DYLD_FALLBACK_LIBRARY_PATH` (`QuicListener.IsSupported`) |
| YARP | Yarp.ReverseProxy **2.3.0** |
| Harness | RpsLoadProbe Release; median of 3 repeats where noted |

Use the pinned `macos-15` label (not `macos-latest`) so the image does not change between runs.

### Saturation control

Calibration for the shared 4 vCPU loopback shape: how close client + origin are to saturated before ranking reverse peers. Tiny keep-alive GET. Median of **3** repeats @ `4a97cb0d` — [44654047300](https://github.com/justcoding121/titanium-web-proxy/actions/runs/44654047300). Warmup 2s / measure 8s; concurrency 8, 16, 32, 64. Block A **% of origin-HttpClient** uses median **peak** RPS. Blocks B/C use peer÷YARP / ÷nginx on median peak (not % of H1 origin). **RPS cells** embed median RSS / CPU for the **proxy child** plus its **full descendant tree** (serve-proxy → nginx master → workers); origin-direct samples the **origin** child. Product matrices below use matched `dotnet-httpclient` only (not bombardier).


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
| origin-direct | dotnet-httpclient | **99,979**<br><sub>(57 MiB / 48.7% CPU · 19.5 µs CPU/req)</sub> | **100.0%** |
| origin-direct-bombardier | bombardier | **77,264**<br><sub>(59 MiB / 35.3% CPU · 18.3 µs CPU/req)</sub> | **77.3%** |
| bare-reverse-http1 | dotnet-httpclient | **45,290**<br><sub>(63 MiB / 49.9% CPU · 44.1 µs CPU/req)</sub> | **45.3%** |
| nginx-reverse-http1 | dotnet-httpclient | **36,662**<br><sub>(128 MiB / 24.8% CPU · 27.0 µs CPU/req)</sub> | **36.7%** |
| yarp-reverse-http1 | dotnet-httpclient | **42,428**<br><sub>(92 MiB / 51.0% CPU · 48.1 µs CPU/req)</sub> | **42.4%** |
| twp-reverse-http1 | dotnet-httpclient | 🥇 **45,471**<br><sub>(80 MiB / 48.7% CPU · 42.8 µs CPU/req)</sub> | **45.5%** |

**Linux** (`ubuntu-26.04`)

| Arm | Generator | RPS | % of origin-HttpClient |
|---|---|---:|---:|
| origin-direct | dotnet-httpclient | **92,590**<br><sub>(80 MiB / 41.7% CPU · 18.0 µs CPU/req)</sub> | **100.0%** |
| origin-direct-bombardier | bombardier | **66,400**<br><sub>(80 MiB / 31.4% CPU · 18.9 µs CPU/req)</sub> | **71.7%** |
| bare-reverse-http1 | dotnet-httpclient | **40,737**<br><sub>(66 MiB / 38.8% CPU · 38.0 µs CPU/req)</sub> | **44.0%** |
| nginx-reverse-http1 | dotnet-httpclient | 🥇 **56,390**<br><sub>(75 MiB / 38.5% CPU · 27.3 µs CPU/req)</sub> | **60.9%** |
| yarp-reverse-http1 | dotnet-httpclient | **37,713**<br><sub>(122 MiB / 43.3% CPU · 45.9 µs CPU/req)</sub> | **40.7%** |
| twp-reverse-http1 | dotnet-httpclient | **40,892**<br><sub>(88 MiB / 40.6% CPU · 39.7 µs CPU/req)</sub> | **44.2%** |


**macOS** (`macos-15`, Apple Silicon)

| Arm | Generator | RPS | % of origin-HttpClient |
|---|---|---:|---:|
| origin-direct | dotnet-httpclient | **60,097**<br><sub>(92 MiB / 37.0% CPU · 24.6 µs CPU/req)</sub> | **100.0%** |
| origin-direct-bombardier | bombardier | **46,829**<br><sub>(91 MiB / 37.5% CPU · 32.0 µs CPU/req)</sub> | **77.9%** |
| bare-reverse-http1 | dotnet-httpclient | **27,412**<br><sub>(78 MiB / 27.7% CPU · 40.4 µs CPU/req)</sub> | **45.6%** |
| nginx-reverse-http1 | dotnet-httpclient | **19,893**<br><sub>(68 MiB / 15.6% CPU · 31.3 µs CPU/req)</sub> | **33.1%** |
| yarp-reverse-http1 | dotnet-httpclient | **18,776**<br><sub>(139 MiB / 27.3% CPU · 58.1 µs CPU/req)</sub> | **31.2%** |
| twp-reverse-http1 | dotnet-httpclient | 🥇 **21,803**<br><sub>(104 MiB / 35.2% CPU · 64.6 µs CPU/req)</sub> | **36.3%** |

On this macOS run `origin-direct` was **37.0%** CPU, so the % of origin-HttpClient column is not a ceiling when that CPU is near idle. Rank those Mac peers by RPS.

Reverse peers on Block A @ `4a97cb0d`: Windows TWP is **45.5%** of origin-direct and Linux TWP is **44.2%**. Prefer the **%** column over absolute RPS across runs. Bare and origin-direct are controls (not medal peers).

#### Block B — H2 TLS→H1

Peer ratios (÷YARP / ÷nginx) on median peak; **RPS cells** embed `(MiB / CPU%)`.

**Windows** (`windows-latest`)

| Arm | Generator | RPS | ÷YARP | ÷nginx |
|---|---|---:|---:|---:|
| nginx-reverse-http2 | dotnet-httpclient | **24,119**<br><sub>(144 MiB / 24.7% CPU · 40.9 µs CPU/req)</sub> | **0.43×** | **1.00×** |
| yarp-reverse-http2 | dotnet-httpclient | 🥇 **56,340**<br><sub>(109 MiB / 47.7% CPU · 33.9 µs CPU/req)</sub> | **1.00×** | **2.34×** |
| twp-reverse-http2-cleartext | dotnet-httpclient | **54,869**<br><sub>(129 MiB / 48.5% CPU · 35.4 µs CPU/req)</sub> | **0.97×** | **2.27×** |

**Linux** (`ubuntu-26.04`)

| Arm | Generator | RPS | ÷YARP | ÷nginx |
|---|---|---:|---:|---:|
| nginx-reverse-http2 | dotnet-httpclient | 🥇 **39,563**<br><sub>(107 MiB / 41.0% CPU · 41.4 µs CPU/req)</sub> | **1.06×** | **1.00×** |
| yarp-reverse-http2 | dotnet-httpclient | **37,389**<br><sub>(142 MiB / 43.4% CPU · 46.4 µs CPU/req)</sub> | **1.00×** | **0.95×** |
| twp-reverse-http2-cleartext | dotnet-httpclient | **38,331**<br><sub>(167 MiB / 43.2% CPU · 45.1 µs CPU/req)</sub> | **1.03×** | **0.97×** |

**macOS** (`macos-15`, Apple Silicon)

| Arm | Generator | RPS | ÷YARP | ÷nginx |
|---|---|---:|---:|---:|
| nginx-reverse-http2 | dotnet-httpclient | **14,859**<br><sub>(85 MiB / 15.8% CPU · 42.6 µs CPU/req)</sub> | **0.82×** | **1.00×** |
| yarp-reverse-http2 | dotnet-httpclient | **18,083**<br><sub>(157 MiB / 33.1% CPU · 73.2 µs CPU/req)</sub> | **1.00×** | **1.22×** |
| twp-reverse-http2-cleartext | dotnet-httpclient | 🥇 **21,054**<br><sub>(162 MiB / 39.6% CPU · 75.2 µs CPU/req)</sub> | **1.16×** | **1.42×** |
#### Block C — H3→H1

Same layout as Block B. Requires QuicListener. nginx needs `http_v3_module` (Windows nginx has no QUIC). HAProxy needs `USE_QUIC` (GHA Linux/macOS require it). Envoy 1.20+ includes HTTP/3.

Medals are nginx / YARP / Titanium only, from the same run as Blocks A and B ([40610501155](https://github.com/justcoding121/titanium-web-proxy/actions/runs/40610501155)).

**Windows** (`windows-latest`)

| Arm | Generator | RPS | ÷YARP | ÷nginx |
|---|---|---:|---:|---:|
| nginx-reverse-http3-cleartext | dotnet-httpclient | *Not possible (no QUIC)* | — | — |
| yarp-reverse-http3-cleartext | dotnet-httpclient | **18,273**<br><sub>(170 MiB / 49.1% CPU · 107.4 µs CPU/req)</sub> | **1.00×** | — |
| twp-reverse-http3-cleartext | dotnet-httpclient | 🥇 **19,816**<br><sub>(120 MiB / 48.2% CPU · 97.2 µs CPU/req)</sub> | **1.08×** | — |

**Linux** (`ubuntu-26.04`)

| Arm | Generator | RPS | ÷YARP | ÷nginx |
|---|---|---:|---:|---:|
| nginx-reverse-http3-cleartext | dotnet-httpclient | 🥇 **25,588**<br><sub>(peak 32,212 · 109 MiB / 29.7% CPU · 46.4 µs CPU/req)</sub> | **1.56×** | **1.00×** |
| yarp-reverse-http3-cleartext | dotnet-httpclient | **20,686**<br><sub>(205 MiB / 48.1% CPU · 93.0 µs CPU/req)</sub> | **1.00×** | **0.64×** |
| twp-reverse-http3-cleartext | dotnet-httpclient | **22,858**<br><sub>(154 MiB / 45.4% CPU · 79.5 µs CPU/req)</sub> | **1.11×** | **0.71×** |

**macOS** (`macos-15`, Apple Silicon)

| Arm | Generator | RPS | ÷YARP | ÷nginx |
|---|---|---:|---:|---:|
| nginx-reverse-http3-cleartext | dotnet-httpclient | 🥇 **10,896**<br><sub>(88 MiB / 12.7% CPU · 46.5 µs CPU/req)</sub> | **1.96×** | **1.00×** |
| yarp-reverse-http3-cleartext | dotnet-httpclient | **5,546**<br><sub>(196 MiB / 29.5% CPU · 213.0 µs CPU/req)</sub> | **1.00×** | **0.51×** |
| twp-reverse-http3-cleartext | dotnet-httpclient | **6,482**<br><sub>(138 MiB / 30.4% CPU · 187.3 µs CPU/req)</sub> | **1.17×** | **0.59×** |

## Windows — Titanium vs nginx vs HAProxy vs Envoy vs YARP

Client / origin: HTTP version and whether TLS is used (`plain` = cleartext, `TLS` = encrypted, `QUIC` = HTTP/3).

### Reverse

Median of **3 repeats** on `windows-latest` (4 vCPU / 16 GiB). Bare reverse 5×5 @ `4a97cb0d` — `compare-product` [44653985062](https://github.com/justcoding121/titanium-web-proxy/actions/runs/44653985062). Warmup 2s / measure 8s; concurrency 8, 16, 32, 64. Prefer TWP÷peer ratios over absolute RPS. **RPS cells** include median RSS / CPU at the peak-RPS step as `<br><sub>(MiB / CPU%)</sub>`. nginx terminate peers use `keepalive 256` + streaming buffers. **HAProxy / Envoy are Linux-only peers** (no official Windows port). Laptop High-perf / cool-paired numbers stay on the [local lab](Performance-Local-Lab). Product 5×5 is **~56-byte JSON keep-alive GET**; H2/H3 same-protocol cells are mostly header work with a tiny body (Titanium best case) — see [Why this comparison is fair](#user-content-why-this-comparison-is-fair).

**Load generators:** Reverse inbound H3 arms use **`dotnet-httpclient`** (`http_version=3.0`, `RequestVersionExact`). nginx/Windows is same-OS only (no QUIC). HAProxy/Envoy are Linux-only terminate peers.

| Client | Origin | TWP | nginx | YARP |
|---|---|---:|---:|---:|
| HTTP/1 · plain | HTTP/1 · plain | 🥇 **24,761**<br><sub>(82 MiB / 48% CPU · 77.6 µs CPU/req)</sub> | **20,122**<br><sub>(128 MiB / 24.6% CPU · 48.9 µs CPU/req)</sub> | **22,258**<br><sub>(94 MiB / 50.8% CPU · 91.3 µs CPU/req)</sub> |
| HTTP/1 · plain | HTTP/1 · TLS | 🥇 **28,908**<br><sub>(95 MiB / 47.7% CPU · 66.0 µs CPU/req)</sub> | **17,096**<br><sub>(138 MiB / 24.7% CPU · 57.8 µs CPU/req)</sub> | **27,193**<br><sub>(103 MiB / 51.6% CPU · 75.9 µs CPU/req)</sub> |
| HTTP/1 · plain | HTTP/2 · plain | 🥇 **29,138**<br><sub>(94 MiB / 52% CPU · 71.3 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **25,233**<br><sub>(101 MiB / 54.7% CPU · 86.7 µs CPU/req)</sub> |
| HTTP/1 · plain | HTTP/2 · TLS | 🥇 **37,658**<br><sub>(118 MiB / 51.6% CPU · 54.8 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **34,848**<br><sub>(101 MiB / 55.6% CPU · 63.8 µs CPU/req)</sub> |
| HTTP/1 · plain | HTTP/3 · QUIC | 🥇 **15,563**<br><sub>(108 MiB / 50.2% CPU · 128.9 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | **14,640**<br><sub>(118 MiB / 50.4% CPU · 137.8 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/1 · plain | 🥇 **28,781**<br><sub>(95 MiB / 48.4% CPU · 67.3 µs CPU/req)</sub> | **17,806**<br><sub>(144 MiB / 24.5% CPU · 55.0 µs CPU/req)</sub> | **26,267**<br><sub>(105 MiB / 51% CPU · 77.7 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/1 · TLS | 🥇 **25,200**<br><sub>(97 MiB / 47.1% CPU · 74.8 µs CPU/req)</sub> | **13,041**<br><sub>(146 MiB / 24.4% CPU · 74.9 µs CPU/req)</sub> | **23,229**<br><sub>(109 MiB / 47.5% CPU · 81.7 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/2 · plain | 🥇 **35,662**<br><sub>(115 MiB / 50.4% CPU · 56.5 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **34,750**<br><sub>(115 MiB / 51.1% CPU · 58.8 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/2 · TLS | 🥇 **31,280**<br><sub>(120 MiB / 47.2% CPU · 60.3 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **28,573**<br><sub>(116 MiB / 51.1% CPU · 71.5 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/3 · QUIC | 🥇 **22,449**<br><sub>(115 MiB / 50.3% CPU · 89.6 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | **20,851**<br><sub>(132 MiB / 49.5% CPU · 94.9 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | **57,300**<br><sub>(120 MiB / 50.4% CPU · 35.2 µs CPU/req)</sub> | **26,841**<br><sub>(130 MiB / 24.7% CPU · 36.8 µs CPU/req)</sub> | 🥇 **59,383**<br><sub>(94 MiB / 48.8% CPU · 32.8 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/1 · TLS | **35,658**<br><sub>(127 MiB / 49% CPU · 54.9 µs CPU/req)</sub> | **14,000**<br><sub>(140 MiB / 24.7% CPU · 70.5 µs CPU/req)</sub> | 🥇 **36,062**<br><sub>(111 MiB / 49.4% CPU · 54.8 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/2 · plain | 🥇 **76,837**<br><sub>(105 MiB / 35.8% CPU · 18.7 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **60,495**<br><sub>(108 MiB / 49% CPU · 32.4 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/2 · TLS | 🥇 **68,759**<br><sub>(112 MiB / 38.2% CPU · 22.2 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **54,526**<br><sub>(109 MiB / 46.7% CPU · 34.3 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/3 · QUIC | 🥇 **26,668**<br><sub>(135 MiB / 49.2% CPU · 73.8 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | **25,785**<br><sub>(130 MiB / 49.2% CPU · 76.4 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **37,948**<br><sub>(116 MiB / 49.5% CPU · 52.1 µs CPU/req)</sub> | **16,452**<br><sub>(145 MiB / 24.6% CPU · 59.8 µs CPU/req)</sub> | **37,020**<br><sub>(110 MiB / 49.5% CPU · 53.5 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/1 · TLS | 🥇 **50,958**<br><sub>(137 MiB / 47.8% CPU · 37.5 µs CPU/req)</sub> | **19,377**<br><sub>(147 MiB / 24.7% CPU · 50.9 µs CPU/req)</sub> | **50,657**<br><sub>(114 MiB / 45.9% CPU · 36.3 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | 🥇 **65,544**<br><sub>(127 MiB / 37.4% CPU · 22.8 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **49,954**<br><sub>(117 MiB / 50.2% CPU · 40.2 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **62,966**<br><sub>(131 MiB / 35.8% CPU · 22.7 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **48,878**<br><sub>(116 MiB / 48.5% CPU · 39.7 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/3 · QUIC | 🥇 **32,641**<br><sub>(146 MiB / 48.1% CPU · 59.0 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | **31,380**<br><sub>(138 MiB / 50% CPU · 63.8 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **10,697**<br><sub>(112 MiB / 46.9% CPU · 175.4 µs CPU/req)</sub> | *Not possible (no QUIC)* | **9,830**<br><sub>(158 MiB / 51.8% CPU · 211.0 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **7,178**<br><sub>(118 MiB / 46.8% CPU · 261.0 µs CPU/req)</sub> | *Not possible (no QUIC)* | **6,429**<br><sub>(163 MiB / 50.7% CPU · 315.4 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/2 · plain | 🥇 **10,285**<br><sub>(111 MiB / 49.7% CPU · 193.1 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **9,229**<br><sub>(166 MiB / 50.7% CPU · 219.6 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | 🥇 **12,055**<br><sub>(115 MiB / 46.1% CPU · 152.9 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **10,558**<br><sub>(167 MiB / 51.3% CPU · 194.5 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/3 · QUIC | **9,244**<br><sub>(117 MiB / 40.5% CPU · 175.4 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | 🥇 **9,725**<br><sub>(157 MiB / 51.8% CPU · 213.0 µs CPU/req)</sub> |

## Linux — Titanium vs nginx vs HAProxy vs Envoy vs YARP

### Reverse

Median of **3 repeats** on `ubuntu-26.04` (4 vCPU / 16 GiB). Bare reverse 5×5 @ `4a97cb0d` — `compare-product` [44653985062](https://github.com/justcoding121/titanium-web-proxy/actions/runs/44653985062). Warmup 2s / measure 8s; concurrency 8, 16, 32, 64. **Linux nginx is the authoritative nginx baseline.** HAProxy (3.2 `USE_QUIC`) and Envoy (GitHub release, HTTP/3 compiled in) run on the same loopback shape as nginx/YARP. nginx terminate peers use `keepalive 256` + streaming buffers. The RPS workflow installs nginx.org mainline (`http_v3_module`), a QUIC-enabled HAProxy, Envoy, and `libmsquic`. Prefer ratios over absolute RPS. Product 5×5 is **~56-byte JSON keep-alive GET**; H2/H3 same-protocol cells are mostly header work with a tiny body (Titanium best case) — see [Why this comparison is fair](#user-content-why-this-comparison-is-fair).

| Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---:|---:|---:|---:|---:|
| HTTP/1 · plain | HTTP/1 · plain | **33,500**<br><sub>(87 MiB / 41.8% CPU · 49.9 µs CPU/req)</sub> | 🥇 **44,574**<br><sub>(75 MiB / 39% CPU · 35.0 µs CPU/req)</sub> | **40,101**<br><sub>(62 MiB / 43.2% CPU · 43.1 µs CPU/req)</sub> | **23,452**<br><sub>(107 MiB / 58.4% CPU · 99.5 µs CPU/req)</sub> | **29,655**<br><sub>(126 MiB / 44.3% CPU · 59.7 µs CPU/req)</sub> |
| HTTP/1 · plain | HTTP/1 · TLS | **43,372**<br><sub>(117 MiB / 36.6% CPU · 33.8 µs CPU/req)</sub> | 🥇 **59,150**<br><sub>(99 MiB / 37.8% CPU · 25.6 µs CPU/req)</sub> | **54,478**<br><sub>(67 MiB / 39.8% CPU · 29.3 µs CPU/req)</sub> | **43,605**<br><sub>(108 MiB / 48% CPU · 44.1 µs CPU/req)</sub> | **41,734**<br><sub>(136 MiB / 39% CPU · 37.4 µs CPU/req)</sub> |
| HTTP/1 · plain | HTTP/2 · plain | 🥇 **37,737**<br><sub>(119 MiB / 39.6% CPU · 41.9 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **25,574**<br><sub>(63 MiB / 41.2% CPU · 64.5 µs CPU/req)</sub> | **21,866**<br><sub>(107 MiB / 64.4% CPU · 117.8 µs CPU/req)</sub> | **33,928**<br><sub>(134 MiB / 43.8% CPU · 51.7 µs CPU/req)</sub> |
| HTTP/1 · plain | HTTP/2 · TLS | 🥇 **33,261**<br><sub>(153 MiB / 38.9% CPU · 46.7 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **28,578**<br><sub>(63 MiB / 48.5% CPU · 67.8 µs CPU/req)</sub> | **20,781**<br><sub>(108 MiB / 62.2% CPU · 119.7 µs CPU/req)</sub> | **30,937**<br><sub>(137 MiB / 43.5% CPU · 56.2 µs CPU/req)</sub> |
| HTTP/1 · plain | HTTP/3 · QUIC | **37,495**<br><sub>(147 MiB / 40% CPU · 42.6 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **27,896**<br><sub>(111 MiB / 46.8% CPU · 67.1 µs CPU/req)</sub> | 🥇 **37,821**<br><sub>(163 MiB / 40.1% CPU · 42.4 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/1 · plain | **45,499**<br><sub>(115 MiB / 38.2% CPU · 33.6 µs CPU/req)</sub> | 🥇 **58,698**<br><sub>(107 MiB / 39.2% CPU · 26.7 µs CPU/req)</sub> | **55,858**<br><sub>(81 MiB / 42.6% CPU · 30.5 µs CPU/req)</sub> | **40,644**<br><sub>(118 MiB / 51.1% CPU · 50.3 µs CPU/req)</sub> | **40,555**<br><sub>(145 MiB / 40.1% CPU · 39.6 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/1 · TLS | **32,557**<br><sub>(116 MiB / 38.8% CPU · 47.6 µs CPU/req)</sub> | 🥇 **42,129**<br><sub>(113 MiB / 36.2% CPU · 34.3 µs CPU/req)</sub> | **39,481**<br><sub>(83 MiB / 40.3% CPU · 40.8 µs CPU/req)</sub> | **28,158**<br><sub>(119 MiB / 53.5% CPU · 76.1 µs CPU/req)</sub> | **29,112**<br><sub>(146 MiB / 43.9% CPU · 60.3 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/2 · plain | 🥇 **28,646**<br><sub>(137 MiB / 42.4% CPU · 59.2 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **20,183**<br><sub>(81 MiB / 41.7% CPU · 82.6 µs CPU/req)</sub> | **18,503**<br><sub>(118 MiB / 61.5% CPU · 133.0 µs CPU/req)</sub> | **25,464**<br><sub>(155 MiB / 45.8% CPU · 71.9 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/2 · TLS | **44,145**<br><sub>(166 MiB / 39.5% CPU · 35.8 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | 🥇 **47,925**<br><sub>(82 MiB / 39.8% CPU · 33.2 µs CPU/req)</sub> | **36,162**<br><sub>(118 MiB / 54% CPU · 59.7 µs CPU/req)</sub> | **39,088**<br><sub>(146 MiB / 42.4% CPU · 43.4 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/3 · QUIC | 🥇 **26,402**<br><sub>(149 MiB / 42.8% CPU · 64.9 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **14,009**<br><sub>(122 MiB / 56.5% CPU · 161.4 µs CPU/req)</sub> | **24,861**<br><sub>(178 MiB / 43.6% CPU · 70.2 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | **43,309**<br><sub>(146 MiB / 42.8% CPU · 39.5 µs CPU/req)</sub> | **43,185**<br><sub>(78 MiB / 43.3% CPU · 40.1 µs CPU/req)</sub> | 🥇 **51,690**<br><sub>(62 MiB / 42.6% CPU · 32.9 µs CPU/req)</sub> | **31,925**<br><sub>(107 MiB / 59.3% CPU · 74.3 µs CPU/req)</sub> | **42,935**<br><sub>(135 MiB / 43.1% CPU · 40.1 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/1 · TLS | **24,848**<br><sub>(163 MiB / 45% CPU · 72.4 µs CPU/req)</sub> | **24,066**<br><sub>(101 MiB / 45.5% CPU · 75.7 µs CPU/req)</sub> | 🥇 **28,880**<br><sub>(67 MiB / 44.1% CPU · 61.1 µs CPU/req)</sub> | **18,065**<br><sub>(108 MiB / 61.5% CPU · 136.1 µs CPU/req)</sub> | **24,010**<br><sub>(146 MiB / 46.7% CPU · 77.8 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/2 · plain | 🥇 **57,948**<br><sub>(145 MiB / 31.2% CPU · 21.5 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **37,380**<br><sub>(62 MiB / 46.4% CPU · 49.6 µs CPU/req)</sub> | **24,637**<br><sub>(106 MiB / 62.9% CPU · 102.2 µs CPU/req)</sub> | **42,122**<br><sub>(134 MiB / 42.7% CPU · 40.5 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/2 · TLS | 🥇 **79,547**<br><sub>(163 MiB / 31.1% CPU · 15.6 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **69,002**<br><sub>(67 MiB / 35.1% CPU · 20.4 µs CPU/req)</sub> | **46,609**<br><sub>(107 MiB / 52.9% CPU · 45.4 µs CPU/req)</sub> | **57,148**<br><sub>(146 MiB / 41.1% CPU · 28.7 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/3 · QUIC | 🥇 **34,941**<br><sub>(179 MiB / 41.6% CPU · 47.6 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **15,716**<br><sub>(110 MiB / 59.4% CPU · 151.2 µs CPU/req)</sub> | **33,613**<br><sub>(164 MiB / 41% CPU · 48.8 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | **51,086**<br><sub>(169 MiB / 42.6% CPU · 33.3 µs CPU/req)</sub> | **62,004**<br><sub>(107 MiB / 33.7% CPU · 21.7 µs CPU/req)</sub> | 🥇 **66,726**<br><sub>(80 MiB / 36.9% CPU · 22.1 µs CPU/req)</sub> | **41,079**<br><sub>(119 MiB / 55.3% CPU · 53.8 µs CPU/req)</sub> | **47,029**<br><sub>(143 MiB / 42.9% CPU · 36.5 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/1 · TLS | **34,230**<br><sub>(163 MiB / 42.9% CPU · 50.2 µs CPU/req)</sub> | **34,799**<br><sub>(114 MiB / 41.7% CPU · 48.0 µs CPU/req)</sub> | 🥇 **38,629**<br><sub>(82 MiB / 43.2% CPU · 44.7 µs CPU/req)</sub> | **28,686**<br><sub>(119 MiB / 56% CPU · 78.1 µs CPU/req)</sub> | **32,275**<br><sub>(149 MiB / 44.3% CPU · 54.9 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | 🥇 **70,143**<br><sub>(159 MiB / 32% CPU · 18.2 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **44,674**<br><sub>(80 MiB / 44.9% CPU · 40.2 µs CPU/req)</sub> | **37,215**<br><sub>(118 MiB / 58.6% CPU · 63.0 µs CPU/req)</sub> | **51,164**<br><sub>(146 MiB / 41.8% CPU · 32.7 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **81,706**<br><sub>(172 MiB / 31% CPU · 15.2 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **56,517**<br><sub>(80 MiB / 44% CPU · 31.1 µs CPU/req)</sub> | **58,300**<br><sub>(118 MiB / 50% CPU · 34.3 µs CPU/req)</sub> | **63,301**<br><sub>(152 MiB / 39.2% CPU · 24.8 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/3 · QUIC | 🥇 **31,573**<br><sub>(199 MiB / 41.2% CPU · 52.2 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **15,261**<br><sub>(120 MiB / 57.5% CPU · 150.6 µs CPU/req)</sub> | **29,759**<br><sub>(169 MiB / 41.4% CPU · 55.6 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | **27,194**<br><sub>(156 MiB / 44.3% CPU · 65.2 µs CPU/req)</sub> | **45,492**<br><sub>(111 MiB / 23.3% CPU · 20.5 µs CPU/req)</sub> | 🥇 **46,135**<br><sub>(86 MiB / 27.8% CPU · 24.1 µs CPU/req)</sub> | **19,248**<br><sub>(125 MiB / 52.3% CPU · 108.6 µs CPU/req)</sub> | **23,858**<br><sub>(208 MiB / 47.4% CPU · 79.5 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | **16,546**<br><sub>(164 MiB / 45.6% CPU · 110.2 µs CPU/req)</sub> | **24,927**<br><sub>(118 MiB / 28.8% CPU · 46.2 µs CPU/req)</sub> | 🥇 **26,031**<br><sub>(88 MiB / 32.2% CPU · 49.4 µs CPU/req)</sub> | **10,278**<br><sub>(125 MiB / 57% CPU · 221.7 µs CPU/req)</sub> | **14,269**<br><sub>(214 MiB / 48.8% CPU · 136.7 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/2 · plain | **21,904**<br><sub>(158 MiB / 46.5% CPU · 85.0 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | 🥇 **24,181**<br><sub>(86 MiB / 36.1% CPU · 59.8 µs CPU/req)</sub> | **9,796**<br><sub>(124 MiB / 60.9% CPU · 248.5 µs CPU/req)</sub> | **18,753**<br><sub>(204 MiB / 49.1% CPU · 104.7 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | **35,824**<br><sub>(173 MiB / 43.5% CPU · 48.5 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | 🥇 **47,738**<br><sub>(87 MiB / 32.4% CPU · 27.1 µs CPU/req)</sub> | **28,725**<br><sub>(124 MiB / 48.9% CPU · 68.1 µs CPU/req)</sub> | **33,330**<br><sub>(222 MiB / 43.4% CPU · 52.1 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/3 · QUIC | 🥇 **20,225**<br><sub>(159 MiB / 41% CPU · 81.1 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **8,755**<br><sub>(125 MiB / 52.7% CPU · 240.7 µs CPU/req)</sub> | **17,526**<br><sub>(218 MiB / 44.4% CPU · 101.4 µs CPU/req)</sub> |

### MITM (TWP only)

Same Client×Origin wires with interception on (`compare-product` [44653985062](https://github.com/justcoding121/titanium-web-proxy/actions/runs/44653985062)). **Lite** = no-op handlers (unchanged-lite finish). **Full** = append-only header mutation (harness: one probe header each way; product: generic append-only relay via `MitmCompressedRelayHelper`). nginx/HAProxy/Envoy/YARP cannot MITM. **Lite÷Reverse** / **Full÷Reverse** vs bare reverse (**same job / comparison-group shard**). Completion gate: Lite ≥ **0.25×** and Full ≥ **0.25×** reverse sustain @ c=64 (median of 3 GHA runs); reverse TWP ÷ closest peer ≥ **0.40×**.

**v1 append-only relay (2026-08-27):** Pre-fix H2→H2 Full÷Reverse was **0.13–0.16×** ([32960766249](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32960766249)). Post-fix @ `df172718`: H2 plain→H2 plain Full **0.77–0.79×**, H3→H1 Full **0.91–0.93×**, all MITM arms ≥ **0.70×** on median of [33041445371](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33041445371), [33055267086](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33055267086), [33055272140](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33055272140).

**v2 drop-only + non-unique append (2026-08-27):** `MitmStaticRebuildHelper` rebuilds static HPACK/QPACK after 1–4 unique header drops; trailing non-unique appends stay on compressed relay. @ `41f4adee`: all MITM arms ≥ **0.70×** on GHA median ([33087088466](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087088466), [33087091622](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087091622), [33105885748](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33105885748) Linux; [33087085235](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087085235), [33087088466](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087088466), [33087091622](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087091622) Windows). H2 plain→H2 plain Full **0.77–0.79×** (Win) / **0.78×** (Lin).

| Client | Origin | Lite sustain | Full sustain | Lite÷Reverse | Full÷Reverse |
|---|---|---:|---:|---:|---:|
| HTTP/1 · plain | HTTP/1 · plain | **32,914**<br><sub>(93 MiB / 42% CPU · 51.0 µs CPU/req)</sub> | **32,556**<br><sub>(97 MiB / 42% CPU · 51.6 µs CPU/req)</sub> | **0.98×** | **0.97×** |
| HTTP/1 · plain | HTTP/1 · TLS | **44,572**<br><sub>(119 MiB / 37.6% CPU · 33.8 µs CPU/req)</sub> | **43,363**<br><sub>(118 MiB / 38.7% CPU · 35.7 µs CPU/req)</sub> | **1.03×** | **1×** |
| HTTP/1 · plain | HTTP/2 · plain | **37,472**<br><sub>(127 MiB / 42.9% CPU · 45.8 µs CPU/req)</sub> | **36,087**<br><sub>(121 MiB / 42.8% CPU · 47.4 µs CPU/req)</sub> | **0.99×** | **0.96×** |
| HTTP/1 · plain | HTTP/2 · TLS | **33,130**<br><sub>(157 MiB / 41.7% CPU · 50.4 µs CPU/req)</sub> | **32,250**<br><sub>(158 MiB / 41.8% CPU · 51.8 µs CPU/req)</sub> | **1×** | **0.97×** |
| HTTP/1 · plain | HTTP/3 · QUIC | **37,604**<br><sub>(144 MiB / 40.6% CPU · 43.2 µs CPU/req)</sub> | **36,916**<br><sub>(146 MiB / 40.4% CPU · 43.8 µs CPU/req)</sub> | **1×** | **0.98×** |
| HTTP/1 · TLS | HTTP/1 · plain | **45,298**<br><sub>(119 MiB / 40.4% CPU · 35.6 µs CPU/req)</sub> | **41,945**<br><sub>(121 MiB / 39.1% CPU · 37.3 µs CPU/req)</sub> | **1×** | **0.92×** |
| HTTP/1 · TLS | HTTP/1 · TLS | **32,374**<br><sub>(125 MiB / 39.3% CPU · 48.5 µs CPU/req)</sub> | **32,354**<br><sub>(125 MiB / 40.1% CPU · 49.6 µs CPU/req)</sub> | **0.99×** | **0.99×** |
| HTTP/1 · TLS | HTTP/2 · plain | **27,907**<br><sub>(143 MiB / 44% CPU · 63.1 µs CPU/req)</sub> | **26,720**<br><sub>(145 MiB / 43.7% CPU · 65.4 µs CPU/req)</sub> | **0.97×** | **0.93×** |
| HTTP/1 · TLS | HTTP/2 · TLS | **42,017**<br><sub>(173 MiB / 40.7% CPU · 38.7 µs CPU/req)</sub> | **41,338**<br><sub>(174 MiB / 40.8% CPU · 39.5 µs CPU/req)</sub> | **0.95×** | **0.94×** |
| HTTP/1 · TLS | HTTP/3 · QUIC | **26,107**<br><sub>(152 MiB / 43.4% CPU · 66.5 µs CPU/req)</sub> | **25,670**<br><sub>(153 MiB / 43.3% CPU · 67.5 µs CPU/req)</sub> | **0.99×** | **0.97×** |
| HTTP/2 · plain | HTTP/1 · plain | **42,082**<br><sub>(151 MiB / 43.7% CPU · 41.5 µs CPU/req)</sub> | **40,818**<br><sub>(144 MiB / 41.9% CPU · 41.1 µs CPU/req)</sub> | **0.97×** | **0.94×** |
| HTTP/2 · plain | HTTP/1 · TLS | **24,302**<br><sub>(169 MiB / 46.4% CPU · 76.4 µs CPU/req)</sub> | **23,584**<br><sub>(169 MiB / 46.2% CPU · 78.4 µs CPU/req)</sub> | **0.98×** | **0.95×** |
| HTTP/2 · plain | HTTP/2 · plain | **49,575**<br><sub>(141 MiB / 35.3% CPU · 28.5 µs CPU/req)</sub> | **48,193**<br><sub>(144 MiB / 35.3% CPU · 29.3 µs CPU/req)</sub> | **0.86×** | **0.83×** |
| HTTP/2 · plain | HTTP/2 · TLS | **71,214**<br><sub>(166 MiB / 34.4% CPU · 19.3 µs CPU/req)</sub> | **68,375**<br><sub>(166 MiB / 34.5% CPU · 20.2 µs CPU/req)</sub> | **0.9×** | **0.86×** |
| HTTP/2 · plain | HTTP/3 · QUIC | **34,690**<br><sub>(178 MiB / 41.5% CPU · 47.9 µs CPU/req)</sub> | **34,117**<br><sub>(186 MiB / 42% CPU · 49.2 µs CPU/req)</sub> | **0.99×** | **0.98×** |
| HTTP/2 · TLS | HTTP/1 · plain | **48,673**<br><sub>(169 MiB / 43.6% CPU · 35.8 µs CPU/req)</sub> | **48,341**<br><sub>(169 MiB / 43.7% CPU · 36.2 µs CPU/req)</sub> | **0.95×** | **0.95×** |
| HTTP/2 · TLS | HTTP/1 · TLS | **33,335**<br><sub>(171 MiB / 44% CPU · 52.7 µs CPU/req)</sub> | **32,645**<br><sub>(168 MiB / 44% CPU · 53.9 µs CPU/req)</sub> | **0.97×** | **0.95×** |
| HTTP/2 · TLS | HTTP/2 · plain | **62,089**<br><sub>(162 MiB / 35.6% CPU · 22.9 µs CPU/req)</sub> | **60,060**<br><sub>(167 MiB / 35.5% CPU · 23.6 µs CPU/req)</sub> | **0.89×** | **0.86×** |
| HTTP/2 · TLS | HTTP/2 · TLS | **74,521**<br><sub>(180 MiB / 33.1% CPU · 17.8 µs CPU/req)</sub> | **69,929**<br><sub>(178 MiB / 33.7% CPU · 19.3 µs CPU/req)</sub> | **0.91×** | **0.86×** |
| HTTP/2 · TLS | HTTP/3 · QUIC | **31,053**<br><sub>(195 MiB / 41.4% CPU · 53.3 µs CPU/req)</sub> | **30,459**<br><sub>(190 MiB / 41.4% CPU · 54.4 µs CPU/req)</sub> | **0.98×** | **0.96×** |
| HTTP/3 · QUIC | HTTP/1 · plain | **27,083**<br><sub>(155 MiB / 45.6% CPU · 67.4 µs CPU/req)</sub> | **26,395**<br><sub>(158 MiB / 45.7% CPU · 69.3 µs CPU/req)</sub> | **1×** | **0.97×** |
| HTTP/3 · QUIC | HTTP/1 · TLS | **15,948**<br><sub>(162 MiB / 45% CPU · 112.8 µs CPU/req)</sub> | **15,855**<br><sub>(167 MiB / 45.1% CPU · 113.8 µs CPU/req)</sub> | **0.96×** | **0.96×** |
| HTTP/3 · QUIC | HTTP/2 · plain | **20,870**<br><sub>(158 MiB / 46.6% CPU · 89.3 µs CPU/req)</sub> | **21,591**<br><sub>(156 MiB / 48.1% CPU · 89.1 µs CPU/req)</sub> | **0.95×** | **0.99×** |
| HTTP/3 · QUIC | HTTP/2 · TLS | **35,696**<br><sub>(178 MiB / 42.8% CPU · 47.9 µs CPU/req)</sub> | **34,694**<br><sub>(178 MiB / 41.8% CPU · 48.2 µs CPU/req)</sub> | **1×** | **0.97×** |
| HTTP/3 · QUIC | HTTP/3 · QUIC | **19,827**<br><sub>(165 MiB / 42.7% CPU · 86.1 µs CPU/req)</sub> | **19,459**<br><sub>(162 MiB / 42.8% CPU · 88.0 µs CPU/req)</sub> | **0.98×** | **0.96×** |

## macOS — Titanium vs nginx vs HAProxy vs Envoy vs YARP

### Reverse

Median of **3 repeats** on `macos-15` (Apple Silicon M1, 3-core / 7 GB). Bare reverse 5×5 @ `4a97cb0d` — `compare-product` [44653985062](https://github.com/justcoding121/titanium-web-proxy/actions/runs/44653985062). Warmup 2s / measure 8s; concurrency 8, 16, 32, 64. Prefer TWP÷peer ratios over absolute RPS. **RPS cells** include median RSS / CPU at the peak-RPS step as `<br><sub>(MiB / CPU%)</sub>`. The RPS workflow installs Homebrew nginx (`http_v3_module`), Homebrew HAProxy with `USE_QUIC` (3.2 source fallback), Envoy (Homebrew bottle or pinned darwin-arm64 1.36.7), Homebrew `libmsquic` (+ `DYLD_*`), and YARP. Use the pinned `macos-15` label, not `macos-latest`. Product 5×5 is **~56-byte JSON keep-alive GET**; H2/H3 same-protocol cells are mostly header work with a tiny body (Titanium best case) — see [Why this comparison is fair](#user-content-why-this-comparison-is-fair).

| Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---:|---:|---:|---:|---:|
| HTTP/1 · plain | HTTP/1 · plain | 🥇 **17,930**<br><sub>(112 MiB / 26.8% CPU · 44.8 µs CPU/req)</sub> | **14,785**<br><sub>(68 MiB / 14% CPU · 28.4 µs CPU/req)</sub> | **13,436**<br><sub>(97 MiB / 20.6% CPU · 46.1 µs CPU/req)</sub> | **8,600**<br><sub>(112 MiB / 27.6% CPU · 96.3 µs CPU/req)</sub> | **17,900**<br><sub>(144 MiB / 30% CPU · 50.3 µs CPU/req)</sub> |
| HTTP/1 · plain | HTTP/1 · TLS | 🥇 **22,931**<br><sub>(123 MiB / 29.4% CPU · 38.5 µs CPU/req)</sub> | **17,410**<br><sub>(80 MiB / 14.9% CPU · 25.7 µs CPU/req)</sub> | **21,320**<br><sub>(107 MiB / 19.7% CPU · 27.7 µs CPU/req)</sub> | **8,138**<br><sub>(114 MiB / 15.2% CPU · 56.2 µs CPU/req)</sub> | **19,862**<br><sub>(155 MiB / 31.5% CPU · 47.5 µs CPU/req)</sub> |
| HTTP/1 · plain | HTTP/2 · plain | 🥇 **29,980**<br><sub>(130 MiB / 34.4% CPU · 34.4 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **15,060**<br><sub>(117 MiB / 21.4% CPU · 42.5 µs CPU/req)</sub> | **15,521**<br><sub>(111 MiB / 26.4% CPU · 51.0 µs CPU/req)</sub> | **29,338**<br><sub>(146 MiB / 35.2% CPU · 36.0 µs CPU/req)</sub> |
| HTTP/1 · plain | HTTP/2 · TLS | **24,053**<br><sub>(151 MiB / 39.7% CPU · 49.6 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | 🥇 **25,047**<br><sub>(193 MiB / 32.6% CPU · 39.0 µs CPU/req)</sub> | **12,846**<br><sub>(112 MiB / 34.5% CPU · 80.6 µs CPU/req)</sub> | **21,490**<br><sub>(155 MiB / 35.1% CPU · 49.1 µs CPU/req)</sub> |
| HTTP/1 · plain | HTTP/3 · QUIC | **7,457**<br><sub>(144 MiB / 27.3% CPU · 110.0 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **6,118**<br><sub>(116 MiB / 29.4% CPU · 144.2 µs CPU/req)</sub> | 🥇 **10,356**<br><sub>(163 MiB / 34.5% CPU · 99.8 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/1 · plain | **20,800**<br><sub>(139 MiB / 34.6% CPU · 50.0 µs CPU/req)</sub> | **16,527**<br><sub>(85 MiB / 15.5% CPU · 28.1 µs CPU/req)</sub> | 🥇 **20,925**<br><sub>(115 MiB / 26.6% CPU · 38.1 µs CPU/req)</sub> | **8,683**<br><sub>(122 MiB / 26.9% CPU · 93.1 µs CPU/req)</sub> | **8,442**<br><sub>(167 MiB / 25.4% CPU · 90.1 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/1 · TLS | **14,308**<br><sub>(149 MiB / 31.8% CPU · 66.7 µs CPU/req)</sub> | **9,989**<br><sub>(90 MiB / 15.3% CPU · 46.0 µs CPU/req)</sub> | 🥇 **16,139**<br><sub>(117 MiB / 26.4% CPU · 49.0 µs CPU/req)</sub> | **6,596**<br><sub>(123 MiB / 19.7% CPU · 89.4 µs CPU/req)</sub> | **9,158**<br><sub>(168 MiB / 24.2% CPU · 79.2 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/2 · plain | 🥇 **16,596**<br><sub>(152 MiB / 34.1% CPU · 61.6 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **10,997**<br><sub>(117 MiB / 21.4% CPU · 58.5 µs CPU/req)</sub> | **11,786**<br><sub>(122 MiB / 30.6% CPU · 78.0 µs CPU/req)</sub> | **14,094**<br><sub>(169 MiB / 23.5% CPU · 50.1 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/2 · TLS | **10,630**<br><sub>(168 MiB / 20.1% CPU · 56.6 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | 🥇 **23,641**<br><sub>(122 MiB / 28.4% CPU · 36.0 µs CPU/req)</sub> | **14,075**<br><sub>(123 MiB / 24.3% CPU · 51.8 µs CPU/req)</sub> | **17,100**<br><sub>(160 MiB / 28% CPU · 49.2 µs CPU/req)</sub> |
| HTTP/1 · TLS | HTTP/3 · QUIC | 🥇 **10,132**<br><sub>(159 MiB / 34.2% CPU · 101.1 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **6,859**<br><sub>(126 MiB / 25.2% CPU · 110.1 µs CPU/req)</sub> | **9,454**<br><sub>(173 MiB / 23% CPU · 72.9 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | **19,322**<br><sub>(171 MiB / 28.4% CPU · 44.1 µs CPU/req)</sub> | **17,774**<br><sub>(70 MiB / 16.7% CPU · 28.2 µs CPU/req)</sub> | **18,560**<br><sub>(116 MiB / 20.5% CPU · 33.1 µs CPU/req)</sub> | **10,496**<br><sub>(112 MiB / 19% CPU · 54.4 µs CPU/req)</sub> | 🥇 **20,464**<br><sub>(152 MiB / 33.5% CPU · 49.2 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/1 · TLS | **16,132**<br><sub>(176 MiB / 26.7% CPU · 49.6 µs CPU/req)</sub> | **15,435**<br><sub>(81 MiB / 16.7% CPU · 32.4 µs CPU/req)</sub> | 🥇 **20,450**<br><sub>(116 MiB / 24.9% CPU · 36.5 µs CPU/req)</sub> | **12,953**<br><sub>(114 MiB / 24.1% CPU · 55.8 µs CPU/req)</sub> | **16,505**<br><sub>(162 MiB / 33.9% CPU · 61.5 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/2 · plain | 🥇 **33,759**<br><sub>(163 MiB / 27.9% CPU · 24.8 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **21,275**<br><sub>(120 MiB / 23% CPU · 32.4 µs CPU/req)</sub> | **21,552**<br><sub>(111 MiB / 32.8% CPU · 45.6 µs CPU/req)</sub> | **28,252**<br><sub>(158 MiB / 27.5% CPU · 29.2 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/2 · TLS | **20,958**<br><sub>(167 MiB / 23.6% CPU · 33.8 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **17,842**<br><sub>(109 MiB / 22.5% CPU · 37.8 µs CPU/req)</sub> | **12,538**<br><sub>(112 MiB / 23.4% CPU · 55.9 µs CPU/req)</sub> | 🥇 **25,809**<br><sub>(156 MiB / 31.7% CPU · 36.8 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/3 · QUIC | **8,816**<br><sub>(186 MiB / 30.7% CPU · 104.5 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **7,565**<br><sub>(115 MiB / 24.5% CPU · 97.0 µs CPU/req)</sub> | 🥇 **9,872**<br><sub>(166 MiB / 32.2% CPU · 97.9 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | **20,192**<br><sub>(175 MiB / 26.7% CPU · 39.7 µs CPU/req)</sub> | **18,916**<br><sub>(84 MiB / 15% CPU · 23.7 µs CPU/req)</sub> | **19,073**<br><sub>(125 MiB / 24.4% CPU · 38.3 µs CPU/req)</sub> | **12,530**<br><sub>(122 MiB / 27.4% CPU · 65.7 µs CPU/req)</sub> | 🥇 **22,474**<br><sub>(159 MiB / 34% CPU · 45.4 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/1 · TLS | 🥇 **14,217**<br><sub>(173 MiB / 26.5% CPU · 55.8 µs CPU/req)</sub> | **9,616**<br><sub>(90 MiB / 15.1% CPU · 47.1 µs CPU/req)</sub> | **13,900**<br><sub>(117 MiB / 23.5% CPU · 50.7 µs CPU/req)</sub> | **10,892**<br><sub>(123 MiB / 29.1% CPU · 80.0 µs CPU/req)</sub> | **11,439**<br><sub>(172 MiB / 25.7% CPU · 67.5 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | 🥇 **38,801**<br><sub>(180 MiB / 29.3% CPU · 22.6 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **21,438**<br><sub>(137 MiB / 29% CPU · 40.6 µs CPU/req)</sub> | **22,453**<br><sub>(120 MiB / 30.7% CPU · 41.0 µs CPU/req)</sub> | **32,101**<br><sub>(163 MiB / 34.5% CPU · 32.2 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **29,370**<br><sub>(189 MiB / 27.7% CPU · 28.3 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | **16,298**<br><sub>(119 MiB / 24.2% CPU · 44.6 µs CPU/req)</sub> | **18,551**<br><sub>(121 MiB / 30.4% CPU · 49.2 µs CPU/req)</sub> | **28,059**<br><sub>(161 MiB / 37.3% CPU · 39.9 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/3 · QUIC | 🥇 **8,111**<br><sub>(196 MiB / 29.9% CPU · 110.5 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **7,312**<br><sub>(123 MiB / 29.1% CPU · 119.3 µs CPU/req)</sub> | **7,472**<br><sub>(176 MiB / 26.8% CPU · 107.4 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | **4,553**<br><sub>(139 MiB / 31.3% CPU · 206.4 µs CPU/req)</sub> | 🥇 **8,503**<br><sub>(87 MiB / 9% CPU · 31.8 µs CPU/req)</sub> | **7,207**<br><sub>(107 MiB / 16.7% CPU · 69.6 µs CPU/req)</sub> | **4,016**<br><sub>(126 MiB / 38.7% CPU · 288.8 µs CPU/req)</sub> | **4,399**<br><sub>(191 MiB / 33.3% CPU · 227.0 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | **4,264**<br><sub>(152 MiB / 34.1% CPU · 240.1 µs CPU/req)</sub> | 🥇 **9,695**<br><sub>(93 MiB / 13.6% CPU · 42.1 µs CPU/req)</sub> | **7,240**<br><sub>(110 MiB / 19.3% CPU · 79.9 µs CPU/req)</sub> | **4,306**<br><sub>(127 MiB / 33.2% CPU · 231.1 µs CPU/req)</sub> | **4,812**<br><sub>(202 MiB / 33.1% CPU · 206.2 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/2 · plain | **6,578**<br><sub>(138 MiB / 25.2% CPU · 115.1 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | 🥇 **13,068**<br><sub>(110 MiB / 16.1% CPU · 36.8 µs CPU/req)</sub> | **8,764**<br><sub>(124 MiB / 35.9% CPU · 122.7 µs CPU/req)</sub> | **7,229**<br><sub>(202 MiB / 28.7% CPU · 119.2 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | **4,964**<br><sub>(147 MiB / 27.3% CPU · 165.1 µs CPU/req)</sub> | *Not possible (no H2 upstream)* | 🥇 **7,863**<br><sub>(110 MiB / 18.1% CPU · 69.1 µs CPU/req)</sub> | **6,148**<br><sub>(124 MiB / 33.9% CPU · 165.6 µs CPU/req)</sub> | **5,514**<br><sub>(205 MiB / 27.3% CPU · 148.7 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/3 · QUIC | 🥇 **6,074**<br><sub>(142 MiB / 37.7% CPU · 186.2 µs CPU/req)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **4,601**<br><sub>(123 MiB / 33.8% CPU · 220.2 µs CPU/req)</sub> | **5,011**<br><sub>(196 MiB / 37.7% CPU · 225.5 µs CPU/req)</sub> |

## Editions (CLI / Plus / Intercept)

**Note:** `twp-reverse-http1` and other library rows use Core with **probe-tuned** settings (no logging, no Via header, probe-warmed certs). Edition rows use `titanium run -c twp.yaml` **product defaults** — prefer the ÷baseline ratio column over absolute RPS. Inspector GUI is not spawnable in the harness; session-path overhead is `twp-cli-intercept-http1` (route `RequestHeaderSet` transform). Pre-origin Plus middleware (CIDR/WAF deny-list/JWT/rate-limit/cache) runs on H1 terminate-lite without `SessionEventArgs`; a cache hit skips the origin. WAF is split into two rows: `twp-cli-plus-waf-http1` is deny-list only (`waf.mode=off`, denyPaths that miss `/`); `twp-cli-plus-waf-screen-http1` turns screening on (`waf.mode=detect`, no denyPaths) so headers/cookies/small bodies force the session path. `twp-cli-extauth-http1` pays one allow subrequest per request. `twp-cli-plugin-dotnet-noop-http1` is route middleware whose .NET `PassthroughPlugin` only calls `next`. JWT caches successful bearer validations. CORS only adds response headers on the way out, so it stays on terminate-lite. Circuit breaker and idempotent retry have to see the request or the status code, so they stay on the session path and should land near the intercept row. Maintainer gate thresholds live under [Maintainer notes](#user-content-maintainer-notes).

Median of **3** repeats on `ubuntu-26.04`. Linux rows in this paste were re-measured @ `4a97cb0d` (Actions [44654056788](https://github.com/justcoding121/titanium-web-proxy/actions/runs/44654056788)). Every edition row in the table was re-measured in that run. Warmup 2s / measure 8s; concurrency 8–64; sustain = median peak RPS among SLO-pass steps @ **c=64**. **RPS cells** show sustain; `<sub>` holds peak (when higher) plus `(MiB / CPU%)`. The Gate column is that script's floor.

| Arm | Linux | Lin÷ | Gate |
|---|---:|---:|---|
| `twp-cli-reverse-http1` vs library | **46,816**<br><sub>(239 MiB / 39.7% CPU · 33.9 µs CPU/req)</sub> | **1.02×** | ≥ **0.50×** |
| `twp-cli-reverse-http1-tls` vs library TLS | **34,897**<br><sub>(208 MiB / 42.8% CPU · 49.1 µs CPU/req)</sub> | **1.00×** | ≥ **0.50×** |
| `twp-cli-reverse-http1-route` vs CLI | **60,066**<br><sub>(243 MiB / 38.9% CPU · 25.9 µs CPU/req)</sub> | **1.01×** | ≥ **0.50×** |
| `twp-cli-plus-base-http1` vs CLI | **50,661**<br><sub>(178 MiB / 39.0% CPU · 30.8 µs CPU/req)</sub> | **0.98×** | ≥ **0.50×** |
| `twp-cli-plus-cache-http1` (cold) vs CLI | **49,678**<br><sub>(246 MiB / 42.2% CPU · 34.0 µs CPU/req)</sub> | **0.83×** | ≥ **0.50×** |
| `twp-cli-intercept-http1` vs CLI | **33,344**<br><sub>(188 MiB / 45.0% CPU · 54.0 µs CPU/req)</sub> | **0.77×** | ≥ **0.50×** |
| `twp-cli-plus-waf-http1` vs CLI | **50,298**<br><sub>(242 MiB / 38.1% CPU · 30.3 µs CPU/req)</sub> | **0.97×** | ≥ **0.50×** |
| `twp-cli-plus-waf-screen-http1` vs CLI | **41,607**<br><sub>(312 MiB / 43.0% CPU · 41.3 µs CPU/req)</sub> | **0.83×** | ≥ **0.50×** |
| `twp-cli-plus-cidr-http1` vs CLI | **42,509**<br><sub>(180 MiB / 42.3% CPU · 39.8 µs CPU/req)</sub> | **0.97×** | ≥ **0.50×** |
| `twp-cli-plus-jwt-http1` vs CLI | **56,181**<br><sub>(269 MiB / 40.2% CPU · 28.6 µs CPU/req)</sub> | **1.00×** | ≥ **0.50×** |
| `twp-cli-plus-ratelimit-http1` vs CLI | **58,933**<br><sub>(241 MiB / 40.4% CPU · 27.4 µs CPU/req)</sub> | **0.97×** | ≥ **0.50×** |
| `twp-cli-plus-fleet-ratelimit-http1` vs CLI | **49,467**<br><sub>(185 MiB / 39.7% CPU · 32.1 µs CPU/req)</sub> | **0.98×** | ≥ **0.50×** |
| `twp-cli-plus-manager-agent-http1` vs CLI | **41,427**<br><sub>(184 MiB / 40.6% CPU · 39.2 µs CPU/req)</sub> | **0.97×** | ≥ **0.50×** |
| `twp-cli-plus-resilience-http1` vs CLI | **43,009**<br><sub>(187 MiB / 40.9% CPU · 38.0 µs CPU/req)</sub> | **0.99×** | ≥ **0.50×** |
| `twp-cli-plus-discovery-file-http1` vs CLI | **43,422**<br><sub>(185 MiB / 41.9% CPU · 38.6 µs CPU/req)</sub> | **1.00×** | ≥ **0.50×** |
| `twp-cli-plus-metrics-scrape-http1` vs CLI | **58,149**<br><sub>(247 MiB / 39.6% CPU · 27.3 µs CPU/req)</sub> | **0.98×** | ≥ **0.50×** |
| `twp-cli-plus-cache-hit-http1` vs cache cold | **120,671**<br><sub>(245 MiB / 43.7% CPU · 14.5 µs CPU/req)</sub> | **2.43×** | ≥ **0.50×** |
| `twp-cli-plus-cors-http1` vs CLI | **28,895**<br><sub>(178 MiB / 44.7% CPU · 61.9 µs CPU/req)</sub> | **0.92×** | ≥ **0.50×** |
| `twp-cli-plus-circuit-http1` vs CLI | **51,284**<br><sub>(253 MiB / 41.3% CPU · 32.2 µs CPU/req)</sub> | **0.86×** | ≥ **0.50×** |
| `twp-cli-plus-retry-http1` vs CLI | **52,836**<br><sub>(249 MiB / 41.0% CPU · 31.1 µs CPU/req)</sub> | **0.86×** | ≥ **0.50×** |
| `twp-cli-extauth-http1` vs CLI | **23,111**<br><sub>(194 MiB / 44.9% CPU · 77.7 µs CPU/req)</sub> | **0.53×** | ≥ **0.50×** |
| `twp-cli-static-http1` vs CLI | **81,840**<br><sub>(173 MiB / 44.4% CPU · 21.7 µs CPU/req)</sub> | **1.86×** | ≥ **0.50×** |
| `twp-cli-logging-http1` vs CLI | **32,123**<br><sub>(176 MiB / 42.9% CPU · 53.4 µs CPU/req)</sub> | **1.00×** | ≥ **0.50×** |
| `twp-cli-lb-leasttime-http1` vs route | **46,728**<br><sub>(213 MiB / 58.5% CPU · 50.1 µs CPU/req)</sub> | **1.00×** | ≥ **0.50×** |
| `twp-cli-dialect-twp-http1` vs CLI | **45,740**<br><sub>(234 MiB / 39.5% CPU · 34.6 µs CPU/req)</sub> | **1.00×** | ≥ **0.50×** |
| `twp-cli-script-lua-noop-http1` vs route | **19,565**<br><sub>(195 MiB / 54.9% CPU · 112.2 µs CPU/req)</sub> | **0.61×** | ≥ **0.50×** |
| `twp-cli-script-lua-header-http1` vs route | **18,798**<br><sub>(194 MiB / 55.8% CPU · 118.8 µs CPU/req)</sub> | **0.59×** | ≥ **0.50×** |
| `twp-cli-script-wasm-noop-http1` vs route | **36,396**<br><sub>(264 MiB / 45.2% CPU · 49.7 µs CPU/req)</sub> | **0.80×** | ≥ **0.50×** |
| `twp-cli-script-lua-slow-upstream-http1` vs slow upstream | **3,040**<br><sub>(227 MiB / 17.8% CPU · 233.6 µs CPU/req)</sub> | **0.99×** | ≥ **0.50×** |
| `twp-cli-script-1in50-http1` vs route | **49,149**<br><sub>(259 MiB / 43.0% CPU · 35.0 µs CPU/req)</sub> | **0.81×** | ≥ **0.50×** |
| `twp-cli-plugin-dotnet-noop-http1` vs route | **22,995**<br><sub>(189 MiB / 48.1% CPU · 83.7 µs CPU/req)</sub> | **0.73×** | ≥ **0.50×** |
| `twp-cli-geo-http1` vs route | **59,461**<br><sub>(243 MiB / 38.2% CPU · 25.7 µs CPU/req)</sub> | **1.02×** | ≥ **0.50×** |
| `twp-cli-compress-h2-h2` vs route | **33,583**<br><sub>(178 MiB / 41.5% CPU · 49.4 µs CPU/req)</sub> | **1.04×** | ≥ **0.50×** |
| `twp-cli-compress-h3` vs route | **43,930**<br><sub>(178 MiB / 41.8% CPU · 38.0 µs CPU/req)</sub> | **0.99×** | ≥ **0.50×** |
| `twp-cli-health-grpc-http1` vs route | **43,759**<br><sub>(179 MiB / 41.5% CPU · 38.0 µs CPU/req)</sub> | **1.01×** | ≥ **0.50×** |
| `twp-cli-zoneaware-http1` vs route | **31,857**<br><sub>(178 MiB / 43.1% CPU · 54.1 µs CPU/req)</sub> | **1.00×** | ≥ **0.50×** |
| `twp-cli-tcp-tlsterm` vs route | **42,983**<br><sub>(179 MiB / 41.2% CPU · 38.4 µs CPU/req)</sub> | **1.00×** | ≥ **0.50×** |
| `twp-ws-h3` vs route | **31,368**<br><sub>(178 MiB / 42.9% CPU · 54.7 µs CPU/req)</sub> | **1.00×** | ≥ **0.50×** |
| `twp-cli-compress-zstd-http1` vs route | **31,146**<br><sub>(178 MiB / 42.7% CPU · 54.8 µs CPU/req)</sub> | **1.00×** | ≥ **0.50×** |
| `twp-cli-overload-http1` vs route | **31,320**<br><sub>(178 MiB / 43.1% CPU · 55.1 µs CPU/req)</sub> | **1.00×** | ≥ **0.50×** |
| `twp-cli-hotrestart-http1` vs route | **60,967**<br><sub>(243 MiB / 39.1% CPU · 25.7 µs CPU/req)</sub> | **1.00×** | ≥ **0.50×** |

`validate-edition-gates.ps1` floors are **0.50×**. Each ÷ column uses the two arms from the same job. Circuit breaker and idempotent retry stay on the session path, so a ratio near intercept is expected. Laptop smoke ratios stay on [Performance Local Lab — Editions](Performance-Local-Lab#user-content-editions-cli--plus-stress).

## Heavier reverse workloads

Same runners and harness as the tiny-GET tables, but with larger bodies, POST, lossy links, TLS cost, and architecture-sensitive paths (slow consumer / early response / duplex). **PUT with the same body is the same proxy work as POST; DELETE with no body matches GET** — only POST is published. Bodies/POST/lossy stay **half-duplex**. Laptop numbers are on [Performance Local Lab](Performance-Local-Lab#user-content-architecture-sensitive). How maintainers refresh these tables (modes, shards, paste scripts) is under [Maintainer notes](#user-content-maintainer-notes). **Larger-body check:** 64 / 256 KiB H2 TLS→H2 TLS is where body copy dominates headers — ratios vs YARP are in the tables below (@ `4a97cb0d`), unlike the tiny-GET H2↔H2 medals above. Those cells also show goodput (MiB/s) and µs CPU/req, and the medal on those rows follows goodput.

Lossy link = **userspace** delay/drop shim (not kernel `netem`): TCP gets per-buffer delay + occasional whole-connection stalls (honest head-of-line for multiplexed HTTP/2); UDP is **loss% only** (no per-datagram delay) + drops (QUIC / MsQuic-safe). Lossy tables publish HTTP/1, HTTP/2, and HTTP/3.

### Windows — heavier reverse GET (64 KiB / 256 KiB)

Median of **3** repeats on `windows-latest` @ `4a97cb0d`. Source: Actions [44653991213](https://github.com/justcoding121/titanium-web-proxy/actions/runs/44653991213) (`compare-bodies`). Warmup 2s / measure 8s. **RPS cells** include `(MiB / CPU%)` footprints.

*Not possible:* **HAProxy** and **Envoy** columns are omitted (no official Windows port).

























| Body | Client | Origin | TWP | nginx | YARP |
|---|---|---|---:|---:|---:|
| 64 KiB | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **9,359**<br><sub>(97 MiB / 43.2% CPU · 585 MiB/s · 184.8 µs CPU/req)</sub> | **695**<br><sub>(144 MiB / 24.7% CPU · 43 MiB/s · 1423.5 µs CPU/req)</sub> | **7,711**<br><sub>(137 MiB / 46.2% CPU · 482 MiB/s · 239.9 µs CPU/req)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **9,079**<br><sub>(198 MiB / 49.9% CPU · 567 MiB/s · 219.6 µs CPU/req)</sub> | **765**<br><sub>(144 MiB / 24.9% CPU · 48 MiB/s · 1301.4 µs CPU/req)</sub> | **8,430**<br><sub>(130 MiB / 50.2% CPU · 527 MiB/s · 238.1 µs CPU/req)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **3,813**<br><sub>(140 MiB / 42.9% CPU · 238 MiB/s · 449.7 µs CPU/req)</sub> | *Not possible (no QUIC)* | **1,960**<br><sub>(184 MiB / 54.2% CPU · 122 MiB/s · 1106.6 µs CPU/req)</sub> |
| 256 KiB | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **3,009**<br><sub>(129 MiB / 38.4% CPU · 752 MiB/s · 509.9 µs CPU/req)</sub> | **194**<br><sub>(145 MiB / 24.7% CPU · 48 MiB/s · 5101.2 µs CPU/req)</sub> | **2,590**<br><sub>(139 MiB / 44.3% CPU · 647 MiB/s · 683.7 µs CPU/req)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/1 · plain | **3,995**<br><sub>(179 MiB / 43.7% CPU · 999 MiB/s · 437.3 µs CPU/req)</sub> | **303**<br><sub>(144 MiB / 24.7% CPU · 76 MiB/s · 3263.6 µs CPU/req)</sub> | 🥇 **4,272**<br><sub>(132 MiB / 43.8% CPU · 1,068 MiB/s · 410.5 µs CPU/req)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/1 · plain | **667**<br><sub>(116 MiB / 47.0% CPU · 167 MiB/s · 2821.5 µs CPU/req)</sub> | *Not possible (no QUIC)* | 🥇 **1,057**<br><sub>(175 MiB / 47.0% CPU · 264 MiB/s · 1777.8 µs CPU/req)</sub> |
| 64 KiB | HTTP/2 · plain | HTTP/1 · plain | 🥇 **15,569**<br><sub>(172 MiB / 39.7% CPU · 973 MiB/s · 101.9 µs CPU/req)</sub> | **4,892**<br><sub>(130 MiB / 24.5% CPU · 306 MiB/s · 200.1 µs CPU/req)</sub> | **14,480**<br><sub>(115 MiB / 43.7% CPU · 905 MiB/s · 120.8 µs CPU/req)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/2 · plain | 🥇 **10,824**<br><sub>(173 MiB / 42.1% CPU · 676 MiB/s · 155.5 µs CPU/req)</sub> | *Not possible* | **9,880**<br><sub>(161 MiB / 50.8% CPU · 617 MiB/s · 205.6 µs CPU/req)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **5,905**<br><sub>(168 MiB / 44.1% CPU · 369 MiB/s · 298.8 µs CPU/req)</sub> | *Not possible* | **5,407**<br><sub>(157 MiB / 48.0% CPU · 338 MiB/s · 355.3 µs CPU/req)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/2 · TLS | 🥇 **3,220**<br><sub>(128 MiB / 41.2% CPU · 201 MiB/s · 511.5 µs CPU/req)</sub> | *Not possible* | **1,654**<br><sub>(203 MiB / 52.1% CPU · 103 MiB/s · 1260.4 µs CPU/req)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **4,795**<br><sub>(152 MiB / 43.0% CPU · 300 MiB/s · 358.4 µs CPU/req)</sub> | *Not possible (no QUIC)* | **2,702**<br><sub>(197 MiB / 52.2% CPU · 169 MiB/s · 773.4 µs CPU/req)</sub> |
| 256 KiB | HTTP/2 · plain | HTTP/1 · plain | 🥇 **3,924**<br><sub>(158 MiB / 32.1% CPU · 981 MiB/s · 326.9 µs CPU/req)</sub> | **936**<br><sub>(129 MiB / 24.1% CPU · 234 MiB/s · 1031.0 µs CPU/req)</sub> | **3,680**<br><sub>(118 MiB / 41.0% CPU · 920 MiB/s · 445.1 µs CPU/req)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/2 · plain | 🥇 **2,516**<br><sub>(197 MiB / 41.3% CPU · 629 MiB/s · 656.9 µs CPU/req)</sub> | *Not possible* | **2,332**<br><sub>(166 MiB / 44.4% CPU · 583 MiB/s · 761.6 µs CPU/req)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **1,611**<br><sub>(200 MiB / 44.1% CPU · 403 MiB/s · 1094.7 µs CPU/req)</sub> | *Not possible* | **1,439**<br><sub>(140 MiB / 42.6% CPU · 360 MiB/s · 1184.5 µs CPU/req)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/2 · TLS | **86**<br><sub>(103 MiB / 38.8% CPU · 22 MiB/s · 17956.1 µs CPU/req)</sub> | *Not possible* | 🥇 **414**<br><sub>(184 MiB / 48.9% CPU · 104 MiB/s · 4717.0 µs CPU/req)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **461**<br><sub>(126 MiB / 45.2% CPU · 115 MiB/s · 3919.3 µs CPU/req)</sub> | *Not possible (no QUIC)* | **197**<br><sub>(118 MiB / 34.7% CPU · 49 MiB/s · 7062.5 µs CPU/req)</sub> |

nginx/Windows collapses on large reverse bodies in this harness; treat as same-OS only. Prefer the ratio columns; absolute RPS swings by VM.

### Linux — heavier reverse GET (64 KiB / 256 KiB)

Median of **3** repeats @ `4a97cb0d`. Source: Actions [44653991213](https://github.com/justcoding121/titanium-web-proxy/actions/runs/44653991213) (`compare-bodies`). Warmup 2s / measure 8s.


| Body | Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---|---:|---:|---:|---:|---:|
| 64 KiB | HTTP/1 · TLS | HTTP/1 · plain | **12,790**<br><sub>(120 MiB / 37.0% CPU · 799 MiB/s · 115.9 µs CPU/req)</sub> | **8,529**<br><sub>(107 MiB / 51.1% CPU · 533 MiB/s · 239.7 µs CPU/req)</sub> | 🥇 **13,103**<br><sub>(86 MiB / 39.2% CPU · 819 MiB/s · 119.8 µs CPU/req)</sub> | **10,471**<br><sub>(124 MiB / 46.7% CPU · 654 MiB/s · 178.4 µs CPU/req)</sub> | **9,708**<br><sub>(177 MiB / 46.2% CPU · 607 MiB/s · 190.4 µs CPU/req)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **7,541**<br><sub>(234 MiB / 43.6% CPU · 471 MiB/s · 231.3 µs CPU/req)</sub> | **4,825**<br><sub>(106 MiB / 53.0% CPU · 302 MiB/s · 439.6 µs CPU/req)</sub> | **6,644**<br><sub>(86 MiB / 49.2% CPU · 415 MiB/s · 296.3 µs CPU/req)</sub> | **6,933**<br><sub>(126 MiB / 50.3% CPU · 433 MiB/s · 290.3 µs CPU/req)</sub> | **6,327**<br><sub>(174 MiB / 49.9% CPU · 395 MiB/s · 315.2 µs CPU/req)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/1 · plain | **5,307**<br><sub>(190 MiB / 42.6% CPU · 332 MiB/s · 321.0 µs CPU/req)</sub> | **2,576**<br><sub>(118 MiB / 37.1% CPU · 161 MiB/s · 576.8 µs CPU/req)</sub> | 🥇 **5,393**<br><sub>(93 MiB / 40.0% CPU · 337 MiB/s · 297.1 µs CPU/req)</sub> | **3,953**<br><sub>(132 MiB / 52.7% CPU · 247 MiB/s · 533.4 µs CPU/req)</sub> | **3,514**<br><sub>(229 MiB / 52.7% CPU · 220 MiB/s · 600.0 µs CPU/req)</sub> |
| 256 KiB | HTTP/1 · TLS | HTTP/1 · plain | **2,848**<br><sub>(132 MiB / 34.1% CPU · 712 MiB/s · 479.0 µs CPU/req)</sub> | **1,718**<br><sub>(106 MiB / 54.4% CPU · 430 MiB/s · 1266.8 µs CPU/req)</sub> | 🥇 **3,021**<br><sub>(83 MiB / 31.9% CPU · 755 MiB/s · 422.0 µs CPU/req)</sub> | **2,727**<br><sub>(134 MiB / 35.9% CPU · 682 MiB/s · 525.9 µs CPU/req)</sub> | **2,450**<br><sub>(177 MiB / 45.6% CPU · 613 MiB/s · 744.5 µs CPU/req)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/1 · plain | **2,320**<br><sub>(198 MiB / 37.9% CPU · 580 MiB/s · 652.8 µs CPU/req)</sub> | **1,493**<br><sub>(106 MiB / 53.2% CPU · 373 MiB/s · 1425.0 µs CPU/req)</sub> | **2,176**<br><sub>(84 MiB / 43.9% CPU · 544 MiB/s · 807.0 µs CPU/req)</sub> | 🥇 **2,502**<br><sub>(144 MiB / 38.1% CPU · 626 MiB/s · 609.7 µs CPU/req)</sub> | **2,034**<br><sub>(178 MiB / 45.9% CPU · 508 MiB/s · 902.3 µs CPU/req)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/1 · plain | **1,624**<br><sub>(171 MiB / 45.1% CPU · 406 MiB/s · 1111.1 µs CPU/req)</sub> | **788**<br><sub>(118 MiB / 38.6% CPU · 197 MiB/s · 1956.4 µs CPU/req)</sub> | 🥇 **1,864**<br><sub>(94 MiB / 38.0% CPU · 466 MiB/s · 815.9 µs CPU/req)</sub> | **1,569**<br><sub>(146 MiB / 47.2% CPU · 392 MiB/s · 1204.2 µs CPU/req)</sub> | **1,270**<br><sub>(243 MiB / 52.5% CPU · 317 MiB/s · 1653.4 µs CPU/req)</sub> |
| 64 KiB | HTTP/2 · plain | HTTP/1 · plain | 🥇 **18,782**<br><sub>(215 MiB / 36.2% CPU · 1,174 MiB/s · 77.1 µs CPU/req)</sub> | **11,659**<br><sub>(78 MiB / 52.5% CPU · 729 MiB/s · 180.0 µs CPU/req)</sub> | **18,120**<br><sub>(70 MiB / 43.2% CPU · 1,133 MiB/s · 95.3 µs CPU/req)</sub> | **18,660**<br><sub>(115 MiB / 41.9% CPU · 1,166 MiB/s · 89.9 µs CPU/req)</sub> | **17,267**<br><sub>(145 MiB / 39.7% CPU · 1,079 MiB/s · 91.9 µs CPU/req)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/2 · plain | 🥇 **8,429**<br><sub>(208 MiB / 38.0% CPU · 527 MiB/s · 180.1 µs CPU/req)</sub> | *Not possible* | **5,951**<br><sub>(89 MiB / 46.1% CPU · 372 MiB/s · 310.1 µs CPU/req)</sub> | **6,942**<br><sub>(127 MiB / 47.7% CPU · 434 MiB/s · 274.8 µs CPU/req)</sub> | **5,936**<br><sub>(210 MiB / 47.3% CPU · 371 MiB/s · 318.7 µs CPU/req)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **5,756**<br><sub>(195 MiB / 39.5% CPU · 360 MiB/s · 274.6 µs CPU/req)</sub> | *Not possible* | **4,501**<br><sub>(88 MiB / 47.3% CPU · 281 MiB/s · 420.3 µs CPU/req)</sub> | **5,005**<br><sub>(132 MiB / 46.2% CPU · 313 MiB/s · 369.3 µs CPU/req)</sub> | **4,125**<br><sub>(192 MiB / 44.8% CPU · 258 MiB/s · 434.7 µs CPU/req)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/2 · TLS | **3,859**<br><sub>(174 MiB / 40.4% CPU · 241 MiB/s · 418.2 µs CPU/req)</sub> | *Not possible* | 🥇 **3,996**<br><sub>(97 MiB / 41.0% CPU · 250 MiB/s · 410.6 µs CPU/req)</sub> | **2,896**<br><sub>(139 MiB / 50.9% CPU · 181 MiB/s · 703.0 µs CPU/req)</sub> | **2,767**<br><sub>(244 MiB / 49.4% CPU · 173 MiB/s · 714.3 µs CPU/req)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **6,145**<br><sub>(210 MiB / 41.9% CPU · 384 MiB/s · 272.8 µs CPU/req)</sub> | **3,084**<br><sub>(124 MiB / 36.5% CPU · 193 MiB/s · 473.2 µs CPU/req)</sub> | **5,988**<br><sub>(98 MiB / 42.0% CPU · 374 MiB/s · 280.4 µs CPU/req)</sub> | **4,725**<br><sub>(133 MiB / 51.7% CPU · 295 MiB/s · 437.6 µs CPU/req)</sub> | **4,096**<br><sub>(260 MiB / 52.2% CPU · 256 MiB/s · 510.0 µs CPU/req)</sub> |
| 256 KiB | HTTP/2 · plain | HTTP/1 · plain | **4,949**<br><sub>(197 MiB / 25.9% CPU · 1,237 MiB/s · 208.9 µs CPU/req)</sub> | **3,349**<br><sub>(78 MiB / 48.3% CPU · 837 MiB/s · 577.0 µs CPU/req)</sub> | **4,940**<br><sub>(69 MiB / 29.6% CPU · 1,235 MiB/s · 239.4 µs CPU/req)</sub> | 🥇 **5,432**<br><sub>(133 MiB / 27.4% CPU · 1,358 MiB/s · 201.7 µs CPU/req)</sub> | **4,633**<br><sub>(150 MiB / 30.3% CPU · 1,158 MiB/s · 261.8 µs CPU/req)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/2 · plain | 🥇 **3,188**<br><sub>(269 MiB / 33.6% CPU · 797 MiB/s · 421.6 µs CPU/req)</sub> | *Not possible* | **2,859**<br><sub>(98 MiB / 37.7% CPU · 715 MiB/s · 527.2 µs CPU/req)</sub> | **2,962**<br><sub>(139 MiB / 35.6% CPU · 740 MiB/s · 480.6 µs CPU/req)</sub> | **2,329**<br><sub>(201 MiB / 40.8% CPU · 582 MiB/s · 700.2 µs CPU/req)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **1,566**<br><sub>(262 MiB / 38.2% CPU · 392 MiB/s · 976.6 µs CPU/req)</sub> | *Not possible* | **1,408**<br><sub>(95 MiB / 42.6% CPU · 352 MiB/s · 1211.1 µs CPU/req)</sub> | **1,508**<br><sub>(138 MiB / 39.2% CPU · 377 MiB/s · 1039.2 µs CPU/req)</sub> | **1,125**<br><sub>(179 MiB / 41.8% CPU · 281 MiB/s · 1486.0 µs CPU/req)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/2 · TLS | **719**<br><sub>(178 MiB / 50.3% CPU · 180 MiB/s · 2797.9 µs CPU/req)</sub> | *Not possible* | 🥇 **1,205**<br><sub>(104 MiB / 40.1% CPU · 301 MiB/s · 1331.7 µs CPU/req)</sub> | **954**<br><sub>(147 MiB / 46.0% CPU · 238 MiB/s · 1927.5 µs CPU/req)</sub> | **774**<br><sub>(238 MiB / 48.7% CPU · 193 MiB/s · 2516.9 µs CPU/req)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/1 · TLS | **1,225**<br><sub>(192 MiB / 43.5% CPU · 306 MiB/s · 1420.9 µs CPU/req)</sub> | **700**<br><sub>(123 MiB / 38.3% CPU · 175 MiB/s · 2189.7 µs CPU/req)</sub> | 🥇 **1,316**<br><sub>(96 MiB / 40.5% CPU · 329 MiB/s · 1229.7 µs CPU/req)</sub> | **1,222**<br><sub>(146 MiB / 45.1% CPU · 305 MiB/s · 1476.1 µs CPU/req)</sub> | **965**<br><sub>(262 MiB / 49.9% CPU · 241 MiB/s · 2068.6 µs CPU/req)</sub> |

Prefer the ratio columns; absolute RPS swings by VM.

### macOS — heavier reverse GET (64 KiB / 256 KiB)

Median of **3** repeats on `macos-15` (Apple Silicon M1, 3-core / 7 GB) @ `4a97cb0d`. Source: Actions [44653991213](https://github.com/justcoding121/titanium-web-proxy/actions/runs/44653991213) (`compare-bodies`). Warmup 2s / measure 8s.

| Body | Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---|---:|---:|---:|---:|---:|
| 64 KiB | HTTP/1 · TLS | HTTP/1 · plain | **4,281**<br><sub>(157 MiB / 34.9% CPU · 268 MiB/s · 244.2 µs CPU/req)</sub> | **3,625**<br><sub>(85 MiB / 18.1% CPU · 227 MiB/s · 149.5 µs CPU/req)</sub> | 🥇 **5,200**<br><sub>(124 MiB / 26.8% CPU · 325 MiB/s · 154.9 µs CPU/req)</sub> | **3,445**<br><sub>(133 MiB / 26.6% CPU · 215 MiB/s · 231.9 µs CPU/req)</sub> | **2,992**<br><sub>(182 MiB / 31.4% CPU · 187 MiB/s · 314.8 µs CPU/req)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **4,680**<br><sub>(234 MiB / 34.7% CPU · 292 MiB/s · 222.3 µs CPU/req)</sub> | **3,848**<br><sub>(88 MiB / 19.2% CPU · 240 MiB/s · 149.8 µs CPU/req)</sub> | **4,333**<br><sub>(123 MiB / 28.2% CPU · 271 MiB/s · 195.3 µs CPU/req)</sub> | **3,682**<br><sub>(144 MiB / 23.6% CPU · 230 MiB/s · 192.3 µs CPU/req)</sub> | **3,708**<br><sub>(177 MiB / 37.9% CPU · 232 MiB/s · 306.9 µs CPU/req)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/1 · plain | **1,743**<br><sub>(169 MiB / 31.1% CPU · 109 MiB/s · 535.7 µs CPU/req)</sub> | **1,778**<br><sub>(98 MiB / 22.4% CPU · 111 MiB/s · 377.5 µs CPU/req)</sub> | **1,671**<br><sub>(132 MiB / 27.3% CPU · 104 MiB/s · 490.3 µs CPU/req)</sub> | 🥇 **1,953**<br><sub>(133 MiB / 45.0% CPU · 122 MiB/s · 690.5 µs CPU/req)</sub> | **1,008**<br><sub>(226 MiB / 36.4% CPU · 63 MiB/s · 1082.3 µs CPU/req)</sub> |
| 256 KiB | HTTP/1 · TLS | HTTP/1 · plain | **1,630**<br><sub>(203 MiB / 29.2% CPU · 408 MiB/s · 537.3 µs CPU/req)</sub> | **1,506**<br><sub>(84 MiB / 19.9% CPU · 377 MiB/s · 395.7 µs CPU/req)</sub> | 🥇 **1,882**<br><sub>(119 MiB / 23.8% CPU · 471 MiB/s · 379.5 µs CPU/req)</sub> | **1,491**<br><sub>(154 MiB / 24.6% CPU · 373 MiB/s · 494.1 µs CPU/req)</sub> | **1,250**<br><sub>(193 MiB / 33.2% CPU · 312 MiB/s · 796.9 µs CPU/req)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/1 · plain | **1,550**<br><sub>(272 MiB / 32.1% CPU · 387 MiB/s · 621.3 µs CPU/req)</sub> | **1,596**<br><sub>(88 MiB / 22.8% CPU · 399 MiB/s · 428.7 µs CPU/req)</sub> | 🥇 **1,914**<br><sub>(118 MiB / 29.3% CPU · 479 MiB/s · 459.1 µs CPU/req)</sub> | **1,817**<br><sub>(156 MiB / 28.0% CPU · 454 MiB/s · 462.7 µs CPU/req)</sub> | **1,223**<br><sub>(176 MiB / 36.3% CPU · 306 MiB/s · 889.8 µs CPU/req)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/1 · plain | **303**<br><sub>(149 MiB / 36.5% CPU · 76 MiB/s · 3612.5 µs CPU/req)</sub> | 🥇 **601**<br><sub>(98 MiB / 26.8% CPU · 150 MiB/s · 1338.3 µs CPU/req)</sub> | **189**<br><sub>(104 MiB / 18.1% CPU · 47 MiB/s · 2873.2 µs CPU/req)</sub> | **401**<br><sub>(peak 407 · 148 MiB / 45.6% CPU · 100 MiB/s · 3417.1 µs CPU/req)</sub> | **238**<br><sub>(212 MiB / 32.9% CPU · 59 MiB/s · 4156.6 µs CPU/req)</sub> |
| 64 KiB | HTTP/2 · plain | HTTP/1 · plain | 🥇 **7,352**<br><sub>(230 MiB / 32.4% CPU · 459 MiB/s · 132.0 µs CPU/req)</sub> | **6,074**<br><sub>(74 MiB / 16.9% CPU · 380 MiB/s · 83.6 µs CPU/req)</sub> | **6,770**<br><sub>(111 MiB / 25.7% CPU · 423 MiB/s · 114.1 µs CPU/req)</sub> | **5,590**<br><sub>(135 MiB / 23.5% CPU · 349 MiB/s · 126.2 µs CPU/req)</sub> | **4,906**<br><sub>(174 MiB / 30.2% CPU · 307 MiB/s · 184.8 µs CPU/req)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/2 · plain | 🥇 **4,730**<br><sub>(230 MiB / 23.9% CPU · 296 MiB/s · 151.7 µs CPU/req)</sub> | *Not possible* | **3,764**<br><sub>(129 MiB / 24.6% CPU · 235 MiB/s · 196.2 µs CPU/req)</sub> | **3,502**<br><sub>(126 MiB / 22.1% CPU · 219 MiB/s · 189.6 µs CPU/req)</sub> | **3,015**<br><sub>(211 MiB / 30.3% CPU · 188 MiB/s · 301.1 µs CPU/req)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **5,664**<br><sub>(254 MiB / 35.1% CPU · 354 MiB/s · 185.8 µs CPU/req)</sub> | *Not possible* | **4,651**<br><sub>(128 MiB / 29.4% CPU · 291 MiB/s · 189.3 µs CPU/req)</sub> | **4,470**<br><sub>(127 MiB / 25.5% CPU · 279 MiB/s · 171.4 µs CPU/req)</sub> | **4,326**<br><sub>(215 MiB / 36.1% CPU · 270 MiB/s · 250.5 µs CPU/req)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/2 · TLS | **993**<br><sub>(164 MiB / 30.7% CPU · 62 MiB/s · 926.9 µs CPU/req)</sub> | *Not possible* | **1,071**<br><sub>(136 MiB / 24.6% CPU · 67 MiB/s · 689.4 µs CPU/req)</sub> | 🥇 **1,103**<br><sub>(131 MiB / 42.6% CPU · 69 MiB/s · 1158.6 µs CPU/req)</sub> | **646**<br><sub>(246 MiB / 33.4% CPU · 40 MiB/s · 1548.3 µs CPU/req)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/1 · TLS | **1,436**<br><sub>(183 MiB / 33.1% CPU · 90 MiB/s · 691.6 µs CPU/req)</sub> | 🥇 **1,704**<br><sub>(100 MiB / 22.9% CPU · 107 MiB/s · 403.8 µs CPU/req)</sub> | **1,256**<br><sub>(131 MiB / 27.6% CPU · 78 MiB/s · 659.9 µs CPU/req)</sub> | **1,231**<br><sub>(132 MiB / 43.2% CPU · 77 MiB/s · 1053.1 µs CPU/req)</sub> | **911**<br><sub>(231 MiB / 37.6% CPU · 57 MiB/s · 1238.6 µs CPU/req)</sub> |
| 256 KiB | HTTP/2 · plain | HTTP/1 · plain | **2,190**<br><sub>(242 MiB / 26.6% CPU · 548 MiB/s · 364.0 µs CPU/req)</sub> | **2,296**<br><sub>(74 MiB / 17.5% CPU · 574 MiB/s · 228.7 µs CPU/req)</sub> | 🥇 **2,382**<br><sub>(105 MiB / 22.0% CPU · 596 MiB/s · 276.7 µs CPU/req)</sub> | **2,015**<br><sub>(145 MiB / 24.5% CPU · 504 MiB/s · 365.3 µs CPU/req)</sub> | **1,918**<br><sub>(174 MiB / 29.5% CPU · 479 MiB/s · 461.3 µs CPU/req)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/2 · plain | 🥇 **1,839**<br><sub>(249 MiB / 31.1% CPU · 460 MiB/s · 506.8 µs CPU/req)</sub> | *Not possible* | **1,722**<br><sub>(139 MiB / 26.5% CPU · 430 MiB/s · 462.0 µs CPU/req)</sub> | **1,320**<br><sub>(132 MiB / 19.6% CPU · 330 MiB/s · 444.9 µs CPU/req)</sub> | **1,315**<br><sub>(216 MiB / 33.1% CPU · 329 MiB/s · 755.8 µs CPU/req)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **1,146**<br><sub>(265 MiB / 32.5% CPU · 287 MiB/s · 850.4 µs CPU/req)</sub> | *Not possible* | **920**<br><sub>(125 MiB / 22.1% CPU · 230 MiB/s · 721.2 µs CPU/req)</sub> | **656**<br><sub>(127 MiB / 16.7% CPU · 164 MiB/s · 764.6 µs CPU/req)</sub> | **464**<br><sub>(208 MiB / 19.1% CPU · 116 MiB/s · 1235.6 µs CPU/req)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/2 · TLS | **200**<br><sub>(154 MiB / 35.9% CPU · 50 MiB/s · 5376.3 µs CPU/req)</sub> | *Not possible* | 🥇 **453**<br><sub>(118 MiB / 26.4% CPU · 113 MiB/s · 1750.9 µs CPU/req)</sub> | **340**<br><sub>(peak 348 · 133 MiB / 40.3% CPU · 85 MiB/s · 3561.7 µs CPU/req)</sub> | **239**<br><sub>(225 MiB / 36.1% CPU · 60 MiB/s · 4526.4 µs CPU/req)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/1 · TLS | **217**<br><sub>(peak 252 · 147 MiB / 34.5% CPU · 54 MiB/s · 4761.6 µs CPU/req)</sub> | 🥇 **444**<br><sub>(peak 522 · 92 MiB / 24.4% CPU · 111 MiB/s · 1644.8 µs CPU/req)</sub> | **223**<br><sub>(peak 290 · 110 MiB / 23.7% CPU · 56 MiB/s · 3181.4 µs CPU/req)</sub> | **343**<br><sub>(135 MiB / 45.2% CPU · 86 MiB/s · 3958.6 µs CPU/req)</sub> | **292**<br><sub>(230 MiB / 35.4% CPU · 73 MiB/s · 3638.7 µs CPU/req)</sub> |

### Windows — POST 64 KiB request + 64 KiB response

Median of **3** repeats on `windows-latest` @ `4a97cb0d`. Source: Actions [44653997866](https://github.com/justcoding121/titanium-web-proxy/actions/runs/44653997866) (`compare-post`).

*Not possible:* **HAProxy** and **Envoy** columns are omitted (no official Windows port).

























| Client | Origin | TWP | nginx | YARP |
|---|---|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | 🥇 **6,077**<br><sub>(105 MiB / 43.3% CPU · 760 MiB/s · 285.2 µs CPU/req)</sub> | **412**<br><sub>(145 MiB / 24.8% CPU · 51 MiB/s · 2409.1 µs CPU/req)</sub> | **4,313**<br><sub>(144 MiB / 54.8% CPU · 539 MiB/s · 508.3 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **5,649**<br><sub>(230 MiB / 47.4% CPU · 706 MiB/s · 335.4 µs CPU/req)</sub> | **490**<br><sub>(149 MiB / 24.9% CPU · 61 MiB/s · 2030.6 µs CPU/req)</sub> | **5,184**<br><sub>(137 MiB / 48.0% CPU · 648 MiB/s · 370.1 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **4,409**<br><sub>(185 MiB / 46.7% CPU · 551 MiB/s · 423.5 µs CPU/req)</sub> | *Not possible (no QUIC)* | **3,245**<br><sub>(226 MiB / 50.6% CPU · 406 MiB/s · 623.6 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | 🥇 **7,179**<br><sub>(192 MiB / 40.8% CPU · 897 MiB/s · 227.5 µs CPU/req)</sub> | **3,719**<br><sub>(133 MiB / 24.8% CPU · 465 MiB/s · 266.2 µs CPU/req)</sub> | **6,945**<br><sub>(122 MiB / 42.5% CPU · 868 MiB/s · 244.5 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | 🥇 **4,324**<br><sub>(169 MiB / 39.9% CPU · 540 MiB/s · 368.9 µs CPU/req)</sub> | *Not possible* | **3,448**<br><sub>(142 MiB / 47.5% CPU · 431 MiB/s · 551.6 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **3,170**<br><sub>(167 MiB / 43.1% CPU · 396 MiB/s · 543.7 µs CPU/req)</sub> | *Not possible* | **2,414**<br><sub>(161 MiB / 46.9% CPU · 302 MiB/s · 776.9 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | 🥇 **2,290**<br><sub>(178 MiB / 42.1% CPU · 286 MiB/s · 734.9 µs CPU/req)</sub> | *Not possible* | **1,366**<br><sub>(213 MiB / 47.7% CPU · 171 MiB/s · 1396.4 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **2,695**<br><sub>(173 MiB / 46.9% CPU · 337 MiB/s · 695.3 µs CPU/req)</sub> | *Not possible (no QUIC)* | **1,927**<br><sub>(221 MiB / 49.1% CPU · 241 MiB/s · 1019.4 µs CPU/req)</sub> |

TWP leads H2→H1 POST (about **1.1–1.3×** YARP) and H3 POST (about **1.0–1.1×** YARP). H2 TLS→H2 TLS POST sustain is about **1.2–1.4×** YARP.

### Linux — POST 64 KiB request + 64 KiB response

Median of **3** repeats @ `4a97cb0d`. Source: Actions [44653997866](https://github.com/justcoding121/titanium-web-proxy/actions/runs/44653997866) (`compare-post`).


| Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---:|---:|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | **12,043**<br><sub>(127 MiB / 38.2% CPU · 1,505 MiB/s · 126.9 µs CPU/req)</sub> | **10,349**<br><sub>(107 MiB / 44.4% CPU · 1,294 MiB/s · 171.7 µs CPU/req)</sub> | 🥇 **12,378**<br><sub>(87 MiB / 40.5% CPU · 1,547 MiB/s · 130.9 µs CPU/req)</sub> | **11,650**<br><sub>(125 MiB / 44.2% CPU · 1,456 MiB/s · 151.6 µs CPU/req)</sub> | **7,898**<br><sub>(185 MiB / 52.9% CPU · 987 MiB/s · 267.9 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **4,176**<br><sub>(245 MiB / 45.7% CPU · 522 MiB/s · 437.5 µs CPU/req)</sub> | **3,521**<br><sub>(113 MiB / 48.0% CPU · 440 MiB/s · 545.2 µs CPU/req)</sub> | **4,131**<br><sub>(87 MiB / 45.1% CPU · 516 MiB/s · 436.7 µs CPU/req)</sub> | **3,692**<br><sub>(peak 4,605 · 119 MiB / 42.2% CPU · 462 MiB/s · 456.9 µs CPU/req)</sub> | **3,813**<br><sub>(175 MiB / 49.9% CPU · 477 MiB/s · 523.9 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **2,981**<br><sub>(217 MiB / 44.4% CPU · 373 MiB/s · 595.3 µs CPU/req)</sub> | **1,029**<br><sub>(115 MiB / 50.2% CPU · 129 MiB/s · 1949.3 µs CPU/req)</sub> | **2,744**<br><sub>(92 MiB / 44.9% CPU · 343 MiB/s · 654.0 µs CPU/req)</sub> | **0**<br><sub>(122 MiB / 0.2% CPU)</sub> | **2,433**<br><sub>(253 MiB / 52.0% CPU · 304 MiB/s · 855.2 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | **8,333**<br><sub>(243 MiB / 40.0% CPU · 1,042 MiB/s · 192.0 µs CPU/req)</sub> | **7,092**<br><sub>(86 MiB / 46.2% CPU · 887 MiB/s · 260.7 µs CPU/req)</sub> | 🥇 **9,559**<br><sub>(73 MiB / 39.0% CPU · 1,195 MiB/s · 163.1 µs CPU/req)</sub> | **6,048**<br><sub>(peak 8,448 · 108 MiB / 41.1% CPU · 756 MiB/s · 272.0 µs CPU/req)</sub> | **7,929**<br><sub>(158 MiB / 45.0% CPU · 991 MiB/s · 226.9 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | 🥇 **6,718**<br><sub>(203 MiB / 35.4% CPU · 840 MiB/s · 210.7 µs CPU/req)</sub> | *Not possible* | **5,282**<br><sub>(90 MiB / 42.9% CPU · 660 MiB/s · 324.7 µs CPU/req)</sub> | **6,141**<br><sub>(129 MiB / 42.0% CPU · 768 MiB/s · 273.8 µs CPU/req)</sub> | **4,026**<br><sub>(200 MiB / 45.5% CPU · 503 MiB/s · 452.4 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **5,164**<br><sub>(219 MiB / 37.1% CPU · 646 MiB/s · 287.3 µs CPU/req)</sub> | *Not possible* | **4,072**<br><sub>(89 MiB / 44.8% CPU · 509 MiB/s · 440.5 µs CPU/req)</sub> | **4,576**<br><sub>(135 MiB / 43.6% CPU · 572 MiB/s · 380.8 µs CPU/req)</sub> | **3,275**<br><sub>(196 MiB / 43.7% CPU · 409 MiB/s · 533.8 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | 🥇 **2,782**<br><sub>(232 MiB / 40.7% CPU · 348 MiB/s · 585.0 µs CPU/req)</sub> | *Not possible* | **2,678**<br><sub>(95 MiB / 41.0% CPU · 335 MiB/s · 612.5 µs CPU/req)</sub> | **0**<br><sub>(122 MiB / 0.2% CPU)</sub> | **1,955**<br><sub>(252 MiB / 48.4% CPU · 244 MiB/s · 989.6 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **2,340**<br><sub>(240 MiB / 45.0% CPU · 293 MiB/s · 770.0 µs CPU/req)</sub> | **817**<br><sub>(119 MiB / 49.3% CPU · 102 MiB/s · 2410.6 µs CPU/req)</sub> | **2,119**<br><sub>(95 MiB / 45.0% CPU · 265 MiB/s · 850.0 µs CPU/req)</sub> | **0**<br><sub>(123 MiB / 0.3% CPU)</sub> | **1,902**<br><sub>(264 MiB / 50.4% CPU · 238 MiB/s · 1060.4 µs CPU/req)</sub> |

Linux nginx H1/H2/H3 POST completed (nginx.org mainline). TWP÷YARP H1 ≈ **1.5×**; H2→H1 ≈ **1.2×**; H3 ≈ **1.1×**. TWP÷nginx H3 ≈ **6×**.

### macOS — POST 64 KiB request + 64 KiB response

Median of **3** repeats on `macos-15` (Apple Silicon M1, 3-core / 7 GB) @ `4a97cb0d`. Source: Actions [44653997866](https://github.com/justcoding121/titanium-web-proxy/actions/runs/44653997866) (`compare-post`).

| Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---:|---:|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | **3,757**<br><sub>(210 MiB / 35.7% CPU · 470 MiB/s · 285.0 µs CPU/req)</sub> | **3,101**<br><sub>(91 MiB / 20.1% CPU · 388 MiB/s · 194.4 µs CPU/req)</sub> | 🥇 **3,923**<br><sub>(125 MiB / 24.6% CPU · 490 MiB/s · 188.3 µs CPU/req)</sub> | **3,004**<br><sub>(134 MiB / 26.8% CPU · 376 MiB/s · 267.4 µs CPU/req)</sub> | **2,142**<br><sub>(189 MiB / 34.3% CPU · 268 MiB/s · 480.6 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | **2,478**<br><sub>(276 MiB / 34.9% CPU · 310 MiB/s · 422.6 µs CPU/req)</sub> | **2,391**<br><sub>(100 MiB / 19.6% CPU · 299 MiB/s · 245.9 µs CPU/req)</sub> | 🥇 **3,212**<br><sub>(125 MiB / 27.5% CPU · 401 MiB/s · 256.7 µs CPU/req)</sub> | **1,908**<br><sub>(peak 3,130 · 123 MiB / 20.4% CPU · 239 MiB/s · 321.2 µs CPU/req)</sub> | **2,087**<br><sub>(186 MiB / 35.9% CPU · 261 MiB/s · 515.5 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | **1,204**<br><sub>(178 MiB / 36.0% CPU · 150 MiB/s · 896.2 µs CPU/req)</sub> | 🥇 **1,557**<br><sub>(97 MiB / 22.5% CPU · 195 MiB/s · 432.8 µs CPU/req)</sub> | **1,168**<br><sub>(129 MiB / 24.5% CPU · 146 MiB/s · 628.4 µs CPU/req)</sub> | **1,182**<br><sub>(137 MiB / 41.4% CPU · 148 MiB/s · 1049.8 µs CPU/req)</sub> | **1,038**<br><sub>(249 MiB / 31.1% CPU · 130 MiB/s · 897.2 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | **5,041**<br><sub>(263 MiB / 28.4% CPU · 630 MiB/s · 169.3 µs CPU/req)</sub> | 🥇 **5,448**<br><sub>(86 MiB / 16.4% CPU · 681 MiB/s · 90.0 µs CPU/req)</sub> | **4,778**<br><sub>(113 MiB / 23.9% CPU · 597 MiB/s · 150.2 µs CPU/req)</sub> | **4,386**<br><sub>(136 MiB / 21.7% CPU · 548 MiB/s · 148.7 µs CPU/req)</sub> | **5,101**<br><sub>(175 MiB / 37.5% CPU · 638 MiB/s · 220.4 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | 🥇 **2,771**<br><sub>(244 MiB / 28.6% CPU · 346 MiB/s · 309.8 µs CPU/req)</sub> | *Not possible* | **2,726**<br><sub>(133 MiB / 26.9% CPU · 341 MiB/s · 295.7 µs CPU/req)</sub> | **2,445**<br><sub>(130 MiB / 23.0% CPU · 306 MiB/s · 282.2 µs CPU/req)</sub> | **1,713**<br><sub>(220 MiB / 32.9% CPU · 214 MiB/s · 576.8 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **1,400**<br><sub>(260 MiB / 31.5% CPU · 175 MiB/s · 676.0 µs CPU/req)</sub> | *Not possible* | **1,154**<br><sub>(129 MiB / 23.6% CPU · 144 MiB/s · 614.2 µs CPU/req)</sub> | **882**<br><sub>(129 MiB / 18.0% CPU · 110 MiB/s · 612.6 µs CPU/req)</sub> | **499**<br><sub>(219 MiB / 23.0% CPU · 62 MiB/s · 1385.1 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | **500**<br><sub>(203 MiB / 25.3% CPU · 62 MiB/s · 1519.5 µs CPU/req)</sub> | *Not possible* | 🥇 **545**<br><sub>(126 MiB / 27.0% CPU · 68 MiB/s · 1483.5 µs CPU/req)</sub> | **487**<br><sub>(137 MiB / 41.1% CPU · 61 MiB/s · 2528.3 µs CPU/req)</sub> | **436**<br><sub>(248 MiB / 35.8% CPU · 55 MiB/s · 2458.4 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | **813**<br><sub>(201 MiB / 33.8% CPU · 102 MiB/s · 1246.3 µs CPU/req)</sub> | 🥇 **963**<br><sub>(100 MiB / 22.1% CPU · 120 MiB/s · 686.6 µs CPU/req)</sub> | **867**<br><sub>(132 MiB / 26.8% CPU · 108 MiB/s · 926.1 µs CPU/req)</sub> | **412**<br><sub>(134 MiB / 28.8% CPU · 51 MiB/s · 2096.6 µs CPU/req)</sub> | **702**<br><sub>(264 MiB / 36.6% CPU · 88 MiB/s · 1563.8 µs CPU/req)</sub> |

### Windows — lossy / high-RTT (H2 HOL / H3 loss)

Userspace **5 ms** one-way delay + **1%** TCP connection stall (H1/H2); UDP is **loss% only** (no per-datagram delay; MsQuic-safe) + **1%** datagram drop (H3); **64 KiB** GET. Median of **3** repeats on `windows-latest` @ `4a97cb0d` — [44654004329](https://github.com/justcoding121/titanium-web-proxy/actions/runs/44654004329) (`compare-lossy`).

*Not possible:* **HAProxy** and **Envoy** columns are omitted (no official Windows port).


| Client | Origin | TWP | nginx | YARP |
|---|---|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | **676**<br><sub>(90 MiB / 1.6% CPU · 42 MiB/s · 94.7 µs CPU/req)</sub> | **659**<br><sub>(144 MiB / 12.4% CPU · 41 MiB/s · 753.6 µs CPU/req)</sub> | 🥇 **680**<br><sub>(128 MiB / 2.7% CPU · 43 MiB/s · 161.1 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **248**<br><sub>(149 MiB / 2.8% CPU · 15 MiB/s · 457.0 µs CPU/req)</sub> | **244**<br><sub>(145 MiB / 8.4% CPU · 15 MiB/s · 1381.0 µs CPU/req)</sub> | **236**<br><sub>(115 MiB / 5.2% CPU · 15 MiB/s · 888.5 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **975**<br><sub>(130 MiB / 17.3% CPU · 61 MiB/s · 709.9 µs CPU/req)</sub> | *Not possible (no QUIC)* | **736**<br><sub>(159 MiB / 27.9% CPU · 46 MiB/s · 1513.2 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | **248**<br><sub>(155 MiB / 3.3% CPU · 16 MiB/s · 537.1 µs CPU/req)</sub> | 🥇 **250**<br><sub>(129 MiB / 1.8% CPU · 16 MiB/s · 289.1 µs CPU/req)</sub> | **240**<br><sub>(108 MiB / 3.1% CPU · 15 MiB/s · 514.4 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | **223**<br><sub>(120 MiB / 2.5% CPU · 14 MiB/s · 441.1 µs CPU/req)</sub> | *Not possible* | 🥇 **235**<br><sub>(122 MiB / 4.6% CPU · 15 MiB/s · 783.3 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | **224**<br><sub>(122 MiB / 1.0% CPU · 14 MiB/s · 173.4 µs CPU/req)</sub> | *Not possible* | 🥇 **246**<br><sub>(129 MiB / 1.8% CPU · 15 MiB/s · 286.2 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | 🥇 **1,010**<br><sub>(120 MiB / 17.0% CPU · 63 MiB/s · 671.6 µs CPU/req)</sub> | *Not possible* | **832**<br><sub>(168 MiB / 29.7% CPU · 52 MiB/s · 1429.2 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **762**<br><sub>(135 MiB / 20.3% CPU · 48 MiB/s · 1064.4 µs CPU/req)</sub> | *Not possible (no QUIC)* | **557**<br><sub>(155 MiB / 27.7% CPU · 35 MiB/s · 1988.2 µs CPU/req)</sub> |

H1, H2, and H3 loss sustain are in the table. Prefer the ratio columns.

### Linux — lossy / high-RTT (H2 HOL / H3 loss)

Median of **3** repeats @ `4a97cb0d`. Source: [44654004329](https://github.com/justcoding121/titanium-web-proxy/actions/runs/44654004329) (`compare-lossy`; lossy H3 uses `quic-http3`, UDP drop-only).


| Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---:|---:|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | **1,209**<br><sub>(123 MiB / 9.0% CPU · 76 MiB/s · 298.4 µs CPU/req)</sub> | 🥇 **1,214**<br><sub>(105 MiB / 12.4% CPU · 76 MiB/s · 409.2 µs CPU/req)</sub> | **1,208**<br><sub>(83 MiB / 6.2% CPU · 76 MiB/s · 204.6 µs CPU/req)</sub> | **1,208**<br><sub>(120 MiB / 9.0% CPU · 75 MiB/s · 297.0 µs CPU/req)</sub> | **1,203**<br><sub>(150 MiB / 13.0% CPU · 75 MiB/s · 431.4 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **629**<br><sub>(200 MiB / 8.1% CPU · 39 MiB/s · 517.7 µs CPU/req)</sub> | **578**<br><sub>(106 MiB / 6.8% CPU · 36 MiB/s · 473.3 µs CPU/req)</sub> | **551**<br><sub>(83 MiB / 4.5% CPU · 34 MiB/s · 329.6 µs CPU/req)</sub> | **511**<br><sub>(122 MiB / 4.5% CPU · 32 MiB/s · 352.4 µs CPU/req)</sub> | **512**<br><sub>(147 MiB / 7.9% CPU · 32 MiB/s · 615.5 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | **1,318**<br><sub>(171 MiB / 14.3% CPU · 82 MiB/s · 435.1 µs CPU/req)</sub> | 🥇 **1,831**<br><sub>(121 MiB / 19.6% CPU · 114 MiB/s · 427.7 µs CPU/req)</sub> | **1,783**<br><sub>(100 MiB / 13.8% CPU · 111 MiB/s · 309.6 µs CPU/req)</sub> | **1,792**<br><sub>(131 MiB / 18.5% CPU · 112 MiB/s · 412.5 µs CPU/req)</sub> | **1,426**<br><sub>(238 MiB / 26.1% CPU · 89 MiB/s · 731.1 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | 🥇 **653**<br><sub>(180 MiB / 5.2% CPU · 41 MiB/s · 319.2 µs CPU/req)</sub> | **591**<br><sub>(78 MiB / 3.6% CPU · 37 MiB/s · 246.9 µs CPU/req)</sub> | **534**<br><sub>(68 MiB / 2.3% CPU · 33 MiB/s · 169.9 µs CPU/req)</sub> | **509**<br><sub>(112 MiB / 2.7% CPU · 32 MiB/s · 209.9 µs CPU/req)</sub> | **515**<br><sub>(136 MiB / 5.1% CPU · 32 MiB/s · 396.6 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | **511**<br><sub>(155 MiB / 4.3% CPU · 32 MiB/s · 338.8 µs CPU/req)</sub> | *Not possible* | 🥇 **574**<br><sub>(86 MiB / 2.2% CPU · 36 MiB/s · 154.6 µs CPU/req)</sub> | **513**<br><sub>(122 MiB / 2.4% CPU · 32 MiB/s · 190.4 µs CPU/req)</sub> | **516**<br><sub>(156 MiB / 5.8% CPU · 32 MiB/s · 446.9 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | **506**<br><sub>(159 MiB / 5.2% CPU · 32 MiB/s · 407.2 µs CPU/req)</sub> | *Not possible* | 🥇 **592**<br><sub>(85 MiB / 2.6% CPU · 37 MiB/s · 177.6 µs CPU/req)</sub> | **514**<br><sub>(124 MiB / 3.6% CPU · 32 MiB/s · 280.8 µs CPU/req)</sub> | **514**<br><sub>(165 MiB / 7.5% CPU · 32 MiB/s · 587.3 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | **1,447**<br><sub>(160 MiB / 16.9% CPU · 90 MiB/s · 466.0 µs CPU/req)</sub> | *Not possible* | 🥇 **1,680**<br><sub>(104 MiB / 16.4% CPU · 105 MiB/s · 390.5 µs CPU/req)</sub> | **1,626**<br><sub>(134 MiB / 22.1% CPU · 102 MiB/s · 543.8 µs CPU/req)</sub> | **1,270**<br><sub>(232 MiB / 24.9% CPU · 79 MiB/s · 785.5 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | **1,355**<br><sub>(178 MiB / 18.9% CPU · 85 MiB/s · 556.5 µs CPU/req)</sub> | 🥇 **1,501**<br><sub>(124 MiB / 22.2% CPU · 94 MiB/s · 591.3 µs CPU/req)</sub> | **1,446**<br><sub>(102 MiB / 18.2% CPU · 90 MiB/s · 502.5 µs CPU/req)</sub> | **1,441**<br><sub>(131 MiB / 24.3% CPU · 90 MiB/s · 674.3 µs CPU/req)</sub> | **1,357**<br><sub>(217 MiB / 28.2% CPU · 85 MiB/s · 832.4 µs CPU/req)</sub> |

TWP H2 HOL ≫ YARP (~**7.8×**). H3 TWP÷YARP ≈ **1.07×**.

### macOS — lossy / high-RTT (H2 HOL / H3 loss)

Median of **3** repeats on `macos-15` (Apple Silicon M1, 3-core / 7 GB) @ `4a97cb0d`. Source: [44654004329](https://github.com/justcoding121/titanium-web-proxy/actions/runs/44654004329) (`compare-lossy`; lossy H3 uses `quic-http3`, UDP drop-only).

| Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---:|---:|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | 🥇 **522**<br><sub>(167 MiB / 4.0% CPU · 33 MiB/s · 230.5 µs CPU/req)</sub> | **494**<br><sub>(86 MiB / 2.7% CPU · 31 MiB/s · 163.4 µs CPU/req)</sub> | **518**<br><sub>(117 MiB / 3.5% CPU · 32 MiB/s · 200.5 µs CPU/req)</sub> | **458**<br><sub>(131 MiB / 5.0% CPU · 29 MiB/s · 326.5 µs CPU/req)</sub> | **453**<br><sub>(171 MiB / 6.4% CPU · 28 MiB/s · 422.8 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | **158**<br><sub>(206 MiB / 3.0% CPU · 9.9 MiB/s · 562.4 µs CPU/req)</sub> | **144**<br><sub>(87 MiB / 1.2% CPU · 9.0 MiB/s · 243.9 µs CPU/req)</sub> | 🥇 **160**<br><sub>(117 MiB / 1.9% CPU · 10.0 MiB/s · 360.7 µs CPU/req)</sub> | **136**<br><sub>(129 MiB / 1.6% CPU · 8.5 MiB/s · 350.2 µs CPU/req)</sub> | **144**<br><sub>(173 MiB / 3.3% CPU · 9.0 MiB/s · 682.9 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | **312**<br><sub>(142 MiB / 16.7% CPU · 20 MiB/s · 1609.6 µs CPU/req)</sub> | **521**<br><sub>(95 MiB / 13.0% CPU · 33 MiB/s · 746.8 µs CPU/req)</sub> | **207**<br><sub>(122 MiB / 9.6% CPU · 13 MiB/s · 1386.4 µs CPU/req)</sub> | 🥇 **560**<br><sub>(132 MiB / 26.7% CPU · 35 MiB/s · 1431.7 µs CPU/req)</sub> | **350**<br><sub>(223 MiB / 24.0% CPU · 22 MiB/s · 2056.2 µs CPU/req)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | 🥇 **160**<br><sub>(194 MiB / 1.8% CPU · 10.0 MiB/s · 334.2 µs CPU/req)</sub> | **136**<br><sub>(73 MiB / 0.6% CPU · 8.5 MiB/s · 137.0 µs CPU/req)</sub> | **145**<br><sub>(101 MiB / 1.4% CPU · 9.0 MiB/s · 284.2 µs CPU/req)</sub> | **128**<br><sub>(124 MiB / 1.7% CPU · 8.0 MiB/s · 404.1 µs CPU/req)</sub> | **143**<br><sub>(153 MiB / 2.0% CPU · 8.9 MiB/s · 421.1 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | **144**<br><sub>(181 MiB / 3.1% CPU · 9.0 MiB/s · 653.4 µs CPU/req)</sub> | *Not possible* | 🥇 **152**<br><sub>(122 MiB / 1.5% CPU · 9.5 MiB/s · 298.6 µs CPU/req)</sub> | **144**<br><sub>(125 MiB / 2.8% CPU · 9.0 MiB/s · 574.2 µs CPU/req)</sub> | **144**<br><sub>(179 MiB / 4.4% CPU · 9.0 MiB/s · 924.8 µs CPU/req)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | **128**<br><sub>(192 MiB / 3.2% CPU · 8.0 MiB/s · 750.6 µs CPU/req)</sub> | *Not possible* | 🥇 **156**<br><sub>(116 MiB / 2.0% CPU · 9.7 MiB/s · 381.0 µs CPU/req)</sub> | **128**<br><sub>(124 MiB / 1.7% CPU · 8.0 MiB/s · 390.3 µs CPU/req)</sub> | **143**<br><sub>(179 MiB / 4.3% CPU · 9.0 MiB/s · 899.6 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | **595**<br><sub>(146 MiB / 23.7% CPU · 37 MiB/s · 1196.3 µs CPU/req)</sub> | *Not possible* | **446**<br><sub>(130 MiB / 14.3% CPU · 28 MiB/s · 961.9 µs CPU/req)</sub> | 🥇 **645**<br><sub>(131 MiB / 25.1% CPU · 40 MiB/s · 1167.2 µs CPU/req)</sub> | **423**<br><sub>(231 MiB / 23.8% CPU · 26 MiB/s · 1689.8 µs CPU/req)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | **621**<br><sub>(170 MiB / 15.3% CPU · 39 MiB/s · 739.7 µs CPU/req)</sub> | **979**<br><sub>(102 MiB / 13.4% CPU · 61 MiB/s · 410.1 µs CPU/req)</sub> | **682**<br><sub>(130 MiB / 12.3% CPU · 43 MiB/s · 543.5 µs CPU/req)</sub> | 🥇 **1,251**<br><sub>(132 MiB / 28.4% CPU · 78 MiB/s · 681.5 µs CPU/req)</sub> | **626**<br><sub>(234 MiB / 25.6% CPU · 39 MiB/s · 1224.6 µs CPU/req)</sub> |

### Architecture-sensitive

These runs isolate slow app readers, origin-early response, HTTP/2 duplex, and WebSocket echo. See [TWP vs YARP IO model](Performance-Profiling#user-content-twp-vs-yarp-io-model). Laptop 1-rep numbers are on [Performance Local Lab](Performance-Local-Lab#user-content-architecture-sensitive).

Median of **3** repeats on matched 4 vCPU / 16 GiB runners @ `4a97cb0d` ([44654018464](https://github.com/justcoding121/titanium-web-proxy/actions/runs/44654018464)). Slow consumer = 256 KiB GET, 16 KiB read + 8 ms sleep. Early response = 64 KiB POST, origin writes after 8 KiB. Duplex HTTP/2 = overlapping 64 KiB POST on H2 TLS↔H2 TLS. WebSocket row = H1 Upgrade echo round-trips/sec (not RFC 8441; see WebSocket RFC 8441 table below).

Lossy-link runs (slow **network**) are already published above; they are not a slow **app** reader.

#### Windows


*Not possible:* **HAProxy** and **Envoy** columns are omitted (no official Windows port).

| Scenario | Client | Origin | TWP | nginx | YARP |
|---|---|---|---:|---:|---:|
| Slow consumer (256 KiB GET, throttled client read) | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **248**<br><sub>(96 MiB / 6.5% CPU · 62 MiB/s · 1046.8 µs CPU/req)</sub> | **218**<br><sub>(147 MiB / 24.5% CPU · 54 MiB/s · 4502.8 µs CPU/req)</sub> | **248**<br><sub>(116 MiB / 6.7% CPU · 62 MiB/s · 1087.2 µs CPU/req)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **248**<br><sub>(139 MiB / 4.2% CPU · 62 MiB/s · 675.8 µs CPU/req)</sub> | **212**<br><sub>(145 MiB / 24.8% CPU · 53 MiB/s · 4678.9 µs CPU/req)</sub> | **248**<br><sub>(125 MiB / 5.6% CPU · 62 MiB/s · 902.0 µs CPU/req)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/3 · QUIC | HTTP/1 · plain | **130**<br><sub>(109 MiB / 45.5% CPU · 33 MiB/s · 13966.3 µs CPU/req)</sub> | *Not possible (no QUIC)* | 🥇 **147**<br><sub>(152 MiB / 44.2% CPU · 37 MiB/s · 12024.5 µs CPU/req)</sub> |
| Early response (origin writes after first request chunk) | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **10,813**<br><sub>(105 MiB / 42.0% CPU · 1,352 MiB/s · 155.5 µs CPU/req)</sub> | **639**<br><sub>(145 MiB / 24.8% CPU · 80 MiB/s · 1551.6 µs CPU/req)</sub> | **6,556**<br><sub>(139 MiB / 53.7% CPU · 820 MiB/s · 327.4 µs CPU/req)</sub> |
| Early response (origin writes after first request chunk) | HTTP/2 · TLS | HTTP/1 · plain | **3,387**<br><sub>(227 MiB / 52.5% CPU · 423 MiB/s · 619.6 µs CPU/req)</sub> | **400**<br><sub>(149 MiB / 24.8% CPU · 50 MiB/s · 2484.2 µs CPU/req)</sub> | 🥇 **4,113**<br><sub>(142 MiB / 50.0% CPU · 514 MiB/s · 485.9 µs CPU/req)</sub> |
| Early response (origin writes after first request chunk) | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **1,797**<br><sub>(163 MiB / 44.6% CPU · 225 MiB/s · 992.7 µs CPU/req)</sub> | *Not possible (no QUIC)* | **1,140**<br><sub>(214 MiB / 50.8% CPU · 142 MiB/s · 1783.5 µs CPU/req)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · plain | HTTP/1 · plain | **248**<br><sub>(133 MiB / 2.7% CPU · 62 MiB/s · 433.2 µs CPU/req)</sub> | **248**<br><sub>(130 MiB / 6.4% CPU · 62 MiB/s · 1032.3 µs CPU/req)</sub> | 🥇 **256**<br><sub>(114 MiB / 3.4% CPU · 64 MiB/s · 533.4 µs CPU/req)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · TLS | HTTP/2 · TLS | **248**<br><sub>(160 MiB / 8.9% CPU · 62 MiB/s · 1436.2 µs CPU/req)</sub> | *Not possible* | 🥇 **248**<br><sub>(147 MiB / 9.6% CPU · 62 MiB/s · 1545.5 µs CPU/req)</sub> |
| Early response (origin writes after first request chunk) | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **3,166**<br><sub>(165 MiB / 42.2% CPU · 396 MiB/s · 533.7 µs CPU/req)</sub> | *Not possible* | **2,792**<br><sub>(185 MiB / 52.3% CPU · 349 MiB/s · 749.9 µs CPU/req)</sub> |
| Duplex (both directions live) | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **3,543**<br><sub>(170 MiB / 41.1% CPU · 443 MiB/s · 464.3 µs CPU/req)</sub> | *Not possible* | **3,345**<br><sub>(182 MiB / 53.9% CPU · 418 MiB/s · 643.9 µs CPU/req)</sub> |
| Duplex (WebSocket / H1 Upgrade) | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **28,319**<br><sub>(100 MiB / 43.0% CPU · 60.8 µs CPU/req)</sub> | **23,086**<br><sub>(145 MiB / 24.8% CPU · 43.0 µs CPU/req)</sub> | **27,587**<br><sub>(90 MiB / 47.0% CPU · 68.2 µs CPU/req)</sub> |

#### Linux


| Scenario | Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---|---:|---:|---:|---:|---:|
| Slow consumer (256 KiB GET, throttled client read) | HTTP/1 · TLS | HTTP/1 · plain | **474**<br><sub>(119 MiB / 4.7% CPU · 118 MiB/s · 393.3 µs CPU/req)</sub> | **476**<br><sub>(106 MiB / 10.2% CPU · 119 MiB/s · 852.9 µs CPU/req)</sub> | 🥇 **480**<br><sub>(83 MiB / 3.3% CPU · 120 MiB/s · 276.8 µs CPU/req)</sub> | **475**<br><sub>(131 MiB / 5.3% CPU · 119 MiB/s · 448.3 µs CPU/req)</sub> | **472**<br><sub>(161 MiB / 7.8% CPU · 118 MiB/s · 661.3 µs CPU/req)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **480**<br><sub>(158 MiB / 11.0% CPU · 120 MiB/s · 918.2 µs CPU/req)</sub> | **480**<br><sub>(106 MiB / 12.7% CPU · 120 MiB/s · 1054.3 µs CPU/req)</sub> | **480**<br><sub>(83 MiB / 7.8% CPU · 120 MiB/s · 645.7 µs CPU/req)</sub> | **480**<br><sub>(136 MiB / 7.5% CPU · 120 MiB/s · 629.3 µs CPU/req)</sub> | **474**<br><sub>(168 MiB / 12.6% CPU · 118 MiB/s · 1066.7 µs CPU/req)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/3 · QUIC | HTTP/1 · plain | **468**<br><sub>(154 MiB / 42.9% CPU · 117 MiB/s · 3669.2 µs CPU/req)</sub> | **472**<br><sub>(120 MiB / 12.7% CPU · 118 MiB/s · 1079.2 µs CPU/req)</sub> | **470**<br><sub>(91 MiB / 7.2% CPU · 117 MiB/s · 617.7 µs CPU/req)</sub> | 🥇 **475**<br><sub>(143 MiB / 17.6% CPU · 119 MiB/s · 1483.4 µs CPU/req)</sub> | **464**<br><sub>(204 MiB / 45.8% CPU · 116 MiB/s · 3947.5 µs CPU/req)</sub> |
| Early response (origin writes after first request chunk) | HTTP/1 · TLS | HTTP/1 · plain | **10,418**<br><sub>(129 MiB / 41.1% CPU · 1,302 MiB/s · 157.8 µs CPU/req)</sub> | **6,709**<br><sub>(107 MiB / 49.9% CPU · 839 MiB/s · 297.5 µs CPU/req)</sub> | 🥇 **11,060**<br><sub>(88 MiB / 43.4% CPU · 1,382 MiB/s · 156.8 µs CPU/req)</sub> | **0**<br><sub>(peak 64 · 121 MiB / 1.8% CPU)</sub> | **6,129**<br><sub>(186 MiB / 54.6% CPU · 766 MiB/s · 356.6 µs CPU/req)</sub> |
| Early response (origin writes after first request chunk) | HTTP/2 · TLS | HTTP/1 · plain | **3,138**<br><sub>(231 MiB / 52.0% CPU · 392 MiB/s · 663.2 µs CPU/req)</sub> | **3,315**<br><sub>(112 MiB / 46.4% CPU · 414 MiB/s · 560.3 µs CPU/req)</sub> | 🥇 **4,082**<br><sub>(87 MiB / 44.1% CPU · 510 MiB/s · 431.9 µs CPU/req)</sub> | **0**<br><sub>(peak 3,955 · 135 MiB / 44.6% CPU)</sub> | **3,459**<br><sub>(177 MiB / 50.5% CPU · 432 MiB/s · 583.4 µs CPU/req)</sub> |
| Early response (origin writes after first request chunk) | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **2,726**<br><sub>(231 MiB / 42.4% CPU · 341 MiB/s · 622.1 µs CPU/req)</sub> | **1,288**<br><sub>(118 MiB / 44.4% CPU · 161 MiB/s · 1378.8 µs CPU/req)</sub> | **2,434**<br><sub>(94 MiB / 41.1% CPU · 304 MiB/s · 676.2 µs CPU/req)</sub> | **0**<br><sub>(123 MiB / 0.2% CPU)</sub> | **1,827**<br><sub>(263 MiB / 48.4% CPU · 228 MiB/s · 1060.2 µs CPU/req)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · plain | HTTP/1 · plain | **480**<br><sub>(161 MiB / 4.9% CPU · 120 MiB/s · 411.7 µs CPU/req)</sub> | **482**<br><sub>(78 MiB / 8.3% CPU · 121 MiB/s · 685.9 µs CPU/req)</sub> | 🥇 **485**<br><sub>(67 MiB / 3.5% CPU · 121 MiB/s · 292.7 µs CPU/req)</sub> | **480**<br><sub>(126 MiB / 3.0% CPU · 120 MiB/s · 250.0 µs CPU/req)</sub> | **480**<br><sub>(151 MiB / 5.7% CPU · 120 MiB/s · 471.7 µs CPU/req)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · TLS | HTTP/2 · TLS | **478**<br><sub>(214 MiB / 12.2% CPU · 119 MiB/s · 1019.7 µs CPU/req)</sub> | *Not possible* | 🥇 **480**<br><sub>(93 MiB / 7.8% CPU · 120 MiB/s · 648.5 µs CPU/req)</sub> | **480**<br><sub>(133 MiB / 8.5% CPU · 120 MiB/s · 707.6 µs CPU/req)</sub> | **480**<br><sub>(188 MiB / 16.6% CPU · 120 MiB/s · 1380.3 µs CPU/req)</sub> |
| Early response (origin writes after first request chunk) | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **5,007**<br><sub>(228 MiB / 36.9% CPU · 626 MiB/s · 294.4 µs CPU/req)</sub> | *Not possible* | **3,272**<br><sub>(86 MiB / 36.3% CPU · 409 MiB/s · 443.3 µs CPU/req)</sub> | **0**<br><sub>(peak 3,575 · 137 MiB / 36.5% CPU)</sub> | **3,812**<br><sub>(230 MiB / 50.2% CPU · 477 MiB/s · 526.4 µs CPU/req)</sub> |
| Duplex (both directions live) | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **3,270**<br><sub>(211 MiB / 39.7% CPU · 409 MiB/s · 485.7 µs CPU/req)</sub> | *Not possible* | **2,264**<br><sub>(91 MiB / 37.9% CPU · 283 MiB/s · 669.7 µs CPU/req)</sub> | **0**<br><sub>(peak 2,603 · 135 MiB / 36.7% CPU)</sub> | **2,584**<br><sub>(216 MiB / 50.1% CPU · 323 MiB/s · 775.1 µs CPU/req)</sub> |
| Duplex (WebSocket / H1 Upgrade) | HTTP/1 · TLS | HTTP/1 · plain | **45,799**<br><sub>(134 MiB / 36.6% CPU · 31.9 µs CPU/req)</sub> | 🥇 **63,383**<br><sub>(107 MiB / 32.2% CPU · 20.3 µs CPU/req)</sub> | **59,617**<br><sub>(82 MiB / 35.2% CPU · 23.6 µs CPU/req)</sub> | **55,921**<br><sub>(118 MiB / 37.4% CPU · 26.8 µs CPU/req)</sub> | **42,407**<br><sub>(131 MiB / 39.0% CPU · 36.8 µs CPU/req)</sub> |

#### macOS

Median of **3** repeats on `macos-15` (Apple Silicon M1, 3-core / 7 GB) @ `4a97cb0d`. Source: Actions [44654018464](https://github.com/justcoding121/titanium-web-proxy/actions/runs/44654018464) (`compare-arch`).

| Scenario | Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---|---:|---:|---:|---:|---:|
| Slow consumer (256 KiB GET, throttled client read) | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **121**<br><sub>(179 MiB / 2.6% CPU · 30 MiB/s · 649.9 µs CPU/req)</sub> | **115**<br><sub>(85 MiB / 2.6% CPU · 29 MiB/s · 673.3 µs CPU/req)</sub> | **104**<br><sub>(112 MiB / 2.1% CPU · 26 MiB/s · 598.8 µs CPU/req)</sub> | **94**<br><sub>(146 MiB / 1.4% CPU · 23 MiB/s · 431.8 µs CPU/req)</sub> | **104**<br><sub>(172 MiB / 3.4% CPU · 26 MiB/s · 970.1 µs CPU/req)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **128**<br><sub>(188 MiB / 3.9% CPU · 32 MiB/s · 924.2 µs CPU/req)</sub> | **112**<br><sub>(86 MiB / 2.9% CPU · 28 MiB/s · 782.3 µs CPU/req)</sub> | **104**<br><sub>(114 MiB / 3.5% CPU · 26 MiB/s · 996.2 µs CPU/req)</sub> | **96**<br><sub>(147 MiB / 2.8% CPU · 24 MiB/s · 880.0 µs CPU/req)</sub> | **111**<br><sub>(178 MiB / 5.2% CPU · 28 MiB/s · 1406.8 µs CPU/req)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **183**<br><sub>(139 MiB / 32.2% CPU · 46 MiB/s · 5295.2 µs CPU/req)</sub> | **129**<br><sub>(96 MiB / 6.3% CPU · 32 MiB/s · 1468.5 µs CPU/req)</sub> | **137**<br><sub>(114 MiB / 14.0% CPU · 34 MiB/s · 3070.1 µs CPU/req)</sub> | **117**<br><sub>(148 MiB / 20.6% CPU · 29 MiB/s · 5269.9 µs CPU/req)</sub> | **172**<br><sub>(206 MiB / 36.4% CPU · 43 MiB/s · 6365.2 µs CPU/req)</sub> |
| Early response (origin writes after first request chunk) | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **2,724**<br><sub>(206 MiB / 34.8% CPU · 340 MiB/s · 383.4 µs CPU/req)</sub> | **0**<br><sub>(peak 2,765 · 91 MiB / 19.5% CPU)</sub> | **2,706**<br><sub>(122 MiB / 27.4% CPU · 338 MiB/s · 303.7 µs CPU/req)</sub> | **0**<br><sub>(peak 1,564 · 135 MiB / 22.4% CPU)</sub> | **1,979**<br><sub>(194 MiB / 34.3% CPU · 247 MiB/s · 520.3 µs CPU/req)</sub> |
| Early response (origin writes after first request chunk) | HTTP/2 · TLS | HTTP/1 · plain | **2,930**<br><sub>(272 MiB / 39.3% CPU · 366 MiB/s · 402.2 µs CPU/req)</sub> | **2,911**<br><sub>(100 MiB / 19.0% CPU · 364 MiB/s · 195.7 µs CPU/req)</sub> | 🥇 **3,171**<br><sub>(125 MiB / 28.4% CPU · 396 MiB/s · 268.5 µs CPU/req)</sub> | **0**<br><sub>(peak 3,045 · 145 MiB / 22.9% CPU)</sub> | **2,573**<br><sub>(179 MiB / 37.0% CPU · 322 MiB/s · 430.9 µs CPU/req)</sub> |
| Early response (origin writes after first request chunk) | HTTP/3 · QUIC | HTTP/1 · plain | **705**<br><sub>(174 MiB / 34.2% CPU · 88 MiB/s · 1457.6 µs CPU/req)</sub> | 🥇 **1,275**<br><sub>(101 MiB / 21.7% CPU · 159 MiB/s · 510.0 µs CPU/req)</sub> | **392**<br><sub>(110 MiB / 22.3% CPU · 49 MiB/s · 1708.1 µs CPU/req)</sub> | **698**<br><sub>(136 MiB / 39.3% CPU · 87 MiB/s · 1687.9 µs CPU/req)</sub> | **590**<br><sub>(246 MiB / 34.1% CPU · 74 MiB/s · 1734.4 µs CPU/req)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · plain | HTTP/1 · plain | **104**<br><sub>(160 MiB / 2.0% CPU · 26 MiB/s · 580.4 µs CPU/req)</sub> | **104**<br><sub>(72 MiB / 1.6% CPU · 26 MiB/s · 462.4 µs CPU/req)</sub> | **104**<br><sub>(98 MiB / 2.0% CPU · 26 MiB/s · 571.0 µs CPU/req)</sub> | **88**<br><sub>(136 MiB / 1.9% CPU · 22 MiB/s · 660.2 µs CPU/req)</sub> | 🥇 **119**<br><sub>(153 MiB / 3.2% CPU · 30 MiB/s · 809.7 µs CPU/req)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · TLS | HTTP/2 · TLS | **108**<br><sub>(229 MiB / 5.1% CPU · 27 MiB/s · 1401.8 µs CPU/req)</sub> | *Not possible* | 🥇 **112**<br><sub>(126 MiB / 4.1% CPU · 28 MiB/s · 1087.3 µs CPU/req)</sub> | **101**<br><sub>(129 MiB / 3.7% CPU · 25 MiB/s · 1087.1 µs CPU/req)</sub> | **106**<br><sub>(212 MiB / 6.4% CPU · 26 MiB/s · 1831.3 µs CPU/req)</sub> |
| Early response (origin writes after first request chunk) | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **2,421**<br><sub>(254 MiB / 29.9% CPU · 303 MiB/s · 371.0 µs CPU/req)</sub> | *Not possible* | **610**<br><sub>(123 MiB / 11.4% CPU · 76 MiB/s · 560.0 µs CPU/req)</sub> | **321**<br><sub>(peak 678 · 120 MiB / 4.2% CPU · 40 MiB/s · 394.9 µs CPU/req)</sub> | **1,047**<br><sub>(236 MiB / 22.0% CPU · 131 MiB/s · 630.5 µs CPU/req)</sub> |
| Duplex (both directions live) | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **1,975**<br><sub>(270 MiB / 29.2% CPU · 247 MiB/s · 443.4 µs CPU/req)</sub> | *Not possible* | **1,125**<br><sub>(119 MiB / 18.9% CPU · 141 MiB/s · 504.1 µs CPU/req)</sub> | **0**<br><sub>(peak 954 · 133 MiB / 15.4% CPU)</sub> | **1,014**<br><sub>(246 MiB / 24.2% CPU · 127 MiB/s · 716.6 µs CPU/req)</sub> |
| Duplex (WebSocket / H1 Upgrade) | HTTP/1 · TLS | HTTP/1 · plain | **17,803**<br><sub>(146 MiB / 24.7% CPU · 41.6 µs CPU/req)</sub> | **19,986**<br><sub>(87 MiB / 13.0% CPU · 19.5 µs CPU/req)</sub> | 🥇 **22,668**<br><sub>(104 MiB / 21.5% CPU · 28.5 µs CPU/req)</sub> | **17,276**<br><sub>(123 MiB / 21.4% CPU · 37.1 µs CPU/req)</sub> | **15,667**<br><sub>(144 MiB / 27.1% CPU · 51.8 µs CPU/req)</sub> |

Slow consumer is sleep-bound; H1/H2/H3 sit in the same band. Prefer the ratio columns for early-response, duplex, and WebSocket rows.

### TLS termination cost (H1 TLS → cleartext origin)

Isolates keep-alive tiny GET vs **new connection per request** (handshake-dominated) vs keep-alive **256 KiB**. Product comparison uses RPS and end-to-end latency; TWP can also capture `ClientTlsTiming` when `TWP_RPS_CAPTURE_TLS=1` (child process) — nginx/YARP have no equivalent hook.

#### Windows

Median of **3** repeats on `windows-latest` @ `4a97cb0d`. Source: Actions [44654011228](https://github.com/justcoding121/titanium-web-proxy/actions/runs/44654011228). Absolute RPS on GHA swings hard; prefer **TWP÷YARP**.


*Not possible:* **HAProxy** and **Envoy** columns are omitted (no official Windows port).

| Workload | TWP | nginx | YARP |
|---|---:|---:|---:|
| Keep-alive · tiny GET | 🥇 **16,868**<br><sub>(90 MiB / 47.1% CPU · 111.7 µs CPU/req)</sub> | **9,399**<br><sub>(144 MiB / 24.5% CPU · 104.3 µs CPU/req)</sub> | **15,419**<br><sub>(112 MiB / 49.9% CPU · 129.6 µs CPU/req)</sub> |
| New-connection · tiny GET | 🥇 **915**<br><sub>(87 MiB / 1.1% CPU · 49.4 µs CPU/req)</sub> | **309**<br><sub>(143 MiB / 1.9% CPU · 242.2 µs CPU/req)</sub> | **906**<br><sub>(116 MiB / 0.5% CPU · 20.3 µs CPU/req)</sub> |
| Keep-alive · 256 KiB GET | 🥇 **2,824**<br><sub>(129 MiB / 41.0% CPU · 706 MiB/s · 580.5 µs CPU/req)</sub> | **162**<br><sub>(144 MiB / 24.6% CPU · 40 MiB/s · 6085.4 µs CPU/req)</sub> | **2,548**<br><sub>(139 MiB / 46.4% CPU · 637 MiB/s · 727.7 µs CPU/req)</sub> |

#### Linux

Median of **3** repeats @ `4a97cb0d`. Source: Actions [44654011228](https://github.com/justcoding121/titanium-web-proxy/actions/runs/44654011228).


| Workload | TWP | nginx | HAProxy | Envoy | YARP |
|---|---:|---:|---:|---:|---:|
| Keep-alive · tiny GET | **36,679**<br><sub>(111 MiB / 42.5% CPU · 46.3 µs CPU/req)</sub> | 🥇 **46,268**<br><sub>(107 MiB / 40.2% CPU · 34.7 µs CPU/req)</sub> | **43,207**<br><sub>(81 MiB / 44.1% CPU · 40.8 µs CPU/req)</sub> | **27,993**<br><sub>(118 MiB / 57.3% CPU · 81.9 µs CPU/req)</sub> | **31,618**<br><sub>(141 MiB / 46.0% CPU · 58.2 µs CPU/req)</sub> |
| New-connection · tiny GET | **1,079**<br><sub>(123 MiB)</sub> | **1,111**<br><sub>(106 MiB / 1.0% CPU · 35.3 µs CPU/req)</sub> | **1,085**<br><sub>(82 MiB)</sub> | 🥇 **1,256**<br><sub>(121 MiB / 0.2% CPU · 8.0 µs CPU/req)</sub> | **1,057**<br><sub>(153 MiB / 1.0% CPU · 37.1 µs CPU/req)</sub> |
| Keep-alive · 256 KiB GET | **4,688**<br><sub>(125 MiB / 24.7% CPU · 1,172 MiB/s · 210.8 µs CPU/req)</sub> | **3,043**<br><sub>(106 MiB / 48.9% CPU · 761 MiB/s · 642.9 µs CPU/req)</sub> | 🥇 **4,928**<br><sub>(84 MiB / 23.5% CPU · 1,232 MiB/s · 190.5 µs CPU/req)</sub> | **4,367**<br><sub>(138 MiB / 30.5% CPU · 1,092 MiB/s · 279.5 µs CPU/req)</sub> | **3,963**<br><sub>(179 MiB / 36.2% CPU · 991 MiB/s · 365.3 µs CPU/req)</sub> |

#### macOS

Median of **3** repeats on `macos-15` @ `4a97cb0d`. Source: Actions [44654011228](https://github.com/justcoding121/titanium-web-proxy/actions/runs/44654011228).

| Workload | TWP | nginx | HAProxy | Envoy | YARP |
|---|---:|---:|---:|---:|---:|
| Keep-alive · tiny GET | 🥇 **18,819**<br><sub>(131 MiB / 31.7% CPU · 50.5 µs CPU/req)</sub> | **15,336**<br><sub>(85 MiB / 15.3% CPU · 30.0 µs CPU/req)</sub> | **15,363**<br><sub>(114 MiB / 23.1% CPU · 45.1 µs CPU/req)</sub> | **9,517**<br><sub>(123 MiB / 29.1% CPU · 91.7 µs CPU/req)</sub> | **16,532**<br><sub>(158 MiB / 29.6% CPU · 53.7 µs CPU/req)</sub> |
| New-connection · tiny GET | **110**<br><sub>(103 MiB / 0.1% CPU · 40.9 µs CPU/req)</sub> | **623**<br><sub>(85 MiB)</sub> | **608**<br><sub>(106 MiB)</sub> | 🥇 **643**<br><sub>(132 MiB / 0.1% CPU · 2.8 µs CPU/req)</sub> | **109**<br><sub>(133 MiB)</sub> |
| Keep-alive · 256 KiB GET | **2,131**<br><sub>(190 MiB / 31.0% CPU · 533 MiB/s · 436.7 µs CPU/req)</sub> | **1,384**<br><sub>(85 MiB / 18.0% CPU · 346 MiB/s · 389.3 µs CPU/req)</sub> | 🥇 **2,361**<br><sub>(119 MiB / 26.4% CPU · 590 MiB/s · 335.2 µs CPU/req)</sub> | **1,977**<br><sub>(156 MiB / 25.9% CPU · 494 MiB/s · 393.5 µs CPU/req)</sub> | **1,973**<br><sub>(191 MiB / 33.1% CPU · 493 MiB/s · 504.1 µs CPU/req)</sub> |

Prefer **TWP÷YARP** in the table. Absolute RPS on GHA swings hard. New-connection is Darwin SslStream-bound for TWP and YARP (handshake p99 SLO **500 ms** on macOS only — Win/Linux stay at **200 ms**).

## Unary gRPC (H2 TLS)

Unary Echo **RPC/s** @ c=64 over H2 TLS→H2 TLS for Titanium, YARP, nginx (`grpc_pass`), HAProxy, and Envoy (OS-possible peers only). Not folded into the architecture-sensitive tables. Median of **3** repeats @ `4a97cb0d` — [44653985062](https://github.com/justcoding121/titanium-web-proxy/actions/runs/44653985062).

| OS | Titanium | YARP | nginx | HAProxy | Envoy |
|---|---:|---:|---:|---:|---:|
| Windows | 🥇 **45,881**<br><sub>(136 MiB / 32.9% CPU · 28.7 µs CPU/req)</sub> | **29,822**<br><sub>(135 MiB / 45.1% CPU · 60.5 µs CPU/req)</sub> | **0**<br><sub>(147 MiB / 24.3% CPU)</sub> | *Not possible* | *Not possible* |
| Linux | 🥇 **40,581**<br><sub>(171 MiB / 26.2% CPU · 25.8 µs CPU/req)</sub> | **25,384**<br><sub>(166 MiB / 39.2% CPU · 61.8 µs CPU/req)</sub> | **0**<br><sub>(121 MiB / 44.2% CPU)</sub> | **28,717**<br><sub>(81 MiB / 37.6% CPU · 52.3 µs CPU/req)</sub> | **25,226**<br><sub>(118 MiB / 49.6% CPU · 78.6 µs CPU/req)</sub> |
| macOS | 🥇 **15,751**<br><sub>(177 MiB / 19.8% CPU · 37.8 µs CPU/req)</sub> | **14,312**<br><sub>(185 MiB / 35.6% CPU · 74.6 µs CPU/req)</sub> | **0**<br><sub>(85 MiB / 3.0% CPU)</sub> | **0**<br><sub>(109 MiB / 19.3% CPU)</sub> | **15,190**<br><sub>(121 MiB / 30.4% CPU · 59.9 µs CPU/req)</sub> |

## Unary gRPC (H2 TLS → h2c)

Unary Echo **RPC/s** @ c=64 over **H2 TLS → h2c** (edge TLS, cleartext H2 origin). Peers: Titanium, YARP, HAProxy, Envoy. Mode: `compare-grpc` (`*-grpc-h2c`). Median of **3** repeats @ `4a97cb0d` — [44653985062](https://github.com/justcoding121/titanium-web-proxy/actions/runs/44653985062).

*Not possible:* **nginx** column omitted (no H2 upstream).

| OS | Titanium | YARP | HAProxy | Envoy |
|---|---:|---:|---:|---:|
| Windows | 🥇 **54,265**<br><sub>(130 MiB / 32.4% CPU · 23.9 µs CPU/req)</sub> | **36,421**<br><sub>(131 MiB / 46.7% CPU · 51.3 µs CPU/req)</sub> | *Not possible* | *Not possible* |
| Linux | 🥇 **49,636**<br><sub>(166 MiB / 27.1% CPU · 21.8 µs CPU/req)</sub> | **32,344**<br><sub>(164 MiB / 38.7% CPU · 47.9 µs CPU/req)</sub> | **27,185**<br><sub>(81 MiB / 41.3% CPU · 60.8 µs CPU/req)</sub> | **28,041**<br><sub>(118 MiB / 52.9% CPU · 75.4 µs CPU/req)</sub> |
| macOS | 🥇 **18,100**<br><sub>(178 MiB / 29.0% CPU · 48.0 µs CPU/req)</sub> | **17,795**<br><sub>(183 MiB / 31.1% CPU · 52.5 µs CPU/req)</sub> | **12,050**<br><sub>(123 MiB / 23.0% CPU · 57.3 µs CPU/req)</sub> | **14,932**<br><sub>(120 MiB / 28.3% CPU · 56.8 µs CPU/req)</sub> |

## WebSocket (H1 TLS → H1 TLS)

WebSocket echo round-trips/sec over **dual-TLS** H1 (`proxy_ssl` style). Mode: `compare-ws-h1tls` (`*-duplex-ws-h1tls`). Median of **3** repeats @ `4a97cb0d` — [44653985062](https://github.com/justcoding121/titanium-web-proxy/actions/runs/44653985062).

| OS | Titanium | YARP | nginx | HAProxy | Envoy |
|---|---:|---:|---:|---:|---:|
| Windows | 🥇 **44,710**<br><sub>(104 MiB / 47.7% CPU · 42.7 µs CPU/req)</sub> | **44,192**<br><sub>(92 MiB / 50.5% CPU · 45.7 µs CPU/req)</sub> | **30,350**<br><sub>(145 MiB / 24.8% CPU · 32.6 µs CPU/req)</sub> | *Not possible* | *Not possible* |
| Linux | **35,592**<br><sub>(150 MiB / 37.8% CPU · 42.4 µs CPU/req)</sub> | **33,946**<br><sub>(146 MiB / 39.8% CPU · 46.9 µs CPU/req)</sub> | 🥇 **44,538**<br><sub>(110 MiB / 36.1% CPU · 32.4 µs CPU/req)</sub> | **42,269**<br><sub>(83 MiB / 39.8% CPU · 37.6 µs CPU/req)</sub> | **41,576**<br><sub>(119 MiB / 39.0% CPU · 37.6 µs CPU/req)</sub> |
| macOS | **13,551**<br><sub>(134 MiB / 27.3% CPU · 60.4 µs CPU/req)</sub> | **15,210**<br><sub>(162 MiB / 26.3% CPU · 52.0 µs CPU/req)</sub> | **14,510**<br><sub>(93 MiB / 11.7% CPU · 24.1 µs CPU/req)</sub> | 🥇 **15,939**<br><sub>(110 MiB / 19.1% CPU · 36.0 µs CPU/req)</sub> | **15,396**<br><sub>(123 MiB / 19.5% CPU · 38.1 µs CPU/req)</sub> |

## WebSocket (H2 TLS 8441 → H1)

WebSocket echo over **RFC 8441** extended CONNECT (H2 TLS client → H1 plain origin). Mode: `compare-ws-h2` (`*-duplex-ws-h2`).

The probe client sets `NoDelay` and writes each HTTP/2 frame as one TLS record. Median of **3** repeats @ `4a97cb0d` — [44653985062](https://github.com/justcoding121/titanium-web-proxy/actions/runs/44653985062).

*Not possible:* **nginx** column omitted (no RFC 8441 extended CONNECT).

| OS | Titanium | YARP | HAProxy | Envoy |
|---|---:|---:|---:|---:|
| Windows | 🥇 **23,699**<br><sub>(173 MiB / 48.1% CPU · 81.1 µs CPU/req)</sub> | **0**<br><sub>(152 MiB / 20.4% CPU)</sub> | *Not possible* | *Not possible* |
| Linux | **37,431**<br><sub>(198 MiB / 41.2% CPU · 44.0 µs CPU/req)</sub> | **0**<br><sub>(206 MiB / 44.9% CPU)</sub> | 🥇 **37,566**<br><sub>(82 MiB / 41.0% CPU · 43.7 µs CPU/req)</sub> | **0**<br><sub>(115 MiB / 0.3% CPU)</sub> |
| macOS | 🥇 **19,269**<br><sub>(220 MiB / 33.8% CPU · 52.6 µs CPU/req)</sub> | **0**<br><sub>(179 MiB / 11.8% CPU)</sub> | **13,037**<br><sub>(107 MiB / 20.7% CPU · 47.7 µs CPU/req)</sub> | **0**<br><sub>(116 MiB / 0.5% CPU)</sub> |

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

`--arm-shard` takes a comparison-group key (`h1c-h1c`, from `--print-groups`), not an `i/n` index, so a re-run measures the same row after a peer install flake. `i/n` remains for smoke modes. Paste unions `rps-csv-<os>-shard-*` (`paste-compare-product-wiki.ps1 -RunIds …`). Table “Source: Actions” may list several run URLs for one table — **do not mix SHAs**. A single-row leg allows **60** minutes of ramp and **75** minutes overall. Free GitHub accounts run **20** jobs at once, **5** of them macOS. Give a full suite a quiet window: do not push pull requests or start other macOS jobs while it runs. Re-run infrastructure failures only (`gh run rerun --failed`). A failed gate is a finding and is never re-rolled; publish the last attempt, never the best of several. The RPS tables on this page are @ `4a97cb0d` (net10.0 IL on .NET **11.0.0**): product [44653985062](https://github.com/justcoding121/titanium-web-proxy/actions/runs/44653985062), saturation [44654047300](https://github.com/justcoding121/titanium-web-proxy/actions/runs/44654047300), bodies [44653991213](https://github.com/justcoding121/titanium-web-proxy/actions/runs/44653991213), POST [44653997866](https://github.com/justcoding121/titanium-web-proxy/actions/runs/44653997866), lossy [44654004329](https://github.com/justcoding121/titanium-web-proxy/actions/runs/44654004329), arch [44654018464](https://github.com/justcoding121/titanium-web-proxy/actions/runs/44654018464), TLS cost [44654011228](https://github.com/justcoding121/titanium-web-proxy/actions/runs/44654011228), gRPC [44654025524](https://github.com/justcoding121/titanium-web-proxy/actions/runs/44654025524), WebSocket H1 TLS [44654032549](https://github.com/justcoding121/titanium-web-proxy/actions/runs/44654032549), WebSocket H2 [44654039930](https://github.com/justcoding121/titanium-web-proxy/actions/runs/44654039930), editions [44654056788](https://github.com/justcoding121/titanium-web-proxy/actions/runs/44654056788). Ten suite aggregates were green; arch failed the Windows H3→H2 TLS slow 256 KiB closest-peer floor once (`TWP÷YARP = 0.273`, gate 0.40) and that finding is published without a re-roll. The prior refresh @ `b6700ebe` remains historical only. Reverse product rows, bodies, POST, lossy, arch, TLS cost, gRPC, both WebSocket modes, and saturation run on Windows, Linux, and macOS. TWP MITM arms run on Linux only. `compare-editions` (CLI and Plus) runs on Linux only. `compare-spot` on pull requests to beta and stable stays on all three OS. The historical v2 MITM note below the product tables stays @ `41f4adee` because those run ids are from that fix, not this re-measure.

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
