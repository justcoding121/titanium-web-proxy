# Contributing

Thanks for contributing to Titanium Web Proxy.

## Editions

| Paths | License | External PRs |
|-------|---------|--------------|
| `src/Titanium.Web.Proxy/`, `src/Titanium.Web.Proxy.Abstractions/`, `src/Titanium.Web.Proxy.Configuration/`, `src/Titanium.Cli/`, related MIT tests/examples/tools/docs | MIT | Welcome after CLA |
| `src/Titanium.Plus/`, `src/Titanium.Inspector/`, `tests/Titanium.Plus.Tests/`, `tests/Titanium.Inspector.Tests/` | PolyForm Noncommercial | Maintainer allowlist only |

See [CLA.md](CLA.md) and [README.md](README.md) Editions section.

## Before you open a PR

1. Sign the [CLA](CLA.md) if you have not already.
2. Target the `develop` branch (unless a maintainer directs otherwise).
3. Run `dotnet build src/Titanium.Web.Proxy.sln -c Release` and `dotnet test` for affected test projects.
4. Do not introduce third-party product names of other proxies or traffic debuggers into source, tests, CLI help, or docs.
5. Keep hot-path changes minimal; preserve `ForwardHost` terminate-lite eligibility when routes are unset or equivalent to a single sticky destination.

## PR checklist

Use the pull request template. Include tests for behavior changes. Do not weaken performance gates or skip PublicAPI analyzer updates for intentional API surface changes.

## Code owners

PolyForm Noncommercial paths are owned by `@justcoding121`. See [`.github/CODEOWNERS`](.github/CODEOWNERS).
