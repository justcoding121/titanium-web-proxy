# Mirror a fork pull request onto a same-repo branch so SonarCloud and the Sonar coverage
# floors can run. Fork pull_request workflows do not receive SONAR_TOKEN.
#
# Maintainer tool. CI does not run it. Never rebases or amends: the contributor's commits
# stay as they are, and any review fixes are new commits on top. Merge the resulting pull
# request with a merge commit (not squash or rebase) so the original pull request shows as
# merged with those commits.
#
# Refuses, unless the matching switch is passed after the diff has been read:
#   - PolyForm Noncommercial paths (a maintainer-authored mirror would otherwise pass the path guard)
#   - files that execute with repository secrets on a same-repo pull request
param(
    [int]$Pr = 0,
    [switch]$AllowNcPaths,
    [switch]$AllowBuildFiles,
    [switch]$WhatIf,
    # Local check of the path rules. Does not fetch, push, or open a pull request.
    [switch]$SelfTest
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

function Test-NcPath([string]$path) {
    $p = ($path -replace '\\', '/')
    $prefixes = @(
        'src/Titanium.Plus/',
        'tests/Titanium.Plus.Tests/',
        'src/Titanium.Inspector/',
        'tests/Titanium.Inspector.Tests/',
        'src/Titanium.Xds/',
        'tests/Titanium.Xds.Tests/',
        'src/Titanium.Cli.GeoIp/',
        'tests/Titanium.Cli.GeoIp.Tests/'
    )
    foreach ($prefix in $prefixes) {
        if ($p.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { return $true }
    }
    return $false
}

function Test-BuildAffectingPath([string]$path) {
    $p = ($path -replace '\\', '/')
    if ($p.StartsWith('.github/', [StringComparison]::OrdinalIgnoreCase)) { return $true }
    if ($p.StartsWith('tools/ci/', [StringComparison]::OrdinalIgnoreCase)) { return $true }
    if ($p.EndsWith('.props', [StringComparison]::OrdinalIgnoreCase)) { return $true }
    if ($p.EndsWith('.targets', [StringComparison]::OrdinalIgnoreCase)) { return $true }
    if ($p.EndsWith('.csproj', [StringComparison]::OrdinalIgnoreCase)) { return $true }
    foreach ($name in @('NuGet.config', 'global.json', '.config/dotnet-tools.json')) {
        if ($p.Equals($name, [StringComparison]::OrdinalIgnoreCase)) { return $true }
    }
    return $false
}

if ($SelfTest) {
    $expectNc = @(
        'src/Titanium.Plus/Security/AccessSecurity.cs',
        'tests/Titanium.Plus.Tests/PlusResilienceAndSecurityTests.cs',
        'src/Titanium.Inspector/Localization/strings.it.json',
        'tests/Titanium.Inspector.Tests/Foo.cs',
        'src/Titanium.Xds/NOTICE',
        'tests/Titanium.Xds.Tests/Foo.cs',
        'src/Titanium.Cli.GeoIp/Geo.cs',
        'tests/Titanium.Cli.GeoIp.Tests/Foo.cs'
    )
    $expectBuild = @(
        '.github/workflows/dotnetcore.yml',
        'tools/ci/Mirror-ForkPr.ps1',
        'Directory.Build.props',
        'src/Titanium.Web.Proxy/Titanium.Web.Proxy.csproj',
        'NuGet.config',
        'global.json',
        '.config/dotnet-tools.json',
        'build/custom.targets'
    )
    $expectPlain = @(
        'src/Titanium.Web.Proxy/Routing/RouteMatcher.cs',
        'website/docs/ingress.md',
        'CONTRIBUTING.md'
    )
    $bad = @()
    foreach ($path in $expectNc) {
        if (-not (Test-NcPath $path)) { $bad += "expected NC: $path" }
        if (Test-BuildAffectingPath $path) { $bad += "NC path also classified as build: $path" }
    }
    foreach ($path in $expectBuild) {
        if (-not (Test-BuildAffectingPath $path)) { $bad += "expected build: $path" }
    }
    foreach ($path in $expectPlain) {
        if (Test-NcPath $path) { $bad += "plain path classified as NC: $path" }
        if (Test-BuildAffectingPath $path) { $bad += "plain path classified as build: $path" }
    }
    if ($bad.Count -gt 0) {
        $bad | ForEach-Object { Write-Host $_ }
        exit 1
    }
    Write-Host 'Mirror path rules passed.'
    exit 0
}

if ($Pr -lt 1) { throw 'Pass -Pr <number>, or -SelfTest.' }

if (Get-Variable -Name PSNativeCommandUseErrorActionPreference -Scope Global -ErrorAction SilentlyContinue) {
    $PSNativeCommandUseErrorActionPreference = $false
}

$root = (& git rev-parse --show-toplevel).Trim()
if ($LASTEXITCODE -ne 0 -or -not $root) { throw 'git rev-parse failed; run this from the repository.' }
Set-Location -LiteralPath $root

$meta = gh pr view $Pr --json number,title,url,baseRefName,headRefName,isCrossRepository,author | ConvertFrom-Json
if (-not $meta.isCrossRepository) {
    throw "Pull request #$Pr is not from a fork. Same-repo pull requests already receive the Sonar token."
}

& git fetch origin "pull/$Pr/head"
if ($LASTEXITCODE -ne 0) { throw "git fetch origin pull/$Pr/head failed." }
$headSha = (& git rev-parse FETCH_HEAD).Trim()

& git fetch origin $meta.baseRefName
if ($LASTEXITCODE -ne 0) { throw "git fetch origin $($meta.baseRefName) failed." }

$changed = @(
    & git diff --name-only "origin/$($meta.baseRefName)...$headSha"
)
if ($LASTEXITCODE -ne 0) { throw 'git diff failed.' }

$nc = @($changed | Where-Object { $_ -and (Test-NcPath $_) })
$build = @($changed | Where-Object { $_ -and (Test-BuildAffectingPath $_) })
$refuseNc = ($nc.Count -gt 0) -and (-not $AllowNcPaths)
$refuseBuild = ($build.Count -gt 0) -and (-not $AllowBuildFiles)
$branch = "contrib/pr-$Pr"
$author = $meta.author.login

Write-Host "Branch: $branch"
Write-Host "Head:   $headSha"
Write-Host "Base:   $($meta.baseRefName)"
Write-Host "Author: $author"
Write-Host "Title:  $($meta.title)"
if ($nc.Count -gt 0) {
    Write-Host ("Noncommercial paths ({0}):" -f $(if ($AllowNcPaths) { 'allowed by -AllowNcPaths' } else { 'REFUSE' }))
    $nc | ForEach-Object { Write-Host "  $_" }
}
else {
    Write-Host 'Noncommercial paths: none'
}
if ($build.Count -gt 0) {
    Write-Host ("Build or workflow files ({0}):" -f $(if ($AllowBuildFiles) { 'allowed by -AllowBuildFiles' } else { 'REFUSE' }))
    $build | ForEach-Object { Write-Host "  $_" }
}
else {
    Write-Host 'Build or workflow files: none'
}

$verdict = if ($refuseNc -or $refuseBuild) { 'REFUSE' } else { 'allow' }
Write-Host "Guard: $verdict"

if ($WhatIf) {
    if ($verdict -eq 'allow') {
        Write-Host "WhatIf: would push $branch and open a pull request to $($meta.baseRefName). Nothing was changed."
        exit 0
    }
    Write-Host 'WhatIf: would not open a pull request. Nothing was changed.'
    exit 1
}

if ($verdict -eq 'REFUSE') {
    throw "Refusing to mirror #$Pr. Re-run with -AllowNcPaths and/or -AllowBuildFiles only after reading the listed files."
}

$existing = ''
$existing = (& git rev-parse --verify --quiet "refs/heads/$branch")
if ($existing) { $existing = $existing.Trim() }
if (-not $existing) {
    & git branch $branch $headSha
    if ($LASTEXITCODE -ne 0) { throw "Could not create $branch." }
}
else {
    & git merge-base --is-ancestor $headSha $branch
    if ($LASTEXITCODE -ne 0) {
        throw "Branch $branch already exists and does not contain $headSha. Refusing to move it."
    }
    Write-Host "Branch $branch already contains $headSha; leaving existing commits in place."
}

& git push -u origin $branch
if ($LASTEXITCODE -ne 0) { throw "git push of $branch failed." }

$openJson = gh pr list --head $branch --base $meta.baseRefName --json number,url
if ($LASTEXITCODE -ne 0) { throw 'gh pr list failed.' }
$open = @()
if ($openJson) {
    $parsed = $openJson | ConvertFrom-Json
    if ($null -ne $parsed) { $open = @($parsed) }
}
if ($open.Count -gt 0 -and $open[0].url) {
    Write-Host "Pull request already open: $($open[0].url)"
    exit 0
}

$bodyPath = Join-Path ([System.IO.Path]::GetTempPath()) "mirror-pr-$Pr.md"
@"
Mirrored from #$Pr by @$author so SonarCloud and the coverage floors can run.

Fork pull requests do not receive repository secrets, so ``SONAR_TOKEN`` is empty there and the coverage floors cannot use the Sonar metric.

The contributor's commits are unchanged. Review fixes, if any, are additional commits on this branch.

Merge this pull request with a **merge commit** (not squash or rebase) so #$Pr shows as merged with its original commits.

Original: $($meta.url)
"@ | Set-Content -LiteralPath $bodyPath -Encoding utf8

$created = gh pr create --base $meta.baseRefName --head $branch --title $meta.title --body-file $bodyPath
if ($LASTEXITCODE -ne 0) { throw 'gh pr create failed.' }
Write-Host $created

$comment = "Mirrored to $created so SonarCloud can analyze it. Fork pull requests do not receive the Sonar token. Please review that pull request; it will be merged with a merge commit so this one keeps your commits."
gh pr comment $Pr --body $comment
if ($LASTEXITCODE -ne 0) { throw "Could not comment on #$Pr." }
exit 0
