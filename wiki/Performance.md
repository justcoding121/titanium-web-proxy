# Performance

## Why this comparison is fair

These tables are a **same-harness, same-origin, same-runner-class** reverse-proxy comparison — not a blog-post bake-off of mismatched labs.

- **Same load generator** (`dotnet-httpclient`), **same origin process**, **same warmup/measure** (2s / 8s), **same concurrency ramp** (8, 16, 32, 64), **median of 3** GitHub Actions repeats.
- Every reverse arm is **three OS processes** (load generator + origin child + proxy child). Origin-direct omits the proxy. Peers are never in-process with the client.
- **Same runner class per table**: `windows-latest` / `ubuntu-latest` / `macos-15-intel` (4-core-class). Laptop High-perf and `macos-latest` are never mixed into these tables.
- **YARP** is `Yarp.ReverseProxy` **2.3.0** with equivalent TLS/ALPN. **nginx** uses streaming knobs (`keepalive 256`, buffering off). **HAProxy** (GPL v2 Community) and **Envoy** (Apache 2.0) are open-source terminate peers on the same loopback shape — Linux/macOS only; Windows cells are *Not possible* (no official HAProxy port; Envoy Windows support discontinued). Fair knobs: HAProxy `http-reuse aggressive`, `maxconn 256`, `nbthread`=CPU; Envoy `concurrency`=CPU, cluster limits ~256, no access log, upstream TLS verify off for loopback CA. Linux nginx (mainline + `http_v3`) is authoritative; Windows nginx has no QUIC — those cells are *Not possible*, not losses.
- **MITM is TWP-only** (CONNECT + forged certs). nginx/YARP cannot MITM; Lite/Full tables are overhead vs TWP reverse, not vs peers. There are **no MITM charts**.
- The product signal is **TWP÷YARP** (gated ≥ **0.75** reverse) and **MITM÷Reverse** (Lite ≥ **0.50**, Full ≥ **0.50**), not absolute RPS. nginx / HAProxy / Envoy are **wiki and chart peers only** — no CI gate. Absolute RPS moves with runner heat; ratios are the claim.
- Product fast paths (session-lite, skip-poll, compressed relay) are **in-tree defaults** for interception-off reverse — the same class of work YARP/nginx do. The harness does not disable TWP safety that peers also skip, and it does not retune to pass gates ([PERF-GATES.md](https://github.com/justcoding121/titanium-web-proxy/blob/develop/tools/RpsLoadProbe/PERF-GATES.md)).
- **MITM leaf keys** are a shared RSA/ECDSA pair per `CertificateManager` (one key for all forged leaves). That is an intentional RPS tradeoff: compromise of that material affects every host minted by that manager. Reverse/YARP/nginx paths do not mint leaves.

Numbers below are **Release** measurements with [RpsLoadProbe](https://github.com/justcoding121/titanium-web-proxy/tree/develop/tools/RpsLoadProbe). Native terminate peers: **nginx**, **HAProxy**, **Envoy** (where the OS supports them).

For pooling knobs and certificate first-visit tuning, see [Performance and pooling](Home#performance-and-pooling). For the local cool A/B lab, laptop tables, and profiling notes, see [Performance Profiling](Performance-Profiling).

## Contents

- [Measurement environment](#measurement-environment)
    - [Windows (GitHub-hosted `windows-latest`)](#windows-github-hosted-windows-latest)
    - [Linux (GitHub-hosted `ubuntu-latest`)](#linux-github-hosted-ubuntu-latest)
    - [macOS (GitHub-hosted `macos-15-intel`)](#macos-github-hosted-macos-15-intel)
    - [Tiered cadence](#tiered-cadence)
    - [Saturation control](#saturation-control)
- [Windows — Titanium vs nginx vs HAProxy vs Envoy vs YARP](#windows--titanium-vs-nginx-vs-haproxy-vs-envoy-vs-yarp)
- [Linux — Titanium vs nginx vs HAProxy vs Envoy vs YARP](#linux--titanium-vs-nginx-vs-haproxy-vs-envoy-vs-yarp)
    - [Tiny JSON reverse is nginx’s best case on Linux](#tiny-json-reverse-is-nginxs-best-case-on-linux)
    - [Why isn’t HTTP/3 > HTTP/2 > HTTP/1 in raw RPS?](#why-isnt-http3--http2--http1-in-raw-rps)
- [macOS — Titanium vs nginx vs HAProxy vs Envoy vs YARP](#macos--titanium-vs-nginx-vs-haproxy-vs-envoy-vs-yarp)
    - [Reverse](#reverse-2)
    - [MITM (TWP only)](#mitm-twp-only-2)
- [Editions (CLI / Plus / Intercept)](#editions-cli--plus--intercept)
- [Cross-version (7.0 vs 6.0)](#cross-version-70-vs-60)
- [Heavier reverse workloads](#heavier-reverse-workloads)
    - [Windows — heavier reverse GET (64 KiB / 256 KiB)](#windows--heavier-reverse-get-64-kib--256-kib)
    - [Linux — heavier reverse GET (64 KiB / 256 KiB)](#linux--heavier-reverse-get-64-kib--256-kib)
    - [Windows — POST 64 KiB request + 64 KiB response](#windows--post-64-kib-request--64-kib-response)
    - [Linux — POST 64 KiB request + 64 KiB response](#linux--post-64-kib-request--64-kib-response)
    - [Windows — lossy / high-RTT (H2 HOL / H3 loss)](#windows--lossy--high-rtt-h2-hol--h3-loss)
    - [Linux — lossy / high-RTT (H2 HOL / H3 loss)](#linux--lossy--high-rtt-h2-hol--h3-loss)
    - [Architecture-sensitive](#architecture-sensitive)
    - [TLS termination cost (H1 TLS → cleartext origin)](#tls-termination-cost-h1-tls--cleartext-origin)
- [Other measurements](#other-measurements)
- [Raising limits on large hosts](#raising-limits-on-large-hosts)

## Measurement environment

All three OS use the **4-core-class** public-repo GitHub-hosted runners: **Windows / Linux** at **4 vCPU / 16 GiB / 14 GB SSD**, **macOS** at **`macos-15-intel` 4-core / 14 GB** (not `macos-latest`). Same harness knobs (`workflow_dispatch` [RPS saturation](https://github.com/justcoding121/titanium-web-proxy/actions/workflows/rps-saturation.yml): warmup 2s / measure 8s; concurrency 8, 16, 32, 64; median of 3 repeats; `--stop-on-slo-fail` default on). Every `--ramp` arm is **three OS processes** (parent load generator + origin child + proxy child), except **origin-direct** arms (load gen + origin only). Prefer **TWP÷YARP** / **TWP÷nginx** ratios over absolute RPS.

Laptop High-perf / cool-paired Windows numbers live on [Performance Local Lab](Performance-Local-Lab). Do not mix those absolutes into the tables below.

### Tiered cadence

| Tier | Mode | When |
|------|------|------|
| Daily / per-PR | `compare-spot` | minutes |
| Milestone | `compare-terminate` / `compare-matrix` | ~1–2h |
| Editions | `compare-editions` | ~60 min (CLI / Plus / Intercept stress arms) |
| Cross-version (Gate 2) | `compare-cross-version` | ~1–2h vs committed 6.0 baselines |
| Release / wiki | `compare-product` | ~3–4h |
| Heavier tables | `compare-bodies` / `post` / `lossy` / `arch` / `bridges` / `tls-cost` | dispatch independently |

See [PERF-GATES.md](https://github.com/justcoding121/titanium-web-proxy/blob/develop/tools/RpsLoadProbe/PERF-GATES.md).

### Windows (GitHub-hosted `windows-latest`)

| | |
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

| | |
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

| | |
|---|---|
| OS | macOS 15 (GitHub-hosted `macos-15-intel`, Intel x86_64) |
| CPU | **4** logical processors |
| RAM | **14** GB |
| Runtime | .NET 10.0.x |
| nginx | Homebrew nginx with `--with-http_v3_module` (workflow fails if missing) |
| HAProxy | Homebrew `haproxy` (GHA install; bottles typically include `USE_QUIC`) |
| Envoy | Homebrew `envoy` when available (GHA install; HTTP/3 compiled in) |
| MsQuic | Homebrew `libmsquic` + `openssl@3` on `DYLD_LIBRARY_PATH` / `DYLD_FALLBACK_LIBRARY_PATH` (`QuicListener.IsSupported`) |
| YARP | Yarp.ReverseProxy **2.3.0** |
| Harness | RpsLoadProbe Release; median of 3 repeats where noted |

Do **not** use `macos-latest` (Apple Silicon, 3-core / 7 GB) for publishable saturation numbers.

### Saturation control

Calibration for the shared 4 vCPU loopback shape: how close client + origin are to saturated before ranking reverse peers. Tiny keep-alive GET. Median of **3** repeats @ `84b225f7` — [34355140813](https://github.com/justcoding121/titanium-web-proxy/actions/runs/34355140813). Warmup 2s / measure 8s; concurrency 8, 16, 32, 64. Block A **% of origin-HttpClient** uses median **peak** RPS. Blocks B/C use peer÷YARP / ÷nginx on median peak (not % of H1 origin). **RPS cells** embed median RSS / CPU for the **proxy child** plus its **full descendant tree** (serve-proxy → nginx master → workers); origin-direct samples the **origin** child. Product matrices below use matched `dotnet-httpclient` only (not bombardier).



```powershell
pwsh tools/RpsLoadProbe/run-rps.ps1 -Mode compare-saturation
```

#### Block A — H1 plain

| Arm | Generator | Notes |
|---|---|---|
| `origin-direct` | `dotnet-httpclient` | No proxy — origin ceiling under the same client used for publishable ratios |
| `origin-direct-bombardier` | `bombardier` | External client check (CI installs bombardier) |
| `bare-reverse-http1` | `dotnet-httpclient` | Thin C# H1 reverse (`BareHttp1ReverseProxy`) — .NET runtime / loopback ceiling for a three-process reverse hop; **not** a product peer |
| `nginx-reverse-http1` / `yarp-reverse-http1` / `twp-reverse-http1` | `dotnet-httpclient` | Product peers (medals among these three only) |

**Windows** (`windows-latest`)

| Arm | Generator | Sustain | Peak | % of origin-HttpClient |
|---|---|---:|---:|---:|
| origin-direct | dotnet-httpclient | **50,764**<br><sub>(55 MiB / 41.8% CPU)</sub> | **50,764**<br><sub>(55 MiB / 41.8% CPU)</sub> | **100.0%** |
| origin-direct-bombardier | bombardier | **39,174**<br><sub>(55 MiB / 22.4% CPU)</sub> | **39,174**<br><sub>(55 MiB / 22.4% CPU)</sub> | **77.2%** |
| bare-reverse-http1 | dotnet-httpclient | **25,470**<br><sub>(55 MiB / 46.2% CPU)</sub> | **25,470**<br><sub>(55 MiB / 46.2% CPU)</sub> | **50.2%** |
| nginx-reverse-http1 | dotnet-httpclient | **13,510**<br><sub>(123 MiB / 24.9% CPU)</sub> | **13,510**<br><sub>(123 MiB / 24.9% CPU)</sub> | **26.6%** |
| yarp-reverse-http1 | dotnet-httpclient | **21,164**<br><sub>(88 MiB / 51.0% CPU)</sub> | **21,164**<br><sub>(88 MiB / 51.0% CPU)</sub> | **41.7%** |
| twp-reverse-http1 | dotnet-httpclient | 🥇 **24,934**<br><sub>(76 MiB / 50.3% CPU)</sub> | **24,934**<br><sub>(76 MiB / 50.3% CPU)</sub> | **49.1%** |

**Linux** (`ubuntu-latest`)

| Arm | Generator | Sustain | Peak | % of origin-HttpClient |
|---|---|---:|---:|---:|
| origin-direct | dotnet-httpclient | **72,859**<br><sub>(80 MiB / 42.7% CPU)</sub> | **72,859**<br><sub>(80 MiB / 42.7% CPU)</sub> | **100.0%** |
| origin-direct-bombardier | bombardier | **41,914**<br><sub>(80 MiB / 34.1% CPU)</sub> | **41,914**<br><sub>(80 MiB / 34.1% CPU)</sub> | **57.5%** |
| bare-reverse-http1 | dotnet-httpclient | **32,705**<br><sub>(68 MiB / 45.5% CPU)</sub> | **32,705**<br><sub>(68 MiB / 45.5% CPU)</sub> | **44.9%** |
| nginx-reverse-http1 | dotnet-httpclient | 🥇 **38,533**<br><sub>(74 MiB / 40.8% CPU)</sub> | **38,533**<br><sub>(74 MiB / 40.8% CPU)</sub> | **52.9%** |
| yarp-reverse-http1 | dotnet-httpclient | **27,832**<br><sub>(113 MiB / 50.0% CPU)</sub> | **27,832**<br><sub>(113 MiB / 50.0% CPU)</sub> | **38.2%** |
| twp-reverse-http1 | dotnet-httpclient | **31,714**<br><sub>(83 MiB / 50.2% CPU)</sub> | **31,714**<br><sub>(83 MiB / 50.2% CPU)</sub> | **43.5%** |

Reverse peers are about **50–46%** of the origin-direct HttpClient peak on this runner class (Win TWP **50.0%**, Lin TWP **45.6%**). Prefer the **%** column over absolute RPS across runs. Bare and origin-direct are controls (not medal peers).

#### Block B — H2 TLS→H1

Peer ratios (÷YARP / ÷nginx) on median peak; **RPS cells** embed `(MiB / CPU%)`.

**Windows** (`windows-latest`)

| Arm | Generator | Sustain | Peak | ÷YARP | ÷nginx |
|---|---|---:|---:|---:|---:|
| nginx-reverse-http2 | dotnet-httpclient | **8,246**<br><sub>(139 MiB / 24.6% CPU)</sub> | **8,246**<br><sub>(139 MiB / 24.6% CPU)</sub> | **0.28×** | **1.00×** |
| yarp-reverse-http2 | dotnet-httpclient | **29,174**<br><sub>(91 MiB / 53.6% CPU)</sub> | **29,174**<br><sub>(91 MiB / 53.6% CPU)</sub> | **1.00×** | **3.54×** |
| twp-reverse-http2-cleartext | dotnet-httpclient | 🥇 **34,778**<br><sub>(102 MiB / 51.5% CPU)</sub> | **34,778**<br><sub>(102 MiB / 51.5% CPU)</sub> | **1.19×** | **4.22×** |

**Linux** (`ubuntu-latest`)

| Arm | Generator | Sustain | Peak | ÷YARP | ÷nginx |
|---|---|---:|---:|---:|---:|
| nginx-reverse-http2 | dotnet-httpclient | **14,831**<br><sub>(98 MiB / 19.4% CPU)</sub> | **14,831**<br><sub>(98 MiB / 19.4% CPU)</sub> | **0.51×** | **1.00×** |
| yarp-reverse-http2 | dotnet-httpclient | **29,012**<br><sub>(122 MiB / 49.7% CPU)</sub> | **29,012**<br><sub>(122 MiB / 49.7% CPU)</sub> | **1.00×** | **1.96×** |
| twp-reverse-http2-cleartext | dotnet-httpclient | 🥇 **33,240**<br><sub>(120 MiB / 51.6% CPU)</sub> | **33,240**<br><sub>(120 MiB / 51.6% CPU)</sub> | **1.15×** | **2.24×** |

#### Block C — H3→H1

Same layout as Block B. Requires QuicListener. nginx needs `http_v3_module` (Windows nginx has no QUIC). HAProxy needs `USE_QUIC` (GHA Linux compiles it; Homebrew typically has it). Envoy official binaries include HTTP/3.

**Windows** (`windows-latest`)

| Arm | Generator | Sustain | Peak | ÷YARP | ÷nginx |
|---|---|---:|---:|---:|---:|
| nginx-reverse-http3-cleartext | dotnet-httpclient | *Not possible (no QUIC)* | *Not possible (no QUIC)* | — | — |
| yarp-reverse-http3-cleartext | dotnet-httpclient | 🥇 **14,767**<br><sub>(156 MiB / 50.0% CPU)</sub> | **14,767**<br><sub>(156 MiB / 50.0% CPU)</sub> | **1.00×** | — |
| twp-reverse-http3-cleartext | dotnet-httpclient | **14,262**<br><sub>(104 MiB / 46.6% CPU)</sub> | **14,262**<br><sub>(104 MiB / 46.6% CPU)</sub> | **0.97×** | — |

**Linux** (`ubuntu-latest`)

| Arm | Generator | Sustain | Peak | ÷YARP | ÷nginx |
|---|---|---:|---:|---:|---:|
| nginx-reverse-http3-cleartext | dotnet-httpclient | **0**<br><sub>(105 MiB / 22.5% CPU)</sub> | **14,988**<br><sub>(105 MiB / 22.5% CPU)</sub> | **0.86×** | **1.00×** |
| yarp-reverse-http3-cleartext | dotnet-httpclient | **17,479**<br><sub>(184 MiB / 49.8% CPU)</sub> | **17,479**<br><sub>(184 MiB / 49.8% CPU)</sub> | **1.00×** | **1.17×** |
| twp-reverse-http3-cleartext | dotnet-httpclient | 🥇 **19,350**<br><sub>(144 MiB / 49.3% CPU)</sub> | **19,350**<br><sub>(144 MiB / 49.3% CPU)</sub> | **1.11×** | **1.29×** |

**How to read the tables**

- **Reverse** = bare transparent fixed-forward (no TWP plugins / interception). nginx knobs match TWP/YARP streaming (`keepalive 256`, `proxy_buffering off`). **MITM** = TWP-only table on the same Client×Origin wires: **Lite** = no-op handlers (unchanged-lite finish reuses reverse compressed relay); **Full** = mutating handlers that append up to four unique headers per direction (RPS harness adds one; product uses `MitmCompressedRelayHelper` — no probe name in library code). Remove/replace/non-unique header growth and body mutation still force full decode/re-encode. nginx/YARP cannot MITM. **HTTP/3 has no cleartext client** (QUIC always encrypted).
- **Sustainable** = last concurrency that still met error/latency SLOs. **Peak** = highest RPS in that ramp.
- 🥇 = best among **TWP / nginx / YARP** on Reverse rows (or saturation blocks): highest RPS; on an RPS tie, lower Memory (RSS) then lower CPU%. MITM is TWP-only. **Lite÷Reverse** / **Full÷Reverse** = TWP MITM lite or full sustain ÷ TWP Reverse sustain on the same Client×Origin from the same `compare-product` job.
- *Not possible* = product cannot do that path. *Not measured* = path exists but no published number yet for that OS.
- Product refresh: `compare-product` @ `84b225f7` — [34355136373](https://github.com/justcoding121/titanium-web-proxy/actions/runs/34355136373). Heavier/saturation/tls:

```powershell
pwsh tools/RpsLoadProbe/run-rps.ps1 -Mode compare-product
pwsh tools/RpsLoadProbe/run-rps.ps1 -Mode compare-bodies
pwsh tools/RpsLoadProbe/run-rps.ps1 -Mode compare-post
pwsh tools/RpsLoadProbe/run-rps.ps1 -Mode compare-lossy
pwsh tools/RpsLoadProbe/run-rps.ps1 -Mode compare-tls-cost
pwsh tools/RpsLoadProbe/run-rps.ps1 -Mode compare-arch
pwsh tools/RpsLoadProbe/run-rps.ps1 -Mode compare-saturation
```

## Windows — Titanium vs nginx vs HAProxy vs Envoy vs YARP

Client / origin: HTTP version and whether TLS is used (`plain` = cleartext, `TLS` = encrypted, `QUIC` = HTTP/3).

### Reverse

Median of **3 repeats** on `windows-latest` (4 vCPU / 16 GiB). Bare reverse 5×5 @ `84b225f7` — `compare-product` [34355136373](https://github.com/justcoding121/titanium-web-proxy/actions/runs/34355136373). Warmup 2s / measure 8s; concurrency 8, 16, 32, 64. Prefer TWP÷peer ratios over absolute RPS. **RPS cells** include median RSS / CPU at the peak-RPS step as `<br><sub>(MiB / CPU%)</sub>`. nginx terminate peers use `keepalive 256` + streaming buffers. **HAProxy / Envoy are Linux-only peers** (no official Windows port). Laptop High-perf / cool-paired numbers stay on the [local lab](Performance-Local-Lab).

**Load generators:** Reverse inbound H3 arms use **`dotnet-httpclient`** (`http_version=3.0`, `RequestVersionExact`). nginx/Windows is same-OS only (no QUIC). HAProxy/Envoy are Linux-only terminate peers.

![Windows reverse](images/rps-product-reverse-windows.png)

| Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | HAProxy sustain | HAProxy peak | Envoy sustain | Envoy peak | YARP sustain | YARP peak |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| HTTP/1 · plain | HTTP/1 · plain | 🥇 **36826**<br><sub>(79 MiB / 48.2% CPU)</sub> | 🥇 **36826**<br><sub>(79 MiB / 48.2% CPU)</sub> | **27338**<br><sub>(124 MiB / 24.8% CPU)</sub> | **27338**<br><sub>(124 MiB / 24.8% CPU)</sub> | *Not possible* | *Not possible* | *Not possible* | *Not possible* | **33052**<br><sub>(87 MiB / 49.2% CPU)</sub> | **33052**<br><sub>(87 MiB / 49.2% CPU)</sub> |
| HTTP/1 · plain | HTTP/1 · TLS | 🥇 **31728**<br><sub>(90 MiB / 48.2% CPU)</sub> | 🥇 **31728**<br><sub>(90 MiB / 48.2% CPU)</sub> | **16932**<br><sub>(134 MiB / 24.6% CPU)</sub> | **16932**<br><sub>(134 MiB / 24.6% CPU)</sub> | *Not possible* | *Not possible* | *Not possible* | *Not possible* | **29836**<br><sub>(98 MiB / 45.9% CPU)</sub> | **29836**<br><sub>(98 MiB / 45.9% CPU)</sub> |
| HTTP/1 · plain | HTTP/2 · plain | 🥇 **50143**<br><sub>(110 MiB / 47.8% CPU)</sub> | 🥇 **50143**<br><sub>(110 MiB / 47.8% CPU)</sub> | *Not possible* | *Not possible* | *Not possible* | *Not possible* | *Not possible* | *Not possible* | **46652**<br><sub>(97 MiB / 49.7% CPU)</sub> | **46652**<br><sub>(97 MiB / 49.7% CPU)</sub> |
| HTTP/1 · plain | HTTP/2 · TLS | 🥇 **44922**<br><sub>(124 MiB / 46.5% CPU)</sub> | 🥇 **44922**<br><sub>(124 MiB / 46.5% CPU)</sub> | *Not possible* | *Not possible* | *Not possible* | *Not possible* | *Not possible* | *Not possible* | **42567**<br><sub>(96 MiB / 48.3% CPU)</sub> | **42567**<br><sub>(96 MiB / 48.3% CPU)</sub> |
| HTTP/1 · plain | HTTP/3 · QUIC | **22817**<br><sub>(111 MiB / 50.2% CPU)</sub> | **22817**<br><sub>(111 MiB / 50.2% CPU)</sub> | *Not possible (no QUIC)* | *Not possible (no QUIC)* | *Not possible* | *Not possible* | *Not possible* | *Not possible* | 🥇 **23705**<br><sub>(123 MiB / 50.5% CPU)</sub> | 🥇 **23705**<br><sub>(123 MiB / 50.5% CPU)</sub> |
| HTTP/1 · TLS | HTTP/1 · plain | 🥇 **32185**<br><sub>(91 MiB / 49.3% CPU)</sub> | 🥇 **32185**<br><sub>(91 MiB / 49.3% CPU)</sub> | **18207**<br><sub>(140 MiB / 24.6% CPU)</sub> | **18207**<br><sub>(140 MiB / 24.6% CPU)</sub> | *Not possible* | *Not possible* | *Not possible* | *Not possible* | **29623**<br><sub>(100 MiB / 50% CPU)</sub> | **29623**<br><sub>(100 MiB / 50% CPU)</sub> |
| HTTP/1 · TLS | HTTP/1 · TLS | 🥇 **30393**<br><sub>(85 MiB / 46.9% CPU)</sub> | 🥇 **30393**<br><sub>(85 MiB / 46.9% CPU)</sub> | **13608**<br><sub>(142 MiB / 24.9% CPU)</sub> | **13608**<br><sub>(142 MiB / 24.9% CPU)</sub> | *Not possible* | *Not possible* | *Not possible* | *Not possible* | **27048**<br><sub>(104 MiB / 49% CPU)</sub> | **27048**<br><sub>(104 MiB / 49% CPU)</sub> |
| HTTP/1 · TLS | HTTP/2 · plain | 🥇 **42478**<br><sub>(121 MiB / 49% CPU)</sub> | 🥇 **42478**<br><sub>(121 MiB / 49% CPU)</sub> | *Not possible* | *Not possible* | *Not possible* | *Not possible* | *Not possible* | *Not possible* | **39367**<br><sub>(112 MiB / 49.8% CPU)</sub> | **39367**<br><sub>(112 MiB / 49.8% CPU)</sub> |
| HTTP/1 · TLS | HTTP/2 · TLS | 🥇 **38546**<br><sub>(126 MiB / 45.6% CPU)</sub> | 🥇 **38546**<br><sub>(126 MiB / 45.6% CPU)</sub> | *Not possible* | *Not possible* | *Not possible* | *Not possible* | *Not possible* | *Not possible* | **36912**<br><sub>(106 MiB / 48.7% CPU)</sub> | **36912**<br><sub>(106 MiB / 48.7% CPU)</sub> |
| HTTP/1 · TLS | HTTP/3 · QUIC | 🥇 **20778**<br><sub>(115 MiB / 50.7% CPU)</sub> | 🥇 **20778**<br><sub>(115 MiB / 50.7% CPU)</sub> | *Not possible (no QUIC)* | *Not possible (no QUIC)* | *Not possible* | *Not possible* | *Not possible* | *Not possible* | **20571**<br><sub>(130 MiB / 49.7% CPU)</sub> | **20571**<br><sub>(130 MiB / 49.7% CPU)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | 🥇 **46604**<br><sub>(94 MiB / 51.2% CPU)</sub> | 🥇 **46604**<br><sub>(94 MiB / 51.2% CPU)</sub> | *Not possible* | *Not possible* | *Not possible* | *Not possible* | *Not possible* | *Not possible* | **45286**<br><sub>(87 MiB / 49.7% CPU)</sub> | **45286**<br><sub>(87 MiB / 49.7% CPU)</sub> |
| HTTP/2 · plain | HTTP/1 · TLS | 🥇 **41209**<br><sub>(112 MiB / 50.2% CPU)</sub> | 🥇 **41209**<br><sub>(112 MiB / 50.2% CPU)</sub> | *Not possible* | *Not possible* | *Not possible* | *Not possible* | *Not possible* | *Not possible* | **38993**<br><sub>(92 MiB / 50.2% CPU)</sub> | **38993**<br><sub>(92 MiB / 50.2% CPU)</sub> |
| HTTP/2 · plain | HTTP/2 · plain | 🥇 **118838**<br><sub>(58 MiB / 30.7% CPU)</sub> | 🥇 **118838**<br><sub>(58 MiB / 30.7% CPU)</sub> | *Not possible* | *Not possible* | *Not possible* | *Not possible* | *Not possible* | *Not possible* | **69500**<br><sub>(97 MiB / 49.1% CPU)</sub> | **69500**<br><sub>(97 MiB / 49.1% CPU)</sub> |
| HTTP/2 · plain | HTTP/2 · TLS | 🥇 **101514**<br><sub>(64 MiB / 28% CPU)</sub> | 🥇 **101514**<br><sub>(64 MiB / 28% CPU)</sub> | *Not possible* | *Not possible* | *Not possible* | *Not possible* | *Not possible* | *Not possible* | **61945**<br><sub>(99 MiB / 49.1% CPU)</sub> | **61945**<br><sub>(99 MiB / 49.1% CPU)</sub> |
| HTTP/2 · plain | HTTP/3 · QUIC | 🥇 **33632**<br><sub>(128 MiB / 51% CPU)</sub> | 🥇 **33632**<br><sub>(128 MiB / 51% CPU)</sub> | *Not possible (no QUIC)* | *Not possible (no QUIC)* | *Not possible* | *Not possible* | *Not possible* | *Not possible* | **32494**<br><sub>(126 MiB / 51.8% CPU)</sub> | **32494**<br><sub>(126 MiB / 51.8% CPU)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **45454**<br><sub>(104 MiB / 51% CPU)</sub> | 🥇 **45454**<br><sub>(104 MiB / 51% CPU)</sub> | **17529**<br><sub>(139 MiB / 24.2% CPU)</sub> | **17529**<br><sub>(139 MiB / 24.2% CPU)</sub> | *Not possible* | *Not possible* | *Not possible* | *Not possible* | **42007**<br><sub>(93 MiB / 50% CPU)</sub> | **42007**<br><sub>(93 MiB / 50% CPU)</sub> |
| HTTP/2 · TLS | HTTP/1 · TLS | 🥇 **40127**<br><sub>(107 MiB / 49.5% CPU)</sub> | 🥇 **40127**<br><sub>(107 MiB / 49.5% CPU)</sub> | **12976**<br><sub>(142 MiB / 24.4% CPU)</sub> | **12976**<br><sub>(142 MiB / 24.4% CPU)</sub> | *Not possible* | *Not possible* | *Not possible* | *Not possible* | **36749**<br><sub>(100 MiB / 51.3% CPU)</sub> | **36749**<br><sub>(100 MiB / 51.3% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | 🥇 **108930**<br><sub>(76 MiB / 29.9% CPU)</sub> | 🥇 **108930**<br><sub>(76 MiB / 29.9% CPU)</sub> | *Not possible* | *Not possible* | *Not possible* | *Not possible* | *Not possible* | *Not possible* | **60474**<br><sub>(97 MiB / 51.8% CPU)</sub> | **60474**<br><sub>(97 MiB / 51.8% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **97196**<br><sub>(75 MiB / 30% CPU)</sub> | 🥇 **97196**<br><sub>(75 MiB / 30% CPU)</sub> | *Not possible* | *Not possible* | *Not possible* | *Not possible* | *Not possible* | *Not possible* | **56030**<br><sub>(110 MiB / 50.7% CPU)</sub> | **56030**<br><sub>(110 MiB / 50.7% CPU)</sub> |
| HTTP/2 · TLS | HTTP/3 · QUIC | 🥇 **32998**<br><sub>(145 MiB / 51.2% CPU)</sub> | 🥇 **32998**<br><sub>(145 MiB / 51.2% CPU)</sub> | *Not possible (no QUIC)* | *Not possible (no QUIC)* | *Not possible* | *Not possible* | *Not possible* | *Not possible* | **29059**<br><sub>(130 MiB / 52.7% CPU)</sub> | **29059**<br><sub>(130 MiB / 52.7% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **22862**<br><sub>(116 MiB / 41.5% CPU)</sub> | 🥇 **22862**<br><sub>(116 MiB / 41.5% CPU)</sub> | *Not measured* | *Not measured* | *Not possible* | *Not possible* | *Not possible* | *Not possible* | **21476**<br><sub>(146 MiB / 46.2% CPU)</sub> | **21476**<br><sub>(146 MiB / 46.2% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **19828**<br><sub>(118 MiB / 44.4% CPU)</sub> | 🥇 **19828**<br><sub>(118 MiB / 44.4% CPU)</sub> | *Not measured* | *Not measured* | *Not possible* | *Not possible* | *Not possible* | *Not possible* | **19031**<br><sub>(152 MiB / 50.1% CPU)</sub> | **19031**<br><sub>(152 MiB / 50.1% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/2 · plain | 🥇 **38306**<br><sub>(152 MiB / 46.7% CPU)</sub> | 🥇 **38306**<br><sub>(152 MiB / 46.7% CPU)</sub> | *Not possible (no H3 to H2)* | *Not possible (no H3 to H2)* | *Not possible* | *Not possible* | *Not possible* | *Not possible* | **30912**<br><sub>(159 MiB / 46.7% CPU)</sub> | **30912**<br><sub>(159 MiB / 46.7% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | 🥇 **35523**<br><sub>(141 MiB / 45.7% CPU)</sub> | 🥇 **35523**<br><sub>(141 MiB / 45.7% CPU)</sub> | *Not possible (no H3 to H2)* | *Not possible (no H3 to H2)* | *Not possible* | *Not possible* | *Not possible* | *Not possible* | **26464**<br><sub>(175 MiB / 46.2% CPU)</sub> | **26464**<br><sub>(175 MiB / 46.2% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/3 · QUIC | 🥇 **17829**<br><sub>(128 MiB / 46.1% CPU)</sub> | 🥇 **17829**<br><sub>(128 MiB / 46.1% CPU)</sub> | *Not possible (no QUIC)* | *Not possible (no QUIC)* | *Not possible* | *Not possible* | *Not possible* | *Not possible* | **14248**<br><sub>(173 MiB / 48% CPU)</sub> | **14248**<br><sub>(173 MiB / 48% CPU)</sub> |

### MITM (TWP only)

Same Client×Origin wires with interception on (`compare-product` [34355136373](https://github.com/justcoding121/titanium-web-proxy/actions/runs/34355136373)). **Lite** = no-op handlers (unchanged-lite finish). **Full** = append-only header mutation (harness: one probe header each way; product: generic append-only relay via `MitmCompressedRelayHelper`). nginx/HAProxy/Envoy/YARP cannot MITM. **Lite÷Reverse** / **Full÷Reverse** vs bare reverse (same job). Completion gate: Lite ≥ **0.50×** and Full ≥ **0.50×** reverse sustain @ c=64 (median of 3 GHA runs); reverse TWP÷YARP ≥ **0.75×** (no terminate-peer gate).

**v1 append-only relay (2026-08-27):** Pre-fix H2→H2 Full÷Reverse was **0.13–0.16×** ([32960766249](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32960766249)). Post-fix @ `df172718`: H2 plain→H2 plain Full **0.77–0.79×**, H3→H1 Full **0.91–0.93×**, all MITM arms ≥ **0.70×** on median of [33041445371](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33041445371), [33055267086](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33055267086), [33055272140](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33055272140).

**v2 drop-only + non-unique append (2026-08-27):** `MitmStaticRebuildHelper` rebuilds static HPACK/QPACK after 1–4 unique header drops; trailing non-unique appends stay on compressed relay. @ `84b225f7`: all MITM arms ≥ **0.70×** on GHA median ([33087088466](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087088466), [33087091622](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087091622), [33105885748](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33105885748) Linux; [33087085235](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087085235), [33087088466](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087088466), [33087091622](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087091622) Windows). H2 plain→H2 plain Full **0.77–0.79×** (Win) / **0.78×** (Lin).

| Client | Origin | Lite sustain | Full sustain | Lite÷Reverse | Full÷Reverse |
|---|---|---:|---:|---:|---:|
| HTTP/1 · plain | HTTP/1 · plain | **36412**<br><sub>(81 MiB / 51.1% CPU)</sub> | **35739**<br><sub>(81 MiB / 51.2% CPU)</sub> | **0.99×** | **0.97×** |
| HTTP/1 · plain | HTTP/1 · TLS | **31994**<br><sub>(95 MiB / 48.9% CPU)</sub> | **32275**<br><sub>(95 MiB / 47.7% CPU)</sub> | **1.01×** | **1.02×** |
| HTTP/1 · plain | HTTP/2 · plain | **50326**<br><sub>(126 MiB / 52% CPU)</sub> | **47367**<br><sub>(114 MiB / 50.4% CPU)</sub> | **1×** | **0.94×** |
| HTTP/1 · plain | HTTP/2 · TLS | **44750**<br><sub>(123 MiB / 50.3% CPU)</sub> | **41889**<br><sub>(119 MiB / 48.5% CPU)</sub> | **1×** | **0.93×** |
| HTTP/1 · plain | HTTP/3 · QUIC | **23608**<br><sub>(108 MiB / 52.6% CPU)</sub> | **22100**<br><sub>(115 MiB / 52.5% CPU)</sub> | **1.03×** | **0.97×** |
| HTTP/1 · TLS | HTTP/1 · plain | **32024**<br><sub>(95 MiB / 47.8% CPU)</sub> | **31682**<br><sub>(95 MiB / 46.4% CPU)</sub> | **0.99×** | **0.98×** |
| HTTP/1 · TLS | HTTP/1 · TLS | **30200**<br><sub>(95 MiB / 48.4% CPU)</sub> | **29133**<br><sub>(96 MiB / 47.2% CPU)</sub> | **0.99×** | **0.96×** |
| HTTP/1 · TLS | HTTP/2 · plain | **42118**<br><sub>(126 MiB / 48.8% CPU)</sub> | **39595**<br><sub>(122 MiB / 49.2% CPU)</sub> | **0.99×** | **0.93×** |
| HTTP/1 · TLS | HTTP/2 · TLS | **38108**<br><sub>(124 MiB / 47.5% CPU)</sub> | **36366**<br><sub>(127 MiB / 48.1% CPU)</sub> | **0.99×** | **0.94×** |
| HTTP/1 · TLS | HTTP/3 · QUIC | **20258**<br><sub>(116 MiB / 50.9% CPU)</sub> | **20342**<br><sub>(126 MiB / 51.3% CPU)</sub> | **0.97×** | **0.98×** |
| HTTP/2 · plain | HTTP/1 · plain | **43937**<br><sub>(98 MiB / 52.4% CPU)</sub> | **44138**<br><sub>(102 MiB / 51.7% CPU)</sub> | **0.94×** | **0.95×** |
| HTTP/2 · plain | HTTP/1 · TLS | **39591**<br><sub>(106 MiB / 50% CPU)</sub> | **39439**<br><sub>(106 MiB / 52.6% CPU)</sub> | **0.96×** | **0.96×** |
| HTTP/2 · plain | HTTP/2 · plain | **95038**<br><sub>(71 MiB / 37.2% CPU)</sub> | **93716**<br><sub>(71 MiB / 40.9% CPU)</sub> | **0.8×** | **0.79×** |
| HTTP/2 · plain | HTTP/2 · TLS | **84653**<br><sub>(75 MiB / 37% CPU)</sub> | **81641**<br><sub>(79 MiB / 38% CPU)</sub> | **0.83×** | **0.8×** |
| HTTP/2 · plain | HTTP/3 · QUIC | **34076**<br><sub>(132 MiB / 52.2% CPU)</sub> | **33345**<br><sub>(134 MiB / 54.4% CPU)</sub> | **1.01×** | **0.99×** |
| HTTP/2 · TLS | HTTP/1 · plain | **42573**<br><sub>(110 MiB / 54.6% CPU)</sub> | **41705**<br><sub>(111 MiB / 53.6% CPU)</sub> | **0.94×** | **0.92×** |
| HTTP/2 · TLS | HTTP/1 · TLS | **38002**<br><sub>(105 MiB / 51.8% CPU)</sub> | **37626**<br><sub>(104 MiB / 52.9% CPU)</sub> | **0.95×** | **0.94×** |
| HTTP/2 · TLS | HTTP/2 · plain | **91812**<br><sub>(86 MiB / 39.7% CPU)</sub> | **87683**<br><sub>(87 MiB / 39.9% CPU)</sub> | **0.84×** | **0.8×** |
| HTTP/2 · TLS | HTTP/2 · TLS | **82708**<br><sub>(84 MiB / 39% CPU)</sub> | **78347**<br><sub>(82 MiB / 40.1% CPU)</sub> | **0.85×** | **0.81×** |
| HTTP/2 · TLS | HTTP/3 · QUIC | **32768**<br><sub>(138 MiB / 52.9% CPU)</sub> | **32140**<br><sub>(143 MiB / 55% CPU)</sub> | **0.99×** | **0.97×** |
| HTTP/3 · QUIC | HTTP/1 · plain | **21240**<br><sub>(112 MiB / 42.3% CPU)</sub> | **20752**<br><sub>(111 MiB / 43.4% CPU)</sub> | **0.93×** | **0.91×** |
| HTTP/3 · QUIC | HTTP/1 · TLS | **18702**<br><sub>(126 MiB / 42.5% CPU)</sub> | **19024**<br><sub>(116 MiB / 43.9% CPU)</sub> | **0.94×** | **0.96×** |
| HTTP/3 · QUIC | HTTP/2 · plain | **34449**<br><sub>(146 MiB / 49.9% CPU)</sub> | **33000**<br><sub>(144 MiB / 49.1% CPU)</sub> | **0.9×** | **0.86×** |
| HTTP/3 · QUIC | HTTP/2 · TLS | **32315**<br><sub>(150 MiB / 47.2% CPU)</sub> | **30635**<br><sub>(145 MiB / 48.3% CPU)</sub> | **0.91×** | **0.86×** |
| HTTP/3 · QUIC | HTTP/3 · QUIC | **17481**<br><sub>(122 MiB / 49.4% CPU)</sub> | **16604**<br><sub>(122 MiB / 48% CPU)</sub> | **0.98×** | **0.93×** |

## Linux — Titanium vs nginx vs HAProxy vs Envoy vs YARP

### Reverse

Median of **3 repeats** on `ubuntu-latest` (4 vCPU / 16 GiB). Bare reverse 5×5 @ `84b225f7` — `compare-product` [34355136373](https://github.com/justcoding121/titanium-web-proxy/actions/runs/34355136373). Warmup 2s / measure 8s; concurrency 8, 16, 32, 64. **Linux nginx is the authoritative nginx baseline.** HAProxy (3.2 `USE_QUIC`) and Envoy (GitHub release, HTTP/3 compiled in) run on the same loopback shape as nginx/YARP. nginx terminate peers use `keepalive 256` + streaming buffers. The RPS workflow installs nginx.org mainline (`http_v3_module`), a QUIC-enabled HAProxy, Envoy, and `libmsquic`. Prefer ratios over absolute RPS.

![Linux reverse](images/rps-product-reverse-linux.png)

| Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | HAProxy sustain | HAProxy peak | Envoy sustain | Envoy peak | YARP sustain | YARP peak |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| HTTP/1 · plain | HTTP/1 · plain | **32568**<br><sub>(88 MiB / 50.9% CPU)</sub> | **32568**<br><sub>(88 MiB / 50.9% CPU)</sub> | 🥇 **38686**<br><sub>(74 MiB / 41% CPU)</sub> | 🥇 **38686**<br><sub>(74 MiB / 41% CPU)</sub> | *Not measured* | *Not measured* | *Not measured* | *Not measured* | **27488**<br><sub>(114 MiB / 50.2% CPU)</sub> | **27488**<br><sub>(114 MiB / 50.2% CPU)</sub> |
| HTTP/1 · plain | HTTP/1 · TLS | **24138**<br><sub>(105 MiB / 50.8% CPU)</sub> | **24138**<br><sub>(105 MiB / 50.8% CPU)</sub> | 🥇 **28999**<br><sub>(89 MiB / 42.3% CPU)</sub> | 🥇 **28999**<br><sub>(89 MiB / 42.3% CPU)</sub> | *Not measured* | *Not measured* | *Not measured* | *Not measured* | **21712**<br><sub>(128 MiB / 50.2% CPU)</sub> | **21712**<br><sub>(128 MiB / 50.2% CPU)</sub> |
| HTTP/1 · plain | HTTP/2 · plain | 🥇 **40674**<br><sub>(126 MiB / 50.8% CPU)</sub> | 🥇 **40674**<br><sub>(126 MiB / 50.8% CPU)</sub> | *Not possible* | *Not possible* | *Not possible* | *Not possible* | *Not possible* | *Not possible* | **35672**<br><sub>(123 MiB / 48.8% CPU)</sub> | **35672**<br><sub>(123 MiB / 48.8% CPU)</sub> |
| HTTP/1 · plain | HTTP/2 · TLS | 🥇 **32698**<br><sub>(146 MiB / 49.6% CPU)</sub> | 🥇 **32698**<br><sub>(146 MiB / 49.6% CPU)</sub> | *Not possible* | *Not possible* | *Not possible* | *Not possible* | *Not possible* | *Not possible* | **29688**<br><sub>(132 MiB / 48% CPU)</sub> | **29688**<br><sub>(132 MiB / 48% CPU)</sub> |
| HTTP/1 · plain | HTTP/3 · QUIC | 🥇 **23815**<br><sub>(137 MiB / 53% CPU)</sub> | 🥇 **23815**<br><sub>(137 MiB / 53% CPU)</sub> | *Not possible (no QUIC)* | *Not possible (no QUIC)* | *Not possible (no QUIC)* | *Not possible (no QUIC)* | *Not possible (no QUIC)* | *Not possible (no QUIC)* | **21424**<br><sub>(154 MiB / 49% CPU)</sub> | **21424**<br><sub>(154 MiB / 49% CPU)</sub> |
| HTTP/1 · TLS | HTTP/1 · plain | **23109**<br><sub>(108 MiB / 49.8% CPU)</sub> | **23109**<br><sub>(108 MiB / 49.8% CPU)</sub> | 🥇 **27682**<br><sub>(98 MiB / 41.7% CPU)</sub> | 🥇 **27682**<br><sub>(98 MiB / 41.7% CPU)</sub> | *Not measured* | *Not measured* | *Not measured* | *Not measured* | **20355**<br><sub>(136 MiB / 50.6% CPU)</sub> | **20355**<br><sub>(136 MiB / 50.6% CPU)</sub> |
| HTTP/1 · TLS | HTTP/1 · TLS | **19602**<br><sub>(110 MiB / 48.8% CPU)</sub> | **19602**<br><sub>(110 MiB / 48.8% CPU)</sub> | 🥇 **21858**<br><sub>(101 MiB / 41.7% CPU)</sub> | 🥇 **21858**<br><sub>(101 MiB / 41.7% CPU)</sub> | *Not measured* | *Not measured* | *Not measured* | *Not measured* | **17177**<br><sub>(135 MiB / 50% CPU)</sub> | **17177**<br><sub>(135 MiB / 50% CPU)</sub> |
| HTTP/1 · TLS | HTTP/2 · plain | 🥇 **27923**<br><sub>(140 MiB / 50.1% CPU)</sub> | 🥇 **27923**<br><sub>(140 MiB / 50.1% CPU)</sub> | *Not possible* | *Not possible* | *Not possible* | *Not possible* | *Not possible* | *Not possible* | **24884**<br><sub>(153 MiB / 48.9% CPU)</sub> | **24884**<br><sub>(153 MiB / 48.9% CPU)</sub> |
| HTTP/1 · TLS | HTTP/2 · TLS | 🥇 **24310**<br><sub>(155 MiB / 48.8% CPU)</sub> | 🥇 **24310**<br><sub>(155 MiB / 48.8% CPU)</sub> | *Not possible* | *Not possible* | *Not possible* | *Not possible* | *Not possible* | *Not possible* | **22345**<br><sub>(144 MiB / 48.4% CPU)</sub> | **22345**<br><sub>(144 MiB / 48.4% CPU)</sub> |
| HTTP/1 · TLS | HTTP/3 · QUIC | 🥇 **18410**<br><sub>(147 MiB / 51.9% CPU)</sub> | 🥇 **18410**<br><sub>(147 MiB / 51.9% CPU)</sub> | *Not possible (no QUIC)* | *Not possible (no QUIC)* | *Not possible (no QUIC)* | *Not possible (no QUIC)* | *Not possible (no QUIC)* | *Not possible (no QUIC)* | **16630**<br><sub>(162 MiB / 49.9% CPU)</sub> | **16630**<br><sub>(162 MiB / 49.9% CPU)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | 🥇 **37410**<br><sub>(114 MiB / 53.7% CPU)</sub> | 🥇 **37410**<br><sub>(114 MiB / 53.7% CPU)</sub> | *Not possible* | *Not possible* | *Not possible* | *Not possible* | *Not possible* | *Not possible* | **34493**<br><sub>(115 MiB / 50% CPU)</sub> | **34493**<br><sub>(115 MiB / 50% CPU)</sub> |
| HTTP/2 · plain | HTTP/1 · TLS | 🥇 **27674**<br><sub>(122 MiB / 51.8% CPU)</sub> | 🥇 **27674**<br><sub>(122 MiB / 51.8% CPU)</sub> | *Not possible* | *Not possible* | *Not possible* | *Not possible* | *Not possible* | *Not possible* | **25774**<br><sub>(126 MiB / 50.6% CPU)</sub> | **25774**<br><sub>(126 MiB / 50.6% CPU)</sub> |
| HTTP/2 · plain | HTTP/2 · plain | 🥇 **87869**<br><sub>(90 MiB / 36.8% CPU)</sub> | 🥇 **87869**<br><sub>(90 MiB / 36.8% CPU)</sub> | *Not possible* | *Not possible* | *Not possible* | *Not possible* | *Not possible* | *Not possible* | **48686**<br><sub>(120 MiB / 46.9% CPU)</sub> | **48686**<br><sub>(120 MiB / 46.9% CPU)</sub> |
| HTTP/2 · plain | HTTP/2 · TLS | 🥇 **59128**<br><sub>(88 MiB / 36% CPU)</sub> | 🥇 **59128**<br><sub>(88 MiB / 36% CPU)</sub> | *Not possible* | *Not possible* | *Not possible* | *Not possible* | *Not possible* | *Not possible* | **39662**<br><sub>(127 MiB / 46.1% CPU)</sub> | **39662**<br><sub>(127 MiB / 46.1% CPU)</sub> |
| HTTP/2 · plain | HTTP/3 · QUIC | 🥇 **28184**<br><sub>(138 MiB / 50.6% CPU)</sub> | 🥇 **28184**<br><sub>(138 MiB / 50.6% CPU)</sub> | *Not possible (no QUIC)* | *Not possible (no QUIC)* | *Not possible (no QUIC)* | *Not possible (no QUIC)* | *Not possible (no QUIC)* | *Not possible (no QUIC)* | **26445**<br><sub>(151 MiB / 47.3% CPU)</sub> | **26445**<br><sub>(151 MiB / 47.3% CPU)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **34358**<br><sub>(118 MiB / 52.9% CPU)</sub> | 🥇 **34358**<br><sub>(118 MiB / 52.9% CPU)</sub> | **15242**<br><sub>(98 MiB / 19.5% CPU)</sub> | **15242**<br><sub>(98 MiB / 19.5% CPU)</sub> | *Not measured* | *Not measured* | *Not measured* | *Not measured* | **28852**<br><sub>(126 MiB / 50% CPU)</sub> | **28852**<br><sub>(126 MiB / 50% CPU)</sub> |
| HTTP/2 · TLS | HTTP/1 · TLS | 🥇 **26643**<br><sub>(121 MiB / 51.1% CPU)</sub> | 🥇 **26643**<br><sub>(121 MiB / 51.1% CPU)</sub> | **12678**<br><sub>(108 MiB / 20% CPU)</sub> | **12678**<br><sub>(108 MiB / 20% CPU)</sub> | *Not measured* | *Not measured* | *Not measured* | *Not measured* | **23146**<br><sub>(127 MiB / 50.1% CPU)</sub> | **23146**<br><sub>(127 MiB / 50.1% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | 🥇 **75907**<br><sub>(104 MiB / 36.5% CPU)</sub> | 🥇 **75907**<br><sub>(104 MiB / 36.5% CPU)</sub> | *Not possible* | *Not possible* | *Not possible* | *Not possible* | *Not possible* | *Not possible* | **39480**<br><sub>(127 MiB / 46.8% CPU)</sub> | **39480**<br><sub>(127 MiB / 46.8% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **55030**<br><sub>(97 MiB / 35.6% CPU)</sub> | 🥇 **55030**<br><sub>(97 MiB / 35.6% CPU)</sub> | *Not possible* | *Not possible* | *Not possible* | *Not possible* | *Not possible* | *Not possible* | **33713**<br><sub>(129 MiB / 45.6% CPU)</sub> | **33713**<br><sub>(129 MiB / 45.6% CPU)</sub> |
| HTTP/2 · TLS | HTTP/3 · QUIC | 🥇 **26360**<br><sub>(151 MiB / 49.8% CPU)</sub> | 🥇 **26360**<br><sub>(151 MiB / 49.8% CPU)</sub> | *Not possible (no QUIC)* | *Not possible (no QUIC)* | *Not possible (no QUIC)* | *Not possible (no QUIC)* | *Not possible (no QUIC)* | *Not possible (no QUIC)* | **22661**<br><sub>(154 MiB / 47.2% CPU)</sub> | **22661**<br><sub>(154 MiB / 47.2% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **20591**<br><sub>(145 MiB / 50.3% CPU)</sub> | 🥇 **20591**<br><sub>(145 MiB / 50.3% CPU)</sub> | **0**<br><sub>(105 MiB / 22.5% CPU)</sub> | **15091**<br><sub>(105 MiB / 22.5% CPU)</sub> | *Not measured* | *Not measured* | *Not measured* | *Not measured* | **18663**<br><sub>(182 MiB / 50.6% CPU)</sub> | **18663**<br><sub>(182 MiB / 50.6% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | 🥇 **16216**<br><sub>(158 MiB / 48% CPU)</sub> | 🥇 **16216**<br><sub>(158 MiB / 48% CPU)</sub> | **0**<br><sub>(113 MiB / 23.2% CPU)</sub> | **11616**<br><sub>(113 MiB / 23.2% CPU)</sub> | *Not measured* | *Not measured* | *Not measured* | *Not measured* | **15332**<br><sub>(194 MiB / 51.1% CPU)</sub> | **15332**<br><sub>(194 MiB / 51.1% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/2 · plain | 🥇 **29330**<br><sub>(154 MiB / 53.2% CPU)</sub> | 🥇 **29330**<br><sub>(154 MiB / 53.2% CPU)</sub> | *Not possible (no H3 to H2)* | *Not possible (no H3 to H2)* | *Not possible (no H3 to H2)* | *Not possible (no H3 to H2)* | *Not possible (no H3 to H2)* | *Not possible (no H3 to H2)* | **24196**<br><sub>(194 MiB / 48.8% CPU)</sub> | **24196**<br><sub>(194 MiB / 48.8% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | 🥇 **25134**<br><sub>(156 MiB / 50.3% CPU)</sub> | 🥇 **25134**<br><sub>(156 MiB / 50.3% CPU)</sub> | *Not possible (no H3 to H2)* | *Not possible (no H3 to H2)* | *Not possible (no H3 to H2)* | *Not possible (no H3 to H2)* | *Not possible (no H3 to H2)* | *Not possible (no H3 to H2)* | **21501**<br><sub>(192 MiB / 47.6% CPU)</sub> | **21501**<br><sub>(192 MiB / 47.6% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/3 · QUIC | 🥇 **20462**<br><sub>(151 MiB / 47.2% CPU)</sub> | 🥇 **20462**<br><sub>(151 MiB / 47.2% CPU)</sub> | *Not possible (no QUIC)* | *Not possible (no QUIC)* | *Not possible (no QUIC)* | *Not possible (no QUIC)* | *Not possible (no QUIC)* | *Not possible (no QUIC)* | **15995**<br><sub>(200 MiB / 48.2% CPU)</sub> | **15995**<br><sub>(200 MiB / 48.2% CPU)</sub> |

### MITM (TWP only)

Same Client×Origin wires with interception on (`compare-product` [34355136373](https://github.com/justcoding121/titanium-web-proxy/actions/runs/34355136373)). **Lite** = no-op handlers (unchanged-lite finish). **Full** = append-only header mutation (harness: one probe header each way; product: generic append-only relay via `MitmCompressedRelayHelper`). nginx/HAProxy/Envoy/YARP cannot MITM. **Lite÷Reverse** / **Full÷Reverse** vs bare reverse (same job). Completion gate: Lite ≥ **0.50×** and Full ≥ **0.50×** reverse sustain @ c=64 (median of 3 GHA runs); reverse TWP÷YARP ≥ **0.75×** (no terminate-peer gate).

**v1 append-only relay (2026-08-27):** Pre-fix H2→H2 Full÷Reverse was **0.13–0.16×** ([32960766249](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32960766249)). Post-fix @ `df172718`: H2 plain→H2 plain Full **0.77–0.79×**, H3→H1 Full **0.91–0.93×**, all MITM arms ≥ **0.70×** on median of [33041445371](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33041445371), [33055267086](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33055267086), [33055272140](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33055272140).

**v2 drop-only + non-unique append (2026-08-27):** `MitmStaticRebuildHelper` rebuilds static HPACK/QPACK after 1–4 unique header drops; trailing non-unique appends stay on compressed relay. @ `84b225f7`: all MITM arms ≥ **0.70×** on GHA median ([33087088466](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087088466), [33087091622](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087091622), [33105885748](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33105885748) Linux; [33087085235](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087085235), [33087088466](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087088466), [33087091622](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087091622) Windows). H2 plain→H2 plain Full **0.77–0.79×** (Win) / **0.78×** (Lin).

| Client | Origin | Lite sustain | Full sustain | Lite÷Reverse | Full÷Reverse |
|---|---|---:|---:|---:|---:|
| HTTP/1 · plain | HTTP/1 · plain | **31069**<br><sub>(86 MiB / 50.9% CPU)</sub> | **29689**<br><sub>(86 MiB / 50.4% CPU)</sub> | **0.95×** | **0.91×** |
| HTTP/1 · plain | HTTP/1 · TLS | **23954**<br><sub>(110 MiB / 51.7% CPU)</sub> | **23460**<br><sub>(111 MiB / 51.1% CPU)</sub> | **0.99×** | **0.97×** |
| HTTP/1 · plain | HTTP/2 · plain | **40096**<br><sub>(129 MiB / 52.2% CPU)</sub> | **38521**<br><sub>(125 MiB / 53.2% CPU)</sub> | **0.99×** | **0.95×** |
| HTTP/1 · plain | HTTP/2 · TLS | **31783**<br><sub>(142 MiB / 49.4% CPU)</sub> | **31668**<br><sub>(146 MiB / 50.8% CPU)</sub> | **0.97×** | **0.97×** |
| HTTP/1 · plain | HTTP/3 · QUIC | **23694**<br><sub>(141 MiB / 53.9% CPU)</sub> | **23040**<br><sub>(132 MiB / 54.5% CPU)</sub> | **0.99×** | **0.97×** |
| HTTP/1 · TLS | HTTP/1 · plain | **23066**<br><sub>(115 MiB / 51.1% CPU)</sub> | **22531**<br><sub>(114 MiB / 50.8% CPU)</sub> | **1×** | **0.97×** |
| HTTP/1 · TLS | HTTP/1 · TLS | **19653**<br><sub>(115 MiB / 49.4% CPU)</sub> | **19272**<br><sub>(115 MiB / 49.8% CPU)</sub> | **1×** | **0.98×** |
| HTTP/1 · TLS | HTTP/2 · plain | **27120**<br><sub>(144 MiB / 50.6% CPU)</sub> | **26816**<br><sub>(145 MiB / 50.9% CPU)</sub> | **0.97×** | **0.96×** |
| HTTP/1 · TLS | HTTP/2 · TLS | **23832**<br><sub>(172 MiB / 48.8% CPU)</sub> | **23310**<br><sub>(166 MiB / 49% CPU)</sub> | **0.98×** | **0.96×** |
| HTTP/1 · TLS | HTTP/3 · QUIC | **18298**<br><sub>(154 MiB / 53.6% CPU)</sub> | **17484**<br><sub>(145 MiB / 53.4% CPU)</sub> | **0.99×** | **0.95×** |
| HTTP/2 · plain | HTTP/1 · plain | **35680**<br><sub>(120 MiB / 55.3% CPU)</sub> | **34876**<br><sub>(118 MiB / 54.6% CPU)</sub> | **0.95×** | **0.93×** |
| HTTP/2 · plain | HTTP/1 · TLS | **27481**<br><sub>(121 MiB / 52.8% CPU)</sub> | **26631**<br><sub>(122 MiB / 52.4% CPU)</sub> | **0.99×** | **0.96×** |
| HTTP/2 · plain | HTTP/2 · plain | **63316**<br><sub>(94 MiB / 41.1% CPU)</sub> | **59610**<br><sub>(95 MiB / 41.3% CPU)</sub> | **0.72×** | **0.68×** |
| HTTP/2 · plain | HTTP/2 · TLS | **49517**<br><sub>(102 MiB / 40.3% CPU)</sub> | **47032**<br><sub>(103 MiB / 39.3% CPU)</sub> | **0.84×** | **0.8×** |
| HTTP/2 · plain | HTTP/3 · QUIC | **28010**<br><sub>(143 MiB / 51.2% CPU)</sub> | **27712**<br><sub>(154 MiB / 51% CPU)</sub> | **0.99×** | **0.98×** |
| HTTP/2 · TLS | HTTP/1 · plain | **32976**<br><sub>(118 MiB / 53.2% CPU)</sub> | **33281**<br><sub>(127 MiB / 53.9% CPU)</sub> | **0.96×** | **0.97×** |
| HTTP/2 · TLS | HTTP/1 · TLS | **26015**<br><sub>(121 MiB / 51.4% CPU)</sub> | **25321**<br><sub>(123 MiB / 51.8% CPU)</sub> | **0.98×** | **0.95×** |
| HTTP/2 · TLS | HTTP/2 · plain | **57548**<br><sub>(109 MiB / 41.5% CPU)</sub> | **53846**<br><sub>(110 MiB / 40.8% CPU)</sub> | **0.76×** | **0.71×** |
| HTTP/2 · TLS | HTTP/2 · TLS | **46295**<br><sub>(106 MiB / 39.7% CPU)</sub> | **43960**<br><sub>(112 MiB / 39.9% CPU)</sub> | **0.84×** | **0.8×** |
| HTTP/2 · TLS | HTTP/3 · QUIC | **26239**<br><sub>(147 MiB / 50.6% CPU)</sub> | **26112**<br><sub>(151 MiB / 51.2% CPU)</sub> | **1×** | **0.99×** |
| HTTP/3 · QUIC | HTTP/1 · plain | **19986**<br><sub>(142 MiB / 51.2% CPU)</sub> | **19542**<br><sub>(145 MiB / 50.4% CPU)</sub> | **0.97×** | **0.95×** |
| HTTP/3 · QUIC | HTTP/1 · TLS | **16039**<br><sub>(164 MiB / 48.7% CPU)</sub> | **15437**<br><sub>(148 MiB / 48.7% CPU)</sub> | **0.99×** | **0.95×** |
| HTTP/3 · QUIC | HTTP/2 · plain | **28091**<br><sub>(161 MiB / 55% CPU)</sub> | **27167**<br><sub>(162 MiB / 54.1% CPU)</sub> | **0.96×** | **0.93×** |
| HTTP/3 · QUIC | HTTP/2 · TLS | **23686**<br><sub>(158 MiB / 51.8% CPU)</sub> | **22948**<br><sub>(163 MiB / 51.6% CPU)</sub> | **0.94×** | **0.91×** |
| HTTP/3 · QUIC | HTTP/3 · QUIC | **19537**<br><sub>(156 MiB / 49.6% CPU)</sub> | **19191**<br><sub>(153 MiB / 50% CPU)</sub> | **0.95×** | **0.94×** |

## macOS — Titanium vs nginx vs HAProxy vs Envoy vs YARP

Numbers are filled by `tools/RpsLoadProbe/apply-wiki-paste.ps1` after `compare-product` on `macos-15-intel` (placeholder headers match the Linux table shape).

### Reverse

Median of **3 repeats** on `macos-15-intel` (4-core / 14 GB). Bare reverse 5×5 @ `84b225f7` — `compare-product` [34355136373](https://github.com/justcoding121/titanium-web-proxy/actions/runs/34355136373). Warmup 2s / measure 8s; concurrency 8, 16, 32, 64. Prefer TWP÷peer ratios over absolute RPS. **RPS cells** include median RSS / CPU at the peak-RPS step as `<br><sub>(MiB / CPU%)</sub>`. The RPS workflow installs Homebrew nginx (`http_v3_module`), Homebrew `libmsquic` (+ `DYLD_*`), and YARP. HAProxy/Envoy are not published on macOS. Do not publish from `macos-latest` (3-core / 7 GB).

![macOS reverse](images/rps-product-reverse-macos.png)

| Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | HAProxy sustain | HAProxy peak | Envoy sustain | Envoy peak | YARP sustain | YARP peak |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| HTTP/1 · plain | HTTP/1 · plain | 🥇 **14478**<br><sub>(83 MiB / 30.4% CPU)</sub> | 🥇 **14478**<br><sub>(83 MiB / 30.4% CPU)</sub> | **10550**<br><sub>(50 MiB / 16.7% CPU)</sub> | **10550**<br><sub>(50 MiB / 16.7% CPU)</sub> | *Not measured* | *Not measured* | *Not measured* | *Not measured* | **12414**<br><sub>(103 MiB / 36.5% CPU)</sub> | **12414**<br><sub>(103 MiB / 36.5% CPU)</sub> |
| HTTP/1 · plain | HTTP/1 · TLS | 🥇 **13700**<br><sub>(93 MiB / 35.7% CPU)</sub> | 🥇 **13700**<br><sub>(93 MiB / 35.7% CPU)</sub> | **6014**<br><sub>(71 MiB / 14.9% CPU)</sub> | **6014**<br><sub>(71 MiB / 14.9% CPU)</sub> | *Not measured* | *Not measured* | *Not measured* | *Not measured* | **11438**<br><sub>(124 MiB / 36% CPU)</sub> | **11438**<br><sub>(124 MiB / 36% CPU)</sub> |
| HTTP/1 · plain | HTTP/2 · plain | **13919**<br><sub>(90 MiB / 35.7% CPU)</sub> | **13919**<br><sub>(90 MiB / 35.7% CPU)</sub> | *Not possible* | *Not possible* | *Not possible* | *Not possible* | *Not possible* | *Not possible* | 🥇 **16104**<br><sub>(112 MiB / 31.9% CPU)</sub> | 🥇 **16104**<br><sub>(112 MiB / 31.9% CPU)</sub> |
| HTTP/1 · plain | HTTP/2 · TLS | **11700**<br><sub>(128 MiB / 35.6% CPU)</sub> | **11700**<br><sub>(128 MiB / 35.6% CPU)</sub> | *Not possible* | *Not possible* | *Not possible* | *Not possible* | *Not possible* | *Not possible* | 🥇 **13487**<br><sub>(115 MiB / 32.1% CPU)</sub> | 🥇 **13487**<br><sub>(115 MiB / 32.1% CPU)</sub> |
| HTTP/1 · plain | HTTP/3 · QUIC | **4115**<br><sub>(89 MiB / 37.8% CPU)</sub> | **4115**<br><sub>(89 MiB / 37.8% CPU)</sub> | *Not possible (no QUIC)* | *Not possible (no QUIC)* | *Not possible (no QUIC)* | *Not possible (no QUIC)* | *Not possible (no QUIC)* | *Not possible (no QUIC)* | 🥇 **5811**<br><sub>(114 MiB / 35.8% CPU)</sub> | 🥇 **5811**<br><sub>(114 MiB / 35.8% CPU)</sub> |
| HTTP/1 · TLS | HTTP/1 · plain | **7678**<br><sub>(94 MiB / 29.8% CPU)</sub> | **7678**<br><sub>(94 MiB / 29.8% CPU)</sub> | **5877**<br><sub>(71 MiB / 14.1% CPU)</sub> | **5877**<br><sub>(71 MiB / 14.1% CPU)</sub> | *Not measured* | *Not measured* | *Not measured* | *Not measured* | 🥇 **11015**<br><sub>(117 MiB / 35% CPU)</sub> | 🥇 **11015**<br><sub>(117 MiB / 35% CPU)</sub> |
| HTTP/1 · TLS | HTTP/1 · TLS | 🥇 **9657**<br><sub>(98 MiB / 33.5% CPU)</sub> | 🥇 **9657**<br><sub>(98 MiB / 33.5% CPU)</sub> | **6106**<br><sub>(80 MiB / 20% CPU)</sub> | **6106**<br><sub>(80 MiB / 20% CPU)</sub> | *Not measured* | *Not measured* | *Not measured* | *Not measured* | **9081**<br><sub>(119 MiB / 33.3% CPU)</sub> | **9081**<br><sub>(119 MiB / 33.3% CPU)</sub> |
| HTTP/1 · TLS | HTTP/2 · plain | 🥇 **13563**<br><sub>(96 MiB / 35.6% CPU)</sub> | 🥇 **13563**<br><sub>(96 MiB / 35.6% CPU)</sub> | *Not possible* | *Not possible* | *Not possible* | *Not possible* | *Not possible* | *Not possible* | **13036**<br><sub>(115 MiB / 30.8% CPU)</sub> | **13036**<br><sub>(115 MiB / 30.8% CPU)</sub> |
| HTTP/1 · TLS | HTTP/2 · TLS | **10792**<br><sub>(166 MiB / 33% CPU)</sub> | **10792**<br><sub>(166 MiB / 33% CPU)</sub> | *Not possible* | *Not possible* | *Not possible* | *Not possible* | *Not possible* | *Not possible* | 🥇 **12303**<br><sub>(118 MiB / 29.9% CPU)</sub> | 🥇 **12303**<br><sub>(118 MiB / 29.9% CPU)</sub> |
| HTTP/1 · TLS | HTTP/3 · QUIC | **3572**<br><sub>(106 MiB / 47.1% CPU)</sub> | **3572**<br><sub>(106 MiB / 47.1% CPU)</sub> | *Not possible (no QUIC)* | *Not possible (no QUIC)* | *Not possible (no QUIC)* | *Not possible (no QUIC)* | *Not possible (no QUIC)* | *Not possible (no QUIC)* | 🥇 **5128**<br><sub>(119 MiB / 32.2% CPU)</sub> | 🥇 **5128**<br><sub>(119 MiB / 32.2% CPU)</sub> |
| HTTP/2 · plain | HTTP/1 · plain | 🥇 **19995**<br><sub>(88 MiB / 36.9% CPU)</sub> | 🥇 **19995**<br><sub>(88 MiB / 36.9% CPU)</sub> | *Not possible* | *Not possible* | *Not possible* | *Not possible* | *Not possible* | *Not possible* | **18482**<br><sub>(104 MiB / 38.6% CPU)</sub> | **18482**<br><sub>(104 MiB / 38.6% CPU)</sub> |
| HTTP/2 · plain | HTTP/1 · TLS | 🥇 **16104**<br><sub>(96 MiB / 39.9% CPU)</sub> | 🥇 **16104**<br><sub>(96 MiB / 39.9% CPU)</sub> | *Not possible* | *Not possible* | *Not possible* | *Not possible* | *Not possible* | *Not possible* | **14089**<br><sub>(118 MiB / 39.5% CPU)</sub> | **14089**<br><sub>(118 MiB / 39.5% CPU)</sub> |
| HTTP/2 · plain | HTTP/2 · plain | 🥇 **41873**<br><sub>(73 MiB / 28.5% CPU)</sub> | 🥇 **41873**<br><sub>(73 MiB / 28.5% CPU)</sub> | *Not possible* | *Not possible* | *Not possible* | *Not possible* | *Not possible* | *Not possible* | **27049**<br><sub>(111 MiB / 34.2% CPU)</sub> | **27049**<br><sub>(111 MiB / 34.2% CPU)</sub> |
| HTTP/2 · plain | HTTP/2 · TLS | 🥇 **33498**<br><sub>(76 MiB / 26.8% CPU)</sub> | 🥇 **33498**<br><sub>(76 MiB / 26.8% CPU)</sub> | *Not possible* | *Not possible* | *Not possible* | *Not possible* | *Not possible* | *Not possible* | **17026**<br><sub>(112 MiB / 35.1% CPU)</sub> | **17026**<br><sub>(112 MiB / 35.1% CPU)</sub> |
| HTTP/2 · plain | HTTP/3 · QUIC | **4511**<br><sub>(93 MiB / 43.8% CPU)</sub> | **4511**<br><sub>(93 MiB / 43.8% CPU)</sub> | *Not possible (no QUIC)* | *Not possible (no QUIC)* | *Not possible (no QUIC)* | *Not possible (no QUIC)* | *Not possible (no QUIC)* | *Not possible (no QUIC)* | 🥇 **5514**<br><sub>(117 MiB / 34.1% CPU)</sub> | 🥇 **5514**<br><sub>(117 MiB / 34.1% CPU)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **14218**<br><sub>(93 MiB / 39.1% CPU)</sub> | 🥇 **14218**<br><sub>(93 MiB / 39.1% CPU)</sub> | **9156**<br><sub>(71 MiB / 13.5% CPU)</sub> | **9156**<br><sub>(71 MiB / 13.5% CPU)</sub> | *Not measured* | *Not measured* | *Not measured* | *Not measured* | **12587**<br><sub>(112 MiB / 35.7% CPU)</sub> | **12587**<br><sub>(112 MiB / 35.7% CPU)</sub> |
| HTTP/2 · TLS | HTTP/1 · TLS | 🥇 **12930**<br><sub>(97 MiB / 40.2% CPU)</sub> | 🥇 **12930**<br><sub>(97 MiB / 40.2% CPU)</sub> | **7276**<br><sub>(91 MiB / 14% CPU)</sub> | **7276**<br><sub>(91 MiB / 14% CPU)</sub> | *Not measured* | *Not measured* | *Not measured* | *Not measured* | **9599**<br><sub>(126 MiB / 37.7% CPU)</sub> | **9599**<br><sub>(126 MiB / 37.7% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · plain | 🥇 **27986**<br><sub>(83 MiB / 25.4% CPU)</sub> | 🥇 **27986**<br><sub>(83 MiB / 25.4% CPU)</sub> | *Not possible* | *Not possible* | *Not possible* | *Not possible* | *Not possible* | *Not possible* | **17430**<br><sub>(117 MiB / 34.7% CPU)</sub> | **17430**<br><sub>(117 MiB / 34.7% CPU)</sub> |
| HTTP/2 · TLS | HTTP/2 · TLS | 🥇 **27877**<br><sub>(84 MiB / 28% CPU)</sub> | 🥇 **27877**<br><sub>(84 MiB / 28% CPU)</sub> | *Not possible* | *Not possible* | *Not possible* | *Not possible* | *Not possible* | *Not possible* | **17531**<br><sub>(116 MiB / 35.2% CPU)</sub> | **17531**<br><sub>(116 MiB / 35.2% CPU)</sub> |
| HTTP/2 · TLS | HTTP/3 · QUIC | **4398**<br><sub>(106 MiB / 37.2% CPU)</sub> | **4398**<br><sub>(106 MiB / 37.2% CPU)</sub> | *Not possible (no QUIC)* | *Not possible (no QUIC)* | *Not possible (no QUIC)* | *Not possible (no QUIC)* | *Not possible (no QUIC)* | *Not possible (no QUIC)* | 🥇 **4593**<br><sub>(124 MiB / 33.3% CPU)</sub> | 🥇 **4593**<br><sub>(124 MiB / 33.3% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | **3758**<br><sub>(100 MiB / 35.5% CPU)</sub> | **3758**<br><sub>(100 MiB / 35.5% CPU)</sub> | **0**<br><sub>(60 MiB / 9% CPU)</sub> | **4865**<br><sub>(60 MiB / 9% CPU)</sub> | *Not measured* | *Not measured* | *Not measured* | *Not measured* | 🥇 **4861**<br><sub>(185 MiB / 36% CPU)</sub> | 🥇 **4861**<br><sub>(185 MiB / 36% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · TLS | **2842**<br><sub>(119 MiB / 29.7% CPU)</sub> | **2842**<br><sub>(119 MiB / 29.7% CPU)</sub> | **0**<br><sub>(65 MiB / 3.4% CPU)</sub> | **1572**<br><sub>(65 MiB / 3.4% CPU)</sub> | *Not measured* | *Not measured* | *Not measured* | *Not measured* | 🥇 **3779**<br><sub>(208 MiB / 33.4% CPU)</sub> | 🥇 **3779**<br><sub>(208 MiB / 33.4% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/2 · plain | **4523**<br><sub>(96 MiB / 35.4% CPU)</sub> | **4523**<br><sub>(96 MiB / 35.4% CPU)</sub> | *Not possible (no H3 to H2)* | *Not possible (no H3 to H2)* | *Not possible (no H3 to H2)* | *Not possible (no H3 to H2)* | *Not possible (no H3 to H2)* | *Not possible (no H3 to H2)* | 🥇 **5580**<br><sub>(176 MiB / 31.1% CPU)</sub> | 🥇 **5580**<br><sub>(176 MiB / 31.1% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/2 · TLS | **3932**<br><sub>(106 MiB / 34.6% CPU)</sub> | **3932**<br><sub>(106 MiB / 34.6% CPU)</sub> | *Not possible (no H3 to H2)* | *Not possible (no H3 to H2)* | *Not possible (no H3 to H2)* | *Not possible (no H3 to H2)* | *Not possible (no H3 to H2)* | *Not possible (no H3 to H2)* | 🥇 **5020**<br><sub>(185 MiB / 32.1% CPU)</sub> | 🥇 **5020**<br><sub>(185 MiB / 32.1% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/3 · QUIC | **3057**<br><sub>(99 MiB / 39.9% CPU)</sub> | **3057**<br><sub>(99 MiB / 39.9% CPU)</sub> | *Not possible (no QUIC)* | *Not possible (no QUIC)* | *Not possible (no QUIC)* | *Not possible (no QUIC)* | *Not possible (no QUIC)* | *Not possible (no QUIC)* | 🥇 **3612**<br><sub>(180 MiB / 32.4% CPU)</sub> | 🥇 **3612**<br><sub>(180 MiB / 32.4% CPU)</sub> |

### MITM (TWP only)

Same Client×Origin wires with interception on (`compare-product` [34355136373](https://github.com/justcoding121/titanium-web-proxy/actions/runs/34355136373)). **Lite** = no-op handlers (unchanged-lite finish). **Full** = append-only header mutation (harness: one probe header each way; product: generic append-only relay via `MitmCompressedRelayHelper`). nginx/HAProxy/Envoy/YARP cannot MITM. **Lite÷Reverse** / **Full÷Reverse** vs bare reverse (same job). Completion gate: Lite ≥ **0.50×** and Full ≥ **0.50×** reverse sustain @ c=64 (median of 3 GHA runs); reverse TWP÷YARP ≥ **0.75×** (no terminate-peer gate).

**v1 append-only relay (2026-08-27):** Pre-fix H2→H2 Full÷Reverse was **0.13–0.16×** ([32960766249](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32960766249)). Post-fix @ `df172718`: H2 plain→H2 plain Full **0.77–0.79×**, H3→H1 Full **0.91–0.93×**, all MITM arms ≥ **0.70×** on median of [33041445371](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33041445371), [33055267086](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33055267086), [33055272140](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33055272140).

**v2 drop-only + non-unique append (2026-08-27):** `MitmStaticRebuildHelper` rebuilds static HPACK/QPACK after 1–4 unique header drops; trailing non-unique appends stay on compressed relay. @ `84b225f7`: all MITM arms ≥ **0.70×** on GHA median ([33087088466](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087088466), [33087091622](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087091622), [33105885748](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33105885748) Linux; [33087085235](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087085235), [33087088466](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087088466), [33087091622](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087091622) Windows). H2 plain→H2 plain Full **0.77–0.79×** (Win) / **0.78×** (Lin).

| Client | Origin | Lite sustain | Full sustain | Lite÷Reverse | Full÷Reverse |
|---|---|---:|---:|---:|---:|
| HTTP/1 · plain | HTTP/1 · plain | **11551**<br><sub>(84 MiB / 33.4% CPU)</sub> | **10010**<br><sub>(84 MiB / 30.5% CPU)</sub> | **0.8×** | **0.69×** |
| HTTP/1 · plain | HTTP/1 · TLS | **7962**<br><sub>(93 MiB / 33.8% CPU)</sub> | **8731**<br><sub>(95 MiB / 35.2% CPU)</sub> | **0.58×** | **0.64×** |
| HTTP/1 · plain | HTTP/2 · plain | **12047**<br><sub>(89 MiB / 35.7% CPU)</sub> | **11489**<br><sub>(90 MiB / 35.6% CPU)</sub> | **0.87×** | **0.83×** |
| HTTP/1 · plain | HTTP/2 · TLS | **9733**<br><sub>(103 MiB / 35% CPU)</sub> | **9788**<br><sub>(113 MiB / 34.8% CPU)</sub> | **0.83×** | **0.84×** |
| HTTP/1 · plain | HTTP/3 · QUIC | **3650**<br><sub>(90 MiB / 39.2% CPU)</sub> | **3366**<br><sub>(90 MiB / 41% CPU)</sub> | **0.89×** | **0.82×** |
| HTTP/1 · TLS | HTTP/1 · plain | **6968**<br><sub>(95 MiB / 31.4% CPU)</sub> | **6771**<br><sub>(94 MiB / 31.8% CPU)</sub> | **0.91×** | **0.88×** |
| HTTP/1 · TLS | HTTP/1 · TLS | **6188**<br><sub>(98 MiB / 30.9% CPU)</sub> | **5395**<br><sub>(98 MiB / 31.4% CPU)</sub> | **0.64×** | **0.56×** |
| HTTP/1 · TLS | HTTP/2 · plain | **10565**<br><sub>(100 MiB / 34.4% CPU)</sub> | **7140**<br><sub>(123 MiB / 31.8% CPU)</sub> | **0.78×** | **0.53×** |
| HTTP/1 · TLS | HTTP/2 · TLS | **8064**<br><sub>(142 MiB / 33.3% CPU)</sub> | **6232**<br><sub>(149 MiB / 30.9% CPU)</sub> | **0.75×** | **0.58×** |
| HTTP/1 · TLS | HTTP/3 · QUIC | **3399**<br><sub>(103 MiB / 44.7% CPU)</sub> | **2361**<br><sub>(103 MiB / 42.2% CPU)</sub> | **0.95×** | **0.66×** |
| HTTP/2 · plain | HTTP/1 · plain | **18121**<br><sub>(90 MiB / 36.8% CPU)</sub> | **12319**<br><sub>(90 MiB / 36.1% CPU)</sub> | **0.91×** | **0.62×** |
| HTTP/2 · plain | HTTP/1 · TLS | **11021**<br><sub>(97 MiB / 39.1% CPU)</sub> | **10356**<br><sub>(97 MiB / 39.1% CPU)</sub> | **0.68×** | **0.64×** |
| HTTP/2 · plain | HTTP/2 · plain | **23305**<br><sub>(75 MiB / 27.9% CPU)</sub> | **26794**<br><sub>(76 MiB / 28.6% CPU)</sub> | **0.56×** | **0.64×** |
| HTTP/2 · plain | HTTP/2 · TLS | **23391**<br><sub>(81 MiB / 30.9% CPU)</sub> | **20102**<br><sub>(80 MiB / 31.1% CPU)</sub> | **0.7×** | **0.6×** |
| HTTP/2 · plain | HTTP/3 · QUIC | **4355**<br><sub>(94 MiB / 45.1% CPU)</sub> | **3978**<br><sub>(94 MiB / 44.1% CPU)</sub> | **0.97×** | **0.88×** |
| HTTP/2 · TLS | HTTP/1 · plain | **13960**<br><sub>(93 MiB / 40.6% CPU)</sub> | **11494**<br><sub>(93 MiB / 38.2% CPU)</sub> | **0.98×** | **0.81×** |
| HTTP/2 · TLS | HTTP/1 · TLS | **10042**<br><sub>(95 MiB / 38.5% CPU)</sub> | **9315**<br><sub>(99 MiB / 38.4% CPU)</sub> | **0.78×** | **0.72×** |
| HTTP/2 · TLS | HTTP/2 · plain | **26966**<br><sub>(87 MiB / 29.9% CPU)</sub> | **24551**<br><sub>(88 MiB / 31.6% CPU)</sub> | **0.96×** | **0.88×** |
| HTTP/2 · TLS | HTTP/2 · TLS | **23643**<br><sub>(86 MiB / 31.3% CPU)</sub> | **20835**<br><sub>(87 MiB / 30.8% CPU)</sub> | **0.85×** | **0.75×** |
| HTTP/2 · TLS | HTTP/3 · QUIC | **4434**<br><sub>(109 MiB / 40.3% CPU)</sub> | **4820**<br><sub>(106 MiB / 41.9% CPU)</sub> | **1.01×** | **1.1×** |
| HTTP/3 · QUIC | HTTP/1 · plain | **4184**<br><sub>(101 MiB / 39.5% CPU)</sub> | **3683**<br><sub>(102 MiB / 37.9% CPU)</sub> | **1.11×** | **0.98×** |
| HTTP/3 · QUIC | HTTP/1 · TLS | **3767**<br><sub>(115 MiB / 38.4% CPU)</sub> | **3587**<br><sub>(115 MiB / 36.5% CPU)</sub> | **1.33×** | **1.26×** |
| HTTP/3 · QUIC | HTTP/2 · plain | **5009**<br><sub>(98 MiB / 35.9% CPU)</sub> | **4245**<br><sub>(97 MiB / 39.4% CPU)</sub> | **1.11×** | **0.94×** |
| HTTP/3 · QUIC | HTTP/2 · TLS | **4944**<br><sub>(101 MiB / 39.7% CPU)</sub> | **3887**<br><sub>(106 MiB / 40.4% CPU)</sub> | **1.26×** | **0.99×** |
| HTTP/3 · QUIC | HTTP/3 · QUIC | **3059**<br><sub>(93 MiB / 35.6% CPU)</sub> | **2631**<br><sub>(94 MiB / 35.6% CPU)</sub> | **1×** | **0.86×** |

## Editions (CLI / Plus / Intercept)

**Note:** `twp-reverse-http1` and other library rows use Core with **probe-tuned** settings (no logging, no Via header, probe-warmed certs). Edition rows use `titanium run -c twp.yaml` **product defaults** — prefer the ÷baseline ratio column over absolute RPS. Inspector GUI is not spawnable in the harness; session-path overhead is `twp-cli-intercept-http1` (route `RequestHeaderSet` transform).

Gates prioritize **SLO survival under load** and **reasonable overhead vs baseline**. Pre-origin Plus middleware (CIDR/WAF/JWT/rate-limit) runs on H1 terminate-lite without `SessionEventArgs`; JWT caches successful bearer validations. Thresholds — see [PERF-GATES.md](https://github.com/justcoding121/titanium-web-proxy/blob/develop/tools/RpsLoadProbe/PERF-GATES.md).

Median of **3** repeats @ `6d2a7c9d`. Source: Actions [33259699099](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33259699099) (`compare-editions`). Warmup 2s / measure 8s; concurrency 8–64; sustain = median peak RPS among SLO-pass steps @ **c=64**. **RPS cells** include `(MiB / CPU%)` at that step.

```powershell
pwsh tools/RpsLoadProbe/run-rps.ps1 -Mode compare-editions
pwsh tools/RpsLoadProbe/validate-edition-gates.ps1 -CsvPath tools/RpsLoadProbe/results/rps-ramp-*.csv
```

| Arm | Win sustain | Win peak | Linux sustain | Linux peak | Win÷ | Lin÷ | Gate |
|---|---:|---:|---:|---:|---:|---:|---:|
| `twp-cli-reverse-http1` vs library | **32214**<br><sub>(118 MiB / 43.4% CPU)</sub> | **32219** | **47734**<br><sub>(151 MiB / 49.0% CPU)</sub> | **47844** | **1.02×** | **1.04×** | ≥ **0.80×** |
| `twp-cli-reverse-http1-tls` vs library TLS | **26495**<br><sub>(138 MiB / 48.3% CPU)</sub> | **26651** | **36932**<br><sub>(174 MiB / 48.5% CPU)</sub> | **37200** | **1.01×** | **1.00×** | ≥ **0.80×** |
| `twp-cli-reverse-http1-route` vs CLI | **32394**<br><sub>(120 MiB / 47.7% CPU)</sub> | **32428** | **47369**<br><sub>(149 MiB / 49.2% CPU)</sub> | **47398** | **1.01×** | **0.99×** | ≥ **0.90×** |
| `twp-cli-plus-base-http1` vs CLI | **32475**<br><sub>(123 MiB / 46.0% CPU)</sub> | **32695** | **47322**<br><sub>(154 MiB / 49.8% CPU)</sub> | **47744** | **1.01×** | **0.99×** | ≥ **0.90×** |
| `twp-cli-plus-cache-http1` (cold) vs CLI | **34029**<br><sub>(118 MiB / 64.5% CPU)</sub> | **34460** | **49409**<br><sub>(148 MiB / 62.3% CPU)</sub> | **50104** | **1.06×** | **1.04×** | ≥ **0.70×** |
| `twp-cli-intercept-http1` vs CLI | **25219**<br><sub>(124 MiB / 54.0% CPU)</sub> | **25377** | **35969**<br><sub>(155 MiB / 51.2% CPU)</sub> | **35977** | **0.78×** | **0.75×** | ≥ **0.70×** |
| `twp-cli-plus-waf-http1` vs CLI | **31653**<br><sub>(124 MiB / 46.8% CPU)</sub> | **31837** | **46367**<br><sub>(151 MiB / 49.6% CPU)</sub> | **46685** | **0.98×** | **0.97×** | ≥ **0.80×** |
| `twp-cli-plus-cidr-http1` vs CLI | **31730**<br><sub>(123 MiB / 51.1% CPU)</sub> | **31757** | **46828**<br><sub>(150 MiB / 50.2% CPU)</sub> | **47093** | **0.98×** | **0.98×** | ≥ **0.80×** |
| `twp-cli-plus-jwt-http1` vs CLI | **30326**<br><sub>(138 MiB / 46.6% CPU)</sub> | **30386** | **43906**<br><sub>(179 MiB / 49.5% CPU)</sub> | **44433** | **0.94×** | **0.92×** | ≥ **0.70×** |
| `twp-cli-plus-ratelimit-http1` vs CLI | **31713**<br><sub>(123 MiB / 48.8% CPU)</sub> | **31944** | **46435**<br><sub>(151 MiB / 49.4% CPU)</sub> | **46860** | **0.98×** | **0.97×** | ≥ **0.80×** |
| `twp-cli-plus-resilience-http1` vs CLI | **32373**<br><sub>(128 MiB / 50.5% CPU)</sub> | **32416** | **47302**<br><sub>(155 MiB / 49.5% CPU)</sub> | **47654** | **1.00×** | **0.99×** | ≥ **0.85×** |
| `twp-cli-plus-discovery-file-http1` vs CLI | **32209**<br><sub>(121 MiB / 46.5% CPU)</sub> | **32332** | **47485**<br><sub>(151 MiB / 49.1% CPU)</sub> | **47770** | **1.00×** | **0.99×** | ≥ **0.80×** |
| `twp-cli-plus-metrics-scrape-http1` vs CLI | **32192**<br><sub>(126 MiB / 44.8% CPU)</sub> | **32427** | **47348**<br><sub>(159 MiB / 49.5% CPU)</sub> | **48665** | **1.00×** | **0.99×** | ≥ **0.80×** |
| `twp-cli-plus-cache-hit-http1` vs cache cold | **34015**<br><sub>(117 MiB / 66.0% CPU)</sub> | **34022** | **49153**<br><sub>(149 MiB / 63.1% CPU)</sub> | **49358** | **1.00×** | **0.99×** | ≥ **0.90×** |
| `twp-cli-static-http1` vs CLI | **53078**<br><sub>(109 MiB / 52.1% CPU)</sub> | **53980** | **78333**<br><sub>(150 MiB / 48.4% CPU)</sub> | **82955** | **1.65×** | **1.64×** | ≥ **0.85×** |
| `twp-cli-logging-http1` vs CLI | **32440**<br><sub>(119 MiB / 47.4% CPU)</sub> | **32581** | **47624**<br><sub>(150 MiB / 49.3% CPU)</sub> | **48604** | **1.01×** | **1.00×** | ≥ **0.90×** |
| `twp-cli-lb-leasttime-http1` vs route | **29649**<br><sub>(134 MiB / 54.7% CPU)</sub> | **30386** | **44790**<br><sub>(180 MiB / 50.6% CPU)</sub> | **44825** | **0.92×** | **0.95×** | ≥ **0.85×** |
| `twp-cli-dialect-twp-http1` vs CLI | **32461**<br><sub>(114 MiB / 49.7% CPU)</sub> | **32780** | **47898**<br><sub>(145 MiB / 49.3% CPU)</sub> | **48154** | **1.01×** | **1.00×** | ≥ **0.90×** |

`validate-edition-gates.ps1` **passed** on both OS for this run. Library baselines @ c=64 (same job): Win H1 **31561** / TLS **26128**; Linux H1 **45818** / TLS **36869**. Laptop smoke ratios stay on [Performance Local Lab — Editions](Performance-Local-Lab#editions-cli--plus-stress).

## Cross-version (7.0 vs 6.0)

Gate 2 prerequisite before the `v7.0.0` tag: run `compare-cross-version` (reverse matrix, **routes unset**) on `develop` and compare against committed 6.0 baselines from GHA [33087088466](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087088466) (`tools/RpsLoadProbe/results/baseline-6.0-win.csv` / `baseline-6.0-linux.csv`).

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

Median of **3** @ `0ef6d4dd`. Source: Actions [33270571908](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33270571908). Sustain RPS @ **c=64** (SLO-pass median). Absolute 7.0÷6.0 ≈ **0.96–1.00×** on same-protocol arms; peer-norm ≈ **0.90–1.03×**.

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

`validate-cross-version.ps1` **passed** on both OS CSVs (RSS floor **1.20**; peer-norm also passes when current TWP÷YARP ≥ **0.90**). MITM arms live in `compare-product` / `compare-mitm`, not this reverse-only matrix.


## Heavier reverse workloads

Separate from the tiny-GET matrix. Same measurement environments. Modes: `compare-bodies`, `compare-post`, `compare-lossy`, `compare-tls-cost`, `compare-arch` in [RpsLoadProbe](https://github.com/justcoding121/titanium-web-proxy/tree/develop/tools/RpsLoadProbe). Dispatch each independently via `workflow_dispatch` (no need to re-run full `compare-product`). Charts: [`render-heavier-charts.py`](https://github.com/justcoding121/titanium-web-proxy/blob/develop/tools/RpsLoadProbe/render-heavier-charts.py) (`rps-heavier-*-{windows,linux}.png`). **PUT with the same body is the same proxy work as POST; DELETE with no body matches GET** — only POST is published. Bodies/POST/lossy stay **half-duplex**. `compare-arch` is the slow-consumer / early-response / duplex set. Laptop numbers are on [Performance Local Lab](Performance-Local-Lab#architecture-sensitive); CI medians go in the tables below. Tables below are pre–5-peer publish; re-paste with `paste-heavier-wiki.py` after the next heavier GHA runs for HAProxy/Envoy columns.

Lossy link = **userspace** shim (not kernel `netem`): TCP gets per-buffer delay + occasional whole-connection stalls (honest HOL for multiplexed H2); UDP gets per-datagram delay + drops (QUIC). `compare-lossy` publishes H1/H2/H3; H3 is where the protocol design is supposed to matter.

### Windows — heavier reverse GET (64 KiB / 256 KiB)

![Windows heavier bodies](images/rps-heavier-bodies-windows.png)

Median of **3** repeats on `windows-latest` @ `84b225f7`. Source: Actions [34355153953](https://github.com/justcoding121/titanium-web-proxy/actions/runs/34355153953) (`compare-bodies`). Warmup 2s / measure 8s. **RPS cells** include `(MiB / CPU%)` footprints.

| Body | Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | HAProxy sustain | HAProxy peak | Envoy sustain | Envoy peak | YARP sustain | YARP peak |
|---|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| 64 KiB | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **12,918**<br><sub>(123 MiB / 45.1% CPU)</sub> | **12,918**<br><sub>(123 MiB / 45.1% CPU)</sub> | **897**<br><sub>(140 MiB / 24.9% CPU)</sub> | **897**<br><sub>(140 MiB / 24.9% CPU)</sub> | *Not possible* | *Not possible* | *Not possible* | *Not possible* | **11,239**<br><sub>(133 MiB / 48.6% CPU)</sub> | **11,239**<br><sub>(133 MiB / 48.6% CPU)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **11,029**<br><sub>(173 MiB / 49.0% CPU)</sub> | **11,029**<br><sub>(173 MiB / 49.0% CPU)</sub> | **805**<br><sub>(140 MiB / 24.9% CPU)</sub> | **805**<br><sub>(140 MiB / 24.9% CPU)</sub> | *Not possible* | *Not possible* | *Not possible* | *Not possible* | **9,277**<br><sub>(134 MiB / 49.2% CPU)</sub> | **9,277**<br><sub>(134 MiB / 49.2% CPU)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **6,015**<br><sub>(139 MiB / 39.8% CPU)</sub> | **6,015**<br><sub>(139 MiB / 39.8% CPU)</sub> | *Not possible (no QUIC)* | *Not possible (no QUIC)* | *Not possible* | *Not possible* | *Not possible* | *Not possible* | **5,025**<br><sub>(193 MiB / 48.9% CPU)</sub> | **5,025**<br><sub>(193 MiB / 48.9% CPU)</sub> |
| 256 KiB | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **3,517**<br><sub>(133 MiB / 40.5% CPU)</sub> | **3,517**<br><sub>(133 MiB / 40.5% CPU)</sub> | **238**<br><sub>(141 MiB / 24.9% CPU)</sub> | **238**<br><sub>(141 MiB / 24.9% CPU)</sub> | *Not possible* | *Not possible* | *Not possible* | *Not possible* | **3,351**<br><sub>(130 MiB / 47.9% CPU)</sub> | **3,351**<br><sub>(130 MiB / 47.9% CPU)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **3,273**<br><sub>(148 MiB / 39.7% CPU)</sub> | **3,273**<br><sub>(148 MiB / 39.7% CPU)</sub> | **213**<br><sub>(140 MiB / 24.8% CPU)</sub> | **213**<br><sub>(140 MiB / 24.8% CPU)</sub> | *Not possible* | *Not possible* | *Not possible* | *Not possible* | **2,343**<br><sub>(133 MiB / 42.5% CPU)</sub> | **2,343**<br><sub>(133 MiB / 42.5% CPU)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **1,504**<br><sub>(113 MiB / 40.9% CPU)</sub> | **1,504**<br><sub>(113 MiB / 40.9% CPU)</sub> | *Not possible (no QUIC)* | *Not possible (no QUIC)* | *Not possible* | *Not possible* | *Not possible* | *Not possible* | **1,366**<br><sub>(177 MiB / 46.7% CPU)</sub> | **1,366**<br><sub>(177 MiB / 46.7% CPU)</sub> |

nginx/Windows collapses on large reverse bodies in this harness; treat as same-OS only. H1 TLS **64 KiB** ≈ **1.11×** YARP; **256 KiB** ≈ **1.23×**. H2→H1 64 KiB ≈ **1.21×**; H3→H1 64 KiB ≈ **1.18×**.

### Linux — heavier reverse GET (64 KiB / 256 KiB)

![Linux heavier bodies](images/rps-heavier-bodies-linux.png)

Median of **3** repeats @ `84b225f7`. Source: Actions [34355153953](https://github.com/justcoding121/titanium-web-proxy/actions/runs/34355153953) (`compare-bodies`). Warmup 2s / measure 8s.

| Body | Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | HAProxy sustain | HAProxy peak | Envoy sustain | Envoy peak | YARP sustain | YARP peak |
|---|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| 64 KiB | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **7,745**<br><sub>(175 MiB / 45.1% CPU)</sub> | **7,745**<br><sub>(175 MiB / 45.1% CPU)</sub> | **5,430**<br><sub>(98 MiB / 51.4% CPU)</sub> | **5,430**<br><sub>(98 MiB / 51.4% CPU)</sub> | *Not measured* | *Not measured* | *Not measured* | *Not measured* | **6,321**<br><sub>(164 MiB / 48.6% CPU)</sub> | **6,321**<br><sub>(164 MiB / 48.6% CPU)</sub> |
| 64 KiB | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **5,731**<br><sub>(236 MiB / 40.2% CPU)</sub> | **5,731**<br><sub>(236 MiB / 40.2% CPU)</sub> | **1,738**<br><sub>(99 MiB / 15.7% CPU)</sub> | **1,738**<br><sub>(99 MiB / 15.7% CPU)</sub> | *Not measured* | *Not measured* | *Not measured* | *Not measured* | **4,888**<br><sub>(162 MiB / 47.8% CPU)</sub> | **4,888**<br><sub>(162 MiB / 47.8% CPU)</sub> |
| 64 KiB | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **5,505**<br><sub>(179 MiB / 43.9% CPU)</sub> | **5,505**<br><sub>(179 MiB / 43.9% CPU)</sub> | **0**<br><sub>(126 MiB / 22.2% CPU)</sub> | **1,686**<br><sub>(126 MiB / 22.2% CPU)</sub> | *Not measured* | *Not measured* | *Not measured* | *Not measured* | **4,256**<br><sub>(227 MiB / 51.5% CPU)</sub> | **4,256**<br><sub>(227 MiB / 51.5% CPU)</sub> |
| 256 KiB | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **2,670**<br><sub>(122 MiB / 37.4% CPU)</sub> | **2,670**<br><sub>(122 MiB / 37.4% CPU)</sub> | **1,712**<br><sub>(98 MiB / 53.1% CPU)</sub> | **1,712**<br><sub>(98 MiB / 53.1% CPU)</sub> | *Not measured* | *Not measured* | *Not measured* | *Not measured* | **2,139**<br><sub>(168 MiB / 45.9% CPU)</sub> | **2,139**<br><sub>(168 MiB / 45.9% CPU)</sub> |
| 256 KiB | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **1,498**<br><sub>(192 MiB / 32.1% CPU)</sub> | **1,498**<br><sub>(192 MiB / 32.1% CPU)</sub> | **568**<br><sub>(98 MiB / 23.9% CPU)</sub> | **568**<br><sub>(98 MiB / 23.9% CPU)</sub> | *Not measured* | *Not measured* | *Not measured* | *Not measured* | **1,317**<br><sub>(159 MiB / 44.6% CPU)</sub> | **1,317**<br><sub>(159 MiB / 44.6% CPU)</sub> |
| 256 KiB | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **1,442**<br><sub>(152 MiB / 44.2% CPU)</sub> | **1,442**<br><sub>(152 MiB / 44.2% CPU)</sub> | **0**<br><sub>(119 MiB / 20.1% CPU)</sub> | **0**<br><sub>(119 MiB / 20.1% CPU)</sub> | *Not measured* | *Not measured* | *Not measured* | *Not measured* | **1,253**<br><sub>(210 MiB / 48.4% CPU)</sub> | **1,253**<br><sub>(210 MiB / 48.4% CPU)</sub> |

On this GHA pass TWP÷YARP H1 TLS ≈ **1.23×** (64 KiB) / **1.28×** (256 KiB); H2→H1 ≈ **1.23×** / **1.16×**; H3→H1 ≈ **1.27×** / **1.13×**. TWP÷nginx H1 TLS ≈ **1.43** / **1.58**. Absolute RPS swings by VM; prefer ratios.

### Windows — POST 64 KiB request + 64 KiB response

![Windows heavier POST](images/rps-heavier-post-windows.png)

Median of **3** repeats on `windows-latest` @ `84b225f7`. Source: Actions [34355158209](https://github.com/justcoding121/titanium-web-proxy/actions/runs/34355158209) (`compare-post`).

| Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | HAProxy sustain | HAProxy peak | Envoy sustain | Envoy peak | YARP sustain | YARP peak |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | 🥇 **9,481**<br><sub>(108 MiB / 40.6% CPU)</sub> | **9,481**<br><sub>(108 MiB / 40.6% CPU)</sub> | **573**<br><sub>(141 MiB / 24.8% CPU)</sub> | **573**<br><sub>(141 MiB / 24.8% CPU)</sub> | *Not possible* | *Not possible* | *Not possible* | *Not possible* | **6,465**<br><sub>(136 MiB / 52.0% CPU)</sub> | **6,465**<br><sub>(136 MiB / 52.0% CPU)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **6,817**<br><sub>(180 MiB / 49.4% CPU)</sub> | **6,817**<br><sub>(180 MiB / 49.4% CPU)</sub> | **587**<br><sub>(142 MiB / 24.8% CPU)</sub> | **587**<br><sub>(142 MiB / 24.8% CPU)</sub> | *Not possible* | *Not possible* | *Not possible* | *Not possible* | **5,523**<br><sub>(135 MiB / 53.4% CPU)</sub> | **5,523**<br><sub>(135 MiB / 53.4% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **3,502**<br><sub>(175 MiB / 44.1% CPU)</sub> | **3,502**<br><sub>(175 MiB / 44.1% CPU)</sub> | *Not possible (no QUIC)* | *Not possible (no QUIC)* | *Not possible* | *Not possible* | *Not possible* | *Not possible* | **3,143**<br><sub>(212 MiB / 51.6% CPU)</sub> | **3,143**<br><sub>(212 MiB / 51.6% CPU)</sub> |

TWP leads H1 POST (~**1.5×** YARP), H2 POST (~**1.2×** YARP), and H3 POST (~**1.1×** YARP).

### Linux — POST 64 KiB request + 64 KiB response

![Linux heavier POST](images/rps-heavier-post-linux.png)

Median of **3** repeats @ `84b225f7`. Source: Actions [34355158209](https://github.com/justcoding121/titanium-web-proxy/actions/runs/34355158209) (`compare-post`).

| Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | HAProxy sustain | HAProxy peak | Envoy sustain | Envoy peak | YARP sustain | YARP peak |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | 🥇 **4,787**<br><sub>(132 MiB / 45.5% CPU)</sub> | **4,787**<br><sub>(132 MiB / 45.5% CPU)</sub> | **3,500**<br><sub>(98 MiB / 47.8% CPU)</sub> | **3,500**<br><sub>(98 MiB / 47.8% CPU)</sub> | *Not measured* | *Not measured* | *Not measured* | *Not measured* | **3,143**<br><sub>(175 MiB / 55.5% CPU)</sub> | **3,143**<br><sub>(175 MiB / 55.5% CPU)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **2,829**<br><sub>(210 MiB / 44.6% CPU)</sub> | **2,829**<br><sub>(210 MiB / 44.6% CPU)</sub> | **1,431**<br><sub>(113 MiB / 23.1% CPU)</sub> | **1,431**<br><sub>(113 MiB / 23.1% CPU)</sub> | *Not measured* | *Not measured* | *Not measured* | *Not measured* | **2,524**<br><sub>(169 MiB / 47.6% CPU)</sub> | **2,524**<br><sub>(169 MiB / 47.6% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **2,907**<br><sub>(224 MiB / 44.1% CPU)</sub> | **2,907**<br><sub>(224 MiB / 44.1% CPU)</sub> | **457**<br><sub>(108 MiB / 24.8% CPU)</sub> | **457**<br><sub>(108 MiB / 24.8% CPU)</sub> | *Not measured* | *Not measured* | *Not measured* | *Not measured* | **2,642**<br><sub>(248 MiB / 49.6% CPU)</sub> | **2,642**<br><sub>(248 MiB / 49.6% CPU)</sub> |

Linux nginx H1/H2/H3 POST completed (nginx.org mainline). TWP÷YARP H1 ≈ **1.5×**; H2 ≈ **1.3×**; H3 ≈ **1.1×**. TWP÷nginx H3 ≈ **4×**.

### Windows — lossy / high-RTT (H2 HOL / H3 loss)

![Windows heavier lossy](images/rps-heavier-lossy-windows.png)

Userspace **5 ms** one-way delay + **1%** TCP connection stall (H1/H2) or UDP datagram drop (H3); **64 KiB** GET. Median of **3** repeats on `windows-latest` @ `84b225f7` — [34355162890](https://github.com/justcoding121/titanium-web-proxy/actions/runs/34355162890) (`compare-lossy`).
| Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | HAProxy sustain | HAProxy peak | Envoy sustain | Envoy peak | YARP sustain | YARP peak |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | **662**<br><sub>(111 MiB / 5.0% CPU)</sub> | **662**<br><sub>(111 MiB / 5.0% CPU)</sub> | **628**<br><sub>(140 MiB / 19.1% CPU)</sub> | **628**<br><sub>(140 MiB / 19.1% CPU)</sub> | *Not possible* | *Not possible* | *Not possible* | *Not possible* | 🥇 **672**<br><sub>(115 MiB / 5.1% CPU)</sub> | **672**<br><sub>(115 MiB / 5.1% CPU)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | **0**<br><sub>(126 MiB / 1.9% CPU)</sub> | **86**<br><sub>(126 MiB / 1.9% CPU)</sub> | **0**<br><sub>(140 MiB / 0.8% CPU)</sub> | **17**<br><sub>(140 MiB / 0.8% CPU)</sub> | *Not possible* | *Not possible* | *Not possible* | *Not possible* | **0**<br><sub>(99 MiB / 0.5% CPU)</sub> | **16**<br><sub>(99 MiB / 0.5% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | **0**<br><sub>(66 MiB / 0.0% CPU)</sub> | **0**<br><sub>(66 MiB / 0.0% CPU)</sub> | *Not possible (no QUIC)* | *Not possible (no QUIC)* | *Not possible* | *Not possible* | *Not possible* | *Not possible* | **0**<br><sub>(78 MiB / 0.0% CPU)</sub> | **0**<br><sub>(78 MiB / 0.0% CPU)</sub> |

TWP H2 HOL leads (~**3.31×** YARP). H3 is the protocol-shape win vs H2 HOL on the same lossy session; Win H3 GHA remains 0 (laptop remeasure kept above).

### Linux — lossy / high-RTT (H2 HOL / H3 loss)

![Linux heavier lossy](images/rps-heavier-lossy-linux.png)

Median of **3** repeats @ `84b225f7`. Source: [34355162890](https://github.com/justcoding121/titanium-web-proxy/actions/runs/34355162890) (`compare-lossy`; lossy H3 uses `quic-http3`).

| Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | HAProxy sustain | HAProxy peak | Envoy sustain | Envoy peak | YARP sustain | YARP peak |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | **1,213**<br><sub>(142 MiB / 8.5% CPU)</sub> | **1,213**<br><sub>(142 MiB / 8.5% CPU)</sub> | 🥇 **1,221**<br><sub>(99 MiB / 6.0% CPU)</sub> | **1,221**<br><sub>(99 MiB / 6.0% CPU)</sub> | *Not measured* | *Not measured* | *Not measured* | *Not measured* | **1,212**<br><sub>(152 MiB / 11.9% CPU)</sub> | **1,212**<br><sub>(152 MiB / 11.9% CPU)</sub> |
| HTTP/2 · TLS | HTTP/1 · plain | 🥇 **321**<br><sub>(188 MiB / 4.6% CPU)</sub> | **321**<br><sub>(188 MiB / 4.6% CPU)</sub> | **40**<br><sub>(99 MiB / 0.2% CPU)</sub> | **40**<br><sub>(99 MiB / 0.2% CPU)</sub> | *Not measured* | *Not measured* | *Not measured* | *Not measured* | **40**<br><sub>(127 MiB / 0.9% CPU)</sub> | **40**<br><sub>(127 MiB / 0.9% CPU)</sub> |
| HTTP/3 · QUIC | HTTP/1 · plain | **333**<br><sub>(146 MiB / 8.7% CPU)</sub> | **333**<br><sub>(146 MiB / 8.7% CPU)</sub> | **103**<br><sub>(110 MiB / 1.3% CPU)</sub> | **103**<br><sub>(110 MiB / 1.3% CPU)</sub> | *Not measured* | *Not measured* | *Not measured* | *Not measured* | 🥇 **353**<br><sub>(192 MiB / 14.7% CPU)</sub> | **353**<br><sub>(192 MiB / 14.7% CPU)</sub> |

TWP H2 HOL ≫ YARP (~**7.7×**). H3 TWP÷YARP ≈ **1×**.

### Architecture-sensitive

![Windows heavier architecture-sensitive workloads](images/rps-heavier-arch-windows.png)

![Linux heavier architecture-sensitive workloads](images/rps-heavier-arch-linux.png)

`compare-arch` isolates slow app readers, origin-early response, H2 duplex, and WebSocket echo. See [TWP vs YARP IO model](Performance-Profiling#twp-vs-yarp-io-model). Laptop 1-rep numbers are on [Performance Local Lab](Performance-Local-Lab#architecture-sensitive).

Median of **3** repeats on matched 4 vCPU / 16 GiB runners @ `84b225f7` ([34355169025](https://github.com/justcoding121/titanium-web-proxy/actions/runs/34355169025)) (`compare-arch`). Slow consumer = 256 KiB GET, 16 KiB read + 8 ms sleep. Early response = 64 KiB POST, origin writes after 8 KiB. Duplex H2 = overlapping 64 KiB POST on H2 TLS↔H2 TLS. WebSocket = echo round-trips/sec.

`compare-lossy` (slow **network**) is already published above; it is not a slow **app** reader.

#### Windows

![Windows heavier arch](images/rps-heavier-arch-windows.png)

| Scenario | Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | HAProxy sustain | HAProxy peak | Envoy sustain | Envoy peak | YARP sustain | YARP peak |
|---|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| Slow consumer (256 KiB GET, throttled client read) | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **248**<br><sub>(100 MiB / 4.1% CPU)</sub> | **248**<br><sub>(100 MiB / 4.1% CPU)</sub> | **206**<br><sub>(142 MiB / 24.8% CPU)</sub> | **206**<br><sub>(142 MiB / 24.8% CPU)</sub> | *Not possible* | *Not possible* | *Not possible* | *Not possible* | **240**<br><sub>(113 MiB / 4.2% CPU)</sub> | **240**<br><sub>(113 MiB / 4.2% CPU)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · TLS | HTTP/1 · plain | **256**<br><sub>(122 MiB / 3.6% CPU)</sub> | **256**<br><sub>(122 MiB / 3.6% CPU)</sub> | **188**<br><sub>(140 MiB / 24.6% CPU)</sub> | **188**<br><sub>(140 MiB / 24.6% CPU)</sub> | *Not possible* | *Not possible* | *Not possible* | *Not possible* | 🥇 **256**<br><sub>(113 MiB / 6.5% CPU)</sub> | **256**<br><sub>(113 MiB / 6.5% CPU)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **271**<br><sub>(97 MiB / 17.2% CPU)</sub> | **271**<br><sub>(97 MiB / 17.2% CPU)</sub> | *Not possible (no QUIC)* | *Not possible (no QUIC)* | *Not possible* | *Not possible* | *Not possible* | *Not possible* | **260**<br><sub>(166 MiB / 21.5% CPU)</sub> | **260**<br><sub>(166 MiB / 21.5% CPU)</sub> |
| Early response (origin writes after first request chunk) | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **6,137**<br><sub>(102 MiB / 43.5% CPU)</sub> | **6,137**<br><sub>(102 MiB / 43.5% CPU)</sub> | **383**<br><sub>(141 MiB / 24.6% CPU)</sub> | **383**<br><sub>(141 MiB / 24.6% CPU)</sub> | *Not possible* | *Not possible* | *Not possible* | *Not possible* | **4,517**<br><sub>(138 MiB / 54.5% CPU)</sub> | **4,517**<br><sub>(138 MiB / 54.5% CPU)</sub> |
| Early response (origin writes after first request chunk) | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **5,068**<br><sub>(182 MiB / 48.1% CPU)</sub> | **5,068**<br><sub>(182 MiB / 48.1% CPU)</sub> | **0**<br><sub>(142 MiB / 25.0% CPU)</sub> | **345**<br><sub>(142 MiB / 25.0% CPU)</sub> | *Not possible* | *Not possible* | *Not possible* | *Not possible* | **3,675**<br><sub>(134 MiB / 52.2% CPU)</sub> | **3,675**<br><sub>(134 MiB / 52.2% CPU)</sub> |
| Early response (origin writes after first request chunk) | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **2,147**<br><sub>(149 MiB / 41.5% CPU)</sub> | **2,147**<br><sub>(149 MiB / 41.5% CPU)</sub> | *Not possible (no QUIC)* | *Not possible (no QUIC)* | *Not possible* | *Not possible* | *Not possible* | *Not possible* | **1,923**<br><sub>(207 MiB / 51.9% CPU)</sub> | **1,923**<br><sub>(207 MiB / 51.9% CPU)</sub> |
| Duplex (both directions live) | HTTP/2 · TLS | HTTP/2 · TLS | **0**<br><sub>(90 MiB / 0.1% CPU)</sub> | **0**<br><sub>(90 MiB / 0.1% CPU)</sub> | *Not possible* | *Not possible* | *Not possible* | *Not possible* | *Not possible* | *Not possible* | **0**<br><sub>(121 MiB / 0.3% CPU)</sub> | **0**<br><sub>(121 MiB / 0.3% CPU)</sub> |
| Duplex (WebSocket / extended CONNECT) | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **33,646**<br><sub>(97 MiB / 41.3% CPU)</sub> | **33,646**<br><sub>(97 MiB / 41.3% CPU)</sub> | **19,492**<br><sub>(141 MiB / 24.8% CPU)</sub> | **19,492**<br><sub>(141 MiB / 24.8% CPU)</sub> | *Not possible* | *Not possible* | *Not possible* | *Not possible* | **30,989**<br><sub>(88 MiB / 44.0% CPU)</sub> | **30,989**<br><sub>(88 MiB / 44.0% CPU)</sub> |

#### Linux

![Linux heavier arch](images/rps-heavier-arch-linux.png)

| Scenario | Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | HAProxy sustain | HAProxy peak | Envoy sustain | Envoy peak | YARP sustain | YARP peak |
|---|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| Slow consumer (256 KiB GET, throttled client read) | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **468**<br><sub>(117 MiB / 9.2% CPU)</sub> | **468**<br><sub>(117 MiB / 9.2% CPU)</sub> | **419**<br><sub>(97 MiB / 9.1% CPU)</sub> | **419**<br><sub>(97 MiB / 9.1% CPU)</sub> | *Not measured* | *Not measured* | *Not measured* | *Not measured* | **427**<br><sub>(149 MiB / 13.2% CPU)</sub> | **427**<br><sub>(149 MiB / 13.2% CPU)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **474**<br><sub>(143 MiB / 18.7% CPU)</sub> | **474**<br><sub>(143 MiB / 18.7% CPU)</sub> | **417**<br><sub>(98 MiB / 15.2% CPU)</sub> | **417**<br><sub>(98 MiB / 15.2% CPU)</sub> | *Not measured* | *Not measured* | *Not measured* | *Not measured* | **470**<br><sub>(146 MiB / 22.4% CPU)</sub> | **470**<br><sub>(146 MiB / 22.4% CPU)</sub> |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **474**<br><sub>(128 MiB / 34.3% CPU)</sub> | **474**<br><sub>(128 MiB / 34.3% CPU)</sub> | **0**<br><sub>(118 MiB / 22.8% CPU)</sub> | **122**<br><sub>(118 MiB / 22.8% CPU)</sub> | *Not measured* | *Not measured* | *Not measured* | *Not measured* | **472**<br><sub>(189 MiB / 38.5% CPU)</sub> | **472**<br><sub>(189 MiB / 38.5% CPU)</sub> |
| Early response (origin writes after first request chunk) | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **5,002**<br><sub>(140 MiB / 45.7% CPU)</sub> | **5,002**<br><sub>(140 MiB / 45.7% CPU)</sub> | **4,002**<br><sub>(98 MiB / 49.4% CPU)</sub> | **4,002**<br><sub>(98 MiB / 49.4% CPU)</sub> | *Not measured* | *Not measured* | *Not measured* | *Not measured* | **3,412**<br><sub>(175 MiB / 55.6% CPU)</sub> | **3,412**<br><sub>(175 MiB / 55.6% CPU)</sub> |
| Early response (origin writes after first request chunk) | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **3,683**<br><sub>(231 MiB / 47.6% CPU)</sub> | **3,683**<br><sub>(231 MiB / 47.6% CPU)</sub> | **0**<br><sub>(114 MiB / 24.5% CPU)</sub> | **1,617**<br><sub>(114 MiB / 24.5% CPU)</sub> | *Not measured* | *Not measured* | *Not measured* | *Not measured* | **2,464**<br><sub>(165 MiB / 48.2% CPU)</sub> | **2,464**<br><sub>(165 MiB / 48.2% CPU)</sub> |
| Early response (origin writes after first request chunk) | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **3,103**<br><sub>(209 MiB / 45.6% CPU)</sub> | **3,103**<br><sub>(209 MiB / 45.6% CPU)</sub> | **0**<br><sub>(113 MiB / 24.8% CPU)</sub> | **555**<br><sub>(113 MiB / 24.8% CPU)</sub> | *Not measured* | *Not measured* | *Not measured* | *Not measured* | **2,263**<br><sub>(258 MiB / 47.4% CPU)</sub> | **2,263**<br><sub>(258 MiB / 47.4% CPU)</sub> |
| Duplex (both directions live) | HTTP/2 · TLS | HTTP/2 · TLS | **0**<br><sub>(112 MiB / 0.1% CPU)</sub> | **0**<br><sub>(112 MiB / 0.1% CPU)</sub> | *Not possible* | *Not possible* | *Not possible* | *Not possible* | *Not possible* | *Not possible* | **0**<br><sub>(149 MiB / 0.2% CPU)</sub> | **0**<br><sub>(149 MiB / 0.2% CPU)</sub> |
| Duplex (WebSocket / extended CONNECT) | HTTP/1 · TLS | HTTP/1 · plain | **34,674**<br><sub>(124 MiB / 44.6% CPU)</sub> | **34,674**<br><sub>(124 MiB / 44.6% CPU)</sub> | 🥇 **38,800**<br><sub>(99 MiB / 36.5% CPU)</sub> | **38,800**<br><sub>(99 MiB / 36.5% CPU)</sub> | *Not measured* | *Not measured* | *Not measured* | *Not measured* | **32,017**<br><sub>(124 MiB / 44.8% CPU)</sub> | **32,017**<br><sub>(124 MiB / 44.8% CPU)</sub> |

Slow consumer is sleep-bound; H1/H2/H3 sit in the same band. Early-response H1/H2/H3: TWP leads (H1 early ≈ **2.00×** / **1.47×** YARP Win/Linux). **Duplex H2**: YARP leads by design — Win ≈ **0.59×** (1,270 / 2,135), Linux ≈ **0.15×** (282 / 1,882); irreducible concurrent-copier cell (see [IO model](Performance-Profiling#twp-vs-yarp-io-model)). WebSocket: TWP÷YARP Windows ≈ **1.06×**; Linux nginx leads.

### TLS termination cost (H1 TLS → cleartext origin)

Isolates keep-alive tiny GET vs **new connection per request** (handshake-dominated) vs keep-alive **256 KiB**. Product comparison uses RPS and end-to-end latency; TWP can also capture `ClientTlsTiming` when `TWP_RPS_CAPTURE_TLS=1` (child process) — nginx/YARP have no equivalent hook.

![Windows TLS termination cost](images/rps-heavier-tls-cost-windows.png)

![Linux TLS termination cost](images/rps-heavier-tls-cost-linux.png)

#### Windows

Median of **3** repeats on `windows-latest` @ `84b225f7`. Source: Actions [34355173969](https://github.com/justcoding121/titanium-web-proxy/actions/runs/34355173969) (`compare-tls-cost`). Absolute RPS on GHA swings hard; prefer **TWP÷YARP**.

![Windows heavier TLS cost](images/rps-heavier-tls-cost-windows.png)

| Workload | TWP sustain | TWP peak | nginx sustain | nginx peak | HAProxy sustain | HAProxy peak | Envoy sustain | Envoy peak | YARP sustain | YARP peak |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| Keep-alive · tiny GET | 🥇 **36,005**<br><sub>(86 MiB / 45.7% CPU)</sub> | **36,005**<br><sub>(86 MiB / 45.7% CPU)</sub> | **17,169**<br><sub>(140 MiB / 24.9% CPU)</sub> | **17,169**<br><sub>(140 MiB / 24.9% CPU)</sub> | *Not possible* | *Not possible* | *Not possible* | *Not possible* | **31,526**<br><sub>(102 MiB / 50.7% CPU)</sub> | **31,526**<br><sub>(102 MiB / 50.7% CPU)</sub> |
| New-connection · tiny GET | 🥇 **957**<br><sub>(89 MiB / 9.3% CPU)</sub> | **957**<br><sub>(89 MiB / 9.3% CPU)</sub> | **0**<br><sub>(139 MiB / 24.3% CPU)</sub> | **310**<br><sub>(139 MiB / 24.3% CPU)</sub> | *Not possible* | *Not possible* | *Not possible* | *Not possible* | **941**<br><sub>(116 MiB / 8.9% CPU)</sub> | **941**<br><sub>(116 MiB / 8.9% CPU)</sub> |
| Keep-alive · 256 KiB GET | 🥇 **3,754**<br><sub>(135 MiB / 39.7% CPU)</sub> | **3,754**<br><sub>(135 MiB / 39.7% CPU)</sub> | **233**<br><sub>(141 MiB / 24.7% CPU)</sub> | **233**<br><sub>(141 MiB / 24.7% CPU)</sub> | *Not possible* | *Not possible* | *Not possible* | *Not possible* | **3,304**<br><sub>(128 MiB / 45.5% CPU)</sub> | **3,304**<br><sub>(128 MiB / 45.5% CPU)</sub> |

#### Linux

Median of **3** repeats @ `84b225f7`. Source: Actions [34355173969](https://github.com/justcoding121/titanium-web-proxy/actions/runs/34355173969) (`compare-tls-cost`).

![Linux heavier TLS cost](images/rps-heavier-tls-cost-linux.png)

| Workload | TWP sustain | TWP peak | nginx sustain | nginx peak | HAProxy sustain | HAProxy peak | Envoy sustain | Envoy peak | YARP sustain | YARP peak |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| Keep-alive · tiny GET | **48,132**<br><sub>(111 MiB / 47.5% CPU)</sub> | **48,132**<br><sub>(111 MiB / 47.5% CPU)</sub> | 🥇 **62,451**<br><sub>(102 MiB / 39.3% CPU)</sub> | **62,451**<br><sub>(102 MiB / 39.3% CPU)</sub> | *Not measured* | *Not measured* | *Not measured* | *Not measured* | **42,804**<br><sub>(134 MiB / 49.3% CPU)</sub> | **42,804**<br><sub>(134 MiB / 49.3% CPU)</sub> |
| New-connection · tiny GET | **1,542**<br><sub>(126 MiB / 38.7% CPU)</sub> | **1,542**<br><sub>(126 MiB / 38.7% CPU)</sub> | 🥇 **1,633**<br><sub>(102 MiB / 34.0% CPU)</sub> | **1,633**<br><sub>(102 MiB / 34.0% CPU)</sub> | *Not measured* | *Not measured* | *Not measured* | *Not measured* | **1,547**<br><sub>(149 MiB / 37.1% CPU)</sub> | **1,547**<br><sub>(149 MiB / 37.1% CPU)</sub> |
| Keep-alive · 256 KiB GET | 🥇 **4,816**<br><sub>(126 MiB / 29.8% CPU)</sub> | **4,816**<br><sub>(126 MiB / 29.8% CPU)</sub> | **3,341**<br><sub>(100 MiB / 38.3% CPU)</sub> | **3,341**<br><sub>(100 MiB / 38.3% CPU)</sub> | *Not measured* | *Not measured* | *Not measured* | *Not measured* | **3,464**<br><sub>(170 MiB / 41.3% CPU)</sub> | **3,464**<br><sub>(170 MiB / 41.3% CPU)</sub> |

All three workloads are **>1.00×** YARP on both OS. nginx leads Linux keep-alive tiny and Linux new-connection; TWP is second, YARP third.

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
