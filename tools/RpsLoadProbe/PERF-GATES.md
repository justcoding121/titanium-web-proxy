# Perf gate checklist (7.0)

Run on the release SHA via [`.github/workflows/rps-saturation.yml`](../../.github/workflows/rps-saturation.yml) (manual `workflow_dispatch`).

Product version stays **`7.0.0.0`** until the first beta cut; gates block beta/stable tags, not feature commits.

## Gate 1 (after Core route wire)

- [x] Probe config: routes **unset** / null `ReverseProxy` (zero-cost default)
- [x] Plus / Inspector DLLs **not** loaded by the probe process
- [ ] Full matrix (ubuntu+windows, compare modes) — fail if RPS drops >5% or RSS rises >10% vs last GHA median
- [ ] Additional: single-route table ≡ ForwardHost within **2%** RPS of pure ForwardHost on the same build

**Runs (2026-08-28, `develop` @ `d1e0c65c`):**

| Run | Mode | Status | URL |
|-----|------|--------|-----|
| Gate 1 terminate | `compare-terminate` | **success** | https://github.com/justcoding121/titanium-web-proxy/actions/runs/33151234059 |
| Gate 1 matrix | `compare-matrix` | in progress / analyze when green | https://github.com/justcoding121/titanium-web-proxy/actions/runs/33151235741 |

Terminate smoke (peak RPS; routes unset): TWP H1 TLS win **34273**, ubuntu **24171** — ahead of YARP on H1/H2c arms in that run.

**Matrix analysis (when complete):** compare each arm’s peak RPS and RSS to the prior successful matrix median (`33087091622` / `33087088466`). Fail criteria above. Single-route ≡ ForwardHost check uses the same-build ForwardHost arm.

## Gate 2 (before first beta / `v7.0.0` stable tag)

- [ ] Same unset-routes probe matrix as gate 1 (re-run on the release SHA)
- [x] Plus / Inspector DLLs still absent from probe (`RpsLoadProbe` references Core only)

Do not retune the harness to pass a gate — fix Core instead.

**Pre-beta note:** Gate 1 matrix run `33151235741` was still in progress during this parity land; re-check conclusion and medians before cutting beta. Gate 2 is a fresh unset-routes matrix on the release SHA after feature freeze.
