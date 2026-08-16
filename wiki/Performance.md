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
| Reverse HTTP/2 saturation (sustainable) | **~6.2k RPS** (TWP) vs **~5.2k RPS** (nginx/Windows) |
| Reverse HTTP/3 saturation (peak) | **~4.3k RPS** (TWP only; nginx/Windows has no QUIC) |
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

- Body: ~64-byte JSON from Kestrel (HTTP/3 uses a QuicListener origin with the same body)
- Generator: embedded `dotnet-httpclient` for TCP arms; `quic-http3` native Quic client for UDP-only `TransparentQuicProxyEndPoint` (HttpClient cannot drive that endpoint)
- Concurrency steps vary by arm (see CSV stamps below)
- Sustainable = last step with error rate &lt; 0.1% and p99 ≤ SLO (50 ms HTTP/1, 100 ms HTTP/2/MITM, 150 ms HTTP/3)
- Peak = maximum observed RPS in the ramp
- Library defaults: `MaxCachedConnections=128` (**per upstream host**), `ListenerBackLog=1024`, `ThreadPoolWorkerThread=max(2×cores, 16)`

```powershell
pwsh tools/RpsLoadProbe/run-rps.ps1 -Mode compare
pwsh tools/RpsLoadProbe/run-rps.ps1 -Mode compare-http2
pwsh tools/RpsLoadProbe/run-rps.ps1 -Mode explicit-pool-sweep
```

### Reverse HTTP/1 — TWP vs nginx/Windows

Same Kestrel origin, same flags, sequential full-machine runs. Control is **nginx/Windows** (not Linux epoll nginx).

| Arm | Sustainable RPS | @ concurrency | Peak RPS | @ concurrency |
|---|---:|---:|---:|---:|
| TWP reverse HTTP/1 | **17,392** | 128 | **22,656** | 32 |
| nginx/Windows 1.31.3 `proxy_pass` | **8,952** | 128 | **18,113** | 16 |

CSV: `tools/RpsLoadProbe/results/rps-ramp-20260816-035548.csv` (TWP), `…-035704.csv` (nginx)

### Reverse HTTP/2 — TWP vs nginx/Windows

Client TLS+h2 → proxy → HTTPS origin (Kestrel `Http1AndHttp2`). nginx uses `listen ssl` + `http2 on` and `proxy_pass https://origin`.

| Arm | Sustainable RPS | @ concurrency | Peak RPS | @ concurrency |
|---|---:|---:|---:|---:|
| TWP reverse HTTP/2 | **6,169** | 64 | **6,169** | 64 |
| nginx/Windows 1.31.3 ssl+http2 | **5,191** | 32 | **6,826** | 16 |

nginx peaked slightly higher at low concurrency, then collapsed at c=64 (p99 SLO fail). TWP held throughput through c=64.

CSV: `tools/RpsLoadProbe/results/rps-ramp-20260816-040607.csv`

### Reverse HTTP/3 — TWP only

`TransparentQuicProxyEndPoint` → Quic HTTP/3 origin. **nginx/Windows does not support UDP/QUIC**, so there is no same-machine nginx control arm.

| Arm | Sustainable RPS | @ concurrency | Peak RPS | @ concurrency |
|---|---:|---:|---:|---:|
| TWP reverse HTTP/3 | **4,032** | 128 | **4,301** | 16 |

CSV: `tools/RpsLoadProbe/results/rps-ramp-20260816-041406.csv`

### TWP HTTPS MITM (no nginx equivalent)

| Arm | Sustainable RPS | @ concurrency | Peak RPS | @ concurrency |
|---|---:|---:|---:|---:|
| TWP HTTPS MITM | **13,044** | 64 | **14,078** | 32 |

CSV: `tools/RpsLoadProbe/results/rps-ramp-20260816-035820.csv`

Do **not** put this row in the vs-nginx table.

### Explicit multi-origin pool depth (`MaxCachedConnections`)

Explicit MITM across **16** loopback HTTPS origins (fan-out). `MaxCachedConnections` is already **per host**, not a process-wide cap.

| MaxCachedConnections | Peak RPS | Notes |
|---:|---:|---|
| 4 | **10,223** @ c=32 | Fails p99 SLO at c=64 |
| 32 | **12,977** @ c=64 | Best of the sweep |
| 128 (default) | **12,488** @ c=64 | Within noise of 32 |

**Conclusion:** keep the global default at **128** (needed for reverse single-origin saturation). Explicit multi-origin does not need a lower default or a per-endpoint override — depth is already scoped per host, and idle connections expire via `ConnectionTimeOutSeconds` (60s). Raise further only for pathological single-origin fan-in; lower via `ProxyServer.MaxCachedConnections` if you want tighter idle FD/memory bounds across thousands of hosts.

CSV: `tools/RpsLoadProbe/results/rps-ramp-20260816-041115.csv`

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
