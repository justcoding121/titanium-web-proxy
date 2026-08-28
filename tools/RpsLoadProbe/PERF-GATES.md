# Perf gate checklist (7.0)

Run on the release SHA via [`.github/workflows/rps-saturation.yml`](../../.github/workflows/rps-saturation.yml) (manual `workflow_dispatch`).

## Gate 1 (after Core route wire)

- [ ] Probe config: routes **unset** / null `ReverseProxy` (zero-cost default)
- [ ] Plus / Inspector DLLs **not** loaded by the probe process
- [ ] Full matrix (ubuntu+windows, compare modes) — fail if RPS drops >5% or RSS rises >10% vs last GHA median
- [ ] Additional: single-route table ≡ ForwardHost within **2%** RPS of pure ForwardHost on the same build

## Gate 2 (before `v7.0.0` stable tag)

- [ ] Same unset-routes probe matrix as gate 1
- [ ] Plus / Inspector DLLs still absent from probe

Do not retune the harness to pass a gate — fix Core instead.
