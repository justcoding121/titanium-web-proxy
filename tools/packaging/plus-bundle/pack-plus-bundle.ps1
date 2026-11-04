# Builds Titanium.Plus-<tag>.zip: Plus + Xds + GeoIp + managed deps not already in the CLI publish folder.
param(
    [Parameter(Mandatory = $true)][string]$Tag,
    [Parameter(Mandatory = $true)][string]$CliPublishDir,
    [Parameter(Mandatory = $true)][string]$OutDir,
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$repo = (Resolve-Path (Join-Path $here '..\..\..')).Path
$bundleProj = Join-Path $here 'PlusBundle.csproj'
$stage = Join-Path $OutDir '_plus-bundle-stage'
$publish = Join-Path $stage 'publish'

if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $publish, $OutDir | Out-Null

dotnet publish $bundleProj -c $Configuration -o $publish --self-contained false
if ($LASTEXITCODE -ne 0) { throw "dotnet publish PlusBundle failed ($LASTEXITCODE)" }

# Also copy satellite outputs (ReferenceOutputAssembly=false does not always copy deps on publish).
foreach ($sat in @(
    'src\Titanium.Xds\bin\{0}\net10.0',
    'src\Titanium.Cli.GeoIp\bin\{0}\net10.0',
    'src\Titanium.Plus\bin\{0}\net10.0'
)) {
    $dir = Join-Path $repo ($sat -f $Configuration)
    if (-not (Test-Path $dir)) { continue }
    Get-ChildItem $dir -Filter '*.dll' | ForEach-Object {
        Copy-Item $_.FullName (Join-Path $publish $_.Name) -Force
    }
}

$cliNames = @{}
Get-ChildItem $CliPublishDir -File | ForEach-Object { $cliNames[$_.Name] = $true }

# Never ship core proxy assemblies from Plus's reference set into the Plus bundle.
$exclude = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($n in @(
    'PlusBundle.dll', 'PlusBundle.pdb', 'PlusBundle.deps.json',
    'Titanium.Web.Proxy.dll', 'Titanium.Web.Proxy.Abstractions.dll',
    'Titanium.Web.Proxy.Configuration.dll', 'Titanium.Web.Proxy.Scripting.dll',
    'titanium.dll'
)) { [void]$exclude.Add($n) }

$files = New-Object System.Collections.Generic.List[string]
Get-ChildItem $publish -File | Where-Object {
    $_.Extension -in '.dll', '.json' -and
    -not $cliNames.ContainsKey($_.Name) -and
    -not $exclude.Contains($_.Name) -and
    -not $_.Name.EndsWith('.pdb', [StringComparison]::OrdinalIgnoreCase)
} | Sort-Object Name | ForEach-Object {
    $files.Add($_.Name)
}

if ($files.Count -lt 1 -or -not ($files -contains 'Titanium.Plus.dll')) {
    throw "Plus bundle is missing Titanium.Plus.dll (files=$($files.Count))"
}

$listPath = Join-Path $publish 'Titanium.Plus.files'
$files | Set-Content -Path $listPath -Encoding utf8
$files.Add('Titanium.Plus.files')

$zip = Join-Path $OutDir "Titanium.Plus-$Tag.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::Open($zip, 'Create')
try {
    foreach ($name in $files) {
        $src = Join-Path $publish $name
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $src, $name) | Out-Null
    }
}
finally {
    $archive.Dispose()
}

Write-Host "Wrote $zip ($($files.Count) entries)"
Get-FileHash $zip -Algorithm SHA256 | Format-List
