# Fail when a product assembly's line coverage is below tests/coverage-floors.json.
# Prefers SonarCloud component coverage (same metric as the merge gate). Falls back to
# VS coverage XML with tests/coverage-exclusions.txt applied when Sonar is unavailable.
param(
    [string]$CoverageXml = 'coverage/coverage.xml',
    [string]$Floors = 'tests/coverage-floors.json',
    [string]$Exclusions = 'tests/coverage-exclusions.txt',
    [string]$SonarProjectKey = 'justcoding121_titanium-web-proxy',
    [string]$PullRequest = '',
    [string]$Branch = ''
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$floorsDoc = Get-Content -LiteralPath $Floors -Raw | ConvertFrom-Json
$allow = [double]$floorsDoc.dropAllowance
$failed = $false
$floorsAdvisory = $false

$assemblies = @($floorsDoc.assemblies.PSObject.Properties) | Sort-Object { $_.Name.Length } -Descending

$componentDirs = @{
    'Titanium.Web.Proxy' = 'src/Titanium.Web.Proxy'
    'Titanium.Web.Proxy.Configuration' = 'src/Titanium.Web.Proxy.Configuration'
    'Titanium.Cli' = 'src/Titanium.Cli'
    'Titanium.Plus' = 'src/Titanium.Plus'
    'Titanium.Gateway' = 'src/Titanium.Gateway'
    'Titanium.Manager' = 'src/Titanium.Manager'
    'Titanium.Xds' = 'src/Titanium.Xds'
    'Titanium.Inspector' = 'src/Titanium.Inspector'
    'Titanium.Web.Proxy.Scripting' = 'src/Titanium.Web.Proxy.Scripting'
    'Titanium.Cli.GeoIp' = 'src/Titanium.Cli.GeoIp'
}

if (-not $PullRequest -and $env:GITHUB_EVENT_NAME -eq 'pull_request' -and $env:GITHUB_REF_NAME) {
    # github.ref_name is the PR number on pull_request events when using merge refs; prefer explicit input.
}
if (-not $PullRequest -and $env:TWP_SONAR_PULL_REQUEST) {
    $PullRequest = $env:TWP_SONAR_PULL_REQUEST
}
if (-not $Branch -and $env:TWP_SONAR_BRANCH) {
    $Branch = $env:TWP_SONAR_BRANCH
}

function Get-CoverageFromSonar {
    if (-not $env:SONAR_TOKEN) { return $null }

    $auth = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes("$($env:SONAR_TOKEN):"))
    $headers = @{ Authorization = "Basic $auth" }
    $map = @{}

    foreach ($prop in $assemblies) {
        $name = $prop.Name
        if (-not $componentDirs.ContainsKey($name)) { continue }
        $component = "${SonarProjectKey}:$($componentDirs[$name])"
        $uri = "https://sonarcloud.io/api/measures/component?component=$([uri]::EscapeDataString($component))&metricKeys=coverage"
        if ($PullRequest) {
            $uri += "&pullRequest=$([uri]::EscapeDataString($PullRequest))"
        }
        elseif ($Branch) {
            $uri += "&branch=$([uri]::EscapeDataString($Branch))"
        }

        try {
            $r = Invoke-RestMethod -Headers $headers -Uri $uri
            $cov = ($r.component.measures | Where-Object { $_.metric -eq 'coverage' } | Select-Object -First 1).value
            if ($null -ne $cov) {
                $map[$name] = [double]$cov
                continue
            }
        }
        catch {
            # PR analysis often omits unchanged assemblies — fall back to the main branch measure.
        }

        try {
            $uriMain = "https://sonarcloud.io/api/measures/component?component=$([uri]::EscapeDataString($component))&metricKeys=coverage"
            $r = Invoke-RestMethod -Headers $headers -Uri $uriMain
            $cov = ($r.component.measures | Where-Object { $_.metric -eq 'coverage' } | Select-Object -First 1).value
            if ($null -ne $cov) { $map[$name] = [double]$cov }
        }
        catch {
            Write-Host ("{0}: Sonar measure unavailable ({1})" -f $name, $_.Exception.Message)
        }
    }

    if ($map.Count -eq 0) { return $null }
    return $map
}

function Test-ExcludedPath([string]$path, [string[]]$exclusionGlobs) {
    if ([string]::IsNullOrWhiteSpace($path)) { return $false }
    $normalized = ($path -replace '\\', '/')
    foreach ($glob in $exclusionGlobs) {
        $pattern = ($glob -replace '\\', '/').Trim()
        if ([string]::IsNullOrWhiteSpace($pattern)) { continue }
        $core = $pattern -replace '^\*\*/', ''
        if ($core.EndsWith('/**')) {
            $dir = $core.Substring(0, $core.Length - 3)
            if ($dir -and $normalized.IndexOf($dir, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
                return $true
            }
            continue
        }

        if ($core -and (
                $normalized.EndsWith('/' + $core, [StringComparison]::OrdinalIgnoreCase) -or
                $normalized.EndsWith($core, [StringComparison]::OrdinalIgnoreCase) -or
                $normalized.IndexOf('/' + $core, [StringComparison]::OrdinalIgnoreCase) -ge 0)) {
            return $true
        }
    }
    return $false
}

function Get-CoverageFromXml {
    if (-not (Test-Path -LiteralPath $CoverageXml)) { return $null }

    $exclusionGlobs = @()
    if (Test-Path -LiteralPath $Exclusions) {
        $exclusionGlobs = @(
            Get-Content -LiteralPath $Exclusions |
                ForEach-Object { $_.Trim() } |
                Where-Object { $_ -and -not $_.StartsWith('#') }
        )
    }

    $aliases = @{
        'Titanium.Cli' = @('titanium.dll')
        'Titanium.Gateway' = @('titanium-gateway.dll')
        'Titanium.Manager' = @('titanium-manager.dll')
        'Titanium.Inspector' = @('TitaniumInspector.dll')
    }

    [xml]$doc = Get-Content -LiteralPath $CoverageXml
    $sourcesById = @{}
    foreach ($source in @($doc.SelectNodes('//source_file'))) {
        $id = [string]$source.id
        $path = [string]$source.path
        if ($id -and $path) { $sourcesById[$id] = $path }
    }

    $modules = @($doc.SelectNodes('//module'))
    $claimed = New-Object 'System.Collections.Generic.HashSet[string]'
    $map = @{}

    foreach ($prop in $assemblies) {
        $name = $prop.Name
        $candidates = @("$name.dll")
        if ($aliases.ContainsKey($name)) { $candidates += $aliases[$name] }
        $matched = @()
        foreach ($module in $modules) {
            $moduleName = [string]$module.name
            if ([string]::IsNullOrWhiteSpace($moduleName)) { continue }
            if ($claimed.Contains($moduleName)) { continue }
            $hit = $false
            foreach ($dll in $candidates) {
                if ($moduleName -eq $dll -or $moduleName -like $dll) { $hit = $true; break }
            }
            if ($hit) {
                $matched += $module
                [void]$claimed.Add($moduleName)
            }
        }
        if ($matched.Count -eq 0) { continue }

        $covered = 0.0
        $total = 0.0
        foreach ($module in $matched) {
            foreach ($range in @($module.SelectNodes('.//range'))) {
                $sourceId = [string]$range.source_id
                if (-not $sourceId) { $sourceId = [string]$range.source }
                if ($sourceId -and $sourcesById.ContainsKey($sourceId) -and (Test-ExcludedPath $sourcesById[$sourceId] $exclusionGlobs)) {
                    continue
                }

                $lines = 1
                if ($range.start_line -and $range.end_line) {
                    $lines = [int]$range.end_line - [int]$range.start_line + 1
                    if ($lines -lt 1) { $lines = 1 }
                }
                $total += $lines
                $flag = [string]$range.covered
                if ($flag -eq 'yes' -or $flag -eq 'true' -or $flag -eq 'partial') { $covered += $lines }
            }
        }

        if ($total -eq 0) {
            foreach ($module in $matched) {
                if ($module.lines_covered -and $module.lines_not_covered) {
                    $linesCovered = [double]$module.lines_covered
                    $linesPartial = 0.0
                    if ($module.lines_partially_covered) { $linesPartial = [double]$module.lines_partially_covered }
                    $linesNot = [double]$module.lines_not_covered
                    $covered += $linesCovered + (0.5 * $linesPartial)
                    $total += $linesCovered + $linesPartial + $linesNot
                }
            }
        }

        if ($total -gt 0) {
            $map[$name] = [math]::Round(100.0 * $covered / $total, 2)
        }
    }
    return $map
}

$coverageMap = Get-CoverageFromSonar
$sourceLabel = 'sonar'
if (-not $coverageMap -or $coverageMap.Count -eq 0) {
    $coverageMap = Get-CoverageFromXml
    $sourceLabel = 'coverage.xml'
}

if (-not $coverageMap -or $coverageMap.Count -eq 0) {
    Write-Error 'No coverage measures available from Sonar or coverage.xml'
    exit 1
}

foreach ($prop in $assemblies) {
    $name = $prop.Name
    $floor = [double]$prop.Value
    if (-not $coverageMap.ContainsKey($name)) {
        Write-Host ("{0}: (not in report via {1})" -f $name, $sourceLabel)
        continue
    }

    $pct = [math]::Round([double]$coverageMap[$name], 2)
    $limit = [math]::Round($floor - $allow, 2)
    Write-Host ("{0}: {1}% (floor {2}, via {3})" -f $name, $pct, $floor, $sourceLabel)
    if ($pct + 0.001 -lt $limit) {
        # Advisory only for a fork pull request that had to use coverage.xml because the
        # Sonar token is withheld. A present token (including a same-repo run whose token
        # is missing or rotated, and any push) stays strict even when the XML tally is the
        # only measure we could read.
        $advisory = ($env:TWP_FLOORS_ADVISORY -eq 'true') -and
            [string]::IsNullOrWhiteSpace($env:SONAR_TOKEN) -and
            ($sourceLabel -eq 'coverage.xml')
        if ($advisory) {
            Write-Host ("::warning::{0} line coverage {1} is below {2} (coverage.xml on a fork pull request; advisory)" -f $name, $pct, $limit)
            $script:floorsAdvisory = $true
        }
        else {
            Write-Host ("::error::{0} line coverage {1} is below {2}" -f $name, $pct, $limit)
            $failed = $true
        }
    }
}

if ($script:floorsAdvisory) {
    Write-Host '::notice::Coverage floors are measured on develop with the Sonar metric. A maintainer can mirror this pull request with tools/ci/Mirror-ForkPr.ps1 so that metric is applied.'
}

if ($failed) { exit 1 }
exit 0
