# Run one class-stable shard of the integration suite.
# Each class stays in a single process so ClassInitialize and DoNotParallelize keep their meaning.
# Shards are separate processes (separate CI jobs), which is what overlaps the serialized classes.
param(
    [Parameter(Mandatory = $true)][int]$ShardIndex,
    [Parameter(Mandatory = $true)][int]$ShardCount,
    [string]$Project = 'tests/Titanium.Web.Proxy.IntegrationTests/Titanium.Web.Proxy.IntegrationTests.csproj',
    [string]$Settings = 'tests/integration-shard.runsettings',
    [string]$WeightsFile = 'tests/integration-shard-weights.json',
    [string]$ResultsRoot = 'coverage/integration',
    [int]$MaxAttempts = 2
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

# Prefer measured/estimated weights so the slowest shard is not the wall clock after
# the build job is split. Fall back to the stable SHA256 bucket when a class has no weight.
function Get-ClassWeightMap([string]$path) {
    $map = @{}
    if (-not (Test-Path -LiteralPath $path)) { return $map }
    $json = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    if ($null -eq $json.weights) { return $map }
    foreach ($prop in $json.weights.PSObject.Properties) {
        $map[$prop.Name] = [double]$prop.Value
    }
    return $map
}

function Get-WeightedShardAssignment([string[]]$names, [hashtable]$weights, [int]$count) {
    $loads = New-Object double[] $count
    $buckets = @{}
    for ($i = 0; $i -lt $count; $i++) { $buckets[$i] = New-Object System.Collections.Generic.List[string] }

    $ordered = $names | Sort-Object {
        $w = if ($weights.ContainsKey($_)) { [double]$weights[$_] } else { 0.0 }
        # Heaviest first. Equal weights keep a stable order via the name.
        '{0:0000000000}-{1}' -f ([int](1000000000 - [Math]::Min($w, 1000000000))), $_
    }

    foreach ($name in $ordered) {
        if ($weights.ContainsKey($name)) {
            $best = 0
            $bestLoad = [double]::MaxValue
            for ($i = 0; $i -lt $count; $i++) {
                if ($loads[$i] -lt $bestLoad) { $bestLoad = $loads[$i]; $best = $i }
            }
            $buckets[$best].Add($name)
            $loads[$best] += [double]$weights[$name]
        }
        else {
            $bucket = Get-StableBucket $name $count
            $buckets[$bucket].Add($name)
            $loads[$bucket] += 1.0
        }
    }

    return @{ Buckets = $buckets; Loads = $loads }
}

# dotnet test --list-tests prints the MSTest display name (the method), not the class.
# [TestClass] in source is the class-stable key, and it does not require starting the test host.
$sourceRoot = 'tests/Titanium.Web.Proxy.IntegrationTests'
$classPattern = [regex]'\[TestClass[^\]]*\](?:\s|//[^\r\n]*[\r\n]+|/\*[\s\S]*?\*/|\[[^\]]*\])*(?:public|internal)\s+(?:sealed\s+|partial\s+|abstract\s+)*class\s+(?<name>\w+)'
$discovered = New-Object System.Collections.Generic.List[string]
$seen = @{}
Get-ChildItem -Path $sourceRoot -Filter *.cs -Recurse -File | ForEach-Object {
    $text = [IO.File]::ReadAllText($_.FullName)
    foreach ($match in $classPattern.Matches($text)) {
        $name = $match.Groups['name'].Value
        if (-not $seen.ContainsKey($name)) {
            $seen[$name] = $true
            $discovered.Add($name)
        }
    }
}

if ($discovered.Count -eq 0) {
    throw "No [TestClass] types found under $sourceRoot"
}

$weightMap = Get-ClassWeightMap $WeightsFile
$assignment = Get-WeightedShardAssignment $discovered.ToArray() $weightMap $ShardCount
$classes = $assignment.Buckets[$ShardIndex]
$load = $assignment.Loads[$ShardIndex]

Write-Host ("Shard {0}: {1} of {2} test classes (weight {3})" -f $ShardIndex, $classes.Count, $discovered.Count, $load)
if ($classes.Count -eq 0) {
    Write-Host "Shard $ShardIndex has no classes."
    exit 0
}

$prefix = 'Titanium.Web.Proxy.IntegrationTests.'
# This class passes in its own process and fails after the rest of shard 0 has run,
# even one class at a time. Keep it out of that process.
$ownProcess = @{ 'Http1FramingSafetyTests' = $true }
$batch = New-Object System.Collections.Generic.List[string]
$solo = New-Object System.Collections.Generic.List[string]
foreach ($class in $classes) {
    if ($ownProcess.ContainsKey($class)) { $solo.Add($class) } else { $batch.Add($class) }
}

function Write-TestRsp([string]$rspPath, [string]$testFilter, [string]$resultsDir) {
    $lines = @(
        '--configuration', 'Release',
        '--no-build', '--no-restore',
        '--settings', $Settings,
        '--filter', $testFilter,
        '--blame-hang', '--blame-hang-timeout', '5m',
        '--collect:Code Coverage',
        '--results-directory', $resultsDir
    )
    $utf8 = New-Object System.Text.UTF8Encoding $false
    [IO.File]::WriteAllLines((Join-Path (Get-Location) $rspPath), $lines, $utf8)
}

$results = Join-Path $ResultsRoot "shard-$ShardIndex"
New-Item -ItemType Directory -Force -Path $results | Out-Null
$rspDir = Join-Path $ResultsRoot 'rsp'
New-Item -ItemType Directory -Force -Path $rspDir | Out-Null

if ($batch.Count -gt 0) {
    $parts = New-Object System.Collections.Generic.List[string]
    foreach ($class in $batch) {
        $parts.Add("FullyQualifiedName~$prefix$class.")
        $parts.Add("FullyQualifiedName~$prefix$class+")
    }

    # & binds tighter than | in the VSTest filter language, so the class disjunction stays in parentheses.
    $filter = '(' + ($parts -join '|') + ')&TestCategory!=Slow'
    $rsp = Join-Path $rspDir "shard-$ShardIndex.rsp"
    Write-TestRsp $rsp $filter $results

    $batchOk = $false
    for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
        Write-Host "Integration shard $ShardIndex attempt $attempt of $MaxAttempts"
        & dotnet test $Project "@$rsp"
        if ($LASTEXITCODE -eq 0) { $batchOk = $true; break }
        if ($attempt -eq $MaxAttempts) { exit $LASTEXITCODE }
        Write-Host "Integration shard $ShardIndex failed on attempt $attempt, retrying..."
        Start-Sleep -Seconds 5
    }
    if (-not $batchOk) { exit 1 }
}

foreach ($class in $solo) {
    $soloFilter = "(FullyQualifiedName~$prefix$class.|FullyQualifiedName~$prefix$class+)&TestCategory!=Slow"
    $soloRsp = Join-Path $rspDir "shard-$ShardIndex-$class.rsp"
    $soloResults = Join-Path $results $class
    Write-Host "Isolated class $class"
    Write-TestRsp $soloRsp $soloFilter $soloResults
    & dotnet test $Project "@$soloRsp"
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

exit 0
