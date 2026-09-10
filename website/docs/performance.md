# Performance

Titanium targets low-overhead reverse proxying and HTTPS interception: connection pooling, HTTP/2 multiplexing, and buffer reuse.

**RPS** is requests per second. gRPC bars are **RPC/s** (unary calls). Read **within** a chart (same OS), not Windows vs Linux as one number. Missing bars mean that peer cannot run that wire on that OS.

## Practical reverse RPS (tiny requests)

Common reverse wires with **tiny keep-alive GET (~56 B)**, plus WebSocket and unary gRPC — one chart per OS. How to read medals and workload shape: [Performance wiki](https://github.com/justcoding121/titanium-web-proxy/wiki/Performance#why-this-comparison-is-fair).

### Windows

![Practical reverse RPS on Windows (tiny requests)](../../wiki/images/rps-practical-windows.png)

### Linux

![Practical reverse RPS on Linux (tiny requests)](../../wiki/images/rps-practical-linux.png)

### macOS

![Practical reverse RPS on macOS (tiny requests)](../../wiki/images/rps-practical-macos.png)

## Practical reverse RPS (64 KB)

Typical reverse wires with **64 KB GET/POST** (plus 256 KB H1 terminate) — body work separate from the tiny-GET chart above. Windows and Linux below; macOS heavier bodies are not published yet.

### Windows

![Practical reverse RPS on Windows (64 KB)](../../wiki/images/rps-practical-heavier-windows.png)

### Linux

![Practical reverse RPS on Linux (64 KB)](../../wiki/images/rps-practical-heavier-linux.png)

## Heavier reverse workloads

Larger bodies, POST, lossy links, TLS termination cost, and architecture-sensitive shapes (slow consumers, duplex, WebSocket). Linux charts below; Windows tables and charts are on the [Performance wiki](https://github.com/justcoding121/titanium-web-proxy/wiki/Performance#heavier-reverse-workloads).

![Heavier bodies (Linux)](../../wiki/images/rps-heavier-bodies-linux.png)

![Heavier POST (Linux)](../../wiki/images/rps-heavier-post-linux.png)

![Heavier lossy link (Linux)](../../wiki/images/rps-heavier-lossy-linux.png)

![TLS termination cost (Linux)](../../wiki/images/rps-heavier-tls-cost-linux.png)

![Architecture-sensitive (Linux)](../../wiki/images/rps-heavier-arch-linux.png)

## Full measurements

Detailed tables and methodology: [Performance wiki](https://github.com/justcoding121/titanium-web-proxy/wiki/Performance) · [Performance profiling](https://github.com/justcoding121/titanium-web-proxy/wiki/Performance-Profiling)

---

*How we measure:* matched GitHub Actions runners (~4 vCPU / 16 GiB) on Windows, Linux, and macOS; Titanium vs YARP, nginx, HAProxy, and Envoy; same client, origin, warmup, duration, and concurrency (sustain at 64). Absolute RPS varies slightly with runner noise.
