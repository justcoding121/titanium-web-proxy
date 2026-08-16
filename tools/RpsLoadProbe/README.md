# RpsLoadProbe

Saturation RPS harness for Titanium.Web.Proxy. Measures the **breaking point** (last concurrency that still meets error/latency SLOs) and **peak RPS**, with optional same-machine **nginx** control arms.

## Same-protocol matrix

```powershell
pwsh tools/RpsLoadProbe/run-rps.ps1 -Mode compare-same
```

| Arm | Topology | nginx |
|---|---|---|
| `reverse-http1` | H1 cleartext → H1 cleartext | yes |
| `reverse-http1-tls` | H1 TLS terminate → H1 cleartext | yes |
| `https-mitm` | Explicit H1 TLS MITM → HTTPS H1 | — |
| `reverse-http2` | H2 TLS MITM → HTTPS H2 | — |
| `nginx-reverse-http2` | Client H2 TLS → cleartext H1 (terminate) | yes (not MITM) |
| `reverse-http3` | H3 QUIC MITM → H3 | — (no QUIC on nginx/Windows) |

**Not supported:** `Upgrade: h2c`. Explicit-proxy inbound h2c is not implemented. Outbound and inbound prior-knowledge h2c on transparent reverse are supported. H3 is always QUIC/TLS.

## Fair terminate compare

```powershell
pwsh tools/RpsLoadProbe/run-rps.ps1 -Mode compare-terminate
```

Client TLS → cleartext origin: TWP/nginx H1 TLS, TWP H2→H1, TWP h2c→H1, nginx H2, TWP H3→H1.

Cleartext-origin terminate arms run **origin and proxy in separate processes** so TWP is not sharing a ThreadPool/GC with Kestrel the way a separate nginx process does not.

## Heavier reverse workloads (bodies / POST / lossy / TLS cost)

Tiny keep-alive GET is nginx’s best case and TWP’s worst. These modes exercise heavier reverse paths:

```powershell
pwsh tools/RpsLoadProbe/run-rps.ps1 -Mode compare-bodies
pwsh tools/RpsLoadProbe/run-rps.ps1 -Mode compare-post
pwsh tools/RpsLoadProbe/run-rps.ps1 -Mode compare-lossy
pwsh tools/RpsLoadProbe/run-rps.ps1 -Mode compare-tls-cost
```

| Mode | Workload | Arms |
|---|---|---|
| `compare-bodies` | GET, keep-alive, **64 KiB** and **256 KiB** responses | TWP+nginx H1 TLS, TWP+nginx H2→H1, TWP H3→H1 |
| `compare-post` | POST **64 KiB** request + **64 KiB** response | same |
| `compare-lossy` | GET **64 KiB**, userspace **5 ms** delay + **1%** stall/drop | same |
| `compare-tls-cost` | H1 TLS only: keep-alive tiny, **new-connection** tiny, keep-alive **256 KiB** | TWP+nginx |

Lossy link is a userspace shim (not kernel netem): TCP gets per-buffer delay + occasional whole-connection stalls (HOL for multiplexed H2); UDP gets delay + datagram drops (QUIC). PUT with the same body is the same proxy work as POST; DELETE with no body matches GET.

CLI knobs (also usable on single arms): `--method`, `--response-bytes`, `--request-bytes`, `--no-keepalive`, `--delay-ms`, `--loss-percent`.

## MITM matrix (dual-crypto)

```powershell
pwsh tools/RpsLoadProbe/run-rps.ps1 -Mode compare-mitm
```

Explicit H1 MITM, transparent H2/H3 MITM, H2→H1 / H3→H1 MITM to HTTPS origins, and dual-crypto bridges (H1↔H2↔H3). nginx cannot MITM — these arms are TWP-only.

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
| `reverse-http3-cleartext` | H3 → cleartext H1 (`ForwardCleartext`) |
| `reverse-http3-to-http2` | H3 → H3→H2 → HTTPS h2 |

Also: `reverse-h2c` (h2c → HTTPS h2) in `compare-same`.

Other modes: `compare`, `compare-tls`, `compare-http2`, `explicit-pool-sweep`. See `--help`.

Status lines use non-blocking `ProbeLog` (not sync `Console.WriteLine` on workers).
