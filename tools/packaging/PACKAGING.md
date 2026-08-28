# Packaging notes (Titanium Inspector / Cli)

## Windows (7.0 ship bar)

```powershell
dotnet publish src/Titanium.Inspector/Titanium.Inspector.csproj -c Release -r win-x64 --self-contained true -o artifacts/inspector/win-x64
dotnet publish src/Titanium.Cli/Titanium.Cli.csproj -c Release -r win-x64 --self-contained true -p:AssemblyName=titanium -o artifacts/cli/win-x64
```

Zip the output folders as `TitaniumInspector-win-x64.zip` and `Titanium.Cli-win-x64.zip`.

### MSI (Inspector)

```powershell
./tools/packaging/build-inspector-msi.ps1 `
  -PayloadDir artifacts/inspector/win-x64 `
  -OutputMsi TitaniumInspector-win-x64.msi `
  -Version 7.0.0
```

Uses WiX 5 (`dotnet tool` manifest under `tools/packaging/wix/`). Authenticode signing is stretch; unsigned MSI is fine for GitHub Releases / early winget.

Winget package IDs:
- `justcoding121.TitaniumInspector` (prefer MSI installer when attached to the Release)
- `justcoding121.TitaniumCli` (portable zip)

Manifest stubs live in `tools/packaging/winget/`.

## macOS / Linux (stretch)

Publish with `-r osx-x64`, `osx-arm64`, `linux-x64` and attach tarballs / `.deb` / `.dmg` in `release.yml`.
