AI agents author tests, explore, and triage. A pull request passes only when the deterministic checks pass. An agent comment is not a gate.

## Pull request

An agent reads the diff and names missing assertions. The diff-coverage script and the `qa-waiver` label are what fail the pull request. When the diff touches a bridge, terminate path, HTTP/3 tunnel, MASQUE handler, Manager store, or GeoIP command, the same change includes a test that fails if that behavior is reverted.

## Nightly

`.github/workflows/ai-qa.yml` runs a charter against the built CLI: help, an unknown flag, and a config the validator must reject. A failure opens nothing by itself. A person or a follow-up agent turns a confirmed failure into a regression test named with the date, the same way `Regression-2026-10-07` is named.

## Triage

A failed job is classified as a regression, an environment flake, or a harness bug. A test that uses the integration retry twice in a week moves to the `Quarantine` category. It still runs. It does not fail the pull request. The move is a commit, not a silent skip.

## What an agent must not change alone

Coverage floors, `.github/required-checks.json`, and `tests/test-hygiene-allowlist.json` need a pull request that says why. An agent does not set `CONFORMANCE_ENFORCE`, `GATEWAY_CONFORMANCE_ENFORCE`, or branch protection.
