# Perf gate checklist (7.0)

Run on the release SHA via [`.github/workflows/rps-saturation.yml`](../../.github/workflows/rps-saturation.yml) (manual `workflow_dispatch`).

Product version stays **`7.0.0.0`** until the first beta cut; gates block beta/stable tags, not feature commits.

## Tiered cadence (when to run which mode)

| Tier | When | Mode | Wall-clock |
|------|------|------|------------|
| Daily / per-PR | feature commits | `compare-spot` ([`run-spot-matrix.ps1`](run-spot-matrix.ps1)) | minutes |
| Milestone / investigation | before merge to main | `compare-terminate` or `compare-matrix` | ~1–2h |
| Editions | after CLI/Plus changes | `compare-editions` + [`validate-edition-gates.ps1`](validate-edition-gates.ps1) | ~60 min |
| Cross-version (Gate 2) | before `v7.0.0` tag | `compare-cross-version` + [`validate-cross-version.ps1`](validate-cross-version.ps1) | ~1–2h |
| Release / wiki refresh | release SHA | `compare-product` (median of 3) | ~3–4h |
| Heavier wiki tables | as needed | `compare-bodies` / `post` / `lossy` / `arch` / `bridges` / `tls-cost` (independent dispatch) | 30–60 min each |

Do **not** run full `compare-product` as a daily smoke. Prefer TWP÷YARP / TWP÷nginx / edition ratios over absolute RPS. Early-stop (`--stop-on-slo-fail`, default on) aborts an arm after the first SLO fail plus one peak confirmation step.

## Gate 1 (after Core route wire)

- [x] Probe config: routes **unset** / null `ReverseProxy` (zero-cost default)
- [x] Plus / Inspector DLLs **not** loaded by the probe process (library arms); edition arms spawn CLI externally
- [x] Full matrix (ubuntu+windows, `compare-matrix`) — GHA [33151235741](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33151235741) @ `d1e0c65c` **success** both OS. Absolute RPS vs prior medians (`33087091622` / `33087088466`) swings with runner heat (Win down / Linux up); **peer-normalized** TWP÷YARP is the regression signal (same policy as Gate 2 cross-version). A few Win peer-norm cells dipped under 0.90 on that older SHA — re-locked on current `develop` via Gate 2 matrix below.
- [x] Additional: single-route table ≡ ForwardHost within **10%** RPS of pure ForwardHost on the same build (`twp-cli-reverse-http1-route` ÷ `twp-cli-reverse-http1` ≥ **0.90**)

**Runs (2026-08-28, `develop` @ `d1e0c65c`):**

| Run | Mode | Status | URL |
|-----|------|--------|-----|
| Gate 1 terminate | `compare-terminate` | **success** | https://github.com/justcoding121/titanium-web-proxy/actions/runs/33151234059 |
| Gate 1 matrix | `compare-matrix` | **success** | https://github.com/justcoding121/titanium-web-proxy/actions/runs/33151235741 |

Terminate smoke (peak RPS; routes unset): TWP H1 TLS win **34273**, ubuntu **24171** — ahead of YARP on H1/H2c arms in that run.

## Gate 2 (before first beta / `v7.0.0` stable tag)

- [ ] Same unset-routes probe matrix as gate 1 (re-run on the release SHA) — in flight: [33263427055](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33263427055) `compare-matrix` @ `3d9aba23`
- [x] Plus / Inspector DLLs still absent from probe library path (`RpsLoadProbe` references Core only)
- [ ] **Cross-version:** refresh on current tip — in flight: [33263428508](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33263428508) (prior [33253789356](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33253789356) @ `79793303`; RSS floor **1.15** + peer-norm ≥ **0.90**)
- [x] **Editions:** `compare-editions` passes [`validate-edition-gates.ps1`](validate-edition-gates.ps1) on both Win and Linux — GHA [33259699099](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33259699099) @ `6d2a7c9d` (median of 3; middleware-on-lite + JWT cache)
- [ ] **Product:** `compare-product` median of 3 — in flight: [33263425394](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33263425394); gates MITM÷Reverse ≥ 0.70 and reverse TWP÷YARP ≥ 0.95 via [`validate-compare-product-gates.ps1`](validate-compare-product-gates.ps1) (also wired in workflow as of `0cb9a12f`)

### Edition ratio gates (first-run estimates; lock after first clean Win+Linux baseline)

| Arm | Baseline | Gate |
|-----|----------|------|
| `twp-cli-reverse-http1` | `twp-reverse-http1` | ≥ **0.80×** |
| `twp-cli-reverse-http1-tls` | `twp-reverse-http1-tls` | ≥ **0.80×** |
| `twp-cli-reverse-http1-route` | `twp-cli-reverse-http1` | ≥ **0.90×** |
| `twp-cli-plus-base-http1` | `twp-cli-reverse-http1` | ≥ **0.90×** |
| `twp-cli-plus-cache-http1` | `twp-cli-reverse-http1` | ≥ **0.70×** |
| `twp-cli-intercept-http1` | `twp-cli-reverse-http1` | ≥ **0.70×** |
| `twp-cli-plus-waf-http1` | `twp-cli-reverse-http1` | ≥ **0.80×** |
| `twp-cli-plus-cidr-http1` | `twp-cli-reverse-http1` | ≥ **0.80×** |
| `twp-cli-plus-jwt-http1` | `twp-cli-reverse-http1` | ≥ **0.70×** |
| `twp-cli-plus-ratelimit-http1` | `twp-cli-reverse-http1` | ≥ **0.80×** |
| `twp-cli-plus-resilience-http1` | `twp-cli-reverse-http1` | ≥ **0.85×** |
| `twp-cli-plus-discovery-file-http1` | `twp-cli-reverse-http1` | ≥ **0.80×** |
| `twp-cli-plus-metrics-scrape-http1` | `twp-cli-reverse-http1` | ≥ **0.80×** |
| `twp-cli-plus-cache-hit-http1` | `twp-cli-plus-cache-http1` (cold) | ≥ **0.90×** |
| `twp-cli-static-http1` | `twp-cli-reverse-http1` | ≥ **0.85×** |
| `twp-cli-logging-http1` | `twp-cli-reverse-http1` | ≥ **0.90×** |
| `twp-cli-lb-leasttime-http1` | `twp-cli-reverse-http1-route` | ≥ **0.85×** |
| `twp-cli-dialect-twp-http1` | `twp-cli-reverse-http1` | ≥ **0.90×** |

Do not retune the harness to pass a gate — fix Core / CLI / Plus instead. Never adjust a gate threshold without a written reason here and a commit message. Thresholds lock after a clean Win+Linux compare-editions pass.

**Lock notes (local Win + Docker Linux, 2026-08-29):**
- **Route / dialect `.twp` → 0.90×:** Locked at **0.90** (within 10% of ForwardHost). Local Win/Linux measured route ≥0.969× and dialect ≥0.948×.
- **Plus middleware on terminate-lite (2026-08-29):** Pre-origin middleware no longer forces the full `SessionEventArgs` path. Lite populates `ProxyMiddlewareContext` (client IP + request view); deny uses handled status fields. JWT caches successful bearer validations until near `exp` (same token under load skips RS256). Cool paired Win:
  - JWT ≈ **0.78×** (was ~0.51×) → gate **0.70×**
  - CIDR/WAF/rate-limit ≈ **0.88–0.91×** → gate **0.80×**
  - Cache (loopback; fills then hits) ≈ **0.99×** → gate **0.70×** (session + AfterResponse on miss; hits skip origin)
- **Noise / measured floors tightened:**
  - Plus-base **0.90**, cache-hit **0.90**, intercept **0.70**
  - Resilience **0.85** (health probes; measured ~1.0×)
  - Discovery-file **0.80**, metrics-scrape **0.80** vs CLI
  - lb-leasttime stays **0.85×**

**Pre-beta note:** Gate 1 matrix run `33151235741` was still in progress during this parity land; re-check conclusion and medians before cutting beta. Gate 2 is a fresh unset-routes matrix on the release SHA after feature freeze.

### Fix-and-rerun policy

On gate failure: classify (real regression / miscalibrated threshold / runner noise / harness bug / build-env), fix the root cause, and re-run until **both Win and Linux pass**. Partial OS passes do not count. Cross-version thresholds (0.95 / 1.10) match the same-version gate and must not be relaxed for code convenience.
