# Titanium E2E Tests

Process-level and service-level end-to-end coverage for CLI, CLI+Plus, and Inspector.

## Categories

| Category | CI | Description |
|----------|----|-------------|
| `E2E` | Yes | Spawn `titanium`, MITM HttpClient, Plus control plane |
| `E2E-UI` | Yes | ViewModel command smoke (same commands Avalonia binds) |
| `E2E-Slow` | No | Windows WinINET system proxy + Chrome `--disable-quic` |

```powershell
# PR / local fast suite (same filter as CI)
dotnet test tests/Titanium.E2E.Tests -c Release --filter "TestCategory=E2E|TestCategory=E2E-UI"

# Optional Chrome + system proxy (mutates WinINET; restores in finally)
dotnet test tests/Titanium.E2E.Tests -c Release --filter "TestCategory=E2E-Slow"
```

Build `Titanium.Cli` and `Titanium.Plus` (Release or Debug) before process tests so `CliProcessHarness` can locate `titanium.dll`.
