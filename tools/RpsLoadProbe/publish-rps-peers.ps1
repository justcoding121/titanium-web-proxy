# Publish product + heavier peer numbers into wiki/Performance.md and regenerate practical charts.
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
    [string[]] $WsH1TlsRunIds = @(),
    [string[]] $WsH2RunIds = @(),
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
$WsH1TlsRunIds = Expand-Ids $WsH1TlsRunIds
$WsH2RunIds = Expand-Ids $WsH2RunIds

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
    $heavierArgs += @('--head-sha', $HeadSha)
    & $py tools/RpsLoadProbe/paste-heavier-wiki.py @heavierArgs
}

if ($GrpcRunIds.Count -or $WsH1TlsRunIds.Count -or $WsH2RunIds.Count) {
    $grpcWsArgs = @('--apply', '--head-sha', $HeadSha, '--primary-run-id', $primary)
    foreach ($id in $GrpcRunIds) { $grpcWsArgs += @('--grpc-root', $id) }
    foreach ($id in $WsH1TlsRunIds) { $grpcWsArgs += @('--ws-h1tls-root', $id) }
    foreach ($id in $WsH2RunIds) { $grpcWsArgs += @('--ws-h2-root', $id) }
    & $py tools/RpsLoadProbe/paste-grpc-ws-wiki.py @grpcWsArgs
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

Write-Host 'Done. Review wiki/Performance.md and wiki/images/rps-practical-*.png before commit.' -ForegroundColor Green
