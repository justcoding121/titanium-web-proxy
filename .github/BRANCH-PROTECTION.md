# Branch protection (beta / stable)

Configure in GitHub **Settings → Rules → Rulesets** (or classic branch protection).

## Ruleset: `release-lines` (`beta`, `stable`)

| Rule | Value |
|------|-------|
| Restrict creations | optional |
| Restrict updates / deletions | yes |
| Block force pushes | yes |
| Require a pull request before merging | **yes** |
| Require approvals | recommended (≥1 maintainer) |
| Require conversation resolution | recommended |
| Require status checks to pass | **yes** |
| Required checks | `.NET / build`, `.NET / ui-portable` (all 3 OS), `.NET / cli-e2e` (all 3 OS), `RPS saturation / rps` (all 3 OS on PR `compare-spot`) |
| Allow bypass | admins only for emergencies |

Exact check names must match the Actions job names as shown on a PR (including matrix suffixes). The checked-in template is [`.github/branch-ruleset-beta-stable.json`](branch-ruleset-beta-stable.json) — after the first green PR, confirm the matrix suffixes in the Actions UI and adjust the JSON if GitHub renamed a leg.

## `develop`

Keep lighter rules for velocity (direct push OK if that is current practice). Do **not** require full `compare-product` on every feature PR.

## Applying via `gh` (optional)

```bash
# Example — adjust org/repo and exact check names from Actions UI after first green run
gh api repos/{owner}/{repo}/rulesets --input .github/branch-ruleset-beta-stable.json
```

## Stable release RPS

After merge to `stable`, [`release.yml`](workflows/release.yml) runs Linux **`compare-product-smoke`** before the GA GitHub Release. Full 3-OS wiki-grade `compare-product` stays a maintainer `workflow_dispatch` on [`rps-saturation.yml`](workflows/rps-saturation.yml).
