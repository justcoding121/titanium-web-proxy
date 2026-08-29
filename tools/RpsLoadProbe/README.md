# RpsLoadProbe

Saturation RPS harness for Titanium.Web.Proxy. Measures the **breaking point** (last concurrency that still meets error/latency SLOs) and **peak RPS**.

Published numbers and external control-arm comparisons live only on the wiki [Performance](../../wiki/Performance.md) page (GitHub Actions medians on matched 4 vCPU / 16 GiB Linux+Windows runners). Local cool A/B and laptop tables live on [Performance Local Lab](../../wiki/Performance-Local-Lab.md); the playbook is on [Performance Profiling](../../wiki/Performance-Profiling.md). This README lists how to run the local harness.

Manual CI: [RPS saturation](../../.github/workflows/rps-saturation.yml) (`workflow_dispatch`, both `ubuntu-latest` and `windows-latest`).

## Tiered cadence

| Tier | Mode | When |
|------|------|------|
| Daily / per-PR | `compare-spot` ([`run-spot-matrix.ps1`](run-spot-matrix.ps1)) | minutes; Full÷Reverse + TWP÷YARP @ c=64 |
| Milestone | `compare-terminate` / `compare-matrix` | ~1–2h investigation |
| Editions | `compare-editions` | CLI / Plus / Intercept / stress arms vs baselines (~60 min) |
| Cross-version | `compare-cross-version` | 7.0 vs committed 6.0 baselines (Gate 2) |
| Release / wiki | `compare-product` | median of 3; full reverse + MITM (~3–4h with early-stop) |
| Heavier tables | `compare-bodies` / `post` / `lossy` / `arch` / `bridges` / `tls-cost` | dispatch independently from the workflow |

Harness defaults: warmup **2s** / measure **8s** / concurrency **8,16,32,64** / median of **3** for publishable GHA numbers. `--stop-on-slo-fail` (default **on**) stops an arm after the first SLO fail plus one peak confirmation step. See [PERF-GATES.md](PERF-GATES.md).

## Full 5×5 reverse matrix

Client × origin wire cartesian: **H1·plain, H1·TLS, H2·plain (h2c), H2·TLS, H3·QUIC** (25 cells). Each cell has a TWP reverse arm and a YARP peer.

```powershell
pwsh tools/RpsLoadProbe/run-rps.ps1 -Mode compare-matrix
```

New bridge arms that complete the grid (beyond the historical subset):

| Arm | Topology |
|---|---|
| `reverse-http3-to-h2c` | H3 → prior-knowledge h2c |
| `reverse-http1-to-h2c` | H1 TLS → prior-knowledge h2c |
| `reverse-http1-plain-to-h2c` | H1 plain → prior-knowledge h2c |
| `reverse-http1-plain-to-http2` | H1 plain → HTTPS h2 |
| `reverse-http1-plain-to-http3` | H1 plain → QUIC/h3 |
| `reverse-h2c-to-https` | h2c → HTTPS HTTP/1 |

YARP dual-crypto peers: `yarp-reverse-http1-tls-to-https`, `yarp-reverse-http2-to-https-http1`, `yarp-reverse-http3-to-https-http1`.

**Not supported:** `Upgrade: h2c`. Explicit-proxy inbound h2c is not implemented. Outbound and inbound prior-knowledge h2c on transparent reverse are supported. H3 is always QUIC/TLS for TWP (no cleartext H3 client or origin).

## Same-protocol matrix

```powershell
pwsh tools/RpsLoadProbe/run-rps.ps1 -Mode compare-same
```

| Arm | Topology |
|---|---|
| `reverse-http1` | H1 cleartext → H1 cleartext |
| `reverse-http1-tls` | H1 TLS terminate → H1 cleartext |
| `https-mitm` | Explicit H1 TLS MITM → HTTPS H1 |
| `reverse-http2` | H2 TLS MITM → HTTPS H2 |
| `reverse-http3` | H3 QUIC → H3 origin (dual-listen reverse) |
| `yarp-reverse-http3-to-http3` | Managed reverse peer H3 → H3 |

## Fair terminate compare

```powershell
pwsh tools/RpsLoadProbe/run-rps.ps1 -Mode compare-terminate
```

Client TLS → cleartext origin across H1 TLS terminate, H2→H1, h2c→H1, and H3→H1 arms.

Every `--ramp` arm is **three OS processes**: parent load generator, `--serve-origin` child, `--serve-proxy` child (except **origin-direct** arms, which omit the proxy child). The parent seeds a temp test CA (`TWP_RPS_CERT_DIR`) so HTTPS/QUIC origin and proxy share the same root. Combined `--serve` remains for local debugging only; it is not on the ramp path. Absolute RPS from older combined TLS/QUIC-origin cells is not comparable to split runs — prefer TWP÷peer ratios.

## Saturation control (origin ceiling)

Calibration only — not a product ranking matrix. Tiny keep-alive GET; three blocks:

```powershell
pwsh tools/RpsLoadProbe/run-rps.ps1 -Mode compare-saturation
```

| Block | Arms |
|---|---|
| **A — H1 plain** | `origin-direct`, `origin-direct-bombardier` (if PATH), `bare-reverse-http1`, `nginx-reverse-http1`, `yarp-reverse-http1`, `twp-reverse-http1` |
| **B — H2 TLS→H1** | `nginx-reverse-http2` (if nginx), `yarp-reverse-http2`, `twp-reverse-http2-cleartext` |
| **C — H3→H1** | `nginx-reverse-http3-cleartext` (if nginx + `http_v3_module`), `yarp-reverse-http3-cleartext`, `twp-reverse-http3-cleartext` (skipped when `QuicListener` unsupported) |

**CSV resource columns** (every measure step): `proxy_rss_peak_bytes`, `proxy_cpu_avg_pct` (wiki label: **Memory (RSS)**). Names stay `proxy_*` even on origin-direct arms (those sample the **origin** child PID). Otherwise sample the **proxy** child PID plus its **full descendant tree** (so nginx workers under the serve-proxy → master chain are included). Empty when the PID cannot be sampled. Poll ~200ms during the measure window; peak Working Set / VmRSS sum and average CPU% (of all logical processors).

**Summary:**
- **compare-saturation:** Block A prints median peak RPS as **% of origin-direct** (and bombardier when present) plus median Memory (RSS) / CPU at the peak-RPS step. Blocks B/C print peer÷YARP and peer÷nginx (when present) plus Memory (RSS) / CPU — not % of H1 origin-direct.
- **Other compare-* matrix modes:** median peak RPS for all arms; **TWP-only** median Memory (RSS) / CPU (`median_memory_rss_bytes` / `median_cpu_avg_pct`). nginx/YARP Memory/CPU remain saturation-only for peer comparison.

Memory A/B knobs (load generator): `TWP_RPS_SINGLE_HTTP2_CONNECTION=1` / `TWP_RPS_SINGLE_HTTP3_CONNECTION=1` force a single multiplexed client connection (`EnableMultipleHttp2Connections` / `EnableMultipleHttp3Connections` off). See [Performance Profiling — Memory (RSS)](../../wiki/Performance-Profiling.md#memory-rss--h2h1-vs-h1--h3).

Paste CI medians into the wiki [Performance — Saturation control](../../wiki/Performance.md#saturation-control) section. Do not mix bombardier into the publishable TWP÷YARP÷nginx matrices.

## Heavier reverse workloads (bodies / POST / lossy / TLS cost)

Tiny keep-alive GET stresses the per-request path hardest. These modes exercise heavier reverse work:

```powershell
pwsh tools/RpsLoadProbe/run-rps.ps1 -Mode compare-bodies
pwsh tools/RpsLoadProbe/run-rps.ps1 -Mode compare-post
pwsh tools/RpsLoadProbe/run-rps.ps1 -Mode compare-lossy
pwsh tools/RpsLoadProbe/run-rps.ps1 -Mode compare-tls-cost
pwsh tools/RpsLoadProbe/run-rps.ps1 -Mode compare-arch
```

| Mode | Workload |
|---|---|
| `compare-bodies` | GET, keep-alive, **64 KiB** and **256 KiB** responses |
| `compare-post` | POST **64 KiB** request + **64 KiB** response |
| `compare-lossy` | GET **64 KiB**, userspace **5 ms** delay + **1%** stall/drop (H1/H2 TCP stall; H3 UDP datagram drop) |
| `compare-tls-cost` | H1 TLS only: keep-alive tiny, **new-connection** tiny, keep-alive **256 KiB** |
| `compare-arch` | Slow consumer (256 KiB GET, throttled read), early response (POST overlap), H2 TLS↔H2 TLS duplex, WebSocket echo |

Lossy link is a userspace shim (not kernel netem): TCP gets per-buffer delay + occasional whole-connection stalls (HOL for multiplexed H2); UDP gets delay + datagram drops (QUIC). PUT with the same body is the same proxy work as POST; DELETE with no body matches GET.

CLI knobs (also usable on single arms): `--method`, `--response-bytes`, `--request-bytes`, `--no-keepalive`, `--delay-ms`, `--loss-percent`.

## MITM matrix (true interception, TWP-only)

```powershell
pwsh tools/RpsLoadProbe/run-rps.ps1 -Mode compare-mitm
# Same-job reverse peers + MITM lite + MITM full (for wiki Lite÷Reverse / Full÷Reverse):
pwsh tools/RpsLoadProbe/run-rps.ps1 -Mode compare-product
```

Two TWP-only MITM shapes on the same Client×Origin wires (+ CONNECT). nginx/YARP cannot MITM.

| Shape | Arms | Child env | Path |
|---|---|---|---|
| **Lite** | `twp-mitm-*` | `TWP_RPS_HTTP_INTERCEPTION=1` | No-op `BeforeRequest`/`BeforeResponse`; after unchanged-lite finish, can reuse reverse compressed-relay / terminate-lite |
| **Full** | `twp-mitm-full-*` | `…_INTERCEPTION=1` + `TWP_RPS_HTTP_INTERCEPTION_MUTATE=1` | Handlers append `x-twp-rps-probe` on request and response; product uses generic append-only relay via `MitmCompressedRelayHelper` |

`compare-mitm` and `compare-product` both run Lite then Full (Full roughly doubles MITM wall time; GHA `rps-saturation` job timeout is 420m so `compare-product` ×3 can finish). Wiki MITM table columns: Lite sustain, Full sustain, Lite÷Reverse, Full÷Reverse (RSS/CPU footnotes on sustain cells).

**Reverse** (`compare-matrix` / reverse half of `compare-product`) is bare terminate (no handlers). nginx conf matches TWP/YARP streaming: `keepalive 256`, `proxy_buffering off`, `proxy_request_buffering off`.

## Editions (`titanium run` daemon)

Library arms (`twp-reverse-*`) embed Core with probe-tuned settings. Edition arms spawn the shipped CLI (`titanium run -c twp.yaml`) as an external process — same shape as nginx — for product-defaults comparison. Full matrix is ~60 min (expanded Plus/CLI stress arms).

```powershell
pwsh tools/RpsLoadProbe/run-rps.ps1 -Mode compare-editions
pwsh tools/RpsLoadProbe/validate-edition-gates.ps1 -CsvPath tools/RpsLoadProbe/results/rps-ramp-*.csv
```

| Arm | What it measures |
|-----|------------------|
| `twp-cli-reverse-http1` / `-tls` | CLI daemon, product defaults vs library |
| `twp-cli-reverse-http1-route` | Single route table ≡ ForwardHost |
| `twp-cli-plus-base-http1` | Plus ALC + control plane (no options) |
| `twp-cli-plus-cache-http1` | Plus + `cache.enable` (cold) |
| `twp-cli-intercept-http1` | Route `RequestHeaderSet` transform → session path |
| `twp-cli-plus-waf-http1` | WAF denyPaths that do not match `/` |
| `twp-cli-plus-cidr-http1` | `security.allowCidrs=127.0.0.0/8` |
| `twp-cli-plus-jwt-http1` | RS256 JWT + JWKS mini-server; Bearer on every request |
| `twp-cli-plus-ratelimit-http1` | `state.mode=memory` + very high rate limit |
| `twp-cli-plus-resilience-http1` | Active health vs ForwardHost+cluster destinations |
| `twp-cli-plus-discovery-file-http1` | File discovery + mid-ramp rewrite |
| `twp-cli-plus-metrics-scrape-http1` | Background `/metrics` + `/v1/snapshot` every 5s |
| `twp-cli-plus-cache-hit-http1` | Cache warm then measure (vs plus-cache cold) |
| `twp-cli-static-http1` | `staticFiles.root` tiny file |
| `twp-cli-logging-http1` | Logging enabled + Info file sink |
| `twp-cli-lb-leasttime-http1` | LeastTime across two healthy origins |
| `twp-cli-dialect-twp-http1` | `.twp` `listen`/`forward` site-file |

Gates: see [PERF-GATES.md](PERF-GATES.md). Thresholds lock after a clean Win+Linux pass. Build/publish `Titanium.Cli` (and Plus DLL beside it for Plus arms) before ramping.

## Cross-version (7.0 vs 6.0)

```powershell
pwsh tools/RpsLoadProbe/run-rps.ps1 -Mode compare-cross-version
pwsh tools/RpsLoadProbe/validate-cross-version.ps1 `
  -BaselineCsv tools/RpsLoadProbe/results/baseline-6.0-win.csv `
  -CurrentCsv  tools/RpsLoadProbe/results/rps-ramp-*.csv
```

`compare-cross-version` runs the reverse matrix with routes unset (same ForwardHost path as 6.x). Baselines are committed CSVs from the published 6.0 GHA medians — do not re-run 6.0.

## Bridge matrix (cross-version)

```powershell
pwsh tools/RpsLoadProbe/run-rps.ps1 -Mode compare-bridges
```

| Arm | Topology |
|---|---|
| `reverse-http2-cleartext` | H2 TLS → H2→H1 → cleartext H1 |
| `reverse-http2-to-h2c` | H2 TLS → prior-knowledge h2c → cleartext H2 |
| `reverse-h2c-to-h1` | h2c → H2→H1 → cleartext H1 |
| `reverse-h2c-to-h2c` | h2c → cleartext H2 |
| `reverse-h2c-to-https` | h2c → H2→H1 → HTTPS H1 |
| `reverse-h2c-to-h3` | h2c → H2→H3 → QUIC/h3 |
| `reverse-http11-to-http2` | H1 TLS → H1→H2 → HTTPS h2 |
| `reverse-http1-to-h2c` | H1 TLS → prior-knowledge h2c |
| `reverse-http1-plain-to-h2c` | H1 plain → prior-knowledge h2c |
| `reverse-http1-plain-to-http2` | H1 plain → HTTPS h2 |
| `reverse-http1-plain-to-http3` | H1 plain → QUIC/h3 |
| `reverse-http1-to-http3` | H1 TLS → H1→H3 → QUIC/h3 |
| `reverse-http2-to-http3` | H2 TLS → H2→H3 → QUIC/h3 |
| `reverse-http3-cleartext` | H3 → cleartext H1 |
| `nginx-reverse-http3-cleartext` | Native reverse H3 → cleartext H1 (`http_v3_module`) |
| `reverse-http3-to-h2c` | H3 → prior-knowledge h2c |
| `reverse-http3-to-http2` | H3 → H3→H2 → HTTPS h2 |

Also: `reverse-h2c` (h2c → HTTPS h2) in `compare-same`. Full TWP+YARP 5×5: `compare-matrix`.

Other modes: `compare`, `compare-tls`, `compare-http2`, `explicit-pool-sweep`. See `--help`.

Status lines use non-blocking `ProbeLog` (not sync `Console.WriteLine` on workers).
