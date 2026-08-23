# Performance

Titanium targets **low-overhead MITM proxying**: connection pooling, HTTP/2 multiplexing, and buffer reuse. Numbers below are **Release** measurements with [RpsLoadProbe](https://github.com/justcoding121/titanium-web-proxy/tree/develop/tools/RpsLoadProbe) (and BenchmarkDotNet / Basic example where noted). Publishable tables cite **GitHub Actions** medians on matched **4 vCPU / 16 GiB** runners. Absolute RPS still varies by OS kernel, TLS, and MsQuic packaging — compare **within a table**, not across Windows vs Linux.

Control arms: **nginx** (native C reverse-proxy ceiling; Linux is authoritative) and **YARP** (`Yarp.ReverseProxy`, managed .NET reverse proxy). Neither can MITM (no CONNECT / forged certs). FiddlerCore is not compared (commercial debugger license; not a throughput peer).

For pooling knobs and certificate first-visit tuning, see [Performance and pooling](Home#performance-and-pooling). For the local cool A/B lab, laptop tables, and the techniques used to find each hotspot, see [Performance Profiling](Performance-Profiling).

## Contents

- [Measurement environment](#measurement-environment)
    - [Windows (GitHub-hosted `windows-latest`)](#windows-github-hosted-windows-latest)
    - [Linux (GitHub-hosted `ubuntu-latest`)](#linux-github-hosted-ubuntu-latest)
- [Windows — Titanium vs nginx vs YARP](#windows--titanium-vs-nginx-vs-yarp)
- [Linux — Titanium vs nginx vs YARP](#linux--titanium-vs-nginx-vs-yarp)
    - [Tiny JSON reverse is nginx’s best case on Linux](#tiny-json-reverse-is-nginxs-best-case-on-linux)
    - [Why isn’t HTTP/3 > HTTP/2 > HTTP/1 in raw RPS?](#why-isnt-http3--http2--http1-in-raw-rps)
- [Heavier reverse workloads](#heavier-reverse-workloads)
    - [Windows — heavier reverse GET (64 KiB / 256 KiB)](#windows--heavier-reverse-get-64-kib--256-kib)
    - [Linux — heavier reverse GET (64 KiB / 256 KiB)](#linux--heavier-reverse-get-64-kib--256-kib)
    - [Windows — POST 64 KiB request + 64 KiB response](#windows--post-64-kib-request--64-kib-response)
    - [Linux — POST 64 KiB request + 64 KiB response](#linux--post-64-kib-request--64-kib-response)
    - [Windows — lossy / high-RTT (H2 HOL / H3 loss)](#windows--lossy--high-rtt-h2-hol--h3-loss)
    - [Linux — lossy / high-RTT (H2 HOL / H3 loss)](#linux--lossy--high-rtt-h2-hol--h3-loss)
    - [Architecture-sensitive](#architecture-sensitive)
    - [TLS termination cost (H1 TLS → cleartext origin)](#tls-termination-cost-h1-tls--cleartext-origin)
- [Other measurements](#other-measurements)
- [Raising limits on large hosts](#raising-limits-on-large-hosts)

## Measurement environment

Both OS use the standard public-repo GitHub-hosted runner class (**4 vCPU / 16 GiB / 14 GB SSD**). Same harness knobs (`workflow_dispatch` [RPS saturation](https://github.com/justcoding121/titanium-web-proxy/actions/workflows/rps-saturation.yml): warmup 2s / measure 8s; concurrency 8, 16, 32, 64; median of 3 repeats). Prefer **TWP÷YARP** / **TWP÷nginx** ratios over absolute RPS.

Laptop High-perf / cool-paired Windows numbers live on [Performance Profiling — Local Windows lab](Performance-Profiling#local-windows-lab-developer-laptop). Do not mix those absolutes into the tables below.

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

- **Mode**: **Reverse** = transparent fixed-forward (may TLS-terminate to a cleartext origin, or re-encrypt to a configured HTTPS/QUIC origin). **MITM** = both legs are visible in the clear inside TWP — either by decrypting client TLS/QUIC (forged cert / CONNECT) **or** by accepting an already-cleartext client (explicit HTTP proxy / inspectable transparent reverse) while still speaking plain or TLS to the origin. nginx and YARP cannot do MITM. **HTTP/3 has no cleartext client** (QUIC always encrypted).
- **Sustainable** = last concurrency that still met error/latency SLOs. **Peak** = highest RPS in that ramp.
- 🟢 = highest **sustainable** RPS among products that have a number on that row. Omitted when only TWP can run the path (no fair multi-product comparison).
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

## Windows — Titanium vs nginx vs YARP

Client / origin: HTTP version and whether TLS is used (`plain` = cleartext, `TLS` = encrypted, `QUIC` = HTTP/3).

Median of **3 repeats** on `windows-latest` (4 vCPU / 16 GiB). Same @ `13059143` ([32625532123](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32625532123)); bridges @ `11e32f1c` ([32621830861](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32621830861)); MITM @ `1b5ca9f9` ([32588707712](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32588707712)). Warmup 2s / measure 8s; concurrency 8, 16, 32, 64. Laptop High-perf / cool-paired numbers stay on the [local lab](Performance-Profiling#local-windows-lab-developer-laptop). Tip remasure after session-lite H2/H3 gate (`62e5efcd`) is in flight for bridges.

**Load generators:** Reverse inbound H3 arms use **`dotnet-httpclient`** (`http_version=3.0`, `RequestVersionExact`) after dual-listen reverse H3. nginx/Windows is same-OS only (no QUIC).

| Mode | Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---|---|---:|---:|---:|---:|---:|---:|
| Reverse | HTTP/1 · plain | HTTP/1 · plain | 🟢 **28,048** | **28,048** | **19,492** | **19,695** | **27,096** | **27,096** |
| Reverse | HTTP/1 · plain | HTTP/1 · TLS | 🟢 **25,178** | **25,178** | *Not possible* | *Not possible* | **24,534** | **24,534** |
| Reverse | HTTP/1 · TLS | HTTP/1 · plain | 🟢 **24,853** | **24,853** | **12,892** | **13,145** | **23,664** | **23,664** |
| Reverse | HTTP/1 · TLS | HTTP/2 · TLS | 🟢 **24,028** | **24,028** | *Not possible* | *Not possible* | **23,189** | **23,189** |
| Reverse | HTTP/1 · TLS | HTTP/3 · QUIC | **13,331** | **13,331** | *Not possible* (no QUIC) | *Not possible* (no QUIC) | 🟢 **14,016** | **14,016** |
| Reverse | HTTP/2 · plain | HTTP/1 · plain | **31,633** | **31,633** | *Not possible* | *Not possible* | 🟢 **32,548** | **32,548** |
| Reverse | HTTP/2 · plain | HTTP/2 · plain | 🟢 **89,558** | **89,558** | *Not possible* | *Not possible* | **65,850** | **65,850** |
| Reverse | HTTP/2 · plain | HTTP/2 · TLS | 🟢 **78,997** | **78,997** | *Not possible* | *Not possible* | **60,530** | **60,530** |
| Reverse | HTTP/2 · plain | HTTP/3 · QUIC | 🟢 **32,024** | **32,024** | *Not possible* (no QUIC) | *Not possible* (no QUIC) | **29,939** | **29,939** |
| Reverse | HTTP/2 · TLS | HTTP/1 · plain | **29,741** | **29,741** | **15,610** | **15,610** | 🟢 **29,753** | **29,753** |
| Reverse | HTTP/2 · TLS | HTTP/2 · plain | 🟢 **83,823** | **83,823** | *Not possible* | *Not possible* | **56,331** | **56,331** |
| Reverse | HTTP/2 · TLS | HTTP/3 · QUIC | 🟢 **31,038** | **31,038** | *Not possible* (no QUIC) | *Not possible* (no QUIC) | **27,242** | **27,242** |
| Reverse | HTTP/3 · QUIC | HTTP/1 · plain | **13,015** | **13,028** | *Not possible* (no QUIC) | *Not possible* (no QUIC) | 🟢 **14,851** | **15,187** |
| Reverse | HTTP/3 · QUIC | HTTP/2 · TLS | 🟢 **23,936** | **23,936** | *Not possible* (no QUIC) | *Not possible* (no QUIC) | **20,449** | **20,449** |
| Reverse | HTTP/3 · QUIC | HTTP/3 · QUIC | 🟢 **21,570** | **21,570** | *Not possible* (no QUIC) | *Not possible* (no QUIC) | **17,028** | **17,028** |
| MITM | HTTP/1 · plain | HTTP/1 · plain | **24,782** | **24,782** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/1 · plain | HTTP/1 · TLS | **25,503** | **25,503** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/1 · TLS | HTTP/1 · plain | **24,089** | **24,089** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/1 · TLS | HTTP/2 · TLS | **30,918** | **30,918** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/1 · TLS | HTTP/3 · QUIC | **16,345** | **16,345** | *Not possible* (no QUIC) | *Not possible* (no QUIC) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 · plain | HTTP/1 · plain | **37,432** | **37,432** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 · plain | HTTP/2 · plain | **92,679** | **92,679** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 · plain | HTTP/2 · TLS | **80,616** | **80,616** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 · plain | HTTP/3 · QUIC | **34,415** | **34,415** | *Not possible* (no QUIC) | *Not possible* (no QUIC) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 · TLS | HTTP/1 · plain | **36,313** | **36,313** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 · TLS | HTTP/2 · plain | **86,878** | **86,878** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 · TLS | HTTP/3 · QUIC | **33,304** | **33,304** | *Not possible* (no QUIC) | *Not possible* (no QUIC) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/3 · QUIC | HTTP/1 · plain | **17,578** | **17,578** | *Not possible* (no QUIC) | *Not possible* (no QUIC) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/3 · QUIC | HTTP/2 · TLS | **26,686** | **26,686** | *Not possible* (no QUIC) | *Not possible* (no QUIC) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/3 · QUIC | HTTP/3 · QUIC | **23,095** | **23,095** | *Not possible* (no QUIC) | *Not possible* (no QUIC) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/1 · plain (CONNECT) | HTTP/1 · TLS | **19,990** | **19,990** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/1 · TLS | HTTP/1 · TLS | **22,308** | **22,308** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 · TLS | HTTP/2 · TLS | **79,620** | **79,620** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 · TLS | HTTP/1 · TLS | **28,034** | **28,034** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/3 · QUIC | HTTP/1 · TLS | **15,082** | **15,082** | *Not possible* (no QUIC) | *Not possible* (no QUIC) | *Not possible* (no MITM) | *Not possible* (no MITM) |

TWP÷YARP H1 plain ≈ **1.04×** (28,048 / 27,096); H1 TLS terminate ≈ **1.05×** (24,853 / 23,664). Open Win gaps vs YARP (gate **>1.00×**): H3→H1 ≈ **0.88×**, H1→H3 ≈ **0.95×**, h2c→H1 ≈ **0.97×**, H2 TLS→H1 ≈ **1.00×**. Cool laptop H3→H1 paired mean ≈ **1.09×** — CI remasure on tip pending. H3→H2 ≈ **1.17×**, H3→H3 ≈ **1.27×**. Prefer ratios over absolute RPS on GHA VMs. MITM publishes the same **15** Client×Origin pairs as Reverse (inspectable/decrypt), then dual-crypto extras (CONNECT, TLS↔TLS). nginx/YARP cannot MITM.

## Linux — Titanium vs nginx vs YARP

Median of **3 repeats** on `ubuntu-latest` (4 vCPU / 16 GiB). Same @ `13059143` ([32625532123](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32625532123)); bridges @ `11e32f1c` ([32621830861](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32621830861)); MITM @ `1b5ca9f9` ([32588707712](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32588707712)). Warmup 2s / measure 8s; concurrency 8, 16, 32, 64. **Linux nginx is the authoritative nginx baseline.** The RPS workflow installs nginx.org mainline (`http_v3_module`) and `libmsquic` (`QuicListener.IsSupported=true` on `ubuntu-latest`).

TWP÷nginx H1 plain reverse ≈ **0.84** (31,945 / 38,018); TWP÷YARP H1 plain ≈ **1.15×** (31,945 / 27,686). Prefer ratios over absolute RPS on GHA VMs.

| Mode | Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---|---|---:|---:|---:|---:|---:|---:|
| Reverse | HTTP/1 · plain | HTTP/1 · plain | **31,945** | **31,945** | 🟢 **38,018** | **38,018** | **27,686** | **27,686** |
| Reverse | HTTP/1 · plain | HTTP/1 · TLS | 🟢 **26,342** | **26,342** | *Not possible* | *Not possible* | **23,454** | **23,454** |
| Reverse | HTTP/1 · TLS | HTTP/1 · plain | **23,229** | **23,229** | 🟢 **27,532** | **27,532** | **20,313** | **20,313** |
| Reverse | HTTP/1 · TLS | HTTP/2 · TLS | 🟢 **25,235** | **25,235** | *Not possible* | *Not possible* | **23,522** | **23,522** |
| Reverse | HTTP/1 · TLS | HTTP/3 · QUIC | 🟢 **17,573** | **17,573** | *Not possible* (no H3 origin) | *Not possible* (no H3 origin) | **17,323** | **17,323** |
| Reverse | HTTP/2 · plain | HTTP/1 · plain | 🟢 **38,725** | **38,725** | *Not possible* | *Not possible* | **35,831** | **35,831** |
| Reverse | HTTP/2 · plain | HTTP/2 · plain | 🟢 **68,817** | **68,817** | *Not possible* | *Not possible* | **50,396** | **50,396** |
| Reverse | HTTP/2 · plain | HTTP/2 · TLS | 🟢 **52,884** | **52,884** | *Not possible* | *Not possible* | **40,346** | **40,346** |
| Reverse | HTTP/2 · plain | HTTP/3 · QUIC | 🟢 **30,145** | **30,145** | *Not possible* (no H3 origin) | *Not possible* (no H3 origin) | **27,845** | **27,845** |
| Reverse | HTTP/2 · TLS | HTTP/1 · plain | 🟢 **36,005** | **36,005** | **13,176** | **18,529** | **30,429** | **30,429** |
| Reverse | HTTP/2 · TLS | HTTP/2 · plain | 🟢 **61,970** | **61,970** | *Not possible* | *Not possible* | **40,616** | **40,616** |
| Reverse | HTTP/2 · TLS | HTTP/3 · QUIC | 🟢 **27,896** | **27,896** | *Not possible* (no H3 origin) | *Not possible* (no H3 origin) | **24,168** | **24,168** |
| Reverse | HTTP/3 · QUIC | HTTP/1 · plain | 🟢 **20,913** | **20,913** | **12,727** | **15,409** | **19,158** | **19,158** |
| Reverse | HTTP/3 · QUIC | HTTP/2 · TLS | 🟢 **27,146** | **27,146** | *Not possible* (no H3→H2) | *Not possible* (no H3→H2) | **22,493** | **22,493** |
| Reverse | HTTP/3 · QUIC | HTTP/3 · QUIC | 🟢 **19,155** | **19,155** | *Not possible* (no H3 origin) | *Not possible* (no H3 origin) | **16,761** | **16,761** |
| MITM | HTTP/1 · plain | HTTP/1 · plain | **29,982** | **29,982** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/1 · plain | HTTP/1 · TLS | **27,856** | **27,856** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/1 · TLS | HTTP/1 · plain | **23,869** | **23,869** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/1 · TLS | HTTP/2 · TLS | **29,829** | **29,829** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/1 · TLS | HTTP/3 · QUIC | **20,195** | **20,195** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 · plain | HTTP/1 · plain | **40,923** | **40,923** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 · plain | HTTP/2 · plain | **73,146** | **73,146** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 · plain | HTTP/2 · TLS | **60,131** | **60,131** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 · plain | HTTP/3 · QUIC | **30,523** | **30,523** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 · TLS | HTTP/1 · plain | **37,691** | **37,691** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 · TLS | HTTP/2 · plain | **64,854** | **64,854** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 · TLS | HTTP/3 · QUIC | **29,689** | **29,689** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/3 · QUIC | HTTP/1 · plain | **22,443** | **22,443** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/3 · QUIC | HTTP/2 · TLS | **28,182** | **28,182** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/3 · QUIC | HTTP/3 · QUIC | **21,668** | **21,668** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/1 · plain (CONNECT) | HTTP/1 · TLS | **20,981** | **20,981** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/1 · TLS | HTTP/1 · TLS | **22,112** | **22,112** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 · TLS | HTTP/2 · TLS | **57,725** | **57,725** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/2 · TLS | HTTP/1 · TLS | **31,697** | **31,697** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |
| MITM | HTTP/3 · QUIC | HTTP/1 · TLS | **21,573** | **21,573** | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) | *Not possible* (no MITM) |

On this GHA shape, TWP H1 plain ÷ nginx H1 plain ≈ **0.84** (31,945 / 38,018). H1 TLS terminate ≈ **0.84** (23,229 / 27,532). TWP÷YARP H1 plain ≈ **1.15×**; H1 TLS terminate ≈ **1.14×**. Absolute RPS swings by VM; prefer the **ratio** and **median across repeats**. MITM publishes the same **15** Client×Origin pairs as Reverse (inspectable/decrypt), then dual-crypto extras (CONNECT, TLS↔TLS). nginx/YARP cannot MITM.

**nginx HTTP/3:** inbound QUIC terminate → cleartext H1 (`nginx-reverse-http3-cleartext`) @ `11e32f1c` bridges: sustain **12,727** / peak **15,409**. TWP/YARP H3→H1 on this row are from the same bridges pass. nginx still cannot speak HTTP/3 to an origin (no H3 upstream in this conf).

**YARP HTTP/3 (this matrix):** TWP leads H3→H1 ≈ **1.09×** (20,913 / 19,158), H3→H2 ≈ **1.21×** (27,146 / 22,493), H3→H3 ≈ **1.14×** (19,155 / 16,761). H1→H2 ≈ **1.07×** (25,235 / 23,522). H1→H3 ≈ **1.01×** (17,573 / 17,323). h2c→H3 ≈ **1.08×**.

**Windows vs Linux:** both CI envs are **4 vCPU / 16 GiB**, but do **not** compare absolute RPS across OS. Linux nginx still leads H1 plain/TLS terminate (TWP second, ahead of YARP). Windows reverse tiny-GET still has a few TWP÷YARP cells ≤1.00× on the `11e32f1c` bridges pass (H3→H1 / H1→H3 / h2c→H1); cool laptop pairs already lead those arms — tip remasure @ `62e5efcd` in flight. Cool laptop notes remain on [Performance Profiling](Performance-Profiling#local-windows-lab-developer-laptop).


### Tiny JSON reverse is nginx’s best case on Linux

The tables above use **~64 B keep-alive GET** on loopback. On Linux H1 reverse, nginx leads; YARP sits near TWP. Heavier bodies, POSTs, TLS handshake cost, and lossy/HOL workloads (below) change the picture. MITM rows remain TWP-only. nginx HTTP/3 is inbound-terminate only (see note above).

### Why isn’t HTTP/3 > HTTP/2 > HTTP/1 in raw RPS?

For **tiny JSON responses** (~64 B) on loopback, that ordering is **not** expected: topology (TLS hop count, terminate vs MITM) dominates; HTTP/2 and HTTP/3 help multiplexing, not single-origin tiny-GET RPS. See the **lossy** tables below for a workload where protocol design matters.

## Heavier reverse workloads

Separate from the tiny-GET matrix. Same measurement environments. Modes: `compare-bodies`, `compare-post`, `compare-lossy`, `compare-tls-cost`, `compare-arch` in [RpsLoadProbe](https://github.com/justcoding121/titanium-web-proxy/tree/develop/tools/RpsLoadProbe). **PUT with the same body is the same proxy work as POST; DELETE with no body matches GET** — only POST is published. Bodies/POST/lossy stay **half-duplex**. `compare-arch` is the slow-consumer / early-response / duplex set. Laptop numbers are on [Performance-Profiling](Performance-Profiling#architecture-sensitive); CI medians go in the tables below.

Lossy link = **userspace** shim (not kernel `netem`): TCP gets per-buffer delay + occasional whole-connection stalls (honest HOL for multiplexed H2); UDP gets per-datagram delay + drops (QUIC). `compare-lossy` publishes H1/H2/H3; H3 is where the protocol design is supposed to matter.

### Windows — heavier reverse GET (64 KiB / 256 KiB)

Median of **3** repeats on `windows-latest` @ `106e73b9`. Source: Actions [32614286032](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32614286032) (`compare-bodies`). Warmup 2s / measure 8s.

| Body | Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---|---|---:|---:|---:|---:|---:|---:|
| 64 KiB | HTTP/1 · TLS | HTTP/1 · plain | 🟢 **9,069** | **9,147** | **867** | **956** | **8,047** | **8,053** |
| 64 KiB | HTTP/2 · TLS | HTTP/1 · plain | 🟢 **8,328** | **8,328** | **753** | **794** | **6,653** | **6,891** |
| 64 KiB | HTTP/3 · QUIC | HTTP/1 · plain | **3,720** | **3,933** | *Not possible* (no QUIC) | *Not possible* (no QUIC) | 🟢 **3,836** | **3,836** |
| 256 KiB | HTTP/1 · TLS | HTTP/1 · plain | 🟢 **2,617** | **2,946** | **236** | **250** | **2,347** | **2,501** |
| 256 KiB | HTTP/2 · TLS | HTTP/1 · plain | 🟢 **2,497** | **2,559** | **174** | **175** | **1,738** | **1,827** |
| 256 KiB | HTTP/3 · QUIC | HTTP/1 · plain | 🟢 **1,084** | **1,163** | *Not possible* (no QUIC) | *Not possible* (no QUIC) | **1,073** | **1,078** |

nginx/Windows collapses on large reverse bodies in this harness; treat as same-OS only. H1 TLS **256 KiB** after 64 KiB `CopyBytesToStream` grain (`106e73b9`): TWP÷YARP ≈ **1.12×** (was ~0.85×). H3→H1 64 KiB ≈ **0.97×** (under **>1.00×** gate — cool laptop historically ~1.13×; `compare-bodies` remasure on tip in flight).

### Linux — heavier reverse GET (64 KiB / 256 KiB)

Median of **3** repeats @ `106e73b9`. Source: Actions [32614286032](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32614286032) (`compare-bodies`). Warmup 2s / measure 8s.

| Body | Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---|---|---:|---:|---:|---:|---:|---:|
| 64 KiB | HTTP/1 · TLS | HTTP/1 · plain | **7,314** | **7,565** | 🟢 **7,601** | **7,601** | **5,914** | **6,018** |
| 64 KiB | HTTP/2 · TLS | HTTP/1 · plain | 🟢 **5,542** | **5,542** | **3,340** | **3,340** | **4,605** | **4,605** |
| 64 KiB | HTTP/3 · QUIC | HTTP/1 · plain | 🟢 **4,751** | **4,751** | **1,408** | **1,471** | **3,848** | **3,848** |
| 256 KiB | HTTP/1 · TLS | HTTP/1 · plain | **2,346** | **2,346** | 🟢 **2,357** | **2,357** | **1,856** | **1,889** |
| 256 KiB | HTTP/2 · TLS | HTTP/1 · plain | 🟢 **1,387** | **1,387** | **852** | **860** | **1,240** | **1,240** |
| 256 KiB | HTTP/3 · QUIC | HTTP/1 · plain | 🟢 **1,266** | **1,270** | **356** | **371** | **1,111** | **1,126** |

On this GHA pass TWP÷YARP H1 TLS ≈ **1.24×** (64 KiB) / **1.26×** (256 KiB); H2→H1 ≈ **1.20×** / **1.12×**; H3→H1 ≈ **1.23×** / **1.14×**. TWP÷nginx H1 TLS ≈ **0.96** / **1.00**. Absolute RPS swings by VM; prefer ratios.

### Windows — POST 64 KiB request + 64 KiB response

Median of **3** repeats on `windows-latest` @ `21396a4d`. Source: Actions [32608567396](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32608567396) (`compare-post`).

| Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---|---:|---:|---:|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | 🟢 **7,594** | **7,819** | **425** | **433** | **5,921** | **5,927** |
| HTTP/2 · TLS | HTTP/1 · plain | 🟢 **6,006** | **6,006** | **423** | **444** | **4,880** | **4,880** |
| HTTP/3 · QUIC | HTTP/1 · plain | 🟢 **2,772** | **2,816** | *Not possible* | *Not possible* | **2,769** | **2,861** |

TWP leads H1 POST (~**1.28×** YARP), H2 POST (~**1.23×** YARP), and H3 POST (~**1.00×** YARP) after the streamed-CL fix (`ab16a871`) and CI dual-listen / origin-release hardening (`21396a4d`).

### Linux — POST 64 KiB request + 64 KiB response

Median of **3** repeats @ `21396a4d`. Source: Actions [32608567396](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32608567396) (`compare-post`).

| Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---|---:|---:|---:|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | 🟢 **4,415** | **4,415** | **4,277** | **4,292** | **3,406** | **3,406** |
| HTTP/2 · TLS | HTTP/1 · plain | 🟢 **3,350** | **3,350** | **2,071** | **2,147** | **2,776** | **2,776** |
| HTTP/3 · QUIC | HTTP/1 · plain | 🟢 **2,919** | **2,919** | **799** | **802** | **2,771** | **2,771** |

Linux nginx H1/H2/H3 POST completed (nginx.org mainline). TWP÷YARP H3 POST ≈ **1.05×**; TWP÷nginx H3 ≈ **3.65×**.

### Windows — lossy / high-RTT (H2 HOL / H3 loss)

Userspace **5 ms** one-way delay + **1%** TCP connection stall (H1/H2) or UDP datagram drop (H3); **64 KiB** GET. Median of **3** repeats on `windows-latest` @ `77373751`. Source: [32627967699](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32627967699) (`compare-lossy`). H3 on GHA Windows collapses through the userspace UDP shim (sustain ≈ **0**); use the [laptop lab](Performance-Profiling#lossy--high-rtt-h2-hol--h3-packet-loss) for the Windows H3 signal.

| Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---|---:|---:|---:|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | 🟢 **670** | **670** | **650** | **650** | **664** | **664** |
| HTTP/2 · TLS | HTTP/1 · plain | **16** | **18** | **16** | **18** | 🟢 **18** | **18** |
| HTTP/3 · QUIC | HTTP/1 · plain | *Not measured* (GHA UDP-shim) | *Not measured* | *Not possible* (no QUIC) | *Not possible* | *Not measured* (GHA UDP-shim) | *Not measured* |

H1: TWP÷YARP ≈ **1.01×** (was ~1.00× / ~0.86× before coalesce). H2 collapses under connection stalls (HOL) — YARP still edges the median (16 vs 18); open under **>1.00×**. Laptop Windows (same shim, `quic-http3`): TWP H3 ≈ **1,572** sustain vs H2 ≈ **14** (~**112×**). Absolute RPS is low because the shim delays every buffer/datagram — the point is the **protocol shape**.

### Linux — lossy / high-RTT (H2 HOL / H3 loss)

Median of **3** repeats @ `77373751`. Source: [32627967699](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32627967699) (`compare-lossy`; lossy H3 uses `quic-http3`).

| Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---|---:|---:|---:|---:|---:|---:|
| HTTP/1 · TLS | HTTP/1 · plain | **1,205** | **1,205** | 🟢 **1,213** | **1,213** | **1,204** | **1,204** |
| HTTP/2 · TLS | HTTP/1 · plain | 🟢 **44** | **46** | **40** | **40** | **40** | **44** |
| HTTP/3 · QUIC | HTTP/1 · plain | 🟢 **321** | **321** | **93** | **93** | **309** | **309** |

Same H1 story (nginx 1st, TWP 2nd ahead of YARP ≈ **1.00×**). **H2:** TWP leads ≈ **1.10×** YARP. **H3 is where the protocol design shows**: TWP H3 sustain ≈ **7×** H2 on this runner; nginx H3 terminate stays far below under the same UDP loss.

### Architecture-sensitive

`compare-arch` isolates slow app readers, origin-early response, H2 duplex, and WebSocket echo. See [TWP vs YARP IO model](Performance-Profiling#twp-vs-yarp-io-model). Laptop 1-rep numbers are on [Performance-Profiling](Performance-Profiling#architecture-sensitive).

Median of **3** repeats on matched 4 vCPU / 16 GiB runners @ `21396a4d`. Source: [32608568872](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32608568872) (`compare-arch`). Slow consumer = 256 KiB GET, 16 KiB read + 8 ms sleep. Early response = 64 KiB POST, origin writes after 8 KiB. Duplex H2 = overlapping 64 KiB POST on H2 TLS↔H2 TLS. WebSocket = echo round-trips/sec.

`compare-lossy` (slow **network**) is already published above; it is not a slow **app** reader.

#### Windows

| Scenario | Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---|---|---:|---:|---:|---:|---:|---:|
| Slow consumer (256 KiB GET, throttled client read) | HTTP/1 · TLS | HTTP/1 · plain | 🟢 **256** | **256** | **243** | **243** | 🟢 **256** | **256** |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · TLS | HTTP/1 · plain | 🟢 **256** | **256** | **248** | **248** | 🟢 **256** | **256** |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/3 · QUIC | HTTP/1 · plain | 🟢 **272** | **272** | *Not possible* (no QUIC) | *Not possible* | 🟢 **272** | **272** |
| Early response (origin writes after first request chunk) | HTTP/1 · TLS | HTTP/1 · plain | 🟢 **11,243** | **12,400** | **584** | **637** | **6,868** | **9,421** |
| Early response (origin writes after first request chunk) | HTTP/2 · TLS | HTTP/1 · plain | 🟢 **8,770** | **9,163** | **0** | **622** | **6,867** | **6,867** |
| Early response (origin writes after first request chunk) | HTTP/3 · QUIC | HTTP/1 · plain | 🟢 **4,041** | **4,256** | *Not possible* (no QUIC) | *Not possible* | **3,656** | **3,955** |
| Duplex (both directions live) | HTTP/2 · TLS | HTTP/2 · TLS | 🟢 **35** | **1,102** | *Not possible* | *Not possible* | **0** | **2,235** |
| Duplex (WebSocket / extended CONNECT) | HTTP/1 · TLS | HTTP/1 · plain | **65,885** | **65,885** | **38,329** | **38,329** | 🟢 **66,934** | **66,934** |

#### Linux

| Scenario | Client | Origin | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---|---|---:|---:|---:|---:|---:|---:|
| Slow consumer (256 KiB GET, throttled client read) | HTTP/1 · TLS | HTTP/1 · plain | **447** | **447** | 🟢 **472** | **472** | **419** | **419** |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/2 · TLS | HTTP/1 · plain | **472** | **472** | 🟢 **479** | **479** | **472** | **472** |
| Slow consumer (256 KiB GET, throttled client read) | HTTP/3 · QUIC | HTTP/1 · plain | 🟢 **473** | **473** | **120** | **427** | **472** | **472** |
| Early response (origin writes after first request chunk) | HTTP/1 · TLS | HTTP/1 · plain | 🟢 **4,334** | **4,334** | **4,305** | **4,305** | **3,336** | **3,336** |
| Early response (origin writes after first request chunk) | HTTP/2 · TLS | HTTP/1 · plain | 🟢 **3,695** | **3,695** | **0** | **2,128** | **2,402** | **2,555** |
| Early response (origin writes after first request chunk) | HTTP/3 · QUIC | HTTP/1 · plain | 🟢 **2,861** | **2,861** | **0** | **784** | **2,227** | **2,227** |
| Duplex (both directions live) | HTTP/2 · TLS | HTTP/2 · TLS | 🟢 **16** | **210** | *Not possible* | *Not possible* | **13** | **1,884** |
| Duplex (WebSocket / extended CONNECT) | HTTP/1 · TLS | HTTP/1 · plain | **35,119** | **35,119** | 🟢 **39,090** | **39,090** | **32,230** | **32,230** |

Slow consumer is sleep-bound; H1/H2/H3 sit in the same band once bodies stream. H3 slow-consumer sustain **0** on older GHA (fast path dropped CL>16 KiB — fixed in `36d21f67` / `cffd9f09`; incomplete-copy pool poison fixed in `21396a4d`). Early-response H3: TWP leads on both OS after duplex upload/`ReceiveResponse` overlap. Duplex H2: TWP holds a higher sustain than YARP on this pass (YARP peaks higher). WebSocket: YARP leads on Windows; Linux nginx leads.

### TLS termination cost (H1 TLS → cleartext origin)

Isolates keep-alive tiny GET vs **new connection per request** (handshake-dominated) vs keep-alive **256 KiB**. Product comparison uses RPS and end-to-end latency; TWP can also capture `ClientTlsTiming` when `TWP_RPS_CAPTURE_TLS=1` (child process) — nginx/YARP have no equivalent hook.

#### Windows

Median of **3** repeats on `windows-latest` @ `13059143` (origin Connection strip on H1 terminate lite). Source: Actions [32625349927](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32625349927) (`compare-tls-cost`). Absolute RPS on GHA swings hard; prefer **TWP÷YARP**. Gate: **>1.00×** YARP (second when nginx leads).

| Workload | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---:|---:|---:|---:|---:|---:|
| Keep-alive · tiny GET | 🟢 **23,320** | **23,320** | **11,801** | **12,719** | **21,827** | **21,827** |
| New-connection · tiny GET | 🟢 **708** | **708** | **235** | **238** | **701** | **701** |
| Keep-alive · 256 KiB GET | 🟢 **2,840** | **2,891** | **220** | **236** | **2,582** | **2,594** |

#### Linux

Median of **3** repeats @ `13059143`. Source: Actions [32625349927](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32625349927) (`compare-tls-cost`).

| Workload | TWP sustain | TWP peak | nginx sustain | nginx peak | YARP sustain | YARP peak |
|---|---:|---:|---:|---:|---:|---:|
| Keep-alive · tiny GET | **24,241** | **24,241** | 🟢 **29,229** | **29,229** | **21,058** | **21,058** |
| New-connection · tiny GET | **999** | **999** | 🟢 **1,023** | **1,024** | **986** | **986** |
| Keep-alive · 256 KiB GET | 🟢 **2,776** | **2,776** | **2,685** | **2,685** | **2,194** | **2,194** |

**Verdict:** All three workloads **>1.00×** YARP on both OS. nginx still leads Linux keep-alive tiny and Linux NC — TWP **second**, YARP third. Root cause for the old NC gap: lite path forwarded client `Connection: close` to the origin (pool miss every request).

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
