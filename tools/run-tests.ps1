# One entry point for local and CI test runs.
# Sets hang blame, the shared runsettings, a prompt watcher, and an optional machine-state diff.
param(
    [string]$Project = 'all',
    [string]$Filter = '',
    [string]$Configuration = 'Release',
    [switch]$Verify,
    [string]$Root = ''
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$repo = Resolve-Path (Join-Path $PSScriptRoot '..')
if ([string]::IsNullOrWhiteSpace($Root)) { $Root = $repo }
$results = Join-Path $repo 'TestResults\run-tests'
New-Item -ItemType Directory -Force -Path $results | Out-Null

$projects = [ordered]@{
    configuration = 'tests\Titanium.Web.Proxy.Configuration.Tests\Titanium.Web.Proxy.Configuration.Tests.csproj'
    scripting     = 'tests\Titanium.Web.Proxy.Scripting.Tests\Titanium.Web.Proxy.Scripting.Tests.csproj'
    manager       = 'tests\Titanium.Manager.Tests\Titanium.Manager.Tests.csproj'
    gateway       = 'tests\Titanium.Gateway.Tests\Titanium.Gateway.Tests.csproj'
    xds           = 'tests\Titanium.Xds.Tests\Titanium.Xds.Tests.csproj'
    geoip         = 'tests\Titanium.Cli.GeoIp.Tests\Titanium.Cli.GeoIp.Tests.csproj'
    unit          = 'tests\Titanium.Web.Proxy.UnitTests\Titanium.Web.Proxy.UnitTests.csproj'
    cli           = 'tests\Titanium.Cli.Tests\Titanium.Cli.Tests.csproj'
    plus          = 'tests\Titanium.Plus.Tests\Titanium.Plus.Tests.csproj'
    inspector     = 'tests\Titanium.Inspector.Tests\Titanium.Inspector.Tests.csproj'
    integration   = 'tests\Titanium.Web.Proxy.IntegrationTests\Titanium.Web.Proxy.IntegrationTests.csproj'
    e2e           = 'tests\Titanium.E2E.Tests\Titanium.E2E.Tests.csproj'
}

$selected = @()
if ($Project -eq 'all') { $selected = @($projects.Keys) }
else {
    foreach ($name in ($Project -split ',')) {
        $key = $name.Trim().ToLowerInvariant()
        if (-not $projects.Contains($key)) { Write-Error "Unknown project '$key'. Known: $($projects.Keys -join ', ')"; exit 1 }
        $selected += $key
    }
}

Push-Location $repo
try {
    Write-Host "Building $($selected -join ', ') ($Configuration)"
    foreach ($key in $selected) {
        & dotnet build (Join-Path $repo $projects[$key]) -c $Configuration --nologo
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }

    $before = Join-Path $results 'state-before'
    if ($Verify) {
        & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repo 'tools\test-hygiene\Capture-MachineState.ps1') -OutDir $before
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }

    $watchLog = Join-Path $results 'watch'
    $watch = Start-Process -FilePath powershell -ArgumentList @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $repo 'tools\test-hygiene\Watch-Prompts.ps1'),
        '-Root', $Root, '-LogDir', $watchLog, '-Seconds', '7200'
    ) -PassThru -WindowStyle Hidden

    $failed = $false
    try {
        foreach ($key in $selected) {
            $proj = Join-Path $repo $projects[$key]
            $attempts = 1
            if ($key -eq 'integration' -and -not $Verify -and $env:GITHUB_ACTIONS -eq 'true') { $attempts = 2 }
            $ok = $false
            for ($i = 1; $i -le $attempts; $i++) {
                $args = @(
                    'test', $proj, '-c', $Configuration, '--no-build', '--nologo',
                    '--blame-hang', '--blame-hang-timeout', '5m',
                    '--logger', "trx;LogFileName=$key.trx",
                    '--logger', 'console;verbosity=normal',
                    '--results-directory', (Join-Path $results $key)
                )
                if ($Filter) { $args += @('--filter', $Filter) }
                Write-Host "=== $key attempt $i/$attempts ==="
                & dotnet @args
                if ($LASTEXITCODE -eq 0) { $ok = $true; break }
                if ($i -eq $attempts) { $failed = $true }
            }
            if (-not $ok) { break }
        }
    }
    finally {
        if ($watch -and -not $watch.HasExited) { Stop-Process -Id $watch.Id -Force -ErrorAction SilentlyContinue }
    }

    $promptLog = Join-Path $watchLog 'prompts.jsonl'
    if (Test-Path -LiteralPath $promptLog) {
        $hits = @(Get-Content -LiteralPath $promptLog | Where-Object { $_ -match '"kind":"(window|listener|UAC_PENDING)"' })
        if ($Verify -and $hits.Count -gt 0) {
            Write-Host "Prompt watcher recorded $($hits.Count) event(s). See $promptLog"
            $failed = $true
        }
    }

    if ($Verify) {
        $after = Join-Path $results 'state-after'
        & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repo 'tools\test-hygiene\Capture-MachineState.ps1') -OutDir $after
        & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repo 'tools\test-hygiene\Capture-MachineState.ps1') -Compare ($before + '|' + $after)
        if ($LASTEXITCODE -ne 0) { $failed = $true }
    }

    if ($failed) { exit 1 }
    exit 0
}
finally {
    Pop-Location
}
