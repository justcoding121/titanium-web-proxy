# Performance

Titanium targets **low-overhead MITM proxying**: connection pooling, HTTP/2 multiplexing, and buffer reuse. Numbers below are **Release** measurements with [RpsLoadProbe](https://github.com/justcoding121/titanium-web-proxy/tree/develop/tools/RpsLoadProbe) (and BenchmarkDotNet / Basic example where noted). Publishable tables cite **GitHub Actions** medians on matched **4 vCPU / 16 GiB** runners. Absolute RPS still varies by OS kernel, TLS, and MsQuic packaging â€” compare **within a table**, not across Windows vs Linux.

Control arms: **nginx** (native C reverse-proxy ceiling; Linux is authoritative) and **YARP** (`Yarp.ReverseProxy`, managed .NET reverse proxy). Neither can MITM (no CONNECT / forged certs). FiddlerCore is not compared (commercial debugger license; not a throughput peer).

For pooling knobs and certificate first-visit tuning, see [Performance and pooling](Home#performance-and-pooling). For the local cool A/B lab, laptop tables, and the techniques used to find each hotspot, see [Performance Profiling](Performance-Profiling).

## Contents

- [Measurement environment](#measurement-environment)
    - [Windows (GitHub-hosted `windows-latest`)](#windows-github-hosted-windows-latest)
    - [Linux (GitHub-hosted `ubuntu-latest`)](#linux-github-hosted-ubuntu-latest)
- [Windows â€” Titanium vs nginx vs YARP](#windows--titanium-vs-nginx-vs-yarp)
- [Linux â€” Titanium vs nginx vs YARP](#linux--titanium-vs-nginx-vs-yarp)
    - [Tiny JSON reverse is nginxâ€™s best case on Linux](#tiny-json-reverse-is-nginxs-best-case-on-linux)
    - [Why isnâ€™t HTTP/3 > HTTP/2 > HTTP/1 in raw RPS?](#why-isnt-http3--http2--http1-in-raw-rps)
- [Heavier reverse workloads](#heavier-reverse-workloads)
    - [Windows â€” heavier reverse GET (64 KiB / 256 KiB)](#windows--heavier-reverse-get-64-kib--256-kib)
    - [Linux â€” heavier reverse GET (64 KiB / 256 KiB)](#linux--heavier-reverse-get-64-kib--256-kib)
    - [Windows â€” POST 64 KiB request + 64 KiB response](#windows--post-64-kib-request--64-kib-response)
    - [Linux â€” POST 64 KiB request + 64 KiB response](#linux--post-64-kib-request--64-kib-response)
    - [Windows â€” lossy / high-RTT (H2 HOL / H3 loss)](#windows--lossy--high-rtt-h2-hol--h3-loss)
    - [Linux â€” lossy / high-RTT (H2 HOL / H3 loss)](#linux--lossy--high-rtt-h2-hol--h3-loss)
    - [Architecture-sensitive](#architecture-sensitive)
    - [TLS termination cost (H1 TLS â†’ cleartext origin)](#tls-termination-cost-h1-tls--cleartext-origin)
- [Other measurements](#other-measurements)
- [Raising limits on large hosts](#raising-limits-on-large-hosts)

## Measurement environment

Both OS use the standard public-repo GitHub-hosted runner class (**4 vCPU / 16 GiB / 14 GB SSD**). Same harness knobs (`workflow_dispatch` [RPS saturation](https://github.com/justcoding121/titanium-web-proxy/actions/workflows/rps-saturation.yml): warmup 2s / measure 8s; concurrency 8, 16, 32, 64; median of 3 repeats). Prefer **TWPÃ·YARP** / **TWPÃ·nginx** ratios over absolute RPS.

Laptop High-perf / cool-paired Windows numbers live on [Performance Profiling â€” Local Windows lab](Performance-Profiling#local-windows-lab-developer-laptop). Do not mix those absolutes into the tables below.

### Windows (GitHub-hosted `windows-latest`)

| | |
|---|---|
| OS | Windows Server (GitHub-hosted `windows-latest`) |
| CPU | **4** logical processors |
| RAM | **16** GiB |
| Runtime | .NET 10.0.x |
| nginx | nginx/Windows **1.31.3** (same-OS only; no QUIC) |
| YARP | Yarp.ReverseProxy **2.3.0** |
| Harness | RpsLoadProbe Release; median of 3 repeats |

### Linux (GitHub-hosted `ubuntu-latest`)

| | |
|---|---|
| OS | Ubuntu 24.04.x LTS |
| CPU | **4** logical processors (AMD EPYC; runners this pass were 7763 / 9V74) |
| RAM | **16** GiB |
| Runtime | .NET 10.0.11 |
| nginx | nginx/**1.31.4** (nginx.org mainline, `--with-http_v3_module`) |
| YARP | Yarp.ReverseProxy **2.3.0** |
| Harness | RpsLoadProbe Release; median of 3 repeats where noted |

**How to read the tables**

- **Mode**: **Reverse** = transparent fixed-forward (may TLS-terminate to a cleartext origin, or re-encrypt to a configured HTTPS/QUIC origin). **MITM** = both legs are visible in the clear inside TWP â€” either by decrypting client TLS/QUIC (forged cert / CONNECT) **or** by accepting an already-cleartext client (explicit HTTP proxy / inspectable transparent reverse) while still speaking plain or TLS to the origin. nginx and YARP cannot do MITM. **HTTP/3 has no cleartext client** (QUIC always encrypted).
- **Sustainable** = last concurrency that still met error/latency SLOs. **Peak** = highest RPS in that ramp.
- ðŸŸ¢ = highest **sustainable** RPS among products that have a number on that row. Omitted when only TWP can run the path (no fair multi-product comparison).
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
pwsh tools/RpsLoadProbe/run-rps.ps1 -Mode compare-arch
```

## Windows â€” Titanium vs nginx vs YARP

Client / origin: HTTP version and whether TLS is used (`plain` = cleartext, `TLS` = encrypted, `QUIC` = HTTP/3).

Median of **3 repeats** on `windows-latest` (4 vCPU / 16 GiB). Same @ `62e5efcd` ([32627482477](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32627482477)); bridges @ `8789d6de` ([32636039240](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32636039240)); MITM @ `1b5ca9f9` ([32588707712](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32588707712)). Warmup 2s / measure 8s; concurrency 8, 16, 32, 64. Laptop High-perf / cool-paired numbers stay on the [local lab](Performance-Profiling#local-windows-lab-developer-laptop).

**Load generators:** Reverse inbound H3 arms use **`dotnet-httpclient`** (`http_version=3.0`, `RequestVersionExact`) after dual-listen reverse H3. nginx/Windows is same-OS only (no QUIC).

| Mode | Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---|---|---:|---:|---:|---:|---:|---:|
| Reverse | HTTP/1 Â· plain | HTTP/1 Â· plain | ðŸŸ¢ **22,974** | **22,974** | **13,916** | **13,916** | **22,066** | **22,066** |
| Reverse | HTTP/1 Â· plain | HTTP/1 Â· TLS | ðŸŸ¢ **20,282** | **20,282** | *Not possible* | *Not possible* | **19,046** | **19,046** |
| Reverse | HTTP/1 Â· TLS | HTTP/1 Â· plain | ðŸŸ¢ **19,831** | **19,831** | **8,785** | **8,968** | **18,646** | **18,646** |
| Reverse | HTTP/1 · TLS | HTTP/2 · TLS | 🟢 **23,662** | **23,662** | *Not possible* | *Not possible* | **22,878** | **22,878** |
| Reverse | HTTP/1 · TLS | HTTP/3 · QUIC | 🟢 **14,389** | **14,389** | *Not possible* (no QUIC) | *Not possible* (no QUIC) | **13,806** | **13,806** |
| Reverse | HTTP/2 · plain | HTTP/1 · plain | **31,031** | **31,031** | *Not possible* | *Not possible* | 🟢 **32,282** | **32,282** |
| Reverse | HTTP/2 · plain | HTTP/2 · plain | 🟢 **89,143** | **89,143** | *Not possible* | *Not possible* | **65,827** | **65,827** |
| Reverse | HTTP/2 Â· plain | HTTP/2 Â· TLS | ðŸŸ¢ **76,008** | **76,008** | *Not possible* | *Not possible* | **57,302** | **57,302** |
| Reverse | HTTP/2 · plain | HTTP/3 · QUIC | 🟢 **31,394** | **31,394** | *Not possible* (no QUIC) | *Not possible* (no QUIC) | **30,002** | **30,002** |
| Reverse | HTTP/2 · TLS | HTTP/1 · plain | 🟢 **30,418** | **30,418** | **11,172** | **11,172** | **29,457** | **29,457** |
| Reverse | HTTP/2 · TLS | HTTP/2 · plain | 🟢 **84,001** | **84,001** | *Not possible* | *Not possible* | **55,908** | **55,908** |
| Reverse | HTTP/2 · TLS | HTTP/3 · QUIC | 🟢 **31,105** | **31,105** | *Not possible* (no QUIC) | *Not possible* (no QUIC) | **27,323** | **27,323** |
| Reverse | HTTP/3 · QUIC | HTTP/1 · plain | **13,040** | **13,040** | *Not possible* (no QUIC) | *Not possible* (no QUIC) | 🟢 **14,691** | **14,691** |
| Reverse | HTTP/3 · QUIC | HTTP/2 · TLS | 🟢 **24,027** | **24,027** | *Not possible* (no QUIC) | *Not possible* (no QUIC) | **22,852** | **22,852** |
| Reverse | HTTP/3 Â· QUIC | HTTP/3 Â· QUIC | ðŸŸ¢ **20,726** | **20,726** | *Not possible* (no QUIC) | *Not possible* (no QUIC) | **13,715** | **13,715** |
| MITM | HTTP/1 Â· plain | HTTP/1 Â· plain | **24,782** | **24,782** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/1 Â· plain | HTTP/1 Â· TLS | **25,503** | **25,503** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/1 Â· TLS | HTTP/1 Â· plain | **24,089** | **24,089** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/1 Â· TLS | HTTP/2 Â· TLS | **30,918** | **30,918** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/1 Â· TLS | HTTP/3 Â· QUIC | **16,345** | **16,345** | *Not possible* (no QUIC) | *Not possible* (no QUIC) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 Â· plain | HTTP/1 Â· plain | **37,432** | **37,432** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 Â· plain | HTTP/2 Â· plain | **92,679** | **92,679** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 Â· plain | HTTP/2 Â· TLS | **80,616** | **80,616** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 Â· plain | HTTP/3 Â· QUIC | **34,415** | **34,415** | *Not possible* (no QUIC) | *Not possible* (no QUIC) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 Â· TLS | HTTP/1 Â· plain | **36,313** | **36,313** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 Â· TLS | HTTP/2 Â· plain | **86,878** | **86,878** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 Â· TLS | HTTP/3 Â· QUIC | **33,304** | **33,304** | *Not possible* (no QUIC) | *Not possible* (no QUIC) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/3 Â· QUIC | HTTP/1 Â· plain | **17,578** | **17,578** | *Not possible* (no QUIC) | *Not possible* (no QUIC) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/3 Â· QUIC | HTTP/2 Â· TLS | **26,686** | **26,686** | *Not possible* (no QUIC) | *Not possible* (no QUIC) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/3 Â· QUIC | HTTP/3 Â· QUIC | **23,095** | **23,095** | *Not possible* (no QUIC) | *Not possible* (no QUIC) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/1 Â· plain (CONNECT) | HTTP/1 Â· TLS | **19,990** | **19,990** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/1 Â· TLS | HTTP/1 Â· TLS | **22,308** | **22,308** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 Â· TLS | HTTP/2 Â· TLS | **79,620** | **79,620** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 Â· TLS | HTTP/1 Â· TLS | **28,034** | **28,034** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/3 Â· QUIC | HTTP/1 Â· TLS | **15,082** | **15,082** | *Not possible* (no QUIC) | *Not possible* (no QUIC) | *Not possible* (no MITM) | *Not possible* (no MITM) |

TWP÷YARP H1 plain ≈ **1.04×** (22,974 / 22,066); H1 TLS terminate ≈ **1.06×** (19,831 / 18,646). Open Win gaps vs YARP (gate **>1.00×**): h2c→H1 ≈ **0.96×**, H3→H1 ≈ **0.89×**. Closed this tip: H1→H3 ≈ **1.04×**, h2c→H3 ≈ **1.05×**. Cool laptop pairs still lead the open arms; dig continues. H3→H3 ≈ **1.67×**. Prefer ratios over absolute RPS on GHA VMs. MITM publishes the same **15** Client×Origin pairs as Reverse (inspectable/decrypt), then dual-crypto extras (CONNECT, TLS↔TLS). nginx/YARP cannot MITM.

## Linux â€” Titanium vs nginx vs YARP

Median of **3 repeats** on `ubuntu-latest` (4 vCPU / 16 GiB). Same @ `62e5efcd` ([32627482477](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32627482477)); bridges @ `8789d6de` ([32636039240](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32636039240)); MITM @ `1b5ca9f9` ([32588707712](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32588707712)). Warmup 2s / measure 8s; concurrency 8, 16, 32, 64. **Linux nginx is the authoritative nginx baseline.** The RPS workflow installs nginx.org mainline (`http_v3_module`) and `libmsquic` (`QuicListener.IsSupported=true` on `ubuntu-latest`).

TWPÃ·nginx H1 plain reverse â‰ˆ **0.76** (65,887 / 87,232); TWPÃ·YARP H1 plain â‰ˆ **1.12Ã—** (65,887 / 58,658). Prefer ratios over absolute RPS on GHA VMs.

| Mode | Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---|---|---:|---:|---:|---:|---:|---:|
| Reverse | HTTP/1 Â· plain | HTTP/1 Â· plain | **65,887** | **65,926** | ðŸŸ¢ **87,232** | **87,232** | **58,658** | **58,734** |
| Reverse | HTTP/1 Â· plain | HTTP/1 Â· TLS | ðŸŸ¢ **55,156** | **55,156** | *Not possible* | *Not possible* | **50,228** | **50,228** |
| Reverse | HTTP/1 Â· TLS | HTTP/1 Â· plain | **52,041** | **52,275** | ðŸŸ¢ **65,621** | **65,621** | **45,466** | **45,466** |
| Reverse | HTTP/1 · TLS | HTTP/2 · TLS | 🟢 **29,775** | **29,775** | *Not possible* | *Not possible* | **28,677** | **28,677** |
| Reverse | HTTP/1 · TLS | HTTP/3 · QUIC | 🟢 **23,062** | **23,062** | *Not possible* (no H3 origin) | *Not possible* (no H3 origin) | **21,278** | **21,278** |
| Reverse | HTTP/2 · plain | HTTP/1 · plain | 🟢 **41,028** | **41,028** | *Not possible* | *Not possible* | **39,759** | **39,759** |
| Reverse | HTTP/2 · plain | HTTP/2 · plain | 🟢 **73,464** | **73,464** | *Not possible* | *Not possible* | **55,555** | **55,555** |
| Reverse | HTTP/2 Â· plain | HTTP/2 Â· TLS | ðŸŸ¢ **96,393** | **96,393** | *Not possible* | *Not possible* | **72,066** | **72,066** |
| Reverse | HTTP/2 · plain | HTTP/3 · QUIC | 🟢 **31,206** | **31,206** | *Not possible* (no H3 origin) | *Not possible* (no H3 origin) | **30,640** | **30,640** |
| Reverse | HTTP/2 · TLS | HTTP/1 · plain | 🟢 **38,024** | **38,024** | **32,404** | **46,118** | **34,503** | **34,503** |
| Reverse | HTTP/2 · TLS | HTTP/2 · plain | 🟢 **68,702** | **68,702** | *Not possible* | *Not possible* | **45,383** | **45,383** |
| Reverse | HTTP/2 · TLS | HTTP/3 · QUIC | 🟢 **30,303** | **30,303** | *Not possible* (no H3 origin) | *Not possible* (no H3 origin) | **26,549** | **26,549** |
| Reverse | HTTP/3 · QUIC | HTTP/1 · plain | 🟢 **23,373** | **23,373** | **15,563** | **18,987** | **22,070** | **22,070** |
| Reverse | HTTP/3 · QUIC | HTTP/2 · TLS | 🟢 **28,967** | **28,967** | *Not possible* (no H3→H2) | *Not possible* (no H3→H2) | **25,162** | **25,162** |
| Reverse | HTTP/3 Â· QUIC | HTTP/3 Â· QUIC | ðŸŸ¢ **28,104** | **28,104** | *Not possible* (no H3 origin) | *Not possible* (no H3 origin) | **23,708** | **23,708** |
| MITM | HTTP/1 Â· plain | HTTP/1 Â· plain | **29,982** | **29,982** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/1 Â· plain | HTTP/1 Â· TLS | **27,856** | **27,856** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/1 Â· TLS | HTTP/1 Â· plain | **23,869** | **23,869** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/1 Â· TLS | HTTP/2 Â· TLS | **29,829** | **29,829** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/1 Â· TLS | HTTP/3 Â· QUIC | **20,195** | **20,195** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 Â· plain | HTTP/1 Â· plain | **40,923** | **40,923** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 Â· plain | HTTP/2 Â· plain | **73,146** | **73,146** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 Â· plain | HTTP/2 Â· TLS | **60,131** | **60,131** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 Â· plain | HTTP/3 Â· QUIC | **30,523** | **30,523** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 Â· TLS | HTTP/1 Â· plain | **37,691** | **37,691** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 Â· TLS | HTTP/2 Â· plain | **64,854** | **64,854** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 Â· TLS | HTTP/3 Â· QUIC | **29,689** | **29,689** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/3 Â· QUIC | HTTP/1 Â· plain | **22,443** | **22,443** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/3 Â· QUIC | HTTP/2 Â· TLS | **28,182** | **28,182** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/3 Â· QUIC | HTTP/3 Â· QUIC | **21,668** | **21,668** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/1 Â· plain (CONNECT) | HTTP/1 Â· TLS | **20,981** | **20,981** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/1 Â· TLS | HTTP/1 Â· TLS | **22,112** | **22,112** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 Â· TLS | HTTP/2 Â· TLS | **57,725** | **57,725** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 Â· TLS | HTTP/1 Â· TLS | **31,697** | **31,697** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/3 Â· QUIC | HTTP/1 Â· TLS | **21,573** | **21,573** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |

On this GHA shape, TWP H1 plain ÷ nginx H1 plain ≈ **0.76** (65,887 / 87,232). H1 TLS terminate ≈ **0.79** (52,041 / 65,621). TWP÷YARP H1 plain ≈ **1.12×**; H1 TLS terminate ≈ **1.14×**. Open vs YARP: none on bridges @ `8789d6de` (all published TWP÷YARP **>1.00×**). Absolute RPS swings by VM; prefer the **ratio** and **median across repeats**. MITM publishes the same **15** Client×Origin pairs as Reverse (inspectable/decrypt), then dual-crypto extras (CONNECT, TLS↔TLS). nginx/YARP cannot MITM.

**nginx HTTP/3:** inbound QUIC terminate â†’ cleartext H1 (`nginx-reverse-http3-cleartext`) @ `11e32f1c` bridges: sustain **12,727** / peak **15,409**. TWP/YARP H3â†’H1 on this row are from the same bridges pass. nginx still cannot speak HTTP/3 to an origin (no H3 upstream in this conf).

**YARP HTTP/3 (this matrix):** TWP leads H3→H1 ≈ **1.06×** (23,373 / 22,070), H3→H2 ≈ **1.15×** (28,967 / 25,162). H1→H2 ≈ **1.04×** (29,775 / 28,677). H1→H3 ≈ **1.08×** (23,062 / 21,278). h2c→H3 ≈ **1.02×**.

**Windows vs Linux:** both CI envs are **4 vCPU / 16 GiB**, but do **not** compare absolute RPS across OS. Linux nginx still leads H1 plain/TLS terminate (TWP second, ahead of YARP). Windows reverse tiny-GET still has open TWP÷YARP cells on bridges @ `8789d6de` (h2c→H1 / H3→H1). Cool laptop notes remain on [Performance Profiling](Performance-Profiling#local-windows-lab-developer-laptop).


### Tiny JSON reverse is nginxâ€™s best case on Linux

The tables above use **~64 B keep-alive GET** on loopback. On Linux H1 reverse, nginx leads; YARP sits near TWP. Heavier bodies, POSTs, TLS handshake cost, and lossy/HOL workloads (below) change the picture. MITM rows remain TWP-only. nginx HTTP/3 is inbound-terminate only (see note above).

### Why isnâ€™t HTTP/3 > HTTP/2 > HTTP/1 in raw RPS?

For **tiny JSON responses** (~64 B) on loopback, that ordering is **not** expected: topology (TLS hop count, terminate vs MITM) dominates; HTTP/2 and HTTP/3 help multiplexing, not single-origin tiny-GET RPS. See the **lossy** tables below for a workload where protocol design matters.

## Heavier reverse workloads

Separate from the tiny-GET matrix. Same measurement environments. Modes: `compare-bodies`, `compare-post`, `compare-lossy`, `compare-tls-cost`, `compare-arch` in [RpsLoadProbe](https://github.com/justcoding121/titanium-web-proxy/tree/develop/tools/RpsLoadProbe). **PUT with the same body is the same proxy work as POST; DELETE with no body matches GET** â€” only POST is published. Bodies/POST/lossy stay **half-duplex**. `compare-arch` is the slow-consumer / early-response / duplex set. Laptop numbers are on [Performance-Profiling](Performance-Profiling#architecture-sensitive); CI medians go in the tables below.

Lossy link = **userspace** shim (not kernel `netem`): TCP gets per-buffer delay + occasional whole-connection stalls (honest HOL for multiplexed H2); UDP gets per-datagram delay + drops (QUIC). `compare-lossy` publishes H1/H2/H3; H3 is where the protocol design is supposed to matter.

### Windows â€” heavier reverse GET (64 KiB / 256 KiB)

Median of **3** repeats on `windows-latest` @ `8ac422ee`. Source: Actions [32631121563](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32631121563) (`compare-bodies`). Warmup 2s / measure 8s.

| Body | Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---|---|---:|---:|---:|---:|---:|---:|
| 64 KiB | HTTP/1 · TLS | HTTP/1 · plain | 🟢 **11,026** | **12,257** | **1,129** | **1,184** | **9,985** | **11,229** |
| 64 KiB | HTTP/2 · TLS | HTTP/1 · plain | 🟢 **10,112** | **10,346** | **1,006** | **1,031** | **8,630** | **9,265** |
| 64 KiB | HTTP/3 · QUIC | HTTP/1 · plain | 🟢 **5,361** | **6,006** | *Not possible* (no QUIC) | *Not possible* (no QUIC) | **4,913** | **4,913** |
| 256 KiB | HTTP/1 · TLS | HTTP/1 · plain | 🟢 **3,323** | **3,687** | **274** | **309** | **2,244** | **2,244** |
| 256 KiB | HTTP/2 · TLS | HTTP/1 · plain | 🟢 **2,414** | **2,438** | **222** | **222** | **2,302** | **2,642** |
| 256 KiB | HTTP/3 · QUIC | HTTP/1 · plain | 🟢 **1,495** | **1,569** | *Not possible* (no QUIC) | *Not possible* (no QUIC) | **1,371** | **1,410** |

nginx/Windows collapses on large reverse bodies in this harness; treat as same-OS only. H1 TLS **64 KiB** ≈ **1.10×** YARP; **256 KiB** ≈ **1.48×**. H2→H1 64 KiB ≈ **1.17×**. H3→H1 64 KiB ≈ **1.09×** (closed **>1.00×** gate); 256 KiB ≈ **1.09×**.

### Linux â€” heavier reverse GET (64 KiB / 256 KiB)

Median of **3** repeats @ `8ac422ee`. Source: Actions [32631121563](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32631121563) (`compare-bodies`). Warmup 2s / measure 8s.

| Body | Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---|---|---:|---:|---:|---:|---:|---:|
| 64 KiB | HTTP/1 · TLS | HTTP/1 · plain | **8,457** | **8,457** | 🟢 **8,789** | **8,800** | **6,885** | **7,036** |
| 64 KiB | HTTP/2 · TLS | HTTP/1 · plain | 🟢 **5,953** | **5,953** | **4,004** | **4,004** | **5,251** | **5,251** |
| 64 KiB | HTTP/3 · QUIC | HTTP/1 · plain | 🟢 **5,776** | **5,776** | **1,737** | **1,832** | **4,462** | **4,462** |
| 256 KiB | HTTP/1 · TLS | HTTP/1 · plain | 🟢 **2,821** | **2,821** | **2,726** | **2,726** | **2,186** | **2,186** |
| 256 KiB | HTTP/2 · TLS | HTTP/1 · plain | 🟢 **1,543** | **1,543** | **1,010** | **1,014** | **1,407** | **1,407** |
| 256 KiB | HTTP/3 · QUIC | HTTP/1 · plain | 🟢 **1,546** | **1,546** | **440** | **458** | **1,299** | **1,325** |

On this GHA pass TWP÷YARP H1 TLS ≈ **1.23×** (64 KiB) / **1.29×** (256 KiB); H2→H1 ≈ **1.13×** / **1.10×**; H3→H1 ≈ **1.29×** / **1.19×**. TWP÷nginx H1 TLS ≈ **0.96** / **1.03**. Absolute RPS swings by VM; prefer ratios.

### Windows â€” POST 64 KiB request + 64 KiB response

Median of **3** repeats on `windows-latest` @ `21396a4d`. Source: Actions [32608567396](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32608567396) (`compare-post`).

| Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---|---:|---:|---:|---:|---:|---:|
| HTTP/1 Â· TLS | HTTP/1 Â· plain | ðŸŸ¢ **7,594** | **7,819** | **425** | **433** | **5,921** | **5,927** |
| HTTP/2 Â· TLS | HTTP/1 Â· plain | ðŸŸ¢ **6,006** | **6,006** | **423** | **444** | **4,880** | **4,880** |
| HTTP/3 Â· QUIC | HTTP/1 Â· plain | ðŸŸ¢ **2,772** | **2,816** | *Not possible* | *Not possible* | **2,769** | **2,861** |

TWP leads H1 POST (~**1.28Ã—** YARP), H2 POST (~**1.23Ã—** YARP), and H3 POST (~**1.00Ã—** YARP) after the streamed-CL fix (`ab16a871`) and CI dual-listen / origin-release hardening (`21396a4d`).

### Linux â€” POST 64 KiB request + 64 KiB response

Median of **3** repeats @ `21396a4d`. Source: Actions [32608567396](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32608567396) (`compare-post`).

| Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---|---:|---:|---:|---:|---:|---:|
| HTTP/1 Â· TLS | HTTP/1 Â· plain | ðŸŸ¢ **4,415** | **4,415** | **4,277** | **4,292** | **3,406** | **3,406** |
| HTTP/2 Â· TLS | HTTP/1 Â· plain | ðŸŸ¢ **3,350** | **3,350** | **2,071** | **2,147** | **2,776** | **2,776** |
| HTTP/3 Â· QUIC | HTTP/1 Â· plain | ðŸŸ¢ **2,919** | **2,919** | **799** | **802** | **2,771** | **2,771** |

Linux nginx H1/H2/H3 POST completed (nginx.org mainline). TWPÃ·YARP H3 POST â‰ˆ **1.05Ã—**; TWPÃ·nginx H3 â‰ˆ **3.65Ã—**.

### Windows â€” lossy / high-RTT (H2 HOL / H3 loss)

Userspace **5 ms** one-way delay + **1%** TCP connection stall (H1/H2) or UDP datagram drop (H3); **64 KiB** GET. Median of **3** repeats on \windows-latest\ @ `8789d6de`. Source: [32636044385](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32636044385) (`compare-lossy`). H3 on GHA Windows collapses through the userspace UDP shim (sustain ≈ **0**); use the [laptop lab](Performance-Profiling#lossy--high-rtt-h2-hol--h3-packet-loss) for the Windows H3 signal.

| Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---|---:|---:|---:|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | **663** | **663** | **640** | **640** | 🟢 **670** | **670** |
| HTTP/2 · TLS | HTTP/1 · plain | **16** | **18** | **16** | **18** | 🟢 **17** | **17** |
| HTTP/3 Â· QUIC | HTTP/1 Â· plain | *Not measured* (GHA UDP-shim) | *Not measured* | *Not possible* (no QUIC) | *Not possible* | *Not measured* (GHA UDP-shim) | *Not measured* |

H1: TWP÷YARP ≈ **0.99×** (663 / 670). H2 collapses under connection stalls (HOL) — YARP still edges the median (16 vs 17); open under **>1.00×**. Laptop Windows (same shim, \quic-http3\): TWP H3 ≈ **1,572** sustain vs H2 ≈ **14** (~**112×**). Absolute RPS is low because the shim delays every buffer/datagram — the point is the **protocol shape**.

### Linux â€” lossy / high-RTT (H2 HOL / H3 loss)

Median of **3** repeats @ `8789d6de`. Source: [32636044385](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32636044385) (`compare-lossy`; lossy H3 uses `quic-http3`).

| Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---|---:|---:|---:|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | **1,215** | **1,215** | 🟢 **1,219** | **1,219** | **1,212** | **1,212** |
| HTTP/2 · TLS | HTTP/1 · plain | 🟢 **40** | **46** | **40** | **44** | **40** | **44** |
| HTTP/3 · QUIC | HTTP/1 · plain | **258** | **258** | **94** | **94** | 🟢 **351** | **351** |

Same H1 story (nginx 1st, TWP 2nd ahead of YARP ≈ **1.00×**). **H2:** TWP ties YARP median sustain (40). **H3:** YARP edges this pass (351 vs 258); nginx H3 terminate stays far below under the same UDP loss.

### Architecture-sensitive

`compare-arch` isolates slow app readers, origin-early response, H2 duplex, and WebSocket echo. See [TWP vs YARP IO model](Performance-Profiling#twp-vs-yarp-io-model). Laptop 1-rep numbers are on [Performance-Profiling](Performance-Profiling#architecture-sensitive).

Median of **3** repeats on matched 4 vCPU / 16 GiB runners @ `21396a4d`. Source: [32608568872](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32608568872) (`compare-arch`). Slow consumer = 256 KiB GET, 16 KiB read + 8 ms sleep. Early response = 64 KiB POST, origin writes after 8 KiB. Duplex H2 = overlapping 64 KiB POST on H2 TLSâ†”H2 TLS. WebSocket = echo round-trips/sec.

`compare-lossy` (slow **network**) is already published above; it is not a slow **app** reader.

#### Windows

| Scenario | Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---|---|---:|---:|---:|---:|---:|---:|
| Slow consumer (256 KiB GET, throttled client read) | HTTP/1 Â· TLS | HTTP/1 Â· plain | ðŸŸ¢ **256** | **256** | **243** | **243** | ðŸŸ¢ **256** | **256** |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 Â· TLS | HTTP/1 Â· plain | ðŸŸ¢ **256** | **256** | **248** | **248** | ðŸŸ¢ **256** | **256** |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/3 Â· QUIC | HTTP/1 Â· plain | ðŸŸ¢ **272** | **272** | *Not possible* (no QUIC) | *Not possible* | ðŸŸ¢ **272** | **272** |
| Early response (origin writes after first request chunk) | HTTP/1 Â· TLS | HTTP/1 Â· plain | ðŸŸ¢ **11,243** | **12,400** | **584** | **637** | **6,868** | **9,421** |
| Early response (origin writes after first request chunk) | HTTP/2 Â· TLS | HTTP/1 Â· plain | ðŸŸ¢ **8,770** | **9,163** | **0** | **622** | **6,867** | **6,867** |
| Early response (origin writes after first request chunk) | HTTP/3 Â· QUIC | HTTP/1 Â· plain | ðŸŸ¢ **4,041** | **4,256** | *Not possible* (no QUIC) | *Not possible* | **3,656** | **3,955** |
| Duplex (both directions live) | HTTP/2 Â· TLS | HTTP/2 Â· TLS | ðŸŸ¢ **35** | **1,102** | *Not possible* | *Not possible* | **0** | **2,235** |
| Duplex (WebSocket / extended CONNECT) | HTTP/1 Â· TLS | HTTP/1 Â· plain | **65,885** | **65,885** | **38,329** | **38,329** | ðŸŸ¢ **66,934** | **66,934** |

#### Linux

| Scenario | Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---|---|---:|---:|---:|---:|---:|---:|
| Slow consumer (256 KiB GET, throttled client read) | HTTP/1 Â· TLS | HTTP/1 Â· plain | **447** | **447** | ðŸŸ¢ **472** | **472** | **419** | **419** |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 Â· TLS | HTTP/1 Â· plain | **472** | **472** | ðŸŸ¢ **479** | **479** | **472** | **472** |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/3 Â· QUIC | HTTP/1 Â· plain | ðŸŸ¢ **473** | **473** | **120** | **427** | **472** | **472** |
| Early response (origin writes after first request chunk) | HTTP/1 Â· TLS | HTTP/1 Â· plain | ðŸŸ¢ **4,334** | **4,334** | **4,305** | **4,305** | **3,336** | **3,336** |
| Early response (origin writes after first request chunk) | HTTP/2 Â· TLS | HTTP/1 Â· plain | ðŸŸ¢ **3,695** | **3,695** | **0** | **2,128** | **2,402** | **2,555** |
| Early response (origin writes after first request chunk) | HTTP/3 Â· QUIC | HTTP/1 Â· plain | ðŸŸ¢ **2,861** | **2,861** | **0** | **784** | **2,227** | **2,227** |
| Duplex (both directions live) | HTTP/2 Â· TLS | HTTP/2 Â· TLS | ðŸŸ¢ **16** | **210** | *Not possible* | *Not possible* | **13** | **1,884** |
| Duplex (WebSocket / extended CONNECT) | HTTP/1 Â· TLS | HTTP/1 Â· plain | **35,119** | **35,119** | ðŸŸ¢ **39,090** | **39,090** | **32,230** | **32,230** |

Slow consumer is sleep-bound; H1/H2/H3 sit in the same band once bodies stream. H3 slow-consumer sustain **0** on older GHA (fast path dropped CL>16 KiB â€” fixed in `36d21f67` / `cffd9f09`; incomplete-copy pool poison fixed in `21396a4d`). Early-response H3: TWP leads on both OS after duplex upload/`ReceiveResponse` overlap. Duplex H2: TWP holds a higher sustain than YARP on this pass (YARP peaks higher). WebSocket: YARP leads on Windows; Linux nginx leads.

### TLS termination cost (H1 TLS â†’ cleartext origin)

Isolates keep-alive tiny GET vs **new connection per request** (handshake-dominated) vs keep-alive **256 KiB**. Product comparison uses RPS and end-to-end latency; TWP can also capture `ClientTlsTiming` when `TWP_RPS_CAPTURE_TLS=1` (child process) â€” nginx/YARP have no equivalent hook.

#### Windows

Median of **3** repeats on `windows-latest` @ `13059143` (origin Connection strip on H1 terminate lite). Source: Actions [32625349927](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32625349927) (`compare-tls-cost`). Absolute RPS on GHA swings hard; prefer **TWPÃ·YARP**. Gate: **>1.00Ã—** YARP (second when nginx leads).

| Workload | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---:|---:|---:|---:|---:|---:|
| Keep-alive Â· tiny GET | ðŸŸ¢ **23,320** | **23,320** | **11,801** | **12,719** | **21,827** | **21,827** |
| New-connection Â· tiny GET | ðŸŸ¢ **708** | **708** | **235** | **238** | **701** | **701** |
| Keep-alive Â· 256 KiB GET | ðŸŸ¢ **2,840** | **2,891** | **220** | **236** | **2,582** | **2,594** |

#### Linux

Median of **3** repeats @ `13059143`. Source: Actions [32625349927](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32625349927) (`compare-tls-cost`).

| Workload | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---:|---:|---:|---:|---:|---:|
| Keep-alive Â· tiny GET | **24,241** | **24,241** | ðŸŸ¢ **29,229** | **29,229** | **21,058** | **21,058** |
| New-connection Â· tiny GET | **999** | **999** | ðŸŸ¢ **1,023** | **1,024** | **986** | **986** |
| Keep-alive Â· 256 KiB GET | ðŸŸ¢ **2,776** | **2,776** | **2,685** | **2,685** | **2,194** | **2,194** |

**Verdict:** All three workloads **>1.00Ã—** YARP on both OS. nginx still leads Linux keep-alive tiny and Linux NC â€” TWP **second**, YARP third. Root cause for the old NC gap: lite path forwarded client `Connection: close` to the origin (pool miss every request).

## Other measurements

| What | Result |
|---|---|
| HTTPS TTFB vs direct (median, 14 hosts) | Cold **â‰ˆ parity** (âˆ’1 ms); warm **âˆ’25 ms** (proxy faster) |
| HTTP/1 loopback GET (no body intercept) | **~186 Âµs**, **~17.5 KB** allocated / request |
| Basic example footprint (Release, after load) | **~74 MB** working set Â· **~24â€“29 MB** private bytes |

```powershell
dotnet run -c Release --project benchmarks/Titanium.Web.Proxy.Benchmarks -- --filter '*Throughput*'
```

| Benchmark | Setup | Mean | Allocated / op |
|---|---|---:|---:|
| HTTP/1 GET through proxy | Passthrough | **186 Âµs** | **17.5 KB** |
| HTTP/2 multiplexed GETs | 10 concurrent streams | **3.0 ms** / batch | **~14 KB** / request |

## Raising limits on large hosts

There is **no artificial upper clamp** on server defaults. Per-endpoint overrides:

| Knob | Scope | Default | Override |
|---|---|---|---|
| `ProxyServer.MaxCachedConnections` | process, per upstream host | 128 | any â‰¥ 1 |
| `ProxyEndPoint.MaxCachedConnections` | endpoint â†’ pool depth for that EPâ€™s sessions | null (use server) | e.g. `256` on reverse EP |
| `ProxyEndPoint.MaxConcurrentClients` | endpoint admission | null (off) | any â‰¥ 1 |
| `ResourceLimits.MaxConcurrentStreamsPerConnection` | H2 streams | 256 | `ProxyResourceLimits.Create(...)` |
| `TransparentQuicProxyEndPoint.MaxInboundBidirectionalStreams` | H3 | 100 (probe uses 256) | property on EP |
| `ForwardCleartext` | transparent TLS terminate | false | `true` + decrypt |

```csharp
proxy.MaxCachedConnections = 512;
proxy.ResourceLimits = ProxyResourceLimits.Create(
    /* â€¦ */,
    maxConcurrentStreamsPerConnection: 1000,
    maxCachedConnectionsPerHost: 512,
    /* â€¦ */);

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
    a.AllowHttpProtocolTranslation = true; // HTTP/2 client â†’ HTTP/1 origin
    return Task.CompletedTask;
};
```
