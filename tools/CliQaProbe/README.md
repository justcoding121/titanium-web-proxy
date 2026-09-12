# CliQaProbe

> **For maintainers / contributors** — per-machine Titanium CLI checklist (optional; CI `cli-e2e` is the source of truth).

Per-machine Titanium CLI checklist (not in the solution). Spawns the built `titanium` / `titanium.exe` apphost. **PR CI now runs the full command-tree + Plus process E2E on Windows/Linux/macOS** (`TestCategory=E2E` in the `cli-e2e` job). Use this probe for ad-hoc local checks or when debugging elevation UX.

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
dotnet run --project tools/CliQaProbe -- meta
dotnet run --project tools/CliQaProbe -- core
dotnet run --project tools/CliQaProbe -- all
dotnet run --project tools/CliQaProbe -- all --elevated   # login user + passwordless sudo (Linux)
# or: sudo -E dotnet run --project tools/CliQaProbe -- all --elevated
dotnet run --project tools/CliQaProbe -- service
dotnet run --project tools/CliQaProbe -- service --elevated
```

## What it covers

| Area | Steps |
|------|--------|
| Nested help | root, run/test/version/update/http3-deps/service/install/start `--help` (update help asserts `--plus`) |
| Meta | `version`, `version --check` (soft), `version --check --plus` (soft), `update --plus` (soft feed; restores prior Plus.dll), `update --remove-plus` (restores prior Plus.dll), `http3-deps status` (never install) |
| `test` dialects | yaml / json / twp / `.conf` (http-server) + invalid |
| Live `run` | forward, `.conf` reverse, site-file listen+forward, routes, static+ETag, TLS leaf, MITM→local HTTPS echo, http2-off, file logging, Plus soft |
| OS service | status missing; unelevated install message; `--elevated` machine install→start→HTTP→stop→uninstall as **`titanium-qa-probe`** (via `sudo -n` when not root); Linux/macOS also **`service --user`** as the **login user** (`SUDO_USER` when the probe was started with sudo). Bare root without `SUDO_USER` skips `--user` (no user bus). |

Skipped (already unit/E2E): factory `binPath` snapshots, flag parse unit tests, live CLI zip `update` / `http3-deps install`.

## Results

- `tools/CliQaProbe/results/last-run.json`
- `tools/CliQaProbe/results/probe-*.log`

See [LOCAL-QA.md](../LOCAL-QA.md) for the full solo QA recipe with E2E tests and InspectorDesktopProbe.
