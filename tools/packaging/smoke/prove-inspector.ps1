#Requires -Version 5.1
param(
    [Parameter(Mandatory = $true)][string] $Exe,
    [Parameter(Mandatory = $true)][string] $Browsers,
    [Parameter(Mandatory = $true)][string] $Out
)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..\..')
dotnet build (Join-Path $root 'tools\InspectorInstallSmoke\InspectorInstallSmoke.csproj') -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw "smoke driver build failed $LASTEXITCODE" }
$driver = Join-Path $root 'tools\InspectorInstallSmoke\bin\Release\net10.0\InspectorInstallSmoke.exe'
New-Item -ItemType Directory -Force -Path $Out | Out-Null
& $driver fresh --exe $Exe --browsers $Browsers --out $Out
if ($LASTEXITCODE -ne 0) { throw "ASSERT: inspector fresh decrypt failed $LASTEXITCODE" }
