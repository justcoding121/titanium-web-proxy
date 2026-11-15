# Contributing

Thanks for contributing to Titanium Web Proxy.

## Editions

| Paths | License | External PRs |
|-------|---------|--------------|
| `src/Titanium.Web.Proxy/`, `src/Titanium.Web.Proxy.Abstractions/`, `src/Titanium.Web.Proxy.Configuration/`, `src/Titanium.Web.Proxy.Scripting/`, `src/Titanium.Cli/`, `src/Titanium.Manager/`, `src/Titanium.Gateway/`, related MIT tests, examples, tools, deploy, docs, wiki, `website/` | MIT | Welcome after CLA |
| `src/Titanium.Plus/`, `src/Titanium.Inspector/`, `src/Titanium.Xds/`, `src/Titanium.Cli.GeoIp/`, `tests/Titanium.Plus.Tests/`, `tests/Titanium.Inspector.Tests/`, `tests/Titanium.Xds.Tests/`, `tests/Titanium.Cli.GeoIp.Tests/` | PolyForm Noncommercial | Maintainer allowlist only |

See [CLA.md](CLA.md) and the [README License](README.md#license) section.

## Before you open a PR

1. Sign the [CLA](CLA.md) if you have not already.
2. Target the `develop` branch (unless a maintainer directs otherwise).
3. Run `dotnet build src/Titanium.Web.Proxy.sln -c Release` and `dotnet test` for affected test projects.
4. Do not introduce third-party product names of other proxies or traffic debuggers into source, tests, CLI help, or docs.
5. Keep hot-path changes minimal; preserve `ForwardHost` terminate-lite eligibility when routes are unset or equivalent to a single sticky destination.
6. A `yaml` example in `website/docs/*.md` or `wiki/*.md` that is a whole `twp.yaml` is tagged <code>```yaml twp</code>, and `dotnet test tests/Titanium.Web.Proxy.Configuration.Tests` loads, validates, and schema-checks it. A snippet that only makes sense inside a larger file is tagged <code>```yaml twp-fragment</code> and skipped. A wiki page does not start with a `#` heading that repeats its file name.
7. A behavior change lands with the automated test that fails if the behavior is reverted. Run `tools/run-tests.ps1 -Project <name> -Verify` on Windows so a certificate prompt or firewall alert fails the run instead of waiting. Do not write Titanium certificates into the real user or machine store.

### Local QA tooling (maintainers)

Not for end users. Per-OS checklists and on-demand probes live under `tools/`:

- [LOCAL-QA.md](tools/LOCAL-QA.md) — solo checklist (E2E + probes)
- [CliQaProbe](tools/CliQaProbe) — CLI help / dialects / live `run` / optional OS service
- [InspectorDesktopProbe](tools/InspectorDesktopProbe) — system proxy / CA / browser UX
- [RpsLoadProbe](tools/RpsLoadProbe) — concurrent breaking-point load in requests per second (RPS)

## Fork pull requests and coverage

A pull request from a fork does not receive repository secrets. SonarCloud does not analyze it, and the coverage-floor check uses `coverage.xml` as a warning rather than a failure. Diff coverage still blocks the pull request. Floors are enforced with the Sonar metric on the next push to `develop`.

A maintainer who wants the full quality gate runs `tools/ci/Mirror-ForkPr.ps1 -Pr <number>` (`-WhatIf` previews and changes nothing). The script copies the head onto a same-repo branch and opens a pull request. It refuses PolyForm Noncommercial paths and files that run during a same-repo build unless `-AllowNcPaths` or `-AllowBuildFiles` is passed after the diff has been read. Merge that pull request with a merge commit, not squash or rebase.

## PR checklist

Use the pull request template. Include tests for behavior changes. Do not weaken performance gates or skip PublicAPI analyzer updates for intentional API surface changes.

## Code owners

PolyForm Noncommercial paths are owned by `@justcoding121`. See [`.github/CODEOWNERS`](.github/CODEOWNERS).
