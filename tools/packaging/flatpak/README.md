# Flathub packaging (Titanium Inspector)

Flatpak manifests live here for local smoke builds and a future **human-led** Flathub listing of **Inspector only**.

## Eligibility

| App | Flatpak ID | Flathub |
| --- | --- | --- |
| Inspector | `io.github.justcoding121.TitaniumInspector` | Allowed as a desktop app (subject to review) |
| CLI | `io.github.justcoding121.TitaniumCli` | **Not accepted** — [console software](https://docs.flathub.org/docs/for-app-authors/requirements#console-software) |

Ship CLI via GitHub Releases, Homebrew (`justcoding121/titanium`), and winget — not Flathub.

## v7.0.5 submission outcome (2026-09-02)

Both first-listing PRs were **closed** by Flathub reviewers:

- Inspector: https://github.com/flathub/flathub/pull/10054 (`duplicate` / `spam` / `AI Slop`)
- CLI: https://github.com/flathub/flathub/pull/10055 (same + `blocked`; console software)

Reviewer feedback in short:

1. Demo videos did not show real Flatpak usage on Linux.
2. Checklist items must be satisfied (video is required — not “N/A until bot build”).
3. Do not spam `bot, build`.
4. Submissions must follow the [generative AI policy](https://docs.flathub.org/docs/for-app-authors/requirements#generative-ai-policy): **do not** open, edit, or reply on Flathub PRs via AI agents; PR body / replies must be written by the maintainer.

Do **not** reopen Flathub PRs from automation. Any retry is a **manual** maintainer action after trust / media issues are fixed.

## Before a human re-submit (Inspector only)

1. Build and install the Flatpak locally on a real Linux desktop (`flatpak-builder` / smoke workflow).
2. Record a clear screencast of **the Flatpak** running (launch UI, proxy a request, show useful workflow) — not a zoomed still / unrelated clip.
3. Fill the Flathub checklist yourself; leave no placeholder “N/A” on required items.
4. Open the submission PR yourself (no agent). Keep replies short and human.
5. Manifest / metainfo / finish-args in this directory are the starting point (`wayland` + `fallback-x11`, no `--filesystem=home`).

App-ID companion repos (for `appid-url-not-reachable`):

- https://github.com/justcoding121/TitaniumInspector
- https://github.com/justcoding121/TitaniumCli (kept for ID consistency; not for Flathub listing)

## Local build smoke

```shell
flatpak-builder --user --install --force-clean /tmp/ti-build \
  tools/packaging/flatpak/io.github.justcoding121.TitaniumInspector.yml
flatpak run io.github.justcoding121.TitaniumInspector
```

Optional CI: [`.github/workflows/packaging-catalog-smoke.yml`](../../.github/workflows/packaging-catalog-smoke.yml) (`run_flatpak=true`).

## Later versions (after a successful first listing)

Manifests include `x-checker-data` for flathub-external-data-checker. After each stable cut, bump `url` + `sha256` in the Flathub app repo (or let the checker open a PR).
