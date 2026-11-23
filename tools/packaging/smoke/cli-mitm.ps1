#Requires -Version 5.1
param(
    [Parameter(Mandatory = $true)][string] $Titanium,
    [Parameter(Mandatory = $true)][string] $Out
)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..\..')
dotnet build (Join-Path $root 'tools\InspectorInstallSmoke\InspectorInstallSmoke.csproj') -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw "smoke driver build failed $LASTEXITCODE" }
$driver = Join-Path $root 'tools\InspectorInstallSmoke\bin\Release\net10.0\InspectorInstallSmoke.exe'
& $driver cli --titanium $Titanium --out $Out
if ($LASTEXITCODE -ne 0) { throw "ASSERT: cli MITM access log proof failed $LASTEXITCODE" }
