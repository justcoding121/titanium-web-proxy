# Download the CSV artifacts of one or more RPS suite runs into the layout the paste scripts read:
# tools/RpsLoadProbe/results/gha-dl/<runId>/rps-csv-<os>-shard-<key>/.
param(
    [Parameter(Mandatory)][string[]] $RunIds,
    [string] $Repo = 'justcoding121/titanium-web-proxy',
    [string] $DestRoot = 'tools/RpsLoadProbe/results/gha-dl'
)

$ErrorActionPreference = 'Stop'

$ids = @()
foreach ($raw in $RunIds) {
    foreach ($part in ($raw -split ',')) {
        $t = $part.Trim()
        if ($t) { $ids += $t }
    }
}

foreach ($id in $ids) {
    $dest = Join-Path $DestRoot $id
    New-Item -ItemType Directory -Force -Path $dest | Out-Null
    Write-Host "Downloading run $id -> $dest" -ForegroundColor Cyan
    gh run download $id --repo $Repo --dir $dest --pattern 'rps-csv-*'
    if ($LASTEXITCODE -ne 0) { throw "gh run download failed for $id" }
    $csvs = @(Get-ChildItem $dest -Recurse -Filter 'rps-ramp-*.csv')
    Write-Host ("  {0} CSV file(s) in {1} folder(s)" -f $csvs.Count, @($csvs | ForEach-Object { $_.DirectoryName } | Select-Object -Unique).Count)
}

Write-Host "Done. Paste with publish-rps-peers.ps1 -ProductRunIds <id> ..." -ForegroundColor Green
