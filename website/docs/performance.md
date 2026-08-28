# Performance

Titanium targets low-overhead MITM and reverse proxying: connection pooling, HTTP/2 multiplexing, and buffer reuse.

## Summary (from publishable CI tables)

On matched **GitHub Actions 4 vCPU / 16 GiB** runners, Titanium is typically:

- **at or above YARP** for reverse-proxy workloads
- **ahead of nginx** on H2/H3→H1 reverse
- **near parity on H1** (nginx still edges tiny keep-alive)

MITM is Titanium-only among those peers (they cannot MITM). Absolute RPS varies by OS, TLS, and MsQuic packaging — compare **within a table**, not across Windows vs Linux.

## Full measurements

Detailed tables, harness knobs, and methodology live in the project wiki:

- [Performance wiki](https://github.com/justcoding121/titanium-web-proxy/wiki/Performance)
- [Performance profiling](https://github.com/justcoding121/titanium-web-proxy/wiki/Performance-Profiling)
- Local cool A/B lab (not publishable): [Performance Local Lab](https://github.com/justcoding121/titanium-web-proxy/wiki/Performance-Local-Lab)

Harness: [`tools/RpsLoadProbe`](https://github.com/justcoding121/titanium-web-proxy/tree/develop/tools/RpsLoadProbe) and [PERF-GATES.md](https://github.com/justcoding121/titanium-web-proxy/blob/develop/tools/RpsLoadProbe/PERF-GATES.md).

```powershell
pwsh tools/RpsLoadProbe/run-rps.ps1 -Mode compare-saturation
```
