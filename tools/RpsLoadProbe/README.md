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

**Not supported:** cleartext h2c (no h2c upstream). H3 is always QUIC/TLS.

## Fair terminate compare

```powershell
pwsh tools/RpsLoadProbe/run-rps.ps1 -Mode compare-terminate
```

Client TLS → cleartext origin: TWP/nginx H1 TLS, TWP H2→H1, nginx H2, TWP H3→H1.

Cleartext-origin terminate arms run **origin and proxy in separate processes** so TWP is not sharing a ThreadPool/GC with Kestrel the way a separate nginx process does not.

## Bridge matrix (cross-version)

```powershell
pwsh tools/RpsLoadProbe/run-rps.ps1 -Mode compare-bridges
```

| Arm | Topology |
|---|---|
| `reverse-http2-cleartext` | H2 TLS → H2→H1 → cleartext H1 |
| `reverse-http11-to-http2` | H1 TLS → H1→H2 → HTTPS h2 |
| `reverse-http1-to-http3` | H1 TLS → H1→H3 → QUIC/h3 |
| `reverse-http2-to-http3` | H2 TLS → H2→H3 → QUIC/h3 |
| `reverse-http3-cleartext` | H3 → cleartext H1 (`ForwardCleartext`) |
| `reverse-http3-to-http2` | H3 → H3→H2 → HTTPS h2 |

Other modes: `compare`, `compare-tls`, `compare-http2`, `explicit-pool-sweep`. See `--help`.

Status lines use non-blocking `ProbeLog` (not sync `Console.WriteLine` on workers).
