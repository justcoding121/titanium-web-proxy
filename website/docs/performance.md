# Performance

Titanium targets low-overhead reverse proxying and HTTPS interception: connection pooling, HTTP/2 multiplexing, and buffer reuse.

## How we measure

Charts below come from the same load harness on matched **GitHub Actions** runners (**4 vCPU / ~16 GiB** class) for Windows, Linux, and macOS. Each reverse-proxy arm runs as three processes (load generator, origin, and proxy). We compare **Titanium**, **YARP**, **nginx**, **HAProxy**, and **Envoy** under identical warmup, duration, and concurrency (sustain at concurrency **64**).

That keeps the comparison fair: same client, same origin, same runner class, and the same protocol wire for every product in a group. Missing bars mean that peer cannot run that wire on that OS (for example HAProxy/Envoy on Windows, or nginx without an HTTP/2 or HTTP/3 upstream). Absolute requests per second (RPS) still move a bit with runner noise — read **within** a chart, not Windows vs Linux as one number.

**RPS** is requests per second. gRPC bars are **RPC/s** (unary calls).

## Practical reverse RPS

Common industry reverse wires (tiny keep-alive GET) plus POST 64 KiB, WebSocket, and unary gRPC — one chart per OS.

### Windows

![Practical reverse RPS on Windows](../../wiki/images/rps-practical-windows.png)

### Linux

![Practical reverse RPS on Linux](../../wiki/images/rps-practical-linux.png)

### macOS

![Practical reverse RPS on macOS](../../wiki/images/rps-practical-macos.png)

## Heavier reverse workloads

Larger bodies, POST, lossy links, TLS termination cost, and architecture-sensitive shapes (slow consumers, duplex, WebSocket). Linux charts below; Windows tables and charts are on the [Performance wiki](https://github.com/justcoding121/titanium-web-proxy/wiki/Performance#heavier-reverse-workloads).

![Heavier bodies (Linux)](../../wiki/images/rps-heavier-bodies-linux.png)

![Heavier POST (Linux)](../../wiki/images/rps-heavier-post-linux.png)

![Heavier lossy link (Linux)](../../wiki/images/rps-heavier-lossy-linux.png)

![TLS termination cost (Linux)](../../wiki/images/rps-heavier-tls-cost-linux.png)

![Architecture-sensitive (Linux)](../../wiki/images/rps-heavier-arch-linux.png)

## Full measurements

Detailed tables and methodology live in the project wiki:

- [Performance wiki](https://github.com/justcoding121/titanium-web-proxy/wiki/Performance)
- [Performance profiling](https://github.com/justcoding121/titanium-web-proxy/wiki/Performance-Profiling)
