Throughput and footprint of **Titanium** as a reverse / edge proxy and as a decrypting (**MITM**) proxy, measured on the same harness against **YARP**, **nginx**, **HAProxy**, and **Envoy** where each OS can run them.

**RPS** is requests per second (gRPC tables use **RPC/s**). Numbers are Release builds on matched GitHub-hosted runners: Windows and Linux at **4 vCPU / 16 GiB**, macOS at **`macos-15` Apple Silicon 3-core / 7 GB**. We compare products within an OS, not OS against OS, so read within one table — absolute RPS is not comparable across operating systems. *Not possible* means that product cannot run that path on that OS; *Not measured* means the path exists but no published number yet.

For pooling knobs and certificate first-visit tuning, see [Performance and pooling](Home#performance-and-pooling). Laptop cool A/B tables (not publishable) live on [Performance Local Lab](Performance-Local-Lab).

## Why this comparison is fair

- Same load generator, same origin process, and the same warmup / measure windows (2s / 8s) with the same concurrency ramp (8, 16, 32, 64).
- Every reverse arm is three OS processes: load generator + origin + proxy. Origin-direct omits the proxy; peers are never in-process with the client.
- Same runner class per table (`windows-latest` / `ubuntu-latest` / `macos-15`). Laptop numbers are never mixed into these tables.
- Peers use equivalent TLS/ALPN and streaming-friendly settings on the same loopback shape. HAProxy and Envoy are Linux/macOS only — *Not possible* on Windows, so their columns are omitted there.
- MITM (HTTPS decryption with forged certificates) is Titanium-only; peers cannot MITM. Those tables show Titanium MITM overhead versus its own reverse path on the same wires.
- **Tiny keep-alive GET** (~56-byte JSON) is the industry RPS shape (same class as wrk / TechEmpower). It is also real for small JSON APIs and health checks.
- **Same-protocol H2↔H2 / H3↔H3** on that shape is Titanium’s **best case**: with interception off, Titanium copies frames instead of decoding and re-encoding headers (peers do a full HTTP decode). Medals there are not the typical reverse-proxy job.
- **Typical reverse** is H1 TLS→H1 or H2→H1 (~1.1× YARP on Win/Linux tiny GET). With **larger bodies**, see [Heavier reverse](#heavier-reverse-workloads) (@ `0386b2aa`).

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
    - [macOS (GitHub-hosted `macos-15`, Apple Silicon)](#macos-github-hosted-macos-15-apple-silicon)
    - [Saturation control](#saturation-control)
- [Windows — Titanium vs nginx vs YARP](#windows--titanium-vs-nginx-vs-yarp)
- [Linux — Titanium vs nginx vs HAProxy vs Envoy vs YARP](#linux--titanium-vs-nginx-vs-haproxy-vs-envoy-vs-yarp)
- [macOS — Titanium vs nginx vs HAProxy vs Envoy vs YARP](#macos--titanium-vs-nginx-vs-haproxy-vs-envoy-vs-yarp)
- [Editions (CLI / Plus / Intercept)](#editions-cli--plus--intercept)
- [Heavier reverse workloads](#heavier-reverse-workloads)
- [Unary gRPC (H2 TLS)](#unary-grpc-h2-tls)
- [Unary gRPC (H2 TLS → h2c)](#unary-grpc-h2-tls--h2c)
- [WebSocket (H1 TLS → H1 TLS)](#websocket-h1-tls--h1-tls)
- [WebSocket (H2 TLS 8441 → H1)](#websocket-h2-tls-8441--h1)
- [Other measurements](#other-measurements)
- [Raising limits on large hosts](#raising-limits-on-large-hosts)
- [Maintainer notes](#maintainer-notes)

## Measurement environment

All three OS use public-repo GitHub-hosted runners: **Windows / Linux** at **4 vCPU / 16 GiB / 14 GB SSD**, **macOS** at **`macos-15` Apple Silicon (M1) 3-core / 7 GB** (pinned, not `macos-latest`). macOS is Apple Silicon because that is what Mac users run today; the runner sizes differ, which is fine because we compare products within an OS. Same harness knobs: warmup 2s / measure 8s; concurrency 8, 16, 32, 64; median of 3 repeats. Prefer Titanium÷YARP / Titanium÷nginx ratios over absolute RPS.

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

Calibration for the shared 4 vCPU loopback shape: how close client + origin are to saturated before ranking reverse peers. Tiny keep-alive GET. Median of **3** repeats @ `0386b2aa` — [37156912437](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37156912437). Warmup 2s / measure 8s; concurrency 8, 16, 32, 64. Block A **% of origin-HttpClient** uses median **peak** RPS. Blocks B/C use peer÷YARP / ÷nginx on median peak (not % of H1 origin). **RPS cells** embed median RSS / CPU for the **proxy child** plus its **full descendant tree** (serve-proxy → nginx master → workers); origin-direct samples the **origin** child. Product matrices below use matched `dotnet-httpclient` only (not bombardier).


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
| origin-direct | dotnet-httpclient | **81,424**<br><sub>(55 MiB / 44.9% CPU)</sub> | **100.0%** |
| origin-direct-bombardier | bombardier | **62,541**<br><sub>(56 MiB / 24.2% CPU)</sub> | **76.8%** |
| bare-reverse-http1 | dotnet-httpclient | **40,689**<br><sub>(58 MiB / 45.4% CPU)</sub> | **50.0%** |
| nginx-reverse-http1 | dotnet-httpclient | **25,102**<br><sub>(126 MiB / 24.7% CPU)</sub> | **30.8%** |
| yarp-reverse-http1 | dotnet-httpclient | **35,822**<br><sub>(88 MiB / 50.3% CPU)</sub> | **44.0%** |
| twp-reverse-http1 | dotnet-httpclient | 🥇 **42,740**<br><sub>(71 MiB / 48.0% CPU)</sub> | **52.5%** |

**Linux** (`ubuntu-latest`)

| Arm | Generator | RPS | % of origin-HttpClient |
|---|---|---:|---:|
| origin-direct | dotnet-httpclient | **69,231**<br><sub>(79 MiB / 42.2% CPU)</sub> | **100.0%** |
| origin-direct-bombardier | bombardier | **41,477**<br><sub>(80 MiB / 35.1% CPU)</sub> | **59.9%** |
| bare-reverse-http1 | dotnet-httpclient | **32,207**<br><sub>(72 MiB / 45.1% CPU)</sub> | **46.5%** |
| nginx-reverse-http1 | dotnet-httpclient | 🥇 **38,745**<br><sub>(76 MiB / 41.1% CPU)</sub> | **56.0%** |
| yarp-reverse-http1 | dotnet-httpclient | **27,681**<br><sub>(114 MiB / 50.5% CPU)</sub> | **40.0%** |
| twp-reverse-http1 | dotnet-httpclient | **32,368**<br><sub>(85 MiB / 49.3% CPU)</sub> | **46.8%** |


**macOS** (`macos-15`, Apple Silicon)

| Arm | Generator | RPS | % of origin-HttpClient |
|---|---|---:|---:|
| origin-direct | dotnet-httpclient | **52,539**<br><sub>(89 MiB / 31.7% CPU)</sub> | **100.0%** |
| origin-direct-bombardier | bombardier | **38,063**<br><sub>(89 MiB / 22.4% CPU)</sub> | **72.4%** |
| bare-reverse-http1 | dotnet-httpclient | **20,688**<br><sub>(91 MiB / 27.7% CPU)</sub> | **39.4%** |
| nginx-reverse-http1 | dotnet-httpclient | 🥇 **26,334**<br><sub>(68 MiB / 20.6% CPU)</sub> | **50.1%** |
| yarp-reverse-http1 | dotnet-httpclient | **22,962**<br><sub>(147 MiB / 36.7% CPU)</sub> | **43.7%** |
| twp-reverse-http1 | dotnet-httpclient | **18,985**<br><sub>(114 MiB / 30.5% CPU)</sub> | **36.1%** |

On this macOS run `origin-direct` was **31.7%** CPU, so the % of origin-HttpClient column is not a ceiling when that CPU is near idle. Rank those Mac peers by RPS.

Reverse peers on Block A @ `0386b2aa`: Windows TWP is **52.5%** of origin-direct and Linux TWP is **46.8%**. Prefer the **%** column over absolute RPS across runs. Bare and origin-direct are controls (not medal peers).

#### Block B — H2 TLS→H1

Peer ratios (÷YARP / ÷nginx) on median peak; **RPS cells** embed `(MiB / CPU%)`.

**Windows** (`windows-latest`)

| Arm | Generator | RPS | ÷YARP | ÷nginx |
|---|---|---:|---:|---:|
| nginx-reverse-http2 | dotnet-httpclient | **14,643**<br><sub>(142 MiB / 24.4% CPU)</sub> | **0.31×** | **1.00×** |
| yarp-reverse-http2 | dotnet-httpclient | **46,665**<br><sub>(92 MiB / 52.2% CPU)</sub> | **1.00×** | **3.19×** |
| twp-reverse-http2-cleartext | dotnet-httpclient | 🥇 **54,734**<br><sub>(103 MiB / 50.0% CPU)</sub> | **1.17×** | **3.74×** |

**Linux** (`ubuntu-latest`)

| Arm | Generator | RPS | ÷YARP | ÷nginx |
|---|---|---:|---:|---:|
| nginx-reverse-http2 | dotnet-httpclient | **15,027**<br><sub>(100 MiB / 19.4% CPU)</sub> | **0.52×** | **1.00×** |
| yarp-reverse-http2 | dotnet-httpclient | **29,082**<br><sub>(122 MiB / 50.3% CPU)</sub> | **1.00×** | **1.94×** |
| twp-reverse-http2-cleartext | dotnet-httpclient | 🥇 **34,243**<br><sub>(118 MiB / 51.6% CPU)</sub> | **1.18×** | **2.28×** |

**macOS** (`macos-15`, Apple Silicon)

| Arm | Generator | RPS | ÷YARP | ÷nginx |
|---|---|---:|---:|---:|
| nginx-reverse-http2 | dotnet-httpclient | 🥇 **32,302**<br><sub>(82 MiB / 22.7% CPU)</sub> | **1.01×** | **1.00×** |
| yarp-reverse-http2 | dotnet-httpclient | **31,922**<br><sub>(154 MiB / 39.3% CPU)</sub> | **1.00×** | **0.99×** |
| twp-reverse-http2-cleartext | dotnet-httpclient | **29,912**<br><sub>(127 MiB / 38.7% CPU)</sub> | **0.94×** | **0.93×** |
#### Block C — H3→H1

Same layout as Block B. Requires QuicListener. nginx needs `http_v3_module` (Windows nginx has no QUIC). HAProxy needs `USE_QUIC` (GHA Linux/macOS require it). Envoy 1.20+ includes HTTP/3.

Medals are nginx / YARP / Titanium only, from the same run as Blocks A and B ([37156912437](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37156912437)). On the Linux run HAProxy was **23,420** (87 MiB / 26.7% CPU) and Envoy **3,838** (136 MiB / 24.5% CPU). On the macOS run HAProxy was **33,360** (101 MiB / 27.7% CPU) and Envoy **6,764** (129 MiB / 38.3% CPU).

**Windows** (`windows-latest`)

| Arm | Generator | RPS | ÷YARP | ÷nginx |
|---|---|---:|---:|---:|
| nginx-reverse-http3-cleartext | dotnet-httpclient | *Not possible (no QUIC)* | — | — |
| yarp-reverse-http3-cleartext | dotnet-httpclient | **23,173**<br><sub>(171 MiB / 51.9% CPU)</sub> | **1.00×** | — |
| twp-reverse-http3-cleartext | dotnet-httpclient | 🥇 **26,239**<br><sub>(115 MiB / 43.6% CPU)</sub> | **1.13×** | — |

**Linux** (`ubuntu-latest`)

| Arm | Generator | RPS | ÷YARP | ÷nginx |
|---|---|---:|---:|---:|
| nginx-reverse-http3-cleartext | dotnet-httpclient | **0**<br><sub>(peak 15,029 · 107 MiB / 22.4% CPU)</sub> | **0.80×** | **1.00×** |
| yarp-reverse-http3-cleartext | dotnet-httpclient | **18,681**<br><sub>(182 MiB / 50.7% CPU)</sub> | **1.00×** | **1.24×** |
| twp-reverse-http3-cleartext | dotnet-httpclient | 🥇 **20,402**<br><sub>(142 MiB / 50.2% CPU)</sub> | **1.09×** | **1.36×** |

**macOS** (`macos-15`, Apple Silicon)

| Arm | Generator | RPS | ÷YARP | ÷nginx |
|---|---|---:|---:|---:|
| nginx-reverse-http3-cleartext | dotnet-httpclient | **0**<br><sub>(peak 26,913 · 88 MiB / 19.0% CPU)</sub> | **1.24×** | **1.00×** |
| yarp-reverse-http3-cleartext | dotnet-httpclient | 🥇 **21,658**<br><sub>(229 MiB / 44.6% CPU)</sub> | **1.00×** | **0.80×** |
| twp-reverse-http3-cleartext | dotnet-httpclient | **18,385**<br><sub>(140 MiB / 44.6% CPU)</sub> | **0.85×** | **0.68×** |

## Windows — Titanium vs nginx vs HAProxy vs Envoy vs YARP

Client / origin: HTTP version and whether TLS is used (`plain` = cleartext, `TLS` = encrypted, `QUIC` = HTTP/3).

### Reverse

Median of **3 repeats** on `windows-latest` (4 vCPU / 16 GiB). Bare reverse 5×5 @ `0386b2aa` — `compare-product` [37138944205](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37138944205). Warmup 2s / measure 8s; concurrency 8, 16, 32, 64. Prefer TWP÷peer ratios over absolute RPS. **RPS cells** include median RSS / CPU at the peak-RPS step as `<br><sub>(MiB / CPU%)</sub>`. nginx terminate peers use `keepalive 256` + streaming buffers. **HAProxy / Envoy are Linux-only peers** (no official Windows port). Laptop High-perf / cool-paired numbers stay on the [local lab](Performance-Local-Lab). Product 5×5 is **~56-byte JSON keep-alive GET**; H2/H3 same-protocol cells are mostly header work with a tiny body (Titanium best case) — see [Why this comparison is fair](#why-this-comparison-is-fair).

**Load generators:** Reverse inbound H3 arms use **`dotnet-httpclient`** (`http_version=3.0`, `RequestVersionExact`). nginx/Windows is same-OS only (no QUIC). HAProxy/Envoy are Linux-only terminate peers.

| Client | Origin | TWP | nginx | YARP |
|---|---|---:|---:|---:|
| HTTP/1 · plain | HTTP/1 · plain | 🥇 **25,718**<br><sub>(73 MiB / 48.6% CPU)</sub> | **13,568**<br><sub>(125 MiB / 24.7% CPU)</sub> | **21,289**<br><sub>(88 MiB / 50.3% CPU)</sub> |
| HTTP/1 · plain | HTTP/1 · TLS | 🥇 **63,316**<br><sub>(88 MiB / 48.7% CPU)</sub> | **27,918**<br><sub>(136 MiB / 24.8% CPU)</sub> | **60,829**<br><sub>(97 MiB / 49.8% CPU)</sub> |
| HTTP/1 · plain | HTTP/2 · plain | 🥇 **37,192**<br><sub>(109 MiB / 46.6% CPU)</sub> | *Not possible (no H2 upstream)* | **32,784**<br><sub>(93 MiB / 48.2% CPU)</sub> |
| HTTP/1 · plain | HTTP/2 · TLS | 🥇 **38,763**<br><sub>(117 MiB / 45.1% CPU)</sub> | *Not possible (no H2 upstream)* | **36,723**<br><sub>(95 MiB / 48.6% CPU)</sub> |
| HTTP/1 · plain | HTTP/3 · QUIC | 🥇 **30,111**<br><sub>(117 MiB / 50.3% CPU)</sub> | *Not possible (no H3 upstream)* | **29,414**<br><sub>(128 MiB / 51.4% CPU)</sub> |
| HTTP/1 · TLS | HTTP/1 · plain | 🥇 **21,006**<br><sub>(84 MiB / 48.8% CPU)</sub> | **8,717**<br><sub>(142 MiB / 24.7% CPU)</sub> | **17,968**<br><sub>(102 MiB / 48.2% CPU)</sub> |
| HTTP/1 · TLS | HTTP/1 · TLS | 🥇 **28,401**<br><sub>(85 MiB / 48.3% CPU)</sub> | **12,590**<br><sub>(144 MiB / 24.8% CPU)</sub> | **24,927**<br><sub>(102 MiB / 48.2% CPU)</sub> |
| HTTP/1 · TLS | HTTP/2 · plain | 🥇 **28,111**<br><sub>(111 MiB / 45.8% CPU)</sub> | *Not possible (no H2 upstream)* | **26,458**<br><sub>(103 MiB / 47% CPU)</sub> |
| HTTP/1 · TLS | HTTP/2 · TLS | 🥇 **24,680**<br><sub>(114 MiB / 42.6% CPU)</sub> | *Not possible (no H2 upstream)* | **23,968**<br><sub>(112 MiB / 45.3% CPU)</sub> |
| HTTP/1 · TLS | HTTP/3 · QUIC | 🥇 **16,100**<br><sub>(105 MiB / 49% CPU)</sub> | *Not possible (no H3 upstream)* | **15,440**<br><sub>(128 MiB / 50.2% CPU)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | 🥇 **36,931**<br><sub>(88 MiB / 52.9% CPU)</sub> | **9,197**<br><sub>(127 MiB / 24.8% CPU)</sub> | **32,084**<br><sub>(89 MiB / 54.6% CPU)</sub> |
| HTTP/2 · plain | HTTP/1 · TLS | 🥇 **31,632**<br><sub>(101 MiB / 53% CPU)</sub> | **6,653**<br><sub>(139 MiB / 24.8% CPU)</sub> | **27,576**<br><sub>(97 MiB / 51.8% CPU)</sub> |
| HTTP/2 · plain | HTTP/2 · plain | 🥇 **111,921**<br><sub>(59 MiB / 29.2% CPU)</sub> | *Not possible (no H2 upstream)* | **66,064**<br><sub>(93 MiB / 51.5% CPU)</sub> |
| HTTP/2 · plain | HTTP/2 · TLS | 🥇 **89,109**<br><sub>(63 MiB / 29.6% CPU)</sub> | *Not possible (no H2 upstream)* | **56,161**<br><sub>(100 MiB / 48.5% CPU)</sub> |
| HTTP/2 · plain | HTTP/3 · QUIC | 🥇 **45,242**<br><sub>(139 MiB / 54.2% CPU)</sub> | *Not possible (no H3 upstream)* | **41,353**<br><sub>(139 MiB / 52.4% CPU)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **35,150**<br><sub>(97 MiB / 51.8% CPU)</sub> | **8,094**<br><sub>(141 MiB / 24.5% CPU)</sub> | **28,547**<br><sub>(95 MiB / 52.3% CPU)</sub> |
| HTTP/2 · TLS | HTTP/1 · TLS | 🥇 **45,544**<br><sub>(108 MiB / 49.7% CPU)</sub> | **11,455**<br><sub>(144 MiB / 24.4% CPU)</sub> | **39,826**<br><sub>(98 MiB / 49.6% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | 🥇 **106,542**<br><sub>(77 MiB / 29.9% CPU)</sub> | *Not possible (no H2 upstream)* | **59,502**<br><sub>(102 MiB / 52.6% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **111,358**<br><sub>(78 MiB / 30.6% CPU)</sub> | *Not possible (no H2 upstream)* | **65,776**<br><sub>(97 MiB / 49% CPU)</sub> |
| HTTP/2 · TLS | HTTP/3 · QUIC | 🥇 **43,671**<br><sub>(141 MiB / 52.9% CPU)</sub> | *Not possible (no H3 upstream)* | **37,298**<br><sub>(136 MiB / 53% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **15,288**<br><sub>(103 MiB / 45.6% CPU)</sub> | *Not possible (no QUIC)* | **14,486**<br><sub>(158 MiB / 51.2% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **13,559**<br><sub>(112 MiB / 45.6% CPU)</sub> | *Not possible (no QUIC)* | **13,225**<br><sub>(145 MiB / 51.2% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/2 · plain | 🥇 **35,201**<br><sub>(129 MiB / 45.2% CPU)</sub> | *Not possible (no H2 upstream)* | **23,716**<br><sub>(172 MiB / 48.4% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | 🥇 **31,884**<br><sub>(131 MiB / 45.7% CPU)</sub> | *Not possible (no H2 upstream)* | **21,920**<br><sub>(151 MiB / 47.7% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/3 · QUIC | 🥇 **16,882**<br><sub>(117 MiB / 47.2% CPU)</sub> | *Not possible (no H3 upstream)* | **12,348**<br><sub>(150 MiB / 49.9% CPU)</sub> |

### MITM (TWP only)

Same Client×Origin wires with interception on (`compare-product` [37138944205](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37138944205)). **Lite** = no-op handlers (unchanged-lite finish). **Full** = append-only header mutation (harness: one probe header each way; product: generic append-only relay via `MitmCompressedRelayHelper`). nginx/HAProxy/Envoy/YARP cannot MITM. **Lite÷Reverse** / **Full÷Reverse** vs bare reverse (**same job / comparison-group shard**). Completion gate: Lite ≥ **0.25×** and Full ≥ **0.25×** reverse sustain @ c=64 (median of 3 GHA runs); reverse TWP ÷ closest peer ≥ **0.50×**.

**v1 append-only relay (2026-08-27):** Pre-fix H2→H2 Full÷Reverse was **0.13–0.16×** ([32960766249](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32960766249)). Post-fix @ `df172718`: H2 plain→H2 plain Full **0.77–0.79×**, H3→H1 Full **0.91–0.93×**, all MITM arms ≥ **0.70×** on median of [33041445371](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33041445371), [33055267086](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33055267086), [33055272140](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33055272140).

**v2 drop-only + non-unique append (2026-08-27):** `MitmStaticRebuildHelper` rebuilds static HPACK/QPACK after 1–4 unique header drops; trailing non-unique appends stay on compressed relay. @ `41f4adee`: all MITM arms ≥ **0.70×** on GHA median ([33087088466](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087088466), [33087091622](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087091622), [33105885748](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33105885748) Linux; [33087085235](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087085235), [33087088466](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087088466), [33087091622](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087091622) Windows). H2 plain→H2 plain Full **0.77–0.79×** (Win) / **0.78×** (Lin).

| Client | Origin | Lite sustain | Full sustain | Lite÷Reverse | Full÷Reverse |
|---|---|---:|---:|---:|---:|
| HTTP/1 · plain | HTTP/1 · plain | **25,596**<br><sub>(79 MiB / 51.2% CPU)</sub> | **24,770**<br><sub>(77 MiB / 50.7% CPU)</sub> | **1×** | **0.96×** |
| HTTP/1 · plain | HTTP/1 · TLS | **64,100**<br><sub>(91 MiB / 48.2% CPU)</sub> | **62,483**<br><sub>(91 MiB / 48.5% CPU)</sub> | **1.01×** | **0.99×** |
| HTTP/1 · plain | HTTP/2 · plain | **36,209**<br><sub>(106 MiB / 46.9% CPU)</sub> | **35,681**<br><sub>(105 MiB / 45.4% CPU)</sub> | **0.97×** | **0.96×** |
| HTTP/1 · plain | HTTP/2 · TLS | **38,208**<br><sub>(118 MiB / 48.4% CPU)</sub> | **37,384**<br><sub>(112 MiB / 47.8% CPU)</sub> | **0.99×** | **0.96×** |
| HTTP/1 · plain | HTTP/3 · QUIC | **29,048**<br><sub>(125 MiB / 52.1% CPU)</sub> | **28,872**<br><sub>(124 MiB / 48.6% CPU)</sub> | **0.96×** | **0.96×** |
| HTTP/1 · TLS | HTTP/1 · plain | **20,594**<br><sub>(88 MiB / 48.4% CPU)</sub> | **20,274**<br><sub>(91 MiB / 48.8% CPU)</sub> | **0.98×** | **0.97×** |
| HTTP/1 · TLS | HTTP/1 · TLS | **27,913**<br><sub>(93 MiB / 48.2% CPU)</sub> | **27,461**<br><sub>(90 MiB / 47.1% CPU)</sub> | **0.98×** | **0.97×** |
| HTTP/1 · TLS | HTTP/2 · plain | **27,517**<br><sub>(112 MiB / 43.8% CPU)</sub> | **27,067**<br><sub>(109 MiB / 45.5% CPU)</sub> | **0.98×** | **0.96×** |
| HTTP/1 · TLS | HTTP/2 · TLS | **25,288**<br><sub>(129 MiB / 46.3% CPU)</sub> | **24,611**<br><sub>(120 MiB / 45.8% CPU)</sub> | **1.02×** | **1×** |
| HTTP/1 · TLS | HTTP/3 · QUIC | **15,935**<br><sub>(113 MiB / 50.6% CPU)</sub> | **15,776**<br><sub>(116 MiB / 51.1% CPU)</sub> | **0.99×** | **0.98×** |
| HTTP/2 · plain | HTTP/1 · plain | **35,722**<br><sub>(89 MiB / 54.4% CPU)</sub> | **35,125**<br><sub>(88 MiB / 53.6% CPU)</sub> | **0.97×** | **0.95×** |
| HTTP/2 · plain | HTTP/1 · TLS | **27,212**<br><sub>(93 MiB / 48.9% CPU)</sub> | **29,793**<br><sub>(102 MiB / 54.1% CPU)</sub> | **0.86×** | **0.94×** |
| HTTP/2 · plain | HTTP/2 · plain | **86,486**<br><sub>(68 MiB / 41.6% CPU)</sub> | **82,565**<br><sub>(65 MiB / 43.3% CPU)</sub> | **0.77×** | **0.74×** |
| HTTP/2 · plain | HTTP/2 · TLS | **73,024**<br><sub>(72 MiB / 39% CPU)</sub> | **60,496**<br><sub>(72 MiB / 32.7% CPU)</sub> | **0.82×** | **0.68×** |
| HTTP/2 · plain | HTTP/3 · QUIC | **44,714**<br><sub>(138 MiB / 51.8% CPU)</sub> | **43,393**<br><sub>(142 MiB / 53.7% CPU)</sub> | **0.99×** | **0.96×** |
| HTTP/2 · TLS | HTTP/1 · plain | **34,283**<br><sub>(98 MiB / 54.4% CPU)</sub> | **33,345**<br><sub>(101 MiB / 53.7% CPU)</sub> | **0.98×** | **0.95×** |
| HTTP/2 · TLS | HTTP/1 · TLS | **42,583**<br><sub>(105 MiB / 50.4% CPU)</sub> | **44,500**<br><sub>(105 MiB / 52.2% CPU)</sub> | **0.93×** | **0.98×** |
| HTTP/2 · TLS | HTTP/2 · plain | **86,893**<br><sub>(87 MiB / 41% CPU)</sub> | **82,646**<br><sub>(84 MiB / 40.1% CPU)</sub> | **0.82×** | **0.78×** |
| HTTP/2 · TLS | HTTP/2 · TLS | **95,422**<br><sub>(80 MiB / 38.6% CPU)</sub> | **91,256**<br><sub>(86 MiB / 39.3% CPU)</sub> | **0.86×** | **0.82×** |
| HTTP/2 · TLS | HTTP/3 · QUIC | **42,970**<br><sub>(137 MiB / 53.7% CPU)</sub> | **40,850**<br><sub>(146 MiB / 50.5% CPU)</sub> | **0.98×** | **0.94×** |
| HTTP/3 · QUIC | HTTP/1 · plain | **14,677**<br><sub>(110 MiB / 46.1% CPU)</sub> | **13,733**<br><sub>(111 MiB / 45% CPU)</sub> | **0.96×** | **0.9×** |
| HTTP/3 · QUIC | HTTP/1 · TLS | **12,654**<br><sub>(114 MiB / 46.6% CPU)</sub> | **11,528**<br><sub>(115 MiB / 46.4% CPU)</sub> | **0.93×** | **0.85×** |
| HTTP/3 · QUIC | HTTP/2 · plain | **34,184**<br><sub>(130 MiB / 48.5% CPU)</sub> | **31,041**<br><sub>(124 MiB / 44.8% CPU)</sub> | **0.97×** | **0.88×** |
| HTTP/3 · QUIC | HTTP/2 · TLS | **31,178**<br><sub>(136 MiB / 47.7% CPU)</sub> | **30,002**<br><sub>(136 MiB / 47.4% CPU)</sub> | **0.98×** | **0.94×** |
| HTTP/3 · QUIC | HTTP/3 · QUIC | **15,983**<br><sub>(119 MiB / 49.3% CPU)</sub> | **15,523**<br><sub>(114 MiB / 48.4% CPU)</sub> | **0.95×** | **0.92×** |

## Linux — Titanium vs nginx vs HAProxy vs Envoy vs YARP

### Reverse

Median of **3 repeats** on `ubuntu-latest` (4 vCPU / 16 GiB). Bare reverse 5×5 @ `0386b2aa` — `compare-product` [37138944205](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37138944205). Warmup 2s / measure 8s; concurrency 8, 16, 32, 64. **Linux nginx is the authoritative nginx baseline.** HAProxy (3.2 `USE_QUIC`) and Envoy (GitHub release, HTTP/3 compiled in) run on the same loopback shape as nginx/YARP. nginx terminate peers use `keepalive 256` + streaming buffers. The RPS workflow installs nginx.org mainline (`http_v3_module`), a QUIC-enabled HAProxy, Envoy, and `libmsquic`. Prefer ratios over absolute RPS. Product 5×5 is **~56-byte JSON keep-alive GET**; H2/H3 same-protocol cells are mostly header work with a tiny body (Titanium best case) — see [Why this comparison is fair](#why-this-comparison-is-fair).

| Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---:|---:|---:|---:|---:|
| HTTP/1 · plain | HTTP/1 · plain | **32,632**<br><sub>(84 MiB / 50.1% CPU)</sub> | 🥇 **39,708**<br><sub>(76 MiB / 41.3% CPU)</sub> | **36,675**<br><sub>(66 MiB / 43.6% CPU)</sub> | **20,251**<br><sub>(115 MiB / 62.5% CPU)</sub> | **28,589**<br><sub>(115 MiB / 50.3% CPU)</sub> |
| HTTP/1 · plain | HTTP/1 · TLS | **24,764**<br><sub>(102 MiB / 49.8% CPU)</sub> | 🥇 **28,840**<br><sub>(92 MiB / 42.5% CPU)</sub> | **27,741**<br><sub>(68 MiB / 44.5% CPU)</sub> | **17,414**<br><sub>(117 MiB / 58.4% CPU)</sub> | **21,704**<br><sub>(132 MiB / 50.6% CPU)</sub> |
| HTTP/1 · plain | HTTP/2 · plain | 🥇 **80,883**<br><sub>(133 MiB / 49.8% CPU)</sub> | *Not possible (no H2 upstream)* | **49,902**<br><sub>(66 MiB / 41.3% CPU)</sub> | **57,690**<br><sub>(116 MiB / 53.8% CPU)</sub> | **71,970**<br><sub>(129 MiB / 47.5% CPU)</sub> |
| HTTP/1 · plain | HTTP/2 · TLS | 🥇 **70,384**<br><sub>(155 MiB / 48.8% CPU)</sub> | *Not possible (no H2 upstream)* | **65,133**<br><sub>(66 MiB / 42.6% CPU)</sub> | **55,862**<br><sub>(117 MiB / 53.6% CPU)</sub> | **64,060**<br><sub>(129 MiB / 45.9% CPU)</sub> |
| HTTP/1 · plain | HTTP/3 · QUIC | 🥇 **53,655**<br><sub>(159 MiB / 54.8% CPU)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **29,207**<br><sub>(121 MiB / 47.6% CPU)</sub> | **45,267**<br><sub>(170 MiB / 47.2% CPU)</sub> |
| HTTP/1 · TLS | HTTP/1 · plain | **37,443**<br><sub>(108 MiB / 49.1% CPU)</sub> | 🥇 **43,780**<br><sub>(102 MiB / 41.4% CPU)</sub> | **43,464**<br><sub>(83 MiB / 43.8% CPU)</sub> | **27,932**<br><sub>(127 MiB / 57.3% CPU)</sub> | **32,652**<br><sub>(133 MiB / 50.2% CPU)</sub> |
| HTTP/1 · TLS | HTTP/1 · TLS | **20,479**<br><sub>(111 MiB / 48.3% CPU)</sub> | **22,614**<br><sub>(103 MiB / 41.6% CPU)</sub> | 🥇 **22,867**<br><sub>(86 MiB / 42.8% CPU)</sub> | **15,496**<br><sub>(127 MiB / 56.4% CPU)</sub> | **17,542**<br><sub>(142 MiB / 50.1% CPU)</sub> |
| HTTP/1 · TLS | HTTP/2 · plain | 🥇 **53,639**<br><sub>(157 MiB / 48.2% CPU)</sub> | *Not possible (no H2 upstream)* | **45,206**<br><sub>(85 MiB / 39.2% CPU)</sub> | **40,719**<br><sub>(125 MiB / 56.7% CPU)</sub> | **48,562**<br><sub>(146 MiB / 49% CPU)</sub> |
| HTTP/1 · TLS | HTTP/2 · TLS | **47,125**<br><sub>(162 MiB / 47% CPU)</sub> | *Not possible (no H2 upstream)* | 🥇 **52,892**<br><sub>(85 MiB / 40.4% CPU)</sub> | **38,120**<br><sub>(127 MiB / 55.7% CPU)</sub> | **43,703**<br><sub>(146 MiB / 48% CPU)</sub> |
| HTTP/1 · TLS | HTTP/3 · QUIC | 🥇 **18,332**<br><sub>(142 MiB / 52.3% CPU)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **9,524**<br><sub>(130 MiB / 55.4% CPU)</sub> | **16,417**<br><sub>(158 MiB / 49.8% CPU)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | 🥇 **38,451**<br><sub>(114 MiB / 52.8% CPU)</sub> | **16,655**<br><sub>(79 MiB / 19% CPU)</sub> | **23,689**<br><sub>(67 MiB / 24.6% CPU)</sub> | **14,151**<br><sub>(118 MiB / 23.3% CPU)</sub> | **34,302**<br><sub>(114 MiB / 50.5% CPU)</sub> |
| HTTP/2 · plain | HTTP/1 · TLS | 🥇 **53,871**<br><sub>(132 MiB / 45.3% CPU)</sub> | **36,494**<br><sub>(102 MiB / 19.6% CPU)</sub> | **45,460**<br><sub>(70 MiB / 24.6% CPU)</sub> | **28,035**<br><sub>(119 MiB / 23.2% CPU)</sub> | **51,217**<br><sub>(124 MiB / 46.4% CPU)</sub> |
| HTTP/2 · plain | HTTP/2 · plain | 🥇 **93,213**<br><sub>(87 MiB / 39% CPU)</sub> | *Not possible (no H2 upstream)* | **29,683**<br><sub>(66 MiB / 24.3% CPU)</sub> | **25,025**<br><sub>(116 MiB / 21.7% CPU)</sub> | **54,957**<br><sub>(122 MiB / 46.7% CPU)</sub> |
| HTTP/2 · plain | HTTP/2 · TLS | 🥇 **58,499**<br><sub>(92 MiB / 36.4% CPU)</sub> | *Not possible (no H2 upstream)* | **19,745**<br><sub>(69 MiB / 24.4% CPU)</sub> | **16,269**<br><sub>(117 MiB / 21.4% CPU)</sub> | **39,312**<br><sub>(131 MiB / 46.2% CPU)</sub> |
| HTTP/2 · plain | HTTP/3 · QUIC | 🥇 **28,699**<br><sub>(139 MiB / 50.6% CPU)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **4,384**<br><sub>(121 MiB / 24.7% CPU)</sub> | **26,459**<br><sub>(152 MiB / 47.4% CPU)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **34,790**<br><sub>(117 MiB / 51.8% CPU)</sub> | **15,027**<br><sub>(100 MiB / 19.2% CPU)</sub> | **20,595**<br><sub>(82 MiB / 24.6% CPU)</sub> | **14,224**<br><sub>(128 MiB / 22.9% CPU)</sub> | **29,235**<br><sub>(121 MiB / 50.3% CPU)</sub> |
| HTTP/2 · TLS | HTTP/1 · TLS | 🥇 **27,313**<br><sub>(127 MiB / 49.9% CPU)</sub> | **12,471**<br><sub>(110 MiB / 19.9% CPU)</sub> | **17,008**<br><sub>(83 MiB / 24.3% CPU)</sub> | **12,882**<br><sub>(129 MiB / 23.2% CPU)</sub> | **23,308**<br><sub>(126 MiB / 50.2% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | 🥇 **83,894**<br><sub>(104 MiB / 33.6% CPU)</sub> | *Not possible (no H2 upstream)* | **34,166**<br><sub>(83 MiB / 24.1% CPU)</sub> | **19,477**<br><sub>(128 MiB / 21.4% CPU)</sub> | **45,281**<br><sub>(131 MiB / 45.6% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **105,834**<br><sub>(101 MiB / 33.8% CPU)</sub> | *Not possible (no H2 upstream)* | **49,401**<br><sub>(83 MiB / 24.2% CPU)</sub> | **28,708**<br><sub>(127 MiB / 21% CPU)</sub> | **60,670**<br><sub>(136 MiB / 43.9% CPU)</sub> |
| HTTP/2 · TLS | HTTP/3 · QUIC | 🥇 **26,506**<br><sub>(152 MiB / 49.3% CPU)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **4,391**<br><sub>(129 MiB / 24.8% CPU)</sub> | **23,067**<br><sub>(162 MiB / 47.3% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | **21,432**<br><sub>(142 MiB / 50.4% CPU)</sub> | **0**<br><sub>(peak 15,402 · 105 MiB / 22.5% CPU)</sub> | 🥇 **23,096**<br><sub>(87 MiB / 27.1% CPU)</sub> | **3,855**<br><sub>(134 MiB / 24.6% CPU)</sub> | **18,435**<br><sub>(182 MiB / 50.4% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | **24,920**<br><sub>(159 MiB / 48.3% CPU)</sub> | **0**<br><sub>(peak 19,115 · 120 MiB / 22.4% CPU)</sub> | 🥇 **27,176**<br><sub>(91 MiB / 28.7% CPU)</sub> | **1,058**<br><sub>(138 MiB / 4.6% CPU)</sub> | **22,554**<br><sub>(201 MiB / 49% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/2 · plain | 🥇 **29,202**<br><sub>(157 MiB / 52.8% CPU)</sub> | *Not possible (no H2 upstream)* | **25,453**<br><sub>(86 MiB / 26% CPU)</sub> | **422**<br><sub>(130 MiB / 2.3% CPU)</sub> | **23,989**<br><sub>(197 MiB / 48.4% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | 🥇 **25,256**<br><sub>(155 MiB / 50% CPU)</sub> | *Not possible (no H2 upstream)* | **21,256**<br><sub>(85 MiB / 26.6% CPU)</sub> | **514**<br><sub>(133 MiB / 2.8% CPU)</sub> | **21,430**<br><sub>(196 MiB / 47.6% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/3 · QUIC | 🥇 **42,187**<br><sub>(208 MiB / 49.3% CPU)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **157**<br><sub>(129 MiB / 0.5% CPU)</sub> | **31,146**<br><sub>(233 MiB / 46.3% CPU)</sub> |

### MITM (TWP only)

Same Client×Origin wires with interception on (`compare-product` [37138944205](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37138944205)). **Lite** = no-op handlers (unchanged-lite finish). **Full** = append-only header mutation (harness: one probe header each way; product: generic append-only relay via `MitmCompressedRelayHelper`). nginx/HAProxy/Envoy/YARP cannot MITM. **Lite÷Reverse** / **Full÷Reverse** vs bare reverse (**same job / comparison-group shard**). Completion gate: Lite ≥ **0.25×** and Full ≥ **0.25×** reverse sustain @ c=64 (median of 3 GHA runs); reverse TWP ÷ closest peer ≥ **0.50×**.

**v1 append-only relay (2026-08-27):** Pre-fix H2→H2 Full÷Reverse was **0.13–0.16×** ([32960766249](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32960766249)). Post-fix @ `df172718`: H2 plain→H2 plain Full **0.77–0.79×**, H3→H1 Full **0.91–0.93×**, all MITM arms ≥ **0.70×** on median of [33041445371](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33041445371), [33055267086](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33055267086), [33055272140](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33055272140).

**v2 drop-only + non-unique append (2026-08-27):** `MitmStaticRebuildHelper` rebuilds static HPACK/QPACK after 1–4 unique header drops; trailing non-unique appends stay on compressed relay. @ `41f4adee`: all MITM arms ≥ **0.70×** on GHA median ([33087088466](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087088466), [33087091622](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087091622), [33105885748](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33105885748) Linux; [33087085235](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087085235), [33087088466](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087088466), [33087091622](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087091622) Windows). H2 plain→H2 plain Full **0.77–0.79×** (Win) / **0.78×** (Lin).

| Client | Origin | Lite sustain | Full sustain | Lite÷Reverse | Full÷Reverse |
|---|---|---:|---:|---:|---:|
| HTTP/1 · plain | HTTP/1 · plain | **33,259**<br><sub>(92 MiB / 50.4% CPU)</sub> | **32,587**<br><sub>(89 MiB / 49.7% CPU)</sub> | **1.02×** | **1×** |
| HTTP/1 · plain | HTTP/1 · TLS | **24,374**<br><sub>(110 MiB / 50.7% CPU)</sub> | **24,526**<br><sub>(110 MiB / 50.5% CPU)</sub> | **0.98×** | **0.99×** |
| HTTP/1 · plain | HTTP/2 · plain | **78,913**<br><sub>(135 MiB / 51% CPU)</sub> | **77,344**<br><sub>(137 MiB / 51.6% CPU)</sub> | **0.98×** | **0.96×** |
| HTTP/1 · plain | HTTP/2 · TLS | **69,934**<br><sub>(149 MiB / 49% CPU)</sub> | **67,540**<br><sub>(157 MiB / 49.6% CPU)</sub> | **0.99×** | **0.96×** |
| HTTP/1 · plain | HTTP/3 · QUIC | **49,997**<br><sub>(174 MiB / 56.3% CPU)</sub> | **51,671**<br><sub>(171 MiB / 55.8% CPU)</sub> | **0.93×** | **0.96×** |
| HTTP/1 · TLS | HTTP/1 · plain | **37,625**<br><sub>(115 MiB / 49.8% CPU)</sub> | **36,499**<br><sub>(116 MiB / 49.1% CPU)</sub> | **1×** | **0.97×** |
| HTTP/1 · TLS | HTTP/1 · TLS | **20,555**<br><sub>(115 MiB / 49.2% CPU)</sub> | **19,875**<br><sub>(115 MiB / 49% CPU)</sub> | **1×** | **0.97×** |
| HTTP/1 · TLS | HTTP/2 · plain | **53,451**<br><sub>(145 MiB / 50.7% CPU)</sub> | **51,888**<br><sub>(147 MiB / 50.6% CPU)</sub> | **1×** | **0.97×** |
| HTTP/1 · TLS | HTTP/2 · TLS | **46,958**<br><sub>(153 MiB / 48.9% CPU)</sub> | **46,309**<br><sub>(154 MiB / 49.6% CPU)</sub> | **1×** | **0.98×** |
| HTTP/1 · TLS | HTTP/3 · QUIC | **17,442**<br><sub>(153 MiB / 52.6% CPU)</sub> | **16,947**<br><sub>(152 MiB / 52.9% CPU)</sub> | **0.95×** | **0.92×** |
| HTTP/2 · plain | HTTP/1 · plain | **37,304**<br><sub>(121 MiB / 53.8% CPU)</sub> | **35,995**<br><sub>(124 MiB / 53.7% CPU)</sub> | **0.97×** | **0.94×** |
| HTTP/2 · plain | HTTP/1 · TLS | **52,626**<br><sub>(136 MiB / 46.2% CPU)</sub> | **51,109**<br><sub>(134 MiB / 46.6% CPU)</sub> | **0.98×** | **0.95×** |
| HTTP/2 · plain | HTTP/2 · plain | **70,046**<br><sub>(95 MiB / 43.8% CPU)</sub> | **64,950**<br><sub>(94 MiB / 42.7% CPU)</sub> | **0.75×** | **0.7×** |
| HTTP/2 · plain | HTTP/2 · TLS | **48,692**<br><sub>(97 MiB / 40.1% CPU)</sub> | **46,169**<br><sub>(96 MiB / 39.9% CPU)</sub> | **0.83×** | **0.79×** |
| HTTP/2 · plain | HTTP/3 · QUIC | **28,077**<br><sub>(144 MiB / 50.8% CPU)</sub> | **27,749**<br><sub>(145 MiB / 51.3% CPU)</sub> | **0.98×** | **0.97×** |
| HTTP/2 · TLS | HTTP/1 · plain | **33,799**<br><sub>(118 MiB / 53.1% CPU)</sub> | **33,370**<br><sub>(119 MiB / 53.1% CPU)</sub> | **0.97×** | **0.96×** |
| HTTP/2 · TLS | HTTP/1 · TLS | **26,984**<br><sub>(122 MiB / 51.6% CPU)</sub> | **25,959**<br><sub>(128 MiB / 51.2% CPU)</sub> | **0.99×** | **0.95×** |
| HTTP/2 · TLS | HTTP/2 · plain | **63,878**<br><sub>(109 MiB / 37.2% CPU)</sub> | **60,327**<br><sub>(113 MiB / 36.8% CPU)</sub> | **0.76×** | **0.72×** |
| HTTP/2 · TLS | HTTP/2 · TLS | **83,024**<br><sub>(111 MiB / 35.7% CPU)</sub> | **80,237**<br><sub>(112 MiB / 35.3% CPU)</sub> | **0.78×** | **0.76×** |
| HTTP/2 · TLS | HTTP/3 · QUIC | **26,796**<br><sub>(155 MiB / 50.8% CPU)</sub> | **26,187**<br><sub>(152 MiB / 50.9% CPU)</sub> | **1.01×** | **0.99×** |
| HTTP/3 · QUIC | HTTP/1 · plain | **20,453**<br><sub>(140 MiB / 51.4% CPU)</sub> | **19,695**<br><sub>(143 MiB / 50.5% CPU)</sub> | **0.95×** | **0.92×** |
| HTTP/3 · QUIC | HTTP/1 · TLS | **24,865**<br><sub>(157 MiB / 49.6% CPU)</sub> | **23,742**<br><sub>(158 MiB / 49% CPU)</sub> | **1×** | **0.95×** |
| HTTP/3 · QUIC | HTTP/2 · plain | **29,174**<br><sub>(157 MiB / 53.8% CPU)</sub> | **28,145**<br><sub>(159 MiB / 53.5% CPU)</sub> | **1×** | **0.96×** |
| HTTP/3 · QUIC | HTTP/2 · TLS | **24,698**<br><sub>(153 MiB / 51.1% CPU)</sub> | **24,065**<br><sub>(152 MiB / 50.3% CPU)</sub> | **0.98×** | **0.95×** |
| HTTP/3 · QUIC | HTTP/3 · QUIC | **37,404**<br><sub>(201 MiB / 52.5% CPU)</sub> | **38,997**<br><sub>(206 MiB / 53.1% CPU)</sub> | **0.89×** | **0.92×** |

## macOS — Titanium vs nginx vs HAProxy vs Envoy vs YARP

### Reverse

Median of **3 repeats** on `macos-15` (Apple Silicon M1, 3-core / 7 GB). Bare reverse 5×5 @ `0386b2aa` — `compare-product` [37138944205](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37138944205). Warmup 2s / measure 8s; concurrency 8, 16, 32, 64. Prefer TWP÷peer ratios over absolute RPS. **RPS cells** include median RSS / CPU at the peak-RPS step as `<br><sub>(MiB / CPU%)</sub>`. The RPS workflow installs Homebrew nginx (`http_v3_module`), Homebrew HAProxy with `USE_QUIC` (3.2 source fallback), Envoy (Homebrew bottle or pinned darwin-arm64 1.36.7), Homebrew `libmsquic` (+ `DYLD_*`), and YARP. Use the pinned `macos-15` label, not `macos-latest`. Product 5×5 is **~56-byte JSON keep-alive GET**; H2/H3 same-protocol cells are mostly header work with a tiny body (Titanium best case) — see [Why this comparison is fair](#why-this-comparison-is-fair).

| Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---:|---:|---:|---:|---:|
| HTTP/1 · plain | HTTP/1 · plain | **11,350**<br><sub>(115 MiB / 19.3% CPU)</sub> | **16,390**<br><sub>(68 MiB / 15.7% CPU)</sub> | 🥇 **16,634**<br><sub>(83 MiB / 17.7% CPU)</sub> | **6,996**<br><sub>(112 MiB / 21.6% CPU)</sub> | **5,588**<br><sub>(159 MiB / 12.1% CPU)</sub> |
| HTTP/1 · plain | HTTP/1 · TLS | **19,968**<br><sub>(129 MiB / 24.9% CPU)</sub> | 🥇 **25,998**<br><sub>(78 MiB / 21.8% CPU)</sub> | **19,833**<br><sub>(88 MiB / 20.1% CPU)</sub> | **11,274**<br><sub>(114 MiB / 30.5% CPU)</sub> | **22,188**<br><sub>(164 MiB / 28.3% CPU)</sub> |
| HTTP/1 · plain | HTTP/2 · plain | 🥇 **21,519**<br><sub>(129 MiB / 20.2% CPU)</sub> | *Not possible (no H2 upstream)* | **19,366**<br><sub>(84 MiB / 20.1% CPU)</sub> | **11,218**<br><sub>(111 MiB / 25% CPU)</sub> | **11,842**<br><sub>(166 MiB / 13.4% CPU)</sub> |
| HTTP/1 · plain | HTTP/2 · TLS | **17,272**<br><sub>(169 MiB / 20.6% CPU)</sub> | *Not possible (no H2 upstream)* | 🥇 **19,290**<br><sub>(85 MiB / 22.1% CPU)</sub> | **18,595**<br><sub>(112 MiB / 44.8% CPU)</sub> | **12,441**<br><sub>(177 MiB / 14.4% CPU)</sub> |
| HTTP/1 · plain | HTTP/3 · QUIC | **7,979**<br><sub>(129 MiB / 15% CPU)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **9,001**<br><sub>(116 MiB / 27.1% CPU)</sub> | 🥇 **14,647**<br><sub>(163 MiB / 22% CPU)</sub> |
| HTTP/1 · TLS | HTTP/1 · plain | **8,027**<br><sub>(125 MiB / 19.2% CPU)</sub> | 🥇 **17,442**<br><sub>(84 MiB / 17.5% CPU)</sub> | **10,406**<br><sub>(100 MiB / 19.7% CPU)</sub> | **15,576**<br><sub>(122 MiB / 37.2% CPU)</sub> | **9,049**<br><sub>(162 MiB / 21.5% CPU)</sub> |
| HTTP/1 · TLS | HTTP/1 · TLS | **12,065**<br><sub>(154 MiB / 25.1% CPU)</sub> | 🥇 **16,714**<br><sub>(89 MiB / 17.9% CPU)</sub> | **9,055**<br><sub>(103 MiB / 17.9% CPU)</sub> | **6,018**<br><sub>(122 MiB / 20.8% CPU)</sub> | **15,945**<br><sub>(180 MiB / 12.1% CPU)</sub> |
| HTTP/1 · TLS | HTTP/2 · plain | **33,147**<br><sub>(155 MiB / 40.3% CPU)</sub> | *Not possible (no H2 upstream)* | **18,269**<br><sub>(101 MiB / 30.7% CPU)</sub> | **27,665**<br><sub>(121 MiB / 45.4% CPU)</sub> | 🥇 **45,625**<br><sub>(165 MiB / 36.5% CPU)</sub> |
| HTTP/1 · TLS | HTTP/2 · TLS | **11,455**<br><sub>(207 MiB / 17.2% CPU)</sub> | *Not possible (no H2 upstream)* | 🥇 **30,959**<br><sub>(101 MiB / 27.9% CPU)</sub> | **23,149**<br><sub>(120 MiB / 38.3% CPU)</sub> | **4,878**<br><sub>(223 MiB / 7.6% CPU)</sub> |
| HTTP/1 · TLS | HTTP/3 · QUIC | **6,409**<br><sub>(152 MiB / 23.6% CPU)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **7,288**<br><sub>(125 MiB / 35.9% CPU)</sub> | 🥇 **7,959**<br><sub>(183 MiB / 22.8% CPU)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | **19,856**<br><sub>(133 MiB / 20.8% CPU)</sub> | 🥇 **21,532**<br><sub>(70 MiB / 17% CPU)</sub> | **7,455**<br><sub>(87 MiB / 7.2% CPU)</sub> | **9,828**<br><sub>(112 MiB / 18.2% CPU)</sub> | **11,796**<br><sub>(151 MiB / 14.3% CPU)</sub> |
| HTTP/2 · plain | HTTP/1 · TLS | 🥇 **39,072**<br><sub>(134 MiB / 35% CPU)</sub> | **22,480**<br><sub>(80 MiB / 22.6% CPU)</sub> | **37,659**<br><sub>(92 MiB / 20.2% CPU)</sub> | **15,170**<br><sub>(114 MiB / 17.5% CPU)</sub> | **36,270**<br><sub>(162 MiB / 34.7% CPU)</sub> |
| HTTP/2 · plain | HTTP/2 · plain | 🥇 **38,837**<br><sub>(115 MiB / 9.8% CPU)</sub> | *Not possible (no H2 upstream)* | **35,788**<br><sub>(87 MiB / 18.4% CPU)</sub> | **20,918**<br><sub>(109 MiB / 17.9% CPU)</sub> | **34,366**<br><sub>(164 MiB / 19.1% CPU)</sub> |
| HTTP/2 · plain | HTTP/2 · TLS | 🥇 **20,738**<br><sub>(118 MiB / 6.4% CPU)</sub> | *Not possible (no H2 upstream)* | **19,404**<br><sub>(89 MiB / 18.2% CPU)</sub> | **18,552**<br><sub>(111 MiB / 26.2% CPU)</sub> | **6,645**<br><sub>(167 MiB / 7.3% CPU)</sub> |
| HTTP/2 · plain | HTTP/3 · QUIC | 🥇 **10,950**<br><sub>(139 MiB / 20.9% CPU)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **10,452**<br><sub>(114 MiB / 23.7% CPU)</sub> | **4,648**<br><sub>(167 MiB / 9.7% CPU)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | **18,702**<br><sub>(132 MiB / 19.7% CPU)</sub> | **19,621**<br><sub>(81 MiB / 21.2% CPU)</sub> | 🥇 **36,352**<br><sub>(101 MiB / 19.4% CPU)</sub> | **17,403**<br><sub>(121 MiB / 21.3% CPU)</sub> | **9,754**<br><sub>(154 MiB / 17.5% CPU)</sub> |
| HTTP/2 · TLS | HTTP/1 · TLS | **9,044**<br><sub>(140 MiB / 20.3% CPU)</sub> | 🥇 **17,051**<br><sub>(86 MiB / 15.6% CPU)</sub> | **12,741**<br><sub>(104 MiB / 18% CPU)</sub> | **8,500**<br><sub>(121 MiB / 13.9% CPU)</sub> | **4,103**<br><sub>(168 MiB / 9.4% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | 🥇 **68,492**<br><sub>(130 MiB / 22.1% CPU)</sub> | *Not possible (no H2 upstream)* | **27,664**<br><sub>(101 MiB / 19.1% CPU)</sub> | **19,693**<br><sub>(119 MiB / 20.7% CPU)</sub> | **28,016**<br><sub>(158 MiB / 16.2% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **88,279**<br><sub>(136 MiB / 33% CPU)</sub> | *Not possible (no H2 upstream)* | **37,474**<br><sub>(102 MiB / 28.7% CPU)</sub> | **22,960**<br><sub>(119 MiB / 25.1% CPU)</sub> | **47,514**<br><sub>(159 MiB / 42.4% CPU)</sub> |
| HTTP/2 · TLS | HTTP/3 · QUIC | **5,481**<br><sub>(152 MiB / 11% CPU)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | 🥇 **8,025**<br><sub>(123 MiB / 20% CPU)</sub> | **4,994**<br><sub>(171 MiB / 8.9% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | **8,392**<br><sub>(131 MiB / 20.4% CPU)</sub> | **0**<br><sub>(peak 14,230 · 87 MiB / 13.3% CPU)</sub> | 🥇 **15,556**<br><sub>(101 MiB / 20.2% CPU)</sub> | **5,735**<br><sub>(130 MiB / 32.9% CPU)</sub> | **5,035**<br><sub>(244 MiB / 16.8% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | **12,889**<br><sub>(152 MiB / 28.8% CPU)</sub> | **0**<br><sub>(peak 14,318 · 92 MiB / 18.5% CPU)</sub> | 🥇 **17,962**<br><sub>(104 MiB / 22.1% CPU)</sub> | **4,688**<br><sub>(130 MiB / 27.4% CPU)</sub> | **12,974**<br><sub>(239 MiB / 33.9% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/2 · plain | **15,867**<br><sub>(139 MiB / 21.5% CPU)</sub> | *Not possible (no H2 upstream)* | 🥇 **21,919**<br><sub>(102 MiB / 19.7% CPU)</sub> | **9,652**<br><sub>(125 MiB / 32.5% CPU)</sub> | **10,054**<br><sub>(267 MiB / 14.1% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | **28,390**<br><sub>(146 MiB / 24.8% CPU)</sub> | *Not possible (no H2 upstream)* | 🥇 **34,002**<br><sub>(102 MiB / 20.4% CPU)</sub> | **11,891**<br><sub>(125 MiB / 28.3% CPU)</sub> | **26,452**<br><sub>(232 MiB / 28% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/3 · QUIC | **4,442**<br><sub>(138 MiB / 9% CPU)</sub> | *Not possible (no H3 upstream)* | *Not possible (no H3 upstream)* | **7,144**<br><sub>(124 MiB / 38.2% CPU)</sub> | 🥇 **23,628**<br><sub>(226 MiB / 44.2% CPU)</sub> |

### MITM (TWP only)

Same Client×Origin wires with interception on (`compare-product` [37138944205](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37138944205)). **Lite** = no-op handlers (unchanged-lite finish). **Full** = append-only header mutation (harness: one probe header each way; product: generic append-only relay via `MitmCompressedRelayHelper`). nginx/HAProxy/Envoy/YARP cannot MITM. **Lite÷Reverse** / **Full÷Reverse** vs bare reverse (**same job / comparison-group shard**). Completion gate: Lite ≥ **0.25×** and Full ≥ **0.25×** reverse sustain @ c=64 (median of 3 GHA runs); reverse TWP ÷ closest peer ≥ **0.50×**.

**v1 append-only relay (2026-08-27):** Pre-fix H2→H2 Full÷Reverse was **0.13–0.16×** ([32960766249](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32960766249)). Post-fix @ `df172718`: H2 plain→H2 plain Full **0.77–0.79×**, H3→H1 Full **0.91–0.93×**, all MITM arms ≥ **0.70×** on median of [33041445371](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33041445371), [33055267086](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33055267086), [33055272140](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33055272140).

**v2 drop-only + non-unique append (2026-08-27):** `MitmStaticRebuildHelper` rebuilds static HPACK/QPACK after 1–4 unique header drops; trailing non-unique appends stay on compressed relay. @ `41f4adee`: all MITM arms ≥ **0.70×** on GHA median ([33087088466](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087088466), [33087091622](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087091622), [33105885748](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33105885748) Linux; [33087085235](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087085235), [33087088466](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087088466), [33087091622](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087091622) Windows). H2 plain→H2 plain Full **0.77–0.79×** (Win) / **0.78×** (Lin).

| Client | Origin | Lite sustain | Full sustain | Lite÷Reverse | Full÷Reverse |
|---|---|---:|---:|---:|---:|
| HTTP/1 · plain | HTTP/1 · plain | **7,576**<br><sub>(114 MiB / 14.9% CPU)</sub> | **9,645**<br><sub>(115 MiB / 16.8% CPU)</sub> | **0.67×** | **0.85×** |
| HTTP/1 · plain | HTTP/1 · TLS | **24,309**<br><sub>(128 MiB / 35.4% CPU)</sub> | **27,202**<br><sub>(131 MiB / 37.7% CPU)</sub> | **1.22×** | **1.36×** |
| HTTP/1 · plain | HTTP/2 · plain | **14,974**<br><sub>(124 MiB / 19.1% CPU)</sub> | **17,313**<br><sub>(126 MiB / 21.7% CPU)</sub> | **0.7×** | **0.8×** |
| HTTP/1 · plain | HTTP/2 · TLS | **34,172**<br><sub>(159 MiB / 39.3% CPU)</sub> | **31,029**<br><sub>(164 MiB / 38.3% CPU)</sub> | **1.98×** | **1.8×** |
| HTTP/1 · plain | HTTP/3 · QUIC | **12,590**<br><sub>(132 MiB / 25.2% CPU)</sub> | **11,926**<br><sub>(130 MiB / 20.1% CPU)</sub> | **1.58×** | **1.49×** |
| HTTP/1 · TLS | HTTP/1 · plain | **20,534**<br><sub>(131 MiB / 26.5% CPU)</sub> | **15,946**<br><sub>(129 MiB / 22.1% CPU)</sub> | **2.56×** | **1.99×** |
| HTTP/1 · TLS | HTTP/1 · TLS | **11,892**<br><sub>(153 MiB / 21.9% CPU)</sub> | **17,346**<br><sub>(153 MiB / 26.1% CPU)</sub> | **0.99×** | **1.44×** |
| HTTP/1 · TLS | HTTP/2 · plain | **35,283**<br><sub>(155 MiB / 40.2% CPU)</sub> | **31,431**<br><sub>(140 MiB / 42.3% CPU)</sub> | **1.06×** | **0.95×** |
| HTTP/1 · TLS | HTTP/2 · TLS | **14,403**<br><sub>(194 MiB / 20.9% CPU)</sub> | **12,129**<br><sub>(183 MiB / 20.9% CPU)</sub> | **1.26×** | **1.06×** |
| HTTP/1 · TLS | HTTP/3 · QUIC | **10,352**<br><sub>(163 MiB / 35% CPU)</sub> | **14,285**<br><sub>(168 MiB / 45.1% CPU)</sub> | **1.62×** | **2.23×** |
| HTTP/2 · plain | HTTP/1 · plain | **15,612**<br><sub>(125 MiB / 19.1% CPU)</sub> | **21,319**<br><sub>(131 MiB / 23% CPU)</sub> | **0.79×** | **1.07×** |
| HTTP/2 · plain | HTTP/1 · TLS | **34,941**<br><sub>(135 MiB / 28.7% CPU)</sub> | **24,438**<br><sub>(133 MiB / 21.6% CPU)</sub> | **0.89×** | **0.63×** |
| HTTP/2 · plain | HTTP/2 · plain | **31,292**<br><sub>(117 MiB / 8.2% CPU)</sub> | **12,801**<br><sub>(118 MiB / 4.9% CPU)</sub> | **0.81×** | **0.33×** |
| HTTP/2 · plain | HTTP/2 · TLS | **20,941**<br><sub>(122 MiB / 9.6% CPU)</sub> | **22,810**<br><sub>(122 MiB / 9.8% CPU)</sub> | **1.01×** | **1.1×** |
| HTTP/2 · plain | HTTP/3 · QUIC | **17,307**<br><sub>(142 MiB / 27.5% CPU)</sub> | **20,389**<br><sub>(141 MiB / 28.4% CPU)</sub> | **1.58×** | **1.86×** |
| HTTP/2 · TLS | HTTP/1 · plain | **23,294**<br><sub>(129 MiB / 20.6% CPU)</sub> | **43,858**<br><sub>(134 MiB / 36.2% CPU)</sub> | **1.25×** | **2.35×** |
| HTTP/2 · TLS | HTTP/1 · TLS | **22,634**<br><sub>(136 MiB / 24.4% CPU)</sub> | **25,783**<br><sub>(138 MiB / 27.3% CPU)</sub> | **2.5×** | **2.85×** |
| HTTP/2 · TLS | HTTP/2 · plain | **42,178**<br><sub>(131 MiB / 13.2% CPU)</sub> | **17,298**<br><sub>(129 MiB / 6% CPU)</sub> | **0.62×** | **0.25×** |
| HTTP/2 · TLS | HTTP/2 · TLS | **62,521**<br><sub>(135 MiB / 35.5% CPU)</sub> | **64,449**<br><sub>(128 MiB / 36.4% CPU)</sub> | **0.71×** | **0.73×** |
| HTTP/2 · TLS | HTTP/3 · QUIC | **18,866**<br><sub>(155 MiB / 24.2% CPU)</sub> | **14,641**<br><sub>(148 MiB / 24.6% CPU)</sub> | **3.44×** | **2.67×** |
| HTTP/3 · QUIC | HTTP/1 · plain | **8,665**<br><sub>(134 MiB / 26.2% CPU)</sub> | **9,121**<br><sub>(133 MiB / 23.3% CPU)</sub> | **1.03×** | **1.09×** |
| HTTP/3 · QUIC | HTTP/1 · TLS | **4,672**<br><sub>(145 MiB / 13.6% CPU)</sub> | **11,598**<br><sub>(162 MiB / 44.7% CPU)</sub> | **0.36×** | **0.9×** |
| HTTP/3 · QUIC | HTTP/2 · plain | **18,806**<br><sub>(138 MiB / 22.9% CPU)</sub> | **20,631**<br><sub>(139 MiB / 23.9% CPU)</sub> | **1.19×** | **1.3×** |
| HTTP/3 · QUIC | HTTP/2 · TLS | **25,202**<br><sub>(146 MiB / 29.8% CPU)</sub> | **33,495**<br><sub>(166 MiB / 48.8% CPU)</sub> | **0.89×** | **1.18×** |
| HTTP/3 · QUIC | HTTP/3 · QUIC | **15,107**<br><sub>(147 MiB / 44.2% CPU)</sub> | **18,546**<br><sub>(143 MiB / 47.7% CPU)</sub> | **3.4×** | **4.18×** |

## Editions (CLI / Plus / Intercept)

**Note:** `twp-reverse-http1` and other library rows use Core with **probe-tuned** settings (no logging, no Via header, probe-warmed certs). Edition rows use `titanium run -c twp.yaml` **product defaults** — prefer the ÷baseline ratio column over absolute RPS. Inspector GUI is not spawnable in the harness; session-path overhead is `twp-cli-intercept-http1` (route `RequestHeaderSet` transform). Pre-origin Plus middleware (CIDR/WAF/JWT/rate-limit/cache) runs on H1 terminate-lite without `SessionEventArgs`; a cache hit skips the origin. JWT caches successful bearer validations. CORS only adds response headers on the way out, so it stays on terminate-lite. Circuit breaker and idempotent retry have to see the request or the status code, so they stay on the session path and should land near the intercept row. Maintainer gate thresholds live under [Maintainer notes](#maintainer-notes).

Median of **3** repeats @ `0386b2aa`. Source: Actions [37156921654](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37156921654). Warmup 2s / measure 8s; concurrency 8–64; sustain = median peak RPS among SLO-pass steps @ **c=64**. **RPS cells** show sustain; `<sub>` holds peak (when higher) plus `(MiB / CPU%)`. The Gate column is the floor enforced by `validate-edition-gates.ps1`.

| Arm | Win | Linux | Win÷ | Lin÷ | Gate |
|---|---:|---:|---:|---:|---|
| `twp-cli-reverse-http1` vs library | **26,229**<br><sub>(126 MiB / 47.2% CPU)</sub> | **48,005**<br><sub>(172 MiB / 48.5% CPU)</sub> | **1.01×** | **1.04×** | ≥ **0.50×** |
| `twp-cli-reverse-http1-tls` vs library TLS | **20,885**<br><sub>(147 MiB / 46.5% CPU)</sub> | **35,819**<br><sub>(203 MiB / 48.7% CPU)</sub> | **0.99×** | **1.02×** | ≥ **0.50×** |
| `twp-cli-reverse-http1-route` vs CLI | **26,081**<br><sub>(135 MiB / 46.6% CPU)</sub> | **47,759**<br><sub>(174 MiB / 48.4% CPU)</sub> | **0.99×** | **0.99×** | ≥ **0.50×** |
| `twp-cli-plus-base-http1` vs CLI | **26,212**<br><sub>(136 MiB / 49.8% CPU)</sub> | **47,791**<br><sub>(174 MiB / 48.5% CPU)</sub> | **1.00×** | **1.00×** | ≥ **0.50×** |
| `twp-cli-plus-cache-http1` (cold) vs CLI | **21,979**<br><sub>(143 MiB / 56.1% CPU)</sub> | **40,544**<br><sub>(182 MiB / 52.4% CPU)</sub> | **0.84×** | **0.84×** | ≥ **0.50×** |
| `twp-cli-intercept-http1` vs CLI | **19,785**<br><sub>(143 MiB / 56.1% CPU)</sub> | **35,573**<br><sub>(183 MiB / 52.1% CPU)</sub> | **0.75×** | **0.74×** | ≥ **0.50×** |
| `twp-cli-plus-waf-http1` vs CLI | **25,176**<br><sub>(137 MiB / 52.7% CPU)</sub> | **46,484**<br><sub>(179 MiB / 48.9% CPU)</sub> | **0.96×** | **0.97×** | ≥ **0.50×** |
| `twp-cli-plus-cidr-http1` vs CLI | **25,358**<br><sub>(138 MiB / 48.7% CPU)</sub> | **46,646**<br><sub>(178 MiB / 48.9% CPU)</sub> | **0.97×** | **0.97×** | ≥ **0.50×** |
| `twp-cli-plus-jwt-http1` vs CLI | **24,452**<br><sub>(156 MiB / 49.2% CPU)</sub> | **43,613**<br><sub>(199 MiB / 48.7% CPU)</sub> | **0.93×** | **0.91×** | ≥ **0.50×** |
| `twp-cli-plus-ratelimit-http1` vs CLI | **25,149**<br><sub>(135 MiB / 48.0% CPU)</sub> | **46,620**<br><sub>(177 MiB / 49.1% CPU)</sub> | **0.96×** | **0.97×** | ≥ **0.50×** |
| `twp-cli-plus-resilience-http1` vs CLI | **25,850**<br><sub>(147 MiB / 47.5% CPU)</sub> | **47,710**<br><sub>(181 MiB / 48.4% CPU)</sub> | **0.99×** | **0.99×** | ≥ **0.50×** |
| `twp-cli-plus-discovery-file-http1` vs CLI | **26,007**<br><sub>(143 MiB / 50.4% CPU)</sub> | **47,630**<br><sub>(180 MiB / 48.5% CPU)</sub> | **0.99×** | **0.99×** | ≥ **0.50×** |
| `twp-cli-plus-metrics-scrape-http1` vs CLI | **26,308**<br><sub>(133 MiB / 48.3% CPU)</sub> | **48,257**<br><sub>(183 MiB / 48.5% CPU)</sub> | **1.00×** | **1.01×** | ≥ **0.50×** |
| `twp-cli-plus-cache-hit-http1` vs cache cold | **21,508**<br><sub>(139 MiB / 53.1% CPU)</sub> | **39,610**<br><sub>(181 MiB / 52.1% CPU)</sub> | **0.98×** | **0.98×** | ≥ **0.50×** |
| `twp-cli-static-http1` vs CLI | **40,478**<br><sub>(129 MiB / 51.9% CPU)</sub> | **80,449**<br><sub>(175 MiB / 50.3% CPU)</sub> | **1.54×** | **1.68×** | ≥ **0.50×** |
| `twp-cli-logging-http1` vs CLI | **26,283**<br><sub>(132 MiB / 49.0% CPU)</sub> | **47,905**<br><sub>(175 MiB / 48.5% CPU)</sub> | **1.00×** | **1.00×** | ≥ **0.50×** |
| `twp-cli-lb-leasttime-http1` vs route | **23,925**<br><sub>(146 MiB / 53.5% CPU)</sub> | **43,887**<br><sub>(197 MiB / 50.7% CPU)</sub> | **0.92×** | **0.92×** | ≥ **0.50×** |
| `twp-cli-dialect-twp-http1` vs CLI | **26,366**<br><sub>(134 MiB / 47.3% CPU)</sub> | **47,368**<br><sub>(168 MiB / 48.3% CPU)</sub> | **1.01×** | **0.99×** | ≥ **0.50×** |

`validate-edition-gates.ps1` **passed** on Windows and Linux for this run (every ratio ≥ **0.50×**). Library baselines @ c=64 (same job): Win H1 **25,970** / TLS **21,167**; Linux H1 **45,968** / TLS **35,115**. The macOS job lost the hosted runner before the ramp finished, so this table stays Windows and Linux. Laptop smoke ratios stay on [Performance Local Lab — Editions](Performance-Local-Lab#editions-cli--plus-stress).

## Heavier reverse workloads

Same runners and harness as the tiny-GET tables, but with larger bodies, POST, lossy links, TLS cost, and architecture-sensitive paths (slow consumer / early response / duplex). **PUT with the same body is the same proxy work as POST; DELETE with no body matches GET** — only POST is published. Bodies/POST/lossy stay **half-duplex**. Laptop numbers are on [Performance Local Lab](Performance-Local-Lab#architecture-sensitive). How maintainers refresh these tables (modes, shards, paste scripts) is under [Maintainer notes](#maintainer-notes). **Larger-body check:** 64 / 256 KiB H2 TLS→H2 TLS is where body copy dominates headers — ratios vs YARP are in the tables below (@ `0386b2aa`), unlike the tiny-GET H2↔H2 medals above.

Lossy link = **userspace** delay/drop shim (not kernel `netem`): TCP gets per-buffer delay + occasional whole-connection stalls (honest head-of-line for multiplexed HTTP/2); UDP is **loss% only** (no per-datagram delay) + drops (QUIC / MsQuic-safe). Lossy tables publish HTTP/1, HTTP/2, and HTTP/3.

### Windows — heavier reverse GET (64 KiB / 256 KiB)

Median of **3** repeats on `windows-latest` @ `0386b2aa`. Source: Actions [37139047700](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37139047700) (`compare-bodies`). Warmup 2s / measure 8s. **RPS cells** include `(MiB / CPU%)` footprints.

*Not possible:* **HAProxy** and **Envoy** columns are omitted (no official Windows port).
















| Body | Client | Origin | TWP | nginx | YARP |
|---|---|---|---:|---:|---:|
| 64 KiB | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **9,209**<br><sub>(89 MiB / 44.8% CPU)</sub> | **571**<br><sub>(141 MiB / 24.4% CPU)</sub> | **7,978**<br><sub>(137 MiB / 51.3% CPU)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **7,957**<br><sub>(170 MiB / 46.5% CPU)</sub> | **555**<br><sub>(142 MiB / 24.8% CPU)</sub> | **6,580**<br><sub>(134 MiB / 50.6% CPU)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **4,140**<br><sub>(129 MiB / 37.4% CPU)</sub> | *Not possible (no QUIC)* | **3,984**<br><sub>(179 MiB / 48.8% CPU)</sub> |
| 256 KiB | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **2,846**<br><sub>(126 MiB / 47.1% CPU)</sub> | **173**<br><sub>(142 MiB / 24.8% CPU)</sub> | **2,689**<br><sub>(135 MiB / 47.9% CPU)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **4,398**<br><sub>(155 MiB / 34.0% CPU)</sub> | **306**<br><sub>(142 MiB / 24.8% CPU)</sub> | **3,753**<br><sub>(135 MiB / 41.6% CPU)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **1,140**<br><sub>(113 MiB / 38.8% CPU)</sub> | *Not possible (no QUIC)* | **1,096**<br><sub>(177 MiB / 44.3% CPU)</sub> |
| 64 KiB | HTTP/2 · plain | HTTP/1 · plain | **20,936**<br><sub>(163 MiB / 35.6% CPU)</sub> | **4,959**<br><sub>(127 MiB / 24.6% CPU)</sub> | 🥇 **23,518**<br><sub>(106 MiB / 44.2% CPU)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/2 · plain | 🥇 **10,843**<br><sub>(118 MiB / 39.3% CPU)</sub> | *Not possible* | **7,247**<br><sub>(142 MiB / 51.0% CPU)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **7,380**<br><sub>(122 MiB / 39.0% CPU)</sub> | *Not possible* | **5,547**<br><sub>(142 MiB / 48.2% CPU)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/2 · TLS | 🥇 **3,985**<br><sub>(124 MiB / 40.0% CPU)</sub> | *Not possible* | **3,755**<br><sub>(198 MiB / 48.2% CPU)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **4,000**<br><sub>(143 MiB / 40.5% CPU)</sub> | *Not possible (no QUIC)* | **3,361**<br><sub>(193 MiB / 49.5% CPU)</sub> |
| 256 KiB | HTTP/2 · plain | HTTP/1 · plain | 🥇 **3,240**<br><sub>(149 MiB / 31.6% CPU)</sub> | **842**<br><sub>(127 MiB / 24.4% CPU)</sub> | **2,740**<br><sub>(124 MiB / 37.5% CPU)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/2 · plain | 🥇 **2,591**<br><sub>(152 MiB / 37.1% CPU)</sub> | *Not possible* | **1,874**<br><sub>(167 MiB / 46.7% CPU)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **1,868**<br><sub>(129 MiB / 35.2% CPU)</sub> | *Not possible* | **1,495**<br><sub>(148 MiB / 43.8% CPU)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/2 · TLS | **870**<br><sub>(148 MiB / 42.1% CPU)</sub> | *Not possible* | 🥇 **990**<br><sub>(198 MiB / 44.5% CPU)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **1,030**<br><sub>(145 MiB / 39.2% CPU)</sub> | *Not possible (no QUIC)* | **975**<br><sub>(195 MiB / 44.0% CPU)</sub> |

nginx/Windows collapses on large reverse bodies in this harness; treat as same-OS only. Prefer the ratio columns; absolute RPS swings by VM.

### Linux — heavier reverse GET (64 KiB / 256 KiB)

Median of **3** repeats @ `0386b2aa`. Source: Actions [37139047700](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37139047700) (`compare-bodies`). Warmup 2s / measure 8s.


| Body | Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---|---:|---:|---:|---:|---:|
| 64 KiB | HTTP/1 · TLS | HTTP/1 · plain | **11,499**<br><sub>(122 MiB / 40.7% CPU)</sub> | **8,186**<br><sub>(100 MiB / 45.2% CPU)</sub> | 🥇 **12,944**<br><sub>(85 MiB / 36.9% CPU)</sub> | **9,632**<br><sub>(132 MiB / 46.7% CPU)</sub> | **8,511**<br><sub>(166 MiB / 48.1% CPU)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/1 · plain | **8,703**<br><sub>(241 MiB / 39.1% CPU)</sub> | **3,316**<br><sub>(103 MiB / 13.0% CPU)</sub> | 🥇 **9,029**<br><sub>(84 MiB / 23.7% CPU)</sub> | **7,535**<br><sub>(142 MiB / 23.1% CPU)</sub> | **7,121**<br><sub>(165 MiB / 46.5% CPU)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **11,644**<br><sub>(204 MiB / 42.2% CPU)</sub> | **2,882**<br><sub>(104 MiB / 22.4% CPU)</sub> | **9,769**<br><sub>(92 MiB / 32.1% CPU)</sub> | **1**<br><sub>(136 MiB / 0.1% CPU)</sub> | **8,428**<br><sub>(244 MiB / 51.0% CPU)</sub> |
| 256 KiB | HTTP/1 · TLS | HTTP/1 · plain | **3,869**<br><sub>(131 MiB / 31.6% CPU)</sub> | **2,488**<br><sub>(102 MiB / 49.3% CPU)</sub> | 🥇 **4,108**<br><sub>(83 MiB / 28.1% CPU)</sub> | **3,659**<br><sub>(145 MiB / 32.0% CPU)</sub> | **3,083**<br><sub>(171 MiB / 41.9% CPU)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/1 · plain | **3,156**<br><sub>(219 MiB / 31.0% CPU)</sub> | **1,239**<br><sub>(103 MiB / 19.9% CPU)</sub> | 🥇 **3,594**<br><sub>(84 MiB / 19.1% CPU)</sub> | **3,430**<br><sub>(166 MiB / 20.3% CPU)</sub> | **3,030**<br><sub>(162 MiB / 37.9% CPU)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **2,130**<br><sub>(163 MiB / 42.8% CPU)</sub> | **637**<br><sub>(113 MiB / 23.2% CPU)</sub> | **1,893**<br><sub>(88 MiB / 29.3% CPU)</sub> | **1,144**<br><sub>(143 MiB / 19.5% CPU)</sub> | **1,770**<br><sub>(227 MiB / 48.2% CPU)</sub> |
| 64 KiB | HTTP/2 · plain | HTTP/1 · plain | 🥇 **10,441**<br><sub>(231 MiB / 42.2% CPU)</sub> | **2,853**<br><sub>(80 MiB / 18.1% CPU)</sub> | **7,618**<br><sub>(70 MiB / 24.4% CPU)</sub> | **6,838**<br><sub>(133 MiB / 24.1% CPU)</sub> | **8,649**<br><sub>(153 MiB / 45.9% CPU)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/2 · plain | 🥇 **5,820**<br><sub>(157 MiB / 36.5% CPU)</sub> | *Not possible* | **2,095**<br><sub>(87 MiB / 24.6% CPU)</sub> | **4,340**<br><sub>(144 MiB / 22.2% CPU)</sub> | **4,490**<br><sub>(184 MiB / 45.6% CPU)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **6,264**<br><sub>(159 MiB / 36.9% CPU)</sub> | *Not possible* | **2,627**<br><sub>(82 MiB / 24.4% CPU)</sub> | **4,673**<br><sub>(147 MiB / 23.0% CPU)</sub> | **5,327**<br><sub>(180 MiB / 44.0% CPU)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/2 · TLS | 🥇 **3,724**<br><sub>(169 MiB / 44.7% CPU)</sub> | *Not possible* | **2,088**<br><sub>(90 MiB / 35.4% CPU)</sub> | **25**<br><sub>(143 MiB / 0.4% CPU)</sub> | **3,029**<br><sub>(231 MiB / 48.8% CPU)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **4,041**<br><sub>(193 MiB / 44.7% CPU)</sub> | **1,460**<br><sub>(peak 1,481 · 111 MiB / 22.3% CPU)</sub> | **3,083**<br><sub>(96 MiB / 31.6% CPU)</sub> | **6**<br><sub>(138 MiB / 0.2% CPU)</sub> | **3,194**<br><sub>(253 MiB / 49.5% CPU)</sub> |
| 256 KiB | HTTP/2 · plain | HTTP/1 · plain | **2,897**<br><sub>(201 MiB / 34.9% CPU)</sub> | **901**<br><sub>(79 MiB / 18.9% CPU)</sub> | **2,775**<br><sub>(69 MiB / 22.4% CPU)</sub> | **2,922**<br><sub>(158 MiB / 21.8% CPU)</sub> | 🥇 **2,931**<br><sub>(154 MiB / 38.8% CPU)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/2 · plain | 🥇 **1,734**<br><sub>(168 MiB / 32.4% CPU)</sub> | *Not possible* | **609**<br><sub>(95 MiB / 24.6% CPU)</sub> | **1,694**<br><sub>(171 MiB / 22.7% CPU)</sub> | **1,261**<br><sub>(187 MiB / 42.5% CPU)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **2,452**<br><sub>(177 MiB / 33.1% CPU)</sub> | *Not possible* | **1,139**<br><sub>(89 MiB / 23.9% CPU)</sub> | **1,840**<br><sub>(164 MiB / 22.7% CPU)</sub> | **2,181**<br><sub>(194 MiB / 38.9% CPU)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/2 · TLS | **812**<br><sub>(187 MiB / 50.7% CPU)</sub> | *Not possible* | **644**<br><sub>(93 MiB / 37.9% CPU)</sub> | **634**<br><sub>(162 MiB / 22.3% CPU)</sub> | 🥇 **906**<br><sub>(227 MiB / 46.5% CPU)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **2,006**<br><sub>(188 MiB / 41.3% CPU)</sub> | **746**<br><sub>(129 MiB / 18.4% CPU)</sub> | **1,786**<br><sub>(96 MiB / 28.6% CPU)</sub> | **1,312**<br><sub>(142 MiB / 20.4% CPU)</sub> | **1,693**<br><sub>(241 MiB / 46.8% CPU)</sub> |

Prefer the ratio columns; absolute RPS swings by VM.

### macOS — heavier reverse GET (64 KiB / 256 KiB)

Median of **3** repeats on `macos-15` (Apple Silicon M1, 3-core / 7 GB) @ `0386b2aa`. Source: Actions [37139047700](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37139047700) (`compare-bodies`). Warmup 2s / measure 8s.

| Body | Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---|---:|---:|---:|---:|---:|
| 64 KiB | HTTP/1 · TLS | HTTP/1 · plain | **4,260**<br><sub>(191 MiB / 22.4% CPU)</sub> | 🥇 **6,206**<br><sub>(85 MiB / 20.4% CPU)</sub> | **5,074**<br><sub>(102 MiB / 22.4% CPU)</sub> | **5,600**<br><sub>(131 MiB / 32.1% CPU)</sub> | **3,732**<br><sub>(172 MiB / 25.8% CPU)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/1 · plain | **7,770**<br><sub>(429 MiB / 35.6% CPU)</sub> | **7,476**<br><sub>(85 MiB / 24.4% CPU)</sub> | **7,184**<br><sub>(102 MiB / 27.2% CPU)</sub> | 🥇 **8,761**<br><sub>(145 MiB / 27.6% CPU)</sub> | **5,857**<br><sub>(176 MiB / 41.9% CPU)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **2,449**<br><sub>(273 MiB / 32.2% CPU)</sub> | **0**<br><sub>(peak 2,351 · 113 MiB / 27.4% CPU)</sub> | **1,407**<br><sub>(103 MiB / 30.8% CPU)</sub> | **1,359**<br><sub>(140 MiB / 31.3% CPU)</sub> | **1,428**<br><sub>(428 MiB / 32.5% CPU)</sub> |
| 256 KiB | HTTP/1 · TLS | HTTP/1 · plain | **1,699**<br><sub>(222 MiB / 21.9% CPU)</sub> | **1,737**<br><sub>(85 MiB / 19.0% CPU)</sub> | 🥇 **2,400**<br><sub>(101 MiB / 29.3% CPU)</sub> | **1,971**<br><sub>(154 MiB / 25.9% CPU)</sub> | **1,610**<br><sub>(186 MiB / 32.6% CPU)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/1 · plain | **991**<br><sub>(417 MiB / 21.5% CPU)</sub> | 🥇 **1,960**<br><sub>(85 MiB / 21.6% CPU)</sub> | **1,654**<br><sub>(102 MiB / 20.0% CPU)</sub> | **1,784**<br><sub>(156 MiB / 23.3% CPU)</sub> | **1,066**<br><sub>(179 MiB / 27.6% CPU)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/1 · plain | **500**<br><sub>(181 MiB / 32.8% CPU)</sub> | **331**<br><sub>(81 MiB / 14.3% CPU)</sub> | 🥇 **578**<br><sub>(101 MiB / 33.3% CPU)</sub> | **358**<br><sub>(160 MiB / 31.1% CPU)</sub> | **285**<br><sub>(450 MiB / 25.5% CPU)</sub> |
| 64 KiB | HTTP/2 · plain | HTTP/1 · plain | **5,880**<br><sub>(419 MiB / 16.0% CPU)</sub> | 🥇 **9,280**<br><sub>(73 MiB / 22.4% CPU)</sub> | **8,522**<br><sub>(89 MiB / 23.8% CPU)</sub> | **6,415**<br><sub>(135 MiB / 26.8% CPU)</sub> | **8,242**<br><sub>(176 MiB / 38.6% CPU)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/2 · plain | **4,883**<br><sub>(322 MiB / 17.7% CPU)</sub> | *Not possible* | **3,716**<br><sub>(103 MiB / 19.9% CPU)</sub> | **5,955**<br><sub>(130 MiB / 27.4% CPU)</sub> | 🥇 **8,119**<br><sub>(246 MiB / 45.1% CPU)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **3,580**<br><sub>(269 MiB / 20.5% CPU)</sub> | *Not possible* | **2,177**<br><sub>(102 MiB / 19.3% CPU)</sub> | **3,459**<br><sub>(130 MiB / 23.8% CPU)</sub> | **2,472**<br><sub>(234 MiB / 22.0% CPU)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/2 · TLS | **1,701**<br><sub>(203 MiB / 30.6% CPU)</sub> | *Not possible* | **1,615**<br><sub>(103 MiB / 32.6% CPU)</sub> | **1,207**<br><sub>(134 MiB / 41.4% CPU)</sub> | 🥇 **1,785**<br><sub>(467 MiB / 33.9% CPU)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **1,768**<br><sub>(239 MiB / 29.3% CPU)</sub> | **0**<br><sub>(peak 1,942 · 113 MiB / 19.9% CPU)</sub> | **1,702**<br><sub>(108 MiB / 32.3% CPU)</sub> | **996**<br><sub>(140 MiB / 35.1% CPU)</sub> | **987**<br><sub>(389 MiB / 23.9% CPU)</sub> |
| 256 KiB | HTTP/2 · plain | HTTP/1 · plain | **1,156**<br><sub>(385 MiB / 14.6% CPU)</sub> | **2,954**<br><sub>(72 MiB / 17.4% CPU)</sub> | 🥇 **2,996**<br><sub>(88 MiB / 19.3% CPU)</sub> | **2,673**<br><sub>(146 MiB / 19.7% CPU)</sub> | **1,660**<br><sub>(206 MiB / 21.7% CPU)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/2 · plain | **1,553**<br><sub>(625 MiB / 23.6% CPU)</sub> | *Not possible* | **686**<br><sub>(114 MiB / 18.1% CPU)</sub> | 🥇 **1,946**<br><sub>(156 MiB / 22.5% CPU)</sub> | **1,105**<br><sub>(308 MiB / 23.5% CPU)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/2 · TLS | **961**<br><sub>(541 MiB / 23.5% CPU)</sub> | *Not possible* | **619**<br><sub>(101 MiB / 18.1% CPU)</sub> | 🥇 **1,271**<br><sub>(151 MiB / 24.2% CPU)</sub> | **1,030**<br><sub>(314 MiB / 28.6% CPU)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/2 · TLS | **310**<br><sub>(peak 322 · 323 MiB / 26.4% CPU)</sub> | *Not possible* | 🥇 **379**<br><sub>(103 MiB / 29.0% CPU)</sub> | **243**<br><sub>(126 MiB / 35.3% CPU)</sub> | **367**<br><sub>(503 MiB / 27.4% CPU)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **543**<br><sub>(240 MiB / 31.6% CPU)</sub> | **400**<br><sub>(peak 466 · 83 MiB / 22.2% CPU)</sub> | **438**<br><sub>(104 MiB / 31.1% CPU)</sub> | **294**<br><sub>(130 MiB / 37.7% CPU)</sub> | **382**<br><sub>(442 MiB / 32.1% CPU)</sub> |

### Windows — POST 64 KiB request + 64 KiB response

Median of **3** repeats on `windows-latest` @ `0386b2aa`. Source: Actions [37139137891](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37139137891) (`compare-post`).

*Not possible:* **HAProxy** and **Envoy** columns are omitted (no official Windows port).
















| Client | Origin | TWP | nginx | YARP |
|---|---|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | 🥇 **5,871**<br><sub>(101 MiB / 44.4% CPU)</sub> | **362**<br><sub>(142 MiB / 24.6% CPU)</sub> | **4,143**<br><sub>(136 MiB / 57.0% CPU)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **4,111**<br><sub>(180 MiB / 49.4% CPU)</sub> | **361**<br><sub>(144 MiB / 24.8% CPU)</sub> | **3,522**<br><sub>(142 MiB / 51.4% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **2,081**<br><sub>(178 MiB / 40.7% CPU)</sub> | *Not possible (no QUIC)* | **2,081**<br><sub>(210 MiB / 50.8% CPU)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | 🥇 **6,696**<br><sub>(193 MiB / 45.7% CPU)</sub> | **2,165**<br><sub>(131 MiB / 24.8% CPU)</sub> | **6,333**<br><sub>(125 MiB / 51.9% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | 🥇 **5,203**<br><sub>(118 MiB / 36.4% CPU)</sub> | *Not possible* | **3,438**<br><sub>(146 MiB / 43.9% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **5,556**<br><sub>(127 MiB / 37.9% CPU)</sub> | *Not possible* | **4,138**<br><sub>(145 MiB / 48.2% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | 🥇 **2,162**<br><sub>(176 MiB / 42.6% CPU)</sub> | *Not possible* | **1,890**<br><sub>(205 MiB / 48.2% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **1,817**<br><sub>(186 MiB / 41.2% CPU)</sub> | *Not possible (no QUIC)* | **1,812**<br><sub>(218 MiB / 50.6% CPU)</sub> |

TWP leads H2→H1 POST (about **1.1–1.3×** YARP) and H3 POST (about **1.0–1.1×** YARP). H2 TLS→H2 TLS POST sustain is about **1.2–1.4×** YARP.

### Linux — POST 64 KiB request + 64 KiB response

Median of **3** repeats @ `0386b2aa`. Source: Actions [37139137891](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37139137891) (`compare-post`).


| Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---:|---:|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | **4,911**<br><sub>(131 MiB / 43.0% CPU)</sub> | **3,919**<br><sub>(100 MiB / 47.8% CPU)</sub> | 🥇 **5,183**<br><sub>(83 MiB / 39.9% CPU)</sub> | **5,162**<br><sub>(132 MiB / 40.4% CPU)</sub> | **3,337**<br><sub>(172 MiB / 54.4% CPU)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **4,484**<br><sub>(232 MiB / 41.6% CPU)</sub> | **2,130**<br><sub>(118 MiB / 20.8% CPU)</sub> | **2,955**<br><sub>(84 MiB / 24.3% CPU)</sub> | **3,856**<br><sub>(peak 4,235 · 130 MiB / 20.9% CPU)</sub> | **3,975**<br><sub>(166 MiB / 46.9% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **6,094**<br><sub>(247 MiB / 41.2% CPU)</sub> | **975**<br><sub>(116 MiB / 24.8% CPU)</sub> | **3,555**<br><sub>(90 MiB / 28.5% CPU)</sub> | **0**<br><sub>(126 MiB)</sub> | **5,337**<br><sub>(269 MiB / 47.0% CPU)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | 🥇 **7,209**<br><sub>(229 MiB / 42.9% CPU)</sub> | **3,580**<br><sub>(97 MiB / 24.9% CPU)</sub> | **3,908**<br><sub>(68 MiB / 22.9% CPU)</sub> | **0**<br><sub>(peak 6,582 · 133 MiB / 22.4% CPU)</sub> | **5,528**<br><sub>(159 MiB / 42.7% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | 🥇 **6,411**<br><sub>(180 MiB / 34.6% CPU)</sub> | *Not possible* | **3,493**<br><sub>(88 MiB / 22.9% CPU)</sub> | **5,385**<br><sub>(143 MiB / 21.2% CPU)</sub> | **4,746**<br><sub>(178 MiB / 48.1% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **5,677**<br><sub>(180 MiB / 37.6% CPU)</sub> | *Not possible* | **2,530**<br><sub>(88 MiB / 23.4% CPU)</sub> | **4,285**<br><sub>(147 MiB / 22.2% CPU)</sub> | **4,822**<br><sub>(175 MiB / 45.9% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | 🥇 **2,134**<br><sub>(226 MiB / 42.5% CPU)</sub> | *Not possible* | **1,190**<br><sub>(96 MiB / 32.2% CPU)</sub> | **0**<br><sub>(127 MiB / 0.1% CPU)</sub> | **1,812**<br><sub>(252 MiB / 47.3% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **3,226**<br><sub>(249 MiB / 43.3% CPU)</sub> | **612**<br><sub>(125 MiB / 24.9% CPU)</sub> | **1,969**<br><sub>(97 MiB / 25.0% CPU)</sub> | **0**<br><sub>(127 MiB / 0.1% CPU)</sub> | **2,905**<br><sub>(266 MiB / 46.7% CPU)</sub> |

Linux nginx H1/H2/H3 POST completed (nginx.org mainline). TWP÷YARP H1 ≈ **1.5×**; H2→H1 ≈ **1.2×**; H3 ≈ **1.1×**. TWP÷nginx H3 ≈ **6×**.

### macOS — POST 64 KiB request + 64 KiB response

Median of **3** repeats on `macos-15` (Apple Silicon M1, 3-core / 7 GB) @ `0386b2aa`. Source: Actions [37139137891](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37139137891) (`compare-post`).

| Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---:|---:|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | **3,019**<br><sub>(239 MiB / 19.0% CPU)</sub> | 🥇 **3,977**<br><sub>(89 MiB / 21.4% CPU)</sub> | **3,964**<br><sub>(103 MiB / 26.8% CPU)</sub> | **3,656**<br><sub>(131 MiB / 24.2% CPU)</sub> | **2,272**<br><sub>(203 MiB / 33.7% CPU)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | **1,884**<br><sub>(429 MiB / 21.9% CPU)</sub> | 🥇 **3,252**<br><sub>(98 MiB / 20.7% CPU)</sub> | **2,375**<br><sub>(101 MiB / 19.8% CPU)</sub> | **0**<br><sub>(peak 3,445 · 146 MiB / 20.6% CPU)</sub> | **1,606**<br><sub>(185 MiB / 22.6% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | **1,021**<br><sub>(417 MiB / 28.5% CPU)</sub> | **0**<br><sub>(peak 1,326 · 106 MiB / 23.7% CPU)</sub> | **853**<br><sub>(101 MiB / 30.0% CPU)</sub> | **472**<br><sub>(135 MiB / 36.0% CPU)</sub> | 🥇 **1,118**<br><sub>(501 MiB / 29.0% CPU)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | **2,726**<br><sub>(378 MiB / 17.8% CPU)</sub> | 🥇 **4,777**<br><sub>(85 MiB / 17.5% CPU)</sub> | **2,289**<br><sub>(87 MiB / 19.6% CPU)</sub> | **0**<br><sub>(peak 2,511 · 114 MiB / 17.9% CPU)</sub> | **2,642**<br><sub>(199 MiB / 24.0% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | **1,604**<br><sub>(287 MiB / 14.6% CPU)</sub> | *Not possible* | **1,321**<br><sub>(103 MiB / 22.1% CPU)</sub> | 🥇 **2,760**<br><sub>(132 MiB / 21.9% CPU)</sub> | **856**<br><sub>(222 MiB / 16.4% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **3,102**<br><sub>(277 MiB / 34.3% CPU)</sub> | *Not possible* | **1,872**<br><sub>(102 MiB / 24.8% CPU)</sub> | **2,205**<br><sub>(133 MiB / 25.9% CPU)</sub> | **2,831**<br><sub>(204 MiB / 44.4% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | 🥇 **1,342**<br><sub>(283 MiB / 39.3% CPU)</sub> | *Not possible* | **858**<br><sub>(105 MiB / 32.9% CPU)</sub> | **514**<br><sub>(peak 648 · 128 MiB / 40.4% CPU)</sub> | **1,031**<br><sub>(450 MiB / 40.8% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **828**<br><sub>(284 MiB / 27.5% CPU)</sub> | **327**<br><sub>(83 MiB / 10.3% CPU)</sub> | **715**<br><sub>(peak 752 · 107 MiB / 28.8% CPU)</sub> | **334**<br><sub>(peak 450 · 130 MiB / 26.3% CPU)</sub> | **736**<br><sub>(454 MiB / 25.5% CPU)</sub> |

### Windows — lossy / high-RTT (H2 HOL / H3 loss)

Userspace **5 ms** one-way delay + **1%** TCP connection stall (H1/H2); UDP is **loss% only** (no per-datagram delay; MsQuic-safe) + **1%** datagram drop (H3); **64 KiB** GET. Median of **3** repeats on `windows-latest` @ `0386b2aa` — [37139186736](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37139186736) (`compare-lossy`).

*Not possible:* **HAProxy** and **Envoy** columns are omitted (no official Windows port).


| Client | Origin | TWP | nginx | YARP |
|---|---|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | 🥇 **675**<br><sub>(84 MiB / 2.1% CPU)</sub> | **663**<br><sub>(142 MiB / 11.3% CPU)</sub> | **671**<br><sub>(122 MiB / 2.1% CPU)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **62**<br><sub>(peak 91 · 113 MiB / 1.4% CPU)</sub> | **18**<br><sub>(142 MiB / 0.4% CPU)</sub> | **18**<br><sub>(86 MiB / 0.8% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | **1,060**<br><sub>(126 MiB / 14.2% CPU)</sub> | *Not possible (no QUIC)* | 🥇 **1,333**<br><sub>(180 MiB / 28.5% CPU)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | 🥇 **60**<br><sub>(peak 85 · 111 MiB / 1.4% CPU)</sub> | **18**<br><sub>(128 MiB / 0.1% CPU)</sub> | **17**<br><sub>(78 MiB / 0.8% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | **16**<br><sub>(77 MiB / 0.6% CPU)</sub> | *Not possible* | 🥇 **18**<br><sub>(85 MiB / 0.8% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | **16**<br><sub>(77 MiB / 0.7% CPU)</sub> | *Not possible* | 🥇 **18**<br><sub>(91 MiB / 1.3% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | **1,065**<br><sub>(118 MiB / 9.2% CPU)</sub> | *Not possible* | 🥇 **1,610**<br><sub>(180 MiB / 28.2% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | **799**<br><sub>(133 MiB / 16.7% CPU)</sub> | *Not possible (no QUIC)* | 🥇 **828**<br><sub>(177 MiB / 29.6% CPU)</sub> |

H1, H2, and H3 loss sustain are in the table. Prefer the ratio columns.

### Linux — lossy / high-RTT (H2 HOL / H3 loss)

Median of **3** repeats @ `0386b2aa`. Source: [37139186736](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37139186736) (`compare-lossy`; lossy H3 uses `quic-http3`, UDP drop-only).


| Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---:|---:|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | **1,214**<br><sub>(121 MiB / 9.6% CPU)</sub> | 🥇 **1,220**<br><sub>(102 MiB / 8.1% CPU)</sub> | **1,220**<br><sub>(83 MiB / 4.8% CPU)</sub> | **1,211**<br><sub>(128 MiB / 6.5% CPU)</sub> | **1,213**<br><sub>(151 MiB / 13.0% CPU)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **312**<br><sub>(184 MiB / 6.6% CPU)</sub> | **40**<br><sub>(98 MiB / 0.3% CPU)</sub> | **41**<br><sub>(85 MiB / 0.2% CPU)</sub> | **40**<br><sub>(137 MiB / 0.3% CPU)</sub> | **40**<br><sub>(127 MiB / 1.5% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **1,152**<br><sub>(166 MiB / 24.3% CPU)</sub> | **1,094**<br><sub>(114 MiB / 23.8% CPU)</sub> | **946**<br><sub>(87 MiB / 19.4% CPU)</sub> | **1,043**<br><sub>(138 MiB / 24.5% CPU)</sub> | **1,132**<br><sub>(240 MiB / 31.4% CPU)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | 🥇 **323**<br><sub>(174 MiB / 6.0% CPU)</sub> | **40**<br><sub>(78 MiB / 0.2% CPU)</sub> | **41**<br><sub>(69 MiB / 0.2% CPU)</sub> | **40**<br><sub>(126 MiB / 0.3% CPU)</sub> | **42**<br><sub>(121 MiB / 1.2% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | **40**<br><sub>(102 MiB / 0.8% CPU)</sub> | *Not possible* | 🥇 **41**<br><sub>(85 MiB / 0.3% CPU)</sub> | **40**<br><sub>(133 MiB / 0.3% CPU)</sub> | **40**<br><sub>(130 MiB / 1.3% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | **40**<br><sub>(101 MiB / 1.0% CPU)</sub> | *Not possible* | **40**<br><sub>(86 MiB / 0.5% CPU)</sub> | **40**<br><sub>(137 MiB / 0.4% CPU)</sub> | 🥇 **40**<br><sub>(127 MiB / 1.4% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | **1,385**<br><sub>(162 MiB / 22.1% CPU)</sub> | *Not possible* | **1,177**<br><sub>(93 MiB / 21.4% CPU)</sub> | **1,376**<br><sub>(141 MiB / 25.1% CPU)</sub> | 🥇 **1,408**<br><sub>(229 MiB / 30.9% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **1,194**<br><sub>(177 MiB / 25.6% CPU)</sub> | **1,116**<br><sub>(117 MiB / 24.0% CPU)</sub> | **970**<br><sub>(91 MiB / 21.3% CPU)</sub> | **1,055**<br><sub>(139 MiB / 25.8% CPU)</sub> | **1,112**<br><sub>(221 MiB / 33.0% CPU)</sub> |

TWP H2 HOL ≫ YARP (~**7.8×**). H3 TWP÷YARP ≈ **1.07×**.

### macOS — lossy / high-RTT (H2 HOL / H3 loss)

Median of **3** repeats on `macos-15` (Apple Silicon M1, 3-core / 7 GB) @ `0386b2aa`. Source: [37139186736](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37139186736) (`compare-lossy`; lossy H3 uses `quic-http3`, UDP drop-only).

| Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---:|---:|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | **391**<br><sub>(248 MiB / 4.0% CPU)</sub> | **371**<br><sub>(83 MiB / 1.3% CPU)</sub> | **421**<br><sub>(100 MiB / 1.9% CPU)</sub> | **402**<br><sub>(134 MiB / 2.6% CPU)</sub> | 🥇 **463**<br><sub>(215 MiB / 4.9% CPU)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **19**<br><sub>(peak 29 · 132 MiB / 3.6% CPU)</sub> | **8**<br><sub>(peak 9 · 81 MiB / 0.1% CPU)</sub> | **8**<br><sub>(96 MiB / 0.2% CPU)</sub> | **8**<br><sub>(118 MiB / 0.2% CPU)</sub> | **8**<br><sub>(128 MiB / 1.0% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | **1,178**<br><sub>(252 MiB / 27.1% CPU)</sub> | 🥇 **1,305**<br><sub>(98 MiB / 17.9% CPU)</sub> | **843**<br><sub>(101 MiB / 22.4% CPU)</sub> | **1,061**<br><sub>(132 MiB / 33.8% CPU)</sub> | **1,023**<br><sub>(427 MiB / 31.0% CPU)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | 🥇 **18**<br><sub>(peak 30 · 123 MiB / 2.8% CPU)</sub> | **8**<br><sub>(peak 9 · 69 MiB / 0.1% CPU)</sub> | **8**<br><sub>(83 MiB / 0.1% CPU)</sub> | **8**<br><sub>(109 MiB / 0.2% CPU)</sub> | **9**<br><sub>(117 MiB / 7.3% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | **8**<br><sub>(128 MiB / 1.2% CPU)</sub> | *Not possible* | 🥇 **8**<br><sub>(97 MiB / 0.2% CPU)</sub> | **8**<br><sub>(118 MiB / 0.2% CPU)</sub> | **8**<br><sub>(129 MiB / 1.3% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | **9**<br><sub>(118 MiB / 1.4% CPU)</sub> | *Not possible* | **10**<br><sub>(98 MiB / 0.5% CPU)</sub> | **10**<br><sub>(122 MiB / 0.4% CPU)</sub> | 🥇 **12**<br><sub>(144 MiB / 3.0% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | **712**<br><sub>(185 MiB / 19.8% CPU)</sub> | *Not possible* | **419**<br><sub>(105 MiB / 16.8% CPU)</sub> | 🥇 **800**<br><sub>(131 MiB / 28.6% CPU)</sub> | **532**<br><sub>(439 MiB / 21.0% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | **668**<br><sub>(215 MiB / 18.6% CPU)</sub> | **916**<br><sub>(100 MiB / 14.1% CPU)</sub> | **588**<br><sub>(106 MiB / 16.1% CPU)</sub> | 🥇 **1,041**<br><sub>(132 MiB / 27.8% CPU)</sub> | **534**<br><sub>(383 MiB / 19.1% CPU)</sub> |

### Architecture-sensitive

These runs isolate slow app readers, origin-early response, HTTP/2 duplex, and WebSocket echo. See [TWP vs YARP IO model](Performance-Profiling#twp-vs-yarp-io-model). Laptop 1-rep numbers are on [Performance Local Lab](Performance-Local-Lab#architecture-sensitive).

Median of **3** repeats on matched 4 vCPU / 16 GiB runners @ `0386b2aa` ([37138997054](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37138997054)). Slow consumer = 256 KiB GET, 16 KiB read + 8 ms sleep. Early response = 64 KiB POST, origin writes after 8 KiB. Duplex HTTP/2 = overlapping 64 KiB POST on H2 TLS↔H2 TLS. WebSocket row = H1 Upgrade echo round-trips/sec (not RFC 8441; see WebSocket RFC 8441 table below).

Lossy-link runs (slow **network**) are already published above; they are not a slow **app** reader.

#### Windows


*Not possible:* **HAProxy** and **Envoy** columns are omitted (no official Windows port).

| Scenario | Client | Origin | TWP | nginx | YARP |
|---|---|---|---:|---:|---:|
| Slow consumer (256 KiB GET, throttled client read) | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **256**<br><sub>(94 MiB / 2.7% CPU)</sub> | **243**<br><sub>(144 MiB / 15.4% CPU)</sub> | **255**<br><sub>(115 MiB / 3.8% CPU)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · TLS | HTTP/1 · plain | **256**<br><sub>(138 MiB / 5.8% CPU)</sub> | **164**<br><sub>(142 MiB / 24.9% CPU)</sub> | 🥇 **256**<br><sub>(110 MiB / 6.9% CPU)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **272**<br><sub>(101 MiB / 11.5% CPU)</sub> | *Not possible (no QUIC)* | **267**<br><sub>(171 MiB / 13.7% CPU)</sub> |
| Early response (origin writes after first request chunk) | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **8,941**<br><sub>(91 MiB / 43.9% CPU)</sub> | **516**<br><sub>(143 MiB / 25.0% CPU)</sub> | **6,156**<br><sub>(136 MiB / 55.7% CPU)</sub> |
| Early response (origin writes after first request chunk) | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **3,500**<br><sub>(193 MiB / 55.5% CPU)</sub> | **370**<br><sub>(144 MiB / 24.9% CPU)</sub> | **3,260**<br><sub>(137 MiB / 54.2% CPU)</sub> |
| Early response (origin writes after first request chunk) | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **2,664**<br><sub>(150 MiB / 42.9% CPU)</sub> | *Not possible (no QUIC)* | **2,257**<br><sub>(218 MiB / 51.9% CPU)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · plain | HTTP/1 · plain | **256**<br><sub>(114 MiB / 2.6% CPU)</sub> | **256**<br><sub>(127 MiB / 4.0% CPU)</sub> | 🥇 **256**<br><sub>(106 MiB / 1.9% CPU)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · TLS | HTTP/2 · TLS | **250**<br><sub>(120 MiB / 5.4% CPU)</sub> | *Not possible* | 🥇 **256**<br><sub>(141 MiB / 7.5% CPU)</sub> |
| Early response (origin writes after first request chunk) | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **2,500**<br><sub>(120 MiB / 29.6% CPU)</sub> | *Not possible* | **4**<br><sub>(105 MiB / 0.2% CPU)</sub> |
| Duplex (both directions live) | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **2,028**<br><sub>(peak 2,640 · 114 MiB / 23.8% CPU)</sub> | *Not possible* | **17**<br><sub>(105 MiB / 0.4% CPU)</sub> |
| Duplex (WebSocket / H1 Upgrade) | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **25,684**<br><sub>(96 MiB / 43.2% CPU)</sub> | **12,522**<br><sub>(143 MiB / 24.6% CPU)</sub> | **23,768**<br><sub>(90 MiB / 43.6% CPU)</sub> |

#### Linux


| Scenario | Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---|---:|---:|---:|---:|---:|
| Slow consumer (256 KiB GET, throttled client read) | HTTP/1 · TLS | HTTP/1 · plain | **469**<br><sub>(114 MiB / 9.4% CPU)</sub> | **416**<br><sub>(101 MiB / 9.8% CPU)</sub> | 🥇 **473**<br><sub>(84 MiB / 5.9% CPU)</sub> | **472**<br><sub>(138 MiB / 6.1% CPU)</sub> | **422**<br><sub>(148 MiB / 15.2% CPU)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · TLS | HTTP/1 · plain | **469**<br><sub>(144 MiB / 18.3% CPU)</sub> | **429**<br><sub>(100 MiB / 17.4% CPU)</sub> | 🥇 **474**<br><sub>(82 MiB / 9.2% CPU)</sub> | **471**<br><sub>(157 MiB / 7.3% CPU)</sub> | **470**<br><sub>(146 MiB / 24.3% CPU)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/3 · QUIC | HTTP/1 · plain | **480**<br><sub>(134 MiB / 24.4% CPU)</sub> | **120**<br><sub>(97 MiB / 3.9% CPU)</sub> | **478**<br><sub>(88 MiB / 5.6% CPU)</sub> | **2**<br><sub>(151 MiB / 0.1% CPU)</sub> | 🥇 **480**<br><sub>(197 MiB / 28.6% CPU)</sub> |
| Early response (origin writes after first request chunk) | HTTP/1 · TLS | HTTP/1 · plain | **9,402**<br><sub>(134 MiB / 42.2% CPU)</sub> | **0**<br><sub>(peak 6,896 · 102 MiB / 49.1% CPU)</sub> | 🥇 **10,322**<br><sub>(84 MiB / 40.7% CPU)</sub> | **0**<br><sub>(peak 64 · 129 MiB / 1.9% CPU)</sub> | **5,960**<br><sub>(171 MiB / 54.1% CPU)</sub> |
| Early response (origin writes after first request chunk) | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **4,856**<br><sub>(232 MiB / 44.5% CPU)</sub> | **0**<br><sub>(peak 2,632 · 120 MiB / 18.9% CPU)</sub> | **4,798**<br><sub>(86 MiB / 20.8% CPU)</sub> | **0**<br><sub>(peak 5,095 · 148 MiB / 23.5% CPU)</sub> | **4,360**<br><sub>(166 MiB / 49.9% CPU)</sub> |
| Early response (origin writes after first request chunk) | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **2,861**<br><sub>(202 MiB / 45.0% CPU)</sub> | **0**<br><sub>(peak 446 · 117 MiB / 24.6% CPU)</sub> | **1,950**<br><sub>(90 MiB / 34.4% CPU)</sub> | **0**<br><sub>(127 MiB / 0.1% CPU)</sub> | **2,126**<br><sub>(258 MiB / 47.5% CPU)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · plain | HTTP/1 · plain | 🥇 **481**<br><sub>(146 MiB / 14.2% CPU)</sub> | **457**<br><sub>(79 MiB / 9.2% CPU)</sub> | **472**<br><sub>(70 MiB / 4.8% CPU)</sub> | **473**<br><sub>(147 MiB / 3.4% CPU)</sub> | **480**<br><sub>(147 MiB / 12.5% CPU)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **471**<br><sub>(155 MiB / 12.5% CPU)</sub> | *Not possible* | **458**<br><sub>(90 MiB / 8.8% CPU)</sub> | **470**<br><sub>(153 MiB / 6.6% CPU)</sub> | **470**<br><sub>(167 MiB / 17.9% CPU)</sub> |
| Early response (origin writes after first request chunk) | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **2,725**<br><sub>(peak 3,366 · 150 MiB / 23.9% CPU)</sub> | *Not possible* | **72**<br><sub>(peak 2,790 · 81 MiB / 0.5% CPU)</sub> | **0**<br><sub>(peak 3,878 · 159 MiB / 20.1% CPU)</sub> | **167**<br><sub>(164 MiB / 2.4% CPU)</sub> |
| Duplex (both directions live) | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **2,974**<br><sub>(145 MiB / 31.4% CPU)</sub> | *Not possible* | **1,998**<br><sub>(83 MiB / 24.0% CPU)</sub> | **0**<br><sub>(peak 2,896 · 158 MiB / 19.6% CPU)</sub> | **21**<br><sub>(145 MiB / 0.5% CPU)</sub> |
| Duplex (WebSocket / H1 Upgrade) | HTTP/1 · TLS | HTTP/1 · plain | **47,808**<br><sub>(126 MiB / 44.9% CPU)</sub> | 🥇 **50,900**<br><sub>(101 MiB / 36.8% CPU)</sub> | **48,487**<br><sub>(84 MiB / 39.9% CPU)</sub> | **46,374**<br><sub>(127 MiB / 41.9% CPU)</sub> | **41,253**<br><sub>(125 MiB / 45.3% CPU)</sub> |

#### macOS

Median of **3** repeats on `macos-15` (Apple Silicon M1, 3-core / 7 GB) @ `0386b2aa`. Source: Actions [37138997054](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37138997054) (`compare-arch`).

| Scenario | Client | Origin | TWP | nginx | HAProxy | Envoy | YARP |
|---|---|---|---:|---:|---:|---:|---:|
| Slow consumer (256 KiB GET, throttled client read) | HTTP/1 · TLS | HTTP/1 · plain | **97**<br><sub>(260 MiB / 4.1% CPU)</sub> | **104**<br><sub>(84 MiB / 0.9% CPU)</sub> | 🥇 **112**<br><sub>(101 MiB / 1.2% CPU)</sub> | **88**<br><sub>(146 MiB / 1.3% CPU)</sub> | **96**<br><sub>(222 MiB / 2.8% CPU)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · TLS | HTTP/1 · plain | **87**<br><sub>(326 MiB / 3.8% CPU)</sub> | 🥇 **102**<br><sub>(85 MiB / 1.9% CPU)</sub> | **102**<br><sub>(100 MiB / 1.8% CPU)</sub> | **80**<br><sub>(154 MiB / 1.8% CPU)</sub> | **102**<br><sub>(210 MiB / 4.6% CPU)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **112**<br><sub>(132 MiB / 21.5% CPU)</sub> | **104**<br><sub>(93 MiB / 4.9% CPU)</sub> | **96**<br><sub>(100 MiB / 8.1% CPU)</sub> | **87**<br><sub>(158 MiB / 18.2% CPU)</sub> | **107**<br><sub>(374 MiB / 20.1% CPU)</sub> |
| Early response (origin writes after first request chunk) | HTTP/1 · TLS | HTTP/1 · plain | **2,971**<br><sub>(237 MiB / 27.0% CPU)</sub> | 🥇 **4,852**<br><sub>(89 MiB / 21.4% CPU)</sub> | **3,724**<br><sub>(103 MiB / 24.9% CPU)</sub> | **0**<br><sub>(peak 2,768 · 132 MiB / 25.0% CPU)</sub> | **2,034**<br><sub>(219 MiB / 27.2% CPU)</sub> |
| Early response (origin writes after first request chunk) | HTTP/2 · TLS | HTTP/1 · plain | **1,284**<br><sub>(440 MiB / 23.5% CPU)</sub> | **0**<br><sub>(peak 3,030 · 98 MiB / 18.0% CPU)</sub> | 🥇 **3,848**<br><sub>(104 MiB / 22.4% CPU)</sub> | **0**<br><sub>(peak 2,789 · 145 MiB / 21.4% CPU)</sub> | **1,104**<br><sub>(180 MiB / 16.6% CPU)</sub> |
| Early response (origin writes after first request chunk) | HTTP/3 · QUIC | HTTP/1 · plain | **976**<br><sub>(310 MiB / 20.9% CPU)</sub> | **0**<br><sub>(peak 1,350 · 112 MiB / 23.1% CPU)</sub> | 🥇 **1,015**<br><sub>(104 MiB / 29.1% CPU)</sub> | **560**<br><sub>(131 MiB / 33.3% CPU)</sub> | **607**<br><sub>(458 MiB / 29.6% CPU)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · plain | HTTP/1 · plain | **104**<br><sub>(220 MiB / 2.4% CPU)</sub> | **104**<br><sub>(72 MiB / 1.2% CPU)</sub> | **104**<br><sub>(87 MiB / 1.1% CPU)</sub> | **80**<br><sub>(145 MiB / 1.0% CPU)</sub> | 🥇 **111**<br><sub>(145 MiB / 2.1% CPU)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **112**<br><sub>(379 MiB / 5.2% CPU)</sub> | *Not possible* | **112**<br><sub>(102 MiB / 4.2% CPU)</sub> | **103**<br><sub>(144 MiB / 3.6% CPU)</sub> | **110**<br><sub>(308 MiB / 7.8% CPU)</sub> |
| Early response (origin writes after first request chunk) | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **913**<br><sub>(peak 1,578 · 174 MiB / 11.3% CPU)</sub> | *Not possible* | **604**<br><sub>(98 MiB / 10.6% CPU)</sub> | **0**<br><sub>(peak 2,019 · 145 MiB / 20.6% CPU)</sub> | **0**<br><sub>(177 MiB / 1.0% CPU)</sub> |
| Duplex (both directions live) | HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **778**<br><sub>(peak 1,867 · 172 MiB / 9.4% CPU)</sub> | *Not possible* | **730**<br><sub>(98 MiB / 7.8% CPU)</sub> | **0**<br><sub>(peak 2,352 · 147 MiB / 21.2% CPU)</sub> | **8**<br><sub>(179 MiB / 1.3% CPU)</sub> |
| Duplex (WebSocket / H1 Upgrade) | HTTP/1 · TLS | HTTP/1 · plain | **18,181**<br><sub>(162 MiB / 18.4% CPU)</sub> | 🥇 **29,332**<br><sub>(86 MiB / 20.3% CPU)</sub> | **16,778**<br><sub>(101 MiB / 20.1% CPU)</sub> | **21,739**<br><sub>(122 MiB / 26.7% CPU)</sub> | **17,478**<br><sub>(162 MiB / 25.8% CPU)</sub> |

Slow consumer is sleep-bound; H1/H2/H3 sit in the same band. Prefer the ratio columns for early-response, duplex, and WebSocket rows.

### TLS termination cost (H1 TLS → cleartext origin)

Isolates keep-alive tiny GET vs **new connection per request** (handshake-dominated) vs keep-alive **256 KiB**. Product comparison uses RPS and end-to-end latency; TWP can also capture `ClientTlsTiming` when `TWP_RPS_CAPTURE_TLS=1` (child process) — nginx/YARP have no equivalent hook.

#### Windows

Median of **3** repeats on `windows-latest` @ `0386b2aa`. Source: Actions [37139092496](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37139092496). Absolute RPS on GHA swings hard; prefer **TWP÷YARP**.


*Not possible:* **HAProxy** and **Envoy** columns are omitted (no official Windows port).

| Workload | TWP | nginx | YARP |
|---|---:|---:|---:|
| Keep-alive · tiny GET | 🥇 **20,474**<br><sub>(84 MiB / 47.4% CPU)</sub> | **8,709**<br><sub>(142 MiB / 24.6% CPU)</sub> | **18,264**<br><sub>(102 MiB / 48.3% CPU)</sub> |
| New-connection · tiny GET | 🥇 **725**<br><sub>(84 MiB / 0.8% CPU)</sub> | **248**<br><sub>(141 MiB / 2.2% CPU)</sub> | **712**<br><sub>(113 MiB / 0.5% CPU)</sub> |
| Keep-alive · 256 KiB GET | 🥇 **2,948**<br><sub>(119 MiB / 47.1% CPU)</sub> | **170**<br><sub>(142 MiB / 24.8% CPU)</sub> | **2,709**<br><sub>(139 MiB / 49.1% CPU)</sub> |

#### Linux

Median of **3** repeats @ `0386b2aa`. Source: Actions [37139092496](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37139092496).


| Workload | TWP | nginx | HAProxy | Envoy | YARP |
|---|---:|---:|---:|---:|---:|
| Keep-alive · tiny GET | **24,036**<br><sub>(106 MiB / 49.4% CPU)</sub> | 🥇 **27,574**<br><sub>(99 MiB / 42.0% CPU)</sub> | **26,687**<br><sub>(82 MiB / 43.7% CPU)</sub> | **16,103**<br><sub>(128 MiB / 57.4% CPU)</sub> | **19,910**<br><sub>(134 MiB / 50.3% CPU)</sub> |
| New-connection · tiny GET | **1,143**<br><sub>(120 MiB / 0.5% CPU)</sub> | 🥇 **1,204**<br><sub>(103 MiB / 0.2% CPU)</sub> | **1,141**<br><sub>(82 MiB / 0.5% CPU)</sub> | **1,082**<br><sub>(129 MiB / 0.2% CPU)</sub> | **1,141**<br><sub>(146 MiB)</sub> |
| Keep-alive · 256 KiB GET | **2,814**<br><sub>(129 MiB / 35.2% CPU)</sub> | **1,780**<br><sub>(100 MiB / 51.1% CPU)</sub> | 🥇 **3,012**<br><sub>(83 MiB / 32.6% CPU)</sub> | **2,766**<br><sub>(145 MiB / 31.9% CPU)</sub> | **2,247**<br><sub>(162 MiB / 43.9% CPU)</sub> |

#### macOS

Median of **3** repeats on `macos-15` @ `0386b2aa`. Source: Actions [37139092496](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37139092496).

| Workload | TWP | nginx | HAProxy | Envoy | YARP |
|---|---:|---:|---:|---:|---:|
| Keep-alive · tiny GET | **8,658**<br><sub>(149 MiB / 20.7% CPU)</sub> | **10,695**<br><sub>(85 MiB / 16.6% CPU)</sub> | **11,290**<br><sub>(101 MiB / 12.2% CPU)</sub> | **8,861**<br><sub>(122 MiB / 22.9% CPU)</sub> | 🥇 **12,042**<br><sub>(162 MiB / 22.4% CPU)</sub> |
| New-connection · tiny GET | **110**<br><sub>(peak 118 · 107 MiB / 0.2% CPU)</sub> | **478**<br><sub>(85 MiB)</sub> | 🥇 **651**<br><sub>(101 MiB)</sub> | **627**<br><sub>(131 MiB / 0.1% CPU)</sub> | **106**<br><sub>(peak 119 · 152 MiB / 0.2% CPU)</sub> |
| Keep-alive · 256 KiB GET | **2,880**<br><sub>(214 MiB / 24.6% CPU)</sub> | **2,330**<br><sub>(85 MiB / 16.6% CPU)</sub> | 🥇 **2,998**<br><sub>(102 MiB / 25.7% CPU)</sub> | **2,874**<br><sub>(154 MiB / 24.8% CPU)</sub> | **2,034**<br><sub>(182 MiB / 29.2% CPU)</sub> |

Prefer **TWP÷YARP** in the table. Absolute RPS on GHA swings hard. New-connection is Darwin SslStream-bound for TWP and YARP (handshake p99 SLO **500 ms** on macOS only — Win/Linux stay at **200 ms**).

## Unary gRPC (H2 TLS)

Unary Echo **RPC/s** @ c=64 over H2 TLS→H2 TLS for Titanium, YARP, nginx (`grpc_pass`), HAProxy, and Envoy (OS-possible peers only). Not folded into the architecture-sensitive tables. Median of **3** repeats @ `0386b2aa` — [37139283273](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37139283273).

| OS | Titanium | YARP | nginx | HAProxy | Envoy |
|---|---:|---:|---:|---:|---:|
| Windows | 🥇 **52,937**<br><sub>(97 MiB / 23.1% CPU)</sub> | **31,254**<br><sub>(120 MiB / 44.9% CPU)</sub> | **0**<br><sub>(143 MiB / 24.8% CPU)</sub> | *Not possible* | *Not possible* |
| Linux | 🥇 **36,435**<br><sub>(116 MiB / 29.4% CPU)</sub> | **21,650**<br><sub>(162 MiB / 42.4% CPU)</sub> | **0**<br><sub>(114 MiB / 24.8% CPU)</sub> | **6,780**<br><sub>(84 MiB / 24.4% CPU)</sub> | **11,826**<br><sub>(128 MiB / 21.5% CPU)</sub> |
| macOS | 🥇 **23,481**<br><sub>(147 MiB / 9.2% CPU)</sub> | **13,123**<br><sub>(198 MiB / 13.4% CPU)</sub> | **0**<br><sub>(85 MiB / 2.8% CPU)</sub> | **0**<br><sub>(104 MiB / 13.3% CPU)</sub> | **8,939**<br><sub>(120 MiB / 13.4% CPU)</sub> |

## Unary gRPC (H2 TLS → h2c)

Unary Echo **RPC/s** @ c=64 over **H2 TLS → h2c** (edge TLS, cleartext H2 origin). Peers: Titanium, YARP, HAProxy, Envoy. Mode: `compare-grpc` (`*-grpc-h2c`). Median of **3** repeats @ `0386b2aa` — [37139283273](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37139283273).

*Not possible:* **nginx** column omitted (no H2 upstream).

| OS | Titanium | YARP | HAProxy | Envoy |
|---|---:|---:|---:|---:|
| Windows | 🥇 **55,113**<br><sub>(88 MiB / 23.0% CPU)</sub> | **33,033**<br><sub>(115 MiB / 44.6% CPU)</sub> | *Not possible* | *Not possible* |
| Linux | 🥇 **43,057**<br><sub>(123 MiB / 29.8% CPU)</sub> | **24,248**<br><sub>(164 MiB / 43.2% CPU)</sub> | **7,734**<br><sub>(82 MiB / 24.4% CPU)</sub> | **12,392**<br><sub>(127 MiB / 22.4% CPU)</sub> |
| macOS | **37,144**<br><sub>(158 MiB / 12.6% CPU)</sub> | 🥇 **50,508**<br><sub>(178 MiB / 37.7% CPU)</sub> | **24,505**<br><sub>(104 MiB / 24.4% CPU)</sub> | **30,920**<br><sub>(119 MiB / 26.1% CPU)</sub> |

## WebSocket (H1 TLS → H1 TLS)

WebSocket echo round-trips/sec over **dual-TLS** H1 (`proxy_ssl` style). Mode: `compare-ws-h1tls` (`*-duplex-ws-h1tls`). Median of **3** repeats @ `0386b2aa`. Source: [37156931630](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37156931630).

| OS | Titanium | YARP | nginx | HAProxy | Envoy |
|---|---:|---:|---:|---:|---:|
| Windows | 🥇 **22,395**<br><sub>(100 MiB / 44.5% CPU)</sub> | **21,451**<br><sub>(92 MiB / 43.9% CPU)</sub> | **9,638**<br><sub>(142 MiB / 24.8% CPU)</sub> | *Not possible* | *Not possible* |
| Linux | **24,640**<br><sub>(133 MiB / 44.2% CPU)</sub> | **22,976**<br><sub>(134 MiB / 43.9% CPU)</sub> | 🥇 **28,478**<br><sub>(103 MiB / 37.0% CPU)</sub> | **26,978**<br><sub>(85 MiB / 39.3% CPU)</sub> | **27,918**<br><sub>(128 MiB / 38.2% CPU)</sub> |
| macOS | **28,012**<br><sub>(192 MiB / 28.5% CPU)</sub> | **32,133**<br><sub>(215 MiB / 34.9% CPU)</sub> | **35,774**<br><sub>(85 MiB / 22.8% CPU)</sub> | 🥇 **36,420**<br><sub>(100 MiB / 33.0% CPU)</sub> | **35,325**<br><sub>(119 MiB / 29.9% CPU)</sub> |

## WebSocket (H2 TLS 8441 → H1)

WebSocket echo over **RFC 8441** extended CONNECT (H2 TLS client → H1 plain origin). Mode: `compare-ws-h2` (`*-duplex-ws-h2`).

The probe client sets `NoDelay` and writes each HTTP/2 frame as one TLS record. Median of **3** repeats @ `0386b2aa`. Source: [37156946870](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37156946870).

*Not possible:* **nginx** column omitted (no RFC 8441 extended CONNECT).

| OS | Titanium | YARP | HAProxy | Envoy |
|---|---:|---:|---:|---:|
| Windows | 🥇 **26,080**<br><sub>(171 MiB / 47.0% CPU)</sub> | **0**<br><sub>(155 MiB / 20.4% CPU)</sub> | *Not possible* | *Not possible* |
| Linux | **47,626**<br><sub>(197 MiB / 40.1% CPU)</sub> | **0**<br><sub>(220 MiB / 42.2% CPU)</sub> | 🥇 **47,638**<br><sub>(85 MiB / 35.0% CPU)</sub> | **0**<br><sub>(123 MiB / 0.1% CPU)</sub> |
| macOS | **8,899**<br><sub>(238 MiB / 14.0% CPU)</sub> | **0**<br><sub>(193 MiB / 8.9% CPU)</sub> | 🥇 **20,337**<br><sub>(100 MiB / 21.3% CPU)</sub> | **0**<br><sub>(115 MiB / 0.2% CPU)</sub> |

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
| Release / wiki | `compare-product` via [RPS suite](https://github.com/justcoding121/titanium-web-proxy/blob/develop/.github/workflows/rps-suite.yml) | one job per wiki row on Win/Linux/macOS |
| Unary gRPC | `compare-grpc` | Linux only; paste leaves Win/macOS rows when those folders are absent |
| WebSocket dual-TLS | `compare-ws-h1tls` | Linux only |
| WebSocket RFC 8441 | `compare-ws-h2` | Linux only (nginx N/A) |
| Heavier tables | `compare-bodies` on Win/Linux/macOS; `post` / `lossy` / `arch` / `tls-cost` / `compare-saturation` on Linux only | one suite run each, one job per row |

See [PERF-GATES.md](https://github.com/justcoding121/titanium-web-proxy/blob/develop/tools/RpsLoadProbe/PERF-GATES.md).

### Arm shards (comparison groups)

`--arm-shard` takes a comparison-group key (`h1c-h1c`, from `--print-groups`), not an `i/n` index, so a re-run measures the same row after a peer install flake. `i/n` remains for smoke modes. Paste unions `rps-csv-<os>-shard-*` (`paste-compare-product-wiki.ps1 -RunIds …`). Table “Source: Actions” may list several run URLs for one table — **do not mix SHAs**. A single-row leg allows **60** minutes of ramp and **75** minutes overall. Free GitHub accounts run **20** jobs at once, **5** of them macOS. Give a full suite a quiet window: do not push pull requests or start other macOS jobs while it runs. Re-run infrastructure failures only (`gh run rerun --failed`). A failed gate is a finding and is never re-rolled; publish the last attempt, never the best of several. The tables on this page are @ `0386b2aa`. HTTP/3 comparison groups are extra runs on that same SHA: suite prep listed groups before libmsquic was installed, so those rows were missing from the suite matrix. Saturation, editions, and both WebSocket modes on that SHA were one job per OS. `compare-product` and `compare-bodies` still run on Windows, Linux, and macOS. Every other RPS suite mode runs on Linux only. `compare-spot` on pull requests to beta and stable stays on all three OS. The historical v2 MITM note below the product tables stays @ `41f4adee` because those run ids are from that fix, not this re-measure.

One wiki row = one GHA job’s Client×Origin cell set: TWP + YARP + nginx + HAProxy + Envoy (when the OS can run them) + TWP Lite + Full stay on the **same VM**. Shards split **rows**, not individual proxies — so **TWP÷YARP** and **Lite÷Reverse** remain same-job ratios. Do not compare **absolute** RPS across shards (different VMs).

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
