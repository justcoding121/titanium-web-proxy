# RpsLoadProbe

Saturation RPS harness for Titanium.Web.Proxy. Measures the **breaking point** (last concurrency that still meets error/latency SLOs) and **peak RPS**.

Published numbers and external control-arm comparisons live only on the wiki [Performance](../../wiki/Performance.md) page (GitHub Actions medians on matched 4 vCPU / 16 GiB Linux+Windows runners). Local cool A/B and laptop tables live on [Performance Profiling](../../wiki/Performance-Profiling.md). This README lists how to run the local harness.

Manual CI: [RPS saturation](../../.github/workflows/rps-saturation.yml) (`workflow_dispatch`, both `ubuntu-latest` and `windows-latest`).

## Same-protocol matrix

```powershell
pwsh tools/RpsLoadProbe/run-rps.ps1 -Mode compare-same
```

| Arm | Topology |
|---|---|
| `reverse-http1` | H1 cleartext → H1 cleartext |
| `reverse-http1-tls` | H1 TLS terminate → H1 cleartext |
| `https-mitm` | Explicit H1 TLS MITM → HTTPS H1 |
| `reverse-http2` | H2 TLS MITM → HTTPS H2 |
| `reverse-http3` | H3 QUIC → H3 origin (dual-listen reverse) |
| `yarp-reverse-http3-to-http3` | Managed reverse peer H3 → H3 |

**Not supported:** `Upgrade: h2c`. Explicit-proxy inbound h2c is not implemented. Outbound and inbound prior-knowledge h2c on transparent reverse are supported. H3 is always QUIC/TLS for TWP.

## Fair terminate compare

```powershell
pwsh tools/RpsLoadProbe/run-rps.ps1 -Mode compare-terminate
```

Client TLS → cleartext origin across H1 TLS terminate, H2→H1, h2c→H1, and H3→H1 arms.

Every `--ramp` arm is **three OS processes**: parent load generator, `--serve-origin` child, `--serve-proxy` child (except **origin-direct** arms, which omit the proxy child). The parent seeds a temp test CA (`TWP_RPS_CERT_DIR`) so HTTPS/QUIC origin and proxy share the same root. Combined `--serve` remains for local debugging only; it is not on the ramp path. Absolute RPS from older combined TLS/QUIC-origin cells is not comparable to split runs — prefer TWP÷peer ratios.

## Saturation control (origin ceiling)

Calibration only — not a product ranking matrix. Tiny keep-alive GET; three blocks:

```powershell
pwsh tools/RpsLoadProbe/run-rps.ps1 -Mode compare-saturation
```

| Block | Arms |
|---|---|
| **A — H1 plain** | `origin-direct`, `origin-direct-bombardier` (if PATH), `bare-reverse-http1`, `nginx-reverse-http1`, `yarp-reverse-http1`, `twp-reverse-http1` |
| **B — H2 TLS→H1** | `nginx-reverse-http2` (if nginx), `yarp-reverse-http2`, `twp-reverse-http2-cleartext` |
| **C — H3→H1** | `nginx-reverse-http3-cleartext` (if nginx + `http_v3_module`), `yarp-reverse-http3-cleartext`, `twp-reverse-http3-cleartext` (skipped when `QuicListener` unsupported) |

**CSV resource columns** (every measure step): `proxy_rss_peak_bytes`, `proxy_cpu_avg_pct` (wiki label: **Memory (RSS)**). Names stay `proxy_*` even on origin-direct arms (those sample the **origin** child PID). Otherwise sample the **proxy** child PID plus its **full descendant tree** (so nginx workers under the serve-proxy → master chain are included). Empty when the PID cannot be sampled. Poll ~200ms during the measure window; peak Working Set / VmRSS sum and average CPU% (of all logical processors).

**Summary:** Block A prints median peak RPS as **% of origin-direct** (and bombardier when present) plus median RSS/CPU at the peak-RPS step. Blocks B/C print peer÷YARP and peer÷nginx (when present) plus RSS/CPU — not % of H1 origin-direct.

Paste CI medians into the wiki [Performance — Saturation control](../../wiki/Performance.md#saturation-control) section. Do not mix bombardier into the publishable TWP÷YARP÷nginx matrices.

## Heavier reverse workloads (bodies / POST / lossy / TLS cost)

Tiny keep-alive GET stresses the per-request path hardest. These modes exercise heavier reverse work:

```powershell
pwsh tools/RpsLoadProbe/run-rps.ps1 -Mode compare-bodies
pwsh tools/RpsLoadProbe/run-rps.ps1 -Mode compare-post
pwsh tools/RpsLoadProbe/run-rps.ps1 -Mode compare-lossy
pwsh tools/RpsLoadProbe/run-rps.ps1 -Mode compare-tls-cost
pwsh tools/RpsLoadProbe/run-rps.ps1 -Mode compare-arch
```

| Mode | Workload |
|---|---|
| `compare-bodies` | GET, keep-alive, **64 KiB** and **256 KiB** responses |
| `compare-post` | POST **64 KiB** request + **64 KiB** response |
| `compare-lossy` | GET **64 KiB**, userspace **5 ms** delay + **1%** stall/drop (H1/H2 TCP stall; H3 UDP datagram drop) |
| `compare-tls-cost` | H1 TLS only: keep-alive tiny, **new-connection** tiny, keep-alive **256 KiB** |
| `compare-arch` | Slow consumer (256 KiB GET, throttled read), early response (POST overlap), H2 TLS↔H2 TLS duplex, WebSocket echo |

Lossy link is a userspace shim (not kernel netem): TCP gets per-buffer delay + occasional whole-connection stalls (HOL for multiplexed H2); UDP gets delay + datagram drops (QUIC). PUT with the same body is the same proxy work as POST; DELETE with no body matches GET.

CLI knobs (also usable on single arms): `--method`, `--response-bytes`, `--request-bytes`, `--no-keepalive`, `--delay-ms`, `--loss-percent`.

## MITM matrix (dual-crypto)

```powershell
pwsh tools/RpsLoadProbe/run-rps.ps1 -Mode compare-mitm
```

Explicit H1 MITM, transparent H2/H3 MITM, H2→H1 / H3→H1 MITM to HTTPS origins, and dual-crypto bridges (H1↔H2↔H3).

## Bridge matrix (cross-version)

```powershell
pwsh tools/RpsLoadProbe/run-rps.ps1 -Mode compare-bridges
```

| Arm | Topology |
|---|---|
| `reverse-http2-cleartext` | H2 TLS → H2→H1 → cleartext H1 |
| `reverse-http2-to-h2c` | H2 TLS → prior-knowledge h2c → cleartext H2 |
| `reverse-h2c-to-h1` | h2c → H2→H1 → cleartext H1 |
| `reverse-h2c-to-h2c` | h2c → cleartext H2 |
| `reverse-h2c-to-h3` | h2c → H2→H3 → QUIC/h3 |
| `reverse-http11-to-http2` | H1 TLS → H1→H2 → HTTPS h2 |
| `reverse-http1-to-http3` | H1 TLS → H1→H3 → QUIC/h3 |
| `reverse-http2-to-http3` | H2 TLS → H2→H3 → QUIC/h3 |
| `reverse-http3-cleartext` | H3 → cleartext H1 |
| `nginx-reverse-http3-cleartext` | Native reverse H3 → cleartext H1 (`http_v3_module`) |
| `reverse-http3-to-http2` | H3 → H3→H2 → HTTPS h2 |

Also: `reverse-h2c` (h2c → HTTPS h2) in `compare-same`.

Other modes: `compare`, `compare-tls`, `compare-http2`, `explicit-pool-sweep`. See `--help`.

Status lines use non-blocking `ProbeLog` (not sync `Console.WriteLine` on workers).
