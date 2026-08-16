# RpsLoadProbe

Saturation RPS harness for Titanium.Web.Proxy. Measures the **breaking point** (last concurrency that still meets error/latency SLOs) and **peak RPS**, with optional same-machine **nginx** control arms on reverse HTTP/1 and HTTP/2.

This is **not** run from the per-PR build. Run it manually in Release (local Windows or via the `rps-saturation` GitHub Actions workflow on `ubuntu-latest`).

## What it measures

| Arm | Topology |
|---|---|
| `twp-reverse-http1` | Client → TWP `TransparentProxyEndPoint` → Kestrel HTTP |
| `nginx-reverse-http1` | Client → nginx `proxy_pass` → **the same** Kestrel origin |
| `twp-reverse-http1-tls` | Client TLS → TWP terminate (`ForwardCleartext`) → cleartext origin |
| `nginx-reverse-http1-tls` | Client TLS → nginx ssl → cleartext origin |
| `twp-reverse-http2` | Client TLS+h2 → TWP transparent MITM → Kestrel **HTTPS h2** |
| `nginx-reverse-http2` | Client TLS+h2 → nginx ssl+http2 → **cleartext** HTTP origin |
| `twp-reverse-http3` | Client QUIC/h3 → TWP `TransparentQuicProxyEndPoint` → Quic HTTP/3 origin (**no nginx/Windows**) |
| `twp-https-mitm` | Client → TWP explicit MITM → Kestrel HTTPS |
| `twp-explicit-http1-multi` | Explicit MITM across 16 HTTPS origins (pool-depth study) |
| `explicit-pool-sweep` | Same fan-out at `MaxCachedConnections` 4 / 32 / 128 |

Arms always run **one after another** so each proxy gets the full machine. Status lines use a non-blocking `ProbeLog` channel (not sync `Console.WriteLine` on worker threads).

**Breaking-point SLOs (defaults):**

- error rate &lt; 0.1%
- p99 ≤ 50 ms (HTTP/1), 100 ms (HTTP/2 / MITM), 150 ms (HTTP/3)

TCP arms use embedded `SocketsHttpHandler` (`dotnet-httpclient`). HTTP/3 uses a native Quic load generator (`quic-http3`) because HttpClient cannot drive a UDP-only transparent QUIC endpoint.

## Quick start

```powershell
# Cleartext HTTP/1 compare (TWP reverse, nginx if present, TWP MITM)
pwsh tools/RpsLoadProbe/run-rps.ps1 -Mode compare

# Fair TLS-terminate HTTP/1 + HTTP/2 + HTTP/3
pwsh tools/RpsLoadProbe/run-rps.ps1 -Mode compare-tls

# Explicit multi-origin MaxCachedConnections sweep
pwsh tools/RpsLoadProbe/run-rps.ps1 -Mode explicit-pool-sweep
```

Serve a single arm for an external tool:

```powershell
dotnet run -c Release --project tools/RpsLoadProbe -- --serve --mode reverse-http2
```

## Installing nginx (control arm)

The harness does **not** download nginx. If `nginx` / `nginx.exe` is missing, TWP arms still run and a message explains how to install.

### Windows (native — fair peer for local TWP)

1. Official zip: [nginx for Windows](https://nginx.org/en/docs/windows.html) — unpack and add the folder containing `nginx.exe` to `PATH`, **or**
2. `scoop install nginx`, **or**
3. `choco install nginx`

Then either ensure `nginx.exe` is on `PATH`, or pass `--nginx-path "C:\path\to\nginx.exe"`.

Label results as **nginx/Windows**. The Windows port is not the Linux epoll binary from nginx blog posts. **HTTP/3/QUIC is not available on nginx/Windows** (UDP unsupported).

### Linux (apt — closer to published nginx methodology)

```bash
sudo apt-get update && sudo apt-get install -y nginx
```

On GitHub Actions, prefer the dedicated `rps-saturation.yml` workflow (`workflow_dispatch`) rather than installing by hand.

## Honesty rules

- Compare TWP and nginx only when the **crypto hop count and upstream protocol** match (or call out the gap in the topology column).
- Tiny-payload loopback RPS often favors HTTP/1.1 keepalive over H2/H3; do not expect H3 > H2 > H1 from RPS alone.
- Do **not** compare TWP HTTPS MITM or HTTP/3 to nginx on Windows.
- Do **not** mix Windows-local numbers and GitHub `ubuntu-latest` numbers into one winner row.
- Do **not** claim a single GHA run is a stable breaking point (shared runners are noisy).
- Close browsers and heavy apps before a publishable local run.
- `--ramp` uses a **process split** where possible; TLS arms keep origin+proxy together so they share one test CA.
