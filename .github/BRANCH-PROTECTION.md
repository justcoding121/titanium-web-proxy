# Branch protection (beta / stable)

Configure in GitHub **Settings → Rules → Rulesets**. Classic branch protection is not used. `develop` has no required checks: direct push stays allowed.

The only list of check contexts is [`.github/required-checks.json`](required-checks.json). [`.github/branch-ruleset-beta-stable.json`](branch-ruleset-beta-stable.json) is the ruleset body for `release-lines` (ruleset id `24311415`). [`.github/workflows/required-checks.yml`](workflows/required-checks.yml) fails when those two files disagree, or when a context does not match a workflow job. On a schedule it also compares the live rules for `beta` and `stable`.

## Ruleset: `release-lines` (`beta`, `stable`)

| Rule | Value |
|------|-------|
| Restrict updates / deletions | yes |
| Block force pushes | yes |
| Require a pull request before merging | **yes** (1 approval, stale reviews dismissed, threads resolved) |
| Require status checks to pass | **yes**, strict |
| Required checks | `merge-gate`, `rps (ubuntu-latest)`, `rps (windows-latest)`, `rps (macos-15)`, `gitleaks`, `dependency-review`, `tests-or-waiver`, `guard-nc-paths`, `cla` |
| Code scanning | CodeQL, high-or-higher security alerts, error alerts |
| Admin bypass | repository admin role, **pull request only** (no direct push) |

`merge-gate` is the single `.NET` context. It passes when `build`, `redis-fleet`, `ui-portable` (all three OS), and `cli-e2e` (all three OS) succeed. A docs-only pull request skips those four jobs and `merge-gate` still reports success, so the ruleset does not wait forever. `build`, `ui-portable (...)`, and `cli-e2e (...)` are not required contexts: `Deploy website` also has a job named `build`.

Apply after a promotion pull request has shown `merge-gate` green:

```bash
gh api -X PUT repos/justcoding121/titanium-web-proxy/rulesets/24311415 --input .github/branch-ruleset-beta-stable.json
```

## Release ladder

1. **Pull request into `beta` or `stable`.** The ruleset above, plus the CodeQL rule. `rps` runs `compare-spot` on three OS.
2. **Push to `beta` or `stable`.** `.NET` runs `merge-gate`, then `rps-publish-gate` (edition comparison) and `rps-peer-gate` (spot matrix) on the merge commit. Those push jobs are the evidence on the release SHA, because pull-request checks attach to the PR head, not the merge commit. `cut-product-tag` then dispatches `release.yml`. The six readiness workflows (`conformance`, `soak`, `kubernetes-smoke`, `inspector-unattended`, `fuzz-nightly`, `dast`) also run on that push.
3. **`release.yml`.** `release-readiness` waits for those six workflows on the same SHA. `gate-evidence` records whether the push `.NET` run already passed `merge-gate`, `rps-publish-gate`, and `rps-peer-gate`. `test` and `cli-e2e` run only when that evidence is missing. `release-gate` then unlocks the CLI, Plus, and Inspector builds. `smoke-http3` and `smoke-scripting` must pass. `rps-product-smoke` runs on stable only. `publish-release` creates the GitHub Release. `publish-nuget` runs only after that. Images, Chocolatey, charts, and the image scan follow.

`publish-charts.yml`, `chocolatey-publish.yml`, and `packaging-catalog-smoke.yml` are manual fallbacks. They are not a second release pipeline.

## `develop`

Keep lighter rules. Do not require `compare-product` on a feature pull request. Advisory checks (not enforced) are `merge-gate`, `gitleaks`, `dependency-review`, `tests-or-waiver`, `guard-nc-paths`, and `cla`.
