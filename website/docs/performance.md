# Performance

Titanium targets low-overhead reverse proxying and HTTPS interception: connection pooling, HTTP/2 multiplexing, and buffer reuse.

**RPS** is requests per second. gRPC bars are **RPC/s** (unary calls). Read **within** a chart (same OS), not Windows vs Linux as one number. Missing bars mean that peer cannot run that wire on that OS.

## Practical reverse RPS

Tiny keep-alive GET (~56 B JSON) plus POST 64 KiB, WebSocket, and unary gRPC — one chart per OS. Paths like H1/H2/H3 → HTTP/1 and POST 64 KiB are the closer typical reverse-proxy read; H2→H2 / H2→h2c on tiny GET are Titanium’s best case (small JSON, same protocol), not a typical TLS-in → HTTP/1-out job.

### Windows

![Practical reverse RPS on Windows](../../wiki/images/rps-practical-windows.png)

### Linux

![Practical reverse RPS on Linux](../../wiki/images/rps-practical-linux.png)

### macOS

![Practical reverse RPS on macOS](../../wiki/images/rps-practical-macos.png)

## Heavier reverse workloads

Larger bodies, POST, lossy links, TLS termination cost, and architecture-sensitive shapes (slow consumers, duplex, WebSocket). At **64 KiB H2 TLS→H2 TLS**, Titanium does not lead YARP — that is the larger-body check against the tiny-GET same-protocol medals. Linux charts below; Windows tables and charts are on the [Performance wiki](https://github.com/justcoding121/titanium-web-proxy/wiki/Performance#heavier-reverse-workloads).

![Heavier bodies (Linux)](../../wiki/images/rps-heavier-bodies-linux.png)

![Heavier POST (Linux)](../../wiki/images/rps-heavier-post-linux.png)

![Heavier lossy link (Linux)](../../wiki/images/rps-heavier-lossy-linux.png)

![TLS termination cost (Linux)](../../wiki/images/rps-heavier-tls-cost-linux.png)

![Architecture-sensitive (Linux)](../../wiki/images/rps-heavier-arch-linux.png)

## Full measurements

Detailed tables and methodology: [Performance wiki](https://github.com/justcoding121/titanium-web-proxy/wiki/Performance) · [Performance profiling](https://github.com/justcoding121/titanium-web-proxy/wiki/Performance-Profiling)

---

*How we measure:* matched GitHub Actions runners (~4 vCPU / 16 GiB) on Windows, Linux, and macOS; Titanium vs YARP, nginx, HAProxy, and Envoy; same client, origin, warmup, duration, and concurrency (sustain at 64). Absolute RPS varies slightly with runner noise.
