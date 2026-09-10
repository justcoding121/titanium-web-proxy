# Publish HAProxy + Envoy peer numbers into wiki/Performance.md and regenerate charts.
# Requires completed GHA CSV downloads under tools/RpsLoadProbe/results/gha-dl/<runId>/.
#
# 1) Smoke (repeats=1) — after pushing harness + workflow changes:
#    gh workflow run rps-saturation.yml --ref develop -f mode=compare-haproxy-smoke -f runner_os=ubuntu-latest -f repeats=1
#    gh workflow run rps-saturation.yml --ref develop -f mode=compare-haproxy-smoke -f runner_os=macos-15-intel -f repeats=1
#    gh workflow run rps-saturation.yml --ref develop -f mode=compare-haproxy-smoke -f runner_os=windows-latest -f repeats=1
#    (repeat for compare-envoy-smoke)
#
# 2) Full publish (repeats=3, warmup 2s / measure 8s, c=8,16,32,64):
#    compare-product, compare-saturation, compare-bodies, compare-post, compare-lossy,
#    compare-tls-cost, compare-arch, compare-grpc
#
# 3) Download artifacts into gha-dl/<runId>/rps-csv-{ubuntu-latest,windows-latest,macos-15-intel}/
#
# 4) Run this script with -ProductRunId and heavier run IDs.

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string[]] $ProductRunIds,
    [string] $SaturationRunId,
    [string] $BodiesRunId,
    [string] $PostRunId,
    [string] $LossyRunId,
    [string] $TlsRunId,
    [string] $ArchRunId,
    [string] $GrpcRunId,
    [string] $HeadSha = (git rev-parse --short HEAD),
    [string] $GhaDlRoot = 'tools/RpsLoadProbe/results/gha-dl'
)

$ErrorActionPreference = 'Stop'
$primary = $ProductRunIds[0]
$root = Join-Path $GhaDlRoot $primary

if (-not (Test-Path $root)) {
    throw "Missing product CSV root: $root"
}

Write-Host "Product CSV runs: $($ProductRunIds -join ', ')" -ForegroundColor Cyan

pwsh tools/RpsLoadProbe/paste-compare-product-wiki.ps1 `
    -RunIds $ProductRunIds `
    -ResultsRoot $GhaDlRoot `
    -HeadSha $HeadSha `
    -PrimaryRunId $primary `
    -OutFile tools/RpsLoadProbe/results/wiki-paste-out.txt

pwsh tools/RpsLoadProbe/apply-wiki-paste.ps1 `
    -PasteFile tools/RpsLoadProbe/results/wiki-paste-out.txt `
    -HeadSha $HeadSha `
    -PrimaryRunId $primary

if ($SaturationRunId -or $BodiesRunId) {
    $env:PYTHONIOENCODING = 'utf-8'
    python3 tools/RpsLoadProbe/paste-heavier-wiki.py
}

pip install -q -r tools/RpsLoadProbe/requirements-charts.txt

$practicalArgs = @(
    '--results-root', (Join-Path $GhaDlRoot $primary),
    '--out-dir', 'wiki/images',
    '--title-suffix', "@ $HeadSha"
)
if ($PostRunId) {
    $practicalArgs += @('--post-root', (Join-Path $GhaDlRoot $PostRunId))
}
if ($ArchRunId) {
    $practicalArgs += @('--arch-root', (Join-Path $GhaDlRoot $ArchRunId))
}
if ($GrpcRunId) {
    $practicalArgs += @('--grpc-root', (Join-Path $GhaDlRoot $GrpcRunId))
}

python3 tools/RpsLoadProbe/render-practical-charts.py @practicalArgs

python3 tools/RpsLoadProbe/render-product-matrix-charts.py `
    --from-wiki wiki/Performance.md `
    --out-dir wiki/images

if ($BodiesRunId -and $PostRunId -and $LossyRunId -and $TlsRunId -and $ArchRunId) {
    python3 tools/RpsLoadProbe/render-heavier-charts.py `
        --bodies-root (Join-Path $GhaDlRoot $BodiesRunId) `
        --post-root (Join-Path $GhaDlRoot $PostRunId) `
        --lossy-root (Join-Path $GhaDlRoot $LossyRunId) `
        --tls-root (Join-Path $GhaDlRoot $TlsRunId) `
        --arch-root (Join-Path $GhaDlRoot $ArchRunId) `
        --out-dir wiki/images
}

Write-Host 'Done. Review wiki/Performance.md and wiki/images/*.png before commit.' -ForegroundColor Green
