# Performance

Titanium targets **low-overhead MITM proxying**: connection pooling, HTTP/2 multiplexing, and buffer reuse. Numbers below are **Release** measurements with [RpsLoadProbe](https://github.com/justcoding121/titanium-web-proxy/tree/develop/tools/RpsLoadProbe) (and BenchmarkDotNet / Basic example where noted). Absolute RPS varies by hardware, OS, and background load — compare **within a table**, not across Windows vs Linux.

For pooling knobs and certificate first-visit tuning, see [Performance and pooling](Home#performance-and-pooling).

## Measurement environment

### Windows (developer laptop)

| | |
|---|---|
| OS | Windows 11 (10.0.26200) |
| CPU | 11th Gen Intel Core i7-1185G7 @ 3.00 GHz (8 logical processors) |
| RAM | 31.8 GiB |
| Runtime | .NET 10.0.10 |
| nginx | nginx/Windows **1.31.3** |
| Harness | RpsLoadProbe Release; arms run **sequentially** |

### Linux (GitHub-hosted `ubuntu-latest`)

| | |
|---|---|
| OS | Ubuntu 24.04.4 LTS |
| CPU | AMD EPYC 7763 (4 logical processors on the VM) |
| RAM | 15.6 GiB |
| Runtime | .NET 10.0.11 |
| nginx | nginx/1.24.0 (Ubuntu) |
| Harness | RpsLoadProbe Release; median of 3 repeats where noted |

**How to read the tables**

- **Mode**: **Reverse** = transparent fixed-forward (may TLS-terminate to a cleartext origin). **MITM** = proxy decrypts the client crypto **and** speaks TLS/QUIC to the origin (or explicit decrypt proxy), so both legs are visible in the clear inside TWP. nginx cannot do MITM.
- **Sustainable** = last concurrency that still met error/latency SLOs. **Peak** = highest RPS in that ramp.
- **Winner** = higher **sustainable** RPS when **both** TWP and nginx have a number. Left **blank** when nginx cannot run that path (no fair comparison).
- *Not possible* = product cannot do that path. *Not measured* = path exists but no published number yet for that OS.

```powershell
pwsh tools/RpsLoadProbe/run-rps.ps1 -Mode compare-same
pwsh tools/RpsLoadProbe/run-rps.ps1 -Mode compare-terminate
pwsh tools/RpsLoadProbe/run-rps.ps1 -Mode compare-bridges
pwsh tools/RpsLoadProbe/run-rps.ps1 -Mode compare-mitm
```

## Windows — Titanium vs nginx

Client / origin: HTTP version and whether TLS is used (`plain` = cleartext, `TLS` = encrypted, `QUIC` = HTTP/3).

| Mode | Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | Winner |
|---|---|---|---:|---:|---:|---:|---|
| Reverse | HTTP/1 · plain | HTTP/1 · plain | **29,376** | **29,376** | **24,587** | **24,587** | **TWP** |
| Reverse | HTTP/1 · TLS | HTTP/1 · plain | **19,951** | **29,511** | **12,072** | **13,501** | **TWP** |
| MITM | HTTP/1 · TLS | HTTP/1 · TLS | **29,713** | **30,755** | *Not possible* (no MITM) | *Not possible* | |
| Reverse | HTTP/2 · TLS | HTTP/1 · plain | **9,875** | **10,441** | **5,841** | **13,465** | **TWP** |
| MITM | HTTP/2 · TLS | HTTP/1 · TLS | **9,774** | **10,242** | *Not possible* (no MITM) | *Not possible* | |
| MITM | HTTP/2 · TLS | HTTP/2 · TLS | **6,222** | **6,222** | *Not possible* (no MITM) | *Not possible* | |
| Reverse | HTTP/2 · TLS | HTTP/2 · plain | **6,889** | **6,889** | *Not possible* | *Not possible* | |
| Reverse | HTTP/2 · plain | HTTP/1 · plain | **10,757** | **11,088** | *Not possible* | *Not possible* | |
| Reverse | HTTP/2 · plain | HTTP/2 · plain | **6,344** | **6,344** | *Not possible* | *Not possible* | |
| Reverse | HTTP/2 · plain | HTTP/2 · TLS | **6,036** | **6,036** | *Not possible* | *Not possible* | |
| Reverse | HTTP/2 · plain | HTTP/3 · QUIC | **7,587** | **7,949** | *Not possible* (no QUIC) | *Not possible* | |
| Reverse | HTTP/3 · QUIC | HTTP/1 · plain | **2,246** | **3,541** | *Not possible* (no QUIC) | *Not possible* | |
| MITM | HTTP/3 · QUIC | HTTP/1 · TLS | **10,416** | **10,416** | *Not possible* (no QUIC) | *Not possible* | |
| MITM | HTTP/3 · QUIC | HTTP/2 · TLS | **2,127** | **7,779** | *Not possible* (no QUIC) | *Not possible* | |
| MITM | HTTP/3 · QUIC | HTTP/3 · QUIC | **6,479** | **7,430** | *Not possible* (no QUIC) | *Not possible* | |
| MITM | HTTP/1 · TLS | HTTP/2 · TLS | **16,423** | **16,709** | *Not possible* | *Not possible* | |
| MITM | HTTP/1 · TLS | HTTP/3 · QUIC | **16,524** | **16,524** | *Not possible* (no QUIC) | *Not possible* | |
| MITM | HTTP/2 · TLS | HTTP/3 · QUIC | **7,151** | **7,151** | *Not possible* (no QUIC) | *Not possible* | |

Windows sources: `compare-same` / `compare-terminate` / `compare-bridges` / local `compare-mitm` (`rps-ramp-20260816-123409`, warmup 1s; measure 3s; concurrency 8–64). H3→H1 TLS MITM from `rps-ramp-20260816-132745` after origin TCP pool release. All published TWP arms **0% error**.

nginx/Windows is a limited port. Use it for **same-OS** comparison only — not as the industry nginx baseline.

**H2 TLS → H1 plain on Windows:** TWP wins **sustain** when both products run reverse terminate. nginx can still post a higher **short-burst peak** at low concurrency before collapsing.

## Linux — Titanium vs nginx

Median of **3 repeats** from Actions runs [31954143134](https://github.com/justcoding121/titanium-web-proxy/actions/runs/31954143134) (`compare-same`), [31954144794](https://github.com/justcoding121/titanium-web-proxy/actions/runs/31954144794) (`compare-terminate`), [31955723330](https://github.com/justcoding121/titanium-web-proxy/actions/runs/31955723330) (`compare-mitm`), [31955725199](https://github.com/justcoding121/titanium-web-proxy/actions/runs/31955725199) (`compare-bridges`). Ceiling control: [31954146336](https://github.com/justcoding121/titanium-web-proxy/actions/runs/31954146336) (`compare-ceiling`). Warmup 2s / measure 8s; concurrency 8, 16, 32, 64. All published TWP arms **0% error**. **Linux nginx is the authoritative nginx baseline.** The RPS workflow installs `libmsquic` (`QuicListener.IsSupported=true` on `ubuntu-latest`).

**Why nginx still leads on H1 plain reverse (~0.71×):** fair harness (split processes, same Kestrel origin). Absolute RPS swings by GHA VM; prefer the **ratio**. A bare C# HTTP/1 reverse on the same job is ~**0.81×** nginx (`compare-ceiling`) — most of the gap is managed runtime vs nginx’s thin C `proxy_pass`, not remaining TWP session work.

| Mode | Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | Winner |
|---|---|---|---:|---:|---:|---:|---|
| Reverse | HTTP/1 · plain | HTTP/1 · plain | **27,826** | **27,826** | **39,254** | **39,254** | **nginx** |
| Reverse | HTTP/1 · TLS | HTTP/1 · plain | **29,808** | **29,808** | **44,200** | **44,200** | **nginx** |
| MITM | HTTP/1 · TLS | HTTP/1 · TLS | **25,493** | **25,493** | *Not possible* (no MITM) | *Not possible* | |
| Reverse | HTTP/2 · TLS | HTTP/1 · plain | **18,583** | **18,726** | **21,177** | **29,330** | **nginx** |
| MITM | HTTP/2 · TLS | HTTP/1 · TLS | **17,868** | **17,868** | *Not possible* (no MITM) | *Not possible* | |
| MITM | HTTP/2 · TLS | HTTP/2 · TLS | **8,508** | **8,508** | *Not possible* (no MITM) | *Not possible* | |
| Reverse | HTTP/2 · TLS | HTTP/2 · plain | **7,345** | **7,546** | *Not possible* | *Not possible* | |
| Reverse | HTTP/2 · plain | HTTP/1 · plain | **18,324** | **18,324** | *Not possible* | *Not possible* | |
| Reverse | HTTP/2 · plain | HTTP/2 · plain | **9,108** | **9,108** | *Not possible* | *Not possible* | |
| Reverse | HTTP/2 · plain | HTTP/2 · TLS | **5,846** | **5,846** | *Not possible* | *Not possible* | |
| Reverse | HTTP/2 · plain | HTTP/3 · QUIC | **13,910** | **13,910** | *Not possible* (no QUIC) | *Not possible* | |
| Reverse | HTTP/3 · QUIC | HTTP/1 · plain | **19,981** | **19,981** | *Not possible* (no QUIC) | *Not possible* | |
| MITM | HTTP/3 · QUIC | HTTP/1 · TLS | **18,153** | **18,153** | *Not possible* (no QUIC) | *Not possible* | |
| MITM | HTTP/3 · QUIC | HTTP/2 · TLS | **3,049** | **9,909** | *Not possible* (no QUIC) | *Not possible* | |
| MITM | HTTP/3 · QUIC | HTTP/3 · QUIC | **11,027** | **11,027** | *Not possible* (no QUIC) | *Not possible* | |
| MITM | HTTP/1 · TLS | HTTP/2 · TLS | **16,783** | **16,783** | *Not possible* | *Not possible* | |
| MITM | HTTP/1 · TLS | HTTP/3 · QUIC | **19,233** | **19,233** | *Not possible* (no QUIC) | *Not possible* | |
| MITM | HTTP/2 · TLS | HTTP/3 · QUIC | **13,803** | **13,803** | *Not possible* (no QUIC) | *Not possible* | |

On this GHA shape, TWP H1 plain ÷ nginx H1 plain ≈ **0.71** (27,826 / 39,254). H1 TLS terminate ≈ **0.67** (29,808 / 44,200). Absolute RPS swings by VM; prefer the **ratio** and **median across repeats**.

### Why isn’t HTTP/3 > HTTP/2 > HTTP/1 in raw RPS?

For **tiny JSON responses** (~64 B) on loopback, that ordering is **not** expected: topology (TLS hop count, terminate vs MITM) dominates; HTTP/2 and HTTP/3 help multiplexing, not single-origin tiny-GET RPS.

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
