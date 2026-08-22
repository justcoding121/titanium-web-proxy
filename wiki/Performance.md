# Performance

Titanium targets **low-overhead MITM proxying**: connection pooling, HTTP/2 multiplexing, and buffer reuse. Numbers below are **Release** measurements with [RpsLoadProbe](https://github.com/justcoding121/titanium-web-proxy/tree/develop/tools/RpsLoadProbe) (and BenchmarkDotNet / Basic example where noted). Publishable tables cite **GitHub Actions** medians on matched **4 vCPU / 16 GiB** runners. Absolute RPS still varies by OS kernel, TLS, and MsQuic packaging — compare **within a table**, not across Windows vs Linux.

Control arms: **nginx** (native C reverse-proxy ceiling; Linux is authoritative) and **YARP** (`Yarp.ReverseProxy`, managed .NET reverse proxy). Neither can MITM (no CONNECT / forged certs). FiddlerCore is not compared (commercial debugger license; not a throughput peer).

For pooling knobs and certificate first-visit tuning, see [Performance and pooling](Home#performance-and-pooling). For the local cool A/B lab, laptop tables, and the techniques used to find each hotspot, see [Performance Profiling](Performance-Profiling).

## Measurement environment

Both OS use the standard public-repo GitHub-hosted runner class (**4 vCPU / 16 GiB / 14 GB SSD**). Same harness knobs (`workflow_dispatch` [RPS saturation](https://github.com/justcoding121/titanium-web-proxy/actions/workflows/rps-saturation.yml): warmup 2s / measure 8s; concurrency 8, 16, 32, 64; median of 3 repeats). Prefer **TWP÷YARP** / **TWP÷nginx** ratios over absolute RPS.

Laptop High-perf / cool-paired Windows numbers live on [Performance Profiling — Local Windows lab](Performance-Profiling#local-windows-lab-developer-laptop). Do not mix those absolutes into the tables below.

### Windows (GitHub-hosted `windows-latest`)

| | |
|---|---|
| OS | Windows Server (GitHub-hosted `windows-latest`) |
| CPU | **4** logical processors |
| RAM | **16** GiB |
| Runtime | .NET 10.0.x |
| nginx | nginx/Windows **1.31.3** (same-OS only; no QUIC) |
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
| YARP | Yarp.ReverseProxy **2.3.0** |
| Harness | RpsLoadProbe Release; median of 3 repeats where noted |

**How to read the tables**

- **Mode**: **Reverse** = transparent fixed-forward (may TLS-terminate to a cleartext origin, or re-encrypt to a configured HTTPS/QUIC origin). **MITM** = both legs are visible in the clear inside TWP — either by decrypting client TLS/QUIC (forged cert / CONNECT) **or** by accepting an already-cleartext client (explicit HTTP proxy / inspectable transparent reverse) while still speaking plain or TLS to the origin. nginx and YARP cannot do MITM. **HTTP/3 has no cleartext client** (QUIC always encrypted).
- **Sustainable** = last concurrency that still met error/latency SLOs. **Peak** = highest RPS in that ramp.
- 🟢 = highest **sustainable** RPS among products that have a number on that row. Omitted when only TWP can run the path (no fair multi-product comparison).
- *Not possible* = product cannot do that path. *Not measured* = path exists but no published number yet for that OS.

```powershell
pwsh tools/RpsLoadProbe/run-rps.ps1 -Mode compare-same
pwsh tools/RpsLoadProbe/run-rps.ps1 -Mode compare-terminate
pwsh tools/RpsLoadProbe/run-rps.ps1 -Mode compare-bridges
pwsh tools/RpsLoadProbe/run-rps.ps1 -Mode compare-mitm
pwsh tools/RpsLoadProbe/run-rps.ps1 -Mode compare-bodies
pwsh tools/RpsLoadProbe/run-rps.ps1 -Mode compare-post
pwsh tools/RpsLoadProbe/run-rps.ps1 -Mode compare-lossy
pwsh tools/RpsLoadProbe/run-rps.ps1 -Mode compare-tls-cost
```

## Windows — Titanium vs nginx vs YARP

Client / origin: HTTP version and whether TLS is used (`plain` = cleartext, `TLS` = encrypted, `QUIC` = HTTP/3).

Median of **3 repeats** on `windows-latest` (4 vCPU / 16 GiB). After a [cool local win](Performance-Profiling#local-windows-lab-developer-laptop), run [RPS saturation](https://github.com/justcoding121/titanium-web-proxy/actions/workflows/rps-saturation.yml) (`compare-same`, `compare-bridges`, `compare-mitm`) and paste medians + the Actions run ID here. **No Windows CI matrix has been pasted yet** — cells below stay *Not measured* until that run. Laptop High-perf / cool-paired numbers stay on the [local lab](Performance-Profiling#local-windows-lab-developer-laptop).

**Load generators:** Reverse inbound H3 arms use **`dotnet-httpclient`** (`http_version=3.0`, `RequestVersionExact`) after dual-listen reverse H3. nginx/Windows is same-OS only (no QUIC).

| Mode | Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---|---|---:|---:|---:|---:|---:|---:|
| Reverse | HTTP/1 · plain | HTTP/1 · plain | *Not measured* | *Not measured* | *Not measured* | *Not measured* | *Not measured* | *Not measured* |
| Reverse | HTTP/1 · plain | HTTP/1 · TLS | *Not measured* | *Not measured* | *Not possible* | *Not possible* | *Not measured* | *Not measured* |
| Reverse | HTTP/1 · TLS | HTTP/1 · plain | *Not measured* | *Not measured* | *Not measured* | *Not measured* | *Not measured* | *Not measured* |
| Reverse | HTTP/1 · TLS | HTTP/2 · TLS | *Not measured* | *Not measured* | *Not possible* | *Not possible* | *Not measured* | *Not measured* |
| Reverse | HTTP/1 · TLS | HTTP/3 · QUIC | *Not measured* | *Not measured* | *Not possible* (no QUIC) | *Not possible* | *Not measured* | *Not measured* |
| Reverse | HTTP/2 · plain | HTTP/1 · plain | *Not measured* | *Not measured* | *Not possible* | *Not possible* | *Not measured* | *Not measured* |
| Reverse | HTTP/2 · plain | HTTP/2 · plain | *Not measured* | *Not measured* | *Not possible* | *Not possible* | *Not measured* | *Not measured* |
| Reverse | HTTP/2 · plain | HTTP/2 · TLS | *Not measured* | *Not measured* | *Not possible* | *Not possible* | *Not measured* | *Not measured* |
| Reverse | HTTP/2 · plain | HTTP/3 · QUIC | *Not measured* | *Not measured* | *Not possible* (no QUIC) | *Not possible* | *Not measured* | *Not measured* |
| Reverse | HTTP/2 · TLS | HTTP/1 · plain | *Not measured* | *Not measured* | *Not measured* | *Not measured* | *Not measured* | *Not measured* |
| Reverse | HTTP/2 · TLS | HTTP/2 · plain | *Not measured* | *Not measured* | *Not possible* | *Not possible* | *Not measured* | *Not measured* |
| Reverse | HTTP/2 · TLS | HTTP/3 · QUIC | *Not measured* | *Not measured* | *Not possible* (no QUIC) | *Not possible* | *Not measured* | *Not measured* |
| Reverse | HTTP/3 · QUIC | HTTP/1 · plain | *Not measured* | *Not measured* | *Not possible* (no QUIC) | *Not possible* | *Not measured* | *Not measured* |
| Reverse | HTTP/3 · QUIC | HTTP/2 · TLS | *Not measured* | *Not measured* | *Not possible* (no QUIC) | *Not possible* | *Not measured* | *Not measured* |
| Reverse | HTTP/3 · QUIC | HTTP/3 · QUIC | *Not measured* | *Not measured* | *Not possible* (no QUIC) | *Not possible* | *Not measured* | *Not measured* |
| MITM | HTTP/1 · plain | HTTP/1 · plain | *Not measured* | *Not measured* | *Not possible* (no MITM) | *Not possible* | *Not possible* (no MITM) | *Not possible* |
| MITM | HTTP/1 · plain | HTTP/1 · TLS | *Not measured* | *Not measured* | *Not possible* (no MITM) | *Not possible* | *Not possible* (no MITM) | *Not possible* |
| MITM | HTTP/1 · TLS | HTTP/1 · TLS | *Not measured* | *Not measured* | *Not possible* (no MITM) | *Not possible* | *Not possible* (no MITM) | *Not possible* |
| MITM | HTTP/2 · plain | HTTP/2 · plain | *Not measured* | *Not measured* | *Not possible* (no MITM) | *Not possible* | *Not possible* (no MITM) | *Not possible* |
| MITM | HTTP/2 · plain | HTTP/2 · TLS | *Not measured* | *Not measured* | *Not possible* (no MITM) | *Not possible* | *Not possible* (no MITM) | *Not possible* |
| MITM | HTTP/2 · TLS | HTTP/1 · TLS | *Not measured* | *Not measured* | *Not possible* (no MITM) | *Not possible* | *Not possible* (no MITM) | *Not possible* |
| MITM | HTTP/2 · TLS | HTTP/2 · TLS | *Not measured* | *Not measured* | *Not possible* (no MITM) | *Not possible* | *Not possible* (no MITM) | *Not possible* |
| MITM | HTTP/3 · QUIC | HTTP/1 · TLS | *Not measured* | *Not measured* | *Not possible* (no QUIC) | *Not possible* | *Not possible* (no MITM) | *Not possible* |
| MITM | HTTP/3 · QUIC | HTTP/2 · TLS | *Not measured* | *Not measured* | *Not possible* (no QUIC) | *Not possible* | *Not possible* (no MITM) | *Not possible* |
| MITM | HTTP/3 · QUIC | HTTP/3 · QUIC | *Not measured* | *Not measured* | *Not possible* (no QUIC) | *Not possible* | *Not possible* (no MITM) | *Not possible* |

## Linux — Titanium vs nginx vs YARP

Median of **3 repeats**. **2026-08-22 post-Windows-parity remasure** on `develop` @ `193d5101` (library unchanged since H3→H1 chunked-drain / QPACK lowercase / H2 header-decode): [32555967423](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32555967423) (`compare-same`), [32555968862](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32555968862) (`compare-bridges`). Absolute RPS on this GHA pass is lower than the earlier same-day remasure ([32552296839](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32552296839) / [32552295495](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32552295495)) — **TWP÷YARP ratios hold**; prefer ratios over absolutes across VMs. MITM rows still from [32334395907](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32334395907); nginx HTTP/3 tiny-GET peak note from [32337905168](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32337905168). Warmup 2s / measure 8s; concurrency 8, 16, 32, 64. **Linux nginx is the authoritative nginx baseline.** The RPS workflow installs nginx.org mainline (`http_v3_module`) and `libmsquic` (`QuicListener.IsSupported=true` on `ubuntu-latest`).

TWP÷nginx H1 plain reverse ≈ **0.73** (27,449 / 37,656); TWP÷YARP H1 plain ≈ **0.96** (27,449 / 28,447). Prefer ratios over absolute RPS on GHA VMs.

| Mode | Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---|---|---:|---:|---:|---:|---:|---:|
| Reverse | HTTP/1 · plain | HTTP/1 · plain | **27,449** | **27,449** | 🟢 **37,656** | **37,656** | **28,447** | **28,447** |
| Reverse | HTTP/1 · TLS | HTTP/1 · plain | **20,203** | **20,203** | 🟢 **28,282** | **28,282** | **20,678** | **20,678** |
| Reverse | HTTP/1 · TLS | HTTP/2 · TLS | 🟢 **29,290** | **29,290** | *Not possible* | *Not possible* | **28,702** | **28,702** |
| Reverse | HTTP/1 · TLS | HTTP/3 · QUIC | **20,081** | **20,081** | *Not possible* (no H3 origin) | *Not possible* | 🟢 **21,054** | **21,054** |
| Reverse | HTTP/2 · plain | HTTP/1 · plain | 🟢 **40,559** | **40,559** | *Not possible* | *Not possible* | **39,878** | **39,878** |
| Reverse | HTTP/2 · plain | HTTP/2 · plain | 🟢 **64,883** | **64,883** | *Not possible* | *Not possible* | **49,354** | **49,354** |
| Reverse | HTTP/2 · plain | HTTP/2 · TLS | 🟢 **54,002** | **54,002** | *Not possible* | *Not possible* | **41,158** | **41,158** |
| Reverse | HTTP/2 · plain | HTTP/3 · QUIC | **30,525** | **30,525** | *Not possible* (no H3 origin) | *Not possible* | 🟢 **30,610** | **30,610** |
| Reverse | HTTP/2 · TLS | HTTP/1 · plain | 🟢 **52,431** | **52,431** | **13,396** | **18,791** | **29,371** | **29,371** |
| Reverse | HTTP/2 · TLS | HTTP/2 · plain | 🟢 **65,325** | **65,325** | *Not possible* | *Not possible* | **45,414** | **45,414** |
| Reverse | HTTP/2 · TLS | HTTP/3 · QUIC | 🟢 **29,096** | **29,096** | *Not possible* (no H3 origin) | *Not possible* | **26,569** | **26,569** |
| Reverse | HTTP/3 · QUIC | HTTP/1 · plain | 🟢 **22,521** | **22,521** | **0** | **18,996** | **21,831** | **21,831** |
| Reverse | HTTP/3 · QUIC | HTTP/2 · TLS | 🟢 **28,850** | **28,850** | *Not possible* (no H3→H2) | *Not possible* | **24,902** | **24,902** |
| Reverse | HTTP/3 · QUIC | HTTP/3 · QUIC | 🟢 **20,621** | **20,621** | *Not possible* (no H3 origin) | *Not possible* | **16,836** | **16,836** |
| MITM | HTTP/1 · TLS | HTTP/1 · TLS | **30,703** | **30,703** | *Not possible* (no MITM) | *Not possible* | *Not possible* (no MITM) | *Not possible* |
| MITM | HTTP/2 · TLS | HTTP/1 · TLS | **38,764** | **38,764** | *Not possible* (no MITM) | *Not possible* | *Not possible* (no MITM) | *Not possible* |
| MITM | HTTP/2 · TLS | HTTP/2 · TLS | **52,372** | **52,372** | *Not possible* (no MITM) | *Not possible* | *Not possible* (no MITM) | *Not possible* |
| MITM | HTTP/3 · QUIC | HTTP/1 · TLS | **24,036** | **24,036** | *Not possible* (no MITM) | *Not possible* | *Not possible* (no MITM) | *Not possible* |
| MITM | HTTP/3 · QUIC | HTTP/2 · TLS | **26,746** | **26,746** | *Not possible* (no MITM) | *Not possible* | *Not possible* (no MITM) | *Not possible* |
| MITM | HTTP/3 · QUIC | HTTP/3 · QUIC | **22,346** | **22,346** | *Not possible* (no MITM) | *Not possible* | *Not possible* (no MITM) | *Not possible* |

On this GHA shape, TWP H1 plain ÷ nginx H1 plain ≈ **0.73** (27,449 / 37,656). H1 TLS terminate ≈ **0.71** (20,203 / 28,282). TWP÷YARP H1 plain ≈ **0.96**; H1 TLS terminate ≈ **0.98**. Absolute RPS swings by VM; prefer the **ratio** and **median across repeats**.

**nginx HTTP/3:** inbound QUIC terminate → cleartext H1 is in the harness (`nginx-reverse-http3-cleartext`, nginx.org 1.31.4). HttpClient/MsQuic negotiates `3.0` and peaks at **~19k** RPS, but the error rate stays above the 0.1% SLO (sustain **0**). nginx still cannot speak HTTP/3 to an origin (no H3 upstream in this conf), so H3-origin rows stay blank. nginx peak on the H3→H1 row is retained from the older tiny-GET pass.

**YARP HTTP/3 (2026-08-22 post-parity remasure):** TWP leads H3→H1 ≈ **1.03×** (22,521 / 21,831), H3→H2 ≈ **1.16×** (28,850 / 24,902), H3→H3 ≈ **1.22×** (20,621 / 16,836). H1→H2 ≈ **1.02×** (29,290 / 28,702). Near-ties: H1→H3 ≈ **0.95×**, h2c→H3 ≈ **1.00×**. H2 TLS→H1 YARP looked soft this pass (~29k vs TWP ~52k) — treat that absolute cautiously; ratio still TWP-led.

**Windows vs Linux:** both CI envs are **4 vCPU / 16 GiB**, but do **not** compare absolute RPS across OS. Linux still has nginx leading H1 plain/TLS terminate; vs YARP, H3 bridges and same-protocol H2 are TWP-led, with H1 plain still ~**0.96×** YARP. Cool laptop Windows ratios (tiny-GET at parity or better vs YARP) live on [Performance Profiling](Performance-Profiling#local-windows-lab-developer-laptop) until the first `windows-latest` median is pasted above.

### Tiny JSON reverse is nginx’s best case on Linux

The tables above use **~64 B keep-alive GET** on loopback. On Linux H1 reverse, nginx leads; YARP sits near TWP. Heavier bodies, POSTs, TLS handshake cost, and lossy/HOL workloads (below) change the picture. MITM rows remain TWP-only. nginx HTTP/3 is inbound-terminate only (see note above).

### Why isn’t HTTP/3 > HTTP/2 > HTTP/1 in raw RPS?

For **tiny JSON responses** (~64 B) on loopback, that ordering is **not** expected: topology (TLS hop count, terminate vs MITM) dominates; HTTP/2 and HTTP/3 help multiplexing, not single-origin tiny-GET RPS. See the **lossy** tables below for a workload where protocol design matters.

## Heavier reverse workloads

Separate from the tiny-GET matrix. Same measurement environments. Modes: `compare-bodies`, `compare-post`, `compare-lossy`, `compare-tls-cost` in [RpsLoadProbe](https://github.com/justcoding121/titanium-web-proxy/tree/develop/tools/RpsLoadProbe). **PUT with the same body is the same proxy work as POST; DELETE with no body matches GET** — only POST is published.

Lossy link = **userspace** shim (not kernel `netem`): TCP gets per-buffer delay + occasional whole-connection stalls (honest HOL for multiplexed H2); UDP datagram drop exists in the harness but **H3 lossy is not published** — rechecked at concurrency 8 after H2/H3 streaming work (`rps-ramp-20260817-212421`): TWP H3 through the UDP shim stayed at **0** sustain (multi-second p99); YARP H3 via the TCP shim also failed to establish. Treat as a **measurement limitation** (MsQuic + lossy shim), not a capability claim.

### Windows — heavier reverse GET (64 KiB / 256 KiB)

Median of **3** repeats on `windows-latest`. Mode: `compare-bodies`. **Pending first Windows CI paste.** Cool laptop remasures stay on the [local lab](Performance-Profiling#local-windows-lab-developer-laptop).

| Body | Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---|---|---:|---:|---:|---:|---:|---:|
| 64 KiB | HTTP/1 · TLS | HTTP/1 · plain | *Not measured* | *Not measured* | *Not measured* | *Not measured* | *Not measured* | *Not measured* |
| 64 KiB | HTTP/2 · TLS | HTTP/1 · plain | *Not measured* | *Not measured* | *Not measured* | *Not measured* | *Not measured* | *Not measured* |
| 64 KiB | HTTP/3 · QUIC | HTTP/1 · plain | *Not measured* | *Not measured* | *Not possible* (no QUIC) | *Not possible* | *Not measured* | *Not measured* |
| 256 KiB | HTTP/1 · TLS | HTTP/1 · plain | *Not measured* | *Not measured* | *Not measured* | *Not measured* | *Not measured* | *Not measured* |
| 256 KiB | HTTP/2 · TLS | HTTP/1 · plain | *Not measured* | *Not measured* | *Not measured* | *Not measured* | *Not measured* | *Not measured* |
| 256 KiB | HTTP/3 · QUIC | HTTP/1 · plain | *Not measured* | *Not measured* | *Not possible* (no QUIC) | *Not possible* | *Not measured* | *Not measured* |

### Linux — heavier reverse GET (64 KiB / 256 KiB)

Median of **3** repeats. Source: Actions [32562607744](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32562607744) (`compare-bodies` on `0726610e` — post H2→H1 unbuffered read + 288 KiB flatten). Warmup 2s / measure 8s.

| Body | Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---|---|---:|---:|---:|---:|---:|---:|
| 64 KiB | HTTP/1 · TLS | HTTP/1 · plain | **6,618** | **6,618** | 🟢 **8,251** | **8,251** | **6,583** | **6,583** |
| 64 KiB | HTTP/2 · TLS | HTTP/1 · plain | 🟢 **6,048** | **6,048** | **3,588** | **3,588** | **4,691** | **4,691** |
| 64 KiB | HTTP/3 · QUIC | HTTP/1 · plain | 🟢 **4,396** | **4,396** | **1,647** | **1,647** | **4,353** | **4,353** |
| 256 KiB | HTTP/1 · TLS | HTTP/1 · plain | **2,105** | **2,105** | 🟢 **2,710** | **2,710** | **2,192** | **2,192** |
| 256 KiB | HTTP/2 · TLS | HTTP/1 · plain | 🟢 **1,489** | **1,489** | **903** | **903** | **1,404** | **1,404** |
| 256 KiB | HTTP/3 · QUIC | HTTP/1 · plain | 🟢 **4,115** | **4,115** | **415** | **415** | **1,318** | **1,318** |

On this GHA pass TWP÷YARP H1 TLS ≈ **1.01×** (64 KiB) / **0.96×** (256 KiB); H2→H1 ≈ **1.29×** / **1.06×**; H3→H1 ≈ **1.01×** / **3.12×** (YARP soft on 256 KiB H3 — treat absolute cautiously). TWP÷nginx H1 TLS ≈ **0.80** / **0.78**. Absolute RPS swings by VM; prefer ratios.

### Windows — POST 64 KiB request + 64 KiB response

Median of **3** repeats on `windows-latest`. Mode: `compare-post`. **Pending first Windows CI paste.** Laptop 1-rep / cool POST notes stay on the [local lab](Performance-Profiling#local-windows-lab-developer-laptop).

| Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---|---:|---:|---:|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | *Not measured* | *Not measured* | *Not measured* | *Not measured* | *Not measured* | *Not measured* |
| HTTP/2 · TLS | HTTP/1 · plain | *Not measured* | *Not measured* | *Not measured* | *Not measured* | *Not measured* | *Not measured* |
| HTTP/3 · QUIC | HTTP/1 · plain | *Not measured* | *Not measured* | *Not possible* | *Not possible* | *Not measured* | *Not measured* |

### Linux — POST 64 KiB request + 64 KiB response

Median of **3** repeats. Source: Actions [32335541836](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32335541836) (`compare-post`).

| Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---|---:|---:|---:|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | **3,885** | **3,885** | 🟢 **4,041** | **4,041** | **3,112** | **3,112** |
| HTTP/2 · TLS | HTTP/1 · plain | 🟢 **2,763** | **2,763** | **1,929** | **1,990** | **2,468** | **2,525** |
| HTTP/3 · QUIC | HTTP/1 · plain | **0** | **2,033** | *Not measured* | *Not measured* | 🟢 **2,592** | **2,592** |

Linux nginx H1/H2 POST completed this pass (nginx.org 1.31.4; the previous Ubuntu 1.24 arm returned 100% errors). TWP H3 POST peaked at **2,033** but did not hold the error/latency SLO (sustain **0**). nginx HTTP/3 POST used the pre-fix IPv4-only QUIC listen and is left unmeasured.

### Windows — lossy / high-RTT (H2 HOL)

Userspace **5 ms** one-way delay + **1%** connection stall; **64 KiB** GET. Median of **3** repeats on `windows-latest`. Mode: `compare-lossy`. **Pending first Windows CI paste.**

| Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---|---:|---:|---:|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | *Not measured* | *Not measured* | *Not measured* | *Not measured* | *Not measured* | *Not measured* |
| HTTP/2 · TLS | HTTP/1 · plain | *Not measured* | *Not measured* | *Not measured* | *Not measured* | *Not measured* | *Not measured* |

H1 stays usable; H2 collapses under connection stalls (HOL) on every product in the [laptop lab](Performance-Profiling#lossy--high-rtt-h2-hol). Absolute RPS is low because the shim delays every buffer — the point is the **protocol shape**, not competing with the tiny-GET table.

### Linux — lossy / high-RTT (H2 HOL)

Median of **3** repeats. Source: Actions [32335543372](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32335543372) (`compare-lossy`).

| Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---|---:|---:|---:|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | **1,168** | **1,168** | 🟢 **1,220** | **1,220** | **1,217** | **1,217** |
| HTTP/2 · TLS | HTTP/1 · plain | 🟢 **40** | **44** | 🟢 **40** | **44** | 🟢 **40** | **40** |

Same story as Windows: H1 stays usable; H2 falls to tens of RPS for all three products. Tiny-GET H1 leadership does not carry over.

### TLS termination cost (H1 TLS → cleartext origin)

Isolates keep-alive tiny GET vs **new connection per request** (handshake-dominated) vs keep-alive **256 KiB**. Product comparison uses RPS and end-to-end latency; TWP can also capture `ClientTlsTiming` when `TWP_RPS_CAPTURE_TLS=1` (child process) — nginx/YARP have no equivalent hook.

#### Windows

Median of **3** repeats on `windows-latest`. Mode: `compare-tls-cost`. **Pending first Windows CI paste.**

| Workload | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---:|---:|---:|---:|---:|---:|
| Keep-alive · tiny GET | *Not measured* | *Not measured* | *Not measured* | *Not measured* | *Not measured* | *Not measured* |
| New-connection · tiny GET | *Not measured* | *Not measured* | *Not measured* | *Not measured* | *Not measured* | *Not measured* |
| Keep-alive · 256 KiB GET | *Not measured* | *Not measured* | *Not measured* | *Not measured* | *Not measured* | *Not measured* |

#### Linux

Median of **3** repeats. Source: Actions [32335545066](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32335545066) (`compare-tls-cost`).

| Workload | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---:|---:|---:|---:|---:|---:|
| Keep-alive · tiny GET | **18,880** | **18,880** | 🟢 **28,245** | **28,245** | **20,746** | **20,746** |
| New-connection · tiny GET | 🟢 **1,106** | **1,113** | **1,005** | **1,009** | **957** | **957** |
| Keep-alive · 256 KiB GET | **2,126** | **2,126** | 🟢 **2,627** | **2,627** | **2,134** | **2,134** |

**Verdict (Linux, authoritative nginx):** On keep-alive tiny terminate, TWP is ~**0.67×** nginx and ~**0.91×** YARP. On **new-connection** terminate, TWP is **ahead** of both (~1.10× nginx, ~1.16× YARP). With **256 KiB** bodies, nginx still leads sustain when all three are healthy.

## Other measurements

| What | Result |
|---|---|
| HTTPS TTFB vs direct (median, 14 hosts) | Cold **≈ parity** (−1 ms); warm **−25 ms** (proxy faster) |
| HTTP/1 loopback GET (no body intercept) | **~186 µs**, **~17.5 KB** allocated / request |
| Basic example footprint (Release, after load) | **~74 MB** working set · **~24–29 MB** private bytes |

```powershell
dotnet run -c Release --project benchmarks/Titanium.Web.Proxy.Benchmarks -- --filter '*Throughput*'
```

| Benchmark | Setup | Mean | Allocated / op |
|---|---|---:|---:|
| HTTP/1 GET through proxy | Passthrough | **186 µs** | **17.5 KB** |
| HTTP/2 multiplexed GETs | 10 concurrent streams | **3.0 ms** / batch | **~14 KB** / request |

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
