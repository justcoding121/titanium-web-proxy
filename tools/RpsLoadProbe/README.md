# RpsLoadProbe

Saturation RPS harness for Titanium.Web.Proxy. Measures the **breaking point** (last concurrency that still meets error/latency SLOs) and **peak RPS**, with optional same-machine **nginx** control arms.

## Fair terminate compare (recommended)

```powershell
pwsh tools/RpsLoadProbe/run-rps.ps1 -Mode compare-terminate
```

Runs sequentially: TWP/nginx H1 TLS terminate, TWP H2→H1 cleartext, nginx H2, TWP H3→H1 cleartext.

Cleartext-origin terminate arms run **origin and proxy in separate processes** so TWP is not sharing a ThreadPool/GC with Kestrel the way a separate nginx process does not.

## Bridge matrix

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
| `reverse-http2` / `reverse-http3` | Native same-version MITM |

**Not supported:** cleartext h2c upstream (TWP has no h2c). nginx/Windows has no QUIC.

Other modes: `compare`, `compare-tls`, `explicit-pool-sweep`. See `--help`.

Status lines use non-blocking `ProbeLog` (not sync `Console.WriteLine` on workers).
