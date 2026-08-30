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
| Required checks | `.NET / build`, `.NET / ui-portable` (all matrix legs), `RPS saturation / rps` |
| Allow bypass | admins only for emergencies |

## `develop`

Keep lighter rules for velocity (direct push OK if that is current practice). Do **not** require full `compare-product` on every feature PR.

## Applying via `gh` (optional)

```bash
# Example — adjust org/repo and exact check names from Actions UI after first green run
gh api repos/{owner}/{repo}/rulesets --input .github/branch-ruleset-beta-stable.json
```

Exact check names must match the Actions job names as shown on a PR (including matrix suffixes for `ui-portable`).
