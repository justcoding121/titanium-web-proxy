# Performance Profiling

How the throughput hotspots behind the numbers on the [Performance](Performance) page were found. This page documents the techniques and tools so future performance work (or a regression hunt) can follow the same playbook instead of rediscovering it. Everything here was used in the 2026 pass that took the HTTP/2 bridge arms from ~6× behind the managed reverse peer to parity-or-close.

## Contents

- [The measurement harness](#the-measurement-harness)
- [Controlling measurement noise](#controlling-measurement-noise)
- [Local Windows lab (developer laptop)](#local-windows-lab-developer-laptop)
- [Technique 1: concurrency sweep as a shape test](#technique-1-concurrency-sweep-as-a-shape-test)
- [Technique 2: async dumps — find where requests wait](#technique-2-async-dumps--find-where-requests-wait)
- [Technique 3: per-stage latency decomposition](#technique-3-per-stage-latency-decomposition)
- [Technique 4: CPU sampling](#technique-4-cpu-sampling)
- [Technique 5: reference-source comparison](#technique-5-reference-source-comparison)
- [Case studies: symptom → tool → root cause → fix](#case-studies-symptom--tool--root-cause--fix)
- [Guardrails while optimizing](#guardrails-while-optimizing)
- [Checklist](#checklist)

## The measurement harness

All throughput work starts from [RpsLoadProbe](https://github.com/justcoding121/titanium-web-proxy/tree/develop/tools/RpsLoadProbe):

- Each **arm** is one topology: client protocol × origin protocol × TLS/cleartext × reverse/MITM (e.g. `twp-reverse-http2-cleartext` = H2 TLS client → H2→H1 bridge → cleartext H1 origin).
- Every TWP arm has a **control arm** — the managed reverse peer (and the native reverse peer where it can run the path) hosting the *identical* workload in the same process and session, so both sides see the same machine state.
- The probe **ramps concurrency** (typically c=8→64) and reports **sustainable RPS**: the last concurrency step that still met the error-rate and p99-latency SLO. A ramp that grows RPS but blows p99 is a queue, not throughput.
- Results land in timestamped CSVs under `tools/RpsLoadProbe/results/`. Publishable [Performance](Performance) tables cite **GitHub Actions** run IDs (median of 3 on matched 4 vCPU / 16 GiB runners). This page keeps the **local Windows lab** so a cool paired win can be proven before spending a CI remasure.

```powershell
# one suite
pwsh tools/RpsLoadProbe/run-rps.ps1 -Mode compare-bridges
# one arm, custom ramp (apphost, not `dotnet <dll>` — the child processes re-exec the host)
tools/RpsLoadProbe/bin/Release/net10.0/RpsLoadProbe.exe --ramp --mode reverse-http2-cleartext `
  --concurrency 8,16,32,64 --warmup-sec 2 --duration-sec 5 --results-dir tools/RpsLoadProbe/results
```

## Controlling measurement noise

On a laptop, thermal throttling dominates everything else: the *same* arm measured 23k, 11k, and 51k RPS in one afternoon depending on accumulated heat. Rules that kept conclusions honest:

- **Compare only within one back-to-back session.** Never compare a number from this run against a number from an hour ago.
- **Prefer TWP÷peer ratios over absolutes.** The control arm soaks up the same throttling.
- **For a targeted A/B question, run the two arms paired**: cooldown (~2 min idle), arm A, arm B immediately after — and alternate which goes first across repeats so heat bias cancels. This is how "MITM costs 0.65–0.75× of its reverse twin, and the delta is purely the extra origin TLS leg" was established: the two probe arms differ by exactly one flag (`ForwardCleartext`).
- If an arm's ratio looks newly bad, **re-measure before profiling** — several "regressions" were heat.
- **Gate before publishing:** optimize against cool paired ratios on the laptop below. After a cool win, remasure on matched Windows+Linux GHA (`workflow_dispatch` [RPS saturation](https://github.com/justcoding121/titanium-web-proxy/actions/workflows/rps-saturation.yml)) and paste medians into [Performance](Performance). Windows GHA removes laptop thermal skew; it is **not** the same as a cool pair — shared-VM noise still applies, so prefer TWP÷peer ratios.

## Local Windows lab (developer laptop)

Local debug setup and historical High-perf / cool-paired tables. **Do not paste these absolutes onto [Performance](Performance)** — that page is CI-only. Use this section to iterate: cool A/B, then publish from GHA.

### Measurement environment

| | |
|---|---|
| OS | Windows 11 (10.0.26200) |
| CPU | 11th Gen Intel Core i7-1185G7 @ 3.00 GHz (8 logical processors) |
| RAM | 31.8 GiB |
| Runtime | .NET 10.0.10 |
| nginx | nginx/Windows **1.31.3** |
| YARP | Yarp.ReverseProxy **2.3.0** |
| Harness | RpsLoadProbe Release; arms run **sequentially** |

This box is **8 logical / ~32 GiB** — not the 4 vCPU / 16 GiB GHA class. Treat ratios as the local gate; remasure on CI before claiming a publishable win.

**Cool** = ~2 min idle, then paired A/B (alternate who goes first; mean of both orders) — **authoritative local gate**. **Heated** = long sequential matrix; absolute RPS swings with thermal throttling (later arms look worse). Table cells keep heated absolutes for provenance; **🟢 marks the cool-paired TWP÷YARP winner** when a cool remasure exists (not the heated sustain max). If only a heated 1-rep exists, 🟢 follows that row’s sustain and the note says so.

### Windows — Titanium vs nginx vs YARP (laptop)

Client / origin: HTTP version and whether TLS is used (`plain` = cleartext, `TLS` = encrypted, `QUIC` = HTTP/3).

| Mode | Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---|---|---:|---:|---:|---:|---:|---:|
| Reverse | HTTP/1 · plain | HTTP/1 · plain | 🟢 **41,942** | **41,942** | **15,196** | **18,806** | **44,229** | **44,229** |
| Reverse | HTTP/1 · plain | HTTP/1 · TLS | 🟢 **36,188** | **36,188** | *Not possible* | *Not possible* | **37,786** | **37,786** |
| Reverse | HTTP/1 · TLS | HTTP/1 · plain | 🟢 **36,069** | **36,069** | **10,252** | **13,741** | **37,852** | **37,852** |
| Reverse | HTTP/1 · TLS | HTTP/2 · TLS | 🟢 **40,355** | **40,355** | *Not possible* | *Not possible* | **42,122** | **42,122** |
| Reverse | HTTP/1 · TLS | HTTP/3 · QUIC | 🟢 **25,125** | **25,125** | *Not possible* (no QUIC) | *Not possible* | **27,596** | **27,596** |
| Reverse | HTTP/2 · plain | HTTP/1 · plain | 🟢 **52,183** | **52,183** | *Not possible* | *Not possible* | **52,543** | **52,543** |
| Reverse | HTTP/2 · plain | HTTP/2 · plain | 🟢 **100,568** | **100,568** | *Not possible* | *Not possible* | **86,021** | **86,021** |
| Reverse | HTTP/2 · plain | HTTP/2 · TLS | 🟢 **88,006** | **88,006** | *Not possible* | *Not possible* | **84,634** | **84,634** |
| Reverse | HTTP/2 · plain | HTTP/3 · QUIC | 🟢 **40,075** | **40,075** | *Not possible* (no QUIC) | *Not possible* | **42,235** | **42,235** |
| Reverse | HTTP/2 · TLS | HTTP/1 · plain | 🟢 **49,548** | **49,548** | **15,793** | **15,793** | **49,072** | **49,072** |
| Reverse | HTTP/2 · TLS | HTTP/2 · plain | 🟢 **94,238** | **94,238** | *Not possible* | *Not possible* | **81,266** | **81,266** |
| Reverse | HTTP/2 · TLS | HTTP/3 · QUIC | 🟢 **34,506** | **34,506** | *Not possible* (no QUIC) | *Not possible* | **35,388** | **35,388** |
| Reverse | HTTP/3 · QUIC | HTTP/1 · plain | 🟢 **24,312** | **24,312** | *Not possible* (no QUIC) | *Not possible* | **30,834** | **30,834** |
| Reverse | HTTP/3 · QUIC | HTTP/2 · TLS | 🟢 **30,460** | **30,460** | *Not possible* (no QUIC) | *Not possible* | **36,698** | **36,698** |
| Reverse | HTTP/3 · QUIC | HTTP/3 · QUIC | 🟢 **24,097** | **24,097** | *Not possible* (no QUIC) | *Not possible* | **25,463** | **25,463** |
| MITM | HTTP/1 · plain | HTTP/1 · plain | **31,307** | **31,307** | *Not possible* (no MITM) | *Not possible* | *Not possible* (no MITM) | *Not possible* |
| MITM | HTTP/1 · plain | HTTP/1 · TLS | **27,204** | **27,204** | *Not possible* (no MITM) | *Not possible* | *Not possible* (no MITM) | *Not possible* |
| MITM | HTTP/1 · TLS | HTTP/1 · TLS | **30,244** | **30,244** | *Not possible* (no MITM) | *Not possible* | *Not possible* (no MITM) | *Not possible* |
| MITM | HTTP/2 · plain | HTTP/2 · plain | **91,005** | **91,005** | *Not possible* (no MITM) | *Not possible* | *Not possible* (no MITM) | *Not possible* |
| MITM | HTTP/2 · plain | HTTP/2 · TLS | **85,618** | **85,618** | *Not possible* (no MITM) | *Not possible* | *Not possible* (no MITM) | *Not possible* |
| MITM | HTTP/2 · TLS | HTTP/1 · TLS | **40,498** | **40,498** | *Not possible* (no MITM) | *Not possible* | *Not possible* (no MITM) | *Not possible* |
| MITM | HTTP/2 · TLS | HTTP/2 · TLS | **76,719** | **76,719** | *Not possible* (no MITM) | *Not possible* | *Not possible* (no MITM) | *Not possible* |
| MITM | HTTP/3 · QUIC | HTTP/1 · TLS | **20,134** | **20,418** | *Not possible* (no QUIC) | *Not possible* | *Not possible* (no MITM) | *Not possible* |
| MITM | HTTP/3 · QUIC | HTTP/2 · TLS | **35,460** | **35,460** | *Not possible* (no QUIC) | *Not possible* | *Not possible* (no MITM) | *Not possible* |
| MITM | HTTP/3 · QUIC | HTTP/3 · QUIC | **26,147** | **26,147** | *Not possible* (no QUIC) | *Not possible* | *Not possible* (no MITM) | *Not possible* |

Windows reverse tiny-GET: base matrix **2026-08-20** High-perf, Linux-matched harness (warmup 2s / measure 8s; concurrency 8, 16, 32, 64; median of 3 repeats except H2 TLS→H3 and H3→H1/H2, which have 2). CSVs under `tools/RpsLoadProbe/results/windows-20260820/` (`compare-same`, `compare-bridges`). MITM and heavier reverse: 1-repeat follow-up under `windows-20260820-quick/`. Absolute RPS swings with sequential-arm heat; prefer TWP÷YARP ratios.

**2026-08-21 remasure (through exact-body + H3 QPACK-normalized names):** H1 plain, H1 TLS, H1→H2, H3→H1, H3→H2 refreshed as mean of both arm orders at c=32 (`win-final-*`). Exact-size H2 origin body materialize (no MemoryStream+ToArray) and `HeaderNamesAreHttp2Normalized` on the H3 fast Request. Other reverse Windows rows still **2026-08-20** unless noted.

**2026-08-22 matrix fill (missing plain + MITM cells):** Library fix so cleartext-listen reverse (`DecryptSsl=false`) honors `ForwardCleartext=false` as origin HTTPS (H1 plain→HTTPS). New probe arms: `reverse-http1-to-https` / `yarp-reverse-http1-to-https`, `http-mitm` (explicit plain→plain). Full Windows `compare-same` + `compare-bridges` + `compare-mitm` + plain twins under `tools/RpsLoadProbe/results/windows-20260822-matrix/` (1-rep; warmup 2s / measure 8s; c=8,16,32,64). New/updated table cells above use that run. H2 plain MITM rows use the inspectable transparent reverse path (client already cleartext; same topology as reverse h2c arms from `compare-same`).

**Load generators:** Reverse inbound H3 arms use **`dotnet-httpclient`** (`http_version=3.0`, `RequestVersionExact`) after dual-listen reverse H3. MITM H3→H2 / H3→H3 reuse dual-listen transparent reverse (`reverse-http3-to-http2`, `reverse-http3`). Older UDP-only `quic-http3` MITM H3→H1 numbers are **not** comparable to HttpClient reverse twins.

**Matched HttpClient TWP÷YARP (cool reverse rows) — 🟢 in the table above follows these, not heated sustain:** **2026-08-22 cool parity audit** (`tools/RpsLoadProbe/results/win-parity-audit-20260822-004214/`, mean of both arm orders @ c=32): H1 plain ≈ **1.07×**; H1 TLS terminate ≈ **1.18×**; H1→H2 ≈ **1.14×**; H1→H3 ≈ **1.05×**; H3→H3 ≈ **1.76×** (YARP soft on that pair — treat absolute cautiously). Earlier cool H3→H1 ≈ **~0.96×**, H3→H2 ≈ **~1.06×**. **2026-08-22 residual cool remasure** (`win-residual-20260822-015751/`, mean of both arm orders @ c=32): H1 plain→HTTPS ≈ **1.08×**; h2c→H3 ≈ **1.16×**; H2 TLS→H3 ≈ **1.40×** (YARP soft on TWP-first order — still ≥**1.24×** YARP-first); h2c→H1 ≈ **1.00×**. Heated High-perf / matrix-fill absolutes can still show YARP with a higher raw sustain — thermal order bias, not the cool verdict. TWP-led H2 same-protocol cool ratios unchanged (h2c↔h2c ≈ **1.17×**, etc.). **Windows reverse tiny-GET is at parity or better vs YARP on every cool-paired arm.** Remaining local gaps vs YARP are **heavier reverse bodies / POST** (see below), not the tiny-GET matrix.

**Attempted H1→H3 micro-opts (2026-08-22, reverted):** Lowercasing H1 request names before QPACK + buffering tiny H3 origin bodies for TLS coalesce **regressed** cool H1→H3 from ~1.13× to ~0.65× — kept out. Baseline already at parity; do not land probe-shaped encode/coalesce without a cool A/B win.

**2026-08-22 H3 bridge hot-path (kept):** Decode H2 origin HEADERS into the Response `HeaderCollection` (no second collection + copy). H3→H1 fast path: drain chunked/connection-close origin bodies before pool Release (Kestrel `WriteAsync` often chunked — empty DATA was a correctness bug); lowercase H1 response names once for QPACK; skip `GetOriginHostPort` on warm pool hit. Cool CSVs: `tools/RpsLoadProbe/results/win-h3h1-postfix-20260821-231146/`, `win-h3h1-yarpfirst-20260821-231420/`.

**MITM÷TWP reverse twin (2026-08-22):** Explicit plain→plain (`http-mitm`) ÷ H1 plain reverse ≈ **0.66** of the heated compare-same H1 plain absolute (31,307 vs 47,122 in-session) — prefer same-session twins. Transparent H1 dual-TLS (`reverse-http1-mitm`) ≈ **30,244**. Explicit CONNECT `https-mitm` (plain client → TLS origin) ≈ **27,204**. H2→H1 MITM ≈ **40,498**. H2 TLS↔H2 TLS MITM ≈ **76,719** (heat vs earlier ~99k peak publishes).

**Why H3 absolute RPS ≪ H2 on this box:** tiny-GET loopback is not where H3 wins. Cool paired H3→H3 now leads YARP (~1.76× on the soft 2026-08-22 audit; High-perf matrix still shows ~0.95×). MsQuic + dual QUIC hops dominate absolute RPS vs H2 same-protocol (~90–100k), not TWP architecture.

nginx/Windows is a limited port — use it for **same-OS** comparison only, not as the industry nginx baseline.

**H2 TLS → H1 plain on Windows:** fair terminate — **TWP leads sustain (~1.01× YARP)** in the current table. Absolute RPS swings with background load; treat as same-OS only.

### Heavier reverse GET (64 KiB / 256 KiB)

1-repeat; warmup 2s / measure 8s; concurrency 8–64. Source: `windows-20260820-quick/compare-bodies`.

| Body | Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---|---|---:|---:|---:|---:|---:|---:|
| 64 KiB | HTTP/1 · TLS | HTTP/1 · plain | 🟢 **11,414** | **12,184** | **1,119** | **1,184** | **12,664** | **13,403** |
| 64 KiB | HTTP/2 · TLS | HTTP/1 · plain | 🟢 **5,778** | **5,792** | **1,030** | **1,063** | **8,081** | **8,323** |
| 64 KiB | HTTP/3 · QUIC | HTTP/1 · plain | **1,109** | **1,109** | *Not possible* (no QUIC) | *Not possible* | 🟢 **3,108** | **3,108** |
| 256 KiB | HTTP/1 · TLS | HTTP/1 · plain | **3,260** | **3,260** | **292** | **301** | 🟢 **3,882** | **3,934** |
| 256 KiB | HTTP/2 · TLS | HTTP/1 · plain | **1,294** | **1,353** | **236** | **239** | 🟢 **2,253** | **2,347** |
| 256 KiB | HTTP/3 · QUIC | HTTP/1 · plain | **771** | **771** | *Not possible* (no QUIC) | *Not possible* | 🟢 **939** | **939** |

nginx/Windows collapses on large reverse bodies in this harness; treat as same-OS only.

**2026-08-22 cool remasure (bodies) — 🟢 above follows cool when measured, else heated:** H1 TLS→H1 / H2→H1 **64 KiB** cool ≈ **1.09×** / **1.13×** → TWP 🟢 (`win-bodies-cool-20260822/`, `win-bodies-coalesce288-20260822/`). H3→H1 64 KiB cool ≈ **0.96×** → YARP 🟢 (near-parity; heated was a blowout). 256 KiB H2→H1 cool ≈ **0.89–0.93×** → YARP 🟢. 256 KiB H1/H3: no cool TWP win yet → YARP 🟢 from heated. Heated 1-rep absolutes still show YARP ahead on raw sustain even where cool flips the gate — ignore those for the verdict.

### POST 64 KiB request + 64 KiB response

1-repeat; warmup 2s / measure 8s. Source: `windows-20260820-quick/compare-post`.

| Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---|---:|---:|---:|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | 🟢 **6,003** | **6,160** | **383** | **413** | **5,264** | **5,420** |
| HTTP/2 · TLS | HTTP/1 · plain | **3,519** | **3,519** | **357** | **389** | 🟢 **4,741** | **4,871** |
| HTTP/3 · QUIC | HTTP/1 · plain | **0** | **185** | *Not possible* | *Not possible* | 🟢 **1,890** | **1,890** |

H1 POST: TWP 🟢 (heated and cool). H2 POST: YARP 🟢 — heated ≈ **0.74×**, cool ≈ **~0.88–0.95×** with c=1 TWP ahead (~**1.2×**); residual is multiplex scaling, not single-stream cost. H3 POST: YARP 🟢 (TWP sustain **0**).

### Lossy / high-RTT (H2 HOL)

Userspace **5 ms** one-way delay + **1%** connection stall; **64 KiB** GET. 1-repeat. Source: `windows-20260820-quick/compare-lossy`.

| Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---|---:|---:|---:|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | **287** | **536** | **674** | **674** | 🟢 **678** | **678** |
| HTTP/2 · TLS | HTTP/1 · plain | **14** | **14** | **14** | **15** | 🟢 **15** | **15** |

H1 stays usable; H2 collapses under connection stalls (HOL). Absolute RPS is low because the shim delays every buffer — the point is the **protocol shape**, not competing with the tiny-GET table.

### TLS termination cost (H1 TLS → cleartext origin)

1-repeat. Source: `windows-20260820-quick/compare-tls-cost`.

| Workload | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---:|---:|---:|---:|---:|---:|
| Keep-alive · tiny GET | **35,724** | **39,386** | **17,148** | **18,318** | 🟢 **39,905** | **39,905** |
| New-connection · tiny GET | **926** | **926** | **789** | **800** | 🟢 **1,088** | **1,088** |
| Keep-alive · 256 KiB GET | **3,349** | **3,349** | **291** | **299** | 🟢 **3,916** | **3,916** |

## Technique 1: concurrency sweep as a shape test

Cheapest tool with the highest information density. Run the arm at c=1 and at c=64 and look at the *shape*, before reaching for any profiler:

| Observation | Meaning |
|---|---|
| Slow at c=1 and c=64 by the same factor | Per-request cost (allocations, crypto, syscalls) — go CPU-profile it |
| **Faster** at c=1 but flatlines while the control arm scales | A **serialization point** — something processes streams one at a time; profilers of per-request cost will mislead you |
| Scales to a cliff, then errors/SLO failures | Resource exhaustion or a convoy (locks, pool limits, flow-control windows) |

The h2c→H1 bridge showed the second shape: TWP *beat* the managed reverse peer at c=1 (6,425 vs 5,449 RPS) but flatlined at ~22k while the managed reverse peer scaled to 46k. That single observation eliminated allocation work, `System.IO.Pipelines`, and syscall efficiency as hypotheses and said "find the serial section."

## Technique 2: async dumps — find where requests wait

CPU profilers show where cycles burn; under async I/O the bottleneck is usually where requests **park**. Capture the async state machine population under load:

```powershell
# must be a Full dump; a mini dump lacks the heap metadata dumpasync needs
dotnet-dump collect -p <proxy PID> --type Full
dotnet-dump analyze <dump file>
> dumpasync --stats
```

Read the histogram of parked continuations. In the bridge investigation, hundreds of in-flight requests were parked in synthetic-response emission waiting on one `SemaphoreSlim` (the client write lock) — a classic convoy, with the side signature of high *system* CPU from many tiny socket writes. The fix (a dedicated per-direction frame writer draining a channel and coalescing up to 32 frames / 32 KB per socket write, `Http2FrameWriter`) was worth 3.4× on that arm.

## Technique 3: per-stage latency decomposition

When internal work looks fast but clients still see high latency, decompose the request path. TWP already captures per-request milestones when `EnableRequestTimingCapture` is set (see [Request timing](Home#request-timing)); RpsLoadProbe has an opt-in collector that aggregates them under load:

```powershell
# any non-empty value enables; a path (length > 1) writes reports to that file
$env:TWP_RPS_STAGE_TIMING = "C:\temp\stage-timing.log"
```

`StageTimingCollector` subscribes to `AfterResponse`, buckets `HttpRequestTiming` durations (client read, connection wait, request send, TTFB, delivery, total), and prints p50/p90/p99 per stage every 20 s. Subscribing to `AfterResponse` disables the no-interception fast path, so this must stay out of publishable runs.

The decisive read: the internal pipeline showed **p50 87 µs** per request while clients observed **p50 2.6 ms** — so ~2.5 ms of queueing happened *before* a stream entered the instrumented pipeline. That pointed at the per-connection HTTP/2 frame loop, which was running each stream's BeforeRequest handler prefix inline (~44 µs per HEADERS frame), capping any single client connection at ~22k streams/s regardless of concurrency. Starting the handler on the thread pool from the already-ordered dispatch task took the arm from 22k to 47k RPS.

The same collector separates "proxy is slow" from "origin leg is slow": on the H1→H2 bridge, TTFB was 240 µs at c=8 but 1,830 µs at c=64 with barely more RPS — the signature of CPU saturation, not another serial section.

## Technique 4: CPU sampling

For arms where the sweep says "per-request cost" or "saturation," attach the sampler during a long window (the ramp's default 5–7 s steps are too short to attach; use a 150 s single-concurrency run):

```powershell
tools/RpsLoadProbe/bin/Release/net10.0/RpsLoadProbe.exe --ramp --mode <arm> --concurrency 64 `
  --warmup-sec 2 --duration-sec 150 --results-dir tools/RpsLoadProbe/results/profiling
# ramp logs print: attach: combined --serve pid=N  (or split origin/proxy pids)
# in a second shell:
dotnet-dump collect -p <proxy PID> --type Full
dotnet-trace collect -p <proxy PID> --profile dotnet-sampled-thread-time --duration 00:00:25
```

This is a *confirmation* tool more than a discovery tool here: it confirmed the residual H1→H2 gap after origin-connection sharing is still whole-box cost (dual TLS legs plus the per-request session pipeline). Sharing lifted the arm from **0.33× to 0.53×** peer at c=32 (`rps-ramp-20260818-130040` / `130112`); cool remeasure after grow-at-4 stayed ~**0.51×** (`profile-baseline` / `profile-post-fix`). TTFB still rises with concurrency. At c=32 dumpasync showed **8** origin `ReadLoopAsync` instances (pool already spreading) plus `Monitor` / `SslStream` in the sampled stacks — not a single-conn convoy. Honest remainder: dual-TLS + session cost on this 8-thread box.

## Technique 5: reference-source comparison

When a comparable managed reverse peer is faster, read its source to answer **named hypotheses** — not to port its architecture. Two examples from this pass:

- *"Does the reference .NET server stack tune `MAX_CONCURRENT_STREAMS` dynamically?"* No — it opens additional origin connections when the stream limit is hit. TWP replicated the behavior within its own design (`Http2OriginRelayPool`).
- *"Is `System.IO.Pipelines` the advantage?"* No — TWP's buffered `HttpStream` already amortizes socket reads to one syscall per buffer drain; the memcpy `ReadOnlySequence` would remove costs ~0.02% of a request, and the TLS decrypt copy exists in both models (`SslStream` cannot produce a `ReadOnlySequence`; the reference .NET server stack copies decrypted bytes into its Pipe too). Measured support: H1 arms at parity, and TWP's c=1 latency *lower* than the managed reverse peer's.
- *"When does the managed reverse peer open another origin H2 connection?"* [`ForwarderHttpClientFactory`](https://github.com/microsoft/reverse-proxy) sets `EnableMultipleHttp2Connections = true` by default — SocketsHttpHandler grows sessions under stream pressure. TWP's `PoolGrowActiveStreamThreshold` is the analogous dial (lowered 16→4 after profiling).

## Case studies: symptom → tool → root cause → fix

| Symptom | Tool that found it | Root cause | Fix |
|---|---|---|---|
| h2c→H1 bridge 8.5k vs peer 46k, high system CPU | `dotnet-dump` + `dumpasync --stats` | Response emission convoy on the client write lock; many tiny socket writes | Queue all response frames through `Http2FrameWriter` (coalesced writes) — 3.4× |
| Same arm flat at ~22k at every concurrency, but faster than peer at c=1 | Concurrency sweep + stage timing (87 µs internal vs 2.6 ms observed) | Frame loop ran each stream's BeforeRequest prefix inline (~44 µs/HEADERS) | Start the handler on the thread pool from the ordered dispatch task — 22k → 47k |
| External-site H2 downloads stalled at exactly 64 KB | Standalone repro tool (`tools/H2ExternalRepro`) + a window-size env knob | Flow-control starvation: batched WINDOW_UPDATE threshold larger than the default 65,535 B window | Advertise a reference .NET server stack-class 768 KiB initial stream window in both directions |
| Two bridge arms at 100% errors after the passthrough change | The benchmark suite itself (error-rate SLO) | `:scheme` mismatch in compressed header relay on mixed-transport bridges | Detect and re-encode the header block with the corrected scheme |
| POST arm collapsed 842 → 9 RPS | Benchmark suite + targeted repro | Client DATA frames raced the handler dispatch and were routed before channels existed | Await the stream's dispatch task before routing its DATA frames |
| H1→H2 bridge stuck at ~0.3× peer | Stage timing (TTFB 240 µs → 1,830 µs as c grows) + `dotnet-trace` | Dual TLS + per-request pipeline; also one dedicated origin H2 connection per H1 client | Shared `Http2OriginConnectionPool` (0.33× → 0.53× at c=32). Remainder still looks CPU-bound |
| H3→H2 SLO-failed above c=16 (then 100% errors after pooling) | Error log (`TWP_H3_ERROR_LOG`) + RFC 7540 §5.1.1 | Exclusive `ConcurrentBag` cap 16, then concurrent `SendAsync` allocated stream ids off the write lock so a higher id's HEADERS could hit the wire first; the reference .NET server stack implicitly closed the lower idle streams and GOAWAYed | Shared pool (no exclusive checkout) + allocate stream id and write opening HEADERS under the same write lock. Reverse H3→H2 holds c=64 at 0% errors (`rps-ramp-20260818-130231`) |
| Inbound H3 ~0.40× peer blamed on “managed QUIC vs MsQuic” | Code read of `QuicClientHandler.ListenQuic` | Inbound H3 already is `System.Net.Quic` / MsQuic. Pre-match ratios also mixed `quic-http3` vs HttpClient | Matched-client H3→H1 ≈ **0.87**; do not prototype a second QUIC stack |
| H1→H2 / H3→H2 still ≪0.80 after pool | Cool A/B + c=1 + `dumpasync`/`dotnet-trace` @ c=32 (`results/h2-origin-choke/`) | **Not** dual-TLS polish: c=1 TWP **faster** (1.49×). Residual is **outbound `Http2OriginConnection.SendAsync` queueing** (TTFB≈SendAsync wait grows 624→2263 µs c=8→32; 13 parked `SendAsync` on H3→H2; 102 `SemaphoreSlim` TaskNodes on H1→H2; managed reverse peer only ~7 in-flight forwarders). Monitor slow-path ~2× managed reverse peer | Origin `Http2FrameWriter` exclusive drain: encode+enqueue under short `writeLock`, no `WriteAsync` under the lock (reference .NET server stack model). Cool H1→H2 **0.87× @ c=32** (28,996 / 33,336, `rps-ramp-20260818-170412`/`170452`); H3→H2 **0.64× @ c=32**. TTFB p50 262→894 µs. See `h2-origin-choke/POSTFIX.md` |
| H1→H2 / H3→H2 still &lt;0.80 after origin frame writer | Cool A/B + grow A/B + gcdump/trace (`results/residual-sub08/`) | Scaling wait on origin HEADERS (c=1 **1.04×**); **grow 4→32 regresses**; ForceRead/HPACK noise; Channel/Pipe not retained-heap | Ranked in `residual-sub08/CONCLUSIONS.md` |
| Monitor.Enter_Slowpath ~9.5% after frame writer | syncblk + speedscope + pool-pick diag; post-fix traces (`POSTFIX.md`) | **~70%** Monitor was `TryPick` + `ConcurrentDictionary.Count` under `entry.Gate`. **c=32:** 0% soft-miss. **c=64:** ~21% soft-miss + CreationGate at max | **Shipped A+B+C:** Interlocked `ActiveStreamCount`; skip CreationGate on Gate-held `Count >= max`; snapshot pick outside Gate. Monitor exclusive **9.5% → 3.1%**. Phase C no further win. Long-window TWP @ c=32 unchanged (~28.7k). Residual still HEADERS fan-in |
| H1→H2 still ~0.71× after pool-pick; dumpasync showed ForceRead on origin ReadLoop | Cool remasure + code path (`POSTFIX-INTAKE.md`) | Origin ReadLoop still did ForceRead 9+payload and copied HEADERS to MemoryStream; DATA awaited BodyPipe on the loop | Shared `Http2FrameIntake` on origin + in-place END_HEADERS decode + sync BodyPipe write. **ForceRead removed.** Best long cool pair this session still **0.71×** (thermally soft absolutes) — next dig is post-headers path, not another receive rewrite |
| Post-intake: is residual WriteResponse / SessionEventArgs / still HEADERS wait? | dumpasync + topN + gcdump + stage timing (`POSTFIX-POST-HEADERS.md`) | Soft box (IDE CPU); dumpasync: **no** ForceRead, **no** InterimChannel/`SendAsync` park — bridges on client `ReadRequestLine`, origin on `FrameIntake.Fill`. Stage: TTFB ~93% of total, delivery ~5%. Pooling gates not cleared | **Wait shape fixed.** Do not pool or rewrite H1 write yet. Need cool quiet remasure + high-RPS alloc/CPU sample before next code change |
| Quiet remasure after restart: does cool ratio move? Gate A/B at high RPS? | Cool pairs + dumpasync + AllocationTick (`quiet-remeasure/QUIET-REMEASURE.md`) | High perf: H1→H2 c=32 **0.71×** (31.9k/44.7k); c=64 **0.87×**. High-RPS dump: ForceRead/Interim park still **0**. AllocTick: SessionEventArgs+HeaderCollection **4.5%** (&lt;5% Gate A). Interim channel arrays ~7%+ but gated behind A. Monitor exclusive ~2.5% | **No library change.** c=32 residual confirmed; pooling/write gates still not cleared. Optional: YARP twin AllocTick for asymmetry |
| YARP twin AllocTick + InterimChannel passthrough lite | Twin `gc-verbose` + remasure (`INTERIM-LITE.md`) | TWP ~3× AllocTicks/request vs YARP; Interim Channel/segment ~11% TWP-only. Lazy `InterimChannel` when `on1xx` null; H1→H2 passthrough skips relay when no interception | Landed. Soft post-lite pair **0.82×** (26.8k/32.5k); cool High-perf confirm blocked by IDE CPU — remeasure on quiet box before publishing ≥0.80 |
| Cool confirm after InterimChannel lite | Paired c=32 High perf (`interim-lite-confirm/CONFIRM.md`) | TWP **33.6k** / YARP **44.0k** = **0.76×** (was **0.71×** pre-lite). Phase-A-class absolutes | Lite helped (~+5–7% relative) but still ≪0.80. Next: TTFB residual dig on no-intercept path |
| Post-lite TTFB dig @ ~31k RPS | dumpasync `--fields` + topN (`interim-lite-confirm/TTFB-DIG.md`) | **20** `SendAsync` on origin **writeLock** (Semaphore maxCount=1, `streamOpened=false`); **6** on `HeadersReceived`; InterimChannel still 0. Lite `on1xx=null` confirmed | Residual is **writeLock stream-open convoy**, not headers wait / WriteResponse. Next: shrink work under origin writeLock (HPACK encode+enqueue) |
| H3→H2 cool remasure + gap fix plan | Cool c=32 pair (`h3h2-fresh/CONFIRM.md`) + `FIX-PLAN.md` / canvas | H3→H2 **0.70×** (26.0k/36.9k) — wiki 0.33× stale. Same origin writeLock; H3 still always allocates InterimChannel | **P0** H3 Interim lite → **P1** shrink encode under writeLock → **P2** H3 Via/prep trim → **P3** remasure other H2 arms |
| P0+P1 bundle: H3 lite, Via skip, SoftStream=2, HPACK method cache | Cool High perf (`post-p0p1/`) | H1→H2 **0.89×** (37.3k/42.0k); soft confirm **0.82×**. H3→H2 **0.73×** (24.9k/34.2k). Max-conn 16 aborted (soft regress) | **H1→H2 c=32 bar closed.** Continue H3→H2 (≥0.80) + remasure other H2 arms |
| Remeasure H2 TLS→h2c / h2c→h2c after intake+lite era | Cool High perf c=32 20s (`passthrough-fresh/`) | H2 TLS→h2c **0.78×** (51.0k/65.8k); h2c→h2c **0.73×** (49.7k/68.3k) — up from ~0.66/0.70 wiki | Still ≪0.80 on passthrough; next dig client FrameWriter/HPACK (not origin pool) |
| HPACK static GetIndex bug + encode under writeLock + scheme patch | Cool High perf (`post-hpack-static/` + `post-hpack-confirm/`) | `StaticTable.GetIndex(name,value)` compared ByteString to string → never matched; EncodeHeaderBlock allocated `new Uri` under writeLock; mixed-transport scheme 0x86↔0x87 patch; SoftStream=1; skip Via on H2 response IsFastPath; skip NoOp HPACK decode on verbatim compressed relay | **H2 TLS→h2c 0.81×** (45.8k/56.2k) **closed**. H1→H2 **0.85×**. H3→H2 **0.72×**, h2c→h2c **0.74×** still open |
| OriginRelayPool SoftCap 8→1/2 fan-out | Cool remasure (`post-relay-soft1/2`) | Soft=1/2 did not beat Soft≈8 on h2c→h2c (extra cleartext legs) | **Reverted** SoftCap formula; residual is not origin-leg count |
| H3→H2 dump @ 26k RPS + QPACK dict encode | dumpasync (`h3-profile/`) + QPACK O(1) static lookup | **32/32** `SendAsync` on `HeadersReceived` (not writeLock); **8** origin `ReadLoop`s. SoftStream fan-out already enough | Residual is H3 session/QPACK/bridge CPU, not origin write convoy. QPACK static dict + response header path trim shipped; cool ratio still ≪0.80 — next SessionEventArgs-lite / pool |
| H3 inbound ≪ H2 / ≪ YARP H3→H3 | Cool YARP-first matrix + shape (`h3-vs-h2/`, `h3-verbatim-fair/`) | Full `SessionEventArgs` + response QPACK decode/re-encode on every H3→H3 GET; YARP cool H3→H3 ~26–28k while TWP sat ~20k (0.70×) | Session-lite for H3→H2/H3/H1 + **verbatim origin→client H3 frame relay** (H2 compressed-relay analogue). Cool H3→H3 **1.14× YARP** (29.6k / 26.0k); H3→H2 / H3→H1 ≥0.80; MITM÷cleartext **0.93** |
| H3 bodiless fast path + PrepareH2 skip + EncodeResponse + compressed DATA→wire | Cool High perf (`post-encode-response/`) | Skip InterceptionContext; drain FIN without body-pump lambdas; skip PrepareH2 RemoveHeader scan on IsFastPath; `QpackEncoder.EncodeResponse` (no List); compressed-relay DATA `ReadExact` into rented wire buffer; ReturnPayload after QPACK decode | Absolutes up (H3→H2 **31.7k**/44.1k; h2c→h2c **65.7k**/91.3k) but ratios still **~0.72×**. Lazy `BoundedBodyPipe` aborted (empty-body race). Skip linked-CTS on fast path aborted (abort cancel lost → ~0.67×). Next: SessionEventArgs-lite / pool |
| H3→H2 SessionEventArgs-lite (`H3H2FastForward`) | Cool High perf (`h3-lite-only/`) | Skip entire session/HttpWebClient/Null stream/empty Response on interception-off bodiless H3→H2; keep Request for HPACK only | **H3→H2 0.83×** (33.2k/40.0k) **closed**. Lazy BodyPipe re-tried + aborted again (empty-body hang / ~856 RPS). h2c→h2c still **0.74×** (56.7k/76.3k) |
| h2c ThreadPool IOCP floor + SoftCap32 / exclusive drain / sync cont | Cool High perf (`h2c-iocp-min/`, aborted siblings) | Profile: LowLevelLifoSemaphore wait ~46%. Mirror worker min onto IOCP; default worker floor ×8/64. SoftCap32 / exclusive FrameWriter / sync continuations / WINDOW_UPDATE enqueue / CTS TryReset all aborted (regress or hang) | **h2c→h2c 0.76×** (62.6k/82.5k). Still open vs ≥0.80 |
| Compressed finalize sync + CTS TryReset (no SoftCap change) | Cool pairs (`h2c-cts-reset2/`, `h2c-soft16/`) | Skip PendingFinalizations Task on compressed; TryReset pooled CTS | TWP absolutes up to ~65.7k on soft YARP; cool YARP-first still ~**0.75×**. SoftCap floor 16 neutral; SoftCap 32 convoy |
| Compressed END_STREAM skip force-flush WINDOW_UPDATE | Cool sandwich (`h2c-wu-batch-confirm/`) | Tiny-GET forced connection WINDOW_UPDATE pair per ~56 B response (~6% GrantReceiveCredit); batch connection credit to 384 KiB, drop stream credit on close | **h2c→h2c 1.20×** (102.8k/85.9k) **closed — TWP leads**. TWP A/B both ~102k |
| Full reverse+MITM cool audit + scheme decode-free + H2→H1 bridge trim | Cool High perf (`gap-audit/`, `gap-audit-yarp/`, `h2h1-post-cts/`, `h2h1-vs-h2h2/`) | Wiki h2c→H2 TLS 0.57× / MITM H3→H2 3.5k stale. Mixed-transport `:scheme` skips Decoder when 0x86↔0x87 patch works. H2→H1: buffer tiny bodies, skip Linked CTS / framing validator / empty Before* on IsFastPath; struct enumerator for LowercaseHeaderNames. New `reverse-http1-mitm` fair twin | **h2c→H2 TLS 1.29×** (89k/69k). H1 plain **1.19×**. H1 TLS **0.91×**. Transparent H1 MITM÷terminate **0.96×**. H2→H2 MITM **104k**. H2→H1 MITM÷cleartext **0.82–0.86×** residual (per-stream H1 fan-out × dual TLS; H2→H2 dual-TLS proves TLS not the choke) |
| Origin HTTPS: unwrap nested HttpServerStream + skip SslStream header flush | Cool (`ssl-unwrap2/`) + dump diagnosis | Direct HTTPS was NetworkStream→HttpServerStream→SslStream→HttpServerStream; header Write flushed every SslStream. Finish() no-op when Content-Length exact | Correctness/cleanup landed; cool MITM÷cleartext still **~0.84×** (47.6k/56.8k). Residual not nesting/flush |
| H2→H1 MITM÷cleartext ≪0.90 after ssl-unwrap | Cool 3/5-rep High-perf (`h2h1-hostcache/`, `h2h1-gate12/`, `h2h1-final/`, `h2h1-harden/`) + thread-time sample | Whole-RT SoftCap serialized warm SslStream reuse; `DataAvailable` closed keep-alives after exact-CL reads; per-request pool-key `StringBuilder`; cert callback `.Wait` on CompletedTask | **Create-only SoftCap** (`MaxConcurrentHttp11HttpsOriginCreates`, default `Clamp(ProcessorCount,4,32)`); always close on residual `DataAvailable` (no IsFastPath skip); cache `CachedHttp11PoolKey`; sync cert validation. Cool 5-rep median **0.90** / mean **0.92** (`h2h1-final/`); harden 3-rep median **1.03** / mean **0.91** (`h2h1-harden/`). Lock-free pool rent aborted (noisy dips) |
| H1→H2 still ≪0.80 after pool + named session micro-opts | Cool matched A/B (`matched-post-fix`) | Dual client+origin TLS + per-request session on an 8-thread box; dumpasync already showed multiple origin `ReadLoop`s (not a single-conn convoy) | **Superseded** by `h2-origin-choke/` (2026-08-18): see row above |
| H3→H2 c=8/16 lost ~30–40% vs exclusive-bag after pooling | Cool A/B (`profile-baseline`) + `dumpasync`/`dotnet-trace` @ c=16 (`h3h2-c16.dmp` / `.nettrace`) | Grow threshold 16 pinned all streams on **one** origin `ReadLoopAsync`; **716** `SemaphoreSlim` waiters; H3 GET also did HEADERS + empty DATA | Grow at **4** active streams + drain FIN then HEADERS+`END_STREAM` for bodiless H3. Recovered **8,418 @ c=16** (`profile-post-fix`, vs phase-0 **8,539**) |
| H2 TLS→h2c / h2c→h2c ~0.63–0.66× cool | `dumpasync` + sampled trace @ c=32 | `Http2FrameWriter` already on DATA path; `ForceRead` per frame header; HEADERS still two `WriteAsync` under the lock | Large-read `Http2FrameIntake` (64 KiB) + enqueue stream-scoped HEADERS on `Http2FrameWriter`. Matched cool h2c→h2c ≈ **0.70**, H2 TLS→h2c ≈ **0.66** (`matched-post-headers-writer`) |
| Cool H3→H1 ~0.36× peer (12.1k / 33.4k) | Cool pair + trace @ c=32 | **Invalid ratio**: TWP `quic-http3` vs peer HttpClient. Trace was session/`HandleAsync`, not MsQuic-native | Match clients; later dual-listen reverse H3 enables **HttpClient both sides** (`matched-httpclient-h3/`, H3→H1 ≈ **0.87**) |
| H3→H1 integ empty body; Windows ~0.79× YARP | DualListen / ForcedHttp11Origin + cool pair | Fast path buffered only known `Content-Length`; Kestrel `WriteAsync` often **chunked** → body never drained before pool Release; H1 Title-Case names paid QPACK `ToLower` every response | Drain chunked/connection-close via `LimitedStream` before Release; `NormalizeNamesToLowerAscii` + `HeaderNamesAreHttp2Normalized`; decode H2 HEADERS into Response headers (no second collection). Cool Windows H3→H1 ≈ **0.96×**, H3→H2 ≈ **1.06×**; Linux H3→H1/H2/H3 ≈ **1.04× / 1.15× / 1.20×** ([32552296839](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32552296839), [32552295495](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32552295495)) |
| H1→H3 “name normalize + tiny-body coalesce” looked like H1→H2 gap | Cool A/B (`win-parity-audit-20260822-*`) | Hypothesis: Title-Case QPACK tax + missing H1→H2-style fast commit / TLS coalesce | Implemented then **fully reverted**: cool H1→H3 fell ~**1.13× → 0.65×**. Baseline already at parity (~1.05–1.13×). Lesson: do not land probe-shaped encode/coalesce without a cool paired win — see [Local Windows lab](#local-windows-lab-developer-laptop) |
| H2→H1 64 KiB ~0.87× YARP (tiny-GET already parity) | Cool pair + code compare vs Kestrel/YARP | Streamed path stripped Content-Length then empty END_STREAM DATA; pump wrote 8 KiB fills → 8 DATA frames + trailer; `HttpStream` double-buffered socket→8 KiB→dest; QueueDataFrame + 32 KiB flatten | Keep CL + END_STREAM on last DATA; `HttpStream` large-read bypass; in-place DATA framing (flatten **kept**); skip LimitedStream/Via on known-CL fast path; raise flatten budget to **288 KiB**. Cool 64 KiB ≈ **1.13×**; 256 KiB ≈ **0.89×**. Dropping flatten alone still ~0.65× |
| H2 POST cool ~0.88× / 256 KiB H2→H1 ~0.90× | Shape c=1 vs c=32 + YARP `StreamCopier` (64 KiB) compare | c=1 TWP **leads** POST (~1.2×); c=32 loses when YARP healthy — multiplex tax (frame-loop copy + shared client writer). Extra body memcpy / coalesce experiments | **Kept:** ArrayPool request-body channel + `TryReserve` on `CopyFromAsync`. **Do not:** reserve >1 frame before enqueue; slice control frames into coalesced DATA; drop flatten |

## Guardrails while optimizing

- **Real proxy improvements only — do not game the RPS harness.** YARP (and nginx) are the *yardstick*, not the product. Land changes that reduce real work on the hot path for general reverse-proxy / keep-alive traffic: fewer allocations, fewer syscalls, less protocol waste, less interception tax when unused. Do **not** land knobs, special cases, or architecture copies whose only purpose is to inflate TWP÷YARP on the tiny-GET probe. Use the probe to find where time burns; revert experiments that do not help the proxy itself even if a noisy pair looks green.
- **Full unit + integration suites after every change.** Several perf changes introduced real regressions (the scheme mismatch, the DATA race, HTTP-version and Content-Length bugs on the bridges); the suites and the benchmark's own error SLO caught all of them the same day.
- **A standalone external repro** (`tools/H2ExternalRepro`) validates against real internet sites, which surface flow-control and settings behavior loopback benchmarks never exercise.
- **Wiki numbers carry their run IDs** and an explanation of *why* each number moved, so a future regression has a baseline with provenance.

## Checklist

1. Re-baseline with paired same-thermal A/B before believing any gap.
2. Sweep concurrency — let the curve's shape choose the tool (serialization → dumps; per-request cost → CPU sampling).
3. `dumpasync` for where requests *wait*; `dotnet-trace` for where cycles *burn*.
4. Decompose internal vs client-observed latency (`TWP_RPS_STAGE_TIMING`); a large gap means queueing upstream of the pipeline.
5. Read the faster system's source to answer named hypotheses; keep TWP's architecture.
6. Before keeping a change: confirm it is a real proxy improvement (less work / alloc / I/O on a general hot path), not a probe-only tweak to beat YARP.
7. Run the full test suites and the external repro before publishing; record run IDs in the wiki.
