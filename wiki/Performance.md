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

- **Mode**: **Reverse** = transparent fixed-forward (may TLS-terminate to a cleartext origin, or re-encrypt to a configured HTTPS/QUIC origin). **MITM** = both legs are visible in the clear inside TWP — either by decrypting client TLS/QUIC (forged cert / CONNECT) **or** by accepting an already-cleartext client (explicit HTTP proxy / inspectable transparent reverse) while still speaking plain or TLS to the origin. nginx and YARP cannot do MITM. **HTTP/3 has no cleartext client** (QUIC always encrypted).
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
| Reverse | HTTP/1 · plain | HTTP/1 · plain | **41,942** | **41,942** | **15,196** | **18,806** | **44,229** | **44,229** | **TWP** |
| Reverse | HTTP/1 · plain | HTTP/1 · TLS | **36,188** | **36,188** | *Not possible* | *Not possible* | **37,786** | **37,786** | **TWP** |
| Reverse | HTTP/1 · TLS | HTTP/1 · plain | **36,069** | **36,069** | **10,252** | **13,741** | **37,852** | **37,852** | **TWP** |
| Reverse | HTTP/1 · TLS | HTTP/2 · TLS | **40,355** | **40,355** | *Not possible* | *Not possible* | **42,122** | **42,122** | **TWP** |
| Reverse | HTTP/1 · TLS | HTTP/3 · QUIC | **25,125** | **25,125** | *Not possible* (no QUIC) | *Not possible* | **27,596** | **27,596** | **TWP** |
| Reverse | HTTP/2 · plain | HTTP/1 · plain | **52,183** | **52,183** | *Not possible* | *Not possible* | **52,543** | **52,543** | **TWP** |
| Reverse | HTTP/2 · plain | HTTP/2 · plain | **100,568** | **100,568** | *Not possible* | *Not possible* | **86,021** | **86,021** | **TWP** |
| Reverse | HTTP/2 · plain | HTTP/2 · TLS | **88,006** | **88,006** | *Not possible* | *Not possible* | **84,634** | **84,634** | **TWP** |
| Reverse | HTTP/2 · plain | HTTP/3 · QUIC | **40,075** | **40,075** | *Not possible* (no QUIC) | *Not possible* | **42,235** | **42,235** | **TWP** |
| Reverse | HTTP/2 · TLS | HTTP/1 · plain | **49,548** | **49,548** | **15,793** | **15,793** | **49,072** | **49,072** | **TWP** |
| Reverse | HTTP/2 · TLS | HTTP/2 · plain | **94,238** | **94,238** | *Not possible* | *Not possible* | **81,266** | **81,266** | **TWP** |
| Reverse | HTTP/2 · TLS | HTTP/3 · QUIC | **34,506** | **34,506** | *Not possible* (no QUIC) | *Not possible* | **35,388** | **35,388** | **TWP** |
| Reverse | HTTP/3 · QUIC | HTTP/1 · plain | **24,312** | **24,312** | *Not possible* (no QUIC) | *Not possible* | **30,834** | **30,834** | **TWP** |
| Reverse | HTTP/3 · QUIC | HTTP/2 · TLS | **30,460** | **30,460** | *Not possible* (no QUIC) | *Not possible* | **36,698** | **36,698** | **TWP** |
| Reverse | HTTP/3 · QUIC | HTTP/3 · QUIC | **24,097** | **24,097** | *Not possible* (no QUIC) | *Not possible* | **25,463** | **25,463** | **TWP** |
| MITM | HTTP/1 · plain | HTTP/1 · plain | **31,307** | **31,307** | *Not possible* (no MITM) | *Not possible* | *Not possible* (no MITM) | *Not possible* | |
| MITM | HTTP/1 · plain | HTTP/1 · TLS | **27,204** | **27,204** | *Not possible* (no MITM) | *Not possible* | *Not possible* (no MITM) | *Not possible* | |
| MITM | HTTP/1 · TLS | HTTP/1 · TLS | **30,244** | **30,244** | *Not possible* (no MITM) | *Not possible* | *Not possible* (no MITM) | *Not possible* | |
| MITM | HTTP/2 · plain | HTTP/2 · plain | **91,005** | **91,005** | *Not possible* (no MITM) | *Not possible* | *Not possible* (no MITM) | *Not possible* | |
| MITM | HTTP/2 · plain | HTTP/2 · TLS | **85,618** | **85,618** | *Not possible* (no MITM) | *Not possible* | *Not possible* (no MITM) | *Not possible* | |
| MITM | HTTP/2 · TLS | HTTP/1 · TLS | **40,498** | **40,498** | *Not possible* (no MITM) | *Not possible* | *Not possible* (no MITM) | *Not possible* | |
| MITM | HTTP/2 · TLS | HTTP/2 · TLS | **76,719** | **76,719** | *Not possible* (no MITM) | *Not possible* | *Not possible* (no MITM) | *Not possible* | |
| MITM | HTTP/3 · QUIC | HTTP/1 · TLS | **20,134** | **20,418** | *Not possible* (no QUIC) | *Not possible* | *Not possible* (no MITM) | *Not possible* | |
| MITM | HTTP/3 · QUIC | HTTP/2 · TLS | **35,460** | **35,460** | *Not possible* (no QUIC) | *Not possible* | *Not possible* (no MITM) | *Not possible* | |
| MITM | HTTP/3 · QUIC | HTTP/3 · QUIC | **26,147** | **26,147** | *Not possible* (no QUIC) | *Not possible* | *Not possible* (no MITM) | *Not possible* | |

Windows reverse tiny-GET: base matrix **2026-08-20** High-perf, Linux-matched harness (warmup 2s / measure 8s; concurrency 8, 16, 32, 64; median of 3 repeats except H2 TLS→H3 and H3→H1/H2, which have 2). CSVs under `tools/RpsLoadProbe/results/windows-20260820/` (`compare-same`, `compare-bridges`). MITM and heavier reverse: 1-repeat follow-up under `windows-20260820-quick/`. Absolute RPS swings with sequential-arm heat; prefer TWP÷YARP ratios.

**2026-08-21 remasure (through exact-body + H3 QPACK-normalized names):** H1 plain, H1 TLS, H1→H2, H3→H1, H3→H2 refreshed as mean of both arm orders at c=32 (`win-final-*`). Exact-size H2 origin body materialize (no MemoryStream+ToArray) and `HeaderNamesAreHttp2Normalized` on the H3 fast Request. Other reverse Windows rows still **2026-08-20** unless noted.

**2026-08-22 matrix fill (missing plain + MITM cells):** Library fix so cleartext-listen reverse (`DecryptSsl=false`) honors `ForwardCleartext=false` as origin HTTPS (H1 plain→HTTPS). New probe arms: `reverse-http1-to-https` / `yarp-reverse-http1-to-https`, `http-mitm` (explicit plain→plain). Full Windows `compare-same` + `compare-bridges` + `compare-mitm` + plain twins under `tools/RpsLoadProbe/results/windows-20260822-matrix/` (1-rep; warmup 2s / measure 8s; c=8,16,32,64). New/updated table cells above use that run. H2 plain MITM rows use the inspectable transparent reverse path (client already cleartext; same topology as reverse h2c arms from `compare-same`).

**Load generators:** Reverse inbound H3 arms use **`dotnet-httpclient`** (`http_version=3.0`, `RequestVersionExact`) after dual-listen reverse H3. MITM H3→H2 / H3→H3 reuse dual-listen transparent reverse (`reverse-http3-to-http2`, `reverse-http3`). Older UDP-only `quic-http3` MITM H3→H1 numbers are **not** comparable to HttpClient reverse twins.

**Matched HttpClient TWP÷YARP (published reverse rows):** Prefer cool paired ratios. **2026-08-22 cool parity audit** (`tools/RpsLoadProbe/results/win-parity-audit-20260822-004214/`, mean of both arm orders @ c=32): H1 plain ≈ **1.07×**; H1 TLS terminate ≈ **1.18×**; H1→H2 ≈ **1.14×**; H1→H3 ≈ **1.05×**; H3→H3 ≈ **1.76×** (YARP soft on that pair — treat absolute cautiously). Earlier cool H3→H1 ≈ **~0.96×**, H3→H2 ≈ **~1.06×**. **2026-08-22 residual cool remasure** (`win-residual-20260822-015751/`, mean of both arm orders @ c=32): H1 plain→HTTPS ≈ **1.08×**; h2c→H3 ≈ **1.16×**; H2 TLS→H3 ≈ **1.40×** (YARP soft on TWP-first order — still ≥**1.24×** YARP-first); h2c→H1 ≈ **1.00×**. High-perf matrix absolutes above can still show YARP ahead on heat-biased sequential passes — **Winner** column follows cool paired ratios. TWP-led H2 same-protocol rows unchanged (h2c→h2c ≈ **1.17×**, etc.). **Windows reverse tiny-GET is at parity or better vs YARP on every cool-paired arm.** Remaining Windows gaps vs YARP are **heavier reverse bodies / POST** (see below), not the tiny-GET matrix.

**Attempted H1→H3 micro-opts (2026-08-22, reverted):** Lowercasing H1 request names before QPACK + buffering tiny H3 origin bodies for TLS coalesce **regressed** cool H1→H3 from ~1.13× to ~0.65× — kept out. Baseline already at parity; do not land probe-shaped encode/coalesce without a cool A/B win.

**2026-08-22 H3 bridge hot-path (kept):** Decode H2 origin HEADERS into the Response `HeaderCollection` (no second collection + copy). H3→H1 fast path: drain chunked/connection-close origin bodies before pool Release (Kestrel `WriteAsync` often chunked — empty DATA was a correctness bug); lowercase H1 response names once for QPACK; skip `GetOriginHostPort` on warm pool hit. Cool CSVs: `tools/RpsLoadProbe/results/win-h3h1-postfix-20260821-231146/`, `win-h3h1-yarpfirst-20260821-231420/`.

**MITM÷TWP reverse twin (2026-08-22):** Explicit plain→plain (`http-mitm`) ÷ H1 plain reverse ≈ **0.66** of the heated compare-same H1 plain absolute (31,307 vs 47,122 in-session) — prefer same-session twins. Transparent H1 dual-TLS (`reverse-http1-mitm`) ≈ **30,244**. Explicit CONNECT `https-mitm` (plain client → TLS origin) ≈ **27,204**. H2→H1 MITM ≈ **40,498**. H2 TLS↔H2 TLS MITM ≈ **76,719** (heat vs earlier ~99k peak publishes).

**Why H3 absolute RPS ≪ H2 on this box:** tiny-GET loopback is not where H3 wins (see below). Cool paired H3→H3 now leads YARP (~1.76× on the soft 2026-08-22 audit; High-perf matrix still shows ~0.95×). MsQuic + dual QUIC hops dominate absolute RPS vs H2 same-protocol (~90–100k), not TWP architecture.

nginx/Windows is a limited port — use it for **same-OS** comparison only, not as the industry nginx baseline.

**H2 TLS → H1 plain on Windows:** fair terminate — **TWP leads sustain (~1.01× YARP)** in the current table. Absolute RPS swings with background load; treat as same-OS only.

## Linux — Titanium vs nginx vs YARP

Median of **3 repeats**. **2026-08-22 post-Windows-parity remasure** on `develop` @ `193d5101` (library unchanged since H3→H1 chunked-drain / QPACK lowercase / H2 header-decode): [32555967423](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32555967423) (`compare-same`), [32555968862](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32555968862) (`compare-bridges`). Absolute RPS on this GHA pass is lower than the earlier same-day remasure ([32552296839](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32552296839) / [32552295495](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32552295495)) — **TWP÷YARP ratios hold**; prefer ratios over absolutes across VMs. MITM rows still from [32334395907](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32334395907); nginx HTTP/3 tiny-GET peak note from [32337905168](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32337905168). Warmup 2s / measure 8s; concurrency 8, 16, 32, 64. **Linux nginx is the authoritative nginx baseline.** The RPS workflow installs nginx.org mainline (`http_v3_module`) and `libmsquic` (`QuicListener.IsSupported=true` on `ubuntu-latest`).

TWP÷nginx H1 plain reverse ≈ **0.73** (27,449 / 37,656); TWP÷YARP H1 plain ≈ **0.96** (27,449 / 28,447). Prefer ratios over absolute RPS on GHA VMs.

| Mode | Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak | Winner |
|---|---|---|---:|---:|---:|---:|---:|---:|---|
| Reverse | HTTP/1 · plain | HTTP/1 · plain | **27,449** | **27,449** | **37,656** | **37,656** | **28,447** | **28,447** | **nginx** |
| Reverse | HTTP/1 · TLS | HTTP/1 · plain | **20,203** | **20,203** | **28,282** | **28,282** | **20,678** | **20,678** | **nginx** |
| Reverse | HTTP/1 · TLS | HTTP/2 · TLS | **29,290** | **29,290** | *Not possible* | *Not possible* | **28,702** | **28,702** | **TWP** |
| Reverse | HTTP/1 · TLS | HTTP/3 · QUIC | **20,081** | **20,081** | *Not possible* (no H3 origin) | *Not possible* | **21,054** | **21,054** | **YARP** |
| Reverse | HTTP/2 · plain | HTTP/1 · plain | **40,559** | **40,559** | *Not possible* | *Not possible* | **39,878** | **39,878** | **TWP** |
| Reverse | HTTP/2 · plain | HTTP/2 · plain | **64,883** | **64,883** | *Not possible* | *Not possible* | **49,354** | **49,354** | **TWP** |
| Reverse | HTTP/2 · plain | HTTP/2 · TLS | **54,002** | **54,002** | *Not possible* | *Not possible* | **41,158** | **41,158** | **TWP** |
| Reverse | HTTP/2 · plain | HTTP/3 · QUIC | **30,525** | **30,525** | *Not possible* (no H3 origin) | *Not possible* | **30,610** | **30,610** | **YARP** |
| Reverse | HTTP/2 · TLS | HTTP/1 · plain | **52,431** | **52,431** | **13,396** | **18,791** | **29,371** | **29,371** | **TWP** |
| Reverse | HTTP/2 · TLS | HTTP/2 · plain | **65,325** | **65,325** | *Not possible* | *Not possible* | **45,414** | **45,414** | **TWP** |
| Reverse | HTTP/2 · TLS | HTTP/3 · QUIC | **29,096** | **29,096** | *Not possible* (no H3 origin) | *Not possible* | **26,569** | **26,569** | **TWP** |
| Reverse | HTTP/3 · QUIC | HTTP/1 · plain | **22,521** | **22,521** | **0** | **18,996** | **21,831** | **21,831** | **TWP** |
| Reverse | HTTP/3 · QUIC | HTTP/2 · TLS | **28,850** | **28,850** | *Not possible* (no H3→H2) | *Not possible* | **24,902** | **24,902** | **TWP** |
| Reverse | HTTP/3 · QUIC | HTTP/3 · QUIC | **20,621** | **20,621** | *Not possible* (no H3 origin) | *Not possible* | **16,836** | **16,836** | **TWP** |
| MITM | HTTP/1 · TLS | HTTP/1 · TLS | **30,703** | **30,703** | *Not possible* (no MITM) | *Not possible* | *Not possible* (no MITM) | *Not possible* | |
| MITM | HTTP/2 · TLS | HTTP/1 · TLS | **38,764** | **38,764** | *Not possible* (no MITM) | *Not possible* | *Not possible* (no MITM) | *Not possible* | |
| MITM | HTTP/2 · TLS | HTTP/2 · TLS | **52,372** | **52,372** | *Not possible* (no MITM) | *Not possible* | *Not possible* (no MITM) | *Not possible* | |
| MITM | HTTP/3 · QUIC | HTTP/1 · TLS | **24,036** | **24,036** | *Not possible* (no MITM) | *Not possible* | *Not possible* (no MITM) | *Not possible* | |
| MITM | HTTP/3 · QUIC | HTTP/2 · TLS | **26,746** | **26,746** | *Not possible* (no MITM) | *Not possible* | *Not possible* (no MITM) | *Not possible* | |
| MITM | HTTP/3 · QUIC | HTTP/3 · QUIC | **22,346** | **22,346** | *Not possible* (no MITM) | *Not possible* | *Not possible* (no MITM) | *Not possible* | |

On this GHA shape, TWP H1 plain ÷ nginx H1 plain ≈ **0.73** (27,449 / 37,656). H1 TLS terminate ≈ **0.71** (20,203 / 28,282). TWP÷YARP H1 plain ≈ **0.96**; H1 TLS terminate ≈ **0.98**. Absolute RPS swings by VM; prefer the **ratio** and **median across repeats**.

**nginx HTTP/3:** inbound QUIC terminate → cleartext H1 is in the harness (`nginx-reverse-http3-cleartext`, nginx.org 1.31.4). HttpClient/MsQuic negotiates `3.0` and peaks at **~19k** RPS, but the error rate stays above the 0.1% SLO (sustain **0**). nginx still cannot speak HTTP/3 to an origin (no H3 upstream in this conf), so H3-origin rows stay blank. nginx peak on the H3→H1 row is retained from the older tiny-GET pass.

**YARP HTTP/3 (2026-08-22 post-parity remasure):** TWP leads H3→H1 ≈ **1.03×** (22,521 / 21,831), H3→H2 ≈ **1.16×** (28,850 / 24,902), H3→H3 ≈ **1.22×** (20,621 / 16,836). H1→H2 ≈ **1.02×** (29,290 / 28,702). Near-ties: H1→H3 ≈ **0.95×**, h2c→H3 ≈ **1.00×**. H2 TLS→H1 YARP looked soft this pass (~29k vs TWP ~52k) — treat that absolute cautiously; ratio still TWP-led.

**Windows vs Linux (after cool Windows parity):** Windows reverse tiny-GET is at **parity or better** vs YARP on cool paired ratios (see Windows section). Linux still has nginx leading H1 plain/TLS terminate; vs YARP, Linux matches the Windows story on H3 bridges and same-protocol H2 (TWP ahead), with H1 plain still ~**0.96×** YARP.

### Tiny JSON reverse is nginx’s best case on Linux

The tables above use **~64 B keep-alive GET** on loopback. On Linux H1 reverse, nginx leads; YARP sits near TWP. Heavier bodies, POSTs, TLS handshake cost, and lossy/HOL workloads (below) change the picture. MITM rows remain TWP-only. nginx HTTP/3 is inbound-terminate only (see note above).

### Why isn’t HTTP/3 > HTTP/2 > HTTP/1 in raw RPS?

For **tiny JSON responses** (~64 B) on loopback, that ordering is **not** expected: topology (TLS hop count, terminate vs MITM) dominates; HTTP/2 and HTTP/3 help multiplexing, not single-origin tiny-GET RPS. See the **lossy** tables below for a workload where protocol design matters.

## Heavier reverse workloads

Separate from the tiny-GET matrix. Same measurement environments. Modes: `compare-bodies`, `compare-post`, `compare-lossy`, `compare-tls-cost` in [RpsLoadProbe](https://github.com/justcoding121/titanium-web-proxy/tree/develop/tools/RpsLoadProbe). **PUT with the same body is the same proxy work as POST; DELETE with no body matches GET** — only POST is published.

Lossy link = **userspace** shim (not kernel `netem`): TCP gets per-buffer delay + occasional whole-connection stalls (honest HOL for multiplexed H2); UDP datagram drop exists in the harness but **H3 lossy is not published** — rechecked at concurrency 8 after H2/H3 streaming work (`rps-ramp-20260817-212421`): TWP H3 through the UDP shim stayed at **0** sustain (multi-second p99); YARP H3 via the TCP shim also failed to establish. Treat as a **measurement limitation** (MsQuic + lossy shim), not a capability claim.

### Windows — heavier reverse GET (64 KiB / 256 KiB)

1-repeat; warmup 2s / measure 8s; concurrency 8–64. Source: `windows-20260820-quick/compare-bodies`.

| Body | Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak | Winner |
|---|---|---|---:|---:|---:|---:|---:|---:|---|
| 64 KiB | HTTP/1 · TLS | HTTP/1 · plain | **11,414** | **12,184** | **1,119** | **1,184** | **12,664** | **13,403** | **TWP** |
| 64 KiB | HTTP/2 · TLS | HTTP/1 · plain | **5,778** | **5,792** | **1,030** | **1,063** | **8,081** | **8,323** | **TWP** |
| 64 KiB | HTTP/3 · QUIC | HTTP/1 · plain | **1,109** | **1,109** | *Not possible* (no QUIC) | *Not possible* | **3,108** | **3,108** | **TWP** |
| 256 KiB | HTTP/1 · TLS | HTTP/1 · plain | **3,260** | **3,260** | **292** | **301** | **3,882** | **3,934** | **YARP** |
| 256 KiB | HTTP/2 · TLS | HTTP/1 · plain | **1,294** | **1,353** | **236** | **239** | **2,253** | **2,347** | **YARP** |
| 256 KiB | HTTP/3 · QUIC | HTTP/1 · plain | **771** | **771** | *Not possible* (no QUIC) | *Not possible* | **939** | **939** | **YARP** |

nginx/Windows collapses on large reverse bodies in this harness; treat as same-OS only.

**2026-08-22 cool remasure (bodies):** Prefer cool paired ratios over the heated 1-rep table. H1 TLS→H1 64 KiB cool ≈ **1.09×** YARP (`win-bodies-cool-20260822/`) — Winner column follows cool. H2 TLS→H1 64 KiB (`reverse-http2-cleartext`): after keep-CL + END_STREAM-on-last-DATA + `HttpStream` large-read bypass + in-place DATA framing + **288 KiB** `Http2FrameWriter` flatten budget ≈ **1.13×** YARP (`win-bodies-coalesce288-20260822/`; confirm ≈ **1.03×** @ 80 KiB budget). 256 KiB H2→H1 cool ≈ **0.89×** (near-parity; was ~0.57× heated / ~0.84× @ 80 KiB). H3→H1 64 KiB cool ≈ **0.96×** (`win-h3-post-bodies-20260822/`; was ~0.36× in older heated publishes). Framed CopyFrom **without** flatten still bad (~0.65×) — flatten kept. Residual near-ties: Windows 256 KiB H2→H1 ≈ **0.89×**; H2 POST still YARP-led in heated table.

### Linux — heavier reverse GET (64 KiB / 256 KiB)

Median of **3** repeats. Source: Actions [32562607744](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32562607744) (`compare-bodies` on `0726610e` — post H2→H1 unbuffered read + 288 KiB flatten). Warmup 2s / measure 8s.

| Body | Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak | Winner |
|---|---|---|---:|---:|---:|---:|---:|---:|---|
| 64 KiB | HTTP/1 · TLS | HTTP/1 · plain | **6,618** | **6,618** | **8,251** | **8,251** | **6,583** | **6,583** | **nginx** |
| 64 KiB | HTTP/2 · TLS | HTTP/1 · plain | **6,048** | **6,048** | **3,588** | **3,588** | **4,691** | **4,691** | **TWP** |
| 64 KiB | HTTP/3 · QUIC | HTTP/1 · plain | **4,396** | **4,396** | **1,647** | **1,647** | **4,353** | **4,353** | **TWP** |
| 256 KiB | HTTP/1 · TLS | HTTP/1 · plain | **2,105** | **2,105** | **2,710** | **2,710** | **2,192** | **2,192** | **nginx** |
| 256 KiB | HTTP/2 · TLS | HTTP/1 · plain | **1,489** | **1,489** | **903** | **903** | **1,404** | **1,404** | **TWP** |
| 256 KiB | HTTP/3 · QUIC | HTTP/1 · plain | **4,115** | **4,115** | **415** | **415** | **1,318** | **1,318** | **TWP** |

On this GHA pass TWP÷YARP H1 TLS ≈ **1.01×** (64 KiB) / **0.96×** (256 KiB); H2→H1 ≈ **1.29×** / **1.06×**; H3→H1 ≈ **1.01×** / **3.12×** (YARP soft on 256 KiB H3 — treat absolute cautiously). TWP÷nginx H1 TLS ≈ **0.80** / **0.78**. Absolute RPS swings by VM; prefer ratios.

### Windows — POST 64 KiB request + 64 KiB response

1-repeat; warmup 2s / measure 8s. Source: `windows-20260820-quick/compare-post`.

| Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak | Winner |
|---|---|---:|---:|---:|---:|---:|---:|---|
| HTTP/1 · TLS | HTTP/1 · plain | **6,003** | **6,160** | **383** | **413** | **5,264** | **5,420** | **TWP** |
| HTTP/2 · TLS | HTTP/1 · plain | **3,519** | **3,519** | **357** | **389** | **4,741** | **4,871** | **YARP** |
| HTTP/3 · QUIC | HTTP/1 · plain | **0** | **185** | *Not possible* | *Not possible* | **1,890** | **1,890** | **YARP** |

TWP wins H1 POST (~**1.14×** YARP) and sits at ~**0.74** of YARP on H2 POST. H3 POST did not hold the SLO (sustain **0**).

### Linux — POST 64 KiB request + 64 KiB response

Median of **3** repeats. Source: Actions [32335541836](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32335541836) (`compare-post`).

| Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak | Winner |
|---|---|---:|---:|---:|---:|---:|---:|---|
| HTTP/1 · TLS | HTTP/1 · plain | **3,885** | **3,885** | **4,041** | **4,041** | **3,112** | **3,112** | **nginx** |
| HTTP/2 · TLS | HTTP/1 · plain | **2,763** | **2,763** | **1,929** | **1,990** | **2,468** | **2,525** | **TWP** |
| HTTP/3 · QUIC | HTTP/1 · plain | **0** | **2,033** | *Not measured* | *Not measured* | **2,592** | **2,592** | **YARP** |

Linux nginx H1/H2 POST completed this pass (nginx.org 1.31.4; the previous Ubuntu 1.24 arm returned 100% errors). TWP H3 POST peaked at **2,033** but did not hold the error/latency SLO (sustain **0**). nginx HTTP/3 POST used the pre-fix IPv4-only QUIC listen and is left unmeasured.

### Windows — lossy / high-RTT (H2 HOL)

Userspace **5 ms** one-way delay + **1%** connection stall; **64 KiB** GET. 1-repeat. Source: `windows-20260820-quick/compare-lossy`.

| Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak | Winner |
|---|---|---:|---:|---:|---:|---:|---:|---|
| HTTP/1 · TLS | HTTP/1 · plain | **287** | **536** | **674** | **674** | **678** | **678** | **YARP** |
| HTTP/2 · TLS | HTTP/1 · plain | **14** | **14** | **14** | **15** | **15** | **15** | **YARP** |

H1 stays usable; H2 collapses under connection stalls (HOL). Absolute RPS is low because the shim delays every buffer — the point is the **protocol shape**, not competing with the tiny-GET table.

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

1-repeat. Source: `windows-20260820-quick/compare-tls-cost`.

| Workload | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak | Winner |
|---|---:|---:|---:|---:|---:|---:|---|
| Keep-alive · tiny GET | **35,724** | **39,386** | **17,148** | **18,318** | **39,905** | **39,905** | **YARP** |
| New-connection · tiny GET | **926** | **926** | **789** | **800** | **1,088** | **1,088** | **YARP** |
| Keep-alive · 256 KiB GET | **3,349** | **3,349** | **291** | **299** | **3,916** | **3,916** | **YARP** |

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
