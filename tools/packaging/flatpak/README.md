# Flathub packaging (Titanium Inspector)

Flatpak manifests here are for local smoke builds and a future **human-led** Flathub listing of **Inspector only**.

| App | Flatpak ID | Manifest |
| --- | --- | --- |
| Inspector | `io.github.justcoding121.TitaniumInspector` | [`io.github.justcoding121.TitaniumInspector.yml`](io.github.justcoding121.TitaniumInspector.yml) |

**CLI is not packaged for Flathub** ([console software](https://docs.flathub.org/docs/for-app-authors/requirements#console-software) is not accepted). Ship CLI via GitHub Releases, Homebrew (`justcoding121/titanium`), and winget.

## v7.0.5 attempt (closed)

- Inspector: https://github.com/flathub/flathub/pull/10054 — closed (`spam` / `AI Slop`); demo video / checklist issues
- A CLI Flathub PR was also closed; do not resubmit CLI

Do **not** open Flathub PRs from automation. Retries must be manual and follow the [generative AI policy](https://docs.flathub.org/docs/for-app-authors/requirements#generative-ai-policy).

## Before a human re-submit

1. Build/install the Flatpak locally on a Linux desktop.
2. Record a clear screencast of **that Flatpak** in use (not a still / unrelated clip).
3. Fill the Flathub checklist yourself; no “N/A” on required items; do not spam `bot, build`.
4. Open the PR yourself. finish-args already use `wayland` + `fallback-x11` (no `--filesystem=home`).

App-ID companion repo: https://github.com/justcoding121/TitaniumInspector

## Local build smoke

```shell
flatpak-builder --user --install --force-clean /tmp/ti-build \
  tools/packaging/flatpak/io.github.justcoding121.TitaniumInspector.yml
flatpak run io.github.justcoding121.TitaniumInspector
```

Optional CI: [`.github/workflows/packaging-catalog-smoke.yml`](../../.github/workflows/packaging-catalog-smoke.yml) (`run_flatpak=true`).

## Later versions (after first listing)

Manifest includes `x-checker-data`. After each stable cut, bump `url` + `sha256` in the Flathub app repo (or let flathub-external-data-checker open a PR).
