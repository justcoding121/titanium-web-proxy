# Performance

Titanium targets **low-overhead MITM proxying**: connection pooling, HTTP/2 multiplexing, and buffer reuse. Numbers below are **Release** measurements with [RpsLoadProbe](https://github.com/justcoding121/titanium-web-proxy/tree/develop/tools/RpsLoadProbe) (and BenchmarkDotNet / Basic example where noted). Absolute RPS varies by hardware, OS, and background load — compare **within a table**, not across Windows vs Linux.

Control arms: **nginx** (native C reverse-proxy ceiling; Linux is authoritative) and **YARP** (`Yarp.ReverseProxy`, managed .NET reverse proxy). Neither can MITM (no CONNECT / forged certs). FiddlerCore is not compared (commercial debugger license; not a throughput peer).

For pooling knobs and certificate first-visit tuning, see [Performance and pooling](Home#performance-and-pooling). For the methodology behind these numbers — the harness, noise control, and the profiling techniques used to find each hotspot — see [Performance Profiling](Performance-Profiling).

## Measurement environment

### Windows (developer laptop)

| | |
|---|---|
| OS | Windows 11 (10.0.26200) |
| CPU | 11th Gen Intel Core i7-1185G7 @ 3.00 GHz (8 logical processors) |
| RAM | 31.8 GiB |
| Runtime | .NET 10.0.10 |
| nginx | nginx/Windows **1.31.3** |
| YARP | Yarp.ReverseProxy **2.3.0** |
| Harness | RpsLoadProbe Release; arms run **sequentially** |

### Linux (GitHub-hosted `ubuntu-latest`)

| | |
|---|---|
| OS | Ubuntu 24.04.4 LTS |
| CPU | AMD EPYC (4 logical processors on the VM; runners this pass were 7763 / 9V74) |
| RAM | 15.6 GiB |
| Runtime | .NET 10.0.11 |
| nginx | nginx/**1.31.4** (nginx.org mainline, `--with-http_v3_module`) |
| YARP | Yarp.ReverseProxy **2.3.0** |
| Harness | RpsLoadProbe Release; median of 3 repeats where noted |

**How to read the tables**

- **Mode**: **Reverse** = transparent fixed-forward (may TLS-terminate to a cleartext origin, or re-encrypt to a configured HTTPS/QUIC origin). **MITM** = proxy decrypts the client crypto **and** speaks TLS/QUIC to the origin (or explicit decrypt proxy), so both legs are visible in the clear inside TWP. nginx and YARP cannot do MITM.
- **Sustainable** = last concurrency that still met error/latency SLOs. **Peak** = highest RPS in that ramp.
- **Winner** = highest **sustainable** RPS among products that have a number on that row. Left **blank** when only TWP can run the path (no fair multi-product comparison).
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

| Mode | Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak | Winner |
|---|---|---|---:|---:|---:|---:|---:|---:|---|
| Reverse | HTTP/1 · plain | HTTP/1 · plain | **31,350** | **31,350** | **14,882** | **14,882** | **26,348** | **26,348** | **TWP** |
| Reverse | HTTP/1 · TLS | HTTP/1 · plain | **36,054** | **36,054** | **11,173** | **11,173** | **39,527** | **39,527** | **YARP** |
| Reverse | HTTP/1 · TLS | HTTP/2 · TLS | **38,540** | **38,540** | *Not possible* | *Not possible* | **45,227** | **45,227** | **YARP** |
| Reverse | HTTP/1 · TLS | HTTP/3 · QUIC | **31,951** | **31,951** | *Not possible* (no QUIC) | *Not possible* | **33,028** | **33,028** | **YARP** |
| Reverse | HTTP/2 · plain | HTTP/1 · plain | **23,228** | **23,228** | *Not possible* | *Not possible* | **27,697** | **27,697** | **YARP** |
| Reverse | HTTP/2 · plain | HTTP/2 · plain | **102,819** | **102,819** | *Not possible* | *Not possible* | **85,909** | **85,909** | **TWP** |
| Reverse | HTTP/2 · plain | HTTP/2 · TLS | **89,043** | **89,043** | *Not possible* | *Not possible* | **69,282** | **69,282** | **TWP** |
| Reverse | HTTP/2 · plain | HTTP/3 · QUIC | **42,508** | **42,508** | *Not possible* (no QUIC) | *Not possible* | **43,520** | **43,520** | **YARP** |
| Reverse | HTTP/2 · TLS | HTTP/1 · plain | **63,120** | **63,120** | **4,755** | **10,796** | **27,669** | **27,669** | **TWP** |
| Reverse | HTTP/2 · TLS | HTTP/2 · plain | **88,742** | **88,742** | *Not possible* | *Not possible* | **68,761** | **68,761** | **TWP** |
| Reverse | HTTP/2 · TLS | HTTP/3 · QUIC | **17,141** | **17,141** | *Not possible* (no QUIC) | *Not possible* | **13,473** | **13,473** | **TWP** |
| Reverse | HTTP/3 · QUIC | HTTP/1 · plain | **23,785** | **23,785** | *Not possible* (no QUIC) | *Not possible* | **29,708** | **29,708** | **YARP** |
| Reverse | HTTP/3 · QUIC | HTTP/2 · TLS | **28,887** | **28,887** | *Not possible* (no QUIC) | *Not possible* | **36,040** | **36,040** | **YARP** |
| Reverse | HTTP/3 · QUIC | HTTP/3 · QUIC | **29,601** | **29,601** | *Not possible* (no QUIC) | *Not possible* | **25,968** | **25,968** | **TWP** |
| MITM | HTTP/1 · TLS | HTTP/1 · TLS | **39,415** | **39,415** | *Not possible* (no MITM) | *Not possible* | *Not possible* (no MITM) | *Not possible* | |
| MITM | HTTP/2 · TLS | HTTP/1 · TLS | **49,252** | **49,252** | *Not possible* (no MITM) | *Not possible* | *Not possible* (no MITM) | *Not possible* | |
| MITM | HTTP/2 · TLS | HTTP/2 · TLS | **103,815** | **103,815** | *Not possible* (no MITM) | *Not possible* | *Not possible* (no MITM) | *Not possible* | |
| MITM | HTTP/3 · QUIC | HTTP/1 · TLS | **21,626** | **21,626** | *Not possible* (no QUIC) | *Not possible* | *Not possible* (no MITM) | *Not possible* | |
| MITM | HTTP/3 · QUIC | HTTP/2 · TLS | **30,518** | **30,518** | *Not possible* (no QUIC) | *Not possible* | *Not possible* (no MITM) | *Not possible* | |
| MITM | HTTP/3 · QUIC | HTTP/3 · QUIC | **24,623** | **24,623** | *Not possible* (no QUIC) | *Not possible* | *Not possible* (no MITM) | *Not possible* | |

Windows sources: prior matched cool pairs plus **2026-08-20** High-perf cool remasures under `residual-sub08/quiet-remeasure/` (`h3-verbatim-fair/`, `h3-mitm-twin/`, `h2h1-final/`, …). Warmup 5–10s; measure 20s; concurrency 32. Absolute RPS swings with sequential-arm heat; prefer TWP÷YARP and MITM÷TWP-reverse-twin ratios over absolutes.

**Load generators:** Reverse inbound H3 arms (H3→H1, H3→H2, H3→H3) and matched H3→H1 MITM use **`dotnet-httpclient`** (`http_version=3.0`, `RequestVersionExact`) after dual-listen reverse H3. MITM H3→H2 / H3→H3 reuse dual-listen transparent reverse (`reverse-http3-to-http2`, `reverse-http3`). Older UDP-only `quic-http3` MITM H3→H1 numbers are **not** comparable to HttpClient reverse twins.

**Matched HttpClient TWP÷YARP (cool High-perf, YARP-first):** H3→H3 ≈ **1.14×** (29,601 / 25,968; TWP leads — session-lite + verbatim origin→client frame relay). H3→H2 ≈ **0.80** (28,887 / 36,040; ≥0.80). H3→H1 ≈ **0.80** (23,785 / 29,708; ≥0.80). H1→H2 ≈ **0.85** (38,540 / 45,227; ≥0.80). H1 plain ≈ **1.19×** (31,350 / 26,348; TWP leads). H1 TLS terminate ≈ **0.91** (36,054 / 39,527; ≥0.80). H2 TLS→h2c ≈ **1.29×** (88,742 / 68,761; TWP leads). h2c→h2c ≈ **1.20×** (102,819 / 85,909; TWP leads). h2c→H2 TLS ≈ **1.29×** (89,043 / 69,282; TWP leads) — decode-free indexed `:scheme` override. H1→H3 ≈ **0.97**. h2c→H3 ≈ **0.98**.

**MITM÷TWP reverse pass-through twin (cool):** Transparent H1 dual-TLS (`reverse-http1-mitm`) ÷ H1 TLS terminate ≈ **0.96** (39,415 / 40,980; ≥0.90). H2→H2 MITM ÷ H2 TLS→h2c ≈ **0.97+** (same-protocol compressed relay). H2→H1 MITM ÷ H2→H1 cleartext ≈ **0.90** (median of 5 cool pairs, mean **0.92**; `h2h1-final/`). After harden (`h2h1-harden/` 3-rep High-perf): median **1.03** / mean **0.91** (create-only SoftCap default `Clamp(ProcessorCount,4,32)`, always close on residual `DataAvailable`, cached TCP pool key, sync cert validation). Matched-HttpClient H3→H1 MITM ÷ H3→H1 cleartext ≈ **0.93** (21,626 / 23,341; ≥0.90). Explicit CONNECT `https-mitm` stays ~0.80× of terminate (CONNECT tax; not the fair twin).

**Why H3 absolute RPS ≪ H2 on this box:** tiny-GET loopback is not where H3 wins (see below). Cool YARP H3→H3 also tops out ~26–28k while TWP H2 same-protocol reaches ~90–100k — MsQuic + dual QUIC hops dominate, not TWP architecture. After the H2-style session-lite + verbatim response relay, TWP H3→H3 **leads** YARP.

nginx/Windows is a limited port — use it for **same-OS** comparison only, not as the industry nginx baseline.

**H2 TLS → H1 plain on Windows:** fair terminate — **TWP leads sustain (~1.05× YARP)** in the current table. Absolute RPS swings with background load; treat as same-OS only.

## Linux — Titanium vs nginx vs YARP

Median of **3 repeats** from Actions runs [32335537070](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32335537070) (`compare-same`), [32335538634](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32335538634) (`compare-terminate`), [32334393173](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32334393173) (`compare-bridges`), [32334395907](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32334395907) (`compare-mitm`). nginx HTTP/3 tiny-GET from [32337905168](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32337905168). Warmup 2s / measure 8s; concurrency 8, 16, 32, 64. **Linux nginx is the authoritative nginx baseline.** The RPS workflow installs nginx.org mainline (`http_v3_module`) and `libmsquic` (`QuicListener.IsSupported=true` on `ubuntu-latest`).

TWP÷nginx H1 plain reverse ≈ **0.67**; TWP÷YARP H1 plain ≈ **0.94**. Prefer ratios over absolute RPS on GHA VMs.

| Mode | Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak | Winner |
|---|---|---|---:|---:|---:|---:|---:|---:|---|
| Reverse | HTTP/1 · plain | HTTP/1 · plain | **26,808** | **26,808** | **39,886** | **39,886** | **28,662** | **28,662** | **nginx** |
| Reverse | HTTP/1 · TLS | HTTP/1 · plain | **22,592** | **22,592** | **32,234** | **32,234** | **25,863** | **25,863** | **nginx** |
| Reverse | HTTP/1 · TLS | HTTP/2 · TLS | **18,401** | **18,401** | *Not possible* | *Not possible* | **23,194** | **23,194** | **YARP** |
| Reverse | HTTP/1 · TLS | HTTP/3 · QUIC | **17,030** | **17,030** | *Not possible* (no H3 origin) | *Not possible* | **17,630** | **17,630** | **YARP** |
| Reverse | HTTP/2 · plain | HTTP/1 · plain | **40,459** | **40,459** | *Not possible* | *Not possible* | **40,441** | **40,441** | **TWP** |
| Reverse | HTTP/2 · plain | HTTP/2 · plain | **66,345** | **66,345** | *Not possible* | *Not possible* | **49,812** | **49,812** | **TWP** |
| Reverse | HTTP/2 · plain | HTTP/2 · TLS | **54,696** | **54,696** | *Not possible* | *Not possible* | **41,854** | **41,854** | **TWP** |
| Reverse | HTTP/2 · plain | HTTP/3 · QUIC | **29,621** | **29,621** | *Not possible* (no H3 origin) | *Not possible* | **27,928** | **27,928** | **TWP** |
| Reverse | HTTP/2 · TLS | HTTP/1 · plain | **38,702** | **38,702** | **16,257** | **22,708** | **34,189** | **34,189** | **TWP** |
| Reverse | HTTP/2 · TLS | HTTP/2 · plain | **59,721** | **59,721** | *Not possible* | *Not possible* | **40,008** | **40,008** | **TWP** |
| Reverse | HTTP/2 · TLS | HTTP/3 · QUIC | **28,063** | **28,063** | *Not possible* (no H3 origin) | *Not possible* | **23,698** | **23,698** | **TWP** |
| Reverse | HTTP/3 · QUIC | HTTP/1 · plain | **22,567** | **22,567** | **0** | **18,996** | **22,002** | **22,002** | **TWP** |
| Reverse | HTTP/3 · QUIC | HTTP/2 · TLS | **24,708** | **24,708** | *Not possible* (no H3→H2) | *Not possible* | **21,754** | **21,754** | **TWP** |
| Reverse | HTTP/3 · QUIC | HTTP/3 · QUIC | **20,587** | **20,587** | *Not possible* (no H3 origin) | *Not possible* | **16,736** | **16,736** | **TWP** |
| MITM | HTTP/1 · TLS | HTTP/1 · TLS | **30,703** | **30,703** | *Not possible* (no MITM) | *Not possible* | *Not possible* (no MITM) | *Not possible* | |
| MITM | HTTP/2 · TLS | HTTP/1 · TLS | **38,764** | **38,764** | *Not possible* (no MITM) | *Not possible* | *Not possible* (no MITM) | *Not possible* | |
| MITM | HTTP/2 · TLS | HTTP/2 · TLS | **52,372** | **52,372** | *Not possible* (no MITM) | *Not possible* | *Not possible* (no MITM) | *Not possible* | |
| MITM | HTTP/3 · QUIC | HTTP/1 · TLS | **24,036** | **24,036** | *Not possible* (no MITM) | *Not possible* | *Not possible* (no MITM) | *Not possible* | |
| MITM | HTTP/3 · QUIC | HTTP/2 · TLS | **26,746** | **26,746** | *Not possible* (no MITM) | *Not possible* | *Not possible* (no MITM) | *Not possible* | |
| MITM | HTTP/3 · QUIC | HTTP/3 · QUIC | **22,346** | **22,346** | *Not possible* (no MITM) | *Not possible* | *Not possible* (no MITM) | *Not possible* | |

On this GHA shape, TWP H1 plain ÷ nginx H1 plain ≈ **0.67** (26,808 / 39,886). H1 TLS terminate ≈ **0.70** (22,592 / 32,234). TWP÷YARP H1 plain ≈ **0.94**. Absolute RPS swings by VM; prefer the **ratio** and **median across repeats**.

**nginx HTTP/3:** inbound QUIC terminate → cleartext H1 is now in the harness (`nginx-reverse-http3-cleartext`, nginx.org 1.31.4). HttpClient/MsQuic negotiates `3.0` and peaks at **~19k** RPS, but the error rate stays above the 0.1% SLO (sustain **0**). nginx still cannot speak HTTP/3 to an origin (no H3 upstream in this conf), so H3-origin rows stay blank.

**YARP HTTP/3:** `compare-same` now includes H3→H3; `compare-bridges` fills H3→H2. TWP leads both (20,587 / 16,736 and 24,708 / 21,754).

### Tiny JSON reverse is nginx’s best case on Linux

The tables above use **~64 B keep-alive GET** on loopback. On Linux H1 reverse, nginx leads; YARP sits near TWP. Heavier bodies, POSTs, TLS handshake cost, and lossy/HOL workloads (below) change the picture. MITM rows remain TWP-only. nginx HTTP/3 is inbound-terminate only (see note above).

### Why isn’t HTTP/3 > HTTP/2 > HTTP/1 in raw RPS?

For **tiny JSON responses** (~64 B) on loopback, that ordering is **not** expected: topology (TLS hop count, terminate vs MITM) dominates; HTTP/2 and HTTP/3 help multiplexing, not single-origin tiny-GET RPS. See the **lossy** tables below for a workload where protocol design matters.

## Heavier reverse workloads

Separate from the tiny-GET matrix. Same measurement environments. Modes: `compare-bodies`, `compare-post`, `compare-lossy`, `compare-tls-cost` in [RpsLoadProbe](https://github.com/justcoding121/titanium-web-proxy/tree/develop/tools/RpsLoadProbe). **PUT with the same body is the same proxy work as POST; DELETE with no body matches GET** — only POST is published.

Lossy link = **userspace** shim (not kernel `netem`): TCP gets per-buffer delay + occasional whole-connection stalls (honest HOL for multiplexed H2); UDP datagram drop exists in the harness but **H3 lossy is not published** — rechecked at concurrency 8 after H2/H3 streaming work (`rps-ramp-20260817-212421`): TWP H3 through the UDP shim stayed at **0** sustain (multi-second p99); YARP H3 via the TCP shim also failed to establish. Treat as a **measurement limitation** (MsQuic + lossy shim), not a capability claim.

### Windows — heavier reverse GET (64 KiB / 256 KiB)

Warmup 1s / measure 3s; concurrency 8–64. Source: local `compare-bodies` (`rps-ramp-20260817-190233`).

| Body | Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak | Winner |
|---|---|---|---:|---:|---:|---:|---:|---:|---|
| 64 KiB | HTTP/1 · TLS | HTTP/1 · plain | **6,146** | **6,146** | **447** | **630** | **5,623** | **5,623** | **TWP** |
| 64 KiB | HTTP/2 · TLS | HTTP/1 · plain | **1,974** | **2,152** | **299** | **387** | **2,597** | **3,982** | **YARP** |
| 64 KiB | HTTP/3 · QUIC | HTTP/1 · plain | **866** | **1,950** | *Not possible* (no QUIC) | *Not possible* | **1,451** | **1,451** | **YARP** |
| 256 KiB | HTTP/1 · TLS | HTTP/1 · plain | **2,100** | **2,100** | **214** | **214** | **2,318** | **2,318** | **YARP** |
| 256 KiB | HTTP/2 · TLS | HTTP/1 · plain | **688** | **688** | **177** | **177** | **1,380** | **1,380** | **YARP** |
| 256 KiB | HTTP/3 · QUIC | HTTP/1 · plain | **656** | **748** | *Not possible* (no QUIC) | *Not possible* | **402** | **544** | **TWP** |

nginx/Windows collapses on large reverse bodies in this harness; treat as same-OS only.

### Linux — heavier reverse GET (64 KiB / 256 KiB)

Median of **3** repeats. Source: Actions [32335540288](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32335540288) (`compare-bodies`). Warmup 2s / measure 8s.

| Body | Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak | Winner |
|---|---|---|---:|---:|---:|---:|---:|---:|---|
| 64 KiB | HTTP/1 · TLS | HTTP/1 · plain | **6,213** | **6,241** | **7,874** | **7,904** | **6,339** | **6,383** | **nginx** |
| 64 KiB | HTTP/2 · TLS | HTTP/1 · plain | **4,612** | **4,612** | **3,417** | **3,433** | **4,735** | **4,735** | **YARP** |
| 64 KiB | HTTP/3 · QUIC | HTTP/1 · plain | **3,181** | **3,181** | *Not measured* | *Not measured* | **4,201** | **4,201** | **YARP** |
| 256 KiB | HTTP/1 · TLS | HTTP/1 · plain | **2,068** | **2,068** | **2,607** | **2,607** | **2,114** | **2,114** | **nginx** |
| 256 KiB | HTTP/2 · TLS | HTTP/1 · plain | **1,354** | **1,354** | **910** | **935** | **1,338** | **1,338** | **TWP** |
| 256 KiB | HTTP/3 · QUIC | HTTP/1 · plain | **1,184** | **1,184** | *Not measured* | *Not measured* | **1,238** | **1,238** | **YARP** |

On Linux H1 TLS, TWP÷nginx ≈ **0.79** at 64 KiB and ≈ **0.79** at 256 KiB — better than the tiny-GET ≈0.67 ratio, but nginx still leads sustain when both stay healthy. nginx H2 at 256 KiB stayed healthy this pass (~910 sustain). nginx HTTP/3 body arms ran before the dual-stack QUIC listen fix and never negotiated `h3` — left unmeasured rather than published as a fair zero.

### Windows — POST 64 KiB request + 64 KiB response

Source: local `compare-post` (`rps-ramp-20260818-064426`; warmup 2s; measure 5s; concurrency 8–64).

| Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak | Winner |
|---|---|---:|---:|---:|---:|---:|---:|---|
| HTTP/1 · TLS | HTTP/1 · plain | **3,348** | **3,583** | **259** | **296** | **3,100** | **3,100** | **TWP** |
| HTTP/2 · TLS | HTTP/1 · plain | **2,062** | **2,062** | **255** | **281** | **2,384** | **2,439** | **YARP** |
| HTTP/3 · QUIC | HTTP/1 · plain | **1,346** | **1,346** | *Not possible* | *Not possible* | **1,024** | **1,058** | **TWP** |

TWP wins the H1 and H3 POST arms and sits at ~**0.87** of YARP sustain on H2 POST.

### Linux — POST 64 KiB request + 64 KiB response

Median of **3** repeats. Source: Actions [32335541836](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32335541836) (`compare-post`).

| Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak | Winner |
|---|---|---:|---:|---:|---:|---:|---:|---|
| HTTP/1 · TLS | HTTP/1 · plain | **3,885** | **3,885** | **4,041** | **4,041** | **3,112** | **3,112** | **nginx** |
| HTTP/2 · TLS | HTTP/1 · plain | **2,763** | **2,763** | **1,929** | **1,990** | **2,468** | **2,525** | **TWP** |
| HTTP/3 · QUIC | HTTP/1 · plain | **0** | **2,033** | *Not measured* | *Not measured* | **2,592** | **2,592** | **YARP** |

Linux nginx H1/H2 POST completed this pass (nginx.org 1.31.4; the previous Ubuntu 1.24 arm returned 100% errors). TWP H3 POST peaked at **2,033** but did not hold the error/latency SLO (sustain **0**). nginx HTTP/3 POST used the pre-fix IPv4-only QUIC listen and is left unmeasured.

### Windows — lossy / high-RTT (H2 HOL)

Userspace **5 ms** one-way delay + **1%** connection stall; **64 KiB** GET. Source: local `compare-lossy` (`rps-ramp-20260817-190954`).

| Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak | Winner |
|---|---|---:|---:|---:|---:|---:|---:|---|
| HTTP/1 · TLS | HTTP/1 · plain | **637** | **637** | **375** | **375** | **634** | **634** | **TWP** |
| HTTP/2 · TLS | HTTP/1 · plain | **14** | **14** | **11** | **13** | **12** | **13** | **TWP** |

H1 scales with concurrency; H2 collapses under connection stalls (HOL). Absolute RPS is low because the shim delays every buffer — the point is the **protocol shape**, not competing with the tiny-GET table.

### Linux — lossy / high-RTT (H2 HOL)

Median of **3** repeats. Source: Actions [32335543372](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32335543372) (`compare-lossy`).

| Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak | Winner |
|---|---|---:|---:|---:|---:|---:|---:|---|
| HTTP/1 · TLS | HTTP/1 · plain | **1,168** | **1,168** | **1,220** | **1,220** | **1,217** | **1,217** | **nginx** |
| HTTP/2 · TLS | HTTP/1 · plain | **40** | **44** | **40** | **44** | **40** | **40** | **nginx** |

Same story as Windows: H1 stays usable; H2 falls to tens of RPS for all three products. Tiny-GET H1 leadership does not carry over.

### TLS termination cost (H1 TLS → cleartext origin)

Isolates keep-alive tiny GET vs **new connection per request** (handshake-dominated) vs keep-alive **256 KiB**. Product comparison uses RPS and end-to-end latency; TWP can also capture `ClientTlsTiming` when `TWP_RPS_CAPTURE_TLS=1` (child process) — nginx/YARP have no equivalent hook.

#### Windows

Source: local `compare-tls-cost` (`rps-ramp-20260817-191143`).

| Workload | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak | Winner |
|---|---:|---:|---:|---:|---:|---:|---|
| Keep-alive · tiny GET | **13,007** | **14,868** | **4,863** | **7,286** | **17,105** | **17,105** | **YARP** |
| New-connection · tiny GET | **484** | **484** | **380** | **409** | **555** | **555** | **YARP** |
| Keep-alive · 256 KiB GET | **1,651** | **1,772** | **171** | **171** | **2,038** | **2,038** | **YARP** |

#### Linux

Median of **3** repeats. Source: Actions [32335545066](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32335545066) (`compare-tls-cost`).

| Workload | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak | Winner |
|---|---:|---:|---:|---:|---:|---:|---|
| Keep-alive · tiny GET | **18,880** | **18,880** | **28,245** | **28,245** | **20,746** | **20,746** | **nginx** |
| New-connection · tiny GET | **1,106** | **1,113** | **1,005** | **1,009** | **957** | **957** | **TWP** |
| Keep-alive · 256 KiB GET | **2,126** | **2,126** | **2,627** | **2,627** | **2,134** | **2,134** | **nginx** |

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
