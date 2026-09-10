# Winget follow-up

Status (2026-09-02): Signed stable [`v7.0.5`](https://github.com/justcoding121/titanium-web-proxy/releases/tag/v7.0.5) cut with Authenticode MSI + AppImages + `SHA256SUMS.asc`.

## Submitted winget PRs (v7.0.5)

- CLI: https://github.com/microsoft/winget-pkgs/pull/428410 — validation green (`Azure-Pipeline-Passed`, `Validation-Completed`); awaiting community moderator approval.
- Inspector (Authenticode MSI): https://github.com/microsoft/winget-pkgs/pull/428421 — same (supersedes #428411; fixed `LicenseUrl`).

CLA: `@microsoft-github-policy-service agree` already recorded; `license/cla` success.

## Do not

- Resubmit unsigned `7.0.4` or any beta tag to `microsoft/winget-pkgs`.
- Open duplicate PRs while the above are still open.

## After merge

1. Verify `winget search Titanium` / `winget show justcoding121.TitaniumCli` and Inspector.
2. For later stables: refresh SHA256s in [`winget/`](winget/) from release `SHA256SUMS`, then open version-bump PRs (not new-package).
3. Note Authenticode publisher **Jehonathan Thomas** when relevant.
