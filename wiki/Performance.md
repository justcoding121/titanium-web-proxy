# Performance

Titanium targets **low-overhead MITM proxying**: connection pooling, HTTP/2 multiplexing, and buffer reuse. Figures below were measured on one Windows 11 / .NET 10 machine with the **Basic example built and run in Release**. Treat them as orientation, not a guarantee—re-run on your hardware.

For pooling knobs and certificate first-visit tuning, see [Performance and pooling](Home#performance-and-pooling).

## At a glance

| What | Result |
|---|---|
| HTTPS TTFB vs direct (median, 14 hosts) | Cold **≈ parity** (−1 ms); warm **−25 ms** (proxy faster) |
| HTTP/1 loopback GET (no body intercept) | **~186 µs**, **~17.5 KB** allocated / request |
| HTTP/2 loopback (10 multiplexed streams) | **~3.0 ms** / batch (**~14 KB** / request) |
| Reverse HTTP/1 saturation (peak) | **~22.7k RPS** (TWP) vs **~18.1k RPS** (nginx/Windows) |
| HTTPS MITM saturation (peak) | **~14.1k RPS** |
| Basic example footprint (Release, after load) | **~74 MB** working set · **~24–29 MB** private bytes |

## HTTPS latency (direct vs MITM proxy)

Curl against 14 public HTTPS sites: direct TLS vs `http://127.0.0.1:8000` with decrypt enabled (Release Basic, HTTP/1.1 client):

| Scenario | Median Δ TTFB (proxy − direct) |
|---|---:|
| Cold | **−1 ms** |
| Warm | **−25 ms** |

Warm paths benefit from a hot upstream pool. Absolute times vary with the public internet; the delta is what matters for proxy overhead.

## Loopback throughput and allocations

[BenchmarkDotNet](https://github.com/justcoding121/titanium-web-proxy/tree/develop/benchmarks/Titanium.Web.Proxy.Benchmarks) `ShortRun` (Release) against a local origin isolates proxy cost from the network:

| Benchmark | Setup | Mean | Allocated / op |
|---|---|---:|---:|
| HTTP/1 GET through proxy | Passthrough | **186 µs** | **17.5 KB** |
| HTTP/1 GET through proxy | Buffer request/response body | ~260 µs median | **18.9 KB** |
| HTTP/2 multiplexed GETs | 1 stream | **561 µs** | **15.7 KB** |
| HTTP/2 multiplexed GETs | 10 concurrent streams | **3.0 ms** / batch | **~14 KB** / request |
| HTTP/2 multiplexed GETs | 50 concurrent streams | **12.7 ms** / batch | **~14 KB** / request |

On this machine that is on the order of **~5k HTTP/1 requests/s** on the loopback passthrough path (`1 / 186 µs`). That figure is **sequential single-client latency**, not a saturation breaking point—see the sections below for nginx-style concurrent RPS.

```powershell
dotnet run -c Release --project benchmarks/Titanium.Web.Proxy.Benchmarks -- --filter '*Throughput*'
```

## Saturation RPS (this Windows machine)

Measured with [tools/RpsLoadProbe](https://github.com/justcoding121/titanium-web-proxy/tree/develop/tools/RpsLoadProbe) (Release). Arms run **sequentially** so each proxy gets the full CPU/RAM. Reverse-HTTP/1 uses a **3-process split** (Kestrel origin / proxy / load-gen). HTTPS MITM keeps origin+proxy in one child (shared test CA) with load-gen separate.

**Machine:** Windows 11 (10.0.26200), 11th Gen Intel Core i7-1185G7 @ 3.00 GHz (8 logical), 31.8 GiB RAM, .NET 10.0.10, nginx/Windows **1.31.3**.

**Methodology:**

- Body: ~64-byte JSON from Kestrel
- Generator: embedded `dotnet-httpclient` (not bombardier/wrk)
- Concurrency steps: 8 → 128 (3 s warmup / 12 s measure; MITM 10 s measure)
- Sustainable = last step with error rate &lt; 0.1% and p99 ≤ 50 ms (HTTP/1) or 100 ms (HTTPS MITM)
- Peak = maximum observed RPS in the ramp
- Library defaults used for the win: `MaxCachedConnections=128`, `ListenerBackLog=1024`, `ThreadPoolWorkerThread=max(2×cores, 16)`; reverse path skips TLS peek when `DecryptSsl` is false and uses `ForwardHost` (no per-request rewrite)

```powershell
pwsh tools/RpsLoadProbe/run-rps.ps1 -Mode compare
```

### Reverse HTTP/1 — TWP vs nginx/Windows

Same Kestrel origin, same flags, sequential full-machine runs. Control is **nginx/Windows** (not Linux epoll nginx).

| Arm | Sustainable RPS | @ concurrency | Peak RPS | @ concurrency |
|---|---:|---:|---:|---:|
| TWP reverse HTTP/1 | **17,392** | 128 | **22,656** | 32 |
| nginx/Windows 1.31.3 `proxy_pass` | **8,952** | 128 | **18,113** | 16 |

CSV: `tools/RpsLoadProbe/results/rps-ramp-20260816-035548.csv` (TWP), `…-035704.csv` (nginx)

### TWP HTTPS MITM (no nginx equivalent)

| Arm | Sustainable RPS | @ concurrency | Peak RPS | @ concurrency |
|---|---:|---:|---:|---:|
| TWP HTTPS MITM | **13,044** | 64 | **14,078** | 32 |

CSV: `tools/RpsLoadProbe/results/rps-ramp-20260816-035820.csv`

Do **not** put this row in the vs-nginx table.

## Saturation RPS (GitHub `ubuntu-latest`)

Closer to typical Linux nginx/`wrk` methodology: apt nginx (epoll) on a GitHub-hosted **Ubuntu VM** (currently 4 vCPU / 16 GB). Trigger manually via Actions → **RPS saturation (Linux)** (`workflow_dispatch`). Not a per-PR gate. Shared runners are noisy—prefer the **median of 3 dispatch runs**.

| Arm | Sustainable RPS (median) | Peak RPS (median) | Notes |
|---|---:|---:|---|
| TWP reverse HTTP/1 | *dispatch workflow after push* | | Same harness as local |
| apt nginx `proxy_pass` | *dispatch workflow after push* | | Linux epoll nginx |

This table is **not** comparable to dedicated-server nginx blog numbers, and must not be merged with the Windows-local table into one winner row.

## Process footprint (Basic example)

Release Basic after the HTTPS A/B pass and repeated proxy load:

| Metric | Approx. value |
|---|---:|
| Working set | **~74 MB** |
| Private bytes | **~24–29 MB** |
| CPU while idle / light load | Near **0%**; brief spikes under concurrent curl |

Includes the example host process, logging, and certificate cache—not a stripped library-only process. Idle right after start was about **~70 MB** working set / **~26 MB** private.
