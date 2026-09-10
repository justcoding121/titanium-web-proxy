# Publish product + heavier peer numbers into wiki/Performance.md and regenerate charts.
# Requires completed GHA CSV downloads under tools/RpsLoadProbe/results/gha-dl/<runId>/.
#
# Wiki-grade (repeats=3, warmup 2s / measure 8s, c=8,16,32,64):
#   compare-product (3 OS × shards), compare-saturation, compare-bodies, compare-post,
#   compare-lossy, compare-tls-cost, compare-arch, compare-grpc
#
# Download artifacts into gha-dl/<runId>/rps-csv-{os}[-shard-*]/ then:
#   pwsh tools/RpsLoadProbe/publish-rps-peers.ps1 -ProductRunIds 111,222,... -PasteHeavier

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string[]] $ProductRunIds,
    # Comma-separated or repeated; Win+Linux (+shards) for each heavier mode.
    [string[]] $SaturationRunIds = @(),
    [string[]] $BodiesRunIds = @(),
    [string[]] $PostRunIds = @(),
    [string[]] $LossyRunIds = @(),
    [string[]] $TlsRunIds = @(),
    [string[]] $ArchRunIds = @(),
    [string[]] $GrpcRunIds = @(),
    [switch] $PasteHeavier,
    [string] $HeadSha = '9a2b3a1e',
    [string] $GhaDlRoot = 'tools/RpsLoadProbe/results/gha-dl'
)

$ErrorActionPreference = 'Stop'

function Expand-Ids([string[]] $ids) {
    $out = @()
    foreach ($raw in $ids) {
        if (-not $raw) { continue }
        foreach ($p in ($raw -split ',')) {
            $t = $p.Trim()
            if ($t) { $out += $t }
        }
    }
    return $out
}

function Resolve-Py {
    foreach ($c in @('py', 'python', 'python3')) {
        $cmd = Get-Command $c -ErrorAction SilentlyContinue
        if ($cmd) { return $cmd.Source }
    }
    throw 'Python not found (tried py, python, python3)'
}

$ProductRunIds = Expand-Ids $ProductRunIds
$SaturationRunIds = Expand-Ids $SaturationRunIds
$BodiesRunIds = Expand-Ids $BodiesRunIds
$PostRunIds = Expand-Ids $PostRunIds
$LossyRunIds = Expand-Ids $LossyRunIds
$TlsRunIds = Expand-Ids $TlsRunIds
$ArchRunIds = Expand-Ids $ArchRunIds
$GrpcRunIds = Expand-Ids $GrpcRunIds

$primary = $ProductRunIds[0]
$root = Join-Path $GhaDlRoot $primary
if (-not (Test-Path $root)) {
    throw "Missing product CSV root: $root"
}

$py = Resolve-Py
Write-Host "Product CSV runs: $($ProductRunIds -join ', ')" -ForegroundColor Cyan
Write-Host "Python: $py" -ForegroundColor DarkGray

$runIdsCsv = ($ProductRunIds -join ',')
pwsh tools/RpsLoadProbe/paste-compare-product-wiki.ps1 `
    -RunIds $runIdsCsv `
    -ResultsRoot $GhaDlRoot `
    -HeadSha $HeadSha `
    -PrimaryRunId $primary `
    -OutFile tools/RpsLoadProbe/results/wiki-paste-out.txt

pwsh tools/RpsLoadProbe/apply-wiki-paste.ps1 `
    -PasteFile tools/RpsLoadProbe/results/wiki-paste-out.txt `
    -HeadSha $HeadSha `
    -PrimaryRunId $primary

if ($PasteHeavier -or $SaturationRunIds.Count -or $BodiesRunIds.Count) {
    $env:PYTHONIOENCODING = 'utf-8'
    $heavierArgs = @()
    if ($SaturationRunIds.Count) { $heavierArgs += @('--saturation', ($SaturationRunIds -join ',')) }
    if ($BodiesRunIds.Count) { $heavierArgs += @('--bodies', ($BodiesRunIds -join ',')) }
    if ($PostRunIds.Count) { $heavierArgs += @('--post', ($PostRunIds -join ',')) }
    if ($LossyRunIds.Count) { $heavierArgs += @('--lossy', ($LossyRunIds -join ',')) }
    if ($TlsRunIds.Count) { $heavierArgs += @('--tls', ($TlsRunIds -join ',')) }
    if ($ArchRunIds.Count) { $heavierArgs += @('--arch', ($ArchRunIds -join ',')) }
    & $py tools/RpsLoadProbe/paste-heavier-wiki.py @heavierArgs
}

& $py -m pip install -q -r tools/RpsLoadProbe/requirements-charts.txt

$practicalArgs = @('--out-dir', 'wiki/images', '--title-suffix', "@ $HeadSha")
foreach ($id in $ProductRunIds) {
    $practicalArgs += @('--results-root', (Join-Path $GhaDlRoot $id))
}
foreach ($id in $BodiesRunIds) {
    $practicalArgs += @('--bodies-root', (Join-Path $GhaDlRoot $id))
}
foreach ($id in $PostRunIds) {
    $practicalArgs += @('--post-root', (Join-Path $GhaDlRoot $id))
}
foreach ($id in $ArchRunIds) {
    $practicalArgs += @('--arch-root', (Join-Path $GhaDlRoot $id))
}
foreach ($id in $GrpcRunIds) {
    $practicalArgs += @('--grpc-root', (Join-Path $GhaDlRoot $id))
}

& $py tools/RpsLoadProbe/render-practical-charts.py @practicalArgs

& $py tools/RpsLoadProbe/render-product-matrix-charts.py `
    --from-wiki wiki/Performance.md `
    --out-dir wiki/images

if ($BodiesRunIds.Count -and $PostRunIds.Count -and $LossyRunIds.Count -and $TlsRunIds.Count -and $ArchRunIds.Count) {
    $heavierChartArgs = @('--out-dir', 'wiki/images')
    foreach ($id in $BodiesRunIds) { $heavierChartArgs += @('--bodies-root', (Join-Path $GhaDlRoot $id)) }
    foreach ($id in $PostRunIds) { $heavierChartArgs += @('--post-root', (Join-Path $GhaDlRoot $id)) }
    foreach ($id in $LossyRunIds) { $heavierChartArgs += @('--lossy-root', (Join-Path $GhaDlRoot $id)) }
    foreach ($id in $TlsRunIds) { $heavierChartArgs += @('--tls-root', (Join-Path $GhaDlRoot $id)) }
    foreach ($id in $ArchRunIds) { $heavierChartArgs += @('--arch-root', (Join-Path $GhaDlRoot $id)) }
    & $py tools/RpsLoadProbe/render-heavier-charts.py @heavierChartArgs
}

Write-Host 'Done. Review wiki/Performance.md and wiki/images/*.png before commit.' -ForegroundColor Green
