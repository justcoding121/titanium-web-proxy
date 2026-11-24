#Requires -Version 5.1
<#
.SYNOPSIS
  Fail the cut unless a successful release-dry-run tested the same git tree.

.DESCRIPTION
  A pull_request dry-run checks out GitHub's test merge commit, so the tree it
  records is the tree that lands on beta or stable when the branch is up to date.
  Commits the release workflow itself adds (a version bump, a Release note commit)
  are peeled off the candidate before the comparison. A product commit is not.

  -Live reads successful release-dry-run.yml runs and their release-dry-run-tree
  artifact. -RunsJson is the same data for the replay test.

  evidence_override is the maintainer break-glass. It requires a reason, writes
  the job summary, and opens an escaped-defect issue.
#>
param(
    [string] $Commit = 'HEAD',
    [string] $RunsJson = '',
    [string] $RunsJsonPath = '',
    [switch] $Live,
    [switch] $SelfTest,
    [string] $EvidenceOverride = '',
    [string] $EvidenceOverrideReason = ''
)

$ErrorActionPreference = 'Stop'

function Write-Summary([string] $Text) {
    if ($env:GITHUB_STEP_SUMMARY) {
        Add-Content -Path $env:GITHUB_STEP_SUMMARY -Value $Text
    }
    Write-Host $Text
}

function Test-ReleaseAutomation([string] $Sha) {
    $msg = (& git log -1 --format=%s $Sha).Trim()
    return $msg -match '^(Release |chore\(release\): bump product version)'
}

function Get-ComparableTree([string] $Sha) {
    $current = (& git rev-parse $Sha).Trim()
    while (Test-ReleaseAutomation $current) {
        $parent = (& git rev-parse "$current^").Trim()
        if ($parent -eq $current) { break }
        $current = $parent
    }
    return (& git rev-parse "$current^{tree}").Trim()
}

function Invoke-BreakGlass([string] $Reason) {
    if ([string]::IsNullOrWhiteSpace($Reason)) {
        Write-Error 'evidence_override requires a reason'
        exit 1
    }
    $body = @"
The release-dry-run tree guard was overridden.

Reason: $Reason

Commit: $Commit
Run: $($env:GITHUB_SERVER_URL)/$($env:GITHUB_REPOSITORY)/actions/runs/$($env:GITHUB_RUN_ID)

This is an escaped-defect. The override is not the default path.
"@
    Write-Summary "### tree-evidence break-glass`n$body"
    if ($env:GH_TOKEN -and $env:GITHUB_REPOSITORY) {
        & gh issue create --repo $env:GITHUB_REPOSITORY --title "escaped-defect: release-dry-run tree guard overridden" --body $body --label escaped-defect
        if ($LASTEXITCODE -ne 0) {
            & gh issue create --repo $env:GITHUB_REPOSITORY --title "escaped-defect: release-dry-run tree guard overridden" --body $body
            if ($LASTEXITCODE -ne 0) { throw 'break-glass could not open an issue' }
        }
    }
    exit 0
}

function Get-RunTrees([string] $Json) {
    $runs = $Json | ConvertFrom-Json
    $trees = @()
    foreach ($run in @($runs)) {
        if ($run.conclusion -and $run.conclusion -ne 'success') { continue }
        if ($run.tree) { $trees += [string]$run.tree }
    }
    return $trees
}

function Assert-Evidence([string] $Candidate, [string[]] $Trees) {
    $want = Get-ComparableTree $Candidate
    Write-Summary "comparable tree $want"
    foreach ($tree in $Trees) {
        if ($tree -eq $want) {
            Write-Summary "release-dry-run tree matches $want"
            exit 0
        }
    }
    Write-Error "no successful release-dry-run tested tree $want"
    exit 1
}

function Invoke-SelfTest {
    $shell = (Get-Process -Id $PID).Path
    $git = (Get-Command git).Source
    $root = Join-Path ([IO.Path]::GetTempPath()) ("tree-evidence-" + [guid]::NewGuid().ToString('n'))
    New-Item -ItemType Directory -Force -Path $root | Out-Null
    $env:GIT_AUTHOR_NAME = 'tree-evidence'
    $env:GIT_AUTHOR_EMAIL = 'tree-evidence@example.com'
    $env:GIT_COMMITTER_NAME = 'tree-evidence'
    $env:GIT_COMMITTER_EMAIL = 'tree-evidence@example.com'
    Push-Location $root
    try {
        & $git init -q
        & $git commit -q --allow-empty -m 'product'
        Set-Content a.txt 'a'
        & $git add a.txt
        & $git commit -q -m 'product file'
        $base = (& $git rev-parse HEAD).Trim()
        $baseTree = (& $git rev-parse 'HEAD^{tree}').Trim()
        $fixturePath = Join-Path $root 'runs.json'
        Set-Content -LiteralPath $fixturePath -Value "[{`"tree`":`"$baseTree`",`"conclusion`":`"success`"}]" -Encoding ascii
        & $shell -NoProfile -File $PSCommandPath -Commit $base -RunsJsonPath $fixturePath
        if ($LASTEXITCODE -ne 0) { throw 'same-tree replay did not pass' }

        Set-Content a.txt 'changed'
        & $git add a.txt
        & $git commit -q -m 'product change'
        $changed = (& $git rev-parse HEAD).Trim()
        & $shell -NoProfile -File $PSCommandPath -Commit $changed -RunsJsonPath $fixturePath
        if ($LASTEXITCODE -eq 0) { throw 'mismatched tree did not fail closed' }

        Set-Content version.txt '8.0.8'
        & $git add version.txt
        & $git commit -q -m 'chore(release): bump product version'
        $bump = (& $git rev-parse HEAD).Trim()
        # The bump's parent is the product change, which is not the evidenced tree.
        & $shell -NoProfile -File $PSCommandPath -Commit $bump -RunsJsonPath $fixturePath
        if ($LASTEXITCODE -eq 0) { throw 'a bump on top of an untested product change passed' }

        & $git checkout -q $base
        Set-Content version.txt '8.0.8'
        & $git add version.txt
        & $git commit -q -m 'chore(release): bump product version'
        $onlyBump = (& $git rev-parse HEAD).Trim()
        & $shell -NoProfile -File $PSCommandPath -Commit $onlyBump -RunsJsonPath $fixturePath
        if ($LASTEXITCODE -ne 0) { throw 'release-automation bump was not excluded' }
    }
    finally {
        Pop-Location
        Remove-Item -Recurse -Force $root -ErrorAction SilentlyContinue
    }

    $tag = & git rev-parse 'v8.0.7-beta^{tree}' 2>$null
    if ($LASTEXITCODE -eq 0 -and $tag) {
        $tag = $tag.Trim()
        $tagFixture = Join-Path ([IO.Path]::GetTempPath()) 'tree-evidence-tag.json'
        Set-Content -LiteralPath $tagFixture -Value "[{`"tree`":`"$tag`",`"conclusion`":`"success`"}]" -Encoding ascii
        & $shell -NoProfile -File $PSCommandPath -Commit 'v8.0.7-beta' -RunsJsonPath $tagFixture
        if ($LASTEXITCODE -ne 0) { throw 'v8.0.7-beta replay against its own tree did not pass' }
        Set-Content -LiteralPath $tagFixture -Value '[{"tree":"0123456789abcdef0123456789abcdef01234567","conclusion":"success"}]' -Encoding ascii
        & $shell -NoProfile -File $PSCommandPath -Commit 'v8.0.7-beta' -RunsJsonPath $tagFixture
        if ($LASTEXITCODE -eq 0) { throw 'v8.0.7-beta replay against a mismatched tree did not fail' }
        Write-Host 'v8.0.7-beta tree replay passed and the mismatch failed closed'
    }
    Write-Host 'tree-evidence self-test ok'
    exit 0
}

if ($SelfTest) { Invoke-SelfTest }

if ($EvidenceOverride -eq 'true' -or $EvidenceOverride -eq '1') {
    Invoke-BreakGlass $EvidenceOverrideReason
}

$trees = @()
if ($RunsJsonPath) {
    $RunsJson = Get-Content -LiteralPath $RunsJsonPath -Raw
}
if ($RunsJson) {
    $trees = @(Get-RunTrees $RunsJson)
}
elseif ($Live) {
    if (-not $env:GH_TOKEN) { throw 'GH_TOKEN is required for a live tree-evidence check' }
    $repo = $env:GITHUB_REPOSITORY
    $json = & gh run list --repo $repo --workflow release-dry-run.yml --status success --limit 30 --json databaseId,headSha,url,conclusion
    if ($LASTEXITCODE -ne 0) { throw 'could not list release-dry-run runs' }
    $runs = $json | ConvertFrom-Json
    $dir = Join-Path ([IO.Path]::GetTempPath()) ("dry-run-tree-" + [guid]::NewGuid().ToString('n'))
    foreach ($run in @($runs)) {
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
        & gh run download $run.databaseId --repo $repo --name release-dry-run-tree --dir $dir 2>$null
        $file = Join-Path $dir 'tree.txt'
        if (Test-Path $file) {
            $trees += (Get-Content -LiteralPath $file -Raw).Trim()
        }
        elseif ($run.headSha) {
            & git cat-file -t "$($run.headSha)^{tree}" 2>$null | Out-Null
            if ($LASTEXITCODE -eq 0) {
                $trees += (& git rev-parse "$($run.headSha)^{tree}").Trim()
            }
        }
        Remove-Item -Recurse -Force $dir -ErrorAction SilentlyContinue
    }
}
else {
    throw 'pass -Live or -RunsJson'
}

Assert-Evidence $Commit $trees
