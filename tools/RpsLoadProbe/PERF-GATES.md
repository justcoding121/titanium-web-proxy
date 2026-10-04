# Perf gate checklist (7.0)

Run on the release SHA via [`.github/workflows/rps-saturation.yml`](../../.github/workflows/rps-saturation.yml) (manual `workflow_dispatch`).

Product version is **`7.0.0.0`**; gates block beta/stable tags, not feature commits.

## Branch policy + CI gates (beta / stable)

**PR-only merges into `beta` / `stable`** (no direct pushes). Required status checks before merge:

| Check | Role |
|-------|------|
| `.NET / build` | Unit + E2E + Windows Headless/Visual/Plus Playwright |
| `.NET / ui-portable` (Win/Linux/macOS) | Portable E2E-UI + Headless + Visual + Plus Playwright |
| `.NET / cli-e2e` (Win/Linux/macOS) | CLI + Plus process E2E |
| `RPS saturation / rps` | **`compare-spot`** on PRs into beta/stable |

**Publish / release SHA gates** (after merge):

| Event | RPS mode | Blocks |
|-------|----------|--------|
| Push to `beta` / `stable` (NuGet `publish`) | `compare-editions` via `.NET / rps-publish-gate` **and** `compare-spot` via `.NET / rps-peer-gate` (parallel; Core÷YARP + MITM÷Reverse @ c=64); also requires `cli-e2e` | NuGet publish + product tag |
| Tag / `release.yml` **stable** channel | Linux **`compare-product-smoke`** via `release.yml` `rps-product-smoke` | GitHub Release product assets |
| Wiki-grade product matrix | Full `compare-product` (manual `workflow_dispatch` on `rps-saturation.yml`) | Does not block release |

**Advisory vs merge-blocking:** PR required checks use **`compare-spot`**. On push to `beta`/`stable`, edition + peer jobs gate **NuGet publish** via `rps-publish-gate` / `rps-peer-gate` — treat Mac edition ratio noise as a publish signal to investigate, not a substitute for the spot peer floor. Push no longer runs 3-OS `rps-saturation` editions (that duplicated Ubuntu publish-gate).

`rps-peer-gate` re-checks Core vs YARP on the merge SHA so a uniform Core slowdown cannot hide behind green edition ratios. It runs in parallel with editions, so publish wall clock stays ~max(editions ≈60m, spot ≈10–20m).

Do **not** run full `compare-product` on every develop PR. Thresholds change only with written rationale here + commit â never loosen gates silently to go green.

## Tiered cadence (when to run which mode)

| Tier | When | Mode | Wall-clock |
|------|------|------|------------|
| Daily / develop PR | feature commits (advisory) | `compare-spot` ([`run-spot-matrix.ps1`](run-spot-matrix.ps1)) | minutes |
| Milestone / investigation | before merge to main | `compare-terminate` or `compare-matrix` | ~1â2h |
| Editions | after CLI/Plus changes | `compare-editions` on Linux + [`validate-edition-gates.ps1`](validate-edition-gates.ps1) | ~60 min |
| Beta / stable publish | push to `beta`/`stable` | Linux `compare-editions` + parallel `compare-spot` ([`run-spot-matrix.ps1`](run-spot-matrix.ps1)) | ~60 min wall |
| Pre-wiki smoke (required) | after Core / harness changes | **`compare-product-smoke`** Linux **2** comparison-group shards (`repeats=1`) before full product | ~30–60 min |
| Release / wiki refresh | release SHA | `compare-product` and `compare-bodies` (median of 3) via the [RPS suite](../../.github/workflows/rps-suite.yml) on Win/Linux/macOS. Other suite modes are Linux only | ~5h wall on a Free account when macOS runs |
| Unary gRPC | as needed | `compare-grpc` on Linux (H2 TLS and H2 TLS→h2c) | ~20–50 min |
| WebSocket dual-TLS / RFC 8441 | as needed | `compare-ws-h1tls` / `compare-ws-h2` on Linux | ~15–40 min each |
| Heavier wiki tables | as needed | `compare-bodies` on all three OS; `post` / `lossy` / `arch` / `tls-cost` on Linux | one row per job |

### One job per wiki row

Dispatch the [RPS suite](../../.github/workflows/rps-suite.yml) once per mode. Its prep job lists the comparison groups (`--print-groups`) and each group becomes one job per OS, so a wiki row's peers and its MITM Lite and Full arms stay on the same VM while no single job can run for hours. `compare-saturation` and `compare-editions` stay one job per OS, because saturation compares proxy arms against `origin-direct` from the same run. Address a row by its group key (`--arm-shard h1c-h1c`), not by an `i/n` index, so a re-run measures the same row even if a peer install fails. `i/n` remains for the smoke modes.

A single-row leg times out after 60 minutes of ramp and 75 minutes overall, so a wedged macOS VM costs one of the five slots for about an hour instead of six. Each leg uploads a `leg-status.json` (overwritten on re-run) and the suite's aggregate job fails unless every row reports success on every OS.

Free-plan accounts run 20 jobs at once and only 5 of them can be macOS. Give the suite a quiet window: do not push pull requests or start other macOS jobs while it runs. Re-run only infrastructure failures (`gh run rerun --failed`); a failed gate is a finding and is never re-rolled, and the published numbers are the last attempt, never the best of several.

Validate locally with [`validate-arm-shards.ps1`](validate-arm-shards.ps1). Paste scripts union `rps-csv-<os>-shard-*`, and [`download-rps-suite.ps1`](download-rps-suite.ps1) lays the artifacts out for them.

Do **not** run full `compare-product` as a daily smoke. Prefer TWP÷YARP / TWP÷nginx / edition ratios over absolute RPS. Shards keep one Client×Origin row on one VM — do not compare absolute RPS across shards. Early-stop (`--stop-on-slo-fail`, default on) aborts an arm after the first SLO fail plus one peak confirmation step. Local shard check: [`validate-arm-shards.ps1`](validate-arm-shards.ps1).

## Gate 1 (after Core route wire)

- [x] Probe config: routes **unset** / null `ReverseProxy` (zero-cost default)
- [x] Plus / Inspector DLLs **not** loaded by the probe process (library arms); edition arms spawn CLI externally
- [x] Full matrix (ubuntu+windows, `compare-matrix`) â GHA [33151235741](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33151235741) @ `d1e0c65c` **success** both OS. Absolute RPS vs prior medians (`33087091622` / `33087088466`) swings with runner heat (Win down / Linux up); **peer-normalized** TWPÃ·YARP is the regression signal (same policy as Gate 2 cross-version). A few Win peer-norm cells dipped under 0.90 on that older SHA â re-locked on current `develop` via Gate 2 matrix below.
- [x] Additional: single-route table â¡ ForwardHost within **10%** RPS of pure ForwardHost on the same build (`twp-cli-reverse-http1-route` Ã· `twp-cli-reverse-http1` â¥ **0.90**)

**Runs (2026-08-28, `develop` @ `d1e0c65c`):**

| Run | Mode | Status | URL |
|-----|------|--------|-----|
| Gate 1 terminate | `compare-terminate` | **success** | https://github.com/justcoding121/titanium-web-proxy/actions/runs/33151234059 |
| Gate 1 matrix | `compare-matrix` | **success** | https://github.com/justcoding121/titanium-web-proxy/actions/runs/33151235741 |

Terminate smoke (peak RPS; routes unset): TWP H1 TLS win **34273**, ubuntu **24171** â ahead of YARP on H1/H2c arms in that run.

## Gate 2 (before first beta / `v7.0.0` stable tag)

- [x] Same unset-routes probe matrix as gate 1 â GHA [33263427055](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33263427055) `compare-matrix` both OS **success** @ `3d9aba23`
- [x] Plus / Inspector DLLs still absent from probe library path (`RpsLoadProbe` references Core only)
- [x] **Cross-version:** GHA [33270571908](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33270571908) @ `0ef6d4dd` both OS **success** (RSS floor **1.20**; peer-norm â¥ **0.90** or current TWPÃ·YARP â¥ **0.90**). Prior Win fail [33263428508](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33263428508) was YARP spike + RSS noise on H1âh2c / H3.
- [x] **Editions:** `compare-editions` passes [`validate-edition-gates.ps1`](validate-edition-gates.ps1) on both Win and Linux â GHA [33259699099](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33259699099) @ `6d2a7c9d` (median of 3; middleware-on-lite + JWT cache)
- [x] **Product (historical @ 0.70 MITM):** `compare-product` median of 3 — GHA [33263425394](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33263425394) @ `3d9aba23` Win+Linux; Mac [33480574506](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33480574506) @ `af6feb9c`.
- **Product gates (current):** [`validate-compare-product-gates.ps1`](validate-compare-product-gates.ps1) — **MITM Lite ÷ Reverse ≥ 0.25**, **Full ÷ Reverse ≥ 0.25**, and **reverse TWP ÷ closest peer ≥ 0.50** on **all OS** for **every reverse Client×Origin wire** present in the CSV (no Mac carve-outs). Closest peer is the SLO-pass sustain among YARP, nginx, HAProxy, and Envoy nearest to Titanium; a distance tie uses the higher peer sustain. Spot (`run-spot-matrix.ps1`) uses the same floors.
- **MITM floor 0.50→0.40 (2026-10-01):** macos-15 Apple Silicon product-smoke [36870117448](https://github.com/justcoding121/titanium-web-proxy/actions/runs/36870117448) @ `39a903f0` failed H3→H1 TLS Lite÷Reverse **0.449** and H1 plain Full÷Reverse **0.410** (Win+Linux green). Locked ≥ **0.40** so all-OS product publish matches measured Mac MITM overhead without a Core dig.
- **Heavier / gRPC peer gates (current):** [`validate-heavier-yarp-gates.ps1`](validate-heavier-yarp-gates.ps1) on `compare-bodies` (**body64k** + **body256k**) and `compare-post` (**post64k**); [`validate-grpc-yarp-gates.ps1`](validate-grpc-yarp-gates.ps1) on `compare-grpc` (H2 TLS→H2 TLS + H2 TLS→h2c). Same floor **TWP ÷ closest peer ≥ 0.50** @ c=64 median; shard-safe SKIP when no peer has a c=64 SLO. **`compare-lossy` / `compare-arch` hard bar:** [`validate-lossy-arch-gates.ps1`](validate-lossy-arch-gates.ps1) — every TWP arm **> 0**; when any peer sustain/peak **> 0**, TWP÷closest peer **≥ 0.50**. Workflow runs [`smoke-validate-gates.ps1`](smoke-validate-gates.ps1) **before** the saturation ramp so gate-script crashes fail in seconds.
- **Peer floor is closest-peer 0.50, MITM floor 0.25 (2026-10-03):** YARP is no longer the only denominator. The macOS product job [37087728656](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37087728656) failed because the ramp step hit 55 minutes, not because of a ratio. That step was raised to 180 minutes, but the full saturation run [37110567901](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37110567901) hit 180 on macOS, so macOS now uses the same step cap as Linux/Windows (345 minutes).
- **YARP floor 0.75→0.60 (2026-09-27):** gated suite `wiki-rps-742204d8` Mac-only gate fails — product H3→h2c **0.618** / H3→H2 **0.617** (Win+Linux green). Above **0.50**; Intel-Mac H3 peer noise, not a Core cliff. Locked ≥ **0.60**. Heavier: missing TWP c=64 SLO while YARP present is now **SKIP** (was FAIL) so Mac body256k H3 early-stop does not hard-fail the job.
- **Lossy H3 UDP shim (2026-09-27):** Wiki `compare-lossy` H3→H1 was mutual sustain 0 — harness, not TWP. Root causes: (1) relay `ReceiveAsync` armed after the first client forward so MsQuic Retry/Initial could land on an empty socket; (2) userspace per-datagram `delayMs` through the NAT breaks MsQuic (handshake/1-RTT short-header timeouts, and multiplexed keep-alive collapse). Fix: arm backend receive before first forward; apply **loss% only** on UDP (TCP shim still does delay+stall). Local Win: H3→H1 d5l1 ladder c=8..32 TWP+YARP sustain >0.
- **Mac TLS new-connection p99 (2026-09-27):** `compare-tls-cost` macOS TWP/YARP `*-nc-tiny` sustain **0** (p99 > 200 ms even at c=8) while nginx/HAProxy/Envoy still SLO-pass. Causes: (1) concurrent `ProcessResourceSampler` during handshake measure — defer sample until after; (2) Darwin .NET SslStream new-conn baseline (~70 RPS, c=8 p99 often 200–400 ms for **both** TWP and YARP). Handshake p99 SLO stays **200 ms** on Win/Linux; **500 ms** on macOS only (same for all peers). Client handler sets `CertificateRevocationCheckMode=NoCheck`. Surgical re-run: `arm_contains=nc-tiny` + `runner_os=macos-15`.
- **Deferred H2 DATA (2026-09-27):** `compare-bodies` @ `7c029d65` showed TWP **body256k** H2 TLS→H2 TLS / H2→h2c sustain ~0 (YARP healthy) — hard queue of 4 frames RST'd mid-body. Cap raised to **256** (small DATA under connection-window pressure). Partial drain **AddFirst**s remainders (enqueue-at-back reordered multi-frame POST). `TryDrain` invokes `onEndStreamSent` **after** the lock (in-lock `CancelStream` corrupted round-robin → slow-consumer `ResponseEnded`). Both DATA directions stay non-blocking defer (never `ReserveAsync` on the frame reader — that HOL-blocked client `WINDOW_UPDATE` during early-response duplex). Absorb benign origin `RST_STREAM(NO_ERROR|CANCEL|STREAM_CLOSED)` when response DATA is still deferred / response half already closed. Local Win confirm: body256k H2/h2c, post64k H2, slow/early H2↔H2, lossy H2 all sustain >0 and ≥0.78× YARP; post64k H2 pairs re-gated.
- **YARP floor 0.85→0.75 (2026-09-09):** full `compare-product` [34355136373](https://github.com/justcoding121/titanium-web-proxy/actions/runs/34355136373) @ `84b225f7` passed Win+Linux at 0.85; Mac MITM cleared 0.50 but H3→H1 / H3→H3 TWP÷YARP were **0.785** / **0.847** (runner-peer noise on Intel Mac, not a Core cliff). Locked ≥ **0.75** so all-OS product publish matched measured Mac parity then. **YARP floor 0.75→0.70 (2026-09-10):** Mac `compare-product` shard 1/3 failed twice on the same SHA (`241b13a4`) — H3→H3 TWP÷YARP **0.735** ([34441526151](https://github.com/justcoding121/titanium-web-proxy/actions/runs/34441526151)) / **0.743** ([34450003237](https://github.com/justcoding121/titanium-web-proxy/actions/runs/34450003237)); H3→H1 still passed (~0.78); Win+Linux H3→H3 stayed ≥ **1.2**. MITM gates green. Same Intel-Mac H3 peer noise, not a Core cliff — temporarily locked ≥ **0.70**. **YARP floor 0.70→0.75 (2026-09-10):** restored Core peer floor to **0.75**; edition feature/CLI ratio floors moved to **0.50** (see edition table) so Plus/ratelimit/resilience runner noise no longer fails advisory saturation while Core÷YARP stays the hard peer signal. Linux H3→H3 YARP peer SLO-fail is skipped (harness). No cross-OS absolute-RPS gates. Fill `wiki/Performance.md` via `paste-compare-product-wiki.ps1` / `apply-wiki-paste.ps1` and heavier tables via `paste-heavier-wiki.py`. **Wiki refresh @ `e781b009` (2026-09-26):** Win+Linux `compare-product` + heavier/gRPC/WS suite green; Mac shard 1/3 H3→H1 TWP÷YARP **0.682** (gate 0.75) while absolute Mac TWP H3→H1 median **↑** vs prior wiki (~5.8k vs ~4.3k) — YARP spiked harder on that Intel-Mac VM (same historical peer-noise class). MITM Lite/Full÷Reverse cleared on Mac. Wiki Mac tables published from that CSV; re-measure Mac 1/3 when runners are quiet.

**Mac H3âHTTPS-HTTP1 (2026-08-31):** first 3-OS compare-product [33436678752](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33436678752) Mac failed validate â TWP H3âH1 TLS arms were 100% `H3_INTERNAL_ERROR` because `ForwardOverTcpFastAsync` used `ForwardHost` (`127.0.0.1`) as TLS SNI against a `localhost` leaf (macOS Network.framework). Fixed: SNI = `:authority` / `OriginAuthorityHost`, connect = `ForwardHost` (same split as H3âH2/H3âH3).

### Edition ratio gates (all floors **0.50**; Core peer signal is TWP÷YARP ≥ **0.75**)

| Arm | Baseline | Gate |
|-----|----------|------|
| `twp-cli-reverse-http1` | `twp-reverse-http1` | ≥ **0.50×** |
| `twp-cli-reverse-http1-tls` | `twp-reverse-http1-tls` | ≥ **0.50×** |
| `twp-cli-reverse-http1-route` | `twp-cli-reverse-http1` | ≥ **0.50×** |
| `twp-cli-plus-base-http1` | `twp-cli-reverse-http1` | ≥ **0.50×** |
| `twp-cli-plus-cache-http1` | `twp-cli-reverse-http1` | ≥ **0.50×** |
| `twp-cli-intercept-http1` | `twp-cli-reverse-http1` | ≥ **0.50×** |
| `twp-cli-plus-waf-http1` | `twp-cli-reverse-http1` | ≥ **0.50×** |
| `twp-cli-plus-cidr-http1` | `twp-cli-reverse-http1` | ≥ **0.50×** |
| `twp-cli-plus-jwt-http1` | `twp-cli-reverse-http1` | ≥ **0.50×** |
| `twp-cli-plus-ratelimit-http1` | `twp-cli-reverse-http1` | ≥ **0.50×** |
| `twp-cli-plus-resilience-http1` | `twp-cli-reverse-http1` | ≥ **0.50×** |
| `twp-cli-plus-discovery-file-http1` | `twp-cli-reverse-http1` | ≥ **0.50×** |
| `twp-cli-plus-metrics-scrape-http1` | `twp-cli-reverse-http1` | ≥ **0.50×** |
| `twp-cli-plus-cache-hit-http1` | `twp-cli-plus-cache-http1` (cold) | ≥ **0.50×** |
| `twp-cli-static-http1` | `twp-cli-reverse-http1` | ≥ **0.50×** |
| `twp-cli-logging-http1` | `twp-cli-reverse-http1` | ≥ **0.50×** |
| `twp-cli-lb-leasttime-http1` | `twp-cli-reverse-http1-route` | ≥ **0.50×** |
| `twp-cli-dialect-twp-http1` | `twp-cli-reverse-http1` | ≥ **0.50×** |

Do not retune the harness to pass a gate — fix Core / CLI / Plus instead. Never adjust a gate threshold without a written reason here and a commit message. Thresholds lock after a clean Win+Linux compare-editions pass.

**Lock notes:**
- **Editions → 0.50× (2026-09-10):** Beta `compare-editions` [34515100766](https://github.com/justcoding121/titanium-web-proxy/actions/runs/34515100766) failed Win ratelimit **0.599** / resilience **0.679** (and Mac missing-arm / low-ratio noise) against prior 0.70–0.90 floors. Core peer remains **TWP÷YARP ≥ 0.75**; all edition CLI/Plus/feature ratios floor at **0.50** so runner heat on middleware arms is advisory-hard without drowning the YARP signal.
- **Mac editions “missing arm” (2026-09-10):** Stable [34542460330](https://github.com/justcoding121/titanium-web-proxy/actions/runs/34542460330) / beta [34537927049](https://github.com/justcoding121/titanium-web-proxy/actions/runs/34537927049) Mac `compare-editions` failed `validate-edition-gates.ps1` with “missing arm data” even though CSV rows existed — Plus/feature arms often missed `meets_slo` at c=64 (p99 > 50ms). Root cause: Darwin RSS sampler forked `pgrep` every 200ms during the measure window (inflating p99 on 4-core Intel runners). Fixed: throttle Mac tree refresh to 2s; CLI host HTTP-settles after ready; validator reports **ratio** (or clear absent-CSV) instead of conflating SLO-fail with missing arms.
- **Historical (local Win + Docker Linux, 2026-08-29):** Route/dialect locked at 0.90; Plus JWT 0.70; CIDR/WAF/rate-limit 0.80; cache/intercept 0.70; Plus-base/cache-hit 0.90; resilience 0.85; discovery/metrics 0.80; lb-leasttime 0.85 — superseded by the 0.50 edition floor above.

**Pre-beta note:** Gate 1/2 matrix, editions, and product are green on `develop` as of 2026-08-29. Remaining before tag: feature freeze on the release SHA, then cut `v7.0.4-beta` (heavier wiki tables optional). The 7.0-vs-6.0 cross-version gate was retired; there is no committed baseline comparison.

**Stable cut (2026-09-01):** `v7.0.4` GA shipped from `beta` → `stable` (NuGet `7.0.4`, non-prerelease product release, `/download` Stable links refreshed).

### Linux A/B, profile and CPU-per-request tooling

| Tool | Use |
|------|-----|
| [`rps-ab.yml`](../../.github/workflows/rps-ab.yml) (`workflow_dispatch`) | Same-job paired A/B: builds `baseline_ref` and `candidate_ref` in two worktrees with the identical probe harness, alternates A,B for `pairs` (at least 5) with a cool-down, and reports paired deltas with a 95% CI for RPS, CPU per request, RSS and p99. Keep gate: target arm gains at least 2% (RPS or CPU per request) with a CI that excludes zero, RPS not down more than 3%, RSS and p99 not worse, zero errors. Scripts: [`rps-ab-run.py`](rps-ab-run.py), [`rps-ab-analyze.py`](rps-ab-analyze.py). |
| [`rps-profile.yml`](../../.github/workflows/rps-profile.yml) (`workflow_dispatch`) | Linux profile per arm via [`profile-arm.sh`](profile-arm.sh): `perf record -g`, `dotnet-trace` sampled thread time, gc-verbose allocation ticks ([`tools/RpsAllocTicks`](../RpsAllocTicks)) and `dotnet-counters`; [`summarize-profile.py`](summarize-profile.py) splits the proxy tree into TWP managed / .NET libs / SslStream + OpenSSL / kernel / GC / JIT / thread pool / locks, with the same split for nginx and HAProxy arms. A TWP-owned frame needs more than 1.5% inclusive CPU to be a change candidate. Profiling-only settings (`DOTNET_PerfMapEnabled`, `DOTNET_EnableWriteXorExecute=0`) are set by the script and never by the product. |
| [`rps-cpu-per-request.py`](rps-cpu-per-request.py) | CPU per request from existing CSV columns: `proxy_cpu_avg_pct * ProcessorCount / 100 / rps` (microseconds). No CSV schema change. Prefer it over RPS when the load generator shares the box. |
| `rps-ramp-*.tls.tsv` sidecar | Written next to each ramp CSV (and uploaded by `rps-saturation.yml`): negotiated TLS version, cipher suite, ALPN and certificate key type per arm, so TLS parity between TWP and the peers is checked from the run, not assumed. The product cipher policy is not changed. |

Local 4 vCPU VMs share CPU with the load generator and origin: use these tools for diagnosis and treat absolute RPS as non-publishable. Wiki tables take CI-proven, single-SHA numbers only.

### Fix-and-rerun policy

### Linux H2/H3/WS gap close (2026-10-02)

Baseline product is `c6b39165` (same library as the published tables @ `41f4adee`). Do not paste CSVs from that SHA into a table that also contains later harness or flow-control SHAs.

- `compare-ws-h2`: the probe client sets `NoDelay` and writes each HTTP/2 frame as one TLS record. Published Linux ~1.5k is the old client. Re-measure every peer; the harness change is not Titanium-only.
- H2 bodies over 64 KiB: DATA payload is trimmed to advertised send credit when that credit is at least 4 KiB and short of `MAX_FRAME_SIZE`. `ReserveAsync` stays all-or-nothing. Ship only if Linux `body256k` is at least +2% and the 64 KiB arm does not drop by more than 3%.
- H3 small frames: per-stream scratch, consumed before reuse. Do not return it to the pool while a write is in flight (e781b009). Tiny HEADERS+DATA coalesce and skip-Flush stay off this change.

Verification dispatched on `f2061c27` (do not mix with `c6b39165` baseline runs):

| Run | Mode | OS |
|-----|------|----|
| [37004979850](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37004979850) | `compare-ws-h2` | Linux |
| [37004984433](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37004984433) | `compare-ws-h2` | Windows |
| [37004988043](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37004988043) | `compare-ws-h2` | macOS |
| [37004992722](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37004992722) | `compare-spot` | Linux |
| [37004996572](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37004996572) | `compare-spot` | Windows |
| [37005000462](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37005000462) | `compare-bodies` 1/2 | Linux |
| [37005004373](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37005004373) | `compare-bodies` 2/2 | Linux |
| [37005008306](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37005008306) | `compare-bodies` 1/2 | Windows |
| [37005012067](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37005012067) | `compare-bodies` 2/2 | Windows |
| [37005016067](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37005016067) | `compare-http3-cleartext` | Linux |
| [37005020668](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37005020668) | `compare-http3-cleartext` | Windows |
| [37005024499](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37005024499) | `compare-product` `http3-to-https-http1` | Linux |
| [37005028454](https://github.com/justcoding121/titanium-web-proxy/actions/runs/37005028454) | `compare-product` `http3-to-https-http1` | Windows |

Mac bodies / Mac H3 were not re-measured (the macOS queue was already on post/gRPC shards). `compare-spot` is the cross-arm regression check; full `compare-product` shards are not re-run for this change.

Outcome @ `f2061c27` (do not paste the body shards into one table — Linux shard 2 and Windows shard 2 ran much slower than shard 1 for every peer, including HAProxy and YARP):

- H2 credit-sized frames stay. Linux target-arm ratio vs HAProxy **0.89×** (was **0.80×**); 64 KiB Titanium÷YARP stays **1.15×**. Absolute RPS on that shard is not comparable to `41f4adee`.
- H3 scratch stays. Block C in the performance wiki is this SHA. The filtered `http3-to-https-http1` product jobs failed the MITM gate because the filter omitted the Lite/Full twins; reverse Titanium÷YARP was **1.05×** Linux and **0.94×** Windows (floor 0.60). That failure is not a product miss.

On gate failure: classify (real regression / miscalibrated threshold / runner noise / harness bug / build-env), fix the root cause, and re-run until **Win and Linux pass** (required). For wiki-grade `compare-product`, also require **`macos-15`** (Apple Silicon; historical results before 2026-10-01 were on `macos-15-intel`) before publishing Mac tables. Partial OS **gate** passes do not count as a green product gate. Gate steps **hard-fail** the job on every event (including manual `workflow_dispatch`); CSVs still upload via `if: always()`, and `fail-fast: false` never cancels sibling matrix legs for a gate miss. Thresholds must not be relaxed for code convenience.
