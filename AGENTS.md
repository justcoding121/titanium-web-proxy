# Agent rules for this repository

## Before every push that touches CI

Run:

```powershell
pwsh tools/ci/preflight.ps1
```

Use `-Mode workflows` for workflow-only edits, `-Mode build` for product/PublicAPI edits, or the default `-Mode all`.

## Release and promotion PRs

- Never push a fix to a promotion PR head while its `release-dry-run` is still running unless that run has already failed. A push cancels the in-flight dry-run (`cancel-in-progress` on pull_request).
- For infra failures (runner not acquired, lost communication, ENOSPC with no product step), use `gh run rerun <id> --failed` or `gh run rerun <id> --job <job-id>`. Do not push or re-dispatch to clear infra noise.
- Do not delete or bypass `merge-gate`, `release-dry-run`, or `release-readiness`.
- Promotion PR heads should be `release/<version>-to-<channel>` cut from `develop` (or `beta` for a stable promotion of an already-published beta), so ordinary develop merges do not cancel the dry-run concurrency group.
- Before opening a promotion PR, ensure a same-tree green `soak.yml` exists (nightly or `workflow_dispatch`). Tree-evidence shadow mode (WP12) will eventually require this.

## Smoke harness work

While developing a product-smoke leg, dispatch `product-smoke.yml` with `advisory=true` so one run reports every failing leg instead of stopping at the first red matrix cell.

## Branches

- Land CI and product fixes on `develop` unless you are executing a documented promotion into `beta` or `stable`.
- Do not force-push `beta`, `stable`, or an in-flight `release/*` branch.
