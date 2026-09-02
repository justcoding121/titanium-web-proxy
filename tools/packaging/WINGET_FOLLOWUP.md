# Winget follow-up

Status (2026-09-02): Signed stable [`v7.0.5`](https://github.com/justcoding121/titanium-web-proxy/releases/tag/v7.0.5) cut with Authenticode MSI + AppImages + `SHA256SUMS.asc`.

## Submitted winget PRs

- CLI: https://github.com/microsoft/winget-pkgs/pull/428410
- Inspector (Authenticode MSI): https://github.com/microsoft/winget-pkgs/pull/428421 (supersedes #428411; fixed LicenseUrl)

## Do not

- Resubmit unsigned `7.0.4` or any beta tag to `microsoft/winget-pkgs`.

## After signed stable (`7.0.5+`)

1. Refresh SHA256s in [`winget/`](winget/) from the release `SHA256SUMS`.
2. Open fresh PRs against `microsoft/winget-pkgs` (CLI zip portable + Inspector MSI).
3. Note Authenticode publisher **Jehonathan Thomas** in the PR description.
