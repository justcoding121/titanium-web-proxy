# RpsLoadProbe

Saturation RPS harness for Titanium.Web.Proxy. Measures the **breaking point** (last concurrency that still meets error/latency SLOs) and **peak RPS**, with optional same-machine **nginx** control arms.

## Fair terminate compare (recommended)

```powershell
pwsh tools/RpsLoadProbe/run-rps.ps1 -Mode compare-terminate
```

Runs sequentially: TWP/nginx H1 TLS terminate, TWP H2→H1 cleartext, nginx H2, TWP H3→H1 cleartext.

| Arm | Topology |
|---|---|
| `twp-reverse-http1-tls` | Client TLS → `ForwardCleartext` → Kestrel HTTP/1 |
| `nginx-reverse-http1-tls` | nginx ssl → cleartext HTTP/1 |
| `twp-reverse-http2-cleartext` | Client TLS+h2 → H2→H1 bridge → cleartext HTTP/1 |
| `nginx-reverse-http2` | nginx ssl+http2 → cleartext HTTP/1 |
| `twp-reverse-http3-cleartext` | Client QUIC/h3 → cleartext HTTP/1 (`ForwardCleartext`) |

**Not supported / not compared:** cleartext h2c upstream (TWP has no h2c). H3→H3 always uses QUIC/TLS. nginx/Windows has no QUIC.

Other modes: `compare`, `compare-tls` (includes H2 MITM), `explicit-pool-sweep`. See `--help`.

Status lines use non-blocking `ProbeLog` (not sync `Console.WriteLine` on workers).
