# Performance

Titanium targets **low-overhead MITM proxying**: connection pooling, HTTP/2 multiplexing, and buffer reuse. Figures below were measured on one Windows 11 / .NET 10 machine with **RpsLoadProbe** and the Basic example in Release. Treat them as orientation, not a guarantee—re-run on your hardware.

For pooling knobs and certificate first-visit tuning, see [Performance and pooling](Home#performance-and-pooling).

## At a glance

| What | Result |
|---|---|
| HTTPS TTFB vs direct (median, 14 hosts) | Cold **≈ parity** (−1 ms); warm **−25 ms** (proxy faster) |
| HTTP/1 loopback GET (no body intercept) | **~186 µs**, **~17.5 KB** allocated / request |
| Cleartext reverse HTTP/1 peak | **~16.0k RPS** (TWP) vs **~15.5k RPS** (nginx/Windows) |
| TLS-terminate reverse HTTP/1 peak | **~16.2k RPS** (TWP) vs **~12.4k RPS** (nginx/Windows) |
| Reverse HTTP/2 peak (see topology note) | **~4.7k RPS** (TWP h2↔h2 MITM) · **~13.7k RPS** (nginx ssl+h2 → cleartext) |
| Reverse HTTP/3 peak | **~5.8k RPS** (TWP only; nginx/Windows has no QUIC) |
| Explicit HTTPS MITM peak | **~13.6k RPS** |
| Basic example footprint (Release, after load) | **~74 MB** working set · **~24–29 MB** private bytes |

### Why isn’t HTTP/3 > HTTP/2 > HTTP/1 in raw RPS?

For **tiny JSON responses** (~64 B) on loopback, that ordering is **not** expected:

1. **Topology dominates protocol.** Earlier H2 numbers looked worse than cleartext H1 because H1 reverse was **plain TCP** while H2 paid for **TLS decrypt + re-encrypt + H2 framing**. Always compare arms that share the same crypto hop count.
2. **HTTP/2/3 shine at multiplexing and head-of-line avoidance**, not at maximizing single-origin tiny-GET RPS. Industry reverse-proxy benches often show HTTP/1.1 keepalive winning raw RPS on small payloads; H2 wins page-load / many-stream latency.
3. **TWP HTTP/2 reverse today is full MITM h2↔h2** (client TLS + origin HTTPS). nginx’s control arm is **TLS terminate → cleartext H1** (`proxy_pass http://origin`), which is cheaper. A TWP `ForwardCleartext` + H2→H1 bridge path exists for that topology but still errors under saturation; publishable H2 numbers therefore use the stable MITM path and call out the topology gap.
4. On **TWP-only** native paths in the same run, **HTTP/3 peak (5.8k) > HTTP/2 peak (4.7k)**.

## HTTPS latency (direct vs MITM proxy)

Curl against 14 public HTTPS sites: direct TLS vs `http://127.0.0.1:8000` with decrypt enabled (Release Basic, HTTP/1.1 client):

| Scenario | Median Δ TTFB (proxy − direct) |
|---|---:|
| Cold | **−1 ms** |
| Warm | **−25 ms** |

## Loopback throughput and allocations

[BenchmarkDotNet](https://github.com/justcoding121/titanium-web-proxy/tree/develop/benchmarks/Titanium.Web.Proxy.Benchmarks) `ShortRun` (Release):

| Benchmark | Setup | Mean | Allocated / op |
|---|---|---:|---:|
| HTTP/1 GET through proxy | Passthrough | **186 µs** | **17.5 KB** |
| HTTP/2 multiplexed GETs | 10 concurrent streams | **3.0 ms** / batch | **~14 KB** / request |

```powershell
dotnet run -c Release --project benchmarks/Titanium.Web.Proxy.Benchmarks -- --filter '*Throughput*'
```

## Saturation RPS (this Windows machine)

Measured with [tools/RpsLoadProbe](https://github.com/justcoding121/titanium-web-proxy/tree/develop/tools/RpsLoadProbe) (Release), after removing blocking console I/O from the harness hot path (`ProbeLog` async sink). Arms run **sequentially**.

**Machine:** Windows 11 (10.0.26200), 11th Gen Intel Core i7-1185G7 @ 3.00 GHz (8 logical), 31.8 GiB RAM, .NET 10.0.10, nginx/Windows **1.31.3**.

**Defaults:** `MaxCachedConnections=128` (per host), `ListenerBackLog=1024`, `ThreadPoolWorkerThread=max(2×cores, 16)`, `ResourceLimits.MaxConcurrentStreamsPerConnection=256`. Raise any of these freely on large hosts — see [Home](Home#performance-and-pooling).

```powershell
pwsh tools/RpsLoadProbe/run-rps.ps1 -Mode compare          # cleartext H1 + MITM
pwsh tools/RpsLoadProbe/run-rps.ps1 -Mode compare-tls       # TLS-terminate H1 + H2 + H3
```

### Cleartext reverse HTTP/1

| Arm | Sustainable RPS | @ c | Peak RPS | @ c |
|---|---:|---:|---:|---:|
| TWP reverse HTTP/1 | **15,955** | 64 | **16,043** | 8 |
| nginx/Windows `proxy_pass` | **15,506** | 64 | **15,506** | 64 |

CSV: `tools/RpsLoadProbe/results/rps-ramp-20260816-043328.csv`

### TLS-terminate reverse HTTP/1 (fair crypto baseline)

Client TLS → proxy terminates → **cleartext** HTTP origin (`ForwardCleartext` / nginx `proxy_pass http://`).

| Arm | Sustainable RPS | @ c | Peak RPS | @ c |
|---|---:|---:|---:|---:|
| TWP reverse HTTP/1 TLS | **15,780** | 64 | **16,152** | 8 |
| nginx/Windows ssl → cleartext | **11,961** | 64 | **12,386** | 32 |

### Reverse HTTP/2

| Arm | Topology | Sustainable | Peak |
|---|---|---:|---:|
| TWP | Client h2 TLS → MITM → origin **HTTPS h2** | **4,711** @ 64 | **4,711** |
| nginx/Windows | Client h2 TLS → terminate → origin **cleartext H1** | **13,738** @ 32 | **13,738** |

nginx collapses at c=64 (p99 / errors). TWP’s MITM path is stable but pays double crypto + H2 framing; do not read this row as “H2 is slower than H1” without the topology column.

CSV: `tools/RpsLoadProbe/results/rps-ramp-20260816-042946.csv` (`compare-tls`)

### Reverse HTTP/3 (TWP only)

| Arm | Sustainable RPS | @ c | Peak RPS | @ c |
|---|---:|---:|---:|---:|
| TWP reverse HTTP/3 | **4,566** | 64 | **5,800** | 16 |

nginx/Windows has **no QUIC/UDP**.

### Explicit HTTPS MITM

| Arm | Sustainable RPS | @ c | Peak RPS | @ c |
|---|---:|---:|---:|---:|
| TWP HTTPS MITM | **13,198** | 64 | **13,580** | 32 |

### Explicit multi-origin pool depth

`MaxCachedConnections` is **per host**. Across 16 origins: depth **4** fails p99 at c=64; **32** and **128** are within noise (~13k). Keep default **128** (needed for reverse single-origin). No per-endpoint override required.

## Raising limits on big machines

There is **no artificial upper clamp**. Examples:

```csharp
proxy.MaxCachedConnections = 512; // live TCP pool depth per host
proxy.ResourceLimits = ProxyResourceLimits.Create(
    maxHeaderLineBytes: ProxyResourceLimits.Default.MaxHeaderLineBytes,
    maxHeaderCount: ProxyResourceLimits.Default.MaxHeaderCount,
    maxHeaderAggregateBytes: ProxyResourceLimits.Default.MaxHeaderAggregateBytes,
    maxEncodedBodyBytes: null,
    maxDecodedBodyBytes: null,
    maxDecompressionRatio: 200,
    maxConcurrentClients: null,           // or e.g. 100_000
    maxConcurrentStreamsPerConnection: 1000,
    maxPeerInitiatedIncompleteStreamResets: 100,
    maxOpenHeaderBlockFrames: 128,
    maxOpenHeaderBlockDuration: TimeSpan.FromSeconds(10),
    connectionPoolingEnabled: true,
    maxCachedConnectionsPerHost: 512,     // policy snapshot; sync with MaxCachedConnections
    maxCertificateCacheEntries: 4096);

// Reverse TLS terminate → cleartext origin:
var ep = new TransparentProxyEndPoint(IPAddress.Any, 443, decryptSsl: true)
{
    ForwardHost = "127.0.0.1",
    ForwardPort = 8080,
    ForwardCleartext = true,
    GenericCertificateName = "example.com"
};
```

## Saturation RPS (GitHub `ubuntu-latest`)

Trigger Actions → **RPS saturation (Linux)** (`workflow_dispatch`). Prefer median of 3 runs. Do not merge with Windows-local tables.

## Process footprint (Basic example)

| Metric | Approx. value |
|---|---:|
| Working set | **~74 MB** |
| Private bytes | **~24–29 MB** |
