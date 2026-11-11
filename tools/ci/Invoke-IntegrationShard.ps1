# Run one class-stable shard of the integration suite.
# Each class stays in a single process so ClassInitialize and DoNotParallelize keep their meaning.
# Shards are separate processes (separate CI jobs), which is what overlaps the serialized classes.
param(
    [Parameter(Mandatory = $true)][int]$ShardIndex,
    [Parameter(Mandatory = $true)][int]$ShardCount,
    [string]$Project = 'tests/Titanium.Web.Proxy.IntegrationTests/Titanium.Web.Proxy.IntegrationTests.csproj',
    [string]$Settings = 'tests/test.runsettings',
    [string]$ResultsRoot = 'coverage/integration',
    [int]$MaxAttempts = 3
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

if ($ShardCount -lt 1) { throw "ShardCount must be >= 1 (was $ShardCount)" }
if ($ShardIndex -lt 0 -or $ShardIndex -ge $ShardCount) {
    throw "ShardIndex $ShardIndex is outside 0..$($ShardCount - 1)"
}

function Get-StableBucket([string]$value, [int]$count) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = $sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($value))
    }
    finally {
        $sha.Dispose()
    }

    $bucket = [BitConverter]::ToUInt32($bytes, 0) % [uint32]$count
    return [int]$bucket
}

function Get-ClassKey([string]$displayName) {
    $base = $displayName.Trim()
    $paren = $base.IndexOf(' (')
    if ($paren -ge 0) { $base = $base.Substring(0, $paren) }

    $prefix = 'Titanium.Web.Proxy.IntegrationTests.'
    if ($base.StartsWith($prefix, [StringComparison]::Ordinal)) {
        $rest = $base.Substring($prefix.Length)
        $splitAt = $rest.IndexOfAny([char[]]@('.', '+'))
        if ($splitAt -gt 0) { return $rest.Substring(0, $splitAt) }
        return $rest
    }

    return $base
}

Write-Host "Listing integration tests for shard $ShardIndex of $ShardCount"
$listed = & dotnet test $Project --configuration Release --no-build --no-restore --settings $Settings --list-tests 2>&1 |
    ForEach-Object { "$_" }
if ($LASTEXITCODE -ne 0) {
    $listed | Write-Host
    throw "dotnet test --list-tests exited $LASTEXITCODE"
}

$names = New-Object System.Collections.Generic.List[string]
foreach ($line in $listed) {
    $trim = $line.Trim()
    if ($trim.StartsWith('Titanium.Web.Proxy.IntegrationTests.', [StringComparison]::Ordinal)) {
        $names.Add($trim)
    }
}

if ($names.Count -eq 0) {
    $listed | Select-Object -Last 40 | Write-Host
    throw 'list-tests produced no integration test names'
}

$classes = New-Object System.Collections.Generic.List[string]
$seen = @{}
foreach ($name in $names) {
    $key = Get-ClassKey $name
    if (-not $seen.ContainsKey($key)) {
        $seen[$key] = $true
        if ((Get-StableBucket $key $ShardCount) -eq $ShardIndex) {
            $classes.Add($key)
        }
    }
}

Write-Host ("Shard {0}: {1} classes, {2} tests listed in the assembly" -f $ShardIndex, $classes.Count, $names.Count)
if ($classes.Count -eq 0) {
    Write-Host "Shard $ShardIndex has no classes."
    exit 0
}

$prefix = 'Titanium.Web.Proxy.IntegrationTests.'
$parts = New-Object System.Collections.Generic.List[string]
foreach ($class in $classes) {
    $parts.Add("FullyQualifiedName~$prefix$class.")
    $parts.Add("FullyQualifiedName~$prefix$class+")
}

# & binds tighter than | in the VSTest filter language, so the class disjunction stays in parentheses.
$filter = '(' + ($parts -join '|') + ')&TestCategory!=Slow'
$results = Join-Path $ResultsRoot "shard-$ShardIndex"
New-Item -ItemType Directory -Force -Path $results | Out-Null

$rspDir = Join-Path $ResultsRoot 'rsp'
New-Item -ItemType Directory -Force -Path $rspDir | Out-Null
$rsp = Join-Path $rspDir "shard-$ShardIndex.rsp"
$rspLines = @(
    '--configuration'
    'Release'
    '--no-build'
    '--no-restore'
    '--settings'
    $Settings
    '--filter'
    $filter
    '--blame-hang'
    '--blame-hang-timeout'
    '5m'
    '--collect:Code Coverage'
    '--results-directory'
    $results
)
# UTF-8 without BOM. A BOM becomes part of the first argument.
$utf8 = New-Object System.Text.UTF8Encoding $false
[IO.File]::WriteAllLines((Join-Path (Get-Location) $rsp), $rspLines, $utf8)

for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
    Write-Host "Integration shard $ShardIndex attempt $attempt of $MaxAttempts"
    & dotnet test $Project "@$rsp"
    if ($LASTEXITCODE -eq 0) { exit 0 }
    if ($attempt -eq $MaxAttempts) { exit $LASTEXITCODE }
    Write-Host "Integration shard $ShardIndex failed on attempt $attempt, retrying..."
    Start-Sleep -Seconds 5
}
