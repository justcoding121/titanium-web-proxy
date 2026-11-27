# Compares a fresh BenchmarkDotNet full JSON export to a committed baseline.
# Prints the median ratio (fresh / baseline) for each benchmark present in both files.
param(
    [Parameter(Mandatory = $true)]
    [string] $Baseline,
    [Parameter(Mandatory = $true)]
    [string] $Fresh
)

$ErrorActionPreference = "Stop"

function Read-Medians([string] $path) {
    $json = Get-Content -Raw -Path $path | ConvertFrom-Json
    $rows = @{}
    foreach ($benchmark in $json.Benchmarks) {
        $name = $benchmark.FullName
        if ([string]::IsNullOrWhiteSpace($name)) {
            $name = $benchmark.DisplayInfo
        }

        $median = $benchmark.Statistics.Median
        if ($null -ne $name -and $null -ne $median) {
            $rows[$name] = [double]$median
        }
    }

    return $rows
}

$baseline = Read-Medians $Baseline
$fresh = Read-Medians $Fresh
foreach ($name in ($baseline.Keys | Sort-Object)) {
    if (-not $fresh.ContainsKey($name)) {
        Write-Output ("missing`t{0}" -f $name)
        continue
    }

    $before = $baseline[$name]
    $after = $fresh[$name]
    $ratio = if ($before -eq 0) { 0 } else { $after / $before }
    Write-Output ("{0:N3}x`t{1:N0} -> {2:N0}`t{3}" -f $ratio, $before, $after, $name)
}
