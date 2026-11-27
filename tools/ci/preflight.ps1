#Requires -Version 5.1
<#
.SYNOPSIS
  Local gate before pushing CI or product changes.

.PARAMETER Mode
  workflows | build | all (default all)
#>
param(
    [ValidateSet('workflows', 'build', 'all')]
    [string] $Mode = 'all'
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
Set-Location $root

function Write-Check([string] $Name, [bool] $Ok, [string] $Detail = '') {
    $mark = if ($Ok) { 'PASS' } else { 'FAIL' }
    $line = "[$mark] $Name"
    if ($Detail) { $line += " — $Detail" }
    Write-Host $line
    if (-not $Ok) { throw "preflight failed: $Name" }
}

function Invoke-Workflows {
    $actionlint = Get-Command actionlint -ErrorAction SilentlyContinue
    if ($actionlint) {
        # Disable shellcheck integration: the repo has many pre-existing SC* style
        # findings in long-lived workflow scripts. actionlint still catches workflow
        # syntax, expression, and action-input errors.
        & actionlint -shellcheck=
        Write-Check 'actionlint' ($LASTEXITCODE -eq 0)
    }
    else {
        Write-Host '[SKIP] actionlint — install with: winget install rhysd.actionlint'
    }

    $py = Get-Command python3 -ErrorAction SilentlyContinue
    if (-not $py) { $py = Get-Command python -ErrorAction SilentlyContinue }
    if (-not $py) {
        Write-Host '[SKIP] PyYAML workflow parse — python not on PATH'
    }
    else {
        $script = @'
import sys, pathlib
try:
    import yaml
except ImportError:
    print("PyYAML missing; pip install pyyaml", file=sys.stderr)
    sys.exit(2)
root = pathlib.Path(".github/workflows")
for path in sorted(root.glob("*.yml")):
    yaml.safe_load(path.read_text(encoding="utf-8"))
    print("ok", path)
'@
        & $py.Source -c $script
        Write-Check 'PyYAML workflows' ($LASTEXITCODE -eq 0)
    }

    if ($py -and (Test-Path 'tools/ci/classify-infra-failures.py')) {
        & $py.Source tools/ci/classify-infra-failures.py --self-test
        Write-Check 'classify-infra-failures --self-test' ($LASTEXITCODE -eq 0)
    }

    if (Test-Path 'tools/ci/assert-tree-evidence.ps1') {
        & pwsh -NoProfile -File tools/ci/assert-tree-evidence.ps1 -SelfTest
        Write-Check 'assert-tree-evidence -SelfTest' ($LASTEXITCODE -eq 0)
    }

    if (Test-Path 'tools/packaging/smoke/enforce.sh') {
        bash tools/packaging/smoke/enforce.sh --self-test
        Write-Check 'enforce --self-test' ($LASTEXITCODE -eq 0)
    }
    if (Test-Path 'tools/ci/Invoke-DotnetTestRetry.ps1') {
        & pwsh -NoProfile -File tools/ci/Invoke-DotnetTestRetry.ps1 -SelfTest
        Write-Check 'Invoke-DotnetTestRetry -SelfTest' ($LASTEXITCODE -eq 0)
    }

    if (Test-Path 'tools/ci/assert-signing-environment.sh') {
        bash tools/ci/assert-signing-environment.sh --self-test
        Write-Check 'assert-signing-environment --self-test' ($LASTEXITCODE -eq 0)
    }

    if ($py -and (Test-Path 'tools/ci/reflow-release-notes.py')) {
        & $py.Source tools/ci/reflow-release-notes.py --self-test
        Write-Check 'reflow-release-notes --self-test' ($LASTEXITCODE -eq 0)
    }

    if ($py -and (Test-Path 'tools/ci/verify-release-manifest.py')) {
        & $py.Source tools/ci/verify-release-manifest.py --self-test
        Write-Check 'verify-release-manifest --self-test' ($LASTEXITCODE -eq 0)
    }

    if (Test-Path 'tools/packaging/homebrew/bump-formula-from-sums.sh') {
        bash tools/packaging/homebrew/bump-formula-from-sums.sh --self-test
        Write-Check 'bump-formula-from-sums --self-test' ($LASTEXITCODE -eq 0)
    }

    $isWin = ($IsWindows -eq $true) -or ($env:OS -match 'Windows')
    $psExe = if ($isWin -and $env:SystemRoot) {
        Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    } else {
        $null
    }
    $psRunner = if (Get-Command pwsh -ErrorAction SilentlyContinue) { 'pwsh' }
        elseif ($psExe -and (Test-Path $psExe)) { $psExe }
        else { '' }
    if ($psRunner) {
        if (Test-Path 'tools/packaging/smoke/enforce.ps1') {
            # Windows-only: enforce.ps1 self-test shells out to cmd.exe.
            if ($isWin) {
                & $psRunner -NoProfile -File tools/packaging/smoke/enforce.ps1 --self-test
                Write-Check 'enforce.ps1 --self-test' ($LASTEXITCODE -eq 0)
                & $psRunner -NoProfile -File tools/packaging/smoke/invoke-leg.ps1 --self-test
                Write-Check 'invoke-leg.ps1 --self-test' ($LASTEXITCODE -eq 0)
                & $psRunner -NoProfile -File tools/packaging/smoke/retry.ps1 -SelfTest
                Write-Check 'retry.ps1 -SelfTest' ($LASTEXITCODE -eq 0)
            }
        }
        if (Test-Path 'tools/ci/assert-lifecycle-proof.ps1') {
            & $psRunner -NoProfile -File tools/ci/assert-lifecycle-proof.ps1 -SelfTest
            Write-Check 'assert-lifecycle-proof -SelfTest' ($LASTEXITCODE -eq 0)
        }
        if (Test-Path 'tools/ci/assert-release-assets.ps1') {
            & $psRunner -NoProfile -File tools/ci/assert-release-assets.ps1 -SelfTest
            Write-Check 'assert-release-assets -SelfTest' ($LASTEXITCODE -eq 0)
        }
    }
    if ($isWin -and $psExe -and (Test-Path $psExe)) {
        if (Test-Path 'tools/packaging/smoke/cli-update-ignores-proxy.ps1') {
            & $psExe -NoProfile -File tools/packaging/smoke/cli-update-ignores-proxy.ps1 -SelfTest
            Write-Check 'cli-update-ignores-proxy -SelfTest' ($LASTEXITCODE -eq 0)
        }
        if (Test-Path 'tools/packaging/smoke/cli-upgrade-from-published.ps1') {
            & $psExe -NoProfile -File tools/packaging/smoke/cli-upgrade-from-published.ps1 -SelfTest
            Write-Check 'cli-upgrade-from-published -SelfTest' ($LASTEXITCODE -eq 0)
        }
        if (Test-Path 'tools/packaging/smoke/cli-service-lifecycle.ps1') {
            & $psExe -NoProfile -File tools/packaging/smoke/cli-service-lifecycle.ps1 -SelfTest
            Write-Check 'cli-service-lifecycle -SelfTest' ($LASTEXITCODE -eq 0)
        }
        if (Test-Path 'tools/packaging/smoke/plus-lifecycle.ps1') {
            & $psExe -NoProfile -File tools/packaging/smoke/plus-lifecycle.ps1 -SelfTest
            Write-Check 'plus-lifecycle -SelfTest' ($LASTEXITCODE -eq 0)
        }
    } else {
        if (Test-Path 'tools/packaging/smoke/cli-update-ignores-proxy.sh') {
            bash tools/packaging/smoke/cli-update-ignores-proxy.sh --self-test
            Write-Check 'cli-update-ignores-proxy --self-test' ($LASTEXITCODE -eq 0)
        }
        if (Test-Path 'tools/packaging/smoke/cli-upgrade-from-published.sh') {
            bash tools/packaging/smoke/cli-upgrade-from-published.sh --self-test
            Write-Check 'cli-upgrade-from-published --self-test' ($LASTEXITCODE -eq 0)
        }
        if (Test-Path 'tools/packaging/smoke/cli-package-lifecycle.sh') {
            bash tools/packaging/smoke/cli-package-lifecycle.sh --self-test
            Write-Check 'cli-package-lifecycle --self-test' ($LASTEXITCODE -eq 0)
        }
        if (Test-Path 'tools/packaging/smoke/cli-update-package-managed.sh') {
            bash tools/packaging/smoke/cli-update-package-managed.sh --self-test
            Write-Check 'cli-update-package-managed --self-test' ($LASTEXITCODE -eq 0)
        }
        if (Test-Path 'tools/packaging/smoke/cli-service-lifecycle.sh') {
            bash tools/packaging/smoke/cli-service-lifecycle.sh --self-test
            Write-Check 'cli-service-lifecycle --self-test' ($LASTEXITCODE -eq 0)
        }
        if (Test-Path 'tools/packaging/smoke/plus-lifecycle.sh') {
            bash tools/packaging/smoke/plus-lifecycle.sh --self-test
            Write-Check 'plus-lifecycle --self-test' ($LASTEXITCODE -eq 0)
        }
    }

    if ((Test-Path '.github/required-checks.json') -and (Test-Path '.github/workflows/required-checks.yml')) {
        Write-Host '[INFO] required-checks drift is enforced in CI lint-workflows'
    }
}

function Invoke-Build {
    dotnet restore src/Titanium.Web.Proxy.sln -p:NuGetAudit=true -p:NuGetAuditLevel=high
    Write-Check 'dotnet restore' ($LASTEXITCODE -eq 0)
    dotnet build src/Titanium.Web.Proxy.sln -c Release --no-restore --warnaserror
    Write-Check 'dotnet build solution' ($LASTEXITCODE -eq 0)
    foreach ($proj in @(
        'tests/Titanium.Web.Proxy.UnitTests/Titanium.Web.Proxy.UnitTests.csproj',
        'tests/Titanium.Web.Proxy.IntegrationTests/Titanium.Web.Proxy.IntegrationTests.csproj',
        'tests/Titanium.Inspector.Tests/Titanium.Inspector.Tests.csproj',
        'tests/Titanium.Cli.Tests/Titanium.Cli.Tests.csproj'
    )) {
        if (-not (Test-Path $proj)) { continue }
        dotnet build $proj -c Release --no-restore --warnaserror
        Write-Check "dotnet build $proj" ($LASTEXITCODE -eq 0)
    }
}

Write-Host "preflight mode=$Mode root=$root"
if ($Mode -eq 'workflows' -or $Mode -eq 'all') { Invoke-Workflows }
if ($Mode -eq 'build' -or $Mode -eq 'all') { Invoke-Build }
Write-Host 'preflight ok'
exit 0
