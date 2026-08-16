# Performance

Titanium targets **low-overhead MITM proxying**: connection pooling, HTTP/2 multiplexing, and buffer reuse. Figures below were measured on one Windows 11 / .NET 10 machine with the **Basic example built and run in Release**. Treat them as orientation, not a guarantee—re-run on your hardware.

For pooling knobs and certificate first-visit tuning, see [Performance and pooling](Home#performance-and-pooling).

## At a glance

| What | Result |
|---|---|
| HTTPS TTFB vs direct (median, 14 hosts) | Cold **≈ parity** (−1 ms); warm **−25 ms** (proxy faster) |
| HTTP/1 loopback GET (no body intercept) | **~186 µs**, **~17.5 KB** allocated / request |
| HTTP/2 loopback (10 multiplexed streams) | **~3.0 ms** / batch (**~14 KB** / request) |
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

On this machine that is on the order of **~5k HTTP/1 requests/s** on the loopback passthrough path (`1 / 186 µs`).

```powershell
dotnet run -c Release --project benchmarks/Titanium.Web.Proxy.Benchmarks -- --filter '*Throughput*'
```

## Process footprint (Basic example)

Release Basic after the HTTPS A/B pass and repeated proxy load:

| Metric | Approx. value |
|---|---:|
| Working set | **~74 MB** |
| Private bytes | **~24–29 MB** |
| CPU while idle / light load | Near **0%**; brief spikes under concurrent curl |

Includes the example host process, logging, and certificate cache—not a stripped library-only process. Idle right after start was about **~70 MB** working set / **~26 MB** private.
