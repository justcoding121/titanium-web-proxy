# CliQaProbe

Per-machine Titanium CLI checklist (not in the solution, not CI). Spawns a built `titanium.dll` and exercises nested help, config dialects, live `run` traffic, and optional OS service lifecycle.

## Prerequisites

```powershell
dotnet build src/Titanium.Cli -c Release
# Optional for run-plus:
dotnet build src/Titanium.Plus -c Release
```

## Usage

```powershell
dotnet run --project tools/CliQaProbe -- status
dotnet run --project tools/CliQaProbe -- help-matrix
dotnet run --project tools/CliQaProbe -- core
dotnet run --project tools/CliQaProbe -- all
dotnet run --project tools/CliQaProbe -- all --elevated   # Admin / sudo for real SCM
dotnet run --project tools/CliQaProbe -- service
dotnet run --project tools/CliQaProbe -- service --elevated
```

## What it covers

| Area | Steps |
|------|--------|
| Nested help | root, run/test/version/update/http3-deps/service/install/start `--help` |
| Meta | `version`, `version --check` (soft), `http3-deps status` (never install) |
| `test` dialects | yaml / json / twp / nginx + invalid |
| Live `run` | forward, nginx `.conf`, site-file listen+forward, routes, static+ETag, TLS leaf, MITM→local HTTPS echo, http2-off, file logging, Plus soft |
| OS service | status missing; unelevated install message; `--elevated` install→start→HTTP→stop→uninstall as **`titanium-qa-probe` only** |

Skipped (already unit/E2E): factory `binPath` snapshots, flag parse unit tests, live `update` / `http3-deps install`.

## Results

- `tools/CliQaProbe/results/last-run.json`
- `tools/CliQaProbe/results/probe-*.log`

See [LOCAL-QA.md](../LOCAL-QA.md) for the full solo QA recipe with E2E tests and InspectorDesktopProbe.
