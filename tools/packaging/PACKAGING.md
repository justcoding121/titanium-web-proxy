# Packaging notes (Titanium Inspector / Cli)

## Windows (7.0 ship bar)

```powershell
dotnet publish src/Titanium.Inspector/Titanium.Inspector.csproj -c Release -r win-x64 --self-contained true -o artifacts/inspector/win-x64
dotnet publish src/Titanium.Cli/Titanium.Cli.csproj -c Release -r win-x64 --self-contained true -p:AssemblyName=titanium -o artifacts/cli/win-x64
```

Zip the output folders as `TitaniumInspector-win-x64.zip` and `Titanium.Cli-win-x64.zip`.

MSI: produce via WiX or Advanced Installer in CI once Authenticode secrets are available (stretch). Until then, winget can ship the portable zip (see `tools/packaging/winget/`).

Winget package IDs:
- `justcoding121.TitaniumInspector`
- `justcoding121.TitaniumCli`

## macOS / Linux (stretch)

Publish with `-r osx-x64`, `osx-arm64`, `linux-x64` and attach tarballs / `.deb` / `.dmg` in `release.yml`.
