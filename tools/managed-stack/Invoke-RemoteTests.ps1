# Copies this branch to a remote clone with a git bundle and runs one test project.
# Nothing is pushed. The remote checkout is ~/work/twp and the SDK is ~/.dotnet/dotnet.
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('macmini', 'linuxmini')]
    [string] $Machine,
    [string] $Project = 'tests/Titanium.Tls.Tests/Titanium.Tls.Tests.csproj',
    [string] $Filter = ''
)

$ErrorActionPreference = 'Stop'
$bundle = Join-Path $env:TEMP 'twp-managed.bundle'
git bundle create $bundle 'origin/develop..HEAD'
scp $bundle "${Machine}:~/twp-managed.bundle"
$filterArg = ''
if ($Filter) { $filterArg = "--filter '$Filter'" }
$remote = @"
set -e
git -C "`$HOME/work/twp" fetch "`$HOME/twp-managed.bundle" HEAD
git -C "`$HOME/work/twp" checkout -B feat/managed-tls-quic-stack FETCH_HEAD
"`$HOME/.dotnet/dotnet" test "`$HOME/work/twp/$Project" -c Release --nologo $filterArg
"@
$remote = $remote.Replace("`r`n", "`n")
$remote | ssh $Machine 'bash -s'
