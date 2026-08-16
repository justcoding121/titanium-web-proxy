# RpsLoadProbe

Saturation RPS harness for Titanium.Web.Proxy. Measures the **breaking point** (last concurrency that still meets error/latency SLOs) and **peak RPS**, with an optional same-machine **nginx** control arm on the reverse-HTTP/1 path.

This is **not** run from the per-PR build. Run it manually in Release (local Windows or via the `rps-saturation` GitHub Actions workflow on `ubuntu-latest`).

## What it measures

| Arm | Topology |
|---|---|
| `twp-reverse-http1` | Client → TWP `TransparentProxyEndPoint` → Kestrel HTTP origin |
| `nginx-reverse-http1` | Client → nginx `proxy_pass` → **the same** Kestrel origin |
| `twp-https-mitm` | Client → TWP explicit MITM → Kestrel HTTPS origin (no nginx equivalent) |

Arms always run **one after another** so each proxy gets the full machine.

**Breaking-point SLOs (defaults):**

- error rate &lt; 0.1%
- p99 ≤ 50 ms (HTTP/1) or 100 ms (HTTPS MITM)

The load generator is an embedded `SocketsHttpHandler` pool labeled `dotnet-httpclient` in the CSV. Prefer bombardier/wrk against `--serve` when publishing industry-style numbers.

## Quick start

```powershell
# Full compare (TWP reverse, nginx if present, TWP MITM)
dotnet run -c Release --project tools/RpsLoadProbe -- --ramp --mode compare

# Or use the orchestrator script (builds, prints machine info, optional bombardier check)
pwsh tools/RpsLoadProbe/run-rps.ps1 -Mode compare
```

Serve a single arm for an external tool:

```powershell
dotnet run -c Release --project tools/RpsLoadProbe -- --serve --mode reverse-http1
# then, in another terminal:
bombardier -c 256 -d 30s -l http://127.0.0.1:<listen-port>/
```

## Installing nginx (control arm)

The harness does **not** download nginx. If `nginx` / `nginx.exe` is missing, TWP arms still run and a message explains how to install.

### Windows (native — fair peer for local TWP)

1. Official zip: [nginx for Windows](https://nginx.org/en/docs/windows.html) — unpack and add the folder containing `nginx.exe` to `PATH`, **or**
2. `scoop install nginx`, **or**
3. `choco install nginx`

Then either ensure `nginx.exe` is on `PATH`, or pass:

```powershell
--nginx-path "C:\path\to\nginx.exe"
```

Label results as **nginx/Windows**. The Windows port is not the Linux epoll binary from nginx blog posts.

### Linux (apt — closer to published nginx methodology)

```bash
sudo apt-get update && sudo apt-get install -y nginx
```

On GitHub Actions, prefer the dedicated `rps-saturation.yml` workflow (`workflow_dispatch`) rather than installing by hand.

## Honesty rules

- Compare TWP and nginx only on **reverse HTTP/1** with the same origin, same flags, sequential runs.
- Do **not** compare TWP HTTPS MITM to nginx.
- Do **not** mix Windows-local numbers and GitHub `ubuntu-latest` numbers into one winner row.
- Do **not** claim a single GHA run is a stable breaking point (shared runners are noisy).
- Close browsers and heavy apps before a publishable local run.
- `--ramp` uses a **process split**: origin + proxy as children, load generator in the parent (HTTPS MITM keeps origin+proxy together so they share one test CA).

