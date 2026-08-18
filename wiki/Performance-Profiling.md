# Performance Profiling

How the throughput hotspots behind the numbers on the [Performance](Performance) page were found. This page documents the techniques and tools so future performance work (or a regression hunt) can follow the same playbook instead of rediscovering it. Everything here was used in the 2026 pass that took the HTTP/2 bridge arms from ~6× behind YARP to parity-or-close.

## Contents

- [The measurement harness](#the-measurement-harness)
- [Controlling measurement noise](#controlling-measurement-noise)
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
- Every TWP arm has a **control arm** — YARP (and nginx where it can run the path) hosting the *identical* workload in the same process and session, so both sides see the same machine state.
- The probe **ramps concurrency** (typically c=8→64) and reports **sustainable RPS**: the last concurrency step that still met the error-rate and p99-latency SLO. A ramp that grows RPS but blows p99 is a queue, not throughput.
- Results land in timestamped CSVs under `tools/RpsLoadProbe/results/`; the [Performance](Performance) tables cite the run IDs so every published number is traceable to a raw file.

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
- **Prefer TWP÷YARP ratios over absolutes.** The control arm soaks up the same throttling.
- **For a targeted A/B question, run the two arms paired**: cooldown (~2 min idle), arm A, arm B immediately after — and alternate which goes first across repeats so heat bias cancels. This is how "MITM costs 0.65–0.75× of its reverse twin, and the delta is purely the extra origin TLS leg" was established: the two probe arms differ by exactly one flag (`ForwardCleartext`).
- If an arm's ratio looks newly bad, **re-measure before profiling** — several "regressions" were heat.

## Technique 1: concurrency sweep as a shape test

Cheapest tool with the highest information density. Run the arm at c=1 and at c=64 and look at the *shape*, before reaching for any profiler:

| Observation | Meaning |
|---|---|
| Slow at c=1 and c=64 by the same factor | Per-request cost (allocations, crypto, syscalls) — go CPU-profile it |
| **Faster** at c=1 but flatlines while the control arm scales | A **serialization point** — something processes streams one at a time; profilers of per-request cost will mislead you |
| Scales to a cliff, then errors/SLO failures | Resource exhaustion or a convoy (locks, pool limits, flow-control windows) |

The h2c→H1 bridge showed the second shape: TWP *beat* YARP at c=1 (6,425 vs 5,449 RPS) but flatlined at ~22k while YARP scaled to 46k. That single observation eliminated allocation work, `System.IO.Pipelines`, and syscall efficiency as hypotheses and said "find the serial section."

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
# in a second shell, once the proxy child process exists:
dotnet-trace collect -p <proxy PID>   # default profile; view the .nettrace in PerfView/VS
```

This is a *confirmation* tool more than a discovery tool here: it confirmed the residual H1→H2 gap is whole-box CPU saturation (dual TLS legs plus the per-request session pipeline filling all 8 cores at ~21k RPS, where YARP's cheaper per-request path fits ~42k) — i.e. the honest answer was "this is cost, not a bug."

## Technique 5: reference-source comparison

When a comparable system (YARP/Kestrel) is faster, read its source to answer **named hypotheses** — not to port its architecture. Two examples from this pass:

- *"Does Kestrel tune `MAX_CONCURRENT_STREAMS` dynamically?"* No — it opens additional origin connections when the stream limit is hit. TWP replicated the behavior within its own design (`Http2OriginRelayPool`).
- *"Is `System.IO.Pipelines` the advantage?"* No — TWP's buffered `HttpStream` already amortizes socket reads to one syscall per buffer drain; the memcpy `ReadOnlySequence` would remove costs ~0.02% of a request, and the TLS decrypt copy exists in both models (`SslStream` cannot produce a `ReadOnlySequence`; Kestrel copies decrypted bytes into its Pipe too). Measured support: H1 arms at parity, and TWP's c=1 latency *lower* than YARP's.

## Case studies: symptom → tool → root cause → fix

| Symptom | Tool that found it | Root cause | Fix |
|---|---|---|---|
| h2c→H1 bridge 8.5k vs YARP 46k, high system CPU | `dotnet-dump` + `dumpasync --stats` | Response emission convoy on the client write lock; many tiny socket writes | Queue all response frames through `Http2FrameWriter` (coalesced writes) — 3.4× |
| Same arm flat at ~22k at every concurrency, but faster than YARP at c=1 | Concurrency sweep + stage timing (87 µs internal vs 2.6 ms observed) | Frame loop ran each stream's BeforeRequest prefix inline (~44 µs/HEADERS) | Start the handler on the thread pool from the ordered dispatch task — 22k → 47k |
| External-site H2 downloads stalled at exactly 64 KB | Standalone repro tool (`tools/H2ExternalRepro`) + a window-size env knob | Flow-control starvation: batched WINDOW_UPDATE threshold larger than the default 65,535 B window | Advertise a Kestrel-class 768 KiB initial stream window in both directions |
| Two bridge arms at 100% errors after the passthrough change | The benchmark suite itself (error-rate SLO) | `:scheme` mismatch in compressed header relay on mixed-transport bridges | Detect and re-encode the header block with the corrected scheme |
| POST arm collapsed 842 → 9 RPS | Benchmark suite + targeted repro | Client DATA frames raced the handler dispatch and were routed before channels existed | Await the stream's dispatch task before routing its DATA frames |
| H1→H2 bridge stuck at ~0.3× YARP | Stage timing (TTFB 240 µs → 1,830 µs as c grows) + `dotnet-trace` | Whole-box CPU saturation: dual TLS legs + per-request pipeline; also one origin H2 connection per client connection | Documented as cost; origin-connection sharing is the open follow-up |

## Guardrails while optimizing

- **Full unit + integration suites after every change.** Several perf changes introduced real regressions (the scheme mismatch, the DATA race, HTTP-version and Content-Length bugs on the bridges); the suites and the benchmark's own error SLO caught all of them the same day.
- **A standalone external repro** (`tools/H2ExternalRepro`) validates against real internet sites, which surface flow-control and settings behavior loopback benchmarks never exercise.
- **Wiki numbers carry their run IDs** and an explanation of *why* each number moved, so a future regression has a baseline with provenance.

## Checklist

1. Re-baseline with paired same-thermal A/B before believing any gap.
2. Sweep concurrency — let the curve's shape choose the tool (serialization → dumps; per-request cost → CPU sampling).
3. `dumpasync` for where requests *wait*; `dotnet-trace` for where cycles *burn*.
4. Decompose internal vs client-observed latency (`TWP_RPS_STAGE_TIMING`); a large gap means queueing upstream of the pipeline.
5. Read the faster system's source to answer named hypotheses; keep TWP's architecture.
6. Run the full test suites and the external repro before publishing; record run IDs in the wiki.
