#Requires -Version 5.1
<#
.SYNOPSIS
  Fail the cut unless a successful workflow run tested the same git tree.

.DESCRIPTION
  A pull_request dry-run checks out GitHub's test merge commit, so the tree it
  records is the tree that lands on beta or stable when the branch is up to date.
  Commits the release workflow itself adds (a version bump, a Release note commit)
  are peeled off the candidate before the comparison. A product commit is not.

  -Live reads successful runs of -Workflow (default release-dry-run.yml). For
  pull_request events it downloads -Artifact (default release-dry-run-tree). For
  schedule, push, and workflow_dispatch it uses git rev-parse <head_sha>^{tree}
  (requires fetch-depth 0). Optional -Job requires that job's conclusion success.
  Optional -MaxAgeHours filters by run created_at. -Events defaults to pull_request.

  -RunsJson is the same data for the replay test.

  evidence_override is the maintainer break-glass. It requires a reason, writes
  the job summary, and opens an escaped-defect issue.

  -Soft exits 1 without Write-Error when no matching tree is found (probe mode).
#>
param(
    [string] $Commit = 'HEAD',
    [string] $RunsJson = '',
    [string] $RunsJsonPath = '',
    [switch] $Live,
    [switch] $SelfTest,
    [switch] $Soft,
    [string] $EvidenceOverride = '',
    [string] $EvidenceOverrideReason = '',
    [string] $Workflow = 'release-dry-run.yml',
    [string] $Job = '',
    [string[]] $Events = @('pull_request'),
    [int] $MaxAgeHours = 0,
    [string] $Artifact = 'release-dry-run-tree'
)

$ErrorActionPreference = 'Stop'

function Write-Summary([string] $Text) {
    if ($env:GITHUB_STEP_SUMMARY) {
        Add-Content -Path $env:GITHUB_STEP_SUMMARY -Value $Text
    }
    Write-Host $Text
}

function Get-EventList {
    $list = @()
    foreach ($e in $Events) {
        foreach ($part in (($e -split ',') | ForEach-Object { $_.Trim() } | Where-Object { $_ })) {
            $list += $part
        }
    }
    if ($list.Count -eq 0) { $list = @('pull_request') }
    return ,$list
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

function Test-RunAge([object] $Run) {
    if ($MaxAgeHours -le 0) { return $true }
    $created = $Run.created_at
    if (-not $created) { return $true }
    $when = [datetime]::Parse([string]$created, $null, [System.Globalization.DateTimeStyles]::RoundtripKind)
    return (([datetime]::UtcNow - $when.ToUniversalTime()).TotalHours -le $MaxAgeHours)
}

function Test-RunEvent([object] $Run, [string[]] $Allowed) {
    $ev = [string]$Run.event
    if (-not $ev) { return $true }
    return $Allowed -contains $ev
}

function Test-RunJob([object] $Run) {
    if ([string]::IsNullOrWhiteSpace($Job)) { return $true }
    if ($Run.PSObject.Properties.Name -contains 'job' -and $Run.job) {
        $jobName = [string]$Run.job
        $jobConclusion = if ($Run.PSObject.Properties.Name -contains 'job_conclusion') { [string]$Run.job_conclusion } else { 'success' }
        return ($jobName -eq $Job -and $jobConclusion -eq 'success')
    }
    if ($Run.PSObject.Properties.Name -contains 'jobs') {
        foreach ($j in @($Run.jobs)) {
            if ([string]$j.name -eq $Job -and [string]$j.conclusion -eq 'success') { return $true }
        }
        return $false
    }
    # Live path attaches job_ok after the jobs API check.
    if ($Run.PSObject.Properties.Name -contains 'job_ok') {
        return [bool]$Run.job_ok
    }
    return $true
}

function Get-RunTrees([string] $Json, [string[]] $AllowedEvents) {
    $runs = $Json | ConvertFrom-Json
    $trees = @()
    foreach ($run in @($runs)) {
        if ($run.conclusion -and $run.conclusion -ne 'success') { continue }
        if (-not (Test-RunEvent $run $AllowedEvents)) { continue }
        if (-not (Test-RunAge $run)) { continue }
        if (-not (Test-RunJob $run)) { continue }
        if ($run.tree) { $trees += [string]$run.tree }
    }
    return $trees
}

function Get-TreeFromHeadSha([string] $HeadSha) {
    if ([string]::IsNullOrWhiteSpace($HeadSha)) { return $null }
    & git cat-file -t "$HeadSha^{tree}" 2>$null | Out-Null
    if ($LASTEXITCODE -ne 0) { return $null }
    return (& git rev-parse "$HeadSha^{tree}").Trim()
}

function Get-TreeFromArtifact([int64] $RunId, [string] $Repo, [string] $ArtifactName) {
    $dir = Join-Path ([IO.Path]::GetTempPath()) ("tree-art-" + [guid]::NewGuid().ToString('n'))
    try {
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
        & gh run download $RunId --repo $Repo --name $ArtifactName --dir $dir 2>$null
        $file = Join-Path $dir 'tree.txt'
        if (Test-Path $file) {
            return (Get-Content -LiteralPath $file -Raw).Trim()
        }
    }
    finally {
        Remove-Item -Recurse -Force $dir -ErrorAction SilentlyContinue
    }
    return $null
}

function Test-LiveJobSuccess([int64] $RunId, [string] $Repo, [string] $JobName) {
    if ([string]::IsNullOrWhiteSpace($JobName)) { return $true }
    $json = & gh api "repos/$Repo/actions/runs/$RunId/jobs?per_page=100"
    if ($LASTEXITCODE -ne 0) { return $false }
    $payload = $json | ConvertFrom-Json
    foreach ($j in @($payload.jobs)) {
        if ([string]$j.name -eq $JobName) {
            return ([string]$j.conclusion -eq 'success')
        }
    }
    return $false
}

function Assert-Evidence([string] $Candidate, [string[]] $Trees) {
    $want = Get-ComparableTree $Candidate
    Write-Summary "comparable tree $want (workflow=$Workflow artifact=$Artifact job=$Job)"
    foreach ($tree in $Trees) {
        if ($tree -eq $want) {
            Write-Summary "tree evidence matches $want"
            exit 0
        }
    }
    $msg = "no successful $Workflow tested tree $want"
    if ($Soft) {
        Write-Host $msg
        exit 1
    }
    Write-Error $msg
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

        # New parameters: Events, Job, MaxAgeHours, Workflow, Artifact, Soft
        $fresh = [datetime]::UtcNow.ToString('o')
        $stale = ([datetime]::UtcNow.AddHours(-100)).ToString('o')
        $paramFixture = Join-Path $root 'params.json'
        Set-Content -LiteralPath $paramFixture -Value @"
[
  {"tree":"$baseTree","conclusion":"success","event":"schedule","created_at":"$fresh","job":"rps-publish-gate","job_conclusion":"success"},
  {"tree":"$baseTree","conclusion":"success","event":"pull_request","created_at":"$fresh","job":"merge-gate","job_conclusion":"success"},
  {"tree":"$baseTree","conclusion":"success","event":"schedule","created_at":"$stale","job":"rps-publish-gate","job_conclusion":"success"}
]
"@ -Encoding ascii

        & $shell -NoProfile -File $PSCommandPath -Commit $onlyBump -RunsJsonPath $paramFixture `
            -Workflow rps-edition-gate.yml -Events schedule,push,workflow_dispatch -Job rps-publish-gate -MaxAgeHours 48
        if ($LASTEXITCODE -ne 0) { throw 'schedule event + job + MaxAgeHours replay did not pass' }

        $staleOnly = Join-Path $root 'stale.json'
        Set-Content -LiteralPath $staleOnly -Value @"
[{"tree":"$baseTree","conclusion":"success","event":"schedule","created_at":"$stale","job":"rps-publish-gate","job_conclusion":"success"}]
"@ -Encoding ascii
        & $shell -NoProfile -File $PSCommandPath -Commit $onlyBump -RunsJsonPath $staleOnly `
            -Workflow rps-edition-gate.yml -Events schedule -Job rps-publish-gate -MaxAgeHours 48 -Soft
        if ($LASTEXITCODE -eq 0) { throw 'stale MaxAgeHours run was accepted' }

        & $shell -NoProfile -File $PSCommandPath -Commit $onlyBump -RunsJsonPath $paramFixture `
            -Workflow dotnetcore.yml -Events pull_request -Job merge-gate -Artifact tree-dotnet
        if ($LASTEXITCODE -ne 0) { throw 'pull_request + Job merge-gate replay did not pass' }

        & $shell -NoProfile -File $PSCommandPath -Commit $onlyBump -RunsJsonPath $paramFixture `
            -Workflow dotnetcore.yml -Events pull_request -Job missing-job -Soft
        if ($LASTEXITCODE -eq 0) { throw 'missing job filter did not fail' }

        & $shell -NoProfile -File $PSCommandPath -Commit $onlyBump -RunsJsonPath $paramFixture `
            -Workflow soak.yml -Events push -Soft
        if ($LASTEXITCODE -eq 0) { throw 'event filter did not exclude pull_request/schedule-only fixtures' }

        # Soft miss must not use Write-Error (exit 1 is enough)
        & $shell -NoProfile -File $PSCommandPath -Commit $changed -RunsJsonPath $fixturePath -Soft
        if ($LASTEXITCODE -eq 0) { throw 'soft mismatched tree did not fail' }
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

$allowedEvents = Get-EventList
$trees = @()
if ($RunsJsonPath) {
    $RunsJson = Get-Content -LiteralPath $RunsJsonPath -Raw
}
if ($RunsJson) {
    $trees = @(Get-RunTrees $RunsJson $allowedEvents)
}
elseif ($Live) {
    if (-not $env:GH_TOKEN) { throw 'GH_TOKEN is required for a live tree-evidence check' }
    $repo = $env:GITHUB_REPOSITORY
    $json = & gh run list --repo $repo --workflow $Workflow --status success --limit 50 --json databaseId,headSha,url,conclusion,event,createdAt,displayTitle
    if ($LASTEXITCODE -ne 0) { throw "could not list $Workflow runs" }
    $runs = $json | ConvertFrom-Json
    foreach ($run in @($runs)) {
        # Normalize created_at from gh's createdAt
        if (-not $run.created_at -and $run.createdAt) {
            $run | Add-Member -NotePropertyName created_at -NotePropertyValue $run.createdAt -Force
        }
        if (-not (Test-RunEvent $run $allowedEvents)) { continue }
        if (-not (Test-RunAge $run)) { continue }
        if (-not (Test-LiveJobSuccess ([int64]$run.databaseId) $repo $Job)) { continue }

        $ev = [string]$run.event
        $tree = $null
        if ($ev -eq 'pull_request') {
            $tree = Get-TreeFromArtifact ([int64]$run.databaseId) $repo $Artifact
        }
        else {
            # schedule / push / workflow_dispatch (and any other non-PR): commit tree
            $tree = Get-TreeFromHeadSha ([string]$run.headSha)
            if (-not $tree -and $Artifact) {
                $tree = Get-TreeFromArtifact ([int64]$run.databaseId) $repo $Artifact
            }
        }
        if ($tree) { $trees += $tree }
    }
}
else {
    throw 'pass -Live or -RunsJson'
}

Assert-Evidence $Commit $trees
