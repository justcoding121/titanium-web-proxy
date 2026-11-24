# Run `dotnet test` once; on a small failure set, retry only those tests and report them.
# Whole-batch retry is the fallback when TRX is missing or too many tests failed.
param(
    [Parameter(ParameterSetName = 'Run', Mandatory = $true)][string[]]$Arguments,
    [Parameter(ParameterSetName = 'Run', Mandatory = $true)][string]$ResultsDir,
    [Parameter(ParameterSetName = 'Run')][int]$MaxRetriedTests = 5,
    [Parameter(ParameterSetName = 'SelfTest')][switch]$SelfTest
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Expand-RspArgs([string[]]$argsIn) {
    $out = New-Object System.Collections.Generic.List[string]
    foreach ($a in $argsIn) {
        if ($a -like '@*') {
            $rsp = $a.Substring(1).Trim('"')
            if (-not (Test-Path -LiteralPath $rsp)) { throw "rsp file not found: $rsp" }
            Get-Content -LiteralPath $rsp | ForEach-Object {
                $t = # Run `dotnet test` once; on a small failure set, retry only those tests and report them.
# Whole-batch retry is the fallback when TRX is missing or too many tests failed.
param(
    [Parameter(ParameterSetName = 'Run', Mandatory = $true)][string[]]$Arguments,
    [Parameter(ParameterSetName = 'Run', Mandatory = $true)][string]$ResultsDir,
    [Parameter(ParameterSetName = 'Run')][int]$MaxRetriedTests = 5,
    [Parameter(ParameterSetName = 'SelfTest')][switch]$SelfTest
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-FailedTestsFromTrx([string]$trxPath) {
    if (-not (Test-Path -LiteralPath $trxPath)) { return @() }
    [xml]$doc = Get-Content -LiteralPath $trxPath -Raw
    $ns = New-Object System.Xml.XmlNamespaceManager($doc.NameTable)
    $ns.AddNamespace('t', 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010')

    $defs = @{}
    foreach ($unit in $doc.SelectNodes('//t:TestDefinitions/t:UnitTest', $ns)) {
        $id = $unit.GetAttribute('id')
        $method = $unit.SelectSingleNode('t:TestMethod', $ns)
        if (-not $method) { continue }
        # TRX may store an assembly-qualified className; FQN filters want the type only.
        $className = (($method.GetAttribute('className') -split ',')[0]).Trim()
        $name = $method.GetAttribute('name')
        if ([string]::IsNullOrWhiteSpace($className) -or [string]::IsNullOrWhiteSpace($name)) { continue }
        $defs[$id] = "$className.$name"
    }

    $failed = New-Object System.Collections.Generic.List[string]
    foreach ($result in $doc.SelectNodes('//t:Results/t:UnitTestResult', $ns)) {
        if ($result.GetAttribute('outcome') -ne 'Failed') { continue }
        $testId = $result.GetAttribute('testId')
        if ($defs.ContainsKey($testId)) {
            $failed.Add([string]$defs[$testId])
        }
        else {
            $fallback = $result.GetAttribute('testName')
            if (-not [string]::IsNullOrWhiteSpace($fallback)) { $failed.Add([string]$fallback) }
        }
    }
    return @($failed | Select-Object -Unique)
}

function Write-RetryReport([object[]]$entries) {
    $jsonPath = Join-Path $ResultsDir 'flaky-retried.json'
    $list = @($entries | Where-Object { $null -ne $_ })
    $json = if ($list.Length -eq 0) { '[]' } else { ConvertTo-Json -InputObject $list -Depth 5 }
    New-Item -ItemType Directory -Force -Path $ResultsDir | Out-Null
    Set-Content -LiteralPath $jsonPath -Value $json -Encoding utf8

    if ($env:GITHUB_STEP_SUMMARY) {
        Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value "## Retried tests"
        if ($list.Length -eq 0) {
            Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value "_none_"
        }
        else {
            foreach ($e in $list) {
                Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value ("- `{0}`: {1} -> {2}" -f $e.test, $e.attempt1, $e.attempt2)
            }
        }
    }
    foreach ($e in $list) {
        Write-Host ("::warning::Retried flaky test {0} ({1} -> {2})" -f $e.test, $e.attempt1, $e.attempt2)
    }
}

function Invoke-SelfTest {
    $root = Join-Path $PSScriptRoot 'testdata/trx'
    $single = @(Get-FailedTestsFromTrx (Join-Path $root 'single-failure.trx'))
    if ($single.Length -ne 1 -or $single[0] -ne 'Titanium.Web.Proxy.UnitTests.FlakyTests.FlakyOnce') {
        throw "self-test: single-failure expected one FlakyOnce, got: $($single -join ',')"
    }
    $over = @(Get-FailedTestsFromTrx (Join-Path $root 'over-cap.trx'))
    if ($over.Length -lt 6) {
        throw "self-test: over-cap expected >=6 failures, got $($over.Length)"
    }
    $empty = @(Get-FailedTestsFromTrx (Join-Path $root 'empty-crash.trx'))
    if ($empty.Length -ne 0) {
        throw "self-test: empty-crash expected 0 failures, got $($empty.Length)"
    }
    $missing = @(Get-FailedTestsFromTrx (Join-Path $root 'does-not-exist.trx'))
    if ($missing.Length -ne 0) {
        throw "self-test: missing trx should yield empty list"
    }
    Write-Host 'Invoke-DotnetTestRetry self-test ok'
    exit 0
}

if ($SelfTest) { Invoke-SelfTest }

$effectiveArgs = Expand-RspArgs @($Arguments)

New-Item -ItemType Directory -Force -Path $ResultsDir | Out-Null
$attempt1 = Join-Path $ResultsDir 'attempt1.trx'
$report = New-Object System.Collections.Generic.List[object]

Write-Host "dotnet test (attempt 1) -> $ResultsDir"
$argList = @($effectiveArgs) + @(
    '--logger', "trx;LogFileName=attempt1.trx",
    '--results-directory', $ResultsDir
)
& dotnet test @argList
$exit1 = $LASTEXITCODE
if ($exit1 -eq 0) {
    Write-RetryReport @()
    exit 0
}

$failed = @(Get-FailedTestsFromTrx $attempt1)
if ($failed.Length -eq 0 -or $failed.Length -gt $MaxRetriedTests) {
    Write-Host ("Per-test retry skipped (failed={0}, cap={1}); whole-batch retry once." -f $failed.Length, $MaxRetriedTests)
    Start-Sleep -Seconds 2
    $batchDir = Join-Path $ResultsDir 'batch-retry'
    New-Item -ItemType Directory -Force -Path $batchDir | Out-Null
    $batchArgs = @($effectiveArgs) + @(
        '--logger', 'trx;LogFileName=batch-retry.trx',
        '--results-directory', $batchDir
    )
    & dotnet test @batchArgs
    $exitBatch = $LASTEXITCODE
    Write-RetryReport @()
    exit $exitBatch
}

# Preserve an existing --filter category constraint by AND-ing the FQN list.
$existingFilter = $null
for ($i = 0; $i -lt $effectiveArgs.Count; $i++) {
    if ($effectiveArgs[$i] -eq '--filter' -and ($i + 1) -lt $Arguments.Count) {
        $existingFilter = $effectiveArgs[$i + 1]
        break
    }
}

$fqnParts = foreach ($t in $failed) { "FullyQualifiedName=$t" }
$retryFilter = ($fqnParts -join '|')
if ($existingFilter) {
    $retryFilter = "($existingFilter)&($retryFilter)"
}

$retryArgs = New-Object System.Collections.Generic.List[string]
for ($i = 0; $i -lt $effectiveArgs.Count; $i++) {
    if ($effectiveArgs[$i] -eq '--filter') {
        $i++  # skip value
        continue
    }
    $retryArgs.Add($Arguments[$i])
}
$retryArgs.Add('--filter')
$retryArgs.Add($retryFilter)

$retryDir = Join-Path $ResultsDir 'retry'
New-Item -ItemType Directory -Force -Path $retryDir | Out-Null
Write-Host "dotnet test (per-test retry of $($failed.Length) test(s))"
$retryCmd = @($retryArgs.ToArray()) + @(
    '--logger', 'trx;LogFileName=attempt2.trx',
    '--results-directory', $retryDir
)
& dotnet test @retryCmd
$exit2 = $LASTEXITCODE

$attempt2Path = Join-Path $retryDir 'attempt2.trx'
$stillFailed = @(Get-FailedTestsFromTrx $attempt2Path)
foreach ($t in $failed) {
    $outcome2 = if ($stillFailed -contains $t) { 'Failed' } else { 'Passed' }
    if ($exit2 -ne 0 -and $stillFailed.Length -eq 0 -and $outcome2 -eq 'Passed') {
        # No TRX detail; treat as failed if the process failed.
        $outcome2 = 'Failed'
    }
    if ($exit2 -eq 0) { $outcome2 = 'Passed' }
    $report.Add([pscustomobject]@{ test = $t; attempt1 = 'Failed'; attempt2 = $outcome2 })
}

Write-RetryReport $report.ToArray()
exit $exit2
.Trim()
                if ($t) { [void]$out.Add($t) }
            }
        }
        else { [void]$out.Add($a) }
    }
    return $out.ToArray()
}

function Get-FailedTestsFromTrx([string]$trxPath) {
    if (-not (Test-Path -LiteralPath $trxPath)) { return @() }
    [xml]$doc = Get-Content -LiteralPath $trxPath -Raw
    $ns = New-Object System.Xml.XmlNamespaceManager($doc.NameTable)
    $ns.AddNamespace('t', 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010')

    $defs = @{}
    foreach ($unit in $doc.SelectNodes('//t:TestDefinitions/t:UnitTest', $ns)) {
        $id = $unit.GetAttribute('id')
        $method = $unit.SelectSingleNode('t:TestMethod', $ns)
        if (-not $method) { continue }
        # TRX may store an assembly-qualified className; FQN filters want the type only.
        $className = (($method.GetAttribute('className') -split ',')[0]).Trim()
        $name = $method.GetAttribute('name')
        if ([string]::IsNullOrWhiteSpace($className) -or [string]::IsNullOrWhiteSpace($name)) { continue }
        $defs[$id] = "$className.$name"
    }

    $failed = New-Object System.Collections.Generic.List[string]
    foreach ($result in $doc.SelectNodes('//t:Results/t:UnitTestResult', $ns)) {
        if ($result.GetAttribute('outcome') -ne 'Failed') { continue }
        $testId = $result.GetAttribute('testId')
        if ($defs.ContainsKey($testId)) {
            $failed.Add([string]$defs[$testId])
        }
        else {
            $fallback = $result.GetAttribute('testName')
            if (-not [string]::IsNullOrWhiteSpace($fallback)) { $failed.Add([string]$fallback) }
        }
    }
    return @($failed | Select-Object -Unique)
}

function Write-RetryReport([object[]]$entries) {
    $jsonPath = Join-Path $ResultsDir 'flaky-retried.json'
    $list = @($entries | Where-Object { $null -ne $_ })
    $json = if ($list.Length -eq 0) { '[]' } else { ConvertTo-Json -InputObject $list -Depth 5 }
    New-Item -ItemType Directory -Force -Path $ResultsDir | Out-Null
    Set-Content -LiteralPath $jsonPath -Value $json -Encoding utf8

    if ($env:GITHUB_STEP_SUMMARY) {
        Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value "## Retried tests"
        if ($list.Length -eq 0) {
            Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value "_none_"
        }
        else {
            foreach ($e in $list) {
                Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value ("- `{0}`: {1} -> {2}" -f $e.test, $e.attempt1, $e.attempt2)
            }
        }
    }
    foreach ($e in $list) {
        Write-Host ("::warning::Retried flaky test {0} ({1} -> {2})" -f $e.test, $e.attempt1, $e.attempt2)
    }
}

function Invoke-SelfTest {
    $root = Join-Path $PSScriptRoot 'testdata/trx'
    $single = @(Get-FailedTestsFromTrx (Join-Path $root 'single-failure.trx'))
    if ($single.Length -ne 1 -or $single[0] -ne 'Titanium.Web.Proxy.UnitTests.FlakyTests.FlakyOnce') {
        throw "self-test: single-failure expected one FlakyOnce, got: $($single -join ',')"
    }
    $over = @(Get-FailedTestsFromTrx (Join-Path $root 'over-cap.trx'))
    if ($over.Length -lt 6) {
        throw "self-test: over-cap expected >=6 failures, got $($over.Length)"
    }
    $empty = @(Get-FailedTestsFromTrx (Join-Path $root 'empty-crash.trx'))
    if ($empty.Length -ne 0) {
        throw "self-test: empty-crash expected 0 failures, got $($empty.Length)"
    }
    $missing = @(Get-FailedTestsFromTrx (Join-Path $root 'does-not-exist.trx'))
    if ($missing.Length -ne 0) {
        throw "self-test: missing trx should yield empty list"
    }
    Write-Host 'Invoke-DotnetTestRetry self-test ok'
    exit 0
}

if ($SelfTest) { Invoke-SelfTest }

$effectiveArgs = Expand-RspArgs @($Arguments)

New-Item -ItemType Directory -Force -Path $ResultsDir | Out-Null
$attempt1 = Join-Path $ResultsDir 'attempt1.trx'
$report = New-Object System.Collections.Generic.List[object]

Write-Host "dotnet test (attempt 1) -> $ResultsDir"
$argList = @($effectiveArgs) + @(
    '--logger', "trx;LogFileName=attempt1.trx",
    '--results-directory', $ResultsDir
)
& dotnet test @argList
$exit1 = $LASTEXITCODE
if ($exit1 -eq 0) {
    Write-RetryReport @()
    exit 0
}

$failed = @(Get-FailedTestsFromTrx $attempt1)
if ($failed.Length -eq 0 -or $failed.Length -gt $MaxRetriedTests) {
    Write-Host ("Per-test retry skipped (failed={0}, cap={1}); whole-batch retry once." -f $failed.Length, $MaxRetriedTests)
    Start-Sleep -Seconds 2
    $batchDir = Join-Path $ResultsDir 'batch-retry'
    New-Item -ItemType Directory -Force -Path $batchDir | Out-Null
    $batchArgs = @($effectiveArgs) + @(
        '--logger', 'trx;LogFileName=batch-retry.trx',
        '--results-directory', $batchDir
    )
    & dotnet test @batchArgs
    $exitBatch = $LASTEXITCODE
    Write-RetryReport @()
    exit $exitBatch
}

# Preserve an existing --filter category constraint by AND-ing the FQN list.
$existingFilter = $null
for ($i = 0; $i -lt $effectiveArgs.Count; $i++) {
    if ($effectiveArgs[$i] -eq '--filter' -and ($i + 1) -lt $Arguments.Count) {
        $existingFilter = $effectiveArgs[$i + 1]
        break
    }
}

$fqnParts = foreach ($t in $failed) { "FullyQualifiedName=$t" }
$retryFilter = ($fqnParts -join '|')
if ($existingFilter) {
    $retryFilter = "($existingFilter)&($retryFilter)"
}

$retryArgs = New-Object System.Collections.Generic.List[string]
for ($i = 0; $i -lt $effectiveArgs.Count; $i++) {
    if ($effectiveArgs[$i] -eq '--filter') {
        $i++  # skip value
        continue
    }
    $retryArgs.Add($Arguments[$i])
}
$retryArgs.Add('--filter')
$retryArgs.Add($retryFilter)

$retryDir = Join-Path $ResultsDir 'retry'
New-Item -ItemType Directory -Force -Path $retryDir | Out-Null
Write-Host "dotnet test (per-test retry of $($failed.Length) test(s))"
$retryCmd = @($retryArgs.ToArray()) + @(
    '--logger', 'trx;LogFileName=attempt2.trx',
    '--results-directory', $retryDir
)
& dotnet test @retryCmd
$exit2 = $LASTEXITCODE

$attempt2Path = Join-Path $retryDir 'attempt2.trx'
$stillFailed = @(Get-FailedTestsFromTrx $attempt2Path)
foreach ($t in $failed) {
    $outcome2 = if ($stillFailed -contains $t) { 'Failed' } else { 'Passed' }
    if ($exit2 -ne 0 -and $stillFailed.Length -eq 0 -and $outcome2 -eq 'Passed') {
        # No TRX detail; treat as failed if the process failed.
        $outcome2 = 'Failed'
    }
    if ($exit2 -eq 0) { $outcome2 = 'Passed' }
    $report.Add([pscustomobject]@{ test = $t; attempt1 = 'Failed'; attempt2 = $outcome2 })
}

Write-RetryReport $report.ToArray()
exit $exit2

