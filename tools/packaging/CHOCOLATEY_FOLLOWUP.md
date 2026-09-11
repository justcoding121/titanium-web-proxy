# Chocolatey follow-up

Community packages:

- `titanium-cli` — win-x64 zip (`titanium` / `twp`)
- `titanium-inspector` — win-x64 MSI

Stubs: [`chocolatey/`](chocolatey/). Bump from a release tag:

```powershell
pwsh ./tools/packaging/chocolatey/bump-packages.ps1 -Tag v7.0.5
```

## Secret

| Secret | Purpose |
| --- | --- |
| `CHOCOLATEY_API_KEY` | API key for `https://push.chocolatey.org/` |

```shell
gh secret set CHOCOLATEY_API_KEY --repo justcoding121/titanium-web-proxy
```

Do not paste the key into git, docs, or workflow YAML. CI passes `--api-key` to `choco push` only.

## Publish

**Automatic on merge to `beta` / `stable`:**

1. [`dotnetcore.yml`](../../.github/workflows/dotnetcore.yml) `cut-product-tag` creates `v…` / `v…-beta` and dispatches [`release.yml`](../../.github/workflows/release.yml).
2. `release.yml` builds/signs, creates the GitHub Release, then job `publish-chocolatey` bumps SHA256s and `choco push`es `titanium-cli` + `titanium-inspector` (stable and prerelease).

No extra click after the packaging stubs are on that branch. GitHub Release is created even if Chocolatey push fails.

**Manual** (existing tag only, e.g. first `v7.0.5` before the next beta/stable cut): workflow [`chocolatey-publish.yml`](../../.github/workflows/chocolatey-publish.yml) with input `release_tag`.

First versions wait for chocolatey.org moderation. Users install with:

```shell
choco install titanium-cli
choco install titanium-inspector
choco install titanium-cli --pre
choco install titanium-inspector --pre
```

## Do not

- Use package id `titanium` (unrelated Titanium Studio already exists).
- Put prerelease text in the package id; use version `7.0.5-beta` and `--pre`.
- Embed zip/MSI in the nupkg.
- Push unsigned Windows assets.
- Post chocolatey.org discussion or moderation replies from CI.

## 2026-09-10: push 403 on 7.0.6 / 7.0.6-beta

choco push returned **403 Forbidden** for both packages after 7.0.6-beta / 7.0.6 GitHub Releases succeeded (packs built OK). Re-run via [chocolatey-publish.yml](../../.github/workflows/chocolatey-publish.yml) also 403'd. Likely API key / account permission � rotate CHOCOLATEY_API_KEY on chocolatey.org and gh secret set CHOCOLATEY_API_KEY, then re-dispatch publish for 7.0.6-beta and 7.0.6.

## 2026-09-11: hold Chocolatey for 7.0.7-beta

`v7.0.7-beta` GitHub/NuGet cut intentionally **did not** push Chocolatey. Repo secret `CHOCOLATEY_API_KEY` was removed so `release.yml` `publish-chocolatey` skips. Do not dispatch `chocolatey-publish.yml` until earlier packages clear chocolatey.org moderator review; then restore the secret and push withheld versions in order (7.0.6* before 7.0.7-beta).

## 2026-09-11: hold Chocolatey for 7.0.8-beta

Same hold as 7.0.7-beta: `CHOCOLATEY_API_KEY` remains unset; `v7.0.8-beta` GitHub/NuGet cut intentionally **does not** push Chocolatey. Do not dispatch `chocolatey-publish.yml` until earlier packages clear moderator review.