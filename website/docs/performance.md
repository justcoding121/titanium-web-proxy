# Performance

Titanium targets low-overhead man-in-the-middle (MITM) and reverse proxying: connection pooling, HTTP/2 multiplexing, and buffer reuse.

## Summary (from publishable CI tables)

On matched **GitHub Actions 4 vCPU / 16 GiB** runners, Titanium is typically:

- **at or above YARP** for reverse-proxy workloads
- **ahead of nginx** on H2/H3→H1 reverse; **near parity** for the rest (nginx still edges tiny keep-alive H1)

MITM is Titanium-only among those peers (they cannot MITM). Absolute requests per second (RPS) varies by OS, TLS, and MsQuic packaging — compare **within a table**, not across Windows vs Linux.

## Practical reverse RPS (CI)

Sustain RPS @ concurrency 64 for common reverse wires (tiny keep-alive GET). Grouped bars: Titanium / YARP / nginx. nginx now includes **HTTPS-origin** peers via `proxy_ssl` (H1/H2/H3 → H1 TLS) in addition to cleartext-origin terminate. Missing nginx bars mean that wire is still *Not possible* for stock nginx (no H2/H3 upstream). Charts from `compare-product` @ `024bd68d` ([34126809918](https://github.com/justcoding121/titanium-web-proxy/actions/runs/34126809918)); regenerate with [`render-practical-charts.py`](https://github.com/justcoding121/titanium-web-proxy/blob/develop/tools/RpsLoadProbe/render-practical-charts.py).

### Windows

![Practical reverse RPS on Windows](../../wiki/images/rps-practical-windows.png)

### Linux

![Practical reverse RPS on Linux](../../wiki/images/rps-practical-linux.png)

### macOS

![Practical reverse RPS on macOS](../../wiki/images/rps-practical-macos.png)

## Full measurements

Detailed tables, harness knobs, and methodology live in the project wiki:

- [Performance wiki](https://github.com/justcoding121/titanium-web-proxy/wiki/Performance)
- [Performance profiling](https://github.com/justcoding121/titanium-web-proxy/wiki/Performance-Profiling)
- Local cool A/B lab (not publishable): [Performance Local Lab](https://github.com/justcoding121/titanium-web-proxy/wiki/Performance-Local-Lab)

Harness: [`tools/RpsLoadProbe`](https://github.com/justcoding121/titanium-web-proxy/tree/develop/tools/RpsLoadProbe) and [PERF-GATES.md](https://github.com/justcoding121/titanium-web-proxy/blob/develop/tools/RpsLoadProbe/PERF-GATES.md).

### Feature cost notes

- **gRPC-JSON transcoding** (Plus): when unset, Core pays only a null check. When enabled, the process uses the session interception path and buffers matched unary bodies — do not enable it on default RPS edition arms. Details: [gRPC-JSON transcoding](/docs/grpc-json-transcoding).

```powershell
pwsh tools/RpsLoadProbe/run-rps.ps1 -Mode compare-saturation
```
