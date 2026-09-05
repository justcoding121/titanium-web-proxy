# Performance Local Lab

Local Windows laptop debugging / cool A/B tables. **Not publishable** � do not compare these absolutes to [Performance](Performance) GHA tables. Use cool paired ratios as a gate, then remeasure on matched Windows+Linux GHA and paste CI medians onto Performance.

Playbook (harness, dumps, stage timing, Memory techniques) stays on [Performance Profiling](Performance-Profiling).

## Contents

- [Local Windows lab (developer laptop)](#local-windows-lab-developer-laptop)
- [Local saturation control (laptop)](#local-saturation-control-laptop)
- [Editions (CLI / Plus stress)](#editions-cli--plus-stress)

## Local Windows lab (developer laptop)

Local debug setup and historical High-perf / cool-paired tables. **Do not paste these absolutes onto [Performance](Performance)** — that page is CI-only. Use this section to iterate: cool A/B, then publish from GHA.

### Measurement environment


|         |                                                                 |
| ------- | --------------------------------------------------------------- |
| OS      | Windows 11 (10.0.26200)                                         |
| CPU     | 11th Gen Intel Core i7-1185G7 @ 3.00 GHz (8 logical processors) |
| RAM     | 31.8 GiB                                                        |
| Runtime | .NET 10.0.10                                                    |
| nginx   | nginx/Windows **1.31.3**                                        |
| YARP    | Yarp.ReverseProxy **2.3.0**                                     |
| Harness | RpsLoadProbe Release; arms run **sequentially**                 |


This box is **8 logical / ~32 GiB** — not the 4 vCPU / 16 GiB GHA class. Treat ratios as the local gate; remeasure on CI before claiming a publishable win.

**Cool** = ~2 min idle, then paired A/B (alternate who goes first; **mean of both orders @ c=32**) — **authoritative local gate**; reverse tiny-GET / body cells below use those cool absolutes when cited. **Heated** = long sequential matrix (thermal skew). **🥇** = higher cool sustain in that row (or heated sustain only when no cool pair exists — noted).

### Windows — Titanium vs nginx vs YARP (laptop)

Client / origin: HTTP version and whether TLS is used (`plain` = cleartext, `TLS` = encrypted, `QUIC` = HTTP/3).

Reverse TWP/YARP cells for cool-audited arms are **cool paired means** (see notes). nginx cells remain the older heated High-perf baseline (nginx was not in the cool pairs). MITM rows below are historical heated 1-rep laptop numbers (`windows-20260822-mitm-full/`); publishable **true MITM** (handlers on) is the separate TWP-only table on [Performance](Performance) from `compare-product` (no nginx/YARP MITM columns). Laptop MITM peer columns here are placeholders (*Not possible*).


| Mode    | Client         | Origin         | TWP sustain    | TWP peak    | TWP Memory | TWP CPU % | nginx sustain            | nginx peak     | YARP sustain             | YARP peak      |
| ------- | -------------- | -------------- | -------------- | ----------- | ---------- | --------- | ------------------------ | -------------- | ------------------------ | -------------- |
| Reverse | HTTP/1 · plain | HTTP/1 · plain | 🥇 **41,390**  | **41,390**  | **81 MiB** | **40.3** | **15,196**               | **18,806**     | **38,772**               | **38,772**     |
| Reverse | HTTP/1 · plain | HTTP/1 · TLS   | 🥇 **30,844**  | **30,844**  | **104 MiB** | **44.2** | *Not possible*           | *Not possible* | **30,621**               | **30,621**     |
| Reverse | HTTP/1 · TLS   | HTTP/1 · plain | 🥇 **35,205**  | **35,205**  | **91 MiB** | **42.1** | **10,252**               | **13,741**     | **29,750**               | **29,750**     |
| Reverse | HTTP/1 · TLS   | HTTP/2 · TLS   | 🥇 **25,540**  | **25,540**  | **141 MiB** | **43.9** | *Not possible*           | *Not possible* | **24,920**               | **24,920**     |
| Reverse | HTTP/1 · TLS   | HTTP/3 · QUIC  | 🥇 **21,819**  | **21,819**  | **121 MiB** | **37.1** | *Not possible* (no QUIC) | *Not possible* | **20,712**               | **20,712**     |
| Reverse | HTTP/2 · plain | HTTP/1 · plain | 🥇 **46,517**  | **46,517**  | **190 MiB** | **46** | *Not possible*           | *Not possible* | **42,994**               | **42,994**     |
| Reverse | HTTP/2 · plain | HTTP/2 · plain | 🥇 **100,568** | **100,568** | **81 MiB** | **36.8** | *Not possible*           | *Not possible* | **86,021**               | **86,021**     |
| Reverse | HTTP/2 · plain | HTTP/2 · TLS   | 🥇 **88,006**  | **88,006**  | **89 MiB** | **33.8** | *Not possible*           | *Not possible* | **84,634**               | **84,634**     |
| Reverse | HTTP/2 · plain | HTTP/3 · QUIC  | 🥇 **27,493**  | **27,493**  | **189 MiB** | **42.5** | *Not possible* (no QUIC) | *Not possible* | **24,535**               | **24,535**     |
| Reverse | HTTP/2 · TLS   | HTTP/1 · plain | 🥇 **49,548**  | **49,548**  | **207 MiB** | **40.6** | **15,793**               | **15,793**     | **49,072**               | **49,072**     |
| Reverse | HTTP/2 · TLS   | HTTP/2 · plain | 🥇 **94,238**  | **94,238**  | **98 MiB** | **33.1** | *Not possible*           | *Not possible* | **81,266**               | **81,266**     |
| Reverse | HTTP/2 · TLS   | HTTP/3 · QUIC  | 🥇 **30,813**  | **30,813**  | **192 MiB** | **43.9** | *Not possible* (no QUIC) | *Not possible* | **24,039**               | **24,039**     |
| Reverse | HTTP/3 · QUIC  | HTTP/1 · plain | **22,325**     | **22,325**  | **236 MiB** | **37** | *Not possible* (no QUIC) | *Not possible* | 🥇 **23,773**            | **23,773**     |
| Reverse | HTTP/3 · QUIC  | HTTP/2 · TLS   | 🥇 **22,297**  | **22,297**  | **224 MiB** | **33.8** | *Not possible* (no QUIC) | *Not possible* | **21,914**               | **21,914**     |
| Reverse | HTTP/3 · QUIC  | HTTP/3 · QUIC  | 🥇 **26,299**  | **26,299**  | **263 MiB** | **38.2** | *Not possible* (no QUIC) | *Not possible* | **14,942**               | **14,942**     |
| MITM    | HTTP/1 · plain | HTTP/1 · plain | **33,484**     | **33,484**  | **77 MiB** | **44.9** | *Not possible* (no MITM) | *Not possible* | *Not possible* (no MITM) | *Not possible* |
| MITM    | HTTP/1 · plain | HTTP/1 · TLS   | **38,193**     | **38,193**  | **101 MiB** | **43.1** | *Not possible* (no MITM) | *Not possible* | *Not possible* (no MITM) | *Not possible* |
| MITM    | HTTP/1 · TLS   | HTTP/1 · plain | **36,037**     | **36,037**  | **89 MiB** | **43.1** | *Not possible* (no MITM) | *Not possible* | *Not possible* (no MITM) | *Not possible* |
| MITM    | HTTP/1 · TLS   | HTTP/2 · TLS   | **42,652**     | **42,652**  | **133 MiB** | **44.8** | *Not possible* (no MITM) | *Not possible* | *Not possible* (no MITM) | *Not possible* |
| MITM    | HTTP/1 · TLS   | HTTP/3 · QUIC  | **28,114**     | **28,114**  | **121 MiB** | **42** | *Not possible* (no QUIC) | *Not possible* | *Not possible* (no MITM) | *Not possible* |
| MITM    | HTTP/2 · plain | HTTP/1 · plain | **45,905**     | **45,905**  | **209 MiB** | **46.2** | *Not possible* (no MITM) | *Not possible* | *Not possible* (no MITM) | *Not possible* |
| MITM    | HTTP/2 · plain | HTTP/2 · plain | **98,069**     | **98,069**  | **78 MiB** | **33.4** | *Not possible* (no MITM) | *Not possible* | *Not possible* (no MITM) | *Not possible* |
| MITM    | HTTP/2 · plain | HTTP/2 · TLS   | **89,055**     | **89,055**  | **99 MiB** | **33.6** | *Not possible* (no MITM) | *Not possible* | *Not possible* (no MITM) | *Not possible* |
| MITM    | HTTP/2 · plain | HTTP/3 · QUIC  | **41,662**     | **41,662**  | **202 MiB** | **37.4** | *Not possible* (no QUIC) | *Not possible* | *Not possible* (no MITM) | *Not possible* |
| MITM    | HTTP/2 · TLS   | HTTP/1 · plain | **51,042**     | **51,042**  | **150 MiB** | **44** | *Not possible* (no MITM) | *Not possible* | *Not possible* (no MITM) | *Not possible* |
| MITM    | HTTP/2 · TLS   | HTTP/2 · plain | **97,015**     | **97,015**  | **96 MiB** | **30.4** | *Not possible* (no MITM) | *Not possible* | *Not possible* (no MITM) | *Not possible* |
| MITM    | HTTP/2 · TLS   | HTTP/3 · QUIC  | **40,358**     | **40,358**  | **206 MiB** | **38.7** | *Not possible* (no QUIC) | *Not possible* | *Not possible* (no MITM) | *Not possible* |
| MITM    | HTTP/3 · QUIC  | HTTP/1 · plain | **22,792**     | **22,792**  | **242 MiB** | **40.3** | *Not possible* (no QUIC) | *Not possible* | *Not possible* (no MITM) | *Not possible* |
| MITM    | HTTP/3 · QUIC  | HTTP/2 · TLS   | **34,225**     | **34,225**  | **237 MiB** | **39.9** | *Not possible* (no QUIC) | *Not possible* | *Not possible* (no MITM) | *Not possible* |
| MITM    | HTTP/3 · QUIC  | HTTP/3 · QUIC  | **21,164**     | **21,164**  | **256 MiB** | **38.3** | *Not possible* (no QUIC) | *Not possible* | *Not possible* (no MITM) | *Not possible* |
| MITM    | HTTP/1 · plain (CONNECT) | HTTP/1 · TLS | **27,238** | **27,238** | **123 MiB** | **42.9** | *Not possible* (no MITM) | *Not possible* | *Not possible* (no MITM) | *Not possible* |
| MITM    | HTTP/1 · TLS   | HTTP/1 · TLS   | **29,186**     | **29,186**  | **98 MiB** | **41.2** | *Not possible* (no MITM) | *Not possible* | *Not possible* (no MITM) | *Not possible* |
| MITM    | HTTP/2 · TLS   | HTTP/2 · TLS   | **79,070**     | **79,070**  | **95 MiB** | **33.5** | *Not possible* (no MITM) | *Not possible* | *Not possible* (no MITM) | *Not possible* |
| MITM    | HTTP/2 · TLS   | HTTP/1 · TLS   | **38,943**     | **38,943**  | **175 MiB** | **43.9** | *Not possible* (no MITM) | *Not possible* | *Not possible* (no MITM) | *Not possible* |
| MITM    | HTTP/3 · QUIC  | HTTP/1 · TLS   | **19,571**     | **19,571**  | **241 MiB** | **38.7** | *Not possible* (no QUIC) | *Not possible* | *Not possible* (no MITM) | *Not possible* |



**TWP Memory / CPU** (heated 1-rep @ tip, `laptop-matrix-memory-20260824/`; warmup 2s / measure 8s; c=8–64): filled from compare-same / compare-bridges / compare-mitm peak-RPS step. Cool RPS cells unchanged. H2→H1 ~190–207 MiB (was ~425 laptop / ~848 CI); no outsized Memory arms vs prior H2 bag leak.
Windows reverse tiny-GET: base matrix **2026-08-20** High-perf, Linux-matched harness (warmup 2s / measure 8s; concurrency 8, 16, 32, 64; median of 3 repeats except H2 TLS→H3 and H3→H1/H2, which have 2). CSVs under `tools/RpsLoadProbe/results/windows-20260820/` (`compare-same`, `compare-bridges`). MITM and heavier reverse: 1-repeat follow-up under `windows-20260820-quick/`. Absolute RPS swings with sequential-arm heat; prefer TWP÷YARP ratios.

**2026-08-21 remeasure (through exact-body + H3 QPACK-normalized names):** H1 plain, H1 TLS, H1→H2, H3→H1, H3→H2 refreshed as mean of both arm orders at c=32 (`win-final-`*). Exact-size H2 origin body materialize (no MemoryStream+ToArray) and `HeaderNamesAreHttp2Normalized` on the H3 fast Request. Other reverse Windows rows still **2026-08-20** unless noted.

**2026-08-22 matrix fill (missing plain cells):** Library fix so cleartext-listen reverse (`DecryptSsl=false`) honors `ForwardCleartext=false` as origin HTTPS (H1 plain→HTTPS). New probe arms: `reverse-http1-to-https` / `yarp-reverse-http1-to-https`, `http-mitm` (explicit plain→plain). Full Windows `compare-same` + `compare-bridges` + plain twins under `tools/RpsLoadProbe/results/windows-20260822-matrix/` (1-rep; warmup 2s / measure 8s; c=8,16,32,64).

**2026-08-22 MITM laptop matrix (historical):** heated 1-rep CSV `tools/RpsLoadProbe/results/windows-20260822-mitm-full/`. Publishable CI **true MITM** (separate TWP-only table + **MITM÷Reverse**): [Performance](Performance) `compare-product` @ `9d7c2966` ([32866706475](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32866706475)).

**Load generators:** Reverse inbound H3 arms use `**dotnet-httpclient`** (`http_version=3.0`, `RequestVersionExact`) after dual-listen reverse H3. MITM H3→H2 / H3→H3 / H3→H1 plain reuse the same dual-listen transparent reverse path as their reverse twins (`ForwardCleartext` / decrypt knobs). Older UDP-only `quic-http3` MITM H3→H1 TLS numbers are dual-crypto extras (`mitm-http3-to-http1`).

**Matched HttpClient TWP÷YARP — table cells are cool absolutes where cited:** **parity audit** `win-parity-audit-20260822-004214/` (both orders @ c=32): H1 plain **41,390 / 38,772** ≈ **1.07×**; H1 TLS **35,205 / 29,750** ≈ **1.18×**; H1→H3 **21,819 / 20,712** ≈ **1.05×**; H3→H3 **26,299 / 14,942** ≈ **1.76×** (YARP soft — treat absolute cautiously). **2026-08-22 cool paste** `win-cool-paste-20260822-063226/` (both orders @ c=32): H1→H2 **25,540 / 24,920** ≈ **1.02×**; H1 plain→HTTPS **30,844 / 30,621** ≈ **1.01×**; h2c→H3 **27,493 / 24,535** ≈ **1.12×**; H2 TLS→H3 **30,813 / 24,039** ≈ **1.28×**; h2c→H1 **46,517 / 42,994** ≈ **1.08×**; H3→H1 **22,325 / 23,773** ≈ **0.94×**; H3→H2 **22,297 / 21,914** ≈ **1.02×**. **2026-08-23 soft coolish (both orders @ c=32, after session-lite H2/H3 gate):** H3→H1 ≈ **1.09×**; h2c→H1 ≈ **1.05×**; H1→H3 ≈ **1.25×**. Published CI Win bridges @ `11e32f1c` still show those three ≤1.00× — tip remeasure @ `62e5efcd` in flight. TWP-led H2 same-protocol rows unchanged (h2c↔h2c ≈ **1.17×**, etc.).

**Attempted H1→H3 micro-opts (2026-08-22, reverted):** Lowercasing H1 request names before QPACK + buffering tiny H3 origin bodies **without draining to FIN** **regressed** cool H1→H3 from ~1.13× to ~0.65× — kept out. **2026-08-23 kept:** same ≤64 KiB eager materialize in `ForwardOverQuicAsync` **plus drain-to-FIN before Dispose** (else RST poisons the QUIC pool → handshake-per-request; first attempt ~1.16×→~0.7×). Cool both orders ≈ **1.03–1.23×** (`cool-h3-origin-eager64-drain-20260823/`). Smoke H2→H3 / H3→H3 still lead; H3→H1 unchanged (~0.98× TY).

**2026-08-22 H3 bridge hot-path (kept):** Decode H2 origin HEADERS into the Response `HeaderCollection` (no second collection + copy). H3→H1 fast path: drain chunked/connection-close origin bodies before pool Release (Kestrel `WriteAsync` often chunked — empty DATA was a correctness bug); lowercase H1 response names once for QPACK; skip `GetOriginHostPort` on warm pool hit. Cool CSVs: `tools/RpsLoadProbe/results/win-h3h1-postfix-20260821-231146/`, `win-h3h1-yarpfirst-20260821-231420/`.

**MITM÷Reverse (laptop historical):** Prefer same-session ratios. Heated `mitm-full` absolutes above are **not** true interception (pre-`EnableHttpInterception` twin). For publishable interception tax use CI **MITM÷Reverse** on [Performance](Performance).

**Why H3 absolute RPS ≪ H2 on this box:** tiny-GET loopback is not where H3 wins. Cool paired H3→H3 now leads YARP (~1.76× on the soft 2026-08-22 audit; High-perf matrix still shows ~0.95×). MsQuic + dual QUIC hops dominate absolute RPS vs H2 same-protocol (~90–100k), not TWP architecture.

nginx/Windows is a limited port — use it for **same-OS** comparison only, not as the industry nginx baseline.

**H2 TLS → H1 plain on Windows:** fair terminate — **TWP leads sustain (~1.01× YARP)** in the current table. Absolute RPS swings with background load; treat as same-OS only.

### Heavier reverse GET (64 KiB / 256 KiB)

1-repeat; warmup 2s / measure 8s; concurrency 8–64. Source: `windows-20260820-quick/compare-bodies`.


| Body    | Client        | Origin         | TWP sustain  | TWP peak   | nginx sustain            | nginx peak     | YARP sustain  | YARP peak  |
| ------- | ------------- | -------------- | ------------ | ---------- | ------------------------ | -------------- | ------------- | ---------- |
| 64 KiB  | HTTP/1 · TLS  | HTTP/1 · plain | **11,414**   | **12,184** | **1,119**                | **1,184**      | 🥇 **12,664** | **13,403** |
| 64 KiB  | HTTP/2 · TLS  | HTTP/1 · plain | 🥇 **7,744** | **7,744**  | **1,030**                | **1,063**      | **6,844**     | **6,844**  |
| 64 KiB  | HTTP/3 · QUIC | HTTP/1 · plain | **1,109**    | **1,109**  | *Not possible* (no QUIC) | *Not possible* | 🥇 **3,108**  | **3,108**  |
| 256 KiB | HTTP/1 · TLS  | HTTP/1 · plain | **3,260**    | **3,260**  | **292**                  | **301**        | 🥇 **3,882**  | **3,934**  |
| 256 KiB | HTTP/2 · TLS  | HTTP/1 · plain | **1,935**    | **1,935**  | **236**                  | **239**        | 🥇 **2,179**  | **2,179**  |
| 256 KiB | HTTP/3 · QUIC | HTTP/1 · plain | **771**      | **771**    | *Not possible* (no QUIC) | *Not possible* | 🥇 **939**    | **939**    |


nginx/Windows collapses on large reverse bodies in this harness; treat as same-OS only.

**2026-08-22 cool remeasure (bodies) — H2→H1 cells are cool means** (`win-bodies-coalesce288-20260822/`, both orders @ c=32): 64 KiB **7,744 / 6,844** ≈ **1.13×** → TWP leads; 256 KiB **1,935 / 2,179** ≈ **0.89×** → YARP leads. H1 TLS→H1 64 KiB still heated (marker follows heated). H3→H1 64 KiB cool ≈ **0.96×** → YARP leads. 256 KiB H1/H3: heated → YARP leads.

### POST 64 KiB request + 64 KiB response

1-repeat; warmup 2s / measure 8s. Source: `windows-20260820-quick/compare-post`.


| Client        | Origin         | TWP sustain  | TWP peak  | nginx sustain  | nginx peak     | YARP sustain | YARP peak |
| ------------- | -------------- | ------------ | --------- | -------------- | -------------- | ------------ | --------- |
| HTTP/1 · TLS  | HTTP/1 · plain | 🥇 **6,003** | **6,160** | **383**        | **413**        | **5,264**    | **5,420** |
| HTTP/2 · TLS  | HTTP/1 · plain | **3,519**    | **3,519** | **357**        | **389**        | 🥇 **4,741** | **4,871** |
| HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **1,973** | **2,001** | *Not possible* | *Not possible* | **1,802**    | **1,893** |


H1 POST: TWP leads (heated and cool). H2 POST: YARP leads — heated ≈ **0.74×**, cool ≈ **~0.88–0.95×** with c=1 TWP ahead (~**1.2×**); residual is multiplex scaling, not single-stream cost. **H3 POST (2026-08-22):** `UpdateContentLength` on streamed uploads stamped CL=0 (`ab16a871`). Heated remeasure `sustain0-verify/h3-post/` (c=8–64): TWP sustain **1,973** / YARP **1,802** ≈ **1.09×**.

### Lossy / high-RTT (H2 HOL / H3 packet loss)

Userspace **5 ms** one-way delay + **1%** stall (TCP) or datagram drop (UDP/QUIC); **64 KiB** GET. 1-repeat; warmup 2s / measure 8s; c=8–64. Source: `windows-20260822-lossy-h3-quic/` (lossy H3 forced to `quic-http3`).


| Client        | Origin         | TWP sustain   | TWP peak   | nginx sustain | nginx peak | YARP sustain | YARP peak |
| ------------- | -------------- | ------------- | ---------- | ------------- | ---------- | ------------ | --------- |
| HTTP/1 · TLS  | HTTP/1 · plain | **578**       | **578**    | **500**       | **500**    | 🥇 **656**   | **656**   |
| HTTP/2 · TLS  | HTTP/1 · plain | **14**        | **15**     | **14**        | **14**     | 🥇 **15**    | **15**    |
| HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **1,572**  | **1,572**  | *Not possible* (no QUIC) | *Not possible* | **0** | **50** |


H1 stays usable; H2 collapses under connection stalls (HOL). **H3 is the protocol-shape win**: TWP H3 sustain ≈ **112×** H2 on the same lossy session (datagram drop, not HOL). YARP H3 did not hold the p99 SLO under this userspace UDP shim (peak **50**) — treat as same-session measurement, not a capability claim. Absolute RPS is low because the shim delays every buffer/datagram.

### Architecture-sensitive

`compare-arch` (1-repeat; warmup 2s / measure 8s; c=8,16,32,64). Source: `windows-20260822-arch/`. Slow consumer = 256 KiB GET, client reads 16 KiB then sleeps 8 ms. Early response = 64 KiB POST, origin writes after the first 8 KiB. Duplex H2 = overlapping 64 KiB POST on H2 TLS↔H2 TLS. WebSocket = echo round-trips/sec on H1 TLS→H1 plain `/ws`. See [TWP vs YARP IO model](Performance-Profiling#twp-vs-yarp-io-model).

| Scenario | Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---|---|---:|---:|---:|---:|---:|---:|
| Slow consumer (256 KiB GET, throttled client read) | HTTP/1 · TLS | HTTP/1 · plain | **175** | **175** | 🥇 **203** | **203** | **196** | **196** |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · TLS | HTTP/1 · plain | 🥇 **248** | **248** | **213** | **213** | 🥇 **248** | **248** |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **248** | **248** | *Not possible* (no QUIC) | *Not possible* | 🥇 **248** | **248** |
| Early response (origin writes after first request chunk) | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **6,402** | **6,606** | **270** | **270** | **5,135** | **5,135** |
| Early response (origin writes after first request chunk) | HTTP/2 · TLS | HTTP/1 · plain | **2,938** | **3,330** | **117** | **141** | 🥇 **4,056** | **4,056** |
| Early response (origin writes after first request chunk) | HTTP/3 · QUIC | HTTP/1 · plain | 🥇 **1,794** | **1,794** | *Not possible* (no QUIC) | *Not possible* | **1,485** | **1,485** |
| Duplex (both directions live) | HTTP/2 · TLS | HTTP/2 · TLS | **9** | **590** | *Not possible* | *Not possible* | 🥇 **2,455** | **2,455** |
| Duplex (WebSocket / extended CONNECT) | HTTP/1 · TLS | HTTP/1 · plain | 🥇 **38,235** | **38,823** | **18,251** | **19,054** | **37,803** | **38,454** |

Slow consumer is sleep-bound (~16 × 8 ms per 256 KiB); H1/H2 sit in the same band. **H3 slow-consumer (2026-08-22):** fast path closed the origin socket for CL>16 KiB without `StreamBodyWriter` (`36d21f67`); remeasure `sustain0-verify/h3-slow/` matches YARP at **248** sustain. Early-response H1: TWP leads (~1.25× YARP) — sequential H1 still finishes the exchange quickly when the origin answers after 8 KiB. **H3 early-response (2026-08-22):** cool mean ≈ **1.21×** YARP after overlapping origin upload with `ReceiveResponse` / `StreamBodyWriter` (`fix-early-tls/`). Early-response H2 and duplex H2: YARP leads on heated matrix; TWP H2↔H2 duplex sustain **9** vs peak **590** (errors at higher concurrency) vs YARP **2,455**. WebSocket echo: TWP leads (~1.01× YARP); nginx/Windows same-OS only.

### TLS termination cost (H1 TLS → cleartext origin)

1-repeat. Source: `windows-20260820-quick/compare-tls-cost`.


| Workload                  | TWP sustain | TWP peak   | nginx sustain | nginx peak | YARP sustain  | YARP peak  |
| ------------------------- | ----------- | ---------- | ------------- | ---------- | ------------- | ---------- |
| Keep-alive · tiny GET     | **35,724**  | **39,386** | **17,148**    | **18,318** | 🥇 **39,905** | **39,905** |
| New-connection · tiny GET | **926**     | **926**    | **789**       | **800**    | 🥇 **1,088**  | **1,088**  |
| Keep-alive · 256 KiB GET  | **3,349**   | **3,349**  | **291**       | **299**    | 🥇 **3,916**  | **3,916**  |


### Local saturation control (laptop)

Same shape as [Performance § Saturation control](Performance#saturation-control); one OS = this laptop. Fill after cool `compare-saturation` (median of repeats; Memory/CPU at peak-RPS step).

```powershell
pwsh tools/RpsLoadProbe/run-rps.ps1 -Mode compare-saturation
```

#### Block A — H1 plain

1-rep cool-ish laptop @ `0ff3673c` (`laptop-sat-memory-fix/`, 2026-08-24). Absolutes are local-only; prefer ratios.

| Arm | Generator | Sustain | Peak | % of origin-HttpClient | Memory (RSS) | CPU avg % |
|---|---|---:|---:|---:|---:|---:|
| origin-direct | dotnet-httpclient | **47,946** | **62,661** | **100%** | **58 MiB** | **32.2** |
| bare-reverse-http1 | dotnet-httpclient | **36,177** | **36,177** | **57.7%** | **57 MiB** | **35.4** |
| nginx-reverse-http1 | dotnet-httpclient | **18,208** | **19,436** | **31.0%** | **217 MiB** | 🥇 **12.1** |
| yarp-reverse-http1 | dotnet-httpclient | **29,592** | **34,703** | **55.4%** | **85 MiB** | **41.3** |
| twp-reverse-http1 | dotnet-httpclient | **34,061** | **34,061** | **54.4%** | 🥇 **81 MiB** | **39.4** |

#### Block B — H2 TLS→H1

Same run. **TWP Memory ~199 MiB** vs prior laptop multi-conn **~425 MiB** / CI **~848 MiB** — bag drain + H2→H1 lite wire; RPS still **1.04×** YARP.

| Arm | Generator | Sustain | Peak | ÷YARP | ÷nginx | Memory (RSS) | CPU avg % |
|---|---|---:|---:|---:|---:|---:|---:|
| nginx-reverse-http2 | dotnet-httpclient | **3,186** | **11,389** | **0.25×** | **1.00×** | **243 MiB** | 🥇 **11.4** |
| yarp-reverse-http2 | dotnet-httpclient | **45,269** | **45,269** | **1.00×** | **3.98×** | 🥇 **105 MiB** | **48.3** |
| twp-reverse-http2-cleartext | dotnet-httpclient | 🥇 **46,987** | **46,987** | **1.04×** | **4.13×** | **199 MiB** | **47.3** |

#### Block C — H3→H1

Same sequential run (thermal; treat ÷YARP cautiously vs cool pair).

| Arm | Generator | Sustain | Peak | ÷YARP | Memory (RSS) | CPU avg % |
|---|---|---:|---:|---:|---:|---:|
| yarp-reverse-http3-cleartext | dotnet-httpclient | 🥇 **28,101** | **28,101** | **1.00×** | 🥇 **172 MiB** | **45.4** |
| twp-reverse-http3-cleartext | dotnet-httpclient | **22,910** | **23,628** | **0.84×** | **198 MiB** | 🥇 **40.7** |

Laptop cool-ish A/B before bag/lite fix (2026-08-23, c=64, 8 s) — superseded by Block B once filled:

| Arm | Memory (RSS) | RPS |
|---|---:|---:|
| TWP H2→H1 (`EnableMultipleHttp2Connections=true`, default) | **425 MiB** | 41k |
| TWP H2→H1 (`TWP_RPS_SINGLE_HTTP2_CONNECTION=1`) | **367 MiB** | 32k |
| YARP H2→H1 (multi) | **104 MiB** | 41k |

## Editions (CLI / Plus stress)

Local Win smoke 2026-08-29 (`rps-ramp-20260829-104306.csv`): c=64, warmup 2s / measure 5s, **1** repeat — **not publishable**. Prefer ÷CLI ratios. **Superseded for publishable numbers** by GHA [33259699099](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33259699099) after terminate-lite middleware + JWT validate cache — see [Performance — Editions](Performance#editions-cli--plus--intercept) (JWT ~0.93×, CIDR/WAF/rate-limit ~0.98×, cache ~1.05×). Table below is the pre-fix smoke (JWT ~0.51×).

| Arm | Sustain RPS | Memory (RSS) | ÷CLI | Gate (then) |
|---|---:|---:|---:|---:|
| `twp-cli-reverse-http1` (baseline) | **34,623** | 114 MiB | **1.00×** | — |
| `twp-reverse-http1` (library) | **35,041** | 72 MiB | CLI÷lib **0.99×** | ≥0.80 |
| `twp-cli-reverse-http1-tls` | **24,059** | 140 MiB | TLS÷libTLS **0.80×** | ≥0.80 |
| `twp-cli-reverse-http1-route` | **36,092** | 118 MiB | **1.04×** | ≥0.90 |
| `twp-cli-plus-base-http1` | **36,596** | 120 MiB | **1.06×** | ≥0.90 |
| `twp-cli-plus-cache-http1` | **23,177** | 125 MiB | **0.67×** | ≥0.60 |
| `twp-cli-intercept-http1` | **25,603** | 124 MiB | **0.74×** | ≥0.65 |
| `twp-cli-plus-waf-http1` | **26,820** | 127 MiB | **0.78×** | ≥0.70 |
| `twp-cli-plus-cidr-http1` | **25,338** | 125 MiB | **0.73×** | ≥0.70 |
| `twp-cli-plus-jwt-http1` | **17,545** | 154 MiB | **0.51×** | ≥0.45 |
| `twp-cli-plus-ratelimit-http1` | **27,033** | 130 MiB | **0.78×** | ≥0.70 |
| `twp-cli-plus-resilience-http1` | **25,782** | 128 MiB | **0.75×** | ≥0.65 |
| `twp-cli-plus-discovery-file-http1` | **28,016** | 125 MiB | **0.81×** | ≥0.70 |
| `twp-cli-plus-metrics-scrape-http1` | **27,228** | 129 MiB | **0.79×** | ≥0.70 |
| `twp-cli-plus-cache-hit-http1` | **23,178** | 122 MiB | hit÷cold **1.00×** | ≥0.90 |
| `twp-cli-static-http1` | **53,068** | 117 MiB | **1.53×** | ≥0.85 |
| `twp-cli-logging-http1` | **33,218** | 120 MiB | **0.96×** | ≥0.90 |
| `twp-cli-lb-leasttime-http1` | **31,995** | 131 MiB | ÷route **0.89×** | ≥0.85 |
| `twp-cli-dialect-twp-http1` | **36,532** | 116 MiB | **1.06×** | ≥0.90 |
