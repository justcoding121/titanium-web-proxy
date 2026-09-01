# Perf gate checklist (7.0)

Run on the release SHA via [`.github/workflows/rps-saturation.yml`](../../.github/workflows/rps-saturation.yml) (manual `workflow_dispatch`).

Product version is **`7.0.0.0`**; gates block beta/stable tags, not feature commits.

## Branch policy + CI gates (beta / stable)

**PR-only merges into `beta` / `stable`** (no direct pushes). Required status checks before merge:

| Check | Role |
|-------|------|
| `.NET / build` | Unit + E2E + Windows Headless/Visual/Plus Playwright |
| `.NET / ui-portable` (Win/Linux/macOS) | Portable E2E-UI + Headless + Visual + Plus Playwright |
| `RPS saturation / rps` | **`compare-spot`** on PRs into beta/stable |

**Publish / release SHA gates** (after merge):

| Event | RPS mode | Blocks |
|-------|----------|--------|
| Push to `beta` / `stable` (NuGet `publish`) | `compare-editions` via `.NET / rps-publish-gate` **and** `compare-spot` via `.NET / rps-peer-gate` (parallel; Core÷YARP + MITM÷Reverse @ c=64) | NuGet publish |
| Tag `v*` product release | `compare-product` (manual / release workflow) | GitHub Release product assets |

`rps-peer-gate` re-checks Core vs YARP on the merge SHA so a uniform Core slowdown cannot hide behind green edition ratios. It runs in parallel with editions, so publish wall clock stays ~max(editions ≈60m, spot ≈10–20m).

Do **not** run full `compare-product` on every develop PR. Thresholds change only with written rationale here + commit — never loosen gates silently to go green.

## Tiered cadence (when to run which mode)

| Tier | When | Mode | Wall-clock |
|------|------|------|------------|
| Daily / develop PR | feature commits (advisory) | `compare-spot` ([`run-spot-matrix.ps1`](run-spot-matrix.ps1)) | minutes |
| Milestone / investigation | before merge to main | `compare-terminate` or `compare-matrix` | ~1–2h |
| Editions | after CLI/Plus changes | `compare-editions` + [`validate-edition-gates.ps1`](validate-edition-gates.ps1) | ~60 min |
| Beta / stable publish | push to `beta`/`stable` | `compare-editions` + parallel `compare-spot` ([`run-spot-matrix.ps1`](run-spot-matrix.ps1)) | ~60 min wall |
| Cross-version (Gate 2) | before `v7.0.0` tag | `compare-cross-version` + [`validate-cross-version.ps1`](validate-cross-version.ps1) | ~1–2h |
| Release / wiki refresh | release SHA | `compare-product` (median of 3) on `ubuntu-latest` + `windows-latest` + `macos-15-intel` | ~3–4h |
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

- [x] Same unset-routes probe matrix as gate 1 — GHA [33263427055](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33263427055) `compare-matrix` both OS **success** @ `3d9aba23`
- [x] Plus / Inspector DLLs still absent from probe library path (`RpsLoadProbe` references Core only)
- [x] **Cross-version:** GHA [33270571908](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33270571908) @ `0ef6d4dd` both OS **success** (RSS floor **1.20**; peer-norm ≥ **0.90** or current TWP÷YARP ≥ **0.90**). Prior Win fail [33263428508](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33263428508) was YARP spike + RSS noise on H1→h2c / H3.
- [x] **Editions:** `compare-editions` passes [`validate-edition-gates.ps1`](validate-edition-gates.ps1) on both Win and Linux — GHA [33259699099](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33259699099) @ `6d2a7c9d` (median of 3; middleware-on-lite + JWT cache)
- [x] **Product:** `compare-product` median of 3 — GHA [33263425394](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33263425394) @ `3d9aba23` Win+Linux **success**; MITM÷Reverse ≥ 0.70 and reverse TWP÷YARP ≥ 0.95 (Linux H3→H3 YARP peer SLO-fail skipped — harness, not TWP). Wiki Win/Linux tables refreshed.
- [ ] **Product (macOS Intel):** same `compare-product` matrix leg on `macos-15-intel` (4-core / 14 GB; nginx `http_v3_module` + MsQuic + YARP). MITM÷Reverse ≥ **0.70** (non-H3) and H3→H1 TWP÷YARP ≥ **0.95** (same as Win/Linux). **H3→H3 MITM floor = 0.69** and **H3→H3 TWP÷YARP floor = 0.75** when YARP SLO-passes ([`validate-compare-product-gates.ps1`](validate-compare-product-gates.ps1)): Homebrew MsQuic first baseline — H3→H3 Full median landed at 0.693 with high repeat variance; YARP H3→H3 SLO-passes on Mac while TWP sits ~0.78× (Win often has TWP ahead; Linux YARP H3→H3 SLO-fails and skips). Written floors — not a silent loosen of H3→H1 / non-H3 MITM. No cross-OS absolute-RPS gates. Fill `wiki/Performance.md` Mac Reverse + MITM via `paste-compare-product-wiki.ps1` / `apply-wiki-paste.ps1`.

**Mac H3→HTTPS-HTTP1 (2026-08-31):** first 3-OS compare-product [33436678752](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33436678752) Mac failed validate — TWP H3→H1 TLS arms were 100% `H3_INTERNAL_ERROR` because `ForwardOverTcpFastAsync` used `ForwardHost` (`127.0.0.1`) as TLS SNI against a `localhost` leaf (macOS Network.framework). Fixed: SNI = `:authority` / `OriginAuthorityHost`, connect = `ForwardHost` (same split as H3→H2/H3→H3).

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

**Pre-beta note:** Gate 1/2 matrix, editions, cross-version, and product are green on `develop` as of 2026-08-29. Remaining before tag: feature freeze on the release SHA, then cut `v7.0.3-beta` (heavier wiki tables optional).

### Fix-and-rerun policy

On gate failure: classify (real regression / miscalibrated threshold / runner noise / harness bug / build-env), fix the root cause, and re-run until **Win and Linux pass** (required). For wiki-grade `compare-product`, also require **`macos-15-intel`** before publishing Mac tables. Partial OS passes do not count. When watching a parallel matrix: cancel remaining siblings after the first job failure, fix that failure, then re-dispatch. Cross-version thresholds (0.95 / 1.10) match the same-version gate and must not be relaxed for code convenience.
