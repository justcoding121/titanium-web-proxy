# Perf gate checklist (7.0)

Run on the release SHA via [`.github/workflows/rps-saturation.yml`](../../.github/workflows/rps-saturation.yml) (manual `workflow_dispatch`).

## Gate 1 (after Core route wire)

- [x] Probe config: routes **unset** / null `ReverseProxy` (zero-cost default)
- [x] Plus / Inspector DLLs **not** loaded by the probe process
- [ ] Full matrix (ubuntu+windows, compare modes) — fail if RPS drops >5% or RSS rises >10% vs last GHA median
- [ ] Additional: single-route table ≡ ForwardHost within **2%** RPS of pure ForwardHost on the same build

**Runs (2026-08-28, `develop` @ `d1e0c65c`):**

| Run | Mode | Status | URL |
|-----|------|--------|-----|
| Gate 1 terminate | `compare-terminate` | **success** | https://github.com/justcoding121/titanium-web-proxy/actions/runs/33151234059 |
| Gate 1 matrix | `compare-matrix` | in progress | https://github.com/justcoding121/titanium-web-proxy/actions/runs/33151235741 |

Terminate smoke (peak RPS; routes unset): TWP H1 TLS win **34273**, ubuntu **24171** — ahead of YARP on H1/H2c arms in that run.

## Gate 2 (before `v7.0.0` stable tag)

- [ ] Same unset-routes probe matrix as gate 1
- [ ] Plus / Inspector DLLs still absent from probe

Do not retune the harness to pass a gate — fix Core instead.
